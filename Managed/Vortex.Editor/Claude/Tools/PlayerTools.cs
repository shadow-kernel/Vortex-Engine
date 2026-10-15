using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Build;
using Editor.Core.Viewport;
using ModelContextProtocol.Server;
using VortexEditor.Shell;

namespace VortexEditor.Claude.Tools
{
    /// <summary>The standalone player over MCP (#463): run the game the way it ships — its own process, scripted input, the
    /// game's own arguments — and get back what a tester writes down: exit code, FPS, frame times, errors and screenshots.</summary>
    [McpServerToolType, DisplayName("Player")]
    public static class PlayerTools
    {
        /// <summary>Runs kept per project in the temp folder (captures are full-size BMPs).</summary>
        private const int KeepRuns = 5;
        private const int MaxAutoCaptures = 120;

        // ManagesUndo: the call waits for the game — an open undo group would swallow the user's own edits meanwhile
        [McpServerTool(Name = "run_player"), ManagesUndo]
        [Description("Runs the project in the standalone player — the game as it ships, in its own process and window (usually faster than editor " +
                     "play) — for `seconds`, then returns: the exit code; FPS min/avg/max over the player's 2-second windows (the first one, loading, " +
                     "left out) and frame-time percentiles in ms (p50/p90/p95/p99/max); the log tail with error lines marked '!! ' and listed under " +
                     "errors; the captured frames (BMP paths), the first one as an image. input_script plays the game: one command per line, " +
                     "'<seconds> press|release|tap <key>', '<s> hold <key> [seconds]', '<s> look <dx> <dy> [seconds]', '<s> capture <name>.bmp', " +
                     "'<s> exit' (key names as in Input.GetKey; '#' starts a comment). args reach the scripts as Application.Args — " +
                     "Application.GetArg(\"act\", 1) — so a test picks an act or a beat; in editor play they are empty. The player reads the project " +
                     "from disk: unsaved scene edits are not in the run (the note says so — save_scene first). Its save games and settings go to a " +
                     "scratch folder, never the user's. The editor's viewport pauses meanwhile; after timeout_seconds the player is killed. " +
                     "Not while the editor plays.")]
        public static async Task<ToolResult> RunPlayer(
            [Description("Seconds the game runs before the player closes itself (1–600; counted from the first frame, loading not included)")] double seconds = 10,
            [Description("Scene to start in (default: the active scene) — as saved on disk")] string scene = null,
            [Description("Timed input, one command per line, e.g. \"1 press W\\n3 release W\\n3.5 capture door.bmp\"")] string input_script = null,
            [Description("The game's own arguments {\"name\": value}, e.g. {\"act\": 3, \"skipIntro\": true}: scripts read them with Application.GetArg")] JsonElement? args = null,
            [Description("Window width in pixels (320–7680)")] int width = 1280,
            [Description("Window height in pixels (240–4320)")] int height = 720,
            [Description("3D render scale (0.25–1): below 1 the 3D view renders smaller and is scaled up")] float render_scale = 1f,
            [Description("Also capture a frame every this many seconds (0.5 or more; 0 = only the script's captures and the last frame)")] double capture_every = 0,
            [Description("Seconds before the player is killed (default: seconds + 120, for loading and compiling the scripts)")] double timeout_seconds = 0,
            [Description("Lines of the player's log to return (0–400)")] int log_lines = 40,
            CancellationToken ct = default)
        {
            var project = SceneModel.Project;
            if (SceneModel.Playing) throw new ToolError("Stop editor play first (exit_play_mode): the editor's game and the player would share the GPU, and the numbers would be neither's.");
            if (!(seconds >= 1 && seconds <= 600)) throw new ToolError("seconds must be between 1 and 600.");
            if (width < 320 || width > 7680 || height < 240 || height > 4320) throw new ToolError("width must be 320–7680 and height 240–4320 pixels.");
            if (!(render_scale >= 0.25f && render_scale <= 1f)) throw new ToolError("render_scale must be between 0.25 and 1.");
            if (capture_every < 0 || (capture_every > 0 && capture_every < 0.5)) throw new ToolError("capture_every must be 0 (off) or at least 0.5 seconds.");
            if (timeout_seconds < 0 || (timeout_seconds > 0 && timeout_seconds < 1)) throw new ToolError("timeout_seconds must be at least 1 (or 0 for seconds + 120).");
            double timeout = timeout_seconds > 0 ? Math.Min(timeout_seconds, 1800) : seconds + 120;
            log_lines = Math.Clamp(log_lines, 0, 400);

            var target = string.IsNullOrEmpty(scene) ? SceneModel.ActiveScene
                : project.Scenes.FirstOrDefault(s => s != null && string.Equals(s.Name, scene, StringComparison.OrdinalIgnoreCase))
                  ?? throw new ToolError("No scene named '" + scene + "'. Scenes: " + string.Join(", ", project.Scenes.Where(s => s != null).Select(s => s.Name)));
            if (string.IsNullOrEmpty(target.FilePath) || !File.Exists(target.FilePath))
                throw new ToolError("'" + target.Name + "' was never saved: the player reads scenes from disk — save_scene first.");
            string script = input_script ?? "";
            string problem = PlayerRun.CheckInputScript(script, seconds);
            if (problem != null) throw new ToolError("input_script " + problem);
            if (capture_every > 0)
            {
                string auto = PlayerRun.AutoCaptures(seconds, capture_every);
                int n = auto.Count(c => c == '\n');
                if (n > MaxAutoCaptures) throw new ToolError("capture_every " + capture_every + " makes " + n + " captures — at most " + MaxAutoCaptures + ": raise it.");
                script = script + "\n" + auto;
            }
            var gameArgs = GameArgs(args);
            // only the engine's own player, for the open project — the tool takes no program and no project of its own
            string exe = MainWindow.FindPlayerExecutable()
                         ?? throw new ToolError("Vortex.Player was not found next to the editor (build Managed/Vortex.Player, or use an editor build that bundles it).");

            string runDir = NewRunDir(project);
            string captures = Path.Combine(runDir, "captures"), appData = Path.Combine(runDir, "appdata"), frameFile = Path.Combine(runDir, "frame_times.txt");
            string inputFile = null;
            if (script.Trim().Length > 0) { inputFile = Path.Combine(runDir, "input.txt"); File.WriteAllText(inputFile, script); }
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = runDir,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in PlayerRun.Arguments(new PlayerRun.Options
            {
                Project = project.Path, Scene = target.Name, Seconds = seconds, Width = width, Height = height, RenderScale = render_scale,
                InputScript = inputFile, CaptureDir = captures, FinalCapture = Path.Combine(captures, "last.bmp"), FrameTimes = frameFile, Args = gameArgs,
            }))
                psi.ArgumentList.Add(a);
            psi.Environment["VORTEX_APPDATA_DIR"] = appData;   // save games and settings of a test run stay in the run's folder

