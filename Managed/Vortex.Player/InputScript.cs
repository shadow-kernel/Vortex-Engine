using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Editor.Core.Input;

namespace Vortex.Player
{
    /// <summary>
    /// Scripted input for automated player runs (`--input=file`): a timed list of key presses, mouse look and
    /// frame captures, so gameplay (movement, firing, reloading, aiming, the debug camera) can be exercised and
    /// photographed without a person at the keyboard. One command per line, `t` in seconds since boot:
    /// <code>
    ///   1.0  press W            # key names as in Input.GetKey: W, Space, LeftShift, R, LButton, RButton, P ...
    ///   2.0  release W
    ///   2.5  hold LButton 0.6   # press now, release 0.6 s later
    ///   3.5  tap R              # 0.12 s press
    ///   4.0  look 40 -10 0.5    # mouse delta in px, spread over 0.5 s (0 = one frame)
    ///   5.0  capture fire.bmp   # frame capture into the capture directory
    ///   6.0  exit
    /// </code>
    /// Scripted keys are OR-ed with the real keyboard; while a script is loaded the window counts as focused.
    /// </summary>
    internal sealed class InputScript
    {
        private struct Cmd { public double T; public string Op; public string Arg; public double Num1, Num2, Num3; }
        private readonly List<Cmd> _cmds = new List<Cmd>();
        private readonly Dictionary<int, double> _held = new Dictionary<int, double>();   // vk -> release time (inf = until release)
        private readonly List<(double until, double dxPerSec, double dyPerSec)> _looks = new List<(double, double, double)>();
        private int _next;
        private double _now;
        public string CaptureDir;
        public bool ExitRequested { get; private set; }
        public int Count => _cmds.Count;

        public static InputScript Load(string path, string captureDir)
        {
            var s = new InputScript { CaptureDir = captureDir };
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw; int hash = line.IndexOf('#'); if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim(); if (line.Length == 0) continue;
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2 || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t)) continue;
                var c = new Cmd { T = t, Op = p[1].ToLowerInvariant(), Arg = p.Length > 2 ? p[2] : "" };
                double N(int i) => p.Length > i && double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
                if (c.Op == "look") { c.Num1 = N(2); c.Num2 = N(3); c.Num3 = N(4); }
                else if (c.Op == "hold") c.Num1 = N(3);
                s._cmds.Add(c);
            }
            s._cmds.Sort((a, b) => a.T.CompareTo(b.T));
            return s;
        }

        public bool IsDown(int vk) => _held.TryGetValue(vk, out double until) && _now <= until;

        /// <summary>Advance to time <paramref name="now"/>; returns the mouse delta to add this frame.</summary>
        public (float dx, float dy) Advance(double now, float dt, Action<string> capture)
        {
            _now = now;
            while (_next < _cmds.Count && _cmds[_next].T <= now)
            {
                var c = _cmds[_next++];
                int vk = KeyNames.VirtualKeyFromName(c.Arg);
                switch (c.Op)
                {
                    case "press": if (vk != 0) _held[vk] = double.PositiveInfinity; break;
                    case "release": if (vk != 0) _held.Remove(vk); break;
                    case "hold": if (vk != 0) _held[vk] = now + Math.Max(0.05, c.Num1); break;
                    case "tap": if (vk != 0) _held[vk] = now + 0.12; break;
                    case "look":
                        if (c.Num3 <= 0.0001) _looks.Add((now + 0.0001, c.Num1 / Math.Max(dt, 0.001), c.Num2 / Math.Max(dt, 0.001)));
                        else _looks.Add((now + c.Num3, c.Num1 / c.Num3, c.Num2 / c.Num3));
                        break;
                    case "capture":
                        try { capture(Path.IsPathRooted(c.Arg) ? c.Arg : Path.Combine(CaptureDir ?? Path.GetTempPath(), c.Arg)); } catch { }
                        break;
                    case "exit": ExitRequested = true; break;
                }
            }
            // expire held keys
            var expired = new List<int>();
            foreach (var kv in _held) if (now > kv.Value) expired.Add(kv.Key);
            foreach (int k in expired) _held.Remove(k);
            // integrate look
            double dx = 0, dy = 0;
            for (int i = _looks.Count - 1; i >= 0; i--)
            {
                var l = _looks[i];
                double span = Math.Min(dt, Math.Max(0, l.until - (now - dt)));
                if (l.until <= now - dt) { _looks.RemoveAt(i); continue; }
                dx += l.dxPerSec * span; dy += l.dyPerSec * span;
                if (l.until <= now) _looks.RemoveAt(i);
            }
            return ((float)dx, (float)dy);
        }
    }
}
