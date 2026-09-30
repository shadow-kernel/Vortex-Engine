using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Rendering;
using Editor.DllWrapper;
using VortexEditor.Controls;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Asset Viewer — the large preview opened with Ctrl/Cmd + double-click (port of the Windows Model Viewer window and
    /// prefab large preview): models, prefabs (.ventity) and primitives render isolated with an orbit / pan / zoom /
    /// WASD-QE camera; materials (.vmat) on a sphere / cube / plane / cylinder / cone; textures with zoom + pan, R / G / B / A
    /// channel views and alpha over a checkerboard. The view follows the asset: a saved material, prefab or model
    /// sidecar re-renders it live.
    /// </summary>
    public sealed class ModelViewerWindow : Window
    {
        public static void Open(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            bool primitive = fullPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase);
            if (!primitive && !File.Exists(fullPath)) { EditorCommands.Toast("File not found: " + Path.GetFileName(fullPath)); return; }
            EditorWindows.Show(new ModelViewerWindow(fullPath));
        }

        public enum ViewKind { Model, Prefab, Material, Texture, Primitive, Unsupported }

        private readonly string _path;
        private readonly ViewKind _kind;
        private readonly PreviewViewport _preview;
        private readonly TextureViewer _texture;
        private readonly TextBlock _message = Ui.Overlay("");
        private readonly TextBlock _info = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>();
        private DispatcherTimer _watch;
        private DateTime _stamp;
        private string _shape = "Sphere";

        public Task<bool> WhenReady => _ready.Task;
        public ViewKind Kind => _kind;
        public PreviewViewport Preview => _preview;
        public TextureViewer TextureViewer => _texture;

        public static ViewKind KindOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return ViewKind.Unsupported;
            if (path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return ViewKind.Primitive;
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (ext == ".ventity" || ext == ".vprefab") return ViewKind.Prefab;
            if (ext == ".vmat") return ViewKind.Material;
            if (ModelDocument.IsSupportedModel(path)) return ViewKind.Model;
            if (new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds", ".hdr", ".exr", ".gif", ".webp", ".tif", ".tiff", ".psd" }.Contains(ext)) return ViewKind.Texture;
            return ViewKind.Unsupported;
        }

        public ModelViewerWindow(string path)
        {
            _path = path;
            _kind = KindOf(path);
            string name = _kind == ViewKind.Primitive ? path.Substring(10) : Path.GetFileNameWithoutExtension(path);
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            MinWidth = 640; MinHeight = 420;
            _info.Foreground = Ui.Brush("VxTextSecondaryBrush");
            KeyDown += (s, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };

            if (_kind == ViewKind.Texture)
            {
                Title = "Texture — " + Path.GetFileName(path);
                Width = 1100; Height = 800;
                _texture = new TextureViewer { Background = Ui.PreviewBg };
                var bar = Bar(Path.GetFileName(path), Ui.Button("Texture Editor", () => EditorWindows.TextureEditor(_path), "Open the import settings for this texture"));
                var foot = new Border { Background = Ui.Brush("VxToolbarBrush"), BorderBrush = Ui.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 6), Child = _info };
                var g = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
                g.Children.Add(bar);
                Grid.SetRow(_texture, 1); g.Children.Add(_texture);
                Grid.SetRow(foot, 2); g.Children.Add(foot);
                Content = g;
                _texture.ShowLoading();
            }
            else
            {
                _preview = new PreviewViewport { Background = Ui.PreviewBg };
                Ui.AddKeyboardNavigation(_preview);
                Control extra = null;
                switch (_kind)
                {
                    case ViewKind.Model:
                        Title = "Model Viewer — " + name; Width = 1100; Height = 760;
                        extra = Ui.Button("Model Editor", () => EditorWindows.ModelEditor(_path), "Submeshes, materials and textures of this model");
                        break;
                    case ViewKind.Prefab:
                        Title = "Preview — " + name; Width = 1280; Height = 800;
                        WindowState = WindowState.Maximized;   // the Windows prefab large preview opens maximized
                        extra = Ui.Button("Edit Prefab", () => EditorWindows.PrefabEditor(_path), "Open the prefab in the Prefab Editor");
                        break;
                    case ViewKind.Material:
                        Title = "Material Preview — " + name; Width = 900; Height = 720;
                        extra = ShapePicker();
                        break;
                    default:
                        Title = "Preview — " + name; Width = 900; Height = 700;
                        break;
                }
                var extras = _kind == ViewKind.Material
                    ? new Control[] { extra, Ui.Button("Material Editor", () => EditorWindows.MaterialEditor(_path), "Edit this material") }
                    : new Control[] { extra };
                var bar = Ui.PreviewToolbar(name, _preview, extras);
                var cell = new Grid(); cell.Children.Add(_preview); cell.Children.Add(_message);
                var g = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
                g.Children.Add(bar); Grid.SetRow(cell, 1); g.Children.Add(cell);
                Content = g;
                _message.Text = "Loading…";
            }

            Opened += (s, e) => Dispatcher.UIThread.Post(() => _ = LoadAsync(), DispatcherPriority.Background);
            // free the preview's engine meshes here (before the viewport's own detach-time dispose), queue dropped first
            Closing += (s, e) => { if (!e.Cancel && _preview?.Model != null) { Ui.DropQueuedDraws(); _preview.Model = null; } };
            Closed += (s, e) => { _watch?.Stop(); if (_preview?.Model != null) { Ui.DropQueuedDraws(); _preview.Model = null; } _ready.TrySetResult(false); };
        }

        private Border Bar(string title, params Control[] right)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var l = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            l.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            l.Children.Add(new TextBlock { Text = "Wheel: zoom · Drag: pan · Double-click / 0: fit · 1: actual size", FontSize = 11, Foreground = Ui.Brush("VxTextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center });
            var r = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var c in right) r.Children.Add(c);
            g.Children.Add(l); Grid.SetColumn(r, 1); g.Children.Add(r);
            return new Border { Background = Ui.Brush("VxToolbarBrush"), BorderBrush = Ui.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 6), Child = g };
        }

        private Control ShapePicker()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
            foreach (var s in new[] { "Sphere", "Cube", "Plane", "Cylinder", "Cone" })
            {
                var rb = new RadioButton { Content = s, GroupName = "shape" + GetHashCode(), IsChecked = s == _shape };
                var shape = s;
                rb.IsCheckedChanged += (o, e) => { if (rb.IsChecked == true && _shape != shape) { _shape = shape; LoadMaterial(keepCamera: true); } };
                sp.Children.Add(rb);
            }
            return new Border { Classes = { "segmented" }, Child = sp, VerticalAlignment = VerticalAlignment.Center };
        }

        // ================================================================== load

        private async Task LoadAsync()
        {
            try
            {
                switch (_kind)
                {
                    case ViewKind.Texture:
                        var img = await TextureImage.LoadAsync(_path);
                        _texture.Image = img;
                        _info.Text = Describe(img);
                        _stamp = Stamp();
                        StartWatch();
                        _ready.TrySetResult(img.CanPreview);
                        return;
                    case ViewKind.Model:
                        Show(_preview.LoadAsset(_path), "Could not load this model.");
                        PoseSkinned();
                        break;
                    case ViewKind.Prefab:
                        Show(_preview.LoadAsset(_path, ProjectData.Current?.Path), "This prefab has nothing to preview (no mesh renderers, or its meshes could not be found).");
                        PoseSkinned();
                        break;
                    case ViewKind.Material:
                        LoadMaterial(keepCamera: false);
                        break;
                    case ViewKind.Primitive:
                        long mesh = PreviewModel.CreatePrimitive(_path);
                        Show(mesh >= 0 && (_preview.Model = PreviewModel.FromOwned(new[] { mesh }, null)) != null, "Unknown primitive.");
                        break;
                    default:
                        _message.Text = "No preview for this file type.";
                        _ready.TrySetResult(false);
                        return;
                }
                _stamp = Stamp();
                StartWatch();
                _preview.Focus();
                await Dispatcher.UIThread.InvokeAsync(() => _preview.RenderNow(), DispatcherPriority.Background);
                _ready.TrySetResult(_preview.LastImage != null);
            }
            catch (Exception ex)
            {
                _message.Text = "Could not open the preview: " + ex.Message;
                _ready.TrySetResult(false);
            }
        }

        private void Show(bool ok, string failText)
        {
            _message.Text = ok ? "" : failText;
            _message.IsVisible = !ok;
        }

        /// <summary>Rigged content: the bind pose the scene draws (upright, real scale, correct shading) and its framing.</summary>
        private void PoseSkinned()
        {
            var sc = _preview.Model?.Scene;
            if (sc == null || sc.Items.Count == 0) return;
            int posed;
            if (_kind == ViewKind.Prefab)
            {
                var models = PreviewSkinning.PrefabItemModels(_path, ProjectData.Current?.Path);
                if (models == null || models.Count != sc.Items.Count)
                {
                    // could not mirror the prefab's item order: fall back to its single rigged model, if there is one
                    var distinct = models?.Where(m => m != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    models = distinct != null && distinct.Count == 1 ? Enumerable.Repeat(distinct[0], sc.Items.Count).ToList() : null;
                }
                posed = models != null ? PreviewSkinning.Apply(sc, models) : 0;
            }
            else posed = PreviewSkinning.Apply(sc, _path);
            if (posed > 0) _preview.Scene = sc;   // re-frame the posed content
        }

        /// <summary>The material on the chosen preview shape. Texture paths are resolved against the .vmat's folder (the
        /// .vmat stores them relative to itself).</summary>
        private void LoadMaterial(bool keepCamera)
        {
            var cam = _preview.Camera;
            long mesh = -1, mat = -1;
            try
            {
                var vm = VortexMaterial.Load(_path);
                if (vm == null) { Show(false, "Could not read this material."); return; }
                vm.ResolvePathsAbsolute(Path.GetDirectoryName(_path));
                mat = MaterialService.Instance.BuildEngineMaterial(vm);
                mesh = _shape == "Sphere" ? VortexAPI.CreateSphereMesh(0.62f) : PreviewModel.CreatePrimitive(_shape);
                if (mesh < 0) { if (mat >= 0) VortexAPI.DeleteMaterial(mat); Show(false, "Could not create the preview mesh."); return; }
                if (_preview.Model != null) Ui.DropQueuedDraws();   // the previous shape's mesh is freed by the assignment
                _preview.Model = PreviewModel.FromOwned(new[] { mesh }, new[] { mat });
                Show(true, null);
                if (keepCamera) _preview.Camera = cam;
            }
            catch (Exception ex)
            {
                if (mesh >= 0) { try { VortexAPI.DeleteMesh(mesh); } catch { } }
                if (mat >= 0) { try { VortexAPI.DeleteMaterial(mat); } catch { } }
                Show(false, "Could not build the material: " + ex.Message);
            }
        }

        private static string Describe(TextureImage img)
        {
            if (img == null) return "";
            var parts = new System.Collections.Generic.List<string>();
            if (img.Width > 0) parts.Add(img.Width + " × " + img.Height);
            parts.Add(img.FormatName);
            if (!string.IsNullOrEmpty(img.PixelFormat)) parts.Add(img.PixelFormat);
            parts.Add(img.HasAlpha ? "has alpha" : "opaque");
            parts.Add(ModelDocument.FormatBytes(img.FileSize));
            if (img.StoredMipLevels > 1) parts.Add(img.StoredMipLevels + " stored mips");
            else if (img.FullMipChain > 0) parts.Add(img.FullMipChain + " mip levels");
            return string.Join("  ·  ", parts) + (img.CanPreview ? "" : "  —  " + img.Error);
        }

        // ================================================================== live follow

        /// <summary>Newest timestamp of what the view shows (the asset + a model's sidecar materials).</summary>
        private DateTime Stamp()
        {
            DateTime t = DateTime.MinValue;
            void Take(string p) { try { if (File.Exists(p)) { var w = File.GetLastWriteTimeUtc(p); if (w > t) t = w; } } catch { } }
            if (_kind == ViewKind.Primitive) return t;
            Take(_path);
            if (_kind == ViewKind.Model)
            {
                var matDir = Path.Combine(Path.GetDirectoryName(_path) ?? "", "materials");
                try { if (Directory.Exists(matDir)) foreach (var f in Directory.GetFiles(matDir, "*.vmat")) Take(f); } catch { }
            }
            return t;
        }

        private void StartWatch()
        {
            if (_kind == ViewKind.Primitive || _watch != null) return;
            _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _watch.Tick += async (s, e) =>
            {
                try
                {
                    var now = Stamp();
                    if (now <= _stamp) return;
                    _stamp = now;
                    await ReloadAsync();
                }
                catch (Exception ex) { ConsoleService.Instance.LogWarning("Asset Viewer reload: " + ex.Message); }
            };
            _watch.Start();
        }

        /// <summary>Re-read the asset from disk, keeping the camera.</summary>
        public async Task ReloadAsync()
        {
            if (_kind == ViewKind.Texture)
            {
                var ch = _texture.Channel;
                var img = await TextureImage.LoadAsync(_path);
                _texture.Image = img; _texture.SetChannel(ch);
                _info.Text = Describe(img);
                return;
            }
            var cam = _preview.Camera;
            if (_kind == ViewKind.Material) LoadMaterial(keepCamera: true);
            else if (_kind == ViewKind.Model) { Ui.DropQueuedDraws(); _preview.LoadAsset(_path); PoseSkinned(); _preview.Camera = cam; }
            else if (_kind == ViewKind.Prefab) { Ui.DropQueuedDraws(); _preview.LoadAsset(_path, ProjectData.Current?.Path); PoseSkinned(); _preview.Camera = cam; }
        }

        internal void SetShape(string shape) { _shape = shape; LoadMaterial(keepCamera: true); }
    }
}
