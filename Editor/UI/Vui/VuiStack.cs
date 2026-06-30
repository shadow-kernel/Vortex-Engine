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

        private readonly List<string> _firedActions = new List<string>();
        /// <summary>C# action names fired by clicked buttons since the last call (then cleared). The script host
        /// invokes the matching methods on running behaviours — the button↔code link.</summary>
        public List<string> ConsumeFiredActions()
        {
            if (_firedActions.Count == 0) return null;
            var copy = new List<string>(_firedActions);
            _firedActions.Clear();
            return copy;
        }

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
            // Fall back to the current project's Assets so scripts can just say Gui.Load("HorrorLobby.vui").
            try
            {
                var proj = Editor.Core.Data.ProjectData.Current != null ? Editor.Core.Data.ProjectData.Current.Path : null;
                if (!string.IsNullOrEmpty(proj))
                {
                    var p3 = Path.Combine(proj, "Assets", "UI", name); if (File.Exists(p3)) return p3;
                    var p4 = Path.Combine(proj, "Assets", name); if (File.Exists(p4)) return p4;
                }
            }
            catch { }
            // Shipped game: assets live in the mounted .vpak (RAM), not on disk — return the packed key.
            try
            {
                if (Editor.Core.Services.AssetVfs.IsMounted)
                {
                    foreach (var k in new[] { "Assets/UI/" + name, "Assets/" + name, name })
                        if (Editor.Core.Services.AssetVfs.Contains(k)) return k;
                }
            }
            catch { }
            return name;
        }

        public void Show(VuiCanvas c) { if (c != null && !_stack.Contains(c)) { c.Visible = true; _stack.Add(c); } }
        public void Hide(VuiCanvas c) { if (c != null) { c.Visible = false; _stack.Remove(c); } }

        public VuiCanvas Push(string name) { var c = Load(name); if (c != null) Show(c); return c; }
        public void Pop() { if (_stack.Count > 0) { var c = _stack[_stack.Count - 1]; _stack.RemoveAt(_stack.Count - 1); c.Visible = false; } }
        /// <summary>Drop every screen off the stack (e.g. on a scene switch — UI is scene-specific; the new scene's
        /// scripts re-Push their own HUD/menu in Start). Hides each so a later cached Load()+Show re-adds cleanly.</summary>
        public void Clear() { foreach (var c in _stack) if (c != null) c.Visible = false; _stack.Clear(); }

        /// <summary>Topmost visible canvas's cursor-lock preference (HUD = locked mouse-look; menu = free cursor).</summary>
        public bool CursorLockedForTop()
        {
            for (int i = _stack.Count - 1; i >= 0; i--) if (_stack[i].Visible) return _stack[i].CursorLockedPref;
            return true;
        }

        /// <summary>The single cursor-capture decision shared by BOTH run paths (native GameHost + editor in-viewport
        /// play): when a retained screen is up the TOPMOST one decides — a menu/lobby (CursorLocked=false) frees the
        /// cursor so its buttons are clickable, the HUD (CursorLocked=true) keeps mouse-look locked; otherwise fall
        /// back to the script's flag. Keeping this here (not duplicated per host) means the two paths can't drift.</summary>
        public bool WantsCursorCapture(bool scriptCursorLocked)
            => HasActiveScreens ? CursorLockedForTop() : scriptCursorLocked;

        /// <summary>True while any visible screen OPTS IN to freezing gameplay (its BlocksGameplay checkbox, set in
        /// the UI editor). Gameplay input (Input.GetKey movement + mouse-look) is frozen so the player operates the
        /// UI instead — for a chest/inventory/pause screen, but NOT a hotbar/HUD (leave the box unchecked). This is
        /// independent of cursor-lock: a screen can free the cursor without freezing movement, and vice-versa.</summary>
        public bool GameplayInputBlocked
        {
            get { foreach (var c in _stack) if (c.Visible && c.BlocksGameplayPref) return true; return false; }
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
                if (c.FiredActions.Count > 0) _firedActions.AddRange(c.FiredActions);
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
