using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Editor.UI.Vui;

namespace Editor.Editors.UIEditor
{
    /// <summary>
    /// Renders a VuiCanvas into a WPF Canvas by running the IDENTICAL runtime <see cref="VuiCanvas.Layout"/> and
    /// translating each element's resolved pixel rect 1:1 into WPF shapes. Because the layout math is shared with
    /// the game, the builder preview == the in-game pixels at every resolution — no second layout engine to drift.
    /// </summary>
    public static class VuiPreviewRenderer
    {
        /// <summary>Lay out <paramref name="canvas"/> at (w,h) and draw it into <paramref name="target"/>.
        /// The element whose rect equals <paramref name="selected"/> gets a selection outline.</summary>
        public static void Render(VuiCanvas canvas, Canvas target, double w, double h, VuiElement selected)
        {
            if (target == null) return;
            target.Children.Clear();
            if (canvas == null || canvas.Root == null) return;

            canvas.Layout((float)w, (float)h);
            float scale = canvas.Scale;

            // Backdrop so the design surface reads as a screen.
            var bg = new Rectangle { Width = w, Height = h, Fill = new SolidColorBrush(Color.FromRgb(18, 18, 22)) };
            Canvas.SetLeft(bg, 0); Canvas.SetTop(bg, 0);
            target.Children.Add(bg);

            DrawNode(canvas.Root, target, scale, selected);
        }

        private static void DrawNode(VuiElement e, Canvas target, float scale, VuiElement selected)
        {
            if (e == null || !e.Visible) return;
            EmitNode(e, target, scale);

            // Children (for a List with no fed rows, show its RowTemplate once as a sample so the row is visible).
            if (e.Kind == VuiKind.List && (e.RowPool == null || e.RowPool.Count == 0) && e.RowTemplate != null)
            {
                var sample = e.RowTemplate;
                // place the sample at the list's top-left, its authored size, then draw it + its children
                sample.Resolved = new RectF(e.Resolved.X + e.Padding * scale, e.Resolved.Y + e.Padding * scale,
                    e.Resolved.W - 2 * e.Padding * scale, (sample.H > 0 ? sample.H : 26) * scale);
                LayoutSampleChildren(sample, scale);
                DrawNode(sample, target, scale, selected);
            }
            else
            {
                foreach (var c in e.Children) DrawNode(c, target, scale, selected);
            }

            if (ReferenceEquals(e, selected))
            {
                var sel = new Rectangle
                {
                    Width = Math.Max(1, e.Resolved.W), Height = Math.Max(1, e.Resolved.H),
                    Stroke = new SolidColorBrush(Color.FromRgb(90, 170, 255)), StrokeThickness = 1.5,
                    Fill = Brushes.Transparent, IsHitTestVisible = false
                };
                sel.StrokeDashArray = new DoubleCollection { 3, 2 };
                Canvas.SetLeft(sel, e.Resolved.X); Canvas.SetTop(sel, e.Resolved.Y);
                target.Children.Add(sel);
            }
        }

        // Minimal stand-in layout for a sample row's own children (MidLeft text etc.) so the preview shows them.
        private static void LayoutSampleChildren(VuiElement parent, float scale)
        {
            foreach (var c in parent.Children)
            {
                float w = c.W > 0 ? c.W * scale : parent.Resolved.W;
                float hh = c.H > 0 ? c.H * scale : parent.Resolved.H;
                float x = parent.Resolved.X + c.OffX * scale;
                float y = parent.Resolved.Y + (parent.Resolved.H - hh) * 0.5f + c.OffY * scale;
                c.Resolved = new RectF(x, y, w, hh);
            }
        }

