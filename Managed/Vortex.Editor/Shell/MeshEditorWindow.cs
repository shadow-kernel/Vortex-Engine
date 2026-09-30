using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Services;
using Editor.Core.Services.Rendering;
using Editor.DllWrapper;
using VortexEditor.Controls;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Mesh Editor (port of the Windows MeshEditorDialog): the geometry breakdown of a model — format, vertex / triangle
    /// counts, the submesh list — next to a large live 3D view (orbit / pan / zoom / WASD-QE). Selecting a submesh
    /// highlights it (the others are ghosted) or isolates it; its material, bounds and counts show below the list.
    /// Opened from the Model Editor (double-click a submesh) and via <see cref="Open(string)"/>.
    /// </summary>
    public sealed class MeshEditorWindow : Window
    {
        public static void Open(string fullPath) => Open(fullPath, -1);

        public static void Open(string fullPath, int submeshIndex)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) { EditorCommands.Toast("Model file not found"); return; }
            EditorWindows.Show(new MeshEditorWindow(fullPath, submeshIndex));
        }

        private readonly string _path;
        private readonly int _initialSubmesh;
        private ModelDocument _doc;
        private ModelSubmesh _selected;
        private readonly TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>();

        private readonly TextBlock _title = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _format = new TextBlock { FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        private readonly TextBlock _stats = new TextBlock { FontSize = 12, Margin = new Thickness(14, 0, 14, 12), TextWrapping = TextWrapping.Wrap };
        private readonly ListBox _list = new ListBox { Background = Brushes.Transparent, Padding = new Thickness(6) };
        private readonly StackPanel _details = new StackPanel { Margin = new Thickness(14, 10), Spacing = 3 };
        private readonly PreviewViewport _preview = new PreviewViewport();
        private readonly TextBlock _message = Ui.Overlay("Loading model…");
        private readonly PreviewScene _scene = new PreviewScene();
        private readonly Dictionary<ModelMaterial, long> _materials = new Dictionary<ModelMaterial, long>();
        private long _ghost = -1;
        private ToggleButton _highlight, _isolate;

        public Task<bool> WhenReady => _ready.Task;
        public PreviewViewport Preview => _preview;
        public ModelDocument Document => _doc;

        public MeshEditorWindow(string modelPath, int submeshIndex = -1)
        {
            _path = modelPath;
            _initialSubmesh = submeshIndex;
            Title = "Mesh Editor — " + Path.GetFileName(modelPath);
            Width = 1150; Height = 760; MinWidth = 900; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            _format.Foreground = Ui.Brush("VxAccentBrush");
            _stats.Foreground = Ui.Brush("VxTextSecondaryBrush");
            _title.Text = Path.GetFileName(modelPath);

            // left: identity, stats, submesh list, selection details
            var head = new StackPanel { Margin = new Thickness(14, 14, 14, 10) };
            head.Children.Add(_title); head.Children.Add(_format);
            _list.SelectionChanged += (s, e) => OnSelected();
            var left = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
            left.Children.Add(head);
            Grid.SetRow(_stats, 1); left.Children.Add(_stats);
            var sub = Ui.HeaderBar("Submeshes", Ui.Button("Show all", () => { _list.SelectedIndex = -1; }, "Clear the selection", "ghost"));
            Grid.SetRow(sub, 2); left.Children.Add(sub);
            var sl = new ScrollViewer { Content = _list }; Grid.SetRow(sl, 3); left.Children.Add(sl);
            var det = new Border { BorderBrush = Ui.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Child = _details };
            Grid.SetRow(det, 4); left.Children.Add(det);

            // right: toolbar + live view
            _highlight = Ui.Toggle("Highlight", "Ghost the other submeshes so the selected one stands out", on => RebuildItems(), initial: true);
            _isolate = Ui.Toggle("Isolate", "Show only the selected submesh", on => RebuildItems());
            _preview.Background = Ui.PreviewBg;
            Ui.AddKeyboardNavigation(_preview);
            var frame = Ui.Button("Frame", FrameSelection, "Zoom onto the selected submesh (or double-click it in the list)");
            _list.DoubleTapped += (s, e) => FrameSelection();
            var toolbar = Ui.PreviewToolbar(Path.GetFileNameWithoutExtension(modelPath), _preview, _highlight, _isolate, frame);
            var cell = new Grid(); cell.Children.Add(_preview); cell.Children.Add(_message);
            var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            right.Children.Add(toolbar); Grid.SetRow(cell, 1); right.Children.Add(cell);

            var main = new Grid { ColumnDefinitions = new ColumnDefinitions("300,Auto,*") };
            main.Children.Add(new Border { Background = Ui.Brush("VxSidebarBrush"), Child = left });
            var split = new GridSplitter { Width = 4, Background = Ui.Brush("VxHairlineBrush"), ResizeDirection = GridResizeDirection.Columns };
            Grid.SetColumn(split, 1); main.Children.Add(split);
            Grid.SetColumn(right, 2); main.Children.Add(right);
            Content = main;

            Opened += (s, e) => Dispatcher.UIThread.Post(Load, DispatcherPriority.Background);
            Closed += (s, e) => Release();
        }

        private void Load()
        {
            try { _doc = ModelDocument.Load(_path); }
            catch (Exception ex)
            {
                _message.Text = "Could not load this model:\n" + ex.Message;
                _stats.Text = ex.Message;
                _ready.TrySetResult(false);
                return;
            }
            _format.Text = _doc.FormatName;
            UpdateStats();
            foreach (var s in _doc.Submeshes) _list.Items.Add(Ui.Card(Ui.Chip("Cube"), s.DisplayName, s.GeometryInfo ?? "material: " + (_doc.MaterialOf(s)?.Name ?? "—")));
            foreach (var m in _doc.Materials) { try { _materials[m] = MaterialService.Instance.BuildEngineMaterial(_doc.PreviewVmat(m)); } catch { _materials[m] = -1; } }
            RebuildItems();
            _preview.Scene = _scene;
            _message.IsVisible = _scene.Items.Count == 0;
            if (_scene.Items.Count == 0) _message.Text = "No 3D preview for this file.";
            if (_initialSubmesh >= 0 && _initialSubmesh < _doc.Submeshes.Count) _list.SelectedIndex = _initialSubmesh;
            else ShowDetails();
            _ = _doc.LoadTotalsAsync().ContinueWith(_ => Dispatcher.UIThread.Post(UpdateStats));
            Dispatcher.UIThread.Post(() => { _preview.RenderNow(); _ready.TrySetResult(true); }, DispatcherPriority.Background);
        }

        private void UpdateStats()
        {
            if (_doc == null) return;
            var geo = _doc.GeometrySummary;
            _stats.Text = _doc.StatsSummary + (geo != null ? "\n" + geo : "");
        }

        private void OnSelected()
        {
            int i = _list.SelectedIndex;
            _selected = _doc != null && i >= 0 && i < _doc.Submeshes.Count ? _doc.Submeshes[i] : null;
            RebuildItems();
            ShowDetails();
        }

        private void ShowDetails()
        {
            _details.Children.Clear();
            if (_doc == null) return;
            if (_selected == null)
            {
                _details.Children.Add(Ui.Small("Select a submesh to highlight it in the view.", "VxTextTertiaryBrush"));
                return;
            }
            var s = _selected;
            _details.Children.Add(new TextBlock { Text = s.DisplayName, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            _details.Children.Add(Ui.Small("Submesh " + s.Index + " · " + Path.GetFileName(_path) + "#submesh" + s.Index));
            if (s.HasGeometryCounts) _details.Children.Add(Ui.Small(s.GeometryInfo));
            var m = _doc.MaterialOf(s);
            if (m != null)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(Ui.Swatch(m.BaseColor, 12));
                row.Children.Add(Ui.Small("Material: " + m.Name + " · " + m.TextureSummary));
                _details.Children.Add(row);
            }
            bool skinned = false;
            if (s.MeshId >= 0) { try { skinned = VortexAPI.MeshIsSkinned(s.MeshId); } catch { } }
            if (s.BoundsSize != null)
                _details.Children.Add(Ui.Small((skinned ? "Mesh bounds (mesh space): " : "Bounds: ") + F(s.BoundsSize[0]) + " × " + F(s.BoundsSize[1]) + " × " + F(s.BoundsSize[2])));
            if (skinned)
            {
                var item = _scene.Items.FirstOrDefault(i => i.Mesh == s.MeshId && i.BonePalette != null);
                var posed = item != null ? PreviewSkinning.PosedSize(item) : null;
                if (posed != null) _details.Children.Add(Ui.Small("Posed size (bind pose): " + F(posed[0]) + " × " + F(posed[1]) + " × " + F(posed[2])));
                _details.Children.Add(Ui.Small("Skinned (bone weights) — shown in its bind pose"));
            }
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Point the camera at the selected submesh (as drawn: bind pose for rigged meshes) and zoom to fit it.</summary>
        public void FrameSelection()
        {
            if (_selected == null || _doc == null) { _preview.ResetView(); return; }
            var item = _scene.Items.FirstOrDefault(i => i.Mesh == _selected.MeshId);
            if (item == null) return;
            var one = new PreviewScene(); one.Items.Add(item);
            var sel = item.BonePalette != null ? PreviewSkinning.PosedBounds(one) : PreviewRenderer.ComputeFrame(one);
            var all = PreviewRenderer.ComputeFrame(_scene);
            if (sel == null || all == null || all[3] <= 0) return;
            var cam = _preview.Camera;
            cam.Focus = new[] { sel[0], sel[1], sel[2] };
            cam.DistScale = Math.Max(0.03f, Math.Min(1f, sel[3] / all[3] * 1.2f));
            // look at the part from the side it sits on (a visor from the front, a heel from behind)
            float dx = sel[0] - all[0], dz = sel[2] - all[2];
            if (Math.Sqrt(dx * dx + dz * dz) > all[3] * 0.04f) cam.Yaw = (float)Math.Atan2(dx, dz);
            _preview.Camera = cam;
            _preview.Focus();
        }

        private long Ghost()
        {
            if (_ghost >= 0) return _ghost;
            try
            {
                _ghost = VortexAPI.CreateNewMaterial();
                if (_ghost >= 0)
                {
                    VortexAPI.SetMaterialBaseColor(_ghost, 0.24f, 0.25f, 0.28f, 1f);
                    VortexAPI.SetMaterialRoughnessValue(_ghost, 0.9f);
                    VortexAPI.SetMaterialMetallicValue(_ghost, 0f);
                }
            }
            catch { _ghost = -1; }
            return _ghost;
        }

        private void RebuildItems()
        {
            if (_doc == null) return;
            _scene.Items.Clear();
            bool isolate = _isolate.IsChecked == true && _selected != null;
            bool ghost = _highlight.IsChecked == true && _selected != null && !isolate;
            foreach (var s in _doc.Submeshes)
            {
                if (s.MeshId < 0) continue;
                bool isSel = ReferenceEquals(s, _selected);
                if (isolate && !isSel) continue;
                var m = _doc.MaterialOf(s);
                long mat = ghost && !isSel ? Ghost() : (m != null && _materials.TryGetValue(m, out var id) ? id : -1);
                _scene.Items.Add(new PreviewItem { Mesh = s.MeshId, Material = mat });
            }
            PreviewSkinning.Apply(_scene, _path);   // rigged models: the scene's bind pose
            if (ReferenceEquals(_preview.Scene, _scene)) _preview.Scene = _scene;
            _preview.Invalidate();
        }

        private void Release()
        {
            _preview.Scene = null;
            Ui.DropQueuedDraws();   // no queued draw may still point at the meshes freed below
            foreach (var id in _materials.Values) if (id >= 0) { try { VortexAPI.DeleteMaterial(id); } catch { } }
            _materials.Clear();
            if (_ghost >= 0) { try { VortexAPI.DeleteMaterial(_ghost); } catch { } _ghost = -1; }
            _doc?.Dispose(); _doc = null;
            _ready.TrySetResult(false);
        }

        internal void Select(int i) { if (i >= -1 && i < _list.ItemCount) _list.SelectedIndex = i; }
        internal void SetIsolate(bool on) => _isolate.IsChecked = on;
    }
}