            var notes = new List<string>();
            try
            {
                if (SceneService.Instance.HasUnsavedChanges(target))
                    notes.Add("The player ran '" + target.Name + "' as saved on disk: the open scene has unsaved changes, which were not in the run (save_scene first to test them).");
            }
            catch { }

            var output = new List<(bool Err, string Text)>();
            var fps = new List<int>();
            var gate = new object();
            void OnLine(string text, bool err)
            {
                if (text == null) return;
                lock (gate)
                {
                    if (output.Count < 50000) output.Add((err, text));
                    if (!err && PlayerRun.TryParseFps(text, out int f)) fps.Add(f);
                }
            }
            bool timedOut = false, killed = false;
            int exitCode = -1;
            var clock = Stopwatch.StartNew();
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (s, e) => OnLine(e.Data, false);
            proc.ErrorDataReceived += (s, e) => OnLine(e.Data, true);
            EditorViewportSession.ActivePreviewDialogs++;   // the editor's viewport rests: the GPU is the player's
            try
            {
                try { if (!proc.Start()) throw new ToolError("The player did not start."); }
                catch (System.ComponentModel.Win32Exception ex) { throw new ToolError("The player did not start: " + ex.Message); }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(timeout));
                try { await proc.WaitForExitAsync(limit.Token); }
                catch (OperationCanceledException)
                {
                    timedOut = !ct.IsCancellationRequested;
                    killed = true;
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
                }
                if (proc.WaitForExit(5000)) proc.WaitForExit();   // the last output lines are handled too
                if (proc.HasExited) exitCode = proc.ExitCode;
            }
            finally { EditorViewportSession.ActivePreviewDialogs = Math.Max(0, EditorViewportSession.ActivePreviewDialogs - 1); }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

