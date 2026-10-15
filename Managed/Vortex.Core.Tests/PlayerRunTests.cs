using System;
using System.Collections.Generic;
using System.Linq;
using Editor.Core.Services.Build;

namespace VortexTests
{
    /// <summary>run_player and Application.Args (#463): the game's own arguments come from the player's command line
    /// (<c>--arg:name=value</c>) and stay empty in editor play; an input script is checked before the player sees it; FPS and
    /// frame times come out of the player's log and its --frame-times file.</summary>
    public static class PlayerRunTests
    {
        [Test]
        public static void ScriptsReadTheGamesArguments(TestContext t)
        {
            t.Equal(0, Vortex.Application.Args.Count, "no arguments outside the player (editor play)");
            t.True(Vortex.Application.TryParseArg("--arg:act=3", out var n, out var v) && n == "act" && v == "3", "name=value");
            t.True(Vortex.Application.TryParseArg("--arg:skipIntro", out n, out v) && n == "skipIntro" && v == "true", "a bare name is true");
            t.True(Vortex.Application.TryParseArg("--ARG:path=\"a=b c\"", out n, out v) && n == "path" && v == "\"a=b c\"", "the first '=' splits; the value is verbatim, quotes too");
            t.True(Vortex.Application.TryParseArg("--arg:msg=  two words  ", out n, out v) && v == "  two words  ", "and its spaces: " + v);
            t.False(Vortex.Application.TryParseArg("--arg:=5", out _, out _), "no name, no argument");
            t.False(Vortex.Application.TryParseArg("--scene=Main", out _, out _), "an engine option is no game argument");
            try
            {
                Vortex.Application.SetArgs(new Dictionary<string, string> { ["act"] = "3", ["speed"] = "1.5", ["skipIntro"] = "true", ["name"] = "Zócalo", ["bad"] = "x" });
                t.Equal(3, Vortex.Application.GetArg("Act", 1), "an int, by any case");
                t.Equal(1.5f, Vortex.Application.GetArg("speed", 1f), "a float, invariant");
                t.True(Vortex.Application.GetArg("skipIntro", false) && !Vortex.Application.GetArg("missing", false), "a bool");
                t.Equal("Zócalo", Vortex.Application.GetArg("name"), "text");
                t.Equal("1", Vortex.Application.GetArg("missing", "1"), "the default when missing");
                t.Equal(7, Vortex.Application.GetArg("bad", 7), "the default when it is no number");
                t.True(Vortex.Application.HasArg("SKIPINTRO") && !Vortex.Application.HasArg("missing"), "HasArg");
                t.Equal(5, Vortex.Application.Args.Count, "Args lists them all");
            }
            finally { Vortex.Application.SetArgs(null); }
            t.Equal(0, Vortex.Application.Args.Count, "and they can be cleared again");
        }

        [Test]
        public static void InputScriptsAreCheckedLikeThePlayerReadsThem(TestContext t)
        {
            string good = "# walk to the door\n0.5 press W\n2 release W\n2.2 hold LButton 0.4\n2.5 tap R\n3 look 40 -10 0.5\n3.5 capture door.bmp\n4 exit\n\n";
            t.True(PlayerRun.CheckInputScript(good, 10) == null, "a valid script: " + PlayerRun.CheckInputScript(good, 10));
            t.True(PlayerRun.CheckInputScript(null) == null && PlayerRun.CheckInputScript("") == null, "none at all is fine");
            string Err(string s) => PlayerRun.CheckInputScript(s, 10) ?? "";
            t.True(Err("1 press W\nsoon press W").StartsWith("line 2", StringComparison.Ordinal), "a line without a time, with its number: " + Err("1 press W\nsoon press W"));
            t.True(Err("1 press Shft").Contains("'Shft' is no key"), "an unknown key");
            t.True(Err("1 jump").Contains("'jump' is no command"), "an unknown command");
            t.True(Err("1 look 40").Contains("look takes"), "look needs two numbers");
            t.True(Err("1 capture ../../evil.bmp").Contains("without folders") && Err("1 capture /tmp/x.bmp").Contains("without folders"), "captures stay in the run's folder");
            t.True(Err("1 capture shot.png").Contains("BMP"), "a capture is a BMP");
            t.True(Err("12 exit").Contains("never runs"), "a command after the run ends");

            // the review's payload: a bare \r ends a line for the player (File.ReadAllLines) — the check reads lines the same way
            string payload = "2 press W \r1 capture /Users/someone/.zshrc\n2.5 release W";
            t.True(Err(payload).StartsWith("line 2", StringComparison.Ordinal) && Err(payload).Contains("without folders"), "the hidden second line is checked: " + Err(payload));
            string file = t.Write("input.txt", payload);
            t.True(PlayerRun.ScriptLines(payload).SequenceEqual(System.IO.File.ReadAllLines(file)), "ScriptLines splits like the player's reader");
            t.True(Err("1 press W\u000b").Contains("control character"), "a control character is refused");
            t.True(PlayerRun.CheckInputScript("1 capture a.bmp\n2 capture b.bmp\n3 exit", 10, out int n) == null && n == 2, "the captures are counted: " + n);
            t.True(PlayerRun.IsPlainFileName("door.bmp") && !PlayerRun.IsPlainFileName("../door.bmp") && !PlayerRun.IsPlainFileName("/tmp/door.bmp")
                   && !PlayerRun.IsPlainFileName("a/b.bmp") && !PlayerRun.IsPlainFileName("C:door.bmp") && !PlayerRun.IsPlainFileName(""), "plain file names only");
        }

