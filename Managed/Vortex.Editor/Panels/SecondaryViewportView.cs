using System;
using System.Collections.Generic;
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
    /// A secondary camera view (split / quad layouts): renders through an engine secondary render target,
    /// reads the pixels back and shows them in an Image. Throttled to ~10 fps, and only when a camera moved.
    /// </summary>
    public sealed class SecondaryViewportView : Border
    {
        private sealed class Choice { public string Label; public GameEntity Entity; public bool EditorCam; public override string ToString() => Label; }

        private readonly int _index;
        private readonly Image _image = new Image { Stretch = Stretch.UniformToFill };
        private readonly ComboBox _combo = new ComboBox { MinWidth = 140 };
        private readonly TextBlock _label = new TextBlock { Classes = { "small", "secondary" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        private readonly SecondaryViewSession _session = new SecondaryViewSession();
        private WriteableBitmap _bitmap;
        private DispatcherTimer _timer;
        private bool _syncing;

        public SecondaryViewportView(int index)
        {
            _index = index;
            Background = (IBrush)Application.Current.FindResource("VxViewportBgBrush");
            var bar = new DockPanel { Height = 28, Margin = new Thickness(6, 4) };
            _combo.SelectionChanged += (s, e) => { if (!_syncing && _combo.SelectedItem is Choice c) Apply(c); };
            var close = new Button { Classes = { "icon", "small" }, Content = new Controls.VxIcon { Icon = "Close" } };
            close.Click += (s, e) => (this.FindAncestorOfType<ViewportPanel>())?.SetLayout(1);
            DockPanel.SetDock(close, Dock.Right);
            bar.Children.Add(close);
            bar.Children.Add(_combo);
            bar.Children.Add(_label);
            var grid = new Grid();
            grid.Children.Add(_image);
            var barHost = new Border { Child = bar, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Background = (IBrush)Application.Current.FindResource("VxToolbarBrush"), Opacity = 0.92 };
            grid.Children.Add(barHost);
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
            var items = new List<Choice> { new Choice { Label = "Editor Camera", EditorCam = true } };
            var scene = ProjectData.Current?.ActiveScene;
            if (scene?.Entities != null) foreach (var e in scene.Entities) Add(e, items);
            _combo.ItemsSource = items;
            _combo.SelectedIndex = Math.Min(items.Count - 1, 1 + _index);
            _syncing = false;
            if (_combo.SelectedItem is Choice c) Apply(c);
        }
        private static void Add(GameEntity e, List<Choice> items)
        {
            if (e.GetComponent<Camera>() != null) items.Add(new Choice { Label = e.Name, Entity = e });
            if (e.Children != null) foreach (var c in e.Children) Add(c, items);
        }
        private void Apply(Choice c) { _session.SetCamera(c.EditorCam ? null : c.Entity, c.EditorCam); _label.Text = c.EditorCam ? "free camera" : "scene camera"; }

        private void Tick()
        {
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
        }

        public void Shutdown() { _timer?.Stop(); _session.Shutdown(); }
    }
}
