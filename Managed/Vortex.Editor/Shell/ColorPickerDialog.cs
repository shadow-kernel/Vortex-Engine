using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VortexEditor.Shell.Material;

namespace VortexEditor.Shell
{
    /// <summary>
    /// HSV colour picker (port of the Windows editor's ColorPickerDialog): saturation/value square, hue bar, R/G/B
    /// sliders with number boxes, hex field, new/original preview, OK / Cancel. Extras: optional alpha channel, a live
    /// callback so the caller can preview the colour while the user drags (reverted automatically on Cancel), and a
    /// row of recently picked colours. Modal to the active editor window.
    /// </summary>
    public sealed class ColorPickerDialog : Window
    {
        /// <summary>The dialog that is open right now (null when none) — lets tests and tools drive it.</summary>
        public static ColorPickerDialog Current { get; private set; }

        private static readonly List<Color> _recent = new List<Color>();

        /// <summary>Pick a colour; null when cancelled.</summary>
        public static Task<Color?> Pick(Color initial, string title) => Pick(initial, title, false, null, null);

        /// <summary>Pick a colour with an optional alpha channel. <paramref name="live"/> receives every change while the
        /// dialog is open (and the initial colour again on Cancel), for live previews.</summary>
        public static async Task<Color?> Pick(Color initial, string title, bool alpha, Action<Color> live = null, Window owner = null)
        {
            var dlg = new ColorPickerDialog(initial, string.IsNullOrEmpty(title) ? "Color" : title, alpha, live);
            owner = owner ?? EditorKit.ActiveWindow();
            Current = dlg;
            try
            {
                if (owner != null) await dlg.ShowDialog(owner);
                else
                {
                    var tcs = new TaskCompletionSource<bool>();
                    dlg.Closed += (s, e) => tcs.TrySetResult(true);
                    dlg.Show();
                    await tcs.Task;
                }
            }
            finally { if (ReferenceEquals(Current, dlg)) Current = null; }
            if (!dlg._accepted) return null;
            Remember(dlg.SelectedColor);
            return dlg.SelectedColor;
        }

        private static void Remember(Color c)
        {
            _recent.RemoveAll(x => x == c);
            _recent.Insert(0, c);
            if (_recent.Count > 12) _recent.RemoveRange(12, _recent.Count - 12);
        }

        // ---------------------------------------------------------------- state
        private readonly bool _alpha;
        private readonly Action<Color> _live;
        private bool _accepted, _sync;
        private double _h, _s, _v;   // hue 0..360, saturation / value 0..1 — the source of truth while dragging
        private byte _a = 255;

        public Color InitialColor { get; }
        public Color SelectedColor => ColorMath.FromHsv(_h, _s, _v, _alpha ? _a : (byte)255);

        private readonly SvSquare _square = new SvSquare();
        private readonly HueBar _hue = new HueBar();
        private readonly AlphaBar _alphaBar = new AlphaBar();
        private readonly ColorSwatch _preview = new ColorSwatch { Width = 72, Radius = 6 };
        private readonly Slider[] _sliders = new Slider[4];
        private readonly TextBox[] _boxes = new TextBox[4];
        private readonly TextBox _hex = new TextBox { MinWidth = 110 };
        private readonly TextBlock _hsvText = new TextBlock { Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center };

        public ColorPickerDialog() : this(Colors.White, "Color", false, null) { }