        [Test]
        public static void TheCommandLineCarriesTheRun(TestContext t)
        {
            var a = PlayerRun.Arguments(new PlayerRun.Options
            {
                Project = "/p", Scene = "Main Scene", Seconds = 20, Width = 1600, Height = 900, RenderScale = 0.75f,
                InputScript = "/r/input.txt", CaptureDir = "/r/captures", FinalCapture = "/r/captures/last.bmp", FrameTimes = "/r/frames.txt",
                Args = new[] { new KeyValuePair<string, string>("act", "3"), new KeyValuePair<string, string>("skipIntro", "true") }, ParentPid = 4242,
            });
            foreach (var want in new[] { "--project=/p", "--scene=Main Scene", "--width=1600", "--height=900", "--renderscale=0.75", "--exit-after=20",
                                         "--input=/r/input.txt", "--capture-dir=/r/captures", "--capture=/r/captures/last.bmp", "--frame-times=/r/frames.txt",
                                         "--parent-pid=4242", "--arg:act=3", "--arg:skipIntro=true" })
                t.True(a.Contains(want), "has " + want + ": " + string.Join(" ", a));
            var full = PlayerRun.Arguments(new PlayerRun.Options { Project = "/p", Seconds = 5 });
            t.False(full.Any(x => x.StartsWith("--renderscale", StringComparison.Ordinal) || x.StartsWith("--input", StringComparison.Ordinal)), "full scale and no script: not passed");
            t.True(PlayerRun.IsArgName("act") && PlayerRun.IsArgName("beat.2") && !PlayerRun.IsArgName("a b") && !PlayerRun.IsArgName("a=b") && !PlayerRun.IsArgName(""), "argument names");
            t.Equal("1 capture auto_0001.bmp\n2 capture auto_0002.bmp\n", PlayerRun.AutoCaptures(2.5, 1), "a capture every second, none at the very end");
        }

        [Test]
        public static void FpsAndFrameTimesComeFromTheRun(TestContext t)
        {
            t.True(PlayerRun.TryParseFps("FPS=58 scene=Main draws=120 drawn=3/4 submit=1.2ms/40 gc=0/0", out int f) && f == 58, "the player's FPS line");
            t.True(PlayerRun.TryParseFps("12:00:01.123  FPS=61 scene=x", out f) && f == 61, "as the log file has it");
            t.False(PlayerRun.TryParseFps("[INFO] FPS=3 is what my script says", out _), "a script's own log line is no FPS line");
            var fps = PlayerRun.Fps(new[] { 12, 58, 60, 62 });
            t.True(fps.Min == 58 && fps.Max == 62 && fps.Avg == 60 && fps.Samples == 3 && fps.FirstWindowExcluded, "the loading window is left out");
            t.True(PlayerRun.Fps(new[] { 30 }).Samples == 1 && !PlayerRun.Fps(new[] { 30 }).FirstWindowExcluded, "one window counts");
            t.True(PlayerRun.Fps(new int[0]) == null, "no window, no FPS");

            var ms = new List<double>();
            for (int i = 0; i < 15; i++) ms.Add(100);    // 1.5 s of loading hitches
            for (int i = 0; i < 1000; i++) ms.Add(i % 100 == 99 ? 40 : 16);   // then 1 % hitches of 40 ms
            var st = PlayerRun.Frames(ms);
            t.True(st.WarmupExcludedMs >= 2000 && st.Frames < 1000, "the first 2 s are left out: " + st.WarmupExcludedMs);
            t.True(st.P50 == 16 && st.P90 == 16 && st.P99 == 40 && st.Max == 40, "percentiles p50 " + st.P50 + ", p90 " + st.P90 + ", p99 " + st.P99 + ", max " + st.Max);
            var shortRun = PlayerRun.Frames(new double[] { 20, 16, 16 });
            t.True(shortRun.Frames == 3 && shortRun.WarmupExcludedMs == 0, "a short run keeps every frame");
            t.True(PlayerRun.Frames(new double[0]) == null, "no frames, no numbers");
        }

        [Test]
        public static void ErrorLinesAreTheOnesToRead(TestContext t)
        {
            t.True(PlayerRun.IsErrorLine("[ERROR] Door.cs: NullReferenceException") && PlayerRun.IsErrorLine("[Tick] System.InvalidOperationException: x"), "error entries and exceptions");
            t.True(PlayerRun.IsErrorLine("Unhandled exception. System.Exception: boom") && PlayerRun.IsErrorLine("hot-reload FAILED: CS1002"), "a crash, a failed reload");
            t.True(PlayerRun.IsErrorLine("[sdlgpu] shader compile failed: lit.metal") && PlayerRun.IsErrorLine("[gamehost] fatal: no window"), "an engine line that says it failed");
            // the engine writes every line to stderr: its ready messages are no errors (the review counted 14 on a clean run)
            t.False(PlayerRun.IsErrorLine("[physics] Jolt Physics world ready (10 worker threads, 32768 max bodies)") || PlayerRun.IsErrorLine("[VortexAudio] shut down")
                    || PlayerRun.IsErrorLine("[navigation] baked with 0 errors"), "engine status lines are not");
            t.False(PlayerRun.IsErrorLine("[INFO] the door failed to open (a script's own log)") || PlayerRun.IsErrorLine("FPS=60 scene=x") || PlayerRun.IsErrorLine(""), "a Console line's level decides; the rest is not");
            var path = t.Write("frames.txt", "16.7\n17.1\nnot a number\n33.4\n");
            t.Equal(3, PlayerRun.ReadFrameTimes(path).Count, "the frame-time file, one number per line");
            t.Equal(0, PlayerRun.ReadFrameTimes(t.Path("missing.txt")).Count, "a missing file: none");
        }
    }
}
