using System;
using Avalonia;
using Avalonia.Controls;

namespace VortexEditor.Controls
{
    /// <summary>
    /// A toolbar of up to three groups — tools on the left, view controls in the middle, badges on the right (two children:
    /// left and right) — that never runs past its own width: one row while everything fits (the middle group centred
    /// between the others); otherwise the middle group (or, with two groups, the right one) moves to a second row, and a
    /// group that is still wider than the toolbar wraps (make it a <see cref="WrapPanel"/>) instead of reaching under the
    /// panel next to it. With <see cref="FlexibleLeftMinWidth"/> the left group fills the row (a breadcrumb, scrolling
    /// tabs) and only needs that much room to stay in one row.
    /// </summary>
    public sealed class ResponsiveToolbar : Panel
    {
        public static readonly StyledProperty<double> RowHeightProperty = AvaloniaProperty.Register<ResponsiveToolbar, double>(nameof(RowHeight), 34);
        public static readonly StyledProperty<double> GapProperty = AvaloniaProperty.Register<ResponsiveToolbar, double>(nameof(Gap), 12);
        public static readonly StyledProperty<double> FlexibleLeftMinWidthProperty = AvaloniaProperty.Register<ResponsiveToolbar, double>(nameof(FlexibleLeftMinWidth), 0);

        /// <summary>Height of one toolbar row.</summary>
        public double RowHeight { get => GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
        /// <summary>Space kept between two groups in one row.</summary>
        public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }
        /// <summary>Above 0: the left group stretches over the room the others leave and needs only this much of it.</summary>
        public double FlexibleLeftMinWidth { get => GetValue(FlexibleLeftMinWidthProperty); set => SetValue(FlexibleLeftMinWidthProperty, value); }

        /// <summary>True while the middle group sits in its own row (tests).</summary>
        public bool IsWrapped { get; private set; }

        static ResponsiveToolbar() => AffectsMeasure<ResponsiveToolbar>(RowHeightProperty, GapProperty, FlexibleLeftMinWidthProperty);

        private Control Visible(int i) => i < Children.Count && Children[i].IsVisible ? Children[i] : null;
        private Control LeftGroup => Visible(0);
        private Control MiddleGroup => Children.Count >= 3 ? Visible(1) : null;
        private Control RightGroup => Children.Count >= 3 ? Visible(2) : Visible(1);
        private bool Flexible => FlexibleLeftMinWidth > 0;

        // the arrangement chosen by the last measure
        private bool _wrapped, _rightInFirstRow;
        private double _row1, _row2;

        protected override Size MeasureOverride(Size available)
        {
            Control left = LeftGroup, middle = MiddleGroup, right = RightGroup;
            var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
            double lw = Natural(left, unbounded), mw = Natural(middle, unbounded), rw = Natural(right, unbounded);
            if (Flexible && left != null) lw = Math.Min(lw, FlexibleLeftMinWidth);
            double width = available.Width;
            double rowH = RowHeight;

            int groups = (lw > 0 ? 1 : 0) + (mw > 0 ? 1 : 0) + (rw > 0 ? 1 : 0);
            double oneRow = lw + mw + rw + Gap * Math.Max(0, groups - 1);
            if (double.IsInfinity(width) || oneRow <= width)
            {
                _wrapped = false;
                // a stretching left group gets the room the others leave
                if (Flexible && left != null && !double.IsInfinity(width))
                    left.Measure(new Size(Math.Max(0, width - mw - rw - Gap * Math.Max(0, groups - 1)), double.PositiveInfinity));
                _row1 = Math.Max(rowH, Math.Max(H(left), Math.Max(H(middle), H(right))));
                IsWrapped = false;
                return new Size(double.IsInfinity(width) ? oneRow : width, _row1);
            }

            // two rows: the tools (wrapping if they must) and, when there is room beside them, the badges; then the
            // view controls (and the badges if they did not fit in the first row)
            _wrapped = true;
            IsWrapped = true;
            if (left != null && (Flexible || lw > width)) left.Measure(new Size(width, double.PositiveInfinity));
            double leftW = Flexible ? width : left?.DesiredSize.Width ?? 0;
            // with two groups the right one takes the second row; with three, the badges stay up if they fit
            _rightInFirstRow = right == null || (middle != null && leftW + (leftW > 0 ? Gap : 0) + rw <= width);
            _row1 = Math.Max(rowH, Math.Max(H(left), _rightInFirstRow ? H(right) : 0));
            double second = 0;
            if (middle != null)
            {
                if (mw > width) middle.Measure(new Size(width, double.PositiveInfinity));
                second = Math.Max(rowH, H(middle));
            }
            if (!_rightInFirstRow && right != null) second = Math.Max(second, rowH) + Math.Max(rowH, H(right));
            if (!_rightInFirstRow && right != null && rw > width) right.Measure(new Size(width, double.PositiveInfinity));
            if (!_rightInFirstRow && right != null && middle == null) second = Math.Max(rowH, H(right));
            _row2 = second;
            return new Size(width, _row1 + _row2);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Control left = LeftGroup, middle = MiddleGroup, right = RightGroup;
            double w = finalSize.Width;
            foreach (var c in Children) if (!c.IsVisible) c.Arrange(new Rect(0, 0, 0, 0));
            if (!_wrapped)
            {
                double rowH = finalSize.Height;
                double lw = left?.DesiredSize.Width ?? 0, rw = right?.DesiredSize.Width ?? 0, mw = middle?.DesiredSize.Width ?? 0;
                if (Flexible && left != null) lw = Math.Max(0, w - rw - mw - (rw > 0 ? Gap : 0) - (mw > 0 ? Gap : 0));
                left?.Arrange(CenterV(0, lw, 0, rowH, left));
                right?.Arrange(CenterV(w - rw, rw, 0, rowH, right));
                if (middle != null)
                {
                    // centred in the space between the side groups, like the old three-column grid
                    double from = lw + (lw > 0 ? Gap : 0), to = w - rw - (rw > 0 ? Gap : 0);
                    double x = from + Math.Max(0, (to - from - mw) / 2);
                    middle.Arrange(CenterV(x, mw, 0, rowH, middle));
                }
                return finalSize;
            }

            double leftW = Flexible ? w : Math.Min(w, left?.DesiredSize.Width ?? 0);
            left?.Arrange(CenterV(0, leftW, 0, _row1, left));
            double y = _row1;
            if (right != null && _rightInFirstRow) right.Arrange(CenterV(w - right.DesiredSize.Width, right.DesiredSize.Width, 0, _row1, right));
            if (middle != null)
            {
                double mh = Math.Max(RowHeight, middle.DesiredSize.Height);
                middle.Arrange(CenterV(0, Math.Min(w, middle.DesiredSize.Width), y, mh, middle));
                y += mh;
            }
            if (right != null && !_rightInFirstRow)
            {
                // the right group's own row keeps it at the right edge, as in one row
                double rw = Math.Min(w, right.DesiredSize.Width);
                right.Arrange(CenterV(middle == null ? w - rw : 0, rw, y, Math.Max(RowHeight, right.DesiredSize.Height), right));
            }
            return finalSize;
        }

        private static double Natural(Control c, Size unbounded)
        {
            if (c == null) return 0;
            c.Measure(unbounded);
            return c.DesiredSize.Width;
        }

        private static double H(Control c) => c?.DesiredSize.Height ?? 0;

        private static Rect CenterV(double x, double width, double rowTop, double rowHeight, Control c) =>
            new Rect(x, rowTop + Math.Max(0, (rowHeight - c.DesiredSize.Height) / 2), width, Math.Min(rowHeight, c.DesiredSize.Height));
    }
}