            List<(bool Err, string Text)> lines;
            List<int> windows;
            lock (gate) { lines = output.ToList(); windows = fps.ToList(); }
            try { File.WriteAllLines(Path.Combine(runDir, "player.log"), lines.Select(l => (l.Err ? "[stderr] " : "") + l.Text)); } catch { }
            var errorLines = lines.Where(l => PlayerRun.IsErrorLine(l.Text, l.Err)).Select(l => l.Text).ToList();
            var shots = Directory.Exists(captures)
                ? new DirectoryInfo(captures).GetFiles("*.bmp").OrderBy(f => f.LastWriteTimeUtc).Select(f => f.FullName).ToList()
                : new List<string>();
            var fpsStats = PlayerRun.Fps(windows);
            var frames = PlayerRun.Frames(PlayerRun.ReadFrameTimes(frameFile));
            if (timedOut) notes.Add("The player was still running after " + timeout.ToString("0.#", CultureInfo.InvariantCulture) + " s and was killed"
                                    + (timeout < seconds + 5 ? " (timeout_seconds left it no time to load and run " + seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s)." : "."));
            else if (exitCode != 0) notes.Add("The player exited with code " + exitCode + " — see errors and log_tail.");
            if (windows.Count == 0) notes.Add("No FPS line: the run ended before its first 2-second window" + (timedOut ? " (it never got past loading)." : "."));
            // the player starts in its default scene when it cannot find the one asked for — say so instead of reporting its numbers as the scene's
            string started = lines.Select(l => l.Text).FirstOrDefault(l => l.StartsWith("scene: ", StringComparison.Ordinal));
            if (started != null && !started.StartsWith("scene: " + target.Name + " entities=", StringComparison.OrdinalIgnoreCase))
                notes.Add("The player started in another scene (" + started + "): it finds the scenes the project lists and those in Assets/Scenes — keep '" + target.Name + "' there, or Save All so the project lists it.");

            var result = ToolResult.Json(new
            {
                exit_code = exitCode,
                timed_out = timedOut ? (bool?)true : null,
                killed = killed ? (bool?)true : null,
                scene = target.Name,
                wall_seconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
                fps = fpsStats == null ? null : new { min = fpsStats.Min, avg = fpsStats.Avg, max = fpsStats.Max, windows = fpsStats.Samples, first_window_left_out = fpsStats.FirstWindowExcluded },
                fps_log = windows.Count > 0 ? windows.Take(300).ToArray() : null,
                frame_ms = frames == null ? null : new { frames = frames.Frames, avg = frames.Avg, p50 = frames.P50, p90 = frames.P90, p95 = frames.P95, p99 = frames.P99, max = frames.Max, warmup_left_out_ms = frames.WarmupExcludedMs > 0 ? (double?)frames.WarmupExcludedMs : null },
                error_count = errorLines.Count,
                errors = errorLines.Count > 0 ? errorLines.Distinct().Take(30).Select(e => ToolHost.Shorten(e, 600)).ToArray() : null,
                warning_count = lines.Count(l => l.Text.StartsWith("[WARN]", StringComparison.Ordinal)),
                log_tail = log_lines > 0 ? lines.Skip(Math.Max(0, lines.Count - log_lines)).Select(l => (PlayerRun.IsErrorLine(l.Text, l.Err) ? "!! " : "") + ToolHost.Shorten(l.Text, 600)).ToArray() : null,
                captures = shots.Count > 0 ? shots.ToArray() : null,
                run_dir = runDir,
                note = notes.Count > 0 ? string.Join(" ", notes) : null,
            });
            if (shots.Count > 0)
            {
                try { var shot = ViewportCapture.Encode(File.ReadAllBytes(shots[0]), 1280, png: false); result.WithImage(shot.Data, shot.MimeType); }
                catch (Exception ex) { ConsoleService.Instance.LogWarning("run_player: the capture " + Path.GetFileName(shots[0]) + " could not be read: " + ex.Message); }
            }
            return result;
        }

        /// <summary>The game's arguments as name=value pairs: names scripts can ask for, values as text.</summary>
        private static List<KeyValuePair<string, string>> GameArgs(JsonElement? args)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (args == null || args.Value.ValueKind == JsonValueKind.Null || args.Value.ValueKind == JsonValueKind.Undefined) return list;
            if (args.Value.ValueKind != JsonValueKind.Object) throw new ToolError("args is an object {\"name\": value}, e.g. {\"act\": 3, \"skipIntro\": true}.");
            foreach (var p in args.Value.EnumerateObject())
            {
                if (!PlayerRun.IsArgName(p.Name)) throw new ToolError("'" + p.Name + "' is no argument name: letters, digits, '_', '-' and '.'.");
                string v = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "true",
                    _ => throw new ToolError("The value of '" + p.Name + "' must be a string, a number or true/false: scripts read text."),
                };
                if (v.IndexOfAny(new[] { '\n', '\r' }) >= 0) throw new ToolError("The value of '" + p.Name + "' must be one line.");
                list.Add(new KeyValuePair<string, string>(p.Name, v));
            }
            return list;
        }

        /// <summary>A fresh folder for one run (app data, captures, input, frame times, the log) under the temp folder; older runs
        /// of the project beyond the newest few go.</summary>
        private static string NewRunDir(ProjectData project)
        {
            string key;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                key = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(project.Path)))).Substring(0, 8).ToLowerInvariant();
            string name = new string((project.Name ?? "Project").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
            string root = Path.Combine(Path.GetTempPath(), "vortex-run-player", name + "-" + key);
            Directory.CreateDirectory(root);
            try
            {
                foreach (var old in new DirectoryInfo(root).GetDirectories().OrderByDescending(d => d.Name, StringComparer.Ordinal).Skip(KeepRuns - 1))
                    try { old.Delete(true); } catch { }
            }
            catch { }
            string dir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Path.Combine(dir, "appdata"));
            Directory.CreateDirectory(Path.Combine(dir, "captures"));
            return dir;
        }
    }
}
