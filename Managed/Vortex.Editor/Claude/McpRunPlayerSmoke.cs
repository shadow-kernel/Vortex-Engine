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
    /// save game, a second one starts a helper process that holds the player's output open for 90 s; a scene with both is saved
    /// and run in the standalone player over MCP with arguments, an input script, a size and a render scale. The answer comes
    /// when the player ends — not when the helper does — with exit code 0, FPS and frame times, the script's capture and the
    /// last frame (the first inline as an image), the player it ran and no engine status line counted as an error; while the
    /// player process runs other tools answer, a second run and a capture_viewport are refused (a run over before the probes
    /// could go in skips them, with a log line, when it succeeded), and a moment of editor play during a run is in the answer's
    /// note; the probe read the arguments; the save game went to the run's scratch app-data folder. Refused before anything
    /// starts: a capture outside the run folder (also hidden behind a bare \r), an unknown scene, a bad argument name. A player
    /// still running at timeout_seconds is killed, and one whose parent process is gone closes itself — saying so (#463
    /// review). A failure prints the run: exit code, timeout, the player it ran, its errors and log tail.
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
            "        var psi = System.OperatingSystem.IsWindows() ? new ProcessStartInfo(\"cmd.exe\", \"/c ping -n 90 127.0.0.1\") : new ProcessStartInfo(\"/bin/sleep\", \"90\");\n" +
            "        psi.UseShellExecute = false;\n" +
            "        var p = Process.Start(psi);\n" +
            "        string dir = System.Environment.GetEnvironmentVariable(\"VORTEX_APPDATA_DIR\") ?? \".\";\n" +
            "        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, \"helper.pid\"), p.Id.ToString());\n" +
            "    }\n}\n";

        /// <summary>What a run_player answer says about the run, for a failure: exit code, timeout, the player it ran, its errors
        /// and its last log lines — enough to tell a broken player from a slow runner.</summary>
        internal static string RunSummary(JsonNode r)
        {
            if (r == null) return "(no answer)";
            var tail = (r["log_tail"] as JsonArray)?.Select(l => (string)l).ToList() ?? new System.Collections.Generic.List<string>();
            return "exit_code " + r["exit_code"] + (r["timed_out"] != null ? ", timed out" : "") + (r["killed"] != null ? ", killed" : "")
                 + ", wall " + r["wall_seconds"] + " s, player " + r["player"] + ", run_dir " + r["run_dir"]
                 + ", errors " + (r["errors"]?.ToJsonString() ?? "none") + (r["note"] != null ? ", note: " + r["note"] : "")
                 + ", log tail:\n  " + string.Join("\n  ", tail.Skip(Math.Max(0, tail.Count - 20)));
        }

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

                // ================= the run — other tools answer meanwhile, a second run and a capture are refused
                var clock = Stopwatch.StartNew();
                var running = Raw("run_player", new JsonObject
                {
                    ["seconds"] = 6, ["width"] = 800, ["height"] = 450, ["render_scale"] = 0.75, ["timeout_seconds"] = 90,
                    ["args"] = new JsonObject { ["act"] = 3, ["mode"] = "beat", ["fast"] = true },
                    ["input_script"] = "1 capture first.bmp\n1.5 press W\n2.5 release W",
                });
                // the probes go in while the player process runs — from the moment run_player started it, not after a guess at how
                // long loading takes (a slow runner, WARP): a run that is over by then is no answer to "do tools answer meanwhile"
                var waited = Stopwatch.StartNew();
                while (!Tools.PlayerTools.IsRunning && !running.IsCompleted && waited.Elapsed.TotalSeconds < 60) await Task.Delay(20);
                bool During() => Tools.PlayerTools.IsRunning && !running.IsCompleted;
                bool probed = false, answered = false, second = false, capRefused = false;
                long askedMs = 0;
                JsonNode state = null, cap = null, secondRun = null;
                if (During())
                {
                    var asked = Stopwatch.StartNew();
                    state = await C("get_play_state", new JsonObject());
                    askedMs = asked.ElapsedMilliseconds;
                    answered = askedMs < 3000;
                    probed = During();
                    if (probed)
                    {
                        secondRun = await Raw("run_player", new JsonObject { ["seconds"] = 2 });
                        second = (bool?)secondRun["isError"] == true && McpTestClient.Text(secondRun).Contains("Another run_player");
                        // the viewport rests: a capture is refused at once instead of being written whenever it wakes up
                        cap = await Raw("capture_viewport", new JsonObject());
                        capRefused = (bool?)cap["isError"] == true && McpTestClient.Text(cap).Contains("run_player is running");
                        probed = During() || second;
                    }
                }
                var raw = await running;
                // the run's own answer first: a failed run explains a failed probe, not the other way round
                if ((bool?)raw["isError"] == true) return Fail("run_player: " + McpTestClient.Text(raw));
                var r = JsonNode.Parse(McpTestClient.Text(raw));
                if ((int?)r["exit_code"] != 0 || r["timed_out"] != null) return Fail("the run did not end by itself — " + RunSummary(r));
                if (secondRun != null && (bool?)secondRun["isError"] != true)
                {
                    try { runDirs.Add((string)JsonNode.Parse(McpTestClient.Text(secondRun))["run_dir"]); } catch { }   // the first had ended: it ran
                }
                if (!probed)
                    log.Log("[smoke] mcp run player: the run was over before the probes during it were done (waited " + waited.ElapsedMilliseconds
                            + " ms for the player to start, wall " + r["wall_seconds"] + " s) — skipped them; the run itself succeeded");
                else
                {
                    if (!answered) return Fail("get_play_state waited for the run (" + askedMs + " ms, " + state?.ToJsonString() + ") — " + RunSummary(r));
                    if (!second) return Fail("a second run_player during the first was not refused: " + McpTestClient.Text(secondRun) + " — " + RunSummary(r));
                    if (!capRefused) return Fail("capture_viewport during the run must be refused at once: " + McpTestClient.Text(cap));
                }
                string runDir = (string)r["run_dir"];
                runDirs.Add(runDir);
                string pidFile = runDir != null ? Path.Combine(runDir, "appdata", "helper.pid") : null;
                if (pidFile != null && File.Exists(pidFile)) int.TryParse(File.ReadAllText(pidFile).Trim(), out helperPid);
                // the helper holds the player's output for 90 s: an answer long before that did not wait for it
                if ((double?)r["wall_seconds"] > 75 || clock.Elapsed.TotalSeconds > 80) return Fail("run_player waited for the helper the game started — " + RunSummary(r));
                string playerLog = runDir != null && File.Exists(Path.Combine(runDir, "player.log")) ? File.ReadAllText(Path.Combine(runDir, "player.log")) : "";
                if (!playerLog.Contains("[MCP ARGS] act=3 mode=beat fast=True count=3")) return Fail("the probe did not read the arguments — " + RunSummary(r));
                if (helperPid == 0) return Fail("the helper probe did not start its process — " + RunSummary(r));
                if (r["fps"]?["min"] == null || ((int?)r["frame_ms"]?["frames"] ?? 0) <= 0) return Fail("no FPS / frame times — " + RunSummary(r));
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

                // ================= killed at timeout_seconds — and the editor played for a moment meanwhile: the answer says so
                // although it does not play at the end (the probe's save of that play lands in the editor's app data: it goes after)
                var cutting = C("run_player", new JsonObject { ["seconds"] = 30, ["timeout_seconds"] = 4 });
                await Task.Delay(800);
                bool playStarted = !cutting.IsCompleted;
                await C("enter_play_mode", new JsonObject { ["run_seconds"] = 0.3 });
                await C("exit_play_mode", new JsonObject());
                bool playedDuring = playStarted && !cutting.IsCompleted;
                var cut = await cutting;
                runDirs.Add((string)cut["run_dir"]);
                if ((bool?)cut["timed_out"] != true || (bool?)cut["killed"] != true || ((double?)cut["wall_seconds"] ?? 99) > 20)
                    return Fail("a player still running at timeout_seconds must be killed: " + McpTestClient.Shorten(cut.ToJsonString()));
                if (playedDuring && !((string)cut["note"] ?? "").Contains("played during the run"))
                    return Fail("the editor played during the run and the answer does not say so: " + cut["note"]);
                if (Directory.Exists(ownGames))
                    foreach (var f in Directory.GetFiles(ownGames, "save_slot*.dat", SearchOption.AllDirectories))
                        try { if (File.ReadAllText(f).Contains("mcp_probe_act")) File.Delete(f); } catch { }

                // ================= a player whose parent is gone closes itself (a killed editor leaves no run behind)
                var parent = Process.Start(OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true }
                                                                       : new ProcessStartInfo("/bin/sleep", "1") { UseShellExecute = false });
                var orphanStart = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    // the project's own scene: the probe scene would start another helper
                    ArgumentList = { "--project=" + project.Path, "--scene=" + original.Name, "--exit-after=120", "--width=640", "--height=360", "--parent-pid=" + parent.Id },
                    Environment = { ["VORTEX_APPDATA_DIR"] = Path.Combine(runDir, "appdata-orphan") },
                };
                Editor.Core.Services.Build.PlayerRun.PassNativeLocation(orphanStart.Environment);   // as run_player starts it
                var orphan = Process.Start(orphanStart);
                var orphanOut = new System.Collections.Concurrent.ConcurrentQueue<string>();
                orphan.OutputDataReceived += (s, e) => { if (e.Data != null) orphanOut.Enqueue(e.Data); };
                orphan.ErrorDataReceived += (s, e) => { if (e.Data != null) orphanOut.Enqueue(e.Data); };
                orphan.BeginOutputReadLine();
                orphan.BeginErrorReadLine();
                bool closed = await Task.Run(() => orphan.WaitForExit(60_000));
                string orphanTail = string.Join("\n  ", orphanOut.Reverse().Take(15).Reverse());
                if (!closed) { try { orphan.Kill(true); } catch { } return Fail("a player whose parent is gone kept running:\n  " + orphanTail); }
                await Task.WhenAny(Task.Run(() => orphan.WaitForExit()), Task.Delay(5000));   // the last output lines, if they come
                orphanTail = string.Join("\n  ", orphanOut.Reverse().Take(15).Reverse());
                // closed because the parent went (it says so, or the watch ended it), not because it never ran
                if (!orphanOut.Any(l => l.Contains("is gone — closing")) && orphan.ExitCode != 4)
                    return Fail("the player without a parent ended otherwise (exit code " + orphan.ExitCode + "):\n  " + orphanTail);
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
