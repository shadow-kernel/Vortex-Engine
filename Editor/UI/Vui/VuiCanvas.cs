using System;
using System.Collections.Generic;
using Api = Editor.DllWrapper.VortexAPI;

namespace Editor.UI.Vui
{
    /// <summary>
    /// One loaded .vui screen at runtime: the element tree + an id index + the per-frame Layout/Update/Render and
    /// the named-slot writes/reads scripts use. The SINGLE resolved pixel rect (VuiElement.Resolved) is computed
    /// once in Layout and read by BOTH hit-test (Update) and draw (Render) — so input and pixels never disagree.
    /// </summary>
    public sealed class VuiCanvas
    {
        public VuiElement Root;
        public int DesignW = 1920, DesignH = 1080;
        public float HudScale = 1f;
        public bool Visible = true;
        public string Name;                     // source .vui path

        private readonly Dictionary<string, VuiElement> _byId = new Dictionary<string, VuiElement>();
        private readonly HashSet<string> _clicked = new HashSet<string>();
        private readonly List<string> _fired = new List<string>();   // C# action names fired by clicked buttons this frame
        public List<string> FiredActions => _fired;
        private float _w, _h, _scale = 1f;
        private VuiElement _hot;                 // hovered interactive element this frame

        // captured drag/focus targets are tracked by IDENTITY (Phase 4 uses these)
        internal VuiElement _focus;
        internal VuiElement _dragTarget; internal int _dragKind;
        internal VuiElement _capturedKeyTarget;

        public bool CursorLockedPref => Root != null && Root.CursorLocked;
        public bool BlocksInput => Root != null && Root.BlocksInput;
        public float Scale => _scale;

        public static VuiCanvas Load(string path) => VuiDocument.Load(path);

        /// <summary>Rebuild the id -> element index over the authored tree.</summary>
        public void Reindex()
        {
            _byId.Clear();
            if (Root != null) IndexNode(Root);
        }
        private void IndexNode(VuiElement e)
        {
            if (!string.IsNullOrEmpty(e.Id) && !_byId.ContainsKey(e.Id)) _byId[e.Id] = e;
            foreach (var c in e.Children) IndexNode(c);
        }
        private VuiElement Find(string id) { if (id != null && _byId.TryGetValue(id, out var e)) return e; return null; }

        /// <summary>The resolved pixel rect of an element by id (valid after Layout). Used by tests + the builder.</summary>
        public bool TryGetRect(string id, out RectF r) { var e = Find(id); if (e != null) { r = e.Resolved; return true; } r = default(RectF); return false; }

        private List<VuiElement> ActiveChildren(VuiElement e)
            => (e.Kind == VuiKind.List && e.RowPool != null) ? e.RowPool : e.Children;

        // ===================== LAYOUT =====================
        public void Layout(float vw, float vh)
        {
            if (Root == null) return;
            _w = vw; _h = vh;
            float sx = DesignW > 0 ? vw / DesignW : 1f;
            float sy = DesignH > 0 ? vh / DesignH : 1f;
            _scale = HudScale * Math.Min(sx, sy);
            Root.Resolved = new RectF(0, 0, vw, vh);
            Root.RuntimeVisibleEffective = Root.Visible;
            if (!Root.RuntimeVisibleEffective) return;
            LayoutChildrenOf(Root);
        }

        private void LayoutChildrenOf(VuiElement e)
        {
            var kids = ActiveChildren(e);
            if (e.LayoutMode == StackDir.None)
            {
                foreach (var c in kids)
                {
                    ResolveSelf(c, e.Resolved);
                    c.RuntimeVisibleEffective = e.RuntimeVisibleEffective && c.Visible;
                    if (c.RuntimeVisibleEffective) LayoutChildrenOf(c);
                }
            }
            else
            {
                ArrangeContainer(e, kids);
                foreach (var c in kids)
                {
                    c.RuntimeVisibleEffective = e.RuntimeVisibleEffective && c.Visible;
                    if (c.RuntimeVisibleEffective) LayoutChildrenOf(c);  // c.Resolved was set by ArrangeContainer
                }
            }
        }

