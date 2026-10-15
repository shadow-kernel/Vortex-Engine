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

                // ================= File ▸ Revert Scene: nothing to discard, so it reverts without asking
                await EditorCommands.RevertScene(test);
                await SmokeRegistry.Settle(150);   // the toast is posted
                string toast = EditorCommands.Window?.LastToast ?? "";
                var probe3 = test.Entities.FirstOrDefault(e => e.Name == "Revert Probe");
                if (!toast.StartsWith("Reverted", StringComparison.Ordinal) || probe3 == null || ReferenceEquals(probe3, probe2))
                    return Fail("Revert Scene did not read the scene again: " + toast);
                log.Log("[smoke] mcp scene revert: " + r.ToJsonString());
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
                foreach (var f in new[] { path, scriptFile })
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                    try { if (File.Exists(f + ".vmeta")) File.Delete(f + ".vmeta"); } catch { }
                }
                EditorCommands.AfterSceneEdit();
                await McpHost.StopAsync();
            }
        }
    }
}
