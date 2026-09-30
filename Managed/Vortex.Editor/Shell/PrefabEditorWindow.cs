using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using VortexEditor.Controls;
using VortexEditor.Panels;
using VortexEditor.Shell.Prefab;

namespace VortexEditor.Shell
{
    /// <summary>
    /// The Prefab Editor (port of the Windows editor's prefab hub + isolated prefab editor, merged into one window):
    /// a live orbit preview of what the prefab spawns (unsaved edits included, selected entity boxed), the prefab's
    /// entity tree (add / duplicate / rename / reorder / delete children), an isolated inspector for the selected
    /// entity (all components, add / remove, values) and the workflow actions — Save (writes the .ventity and reloads
    /// every placed instance), Revert (discard edits), Place in Scene, Large Preview, Edit Model, materials, the
    /// "Solid" one-click collision toggle. The template is edited in memory; nothing touches the scene or the global
    /// undo stack until Save.
    /// </summary>
    public sealed class PrefabEditorWindow : Window
    {
        private static readonly Dictionary<string, PrefabEditorWindow> _open = new Dictionary<string, PrefabEditorWindow>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Open the Prefab Editor for a .ventity (brings an already open editor for it to the front).</summary>
        public static void Open(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            string key = PathKey(fullPath);
            if (_open.TryGetValue(key, out var existing)) { try { existing.Activate(); } catch { } return; }
            if (!File.Exists(fullPath)) { EditorCommands.Toast("Prefab file not found: " + Path.GetFileName(fullPath)); return; }
            EditorWindows.Show(new PrefabEditorWindow(fullPath));
        }

        /// <summary>The open editor for this prefab (null when none).</summary>
        public static PrefabEditorWindow Find(string fullPath) => !string.IsNullOrEmpty(fullPath) && _open.TryGetValue(PathKey(fullPath), out var w) ? w : null;

        /// <summary>The .ventity was rewritten by someone else (Apply to Prefab, Save as Prefab over it): a clean editor
        /// reloads, an editor with unsaved changes shows a banner.</summary>
        public static void NotifyChangedOnDisk(string fullPath)
        {
            var w = Find(fullPath);
            if (w != null) Dispatcher.UIThread.Post(w.OnChangedOnDisk);
        }

        private static string PathKey(string p) { try { return Path.GetFullPath(p); } catch { return p; } }

        private readonly string _path;
        private readonly string _name;
        private GameEntity _root;
        private string _savedJson;
        private bool _dirty, _saving, _closingConfirmed;
        private EntityTreeWatcher _watcher;
        private readonly PrefabPreviewBuilder _builder = new PrefabPreviewBuilder();
        private readonly PreviewViewport _preview = new PreviewViewport();
        private readonly TreeView _tree = new TreeView();
        private readonly InspectorPanel _inspector = new InspectorPanel { IsolatedMode = true };
        private readonly TextBlock _title = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _subtitle = new TextBlock { Classes = { "small", "secondary" }, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _stats = new TextBlock { Classes = { "small" }, Foreground = Brushes.White, Opacity = 0.75, IsHitTestVisible = false };
        private readonly Border _banner = new Border { IsVisible = false, Padding = new Thickness(12, 6) };
        private readonly CheckBox _solid = new CheckBox();
        private readonly StackPanel _materials = new StackPanel { Spacing = 4 };
        private readonly StackPanel _materialsBox = new StackPanel { Spacing = 2 };
        private readonly Button _save, _revert;
        private readonly ToggleButton _highlight;
        private bool _syncSolid;

        public GameEntity Root => _root;
        public bool IsDirty => _dirty;
        public string PrefabPath => _path;
        public PreviewViewport Preview => _preview;
        public InspectorPanel Inspector => _inspector;
        public GameEntity SelectedEntity => _tree.SelectedItem as GameEntity;

        public PrefabEditorWindow(string fullPath)
        {
            _path = PathKey(fullPath);
            _name = Path.GetFileNameWithoutExtension(fullPath);
            Title = "Prefab — " + _name;
            Width = 1240; Height = 780; MinWidth = 920; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            _open[PathKey(fullPath)] = this;
            PrefabWorkflow.AdoptDialogOwnership(this);

            try { LoadRoot(); }
            catch (Exception ex)
            {
                Content = ErrorContent(ex.Message);
                Closed += (s, e) => _open.Remove(PathKey(_path));
                return;
            }

            // ------------------------------------------------------------ toolbar
            _save = ToolButton("Save", "Save", "Write the prefab (⌘S) — every placed instance is reloaded from it", () => Save(), accent: true);
            _revert = ToolButton("Undo", "Revert", "Discard unsaved changes and reload the prefab from disk", async () => await RevertInteractive());
            var place = ToolButton("Plus", "Place in Scene", "Drop a linked instance of this prefab into the active scene", async () => await PlaceInteractive());
            var large = ToolButton("Fullscreen", null, "Large preview", () => { if (_dirty) EditorCommands.Toast("The large preview shows the saved prefab"); EditorWindows.AssetViewer(_path); });
            var more = ToolButton("More", null, "More prefab actions", (Action)null);
            more.Click += (s, e) => MoreMenu().ShowAt(more);
            var help = ToolButton("Info", null, "How prefabs work", (Action)null);
            help.Click += (s, e) => new Flyout { Content = new TextBlock { Text = PrefabService.WorkflowHelp, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(4) } }.ShowAt(help);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(place); right.Children.Add(large); right.Children.Add(help); right.Children.Add(more);
            right.Children.Add(new Border { Width = 1, Height = 20, Background = PrefabWorkflow.Res("VxSeparatorBrush"), Margin = new Thickness(4, 0) });
            right.Children.Add(_revert); right.Children.Add(_save);
            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
            titleStack.Children.Add(_title); titleStack.Children.Add(_subtitle);
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new VxIcon { Icon = "Prefab", Width = 22, Height = 22, Foreground = PrefabWorkflow.Res("VxAccentBrush") });
            left.Children.Add(titleStack);
            var bar = new DockPanel { Margin = new Thickness(14, 8, 12, 8) };
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right); bar.Children.Add(left);
            var toolbar = new Border { Classes = { "hairline-bottom" }, Background = PrefabWorkflow.Res("VxToolbarBrush"), Child = bar };

