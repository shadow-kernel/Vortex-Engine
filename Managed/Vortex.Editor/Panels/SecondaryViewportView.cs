using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Panels
{
    /// <summary>
    /// A secondary camera view (split / quad layouts, WPF GamePreviewView secondary panes): renders through an engine
    /// secondary render target, reads the pixels back and shows them in an Image. Throttled to ~10 fps and only when a
    /// camera moved. Pick the editor camera or any scene camera; the info line shows the camera, FOV and position.
    /// </summary>
    public sealed class SecondaryViewportView : Border
    {
        private sealed class Choice { public string Label; public GameEntity Entity; public bool EditorCam; public override string ToString() => Label; }

        private readonly int _index;
        private readonly Image _image = new Image { Stretch = Stretch.UniformToFill };
        private readonly ComboBox _combo = new ComboBox { MinWidth = 150 };
        private readonly TextBlock _label = new TextBlock { Classes = { "small", "secondary" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _info = new TextBlock { Classes = { "small" }, Foreground = Brushes.White, Margin = new Thickness(8, 0, 0, 6), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        private readonly SecondaryViewSession _session = new SecondaryViewSession();
        private WriteableBitmap _bitmap;
        private DispatcherTimer _timer;
        private bool _syncing;
        private int _lastCameraCount = -1;
        private int _pollTicks;

        public int FramesRendered { get; private set; }

        public SecondaryViewportView(int index)
        {
            _index = index;
            Background = (IBrush)Application.Current.FindResource("VxViewportBgBrush");
            var bar = new DockPanel { Height = 28, Margin = new Thickness(6, 4) };
            _combo.SelectionChanged += (s, e) => { if (!_syncing && _combo.SelectedItem is Choice c) Apply(c); };
            ToolTip.SetTip(_combo, "Camera for this view");
            var close = new Button { Classes = { "icon", "small" }, Content = new Controls.VxIcon { Icon = "Close" } };
            ToolTip.SetTip(close, "Close the split view");
            close.Click += (s, e) => (this.FindAncestorOfType<ViewportPanel>())?.SetLayout(1);
            DockPanel.SetDock(close, Dock.Right);
            bar.Children.Add(close);
            bar.Children.Add(_combo);
            bar.Children.Add(_label);
            var grid = new Grid();
            grid.Children.Add(_image);
            var barHost = new Border { Child = bar, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Background = (IBrush)Application.Current.FindResource("VxToolbarBrush"), Opacity = 0.92 };
            grid.Children.Add(barHost);
            grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2), Margin = new Thickness(6), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Child = _info });
            Child = grid;
            Fill();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
            SizeChanged += (s, e) => _session.Resize((int)Math.Max(16, Bounds.Width), (int)Math.Max(16, Bounds.Height));
        }

        private void Fill()
        {
            _syncing = true;
            var previous = (_combo.SelectedItem as Choice)?.Entity;
            bool previousEditor = (_combo.SelectedItem as Choice)?.EditorCam ?? false;
            var items = new List<Choice> { new Choice { Label = "Editor Camera", EditorCam = true } };
            var scene = ProjectData.Current?.ActiveScene;
            if (scene?.Entities != null) foreach (var e in scene.Entities) Add(e, items);
            _combo.ItemsSource = items;
            _lastCameraCount = items.Count - 1;
            var keep = previous != null ? items.FirstOrDefault(c => ReferenceEquals(c.Entity, previous)) : previousEditor ? items[0] : null;
            // default: pane 1 = the first scene camera, pane 2 = the editor camera, pane 3 = the next camera (varied views)
            if (keep != null) _combo.SelectedItem = keep; else _combo.SelectedIndex = (1 + _index) % items.Count;
            _syncing = false;
            if (_combo.SelectedItem is Choice c) Apply(c);
        }

        private static void Add(GameEntity e, List<Choice> items)
        {
            var cam = e.GetComponent<Camera>();
            if (cam != null) items.Add(new Choice { Label = (cam.IsMainCamera ? "★ " : "") + e.Name, Entity = e });
            if (e.Children != null) foreach (var c in e.Children) Add(c, items);
        }

        private static int CountCameras()
        {
            var scene = ProjectData.Current?.ActiveScene; int n = 0;
            if (scene?.Entities != null) foreach (var e in scene.Entities) n += Count(e);
            return n;
        }
        private static int Count(GameEntity e) { int n = e.GetComponent<Camera>() != null ? 1 : 0; if (e.Children != null) foreach (var c in e.Children) n += Count(c); return n; }

        private void Apply(Choice c)
        {
            _session.SetCamera(c.EditorCam ? null : c.Entity, c.EditorCam);
            _label.Text = c.EditorCam ? "Perspective · free camera" : (c.Entity?.GetComponent<Camera>()?.Projection == CameraProjection.Orthographic ? "Orthographic · scene camera" : "Perspective · scene camera");
            UpdateInfo();
        }

        private void UpdateInfo()
        {
            if (!(_combo.SelectedItem is Choice c)) { _info.Text = ""; return; }
            if (c.EditorCam)
            {
                var ec = EditorCameraController.Instance;
                _info.Text = "Editor camera  ·  " + Pos(ec.PositionX, ec.PositionY, ec.PositionZ);
                return;
            }
            var cam = c.Entity?.GetComponent<Camera>(); var t = c.Entity?.Transform;
            if (cam == null || t == null) { _info.Text = "Select a camera"; return; }
            var p = t.LocalPosition;
            _info.Text = c.Entity.Name + "  ·  FOV " + cam.FieldOfView.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "°  ·  " + Pos(p.X, p.Y, p.Z);
        }

        private static string Pos(float x, float y, float z) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.#}, {1:0.#}, {2:0.#})", x, y, z);

        private void Tick()
        {
            if (++_pollTicks >= 8) { _pollTicks = 0; if (CountCameras() != _lastCameraCount) Fill(); UpdateInfo(); }
            if (Bounds.Width < 16 || Bounds.Height < 16) return;
            int w = (int)Bounds.Width, h = (int)Bounds.Height;
            if (!_session.RenderIfNeeded(w, h, out var pixels, out int pw, out int ph, out int pitch)) return;
            if (_bitmap == null || _bitmap.PixelSize.Width != pw || _bitmap.PixelSize.Height != ph)
            {
                _bitmap = new WriteableBitmap(new PixelSize(pw, ph), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                _image.Source = _bitmap;
            }
            using (var fb = _bitmap.Lock())
            {
                unsafe
                {
                    fixed (byte* src = pixels)
                    {
                        byte* dst = (byte*)fb.Address;
                        int rowBytes = Math.Min(pitch, fb.RowBytes);
                        for (int y = 0; y < ph; y++) Buffer.MemoryCopy(src + y * pitch, dst + y * fb.RowBytes, fb.RowBytes, rowBytes);
                    }
                }
            }
            _image.InvalidateVisual();
            FramesRendered++;
        }

        public void Shutdown() { _timer?.Stop(); _session.Shutdown(); }
    }
}
