using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.Services.Terrain;
using Editor.Core.Terrain;
using Editor.ECS;
using VortexEditor.Panels.Inspector;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Shell
{
    /// <summary>
    /// The Terrain Editor (Window ▸ Terrain Editor…, the viewport's globe button): the whole terrain workflow in one big
    /// window, in the engine's own UI. Left, the tool tabs — Sculpt (raise, lower, smooth, flatten, noise), Paint (the four
    /// layers), Generate (landscapes from noise, erosion, smoothing, normalising), Heightmap (import / export), Layers (the
    /// four materials and their tiling), Settings (size, resolution, LOD, collision, navigation, data file). In the middle,
    /// the map: the terrain from above as a shaded relief or as its layers, and you paint straight onto it with the mouse —
    /// every stroke lands in the 3D viewport at once (the chunks rebuild where you touched them). Right, what is under the
    /// cursor and the terrain's facts. The brush settings are shared with the 3D brush of the viewport, so a stroke in the
    /// viewport and a stroke on the map are the same tool. Every stroke and every global operation is one undo step.
    /// </summary>
    public sealed class TerrainEditorWindow : Window
    {
        private static TerrainEditorWindow _open;
        public static void Open(GameEntity prefer = null)
        {
            if (_open != null) { _open.Activate(); if (prefer != null) _open.Rebuild(prefer); return; }
            _open = new TerrainEditorWindow();
            _open.Closed += (s, e) => _open = null;
            EditorWindows.Show(_open);
            if (prefer != null) _open.Rebuild(prefer);
        }
        public static TerrainEditorWindow Current => _open;

        private enum MapTool { Raise, Lower, Smooth, Flatten, Noise, Paint }
        private static readonly string[] ToolNames = { "Raise", "Lower", "Smooth", "Flatten", "Noise", "Paint" };
        private static readonly string[] LayerNames = { "Layer 1", "Layer 2", "Layer 3", "Layer 4" };
        private static readonly Color[] LayerColors = { Avalonia.Media.Color.FromRgb(214, 178, 110), Avalonia.Media.Color.FromRgb(140, 110, 80), Avalonia.Media.Color.FromRgb(120, 120, 120), Avalonia.Media.Color.FromRgb(96, 150, 70) };

        // ---- state ----
        private readonly List<GameEntity> _list = new List<GameEntity>();
        private GameEntity _entity;
        private Editor.ECS.Components.Rendering.Terrain _terrain;
        private MapTool _tool = MapTool.Raise;
        private int _paintLayer;
        private float _noiseScale = 24f;
        private bool _showLayers;
        private int _mapN = 512;
        private WriteableBitmap _mapBitmap;
        private bool _mapDirty = true, _stroking;
        private float _flattenTarget;
        private float[] _strokeHeights; private byte[] _strokeSplat; private SampleRect _strokeRect = SampleRect.Empty;
        private double _lastStrokeX = -1e9, _lastStrokeZ = -1e9;
        private readonly DispatcherTimer _redraw;
        private int _genPreset = 1; private float _genAmp = 40f, _genWave = 120f, _genBase; private int _genSeed = 7;
        private float _importLow, _importHigh = 100f;
        private int _erodeIterations = 30; private float _normLow = 0f, _normHigh = 60f;

        // ---- controls ----
        private readonly ComboBox _terrains = new ComboBox { MinWidth = 200 };
        private readonly Image _map = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Ellipse _brush = new Ellipse { Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false, IsVisible = false, Opacity = 0.9 };
        private readonly Ellipse _brushInner = new Ellipse { Stroke = Brushes.Black, StrokeThickness = 1, IsHitTestVisible = false, IsVisible = false, Opacity = 0.5 };
        private readonly Canvas _overlay = new Canvas { IsHitTestVisible = false };
        private readonly Grid _mapHost = new Grid { Background = new SolidColorBrush(Avalonia.Media.Color.FromRgb(24, 26, 30)), Cursor = new Cursor(StandardCursorType.Cross) };
        private readonly TextBlock _cursorInfo = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _facts = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        private readonly TextBlock _status = new TextBlock { FontSize = 11, Opacity = 0.75, Margin = new Thickness(12, 4, 12, 6) };
        private readonly StackPanel _sculptPanel = new StackPanel { Spacing = 2 }, _paintPanel = new StackPanel { Spacing = 2 }, _generatePanel = new StackPanel { Spacing = 2 };
        private readonly StackPanel _heightmapPanel = new StackPanel { Spacing = 2 }, _layersPanel = new StackPanel { Spacing = 2 }, _settingsPanel = new StackPanel { Spacing = 2 };
        private readonly TabControl _tabs = new TabControl();
        private readonly Button _undo = new Button { Content = "Undo" }, _redo = new Button { Content = "Redo" };
        private bool _syncing;

        private TerrainEditorWindow()
        {
            Title = "Terrain Editor";
            Width = 1240; Height = 860; MinWidth = 900; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

            // ---- top bar ----
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 10, 12, 6) };
            top.Children.Add(new TextBlock { Text = "Terrain", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
            top.Children.Add(_terrains);
            var create = new Button { Content = "＋ New" }; ToolTip.SetTip(create, "A flat 64 m terrain at the origin");
            create.Click += (s, e) => { try { var ent = EditorCommands.CreateTerrain(); Rebuild(ent); } catch (Exception ex) { EditorCommands.Fail("Create terrain", ex); } };
            var fromHm = new Button { Content = "From Heightmap…" }; ToolTip.SetTip(fromHm, "A terrain shaped by a PNG (8 / 16 bit) or RAW 16 picture");
            fromHm.Click += async (s, e) => { await EditorCommands.CreateTerrainFromHeightmap(); Rebuild(SelectionService.Instance.SelectedEntity); };
            var save = new Button { Content = "Save" }; ToolTip.SetTip(save, "Write the heights and the splat map to the data file now (strokes save by themselves)");
            save.Click += (s, e) => { if (_entity != null) { TerrainService.Save(_entity); Status("Saved " + _terrain?.DataPath); } };
            _undo.Click += (s, e) => { try { Editor.Core.UndoRedo.UndoRedoManager.Instance.Undo(); Invalidate(); } catch (Exception ex) { EditorCommands.Fail("Undo", ex); } };
            _redo.Click += (s, e) => { try { Editor.Core.UndoRedo.UndoRedoManager.Instance.Redo(); Invalidate(); } catch (Exception ex) { EditorCommands.Fail("Redo", ex); } };
            var view = new ComboBox { MinWidth = 120 }; view.Items.Add("Map: heights"); view.Items.Add("Map: layers"); view.SelectedIndex = 0;
            view.SelectionChanged += (s, e) => { _showLayers = view.SelectedIndex == 1; _mapDirty = true; };
            var frame = new Button { Content = "Frame in viewport" }; ToolTip.SetTip(frame, "Point the 3D viewport's camera at this terrain");
            frame.Click += (s, e) => { if (_entity != null) { SelectionService.Instance.SelectedEntity = _entity; SelectionService.Instance.RequestFocus(_entity); } };
            foreach (var c in new Control[] { create, fromHm, new Rectangle { Width = 1, Height = 20, Fill = Brushes.Gray, Opacity = 0.4 }, save, _undo, _redo, new Rectangle { Width = 1, Height = 20, Fill = Brushes.Gray, Opacity = 0.4 }, view, frame }) top.Children.Add(c);
            _terrains.SelectionChanged += (s, e) => { if (_syncing) return; int i = _terrains.SelectedIndex; if (i >= 0 && i < _list.Count) { SelectionService.Instance.SelectedEntity = _list[i]; Show(_list[i]); } };

            // ---- tool tabs ----
            _tabs.Items.Add(Tab("Sculpt", _sculptPanel));
            _tabs.Items.Add(Tab("Paint", _paintPanel));
            _tabs.Items.Add(Tab("Generate", _generatePanel));
            _tabs.Items.Add(Tab("Heightmap", _heightmapPanel));
            _tabs.Items.Add(Tab("Layers", _layersPanel));
            _tabs.Items.Add(Tab("Settings", _settingsPanel));
            _tabs.SelectionChanged += (s, e) =>
            {
                // the Paint tab paints, the Sculpt tab sculpts — the map's tool follows the tab
                if (_tabs.SelectedIndex == 1 && _tool != MapTool.Paint) SetTool(MapTool.Paint);
                else if (_tabs.SelectedIndex == 0 && _tool == MapTool.Paint) SetTool(MapTool.Raise);
            };
            var left = new Border { Width = 300, Child = new ScrollViewer { Content = _tabs, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, Margin = new Thickness(12, 0, 6, 0) };

            // ---- the map ----
            _overlay.Children.Add(_brush); _overlay.Children.Add(_brushInner);
            _mapHost.Children.Add(_map); _mapHost.Children.Add(_overlay);
            _mapHost.PointerPressed += OnMapPressed; _mapHost.PointerMoved += OnMapMoved; _mapHost.PointerReleased += OnMapReleased; _mapHost.PointerExited += (s, e) => { _brush.IsVisible = _brushInner.IsVisible = false; };
            var centre = new Border { Child = _mapHost, Margin = new Thickness(0, 0, 6, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };

            // ---- right: info ----
            var right = new StackPanel { Width = 230, Spacing = 8, Margin = new Thickness(6, 0, 12, 0) };
            right.Children.Add(new TextBlock { Text = "Under the cursor", FontWeight = FontWeight.SemiBold });
            right.Children.Add(_cursorInfo);
            right.Children.Add(new TextBlock { Text = "Terrain", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            right.Children.Add(_facts);
            right.Children.Add(new TextBlock { Text = "How to", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            right.Children.Add(new TextBlock { FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Text = "Drag on the map to sculpt or paint. Shift inverts raise / lower, Ctrl smooths. The same brush works in the 3D viewport while this window is open. Everything is undoable (Cmd/Ctrl+Z in the main window)." });

            var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            Grid.SetColumn(left, 0); Grid.SetColumn(centre, 1); Grid.SetColumn(right, 2);
            body.Children.Add(left); body.Children.Add(centre); body.Children.Add(right);

            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(top); root.Children.Add(_status); root.Children.Add(body);
            Content = root;

            _redraw = new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Background, (s, e) => { if (_mapDirty) RedrawMap(); try { PropertyRows.RefreshAll(); } catch { } });
            _redraw.Start();
            SelectionService.Instance.SelectionChanged += OnSelectionChanged;
            EditorSession.Instance.ProjectOpened += OnProjectChanged;
            EditorSession.Instance.ProjectClosed += OnProjectClosed;
            TerrainService.Changed += OnTerrainChanged;
            Closed += (s, e) =>
            {
                _redraw.Stop();
                SelectionService.Instance.SelectionChanged -= OnSelectionChanged;
                EditorSession.Instance.ProjectOpened -= OnProjectChanged;
                EditorSession.Instance.ProjectClosed -= OnProjectClosed;
                TerrainService.Changed -= OnTerrainChanged;
                try { if (TerrainToolService.Tool != TerrainTool.None) TerrainToolService.SetTool(TerrainTool.None); } catch { }
            };
            Rebuild(SelectionService.Instance.SelectedEntity);
        }

        private static TabItem Tab(string header, Control content) => new TabItem { Header = header, Content = content };
        private void Status(string s) => _status.Text = s;

        private void OnProjectChanged(ProjectData p) => Dispatcher.UIThread.Post(() => Rebuild(null));
        private void OnProjectClosed() => Dispatcher.UIThread.Post(() => Rebuild(null));
        private void OnTerrainChanged(GameEntity e) { if (ReferenceEquals(e, _entity)) _mapDirty = true; }
        private void OnSelectionChanged(object s, SelectionEventArgs e)
        {
            var sel = SelectionService.Instance.SelectedEntity;
            if (sel != null && sel.GetComponent<Editor.ECS.Components.Rendering.Terrain>() != null && !ReferenceEquals(sel, _entity)) Dispatcher.UIThread.Post(() => Rebuild(sel));
        }

        // ================================================================ terrains

        private void Rebuild(GameEntity prefer)
        {
            _list.Clear();
            var scene = ProjectData.Current?.ActiveScene;
            if (scene != null)
                foreach (var e in Panels.AssetBrowser.WorldThumbs.Walk(scene))
                    if (e.GetComponent<Editor.ECS.Components.Rendering.Terrain>() != null) _list.Add(e);
            _syncing = true;
            _terrains.Items.Clear();
            foreach (var e in _list) _terrains.Items.Add(e.Name);
            _syncing = false;
            var show = prefer != null && _list.Contains(prefer) ? prefer : (_entity != null && _list.Contains(_entity) ? _entity : _list.FirstOrDefault());
            Show(show);
        }

        private void Show(GameEntity e)
        {
            _entity = e; _terrain = e?.GetComponent<Editor.ECS.Components.Rendering.Terrain>();
            _syncing = true; _terrains.SelectedIndex = e != null ? _list.IndexOf(e) : -1; _syncing = false;
            PropertyRows.ClearRefreshers();
            BuildSculpt(); BuildPaint(); BuildGenerate(); BuildHeightmap(); BuildLayers(); BuildSettings();
            _mapBitmap = null; _mapDirty = true;
            UpdateFacts();
            Status(e == null ? "No terrain in this scene — ＋ New makes a flat one, From Heightmap… shapes one from a picture." : "Editing " + e.Name);
        }

        private bool TryData(out TerrainData data, out float cell)
        {
            data = null; cell = 1f;
            return _entity != null && TerrainService.TryGetData(_entity, out data, out cell);
        }

        // ================================================================ tool panels

        private void SetTool(MapTool t)
        {
            _tool = t;
            // the 3D brush follows: a stroke in the viewport uses the same tool
            try
            {
                TerrainToolService.SetTool(t == MapTool.Paint ? TerrainTool.Paint : t == MapTool.Smooth ? TerrainTool.Smooth : t == MapTool.Flatten ? TerrainTool.Flatten : t == MapTool.Lower ? TerrainTool.Lower : TerrainTool.Raise);
            }
            catch { }
            PropertyRows.RefreshAll();
        }

        private void BuildSculpt()
        {
            var p = _sculptPanel; p.Children.Clear();
            p.Children.Add(Hint("Drag on the map or in the 3D viewport. The brush is shared between both."));
            p.Children.Add(Row("Tool", Choice(() => (int)_tool == 5 ? 0 : (int)_tool, v => SetTool((MapTool)Math.Min(4, v)), "Raise", "Lower", "Smooth", "Flatten", "Noise"), "Raise / Lower the ground, Smooth it, Flatten to a height, or add Noise (rough ground under the brush)"));
            p.Children.Add(Row("Radius", SliderRow(() => TerrainToolService.Radius, v => TerrainToolService.Radius = v, 0.5, 120, "0.0 m"), "Brush radius in metres"));
            p.Children.Add(Row("Strength", SliderRow(() => TerrainToolService.Strength, v => TerrainToolService.Strength = v, 0.02, 1, "0.00"), "How much one stroke step changes"));
            p.Children.Add(Row("Hardness", SliderRow(() => TerrainToolService.Hardness, v => TerrainToolService.Hardness = v, 0, 0.95, "0.00"), "0 a soft dome, 0.9 almost a stamp"));
            p.Children.Add(Row("Flatten to", FlattenRow(), "Flatten pulls the ground to the height where the stroke starts, or to this fixed world height"));
            p.Children.Add(Row("Noise scale", SliderRow(() => _noiseScale, v => _noiseScale = v, 2, 200, "0 m"), "Feature size of the Noise tool in metres"));
            p.Children.Add(Section("Whole terrain"));
            p.Children.Add(Row("", Actions(
                Ghost("Smooth all", () => Global("Smooth terrain", d => SmoothAll(d, 2)), "Two passes of a 3×3 smoothing over every sample"),
                Ghost("Level", () => Global("Level terrain", d => Array.Clear(d.Heights, 0, d.Heights.Length)), "Every height to 0"))));
        }

        private void BuildPaint()
        {
            var p = _paintPanel; p.Children.Clear();
            p.Children.Add(Hint("Paint one of the four layers onto the terrain. The layers' materials are set on the Layers tab."));
            p.Children.Add(Row("Layer", Choice(() => _paintLayer, v => { _paintLayer = v; TerrainToolService.PaintLayer = v; }, LayerLabels()), "The layer the brush paints"));
            p.Children.Add(Row("Radius", SliderRow(() => TerrainToolService.Radius, v => TerrainToolService.Radius = v, 0.5, 120, "0.0 m")));
            p.Children.Add(Row("Strength", SliderRow(() => TerrainToolService.Strength, v => TerrainToolService.Strength = v, 0.02, 1, "0.00"), "Opacity of one stroke step"));
            p.Children.Add(Row("Hardness", SliderRow(() => TerrainToolService.Hardness, v => TerrainToolService.Hardness = v, 0, 0.95, "0.00")));
            p.Children.Add(Section("Whole terrain"));
            p.Children.Add(Row("", Actions(
                Ghost("Fill with this layer", () => Global("Fill layer", d => { for (int i = 0; i < d.Resolution * d.Resolution; i++) { d.Splat[i * 4] = 0; d.Splat[i * 4 + 1] = 0; d.Splat[i * 4 + 2] = 0; d.Splat[i * 4 + 3] = 0; d.Splat[i * 4 + _paintLayer] = 255; } }, splat: true), "Every sample becomes this layer"),
                Ghost("By slope", () => Global("Paint by slope", PaintBySlope, splat: true), "Layer 1 on flat ground, layer 3 on steep slopes, layer 2 between — a quick start"))));
        }

        private string[] LayerLabels()
        {
            var r = new string[4];
            for (int i = 0; i < 4; i++)
            {
                string m = _terrain?.LayerMaterial(i);
                r[i] = LayerNames[i] + (string.IsNullOrEmpty(m) ? "" : " · " + System.IO.Path.GetFileNameWithoutExtension(m));
            }
            return r;
        }

        private void BuildGenerate()
        {
            var p = _generatePanel; p.Children.Clear();
            p.Children.Add(Hint("A landscape from noise as a starting point — then sculpt. Replaces every height; the layers stay."));
            p.Children.Add(Row("Shape", Choice(() => _genPreset, v => _genPreset = v, "Flat", "Hills", "Mountains", "Dunes", "Island", "Canyon")));
            p.Children.Add(Row("Amplitude", FloatBox(() => _genAmp, v => _genAmp = Math.Max(0f, v), 1, 0f, 4000f, "0.#"), "Height of the features (m)"));
            p.Children.Add(Row("Wavelength", FloatBox(() => _genWave, v => _genWave = Math.Max(1f, v), 4, 1f, 10000f, "0.#"), "Width of the features (m)"));
            p.Children.Add(Row("Base height", FloatBox(() => _genBase, v => _genBase = v, 1, null, null, "0.#")));
            p.Children.Add(Row("Seed", IntBox(() => _genSeed, v => _genSeed = v, 0, 999999)));
            p.Children.Add(Row("", Actions(Ghost("Generate", () => { if (_terrain == null) return; var preset = (TerrainPreset)_genPreset; float size = _terrain.Size; Global("Generate terrain (" + preset + ")", d => TerrainGenerator.Generate(d, size, preset, _genAmp, _genWave, _genBase, _genSeed)); }))));
            p.Children.Add(Section("Erosion"));
            p.Children.Add(Row("Iterations", IntBox(() => _erodeIterations, v => _erodeIterations = Math.Max(1, Math.Min(500, v)), 1, 500), "Thermal erosion: sand slides off slopes steeper than ~35° until they rest — rounded ridges, filled hollows"));
            p.Children.Add(Row("", Actions(Ghost("Erode", () => Global("Erode terrain", d => ThermalErode(d, _erodeIterations, CellSize())), "Run the erosion over the whole terrain"))));
            p.Children.Add(Section("Normalise"));
            p.Children.Add(Row("Range", RangeRow(() => _normLow, v => _normLow = v, () => _normHigh, v => _normHigh = v), "Stretch the heights so the lowest sample sits at the first value and the highest at the second"));
            p.Children.Add(Row("", Actions(Ghost("Normalise", () => Global("Normalise terrain", d => Normalise(d, Math.Min(_normLow, _normHigh), Math.Max(_normLow, _normHigh)))))));
        }

        private void BuildHeightmap()
        {
            var p = _heightmapPanel; p.Children.Clear();
            p.Children.Add(Hint("Heightmaps from World Machine, Gaea, Blender or Photoshop: PNG 8 / 16 bit (grey or colour) or RAW 16-bit squares. Export writes a 16-bit PNG or RAW."));
            p.Children.Add(Row("Import range", RangeRow(() => _importLow, v => _importLow = v, () => _importHigh, v => _importHigh = v), "Black maps to the first height, white to the second (m)"));
            p.Children.Add(Row("", Actions(
                Ghost("Import heightmap…", () => _ = ImportHeightmap(), "Resampled to this terrain's resolution. One undo step"),
                Ghost("Export heightmap…", () => { if (_entity != null) _ = TerrainCard.ExportHeightmapFor(_entity); }, "Lowest height = black, highest = white"))));
        }

        private void BuildLayers()
        {
            var p = _layersPanel; p.Children.Clear();
            p.Children.Add(Hint("Four texture layers, a .vmat each: its albedo, normal and roughness maps tile across the terrain where the layer is painted. Tiling in metres per repeat."));
            if (_terrain == null) return;
            var t = _terrain;
            for (int i = 0; i < 4; i++)
            {
                int li = i;
                p.Children.Add(Section(LayerNames[li]));
                p.Children.Add(Row("Material", AssetPath(() => t.LayerMaterial(li), v => { t.SetLayerMaterial(li, string.IsNullOrEmpty(v) ? null : v.Replace('\\', '/')); Touch(); BuildPaint(); }, "Material", new[] { "*.vmat" },
                    () => AssetPickerDialog.Pick("Material", new[] { "*.vmat" }, TerrainService.ResolvePath(t.LayerMaterial(li)))), "Drop a .vmat from the Asset Browser or pick one"));
                p.Children.Add(Row("Tiling", FloatBox(() => t.LayerTile(li), v => { t.SetLayerTile(li, v); Touch(); }, 0.5, 0.1f, 1000f, "0.##"), "Metres per texture repeat"));
            }
        }

        private void BuildSettings()
        {
            var p = _settingsPanel; p.Children.Clear();
            if (_terrain == null) { p.Children.Add(Hint("No terrain selected.")); return; }
            var t = _terrain; var entity = _entity;
            var resolutions = new[] { 33, 65, 129, 257, 513, 1025, 2049 };
            p.Children.Add(Row("Size", FloatBox(() => t.Size, v => { t.Size = v; Touch(); UpdateFacts(); }, 1, 1f, 16384f, "0.#"), "Edge length in metres (square, from the entity's position along +X / +Z)"));
            p.Children.Add(Row("Resolution", Choice(() => Math.Max(0, Array.IndexOf(resolutions, t.Resolution)), i => { t.Resolution = resolutions[Math.Max(0, Math.Min(resolutions.Length - 1, i))]; Touch(); _mapBitmap = null; _mapDirty = true; UpdateFacts(); }, "33", "65", "129", "257", "513", "1025", "2049"), "Height samples per side; changing it resamples the sculpting. 513 on 512 m = one sample per metre"));
            p.Children.Add(Row("LOD distance", FloatBox(() => t.LodDistance, v => { t.LodDistance = v; Touch(); }, 4, 4f, null, "0"), "Metres at which chunks drop to half the vertices"));
            p.Children.Add(Row("Collision", Bool(() => t.Collision, v => { t.Collision = v; Touch(); }), "Jolt height field + the character's collision world, and navmesh ground"));
            p.Children.Add(Row("Navigation", Bool(() => t.Navigation, v => { t.Navigation = v; Touch(); }), "Count as navmesh ground (with Collision) — off for a far backdrop terrain"));
            p.Children.Add(Row("Position", new TextBlock { Text = entity?.Transform != null ? Fmt(entity.Transform.LocalPosition.X, "0.#") + ", " + Fmt(entity.Transform.LocalPosition.Y, "0.#") + ", " + Fmt(entity.Transform.LocalPosition.Z, "0.#") : "", VerticalAlignment = VerticalAlignment.Center }, "The corner of the terrain (move the entity in the viewport or the Inspector)"));
            p.Children.Add(Row("Data file", new TextBlock { Text = string.IsNullOrEmpty(t.DataPath) ? "(saved with the first stroke)" : t.DataPath, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, "The .vterrain with the heights and the splat map"));
        }

        private Control FlattenRow()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(FloatBox(() => TerrainToolService.FlattenHeight, v => TerrainToolService.FlattenHeight = v, 0.5, null, null, "0.##"));
            panel.Children.Add(Check("fixed", () => TerrainToolService.FlattenFixed, v => TerrainToolService.FlattenFixed = v, "Use the height above instead of the stroke's start"));
            return panel;
        }

        private static Control RangeRow(Func<float> getLow, Action<float> setLow, Func<float> getHigh, Action<float> setHigh)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(FloatBox(getLow, setLow, 1, null, null, "0.#"));
            panel.Children.Add(new TextBlock { Text = "…", VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(FloatBox(getHigh, setHigh, 1, null, null, "0.#"));
            panel.Children.Add(new TextBlock { Text = "m", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
            return panel;
        }

        private float CellSize() => _terrain != null && _terrain.Resolution > 1 ? _terrain.Size / (_terrain.Resolution - 1) : 1f;

        private void Touch()
        {
            ComponentEditors.Dirty();
            try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
            _mapDirty = true;
        }

        private void Invalidate()
        {
            if (_entity != null) TerrainService.Invalidate(_entity);
            _mapDirty = true;
            try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
        }

        // ================================================================ global operations (one undo step)

        private void Global(string label, Action<TerrainData> op, bool splat = false)
        {
            TerrainData data; float cell;
            if (!TryData(out data, out cell)) { Status("No terrain selected"); return; }
            var entity = _entity;
            var beforeH = (float[])data.Heights.Clone(); var beforeS = splat ? (byte[])data.Splat.Clone() : null;
            try { op(data); } catch (Exception ex) { EditorCommands.Fail(label, ex); return; }
            data.MarkChanged();
            var afterH = (float[])data.Heights.Clone(); var afterS = splat ? (byte[])data.Splat.Clone() : null;
            var all = new SampleRect { X0 = 0, Z0 = 0, X1 = data.Resolution - 1, Z1 = data.Resolution - 1 };
            void Put(float[] h, byte[] s)
            {
                Array.Copy(h, data.Heights, h.Length);
                if (s != null) Array.Copy(s, data.Splat, s.Length);
                data.MarkChanged();
                TerrainService.MarkDirty(entity, all, true, s != null);
                TerrainService.Save(entity);
                _mapDirty = true;
                try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
            }
            try { Editor.Core.UndoRedo.UndoRedoManager.Instance.Execute(new Editor.Core.UndoRedo.Commands.ActionCommand(label, () => Put(afterH, afterS), () => Put(beforeH, beforeS)), false); } catch { }
            Put(afterH, afterS);
            UpdateFacts();
            Status(label + " done");
        }

        private static void SmoothAll(TerrainData d, int passes)
        {
            int n = d.Resolution;
            var tmp = new float[n * n];
            for (int pass = 0; pass < passes; pass++)
            {
                for (int z = 0; z < n; z++)
                    for (int x = 0; x < n; x++)
                    {
                        float sum = 0f; int cnt = 0;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx, zz = z + dz;
                                if (xx < 0 || zz < 0 || xx >= n || zz >= n) continue;
                                sum += d.Heights[zz * n + xx]; cnt++;
                            }
                        tmp[z * n + x] = sum / cnt;
                    }
                Array.Copy(tmp, d.Heights, tmp.Length);
            }
        }

        /// <summary>Thermal erosion: material slides from a sample to its lower neighbours while the slope exceeds the talus angle.</summary>
        private static void ThermalErode(TerrainData d, int iterations, float cell)
        {
            int n = d.Resolution; var h = d.Heights;
            float talus = (float)Math.Tan(35.0 * Math.PI / 180.0) * cell;   // height difference per cell at 35°
            var delta = new float[n * n];
            for (int it = 0; it < iterations; it++)
            {
                Array.Clear(delta, 0, delta.Length);
                for (int z = 1; z < n - 1; z++)
                    for (int x = 1; x < n - 1; x++)
                    {
                        int i = z * n + x; float hi = h[i];
                        float total = 0f; float dmax = 0f;
                        float d0 = hi - h[i - 1], d1 = hi - h[i + 1], d2 = hi - h[i - n], d3 = hi - h[i + n];
                        if (d0 > talus) { total += d0 - talus; dmax = Math.Max(dmax, d0); }
                        if (d1 > talus) { total += d1 - talus; dmax = Math.Max(dmax, d1); }
                        if (d2 > talus) { total += d2 - talus; dmax = Math.Max(dmax, d2); }
                        if (d3 > talus) { total += d3 - talus; dmax = Math.Max(dmax, d3); }
                        if (total <= 0f) continue;
                        float move = 0.5f * (dmax - talus) * 0.5f;   // half of the excess, split by share
                        if (d0 > talus) { float s = move * (d0 - talus) / total; delta[i] -= s; delta[i - 1] += s; }
                        if (d1 > talus) { float s = move * (d1 - talus) / total; delta[i] -= s; delta[i + 1] += s; }
                        if (d2 > talus) { float s = move * (d2 - talus) / total; delta[i] -= s; delta[i - n] += s; }
                        if (d3 > talus) { float s = move * (d3 - talus) / total; delta[i] -= s; delta[i + n] += s; }
                    }
                for (int i = 0; i < h.Length; i++) h[i] += delta[i];
            }
        }

        private static void Normalise(TerrainData d, float low, float high)
        {
            float mn = d.MinHeight, mx = d.MaxHeight, span = Math.Max(1e-5f, mx - mn);
            for (int i = 0; i < d.Heights.Length; i++) d.Heights[i] = low + (d.Heights[i] - mn) / span * (high - low);
        }

        private void PaintBySlope(TerrainData d)
        {
            int n = d.Resolution; float cell = CellSize();
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    var nrm = d.Normal(x, z, cell);
                    float steep = 1f - Math.Max(0f, Math.Min(1f, nrm.Y));   // 0 flat … 1 vertical
                    float rock = Math.Max(0f, Math.Min(1f, (steep - 0.25f) / 0.25f));   // layer 3 beyond ~30°
                    float mid = Math.Max(0f, Math.Min(1f, (steep - 0.08f) / 0.17f)) * (1f - rock);   // layer 2 on gentle slopes
                    float flat = Math.Max(0f, 1f - mid - rock);
                    int o = (z * n + x) * 4;
                    d.Splat[o] = (byte)(flat * 255f); d.Splat[o + 1] = (byte)(mid * 255f); d.Splat[o + 2] = (byte)(rock * 255f); d.Splat[o + 3] = 0;
                }
        }

        private async Task ImportHeightmap()
        {
            if (_entity == null) return;
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Import heightmap", AllowMultiple = false,
                    FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Heightmap (PNG, RAW 16)") { Patterns = new[] { "*.png", "*.raw", "*.r16" } } }
                });
                string path = files != null && files.Count > 0 ? files[0].TryGetLocalPath() : null;
                if (string.IsNullOrEmpty(path)) return;
                var img = HeightmapIO.Load(path);
                float low = Math.Min(_importLow, _importHigh), high = Math.Max(_importLow, _importHigh);
                if (high - low < 0.001f) high = low + 1f;
                Global("Import heightmap", d => HeightmapIO.Apply(d, img, low, high));
                Status("Imported " + System.IO.Path.GetFileName(path) + " (" + img.Width + "×" + img.Height + ", " + img.BitDepth + " bit) as " + low.ToString("0.#") + " … " + high.ToString("0.#") + " m");
            }
            catch (Exception ex) { EditorCommands.Fail("Import heightmap", ex); }
        }

        // ================================================================ the map

        private void RedrawMap()
        {
            _mapDirty = false;
            TerrainData data; float cell;
            if (!TryData(out data, out cell)) { _map.Source = null; return; }
            int n = Math.Min(1024, Math.Max(256, data.Resolution));
            if (_mapBitmap == null || _mapBitmap.PixelSize.Width != n) { _mapBitmap = new WriteableBitmap(new PixelSize(n, n), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque); _mapN = n; }
            int stride;
            var px = HeightmapIO.ShadedRelief(data, n, cell, out stride);
            if (_showLayers)
            {
                // tint the relief by the splat's layers
                float scale = (data.Resolution - 1) / (float)Math.Max(1, n - 1);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        int sx = data.ClampIndex((int)Math.Round(x * scale)), sz = data.ClampIndex((int)Math.Round(y * scale));
                        float w0, w1, w2, w3; data.Weights(sx, sz, out w0, out w1, out w2, out w3);
                        float r = LayerColors[0].R * w0 + LayerColors[1].R * w1 + LayerColors[2].R * w2 + LayerColors[3].R * w3;
                        float g = LayerColors[0].G * w0 + LayerColors[1].G * w1 + LayerColors[2].G * w2 + LayerColors[3].G * w3;
                        float b = LayerColors[0].B * w0 + LayerColors[1].B * w1 + LayerColors[2].B * w2 + LayerColors[3].B * w3;
                        int o = y * stride + x * 4;
                        float shade = px[o + 2] / 255f * 0.6f + 0.5f;
                        px[o] = (byte)Math.Min(255, b * shade); px[o + 1] = (byte)Math.Min(255, g * shade); px[o + 2] = (byte)Math.Min(255, r * shade);
                    }
            }
            using (var fb = _mapBitmap.Lock())
                for (int y = 0; y < n; y++) System.Runtime.InteropServices.Marshal.Copy(px, y * stride, IntPtr.Add(fb.Address, y * fb.RowBytes), stride);
            _map.Source = null; _map.Source = _mapBitmap;
        }

        /// <summary>The displayed map rectangle inside the host (the image is letterboxed, square).</summary>
        private Rect MapRect()
        {
            double w = _mapHost.Bounds.Width, h = _mapHost.Bounds.Height, s = Math.Min(w, h);
            return new Rect((w - s) / 2, (h - s) / 2, s, s);
        }

        /// <summary>Host point → sample coordinates (x, z) of the terrain; false outside the map.</summary>
        private bool ToSample(Point p, out float sx, out float sz)
        {
            sx = sz = 0f;
            TerrainData data; float cell;
            if (!TryData(out data, out cell)) return false;
            var r = MapRect();
            if (r.Width <= 1) return false;
            double u = (p.X - r.X) / r.Width, v = (p.Y - r.Y) / r.Height;
            if (u < 0 || u > 1 || v < 0 || v > 1) return false;
            sx = (float)(u * (data.Resolution - 1)); sz = (float)(v * (data.Resolution - 1));
            return true;
        }

        private void OnMapMoved(object s, PointerEventArgs e)
        {
            var p = e.GetPosition(_mapHost);
            float sx, sz;
            if (!ToSample(p, out sx, out sz)) { _brush.IsVisible = _brushInner.IsVisible = false; _cursorInfo.Text = ""; return; }
            TerrainData data; float cell; TryData(out data, out cell);
            var r = MapRect();
            double radPx = TerrainToolService.Radius / Math.Max(1e-3f, cell) / (data.Resolution - 1) * r.Width;
            _brush.Width = _brush.Height = radPx * 2; Canvas.SetLeft(_brush, p.X - radPx); Canvas.SetTop(_brush, p.Y - radPx); _brush.IsVisible = true;
            double inner = radPx * 2 * TerrainToolService.Hardness;
            _brushInner.Width = _brushInner.Height = inner; Canvas.SetLeft(_brushInner, p.X - inner / 2); Canvas.SetTop(_brushInner, p.Y - inner / 2); _brushInner.IsVisible = inner > 2;
            float h = data.SampleHeight(sx, sz);
            var pos = _entity?.Transform != null ? _entity.Transform.LocalPosition : new Editor.ECS.Vector3(0, 0, 0);
            float w0, w1, w2, w3; data.Weights(data.ClampIndex((int)sx), data.ClampIndex((int)sz), out w0, out w1, out w2, out w3);
            _cursorInfo.Text = "world x " + (pos.X + sx * cell).ToString("0.0") + "  z " + (pos.Z + sz * cell).ToString("0.0") + "\nheight " + (pos.Y + h).ToString("0.00") + " m\nsample " + (int)sx + ", " + (int)sz +
                               "\nlayers " + (w0 * 100).ToString("0") + " / " + (w1 * 100).ToString("0") + " / " + (w2 * 100).ToString("0") + " / " + (w3 * 100).ToString("0") + " %";
            if (_stroking && e.GetCurrentPoint(_mapHost).Properties.IsLeftButtonPressed) ApplyAt(sx, sz, e.KeyModifiers);
        }

        private void OnMapPressed(object s, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(_mapHost).Properties.IsLeftButtonPressed) return;
            float sx, sz;
            if (!ToSample(e.GetPosition(_mapHost), out sx, out sz)) return;
            TerrainData data; float cell;
            if (!TryData(out data, out cell)) return;
            _stroking = true;
            _strokeHeights = (float[])data.Heights.Clone(); _strokeSplat = (byte[])data.Splat.Clone(); _strokeRect = SampleRect.Empty;
            _lastStrokeX = -1e9; _lastStrokeZ = -1e9;
            _flattenTarget = TerrainToolService.FlattenFixed ? TerrainToolService.FlattenHeight - (_entity?.Transform != null ? _entity.Transform.LocalPosition.Y : 0f) : data.SampleHeight(sx, sz);
            e.Pointer.Capture(_mapHost);
            ApplyAt(sx, sz, e.KeyModifiers);
            e.Handled = true;
        }

        private void OnMapReleased(object s, PointerReleasedEventArgs e)
        {
            if (!_stroking) return;
            _stroking = false;
            e.Pointer.Capture(null);
            TerrainData data; float cell;
            if (!TryData(out data, out cell) || _strokeRect.IsEmpty) return;
            var entity = _entity; var rect = _strokeRect;
            bool splat = _tool == MapTool.Paint;
            var beforeH = _strokeHeights; var beforeS = _strokeSplat;
            var afterH = (float[])data.Heights.Clone(); var afterS = (byte[])data.Splat.Clone();
            void Put(float[] h, byte[] sp)
            {
                Array.Copy(h, data.Heights, h.Length); Array.Copy(sp, data.Splat, sp.Length); data.MarkChanged();
                TerrainService.MarkDirty(entity, rect, !splat, splat); TerrainService.Save(entity); _mapDirty = true;
                try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
            }
            try { Editor.Core.UndoRedo.UndoRedoManager.Instance.Execute(new Editor.Core.UndoRedo.Commands.ActionCommand("Terrain " + ToolNames[(int)_tool].ToLowerInvariant() + " (map)", () => Put(afterH, afterS), () => Put(beforeH, beforeS)), false); } catch { }
            TerrainService.Save(entity);
            UpdateFacts();
            Status(ToolNames[(int)_tool] + " stroke: " + rect.Width + " × " + rect.Height + " samples, saved");
        }

        private void ApplyAt(float sx, float sz, KeyModifiers mods)
        {
            TerrainData data; float cell;
            if (!TryData(out data, out cell)) return;
            float r = Math.Max(0.5f, TerrainToolService.Radius / Math.Max(1e-3f, cell));
            // a step every quarter radius along the drag, so a fast drag still paints a line
            double dx = sx - _lastStrokeX, dz = sz - _lastStrokeZ;
            if (dx * dx + dz * dz < (r * 0.25) * (r * 0.25)) return;
            _lastStrokeX = sx; _lastStrokeZ = sz;
            float strength = TerrainToolService.Strength, hardness = TerrainToolService.Hardness;
            bool shift = mods.HasFlag(KeyModifiers.Shift), ctrl = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta);
            SampleRect rect;
            var tool = ctrl ? MapTool.Smooth : _tool;
            switch (tool)
            {
                case MapTool.Raise: rect = data.Raise(sx, sz, r, (shift ? -1f : 1f) * strength * 0.35f, hardness); break;
                case MapTool.Lower: rect = data.Raise(sx, sz, r, (shift ? 1f : -1f) * strength * 0.35f, hardness); break;
                case MapTool.Smooth: rect = data.Smooth(sx, sz, r, Math.Min(1f, strength * 0.8f), hardness); break;
                case MapTool.Flatten: rect = data.Flatten(sx, sz, r, _flattenTarget, Math.Min(1f, strength), hardness); break;
                case MapTool.Noise: rect = Noise(data, sx, sz, r, strength * 0.6f, hardness, cell); break;
                default: rect = data.Paint(sx, sz, r, _paintLayer, strength, hardness); break;
            }
            _strokeRect = _strokeRect.Union(rect);
            TerrainService.MarkDirty(_entity, rect, tool != MapTool.Paint, tool == MapTool.Paint);
            _mapDirty = true;
            try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
        }

        /// <summary>Rough ground under the brush: fractal noise at the window's noise scale, faded by the brush falloff.</summary>
        private SampleRect Noise(TerrainData d, float cx, float cz, float r, float amount, float hardness, float cell)
        {
            int n = d.Resolution;
            int x0 = d.ClampIndex((int)Math.Floor(cx - r)), x1 = d.ClampIndex((int)Math.Ceiling(cx + r)), z0 = d.ClampIndex((int)Math.Floor(cz - r)), z1 = d.ClampIndex((int)Math.Ceiling(cz + r));
            float scale = Math.Max(1f, _noiseScale / Math.Max(1e-3f, cell));
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    float dist = (float)Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
                    if (dist > r) continue;
                    float f = TerrainData.Falloff(dist, r, hardness);
                    float v = TerrainGenerator.Fbm(x / scale + 5.1f, z / scale + 2.7f, 3, 0.5f) - 0.5f;
                    d.Heights[z * n + x] += v * amount * 2f * f;
                }
            d.MarkChanged();
            return new SampleRect { X0 = x0, Z0 = z0, X1 = x1, Z1 = z1 };
        }

        /// <summary>Smoke hook: a stroke from (sx0, sz0) to (sx1, sz1) in sample coordinates with the current tool, saved like a
        /// mouse stroke. Returns the touched rectangle.</summary>
        internal SampleRect SimulateStroke(float sx0, float sz0, float sx1, float sz1, int steps = 8)
        {
            TerrainData data; float cell;
            if (!TryData(out data, out cell)) return SampleRect.Empty;
            _stroking = true;
            _strokeHeights = (float[])data.Heights.Clone(); _strokeSplat = (byte[])data.Splat.Clone(); _strokeRect = SampleRect.Empty;
            _lastStrokeX = -1e9; _lastStrokeZ = -1e9;
            _flattenTarget = data.SampleHeight(sx0, sz0);
            for (int i = 0; i <= steps; i++) { float t = i / (float)steps; ApplyAt(sx0 + (sx1 - sx0) * t, sz0 + (sz1 - sz0) * t, KeyModifiers.None); }
            _stroking = false;
            var rect = _strokeRect;
            if (!rect.IsEmpty) { TerrainService.Save(_entity); UpdateFacts(); }
            return rect;
        }

        internal GameEntity ShownTerrain => _entity;
        internal void ShowTab(int index) { _tabs.SelectedIndex = Math.Max(0, Math.Min(5, index)); }
        internal void ShowLayersMap(bool layers) { _showLayers = layers; _mapDirty = true; }
        internal void RedrawNow() { RedrawMap(); }
        /// <summary>Smoke / tour hook: the window as a PNG.</summary>
        internal void CaptureTo(string path)
        {
            var bmp = new RenderTargetBitmap(new PixelSize((int)(Bounds.Width * RenderScaling), (int)(Bounds.Height * RenderScaling)), new Vector(96 * RenderScaling, 96 * RenderScaling));
            bmp.Render(this);
            bmp.Save(path);
        }
        internal void SelectTool(int tool) => SetTool((MapTool)Math.Max(0, Math.Min(5, tool)));

        private void UpdateFacts()
        {
            TerrainData data; float cell;
            if (_terrain == null || !TryData(out data, out cell)) { _facts.Text = "—"; return; }
            _facts.Text = _entity.Name + "\n" + _terrain.Size.ToString("0") + " m × " + _terrain.Size.ToString("0") + " m, " + data.Resolution + "² samples (" + cell.ToString("0.##") + " m cells)" +
                          "\nheights " + data.MinHeight.ToString("0.#") + " … " + data.MaxHeight.ToString("0.#") + " m" +
                          "\nlayers: " + string.Join(", ", Enumerable.Range(0, 4).Select(i => string.IsNullOrEmpty(_terrain.LayerMaterial(i)) ? "—" : System.IO.Path.GetFileNameWithoutExtension(_terrain.LayerMaterial(i)))) +
                          "\n" + (string.IsNullOrEmpty(_terrain.DataPath) ? "not saved yet" : _terrain.DataPath);
        }
    }
}