            // ------------------------------------------------------------ left: entity tree + options
            var addChild = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Plus" } };
            ToolTip.SetTip(addChild, "Add a child entity to the selected entity");
            addChild.Click += (s, e) => AddChildMenu(SelectedEntity ?? _root).ShowAt(addChild);
            var delete = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Trash" } };
            ToolTip.SetTip(delete, "Delete the selected child entity");
            delete.Click += (s, e) => DeleteEntity(SelectedEntity);
            var treeHeaderButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            treeHeaderButtons.Children.Add(addChild); treeHeaderButtons.Children.Add(delete);
            var treeHeader = new DockPanel();
            DockPanel.SetDock(treeHeaderButtons, Dock.Right);
            treeHeader.Children.Add(treeHeaderButtons);
            treeHeader.Children.Add(new TextBlock { Text = "Prefab Entities", Classes = { "headertitle" } });
            var treeHeaderBorder = new Border { Classes = { "panelheader" }, Child = treeHeader };

            _tree.ItemTemplate = new FuncTreeDataTemplate<GameEntity>((e, ns) => TreeRow(e), e => e.Children);
            _tree.SelectionChanged += (s, e) => OnTreeSelection();
            _tree.KeyDown += OnTreeKey;
            DragDrop.SetAllowDrop(_tree, true);
            _tree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
            _tree.AddHandler(DragDrop.DropEvent, OnTreeDrop);
            ToolTip.SetTip(_tree, "Drag to re-parent (⇧ after / ⌥ before the target) · drop scripts, models or prefabs from the Project panel");

            _solid.Content = new TextBlock { Text = "Solid — blocks the player", Classes = { "small" } };
            ToolTip.SetTip(_solid, "Adds an exact-shape Mesh Collider (Is Trigger off) to every mesh entity of this prefab; unticking removes those. Save to update the placed instances.");
            _solid.IsCheckedChanged += (s, e) => { if (!_syncSolid && _root != null) { SetSolid(_root, _solid.IsChecked == true); _inspector.Refresh(); } };
            var options = new StackPanel { Spacing = 6, Margin = new Thickness(10, 8, 10, 10) };
            options.Children.Add(_solid);
            _materialsBox.Children.Add(new TextBlock { Text = "MATERIALS", Classes = { "small", "tertiary" }, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 8, 0, 2) });
            _materialsBox.Children.Add(_materials);
            options.Children.Add(_materialsBox);
            var leftDock = new DockPanel();
            DockPanel.SetDock(treeHeaderBorder, Dock.Top);
            var optionsBorder = new Border { Classes = { "hairline-top" }, Child = new ScrollViewer { Content = options, MaxHeight = 280 } };
            DockPanel.SetDock(optionsBorder, Dock.Bottom);
            leftDock.Children.Add(treeHeaderBorder);
            leftDock.Children.Add(optionsBorder);
            leftDock.Children.Add(_tree);
            var leftPane = new Border { Classes = { "sidebar", "hairline-right" }, Child = leftDock };