        private static void EmitNode(VuiElement e, Canvas target, float scale)
        {
            RectF r = e.Resolved;
            switch (e.Kind)
            {
                case VuiKind.Panel:
                    if (e.Bg != null && e.Bg[3] > 0f) AddRect(target, r, Col(e.Bg), e.Radius * scale);
                    break;
                case VuiKind.Text:
                    AddText(target, r, e.Text, e.FontSize * scale, Col(e.Fg), e.Align, e.Weight);
                    break;
                case VuiKind.Image:
                    AddImage(target, r, e.ImageAsset);
                    break;
                case VuiKind.Bar:
                {
                    if (e.Bg != null && e.Bg[3] > 0f) AddRect(target, r, Col(e.Bg), e.Radius * scale);
                    float t = e.Max > e.Min ? (e.Value - e.Min) / (e.Max - e.Min) : e.Value;
                    t = t < 0 ? 0 : (t > 1 ? 1 : t);
                    if (t > 0) AddRect(target, new RectF(r.X, r.Y, r.W * t, r.H), Col(e.Fg), e.Radius * scale);
                    break;
                }
                case VuiKind.Button:
                    AddRect(target, r, Col(e.Bg), e.Radius * scale);
                    AddText(target, r, e.Text, e.FontSize * scale, Col(e.Fg), 1, e.Weight);
                    break;
                case VuiKind.Slider:
                {
                    AddRect(target, new RectF(r.X, r.CenterY - 3 * scale, r.W, 6 * scale), Col(e.Bg), 3 * scale);
                    float t = e.Max > e.Min ? (e.Value - e.Min) / (e.Max - e.Min) : e.Value;
                    t = t < 0 ? 0 : (t > 1 ? 1 : t);
                    AddRect(target, new RectF(r.X + r.W * t - 7 * scale, r.Y, 14 * scale, r.H), Col(e.Fg), 4 * scale);
                    break;
                }
                case VuiKind.Toggle:
                    AddRect(target, new RectF(r.X, r.Y, r.H, r.H), Col(e.On ? e.Fg : e.Bg), 4 * scale);
                    AddText(target, new RectF(r.X + r.H + 8 * scale, r.Y, r.W - r.H - 8 * scale, r.H), e.Text, e.FontSize * scale, Color.FromRgb(230, 230, 237), 0, e.Weight);
                    break;
                case VuiKind.Stepper:
                {
                    if (e.Bg != null && e.Bg[3] > 0f) AddRect(target, r, Col(e.Bg), e.Radius * scale);
                    string val = (e.Options != null && e.OptionIndex >= 0 && e.OptionIndex < e.Options.Length) ? e.Options[e.OptionIndex] : "";
                    AddText(target, r, "‹  " + val + "  ›", e.FontSize * scale, Col(e.Fg), 1, e.Weight);
                    break;
                }
                case VuiKind.TextField:
                    AddRect(target, r, Col(e.Bg), e.Radius * scale);
                    AddText(target, new RectF(r.X + 8 * scale, r.Y, r.W - 16 * scale, r.H), e.Text, e.FontSize * scale, Col(e.Fg), 0, e.Weight);
                    break;
                case VuiKind.Crosshair:
                {
                    float cx = r.CenterX, cy = r.CenterY, ext = r.W * 0.5f, gap = 2 * scale, th = 2 * scale;
                    var c = Col(e.Fg);
                    AddLine(target, cx - ext, cy, cx - gap, cy, c, th); AddLine(target, cx + gap, cy, cx + ext, cy, c, th);
                    AddLine(target, cx, cy - ext, cx, cy - gap, c, th); AddLine(target, cx, cy + gap, cx, cy + ext, c, th);
                    break;
                }
                case VuiKind.List:
                    if (e.Bg != null && e.Bg[3] > 0f) AddRect(target, r, Col(e.Bg), e.Radius * scale);
                    break;
            }
        }

        private static Color Col(float[] c)
        {
            if (c == null || c.Length < 4) return Colors.White;
            return Color.FromArgb(B(c[3]), B(c[0]), B(c[1]), B(c[2]));
        }
        private static byte B(float v) { int i = (int)(v * 255f + 0.5f); return (byte)(i < 0 ? 0 : (i > 255 ? 255 : i)); }

        private static void AddRect(Canvas t, RectF r, Color c, float radius)
        {
            var rc = new Rectangle { Width = Math.Max(0, r.W), Height = Math.Max(0, r.H), Fill = new SolidColorBrush(c) };
            if (radius > 0.5f) { rc.RadiusX = radius; rc.RadiusY = radius; }
            Canvas.SetLeft(rc, r.X); Canvas.SetTop(rc, r.Y);
            t.Children.Add(rc);
        }

        private static void AddText(Canvas t, RectF r, string text, float size, Color c, int align, int weight)
        {
            if (string.IsNullOrEmpty(text)) return;
            var tb = new TextBlock
            {
                Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = Math.Max(1, size),
                Foreground = new SolidColorBrush(c), Width = Math.Max(0, r.W), Height = Math.Max(0, r.H),
                TextAlignment = align == 1 ? TextAlignment.Center : (align == 2 ? TextAlignment.Right : TextAlignment.Left),
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = weight >= 700 ? FontWeights.Bold : (weight >= 600 ? FontWeights.SemiBold : FontWeights.Normal)
            };
            // vertical-center to match the runtime's DirectWrite paragraph-center
            tb.VerticalAlignment = VerticalAlignment.Center;
            var host = new Border { Width = Math.Max(0, r.W), Height = Math.Max(0, r.H), Child = tb };
            tb.HorizontalAlignment = HorizontalAlignment.Stretch;
            Canvas.SetLeft(host, r.X); Canvas.SetTop(host, r.Y);
            t.Children.Add(host);
        }

        private static void AddImage(Canvas t, RectF r, string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    var img = new Image { Width = Math.Max(0, r.W), Height = Math.Max(0, r.H), Stretch = Stretch.Fill };
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path); bmp.EndInit();
                    img.Source = bmp;
                    Canvas.SetLeft(img, r.X); Canvas.SetTop(img, r.Y);
                    t.Children.Add(img);
                    return;
                }
            }
            catch { }
            // placeholder when the image is missing
            AddRect(t, r, Color.FromRgb(60, 60, 70), 0);
            AddText(t, r, "🖼", Math.Max(10, r.H * 0.5f), Colors.Gray, 1, 400);
        }

        private static void AddLine(Canvas t, float x1, float y1, float x2, float y2, Color c, float th)
        {
            var ln = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = new SolidColorBrush(c), StrokeThickness = th };
            t.Children.Add(ln);
        }
    }
}
