using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Editor.Core.Input;

namespace Editor.Core.Services.Build
{
    /// <summary>
    /// What an automated standalone-player run needs besides the process (#463, the run_player tool): the player's command line,
    /// a check of an input script before the player sees it (the player skips a line it cannot read), and the numbers of a run —
    /// FPS from the player's 2-second log lines, frame-time percentiles from its <c>--frame-times</c> file.
    /// </summary>
    public static class PlayerRun
    {
        /// <summary>One run of the player for a project.</summary>
        public sealed class Options
        {
            public string Project, Scene;
            public double Seconds = 10;
            public int Width = 1280, Height = 720;
            /// <summary>Below 1 the 3D view renders smaller and is scaled up.</summary>
            public float RenderScale = 1f;
            public string InputScript, CaptureDir, FinalCapture, FrameTimes;
            public IEnumerable<KeyValuePair<string, string>> Args;
            /// <summary>The process the player watches: when it is gone the player closes (0 = none).</summary>
            public int ParentPid;
        }

        /// <summary>The player's command line, one element per argument (for ProcessStartInfo.ArgumentList: nothing is quoted).</summary>
        public static List<string> Arguments(Options o)
        {
            var ci = CultureInfo.InvariantCulture;
            var list = new List<string> { "--project=" + o.Project };
            if (!string.IsNullOrEmpty(o.Scene)) list.Add("--scene=" + o.Scene);
            list.Add("--width=" + o.Width.ToString(ci));
            list.Add("--height=" + o.Height.ToString(ci));
            if (o.RenderScale > 0f && o.RenderScale < 0.999f) list.Add("--renderscale=" + o.RenderScale.ToString("0.###", ci));
            list.Add("--exit-after=" + o.Seconds.ToString("0.###", ci));
            if (!string.IsNullOrEmpty(o.CaptureDir)) list.Add("--capture-dir=" + o.CaptureDir);
            if (!string.IsNullOrEmpty(o.FinalCapture)) list.Add("--capture=" + o.FinalCapture);
            if (!string.IsNullOrEmpty(o.InputScript)) list.Add("--input=" + o.InputScript);
            if (!string.IsNullOrEmpty(o.FrameTimes)) list.Add("--frame-times=" + o.FrameTimes);
            if (o.ParentPid > 0) list.Add("--parent-pid=" + o.ParentPid.ToString(ci));
            if (o.Args != null) foreach (var kv in o.Args) list.Add("--arg:" + kv.Key + "=" + kv.Value);
            return list;
        }

        /// <summary>
        /// For a player this process starts: the engine library and shaders this process runs with (VORTEX_NATIVE_DIR,
        /// VORTEX_SHADER_DIR), so the player loads exactly the editor's engine — also in an installed layout where it would not
        /// find it from its own folder ({app}\Editor\player next to {app}\VortexAPI.dll on Windows).
        /// </summary>
        public static void PassNativeLocation(IDictionary<string, string> environment)
        {
            if (environment == null) return;
            string lib = Editor.Core.Native.NativeLoader.ResolvedLibraryPath;
            string dir = !string.IsNullOrEmpty(lib) ? Path.GetDirectoryName(lib) : Environment.GetEnvironmentVariable("VORTEX_NATIVE_DIR");
            if (!string.IsNullOrEmpty(dir)) environment["VORTEX_NATIVE_DIR"] = dir;
            string shaders = null;
            try { shaders = Editor.Core.Native.NativeLoader.ShaderDirectory; } catch { }
            if (!string.IsNullOrEmpty(shaders) && Directory.Exists(shaders)) environment["VORTEX_SHADER_DIR"] = shaders;
        }

        /// <summary>A name scripts can ask Application.GetArg for: letters, digits, '_', '-' and '.'.</summary>
        public static bool IsArgName(string name) => !string.IsNullOrEmpty(name) && name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.');

        /// <summary>The script's lines exactly as the player reads them (File.ReadAllLines: a line ends at \r, \n or \r\n) — run_player
        /// writes the script it checked in this form, so the player sees no other line (#463 review).</summary>
        public static List<string> ScriptLines(string text)
        {
            var lines = new List<string>();
            if (text == null) return lines;
            using var r = new StringReader(text);
            for (string l; (l = r.ReadLine()) != null;) lines.Add(l);
            return lines;
        }