        private static void AnchorAxis(AnchorEnum a, out float ax, out float ay)
        {
            switch (a)
            {
                case AnchorEnum.TopLeft: ax = 0; ay = 0; break;
                case AnchorEnum.TopCenter: ax = .5f; ay = 0; break;
                case AnchorEnum.TopRight: ax = 1; ay = 0; break;
                case AnchorEnum.MidLeft: ax = 0; ay = .5f; break;
                case AnchorEnum.Center: ax = .5f; ay = .5f; break;
                case AnchorEnum.MidRight: ax = 1; ay = .5f; break;
                case AnchorEnum.BottomLeft: ax = 0; ay = 1; break;
                case AnchorEnum.BottomCenter: ax = .5f; ay = 1; break;
                default: ax = 1; ay = 1; break;   // BottomRight
            }
        }

        // Resolve one element's pixel rect inside parent rect P (anchor + offset + pct + stretch + size).
        private void ResolveSelf(VuiElement e, RectF P)
        {
            float s = _scale;
            AnchorAxis(e.Anchor, out float ax, out float ay);
            float pivotX = e.HasPivot ? e.PivotX : ax;
            float pivotY = e.HasPivot ? e.PivotY : ay;

            float w, h, px, py;
            if (e.StretchX)
            {
                float left = e.OffX * s + e.PctX * P.W;
                float right = (-e.W) * s;                 // authored size.x = negative right margin when stretching
                w = P.W - left - right;
                px = P.X + left;
            }
            else { w = (e.WPct > 0 ? e.WPct * P.W : e.W * s); px = P.X + ax * P.W + e.PctX * P.W + e.OffX * s - pivotX * w; }

            if (e.StretchY)
            {
                float top = e.OffY * s + e.PctY * P.H;
                float bottom = (-e.H) * s;
                h = P.H - top - bottom;
                py = P.Y + top;
            }
            else { h = (e.HPct > 0 ? e.HPct * P.H : e.H * s); py = P.Y + ay * P.H + e.PctY * P.H + e.OffY * s - pivotY * h; }

            e.Resolved = new RectF((float)Math.Round(px), (float)Math.Round(py), (float)Math.Round(w), (float)Math.Round(h));
        }

        // Containers (the ONE imperative layout path) override children's rects: lay them out sequentially inside
        // the parent rect, inset by Padding, each keeping its authored size.
        private void ArrangeContainer(VuiElement e, List<VuiElement> kids)
        {
            float s = _scale, pad = e.Padding * s, sp = e.Spacing * s;
            float innerX = e.Resolved.X + pad, innerY = e.Resolved.Y + pad;
            float innerW = e.Resolved.W - 2 * pad, innerH = e.Resolved.H - 2 * pad;

            if (e.LayoutMode == StackDir.Vertical)
            {
                float cy = innerY - e.ScrollY;
                foreach (var c in kids)
                {
                    float ch = c.H > 0 ? c.H * s : 24 * s;
                    c.Resolved = new RectF((float)Math.Round(innerX), (float)Math.Round(cy), (float)Math.Round(innerW), (float)Math.Round(ch));
                    cy += ch + sp;
                }
            }
            else if (e.LayoutMode == StackDir.Horizontal)
            {
                float cx = innerX - e.ScrollY;
                foreach (var c in kids)
                {
                    float cw = c.W > 0 ? c.W * s : 80 * s;
                    c.Resolved = new RectF((float)Math.Round(cx), (float)Math.Round(innerY), (float)Math.Round(cw), (float)Math.Round(innerH));
                    cx += cw + sp;
                }
            }
            else // Grid
            {
                int cols = e.GridCols > 0 ? e.GridCols : 1;
                float cellW = (innerW - sp * (cols - 1)) / cols;
                float cellH = kids.Count > 0 && kids[0].H > 0 ? kids[0].H * s : 48 * s;
                for (int i = 0; i < kids.Count; i++)
                {
                    int col = i % cols, row = i / cols;
                    float cx = innerX + col * (cellW + sp);
                    float cy = innerY + row * (cellH + sp) - e.ScrollY;
                    kids[i].Resolved = new RectF((float)Math.Round(cx), (float)Math.Round(cy), (float)Math.Round(cellW), (float)Math.Round(cellH));
                }
            }
        }

