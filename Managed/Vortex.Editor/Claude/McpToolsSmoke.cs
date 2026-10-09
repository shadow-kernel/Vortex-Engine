using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.ECS.Components.Audio;
using Editor.ECS.Components.Rendering;
using Editor.ECS.Components.Scripting;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Editor smoke check "mcp tools": the tool sets beyond the scene basics, end to end over MCP — materials (create,
    /// edit, assign, undo restores the file), world macros (grid, deterministic scatter, align, bulk edit, measure,
    /// one undo step each), scripts (write → compile errors with line/column → fix → attach with fields, API lookup),
    /// assets (list, place a model, save a prefab) and audio (search, AudioSource, mixer, procedural generation).
    /// Everything is undone / removed afterwards.
    /// </summary>
    internal static class McpToolsSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("mcp tools", Run);

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("mcp tools: " + why); return false; }
            var project = ProjectData.Current;
            if (project?.ActiveScene == null) return Fail("no scene");
            if (McpHost.IsRunning) return Fail("a server is already running");
            int port = McpTestClient.FreePort();
            if (!await McpHost.StartAsync(port)) return Fail("server did not start: " + McpHost.LastError);
            int undoBefore = UndoRedoManager.Instance.UndoCount;
            var cleanup = new List<string>();
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(60) };
            async Task<JsonNode> C(string tool, JsonObject args) => McpTestClient.Json(await McpTestClient.Call(http, tool, args));
            async Task<JsonNode> Raw(string tool, JsonObject args) => await McpTestClient.Call(http, tool, args);
            int Steps() => UndoRedoManager.Instance.UndoCount;
            try
            {
                await McpTestClient.Rpc(http, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "vortex-smoke-tools", ["version"] = "1" },
                });

                // ================= materials
                var cube = await C("create_entity", new JsonObject { ["kind"] = "cube", ["name"] = "Mcp Mat Cube", ["position"] = new JsonArray(200, 0.5, 200) });
                string cubeId = (string)cube["id"];
                var mat = await C("create_material", new JsonObject
                {
                    ["name"] = "Mcp Rust", ["properties"] = new JsonObject { ["base_color"] = "#8a3b2a", ["roughness"] = 0.8, ["metallic"] = 0.25 },
                    ["assign_to"] = new JsonArray(cubeId),
                });
                string matPath = (string)mat["path"];
                if (matPath != "Assets/Materials/Mcp Rust.vmat") return Fail("create_material path: " + mat.ToJsonString());
                cleanup.Add(Path.Combine(project.Path, matPath));
                var cubeEntity = SceneModel.Resolve(cubeId);
                if (cubeEntity.GetComponent<MeshRenderer>()?.MaterialPath != matPath) return Fail("the material was not assigned: " + cubeEntity.GetComponent<MeshRenderer>()?.MaterialPath);
                var got = await C("get_material", new JsonObject { ["material"] = matPath });
                if ((string)got["base_color"] != "#8a3b2a" || (double)got["roughness"] != 0.8) return Fail("get_material: " + got.ToJsonString());
                int before = Steps();
                await C("set_material_properties", new JsonObject { ["material"] = matPath, ["properties"] = new JsonObject { ["metallic"] = 0.9, ["blend_mode"] = "Opaque" } });
                if (Steps() != before + 1) return Fail("set_material_properties should be one undo step");
                var bad = await Raw("set_material_properties", new JsonObject { ["material"] = matPath, ["properties"] = new JsonObject { ["blend_mode"] = "Glass" } });
                if ((bool?)bad["isError"] != true || !McpTestClient.Text(bad).Contains("AlphaBlend")) return Fail("an invalid blend mode should list the valid ones");
                await C("undo", new JsonObject());
                if ((double)(await C("get_material", new JsonObject { ["material"] = matPath }))["metallic"] != 0.25) return Fail("undo did not restore the material file");
                var outside = await Raw("get_material", new JsonObject { ["material"] = "../../etc/passwd.vmat" });
                if ((bool?)outside["isError"] != true) return Fail("a path outside Assets/ must be refused");

                // ================= world macros
                before = Steps();
                var grid = await C("place_grid", new JsonObject
                {
                    ["source"] = "cube", ["origin"] = new JsonArray(210, 0.25, 210), ["rows"] = 3, ["cols"] = 4, ["spacing"] = new JsonArray(1), ["scale"] = new JsonArray(1, 0.5, 1), ["name"] = "Mcp Tiles",
                });
                if ((int)grid["created"] != 12 || Steps() != before + 1) return Fail("place_grid: " + grid.ToJsonString() + " steps " + (Steps() - before));
                string gridId = (string)grid["group"]["id"];
                var gb = await C("get_bounds", new JsonObject { ["entities"] = new JsonArray(gridId) });
                if (Math.Abs((double)gb["size"][0] - 4) > 0.01 || Math.Abs((double)gb["size"][2] - 3) > 0.01) return Fail("grid bounds: " + gb.ToJsonString());
                var dry1 = await C("scatter", new JsonObject { ["source"] = "sphere", ["area_min"] = new JsonArray(210, 210), ["area_max"] = new JsonArray(213, 212), ["count"] = 8, ["seed"] = 7, ["dry_run"] = true });
                var dry2 = await C("scatter", new JsonObject { ["source"] = "sphere", ["area_min"] = new JsonArray(210, 210), ["area_max"] = new JsonArray(213, 212), ["count"] = 8, ["seed"] = 7, ["dry_run"] = true });
                if (dry1.ToJsonString() != dry2.ToJsonString() || (int)dry1["would_create"] != 8) return Fail("scatter is not deterministic: " + dry1.ToJsonString() + " vs " + dry2.ToJsonString());
                before = Steps();
                var sc = await C("scatter", new JsonObject { ["source"] = "sphere", ["area_min"] = new JsonArray(210, 210), ["area_max"] = new JsonArray(213, 212), ["count"] = 8, ["seed"] = 7, ["scale_range"] = new JsonArray(0.3, 0.5), ["name"] = "Mcp Balls" });
                if ((int)sc["created"] != 8 || Steps() != before + 1) return Fail("scatter: " + sc.ToJsonString());
                // every ball rests on the tiles (top at y = 0.5)
                var balls = SceneModel.Resolve((string)sc["group"]["id"]).Children.ToList();
                foreach (var ball in balls)
                {
                    Bounds.Of(new[] { ball }, out var mn, out var _);
                    if (Math.Abs(mn.Y - 0.5f) > 0.02f) return Fail("a scattered ball does not rest on the tiles (bottom at " + mn.Y + ")");
                }
                var light1 = await C("create_entity", new JsonObject { ["kind"] = "point_light", ["name"] = "Mcp Lamp A", ["position"] = new JsonArray(210, 3, 210) });
                var light2 = await C("create_entity", new JsonObject { ["kind"] = "point_light", ["name"] = "Mcp Lamp B", ["position"] = new JsonArray(214, 3.4, 217) });
                var bulk = await C("bulk_set_properties", new JsonObject { ["component"] = "Light", ["name"] = "Mcp Lamp*", ["properties"] = new JsonObject { ["intensity"] = 0.6 } });
                if ((int)bulk["changed"] != 2) return Fail("bulk_set_properties: " + bulk.ToJsonString());
                var wide = await Raw("bulk_set_properties", new JsonObject { ["component"] = "Light", ["properties"] = new JsonObject { ["intensity"] = 0 } });
                if ((bool?)wide["isError"] != true) return Fail("a bulk edit without any filter must be refused");
                await C("align_entities", new JsonObject { ["entities"] = new JsonArray((string)light1["id"], (string)light2["id"]), ["axis"] = "y", ["mode"] = "center" });
                var m = await C("measure", new JsonObject { ["a"] = (string)light1["id"], ["b"] = (string)light2["id"] });
                if (Math.Abs((double)m["delta"][1]) > 0.01 || Math.Abs((double)m["horizontal_distance"] - 8.062) > 0.01) return Fail("align/measure: " + m.ToJsonString());
                await C("snap_to_grid", new JsonObject { ["entities"] = new JsonArray((string)light2["id"]), ["cell"] = 0.5, ["axes"] = "y" });

                // ================= scripts
                const string good = "using Vortex;\n\npublic class McpSpinner : VortexBehaviour\n{\n    public float Speed = 90f;\n    public Vector3 Axis = new Vector3(0, 1, 0);\n\n    public override void Update(float dt)\n    {\n        Rotate(0, Speed * dt, 0);\n    }\n}\n";
                var ws = await C("write_script", new JsonObject { ["script"] = "McpSpinner", ["code"] = good });
                cleanup.Add(Path.Combine(project.Path, "Assets", "Scripts", "McpSpinner.cs"));
                if ((bool)ws["compiles"] != true || (string)ws["behaviours"][0] != "McpSpinner") return Fail("write_script: " + ws.ToJsonString());
                var broken = await C("edit_script", new JsonObject { ["script"] = "McpSpinner", ["old_text"] = "Rotate(0, Speed * dt, 0);", ["new_text"] = "Rotate(0, Speed * dt, 0)" });
                var err = broken["errors"]?[0];
                if ((bool)broken["compiles"] != false || (int?)err?["line"] != 10 || (string)err?["file"] != "Assets/Scripts/McpSpinner.cs") return Fail("a syntax error should come back with file and line: " + broken.ToJsonString());
                var fixedS = await C("edit_script", new JsonObject { ["script"] = "McpSpinner", ["old_text"] = "Rotate(0, Speed * dt, 0)\n", ["new_text"] = "Rotate(0, Speed * dt, 0);\n" });
                if ((bool)fixedS["compiles"] != true) return Fail("fixing the script did not compile: " + fixedS.ToJsonString());
                var att = await C("attach_script", new JsonObject { ["entity"] = cubeId, ["script"] = "McpSpinner", ["fields"] = new JsonObject { ["Speed"] = 45, ["Axis"] = new JsonArray(1, 0, 0) } });
                var script = cubeEntity.Components.OfType<Script>().FirstOrDefault();
                if (script == null || script.GetFieldValue("Speed") != "45" || script.GetFieldValue("Axis") != "1,0,0") return Fail("attach_script: " + att.ToJsonString());
                var badField = await Raw("attach_script", new JsonObject { ["entity"] = cubeId, ["script"] = "McpSpinner", ["fields"] = new JsonObject { ["Sped"] = 1 } });
                if ((bool?)badField["isError"] != true || !McpTestClient.Text(badField).Contains("Speed")) return Fail("an unknown field should list the real ones");
                var api = McpTestClient.Text(await McpTestClient.Call(http, "get_scripting_api", new JsonObject { ["topic"] = "Input" }));
                if (!api.Contains("GetKey(")) return Fail("get_scripting_api Input: " + api);
                var escape = await Raw("write_script", new JsonObject { ["script"] = "../Materials/Evil", ["code"] = "class X {}" });
                if ((bool?)escape["isError"] != true) return Fail("a script outside Assets/Scripts must be refused");

                // ================= assets
                var models = await C("list_assets", new JsonObject { ["kind"] = "model", ["filter"] = "wooden_crate" });
                if ((models["assets"] as JsonArray)?.Count == 0) models = await C("list_assets", new JsonObject { ["kind"] = "model" });
                var modelList = models["assets"] as JsonArray;
                // a project without models (a bare template): place a primitive instead
                string crate = modelList != null && modelList.Count > 0 ? (string)modelList[0] : "Primitive:Cube";
                var placed = await C("place_asset", new JsonObject { ["asset"] = crate, ["position"] = new JsonArray(205, 0, 205), ["name"] = "Mcp Crate" });
                if ((string)placed["entity"]["name"] != "Mcp Crate" || (double)placed["bounds"]["size"][1] < 0.1) return Fail("place_asset: " + placed.ToJsonString());
                var pf = await C("create_prefab", new JsonObject { ["entity"] = (string)placed["entity"]["id"], ["name"] = "Mcp Crate Prefab" });
                cleanup.Add(Path.Combine(project.Path, (string)pf["prefab"]));
                if (!File.Exists(Path.Combine(project.Path, (string)pf["prefab"]))) return Fail("create_prefab wrote no file: " + pf.ToJsonString());
                var again = await Raw("create_prefab", new JsonObject { ["entity"] = (string)placed["entity"]["id"], ["name"] = "Mcp Crate Prefab" });
                if ((bool?)again["isError"] != true) return Fail("an existing prefab must not be replaced without overwrite");

                // ================= audio
                var gen = await C("generate_sound", new JsonObject { ["prompt"] = "short metallic clank", ["duration"] = 0.6, ["backend"] = "procedural", ["name"] = "Mcp Clank" });
                string genPath = (string)gen["path"];
                if (genPath == null || !File.Exists(Path.Combine(project.Path, genPath))) return Fail("generate_sound: " + gen.ToJsonString());
                if ((string)gen["family"] != "impact" || gen["warning"] != null) return Fail("generate_sound: 'short metallic clank' must be an impact without a warning (#353): " + gen.ToJsonString());
                cleanup.Add(Path.Combine(project.Path, genPath));
                var sa = await C("search_audio", new JsonObject { ["query"] = "step concrete" });
                if ((sa["project"] as JsonArray)?.Count == 0) sa = await C("search_audio", new JsonObject { ["query"] = "Mcp Clank" });
                string step = (string)(sa["project"] as JsonArray)?.FirstOrDefault()?["path"];
                if (step == null || (double?)sa["project"][0]["duration_s"] is not double dur || dur <= 0) return Fail("search_audio: " + sa.ToJsonString());
                var cfg = await C("configure_audio_source", new JsonObject { ["entity"] = cubeId, ["properties"] = new JsonObject { ["clip"] = step, ["loop"] = true, ["spatial"] = true, ["max_distance"] = 12, ["bus"] = "Ambience" } });
                var src = cubeEntity.GetComponent<AudioSource>();
                if (src == null || src.AudioClipPath != step || !src.Loop || src.SpatialBlend != 1f || src.MaxDistance != 12f || src.OutputBus != 3) return Fail("configure_audio_source: " + cfg.ToJsonString());
                var sct = await C("create_sound_container", new JsonObject { ["name"] = "Mcp Clanks", ["clips"] = new JsonArray(genPath, step), ["pitch"] = new JsonArray(0.9, 1.1) });
                string scPath = (string)sct["path"];
                if (scPath != null) cleanup.Add(Path.Combine(project.Path, scPath));
                var scFile = scPath != null ? Editor.Core.Audio.SoundContainer.Load(Path.Combine(project.Path, scPath)) : null;
                if (scFile == null || scFile.Entries.Count != 2 || scFile.Entries[0].ClipPath != genPath || Math.Abs(scFile.PitchMin - 0.9f) > 0.001f || Math.Abs(scFile.PitchMax - 1.1f) > 0.001f)
                    return Fail("create_sound_container: " + sct.ToJsonString());
                await C("configure_audio_source", new JsonObject { ["entity"] = cubeId, ["properties"] = new JsonObject { ["clip"] = scPath } });
                if (src.AudioClipPath != scPath) return Fail("configure_audio_source does not take the sound container " + scPath);
                var twice = await Raw("add_component", new JsonObject { ["entity"] = cubeId, ["type"] = "AudioListener" });
                var third = await Raw("add_component", new JsonObject { ["entity"] = cubeId, ["type"] = "AudioListener" });
                if ((bool?)twice["isError"] == true || (bool?)third["isError"] != true) return Fail("add_component must refuse a second AudioListener: " + third.ToJsonString());
                await C("remove_component", new JsonObject { ["entity"] = cubeId, ["type"] = "AudioListener" });
                var mix = await C("get_mixer_state", new JsonObject());
                if ((mix as JsonArray)?.Count != 5) return Fail("get_mixer_state: " + mix.ToJsonString());
                float sfxBefore = AudioMixerConfig.Load(project.Path).BusVolumes[2];
                var sv = await C("set_bus_volume", new JsonObject { ["bus"] = "SFX", ["db"] = -6 });
                if (Math.Abs((double)sv["volume_db"] + 6) > 0.1 || Math.Abs(AudioMixerConfig.Load(project.Path).BusVolumes[2] - 0.501f) > 0.01f) return Fail("set_bus_volume: " + sv.ToJsonString());
                await C("undo", new JsonObject());
                if (Math.Abs(AudioMixerConfig.Load(project.Path).BusVolumes[2] - sfxBefore) > 0.001f) return Fail("undo did not restore the bus volume");
                var aud = await C("audition_clip", new JsonObject { ["path"] = genPath, ["volume"] = 0 });
                await C("stop_audition", new JsonObject());

                // ================= play mode (#90): enter, the game camera's image, stats, exit
                var play = await C("enter_play_mode", new JsonObject { ["run_seconds"] = 1 });
                if ((string)play["state"] != "Playing") return Fail("enter_play_mode: " + play.ToJsonString());
                var pshot = await Raw("capture_viewport", new JsonObject { ["max_size"] = 512 });
                bool playImage = (pshot["content"] as JsonArray)?.Any(c => (string)c["type"] == "image") == true;
                if (!playImage || McpTestClient.Text(pshot).IndexOf("play mode", StringComparison.OrdinalIgnoreCase) < 0) return Fail("capture_viewport in play mode: " + McpTestClient.Text(pshot));
                var stats = await C("engine_stats", new JsonObject());
                if ((string)stats["play_state"] != "Playing" || stats["draw_calls"] == null) return Fail("engine_stats: " + stats.ToJsonString());
                var stop = await C("exit_play_mode", new JsonObject());
                if ((string)stop["state"] != "Editing") return Fail("exit_play_mode: " + stop.ToJsonString());

                log.Log("mcp tools: OK — " + ToolCatalog.All.Count + " tools; material, grid (12), scatter (8, resting on the tiles), scripts with line-accurate errors, " + Path.GetFileName(crate) + " + prefab, audio + mixer + generated " + Path.GetFileName(genPath) + " + container " + Path.GetFileName(scPath) + ", play mode (capture, stats, exit)");
                return true;
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                while (UndoRedoManager.Instance.UndoCount > undoBefore && UndoRedoManager.Instance.Undo()) { }
                EditorCommands.AfterSceneEdit();
                foreach (var f in cleanup)
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                    try { if (File.Exists(f + ".vmeta")) File.Delete(f + ".vmeta"); } catch { }
                }
                await McpHost.StopAsync();
            }
        }
    }
}
