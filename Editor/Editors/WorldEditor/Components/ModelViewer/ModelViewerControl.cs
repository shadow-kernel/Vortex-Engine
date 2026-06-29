using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core.Services.Rendering;
using Editor.DllWrapper;

namespace Editor.Editors.WorldEditor.Components.ModelViewer
{
    /// <summary>
    /// A dedicated, full-size model inspector hosted as its OWN viewport tab (opened via Ctrl+double-click in the
    /// asset browser). It renders ONLY the chosen model, isolated, large, with an orbit/zoom/keyboard-navigation
    /// camera — independent of the scene. Rendering is on-demand (on interaction) through the same offscreen
    /// preview pipeline the model editor uses, so it never fights the main Scene viewport for the render queue
    /// (each render is transactional and ends by asking the Scene viewport to re-submit itself).
    /// </summary>
    public class ModelViewerControl : UserControl, IDisposable
    {
        private readonly long[] _meshIds;
        private readonly long[] _matIds;
        private readonly Image _image;
        private readonly TextBlock _emptyHint;

        private float _yaw = 0.7f;
        private float _pitch = 0.35f;
        private float _dist = 1f;        // distance scale (1 = fit)
        private bool _dragging;
        private Point _lastMouse;
        private DateTime _lastRender = DateTime.MinValue;
        private bool _ready;

        public string ModelName { get; }

        public ModelViewerControl(string fullModelPath, string modelName)
        {
            ModelName = modelName;
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 26));
            Focusable = true;

            // Import the model's submeshes once; the engine caches the meshes (no per-frame re-import).
            try
            {
                var subs = VortexAPI.ImportModelWithMaterialsFromFile(fullModelPath);
                if (subs != null && subs.Length > 0)
                {
                    _meshIds = new long[subs.Length];
                    _matIds = new long[subs.Length];
                    for (int i = 0; i < subs.Length; i++) { _meshIds[i] = subs[i].MeshId; _matIds[i] = subs[i].MaterialId; }
                }
            }
            catch { }

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Toolbar
            var bar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 35)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(48, 48, 52)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 6, 10, 6)
            };
            var barStack = new StackPanel { Orientation = Orientation.Horizontal };
            barStack.Children.Add(new TextBlock
            {
                Text = modelName,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            barStack.Children.Add(new TextBlock
            {
                Text = "   ·   Drag: orbit   ·   Wheel: zoom   ·   WASD/QE: navigate",
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 145)),
                VerticalAlignment = VerticalAlignment.Center
            });
            var resetBtn = new Button
            {
                Content = "Reset View",
                Margin = new Thickness(14, 0, 0, 0),
                Padding = new Thickness(10, 2, 10, 2),
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 64)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            resetBtn.Click += (s, e) => { _yaw = 0.7f; _pitch = 0.35f; _dist = 1f; Render(); Focus(); };
            barStack.Children.Add(resetBtn);
            bar.Child = barStack;
            Grid.SetRow(bar, 0);
            root.Children.Add(bar);

            // Render surface
            _image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            Grid.SetRow(_image, 1);
            root.Children.Add(_image);

            _emptyHint = new TextBlock
            {
                Text = "Could not load this model.",
                Foreground = new SolidColorBrush(Color.FromRgb(180, 120, 120)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = (_meshIds == null) ? Visibility.Visible : Visibility.Collapsed
            };
            Grid.SetRow(_emptyHint, 1);
            root.Children.Add(_emptyHint);

            Content = root;

            // Input
            MouseLeftButtonDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseUp;
            MouseWheel += OnWheel;
            KeyDown += OnKeyDown;
            SizeChanged += (s, e) => { if (_ready) Render(); };
            Loaded += (s, e) => { _ready = true; Render(); Focus(); };
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true; _lastMouse = e.GetPosition(this); CaptureMouse(); Focus();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetPosition(this);
            _yaw += (float)(p.X - _lastMouse.X) * 0.01f;
            _pitch += (float)(p.Y - _lastMouse.Y) * 0.01f;
            _pitch = Math.Max(-1.5f, Math.Min(1.5f, _pitch));
            _lastMouse = p;
            Render();
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e) { _dragging = false; ReleaseMouseCapture(); }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            _dist *= e.Delta > 0 ? 0.9f : 1.1f;
            _dist = Math.Max(0.2f, Math.Min(5f, _dist));
            Render();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.W: _dist = Math.Max(0.2f, _dist * 0.9f); break;
                case Key.S: _dist = Math.Min(5f, _dist * 1.1f); break;
                case Key.A: _yaw -= 0.12f; break;
                case Key.D: _yaw += 0.12f; break;
                case Key.Q: _pitch = Math.Max(-1.5f, _pitch - 0.12f); break;
                case Key.E: _pitch = Math.Min(1.5f, _pitch + 0.12f); break;
                default: return;
            }
            e.Handled = true;
            Render();
        }

        private void Render()
        {
            if (_meshIds == null) return;
            // Throttle to ~30 FPS during drags.
            var now = DateTime.UtcNow;
            if ((now - _lastRender).TotalMilliseconds < 33 && _dragging) return;
            _lastRender = now;

            int size = (int)Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) size = 768;
            size = Math.Max(256, Math.Min(1024, size));
            try { _image.Source = AssetPreviewRenderer.RenderMeshes(_meshIds, _matIds, size, _yaw, _pitch, _dist); }
            catch { }
        }

        public void Dispose() { /* meshes are engine-cached + shared; nothing per-instance to free */ }
    }
}
