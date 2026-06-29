using System.Collections.Generic;
using System.IO;

namespace Editor.UI.Vui
{
    /// <summary>
    /// Global screen stack + per-frame driver. Bottom = HUD, top = modal. Owns the cursor-lock authority (a menu
    /// on top unlocks the cursor; HUD-only keeps mouse-look locked). One TickAll per frame: Layout every visible
    /// canvas, route input TOP-FIRST (a BlocksInput screen consumes + stops), then Render BOTTOM->TOP (painter z).
    /// </summary>
    public sealed class VuiStack
    {
        public static VuiStack Instance { get; } = new VuiStack();

        private readonly List<VuiCanvas> _stack = new List<VuiCanvas>();
        private readonly Dictionary<string, VuiCanvas> _loaded = new Dictionary<string, VuiCanvas>();

        /// <summary>Where bare names ("HUD.vui") are resolved from (the project's Assets folder). Set by the host.</summary>
        public string AssetRoot { get; set; }

        public bool HasActiveScreens
        {
            get { foreach (var c in _stack) if (c.Visible) return true; return false; }
        }

        public bool LastConsumedInput { get; private set; }

        /// <summary>Load (and cache by name) a .vui — NOT shown until Show/Push. Returns null on failure.</summary>
        public VuiCanvas Load(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_loaded.TryGetValue(name, out var cached) && cached != null) return cached;
            var path = Resolve(name);
            var canvas = VuiCanvas.Load(path);
            if (canvas != null) _loaded[name] = canvas;
            return canvas;
        }

        private string Resolve(string name)
        {
            if (File.Exists(name)) return name;                              // absolute / already-resolved
            if (!string.IsNullOrEmpty(AssetRoot))
            {
                var p1 = Path.Combine(AssetRoot, name); if (File.Exists(p1)) return p1;
                var p2 = Path.Combine(AssetRoot, "UI", name); if (File.Exists(p2)) return p2;
            }
            return name;
        }

        public void Show(VuiCanvas c) { if (c != null && !_stack.Contains(c)) { c.Visible = true; _stack.Add(c); } }
        public void Hide(VuiCanvas c) { if (c != null) { c.Visible = false; _stack.Remove(c); } }

        public VuiCanvas Push(string name) { var c = Load(name); if (c != null) Show(c); return c; }
        public void Pop() { if (_stack.Count > 0) { var c = _stack[_stack.Count - 1]; _stack.RemoveAt(_stack.Count - 1); c.Visible = false; } }
        public void Clear() { _stack.Clear(); }

        /// <summary>Topmost visible canvas's cursor-lock preference (HUD = locked mouse-look; menu = free cursor).</summary>
        public bool CursorLockedForTop()
        {
            for (int i = _stack.Count - 1; i >= 0; i--) if (_stack[i].Visible) return _stack[i].CursorLockedPref;
            return true;
        }

        /// <summary>Drive every screen for this frame. Returns true if the UI consumed the click (suppress gameplay).</summary>
        public bool TickAll(float vw, float vh, in VuiInput input)
        {
            // 1) Layout all visible canvases (Resolved is needed by both hit-test and render).
            foreach (var c in _stack) if (c.Visible) c.Layout(vw, vh);

            // 2) Input top-first: a BlocksInput screen consumes the click and stops propagation to lower screens.
            bool consumed = false;
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                var c = _stack[i];
                if (!c.Visible) continue;
                bool didConsume = c.Update(input);
                if (c.BlocksInput) { consumed = true; break; }
                consumed |= didConsume;
            }

            // 3) Render bottom -> top (painter z-order: HUD < screens < modals).
            foreach (var c in _stack) if (c.Visible) c.Render();

            LastConsumedInput = consumed;
            return consumed;
        }
    }
}
