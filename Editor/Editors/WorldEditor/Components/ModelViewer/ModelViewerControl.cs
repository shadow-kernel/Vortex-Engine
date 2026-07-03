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
        private long[] _meshIds;
        private long[] _matIds;
        private bool _ownsMeshes;        // true when _meshIds are generated primitives this control created + must free
        private long _ownedMaterial = -1; // a throwaway engine material to free on Dispose (-1 = none)
        private Image _image;
        private TextBlock _emptyHint;

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

            BuildUi(modelName);
        }

        /// <summary>Preview ctor for caller-provided engine meshes (e.g. a generated primitive) rendered with the
        /// same orbit / zoom / keyboard UI as an imported model. When <paramref name="ownsMeshes"/> is true the
        /// meshes — and <paramref name="ownedMaterial"/> if &gt;= 0 — are freed on Dispose (use for throwaway
        /// primitives created just for this preview so they don't leak).</summary>
        public ModelViewerControl(long[] meshIds, long[] matIds, string modelName, bool ownsMeshes, long ownedMaterial = -1)
        {
            ModelName = modelName;
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 26));
            Focusable = true;

            _meshIds = (meshIds != null && meshIds.Length > 0) ? meshIds : null;
            _matIds = matIds;
            _ownsMeshes = ownsMeshes;
            _ownedMaterial = ownedMaterial;

            BuildUi(modelName);
        }

        private void BuildUi(string modelName)
        {
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

        public void Dispose()
        {
            // Imported-model meshes are engine-cached + shared (nothing to free). Generated PRIMITIVE meshes are
            // owned copies created just for this preview — free them (and the throwaway material) so opening the
            // prefab preview repeatedly doesn't leak a mesh/material per open.
            if (_ownsMeshes && _meshIds != null)
                foreach (var m in _meshIds) { try { if (m >= 0) VortexAPI.DeleteMesh(m); } catch { } }
            if (_ownedMaterial >= 0) { try { VortexAPI.DeleteMaterial(_ownedMaterial); } catch { } }
            _meshIds = null; _matIds = null; _ownsMeshes = false; _ownedMaterial = -1;
        }
    }
}
