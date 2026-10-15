using System;
using System.Diagnostics;
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
    /// save game, a second one starts a helper process that holds the player's output open for 40 s; a scene with both is saved
    /// and run in the standalone player over MCP with arguments, an input script, a size and a render scale. The answer comes
    /// when the player ends — not when the helper does — with exit code 0, FPS and frame times, the script's capture and the
    /// last frame (the first inline as an image), the player it ran and no engine status line counted as an error; other tools
    /// answer meanwhile and a second run is refused; the probe read the arguments; the save game went to the run's scratch
    /// app-data folder. Refused before anything starts: a capture outside the run folder (also hidden behind a bare \r), an
    /// unknown scene, a bad argument name. A player still running at timeout_seconds is killed, and one whose parent process
    /// is gone closes itself (#463 review).
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

        // a helper the game starts inherits the player's output pipes and outlives it: run_player must not wait for it
        private const string Helper =
            "using Vortex;\nusing System.Diagnostics;\n\npublic class McpHelperProbe : VortexBehaviour\n{\n" +
            "    public override void Start()\n    {\n" +
            "        var psi = System.OperatingSystem.IsWindows() ? new ProcessStartInfo(\"cmd.exe\", \"/c ping -n 40 127.0.0.1\") : new ProcessStartInfo(\"/bin/sleep\", \"40\");\n" +
            "        psi.UseShellExecute = false;\n" +
            "        var p = Process.Start(psi);\n" +
            "        string dir = System.Environment.GetEnvironmentVariable(\"VORTEX_APPDATA_DIR\") ?? \".\";\n" +
            "        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, \"helper.pid\"), p.Id.ToString());\n" +
            "    }\n}\n";

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("mcp run player: " + why); return false; }
            var project = ProjectData.Current;
            var original = project?.ActiveScene;
            if (original == null) return Fail("no scene");
            if (McpHost.IsRunning) return Fail("a server is already running");
            if (PlayModeService.Instance.IsPlaying) return Fail("the editor is already playing");
            string exe = MainWindow.FindPlayerExecutable();
            if (exe == null) return Fail("no Vortex.Player next to the editor (build Managed/Vortex.Player)");
            int port = McpTestClient.FreePort();
            if (!await McpHost.StartAsync(port)) return Fail("server did not start: " + McpHost.LastError);
            string name = "Mcp Player " + Guid.NewGuid().ToString("N").Substring(0, 6);
            string scenePath = Path.Combine(project.Path, "Assets", "Scenes", name + ".vscene");
            string[] scripts = { Path.Combine(project.Path, "Assets", "Scripts", "McpArgsProbe.cs"), Path.Combine(project.Path, "Assets", "Scripts", "McpHelperProbe.cs") };
            string victim = Path.Combine(Path.GetTempPath(), "vortex-smoke-victim-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");
            var undo = UndoRedoManager.Instance;
            int undoBefore = undo.UndoCount;
            Scene test = null;
            int helperPid = 0;
            var runDirs = new System.Collections.Generic.List<string>();
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
                foreach (var (script, code) in new[] { ("McpArgsProbe", Probe), ("McpHelperProbe", Helper) })
                {
                    var ws = await C("write_script", new JsonObject { ["script"] = script, ["code"] = code });
                    if ((bool?)ws["compiles"] != true) return Fail("write_script " + script + ": " + ws.ToJsonString());
                }

                // ================= a scene on disk: the default camera, light and ground, plus the probes
                var file = SceneService.Instance.CreateDefaultScene(project, name);
                file.FilePath = scenePath;
                file.Save();
                await C("open_scene", new JsonObject { ["scene"] = "Assets/Scenes/" + name + ".vscene" });
                test = project.Scenes.FirstOrDefault(s => s != null && s.Name == name);
                if (test == null || !ReferenceEquals(project.ActiveScene, test)) return Fail("the probe scene did not open");
                string probe = (string)(await C("create_entity", new JsonObject { ["kind"] = "cube", ["name"] = "Args Probe", ["position"] = new JsonArray(0, 0.5, 3) }))["id"];
                await C("attach_script", new JsonObject { ["entity"] = probe, ["script"] = "McpArgsProbe" });
                await C("attach_script", new JsonObject { ["entity"] = probe, ["script"] = "McpHelperProbe" });
                await C("save_scene", new JsonObject());

                // ================= refused before anything starts
                File.WriteAllText(victim, "keep me");
                string hidden = "2 press W \r1 capture " + victim.Replace('\\', '/') + "\n2.5 release W";   // the review's payload: a bare \r ends a line for the player
                if (!await Refused(new JsonObject { ["seconds"] = 3, ["input_script"] = hidden }, "without folders")) return Fail("a capture hidden behind a bare \\r was not refused");
                if (File.ReadAllText(victim) != "keep me") return Fail("the victim file was overwritten");
                if (!await Refused(new JsonObject { ["seconds"] = 3, ["input_script"] = "1 capture ../outside.bmp" }, "without folders")) return Fail("a capture outside the run folder was not refused");
                if (!await Refused(new JsonObject { ["scene"] = "No Such Scene" }, "No scene named")) return Fail("an unknown scene was not refused");
                if (!await Refused(new JsonObject { ["args"] = new JsonObject { ["a b"] = 1 } }, "no argument name")) return Fail("a bad argument name was not refused");
                if (!await Refused(new JsonObject { ["seconds"] = 10, ["capture_every"] = 0.5, ["width"] = 3840, ["height"] = 2160 }, "at most")) return Fail("a run capturing gigabytes was not refused");

                // ================= the run — other tools answer meanwhile, a second run is refused
                var clock = Stopwatch.StartNew();
                var running = Raw("run_player", new JsonObject
                {
                    ["seconds"] = 4, ["width"] = 800, ["height"] = 450, ["render_scale"] = 0.75, ["timeout_seconds"] = 60,
                    ["args"] = new JsonObject { ["act"] = 3, ["mode"] = "beat", ["fast"] = true },
                    ["input_script"] = "1 capture first.bmp\n1.5 press W\n2.5 release W",
                });
                await Task.Delay(1500);
                var asked = Stopwatch.StartNew();
                var state = await C("get_play_state", new JsonObject());
                bool answered = asked.ElapsedMilliseconds < 3000 && !running.IsCompleted;
                bool second = await Refused(new JsonObject { ["seconds"] = 2 }, "Another run_player");
                var raw = await running;
                if (!answered) return Fail("get_play_state waited for the run (" + asked.ElapsedMilliseconds + " ms, run done " + running.IsCompleted + ", " + state.ToJsonString() + ")");
                if (!second) return Fail("a second run_player during the first was not refused");
                if ((bool?)raw["isError"] == true) return Fail("run_player: " + McpTestClient.Text(raw));
                var r = JsonNode.Parse(McpTestClient.Text(raw));
                string runDir = (string)r["run_dir"];
                runDirs.Add(runDir);
                string pidFile = runDir != null ? Path.Combine(runDir, "appdata", "helper.pid") : null;
                if (pidFile != null && File.Exists(pidFile)) int.TryParse(File.ReadAllText(pidFile).Trim(), out helperPid);
                if ((double?)r["wall_seconds"] > 40 || clock.Elapsed.TotalSeconds > 45) return Fail("run_player waited for the helper the game started: " + r["wall_seconds"]);
                string playerLog = runDir != null && File.Exists(Path.Combine(runDir, "player.log")) ? File.ReadAllText(Path.Combine(runDir, "player.log")) : "";
                if ((int?)r["exit_code"] != 0 || r["timed_out"] != null) return Fail("the run did not end by itself: " + McpTestClient.Shorten(r.ToJsonString()));
                if (!playerLog.Contains("[MCP ARGS] act=3 mode=beat fast=True count=3")) return Fail("the probe did not read the arguments: " + McpTestClient.Shorten(playerLog));
                if (helperPid == 0) return Fail("the helper probe did not start its process");
                if (r["fps"]?["min"] == null || ((int?)r["frame_ms"]?["frames"] ?? 0) <= 0) return Fail("no FPS / frame times: " + McpTestClient.Shorten(r.ToJsonString()));
                if ((r["errors"] as JsonArray)?.Any(x => ((string)x).Contains(" ready") || ((string)x).Contains("initialized")) == true)
                    return Fail("engine status lines counted as errors: " + r["errors"]?.ToJsonString());
                if ((string)r["player"] != exe) return Fail("the answer does not name the player it ran: " + r["player"]);
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
                runDirs.Add((string)cut["run_dir"]);
                if ((bool?)cut["timed_out"] != true || (bool?)cut["killed"] != true || ((double?)cut["wall_seconds"] ?? 99) > 20)
                    return Fail("a player still running at timeout_seconds must be killed: " + McpTestClient.Shorten(cut.ToJsonString()));

                // ================= a player whose parent is gone closes itself (a killed editor leaves no run behind)
                var parent = Process.Start(OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true }
                                                                       : new ProcessStartInfo("/bin/sleep", "1") { UseShellExecute = false });
                var orphan = Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    // the project's own scene: the probe scene would start another helper
                    ArgumentList = { "--project=" + project.Path, "--scene=" + original.Name, "--exit-after=120", "--width=640", "--height=360", "--parent-pid=" + parent.Id },
                    Environment = { ["VORTEX_APPDATA_DIR"] = Path.Combine(runDir, "appdata-orphan") },
                });
                orphan.OutputDataReceived += (s, e) => { };
                orphan.ErrorDataReceived += (s, e) => { };
                orphan.BeginOutputReadLine();
                orphan.BeginErrorReadLine();
                bool closed = await Task.Run(() => orphan.WaitForExit(60_000));
                if (!closed) { try { orphan.Kill(true); } catch { } return Fail("a player whose parent is gone kept running"); }
                log.Log("[smoke] mcp run player: fps " + r["fps"]?.ToJsonString() + ", frame_ms " + r["frame_ms"]?.ToJsonString() + ", errors " + r["error_count"]);
                return true;
            }
            catch (Exception ex) { return Fail(ex.Message); }
            finally
            {
                // every helper a run's probe started (it outlives its player on purpose)
                foreach (var dir in runDirs.Where(d => d != null))
                    foreach (var f in Directory.Exists(dir) ? Directory.GetFiles(dir, "helper.pid", SearchOption.AllDirectories) : new string[0])
                        if (int.TryParse(File.ReadAllText(f).Trim(), out int pid) && pid > 0)
                        {
                            try { using var h = Process.GetProcessById(pid); h.Kill(entireProcessTree: true); } catch { }
                        }
                if (helperPid > 0) { try { using var h = Process.GetProcessById(helperPid); h.Kill(entireProcessTree: true); } catch { } }
                while (undo.UndoCount > undoBefore && undo.Undo()) { }
                if (original != null && !ReferenceEquals(project.ActiveScene, original)) EditorSession.Instance.ActivateScene(original);
                if (test != null)
                {
                    try { test.DeactivateEntities(); test.ReleaseEngineScene(); } catch { }
                    project.Scenes.Remove(test);
                }
                foreach (var f in scripts.Concat(new[] { scenePath, victim }))
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
