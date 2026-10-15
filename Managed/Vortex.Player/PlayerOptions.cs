using System;
using System.Collections.Generic;
using System.Globalization;

namespace Vortex.Player
{
    internal sealed class PlayerOptions
    {
        public string ProjectPath;
        public string SceneName;
        public uint Width = 1280;
        public uint Height = 720;
        /// <summary>True when --width/--height were given (otherwise a shipped game uses the project's default resolution).</summary>
        public bool SizeGiven;
        public float RenderScale = 0f;
        public bool ShowHelp;
        /// <summary>Smoke-test mode: close the window after this many seconds (0 = run until closed).</summary>
        public float ExitAfterSeconds = 0f;
        /// <summary>Smoke-test mode: write a frame capture (BMP) to this path shortly before exiting.</summary>
        public string CapturePath;
        /// <summary>Automation: a timed input script (see <see cref="InputScript"/>).</summary>
        public string InputScriptPath;
        /// <summary>Automation: directory for the script's frame captures (default: next to --capture or the temp folder).</summary>
        public string CaptureDir;
        /// <summary>Print the loaded scene's entity tree (components, render layers, scripts) to stdout.</summary>
        public bool DumpScene;
        /// <summary>The game's own arguments, <c>--arg:name=value</c> (#463): scripts read them through Application.Args / GetArg.</summary>
        public readonly Dictionary<string, string> Args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Automation (#463): write each frame's duration in ms to this file, one per line — run_player's frame-time
        /// percentiles. Off unless given.</summary>
        public string FrameTimesPath;
        /// <summary>Automation (#463 review): the process that started this run — the editor of run_player. When it is gone the
        /// player closes itself, so no run outlives a killed editor. 0 = none.</summary>
        public int ParentPid;

        public static PlayerOptions Parse(string[] args)
        {
            var o = new PlayerOptions();
            foreach (string raw in args ?? Array.Empty<string>())
            {
                string a = raw.Trim();
                if (a == "--help" || a == "-h") o.ShowHelp = true;
                else if (a.StartsWith("--project=", StringComparison.OrdinalIgnoreCase)) o.ProjectPath = a.Substring(10).Trim('"');
                else if (a.StartsWith("--scene=", StringComparison.OrdinalIgnoreCase)) o.SceneName = a.Substring(8).Trim('"');
                else if (a.StartsWith("--width=", StringComparison.OrdinalIgnoreCase) && uint.TryParse(a.Substring(8), out uint w)) { o.Width = Math.Max(320u, w); o.SizeGiven = true; }
                else if (a.StartsWith("--height=", StringComparison.OrdinalIgnoreCase) && uint.TryParse(a.Substring(9), out uint h)) { o.Height = Math.Max(240u, h); o.SizeGiven = true; }
                else if (a.StartsWith("--renderscale=", StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(a.Substring(14), NumberStyles.Float, CultureInfo.InvariantCulture, out float s)) o.RenderScale = s;
                else if (a.StartsWith("--exit-after=", StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(a.Substring(13), NumberStyles.Float, CultureInfo.InvariantCulture, out float e)) o.ExitAfterSeconds = e;
                else if (a.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase)) o.CapturePath = a.Substring(10).Trim('"');
                else if (a.StartsWith("--input=", StringComparison.OrdinalIgnoreCase)) o.InputScriptPath = a.Substring(8).Trim('"');
                else if (a.StartsWith("--capture-dir=", StringComparison.OrdinalIgnoreCase)) o.CaptureDir = a.Substring(14).Trim('"');
                else if (a.Equals("--dump-scene", StringComparison.OrdinalIgnoreCase)) o.DumpScene = true;
                else if (a.StartsWith("--frame-times=", StringComparison.OrdinalIgnoreCase)) o.FrameTimesPath = a.Substring(14).Trim('"');
                else if (a.StartsWith("--parent-pid=", StringComparison.OrdinalIgnoreCase) && int.TryParse(a.Substring(13), out int pid)) o.ParentPid = pid;
                else if (Vortex.Application.TryParseArg(raw, out string name, out string value)) o.Args[name] = value;   // the untrimmed token: values stay verbatim
            }
            return o;
        }
    }
}
