using System;
using System.Collections.Generic;
using System.IO;
using Api = Editor.DllWrapper.VortexAPI;

namespace Editor.UI.Vui
{
    /// <summary>A button click that fired a C# action, tagged with the screen it came from. The screen lets the
    /// script host route the call to that screen's own actions class (e.g. PauseMenu.vui -> PauseMenuActions),
    /// so each UI gets its own class and method names can't collide across screens.</summary>
    public struct UiAction
    {
        public string Screen;   // the .vui source name/path the button lives on (VuiCanvas.Name)
        public string Action;   // the C# method name to invoke
    }

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

        private readonly List<UiAction> _firedActions = new List<UiAction>();
        /// <summary>Screen-tagged C# actions fired by clicked buttons since the last call (then cleared). The
        /// script host invokes the matching method on that screen's actions class — the button↔code link.</summary>
        public List<UiAction> ConsumeFiredActions()
        {
            if (_firedActions.Count == 0) return null;
            var copy = new List<UiAction>(_firedActions);
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

        public void Show(VuiCanvas c) { if (c != null && !_stack.Contains(c)) { c.Visible = true; c._navFocus = null; _stack.Add(c); } }
        public void Hide(VuiCanvas c) { if (c != null) { c.Visible = false; c._navFocus = null; _stack.Remove(c); } }

        public VuiCanvas Push(string name) { var c = Load(name); if (c != null) Show(c); return c; }
        public void Pop() { if (_stack.Count > 0) { var c = _stack[_stack.Count - 1]; _stack.RemoveAt(_stack.Count - 1); c.Visible = false; c._navFocus = null; } }
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
                CollectActions(c);
                if (c.BlocksInput) { consumed = true; break; }
                consumed |= didConsume;
            }

            // 2b) Gamepad/keyboard focus navigation (#44) + tooltip dwell tracking (#45). Nav runs
            // AFTER the mouse pass so activation-fired actions are drained immediately (the next
            // Update() clears the canvas's fired list before the loop above would see them).
            UpdateNav(input);
            UpdateTooltip(input);

            // 3) Render bottom -> top (painter z-order: HUD < screens < modals).
            foreach (var c in _stack) if (c.Visible) c.Render();

            // 3b) Topmost overlays: the focus ring (#44) and the tooltip (#45) draw over everything.
            DrawNavHighlight();
            DrawTooltip(vw, vh);

