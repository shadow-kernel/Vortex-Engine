using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.UI.Vui;
using VortexEditor.Controls;
using VortexEditor.Shell.Prefab;
using VortexEditor.Shell.UiEditor;
using static VortexEditor.Panels.Inspector.PropertyRows;
using Path = System.IO.Path;

namespace VortexEditor.Shell
{
    /// <summary>
    /// The UI screen editor for .vui files (port of the Windows editor's UIEditorWindow): a palette that adds widgets
    /// into the selected element, the widget hierarchy (drag to reorder / reparent, copy / paste / duplicate), a design
    /// surface running the runtime layout at a chosen resolution (click to select, drag to move, handles to resize,
    /// arrow keys to nudge, zoom / pan, an interactive Test mode), the property panel (3×3 anchor picker, stretch
    /// margins, pivot, percent layout, colours, text, per-widget values, the button → C# action link with code
    /// generation, container layout, list row templates, sounds, bindings, screen settings), undo / redo, and an
    /// optional live preview through the engine in the main viewport — exactly what the game renders.
    /// </summary>
    public sealed class UiEditorWindow : Window
    {
        private static readonly Dictionary<string, UiEditorWindow> _open = new Dictionary<string, UiEditorWindow>(StringComparer.OrdinalIgnoreCase);
        private static string _elementClipboard;

        /// <summary>Open the UI editor for a .vui (brings an already open editor for it to the front).</summary>
        public static void Open(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            string key = PathKey(fullPath);
            if (_open.TryGetValue(key, out var existing)) { try { existing.Activate(); } catch { } return; }
            EditorWindows.Show(new UiEditorWindow(fullPath));
        }

        public static UiEditorWindow Find(string fullPath) => !string.IsNullOrEmpty(fullPath) && _open.TryGetValue(PathKey(fullPath), out var w) ? w : null;

        private static string PathKey(string p) { try { return Path.GetFullPath(p); } catch { return p; } }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly (int w, int h, string label)[] Resolutions =
        {
            (1920, 1080, "1920 × 1080  Full HD"), (1280, 720, "1280 × 720  HD"), (2560, 1440, "2560 × 1440  QHD"),
            (3440, 1440, "3440 × 1440  Ultrawide"), (1024, 768, "1024 × 768  4:3"), (3840, 2160, "3840 × 2160  4K")
        };

        private readonly string _path;
        private readonly VuiCanvas _canvas;
        private readonly VuiDesignCanvas _design = new VuiDesignCanvas();
        private readonly TreeView _tree = new TreeView();
        private readonly StackPanel _props = new StackPanel { Spacing = 2, Margin = new Thickness(12, 10, 12, 14) };
        private readonly List<Control> _refresherKeys = new List<Control>();
        private readonly List<string> _undo = new List<string>(), _redo = new List<string>();
        private readonly TextBlock _title = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _subtitle = new TextBlock { Classes = { "small", "secondary" }, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _zoomText = new TextBlock { Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center, MinWidth = 38, TextAlignment = TextAlignment.Center };
        private readonly ComboBox _resolution = new ComboBox { MinWidth = 190, MinHeight = 24 };
        private readonly ToggleButton _enginePreview, _test, _grid, _bounds;
        private readonly Button _undoButton, _redoButton;
        private readonly Border _testBanner = new Border { IsVisible = false, Padding = new Thickness(12, 5) };
        private VuiElement _selected;
        private VuiCanvas _engineCanvas;
        private string _savedJson, _lastUndoKey;
        private DateTime _lastUndoAt;
        private bool _dirty, _closingConfirmed, _syncTree, _loadFailed;
        private readonly DispatcherTimer _previewTimer;
        private (VuiElement el, Point start)? _treeDrag;

        public VuiCanvas Canvas => _canvas;
        public VuiDesignCanvas Design => _design;
        public VuiElement SelectedElement => _selected;
        public bool IsDirty => _dirty;

        public UiEditorWindow(string path)
        {
            _path = PathKey(path);
            _open[_path] = this;
            PrefabWorkflow.AdoptDialogOwnership(this);
            Title = "UI Editor — " + Path.GetFileName(path);
            Width = 1480; Height = 920; MinWidth = 1100; MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            try { _canvas = VuiDocument.Load(path); } catch { _canvas = null; }
            if (_canvas?.Root == null)
            {
                _loadFailed = File.Exists(path) && new FileInfo(path).Length > 0;
                _canvas = new VuiCanvas { Name = path, DesignW = 1920, DesignH = 1080, Root = new VuiElement { Kind = VuiKind.Panel, Id = "root", StretchX = true, StretchY = true, Bg = new[] { 0.05f, 0.05f, 0.07f, 1f }, BlocksInput = true } };
                _canvas.Reindex();
            }
            _savedJson = _loadFailed ? null : Snapshot();

            // ------------------------------------------------------------ toolbar
            foreach (var r in Resolutions) _resolution.Items.Add(r.label);
            int design = Array.FindIndex(Resolutions, r => r.w == _canvas.DesignW && r.h == _canvas.DesignH);
            if (design < 0) { _resolution.Items.Insert(0, _canvas.DesignW + " × " + _canvas.DesignH + "  design size"); design = 0; }
            _resolution.SelectedIndex = design;
            _resolution.SelectionChanged += (s, e) => ApplyResolution();
            ToolTip.SetTip(_resolution, "Preview resolution — the runtime layout scales the design to it");
            var zoomOut = IconBtn("Minus", "Zoom out", () => { _design.SetZoom(_design.ViewScale / 1.25); UpdateZoomText(); });
            var zoomIn = IconBtn("Plus", "Zoom in (" + Keys.Cmd + " wheel over the canvas)", () => { _design.SetZoom(_design.ViewScale * 1.25); UpdateZoomText(); });
            var fit = new Button { Content = "Fit", Classes = { "ghost" }, Padding = new Thickness(8, 2) };
            ToolTip.SetTip(fit, "Fit the screen into the view (double-click empty space)");
            fit.Click += (s, e) => { _design.Fit(); UpdateZoomText(); };
            _grid = ToggleIcon("Grid", "Grid (40 design px)", false, v => { _design.ShowGrid = v; _design.InvalidateVisual(); });
            _bounds = ToggleIcon("LayoutSingle", "Show the outline of every element", true, v => { _design.ShowBounds = v; _design.InvalidateVisual(); });
            _test = new ToggleButton { Content = Label("Play", "Test") };
            ToolTip.SetTip(_test, "Test the screen: hover, click buttons, toggles, sliders, steppers and type into fields (on a copy)");
            _test.IsCheckedChanged += (s, e) => SetTestMode(_test.IsChecked == true);
            _enginePreview = new ToggleButton { Content = Label("Eye", "Preview in viewport") };
            ToolTip.SetTip(_enginePreview, "Draw this screen through the engine over the main viewport — exactly what the game renders");
            _enginePreview.IsCheckedChanged += (s, e) => PushEnginePreview();
            _undoButton = IconBtn("Undo", "Undo (" + Keys.Chord("Z") + ")", Undo);
            _redoButton = IconBtn("Redo", "Redo (" + Keys.Chord("Z", shift: true) + ")", Redo);
            var delete = IconBtn("Trash", "Delete the selected element (" + Keys.Delete + ")", DeleteSelected);
            var save = new Button { Content = Label("Save", "Save"), Classes = { "accent" } };
            ToolTip.SetTip(save, "Save the screen (" + Keys.Chord("S") + ")");
            save.Click += (s, e) => Save();

            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new VxIcon { Icon = "LayoutSingle", Width = 20, Height = 20, Foreground = PrefabWorkflow.Res("VxAccentBrush") });
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(_title); titles.Children.Add(_subtitle);
            left.Children.Add(titles);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(_resolution);
            right.Children.Add(Sep());
            right.Children.Add(zoomOut); right.Children.Add(_zoomText); right.Children.Add(zoomIn); right.Children.Add(fit);
            right.Children.Add(_grid); right.Children.Add(_bounds);
            right.Children.Add(Sep());
            right.Children.Add(_test); right.Children.Add(_enginePreview);
            right.Children.Add(Sep());
            right.Children.Add(_undoButton); right.Children.Add(_redoButton); right.Children.Add(delete);
            right.Children.Add(save);
            var bar = new DockPanel { Margin = new Thickness(14, 8, 12, 8) };
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right); bar.Children.Add(left);
            var toolbar = new Border { Classes = { "hairline-bottom" }, Background = PrefabWorkflow.Res("VxToolbarBrush"), Child = bar };