        public ColorPickerDialog(Color initial, string title, bool alpha, Action<Color> live)
        {
            _alpha = alpha;
            _live = live;
            InitialColor = alpha ? initial : Color.FromArgb(255, initial.R, initial.G, initial.B);
            Title = title;
            Width = alpha ? 500 : 470;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            ColorMath.ToHsv(InitialColor, out _h, out _s, out _v);
            _a = InitialColor.A;

            var root = new StackPanel { Margin = new Thickness(16), Spacing = 12 };

            // ---- colour area: SV square + hue bar (+ alpha bar)
            var area = new Grid { Height = 220, ColumnDefinitions = new ColumnDefinitions(alpha ? "*,10,24,10,24" : "*,10,24") };
            area.Children.Add(_square);
            Grid.SetColumn(_hue, 2); area.Children.Add(_hue);
            if (alpha) { Grid.SetColumn(_alphaBar, 4); area.Children.Add(_alphaBar); }
            ToolTip.SetTip(_square, "Saturation (left → right) · Brightness (bottom → top)");
            ToolTip.SetTip(_hue, "Hue");
            ToolTip.SetTip(_alphaBar, "Opacity");
            _square.Changed += (s, v) => { _s = s; _v = v; Push(fromHsv: true); };
            _hue.Changed += h => { _h = h; Push(fromHsv: true); };
            _alphaBar.Changed += a => { _a = a; Push(fromHsv: true); };
            root.Children.Add(area);

            // ---- preview + channels
            var mid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,16,*") };
            var previewCol = new StackPanel { Spacing = 4 };
            _preview.Height = alpha ? 112 : 86;
            _preview.Compare = InitialColor;
            _preview.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(_preview, "Top: new colour · Bottom: original (click to restore)");
            _preview.PointerPressed += (s, e) => { if (e.GetPosition(_preview).Y > _preview.Bounds.Height / 2) SetColor(InitialColor); };
            previewCol.Children.Add(_preview);
            previewCol.Children.Add(new TextBlock { Text = "new / original", Classes = { "small", "tertiary" }, HorizontalAlignment = HorizontalAlignment.Center });
            mid.Children.Add(previewCol);

            var channels = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,54"), RowDefinitions = new RowDefinitions(alpha ? "Auto,Auto,Auto,Auto" : "Auto,Auto,Auto") };
            string[] names = { "R", "G", "B", "A" };
            for (int i = 0; i < (alpha ? 4 : 3); i++)
            {
                int ch = i;
                var lbl = new TextBlock { Text = names[i], Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
                var sl = new Slider { Minimum = 0, Maximum = 255, SmallChange = 1, LargeChange = 16, Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                var box = new TextBox { Classes = { "number" }, MinWidth = 50, MinHeight = 22, VerticalAlignment = VerticalAlignment.Center };
                sl.ValueChanged += (s, e) => { if (!_sync) SetChannel(ch, (int)Math.Round(sl.Value)); };
                box.LostFocus += (s, e) => CommitBox(ch);
                box.KeyDown += (s, e) => { if (e.Key == Key.Return) { CommitBox(ch); e.Handled = true; } };
                Grid.SetRow(lbl, i); Grid.SetRow(sl, i); Grid.SetRow(box, i);
                Grid.SetColumn(sl, 1); Grid.SetColumn(box, 2);
                channels.Children.Add(lbl); channels.Children.Add(sl); channels.Children.Add(box);
                _sliders[i] = sl; _boxes[i] = box;
            }
            Grid.SetColumn(channels, 2);
            mid.Children.Add(channels);
            root.Children.Add(mid);

            // ---- hex + HSV readout
            if (Application.Current != null && Application.Current.TryFindResource("VxMono", out var mono) && mono is FontFamily ff) _hex.FontFamily = ff;
            var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            hexRow.Children.Add(new TextBlock { Text = "Hex", Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center });
            _hex.TextChanged += (s, e) => { if (_sync) return; string t = _hex.Text?.Trim().TrimStart('#') ?? ""; if ((t.Length == 6 || (_alpha && t.Length == 8)) && ColorMath.TryParseHex(t, out var c)) SetColor(_alpha ? c : Color.FromArgb(255, c.R, c.G, c.B), keepHexText: true); };
            _hex.LostFocus += (s, e) => Refresh();
            _hex.KeyDown += (s, e) => { if (e.Key == Key.Return) { if (ColorMath.TryParseHex(_hex.Text, out var c)) SetColor(_alpha ? c : Color.FromArgb(255, c.R, c.G, c.B)); else Refresh(); e.Handled = true; } };
            hexRow.Children.Add(_hex);
            hexRow.Children.Add(_hsvText);
            root.Children.Add(hexRow);

            // ---- recent colours
            if (_recent.Count > 0)
            {
                var recent = new WrapPanel { Orientation = Orientation.Horizontal };
                recent.Children.Add(new TextBlock { Text = "Recent", Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
                foreach (var rc in _recent)
                {
                    var c = rc;
                    var sw = new ColorSwatch { Color = c, Width = 22, Height = 22, Radius = 4, Margin = new Thickness(0, 0, 5, 0), Cursor = new Cursor(StandardCursorType.Hand) };
                    ToolTip.SetTip(sw, ColorMath.ToHex(c, _alpha));
                    sw.PointerPressed += (s, e) => SetColor(_alpha ? c : Color.FromArgb(255, c.R, c.G, c.B));
                    recent.Children.Add(sw);
                }
                root.Children.Add(recent);
            }

            // ---- buttons
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "Cancel", MinWidth = 84, IsCancel = true };
            cancel.Click += (s, e) => Cancel();
            var ok = new Button { Content = "OK", MinWidth = 84, Classes = { "accent" }, IsDefault = true };
            ok.Click += (s, e) => Accept();
            buttons.Children.Add(cancel); buttons.Children.Add(ok);
            root.Children.Add(buttons);

            Content = root;
            Refresh();
            Closed += (s, e) => { if (!_accepted) { try { _live?.Invoke(InitialColor); } catch { } } };
        }

        // ---------------------------------------------------------------- API (buttons, tests)
        public void Accept() { _accepted = true; Close(); }
        public void Cancel() { _accepted = false; Close(); }

        /// <summary>Set the colour from outside (hex field, recent swatch, tests).</summary>
        public void SetColor(Color c) => SetColor(c, false);

        private void SetColor(Color c, bool keepHexText)
        {
            ColorMath.ToHsv(c, out double h, out double s, out double v);
            // keep the hue when the colour carries none (greys) so the square does not jump back to red
            if (s > 0 && v > 0) _h = h;
            _s = s; _v = v;
            if (_alpha) _a = c.A;
            Push(fromHsv: false, keepHexText: keepHexText);
        }

        private void SetChannel(int ch, int value)
        {
            var c = SelectedColor;
            byte b = (byte)Math.Max(0, Math.Min(255, value));
            switch (ch)
            {
                case 0: c = Color.FromArgb(c.A, b, c.G, c.B); break;
                case 1: c = Color.FromArgb(c.A, c.R, b, c.B); break;
                case 2: c = Color.FromArgb(c.A, c.R, c.G, b); break;
                case 3: c = Color.FromArgb(b, c.R, c.G, c.B); break;
            }
            SetColor(c);
        }

        private void CommitBox(int ch)
        {
            if (int.TryParse(_boxes[ch].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) SetChannel(ch, v);
            else Refresh();
        }

        private void Push(bool fromHsv, bool keepHexText = false)
        {
            Refresh(keepHexText);
            try { _live?.Invoke(SelectedColor); } catch { }
        }

        private void Refresh(bool keepHexText = false)
        {
            _sync = true;
            try
            {
                var c = SelectedColor;
                _square.Hue = _h; _square.S = _s; _square.V = _v; _square.InvalidateVisual();
                _hue.Hue = _h; _hue.InvalidateVisual();
                _alphaBar.Color = c; _alphaBar.Alpha = _a; _alphaBar.InvalidateVisual();
                _preview.Color = c;
                byte[] vals = { c.R, c.G, c.B, c.A };
                for (int i = 0; i < 4; i++)
                {
                    if (_sliders[i] == null) continue;
                    _sliders[i].Value = vals[i];
                    if (!_boxes[i].IsFocused) _boxes[i].Text = vals[i].ToString(CultureInfo.InvariantCulture);
                }
                if (!keepHexText) _hex.Text = ColorMath.ToHex(c, _alpha);
                _hsvText.Text = $"H {Math.Round(_h):0}°  S {Math.Round(_s * 100):0}%  V {Math.Round(_v * 100):0}%";
            }
            finally { _sync = false; }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;
            if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        }

        // ---------------------------------------------------------------- the three colour controls
        private abstract class DragControl : Control
        {
            private bool _drag;
            protected DragControl() { ClipToBounds = false; Cursor = new Cursor(StandardCursorType.Cross); }
            protected abstract void Pick(Point p);
            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                _drag = true; e.Pointer.Capture(this); Pick(e.GetPosition(this)); e.Handled = true;
            }
            protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); if (_drag) { Pick(e.GetPosition(this)); e.Handled = true; } }
            protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); if (_drag) { _drag = false; e.Pointer.Capture(null); } }
            protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _drag = false; }
            protected static double Clamp01(double v) => Math.Max(0, Math.Min(1, v));
            protected static void Marker(DrawingContext ctx, Rect r)
            {
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 3), r, 3, 3);
                ctx.DrawRectangle(null, new Pen(Brushes.White, 1.6), r, 3, 3);
            }
        }

        private sealed class SvSquare : DragControl
        {
            public double Hue, S, V;
            public event Action<double, double> Changed;
            protected override void Pick(Point p)
            {
                if (Bounds.Width < 1 || Bounds.Height < 1) return;
                S = Clamp01(p.X / Bounds.Width); V = Clamp01(1 - p.Y / Bounds.Height);
                InvalidateVisual();
                Changed?.Invoke(S, V);
            }
            public override void Render(DrawingContext ctx)
            {
                var r = new Rect(Bounds.Size);
                if (r.Width < 1 || r.Height < 1) return;
                var horiz = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative) };
                horiz.GradientStops.Add(new GradientStop(Colors.White, 0));
                horiz.GradientStops.Add(new GradientStop(ColorMath.FromHsv(Hue, 1, 1), 1));
                var vert = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative) };
                vert.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
                vert.GradientStops.Add(new GradientStop(Colors.Black, 1));
                ctx.DrawRectangle(horiz, null, r, 6, 6);
                ctx.DrawRectangle(vert, null, r, 6, 6);
                var c = new Point(S * r.Width, (1 - V) * r.Height);
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 3.5), c, 7, 7);
                ctx.DrawEllipse(null, new Pen(Brushes.White, 2), c, 7, 7);
            }
        }

        private sealed class HueBar : DragControl
        {
            public double Hue;
            public event Action<double> Changed;
            protected override void Pick(Point p)
            {
                if (Bounds.Height < 1) return;
                Hue = Clamp01(p.Y / Bounds.Height) * 359.999;
                InvalidateVisual();
                Changed?.Invoke(Hue);
            }
            public override void Render(DrawingContext ctx)
            {
                var r = new Rect(Bounds.Size);
                if (r.Width < 1 || r.Height < 1) return;
                var g = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative) };
                for (int i = 0; i <= 6; i++) g.GradientStops.Add(new GradientStop(ColorMath.FromHsv(i * 60 % 360, 1, 1), i / 6.0));
                g.GradientStops[6] = new GradientStop(Color.FromRgb(255, 0, 0), 1);
                ctx.DrawRectangle(g, null, r, 5, 5);
                double y = Hue / 360.0 * r.Height;
                Marker(ctx, new Rect(-1, Math.Max(-1, y - 3), r.Width + 2, 6));
            }
        }

        private sealed class AlphaBar : DragControl
        {
            public Color Color = Colors.White;
            public byte Alpha = 255;
            public event Action<byte> Changed;
            protected override void Pick(Point p)
            {
                if (Bounds.Height < 1) return;
                Alpha = ColorMath.B(1 - Clamp01(p.Y / Bounds.Height));
                InvalidateVisual();
                Changed?.Invoke(Alpha);
            }
            public override void Render(DrawingContext ctx)
            {
                var r = new Rect(Bounds.Size);
                if (r.Width < 1 || r.Height < 1) return;
                using (ctx.PushClip(new RoundedRect(r, 5))) Checker.Draw(ctx, r, 6);
                var g = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative) };
                g.GradientStops.Add(new GradientStop(Color.FromArgb(255, Color.R, Color.G, Color.B), 0));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0, Color.R, Color.G, Color.B), 1));
                ctx.DrawRectangle(g, null, r, 5, 5);
                double y = (1 - Alpha / 255.0) * r.Height;
                Marker(ctx, new Rect(-1, Math.Max(-1, y - 3), r.Width + 2, 6));
            }
        }
    }
}
