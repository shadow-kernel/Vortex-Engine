using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;
using Editor.Utilities;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Editor smoke check "mcp scene revert" (#411): a scene file is opened over MCP and edited (a move, a new entity, a
    /// selection, a script written). open_scene without reload reads nothing and says already_open + unsaved_changes; the file
    /// is put back as by a git checkout; a reload is refused while playing; open_scene reload:true then reads the file into the
    /// same scene: the probe is where the file has it, the new entity is gone, nothing stays selected, the discarded entities
    /// left the engine and the file's joined it, the scene's undo steps are dropped while the script's stays, and the scene
    /// matches its file. Last, File ▸ Revert Scene's command reverts a scene without changes without asking.
    /// </summary>
    internal static class McpSceneRevertSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("mcp scene revert", Run);

        /// <summary>Where two .vscene contents part, as XML lines — what a failure says.</summary>
        internal static string FirstDifference(byte[] a, byte[] b)
        {
            try
            {
                string[] Lines(byte[] data)
                {
                    var doc = System.Xml.Linq.XDocument.Load(System.Xml.XmlDictionaryReader.CreateBinaryReader(data, System.Xml.XmlDictionaryReaderQuotas.Max));
                    return doc.ToString().Split('\n');
                }
                var x = Lines(a); var y = Lines(b);
                for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
                    if (x[i] != y[i]) return "line " + (i + 1) + ": memory «" + x[i].Trim() + "» / file «" + y[i].Trim() + "»";
                return x.Length == y.Length ? "none" : "lengths " + x.Length + " / " + y.Length;
            }
            catch (Exception ex) { return "(" + ex.Message + ")"; }
        }

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("mcp scene revert: " + why); return false; }
            var project = ProjectData.Current;
            var original = project?.ActiveScene;
            if (original == null) return Fail("no scene");
            if (McpHost.IsRunning) return Fail("a server is already running");
            if (PlayModeService.Instance.IsPlaying) return Fail("the editor is already playing");
            int port = McpTestClient.FreePort();
            if (!await McpHost.StartAsync(port)) return Fail("server did not start: " + McpHost.LastError);
            string name = "Mcp Revert " + Guid.NewGuid().ToString("N").Substring(0, 6);
            string rel = "Assets/Scenes/" + name + ".vscene";
            string path = Path.Combine(project.Path, "Assets", "Scenes", name + ".vscene");
            string scriptFile = Path.Combine(project.Path, "Assets", "Scripts", "McpRevertNote.cs");
            var undo = UndoRedoManager.Instance;
            int undoBefore = undo.UndoCount;
            Scene test = null;
            string terrainFile = null;
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(60) };
            async Task<JsonNode> Raw(string tool, JsonObject args) => await McpTestClient.Call(http, tool, args);
            async Task<JsonNode> C(string tool, JsonObject args) => McpTestClient.Json(await Raw(tool, args));
            static bool At(GameEntity e, float x, float y, float z)
            {
                var p = e.Transform.LocalPosition;
                return Math.Abs(p.X - x) < 1e-4f && Math.Abs(p.Y - y) < 1e-4f && Math.Abs(p.Z - z) < 1e-4f;
            }
            try
            {
                await McpTestClient.Rpc(http, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "vortex-smoke-revert", ["version"] = "1" },
                });

                // ================= a scene file: a probe cube at (1, 2, 3) and a lamp, saved the way the editor saves one
                var file = new Scene(project, name) { FilePath = path };
                var probeOnDisk = new GameEntity(file, "Revert Probe");
                probeOnDisk.Transform.LocalPosition = new Vector3(1, 2, 3);
                probeOnDisk.AddComponentDirect(new MeshRenderer(probeOnDisk, "Primitive:Cube"));
                file.Entities.Add(probeOnDisk);
                var lamp = new GameEntity(file, "Revert Lamp");
                lamp.AddComponentDirect(new Light(lamp, LightType.Point));
                file.Entities.Add(lamp);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                file.Save();
                byte[] onDisk = File.ReadAllBytes(path);

                var opened = await C("open_scene", new JsonObject { ["scene"] = rel });
                test = project.Scenes.FirstOrDefault(s => s != null && s.Name == name);
                if (test == null || !ReferenceEquals(project.ActiveScene, test)) return Fail("open_scene did not load and activate the file: " + opened.ToJsonString());

                // ================= the session: a move, a new entity, a script written (a file's step), a selection
                var probe = test.Entities.First(e => e.Name == "Revert Probe");
                string probeId = SceneModel.ShortId(probe);
                await C("set_transform", new JsonObject { ["entity"] = probeId, ["position"] = new JsonArray(9, 9, 9) });
                await C("create_entity", new JsonObject { ["kind"] = "cube", ["name"] = "Revert Extra" });
                var ws = await C("write_script", new JsonObject { ["script"] = "McpRevertNote", ["code"] = "using Vortex;\n\npublic class McpRevertNote : VortexBehaviour { }\n" });
                if ((bool?)ws["compiles"] != true) return Fail("write_script: " + ws.ToJsonString());
                var scriptStep = OperationLog.Items.LastOrDefault(o => o.Tool == "write_script")?.UndoStep;
                await C("select_entities", new JsonObject { ["entities"] = new JsonArray(probeId) });
                if (!ID.IsValid(probe.EntityId)) return Fail("the probe of the open scene has no native entity");
                if (!ReferenceEquals(SelectionService.Instance.SelectedEntity, probe)) return Fail("select_entities did not select the probe");

                // ================= open_scene without reload reads nothing — and says so
                var again = await C("open_scene", new JsonObject { ["scene"] = name });
                if ((bool?)again["already_open"] != true || (bool?)again["unsaved_changes"] != true)
                    return Fail("open_scene on the open, edited scene must say already_open + unsaved_changes: " + again.ToJsonString());
                if (!At(probe, 9, 9, 9)) return Fail("open_scene without reload changed the scene");

                // ================= a git checkout puts the file back; while playing a reload is refused
                File.WriteAllBytes(path, onDisk);
                var play = await C("enter_play_mode", new JsonObject { ["run_seconds"] = 0.3 });
                var refused = await Raw("open_scene", new JsonObject { ["scene"] = name, ["reload"] = true });
                var stopped = await C("exit_play_mode", new JsonObject());
                if ((bool?)refused["isError"] != true || !McpTestClient.Text(refused).Contains("exit_play_mode"))
                    return Fail("a reload while playing must be refused with a hint to exit_play_mode: " + McpTestClient.Text(refused) + " (play " + play.ToJsonString() + ")");
                if ((string)stopped["state"] != "Editing") return Fail("exit_play_mode: " + stopped.ToJsonString());

                // ================= the reload
                var r = await C("open_scene", new JsonObject { ["scene"] = name, ["reload"] = true });
                if ((bool?)r["reloaded"] != true || (bool?)r["discarded_unsaved_changes"] != true || ((int?)r["undo_steps_dropped"] ?? 0) < 2)
                    return Fail("open_scene reload: " + r.ToJsonString());
                var probe2 = test.Entities.FirstOrDefault(e => e.Name == "Revert Probe");
                if (probe2 == null || ReferenceEquals(probe2, probe) || !At(probe2, 1, 2, 3)) return Fail("the probe is not the file's after the reload");
                if (test.Entities.Any(e => e.Name == "Revert Extra")) return Fail("the entity made in memory survived the reload");
                if (!ReferenceEquals(project.ActiveScene, test) || !project.Scenes.Contains(test)) return Fail("the reload replaced the scene object");
                if (SelectionService.Instance.SelectedEntity != null) return Fail("the discarded probe is still selected");
                if (ID.IsValid(probe.EntityId) || !ID.IsValid(probe2.EntityId)) return Fail("native twins: discarded " + probe.EntityId + ", reloaded " + probe2.EntityId);
                if (scriptStep == null || !undo.IsOnUndoStack(scriptStep)) return Fail("the script's undo step was dropped with the scene's");
                if (SceneService.Instance.HasUnsavedChanges(test)) return Fail("the reloaded scene does not match its file");
                var got = await C("get_entity", new JsonObject { ["entity"] = probeId });
                var pos = got["transform"]?["position"] as JsonArray;
                if (pos == null || (double)pos[0] != 1 || (double)pos[1] != 2 || (double)pos[2] != 3) return Fail("get_entity after the reload: " + got.ToJsonString());

                // ================= File ▸ Revert Scene: the scene is its file now — nothing is read, nothing asked
                await EditorCommands.RevertScene(test);
                await SmokeRegistry.Settle(150);   // the toast is posted
                string toast = EditorCommands.Window?.LastToast ?? "";
                if (!toast.Contains("already matches its file") || !ReferenceEquals(test.Entities.FirstOrDefault(e => e.Name == "Revert Probe"), probe2))
                    return Fail("Revert Scene on an unchanged scene: " + toast);

                // ================= save, then revert: nothing to discard, the undo history stays (#411 review)
                await C("set_transform", new JsonObject { ["entity"] = probeId, ["position"] = new JsonArray(4, 4, 4) });
                await C("save_scene", new JsonObject());
                int stepsSaved = undo.UndoCount;
                var same = await C("open_scene", new JsonObject { ["scene"] = name, ["reload"] = true });
                if ((bool?)same["unchanged"] != true || undo.UndoCount != stepsSaved) return Fail("a revert right after a save must change nothing: " + same.ToJsonString());

                // ================= terrain: a stroke writes its data file at once; a revert puts the saved heights back (#411 review)
                await C("create_terrain", new JsonObject { ["position"] = new JsonArray(400, 0, 400), ["size"] = 16, ["resolution"] = 33, ["name"] = "Revert Hill" });
                await C("save_scene", new JsonObject());
                var terrainComp = SceneModel.Resolve("Revert Hill").GetComponent<Editor.ECS.Components.Rendering.Terrain>();
                terrainFile = Editor.Core.Services.Terrain.TerrainService.ResolvePath(terrainComp?.DataPath);
                if (terrainFile == null || !File.Exists(terrainFile)) return Fail("create_terrain left the terrain without a data file");
                var onDiskScene = Editor.Core.Serialization.DataSerializer.LoadFromBinary<Scene>(path);
                if (!onDiskScene.Entities.Any(e => e.Name == "Revert Hill" && !string.IsNullOrEmpty(e.GetComponent<Editor.ECS.Components.Rendering.Terrain>()?.DataPath)))
                    return Fail("the scene's save did not record the terrain's data path");
                byte[] savedHeights = File.ReadAllBytes(terrainFile);
                await C("sculpt_terrain", new JsonObject { ["terrain"] = "Revert Hill", ["point"] = new JsonArray(408, 408), ["op"] = "raise", ["radius"] = 4, ["amount"] = 5 });
                if (File.ReadAllBytes(terrainFile).AsSpan().SequenceEqual(savedHeights)) return Fail("the stroke did not reach the data file");
                var asked = await C("open_scene", new JsonObject { ["scene"] = name });
                if ((bool?)asked["unsaved_changes"] != true || ((int?)asked["reload_drops_undo_steps"] ?? 0) < 1) return Fail("open_scene must say what a reload drops: " + asked.ToJsonString());
                var back = await C("open_scene", new JsonObject { ["scene"] = name, ["reload"] = true });
                var restoredFiles = (back["data_files_restored"] as JsonArray)?.Select(x => (string)x).ToList() ?? new System.Collections.Generic.List<string>();
                if ((bool?)back["reloaded"] != true || !restoredFiles.Any(f => f.EndsWith(".vterrain", StringComparison.Ordinal)))
                    return Fail("the reload must put the terrain file back: " + back.ToJsonString());
                if (!File.ReadAllBytes(terrainFile).AsSpan().SequenceEqual(savedHeights)) return Fail("the terrain file is not the saved one");
                await SmokeRegistry.Settle(300);   // the reloaded terrain renders and reads its file
                var height = await C("terrain_height", new JsonObject { ["points"] = new JsonArray(new JsonArray(408, 408)) });
                var at = (height as JsonArray)?[0];
                if (at == null || Math.Abs((double)at["y"]) > 0.01) return Fail("the reverted terrain is not the saved, flat one: " + height?.ToJsonString());

                // ================= a value set without an undo step (an inspector card's control, a gizmo) is unsaved too (#411 review)
                bool Unsaved() => SceneService.Instance.HasUnsavedChanges(test);
                async Task<bool> Listed() => (await C("list_scenes", new JsonObject()) as JsonArray)?.Any(s => (string)s["name"] == name && (bool?)s["unsaved"] == true) == true;
                if (Unsaved()) return Fail("right after the revert the scene is unsaved; first difference: " + FirstDifference(Editor.Core.Serialization.DataSerializer.ToBinary(test), File.ReadAllBytes(path)));
                var probe3 = test.Entities.First(e => e.Name == "Revert Probe");
                SelectionService.Instance.Select(probe3);
                await SmokeRegistry.Settle(200);   // the inspector shows it
                test.IsDirty = false;
                var saved = probe3.Transform.LocalPosition;
                probe3.Transform.LocalPosition = new Vector3(7, 7, 7);
                if (!test.IsDirty) return Fail("a value of the inspected entity set directly did not mark the scene");
                if (!Unsaved() || !await Listed()) return Fail("list_scenes does not call a directly moved entity unsaved");
                if (!SceneService.Instance.PreviewReload(test).Differs) return Fail("Revert Scene would not ask about a directly moved entity");
                probe3.Transform.LocalPosition = saved;
                if (Unsaved()) return Fail("moved back by hand, the scene is its file again — it must not be unsaved; first difference: " + FirstDifference(Editor.Core.Serialization.DataSerializer.ToBinary(test), File.ReadAllBytes(path)));
                SelectionService.Instance.ClearSelection();

                // ================= set_sky on an existing sky: one undo step, unsaved, and exactly saved again when undone
                await C("set_sky", new JsonObject { ["exposure"] = 0.9 });
                await C("save_scene", new JsonObject());
                await C("set_sky", new JsonObject { ["exposure"] = 0.7 });
                if (!Unsaved() || !await Listed() || !test.IsDirty) return Fail("set_sky on the existing sky left the scene saved (dirty " + test.IsDirty + ")");
                var undone = await C("undo", new JsonObject());
                if (Unsaved()) return Fail("set_sky undone, the scene is its file again: " + undone.ToJsonString());

                // ================= a terrain made and deleted again: a revert takes its file (and .vmeta) along
                var made = await C("create_terrain", new JsonObject { ["position"] = new JsonArray(500, 0, 500), ["size"] = 16, ["resolution"] = 33, ["name"] = "Revert Orphan" });
                string orphan = Editor.Core.Services.Terrain.TerrainService.ResolvePath(SceneModel.Resolve("Revert Orphan").GetComponent<Editor.ECS.Components.Rendering.Terrain>()?.DataPath);
                if (orphan == null || !File.Exists(orphan)) return Fail("create_terrain wrote no data file: " + made.ToJsonString());
                await C("delete_entities", new JsonObject { ["entities"] = new JsonArray("Revert Orphan") });
                var gone = await C("open_scene", new JsonObject { ["scene"] = name, ["reload"] = true });
                if ((bool?)gone["reloaded"] != true || File.Exists(orphan) || File.Exists(orphan + ".vmeta"))
                    return Fail("a revert must remove the data file of a terrain made and deleted since the save: " + gone.ToJsonString() + " (file there " + File.Exists(orphan) + ")");
                log.Log("[smoke] mcp scene revert: " + r.ToJsonString() + " | terrain: " + back.ToJsonString() + " | orphan: " + gone.ToJsonString());
                return true;
            }
            catch (Exception ex) { return Fail(ex.Message); }
            finally
            {
                try { if (PlayModeService.Instance.IsPlaying) EditorSession.Instance.Stop(); } catch { }
                while (undo.UndoCount > undoBefore && undo.Undo()) { }
                if (original != null && !ReferenceEquals(project.ActiveScene, original)) EditorSession.Instance.ActivateScene(original);
                if (test != null)
                {
                    try { test.DeactivateEntities(); test.ReleaseEngineScene(); } catch { }
                    project.Scenes.Remove(test);
                }
                foreach (var f in new[] { path, scriptFile, terrainFile })
                {
                    if (f == null) continue;
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                    try { if (File.Exists(f + ".vmeta")) File.Delete(f + ".vmeta"); } catch { }
                }
                EditorCommands.AfterSceneEdit();
                await McpHost.StopAsync();
            }
        }
    }
}