            // ------------------------------------------------------------ left: palette + hierarchy
            var palette = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 8, 8) };
            foreach (VuiKind k in System.Enum.GetValues(typeof(VuiKind)))
            {
                var kk = k;
                var b = new Button { Classes = { "ghost" }, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(6, 3), Content = Label(IconFor(k), k.ToString(), 12) };
                ToolTip.SetTip(b, "Add a " + k + " into the selected element");
                b.Click += (s, e) => AddElement(kk);
                palette.Children.Add(b);
            }
            var paletteHeader = new StackPanel { Margin = new Thickness(10, 8, 10, 4) };
            paletteHeader.Children.Add(new TextBlock { Text = "ADD ELEMENT", Classes = { "small", "tertiary" }, FontWeight = FontWeight.SemiBold });
            paletteHeader.Children.Add(new TextBlock { Text = "added into the selected element", Classes = { "small", "tertiary" } });
            var paletteBox = new StackPanel();
            paletteBox.Children.Add(paletteHeader); paletteBox.Children.Add(palette);
            var treeHeader = new Border { Classes = { "panelheader" }, Child = new TextBlock { Text = "Hierarchy", Classes = { "headertitle" } } };
            _tree.ItemTemplate = new FuncTreeDataTemplate<UiNode>((n, _) => TreeRow(n), n => n.Kids);
            _tree.SelectionChanged += (s, e) => { if (!_syncTree && _tree.SelectedItem is UiNode n) Select(n.El, fromTree: true); };
            DragDrop.SetAllowDrop(_tree, true);
            _tree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
            _tree.AddHandler(DragDrop.DropEvent, OnTreeDrop);
            var leftDock = new DockPanel();
            var paletteBorder = new Border { Classes = { "hairline-bottom" }, Child = paletteBox };
            DockPanel.SetDock(paletteBorder, Dock.Top); DockPanel.SetDock(treeHeader, Dock.Top);
            leftDock.Children.Add(paletteBorder); leftDock.Children.Add(treeHeader); leftDock.Children.Add(_tree);
            var leftPane = new Border { Classes = { "sidebar", "hairline-right" }, Child = leftDock };

            // ------------------------------------------------------------ centre: design surface
            _design.Canvas = _canvas;
            _design.ScreenW = Resolutions.Length > 0 ? CurrentResolution().w : 1920;
            _design.ScreenH = CurrentResolution().h;
            _design.SelectionRequested += el => Select(el, fromCanvas: true);
            _design.EditStarting += () => PushUndo(null);
            _design.Edited += () => { RefreshValues(); Touch(treeLabels: false); };
            _design.EditCompleted += () => { RefreshValues(); Touch(treeLabels: false); };
            _design.DeleteRequested += DeleteSelected;
            _design.TestActionFired += OnTestAction;
            _design.PropertyChanged += (s, e) => { if (e.Property == BoundsProperty) UpdateZoomText(); };
            _testBanner.Background = PrefabWorkflow.Res("VxAccentSoftBrush");
            _testBanner.Child = new TextBlock { Text = "Test mode — interact with the widgets like in the game (a copy; your screen is not changed). Button clicks show the C# action they call.", Classes = { "small" }, TextWrapping = TextWrapping.Wrap };
            var centerDock = new DockPanel();
            DockPanel.SetDock(_testBanner, Dock.Top);
            centerDock.Children.Add(_testBanner);
            centerDock.Children.Add(_design);

            // ------------------------------------------------------------ right: properties
            var propsHeader = new Border { Classes = { "panelheader" }, Child = new TextBlock { Text = "Properties", Classes = { "headertitle" } } };
            var rightDock = new DockPanel();
            DockPanel.SetDock(propsHeader, Dock.Top);
            rightDock.Children.Add(propsHeader);
            rightDock.Children.Add(new ScrollViewer { Content = _props, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var rightPane = new Border { Classes = { "panel", "hairline-left" }, Child = rightDock };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("280,5,*,5,350") };
            grid.Children.Add(leftPane);
            var s1 = new GridSplitter { ResizeDirection = GridResizeDirection.Columns }; Grid.SetColumn(s1, 1); grid.Children.Add(s1);
            Grid.SetColumn(centerDock, 2); grid.Children.Add(centerDock);
            var s2 = new GridSplitter { ResizeDirection = GridResizeDirection.Columns }; Grid.SetColumn(s2, 3); grid.Children.Add(s2);
            Grid.SetColumn(rightPane, 4); grid.Children.Add(rightPane);

            var root = new DockPanel();
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(toolbar);
            root.Children.Add(grid);
            Content = root;

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _previewTimer.Tick += (s, e) => { _previewTimer.Stop(); PushEnginePreview(); };

            RebuildTree();
            Select(_canvas.Root);
            UpdateTitle();
            AddHandler(KeyDownEvent, OnWindowKey, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, (s, e) => LastModifiers = e.KeyModifiers, RoutingStrategies.Tunnel);
            AddHandler(PointerPressedEvent, (s, e) => LastModifiers = e.KeyModifiers, RoutingStrategies.Tunnel, handledEventsToo: true);
            Opened += (s, e) => { UpdateZoomText(); if (_loadFailed) EditorCommands.Toast("Could not read " + Path.GetFileName(_path) + " — started an empty screen (saving overwrites the file)"); };
            Closing += OnClosing;
            Closed += OnClosed;
        }

        // ================================================================ snapshots / undo / dirty
        private string Snapshot()
            => JsonSerializer.Serialize(new VuiDocument { Vui = 1, DesignW = _canvas.DesignW, DesignH = _canvas.DesignH, Root = VuiDocument.FromRuntime(_canvas.Root) }, Json);

        private void Restore(string json)
        {
            VuiDocument doc;
            try { doc = JsonSerializer.Deserialize<VuiDocument>(json, Json); } catch { return; }
            if (doc?.Root == null) return;
            var keep = PathOf(_selected);
            _canvas.Root = VuiDocument.ToRuntime(doc.Root, null);
            _canvas.DesignW = doc.DesignW > 0 ? doc.DesignW : 1920;
            _canvas.DesignH = doc.DesignH > 0 ? doc.DesignH : 1080;
            _canvas.Reindex();
            _selected = ByPath(keep) ?? _canvas.Root;
            _design.ResetHover();
            RebuildTree();
            Select(_selected, force: true);
        }

        /// <summary>Record the state before an edit. Consecutive edits of the same property within a moment merge.</summary>
        private void PushUndo(string key)
        {
            var now = DateTime.UtcNow;
            if (key != null && key == _lastUndoKey && (now - _lastUndoAt).TotalMilliseconds < 800) { _lastUndoAt = now; return; }
            _lastUndoKey = key; _lastUndoAt = now;
            var snap = Snapshot();
            if (_undo.Count > 0 && _undo[_undo.Count - 1] == snap) return;
            _undo.Add(snap);
            if (_undo.Count > 300) _undo.RemoveAt(0);
            _redo.Clear();
            UpdateUndoButtons();
        }

        public void Undo()
        {
            if (_undo.Count == 0 || _design.TestCanvas != null) return;
            _redo.Add(Snapshot());
            var s = _undo[_undo.Count - 1]; _undo.RemoveAt(_undo.Count - 1);
            _lastUndoKey = null;
            Restore(s);
            Touch(treeLabels: false);
            UpdateUndoButtons();
        }

        public void Redo()
        {
            if (_redo.Count == 0 || _design.TestCanvas != null) return;
            _undo.Add(Snapshot());
            var s = _redo[_redo.Count - 1]; _redo.RemoveAt(_redo.Count - 1);
            _lastUndoKey = null;
            Restore(s);
            Touch(treeLabels: false);
            UpdateUndoButtons();
        }

        private void UpdateUndoButtons()
        {
            if (_undoButton != null) _undoButton.IsEnabled = _undo.Count > 0;
            if (_redoButton != null) _redoButton.IsEnabled = _redo.Count > 0;
        }

        /// <summary>After any change: redraw, dirty state, engine preview, optionally the tree labels.</summary>
        private void Touch(bool treeLabels)
        {
            try { _canvas.Reindex(); } catch { }
            _design.InvalidateVisual();
            if (treeLabels) RebuildTree();
            string now = null;
            try { now = Snapshot(); } catch { }
            _dirty = _savedJson == null || now != _savedJson;
            UpdateTitle();
            if (_enginePreview.IsChecked == true) { _previewTimer.Stop(); _previewTimer.Start(); }
        }

        /// <summary>Apply an edit with undo (key merges rapid edits of the same field).</summary>
        private void Set(string key, Action change, bool treeLabels = false)
        {
            PushUndo(key);
            change();
            Touch(treeLabels);
        }

        private void UpdateTitle()
        {
            string name = Path.GetFileName(_path);
            _title.Text = (_dirty ? "● " : "") + name;
            Title = "UI Editor — " + name + (_dirty ? " (edited)" : "");
            int count = 0; void C(VuiElement e) { if (e == null) return; count++; foreach (var c in e.Children) C(c); if (e.RowTemplate != null) C(e.RowTemplate); }
            C(_canvas.Root);
            _subtitle.Text = PrefabWorkflow.Relative(_path) + "  ·  design " + _canvas.DesignW + " × " + _canvas.DesignH + "  ·  " + count + " element" + (count == 1 ? "" : "s");
        }

        private void UpdateZoomText() => Dispatcher.UIThread.Post(() => _zoomText.Text = Math.Round(_design.ViewScale * 100) + "%", DispatcherPriority.Render);

        private (int w, int h) CurrentResolution()
        {
            int i = _resolution.SelectedIndex;
            string label = i >= 0 && i < _resolution.Items.Count ? _resolution.Items[i] as string : null;
            if (label != null && label.EndsWith("design size")) return (_canvas.DesignW, _canvas.DesignH);
            var r = Resolutions.FirstOrDefault(x => x.label == label);
            return r.w > 0 ? (r.w, r.h) : (_canvas.DesignW, _canvas.DesignH);
        }

        private void ApplyResolution()
        {
            var (w, h) = CurrentResolution();
            _design.ScreenW = w; _design.ScreenH = h;
            _design.InvalidateVisual();
            UpdateZoomText();
        }

        // ================================================================ selection / tree
        private sealed class UiNode
        {
            public VuiElement El;
            public bool Template;
            public readonly ObservableCollection<UiNode> Kids = new ObservableCollection<UiNode>();
        }

        private UiNode _rootNode;

        private UiNode BuildNode(VuiElement e, bool template)
        {
            var n = new UiNode { El = e, Template = template };
            if (e.RowTemplate != null) n.Kids.Add(BuildNode(e.RowTemplate, true));
            foreach (var c in e.Children) n.Kids.Add(BuildNode(c, false));
            return n;
        }

        private void RebuildTree()
        {
            _syncTree = true;
            try
            {
                _rootNode = BuildNode(_canvas.Root, false);
                _tree.ItemsSource = new ObservableCollection<UiNode> { _rootNode };
            }
            finally { _syncTree = false; }
            Dispatcher.UIThread.Post(() =>
            {
                try { if (_tree.TreeContainerFromItem(_rootNode) is TreeViewItem tvi) _tree.ExpandSubTree(tvi); } catch { }
                SyncTreeSelection();
            }, DispatcherPriority.Background);
        }

        private UiNode FindNode(UiNode n, VuiElement e)
        {
            if (n == null) return null;
            if (ReferenceEquals(n.El, e)) return n;
            foreach (var k in n.Kids) { var r = FindNode(k, e); if (r != null) return r; }
            return null;
        }

        private void SyncTreeSelection()
        {
            var node = FindNode(_rootNode, _selected);
            if (node == null || ReferenceEquals(_tree.SelectedItem, node)) return;
            _syncTree = true;
            try { _tree.SelectedItem = node; try { _tree.TreeContainerFromItem(node)?.BringIntoView(); } catch { } }
            finally { _syncTree = false; }
        }

        private static string Describe(VuiElement e, bool template)
        {
            string s = template ? "row template" : (string.IsNullOrEmpty(e.Id) ? e.Kind.ToString() : e.Id);
            if (!string.IsNullOrEmpty(e.Text) && e.Kind != VuiKind.TextField) s += "  “" + (e.Text.Length > 24 ? e.Text.Substring(0, 24) + "…" : e.Text) + "”";
            return s;
        }

        private Control TreeRow(UiNode n)
        {
            var e = n.El;
            var row = new DockPanel { Height = 24, Background = Brushes.Transparent };
            if (e.Kind == VuiKind.Button && !string.IsNullOrEmpty(e.ClickAction))
            {
                var act = new TextBlock { Text = "→ " + e.ClickAction + "()", Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
                DockPanel.SetDock(act, Dock.Right);
                row.Children.Add(act);
            }
            row.Children.Add(new VxIcon { Icon = IconFor(e.Kind), Width = 13, Height = 13, Margin = new Thickness(0, 0, 6, 0), Foreground = PrefabWorkflow.Res(n.Template ? "VxPinkBrush" : "VxAccentBrush") });
            var t = new TextBlock { Text = Describe(e, n.Template), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = e.Visible ? 1 : 0.45 };
            if (n.Template) t.FontStyle = FontStyle.Italic;
            row.Children.Add(t);
            ToolTip.SetTip(row, e.Kind + (string.IsNullOrEmpty(e.Id) ? "" : "  #" + e.Id));
            row.PointerPressed += (s, a) =>
            {
                var pt = a.GetCurrentPoint(row);
                if (pt.Properties.IsRightButtonPressed) { Select(e); ElementMenu(e).ShowAt(row, true); a.Handled = true; return; }
                if (pt.Properties.IsLeftButtonPressed && e.Parent != null && !n.Template) _treeDrag = (e, a.GetPosition(this));
            };
            row.PointerMoved += async (s, a) =>
            {
                if (_treeDrag == null || !ReferenceEquals(_treeDrag.Value.el, e)) return;
                if (!a.GetCurrentPoint(row).Properties.IsLeftButtonPressed) { _treeDrag = null; return; }
                var p = a.GetPosition(this);
                if (Math.Abs(p.X - _treeDrag.Value.start.X) < 6 && Math.Abs(p.Y - _treeDrag.Value.start.Y) < 6) return;
                var data = new DataObject();
                data.Set("vortex/vui-element", e);
                _treeDrag = null;
                try { await DragDrop.DoDragDrop(a, data, DragDropEffects.Move); } catch { }
            };
            row.PointerReleased += (s, a) => _treeDrag = null;
            return row;
        }

        private void OnTreeDragOver(object s, DragEventArgs e)
        {
            var dragged = e.Data.Get("vortex/vui-element") as VuiElement;
            var target = ((e.Source as Control)?.DataContext as UiNode)?.El;
            e.DragEffects = dragged != null && target != null && !ReferenceEquals(dragged, target) && !IsAncestor(dragged, target) ? DragDropEffects.Move : DragDropEffects.None;
        }

        private void OnTreeDrop(object s, DragEventArgs e)
        {
            var dragged = e.Data.Get("vortex/vui-element") as VuiElement;
            var target = ((e.Source as Control)?.DataContext as UiNode)?.El;
            if (dragged == null || target == null || ReferenceEquals(dragged, target) || IsAncestor(dragged, target) || dragged.Parent == null) return;
            bool before = e.KeyModifiers.HasFlag(KeyModifiers.Alt), after = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            Set(null, () =>
            {
                dragged.Parent.Children.Remove(dragged);
                if ((before || after) && target.Parent != null && !IsTemplate(target))
                {
                    var p = target.Parent;
                    int i = p.Children.IndexOf(target);
                    p.Children.Insert(Math.Max(0, after ? i + 1 : i), dragged);
                    dragged.Parent = p;
                }
                else { target.Children.Add(dragged); dragged.Parent = target; }
            }, treeLabels: true);
            Select(dragged, force: true);
            e.Handled = true;
        }

        private static bool IsAncestor(VuiElement a, VuiElement e) { for (var p = e?.Parent; p != null; p = p.Parent) if (ReferenceEquals(p, a)) return true; return false; }
        private static bool IsTemplate(VuiElement e) => e?.Parent != null && ReferenceEquals(e.Parent.RowTemplate, e);

        /// <summary>Select an element (tree, canvas and properties follow).</summary>
        public void Select(VuiElement el, bool fromTree = false, bool fromCanvas = false, bool force = false)
        {
            if (el == null) el = _canvas.Root;
            bool changed = !ReferenceEquals(el, _selected);
            _selected = el;
            if (!fromCanvas) _design.Selected = el; else _design.InvalidateVisual();
            if (!fromTree) SyncTreeSelection();
            if (changed || force) RebuildProps();
        }

        private List<int> PathOf(VuiElement e)
        {
            var path = new List<int>();
            for (var x = e; x != null && x.Parent != null; x = x.Parent)
                path.Insert(0, IsTemplate(x) ? -1 : x.Parent.Children.IndexOf(x));
            return path;
        }

        private VuiElement ByPath(List<int> path)
        {
            var x = _canvas.Root;
            foreach (int i in path ?? new List<int>())
            {
                if (x == null) return null;
                x = i == -1 ? x.RowTemplate : (i >= 0 && i < x.Children.Count ? x.Children[i] : null);
            }
            return x;
        }

        // ================================================================ element operations
        private static string IconFor(VuiKind k)
        {
            switch (k)
            {
                case VuiKind.Text: return "File";
                case VuiKind.Image: return "Image";
                case VuiKind.Button: return "Check";
                case VuiKind.Bar: return "Minus";
                case VuiKind.Slider: return "Minus";
                case VuiKind.Toggle: return "Eye";
                case VuiKind.Stepper: return "ChevronRight";
                case VuiKind.TextField: return "Tag";
                case VuiKind.List: return "Hierarchy";
                case VuiKind.Crosshair: return "Crosshair";
                default: return "LayoutSingle";
            }
        }

        private static bool IsContainer(VuiElement e) => e != null && (e.Parent == null || e.Kind == VuiKind.Panel || e.Kind == VuiKind.List || IsTemplate(e));

        /// <summary>Where a new / pasted element goes: into the selection when it is a container (a list: into its row
        /// template), else next to it.</summary>
        private VuiElement InsertParent()
        {
            var sel = _selected ?? _canvas.Root;
            if (sel.Kind == VuiKind.List && sel.RowTemplate != null) return sel.RowTemplate;
            return IsContainer(sel) ? sel : (sel.Parent ?? _canvas.Root);
        }

        private string SuggestId(string baseId)
        {
            if (string.IsNullOrEmpty(baseId)) baseId = "element";
            var used = new HashSet<string>(StringComparer.Ordinal);
            void C(VuiElement e) { if (e == null) return; if (!string.IsNullOrEmpty(e.Id)) used.Add(e.Id); foreach (var c in e.Children) C(c); C(e.RowTemplate); }
            C(_canvas.Root);
            if (!used.Contains(baseId)) return baseId;
            string stem = baseId.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (stem.Length == 0) stem = baseId;
            for (int n = 2; ; n++) if (!used.Contains(stem + n)) return stem + n;
        }

        private static float[] C4(float r, float g, float b, float a = 1f) => new[] { r, g, b, a };

        private VuiElement NewElement(VuiKind kind, VuiElement parent)
        {
            string kindName = kind.ToString();
            var el = new VuiElement { Kind = kind, Parent = parent, Id = SuggestId(char.ToLowerInvariant(kindName[0]) + kindName.Substring(1)), Anchor = AnchorEnum.Center, W = 240, H = 48 };
            switch (kind)
            {
                case VuiKind.Text: el.Text = "Text"; el.FontSize = 22; el.W = 320; el.H = 36; el.Align = 1; break;
                case VuiKind.Button: el.Text = "Button"; el.Bg = C4(0.2f, 0.2f, 0.24f); el.FontSize = 20; el.W = 240; el.H = 52; el.Radius = 6; break;
                case VuiKind.Panel: el.Bg = C4(0.1f, 0.1f, 0.12f, 0.85f); el.W = 480; el.H = 320; el.Radius = 10; break;
                case VuiKind.Image: el.W = 128; el.H = 128; break;
                case VuiKind.Bar: el.Bg = C4(0.15f, 0.15f, 0.18f); el.Fg = C4(0.3f, 0.85f, 0.4f); el.Value = 0.7f; el.W = 320; el.H = 16; el.Radius = 4; break;
                case VuiKind.Slider: el.Bg = C4(0.22f, 0.22f, 0.26f); el.Fg = C4(0.86f, 0.86f, 0.9f); el.Value = 0.5f; el.W = 320; el.H = 22; break;
                case VuiKind.Toggle: el.Text = "Option"; el.Bg = C4(0.2f, 0.2f, 0.24f); el.Fg = C4(0.42f, 0.36f, 0.9f); el.W = 240; el.H = 30; el.FontSize = 18; break;
                case VuiKind.Stepper: el.Options = new[] { "Low", "Medium", "High" }; el.OptionIndex = 1; el.Bg = C4(0.14f, 0.14f, 0.17f); el.W = 280; el.H = 40; el.FontSize = 18; break;
                case VuiKind.TextField: el.Text = ""; el.Bg = C4(0.1f, 0.1f, 0.12f); el.W = 320; el.H = 40; el.FontSize = 18; el.Radius = 6; break;
                case VuiKind.Crosshair: el.W = 24; el.H = 24; break;
                case VuiKind.List:
                    el.W = 420; el.H = 300; el.Bg = C4(0.08f, 0.08f, 0.1f, 0.6f); el.LayoutMode = StackDir.Vertical; el.Spacing = 4; el.Padding = 6; el.ClipChildren = true; el.Radius = 6;
                    el.RowTemplate = NewRowTemplate(el);
                    break;
            }
            if (parent != null && parent.LayoutMode == StackDir.None)
            {
                // stagger so repeated adds don't stack exactly on top of each other
                int same = parent.Children.Count(c => c.Anchor == el.Anchor && Math.Abs(c.OffX - el.OffX) < 1 && Math.Abs(c.OffY - el.OffY) < 1);
                el.OffX += 24 * same; el.OffY += 24 * same;
            }
            return el;
        }

        private VuiElement NewRowTemplate(VuiElement list)
        {
            var row = new VuiElement { Kind = VuiKind.Panel, Id = SuggestId("row"), Parent = list, H = 32, Bg = C4(0.14f, 0.14f, 0.17f), Radius = 4 };
            row.Children.Add(new VuiElement { Kind = VuiKind.Text, Id = SuggestId("rowLabel"), Parent = row, Anchor = AnchorEnum.MidLeft, OffX = 12, W = 360, H = 28, Text = "Row", FontSize = 16, Align = 0 });
            return row;
        }

        /// <summary>Add a widget of this kind into the selected container (or next to the selection).</summary>
        public VuiElement AddElement(VuiKind kind)
        {
            if (_design.TestCanvas != null) return null;
            var parent = InsertParent();
            VuiElement el = null;
            Set(null, () => { el = NewElement(kind, parent); parent.Children.Add(el); }, treeLabels: true);
            Select(el);
            return el;
        }

        public void DeleteSelected()
        {
            var e = _selected;
            if (e == null || e.Parent == null || _design.TestCanvas != null) return;
            var parent = e.Parent;
            Set(null, () =>
            {
                if (IsTemplate(e)) parent.RowTemplate = null;
                else parent.Children.Remove(e);
            }, treeLabels: true);
            Select(parent);
        }

        private VuiElement CloneElement(VuiElement e, VuiElement parent)
        {
            var json = JsonSerializer.Serialize(VuiDocument.FromRuntime(e), Json);
            var clone = VuiDocument.ToRuntime(JsonSerializer.Deserialize<VuiNodeDto>(json, Json), parent);
            void Rename(VuiElement x) { if (x == null) return; if (!string.IsNullOrEmpty(x.Id)) x.Id = SuggestId(x.Id); foreach (var c in x.Children) Rename(c); Rename(x.RowTemplate); }
            Rename(clone);
            return clone;
        }

        public void DuplicateSelected()
        {
            var e = _selected;
            if (e == null || e.Parent == null || IsTemplate(e) || _design.TestCanvas != null) return;
            var parent = e.Parent;
            VuiElement copy = null;
            Set(null, () =>
            {
                copy = CloneElement(e, parent);
                if (parent.LayoutMode == StackDir.None) { copy.OffX += 20; copy.OffY += 20; }
                parent.Children.Insert(parent.Children.IndexOf(e) + 1, copy);
            }, treeLabels: true);
            Select(copy);
        }

        public void CopySelected()
        {
            if (_selected == null || _selected.Parent == null) return;
            _elementClipboard = JsonSerializer.Serialize(VuiDocument.FromRuntime(_selected), Json);
            EditorCommands.Toast("Copied " + Describe(_selected, false));
        }

        public void Paste()
        {
            if (_elementClipboard == null || _design.TestCanvas != null) return;
            var parent = InsertParent();
            VuiElement el = null;
            Set(null, () =>
            {
                el = VuiDocument.ToRuntime(JsonSerializer.Deserialize<VuiNodeDto>(_elementClipboard, Json), parent);
                void Rename(VuiElement x) { if (x == null) return; if (!string.IsNullOrEmpty(x.Id)) x.Id = SuggestId(x.Id); foreach (var c in x.Children) Rename(c); Rename(x.RowTemplate); }
                Rename(el);
                parent.Children.Add(el);
            }, treeLabels: true);
            Select(el);
        }

        private void MoveSelected(int dir)
        {
            var e = _selected; var p = e?.Parent;
            if (p == null || IsTemplate(e)) return;
            int i = p.Children.IndexOf(e), j = i + dir;
            if (i < 0 || j < 0 || j >= p.Children.Count) return;
            Set(null, () => { p.Children[i] = p.Children[j]; p.Children[j] = e; }, treeLabels: true);
            Select(e, force: true);
        }

        private MenuFlyout ElementMenu(VuiElement e)
        {
            var m = new MenuFlyout();
            var add = new MenuItem { Header = "Add" };
            foreach (VuiKind k in System.Enum.GetValues(typeof(VuiKind))) { var kk = k; add.Items.Add(MI(k.ToString(), () => AddElement(kk))); }
            m.Items.Add(add);
            if (e.Kind == VuiKind.List)
                m.Items.Add(e.RowTemplate == null ? MI("Create Row Template", () => { Set(null, () => e.RowTemplate = NewRowTemplate(e), true); Select(e.RowTemplate); })
                                                  : MI("Remove Row Template", () => { Set(null, () => e.RowTemplate = null, true); Select(e, force: true); }));
            m.Items.Add(new Separator());
            bool child = e.Parent != null;
            var dup = MI("Duplicate", DuplicateSelected); dup.IsEnabled = child && !IsTemplate(e);
            var copy = MI("Copy", CopySelected); copy.IsEnabled = child;
            var paste = MI("Paste", Paste); paste.IsEnabled = _elementClipboard != null;
            m.Items.Add(dup); m.Items.Add(copy); m.Items.Add(paste);
            m.Items.Add(new Separator());
            var up = MI("Move Up", () => MoveSelected(-1)); up.IsEnabled = child && !IsTemplate(e) && e.Parent.Children.IndexOf(e) > 0;
            var down = MI("Move Down", () => MoveSelected(1)); down.IsEnabled = child && !IsTemplate(e) && e.Parent.Children.IndexOf(e) < e.Parent.Children.Count - 1;
            m.Items.Add(up); m.Items.Add(down);
            m.Items.Add(new Separator());
            var del = MI("Delete", DeleteSelected); del.IsEnabled = child;
            m.Items.Add(del);
            return m;
        }

        private static MenuItem MI(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (s, e) => { try { a(); } catch (Exception ex) { EditorCommands.Fail(header, ex); } }; return mi; }

        // ================================================================ properties
        private void ReleaseRefreshers()
        {
            foreach (var k in _refresherKeys) Refreshers.Remove(k);
            _refresherKeys.Clear();
        }

        /// <summary>Re-read every property row from the model (after a drag on the canvas, undo, …).</summary>
        public void RefreshValues()
        {
            foreach (var k in _refresherKeys) if (Refreshers.TryGetValue(k, out var a)) { try { a(); } catch { } }
        }

        private void RebuildProps()
        {
            ReleaseRefreshers();
            _props.Children.Clear();
            var el = _selected;
            if (el == null) return;
            var before = new HashSet<Control>(Refreshers.Keys);
            try { BuildProps(el); }
            catch (Exception ex) { _props.Children.Add(Warning("Property editor error: " + ex.Message)); }
            foreach (var k in Refreshers.Keys.ToList()) if (!before.Contains(k)) _refresherKeys.Add(k);
        }

        private void Section(string title) => _props.Children.Add(new TextBlock { Text = title, Classes = { "section" }, Margin = new Thickness(0, 12, 0, 4) });

        private void BuildProps(VuiElement el)
        {
            bool isRoot = el.Parent == null, template = IsTemplate(el), arranged = VuiDesignCanvas.IsArranged(el);
            // ---- header
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            head.Children.Add(new VxIcon { Icon = IconFor(el.Kind), Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0), Foreground = PrefabWorkflow.Res("VxAccentBrush") });
            var ht = new StackPanel();
            ht.Children.Add(new TextBlock { Text = el.Kind + (isRoot ? "  ·  screen root" : template ? "  ·  row template" : ""), FontWeight = FontWeight.SemiBold, FontSize = 14 });
            ht.Children.Add(new TextBlock { Text = KindHelp(el.Kind), Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap });
            head.Children.Add(ht);
            _props.Children.Add(head);
            _props.Children.Add(Row("Id", Text(() => el.Id, v => Set("id", () => el.Id = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), true), "unique — scripts use it (Gui.SetText(\"id\", …))")));

            // ---- layout
            Section("Layout");
            if (isRoot) _props.Children.Add(Note("The root fills the screen."));
            else if (template) _props.Children.Add(Note("One row of the list — cloned per data row and stacked by the list's layout."));
            else if (arranged)
            {
                _props.Children.Add(Note("Positioned by the parent's " + el.Parent.LayoutMode.ToString().ToLowerInvariant() + " layout — only its size is used."));
                _props.Children.Add(Row("Size", Pair(() => el.W, v => Set("w", () => el.W = v), () => el.H, v => Set("h", () => el.H = v))));
            }
            if (!isRoot && !arranged && !template)
            {
                _props.Children.Add(Row("Anchor", AnchorPicker(el), "Where the element hangs in its parent. Click: anchor there (keeps the margin) \u00b7 " + Keys.Alt + "click: keep the element where it is"));
                _props.Children.Add(Row("Stretch", Pair2(() => el.StretchX, v => Set("sx", () => el.StretchX = v, false, rebuild: true), () => el.StretchY, v => Set("sy", () => el.StretchY = v, false, rebuild: true)), "Stretch to the parent: X / W (Y / H) become margins"));
                if (el.StretchX) _props.Children.Add(Row("Left · Right", Pair(() => el.OffX, v => Set("x", () => el.OffX = v), () => -el.W, v => Set("w", () => el.W = -v)), "Margins from the parent's left and right edge"));
                else _props.Children.Add(Row("X · Width", Pair(() => el.OffX, v => Set("x", () => el.OffX = v), () => el.W, v => Set("w", () => el.W = v)), "Offset from the anchor and width (design pixels)"));
                if (el.StretchY) _props.Children.Add(Row("Top · Bottom", Pair(() => el.OffY, v => Set("y", () => el.OffY = v), () => -el.H, v => Set("h", () => el.H = -v)), "Margins from the parent's top and bottom edge"));
                else _props.Children.Add(Row("Y · Height", Pair(() => el.OffY, v => Set("y", () => el.OffY = v), () => el.H, v => Set("h", () => el.H = v)), "Offset from the anchor and height (design pixels)"));
                _props.Children.Add(Row("Offset %", Pair(() => el.PctX, v => Set("px", () => el.PctX = v), () => el.PctY, v => Set("py", () => el.PctY = v), 0.01), "Extra offset as a fraction of the parent (0..1), added to X / Y"));
                _props.Children.Add(Row("Size %", Pair(() => el.WPct, v => Set("pw", () => el.WPct = Math.Max(0, v)), () => el.HPct, v => Set("ph", () => el.HPct = Math.Max(0, v)), 0.01), "Size as a fraction of the parent (0..1) — overrides width / height when > 0"));
                var pivot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                pivot.Children.Add(Bool(() => el.HasPivot, v => Set("pv", () => { el.HasPivot = v; if (v) { VuiDesignCanvas.AnchorFactors(el.Anchor, out float ax, out float ay); el.PivotX = ax; el.PivotY = ay; } }, false, rebuild: true)));
                if (el.HasPivot) { pivot.Children.Add(FloatBox(() => el.PivotX, v => Set("pvx", () => el.PivotX = v), 0.05)); pivot.Children.Add(FloatBox(() => el.PivotY, v => Set("pvy", () => el.PivotY = v), 0.05)); }
                else pivot.Children.Add(new TextBlock { Text = "= anchor", Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center });
                _props.Children.Add(Row("Pivot", pivot, "The point of the element placed on the anchor (0..1); off = the anchor point itself"));
            }
            else if (template) _props.Children.Add(Row("Row height", FloatBox(() => el.H, v => Set("h", () => el.H = Math.Max(1, v)), 1, 1)));

            // ---- appearance
            Section("Appearance");
            _props.Children.Add(Row("Visible", Bool(() => el.Visible, v => Set("vis", () => el.Visible = v, true))));
            _props.Children.Add(Row("Opacity", SliderRow(() => el.Opacity, v => Set("op", () => el.Opacity = v), 0, 1)));
            bool hasBg = el.Kind != VuiKind.Text && el.Kind != VuiKind.Crosshair && el.Kind != VuiKind.Image;
            if (hasBg) _props.Children.Add(Row(el.Kind == VuiKind.Bar || el.Kind == VuiKind.Slider ? "Track" : el.Kind == VuiKind.Toggle ? "Box (off)" : "Background", Rgba(() => el.Bg, v => Set("bg", () => el.Bg = v))));
            string fgLabel = el.Kind == VuiKind.Bar ? "Fill" : el.Kind == VuiKind.Slider ? "Handle" : el.Kind == VuiKind.Toggle ? "Box (on)" : el.Kind == VuiKind.Image ? "Tint" : el.Kind == VuiKind.Panel || el.Kind == VuiKind.List ? "Foreground" : "Text colour";
            if (el.Kind != VuiKind.Panel && el.Kind != VuiKind.List) _props.Children.Add(Row(fgLabel, Rgba(() => el.Fg, v => Set("fg", () => el.Fg = v))));
            if (el.Kind == VuiKind.Panel || el.Kind == VuiKind.List || el.Kind == VuiKind.Button || el.Kind == VuiKind.Bar || el.Kind == VuiKind.Stepper || el.Kind == VuiKind.TextField)
                _props.Children.Add(Row("Corner radius", FloatBox(() => el.Radius, v => Set("rad", () => el.Radius = Math.Max(0, v)), 1, 0)));
            if (el.Kind == VuiKind.Button)
            {
                var hover = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                hover.Children.Add(Bool(() => el.HoverTint != null, v => Set("hoverOn", () => el.HoverTint = v ? LightenCopy(el.Bg) : null, false, rebuild: true)));
                if (el.HoverTint != null) hover.Children.Add(Rgba(() => el.HoverTint, v => Set("hover", () => el.HoverTint = v)));
                else hover.Children.Add(new TextBlock { Text = "auto (lighter face)", Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center });
                _props.Children.Add(Row("Hover face", hover));
            }

            // ---- text
            if (el.Kind == VuiKind.Text || el.Kind == VuiKind.Button || el.Kind == VuiKind.Toggle || el.Kind == VuiKind.TextField || el.Kind == VuiKind.Stepper)
            {
                Section("Text");
                if (el.Kind != VuiKind.Stepper) _props.Children.Add(Row(el.Kind == VuiKind.TextField ? "Initial text" : "Text", Text(() => el.Text, v => Set("text", () => el.Text = v, true))));
                _props.Children.Add(Row("Font size", FloatBox(() => el.FontSize, v => Set("fs", () => el.FontSize = Math.Max(4, v)), 1, 4)));
                if (el.Kind == VuiKind.Text) _props.Children.Add(Row("Align", Choice(() => el.Align, v => Set("align", () => el.Align = v), "Left", "Center", "Right")));
                _props.Children.Add(Row("Weight", Choice(() => el.Weight >= 700 ? 2 : el.Weight >= 600 ? 1 : 0, v => Set("weight", () => el.Weight = v == 2 ? 700 : v == 1 ? 600 : 400), "Regular (400)", "Semibold (600)", "Bold (700)")));
            }

            // ---- widget values
            switch (el.Kind)
            {
                case VuiKind.Image:
                    Section("Image");
                    _props.Children.Add(Row("Image", AssetPath(() => el.ImageAsset, v => Set("img", () => { el.ImageAsset = string.IsNullOrEmpty(v) ? null : v; _design.ClearImageCache(); }), "Texture", new[] { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp" }, () => AssetPickerDialog.Pick("Textures", new[] { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp" }))));
                    _props.Children.Add(Note("Drawn stretched to the element; the tint's alpha fades it."));
                    break;
                case VuiKind.Bar:
                case VuiKind.Slider:
                    Section(el.Kind == VuiKind.Bar ? "Bar" : "Slider");
                    _props.Children.Add(Row("Value", SliderRow(() => el.Value, v => Set("val", () => el.Value = v), Math.Min(el.Min, el.Max), Math.Max(el.Min + 0.0001f, el.Max))));
                    _props.Children.Add(Row("Min · Max", Pair(() => el.Min, v => Set("min", () => el.Min = v, false, rebuild: true), () => el.Max, v => Set("max", () => el.Max = v, false, rebuild: true))));
                    break;
                case VuiKind.Toggle:
                    Section("Toggle");
                    _props.Children.Add(Row("On", Bool(() => el.On, v => Set("on", () => el.On = v))));
                    break;
                case VuiKind.Stepper:
                    Section("Stepper");
                    _props.Children.Add(Row("Options", Text(() => string.Join(", ", el.Options ?? Array.Empty<string>()), v => Set("opts", () => { el.Options = v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(); if (el.OptionIndex >= el.Options.Length) el.OptionIndex = 0; }, false, rebuild: true), "comma separated")));
                    if (el.Options != null && el.Options.Length > 0) _props.Children.Add(Row("Selected", Choice(() => el.OptionIndex, v => Set("optIdx", () => el.OptionIndex = v), el.Options)));
                    break;
                case VuiKind.TextField:
                    Section("Text field");
                    _props.Children.Add(Row("Max chars", IntBox(() => el.MaxChars, v => Set("max", () => el.MaxChars = v), 1, 4096)));
                    break;
            }

            // ---- button action (the button <-> code link)
            if (el.Kind == VuiKind.Button)
            {
                Section("On Click → C# method");
                string cls = VuiActions.ClassName(_path);
                var box = new TextBox { Text = el.ClickAction ?? "", Watermark = "method name, e.g. OnResume", MinHeight = 22 };
                var status = new TextBlock { Classes = { "small" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
                void UpdateStatus()
                {
                    if (string.IsNullOrEmpty(el.ClickAction)) { status.Text = "No action — type a method name and press Create / Bind."; status.Foreground = PrefabWorkflow.Res("VxTextTertiaryBrush"); return; }
                    bool has = VuiActions.HasMethod(_path, el.ClickAction);
                    status.Text = "runs  " + cls + "." + el.ClickAction + "()" + (has ? "" : "   — method not in the script yet (Create / Bind adds it)");
                    status.Foreground = PrefabWorkflow.Res(has ? "VxGreenBrush" : "VxOrangeBrush");
                }
                void Bind()
                {
                    string m = VuiActions.SanitizeMethod(box.Text);
                    Set("action", () => el.ClickAction = m, true);
                    if (m != null && !VuiActions.EnsureStub(_path, m)) EditorCommands.Toast("Could not write " + cls + ".cs (open a project first)");
                    box.Text = el.ClickAction ?? "";
                    UpdateStatus();
                    try { EditorCommands.Window?.AssetBrowser?.Refresh(); } catch { }
                }
                box.KeyDown += (s, e) => { if (e.Key == Key.Return) { Bind(); e.Handled = true; } };
                box.LostFocus += (s, e) => { if (VuiActions.SanitizeMethod(box.Text) != el.ClickAction) { string m = VuiActions.SanitizeMethod(box.Text); Set("action", () => el.ClickAction = m, true); UpdateStatus(); } };
                _props.Children.Add(box);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
                var bind = new Button { Content = "Create / Bind", Classes = { "accent" } };
                ToolTip.SetTip(bind, "Bind the method and add an empty handler to " + cls + ".cs (Assets/Scripts/UI)");
                bind.Click += (s, e) => Bind();
                var open = new Button { Content = "Open Code" };
                ToolTip.SetTip(open, "Open " + cls + ".cs in your code editor (created if missing)");
                open.Click += (s, e) => { var p = VuiActions.EnsureFile(_path); if (p != null) EditorCommands.OpenInIde(p); else EditorCommands.Toast("Open a project first"); };
                buttons.Children.Add(bind); buttons.Children.Add(open);
                _props.Children.Add(buttons);
                _props.Children.Add(status);
                UpdateStatus();
                _props.Children.Add(Row("Captures key", Bool(() => el.CapturesKey, v => Set("cap", () => el.CapturesKey = v)), "Clicking it waits for the next key press (key rebinding menus: Gui.GetCapturedKey)"));
            }

            // ---- children layout
            if (el.Kind == VuiKind.Panel || el.Kind == VuiKind.List || el.Children.Count > 0)
            {
                Section("Children layout");
                _props.Children.Add(Row("Layout", Enum<StackDir>(() => el.LayoutMode, v => Set("layout", () => el.LayoutMode = v, false, rebuild: true)), "None = every child uses its own anchor; Vertical / Horizontal / Grid stack them"));
                if (el.LayoutMode != StackDir.None)
                {
                    _props.Children.Add(Row("Spacing · Padding", Pair(() => el.Spacing, v => Set("spc", () => el.Spacing = v), () => el.Padding, v => Set("pad", () => el.Padding = v))));
                    if (el.LayoutMode == StackDir.Grid) _props.Children.Add(Row("Grid columns", IntBox(() => el.GridCols, v => Set("cols", () => el.GridCols = v), 1, 32)));
                }
                _props.Children.Add(Row("Clip children", Bool(() => el.ClipChildren, v => Set("clip", () => el.ClipChildren = v)), "Cut children off at the edge (scrolling lists)"));
            }
            if (el.Kind == VuiKind.List)
            {
                Section("List rows");
                if (el.RowTemplate == null)
                {
                    var create = new Button { Content = "Create Row Template" };
                    create.Click += (s, e) => { Set(null, () => el.RowTemplate = NewRowTemplate(el), true); Select(el.RowTemplate); };
                    _props.Children.Add(Row("", create));
                    _props.Children.Add(Note("Scripts fill the list with Gui.SetList(\"" + (el.Id ?? "id") + "\", rows) — one clone of the row template per row; row element ids receive the row's values."));
                }
                else
                {
                    var edit = new Button { Content = "Edit Row Template" };
                    edit.Click += (s, e) => Select(el.RowTemplate);
                    var remove = new Button { Content = "Remove" };
                    remove.Click += (s, e) => { Set(null, () => el.RowTemplate = null, true); Select(el, force: true); };
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    sp.Children.Add(edit); sp.Children.Add(remove);
                    _props.Children.Add(Row("Row template", sp));
                    _props.Children.Add(Note("The canvas shows three design rows. Scripts: Gui.SetList(\"" + (el.Id ?? "id") + "\", rows)."));
                }
            }

            // ---- behaviour
            Section("Behaviour");
            _props.Children.Add(Row("Tooltip", Text(() => el.Tooltip, v => Set("tip", () => el.Tooltip = string.IsNullOrEmpty(v) ? null : v)), "Shown after a short hover"));
            string[] audio = { "*.wav", "*.mp3", "*.ogg", "*.flac", "*.vsndc" };
            _props.Children.Add(Row("Click sound", AssetPath(() => el.ClickSound, v => Set("cs", () => el.ClickSound = string.IsNullOrEmpty(v) ? null : v), "Audio", audio, () => AssetPickerDialog.Pick("Audio", audio))));
            _props.Children.Add(Row("Hover sound", AssetPath(() => el.HoverSound, v => Set("hs", () => el.HoverSound = string.IsNullOrEmpty(v) ? null : v), "Audio", audio, () => AssetPickerDialog.Pick("Audio", audio))));
            if (isRoot) _props.Children.Add(Note("The root's sounds are the defaults for every interactive element of the screen."));
            _props.Children.Add(Row("Target setting", Text(() => el.TargetSetting, v => Set("ts", () => el.TargetSetting = string.IsNullOrEmpty(v) ? null : v), "e.g. Camera.Fov"), "Advisory: the setting this widget edits (the script reads + applies it)"));
            var binds = new WrapPanel();
            void Flag(string label, Func<bool> g, Action<bool> s) { var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 0, 10, 2) }; sp.Children.Add(Bool(g, s)); sp.Children.Add(new TextBlock { Text = label, Classes = { "small" }, VerticalAlignment = VerticalAlignment.Center }); binds.Children.Add(sp); }
            Flag("Value", () => el.BindValue, v => Set("bv", () => el.BindValue = v));
            Flag("Text", () => el.BindText, v => Set("bt", () => el.BindText = v));
            Flag("Visible", () => el.BindVisible, v => Set("bvi", () => el.BindVisible = v));
            Flag("Color", () => el.BindColor, v => Set("bc", () => el.BindColor = v));
            Flag("Image", () => el.BindImage, v => Set("bi", () => el.BindImage = v));
            Flag("Clicked", () => el.BindClicked, v => Set("bcl", () => el.BindClicked = v));
            Flag("List", () => el.BindList, v => Set("bl", () => el.BindList = v));
            _props.Children.Add(Row("Script binds", binds, "Advisory: which slots scripts drive (builder hints)"));

            // ---- screen (root)
            if (isRoot)
            {
                Section("Screen");
                _props.Children.Add(Row("Blocks input", Bool(() => el.BlocksInput, v => Set("bi2", () => el.BlocksInput = v)), "A menu / modal: consumes clicks and owns the mouse"));
                _props.Children.Add(Row("Cursor locked", Bool(() => el.CursorLocked, v => Set("cl", () => el.CursorLocked = v)), "HUD: keep mouse-look while shown"));
                _props.Children.Add(Row("Freeze player", Bool(() => el.BlocksGameplay, v => Set("bg2", () => el.BlocksGameplay = v)), "Freeze gameplay input: movement + mouse-look stop while shown (chest / menu — not for a hotbar)"));
                _props.Children.Add(Row("Design size", Pair(() => _canvas.DesignW, v => Set("dw", () => _canvas.DesignW = Math.Max(64, (int)v)), () => _canvas.DesignH, v => Set("dh", () => _canvas.DesignH = Math.Max(64, (int)v)), 1), "The resolution the pixel values are authored for; other resolutions scale uniformly"));
            }
        }

        private static string KindHelp(VuiKind k)
        {
            switch (k)
            {
                case VuiKind.Panel: return "Container / coloured background";
                case VuiKind.Text: return "Label — scripts: Gui.SetText";
                case VuiKind.Image: return "Textured quad (icon / logo / portrait)";
                case VuiKind.Bar: return "Track + fill (health, stamina, progress) — Gui.SetValue";
                case VuiKind.Button: return "Clickable — runs its C# action, Gui.WasClicked";
                case VuiKind.Slider: return "Draggable 0..1 value (volume, FOV)";
                case VuiKind.Toggle: return "On / off (VSync, subtitles)";
                case VuiKind.Stepper: return "Cycles a list of options (quality)";
                case VuiKind.TextField: return "Typed text (name, chat)";
                case VuiKind.List: return "Repeater: one row-template clone per data row";
                case VuiKind.Crosshair: return "Centre reticle";
                default: return "";
            }
        }

        private void Set(string key, Action change, bool treeLabels, bool rebuild)
        {
            Set(key, change, treeLabels);
            if (rebuild) Dispatcher.UIThread.Post(() => RebuildProps(), DispatcherPriority.Background);
        }

        private static float[] LightenCopy(float[] c)
        {
            if (c == null || c.Length < 4) return new[] { 0.3f, 0.3f, 0.34f, 1f };
            return new[] { Math.Min(1, c[0] + 0.09f), Math.Min(1, c[1] + 0.09f), Math.Min(1, c[2] + 0.09f), c[3] };
        }

        private Control AnchorPicker(VuiElement el)
        {
            var grid = new UniformGrid { Rows = 3, Columns = 3, Width = 84, Height = 84, HorizontalAlignment = HorizontalAlignment.Left };
            var buttons = new List<(Button b, AnchorEnum a)>();
            void Paint() { foreach (var (b, a) in buttons) b.Background = PrefabWorkflow.Res(a == el.Anchor ? "VxAccentBrush" : "VxControlBrush"); }
            for (int i = 0; i < 9; i++)
            {
                var a = (AnchorEnum)i;
                var b = new Button { Width = 26, Height = 26, Margin = new Thickness(1), Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Content = new Ellipse { Width = 6, Height = 6, Fill = PrefabWorkflow.Res("VxTextBrush") } };
                ToolTip.SetTip(b, System.Text.RegularExpressions.Regex.Replace(a.ToString(), "([a-z])([A-Z])", "$1 $2"));
                b.Click += (s, e) =>
                {
                    bool keep = false;
                    try { keep = (TopLevel.GetTopLevel(this) as Window) != null && LastModifiers.HasFlag(KeyModifiers.Alt); } catch { }
                    SetAnchor(el, a, keep);
                    Paint();
                };
                buttons.Add((b, a));
                grid.Children.Add(b);
            }
            Paint();
            var presets = new Button { Content = "Presets", Classes = { "ghost" }, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
            presets.Click += (s, e) =>
            {
                var m = new MenuFlyout();
                m.Items.Add(MI("Fill Parent", () => Set(null, () => { el.StretchX = el.StretchY = true; el.OffX = el.OffY = 0; el.W = el.H = 0; el.PctX = el.PctY = el.WPct = el.HPct = 0; })));
                m.Items.Add(MI("Top Bar (full width)", () => Set(null, () => { el.Anchor = AnchorEnum.TopLeft; el.StretchX = true; el.StretchY = false; el.OffX = 0; el.W = 0; el.OffY = 0; if (el.H <= 0) el.H = 64; })));
                m.Items.Add(MI("Bottom Bar (full width)", () => Set(null, () => { el.Anchor = AnchorEnum.BottomLeft; el.StretchX = true; el.StretchY = false; el.OffX = 0; el.W = 0; el.OffY = 0; if (el.H <= 0) el.H = 64; })));
                m.Items.Add(MI("Left Column (full height)", () => Set(null, () => { el.Anchor = AnchorEnum.TopLeft; el.StretchY = true; el.StretchX = false; el.OffY = 0; el.H = 0; el.OffX = 0; if (el.W <= 0) el.W = 320; })));
                m.Items.Add(MI("Right Column (full height)", () => Set(null, () => { el.Anchor = AnchorEnum.TopRight; el.StretchY = true; el.StretchX = false; el.OffY = 0; el.H = 0; el.OffX = 0; if (el.W <= 0) el.W = 320; })));
                m.Items.Add(MI("Centered", () => Set(null, () => { el.Anchor = AnchorEnum.Center; el.StretchX = el.StretchY = false; el.OffX = el.OffY = 0; el.PctX = el.PctY = 0; if (el.W <= 0) el.W = 240; if (el.H <= 0) el.H = 48; })));
                m.Closed += (a, b) => Dispatcher.UIThread.Post(() => RebuildProps(), DispatcherPriority.Background);
                m.ShowAt(presets);
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(grid); sp.Children.Add(presets);
            return sp;
        }

        private KeyModifiers LastModifiers { get; set; }

        /// <summary>Change the anchor. Default: keep the element's margin, mirrored to the new side; keepPosition: the
        /// element stays exactly where it is (offsets recomputed).</summary>
        public void SetAnchor(VuiElement el, AnchorEnum a, bool keepPosition)
        {
            if (el == null || el.Anchor == a) return;
            Set("anchor", () =>
            {
                VuiDesignCanvas.AnchorFactors(a, out float ax, out float ay);
                if (keepPosition && el.Parent != null)
                {
                    _design.RunLayout();
                    var r = el.Resolved; var P = el.Parent.Resolved; float s = _canvas.Scale > 0 ? _canvas.Scale : 1f;
                    float pvx = el.HasPivot ? el.PivotX : ax, pvy = el.HasPivot ? el.PivotY : ay;
                    el.Anchor = a;
                    if (!el.StretchX) el.OffX = (float)Math.Round((r.X - P.X - ax * P.W - el.PctX * P.W + pvx * r.W) / s);
                    if (!el.StretchY) el.OffY = (float)Math.Round((r.Y - P.Y - ay * P.H - el.PctY * P.H + pvy * r.H) / s);
                }
                else
                {
                    float mx = Math.Abs(el.OffX), my = Math.Abs(el.OffY);
                    el.Anchor = a;
                    if (!el.StretchX) el.OffX = ax < 0.25f ? mx : ax > 0.75f ? -mx : 0;
                    if (!el.StretchY) el.OffY = ay < 0.25f ? my : ay > 0.75f ? -my : 0;
                }
            });
            RefreshValues();
        }

        private static Control Pair(Func<float> gx, Action<float> sx, Func<float> gy, Action<float> sy, double step = 1)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*") };
            var a = FloatBox(gx, sx, step); var b = FloatBox(gy, sy, step);
            Grid.SetColumn(b, 2);
            g.Children.Add(a); g.Children.Add(b);
            return g;
        }

        private static Control Pair2(Func<bool> gx, Action<bool> sx, Func<bool> gy, Action<bool> sy)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var a = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 }; a.Children.Add(Bool(gx, sx)); a.Children.Add(new TextBlock { Text = "X", Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center });
            var b = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 }; b.Children.Add(Bool(gy, sy)); b.Children.Add(new TextBlock { Text = "Y", Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(a); sp.Children.Add(b);
            return sp;
        }

        private static Control Rgba(Func<float[]> get, Action<float[]> set)
        {
            float[] Cur() { var v = get(); return v != null && v.Length >= 4 ? v : new[] { 0f, 0f, 0f, 0f }; }
            Avalonia.Media.Color ToColor(float[] c) => Avalonia.Media.Color.FromArgb(B(c[3]), B(c[0]), B(c[1]), B(c[2]));
            var picker = new ColorPicker { Width = 120, Height = 22, IsAlphaEnabled = true, IsAlphaVisible = true, HorizontalAlignment = HorizontalAlignment.Left, Color = ToColor(Cur()) };
            bool guard = false;
            picker.ColorChanged += (s, e) => { if (guard) return; var c = e.NewColor; set(new[] { c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f }); };
            var alpha = new TextBlock { Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            void Sync() { var c = Cur(); guard = true; var col = ToColor(c); if (picker.Color != col) picker.Color = col; guard = false; alpha.Text = "α " + Math.Round(c[3] * 100) + "%"; }
            Sync();
            picker.ColorChanged += (s, e) => alpha.Text = "α " + Math.Round(e.NewColor.A / 2.55) + "%";
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(picker); sp.Children.Add(alpha);
            Refreshers[picker] = Sync;
            return sp;
        }

        // ================================================================ test / engine preview
        private VuiCanvas CloneCanvas(bool absoluteImages)
        {
            var json = Snapshot();
            var doc = JsonSerializer.Deserialize<VuiDocument>(json, Json);
            var c = new VuiCanvas { Name = _path, DesignW = doc.DesignW, DesignH = doc.DesignH, Root = VuiDocument.ToRuntime(doc.Root, null) };
            if (absoluteImages)
            {
                void Fix(VuiElement e)
                {
                    if (e == null) return;
                    if (!string.IsNullOrEmpty(e.ImageAsset)) e.ImageAsset = VuiDesignCanvas.ResolveAsset(e.ImageAsset);
                    foreach (var k in e.Children) Fix(k);
                    Fix(e.RowTemplate);
                }
                Fix(c.Root);
            }
            c.Reindex();
            return c;
        }

        private void SetTestMode(bool on)
        {
            _design.TestCanvas = on ? CloneCanvas(false) : null;
            _testBanner.IsVisible = on;
            _design.InvalidateVisual();
            _design.Focus();
        }

        private void OnTestAction(string action)
        {
            bool has = VuiActions.HasMethod(_path, action);
            EditorCommands.Toast("Click → " + VuiActions.ClassName(_path) + "." + action + "()" + (has ? "" : "  (method not written yet)"));
        }

        private void PushEnginePreview()
        {
            var session = EditorViewportSession.Main;
            if (session == null)
            {
                if (_enginePreview.IsChecked == true) { _enginePreview.IsChecked = false; EditorCommands.Toast("Open a project with a viewport first"); }
                return;
            }
            if (_enginePreview.IsChecked != true)
            {
                if (_engineCanvas != null && ReferenceEquals(session.PreviewCanvas, _engineCanvas)) session.PreviewCanvas = null;
                _engineCanvas = null;
            }
            else
            {
                _engineCanvas = CloneCanvas(true);
                session.PreviewCanvas = _engineCanvas;
            }
            SceneRenderService.RuntimeDirty = true;
        }

        // ================================================================ save / close / keys
        /// <summary>Write the screen to its .vui.</summary>
        public bool Save()
        {
            try
            {
                VuiDocument.Save(_canvas, _path);
                _savedJson = Snapshot();
                _dirty = false;
                UpdateTitle();
                try { Editor.Core.Assets.AssetDatabase.Instance.Refresh(); } catch { }
                EditorCommands.Toast("UI screen saved");
                return true;
            }
            catch (Exception ex) { EditorCommands.Fail("Save UI screen", ex); return false; }
        }

        private void OnWindowKey(object s, KeyEventArgs e)
        {
            LastModifiers = e.KeyModifiers;
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool typing = FocusManager?.GetFocusedElement() is TextBox;
            if (cmd)
            {
                switch (e.Key)
                {
                    case Key.S: Save(); e.Handled = true; return;
                    case Key.W: Close(); e.Handled = true; return;
                }
                if (typing) return;
                switch (e.Key)
                {
                    case Key.Z: if (shift) Redo(); else Undo(); e.Handled = true; break;
                    case Key.Y: Redo(); e.Handled = true; break;
                    case Key.D: DuplicateSelected(); e.Handled = true; break;
                    case Key.C: CopySelected(); e.Handled = true; break;
                    case Key.V: Paste(); e.Handled = true; break;
                }
                return;
            }
            // Delete / Backspace remove the element only while the hierarchy has focus (the canvas handles its own);
            // in any other control (slider, combo, colour) the key belongs to that control
            if (!typing && (e.Key == Key.Delete || e.Key == Key.Back) && FocusManager?.GetFocusedElement() is Visual fv && (ReferenceEquals(fv, _tree) || fv.GetVisualAncestors().Contains(_tree)))
            { DeleteSelected(); e.Handled = true; }
        }


        /// <summary>Close without the unsaved-changes prompt.</summary>
        public void CloseWithoutSaving() { _closingConfirmed = true; Close(); }

        private async void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (_closingConfirmed || !_dirty) return;
            e.Cancel = true;
            int r = await PrefabWorkflow.Sheet(this, "Save changes to “" + Path.GetFileName(_path) + "”?", "Your changes to this UI screen are lost if you don't save them.", new[] { "Save", "Don't Save", "Cancel" }, destructiveIndex: 1);
            if (r == 2) return;
            if (r == 0 && !Save()) return;
            _closingConfirmed = true;
            Close();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _open.Remove(_path);
            _previewTimer.Stop();
            ReleaseRefreshers();
            var session = EditorViewportSession.Main;
            if (session != null && _engineCanvas != null && ReferenceEquals(session.PreviewCanvas, _engineCanvas)) { session.PreviewCanvas = null; SceneRenderService.RuntimeDirty = true; }
        }

        // ================================================================ small builders
        private static StackPanel Label(string icon, string text, double size = 0)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            sp.Children.Add(new VxIcon { Icon = icon, Width = 12, Height = 12 });
            var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            if (size > 0) t.FontSize = size;
            sp.Children.Add(t);
            return sp;
        }

        private static Button IconBtn(string icon, string tip, Action a)
        {
            var b = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = icon } };
            ToolTip.SetTip(b, tip);
            b.Click += (s, e) => a();
            return b;
        }

        private static ToggleButton ToggleIcon(string icon, string tip, bool on, Action<bool> changed)
        {
            var b = new ToggleButton { Classes = { "icon" }, IsChecked = on, Content = new VxIcon { Icon = icon } };
            ToolTip.SetTip(b, tip);
            b.IsCheckedChanged += (s, e) => changed(b.IsChecked == true);
            return b;
        }

        private static Border Sep() => new Border { Width = 1, Height = 20, Margin = new Thickness(4, 0), Background = PrefabWorkflow.Res("VxSeparatorBrush") };
    }
}