        // ===================== INPUT =====================
        // Returns true if this canvas consumed the click (so the game must not shoot/use this frame).
        public bool Update(in VuiInput input)
        {
            if (Root == null || !Root.RuntimeVisibleEffective) return false;
            _clicked.Clear();
            _fired.Clear();
            if (_hot != null) { _hot.Hot = false; _hot = null; }

            var hit = HitTest(Root, input.Mx, input.My);
            if (hit != null) { hit.Hot = true; _hot = hit; }

            // Press: dispatch by kind + set the capture/focus targets (all tracked by element IDENTITY).
            if (input.Pressed)
            {
                if (hit == null || hit.Kind != VuiKind.TextField) _focus = null;   // click outside a field blurs it
                if (hit != null)
                {
                    switch (hit.Kind)
                    {
                        case VuiKind.Button:
                            if (!string.IsNullOrEmpty(hit.Id)) _clicked.Add(hit.Id);
                            if (!string.IsNullOrEmpty(hit.ClickAction)) _fired.Add(hit.ClickAction);   // -> invoke the C# method
                            if (hit.CapturesKey) _capturedKeyTarget = hit;          // enter keybind-capture mode
                            break;
                        case VuiKind.Toggle: hit.On = !hit.On; break;
                        case VuiKind.Stepper:
                            if (hit.Options != null && hit.Options.Length > 0)
                            {
                                bool right = input.Mx > hit.Resolved.CenterX;
                                hit.OptionIndex = (hit.OptionIndex + (right ? 1 : hit.Options.Length - 1)) % hit.Options.Length;
                            }
                            break;
                        case VuiKind.Slider: _dragTarget = hit; _dragKind = 1; ApplySliderDrag(hit, input.Mx); break;
                        case VuiKind.TextField: _focus = hit; break;
                    }
                }
            }

            // Slider drag: the captured slider tracks the cursor every frame until release.
            if (input.Down && _dragTarget != null && _dragKind == 1) ApplySliderDrag(_dragTarget, input.Mx);
            if (!input.Down) { _dragTarget = null; _dragKind = 0; }

            // Wheel scroll: route to the clipped container under the cursor.
            if (input.Wheel != 0)
            {
                var sc = FindScrollable(Root, input.Mx, input.My);
                if (sc != null) { sc.ScrollY -= input.Wheel * 30f * _scale; if (sc.ScrollY < 0) sc.ScrollY = 0; }
            }

            // Typed text into the focused TextField (printable append / backspace / enter blurs).
            if (_focus != null && _focus.Kind == VuiKind.TextField && input.Chars != null)
            {
                string t = _focus.Text ?? "";
                for (int i = 0; i < input.CharCount; i++)
                {
                    char c = input.Chars[i];
                    if (c == '\b') { if (t.Length > 0) t = t.Substring(0, t.Length - 1); }
                    else if (c == '\r' || c == '\n') { _focus.Text = t; _focus = null; break; }
                    else if (c >= ' ' && t.Length < _focus.MaxChars) t += c;
                }
                if (_focus != null) _focus.Text = t;
            }

            // Keybind capture: the first key after clicking a CapturesKey button is stored on it.
            if (_capturedKeyTarget != null && input.KeyCount > 0)
            {
                _capturedKeyTarget.CapturedKey = input.KeyEvents[0];
                _capturedKeyTarget = null;
            }

            return BlocksInput || hit != null || _dragTarget != null || _focus != null;
        }

        private void ApplySliderDrag(VuiElement s, float mx)
        {
            float t = s.Resolved.W > 0 ? (mx - s.Resolved.X) / s.Resolved.W : 0f;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            s.Value = s.Min + t * (s.Max - s.Min);
        }

