using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Scripting;
using Editor.UI.Vui;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Editor smoke check "mcp play mode leaves no trace": Stop gives back exactly the edit scenes of Play, end to end over
    /// MCP. While playing (and paused) the scene tools create, duplicate, place, grid, scatter, re-parent, delete, move and
    /// build terrain, water, foliage and areas — in the played scene and under an entity of another loaded scene — change a
    /// stored script field and the environment: every result says runtime_only (in the shape it has in edit mode), and after
    /// exit_play_mode the hierarchies, the field and the environment are the ones of Play (#423, #478, #479), the terrain's
    /// data file is gone, the undo history is the one of Play, no selection holds a removed entity, no retained-UI screen is
    /// left (#477) and one Console line says what Stop did. A material written while playing stays (runtime_only false). A
    /// script attached while paused starts only when play resumes. Then a behaviour quits from its Update: play stops after
    /// the tick, through the toolbar's Stop (#410). Last, a prefab applied while playing re-syncs its instance after Stop.
    /// </summary>
    internal static class McpPlayModeSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("mcp play mode leaves no trace", Run);

        private const string Probe =
            "using Vortex;\n\npublic class McpPlayProbe : VortexBehaviour\n{\n" +
            "    public float Marker = 1f;\n    public bool QuitInUpdate = false;\n    public string PushPath = \"\";\n\n" +
            "    public override void Start() { if (PushPath != \"\") Gui.Push(PushPath); }\n\n" +
            "    public override void Update(float dt) { if (QuitInUpdate) Application.Quit(); }\n}\n";

        private const string Starter =
            "using Vortex;\n\npublic class McpPlayStarter : VortexBehaviour\n{\n" +
            "    public override void Start() { Debug.Log(\"[MCP PLAY START] started\"); }\n}\n";

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("mcp play mode: " + why); return false; }
            var project = ProjectData.Current;
            var scene = project?.ActiveScene;
            if (scene == null) return Fail("no scene");
            if (McpHost.IsRunning) return Fail("a server is already running");
            if (PlayModeService.Instance.IsPlaying) return Fail("the editor is already playing");
            int port = McpTestClient.FreePort();
            if (!await McpHost.StartAsync(port)) return Fail("server did not start: " + McpHost.LastError);
            var files = new List<string>();
            var roots = new List<string>();
            var settings = scene.Settings;
            bool taa = settings.TaaEnabled; float sharpen = settings.Sharpen;
            int stopInsideTick = -1;   // 1 = play stopped from inside ScriptRuntime.Update (#410), 0 = after the tick
            EventHandler<PlayState> onState = (s, state) =>
            {
                if (state == PlayState.Editing) stopInsideTick = Environment.StackTrace.Contains("ScriptRuntime.Update(") ? 1 : 0;
            };
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(60) };
            async Task<JsonNode> Raw(string tool, JsonObject args) => await McpTestClient.Call(http, tool, args);
            async Task<JsonNode> C(string tool, JsonObject args) => McpTestClient.Json(await Raw(tool, args));
            bool RuntimeOnly(JsonNode r) => r?["runtime_only"] != null && (bool)r["runtime_only"];
            bool Logged(string text) => log.Entries.Any(en => en.Message != null && en.Message.Contains(text));
            try
            {
                await McpTestClient.Rpc(http, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "vortex-smoke-play", ["version"] = "1" },
                });

                // ================= the edit scenes: a probe behaviour, two parents with a child each, a lamp prefab; another scene loaded
                string vui = Path.Combine(project.Path, "Assets", "UI", "Mcp Play Probe.vui");
                Directory.CreateDirectory(Path.GetDirectoryName(vui));
                File.WriteAllText(vui, "{ \"vui\": 1, \"designW\": 1920, \"designH\": 1080, \"root\": { \"kind\": \"Panel\", \"id\": \"probeRoot\", \"blocksInput\": true } }");
                files.Add(vui);
                foreach (var (name, code) in new[] { ("McpPlayProbe", Probe), ("McpPlayStarter", Starter) })
                {
                    var ws = await C("write_script", new JsonObject { ["script"] = name, ["code"] = code });
                    files.Add(Path.Combine(project.Path, "Assets", "Scripts", name + ".cs"));
                    if ((bool)ws["compiles"] != true) return Fail("write_script " + name + ": " + ws.ToJsonString());
                }
                string probe = (string)(await C("create_entity", new JsonObject { ["name"] = "Mcp Play Probe", ["position"] = new JsonArray(300, 1, 300) }))["id"];
                string a = (string)(await C("create_entity", new JsonObject { ["name"] = "Mcp Play A", ["position"] = new JsonArray(300, 0, 304) }))["id"];
                string b = (string)(await C("create_entity", new JsonObject { ["name"] = "Mcp Play B", ["position"] = new JsonArray(304, 0, 304) }))["id"];
                string lamp = (string)(await C("create_entity", new JsonObject { ["kind"] = "point_light", ["name"] = "Mcp Play Lamp", ["position"] = new JsonArray(306, 2, 300) }))["id"];
                roots.AddRange(new[] { probe, a, b, lamp });
                string childA = (string)(await C("create_entity", new JsonObject { ["name"] = "Mcp Play A Child", ["parent"] = a }))["id"];
                string childB = (string)(await C("create_entity", new JsonObject { ["name"] = "Mcp Play B Child", ["parent"] = b }))["id"];
                await C("attach_script", new JsonObject { ["entity"] = probe, ["script"] = "McpPlayProbe", ["fields"] = new JsonObject { ["Marker"] = 5, ["PushPath"] = vui } });
                var prefab = await C("create_prefab", new JsonObject { ["entity"] = lamp, ["name"] = "Mcp Play Lamp" });
                string prefabRel = (string)prefab["prefab"];
                files.Add(Path.Combine(project.Path, prefabRel));
                var script = SceneModel.Resolve(probe).GetComponent<Script>();
                var other = project.Scenes.FirstOrDefault(s => s != null && !ReferenceEquals(s, scene));
                GameEntity otherRoot = null;
                if (other != null)
                {
                    EditorSession.Instance.ActivateScene(other);   // loaded — and so snapshotted at Play like the played scene
                    EditorSession.Instance.ActivateScene(scene);
                    otherRoot = other.Entities.FirstOrDefault(e => e != null);
                }
                string shape = Shape(scene), otherShape = other != null ? Shape(other) : null;
                int undoAtPlay = UndoRedoManager.Instance.UndoCount;

                // ================= session 1: what the tools change while playing or paused goes again on Stop
                var play = await C("enter_play_mode", new JsonObject { ["run_seconds"] = 0.5 });
                if ((string)play["state"] != "Playing") return Fail("enter_play_mode: " + play.ToJsonString());
                var probeUi = VuiStack.Instance.Load(vui);
                if (!VuiStack.Instance.HasActiveScreens || probeUi == null) return Fail("the probe's Start did not push its screen");
                var calls = new List<(string tool, JsonObject args)>
                {
                    ("duplicate_entities", new JsonObject { ["entities"] = new JsonArray(probe) }),
                    ("place_asset", new JsonObject { ["asset"] = "Primitive:Cube", ["position"] = new JsonArray(302, 0, 300), ["name"] = "Mcp Play Placed" }),
                    ("place_grid", new JsonObject { ["source"] = "cube", ["origin"] = new JsonArray(304, 0, 300), ["rows"] = 2, ["cols"] = 2, ["spacing"] = new JsonArray(1), ["name"] = "Mcp Play Grid" }),
                    ("scatter", new JsonObject { ["source"] = "sphere", ["area_min"] = new JsonArray(304, 300), ["area_max"] = new JsonArray(306, 302), ["count"] = 3, ["fallback_y"] = 0, ["name"] = "Mcp Play Scatter" }),
                    ("parent_entity", new JsonObject { ["entity"] = childA, ["parent"] = b }),
                    ("unparent_entity", new JsonObject { ["entity"] = childB }),
                    ("delete_entities", new JsonObject { ["entities"] = new JsonArray(childB) }),
                    ("set_transform", new JsonObject { ["entity"] = a, ["position"] = new JsonArray(310, 5, 310) }),
                    ("add_component", new JsonObject { ["entity"] = a, ["type"] = "BoxCollider" }),
                    ("remove_component", new JsonObject { ["entity"] = a, ["type"] = "BoxCollider" }),
                    ("attach_script", new JsonObject { ["entity"] = probe, ["script"] = "McpPlayProbe", ["fields"] = new JsonObject { ["Marker"] = 300 } }),
                    ("set_environment", new JsonObject { ["temporal_aa"] = !taa, ["sharpen"] = sharpen > 0.2f ? 0.1 : 0.4 }),
                    ("set_sky", new JsonObject { ["ambient"] = 0.77 }),
                    ("create_area", new JsonObject { ["name"] = "Mcp Play Area", ["position"] = new JsonArray(300, 1, 310), ["size"] = new JsonArray(4, 2, 4) }),
                    ("create_terrain", new JsonObject { ["position"] = new JsonArray(320, 0, 300), ["size"] = 16, ["resolution"] = 33, ["name"] = "Mcp Play Terrain" }),
                    ("create_water", new JsonObject { ["position"] = new JsonArray(330, 0, 300), ["size"] = 8, ["name"] = "Mcp Play Water" }),
                    ("create_foliage_layer", new JsonObject { ["name"] = "Mcp Play Foliage" }),
                };
                if (otherRoot != null) calls.Add(("create_entity", new JsonObject { ["name"] = "Mcp Play Elsewhere", ["parent"] = otherRoot.Id.ToString() }));
                string terrainFile = null;
                foreach (var (tool, args) in calls)
                {
                    var raw = await Raw(tool, args);
                    var texts = (raw["content"] as JsonArray)?.Where(c => (string)c["type"] == "text").Select(c => (string)c["text"]).ToList() ?? new List<string>();
                    if ((bool?)raw["isError"] == true || texts.Count == 0) return Fail(tool + " failed in play mode: " + McpTestClient.Text(raw));
                    var first = JsonNode.Parse(texts[0]);
                    if (first is JsonArray)
                    {
                        // an array keeps its shape: the flag comes as a second text block (#423 review)
                        if (texts.Count < 2 || !texts[1].StartsWith("runtime_only: true", StringComparison.Ordinal)) return Fail(tool + " in play mode: no runtime_only note after the array: " + McpTestClient.Shorten(string.Join(" | ", texts)));
                    }
                    else if (!RuntimeOnly(first)) return Fail(tool + " in play mode does not say runtime_only: " + McpTestClient.Shorten(texts[0]));
                }
                // the first stroke writes the new terrain's data file: made in play, it goes with its terrain on Stop
                var sculpt = await C("sculpt_terrain", new JsonObject { ["terrain"] = "Mcp Play Terrain", ["point"] = new JsonArray(328, 308), ["op"] = "raise", ["radius"] = 3, ["amount"] = 1 });
                if (!RuntimeOnly(sculpt)) return Fail("sculpting a terrain made in play must say runtime_only: " + sculpt.ToJsonString());
                terrainFile = Editor.Core.Services.Terrain.TerrainService.ResolvePath(SceneModel.Resolve("Mcp Play Terrain").GetComponent<Editor.ECS.Components.Rendering.Terrain>()?.DataPath);
                if (terrainFile == null || !File.Exists(terrainFile)) return Fail("the sculpt wrote no terrain data file");
                var leak = await C("create_entity", new JsonObject { ["kind"] = "cube", ["name"] = "Mcp Play Leak", ["position"] = new JsonArray(300, 1, 302) });
                if ((string)leak["id"] == null || !RuntimeOnly(leak)) return Fail("create_entity in play mode must keep id at the top and add runtime_only: " + leak.ToJsonString());
                var mat = await C("create_material", new JsonObject { ["name"] = "Mcp Play Rust", ["properties"] = new JsonObject { ["base_color"] = "#8a3b2a" } });
                string matFile = Path.Combine(project.Path, (string)mat["path"]);
                files.Add(matFile);
                if (RuntimeOnly(mat) || !((string)mat["note"] ?? "").Contains("stay on disk")) return Fail("a file written while playing must not claim runtime_only: " + mat.ToJsonString());
                if (script.GetFieldValue("Marker") != "300") return Fail("attach_script did not set the field in play");
                EditorCommands.SelectMany(new List<GameEntity> { SceneModel.Resolve((string)leak["id"]), SceneModel.Resolve(b) });

                // paused: a script attached now starts only when play goes on
                PlayModeService.Instance.Pause();
                var started = await C("attach_script", new JsonObject { ["entity"] = b, ["script"] = "McpPlayStarter" });
                await Task.Delay(300);
                if (Logged("[MCP PLAY START]")) return Fail("a script attached while paused started at once: " + started.ToJsonString());
                PlayModeService.Instance.Resume();
                for (int i = 0; i < 50 && !Logged("[MCP PLAY START]"); i++) await Task.Delay(100);
                if (!Logged("[MCP PLAY START]")) return Fail("the script attached while paused did not start after Resume");
                PlayModeService.Instance.Pause();
                var paused = await C("create_entity", new JsonObject { ["name"] = "Mcp Play Paused" });
                if (!RuntimeOnly(paused)) return Fail("create_entity while paused does not say runtime_only (#423)");
                var stop = await C("exit_play_mode", new JsonObject());
                if ((string)stop["state"] != "Editing") return Fail("exit_play_mode: " + stop.ToJsonString());

                string after = Shape(scene);
                if (after != shape) return Fail("the hierarchy after Stop is not the one of Play (#423):\n  before " + McpTestClient.Shorten(shape) + "\n  after  " + McpTestClient.Shorten(after));
                if (other != null && Shape(other) != otherShape) return Fail("the other loaded scene was not put back (#423)");
                if (script.GetFieldValue("Marker") != "5") return Fail("the script field changed in play stayed: " + script.GetFieldValue("Marker") + " (#478)");
                if (settings.TaaEnabled != taa || settings.Sharpen != sharpen || !ReferenceEquals(scene.Settings, settings))
                    return Fail("the environment changed in play stayed: temporal_aa " + scene.Settings.TaaEnabled + ", sharpen " + scene.Settings.Sharpen + " (#479)");
                if (VuiStack.Instance.HasActiveScreens) return Fail("a retained-UI screen survived Stop (#477)");
                if (ReferenceEquals(VuiStack.Instance.Load(vui), probeUi)) return Fail("Gui.Load returns the previous session's canvas (#477)");
                VuiStack.Instance.Reset();
                if (UndoRedoManager.Instance.UndoCount != undoAtPlay) return Fail("the undo steps made while playing outlived Stop: " + UndoRedoManager.Instance.UndoCount + " (were " + undoAtPlay + ")");
                UndoRedoManager.Instance.Undo();   // Ctrl+Z after Stop: the last edit made before Play, nothing of the play session
                bool undoKeepsShape = Shape(scene) == shape;
                UndoRedoManager.Instance.Redo();
                if (!undoKeepsShape) return Fail("an undo after Stop changed the restored hierarchy");
                if (File.Exists(terrainFile)) return Fail("the terrain made in play left its data file " + terrainFile);
                if (!File.Exists(matFile)) return Fail("the material written while playing should stay");
                var hierarchy = EditorSession.Instance.Hierarchy;
                if ((hierarchy.SelectedEntities?.Any(e => e.Scene == null) ?? false) || SelectionService.Instance.SelectedEntity?.Scene == null && SelectionService.Instance.SelectedEntity != null)
                    return Fail("a selection still holds an entity Stop removed");
                for (int i = 0; i < 20 && !Logged("Stop: removed"); i++) await Task.Delay(50);
                if (!Logged("Stop: removed") || !Logged("files written while playing stay on disk")) return Fail("no Console line told what Stop removed and that files stay");

                // ================= session 2: Application.Quit() from Update stops play after the tick (#410)
                await C("attach_script", new JsonObject { ["entity"] = probe, ["script"] = "McpPlayProbe", ["fields"] = new JsonObject { ["QuitInUpdate"] = true, ["PushPath"] = "" } });
                shape = Shape(scene);
                PlayModeService.Instance.StateChanged += onState;
                await C("enter_play_mode", new JsonObject { ["run_seconds"] = 0.5 });
                for (int i = 0; i < 150 && PlayModeService.Instance.IsPlaying; i++) await Task.Delay(100);
                PlayModeService.Instance.StateChanged -= onState;
                if (PlayModeService.Instance.IsPlaying) return Fail("Application.Quit() from Update did not stop play");
                if (stopInsideTick != 0) return Fail("play stopped inside the script tick — the rest of the tick then writes into the restored scene (#410)");
                if (CameraService.Instance.IsGameCameraActive) return Fail("after a script's quit the viewport still looks through the game camera (not the toolbar's Stop)");
                if (Shape(scene) != shape || script.GetFieldValue("QuitInUpdate") != "true" || script.GetFieldValue("Marker") != "5")
                    return Fail("the scene after a script's quit is not the one of Play");
                await C("attach_script", new JsonObject { ["entity"] = probe, ["script"] = "McpPlayProbe", ["fields"] = new JsonObject { ["QuitInUpdate"] = false } });

                // ================= session 3: a prefab applied while playing — Stop re-syncs its (restored) instance
                await C("enter_play_mode", new JsonObject { ["run_seconds"] = 0.3 });
                await C("set_component_properties", new JsonObject { ["entity"] = lamp, ["component"] = "Light", ["properties"] = new JsonObject { ["intensity"] = 7.25 } });
                if (!PrefabService.Instance.ApplyToPrefab(SceneModel.Resolve(lamp))) return Fail("Apply to Prefab failed in play");
                await C("exit_play_mode", new JsonObject());
                var instance = SceneModel.All(scene).FirstOrDefault(e => string.Equals(e.PrefabPath, prefabRel, StringComparison.OrdinalIgnoreCase));
                if (instance != null) roots[roots.IndexOf(lamp)] = instance.Id.ToString();
                float intensity = instance?.GetComponent<Light>()?.Intensity ?? -1f;
                if (Math.Abs(intensity - 7.25f) > 1e-3f) return Fail("the prefab saved while playing and its instance after Stop differ (instance intensity " + intensity + ")");

                log.Log("mcp play mode: OK — " + (calls.Count + 3) + " tool edits in play (one in another loaded scene) said runtime_only and were gone after Stop with their terrain file, undo history and selection; a material written stayed (runtime_only false); a script attached while paused started on Resume; Application.Quit() stopped play after the tick; a prefab applied in play re-synced its instance");
                return true;
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                PlayModeService.Instance.StateChanged -= onState;
                if (PlayModeService.Instance.IsPlaying) EditorCommands.Stop();
                try { if (roots.Count > 0) await C("delete_entities", new JsonObject { ["entities"] = new JsonArray(roots.ConvertAll(r => (JsonNode)r).ToArray()) }); } catch { }
                scene.Settings.TaaEnabled = taa; scene.Settings.Sharpen = sharpen;
                try { scene.Settings.Apply(); } catch { }
                VuiStack.Instance.Reset();
                files.Add(Path.Combine(project.Path, "Assets", "Terrain", "Mcp_Play_Terrain.vterrain"));   // if a failure left it
                foreach (var f in files)
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                    try { if (File.Exists(f + ".vmeta")) File.Delete(f + ".vmeta"); } catch { }
                }
                EditorCommands.AfterSceneEdit();
                await McpHost.StopAsync();
            }
        }

        /// <summary>The hierarchy as text: every list position with its entity, depth first — two shapes are equal only when
        /// every entity is in the same parent, at the same index, once.</summary>
        private static string Shape(Scene scene)
        {
            var sb = new StringBuilder();
            void Walk(IList<GameEntity> list, string parent, int depth)
            {
                if (list == null || depth > 64) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    string id = e == null ? "null" : SceneModel.ShortId(e);
                    sb.Append(parent).Append('[').Append(i).Append("]=").Append(id).Append(' ');
                    if (e != null) Walk(e.Children, id, depth + 1);
                }
            }
            Walk(scene.Entities, "", 0);
            return sb.ToString();
        }
    }
}
