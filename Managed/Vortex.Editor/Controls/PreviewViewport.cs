using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Editor.Core.Services.Rendering;

namespace VortexEditor.Controls
{
    /// <summary>
    /// Interactive 3D preview for every editor window (model / material / prefab / animation / socket / collision):
    /// renders a <see cref="PreviewScene"/> offscreen through <see cref="PreviewRenderer"/> and shows it as a bitmap.
    /// Left-drag orbits, right- or middle-drag pans, the wheel zooms, a double-click resets the view. Renders only
    /// when something changed (or every tick with <see cref="Continuous"/> / <see cref="AutoRotate"/>), so an idle
    /// preview costs nothing. Content resources: assign <see cref="Model"/> to let the control own (and dispose) a
    /// <see cref="PreviewModel"/>, or set <see cref="Scene"/> for content the caller manages.
    /// </summary>
    public sealed class PreviewViewport : Border
    {
        private readonly Image _image = new Image { Stretch = Stretch.Fill };
        private readonly TextBlock _hint = new TextBlock
        {
            Text = "Drag: orbit · Right-drag: pan · Wheel: zoom · Double-click: reset",
            FontSize = 11, Opacity = 0.55, Margin = new Thickness(8, 0, 8, 6),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Foreground = Brushes.White, IsHitTestVisible = false
        };
        private readonly TextBlock _empty = new TextBlock
        {
            Text = "No preview", Opacity = 0.5, Foreground = Brushes.White, IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        private readonly DispatcherTimer _timer;
        private WriteableBitmap _bitmap;
        private PreviewScene _scene;
        private PreviewModel _model;
        private bool _dirty = true;
        private Point _last;
        private bool _orbiting, _panning;
        private PreviewCamera _camera = PreviewCamera.Default;
        private float[] _frame;   // cx, cy, cz, radius of the content (for panning in world units)

        /// <summary>Camera around the content (yaw/pitch radians, DistScale 1 = fit, Focus = pan target).</summary>
        public PreviewCamera Camera { get => _camera; set { _camera = value; Invalidate(); } }
        /// <summary>Render every tick (animation playback, live material edits).</summary>
        public bool Continuous { get; set; }
        /// <summary>Slowly spin the content (radians per second, 0 = off).</summary>
        public float AutoRotate { get; set; }
        /// <summary>Called right before each render (update bone palettes, gizmos, …).</summary>
        public Action BeforeRender { get; set; }
        /// <summary>Show the control hint line.</summary>
        public bool ShowHint { get => _hint.IsVisible; set => _hint.IsVisible = value; }
        /// <summary>The last rendered frame (for "save preview image" and tests).</summary>
        public PreviewImage LastImage { get; private set; }

        public PreviewViewport()
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1b, 0x1c, 0x21));
            ClipToBounds = true;
            Focusable = true;
            var grid = new Grid();
            grid.Children.Add(_image);
            grid.Children.Add(_empty);
            grid.Children.Add(_hint);
            Child = grid;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (s, e) => Tick(0.033f);
            AttachedToVisualTree += (s, e) => _timer.Start();
            DetachedFromVisualTree += (s, e) => { _timer.Stop(); DisposeModel(); };
            SizeChanged += (s, e) => Invalidate();
        }

        /// <summary>Content managed by the caller (the caller disposes its meshes/materials).</summary>
        public PreviewScene Scene
        {
            get => _scene;
            set { if (!ReferenceEquals(_scene, _model?.Scene)) { } _scene = value; _frame = null; Invalidate(); }
        }

        /// <summary>Content owned by this control (disposed when replaced or when the control leaves the tree).</summary>
        public PreviewModel Model
        {
            get => _model;
            set { DisposeModel(); _model = value; _scene = value?.Scene; _frame = null; Invalidate(); }
        }

        /// <summary>Load a model / prefab / material file (by extension) as owned content. Returns false when unsupported.</summary>
        public bool LoadAsset(string fullPath, string projectRoot = null)
        {
            string ext = System.IO.Path.GetExtension(fullPath ?? "").ToLowerInvariant();
            if (ext == ".ventity" || ext == ".vprefab") { Model = PreviewModel.LoadPrefab(fullPath, projectRoot); return Model != null; }
            if (ext == ".vmat")
            {
                var pm = PreviewModel.MaterialSphere(fullPath);
                Model = pm; return pm != null;
            }
            Model = PreviewModel.Load(fullPath);
            return Model != null;
        }

        /// <summary>Request a new frame.</summary>
        public void Invalidate() { _dirty = true; }

        /// <summary>Reset the camera to the default framing.</summary>
        public void ResetView() { _camera = PreviewCamera.Default; Invalidate(); }

        private void DisposeModel()
        {
            if (_model != null) { try { _model.Dispose(); } catch { } if (ReferenceEquals(_scene, _model.Scene)) _scene = null; _model = null; }
        }

        private void Tick(float dt)
        {
            if (!IsEffectivelyVisible) return;
            if (AutoRotate != 0f && !_orbiting) { _camera.Yaw += AutoRotate * dt; _dirty = true; }
            if (!_dirty && !Continuous) return;
            _dirty = false;
            RenderNow();
        }

        /// <summary>Render immediately (tests / "save image").</summary>
        public void RenderNow()
        {
            _empty.IsVisible = _scene == null || _scene.Items.Count == 0;
            if (_empty.IsVisible) { _image.Source = null; return; }
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            double w0 = Bounds.Width * scale, h0 = Bounds.Height * scale;
            double k = Math.Min(1.0, 1600.0 / Math.Max(1.0, Math.Max(w0, h0)));   // cap the long side, keep the aspect
            int w = (int)Math.Max(16, w0 * k), h = (int)Math.Max(16, h0 * k);
            try { BeforeRender?.Invoke(); } catch { }
            if (_frame == null) _frame = PreviewRenderer.ComputeFrame(_scene);
            var img = PreviewRenderer.Render(_scene, w, h, _camera);
            if (img == null) return;
            LastImage = img;
            if (_bitmap == null || _bitmap.PixelSize.Width != img.Width || _bitmap.PixelSize.Height != img.Height)
                _bitmap = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var fb = _bitmap.Lock())
            {
                for (int y = 0; y < img.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(img.Bgra, y * img.Stride, IntPtr.Add(fb.Address, y * fb.RowBytes), img.Stride);
            }
            _image.Source = null;   // force the Image to pick up the new pixels
            _image.Source = _bitmap;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var pt = e.GetCurrentPoint(this);
            if (e.ClickCount == 2 && pt.Properties.IsLeftButtonPressed) { ResetView(); e.Handled = true; return; }
            _last = pt.Position;
            _orbiting = pt.Properties.IsLeftButtonPressed;
            _panning = pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_orbiting && !_panning) return;
            var p = e.GetPosition(this);
            double dx = p.X - _last.X, dy = p.Y - _last.Y;
            _last = p;
            if (_orbiting)
            {
                _camera.Yaw -= (float)dx * 0.01f;
                _camera.Pitch = Math.Max(-1.5f, Math.Min(1.5f, _camera.Pitch + (float)dy * 0.01f));
            }
            else if (_panning && _frame != null)
            {
                // move the focus in the camera plane, scaled by the framed size and the zoom
                if (_camera.Focus == null) _camera.Focus = new[] { _frame[0], _frame[1], _frame[2] };
                float k = _frame[3] * 2.2f * Math.Max(0.05f, _camera.DistScale) / (float)Math.Max(1, Bounds.Height);
                float cy = (float)Math.Cos(_camera.Yaw), sy = (float)Math.Sin(_camera.Yaw);
                float cp = (float)Math.Cos(_camera.Pitch), sp = (float)Math.Sin(_camera.Pitch);
                // camera right = (cos yaw, 0, -sin yaw); camera up = (-sin pitch sin yaw, cos pitch, -sin pitch cos yaw)
                float rx = cy, rz = -sy;
                float ux = -sp * sy, uy = cp, uz = -sp * cy;
                _camera.Focus[0] += (float)(-dx * rx + dy * ux) * k;
                _camera.Focus[1] += (float)(dy * uy) * k;
                _camera.Focus[2] += (float)(-dx * rz + dy * uz) * k;
            }
            Invalidate();
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _orbiting = _panning = false;
            e.Pointer.Capture(null);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            float f = (float)Math.Pow(0.88, e.Delta.Y);
            _camera.DistScale = Math.Max(0.02f, Math.Min(12f, (_camera.DistScale <= 0f ? 1f : _camera.DistScale) * f));
            Invalidate();
            e.Handled = true;
        }
    }
}
