using System;

namespace Editor.Core.Input
{
    /// <summary>One controller snapshot in the XInput button/axis convention the gameplay API already uses.</summary>
    public struct GamepadState
    {
        public bool Connected;
        public ushort Buttons;      // XInput bit layout: 0x1000 A, 0x2000 B, 0x4000 X, 0x8000 Y, 0x0100 LB, 0x0200 RB, ...
        public float LeftX, LeftY, RightX, RightY;   // -1..1, dead zone applied
        public float LeftTrigger, RightTrigger;      // 0..1
    }

    /// <summary>
    /// The host shell (native GameHost, Avalonia editor, WPF editor) plugs its physical input sources in here;
    /// the shared runtime never talks to an OS API directly. Every hook has a safe default.
    /// </summary>
    public static class HostInput
    {
        /// <summary>Physical key state by Windows virtual-key code (the contract the scripts use).</summary>
        public static Func<int, bool> KeyDown;
        /// <summary>True while the game/editor window is the focused window.</summary>
        public static Func<bool> WindowFocused;
        /// <summary>The first connected controller's state for this tick.</summary>
        public static Func<GamepadState> Gamepad;
        /// <summary>Caps Lock toggle state (debug freecam: hand input to the player while flying).</summary>
        public static Func<bool> CapsLock;

        public static bool IsKeyDown(int vk) { var f = KeyDown; return f != null && f(vk); }

        // ---- per-tick press latch (#337): a key tapped and released BETWEEN two script ticks is still seen as down
        // for the following tick. Hosts call NotifyKeyDown from their key-down events; the script runtime calls
        // BeginTick once at the start of every update, which turns the presses collected since the previous tick
        // into this tick's latch.
        private static readonly object _latchLock = new object();
        private static System.Collections.Generic.HashSet<int> _pressedPending = new System.Collections.Generic.HashSet<int>();
        private static System.Collections.Generic.HashSet<int> _pressedThisTick = new System.Collections.Generic.HashSet<int>();

        public static void NotifyKeyDown(int vk) { if (vk != 0) lock (_latchLock) _pressedPending.Add(vk); }

        /// <summary>Physically down now, or pressed since the previous script tick.</summary>
        public static bool IsKeyDownThisTick(int vk)
        {
            if (IsKeyDown(vk)) return true;
            lock (_latchLock) return _pressedThisTick.Contains(vk);
        }

        /// <summary>Start of a script tick: the presses since the previous tick become this tick's latch.</summary>
        public static void BeginTick()
        {
            lock (_latchLock)
            {
                var t = _pressedThisTick;
                _pressedThisTick = _pressedPending;
                t.Clear();
                _pressedPending = t;
            }
        }

        public static bool IsWindowFocused() { var f = WindowFocused; return f == null || f(); }
        public static GamepadState PollGamepad() { var f = Gamepad; return f != null ? f() : default(GamepadState); }
        public static bool IsCapsLockOn() { var f = CapsLock; return f != null && f(); }
    }

    /// <summary>Framework-neutral 2D point (replaces System.Windows.Point in the shared core).</summary>
    public struct PointD
    {
        public double X;
        public double Y;
        public PointD(double x, double y) { X = x; Y = y; }
        public override string ToString() => $"({X}, {Y})";
    }

    /// <summary>
    /// Maps the key names gameplay scripts pass to Input.GetKey (the WPF Key enum spelling: "W", "Space",
    /// "LeftShift", "D1", "F5", "OemPeriod", ...) to Windows virtual-key codes, which every host translates
    /// to its own key state. 0 = unknown name.
    /// </summary>
    public static class KeyNames
    {
        private static readonly string[] DigitPrefixes = { "Num", "Alpha", "Digit", "Key" };

        public static int VirtualKeyFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            string n = name.Trim();
            if (n.Length == 0) return 0;   // whitespace-only: the F-key check below indexed n[0] and threw
            if (n.Length == 1)
            {
                char c = char.ToUpperInvariant(n[0]);
                if (c >= 'A' && c <= 'Z') return c;
                if (c >= '0' && c <= '9') return c;
            }
            if (n.Length == 2 && (n[0] == 'D' || n[0] == 'd') && n[1] >= '0' && n[1] <= '9') return n[1];
            if ((n[0] == 'F' || n[0] == 'f') && n.Length <= 3 && int.TryParse(n.Substring(1), out int fn) && fn >= 1 && fn <= 24)
                return 0x70 + (fn - 1);
            if (n.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && n.Length == 7 && n[6] >= '0' && n[6] <= '9')
                return 0x60 + (n[6] - '0');
            // The spellings scripts naturally reach for (the public KeyCode enum, Unity habits) (#336):
            // Num1 / Alpha1 / Digit1 / Key1 = the number-row "1", Keypad1 = the numeric keypad.
            for (int i = 0; i < DigitPrefixes.Length; i++)
            {
                string p = DigitPrefixes[i];
                if (n.Length == p.Length + 1 && n.StartsWith(p, StringComparison.OrdinalIgnoreCase) && n[p.Length] >= '0' && n[p.Length] <= '9')
                    return n[p.Length];
            }
            if (n.StartsWith("Keypad", StringComparison.OrdinalIgnoreCase) && n.Length == 7 && n[6] >= '0' && n[6] <= '9')
                return 0x60 + (n[6] - '0');
            switch (n.ToLowerInvariant())
            {
                case "space": return 0x20;
                case "escape": case "esc": return 0x1B;
                case "return": case "enter": return 0x0D;
                case "tab": return 0x09;
                case "back": case "backspace": return 0x08;
                case "delete": return 0x2E;
                case "insert": return 0x2D;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pageup": case "prior": return 0x21;
                case "pagedown": case "next": return 0x22;
                case "left": return 0x25;
                case "up": return 0x26;
                case "right": return 0x27;
                case "down": return 0x28;
                case "capslock": case "capital": return 0x14;
                case "leftshift": case "lshift": return 0xA0;
                case "rightshift": case "rshift": return 0xA1;
                case "shift": return 0x10;
                case "leftctrl": case "lctrl": case "leftcontrol": return 0xA2;
                case "rightctrl": case "rctrl": case "rightcontrol": return 0xA3;
                case "ctrl": case "control": return 0x11;
                case "leftalt": case "lalt": return 0xA4;
                case "rightalt": case "ralt": return 0xA5;
                case "alt": return 0x12;
                case "lwin": case "leftwindows": case "lcmd": return 0x5B;
                case "rwin": case "rightwindows": case "rcmd": return 0x5C;
                case "oemtilde": case "grave": return 0xC0;
                case "oemminus": case "minus": return 0xBD;
                case "oemplus": case "plus": case "equals": return 0xBB;
                case "oemopenbrackets": return 0xDB;
                case "oemclosebrackets": case "oem6": return 0xDD;
                case "oemsemicolon": case "oem1": return 0xBA;
                case "oemquotes": case "oem7": return 0xDE;
                case "oemcomma": return 0xBC;
                case "oemperiod": return 0xBE;
                case "oemquestion": case "oem2": return 0xBF;
                case "oembackslash": case "oem5": case "oem102": return 0xDC;
                case "add": return 0x6B;
                case "subtract": return 0x6D;
                case "multiply": return 0x6A;
                case "divide": return 0x6F;
                case "decimal": return 0x6E;
                case "pause": return 0x13;
                case "lbutton": case "mouse0": case "leftmouse": return 0x01;
                case "rbutton": case "mouse1": case "rightmouse": return 0x02;
                case "mbutton": case "mouse2": case "middlemouse": return 0x04;
                default: return 0;
            }
        }
    }
}