            LastConsumedInput = consumed;
            return consumed;
        }

        /// <summary>Drain a canvas's fired actions into the global list — the confirm dialog's own
        /// pseudo-actions (#45) are intercepted here instead of reaching a script actions class.</summary>
        private void CollectActions(VuiCanvas c)
        {
            if (c.FiredActions.Count == 0) return;
            foreach (var act in c.FiredActions)
            {
                if (c.Name == VuiDialogs.DialogScreenName) VuiDialogs.HandleAction(act);
                else _firedActions.Add(new UiAction { Screen = c.Name, Action = act });
            }
            c.FiredActions.Clear();
        }

        // ===================== FOCUS NAVIGATION (#44) =====================

        private readonly List<VuiElement> _navScratch = new List<VuiElement>();
        private VuiCanvas _navCanvas;             // the canvas whose focus is highlighted
        private bool _navMode;                    // highlight shown after nav input; hidden on mouse move
        private int _stickHeldSince, _stickNextRepeat;
        private float _lastMx, _lastMy;

        private void UpdateNav(in VuiInput input)
        {
            // Nav target = the TOPMOST visible canvas that has focusable elements (a BlocksInput
            // modal stops the search either way — lower screens must not steal navigation).
            _navScratch.Clear();
            VuiCanvas target = null;
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                var c = _stack[i];
                if (!c.Visible) continue;
                c.CollectFocusables(_navScratch);
                if (_navScratch.Count > 0) { target = c; break; }
                if (c.BlocksInput) break;
                _navScratch.Clear();
            }
            _navCanvas = target;
            if (target == null) { _navMode = false; return; }

            // --- gather nav pulses: keyboard edges (from the host key queue) + gamepad ---
            int dx = 0, dy = 0; bool tab = false, act = false;
            if (input.KeyEvents != null)
            {
                for (int k = 0; k < input.KeyCount; k++)
                {
                    switch (input.KeyEvents[k])
                    {
                        case 0x25: dx = -1; break;   // left
                        case 0x27: dx = 1; break;    // right
                        case 0x26: dy = -1; break;   // up
                        case 0x28: dy = 1; break;    // down
                        case 0x09: tab = true; break;
                        case 0x0D: case 0x20: act = true; break;   // enter / space
                    }
                }
            }
            // Enter/Space/Tab arrive as CHARS on hosts whose key queue only carries non-char keys
            // (the GameHost: arrows land in KeyEvents, '\r'/' '/'\t' in Chars) — accept both routes.
            if (input.Chars != null)
            {
                for (int k = 0; k < input.CharCount; k++)
                {
                    char ch = input.Chars[k];
                    if (ch == '\r' || ch == '\n' || ch == ' ') act = true;
                    else if (ch == '\t') tab = true;
                }
            }

            // While a TextField is being typed into, Enter/Space belong to the text, not activation.
            if (target.TextFocus != null) { act = false; tab = false; dx = 0; dy = 0; }

            try
            {
                // UNGATED pad reads (the public getters go dead exactly while a BlocksGameplay menu
                // is up — which is when navigation is needed).
                if (Vortex.Input.UiButtonDown("DPadLeft")) dx = -1;
                if (Vortex.Input.UiButtonDown("DPadRight")) dx = 1;
                if (Vortex.Input.UiButtonDown("DPadUp")) dy = -1;
                if (Vortex.Input.UiButtonDown("DPadDown")) dy = 1;
                if (Vortex.Input.UiButtonDown("A")) act = true;

                // Left stick with initial-delay + repeat (350 ms, then 140 ms).
                float sx = Vortex.Input.UiLeftStickX, sy = Vortex.Input.UiLeftStickY;
                int sdx = sx < -0.55f ? -1 : (sx > 0.55f ? 1 : 0);
                int sdy = sy > 0.55f ? -1 : (sy < -0.55f ? 1 : 0);   // stick up = focus up
                if (sdx != 0 || sdy != 0)
                {
                    int now = Environment.TickCount;
                    if (_stickHeldSince == 0)
                    {
                        _stickHeldSince = now;
                        if (sdx != 0) dx = sdx; if (sdy != 0) dy = sdy;
                        _stickNextRepeat = now + 350;
                    }
                    else if (now >= _stickNextRepeat)
                    {
                        if (sdx != 0) dx = sdx; if (sdy != 0) dy = sdy;
                        _stickNextRepeat = now + 140;
                    }
                }
                else _stickHeldSince = 0;
            }
            catch { }

            if (dx == 0 && dy == 0 && !tab && !act)
            {
                // Noticeable mouse motion hands control back to the cursor (hide the ring).
                if (Math.Abs(input.Mx - _lastMx) + Math.Abs(input.My - _lastMy) > 12f) _navMode = false;
                _lastMx = input.Mx; _lastMy = input.My;
                return;
            }
            _lastMx = input.Mx; _lastMy = input.My;
            _navMode = true;

            // Left/right adjust a focused Slider/Stepper instead of moving focus.
            if (dx != 0 && target.AdjustFocused(dx)) dx = 0;
            if (dx != 0 || dy != 0) target.MoveFocus(dx, dy, _navScratch);
            if (tab) target.FocusNext(_navScratch);
            if (act)
            {
                target.ActivateFocused();
                CollectActions(target);   // drain now — next frame's Update would wipe them first
            }
        }

        private void DrawNavHighlight()
        {
            if (!_navMode || _navCanvas == null || !_navCanvas.Visible) return;
            var e = _navCanvas.NavFocus;
            if (e == null || !e.RuntimeVisibleEffective) return;
            var r = e.Resolved;
            float s = _navCanvas.Scale;
            float t = 2f * s, p = 3f * s;
            const float cr = 1f, cg = 0.82f, cb = 0.35f, ca = 0.95f;   // warm gold ring
            Api.UIRect(r.X - p, r.Y - p, r.W + 2 * p, t, cr, cg, cb, ca, t * 0.5f);
            Api.UIRect(r.X - p, r.Y + r.H + p - t, r.W + 2 * p, t, cr, cg, cb, ca, t * 0.5f);
            Api.UIRect(r.X - p, r.Y - p, t, r.H + 2 * p, cr, cg, cb, ca, t * 0.5f);
            Api.UIRect(r.X + r.W + p - t, r.Y - p, t, r.H + 2 * p, cr, cg, cb, ca, t * 0.5f);
        }

        // ===================== TOOLTIP (#45) =====================

        private VuiElement _tipElem;
        private int _tipSince;
        private float _tipScale = 1f;

        private void UpdateTooltip(in VuiInput input)
        {
            VuiElement hot = null; VuiCanvas hotCanvas = null;
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                var c = _stack[i];
                if (!c.Visible) continue;
                if (c.HotElement != null) { hot = c.HotElement; hotCanvas = c; break; }
                if (c.BlocksInput) break;
            }
            if (!ReferenceEquals(hot, _tipElem)) { _tipElem = hot; _tipSince = Environment.TickCount; }
            _tipScale = hotCanvas != null ? hotCanvas.Scale : 1f;
        }

        private void DrawTooltip(float vw, float vh)
        {
            var e = _tipElem;
            if (e == null || string.IsNullOrEmpty(e.Tooltip)) return;
            if (Environment.TickCount - _tipSince < 450) return;   // hover dwell

            float s = _tipScale;
            float fs = 13f * s;
            string text = e.Tooltip;
            float w = Math.Min(420f * s, Math.Max(60f * s, text.Length * fs * 0.55f + 20f * s));
            float h = 26f * s;
            float x = _lastMx + 18f, y = _lastMy + 22f;
            if (x + w > vw) x = vw - w - 4f;
            if (y + h > vh) y = _lastMy - h - 6f;
            Api.UIRect(x, y, w, h, 0.08f, 0.08f, 0.10f, 0.96f, 6f * s);
            Api.UIText(x + 10f * s, y, w - 20f * s, h, text, fs, 0.92f, 0.92f, 0.95f, 1f, 0, 500);
        }
    }
}
