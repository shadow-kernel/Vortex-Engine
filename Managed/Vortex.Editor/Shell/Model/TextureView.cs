using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace VortexEditor.Shell.ModelTools
{
    /// <summary>
    /// Texture display surface: the image over a checkerboard (so alpha is visible), one channel at a time as greyscale,
    /// zoom around the cursor (wheel), pan (drag), fit (double-click), crisp texels when magnified.
    /// </summary>
    public sealed class TextureView : Control
    {
        private TextureImage _image;
        private ChannelView _channel = ChannelView.RGB;
        private readonly Dictionary<ChannelView, WriteableBitmap> _bitmaps = new Dictionary<ChannelView, WriteableBitmap>();
        private double _zoom = 1;
        private Vector _offset;          // image top-left in control coordinates
        private bool _fit = true;
        private bool _panning;
        private Point _last;
        private static IBrush _checker;

        /// <summary>Raised when zoom / fit changes (to update a zoom label).</summary>
        public event Action ViewChanged;

        public TextureView()
        {
            ClipToBounds = true;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        public TextureImage Image
        {
            get => _image;
            set { _image = value; foreach (var b in _bitmaps.Values) b?.Dispose(); _bitmaps.Clear(); _fit = true; InvalidateVisual(); ViewChanged?.Invoke(); }
        }

        public ChannelView Channel
        {
            get => _channel;
            set { if (_channel == value) return; _channel = value; InvalidateVisual(); }
        }

        /// <summary>Current magnification (1 = one texel per device-independent pixel).</summary>
        public double Zoom => _fit ? FitZoom() : _zoom;
        public bool IsFit => _fit;
        public string ZoomText => (Zoom * 100).ToString(Zoom < 0.1 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%";

        /// <summary>The bitmap currently shown (tests).</summary>
        public WriteableBitmap CurrentBitmap => Bitmap(_channel);

        public void ZoomIn() => ZoomAround(Zoom * 1.25, Center());
        public void ZoomOut() => ZoomAround(Zoom / 1.25, Center());
        public void Fit() { _fit = true; InvalidateVisual(); ViewChanged?.Invoke(); }
        public void ActualSize() => ZoomAround(1.0, Center());

        private Point Center() => new Point(Bounds.Width / 2, Bounds.Height / 2);

        private WriteableBitmap Bitmap(ChannelView v)
        {
            if (_image == null || !_image.CanPreview) return null;
            if (!_bitmaps.TryGetValue(v, out var b)) { b = _image.ToBitmap(v); _bitmaps[v] = b; }
            return b;
        }

        private double FitZoom()
        {
            if (_image == null || _image.Width <= 0 || _image.Height <= 0) return 1;
            double margin = 24;
            double zx = Math.Max(1, Bounds.Width - margin * 2) / _image.Width, zy = Math.Max(1, Bounds.Height - margin * 2) / _image.Height;
            return Math.Min(zx, zy);
        }

        private Rect ImageRect()
        {
            if (_image == null) return default;
            double z = Zoom;
            double w = _image.Width * z, h = _image.Height * z;
            if (_fit) return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
            return new Rect(_offset.X, _offset.Y, w, h);
        }

        private void ZoomAround(double newZoom, Point anchor)
        {
            if (_image == null) return;
            newZoom = Math.Max(0.02, Math.Min(64, newZoom));
            var r = ImageRect();
            double oldZoom = Zoom;
            // keep the texel under the anchor fixed
            double u = (anchor.X - r.X) / oldZoom, v = (anchor.Y - r.Y) / oldZoom;
            _fit = false;
            _zoom = newZoom;
            _offset = new Vector(anchor.X - u * newZoom, anchor.Y - v * newZoom);
            InvalidateVisual();
            ViewChanged?.Invoke();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16)), new Rect(Bounds.Size));
            var bmp = Bitmap(_channel);
            if (bmp == null) return;
            var r = ImageRect();
            if (_channel == ChannelView.RGB && _image.HasAlpha) context.FillRectangle(Checker(), r);
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Zoom >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality }))
                context.DrawImage(bmp, new Rect(0, 0, _image.Width, _image.Height), r);
        }

        private static IBrush Checker()
        {
            if (_checker != null) return _checker;
            // 16x16 tile, 8 px cells (the WPF texture editor's checkerboard)
            var wb = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var fb = wb.Lock())
            {
                var row = new byte[16 * 4];
                for (int y = 0; y < 16; y++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        byte c = ((x < 8) == (y < 8)) ? (byte)0x3A : (byte)0x2A;
                        row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = c; row[x * 4 + 3] = 255;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, IntPtr.Add(fb.Address, y * fb.RowBytes), row.Length);
                }
            }
            _checker = new ImageBrush(wb) { TileMode = TileMode.Tile, DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute), Stretch = Stretch.None };
            return _checker;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (_image == null) return;
            ZoomAround(Zoom * Math.Pow(1.15, e.Delta.Y), e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            if (e.ClickCount == 2) { Fit(); e.Handled = true; return; }
            if (_image == null) return;
            if (_fit) { var r = ImageRect(); _zoom = Zoom; _offset = new Vector(r.X, r.Y); _fit = false; }
            _panning = true; _last = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_panning) return;
            var p = e.GetPosition(this);
            _offset += p - _last;
            _last = p;
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_panning) { _panning = false; e.Pointer.Capture(null); ViewChanged?.Invoke(); }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.Key)
            {
                case Key.OemPlus: case Key.Add: ZoomIn(); e.Handled = true; break;
                case Key.OemMinus: case Key.Subtract: ZoomOut(); e.Handled = true; break;
                case Key.D0: case Key.NumPad0: Fit(); e.Handled = true; break;
                case Key.D1: case Key.NumPad1: ActualSize(); e.Handled = true; break;
            }
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (_fit) InvalidateVisual();
        }
    }

    /// <summary>
    /// <see cref="TextureView"/> with the Windows texture editor's overlays: channel buttons (RGB / R / G / B / A) bottom-left,
    /// zoom controls (− / % / + / Fit / 1:1) bottom-right, and a message when the file has no preview.
    /// </summary>
    public sealed class TextureViewer : Border
    {
        public readonly TextureView View = new TextureView();
        private readonly TextBlock _zoomText = new TextBlock { MinWidth = 44, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White };
        private readonly TextBlock _message = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(200, 140, 140)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
        private readonly Dictionary<ChannelView, RadioButton> _channels = new Dictionary<ChannelView, RadioButton>();
        private readonly Border _channelBar;

        /// <summary>Raised when the channel view changes.</summary>
        public event Action<ChannelView> ChannelChanged;

        public TextureViewer()
        {
            ClipToBounds = true;
            var grid = new Grid();
            grid.Children.Add(View);
            grid.Children.Add(_message);

            var seg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
            foreach (var (v, label, color) in new[] { (ChannelView.RGB, "RGB", Colors.White), (ChannelView.R, "R", Color.FromRgb(255, 95, 95)), (ChannelView.G, "G", Color.FromRgb(95, 220, 95)), (ChannelView.B, "B", Color.FromRgb(95, 150, 255)), (ChannelView.A, "A", Color.FromRgb(210, 210, 215)) })
            {
                var rb = new RadioButton { Content = new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(color) }, GroupName = "chan" + GetHashCode(), IsChecked = v == ChannelView.RGB, MinWidth = 30 };
                ToolTip.SetTip(rb, v == ChannelView.RGB ? "Colour (alpha shown over the checkerboard)" : "Show the " + label + " channel as greyscale");
                var cv = v;
                rb.IsCheckedChanged += (s, e) => { if (rb.IsChecked == true) SetChannel(cv); };
                _channels[v] = rb;
                seg.Children.Add(rb);
            }
            _channelBar = new Border { Classes = { "segmented" }, Child = seg, Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x12)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10) };
            grid.Children.Add(_channelBar);

            var zoom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            Button B(string text, string tip, Action a) { var b = new Button { Content = text, Classes = { "ghost" }, MinWidth = 28, Foreground = Brushes.White }; ToolTip.SetTip(b, tip); b.Click += (s, e) => a(); return b; }
            zoom.Children.Add(B("−", "Zoom out (−)", View.ZoomOut));
            zoom.Children.Add(_zoomText);
            zoom.Children.Add(B("+", "Zoom in (+)", View.ZoomIn));
            zoom.Children.Add(B("Fit", "Fit to view (0 / double-click)", View.Fit));
            zoom.Children.Add(B("1:1", "Actual size (1)", View.ActualSize));
            grid.Children.Add(new Border { Child = zoom, CornerRadius = new CornerRadius(7), Padding = new Thickness(4, 2), Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x12)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10) });

            View.ViewChanged += () => _zoomText.Text = View.ZoomText;
            View.SizeChanged += (s, e) => { if (View.IsFit) _zoomText.Text = View.ZoomText; };
            _zoomText.Text = "100%";
            Child = grid;
        }

        public TextureImage Image
        {
            get => View.Image;
            set
            {
                View.Image = value;
                _message.Text = value == null ? "Loading…" : value.CanPreview ? "" : (value.Error ?? "No preview");
                _message.IsVisible = value == null || !value.CanPreview;
            }
        }

        public ChannelView Channel => View.Channel;

        public void SetChannel(ChannelView v)
        {
            View.Channel = v;
            if (_channels.TryGetValue(v, out var rb) && rb.IsChecked != true) rb.IsChecked = true;
            ChannelChanged?.Invoke(v);
        }

        public void ShowLoading() { View.Image = null; _message.Text = "Loading…"; _message.IsVisible = true; }
    }
}
