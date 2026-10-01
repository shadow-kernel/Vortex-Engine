using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VortexEditor.Shell.Audio
{
    /// <summary>
    /// Vertical bus meter: RMS fill (green → yellow → red towards full scale) drawn bottom-up with a peak-hold line,
    /// on a dark track with -6 / -12 / -24 dB ticks. Values are linear 0..1 already shaped for display (sqrt).
    /// </summary>
    internal sealed class LevelMeter : Control
    {
        private static readonly IBrush Track = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x13));
        private static readonly IBrush Tick = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        private static readonly IBrush Peak = new SolidColorBrush(Color.FromRgb(0xE7, 0xC5, 0x5C));
        private static readonly IBrush Fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x3F, 0xBF, 0x7F), 0),
                new GradientStop(Color.FromRgb(0x5F, 0xD0, 0x6F), 0.62),
                new GradientStop(Color.FromRgb(0xE7, 0xC5, 0x5C), 0.82),
                new GradientStop(Color.FromRgb(0xF0, 0x5A, 0x4A), 1)
            }
        };

        /// <summary>Displayed RMS level 0..1.</summary>
        public double Rms { get; set; }
        /// <summary>Peak-hold level 0..1 (hidden when ~0).</summary>
        public double PeakHold { get; set; }

        public LevelMeter() { Width = 10; }

        public override void Render(DrawingContext ctx)
        {
            var r = new Rect(Bounds.Size);
            if (r.Width < 1 || r.Height < 1) return;
            ctx.DrawRectangle(Track, null, r, 2, 2);
            double h = Math.Max(0, Math.Min(1, Rms)) * r.Height;
            if (h > 0.5)
                using (ctx.PushClip(new Rect(0, r.Height - h, r.Width, h))) ctx.DrawRectangle(Fill, null, r, 2, 2);
            foreach (double db in new[] { -6.0, -12.0, -24.0 })
            {
                double y = r.Height - Math.Sqrt(Math.Pow(10, db / 20.0)) * r.Height;
                ctx.FillRectangle(Tick, new Rect(0, Math.Round(y), r.Width, 1));
            }
            double p = Math.Max(0, Math.Min(1, PeakHold));
            if (p > 0.006)
            {
                double y = Math.Max(0, r.Height - p * r.Height - 1);
                ctx.FillRectangle(Peak, new Rect(0, y, r.Width, 2));
            }
        }
    }
}