        private VuiElement FindScrollable(VuiElement e, float mx, float my)
        {
            if (!e.RuntimeVisibleEffective) return null;
            var kids = ActiveChildren(e);
            for (int i = kids.Count - 1; i >= 0; i--) { var r = FindScrollable(kids[i], mx, my); if (r != null) return r; }
            if (e.ClipChildren && e.Resolved.Contains(mx, my)) return e;
            return null;
        }

        private VuiElement HitTest(VuiElement e, float mx, float my)
        {
            if (!e.RuntimeVisibleEffective) return null;
            var kids = ActiveChildren(e);
            for (int i = kids.Count - 1; i >= 0; i--)   // children draw after parents -> topmost first
            {
                var hit = HitTest(kids[i], mx, my);
                if (hit != null) return hit;
            }
            if (IsInteractive(e.Kind) && e.Resolved.Contains(mx, my)) return e;
            return null;
        }
        private static bool IsInteractive(VuiKind k)
            => k == VuiKind.Button || k == VuiKind.Toggle || k == VuiKind.Slider || k == VuiKind.Stepper || k == VuiKind.TextField;

        // ===================== RENDER =====================
        public void Render()
        {
            if (Root == null || !Root.RuntimeVisibleEffective) return;
            RenderNode(Root);
        }

        private void RenderNode(VuiElement e)
        {
            if (!e.RuntimeVisibleEffective) return;
            EmitNode(e);
            bool clip = e.ClipChildren;
            if (clip) Api.UIPushClip(e.Resolved.X, e.Resolved.Y, e.Resolved.W, e.Resolved.H);
            foreach (var c in ActiveChildren(e)) RenderNode(c);
            if (clip) Api.UIPopClip();
        }

