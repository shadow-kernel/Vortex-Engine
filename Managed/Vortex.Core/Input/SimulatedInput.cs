using System;
using System.Collections.Generic;

namespace Editor.Core.Input
{
    /// <summary>
    /// Input that a tool or an automated run plays into the game (#457) — the editor's send_input, the player's
    /// <c>--input</c> file. It never synthesizes OS events and never moves the real cursor: <see cref="HostInput"/> ORs
    /// its keys and mouse buttons with the physical ones (Input.GetKey, GetKeyDown / Up, the UI's keys), and the host adds
    /// its mouse movement and wheel notches to Input.MouseDeltaX / Y and Input.ScrollDelta and its UI cursor to
    /// UI.MouseX / Y each tick (<see cref="Advance"/>, before the scripts run). Unlike the physical keyboard, mouse and pads
    /// it reaches the game while another app is in front — the focus gate applies per source.
    /// A key pressed in a tick stays down for that whole tick, so a press and its release within one frame is still one
    /// GetKeyDown. Runs on the host's game thread (the editor's UI thread); the commands keep the game clock of
    /// <see cref="Advance"/>, so a paused game pauses the sequence too.
    /// </summary>
    public sealed class SimulatedInput
    {
        private struct Look { public double Start, End, Dx, Dy; }

        private readonly List<InputScript.Command> _pending = new List<InputScript.Command>();   // by time, on this clock
        private readonly Dictionary<int, double> _held = new Dictionary<int, double>();         // vk -> release time (∞ = until released)
        private readonly List<int> _pressedNow = new List<int>();     // pressed in the running Advance: down for this tick at least
        private readonly List<int> _releaseNext = new List<int>();    // released (or run out) in the Advance that pressed them
        private readonly List<Look> _looks = new List<Look>();
        private readonly List<int> _expired = new List<int>();
        private double _scroll;
        private double _now;

        /// <summary>The clock of the last <see cref="Advance"/> (game seconds).</summary>
        public double Now => _now;
        /// <summary>How many ticks advanced the input since the last <see cref="Clear"/>.</summary>
        public long Frames { get; private set; }
        /// <summary>A command asked the player to quit (<c>exit</c>).</summary>
        public bool ExitRequested { get; private set; }

        /// <summary>The UI cursor was placed (<c>cursor x y</c>): the host reports <see cref="CursorX"/> / <see cref="CursorY"/>
        /// as UI.MouseX / Y until the real pointer moves or the input is cleared.</summary>
        public bool HasCursor { get; private set; }
        public float CursorX { get; private set; }
        public float CursorY { get; private set; }

        /// <summary>Something still plays: queued commands, held keys, a mouse movement in progress — or the last
        /// <see cref="Advance"/> produced mouse movement or wheel notches, which the tick it belongs to still has to read.</summary>
        public bool Active => _pending.Count > 0 || _held.Count > 0 || _looks.Count > 0 || _releaseNext.Count > 0 || _produced;
        private bool _produced;

        /// <summary>A key pressed and run out within the same tick is still down for that tick and goes up in the next
        /// <see cref="Advance"/>: true until then. A caller waiting for a sequence to finish waits for this too — after a long
        /// frame the clock can be past the sequence's end while its last tap is still held (#457).</summary>
        public bool ReleasePending => _releaseNext.Count > 0;

        /// <summary>Is this virtual key (keyboard key or mouse button) held by the simulation?</summary>
        public bool IsDown(int vk) => _held.Count != 0 && _held.ContainsKey(vk);

        /// <summary>The keys that went down in the last <see cref="Advance"/> — key events for a UI that reads them.</summary>
        public IReadOnlyList<int> PressedThisTick => _pressedNow;

        /// <summary>The virtual keys held now.</summary>
        public int[] HeldKeys() { var k = new int[_held.Count]; _held.Keys.CopyTo(k, 0); Array.Sort(k); return k; }

        /// <summary>Queue commands whose times count from now (the clock of the last <see cref="Advance"/>). Returns the
        /// clock time at which the last of them is over.</summary>
        public double Enqueue(IEnumerable<InputScript.Command> commands) => Enqueue(commands, _now);

        /// <summary>Queue commands whose times count from <paramref name="offset"/> on this clock. Returns the clock time at
        /// which the last of them is over.</summary>
        public double Enqueue(IEnumerable<InputScript.Command> commands, double offset)
        {
            double end = offset;
            if (commands == null) return end;
            foreach (var src in commands)
            {
                if (src == null) continue;
                var c = new InputScript.Command { T = offset + src.T, Op = src.Op, Key = src.Key, Vk = src.Vk, X = src.X, Y = src.Y, Seconds = src.Seconds, Text = src.Text };
                // after every queued command of the same or an earlier time: a press stays before its release
                int i = _pending.Count;
                while (i > 0 && _pending[i - 1].T > c.T) i--;
                _pending.Insert(i, c);
                end = Math.Max(end, c.End);
            }
            return end;
        }

        /// <summary>Advance by <paramref name="dt"/> seconds of game time — the editor's tick.</summary>
        public (float dx, float dy, float scroll) Tick(double dt) => Advance(_now + Math.Max(0, dt));

