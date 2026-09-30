using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Assets;
using Editor.Core.Services;
using Editor.Core.Services.Rendering;
using Editor.DllWrapper;
using VortexEditor.Controls;
using VortexEditor.Services;
using VortexEditor.Shell.AssetImport;
using VortexEditor.Shell.ModelTools;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Model Editor (port of the Windows UniversalModelEditorDialog): the model's submeshes and materials on the left,
    /// a large live 3D preview in the centre (every material edit re-renders immediately), and on the right the selected
    /// material's colour / PBR values / texture maps, the Texture Library (every texture around the model, drag onto a
    /// slot), all assigned textures and the model's skeleton + animation clips. "Save Materials" writes one .vmat per
    /// submesh (materials/submesh_N.vmat — what scene placement binds) and pushes the edits onto placed instances.
    /// The "Default scale" is stored in the model's .vimport sidecar and applied when the model is added to a scene.
    /// </summary>
    public sealed class ModelEditorWindow : Window
    {
        /// <summary>Open this window (owned by the main window).</summary>
        public static void Open(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) { EditorCommands.Toast("Model file not found"); return; }
            EditorWindows.Show(new ModelEditorWindow(fullPath));
        }

        private readonly string _path;
        private ModelDocument _doc;
        private ModelMaterial _selectedMaterial;
        private ModelSubmesh _selectedSubmesh;
        private readonly TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>();

        // left
        private readonly TextBlock _title = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _format = new TextBlock { FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        private readonly TextBlock _stats = new TextBlock { FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _warning = new TextBlock { FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        private readonly ListBox _submeshList = new ListBox { Background = Brushes.Transparent, Padding = new Thickness(4) };
        private readonly ListBox _materialList = new ListBox { Background = Brushes.Transparent, Padding = new Thickness(4) };
        private readonly List<(Border swatch, TextBlock summary)> _materialRows = new List<(Border, TextBlock)>();

        // centre
        private readonly PreviewViewport _preview = new PreviewViewport();
        private readonly TextBlock _previewMessage = Ui.Overlay("Loading model…");
        private readonly PreviewScene _scene = new PreviewScene();
        private readonly Dictionary<ModelMaterial, long> _engineMaterials = new Dictionary<ModelMaterial, long>();
        private readonly HashSet<ModelMaterial> _dirtyPreview = new HashSet<ModelMaterial>();
        private DispatcherTimer _previewDebounce;
        private TextBox _scaleBox;
        private float _defaultScale = 1f;
        private ToggleButton _isolate;

        // right
        private readonly TabControl _tabs = new TabControl();
        private readonly StackPanel _props = new StackPanel { Margin = new Thickness(16, 12, 16, 16), Spacing = 2 };
        private readonly WrapPanel _library = new WrapPanel { Margin = new Thickness(14) };
        private readonly TextBlock _libraryHeader = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _libraryCount = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        private readonly StackPanel _assigned = new StackPanel { Margin = new Thickness(16, 12, 16, 16) };
        private readonly StackPanel _animations = new StackPanel { Margin = new Thickness(16, 12, 16, 16), Spacing = 4 };
        private bool _animationsLoaded;

        // bottom
        private readonly TextBlock _status = new TextBlock { Text = "Ready", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

        // live reload of sidecar .vmat files edited elsewhere (Material Editor)
        private readonly Dictionary<string, DateTime> _knownVmatTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer _watch;

        /// <summary>Completes when the model is loaded and the first preview rendered (tests).</summary>
        public Task<bool> WhenReady => _ready.Task;
        public PreviewViewport Preview => _preview;
        public ModelDocument Document => _doc;
        public TabControl Tabs => _tabs;

        public ModelEditorWindow(string modelPath)
        {
            _path = modelPath;
            Title = "Model Editor — " + Path.GetFileName(modelPath);
            Width = 1380; Height = 860; MinWidth = 1000; MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            _format.Foreground = Ui.Brush("VxAccentBrush");
            _stats.Foreground = Ui.Brush("VxTextSecondaryBrush");
            _libraryCount.Foreground = Ui.Brush("VxTextSecondaryBrush");
            _status.Foreground = Ui.Brush("VxTextSecondaryBrush");
            _title.Text = Path.GetFileName(modelPath);

            var main = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), ColumnDefinitions = new ColumnDefinitions("300,Auto,*,Auto,410") };
            var left = BuildStructurePanel();
            var centre = BuildPreviewPanel();
            var right = BuildRightPanel();
            Grid.SetColumn(left, 0); Grid.SetColumn(centre, 2); Grid.SetColumn(right, 4);
            var s1 = new GridSplitter { Width = 4, Background = Ui.Brush("VxHairlineBrush"), ResizeDirection = GridResizeDirection.Columns };
            var s2 = new GridSplitter { Width = 4, Background = Ui.Brush("VxHairlineBrush"), ResizeDirection = GridResizeDirection.Columns };
            Grid.SetColumn(s1, 1); Grid.SetColumn(s2, 3);
            main.Children.Add(left); main.Children.Add(s1); main.Children.Add(centre); main.Children.Add(s2); main.Children.Add(right);
            var status = BuildStatusBar();
            Grid.SetRow(status, 1); Grid.SetColumnSpan(status, 5);
            main.Children.Add(status);
            Content = main;

            Opened += (s, e) => Dispatcher.UIThread.Post(LoadModel, DispatcherPriority.Background);
            Closed += (s, e) => Release();
            KeyDown += (s, e) => { if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Meta)) { _ = SaveMaterialsInteractive(); e.Handled = true; } };
        }

        // ================================================================== layout

        private Control BuildStructurePanel()
        {
            var head = new StackPanel { Margin = new Thickness(12) };
            head.Children.Add(_title); head.Children.Add(_format); head.Children.Add(_stats); head.Children.Add(_warning);
            _warning.Foreground = Ui.Brush("VxOrangeBrush");

            _submeshList.SelectionChanged += (s, e) => OnSubmeshSelected();
            _submeshList.DoubleTapped += (s, e) => { if (_selectedSubmesh != null) MeshEditorWindow.Open(_path, _selectedSubmesh.Index); };
            ToolTip.SetTip(_submeshList, "Select to show its material · double-click: Mesh Editor");
            _materialList.SelectionChanged += (s, e) => OnMaterialSelected();
            _materialList.DoubleTapped += (s, e) => { if (_selectedMaterial != null) OpenSelectedMaterialInEditor(); };
            ToolTip.SetTip(_materialList, "Double-click: open in the Material Editor");

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10, 8) };
            buttons.Children.Add(Ui.Button("Auto-Assign All", AutoAssignAll, "Assign textures from the Texture Library to every empty slot by naming convention"));
            buttons.Children.Add(Ui.Button("Clear All", ClearAllTextures, "Remove every texture assignment"));

            var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,*,Auto") };
            g.Children.Add(head);
            var sh = Ui.HeaderBar("Submeshes"); Grid.SetRow(sh, 1); g.Children.Add(sh);
            var sl = new ScrollViewer { Content = _submeshList }; Grid.SetRow(sl, 2); g.Children.Add(sl);
            var mh = Ui.HeaderBar("Materials"); Grid.SetRow(mh, 3); g.Children.Add(mh);
            var ml = new ScrollViewer { Content = _materialList }; Grid.SetRow(ml, 4); g.Children.Add(ml);
            Grid.SetRow(buttons, 5); g.Children.Add(buttons);
            return new Border { Background = Ui.Brush("VxSidebarBrush"), Child = g };
        }

        private Control BuildPreviewPanel()
        {
            _defaultScale = ModelImportSettings.LoadDefaultScale(_path);
            _scaleBox = new TextBox { Width = 74, Text = _defaultScale.ToString("0.####", CultureInfo.InvariantCulture), Classes = { "number" } };
            _scaleBox.LostFocus += (s, e) => CommitDefaultScale();
            _scaleBox.KeyDown += (s, e) => { if (e.Key == Key.Return) { CommitDefaultScale(); _preview.Focus(); e.Handled = true; } };
            ToolTip.SetTip(_scaleBox, "Placement scale, stored in the model's .vimport sidecar");

            _isolate = Ui.Toggle("Isolate", "Show only the selected submesh in the preview", on => RebuildItems());
            var turn = Ui.TurntableToggle(_preview);
            var reset = Ui.Button("Reset View", () => { _preview.ResetView(); _preview.Focus(); }, "Frame the whole model (F / double-click)");

            var barGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
            barGrid.Children.Add(new TextBlock { Text = "Default scale", FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            Grid.SetColumn(_scaleBox, 1); barGrid.Children.Add(_scaleBox);
            var hint = Ui.Small("applied when this model is added to a scene");
            hint.VerticalAlignment = VerticalAlignment.Center; hint.Margin = new Thickness(10, 0, 10, 0);
            Grid.SetColumn(hint, 2); barGrid.Children.Add(hint);
            var rightRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            rightRow.Children.Add(_isolate); rightRow.Children.Add(turn); rightRow.Children.Add(reset);
            Grid.SetColumn(rightRow, 3); barGrid.Children.Add(rightRow);
            var bar = new Border { Background = Ui.Brush("VxToolbarBrush"), BorderBrush = Ui.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 7), Child = barGrid };

            _preview.Background = Ui.PreviewBg;
            Ui.AddKeyboardNavigation(_preview);
            var cell = new Grid();
            cell.Children.Add(_preview);
            cell.Children.Add(_previewMessage);

            var g = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            g.Children.Add(bar);
            Grid.SetRow(cell, 1); g.Children.Add(cell);
            return g;
        }

        private Control BuildRightPanel()
        {
            _tabs.Items.Add(new TabItem { Header = "Material", Content = new ScrollViewer { Content = _props } });
            var libHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            libHead.Children.Add(_libraryHeader); Grid.SetColumn(_libraryCount, 1); libHead.Children.Add(_libraryCount);
            var libGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            libGrid.Children.Add(new Border { Background = Ui.Brush("VxToolbarBrush"), BorderBrush = Ui.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 8), Child = libHead });
            var libScroll = new ScrollViewer { Content = _library }; Grid.SetRow(libScroll, 1); libGrid.Children.Add(libScroll);
            _tabs.Items.Add(new TabItem { Header = "Texture Library", Content = libGrid });
            _tabs.Items.Add(new TabItem { Header = "Assigned", Content = new ScrollViewer { Content = _assigned } });
            _tabs.Items.Add(new TabItem { Header = "Animations", Content = new ScrollViewer { Content = _animations } });
            _tabs.SelectionChanged += (s, e) => { if (_tabs.SelectedIndex == 3) _ = LoadAnimationsAsync(); };
            _props.Children.Add(Ui.Muted("Select a material to edit its properties"));
            return new Border { Background = Ui.Brush("VxPanelBrush"), Child = _tabs };
        }

        private Control BuildStatusBar()
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            g.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(Ui.Button("Save Materials", () => _ = SaveMaterialsInteractive(), "Write materials/submesh_N.vmat and update placed instances (⌘S)", "accent", 120));
            buttons.Children.Add(Ui.Button("Close", Close, null, null, 80));
            Grid.SetColumn(buttons, 1); g.Children.Add(buttons);
            return new Border { Background = Ui.Brush("VxToolbarBrush"), BorderBrush = Ui.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 8), Child = g };
        }

        // ================================================================== load

        private void LoadModel()
        {
            try
            {
                _doc = ModelDocument.Load(_path);
            }
            catch (Exception ex)
            {
                _previewMessage.Text = "Could not load this model:\n" + ex.Message;
                _status.Text = "Failed to load model: " + ex.Message;
                _status.Foreground = Ui.Brush("VxRedBrush");
                _ready.TrySetResult(false);
                return;
            }
            _format.Text = _doc.FormatName + (_doc.UsesSidecarMaterials ? "  ·  saved materials" : "");
            int siblings = _doc.SiblingModelCount;
            if (siblings > 0)
            {
                _warning.Text = "This folder holds " + siblings + " other model(s); they share materials/submesh_N.vmat. Give each model its own folder (Import does) to keep their materials apart.";
                _warning.IsVisible = true;
            }
            if (_doc.Warnings.Count > 0) ToolTip.SetTip(_warning, string.Join("\n", _doc.Warnings));
            UpdateStats();
            foreach (var s in _doc.Submeshes) _submeshList.Items.Add(SubmeshCard(s));
            _materialRows.Clear();
            foreach (var m in _doc.Materials) _materialList.Items.Add(MaterialCard(m));
            FillLibrary();
            RefreshAssigned();
            foreach (var m in _doc.Materials) { var p = _doc.ResolveMaterialVmatPath(m); if (p != null) _knownVmatTimes[p] = MTime(p); }

            // preview: the document's engine meshes with materials built from the editor state
            foreach (var m in _doc.Materials) _engineMaterials[m] = BuildEngineMaterial(m);
            RebuildItems();
            _preview.Scene = _scene;
            _previewMessage.IsVisible = _scene.Items.Count == 0;
            if (_scene.Items.Count == 0) _previewMessage.Text = "No 3D preview for this file (the native import failed — showing the OBJ/MTL data).";

            if (_doc.Materials.Count > 0) _materialList.SelectedIndex = 0;
            if (_doc.Submeshes.Count > 0) _submeshList.SelectedIndex = 0;
            _status.Text = "Loaded " + _doc.FileName + " — " + _doc.StatsSummary;
            _ = _doc.LoadTotalsAsync().ContinueWith(_ => Dispatcher.UIThread.Post(UpdateStats));

            _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _watch.Tick += (s, e) => PollSidecars();
            _watch.Start();

            Dispatcher.UIThread.Post(() => { _preview.RenderNow(); _ready.TrySetResult(true); }, DispatcherPriority.Background);
        }

        private void UpdateStats()
        {
            if (_doc == null) return;
            var geo = _doc.GeometrySummary;
            _stats.Text = _doc.StatsSummary + (geo != null ? "\n" + geo : "");
        }

        private Control SubmeshCard(ModelSubmesh s)
        {
            var m = _doc.MaterialOf(s);
            string info = s.GeometryInfo ?? ("material: " + (m?.Name ?? "—"));
            return Ui.Card(Ui.Chip("Cube"), s.DisplayName, info);
        }

        private Control MaterialCard(ModelMaterial m)
        {
            var sw = Ui.Swatch(m.BaseColor, 30);
            var card = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(2, 3) };
            sw.VerticalAlignment = VerticalAlignment.Center;
            card.Children.Add(sw);
            var sp = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
            var summary = new TextBlock { Text = m.TextureSummary, FontSize = 11, Foreground = Ui.Brush("VxTextSecondaryBrush") };
            sp.Children.Add(summary);
            Grid.SetColumn(sp, 1); card.Children.Add(sp);
            _materialRows.Add((sw, summary));
            return card;
        }

        private void UpdateMaterialRow(ModelMaterial m)
        {
            int i = _doc.Materials.IndexOf(m);
            if (i < 0 || i >= _materialRows.Count) return;
            _materialRows[i].swatch.Background = new SolidColorBrush(Ui.ToColor(m.BaseColor));
            _materialRows[i].summary.Text = m.TextureSummary;
        }

        // ================================================================== selection

        private void OnSubmeshSelected()
        {
            int i = _submeshList.SelectedIndex;
            _selectedSubmesh = _doc != null && i >= 0 && i < _doc.Submeshes.Count ? _doc.Submeshes[i] : null;
            if (_selectedSubmesh != null && _selectedSubmesh.MaterialIndex >= 0 && _selectedSubmesh.MaterialIndex < _doc.Materials.Count)
                _materialList.SelectedIndex = _selectedSubmesh.MaterialIndex;
            if (_isolate?.IsChecked == true) RebuildItems();
        }

        private void OnMaterialSelected()
        {
            int i = _materialList.SelectedIndex;
            _selectedMaterial = _doc != null && i >= 0 && i < _doc.Materials.Count ? _doc.Materials[i] : null;
            UpdatePropertiesPanel();
        }

        // ================================================================== preview

        private long BuildEngineMaterial(ModelMaterial m)
        {
            try { return MaterialService.Instance.BuildEngineMaterial(_doc.PreviewVmat(m)); }
            catch { return -1; }
        }

        private void RebuildItems()
        {
            if (_doc == null) return;
            _scene.Items.Clear();
            bool isolate = _isolate?.IsChecked == true && _selectedSubmesh != null;
            foreach (var s in _doc.Submeshes)
            {
                if (s.MeshId < 0) continue;
                if (isolate && !ReferenceEquals(s, _selectedSubmesh)) continue;
                var m = _doc.MaterialOf(s);
                long mat = m != null && _engineMaterials.TryGetValue(m, out var id) ? id : -1;
                _scene.Items.Add(new PreviewItem { Mesh = s.MeshId, Material = mat });
            }
            PreviewSkinning.Apply(_scene, _path);                                   // rigged models: the scene's bind pose
            if (ReferenceEquals(_preview.Scene, _scene)) _preview.Scene = _scene;   // re-frame the (isolated) content
            _preview.Invalidate();
        }

        /// <summary>Rebuild a material's preview (debounced so slider drags stay smooth).</summary>
        private void SchedulePreview(ModelMaterial m)
        {
            if (m == null || _doc == null) return;
            _dirtyPreview.Add(m);
            UpdateMaterialRow(m);
            if (_previewDebounce == null)
            {
                _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                _previewDebounce.Tick += (s, e) => { _previewDebounce.Stop(); FlushPreview(); };
            }
            _previewDebounce.Stop();
            _previewDebounce.Start();
        }

        private void FlushPreview()
        {
            if (_doc == null) return;
            var old = new List<long>();
            foreach (var m in _dirtyPreview.ToList())
            {
                if (_engineMaterials.TryGetValue(m, out var prev) && prev >= 0) old.Add(prev);
                _engineMaterials[m] = BuildEngineMaterial(m);
            }
            _dirtyPreview.Clear();
            // swap the ids in place (keeps the framing), then free the replaced materials
            foreach (var it in _scene.Items)
            {
                var s = _doc.Submeshes.FirstOrDefault(x => x.MeshId == it.Mesh);
                var m = _doc.MaterialOf(s);
                if (m != null && _engineMaterials.TryGetValue(m, out var id)) it.Material = id;
            }
            foreach (var id in old) { try { VortexAPI.DeleteMaterial(id); } catch { } }
            _preview.Invalidate();
        }

        private void RebuildAllPreviewMaterials() { if (_doc != null) foreach (var m in _doc.Materials) _dirtyPreview.Add(m); FlushPreview(); }

        // ================================================================== default scale

        private void CommitDefaultScale()
        {
            if (float.TryParse(_scaleBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0.0001f)
            {
                if (Math.Abs(v - _defaultScale) > 1e-6f || !File.Exists(_path + ".vimport"))
                {
                    _defaultScale = v;
                    ModelImportSettings.SaveDefaultScale(_path, _defaultScale);
                    _status.Text = "Default scale set to " + _defaultScale.ToString("0.###", CultureInfo.InvariantCulture) + " — applied when this model is placed";
                }
            }
            _scaleBox.Text = _defaultScale.ToString("0.####", CultureInfo.InvariantCulture);
        }

        // ================================================================== properties panel

        private void UpdatePropertiesPanel()
        {
            ClearRefreshersOf(_props);
            _props.Children.Clear();
            var m = _selectedMaterial;
            if (m == null) { _props.Children.Add(Ui.Muted("Select a material to edit its properties")); return; }

            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(new TextBlock { Text = m.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var open = Ui.Button("Material Editor", OpenSelectedMaterialInEditor, "Open this material's .vmat in the full Material Editor (edits the same file scene placement binds)");
            Grid.SetColumn(open, 1); head.Children.Add(open);
            _props.Children.Add(head);
            var vmat = _doc.ResolveMaterialVmatPath(m);
            _props.Children.Add(Ui.Small(vmat != null && File.Exists(vmat) ? Ui.ProjectRelative(vmat) : "Not saved yet — Save Materials writes " + (vmat != null ? "materials/" + Path.GetFileName(vmat) : "the .vmat"), "VxTextTertiaryBrush"));

            _props.Children.Add(Ui.Header("Base color"));
            _props.Children.Add(ColorBar(m));

            _props.Children.Add(Ui.Header("PBR properties"));
            _props.Children.Add(Row("Metallic", SliderRow(() => m.Metallic, v => { m.Metallic = v; SchedulePreview(m); }, 0, 1)));
            _props.Children.Add(Row("Roughness", SliderRow(() => m.Roughness, v => { m.Roughness = v; SchedulePreview(m); }, 0, 1)));
            _props.Children.Add(Row("Normal strength", SliderRow(() => m.NormalStrength, v => { m.NormalStrength = v; SchedulePreview(m); }, 0, 2)));
            _props.Children.Add(Row("AO strength", SliderRow(() => m.AOStrength, v => { m.AOStrength = v; SchedulePreview(m); }, 0, 1)));
            // the renderer lets an assigned map replace the matching value — say so, or edits look like they do nothing
            var driven = new List<string>();
            if (m.GetSlot(TextureMapType.Albedo)?.IsAssigned == true) driven.Add("base color (Albedo map)");
            if (m.GetSlot(TextureMapType.Metallic)?.IsAssigned == true) driven.Add("metallic");
            if (m.GetSlot(TextureMapType.Roughness)?.IsAssigned == true) driven.Add("roughness");
            if (m.GetSlot(TextureMapType.AmbientOcclusion)?.IsAssigned == true) driven.Add("AO");
            if (driven.Count > 0)
                _props.Children.Add(new TextBlock { Text = "Taken from texture maps while assigned: " + string.Join(", ", driven) + ".", FontSize = 11, Foreground = Ui.Brush("VxTextTertiaryBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

            _props.Children.Add(Ui.Header("Texture maps"));
            var autoBtn = Ui.Button("Auto-Detect Textures", () =>
            {
                _doc.AutoAssignTexturesForMaterial(m);
                AfterSlotChange(m);
                _status.Text = "Auto-detected textures for " + m.Name;
            }, "Fill this material's empty slots from the Texture Library by naming convention");
            autoBtn.HorizontalAlignment = HorizontalAlignment.Left;
            _props.Children.Add(autoBtn);
            _props.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 8) });

            foreach (var slot in m.Slots.ToList()) _props.Children.Add(SlotRow(m, slot));
            if (m.Slots.Count == 0)
                _props.Children.Add(Ui.Muted("This material has no texture maps from the import (base color / PBR only). Use “+ Add Map” to assign one."));

            var add = Ui.Button("+ Add Map", () => { }, "Add a slot for a standard map the model didn't ship with, or a custom one");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(0, 10, 0, 0);
            add.Click += (s, e) =>
            {
                var menu = new MenuFlyout();
                foreach (var t in ModelMaterial.StandardMapTypes)
                {
                    if (m.GetSlot(t) != null) continue;
                    var tt = t;
                    var mi = new MenuItem { Header = ModelDocument.MapName(t) };
                    mi.Click += (s2, e2) => { m.AddStandardSlot(tt); AfterSlotChange(m); };
                    menu.Items.Add(mi);
                }
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                var custom = new MenuItem { Header = "Custom…" };
                custom.Click += (s2, e2) => { m.AddCustomSlot("Custom_" + m.Slots.Count); AfterSlotChange(m); };
                menu.Items.Add(custom);
                menu.ShowAt(add);
            };
            _props.Children.Add(add);
        }

        /// <summary>The material's base colour as a full-width swatch (hex + alpha); click to pick (alpha = opacity).</summary>
        private Control ColorBar(ModelMaterial m)
        {
            var hex = new TextBlock { FontSize = 11.5, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var bar = new Border { Height = 34, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = Ui.Brush("VxControlBorderBrush"), Cursor = new Cursor(StandardCursorType.Hand), Child = hex, Margin = new Thickness(0, 0, 0, 6) };
            void Paint()
            {
                var c = Ui.ToColor(m.BaseColor);
                bar.Background = new SolidColorBrush(c);
                hex.Text = "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2") + (c.A < 255 ? "   α " + (c.A / 255f).ToString("0.##", CultureInfo.InvariantCulture) : "");
                double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
                hex.Foreground = lum > 0.55 ? Brushes.Black : Brushes.White;
            }
            Paint();
            var view = new ColorView { Color = Ui.ToColor(m.BaseColor), IsAlphaEnabled = true, IsAlphaVisible = true, Width = 330 };
            view.ColorChanged += (s, e) =>
            {
                var c = e.NewColor;
                m.BaseColor = new[] { c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f };
                Paint();
                SchedulePreview(m);
            };
            FlyoutBase.SetAttachedFlyout(bar, new Flyout { Content = view, Placement = PlacementMode.BottomEdgeAlignedLeft });
            bar.PointerPressed += (s, e) => FlyoutBase.ShowAttachedFlyout(bar);
            ToolTip.SetTip(bar, "Click to pick the base colour (alpha = opacity). An assigned albedo map replaces it when rendering.");
            return bar;
        }

        private static void ClearRefreshersOf(Control root)
        {
            // PropertyRows keeps a refresher per editor control; drop the ones of the panel being rebuilt (only ours)
            foreach (var k in Refreshers.Keys.ToList())
            {
                StyledElement e = k;
                while (e != null && !ReferenceEquals(e, root)) e = e.Parent;
                if (e != null) Refreshers.Remove(k);
            }
        }

        private Control SlotRow(ModelMaterial m, TextureSlot slot)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("60,*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
            var thumb = Ui.Thumb(slot.FileExists ? slot.FilePath : null, 52, 52, slot.IsAssigned ? (slot.FileExists ? null : "missing") : "Drop");
            ToolTip.SetTip(thumb, slot.IsAssigned ? slot.FilePath : "Drop a texture here (Finder or Asset Browser)");
            Ui.AcceptImageDrop(thumb, p => { slot.FilePath = p; AfterSlotChange(m); });
            g.Children.Add(thumb);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
            info.Children.Add(new TextBlock { Text = slot.DisplayName });
            info.Children.Add(new TextBlock { Text = slot.StatusText, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = slot.IsAssigned && !slot.FileExists ? Ui.Brush("VxRedBrush") : Ui.Brush("VxTextSecondaryBrush") });
            Grid.SetColumn(info, 1); g.Children.Add(info);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(Ui.Button("Browse", async () =>
            {
                try
                {
                    var picked = await Ui.PickImage(this, "Select " + slot.DisplayName + " Texture", _doc?.Directory);
                    if (!string.IsNullOrEmpty(picked) && _doc != null) { slot.FilePath = picked; AfterSlotChange(m); }
                }
                catch (Exception ex) { EditorCommands.Fail("Browse", ex); }
            }, "Choose a texture file"));
            if (slot.IsAssigned)
            {
                var path = slot.FilePath;
                buttons.Children.Add(Ui.Button("Edit", () => OpenTextureInEditor(path), "Open in the Texture Editor"));
                buttons.Children.Add(Ui.IconButton("Close", "Clear", () => { slot.Clear(); AfterSlotChange(m); }));
            }
            Grid.SetColumn(buttons, 2); g.Children.Add(buttons);
            return g;
        }

        private void AfterSlotChange(ModelMaterial m)
        {
            UpdatePropertiesPanel();
            UpdateStats();
            RefreshAssigned();
            SchedulePreview(m);
        }

        private void OpenTextureInEditor(string texturePath)
        {
            if (string.IsNullOrEmpty(texturePath)) return;
            string full = Path.IsPathRooted(texturePath) ? texturePath : Path.Combine(_doc.Directory, texturePath);
            if (!File.Exists(full)) { _ = Dialogs.Alert("Texture Editor", "Texture file not found:\n" + full); return; }
            EditorWindows.TextureEditor(full);
        }

        /// <summary>Jump to the full Material Editor for the selected material (saving first when its .vmat does not exist
        /// yet — or belongs to another model sharing this folder).</summary>
        private async void OpenSelectedMaterialInEditor()
        {
            try
            {
                if (_selectedMaterial == null || _doc == null) return;
                string vmat = _doc.ResolveMaterialVmatPath(_selectedMaterial);
                if (string.IsNullOrEmpty(vmat) || !File.Exists(vmat) || ForeignSidecars().Contains(vmat, StringComparer.OrdinalIgnoreCase))
                {
                    if (!await SaveMaterialsInteractive()) return;
                    vmat = _doc?.ResolveMaterialVmatPath(_selectedMaterial);
                }
                if (!string.IsNullOrEmpty(vmat) && File.Exists(vmat))
                {
                    _knownVmatTimes[vmat] = MTime(vmat);
                    EditorWindows.MaterialEditor(vmat);   // its saves come back here through the sidecar watch (live preview update)
                }
                else _ = Dialogs.Alert("Material Editor", "Save the model's materials first (Save Materials).");
            }
            catch (Exception ex) { EditorCommands.Fail("Material Editor", ex); }
        }

        /// <summary>Sidecars this save would overwrite that belong to ANOTHER model in the same folder (their stored name
        /// differs) — the sidecar convention is per folder.</summary>
        private List<string> ForeignSidecars()
        {
            var list = new List<string>();
            if (_doc == null || _doc.SiblingModelCount == 0) return list;
            for (int i = 0; i < _doc.Submeshes.Count; i++)
            {
                var p = _doc.SidecarPath(i);
                if (!File.Exists(p)) continue;
                string name = null;
                try { name = VortexMaterial.Load(p)?.Name; } catch { }
                var m = _doc.MaterialOf(_doc.Submeshes[i]);
                if (m != null && !string.Equals(name, m.Name, StringComparison.OrdinalIgnoreCase)) list.Add(p);
            }
            return list;
        }

        /// <summary>Save Materials (button / ⌘S): asks before replacing sidecars of another model sharing this folder.</summary>
        private async Task<bool> SaveMaterialsInteractive()
        {
            var foreign = ForeignSidecars();
            if (foreign.Count > 0 && !await Dialogs.Confirm("Overwrite another model's materials?",
                    foreign.Count + " material file(s) in " + Ui.ProjectRelative(_doc.MaterialsDirectory) + " belong to another model in this folder (e.g. " + Path.GetFileName(foreign[0]) +
                    "). Saving replaces them — that model will then render with these materials. Import each model into its own folder to keep them apart.",
                    "Save anyway", "Cancel", destructive: true))
                return false;
            SaveMaterials();
            return true;
        }

        // ================================================================== texture library / assigned

        private void FillLibrary()
        {
            _library.Children.Clear();
            _libraryHeader.Text = "Textures from: " + Ui.ProjectRelative(_doc.Directory);
            _libraryCount.Text = _doc.DiscoveredTextures.Count + " texture(s) found";
            if (_doc.DiscoveredTextures.Count == 0) { _library.Children.Add(Ui.Muted("No textures in or around the model's folder.")); return; }
            foreach (var t in _doc.DiscoveredTextures)
            {
                var sp = new StackPanel { Spacing = 2 };
                var thumb = Ui.Thumb(t.FilePath, 112, 90);
                thumb.Width = double.NaN;
                sp.Children.Add(thumb);
                var name = new TextBlock { Text = t.FileName, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
                ToolTip.SetTip(name, t.FilePath);
                sp.Children.Add(name);
                sp.Children.Add(new TextBlock { Text = ModelDocument.MapName(t.DetectedType), FontSize = 10, Foreground = Ui.Brush("VxAccentBrush") });
                sp.Children.Add(new TextBlock { Text = t.FileSizeText, FontSize = 10, Foreground = Ui.Brush("VxTextTertiaryBrush") });
                var card = new Border { Width = 130, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(8), CornerRadius = new CornerRadius(6), Background = Ui.Brush("VxPanelRaisedBrush"), BorderBrush = Ui.Brush("VxHairlineBrush"), BorderThickness = new Thickness(1), Child = sp, Cursor = new Cursor(StandardCursorType.Hand) };
                ToolTip.SetTip(card, "Drag onto a texture slot · double-click: Texture Editor");
                var path = t.FilePath;
                Point? press = null;
                card.PointerPressed += (s, e) => { if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) press = e.GetPosition(card); if (e.ClickCount == 2) { press = null; OpenTextureInEditor(path); } };
                card.PointerMoved += (s, e) =>
                {
                    if (press == null || !e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
                    var d = e.GetPosition(card) - press.Value;
                    if (Math.Abs(d.X) + Math.Abs(d.Y) < 6) return;
                    press = null;
                    Ui.BeginFileDrag(e, path);
                };
                card.PointerReleased += (s, e) => press = null;
                _library.Children.Add(card);
            }
        }

        private void RefreshAssigned()
        {
            _assigned.Children.Clear();
            _assigned.Children.Add(new TextBlock { Text = "All Assigned Textures", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
            var wrap = new WrapPanel();
            if (_doc != null)
                foreach (var m in _doc.Materials)
                    foreach (var slot in m.Slots.Where(s => s.IsAssigned))
                    {
                        var sp = new StackPanel { Spacing = 2 };
                        var thumb = Ui.Thumb(slot.FileExists ? slot.FilePath : null, 142, 100, slot.FileExists ? null : "missing");
                        thumb.Width = double.NaN;
                        sp.Children.Add(thumb);
                        sp.Children.Add(new TextBlock { Text = m.Name + " - " + slot.DisplayName, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) });
                        sp.Children.Add(new TextBlock { Text = slot.FileName, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = slot.FileExists ? Ui.Brush("VxTextSecondaryBrush") : Ui.Brush("VxRedBrush") });
                        var card = new Border { Width = 160, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(8), CornerRadius = new CornerRadius(6), Background = Ui.Brush("VxPanelRaisedBrush"), BorderBrush = Ui.Brush("VxHairlineBrush"), BorderThickness = new Thickness(1), Child = sp };
                        ToolTip.SetTip(card, slot.FilePath);
                        wrap.Children.Add(card);
                    }
            if (wrap.Children.Count == 0) _assigned.Children.Add(Ui.Muted("No textures assigned yet.\nUse Auto-Assign or drag textures from the Texture Library."));
            else _assigned.Children.Add(wrap);
        }

        // ================================================================== animations (skeleton + clips)

        private async Task LoadAnimationsAsync()
        {
            if (_animationsLoaded || _doc == null) return;
            _animationsLoaded = true;
            _animations.Children.Clear();
            _animations.Children.Add(Ui.Muted("Reading skeleton and clips…"));
            var info = await _doc.LoadAnimationInfoAsync();
            _animations.Children.Clear();
            _animations.Children.Add(new TextBlock { Text = "Skeleton & Animation", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            _animations.Children.Add(Ui.Small(info.Bones > 0 ? info.Bones + " bones (" + info.SkeletonNodes + " skeleton nodes) — skinned model" : info.SkeletonNodes > 0 ? info.SkeletonNodes + " nodes, no skinned bones" : "No skeleton"));

            _animations.Children.Add(Ui.Header("Embedded clips (" + info.Clips.Count + ")"));
            if (info.Clips.Count == 0) _animations.Children.Add(Ui.Muted("This model file embeds no animation clips."));
            foreach (var c in info.Clips)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 1) };
                g.Children.Add(new TextBlock { Text = c.Name, TextTrimming = TextTrimming.CharacterEllipsis });
                var d = Ui.Small(c.DurationSec.ToString("0.00", CultureInfo.InvariantCulture) + " s"); Grid.SetColumn(d, 1); g.Children.Add(d);
                _animations.Children.Add(g);
            }

            _animations.Children.Add(Ui.Header("Extracted .vanim clips (" + info.ExtractedClips.Count + ")"));
            if (info.ExtractedClips.Count == 0) _animations.Children.Add(Ui.Muted(info.Clips.Count > 0 ? "Not extracted yet — extract them to edit clips in the Animation Editor and ship them by name." : "None."));
            foreach (var f in info.ExtractedClips)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 1) };
                g.Children.Add(new TextBlock { Text = Path.GetFileName(f), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                var path = f;
                var open = Ui.Button("Open", () => EditorWindows.AnimationEditor(path), "Open in the Animation Editor", "ghost");
                Grid.SetColumn(open, 1); g.Children.Add(open);
                _animations.Children.Add(g);
            }
            if (info.Clips.Count > 0)
            {
                var extract = Ui.Button(info.ExtractedClips.Count > 0 ? "Re-extract clips" : "Extract clips to .vanim", () =>
                {
                    int n = ModelImportPipeline.ExtractClips(_path);
                    try { AssetDatabase.Instance.Refresh(); } catch { }
                    _status.Text = "Extracted " + n + " clip(s) to " + Ui.ProjectRelative(_doc.AnimationsDirectory);
                    _animationsLoaded = false; _ = LoadAnimationsAsync();
                }, "Write each embedded clip as animations/<clip>.vanim next to the model");
                extract.HorizontalAlignment = HorizontalAlignment.Left;
                extract.Margin = new Thickness(0, 10, 0, 0);
                _animations.Children.Add(extract);
            }
        }

        // ================================================================== actions

        private void AutoAssignAll()
        {
            if (_doc == null) return;
            _doc.AutoAssignTextures();
            foreach (var m in _doc.Materials) SchedulePreview(m);
            UpdatePropertiesPanel(); UpdateStats(); RefreshAssigned();
            _status.Text = "Auto-assigned textures to all materials";
        }

        private void ClearAllTextures()
        {
            if (_doc == null) return;
            foreach (var m in _doc.Materials) { foreach (var s in m.Slots) s.Clear(); SchedulePreview(m); }
            UpdatePropertiesPanel(); UpdateStats(); RefreshAssigned();
            _status.Text = "Cleared all texture assignments";
        }

        private void SaveMaterials()
        {
            if (_doc == null) return;
            try
            {
                var saved = _doc.SaveMaterials();
                foreach (var p in saved) _knownVmatTimes[p] = MTime(p);
                try { AssetDatabase.Instance.Refresh(); } catch { }
                PropagateMaterialsToScene();
                // same refresh recipe as the Material Editor's save: rebuild cached engine materials from disk, bust the
                // thumbnails, re-submit the scene, refresh this preview.
                foreach (var p in saved) { try { MaterialService.Instance.InvalidateVortexMaterial(p); } catch { } ThumbnailService.Invalidate(p); }
                InvalidateModelThumbnail();
                SceneRenderService.RuntimeDirty = true;
                try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
                RebuildAllPreviewMaterials();
                if (_selectedMaterial != null) UpdatePropertiesPanel();
                _format.Text = _doc.FormatName + "  ·  saved materials";
                _status.Foreground = Ui.Brush("VxTextSecondaryBrush");
                _status.Text = "Saved " + saved.Count + " material(s) to " + Ui.ProjectRelative(_doc.MaterialsDirectory);
            }
            catch (Exception ex)
            {
                _status.Foreground = Ui.Brush("VxRedBrush");
                _status.Text = "Error saving materials: " + ex.Message;
                _ = Dialogs.Alert("Save Materials", "Failed to save materials:\n" + ex.Message);
            }
        }

        /// <summary>The Asset Browser's disk thumbnail cache is keyed by the model file's timestamp: bump it so the tile is
        /// re-rendered with the saved materials (memory entries are dropped too).</summary>
        private void InvalidateModelThumbnail()
        {
            try { File.SetLastWriteTimeUtc(_path, DateTime.UtcNow); } catch { }
            ThumbnailService.Invalidate(_path);
        }

        /// <summary>Push the edited values + maps onto every live material of this model's submeshes (placed instances,
        /// prefab-placed ones too) — mutating in place, a no-op when the model is not in the scene.</summary>
        private void PropagateMaterialsToScene()
        {
            for (int i = 0; i < _doc.Submeshes.Count; i++)
            {
                var m = _doc.MaterialOf(_doc.Submeshes[i]);
                if (m == null) continue;
                var c = m.BaseColor;
                SceneRenderService.ApplyToLiveMaterialsForModel(_path, i, liveId =>
                {
                    VortexAPI.SetMaterialBaseColor(liveId, c[0], c[1], c[2], c.Length > 3 ? c[3] : 1f);
                    VortexAPI.SetMaterialMetallicValue(liveId, m.Metallic);
                    VortexAPI.SetMaterialRoughnessValue(liveId, m.Roughness);
                    VortexAPI.SetMaterialAOValue(liveId, m.AOStrength);
                    VortexAPI.SetMaterialNormalStrengthValue(liveId, m.NormalStrength);
                    BindLiveMap(liveId, m.GetTexture(TextureMapType.Albedo), VortexAPI.SetMaterialAlbedoTexture);
                    BindLiveMap(liveId, m.GetTexture(TextureMapType.Normal), VortexAPI.SetMaterialNormalMap);
                    BindLiveMap(liveId, m.GetTexture(TextureMapType.Metallic), VortexAPI.SetMaterialMetallicMap);
                    BindLiveMap(liveId, m.GetTexture(TextureMapType.Roughness), VortexAPI.SetMaterialRoughnessMap);
                    BindLiveMap(liveId, m.GetTexture(TextureMapType.AmbientOcclusion), VortexAPI.SetMaterialAOMap);
                    MaterialService.ApplyTextureChannels(liveId, null, m.GetTexture(TextureMapType.Metallic), m.GetTexture(TextureMapType.Roughness));
                });
            }
        }

        private void BindLiveMap(long materialId, string texturePath, Action<long, long> setter)
        {
            if (string.IsNullOrEmpty(texturePath)) return;
            string full = Path.IsPathRooted(texturePath) ? texturePath : Path.Combine(_doc.Directory, texturePath);
            if (!File.Exists(full)) return;
            long tex = MaterialService.ImportTextureCached(full);
            if (tex >= 0) setter(materialId, tex);
        }

        // ================================================================== live reload (Material Editor saves)

        private static DateTime MTime(string p) { try { return File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue; } catch { return DateTime.MinValue; } }

        private void PollSidecars()
        {
            if (_doc == null) return;
            foreach (var m in _doc.Materials)
            {
                var p = _doc.ResolveMaterialVmatPath(m);
                if (p == null || !File.Exists(p)) continue;
                var t = MTime(p);
                if (!_knownVmatTimes.TryGetValue(p, out var known)) { _knownVmatTimes[p] = t; continue; }
                if (t <= known) continue;
                _knownVmatTimes[p] = t;
                if (_doc.ReloadMaterialFromVmat(m, p))
                {
                    SchedulePreview(m);
                    if (ReferenceEquals(m, _selectedMaterial)) UpdatePropertiesPanel();
                    RefreshAssigned(); UpdateStats();
                    _status.Text = "Reloaded " + m.Name + " from " + Path.GetFileName(p) + " (saved in the Material Editor)";
                }
            }
        }

        // ================================================================== teardown

        private void Release()
        {
            _watch?.Stop();
            _previewDebounce?.Stop();
            _preview.Scene = null;
            Ui.DropQueuedDraws();   // no queued draw may still point at the meshes freed below
            foreach (var id in _engineMaterials.Values) if (id >= 0) { try { VortexAPI.DeleteMaterial(id); } catch { } }
            _engineMaterials.Clear();
            _doc?.Dispose();
            _doc = null;
            ClearRefreshersOf(_props);
            _ready.TrySetResult(false);
        }

        // ================================================================== test hooks

        /// <summary>Select a submesh / material by index (tests).</summary>
        internal void SelectSubmesh(int i) { if (i >= 0 && i < _submeshList.ItemCount) _submeshList.SelectedIndex = i; }
        internal void SelectMaterial(int i) { if (i >= 0 && i < _materialList.ItemCount) _materialList.SelectedIndex = i; }
        internal void SetIsolate(bool on) { if (_isolate != null) _isolate.IsChecked = on; }
        internal ModelMaterial SelectedMaterial => _selectedMaterial;
        internal void ApplyEdit(Action<ModelMaterial> edit) { if (_selectedMaterial == null) return; edit(_selectedMaterial); SchedulePreview(_selectedMaterial); FlushPreview(); UpdatePropertiesPanel(); }
        internal void Save() => SaveMaterials();
    }
}
