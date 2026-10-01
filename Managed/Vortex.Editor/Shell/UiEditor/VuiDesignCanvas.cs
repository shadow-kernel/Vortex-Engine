using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Editor.Core.Data;
using Editor.UI.Vui;

namespace VortexEditor.Shell.UiEditor
{
    /// <summary>
    /// The UI editor's design surface (port of the Windows editor's VuiPreviewRenderer + preview canvas): lays the
    /// screen out with the IDENTICAL runtime <see cref="VuiCanvas.Layout"/> at the chosen preview resolution and draws
    /// every element kind from its resolved pixel rect, so the builder shows exactly where the game puts things.
    /// Click selects (topmost element), drag moves, the 8 handles resize — the math honours anchors, pivots, stretch
    /// margins, percent sizes and layout containers; arrow keys nudge (Shift = 10). Cmd/Ctrl + wheel zooms at the
    /// cursor, wheel / right- or middle-drag pans, double-click on empty space fits. Lists show design-time rows cloned
    /// from their row template exactly like the runtime repeater. <see cref="TestCanvas"/> switches to an interactive
    /// test of a copy (hover, clicks, toggles, sliders, steppers, text fields, fired button actions).
    /// </summary>
    public sealed class VuiDesignCanvas : Control
    {
        private const double ViewMargin = 28;
        private const double HandleSize = 8;
        private static readonly Typeface[] Faces =
        {
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Normal),
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold)
        };

        private VuiCanvas _canvas;
        private VuiElement _selected, _hover;
        private double _vs = 1;          // view pixels per screen pixel
        private Point _origin;           // screen (0,0) in control coordinates
        private double _zoom;            // 0 = fit
        private Vector _pan;
        private readonly Dictionary<VuiElement, VuiElement> _cloneToTemplate = new Dictionary<VuiElement, VuiElement>();
        private readonly Dictionary<VuiElement, VuiElement> _templateToClone = new Dictionary<VuiElement, VuiElement>();
        private readonly Dictionary<string, Bitmap> _images = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        // drag state
        private enum DragKind { None, Move, Resize, Pan, Test }
        private DragKind _drag;
        private int _handle = -1;
        private Point _dragStart;
        private Vector _panStart;
        private bool _dragChanged;
        private (float offX, float offY, float w, float h, float wPct, float hPct) _startVals;
        private RectF _parentRect;
        private float _layoutScale = 1f;
        private bool _testDown;

        public VuiDesignCanvas()
        {
            Focusable = true;
            ClipToBounds = true;
            Cursor = Cursor.Default;
        }

        /// <summary>The edited screen.</summary>
        public VuiCanvas Canvas { get => _canvas; set { _canvas = value; _selected = null; _hover = null; InvalidateVisual(); } }

        /// <summary>When set, the surface runs an interactive test of this copy instead of editing <see cref="Canvas"/>.</summary>
        public VuiCanvas TestCanvas { get; set; }

        /// <summary>Preview resolution (screen pixels the layout runs at).</summary>
        public int ScreenW { get; set; } = 1920;
        public int ScreenH { get; set; } = 1080;

        public bool ShowBounds { get; set; } = true;
        public bool ShowGrid { get; set; }

        public VuiElement Selected { get => _selected; set { _selected = value; InvalidateVisual(); } }

        /// <summary>Current zoom (view pixels per screen pixel; the fit scale while <see cref="IsFit"/>).</summary>
        public double ViewScale => _vs;
        public bool IsFit => _zoom <= 0;

        /// <summary>The user picked an element on the surface (the root when clicking empty screen space).</summary>
        public event Action<VuiElement> SelectionRequested;
        /// <summary>A drag / nudge is about to change the selected element (push an undo snapshot).</summary>
        public event Action EditStarting;
        /// <summary>The selected element's layout values changed (live, during a drag).</summary>
        public event Action Edited;
        /// <summary>A drag finished.</summary>
        public event Action EditCompleted;
        /// <summary>Delete / Backspace on the surface.</summary>
        public event Action DeleteRequested;
        /// <summary>Test mode: a button fired its C# click action.</summary>
        public event Action<string> TestActionFired;

        public void Fit() { _zoom = 0; _pan = default; InvalidateVisual(); }
        /// <summary>Forget the hovered element (the document was replaced, e.g. by undo).</summary>
        public void ResetHover() { _hover = null; InvalidateVisual(); }
        public void SetZoom(double z) { _zoom = Math.Max(0.05, Math.Min(8, z)); InvalidateVisual(); }

        // ================================================================ layout helpers
        private VuiCanvas Active => TestCanvas ?? _canvas;

        private void ComputeView()
        {
            double aw = Math.Max(20, Bounds.Width - 2 * ViewMargin), ah = Math.Max(20, Bounds.Height - 2 * ViewMargin - 14);
            double fit = Math.Min(aw / Math.Max(1, ScreenW), ah / Math.Max(1, ScreenH));
            _vs = _zoom > 0 ? _zoom : fit;
            double w = ScreenW * _vs, h = ScreenH * _vs;
            var center = new Point((Bounds.Width - w) / 2, (Bounds.Height - 14 - h) / 2);
            _origin = _zoom > 0 ? center + _pan : center;
        }

        /// <summary>Run the runtime layout at the preview resolution (+ design-time list rows).</summary>
        public void RunLayout()
        {
            var c = Active;
            if (c?.Root == null) return;
            if (TestCanvas == null) PrepareDesignRows(c.Root);
            try { c.Layout(ScreenW, ScreenH); } catch { }
            _layoutScale = c.Scale > 0 ? c.Scale : 1f;
        }

        /// <summary>Lists show three rows cloned from their row template (the runtime repeater, fed with no data), so
        /// the template can be seen, selected and edited in place.</summary>
        private void PrepareDesignRows(VuiElement root)
        {
            _cloneToTemplate.Clear();
            _templateToClone.Clear();
            try { _canvas.Reindex(); } catch { }
            void Walk(VuiElement e)
            {
                if (e == null) return;
                if (e.Kind == VuiKind.List && e.RowTemplate != null && !string.IsNullOrEmpty(e.Id))
                {
                    e.RowPool = null;
                    try
                    {
                        _canvas.SetList(e.Id, new List<IReadOnlyDictionary<string, string>> { new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>() });
                    }
                    catch { }
                    if (e.RowPool != null) for (int i = 0; i < e.RowPool.Count; i++) Map(e.RowPool[i], e.RowTemplate, i == 0);
                }
                else if (e.Kind == VuiKind.List) e.RowPool = null;
                foreach (var c in e.Children) Walk(c);
            }
            void Map(VuiElement clone, VuiElement template, bool first)
            {
                _cloneToTemplate[clone] = template;
                if (first) _templateToClone[template] = clone;
                for (int i = 0; i < clone.Children.Count && i < template.Children.Count; i++) Map(clone.Children[i], template.Children[i], first);
            }
            Walk(root);
        }

        /// <summary>The on-screen rect of an element (for the template of a list: its first design row).</summary>
        public bool TryGetRect(VuiElement e, out RectF r)
        {
            r = default;
            if (e == null) return false;
            if (_templateToClone.TryGetValue(e, out var clone)) e = clone;
            r = e.Resolved;
            return r.W > 0 || r.H > 0 || ReferenceEquals(e, Active?.Root);
        }

        private Rect ToView(RectF r) => new Rect(_origin.X + r.X * _vs, _origin.Y + r.Y * _vs, Math.Max(0, r.W * _vs), Math.Max(0, r.H * _vs));
        private Point ToScreen(Point view) => new Point((view.X - _origin.X) / _vs, (view.Y - _origin.Y) / _vs);

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
                default: ax = 1; ay = 1; break;
            }
        }

        public static void AnchorFactors(AnchorEnum a, out float ax, out float ay) => AnchorAxis(a, out ax, out ay);

        // ================================================================ render
        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(13, 13, 16)), new Rect(Bounds.Size));
            var c = Active;
            ComputeView();
            var screen = new Rect(_origin.X, _origin.Y, ScreenW * _vs, ScreenH * _vs);
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(20, 20, 25)), screen);
            if (c?.Root != null)
            {
                RunLayout();
                // (no clip pushes in this pass — see Text(); elements past the screen edge stay visible, dimmed below)
                using (ctx.PushTransform(Matrix.CreateScale(_vs, _vs) * Matrix.CreateTranslation(_origin.X, _origin.Y)))
                {
                    if (ShowGrid) DrawGrid(ctx);
                    DrawNode(ctx, c.Root);
                }
                if (TestCanvas == null && ShowBounds) DrawBounds(ctx, c.Root);
                DimOutside(ctx, screen);
                if (TestCanvas == null) DrawSelection(ctx);
            }
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1), screen);
            var label = new FormattedText(ScreenW + " × " + ScreenH + "   ·   " + Math.Round(_vs * 100) + "%" + (TestCanvas != null ? "   ·   TEST — click the widgets" : ""), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Faces[0], 11, new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)));
            ctx.DrawText(label, new Point(screen.X, Math.Min(Bounds.Height - 16, screen.Bottom + 5)));
        }

        private void DrawGrid(DrawingContext ctx)
        {
            float step = 40 * _layoutScale;
            if (step * _vs < 6) return;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1 / _vs);
            for (float x = step; x < ScreenW; x += step) ctx.DrawLine(pen, new Point(x, 0), new Point(x, ScreenH));
            for (float y = step; y < ScreenH; y += step) ctx.DrawLine(pen, new Point(0, y), new Point(ScreenW, y));
        }

        private static IEnumerable<VuiElement> Kids(VuiElement e) => e.Kind == VuiKind.List && e.RowPool != null ? e.RowPool : e.Children;

        private void DrawNode(DrawingContext ctx, VuiElement e)
        {
            if (e == null || !e.RuntimeVisibleEffective) return;
            Emit(ctx, e);
            foreach (var k in Kids(e))
            {
                // clipped containers (scrolling lists): children fully outside are culled (the runtime scissors them)
                if (e.ClipChildren && !Intersects(k.Resolved, e.Resolved)) continue;
                DrawNode(ctx, k);
            }
        }

        private static bool Intersects(RectF a, RectF b) => a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y;

        private void DimOutside(DrawingContext ctx, Rect screen)
        {
            var dim = new SolidColorBrush(Color.FromArgb(185, 13, 13, 16));
            var all = new Rect(Bounds.Size);
            ctx.FillRectangle(dim, new Rect(all.X, all.Y, all.Width, Math.Max(0, screen.Y - all.Y)));
            ctx.FillRectangle(dim, new Rect(all.X, screen.Bottom, all.Width, Math.Max(0, all.Bottom - screen.Bottom)));
            ctx.FillRectangle(dim, new Rect(all.X, screen.Y, Math.Max(0, screen.X - all.X), screen.Height));
            ctx.FillRectangle(dim, new Rect(screen.Right, screen.Y, Math.Max(0, all.Right - screen.Right), screen.Height));
        }

        private static Rect R(RectF r) => new Rect(r.X, r.Y, Math.Max(0, r.W), Math.Max(0, r.H));

        private static Color Col(float[] c, float opacity = 1f)
        {
            if (c == null || c.Length < 4) return Colors.White;
            return Color.FromArgb(B(c[3] * opacity), B(c[0]), B(c[1]), B(c[2]));
        }
        private static byte B(float v) { int i = (int)(v * 255f + 0.5f); return (byte)(i < 0 ? 0 : (i > 255 ? 255 : i)); }

        private static void Rect(DrawingContext ctx, RectF r, float[] col, float radius, float opacity)
        {
            if (col == null || col.Length < 4 || col[3] * opacity <= 0f || r.W <= 0 || r.H <= 0) return;
            double rad = Math.Max(0, Math.Min(radius, Math.Min(r.W, r.H) / 2));
            ctx.DrawRectangle(new SolidColorBrush(Col(col, opacity)), null, R(r), rad, rad);
        }

        private void Text(DrawingContext ctx, RectF r, string text, float size, Color color, int align, int weight)
        {
            if (string.IsNullOrEmpty(text) || r.W <= 1 || r.H <= 1 || color.A == 0) return;
            var face = Faces[weight >= 700 ? 2 : weight >= 600 ? 1 : 0];
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, Math.Max(1, size), new SolidColorBrush(color))
            {
                MaxTextWidth = r.W,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = align == 1 ? TextAlignment.Center : align == 2 ? TextAlignment.Right : TextAlignment.Left
            };
            // No PushClip around text: on this Avalonia build a clip pushed/popped around a text draw makes every
            // later text of the pass disappear. Width is bounded by MaxTextWidth + trimming instead.
            double y = r.Y + (r.H - ft.Height) / 2;
            ctx.DrawText(ft, new Point(r.X, y));
        }

        private void Emit(DrawingContext ctx, VuiElement e)
        {
            var r = e.Resolved;
            float s = _layoutScale, a = e.Opacity <= 0 ? 0 : e.Opacity;
            switch (e.Kind)
            {
                case VuiKind.Panel:
                case VuiKind.List:
                    Rect(ctx, r, e.Bg, e.Radius * s, a);
                    break;
                case VuiKind.Text:
                    Text(ctx, r, e.Text, e.FontSize * s, Col(e.Fg, a), e.Align, e.Weight);
                    break;
                case VuiKind.Image:
                    DrawImage(ctx, e, r, a);
                    break;
                case VuiKind.Bar:
                {
                    Rect(ctx, r, e.Bg, e.Radius * s, a);
                    float t = e.Max > e.Min ? (e.Value - e.Min) / (e.Max - e.Min) : e.Value;
                    t = t < 0 ? 0 : t > 1 ? 1 : t;
                    float fw = (float)Math.Round(r.W * t);
                    if (fw > 0) Rect(ctx, new RectF(r.X, r.Y, fw, r.H), e.Fg, e.Radius * s, a);
                    break;
                }
                case VuiKind.Button:
                {
                    float[] face = e.Hot ? (e.HoverTint ?? Lighten(e.Bg)) : e.Bg;
                    Rect(ctx, r, face, e.Radius * s, a);
                    Text(ctx, r, e.Text, e.FontSize * s, Col(e.Fg, a), 1, e.Weight);
                    break;
                }
                case VuiKind.Slider:
                {
                    Rect(ctx, new RectF(r.X, r.Y + r.H * 0.5f - 3 * s, r.W, 6 * s), e.Bg, 3 * s, a);
                    float t = e.Max > e.Min ? (e.Value - e.Min) / (e.Max - e.Min) : e.Value;
                    t = t < 0 ? 0 : t > 1 ? 1 : t;
                    Rect(ctx, new RectF(r.X + r.W * t - 7 * s, r.Y, 14 * s, r.H), e.Fg, 4 * s, a);
                    break;
                }
                case VuiKind.Toggle:
                    Rect(ctx, new RectF(r.X, r.Y, r.H, r.H), e.On ? e.Fg : e.Bg, 4 * s, a);
                    Text(ctx, new RectF(r.X + r.H + 8 * s, r.Y, r.W - r.H - 8 * s, r.H), e.Text, e.FontSize * s, Color.FromArgb(B(a), 230, 230, 237), 0, e.Weight);
                    break;
                case VuiKind.Stepper:
                {
                    Rect(ctx, r, e.Bg, e.Radius * s, a);
                    string val = e.Options != null && e.OptionIndex >= 0 && e.OptionIndex < e.Options.Length ? e.Options[e.OptionIndex] : "";
                    Text(ctx, r, "‹  " + val + "  ›", e.FontSize * s, Col(e.Fg, a), 1, e.Weight);
                    break;
                }
                case VuiKind.TextField:
                {
                    Rect(ctx, r, e.Bg, e.Radius * s, a);
                    bool focused = TestCanvas != null && ReferenceEquals(e, TestCanvas.TextFocus);
                    Text(ctx, new RectF(r.X + 8 * s, r.Y, r.W - 16 * s, r.H), (e.Text ?? "") + (focused ? "|" : ""), e.FontSize * s, Col(e.Fg, a), 0, e.Weight);
                    break;
                }
                case VuiKind.Crosshair:
                {
                    float cx = r.CenterX, cy = r.CenterY, ext = r.W * 0.5f, gap = 2 * s, th = 2 * s;
                    var pen = new Pen(new SolidColorBrush(Col(e.Fg, a)), th);
                    ctx.DrawLine(pen, new Point(cx - ext, cy), new Point(cx - gap, cy));
                    ctx.DrawLine(pen, new Point(cx + gap, cy), new Point(cx + ext, cy));
                    ctx.DrawLine(pen, new Point(cx, cy - ext), new Point(cx, cy - gap));
                    ctx.DrawLine(pen, new Point(cx, cy + gap), new Point(cx, cy + ext));
                    break;
                }
            }
        }

        private static float[] Lighten(float[] c)
        {
            if (c == null) return new[] { 0.3f, 0.3f, 0.34f, 1f };
            float L(float v) => Math.Min(1f, v + 0.09f);
            return new[] { L(c[0]), L(c[1]), L(c[2]), c.Length > 3 ? c[3] : 1f };
        }

        private void DrawImage(DrawingContext ctx, VuiElement e, RectF r, float a)
        {
            var bmp = ImageFor(e.ImageAsset);
            if (bmp != null)
            {
                using (ctx.PushOpacity((e.Fg != null && e.Fg.Length > 3 ? e.Fg[3] : 1f) * a))
                    ctx.DrawImage(bmp, new Rect(0, 0, bmp.Size.Width, bmp.Size.Height), R(r));
                return;
            }
            // placeholder (missing / not yet assigned image)
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(B(a), 60, 60, 70)), null, R(r));
            if (Application.Current != null && Application.Current.TryFindResource("IconImage", out var g) && g is Geometry geo && r.W > 8 && r.H > 8)
            {
                double side = Math.Min(r.W, r.H) * 0.45;
                var gb = geo.Bounds;
                double k = side / Math.Max(1, Math.Max(gb.Width, gb.Height));
                using (ctx.PushTransform(Matrix.CreateTranslation(-gb.X - gb.Width / 2, -gb.Y - gb.Height / 2) * Matrix.CreateScale(k, k) * Matrix.CreateTranslation(r.CenterX, r.CenterY)))
                    ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(150, 160, 160, 175)), null, geo);
            }
        }

        /// <summary>Resolve an image asset (absolute or project-relative) and cache the decoded bitmap.</summary>
        public Bitmap ImageFor(string asset)
        {
            if (string.IsNullOrEmpty(asset)) return null;
            string path = ResolveAsset(asset);
            if (_images.TryGetValue(path, out var hit)) return hit;
            Bitmap bmp = null;
            try { if (File.Exists(path)) using (var st = File.OpenRead(path)) bmp = new Bitmap(st); } catch { bmp = null; }
            _images[path] = bmp;   // cache misses too
            return bmp;
        }

        public void ClearImageCache() => _images.Clear();

        public static string ResolveAsset(string asset)
        {
            if (string.IsNullOrEmpty(asset)) return asset;
            string p = asset.Replace('\\', '/');
            if (Path.IsPathRooted(p)) return p;
            var root = ProjectData.Current?.Path;
            return string.IsNullOrEmpty(root) ? p : Path.Combine(root, p);
        }

        private void DrawBounds(DrawingContext ctx, VuiElement root)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)), 1);
            void Walk(VuiElement e)
            {
                if (e == null || !e.RuntimeVisibleEffective) return;
                if (!ReferenceEquals(e, root)) ctx.DrawRectangle(null, pen, ToView(e.Resolved).Deflate(0.5));
                foreach (var k in Kids(e)) Walk(k);
            }
            Walk(root);
        }

        private void DrawSelection(DrawingContext ctx)
        {
            var accent = Application.Current != null && Application.Current.TryFindResource("VxAccent", out var ac) && ac is Color col ? col : Color.FromRgb(90, 170, 255);
            if (_hover != null && !ReferenceEquals(_hover, _selected) && TryGetRect(_hover, out var hr))
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(150, accent.R, accent.G, accent.B)), 1), ToView(hr).Deflate(0.5));
            var sel = _selected;
            if (sel == null || !TryGetRect(sel, out var sr)) return;
            var vr = ToView(sr);
            // parent context + anchor marker (where the offsets are measured from)
            var parent = sel.Parent;
            if (parent != null && !_templateToClone.ContainsKey(sel) && !_cloneToTemplate.ContainsKey(sel) && TryGetRect(parent, out var pr))
            {
                var pv = ToView(pr);
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1, new DashStyle(new double[] { 2, 3 }, 0)), pv.Deflate(0.5));
                if (parent.LayoutMode == StackDir.None)
                {
                    AnchorAxis(sel.Anchor, out float ax, out float ay);
                    float pvx = sel.HasPivot ? sel.PivotX : ax, pvy = sel.HasPivot ? sel.PivotY : ay;
                    var anchorPt = new Point(pv.X + ax * pv.Width + sel.PctX * pv.Width, pv.Y + ay * pv.Height + sel.PctY * pv.Height);
                    var pivotPt = new Point(vr.X + pvx * vr.Width, vr.Y + pvy * vr.Height);
                    var ab = new SolidColorBrush(Color.FromArgb(220, 255, 196, 64));
                    ctx.DrawLine(new Pen(ab, 1, new DashStyle(new double[] { 3, 2 }, 0)), anchorPt, pivotPt);
                    var diamond = new StreamGeometry();
                    using (var g = diamond.Open())
                    {
                        g.BeginFigure(new Point(anchorPt.X, anchorPt.Y - 5), true);
                        g.LineTo(new Point(anchorPt.X + 5, anchorPt.Y)); g.LineTo(new Point(anchorPt.X, anchorPt.Y + 5)); g.LineTo(new Point(anchorPt.X - 5, anchorPt.Y));
                        g.EndFigure(true);
                    }
                    ctx.DrawGeometry(ab, null, diamond);
                }
            }
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(accent), 1.5), vr.Deflate(0.75));
            if (CanResize(sel))
            {
                var fill = new SolidColorBrush(Colors.White);
                var stroke = new Pen(new SolidColorBrush(accent), 1.2);
                for (int h = 0; h < 8; h++)
                {
                    if (!HandleAllowed(sel, h)) continue;
                    var p = HandlePoint(vr, h);
                    ctx.DrawRectangle(fill, stroke, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize), 1.5, 1.5);
                }
            }
        }

        // handles: 0 TL, 1 T, 2 TR, 3 R, 4 BR, 5 B, 6 BL, 7 L
        private static Point HandlePoint(Rect r, int h)
        {
            double x = h == 0 || h == 6 || h == 7 ? r.X : h == 1 || h == 5 ? r.X + r.Width / 2 : r.Right;
            double y = h == 0 || h == 1 || h == 2 ? r.Y : h == 3 || h == 7 ? r.Y + r.Height / 2 : r.Bottom;
            return new Point(x, y);
        }

        private bool IsDesignRow(VuiElement e) => _cloneToTemplate.ContainsKey(e) || _templateToClone.ContainsKey(e);

        /// <summary>Positioned by a layout container (Vertical / Horizontal / Grid) rather than its own anchor + offset.</summary>
        public static bool IsArranged(VuiElement e) => e?.Parent != null && e.Parent.LayoutMode != StackDir.None;

        public bool CanMove(VuiElement e) => e != null && e.Parent != null && !IsArranged(e) && !IsDesignRow(e);

        private bool CanResize(VuiElement e)
        {
            if (e == null || e.Parent == null) return false;
            if (IsDesignRow(e)) return false;
            if (!IsArranged(e)) return true;
            return e.Parent.LayoutMode == StackDir.Vertical || e.Parent.LayoutMode == StackDir.Horizontal;
        }

        private static bool HandleAllowed(VuiElement e, int h)
        {
            if (!IsArranged(e)) return true;
            if (e.Parent.LayoutMode == StackDir.Vertical) return h == 5;     // height only
            if (e.Parent.LayoutMode == StackDir.Horizontal) return h == 3;   // width only
            return false;
        }

        // ================================================================ hit testing
        /// <summary>Topmost element at a point in screen pixels (the list template for design rows; the root when none).</summary>
        public VuiElement HitTest(double sx, double sy)
        {
            var c = Active;
            if (c?.Root == null) return null;
            VuiElement Hit(VuiElement e)
            {
                if (e == null || !e.RuntimeVisibleEffective) return null;
                var kids = new List<VuiElement>(Kids(e));
                bool inside = e.Resolved.Contains((float)sx, (float)sy);
                if (!e.ClipChildren || inside)
                    for (int i = kids.Count - 1; i >= 0; i--) { var h = Hit(kids[i]); if (h != null) return h; }
                return inside ? e : null;
            }
            var hit = Hit(c.Root) ?? c.Root;
            return _cloneToTemplate.TryGetValue(hit, out var t) ? t : hit;
        }

        private int HandleAt(Point view)
        {
            var sel = _selected;
            if (sel == null || !CanResize(sel) || !TryGetRect(sel, out var sr)) return -1;
            var vr = ToView(sr);
            for (int h = 0; h < 8; h++)
            {
                if (!HandleAllowed(sel, h)) continue;
                var p = HandlePoint(vr, h);
                if (Math.Abs(view.X - p.X) <= HandleSize && Math.Abs(view.Y - p.Y) <= HandleSize) return h;
            }
            return -1;
        }

        private static Cursor CursorFor(int h)
        {
            switch (h)
            {
                case 0: return new Cursor(StandardCursorType.TopLeftCorner);
                case 2: return new Cursor(StandardCursorType.TopRightCorner);
                case 4: return new Cursor(StandardCursorType.BottomRightCorner);
                case 6: return new Cursor(StandardCursorType.BottomLeftCorner);
                case 1: case 5: return new Cursor(StandardCursorType.SizeNorthSouth);
                case 3: case 7: return new Cursor(StandardCursorType.SizeWestEast);
                default: return Cursor.Default;
            }
        }

        // ================================================================ input
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var pt = e.GetCurrentPoint(this);
            var pos = pt.Position;
            ComputeView();
            if (pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed)
            {
                _drag = DragKind.Pan; _dragStart = pos; _panStart = _pan;
                if (_zoom <= 0) { _zoom = _vs; }   // leave fit mode so panning moves the view
                e.Pointer.Capture(this); e.Handled = true;
                return;
            }
            if (!pt.Properties.IsLeftButtonPressed) return;
            RunLayout();
            if (TestCanvas != null)
            {
                _drag = DragKind.Test; _testDown = true;
                FeedTest(pos, pressed: true);
                e.Pointer.Capture(this); e.Handled = true;
                return;
            }
            if (e.ClickCount == 2)
            {
                var sp2 = ToScreen(pos);
                if (sp2.X < 0 || sp2.Y < 0 || sp2.X > ScreenW || sp2.Y > ScreenH) { Fit(); e.Handled = true; return; }
            }
            int h = HandleAt(pos);
            if (h >= 0)
            {
                BeginDrag(DragKind.Resize, pos, h);
                e.Pointer.Capture(this); e.Handled = true;
                return;
            }
            var sp = ToScreen(pos);
            var hit = HitTest(sp.X, sp.Y);
            if (!ReferenceEquals(hit, _selected)) { _selected = hit; SelectionRequested?.Invoke(hit); }
            if (CanMove(hit)) BeginDrag(DragKind.Move, pos, -1);
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
        }

        private void BeginDrag(DragKind kind, Point pos, int handle)
        {
            var sel = _selected;
            if (sel == null) return;
            _drag = kind; _handle = handle; _dragStart = pos; _dragChanged = false;
            _startVals = (sel.OffX, sel.OffY, sel.W, sel.H, sel.WPct, sel.HPct);
            _parentRect = sel.Parent != null ? sel.Parent.Resolved : new RectF(0, 0, ScreenW, ScreenH);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var pos = e.GetPosition(this);
            if (_drag == DragKind.Pan)
            {
                _pan = _panStart + (pos - _dragStart);
                InvalidateVisual();
                return;
            }
            if (_drag == DragKind.Test) { FeedTest(pos, pressed: false); return; }
            if (TestCanvas != null) { FeedTest(pos, pressed: false); return; }
            if (_drag == DragKind.Move || _drag == DragKind.Resize)
            {
                double dx = (pos.X - _dragStart.X) / _vs, dy = (pos.Y - _dragStart.Y) / _vs;
                if (!_dragChanged && Math.Abs(dx * _vs) < 2 && Math.Abs(dy * _vs) < 2) return;
                if (!_dragChanged) { _dragChanged = true; EditStarting?.Invoke(); }
                ApplyDrag((float)dx, (float)dy, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                Edited?.Invoke();
                InvalidateVisual();
                return;
            }
            // hover + cursor feedback
            ComputeView();
            int h = HandleAt(pos);
            Cursor = h >= 0 ? CursorFor(h) : Cursor.Default;
            var sp = ToScreen(pos);
            var hit = sp.X >= 0 && sp.Y >= 0 && sp.X <= ScreenW && sp.Y <= ScreenH ? HitTest(sp.X, sp.Y) : null;
            if (hit != null && hit.Parent == null) hit = null;
            if (!ReferenceEquals(hit, _hover)) { _hover = hit; InvalidateVisual(); }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_drag == DragKind.Test) { _testDown = false; FeedTest(e.GetPosition(this), pressed: false); }
            bool edited = (_drag == DragKind.Move || _drag == DragKind.Resize) && _dragChanged;
            _drag = DragKind.None; _handle = -1;
            e.Pointer.Capture(null);
            if (edited) EditCompleted?.Invoke();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (_hover != null) { _hover = null; InvalidateVisual(); }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            ComputeView();
            var pos = e.GetPosition(this);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                double old = _vs;
                double z = Math.Max(0.05, Math.Min(8, old * Math.Pow(1.15, e.Delta.Y)));
                // keep the screen point under the cursor fixed
                var sp = ToScreen(pos);
                _zoom = z;
                double w = ScreenW * z, h = ScreenH * z;
                var center = new Point((Bounds.Width - w) / 2, (Bounds.Height - 14 - h) / 2);
                var wantOrigin = new Point(pos.X - sp.X * z, pos.Y - sp.Y * z);
                _pan = wantOrigin - center;
            }
            else
            {
                if (_zoom <= 0) { e.Handled = true; return; }
                _pan += new Vector(e.Delta.X * 40, e.Delta.Y * 40);
            }
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (TestCanvas != null) { FeedTestKey(e); return; }
            var sel = _selected;
            float step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            float nx = 0, ny = 0;
            switch (e.Key)
            {
                case Key.Left: nx = -step; break;
                case Key.Right: nx = step; break;
                case Key.Up: ny = -step; break;
                case Key.Down: ny = step; break;
                case Key.Delete: case Key.Back: DeleteRequested?.Invoke(); e.Handled = true; return;
                case Key.Escape: if (sel?.Parent != null) { _selected = sel.Parent; SelectionRequested?.Invoke(_selected); InvalidateVisual(); } e.Handled = true; return;
                default: return;
            }
            if (!CanMove(sel)) return;
            EditStarting?.Invoke();
            RunLayout();
            _startVals = (sel.OffX, sel.OffY, sel.W, sel.H, sel.WPct, sel.HPct);
            _parentRect = sel.Parent.Resolved;
            _drag = DragKind.Move;
            ApplyDrag(nx * _layoutScale, ny * _layoutScale, false);
            _drag = DragKind.None;
            Edited?.Invoke();
            EditCompleted?.Invoke();
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (TestCanvas == null || string.IsNullOrEmpty(e.Text)) return;
            var input = new VuiInput { Mx = -1, My = -1, Chars = e.Text.ToCharArray(), CharCount = e.Text.Length, KeyEvents = new int[0] };
            try { TestCanvas.Update(input); } catch { }
            InvalidateVisual();
            e.Handled = true;
        }

        /// <summary>Drag the element's body (handle -1) or one of its resize handles (0 TL … 7 L, clockwise) by (dx, dy)
        /// screen pixels — the exact path a mouse drag takes (scripted edits, tests).</summary>
        public void DragBy(VuiElement e, int handle, double dx, double dy)
        {
            if (e == null || e.Parent == null) return;
            _selected = e;
            RunLayout();
            _parentRect = e.Parent.Resolved;
            _startVals = (e.OffX, e.OffY, e.W, e.H, e.WPct, e.HPct);
            _handle = handle;
            _drag = handle < 0 ? DragKind.Move : DragKind.Resize;
            EditStarting?.Invoke();
            ApplyDrag((float)dx, (float)dy, false);
            _drag = DragKind.None; _handle = -1;
            Edited?.Invoke();
            EditCompleted?.Invoke();
            InvalidateVisual();
        }

        // ================================================================ move / resize math
        /// <summary>Apply a drag of (dx, dy) screen pixels from the drag start to the selected element.</summary>
        private void ApplyDrag(float dx, float dy, bool shift)
        {
            var e = _selected;
            if (e == null) return;
            float s = _layoutScale;
            var st = _startVals;
            if (_drag == DragKind.Move)
            {
                if (shift) { if (Math.Abs(dx) > Math.Abs(dy)) dy = 0; else dx = 0; }   // axis lock
                AxisX(e, st, s, dx, dx);
                AxisY(e, st, s, dy, dy);
                return;
            }
            if (IsArranged(e))
            {
                if (_handle == 5) e.H = (float)Math.Max(1, Math.Round(st.h + dy / s));
                if (_handle == 3) e.W = (float)Math.Max(1, Math.Round(st.w + dx / s));
                return;
            }
            float l = 0, r = 0, t = 0, b = 0;
            if (_handle == 0 || _handle == 6 || _handle == 7) l = dx;
            if (_handle == 2 || _handle == 3 || _handle == 4) r = dx;
            if (_handle == 0 || _handle == 1 || _handle == 2) t = dy;
            if (_handle == 4 || _handle == 5 || _handle == 6) b = dy;
            if (l != 0 || r != 0) AxisX(e, st, s, l, r);
            if (t != 0 || b != 0) AxisY(e, st, s, t, b);
        }

        private void AxisX(VuiElement e, (float offX, float offY, float w, float h, float wPct, float hPct) st, float s, float leftDelta, float rightDelta)
        {
            AnchorAxis(e.Anchor, out float ax, out _);
            float pivot = e.HasPivot ? e.PivotX : ax;
            var P = _parentRect;
            if (e.StretchX)
            {
                // OffX = left margin, -W = right margin (design units)
                float left = st.offX * s + e.PctX * P.W + leftDelta, right = -st.w * s - rightDelta;
                if (P.W - left - right < 2) return;
                e.OffX = (float)Math.Round(st.offX + leftDelta / s);
                e.W = (float)Math.Round(st.w + rightDelta / s);
                return;
            }
            float w0 = st.wPct > 0 ? st.wPct * P.W : st.w * s;
            float w1 = Math.Max(2f, w0 + rightDelta - leftDelta);
            float offPx = st.offX * s + leftDelta + pivot * (w1 - w0);
            e.OffX = (float)Math.Round(offPx / s);
            if (st.wPct > 0) e.WPct = (float)Math.Round(w1 / Math.Max(1f, P.W), 4);
            else e.W = (float)Math.Max(1, Math.Round(w1 / s));
        }

        private void AxisY(VuiElement e, (float offX, float offY, float w, float h, float wPct, float hPct) st, float s, float topDelta, float bottomDelta)
        {
            AnchorAxis(e.Anchor, out _, out float ay);
            float pivot = e.HasPivot ? e.PivotY : ay;
            var P = _parentRect;
            if (e.StretchY)
            {
                float top = st.offY * s + e.PctY * P.H + topDelta, bottom = -st.h * s - bottomDelta;
                if (P.H - top - bottom < 2) return;
                e.OffY = (float)Math.Round(st.offY + topDelta / s);
                e.H = (float)Math.Round(st.h + bottomDelta / s);
                return;
            }
            float h0 = st.hPct > 0 ? st.hPct * P.H : st.h * s;
            float h1 = Math.Max(2f, h0 + bottomDelta - topDelta);
            float offPx = st.offY * s + topDelta + pivot * (h1 - h0);
            e.OffY = (float)Math.Round(offPx / s);
            if (st.hPct > 0) e.HPct = (float)Math.Round(h1 / Math.Max(1f, P.H), 4);
            else e.H = (float)Math.Max(1, Math.Round(h1 / s));
        }

        // ================================================================ test mode
        private void FeedTest(Point view, bool pressed)
        {
            var c = TestCanvas;
            if (c == null) return;
            ComputeView();
            var sp = ToScreen(view);
            var input = new VuiInput { Mx = (float)sp.X, My = (float)sp.Y, Down = _testDown, Pressed = pressed, Chars = new char[0], KeyEvents = new int[0] };
            try
            {
                c.Layout(ScreenW, ScreenH);
                c.Update(input);
                if (c.FiredActions != null) foreach (var a in c.FiredActions) TestActionFired?.Invoke(a);
            }
            catch { }
            InvalidateVisual();
        }

        private void FeedTestKey(KeyEventArgs e)
        {
            var c = TestCanvas;
            if (c == null) return;
            char ch = e.Key == Key.Back ? '\b' : e.Key == Key.Return ? '\r' : '\0';
            if (ch == '\0') return;
            var input = new VuiInput { Mx = -1, My = -1, Chars = new[] { ch }, CharCount = 1, KeyEvents = new int[0] };
            try { c.Update(input); } catch { }
            InvalidateVisual();
            e.Handled = true;
        }
    }
}
