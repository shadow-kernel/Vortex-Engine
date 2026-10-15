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
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Editor smoke check "mcp run player" (#463): a probe behaviour logs the game's arguments (Application.GetArg) and writes a
    /// save game; a scene with it is saved and run in the standalone player over MCP with arguments, an input script, a size and
    /// a render scale. The answer has exit code 0, FPS and frame times, the script's capture and the last frame (the first one
    /// inline as an image); the probe logged the arguments; the save game went to the run's scratch app-data folder, not the
    /// editor's; the editor's own Application.Args stay empty. Bad input (a capture outside the run folder, an unknown scene, a
    /// bad argument name) is refused before anything starts, and a player still running at timeout_seconds is killed.
    /// </summary>
    internal static class McpRunPlayerSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("mcp run player", Run);

        private const string Probe =
            "using Vortex;\n\npublic class McpArgsProbe : VortexBehaviour\n{\n" +
            "    public override void Start()\n    {\n" +
            "        Debug.Log(\"[MCP ARGS] act=\" + Application.GetArg(\"act\", 0) + \" mode=\" + Application.GetArg(\"mode\", \"none\") +\n" +
            "                  \" fast=\" + Application.GetArg(\"fast\", false) + \" count=\" + Application.Args.Count);\n" +
            "        Save.SetInt(\"mcp_probe_act\", Application.GetArg(\"act\", 0));\n" +
            "        Save.Flush();\n    }\n}\n";

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("mcp run player: " + why); return false; }
            var project = ProjectData.Current;
            var original = project?.ActiveScene;
            if (original == null) return Fail("no scene");
            if (McpHost.IsRunning) return Fail("a server is already running");
            if (PlayModeService.Instance.IsPlaying) return Fail("the editor is already playing");
            if (MainWindow.FindPlayerExecutable() == null) return Fail("no Vortex.Player next to the editor (build Managed/Vortex.Player)");
            int port = McpTestClient.FreePort();
            if (!await McpHost.StartAsync(port)) return Fail("server did not start: " + McpHost.LastError);
            string name = "Mcp Player " + Guid.NewGuid().ToString("N").Substring(0, 6);
            string scenePath = Path.Combine(project.Path, "Assets", "Scenes", name + ".vscene");
            string scriptFile = Path.Combine(project.Path, "Assets", "Scripts", "McpArgsProbe.cs");
            var undo = UndoRedoManager.Instance;
            int undoBefore = undo.UndoCount;
            Scene test = null;
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(300) };
            async Task<JsonNode> Raw(string tool, JsonObject args) => await McpTestClient.Call(http, tool, args);
            async Task<JsonNode> C(string tool, JsonObject args) => McpTestClient.Json(await Raw(tool, args));
            async Task<bool> Refused(JsonObject args, string says)
            {
                var r = await Raw("run_player", args);
                return (bool?)r["isError"] == true && McpTestClient.Text(r).Contains(says);
            }
            try
            {
                await McpTestClient.Rpc(http, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "vortex-smoke-player", ["version"] = "1" },
                });
                var ws = await C("write_script", new JsonObject { ["script"] = "McpArgsProbe", ["code"] = Probe });
                if ((bool?)ws["compiles"] != true) return Fail("write_script: " + ws.ToJsonString());

                // ================= a scene on disk: the default camera, light and ground, plus the probe
                var file = SceneService.Instance.CreateDefaultScene(project, name);
                file.FilePath = scenePath;
                file.Save();
                await C("open_scene", new JsonObject { ["scene"] = "Assets/Scenes/" + name + ".vscene" });
                test = project.Scenes.FirstOrDefault(s => s != null && s.Name == name);
                if (test == null || !ReferenceEquals(project.ActiveScene, test)) return Fail("the probe scene did not open");
                string probe = (string)(await C("create_entity", new JsonObject { ["kind"] = "cube", ["name"] = "Args Probe", ["position"] = new JsonArray(0, 0.5, 3) }))["id"];
                await C("attach_script", new JsonObject { ["entity"] = probe, ["script"] = "McpArgsProbe" });
                await C("save_scene", new JsonObject());

                // ================= refused before anything starts
                if (!await Refused(new JsonObject { ["seconds"] = 3, ["input_script"] = "1 capture ../outside.bmp" }, "without folders")) return Fail("a capture outside the run folder was not refused");
                if (!await Refused(new JsonObject { ["scene"] = "No Such Scene" }, "No scene named")) return Fail("an unknown scene was not refused");
                if (!await Refused(new JsonObject { ["args"] = new JsonObject { ["a b"] = 1 } }, "no argument name")) return Fail("a bad argument name was not refused");

                // ================= the run
                var raw = await Raw("run_player", new JsonObject
                {
                    ["seconds"] = 4, ["width"] = 800, ["height"] = 450, ["render_scale"] = 0.75,
                    ["args"] = new JsonObject { ["act"] = 3, ["mode"] = "beat", ["fast"] = true },
                    ["input_script"] = "1 capture first.bmp\n1.5 press W\n2.5 release W",
                });
                if ((bool?)raw["isError"] == true) return Fail("run_player: " + McpTestClient.Text(raw));
                var r = JsonNode.Parse(McpTestClient.Text(raw));
                string runDir = (string)r["run_dir"];
                string playerLog = runDir != null && File.Exists(Path.Combine(runDir, "player.log")) ? File.ReadAllText(Path.Combine(runDir, "player.log")) : "";
                if ((int?)r["exit_code"] != 0 || r["timed_out"] != null) return Fail("the run did not end by itself: " + McpTestClient.Shorten(r.ToJsonString()));
                if (!playerLog.Contains("[MCP ARGS] act=3 mode=beat fast=True count=3")) return Fail("the probe did not read the arguments: " + McpTestClient.Shorten(playerLog));
                if (r["fps"]?["min"] == null || ((int?)r["frame_ms"]?["frames"] ?? 0) <= 0) return Fail("no FPS / frame times: " + McpTestClient.Shorten(r.ToJsonString()));
                var shots = (r["captures"] as JsonArray)?.Select(x => (string)x).ToList();
                if (shots == null || !shots.Any(s => s.EndsWith("first.bmp", StringComparison.Ordinal)) || !shots.Any(s => s.EndsWith("last.bmp", StringComparison.Ordinal)))
                    return Fail("captures: " + r["captures"]?.ToJsonString());
                bool image = (raw["content"] as JsonArray)?.Any(c => (string)c["type"] == "image" && ((string)c["mimeType"] ?? "").StartsWith("image/", StringComparison.Ordinal)) == true;
                if (!image) return Fail("the first capture is not in the answer as an image");
                string scratchSave = Directory.Exists(Path.Combine(runDir, "appdata")) ? Directory.GetFiles(Path.Combine(runDir, "appdata"), "save_slot*.dat", SearchOption.AllDirectories).FirstOrDefault() : null;
                if (scratchSave == null || !File.ReadAllText(scratchSave).Contains("mcp_probe_act")) return Fail("the save game is not in the run's app-data folder");
                string ownGames = Path.Combine(EditorPaths.AppDataRoot(), "VortexGames");
                if (Directory.Exists(ownGames) && Directory.GetFiles(ownGames, "save_slot*.dat", SearchOption.AllDirectories).Any(f => File.ReadAllText(f).Contains("mcp_probe_act")))
                    return Fail("the run wrote into the editor's own app data");
                if (Vortex.Application.Args.Count != 0) return Fail("the editor's Application.Args are not empty");

                // ================= killed at timeout_seconds
                var cut = await C("run_player", new JsonObject { ["seconds"] = 30, ["timeout_seconds"] = 3 });
                if ((bool?)cut["timed_out"] != true || (bool?)cut["killed"] != true || ((double?)cut["wall_seconds"] ?? 99) > 20)
                    return Fail("a player still running at timeout_seconds must be killed: " + McpTestClient.Shorten(cut.ToJsonString()));
                log.Log("[smoke] mcp run player: fps " + r["fps"]?.ToJsonString() + ", frame_ms " + r["frame_ms"]?.ToJsonString());
                return true;
            }
            catch (Exception ex) { return Fail(ex.Message); }
            finally
            {
                while (undo.UndoCount > undoBefore && undo.Undo()) { }
                if (original != null && !ReferenceEquals(project.ActiveScene, original)) EditorSession.Instance.ActivateScene(original);
                if (test != null)
                {
                    try { test.DeactivateEntities(); test.ReleaseEngineScene(); } catch { }
                    project.Scenes.Remove(test);
                }
                foreach (var f in new[] { scenePath, scriptFile })
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
