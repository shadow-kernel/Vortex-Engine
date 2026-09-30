using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VortexEditor.Shell.Material;

namespace VortexEditor.Shell.Audio
{
    /// <summary>
    /// Vertical mixer fader (linear gain 0..1, top = 1 = 0 dB): drag the cap or click the track, mouse wheel for fine
    /// steps (Shift = coarse), double-click resets to 0 dB. Drawn directly so it does not depend on the Slider template
    /// (the editor theme sizes slider tracks for horizontal sliders only).
    /// </summary>
    internal sealed class Fader : Control
    {
        private double _value = 1.0;
        private bool _drag;
        private double _grabOffset;

        public event Action<double> ValueChanged;

        public double Value
        {
            get => _value;
            set { double v = Math.Max(0, Math.Min(1, value)); if (Math.Abs(v - _value) < 1e-9) return; _value = v; InvalidateVisual(); ValueChanged?.Invoke(v); }
        }

        public Fader() { Width = 30; Cursor = new Cursor(StandardCursorType.SizeNorthSouth); Focusable = true; }

        private const double CapH = 14, Pad = 8;
        private double TrackTop => Pad;
        private double TrackBottom => Math.Max(Pad + 1, Bounds.Height - Pad);
        private double YFor(double v) => TrackBottom - v * (TrackBottom - TrackTop);
        private double ValueAt(double y) => (TrackBottom - y) / Math.Max(1, TrackBottom - TrackTop);

        public override void Render(DrawingContext ctx)
        {
            var b = Bounds;
            if (b.Width < 4 || b.Height < 4) return;
            double cx = b.Width / 2;
            // track + accent fill below the cap
            ctx.DrawRectangle(EditorKit.Brush("VxFieldBrush"), new Pen(EditorKit.Brush("VxHairlineBrush"), 1), new Rect(cx - 2.5, TrackTop, 5, TrackBottom - TrackTop), 2.5, 2.5);
            double y = YFor(_value);
            ctx.DrawRectangle(EditorKit.Brush("VxAccentBrush"), null, new Rect(cx - 2.5, y, 5, Math.Max(0, TrackBottom - y)), 2.5, 2.5);
            // unity (0 dB) is the top; mark -6 / -12 / -24 dB on the left
            foreach (double db in new[] { -6.0, -12.0, -24.0 })
            {
                double ty = YFor(Math.Pow(10, db / 20.0));
                ctx.FillRectangle(EditorKit.Brush("VxTextTertiaryBrush"), new Rect(cx - 10, Math.Round(ty), 5, 1));
            }
            // cap
            var cap = new Rect(cx - 12, y - CapH / 2, 24, CapH);
            ctx.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1), cap, 4, 4);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), new Rect(cx - 7, y - 0.5, 14, 1));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var pt = e.GetCurrentPoint(this);
            if (!pt.Properties.IsLeftButtonPressed) return;
            if (e.ClickCount >= 2) { Value = 1.0; e.Handled = true; return; }
            double y = pt.Position.Y, capY = YFor(_value);
            _grabOffset = Math.Abs(y - capY) <= CapH ? y - capY : 0;   // grabbing the cap keeps it under the pointer
            if (_grabOffset == 0) Value = ValueAt(y);
            _drag = true;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_drag) return;
            Value = ValueAt(e.GetPosition(this).Y - _grabOffset);
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_drag) { _drag = false; e.Pointer.Capture(null); }
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _drag = false; }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 0.02;
            Value += e.Delta.Y > 0 ? step : e.Delta.Y < 0 ? -step : 0;
            e.Handled = true;
        }
    }
}