        /// <summary>One tick at clock time <paramref name="now"/>, before the scripts run: due commands are applied, and the
        /// mouse movement and wheel notches of this tick come back for the host to add to Input.MouseDeltaX / Y and
        /// Input.ScrollDelta. <paramref name="capture"/> receives the file of a <c>capture</c> command (player only).</summary>
        public (float dx, float dy, float scroll) Advance(double now, Action<string> capture = null)
        {
            if (!(now >= _now)) now = _now;   // never backwards (and never NaN)
            double prev = _now;
            _now = now;
            Frames++;
            _produced = false;
            _pressedNow.Clear();
            if (_releaseNext.Count > 0)
            {
                foreach (int vk in _releaseNext) _held.Remove(vk);
                _releaseNext.Clear();
            }
            if (_pending.Count == 0 && _held.Count == 0 && _looks.Count == 0 && _scroll == 0) return default;   // idle: nothing to do

            int due = 0;
            while (due < _pending.Count && _pending[due].T <= now) Apply(_pending[due++], capture);
            if (due > 0) _pending.RemoveRange(0, due);

            // timed holds and taps run out — one pressed in this tick still shows as down for it
            if (_held.Count > 0)
            {
                _expired.Clear();
                foreach (var kv in _held) if (kv.Value < now) _expired.Add(kv.Key);
                foreach (int vk in _expired)
                {
                    if (_pressedNow.Contains(vk)) _releaseNext.Add(vk);
                    else _held.Remove(vk);
                }
            }

            // mouse movement: the part of each look's span that falls into this tick, so the whole movement adds up exactly
            double dx = 0, dy = 0;
            for (int i = _looks.Count - 1; i >= 0; i--)
            {
                var l = _looks[i];
                if (l.End <= l.Start) { dx += l.Dx; dy += l.Dy; _looks.RemoveAt(i); continue; }
                double a = Math.Max(prev, l.Start), b = Math.Min(now, l.End);
                if (b > a)
                {
                    double f = (b - a) / (l.End - l.Start);
                    dx += l.Dx * f; dy += l.Dy * f;
                }
                if (l.End <= now) _looks.RemoveAt(i);
            }
            float scroll = (float)_scroll;
            _scroll = 0;
            _produced = dx != 0 || dy != 0 || scroll != 0;
            return ((float)dx, (float)dy, scroll);
        }

        private void Apply(InputScript.Command c, Action<string> capture)
        {
            switch (c.Op)
            {
                case "press":
                    if (c.Vk == 0) break;
                    _held[c.Vk] = double.PositiveInfinity;
                    _releaseNext.Remove(c.Vk);   // pressed again after a release in this tick: it stays down
                    if (!_pressedNow.Contains(c.Vk)) _pressedNow.Add(c.Vk);
                    break;
                case "release":
                    if (c.Vk == 0 || !_held.ContainsKey(c.Vk)) break;
                    if (_pressedNow.Contains(c.Vk)) { if (!_releaseNext.Contains(c.Vk)) _releaseNext.Add(c.Vk); }   // pressed this tick: down for it
                    else _held.Remove(c.Vk);
                    break;
                case "hold":
                case "tap":
                {
                    if (c.Vk == 0) break;
                    double until = c.T + (c.Op == "tap" ? InputScript.TapSeconds : Math.Max(InputScript.MinHoldSeconds, c.Seconds));
                    // a key held until released stays held; two holds of one key end with the later one
                    _held[c.Vk] = _held.TryGetValue(c.Vk, out double cur) ? Math.Max(cur, until) : until;
                    _releaseNext.Remove(c.Vk);
                    if (!_pressedNow.Contains(c.Vk)) _pressedNow.Add(c.Vk);
                    break;
                }
                case "look":
                    _looks.Add(new Look { Start = c.T, End = c.T + Math.Max(0, c.Seconds), Dx = c.X, Dy = c.Y });
                    break;
                case "scroll":
                    _scroll += c.X;
                    break;
                case "cursor":
                    HasCursor = true;
                    CursorX = (float)c.X; CursorY = (float)c.Y;
                    break;
                case "capture":
                    try { capture?.Invoke(c.Text); } catch { }
                    break;
                case "exit":
                    ExitRequested = true;
                    break;
            }
        }

        /// <summary>The real pointer moved: the UI cursor is the real one again.</summary>
        public void ReleaseCursor() => HasCursor = false;

        /// <summary>Release everything and drop what is queued; the clock starts at 0 again (a new play session).</summary>
        public void Clear()
        {
            _pending.Clear();
            _held.Clear();
            _pressedNow.Clear();
            _releaseNext.Clear();
            _looks.Clear();
            _scroll = 0;
            _produced = false;
            _now = 0;
            Frames = 0;
            HasCursor = false;
            ExitRequested = false;
        }

        /// <summary>Release every key and drop what is queued, keeping the clock (send_input's cancel).</summary>
        public void Cancel()
        {
            _pending.Clear();
            _held.Clear();
            _pressedNow.Clear();
            _releaseNext.Clear();
            _looks.Clear();
            _scroll = 0;
            _produced = false;
        }
    }
}
