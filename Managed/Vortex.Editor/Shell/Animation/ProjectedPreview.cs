using System;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Services.Rendering;
using VortexEditor.Controls;
using AvPoint = Avalonia.Point;

namespace VortexEditor.Shell.Animation
{
    /// <summary>A transparent layer drawn over the 3D preview (bones, grids, markers) through a callback.</summary>
    public sealed class OverlayLayer : Control
    {
        public Action<DrawingContext> Draw;
        public OverlayLayer() { IsHitTestVisible = false; ClipToBounds = true; }
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            try { Draw?.Invoke(context); } catch { }
        }
    }

    /// <summary>
    /// The shared 3D preview host of the animation / socket / collision editors: the foundation
    /// <see cref="PreviewViewport"/> (renders the content, left-drag orbits, wheel zooms, double-click resets) plus an
    /// <see cref="OverlayLayer"/> that projects managed graphics with the renderer's exact camera
    /// (<see cref="PreviewProjection"/>), consistent panning (right / middle / Shift+left drag — the content tracks the
    /// mouse), focus helpers and hook points for subclasses (joint picking). Content is caller-owned
    /// (<see cref="PreviewViewport.Scene"/>).
    /// </summary>
    public class ProjectedPreview : Grid
    {
        /// <summary>The foundation preview control.</summary>
        public PreviewViewport Viewport { get; }
        protected readonly OverlayLayer Overlay = new OverlayLayer();
        private readonly TextBlock _hint = new TextBlock
        {
            Opacity = 0.6, Foreground = Brushes.White, IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20)
        };
        private readonly TextBlock _help = new TextBlock
        {
            FontSize = 11, Opacity = 0.55, Foreground = Brushes.White, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 10, 7)
        };

        private bool _panning;
        private AvPoint _panLast;

        /// <summary>Raised right before every render (advance playback, update palettes).</summary>
        public event Action BeforeFrame;
        /// <summary>Renders so far (tests).</summary>
        public int RenderCount { get; private set; }
        /// <summary>Camera the content is framed with on bind / Shift+F.</summary>
        public PreviewCamera DefaultCamera { get; set; } = new PreviewCamera { Yaw = 0.9f, Pitch = 0.35f, DistScale = 1.15f, FovDeg = 35f };

        public ProjectedPreview()
        {
            ClipToBounds = true;
            Focusable = true;
            Background = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x10, 0x10, 0x13));
            Viewport = new PreviewViewport { ShowHint = false };
            Viewport.BeforeRender = () =>
            {
                RenderCount++;
                try { BeforeFrame?.Invoke(); } catch { }
                try { OnBeforeFrame(); } catch { }
                Overlay.InvalidateVisual();
            };
            Viewport.Camera = DefaultCamera;
            Overlay.Draw = ctx => { var p = Projection; if (p.Valid) DrawOverlay(ctx, p); };
            Children.Add(Viewport);
            Children.Add(Overlay);
            Children.Add(_hint);
            Children.Add(_help);
            AddHandler(PointerPressedEvent, OnTunnelPressed, RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, OnTunnelMoved, RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, OnTunnelReleased, RoutingStrategies.Tunnel);
            SizeChanged += (s, e) => Overlay.InvalidateVisual();
        }

        /// <summary>Centered hint over the preview (empty = none).</summary>
        public void SetHint(string text) { _hint.Text = text ?? ""; _hint.IsVisible = !string.IsNullOrEmpty(text); }
        /// <summary>The control help line at the bottom-left.</summary>
        public string HelpText { get => _help.Text; set => _help.Text = value; }
        public bool ShowHelp { get => _help.IsVisible; set => _help.IsVisible = value; }

        /// <summary>Request a new frame (and an overlay redraw).</summary>
        public void Invalidate() { Viewport.Invalidate(); Overlay.InvalidateVisual(); }

        /// <summary>The camera of the frame on screen, mirrored for managed projection.</summary>
        public PreviewProjection Projection
        {
            get
            {
                var img = Viewport.LastImage;
                return PreviewProjection.Compute(Viewport.Scene, Viewport.Camera, Bounds.Width, Bounds.Height, img?.Width ?? 0, img?.Height ?? 0);
            }
        }

        /// <summary>Called right before every render (after <see cref="BeforeFrame"/>).</summary>
        protected virtual void OnBeforeFrame() { }
        /// <summary>Draw managed graphics over the rendered frame.</summary>
        protected virtual void DrawOverlay(DrawingContext ctx, PreviewProjection proj) { }
        /// <summary>First chance at a press (joint picking). Return true to take the gesture (the pointer is captured).</summary>
        protected virtual bool OnPress(PointerPressedEventArgs e, AvPoint p) => false;
        /// <summary>Drag of a gesture taken in <see cref="OnPress"/>.</summary>
        protected virtual void OnDrag(PointerEventArgs e, AvPoint p) { }
        /// <summary>End of a gesture taken in <see cref="OnPress"/>.</summary>
        protected virtual void OnRelease(PointerReleasedEventArgs e, AvPoint p) { }
        /// <summary>Pointer moving with no gesture active (hover feedback).</summary>
        protected virtual void OnHover(PointerEventArgs e, AvPoint p) { }
        private bool _gesture;

        private void OnTunnelPressed(object sender, PointerPressedEventArgs e)
        {
            var pt = e.GetCurrentPoint(this);
            var p = pt.Position;
            Focus();
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed || (pt.Properties.IsLeftButtonPressed && shift))
            {
                _panning = true; _panLast = p;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
            if (pt.Properties.IsLeftButtonPressed && OnPress(e, p))
            {
                _gesture = true;
                e.Pointer.Capture(this);
                e.Handled = true;
            }
        }

        private void OnTunnelMoved(object sender, PointerEventArgs e)
        {
            var p = e.GetPosition(this);
            if (_panning)
            {
                PanBy(p.X - _panLast.X, p.Y - _panLast.Y);
                _panLast = p;
                e.Handled = true;
                return;
            }
            if (_gesture) { OnDrag(e, p); e.Handled = true; return; }
            var props = e.GetCurrentPoint(this).Properties;
            if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed && !props.IsMiddleButtonPressed) OnHover(e, p);
        }

        private void OnTunnelReleased(object sender, PointerReleasedEventArgs e)
        {
            if (_panning) { _panning = false; e.Pointer.Capture(null); e.Handled = true; return; }
            if (_gesture) { _gesture = false; OnRelease(e, e.GetPosition(this)); e.Pointer.Capture(null); e.Handled = true; }
        }

        /// <summary>Abort a gesture taken in <see cref="OnPress"/> (Esc).</summary>
        protected void EndGesture() { _gesture = false; }
        public bool IsGestureActive => _gesture;

        /// <summary>Pan the orbit centre so the content follows the mouse 1:1 (dx/dy in control pixels).</summary>
        public void PanBy(double dx, double dy)
        {
            var proj = Projection;
            if (!proj.Valid) return;
            float k = proj.WorldPerPixel;
            var c = proj.Center - proj.Right * (float)dx * k + proj.Up * (float)dy * k;
            var cam = Viewport.Camera;
            cam.Focus = new[] { c.X, c.Y, c.Z };
            Viewport.Camera = cam;
            Overlay.InvalidateVisual();
        }

        /// <summary>Orbit around a world point and move in (never zooms out).</summary>
        public void FocusOn(Vector3 p, float maxDistScale = 0.35f)
        {
            var cam = Viewport.Camera;
            cam.Focus = new[] { p.X, p.Y, p.Z };
            cam.DistScale = Math.Min(cam.DistScale <= 0f ? 1f : cam.DistScale, maxDistScale);
            Viewport.Camera = cam;
            Overlay.InvalidateVisual();
        }

        /// <summary>Back to the default framing (Shift+F).</summary>
        public void ResetFocus()
        {
            var cam = Viewport.Camera;
            cam.Focus = null;
            cam.DistScale = 1f;
            Viewport.Camera = cam;
            Overlay.InvalidateVisual();
        }

        /// <summary>Default camera (yaw/pitch/zoom) and no focus — a freshly bound model.</summary>
        public void ResetCamera() { Viewport.Camera = DefaultCamera; Overlay.InvalidateVisual(); }

        // ---------------------------------------------------------------- overlay drawing helpers
        protected static readonly IBrush JointBrush = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0xB0, 0x7C, 0xE0, 0xA3));
        protected static readonly IBrush AccentBrush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x2F, 0x97, 0xFF));
        protected static readonly Pen JointPen = new Pen(JointBrush, 1.2);
        protected static readonly Pen AccentPen = new Pen(AccentBrush, 2.0);

        protected static void Diamond(DrawingContext ctx, AvPoint c, double size, IBrush fill)
        {
            double h = size * 0.5 * 1.25;
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(new AvPoint(c.X, c.Y - h), true);
                s.LineTo(new AvPoint(c.X + h, c.Y));
                s.LineTo(new AvPoint(c.X, c.Y + h));
                s.LineTo(new AvPoint(c.X - h, c.Y));
                s.EndFigure(true);
            }
            ctx.DrawGeometry(fill, null, g);
        }

        protected static void Segment(DrawingContext ctx, PreviewProjection proj, Vector3 a, Vector3 b, IPen pen)
        {
            if (proj.Project(a, out var pa) && proj.Project(b, out var pb)) ctx.DrawLine(pen, pa, pb);
        }

        /// <summary>A faint reference grid on the plane y = <paramref name="y"/> around (cx, cz): 40 cells, every 5th brighter.</summary>
        protected static void Grid(DrawingContext ctx, PreviewProjection proj, float cx, float y, float cz, float extent)
        {
            var faint = new Pen(new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x30, 0xB0, 0xB0, 0xB8)), 0.7);
            var major = new Pen(new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x55, 0xB8, 0xB8, 0xC2)), 1.1);
            float spacing = extent / 20f;
            int n = 20;
            for (int i = -n; i <= n; i++)
            {
                float t = i * spacing;
                var pen = i % 5 == 0 ? major : faint;
                Segment(ctx, proj, new Vector3(cx + t, y, cz - extent), new Vector3(cx + t, y, cz + extent), pen);
                Segment(ctx, proj, new Vector3(cx - extent, y, cz + t), new Vector3(cx + extent, y, cz + t), pen);
            }
        }
    }
}
