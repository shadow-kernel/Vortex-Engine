using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Editor.Core.Input
{
    /// <summary>
    /// A timed input sequence (#457): what the standalone player's <c>--input=file</c> and the editor's MCP tool
    /// send_input both play into <see cref="SimulatedInput"/>, so gameplay (walking, aiming, firing, menus) can be
    /// exercised without a person at the keyboard. One command per line, <c>t</c> in seconds of game time:
    /// <code>
    ///   1.0  press W            # key names as in Input.GetKey: W, Space, LeftShift, R, LButton, RButton, MButton ...
    ///   2.0  release W
    ///   2.5  hold LButton 0.6   # press now, release 0.6 s later
    ///   3.5  tap R              # a 0.12 s press
    ///   4.0  look 40 -10 0.5    # mouse movement in pixels (Input.MouseDeltaX/Y), spread over 0.5 s (0 = one frame)
    ///   4.5  scroll -1          # mouse-wheel notches (Input.ScrollDelta): +1 up / away, -1 down
    ///   4.8  cursor 640 360     # the UI cursor (UI.MouseX/Y, in points): where clicks reach the game's UI
    ///   5.0  capture fire.bmp   # player only: a frame capture into the capture directory
    ///   6.0  exit               # player only: quit
    /// </code>
    /// The editor's send_input takes the same commands as JSON objects too: <c>{"t": 2.5, "look": [40, -10, 0.5]}</c>.
    /// </summary>
    public static class InputScript
    {
        /// <summary>How long <c>tap</c> holds a key.</summary>
        public const double TapSeconds = 0.12;
        /// <summary>The shortest <c>hold</c>.</summary>
        public const double MinHoldSeconds = 0.05;

        /// <summary>The command words, as the errors list them.</summary>
        public const string Ops = "press, release, tap, hold, look, scroll, cursor";

        /// <summary>One timed command. <see cref="T"/> is relative to the start of its sequence until it is queued.</summary>
        public sealed class Command
        {
            public double T;
            /// <summary>press, release, hold, tap, look, scroll, cursor, capture or exit.</summary>
            public string Op;
            /// <summary>The key's name as written (press / release / hold / tap).</summary>
            public string Key;
            /// <summary>The key's virtual-key code (press / release / hold / tap).</summary>
            public int Vk;
            /// <summary>look: dx; scroll: notches; cursor: x.</summary>
            public double X;
            /// <summary>look: dy; cursor: y.</summary>
            public double Y;
            /// <summary>hold / look: duration in seconds.</summary>
            public double Seconds;
            /// <summary>capture: the file name.</summary>
            public string Text;

            /// <summary>When the command is over (the release of a hold or tap, the end of a look).</summary>
            public double End => Op == "tap" ? T + TapSeconds : Op == "hold" || Op == "look" ? T + Seconds : T;
        }

        /// <summary>A player --input file. Lines that cannot be read are skipped and described in <paramref name="problems"/>;
        /// lines read with a default are described in <paramref name="warnings"/>.</summary>
        public static List<Command> ParseFile(string path, List<string> problems, List<string> warnings = null) => Parse(File.ReadAllLines(path), problems, warnings);

        /// <summary>Script lines (<c>"2.5 hold LButton 0.6"</c>, <c>#</c> starts a comment). Lines that cannot be read are
        /// skipped and described in <paramref name="problems"/> ("line 3: …"); the commands come back sorted by time. What
        /// older player scripts left out keeps the old default, said in <paramref name="warnings"/>: a hold without seconds is
        /// the shortest hold (0.05 s), a look without dy moves along x only.</summary>
        public static List<Command> Parse(IEnumerable<string> lines, List<string> problems, List<string> warnings = null)
        {
            var list = new List<Command>();
            int n = 0;
            foreach (string raw in lines ?? Array.Empty<string>())
            {
                n++;
                string line = raw ?? "";
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0) continue;
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string where = "line " + n + " (" + line + ")";
                if (p.Length < 2 || !TryNumber(p[0], out double t) || t < 0) { problems?.Add(where + ": expected '<seconds> <command> …'"); continue; }
                var c = new Command { T = t, Op = p[1].ToLowerInvariant() };
                string error = null;
                double Num(int i, string what)
                {
                    if (p.Length > i && TryNumber(p[i], out double v)) return v;
                    error ??= what + " is missing or not a number";
                    return 0;
                }
                switch (c.Op)
                {
                    case "press": case "release": case "tap":
                        error = Key(c, p.Length > 2 ? p[2] : null);
                        break;
                    case "hold":
                        error = Key(c, p.Length > 2 ? p[2] : null);
                        if (p.Length > 3) c.Seconds = Math.Max(MinHoldSeconds, Num(3, "the hold's seconds"));
                        else
                        {
                            // the player's scripts before #457 read a missing number as 0: the shortest hold
                            c.Seconds = MinHoldSeconds;
                            if (error == null) warnings?.Add(where + ": hold without seconds — held " + MinHoldSeconds.ToString(CultureInfo.InvariantCulture) + " s");
                        }
                        break;
                    case "look":
                        c.X = Num(2, "dx");
                        if (p.Length > 3) c.Y = Num(3, "dy");
                        else if (error == null) warnings?.Add(where + ": look without dy — moves along x only");   // as before #457: dy 0
                        c.Seconds = p.Length > 4 ? Math.Max(0, Num(4, "the look's seconds")) : 0;
                        break;
                    case "scroll":
                        c.X = Num(2, "the notches");
                        break;
                    case "cursor":
                        c.X = Num(2, "x"); c.Y = Num(3, "y");
                        break;
                    case "capture":
                        c.Text = p.Length > 2 ? p[2] : null;
                        if (string.IsNullOrEmpty(c.Text)) error = "the file name is missing";
                        break;
                    case "exit":
                        break;
                    default:
                        error = "unknown command '" + p[1] + "' (" + Ops + ", capture, exit)";
                        break;
                }
                if (error != null) { problems?.Add(where + ": " + error); continue; }
                list.Add(c);
            }
            Sort(list);
            return list;
        }

        /// <summary>send_input's actions: <c>[{"t": 0, "press": "W"}, {"t": 2, "release": "W"}, {"t": 2.1, "tap": "LButton"},
        /// {"t": 2.5, "look": [40, -10, 0.5]}, {"t": 3, "hold": ["LeftShift", 0.8]}, {"t": 4, "scroll": -1},
        /// {"t": 4.5, "cursor": [640, 360]}]</c> — t defaults to 0. Every action that cannot be read is described in
        /// <paramref name="problems"/> ("action 3: …").</summary>
        public static List<Command> FromJson(JsonElement actions, List<string> problems)
        {
            var list = new List<Command>();
            if (actions.ValueKind != JsonValueKind.Array) { problems?.Add("actions must be an array of objects like {\"t\": 0, \"press\": \"W\"}"); return list; }
            int n = 0;
            foreach (var a in actions.EnumerateArray())
            {
                n++;
                string where = "action " + n;
                if (a.ValueKind != JsonValueKind.Object) { problems?.Add(where + ": expected an object like {\"t\": 0, \"press\": \"W\"}"); continue; }
                var c = new Command();
                string error = null;
                string op = null;
                foreach (var prop in a.EnumerateObject())
                {
                    string name = prop.Name.ToLowerInvariant();
                    if (name == "t")
                    {
                        if (!TryNumber(prop.Value, out c.T) || c.T < 0) error ??= "t must be a number of seconds ≥ 0";
                        continue;
                    }
                    if (op != null) { error ??= "one command per action (" + op + " and " + prop.Name + ")"; continue; }
                    op = name;
                    var v = prop.Value;
                    switch (name)
                    {
                        case "press": case "release": case "tap":
                            error ??= Key(c, v.ValueKind == JsonValueKind.String ? v.GetString() : null);
                            break;
                        case "hold":
                            // ["LeftShift", 0.8]
                            if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2 && v[0].ValueKind == JsonValueKind.String && TryNumber(v[1], out double hs))
                            {
                                error ??= Key(c, v[0].GetString());
                                c.Seconds = Math.Max(MinHoldSeconds, hs);
                            }
                            else error ??= "hold expects [\"Key\", seconds]";
                            break;
                        case "look":
                            // [dx, dy] or [dx, dy, seconds]
                            if (v.ValueKind == JsonValueKind.Array && (v.GetArrayLength() == 2 || v.GetArrayLength() == 3)
                                && TryNumber(v[0], out c.X) && TryNumber(v[1], out c.Y) && (v.GetArrayLength() == 2 || TryNumber(v[2], out c.Seconds)))
                                c.Seconds = Math.Max(0, c.Seconds);
                            else error ??= "look expects [dx, dy] or [dx, dy, seconds] (mouse movement in pixels)";
                            break;
                        case "scroll":
                            if (!TryNumber(v, out c.X)) error ??= "scroll expects a number of wheel notches (+1 up / away, -1 down)";
                            break;
                        case "cursor":
                            if (!(v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2 && TryNumber(v[0], out c.X) && TryNumber(v[1], out c.Y)))
                                error ??= "cursor expects [x, y] in UI points (UI.MouseX / MouseY)";
                            break;
                        case "capture": case "exit":
                            error ??= name + " only works in the standalone player's --input file; use capture_viewport after send_input";
                            break;
                        default:
                            error ??= "unknown command '" + prop.Name + "' (" + Ops + ")";
                            break;
                    }
                }
                if (op == null) error ??= "no command (" + Ops + ")";
                c.Op = op;
                if (error != null) { problems?.Add(where + ": " + error); continue; }
                list.Add(c);
            }
            Sort(list);
            return list;
        }

        /// <summary>Key names that work, for the error about one that does not.</summary>
        public const string KeyHint = "names as Input.GetKey takes them: W, A, S, D, Space, LeftShift, LeftCtrl, Escape, Return, Tab, Up, F5, Num1, LButton, RButton, MButton …";

        private static string Key(Command c, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return c.Op + " needs a key";
            c.Key = name.Trim();
            c.Vk = KeyNames.VirtualKeyFromName(c.Key);
            return c.Vk == 0 ? "unknown key '" + name + "' — " + KeyHint : null;
        }

        /// <summary>By time; commands of the same time keep their order (a press before its release) — OrderBy is stable.</summary>
        private static void Sort(List<Command> list)
        {
            var sorted = list.OrderBy(c => c.T).ToList();
            list.Clear();
            list.AddRange(sorted);
        }

        private static bool TryNumber(string s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

        private static bool TryNumber(JsonElement e, out double v)
        {
            v = 0;
            if (e.ValueKind == JsonValueKind.Number) { v = e.GetDouble(); return double.IsFinite(v); }
            return e.ValueKind == JsonValueKind.String && TryNumber(e.GetString(), out v);
        }
    }
}