        /// <summary>
        /// Check an input script with the player's own reader and parser (<see cref="InputScript"/>: <c>&lt;seconds&gt;
        /// press|release|tap &lt;key&gt;</c>, <c>hold &lt;key&gt; [seconds]</c>, <c>look &lt;dx&gt; [dy] [seconds]</c>, <c>scroll</c>,
        /// <c>cursor</c>, <c>capture &lt;name&gt;.bmp</c>, <c>exit</c>; <c>#</c> starts a comment). Null when it is fine, else the
        /// first problem with its line number — a line the player would skip. A capture takes a plain file name (captures land in
        /// the run's own folder); a control character is refused (no line the player could read differently); a command after
        /// <paramref name="runSeconds"/> would never run.
        /// </summary>
        public static string CheckInputScript(string text, double runSeconds = double.MaxValue) => CheckInputScript(text, runSeconds, out _);

        /// <param name="captures">How many frames the script captures.</param>
        public static string CheckInputScript(string text, double runSeconds, out int captures)
        {
            captures = 0;
            if (text == null) return null;
            var lines = ScriptLines(text);
            for (int i = 0; i < lines.Count; i++)
                foreach (char ch in lines[i])
                    if ((char.IsControl(ch) && ch != '\t') || ch == '\u2028' || ch == '\u2029')
                        return "line " + (i + 1) + ": a control character (U+" + ((int)ch).ToString("X4", CultureInfo.InvariantCulture) + ") — one command per line, as plain text.";
            var problems = new List<string>();
            InputScript.Parse(lines, problems);
            if (problems.Count > 0) return problems[0] + ".";
            for (int i = 0; i < lines.Count; i++)
            {
                // the player's parser, one line at a time: what each line does
                foreach (var c in InputScript.Parse(new[] { lines[i] }, null))
                {
                    string at = "line " + (i + 1) + " (\"" + lines[i].Trim() + "\"): ";
                    if (c.T > runSeconds) return at + "never runs: the run ends after " + runSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s (raise seconds).";
                    if (c.Op != "capture") continue;
                    if (!IsPlainFileName(c.Text))
                        return at + "capture takes a plain file name without folders, not a Windows device name (CON, NUL, COM1 …), e.g. \"capture door.bmp\": captures go to the run's capture folder.";
                    if (!c.Text.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)) return at + "a capture is a BMP: name it like \"door.bmp\".";
                    captures++;
                }
            }
            return null;
        }

        /// <summary>Capture commands every <paramref name="every"/> seconds of a run of <paramref name="seconds"/> (auto_0001.bmp …).</summary>
        public static string AutoCaptures(double seconds, double every)
        {
            var sb = new StringBuilder();
            if (every <= 0) return "";
            int n = 0;
            for (double t = every; t < seconds - 0.25; t += every)
                sb.Append(t.ToString("0.###", CultureInfo.InvariantCulture)).Append(" capture auto_").Append((++n).ToString("0000", CultureInfo.InvariantCulture)).Append(".bmp\n");
            return sb.ToString();
        }

        private static readonly Regex FpsLine = new Regex(@"^(?:\d\d:\d\d:\d\d\.\d{3}\s+)?FPS=(\d+)\s", RegexOptions.CultureInvariant);