            // ------------------------------------------------------------ centre: live preview
            _preview.Scene = _builder.Scene;
            _highlight = new ToggleButton { Classes = { "icon", "small" }, IsChecked = true, Content = new VxIcon { Icon = "Crosshair", Width = 12, Height = 12 } };
            ToolTip.SetTip(_highlight, "Box the selected entity in the preview");
            _highlight.IsCheckedChanged += (s, e) => { _builder.ShowHighlight = _highlight.IsChecked == true; UpdateHighlight(); };
            var frame = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = "Home", Width = 12, Height = 12 } };
            ToolTip.SetTip(frame, "Reset the view (or double-click the preview)");
            frame.Click += (s, e) => _preview.ResetView();
            var overlayButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 8, 0) };
            overlayButtons.Children.Add(_highlight); overlayButtons.Children.Add(frame);
            _stats.Margin = new Thickness(10, 8, 0, 0);
            _stats.HorizontalAlignment = HorizontalAlignment.Left; _stats.VerticalAlignment = VerticalAlignment.Top;
            var center = new Grid();
            center.Children.Add(_preview);
            center.Children.Add(_stats);
            center.Children.Add(overlayButtons);
            var reload = new Button { Content = "Reload", Classes = { "ghost" }, Margin = new Thickness(8, 0, 0, 0) };
            reload.Click += (s, e) => { ReloadFromDisk(); };
            var keep = new Button { Content = "Keep mine", Classes = { "ghost" } };
            keep.Click += (s, e) => _banner.IsVisible = false;
            var bannerRow = new DockPanel();
            var bannerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            bannerButtons.Children.Add(reload); bannerButtons.Children.Add(keep);
            DockPanel.SetDock(bannerButtons, Dock.Right);
            bannerRow.Children.Add(bannerButtons);
            bannerRow.Children.Add(new TextBlock { Text = "The prefab file changed on disk (Apply to Prefab / Save as Prefab). Reload discards your unsaved edits.", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Classes = { "small" } });
            _banner.Child = bannerRow;
            _banner.Background = PrefabWorkflow.Res("VxAccentSoftBrush");
            var centerDock = new DockPanel();
            DockPanel.SetDock(_banner, Dock.Top);
            centerDock.Children.Add(_banner);
            centerDock.Children.Add(center);

            // ------------------------------------------------------------ right: isolated inspector
            var inspectorHeader = new Border { Classes = { "panelheader" }, Child = new TextBlock { Text = "Inspector", Classes = { "headertitle" } } };
            var rightDock = new DockPanel();
            DockPanel.SetDock(inspectorHeader, Dock.Top);
            rightDock.Children.Add(inspectorHeader);
            rightDock.Children.Add(_inspector);
            var rightPane = new Border { Classes = { "panel", "hairline-left" }, Child = rightDock };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("250,5,*,5,340") };
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

            BindRoot(selectRoot: true);
            AddHandler(KeyDownEvent, OnWindowKey, RoutingStrategies.Tunnel);
            Activated += (s, e) => UpdateSubtitle();
            Closing += OnClosing;
            Closed += OnClosed;
        }

        // ================================================================ load / bind
        private void LoadRoot()
        {
            var root = SceneService.Instance.LoadEntityFromPrefab(_path);
            if (root == null) throw new InvalidDataException("The prefab is empty or unreadable.");
            root.PrefabPath = null;   // the TEMPLATE, not an instance
            _root = root;
            _savedJson = DataSerializer.ToJson(root);
            _dirty = false;
        }

        private void BindRoot(bool selectRoot, List<int> selectPath = null)
        {
            _watcher?.Dispose();
            _watcher = new EntityTreeWatcher(_root, 120);
            _watcher.Changed += OnTemplateChanged;
            _tree.ItemsSource = new ObservableCollection<GameEntity> { _root };
            _builder.Highlight = null;
            _builder.Rebuild(_root);
            _preview.Invalidate();
            UpdateAll();
            Dispatcher.UIThread.Post(() =>
            {
                try { if (_tree.TreeContainerFromItem(_root) is TreeViewItem tvi) _tree.ExpandSubTree(tvi); } catch { }
                var target = selectPath != null ? ByPath(selectPath) : null;
                SelectEntity(target ?? _root);
            }, DispatcherPriority.Background);
        }

        private void OnTemplateChanged()
        {
            if (_root == null) return;
            _builder.Rebuild(_root);
            _preview.Invalidate();
            UpdateAll();
        }

        private void UpdateAll()
        {
            string now = null;
            try { now = DataSerializer.ToJson(_root); } catch { }
            _dirty = now != null && now != _savedJson;
            UpdateTitle();
            UpdateSubtitle();
            UpdateStats();
            UpdateMaterials();
            _syncSolid = true; _solid.IsChecked = HasSolidCollider(_root); _syncSolid = false;
        }

        private void UpdateTitle()
        {
            _title.Text = (_dirty ? "● " : "") + _name;
            Title = "Prefab — " + _name + (_dirty ? " (edited)" : "");
            if (_save != null) _save.IsEnabled = true;
            if (_revert != null) _revert.IsEnabled = _dirty;
        }

        private void UpdateSubtitle()
        {
            int n = 0;
            try { n = PrefabWorkflow.InstancesOf(_path).Count; } catch { }
            _subtitle.Text = PrefabWorkflow.Relative(_path) + "  ·  " + (n == 0 ? "not placed in the open scenes" : n + " instance" + (n == 1 ? "" : "s") + " in the open scenes");
        }

        private void UpdateStats()
        {
            int entities = 0, parts = 0;
            void Count(GameEntity e) { if (e == null) return; entities++; var mr = e.GetComponent<MeshRenderer>(); if (mr != null && !string.IsNullOrEmpty(mr.MeshPath)) parts++; foreach (var c in e.Children) Count(c); }
            Count(_root);
            _stats.Text = entities + (entities == 1 ? " entity" : " entities") + "  ·  " + parts + (parts == 1 ? " mesh part" : " mesh parts") + (_builder.MeshItems == 0 ? "  ·  nothing to draw" : "");
        }

        // ================================================================ tree
        private Control TreeRow(GameEntity e)
        {
            var row = new DockPanel { Height = 24, Background = Brushes.Transparent };
            if (e.IsPrefabInstance)
            {
                var nested = new VxIcon { Icon = "Prefab", Width = 12, Height = 12, Foreground = PrefabWorkflow.Res("VxAccentBrush"), Margin = new Thickness(4, 0, 0, 0) };
                ToolTip.SetTip(nested, "Nested prefab: " + e.PrefabPath);
                DockPanel.SetDock(nested, Dock.Right);
                row.Children.Add(nested);
            }
            var icon = new VxIcon { Icon = EntityIconConverter.IconFor(e), Margin = new Thickness(0, 0, 6, 0) };
            icon.Foreground = EntityIconBrushConverter.Instance.Convert(e, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture) as IBrush;
            var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(GameEntity.Name)));
            if (ReferenceEquals(e, _root)) name.FontWeight = FontWeight.SemiBold;
            if (e.IsLockedToParent) ToolTip.SetTip(name, "Part of an imported model (moves with its parent)");
            row.Bind(OpacityProperty, new Binding(nameof(GameEntity.IsActive)) { Converter = new FuncValueConverter<bool, double>(a => a ? 1.0 : 0.45) });
            row.Children.Add(icon);
            row.Children.Add(name);
            row.PointerPressed += (s, a) =>
            {
                var pt = a.GetCurrentPoint(row);
                if (pt.Properties.IsLeftButtonPressed && !ReferenceEquals(e, _root)) _dragFrom = (e, a.GetPosition(this));
                if (!pt.Properties.IsRightButtonPressed) return;
                SelectEntity(e);
                EntityMenu(e).ShowAt(row, true);
                a.Handled = true;
            };
            row.PointerMoved += async (s, a) =>
            {
                if (_dragFrom == null || !ReferenceEquals(_dragFrom.Value.e, e)) return;
                if (!a.GetCurrentPoint(row).Properties.IsLeftButtonPressed) { _dragFrom = null; return; }
                var p = a.GetPosition(this);
                if (Math.Abs(p.X - _dragFrom.Value.at.X) < 6 && Math.Abs(p.Y - _dragFrom.Value.at.Y) < 6) return;
                _dragFrom = null;
                var data = new DataObject();
                data.Set("vortex/prefab-entity", e);
                try { await DragDrop.DoDragDrop(a, data, DragDropEffects.Move); } catch { }
            };
            row.PointerReleased += (s, a) => _dragFrom = null;
            return row;
        }

        private (GameEntity e, Point at)? _dragFrom;

        private GameEntity DropTarget(DragEventArgs e) => ((e.Source as Control)?.DataContext as GameEntity) ?? _root;

        private static bool IsUnder(GameEntity e, GameEntity ancestor) { for (var p = e; p != null; p = p.Parent) if (ReferenceEquals(p, ancestor)) return true; return false; }

        private static readonly string[] ScriptOrAssets = { ".cs", ".ventity", ".glb", ".gltf", ".fbx", ".obj", ".dae", ".3ds", ".blend" };

        private void OnTreeDragOver(object s, DragEventArgs e)
        {
            var target = DropTarget(e);
            if (e.Data.Get("vortex/prefab-entity") is GameEntity dragged)
                e.DragEffects = target != null && !IsUnder(target, dragged) ? DragDropEffects.Move : DragDropEffects.None;
            else
            {
                var p = Panels.Inspector.PropertyRows.DroppedPath(e, "vortex/asset");
                e.DragEffects = p != null && Array.IndexOf(ScriptOrAssets, Path.GetExtension(p).ToLowerInvariant()) >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void OnTreeDrop(object s, DragEventArgs e)
        {
            var target = DropTarget(e);
            if (target == null) return;
            e.Handled = true;
            if (e.Data.Get("vortex/prefab-entity") is GameEntity dragged)
            {
                if (IsUnder(target, dragged) || dragged.Parent == null) return;
                bool after = e.KeyModifiers.HasFlag(KeyModifiers.Shift), before = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                dragged.Parent.Children.Remove(dragged);
                if ((after || before) && target.Parent != null)
                {
                    var p = target.Parent;
                    p.Children.Insert(Math.Max(0, p.Children.IndexOf(target) + (after ? 1 : 0)), dragged);
                    dragged.Parent = p;
                }
                else { target.Children.Add(dragged); dragged.Parent = target; }
                Dispatcher.UIThread.Post(() => SelectEntity(dragged), DispatcherPriority.Background);
                return;
            }
            var path = Panels.Inspector.PropertyRows.DroppedPath(e, "vortex/asset");
            if (string.IsNullOrEmpty(path)) return;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string rel = PrefabWorkflow.Relative(path);
            try
            {
                if (ext == ".cs")
                {
                    if (Path.IsPathRooted(rel)) { EditorCommands.Toast("Import the script into the project first"); return; }
                    if (!target.Components.OfType<Editor.ECS.Components.Scripting.Script>().Any(x => string.Equals(x.ScriptPath, rel, StringComparison.OrdinalIgnoreCase)))
                        target.AddComponentDirect(new Editor.ECS.Components.Scripting.Script(target, rel));
                    SelectEntity(target);
                    _inspector.Refresh();
                }
                else if (ext == ".ventity")
                {
                    if (PrefabWorkflow.SamePath(path, _path)) { EditorCommands.Toast("A prefab can't contain itself"); return; }
                    var nested = PrefabWorkflow.LoadNestedInstance(path);
                    if (nested == null) return;
                    nested.Name = UniqueChildName(target, nested.Name);
                    nested.Parent = target; target.Children.Add(nested);
                    Dispatcher.UIThread.Post(() => SelectEntity(nested), DispatcherPriority.Background);
                }
                else if (PrefabWorkflow.IsModelFile(path))
                {
                    if (Path.IsPathRooted(rel)) { EditorCommands.Toast("Import the model into the project first"); return; }
                    var model = PrefabWorkflow.BuildModelEntity(path);
                    model.Name = UniqueChildName(target, model.Name);
                    model.Parent = target; target.Children.Add(model);
                    Dispatcher.UIThread.Post(() => SelectEntity(model), DispatcherPriority.Background);
                }
            }
            catch (Exception ex) { EditorCommands.Fail("Drop into prefab", ex); }
        }

        private void OnTreeSelection()
        {
            var sel = SelectedEntity ?? _root;
            _inspector.SetEntity(sel);
            UpdateHighlight();
        }

        private void UpdateHighlight()
        {
            var sel = SelectedEntity;
            _builder.Highlight = ReferenceEquals(sel, _root) ? null : sel;
            _builder.Scene.RenderGizmos = _builder.ShowHighlight && _builder.Highlight != null;
            _preview.Invalidate();
        }

        /// <summary>Select an entity of the template (expands its parents).</summary>
        public void SelectEntity(GameEntity e)
        {
            if (e == null) return;
            var chain = new List<GameEntity>();
            for (var p = e.Parent; p != null; p = p.Parent) chain.Insert(0, p);
            foreach (var a in chain) { try { if (_tree.TreeContainerFromItem(a) is TreeViewItem c) c.IsExpanded = true; } catch { } }
            _tree.SelectedItem = e;
            if (!ReferenceEquals(_inspector.Entity, e)) { _inspector.SetEntity(e); UpdateHighlight(); }
        }

        private List<int> PathOf(GameEntity e)
        {
            var path = new List<int>();
            for (var x = e; x != null && x.Parent != null; x = x.Parent) path.Insert(0, x.Parent.Children.IndexOf(x));
            return path;
        }

        private GameEntity ByPath(List<int> path)
        {
            var x = _root;
            foreach (int i in path) { if (x == null || i < 0 || i >= x.Children.Count) return null; x = x.Children[i]; }
            return x;
        }

        private MenuFlyout EntityMenu(GameEntity e)
        {
            var m = new MenuFlyout();
            bool isRoot = ReferenceEquals(e, _root);
            foreach (var item in AddChildItems(e)) m.Items.Add(item);
            m.Items.Add(new Separator());
            m.Items.Add(MI("Rename…", async () => await RenameEntity(e)));
            if (!isRoot)
            {
                m.Items.Add(MI("Duplicate", () => DuplicateEntity(e)));
                var up = MI("Move Up", () => MoveEntity(e, -1)); up.IsEnabled = e.Parent != null && e.Parent.Children.IndexOf(e) > 0;
                var down = MI("Move Down", () => MoveEntity(e, 1)); down.IsEnabled = e.Parent != null && e.Parent.Children.IndexOf(e) < e.Parent.Children.Count - 1;
                m.Items.Add(up); m.Items.Add(down);
                var outOf = MI("Move Out of “" + e.Parent?.Name + "”", () => Unparent(e)); outOf.IsEnabled = e.Parent != null && e.Parent.Parent != null;
                m.Items.Add(outOf);
                m.Items.Add(new Separator());
                m.Items.Add(MI("Delete", () => DeleteEntity(e)));
            }
            return m;
        }

        private MenuFlyout AddChildMenu(GameEntity parent)
        {
            var m = new MenuFlyout();
            foreach (var item in AddChildItems(parent)) m.Items.Add(item);
            return m;
        }

        private IEnumerable<Control> AddChildItems(GameEntity parent)
        {
            yield return MI("Add Empty Child", () => AddChild(parent, "Entity", null));
            var prim = new MenuItem { Header = "Add Primitive Child" };
            foreach (var p in new[] { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" }) { var pp = p; prim.Items.Add(MI(p, () => AddChild(parent, p, pp))); }
            yield return prim;
            var light = new MenuItem { Header = "Add Light Child" };
            foreach (var t in new[] { Editor.ECS.Components.Lighting.LightType.Point, Editor.ECS.Components.Lighting.LightType.Spot, Editor.ECS.Components.Lighting.LightType.Directional })
            {
                var tt = t;
                light.Items.Add(MI(t + " Light", () => { var c = AddChild(parent, t + " Light", null); c.AddComponentDirect(new Editor.ECS.Components.Lighting.Light(c, tt)); }));
            }
            yield return light;
        }

        private static MenuItem MI(string header, Action a)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => { try { a(); } catch (Exception ex) { EditorCommands.Fail(header, ex); } };
            return mi;
        }
        private static MenuItem MI(string header, Func<System.Threading.Tasks.Task> a) => MI(header, () => { _ = a(); });

        private static string UniqueChildName(GameEntity parent, string baseName)
        {
            var names = new HashSet<string>(parent.Children.Select(c => c.Name ?? ""), StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(baseName)) return baseName;
            for (int i = 1; ; i++) { string n = baseName + " (" + i + ")"; if (!names.Contains(n)) return n; }
        }

        /// <summary>Add a child to an entity of the template (optionally a primitive mesh). Returns the child.</summary>
        public GameEntity AddChild(GameEntity parent, string name, string primitive)
        {
            parent = parent ?? _root;
            if (parent == null) return null;
            var child = new GameEntity(UniqueChildName(parent, name ?? "Entity"));
            if (!string.IsNullOrEmpty(primitive)) child.AddComponentDirect(new MeshRenderer(child) { MeshPath = "Primitive:" + primitive });
            child.Parent = parent;
            parent.Children.Add(child);
            try { if (_tree.TreeContainerFromItem(parent) is TreeViewItem tvi) tvi.IsExpanded = true; } catch { }
            Dispatcher.UIThread.Post(() => SelectEntity(child), DispatcherPriority.Background);
            return child;
        }

        public void DuplicateEntity(GameEntity e)
        {
            if (e == null || e.Parent == null) return;
            var parent = e.Parent;
            GameEntity copy;
            try { copy = DataSerializer.FromJson<GameEntity>(DataSerializer.ToJson(e)); }
            catch (Exception ex) { EditorCommands.Fail("Duplicate", ex); return; }
            copy.RegenerateIds();
            copy.Name = UniqueChildName(parent, e.Name);
            copy.Parent = parent;
            parent.Children.Insert(parent.Children.IndexOf(e) + 1, copy);
            Dispatcher.UIThread.Post(() => SelectEntity(copy), DispatcherPriority.Background);
        }

        public void DeleteEntity(GameEntity e)
        {
            if (e == null || e.Parent == null || ReferenceEquals(e, _root)) return;
            var parent = e.Parent;
            int i = parent.Children.IndexOf(e);
            parent.Children.Remove(e);
            e.Parent = null;
            SelectEntity(parent.Children.Count > 0 ? parent.Children[Math.Min(i, parent.Children.Count - 1)] : parent);
        }

        private void MoveEntity(GameEntity e, int dir)
        {
            var parent = e?.Parent;
            if (parent == null) return;
            int i = parent.Children.IndexOf(e), j = i + dir;
            if (i < 0 || j < 0 || j >= parent.Children.Count) return;
            parent.Children.Move(i, j);
            Dispatcher.UIThread.Post(() => SelectEntity(e), DispatcherPriority.Background);
        }

        private void Unparent(GameEntity e)
        {
            var parent = e?.Parent; var grand = parent?.Parent;
            if (grand == null) return;
            parent.Children.Remove(e);
            e.Parent = grand;
            grand.Children.Insert(grand.Children.IndexOf(parent) + 1, e);
            Dispatcher.UIThread.Post(() => SelectEntity(e), DispatcherPriority.Background);
        }

        private async System.Threading.Tasks.Task RenameEntity(GameEntity e)
        {
            if (e == null) return;
            var n = await PrefabWorkflow.Prompt(this, "Rename", "New name for “" + e.Name + "”", e.Name, "Rename");
            if (!string.IsNullOrWhiteSpace(n)) e.Name = n.Trim();
        }

        private async void OnTreeKey(object s, KeyEventArgs e)
        {
            var sel = SelectedEntity;
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            switch (e.Key)
            {
                case Key.Delete: case Key.Back: DeleteEntity(sel); e.Handled = true; break;
                case Key.F2: case Key.Return: await RenameEntity(sel); e.Handled = true; break;
                case Key.D: if (cmd) { DuplicateEntity(sel); e.Handled = true; } break;
            }
        }

        // ================================================================ save / revert / place
        /// <summary>Write the template to the .ventity and reload every placed instance (each keeps its transform).</summary>
        public bool Save()
        {
            if (_root == null) return false;
            _saving = true;
            try
            {
                PrefabWorkflow.NormalizeAssetPaths(_root);
                _root.PrefabPath = null;
                // the scene selection follows its replacement when it was one of the reloaded instances
                var sel = SelectionService.Instance.SelectedEntity;
                var selRoot = sel != null ? PrefabWorkflow.FindInstanceRoot(sel) : null;
                bool reselect = selRoot != null && PrefabWorkflow.SamePath(selRoot.PrefabPath, _path);
                var oldPos = reselect ? selRoot.Transform?.LocalPosition : null;
                SceneService.Instance.SaveEntityAsPrefab(_root, _path);
                _savedJson = DataSerializer.ToJson(_root);
                try { PrefabService.Instance.ReloadInstancesFromPrefab(_path); } catch (Exception ex) { EditorCommands.Fail("Reload instances", ex); }
                if (reselect && oldPos.HasValue)
                {
                    var match = PrefabWorkflow.InstancesOf(_path).FirstOrDefault(x => x.Transform != null && Near(x.Transform.LocalPosition, oldPos.Value));
                    if (match != null) SelectionService.Instance.Select(match);
                }
                PrefabWorkflow.AfterPrefabWritten(_path);
                SceneRenderService.RuntimeDirty = true;
                _banner.IsVisible = false;
                UpdateAll();
                EditorCommands.Toast("Saved to prefab — every instance updated");
                return true;
            }
            catch (Exception ex) { EditorCommands.Fail("Save prefab", ex); return false; }
            finally { _saving = false; }
        }

        private static bool Near(Editor.ECS.Vector3 a, Editor.ECS.Vector3 b) => Math.Abs(a.X - b.X) < 1e-4f && Math.Abs(a.Y - b.Y) < 1e-4f && Math.Abs(a.Z - b.Z) < 1e-4f;

        /// <summary>Discard the in-memory edits and reload the template from disk (keeps the selected entity).</summary>
        public void ReloadFromDisk()
        {
            var keep = SelectedEntity != null ? PathOf(SelectedEntity) : null;
            try { LoadRoot(); }
            catch (Exception ex) { EditorCommands.Fail("Reload prefab", ex); return; }
            _banner.IsVisible = false;
            BindRoot(selectRoot: keep == null, selectPath: keep);
        }

        private async System.Threading.Tasks.Task RevertInteractive()
        {
            if (_dirty && !await PrefabWorkflow.Confirm(this, "Revert “" + _name + "”?", "Discards every unsaved change and reloads the prefab from disk.", "Revert", "Cancel", destructive: true)) return;
            ReloadFromDisk();
        }

        private async System.Threading.Tasks.Task PlaceInteractive()
        {
            if (_dirty)
            {
                int r = await PrefabWorkflow.Sheet(this, "Save before placing?", "Instances are created from the saved prefab file — your unsaved edits are not in it yet.", new[] { "Save & Place", "Place Saved Version", "Cancel" });
                if (r == 2) return;
                if (r == 0 && !Save()) return;
            }
            PrefabWorkflow.PlaceInScene(_path);
            UpdateSubtitle();
        }

        private void OnChangedOnDisk()
        {
            if (_saving || _root == null) return;
            string disk = null;
            try { disk = DataSerializer.ToJson(SceneService.Instance.LoadEntityFromPrefab(_path)); } catch { }
            if (disk != null && disk == _savedJson) return;   // same content
            if (!_dirty) ReloadFromDisk();
            else _banner.IsVisible = true;
        }

        private MenuFlyout MoreMenu()
        {
            var m = new MenuFlyout();
            string model = FirstModelPath(_root);
            var edit = MI("Edit Model…", () => { if (model != null) EditorWindows.ModelEditor(model); });
            edit.IsEnabled = model != null;
            ToolTip.SetTip(edit, model != null ? "Open " + Path.GetFileName(model) + " in the Model Editor (submeshes, materials, textures)" : "This prefab uses no imported model");
            m.Items.Add(edit);
            m.Items.Add(MI("Select in Project", () => PrefabWorkflow.SelectAsset(_path)));
            m.Items.Add(MI(OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Show in Explorer", () => EditorCommands.RevealInFinder(_path)));
            m.Items.Add(new Separator());
            m.Items.Add(MI("Reload from Disk", async () => await RevertInteractive()));
            return m;
        }

        private string FirstModelPath(GameEntity e)
        {
            if (e == null) return null;
            var mr = e.GetComponent<MeshRenderer>();
            var p = mr?.MeshPath;
            if (!string.IsNullOrEmpty(p) && !p.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
            {
                int hash = p.LastIndexOf('#');
                string file = hash > 0 ? p.Substring(0, hash) : p;
                string full = PrefabWorkflow.Resolve(file);
                if (File.Exists(full)) return full;
            }
            foreach (var c in e.Children) { var r = FirstModelPath(c); if (r != null) return r; }
            return null;
        }

        // ================================================================ solid + materials
        private static bool HasSolidCollider(GameEntity e)
        {
            if (e == null) return false;
            var col = e.GetComponent<Collider>();
            if (col != null && !col.IsTrigger) return true;
            foreach (var c in e.Children) if (HasSolidCollider(c)) return true;
            return false;
        }

        /// <summary>"Solid": on = a non-trigger exact-shape Mesh Collider on every mesh entity (the collision service builds
        /// shapes from the entity's own MeshRenderer, so the root container would produce none); off = remove exactly those.</summary>
        private static void SetSolid(GameEntity e, bool solid)
        {
            if (e == null) return;
            var mr = e.GetComponent<MeshRenderer>();
            var col = e.GetComponent<Collider>();
            if (solid)
            {
                if (mr != null && col == null) e.AddComponentDirect(new MeshCollider(e) { IsTrigger = false });
                else if (col != null && col.IsTrigger) col.IsTrigger = false;
            }
            else if (col is MeshCollider && !col.IsTrigger) e.Components.Remove(col);
            foreach (var c in e.Children.ToList()) SetSolid(c, solid);
        }

        private void UpdateMaterials()
        {
            _materials.Children.Clear();
            var parts = new List<(string entity, string mat, string vmat, bool primitive)>();
            void Collect(GameEntity e)
            {
                if (e == null) return;
                var mr = e.GetComponent<MeshRenderer>();
                if (mr != null && !string.IsNullOrEmpty(mr.MeshPath))
                {
                    bool prim = mr.MeshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase);
                    string rel = mr.MaterialPath, full = null, name = "Default";
                    if (!string.IsNullOrEmpty(rel) && !rel.StartsWith("Material:", StringComparison.OrdinalIgnoreCase))
                    {
                        full = PrefabWorkflow.Resolve(rel);
                        name = Path.GetFileNameWithoutExtension(rel);
                        if (!File.Exists(full)) full = null;
                    }
                    if (prim || !string.IsNullOrEmpty(rel)) parts.Add((e.Name, name, full, prim));
                }
                foreach (var c in e.Children) Collect(c);
            }
            Collect(_root);
            _materialsBox.IsVisible = parts.Count > 0;
            foreach (var p in parts)
            {
                string vmat = p.vmat, matName = p.mat;
                bool missing = vmat == null && matName != "Default";
                var b = new Button { Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 2) };
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                sp.Children.Add(new VxIcon { Icon = "Material", Width = 13, Height = 13, Foreground = PrefabWorkflow.Res(missing ? "VxRedBrush" : "VxAccentBrush") });
                var texts = new StackPanel();
                texts.Children.Add(new TextBlock { Text = matName, Classes = { "small" } });
                texts.Children.Add(new TextBlock { Text = p.entity + (vmat != null ? " · open in Material Editor" : missing ? " · .vmat file missing" : " · inline colour (no .vmat)"), Classes = { "small", "tertiary" } });
                sp.Children.Add(texts);
                b.Content = sp;
                b.Click += async (s, e) =>
                {
                    if (vmat != null) EditorWindows.MaterialEditor(vmat);
                    else if (missing) await PrefabWorkflow.Sheet(this, "Material file missing", "The material '" + matName + "' is assigned to this mesh part but its .vmat file was not found — the part renders with the default (white) material until the file is restored or another material is assigned.", new[] { "OK" });
                    else await PrefabWorkflow.Sheet(this, "No material assigned", "This mesh part has no .vmat material — it renders with its inline base colour. Assign a material in the inspector (Material) to give it a named, editable material.", new[] { "OK" });
                };
                _materials.Children.Add(b);
            }
            if (parts.Any(x => x.primitive))
                _materials.Children.Add(new TextBlock { Text = "Footsteps follow the material NAME — the game's footstep script maps each name to a step sound.", Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 0, 0) });
        }

        // ================================================================ window plumbing
        private Button ToolButton(string icon, string text, string tip, Action click, bool accent = false)
        {
            var b = new Button();
            if (text == null) { b.Classes.Add("icon"); b.Content = new VxIcon { Icon = icon }; }
            else
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                sp.Children.Add(new VxIcon { Icon = icon, Width = 13, Height = 13 });
                sp.Children.Add(new TextBlock { Text = text });
                b.Content = sp;
                b.Classes.Add(accent ? "accent" : "ghost");
            }
            ToolTip.SetTip(b, tip);
            if (click != null) b.Click += (s, e) => { try { click(); } catch (Exception ex) { EditorCommands.Fail(text ?? tip, ex); } };
            return b;
        }
        private Button ToolButton(string icon, string text, string tip, Func<System.Threading.Tasks.Task> click, bool accent = false) => ToolButton(icon, text, tip, () => { _ = click(); }, accent);

        private Control ErrorContent(string message)
        {
            var sp = new StackPanel { Spacing = 10, Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 460 };
            sp.Children.Add(new VxIcon { Icon = "Warning", Width = 34, Height = 34, Foreground = PrefabWorkflow.Res("VxOrangeBrush") });
            sp.Children.Add(new TextBlock { Text = "Could not open “" + _name + "”", FontWeight = FontWeight.SemiBold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = message, Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            var reveal = new Button { Content = OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Show in Explorer", HorizontalAlignment = HorizontalAlignment.Center };
            reveal.Click += (s, e) => EditorCommands.RevealInFinder(_path);
            sp.Children.Add(reveal);
            return sp;
        }

        private void OnWindowKey(object s, KeyEventArgs e)
        {
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (!cmd) return;
            if (e.Key == Key.S) { Save(); e.Handled = true; }
            else if (e.Key == Key.W) { Close(); e.Handled = true; }
        }

        /// <summary>Close without the unsaved-changes prompt (edits are discarded).</summary>
        public void CloseWithoutSaving() { _closingConfirmed = true; Close(); }

        private async void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (_closingConfirmed || !_dirty || _root == null) return;
            e.Cancel = true;
            int r = await PrefabWorkflow.Sheet(this, "Save changes to “" + _name + "”?", "Your changes to the prefab are lost if you don't save them.", new[] { "Save", "Don't Save", "Cancel" }, destructiveIndex: 1);
            if (r == 2) return;
            if (r == 0 && !Save()) return;
            _closingConfirmed = true;
            Close();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _open.Remove(PathKey(_path));
            _watcher?.Dispose();
            _inspector.Release();
            _preview.Scene = null;
            _builder.Dispose();
        }
    }
}