        private void EmitNode(VuiElement e)
        {
            RectF r = e.Resolved;
            float a = e.Opacity;
            switch (e.Kind)
            {
                case VuiKind.Panel:
                    if (e.Bg != null && e.Bg[3] > 0f) Api.UIRect(r.X, r.Y, r.W, r.H, e.Bg[0], e.Bg[1], e.Bg[2], e.Bg[3] * a, e.Radius * _scale);
                    break;
                case VuiKind.Text:
                    if (!string.IsNullOrEmpty(e.Text))
                        Api.UIText(r.X, r.Y, r.W, r.H, e.Text, e.FontSize * _scale, e.Fg[0], e.Fg[1], e.Fg[2], e.Fg[3] * a, e.Align, e.Weight);
                    break;
                case VuiKind.Image:
                    if (!string.IsNullOrEmpty(e.ImageAsset))
                        Api.UIImage(r.X, r.Y, r.W, r.H, e.ImageAsset, e.Fg[0], e.Fg[1], e.Fg[2], e.Fg[3] * a);
                    break;
                case VuiKind.Bar:
                {
                    if (e.Bg != null && e.Bg[3] > 0f) Api.UIRect(r.X, r.Y, r.W, r.H, e.Bg[0], e.Bg[1], e.Bg[2], e.Bg[3] * a, e.Radius * _scale);
                    float t = e.Max > e.Min ? (e.Value - e.Min) / (e.Max - e.Min) : e.Value;
                    if (t < 0) t = 0; else if (t > 1) t = 1;
                    float fw = (float)Math.Round(r.W * t);
                    if (fw > 0) Api.UIRect(r.X, r.Y, fw, r.H, e.Fg[0], e.Fg[1], e.Fg[2], e.Fg[3] * a, e.Radius * _scale);
                    break;
                }
                case VuiKind.Button:
                {
                    float[] face = e.Hot ? (e.HoverTint ?? Lighten(e.Bg)) : e.Bg;
                    Api.UIRect(r.X, r.Y, r.W, r.H, face[0], face[1], face[2], face[3] * a, e.Radius * _scale);
                    if (!string.IsNullOrEmpty(e.Text))
                        Api.UIText(r.X, r.Y, r.W, r.H, e.Text, e.FontSize * _scale, e.Fg[0], e.Fg[1], e.Fg[2], e.Fg[3] * a, 1, e.Weight);
                    break;
                }
                case VuiKind.Slider:
                {
                    // track
                    float ty = r.Y + r.H * 0.5f - 3 * _scale;
                    Api.UIRect(r.X, ty, r.W, 6 * _scale, e.Bg[0], e.Bg[1], e.Bg[2], e.Bg[3] * a, 3 * _scale);
                    float t = e.Max > e.Min ? (e.Value - e.Min) / (e.Max - e.Min) : e.Value;
                    if (t < 0) t = 0; else if (t > 1) t = 1;
                    float hx = r.X + r.W * t - 7 * _scale;
                    Api.UIRect(hx, r.Y, 14 * _scale, r.H, e.Fg[0], e.Fg[1], e.Fg[2], e.Fg[3] * a, 4 * _scale);
                    break;
                }
                case VuiKind.Toggle:
                {
                    float[] face = e.On ? (e.Fg) : e.Bg;
                    Api.UIRect(r.X, r.Y, r.H, r.H, face[0], face[1], face[2], face[3] * a, 4 * _scale);   // square box
                    if (!string.IsNullOrEmpty(e.Text))
                        Api.UIText(r.X + r.H + 8 * _scale, r.Y, r.W - r.H - 8 * _scale, r.H, e.Text, e.FontSize * _scale, 0.9f, 0.9f, 0.93f, a, 0, e.Weight);
                    break;
                }
                case VuiKind.Stepper:
                {
                    if (e.Bg != null && e.Bg[3] > 0f) Api.UIRect(r.X, r.Y, r.W, r.H, e.Bg[0], e.Bg[1], e.Bg[2], e.Bg[3] * a, e.Radius * _scale);
                    string val = (e.Options != null && e.OptionIndex >= 0 && e.OptionIndex < e.Options.Length) ? e.Options[e.OptionIndex] : "";
                    Api.UIText(r.X, r.Y, r.W, r.H, "‹  " + val + "  ›", e.FontSize * _scale, e.Fg[0], e.Fg[1], e.Fg[2], e.Fg[3] * a, 1, e.Weight);
                    break;
                }
                case VuiKind.TextField:
                {
                    Api.UIRect(r.X, r.Y, r.W, r.H, e.Bg[0], e.Bg[1], e.Bg[2], e.Bg[3] * a, e.Radius * _scale);
                    string shown = e.Text ?? "";
                    bool focused = ReferenceEquals(e, _focus);
                    Api.UIText(r.X + 8 * _scale, r.Y, r.W - 16 * _scale, r.H, focused ? shown + "|" : shown, e.FontSize * _scale, e.Fg[0], e.Fg[1], e.Fg[2], e.Fg[3] * a, 0, e.Weight);
                    break;
                }
                case VuiKind.Crosshair:
                {
                    float cx = r.CenterX, cy = r.CenterY, ext = r.W * 0.5f, gap = 2 * _scale, th = 2 * _scale;
                    Api.UILine(cx - ext, cy, cx - gap, cy, e.Fg[0], e.Fg[1], e.Fg[2], e.Fg[3] * a, th);
                    Api.UILine(cx + gap, cy, cx + ext, cy, e.Fg[0], e.Fg[1], e.Fg[2], e.Fg[3] * a, th);
                    Api.UILine(cx, cy - ext, cx, cy - gap, e.Fg[0], e.Fg[1], e.Fg[2], e.Fg[3] * a, th);
                    Api.UILine(cx, cy + gap, cx, cy + ext, e.Fg[0], e.Fg[1], e.Fg[2], e.Fg[3] * a, th);
                    break;
                }
                case VuiKind.List:
                    break;   // the rows (RowPool / children) draw themselves
            }
        }