        /// <summary>The player's FPS line, logged every 2 s: <c>FPS=58 scene=… draws=…</c> (with the log file's time stamp or without).</summary>
        public static bool TryParseFps(string line, out int fps)
        {
            fps = 0;
            if (line == null) return false;
            var m = FpsLine.Match(line);
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out fps);
        }

        /// <summary>A capture name the player writes into its capture folder and nowhere else, on every system: no folder, no root,
        /// no "..", no character a file name cannot hold, no Windows device name (CON, NUL, COM1 … with any extension — writing to
        /// one reaches the device, not a file) and no trailing dot or space (Windows drops them: another name than asked).</summary>
        public static bool IsPlainFileName(string name)
            => !string.IsNullOrWhiteSpace(name) && !Path.IsPathRooted(name) && !name.Contains("..") && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0
               && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.IndexOfAny(WindowsInvalid) < 0 && Path.GetFileName(name) == name
               && name.Trim() == name && !name.EndsWith(".", StringComparison.Ordinal) && !IsWindowsDeviceName(name);

        // what Windows refuses in a file name although macOS and Linux take it
        private static readonly char[] WindowsInvalid = { '<', '>', '"', '|', '?', '*' };

        /// <summary>Whether Windows reads <paramref name="name"/> as a device (CON, PRN, AUX, NUL, COM0–9, LPT0–9, COM¹²³, LPT¹²³,
        /// CONIN$, CONOUT$ — before the first dot, trailing spaces ignored, any case).</summary>
        public static bool IsWindowsDeviceName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            int dot = name.IndexOf('.');
            string stem = (dot >= 0 ? name.Substring(0, dot) : name).TrimEnd(' ').ToUpperInvariant();
            switch (stem)
            {
                case "CON": case "PRN": case "AUX": case "NUL": case "CONIN$": case "CONOUT$": return true;
            }
            if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)))
            {
                char d = stem[3];
                return (d >= '0' && d <= '9') || d == '\u00B9' || d == '\u00B2' || d == '\u00B3';
            }
            return false;
        }

        private static readonly Regex EngineError = new Regex(@"\b(failed|fatal|crash(ed)?|abort(ed)?)\b|\berror\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A line of the player's output that reports an error (#463 review): a Console error entry (<c>[ERROR]</c>), an
        /// exception (one in a frame, an unhandled one), a failure the player reports itself, or an engine line (<c>[subsystem]
        /// …</c>) that says failed, error, fatal or crash. The engine writes all its lines to stderr, so where a line came from says
        /// nothing; a Console line's own level decides for it.</summary>
        public static bool IsErrorLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (line.StartsWith("[ERROR]", StringComparison.Ordinal) || line.StartsWith("[Tick] ", StringComparison.Ordinal)) return true;
            if (line.StartsWith("[INFO]", StringComparison.Ordinal) || line.StartsWith("[WARN]", StringComparison.Ordinal) || line.StartsWith("[SYS]", StringComparison.Ordinal)) return false;
            if (line.IndexOf("Exception", StringComparison.Ordinal) >= 0 || line.IndexOf("Unhandled", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("FAILED", StringComparison.Ordinal) >= 0 || line.StartsWith("Vortex.Player failed", StringComparison.Ordinal)) return true;
            return line.StartsWith("[", StringComparison.Ordinal) && EngineError.IsMatch(line);
        }

        /// <summary>FPS over the run's 2-second windows.</summary>
        public sealed class FpsStats
        {
            public int Min, Max, Samples;
            public double Avg;
            /// <summary>The first window (loading, first frames) was left out — there were later ones.</summary>
            public bool FirstWindowExcluded;
        }

        public static FpsStats Fps(IReadOnlyList<int> windows)
        {
            if (windows == null || windows.Count == 0) return null;
            var use = windows.Count >= 2 ? windows.Skip(1).ToList() : windows.ToList();
            return new FpsStats { Min = use.Min(), Max = use.Max(), Avg = Math.Round(use.Average(), 1), Samples = use.Count, FirstWindowExcluded = windows.Count >= 2 };
        }

        /// <summary>Frame times of the run (ms): average and nearest-rank percentiles.</summary>
        public sealed class FrameStats
        {
            public int Frames;
            public double Avg, P50, P90, P95, P99, Max;
            /// <summary>Milliseconds of the start left out (loading, shader compiles) — only when the run was long enough.</summary>
            public double WarmupExcludedMs;
        }

        /// <summary>Statistics of per-frame times; the first <paramref name="warmupMs"/> of the run are left out when the run is
        /// more than twice as long.</summary>
        public static FrameStats Frames(IReadOnlyList<double> ms, double warmupMs = 2000)
        {
            if (ms == null || ms.Count == 0) return null;
            double total = ms.Sum();
            int skip = 0;
            double skipped = 0;
            if (total > 2 * warmupMs)
                while (skip < ms.Count && skipped < warmupMs) skipped += ms[skip++];
            var use = ms.Skip(skip).Where(v => v >= 0 && !double.IsNaN(v)).OrderBy(v => v).ToList();
            if (use.Count == 0) return null;
            double P(double pct) => use[Math.Clamp((int)Math.Ceiling(pct / 100.0 * use.Count) - 1, 0, use.Count - 1)];
            return new FrameStats
            {
                Frames = use.Count,
                Avg = Math.Round(use.Average(), 2),
                P50 = Math.Round(P(50), 2), P90 = Math.Round(P(90), 2), P95 = Math.Round(P(95), 2), P99 = Math.Round(P(99), 2),
                Max = Math.Round(use[use.Count - 1], 2),
                WarmupExcludedMs = Math.Round(skipped, 1),
            };
        }

        /// <summary>The frame times a player wrote with <c>--frame-times</c>, one number per line (missing file: none).</summary>
        public static List<double> ReadFrameTimes(string path)
        {
            var list = new List<double>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return list;
            try
            {
                foreach (var line in File.ReadLines(path))
                    if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) list.Add(v);
            }
            catch { }
            return list;
        }
    }
}