        private static float[] Lighten(float[] c)
        {
            if (c == null) return new float[] { 0.3f, 0.3f, 0.34f, 1f };
            return new float[] { Clamp01(c[0] + 0.09f), Clamp01(c[1] + 0.09f), Clamp01(c[2] + 0.09f), c[3] };
        }
        private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        // ===================== SCRIPT SLOTS =====================
        public void SetValue(string id, float v) { var e = Find(id); if (e != null) e.Value = v; }
        public void SetText(string id, string t) { var e = Find(id); if (e != null) e.Text = t; }
        public void SetVisible(string id, bool v) { var e = Find(id); if (e != null) e.Visible = v; }
        public void SetColor(string id, float r, float g, float b, float a) { var e = Find(id); if (e != null) e.Fg = new[] { r, g, b, a }; }
        public void SetImage(string id, string asset) { var e = Find(id); if (e != null) e.ImageAsset = asset; }

        /// <summary>Feed a repeater List: grow/shrink its pooled RowTemplate clones to match rows + bind row.* sub-ids.</summary>
        public void SetList(string id, IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
        {
            var e = Find(id);
            if (e == null || e.Kind != VuiKind.List || e.RowTemplate == null) return;
            if (e.RowPool == null) e.RowPool = new List<VuiElement>();
            int n = rows != null ? rows.Count : 0;
            while (e.RowPool.Count < n) e.RowPool.Add(Clone(e.RowTemplate, e));   // grow (pooled; reused next time)
            for (int i = 0; i < e.RowPool.Count; i++)
            {
                var row = e.RowPool[i];
                bool active = i < n;
                row.Visible = active;
                if (active) BindRow(row, rows[i]);
            }
        }

        private static void BindRow(VuiElement node, IReadOnlyDictionary<string, string> data)
        {
            if (!string.IsNullOrEmpty(node.Id) && data.TryGetValue(node.Id, out var val))
            {
                if (node.Kind == VuiKind.Image) node.ImageAsset = val;
                else node.Text = val;
            }
            foreach (var c in node.Children) BindRow(c, data);
        }

        private static VuiElement Clone(VuiElement t, VuiElement parent)
        {
            var c = new VuiElement
            {
                Kind = t.Kind, Id = t.Id, Anchor = t.Anchor,
                OffX = t.OffX, OffY = t.OffY, PctX = t.PctX, PctY = t.PctY,
                StretchX = t.StretchX, StretchY = t.StretchY, W = t.W, H = t.H, WPct = t.WPct, HPct = t.HPct,
                HasPivot = t.HasPivot, PivotX = t.PivotX, PivotY = t.PivotY,
                Bg = t.Bg != null ? (float[])t.Bg.Clone() : null, Fg = t.Fg != null ? (float[])t.Fg.Clone() : null,
                HoverTint = t.HoverTint != null ? (float[])t.HoverTint.Clone() : null,
                Radius = t.Radius, Align = t.Align, Weight = t.Weight, FontSize = t.FontSize,
                Text = t.Text, ImageAsset = t.ImageAsset, Value = t.Value, Min = t.Min, Max = t.Max, On = t.On,
                Options = t.Options, OptionIndex = t.OptionIndex, TargetSetting = t.TargetSetting,
                CapturesKey = t.CapturesKey, MaxChars = t.MaxChars, ClickAction = t.ClickAction,
                Visible = t.Visible, Opacity = t.Opacity, BlocksInput = t.BlocksInput,
                ClipChildren = t.ClipChildren, LayoutMode = t.LayoutMode, Spacing = t.Spacing, Padding = t.Padding, GridCols = t.GridCols,
                Parent = parent
            };
            foreach (var ch in t.Children) c.Children.Add(Clone(ch, c));
            return c;
        }

        public bool WasClicked(string id) => id != null && _clicked.Remove(id);   // true once, then cleared
        public float GetSlider(string id) { var e = Find(id); return e != null ? e.Value : 0f; }
        public bool GetToggle(string id) { var e = Find(id); return e != null && e.On; }
        public string GetText(string id) { var e = Find(id); return e != null ? (e.Text ?? "") : ""; }
        public int GetStep(string id) { var e = Find(id); return e != null ? e.OptionIndex : 0; }
        public int GetCapturedKey(string id) { var e = Find(id); return e != null ? e.CapturedKey : 0; }
    }
}
