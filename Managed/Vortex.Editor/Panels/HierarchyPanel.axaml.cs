using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;
using Editor.Editors.WorldEditor.Components.SceneHierarchy;
using VortexEditor.Controls;
using VortexEditor.Shell;

namespace VortexEditor.Panels
{
    /// <summary>
    /// Scene hierarchy (port of the WPF SceneHierarchyView): every scene of the project with its entity tree;
    /// multi-select (⌘/⇧ click), drag &amp; drop to reparent (drop on a row), reorder (drop on the upper / lower edge
    /// of a row) or move to another scene / the root — one undo step per drop; asset drops (models, prefabs,
    /// primitives, scripts, materials); inline rename (↩ / F2); context menus for create / clipboard / prefab /
    /// camera / socket &amp; collision editors; search; eye toggle (editor-only visibility); FP / 3P / prefab / part chips.
    /// </summary>
    public partial class HierarchyPanel : UserControl
    {
        public const string EntityFormat = "vortex/entity";
        public enum DropPosition { Before, Inside, After }

        private SceneHierarchyViewModel Vm => EditorSession.Instance.Hierarchy;
        private bool _syncing;
        private Point _pressPos;
        private GameEntity _pressEntity, _deferredSelect;
        private bool _dragging;
        private static List<GameEntity> _dragEntities;          // in-process payload of an entity drag
        private GameEntity _renaming;
        private TextBox _renameBox;
        private TextBlock _renameLabel;

        public HierarchyPanel()
        {
            InitializeComponent();
            DragDrop.SetAllowDrop(TreeHost, true);
            TreeHost.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            TreeHost.AddHandler(DragDrop.DragLeaveEvent, (s, e) => HideDropIndicator());
            TreeHost.AddHandler(DragDrop.DropEvent, OnDrop);
            Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
            Tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
            Tree.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Tunnel);
            Tree.SelectionChanged += OnTreeSelectionChanged;
            Tree.DoubleTapped += OnTreeDoubleTapped;
            Tree.KeyDown += OnTreeKeyDown;
            Tree.AddHandler(KeyDownEvent, OnRenameBoxKeyDown, RoutingStrategies.Tunnel);
            Tree.AddHandler(LostFocusEvent, OnRenameBoxLostFocus, RoutingStrategies.Bubble);
            SearchResults.SelectionChanged += OnSearchResultSelected;
            SearchResults.DoubleTapped += (s, e) => { if (SearchResults.SelectedItem is GameEntity ge) { EditorCommands.FocusSelected(); } };
            SearchBox.KeyDown += (s, e) => { if (e.Key == Key.Escape) { SearchBox.Text = ""; FocusTree(); e.Handled = true; } else if (e.Key == Key.Down && SearchResults.IsVisible && SearchResults.ItemCount > 0) { SearchResults.SelectedIndex = 0; SearchResults.Focus(); e.Handled = true; } };
            SelectionService.Instance.SelectionChanged += OnSelectionServiceChanged;
            EditorSession.Instance.ProjectOpened += _ => Dispatcher.UIThread.Post(Reload);
            EditorSession.Instance.ProjectClosed += () => Dispatcher.UIThread.Post(Reload);
            Reload();
        }

        public void Reload()
        {
            var project = ProjectData.Current;
            CancelRename();
            Tree.ItemsSource = project?.Scenes;
            EmptyHint.IsVisible = project == null;
            if (!string.IsNullOrEmpty(SearchBox.Text)) OnSearchChanged(null, null);
            if (project?.ActiveScene != null) project.ActiveScene.IsExpanded = true;
        }

        public void FocusSearch() { SearchBox.Focus(); SearchBox.SelectAll(); }
        public void FocusTree() { try { Tree.Focus(); } catch { } }

        // ================================================================ selection sync

        private void OnSelectionServiceChanged(object sender, SelectionEventArgs e)
        {
            if (_syncing) return;
            Dispatcher.UIThread.Post(() =>
            {
                var ent = SelectionService.Instance.SelectedEntity;
                if (ent == null)
                {
                    if (SelectionService.Instance.SelectedScene != null && Tree.SelectedItems.Contains(SelectionService.Instance.SelectedScene)) return;
                    if (Tree.SelectedItems.Count > 0) SetTreeSelection(Array.Empty<object>());
                    return;
                }
                if (Tree.SelectedItems.Contains(ent)) return;   // already part of the (multi) selection
                SelectEntities(new[] { ent });
            });
        }

        /// <summary>Select these entities in the tree (reveals them) and in the view model; the last one is primary.</summary>
        /// <param name="reveal">expand the parents and scroll to the last entity (off for Select All, which would
        /// otherwise unfold the whole tree)</param>
        public void SelectEntities(IList<GameEntity> entities, bool reveal = true)
        {
            if (entities == null) return;
            if (reveal) foreach (var e in entities) ExpandTo(e);
            // containers of freshly expanded parents are realised on the next layout pass
            Dispatcher.UIThread.Post(() =>
            {
                SetTreeSelection(entities.Cast<object>().ToList());
                if (reveal && entities.Count > 0) _ = RealizeContainer(entities[entities.Count - 1]);   // scroll the (virtualised) row into view
            }, DispatcherPriority.Background);
        }

        private void SetTreeSelection(IList<object> items)
        {
            _syncing = true;
            try
            {
                Tree.SelectedItems.Clear();
                foreach (var i in items) Tree.SelectedItems.Add(i);
                SyncViewModel(items.OfType<GameEntity>().ToList(), items.OfType<GameEntity>().LastOrDefault());
            }
            finally { _syncing = false; }
        }

        private void OnTreeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                var items = Tree.SelectedItems.Cast<object>().ToList();
                var ents = items.OfType<GameEntity>().ToList();
                var primary = e.AddedItems.OfType<GameEntity>().LastOrDefault();
                if (primary == null || !ents.Contains(primary))
                    primary = ents.Contains(SelectionService.Instance.SelectedEntity) ? SelectionService.Instance.SelectedEntity : ents.LastOrDefault();
                SyncViewModel(ents, primary);
                if (primary != null)
                {
                    if (!ReferenceEquals(SelectionService.Instance.SelectedEntity, primary)) SelectionService.Instance.Select(primary);
                }
                else if (items.LastOrDefault() is Scene sc) { Vm.SelectedScene = sc; SelectionService.Instance.Select(sc); }
                else SelectionService.Instance.ClearSelection();
            }
            finally { _syncing = false; }
            // a locked submesh part selects its model: show that in the tree too
            var sel = SelectionService.Instance.SelectedEntity;
            if (sel != null && !Tree.SelectedItems.Contains(sel)) Dispatcher.UIThread.Post(() => SelectEntities(new[] { sel }));
        }

        private void SyncViewModel(List<GameEntity> ents, GameEntity primary)
        {
            var vm = Vm;
            foreach (var old in vm.SelectedEntities.ToList()) if (!ents.Contains(old)) old.IsSelected = false;
            foreach (var n in ents) n.IsSelected = true;
            vm.SelectedEntities = new ObservableCollection<GameEntity>(ents);
            var scene = primary?.Scene ?? ents.FirstOrDefault()?.Scene;
            if (scene != null && !ReferenceEquals(vm.SelectedScene, scene)) vm.SelectedScene = scene;
            if (primary != null) vm.SelectedEntity = primary;
        }

        /// <summary>Selected entities in the tree (all levels).</summary>
        public List<GameEntity> SelectedTreeEntities() => Tree.SelectedItems.OfType<GameEntity>().ToList();

        // ================================================================ reveal / expand

        /// <summary>Expand the path to an entity or scene and scroll it into view (does not change the selection).</summary>
        public void Reveal(object item)
        {
            if (item is GameEntity ge) ExpandTo(ge);
            else if (item is Scene sc) sc.IsExpanded = true;
            Dispatcher.UIThread.Post(() => BringIntoView(item), DispatcherPriority.Background);
        }

        private static void ExpandTo(GameEntity ent)
        {
            if (ent == null) return;
            if (ent.Scene != null) ent.Scene.IsExpanded = true;
            for (var p = ent.Parent; p != null; p = p.Parent) p.IsExpanded = true;
        }

        private void BringIntoView(object item)
        {
            try { (Tree.TreeContainerFromItem(item) as Control)?.BringIntoView(); } catch { }
        }

        /// <summary>Make sure the row of <paramref name="item"/> exists (the tree virtualises rows): expand the path and
        /// scroll each level to the next node, from the scene down. Returns the row container (null if not possible).</summary>
        public async Task<TreeViewItem> RealizeContainer(object item)
        {
            var chain = new List<object>();
            if (item is GameEntity ge)
            {
                for (var p = ge; p != null; p = p.Parent) chain.Insert(0, p);
                if (ge.Scene != null) chain.Insert(0, ge.Scene);
                ExpandTo(ge);
            }
            else if (item != null) chain.Add(item);
            ItemsControl level = Tree;
            TreeViewItem container = null;
            foreach (var node in chain)
            {
                container = null;
                for (int attempt = 0; attempt < 8 && container == null; attempt++)
                {
                    try { level.ScrollIntoView(node); } catch { }
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    container = level.ContainerFromItem(node) as TreeViewItem;
                    if (container == null) await Task.Delay(25);
                }
                if (container == null) return null;
                if (!ReferenceEquals(node, item)) container.IsExpanded = true;
                level = container;
            }
            container?.BringIntoView();
            return container;
        }

        // ================================================================ search

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            string q = SearchBox.Text?.Trim();
            bool searching = !string.IsNullOrEmpty(q);
            SearchResults.IsVisible = searching;
            Tree.IsVisible = !searching;
            if (!searching) { SearchResults.ItemsSource = null; return; }
            var hits = new List<GameEntity>();
            var project = ProjectData.Current;
            if (project?.Scenes != null)
                foreach (var s in project.Scenes) if (s?.Entities != null) foreach (var en in s.Entities) Collect(en, q, hits);
            SearchResults.ItemsSource = hits;
        }

        private static void Collect(GameEntity e, string q, List<GameEntity> hits)
        {
            if (e == null) return;
            if (e.Name != null && e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(e);
            if (e.Children != null) foreach (var c in e.Children) Collect(c, q, hits);
        }

        private void OnSearchResultSelected(object sender, SelectionChangedEventArgs e)
        {
            if (SearchResults.SelectedItem is GameEntity ge) SelectionService.Instance.Select(ge);
        }

        // ================================================================ header buttons

        private void OnLocateClick(object sender, RoutedEventArgs e)
        {
            var ent = SelectionService.Instance.SelectedEntity;
            if (ent == null) { EditorCommands.Toast("Select an object first"); return; }
            if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";
            SelectEntities(new[] { ent });
        }

        private void OnSceneMenuClick(object sender, RoutedEventArgs e) => BuildEmptyAreaMenu().ShowAt(SceneButton);
        private void OnAddClick(object sender, RoutedEventArgs e) => BuildCreateMenu(null, null).ShowAt(AddButton);

        // ================================================================ menus

        private static MenuItem Item(string header, Action a, string gesture = null, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            if (gesture != null) { try { mi.InputGesture = KeyGesture.Parse(OperatingSystem.IsMacOS() ? gesture : gesture.Replace("Cmd", "Ctrl")); } catch { } }
            mi.Click += (s, e) => { try { a(); } catch (Exception ex) { EditorCommands.Fail(header, ex); } };
            return mi;
        }

        private static MenuItem Sub(string header, params object[] items)
        {
            var mi = new MenuItem { Header = header };
            foreach (var i in items) if (i != null) mi.Items.Add(i);
            return mi;
        }

        /// <summary>The create entries (WPF scene / add menu). <paramref name="scene"/> null = the active scene;
        /// <paramref name="parent"/> set = create as its child where that makes sense.</summary>
        private MenuFlyout BuildCreateMenu(Scene scene, GameEntity parent)
        {
            var m = new MenuFlyout();
            foreach (var i in CreateItems(scene)) m.Items.Add(i);
            return m;
        }

        private IEnumerable<object> CreateItems(Scene scene)
        {
            yield return Item("Create Empty", () => EditorCommands.CreateEmpty(scene), "Cmd+Shift+N");
            yield return Item("Player", () => EditorCommands.CreatePlayer(scene));
            yield return Item("Folder", () => EditorCommands.CreateFolder(scene));
            yield return Item("Instantiate Prefab…", () => _ = EditorCommands.InstantiatePrefab(scene));
            yield return new Separator();
            var obj = new MenuItem { Header = "3D Object" };
            foreach (var t in new[] { PrimitiveType.Cube, PrimitiveType.Sphere, PrimitiveType.Capsule, PrimitiveType.Cylinder, PrimitiveType.Plane, PrimitiveType.Quad })
            { var tt = t; obj.Items.Add(Item(t.ToString(), () => EditorCommands.CreatePrimitive(tt, scene))); }
            yield return obj;
            yield return Sub("Light",
                Item("Directional Light", () => EditorCommands.CreateLight(LightType.Directional, scene)),
                Item("Point Light", () => EditorCommands.CreateLight(LightType.Point, scene)),
                Item("Spot Light", () => EditorCommands.CreateLight(LightType.Spot, scene)),
                new Separator(),
                Item("Skybox", () => EditorCommands.CreateSkybox(scene)));
            yield return Item("Camera", () => EditorCommands.CreateCamera(scene));
            yield return Sub("Audio",
                Item("Audio Source", () => EditorCommands.CreateAudioSource(scene)),
                Item("Reverb Zone", () => EditorCommands.CreateReverbZone(scene)));
            var ui = new MenuItem { Header = "UI" };
            foreach (var k in new[] { "Canvas", "Text", "Image", "Button" }) { var kk = k; ui.Items.Add(Item(k, () => EditorCommands.CreateUI(kk, scene))); }
            yield return ui;
        }

        private MenuFlyout BuildEntityMenu(GameEntity ent)
        {
            var sel = EditorCommands.SelectedEntities();
            int n = sel.Count;
            var m = new MenuFlyout();
            m.Items.Add(Item("Cut", () => EditorCommands.Cut(), "Cmd+X"));
            m.Items.Add(Item("Copy", () => EditorCommands.Copy(), "Cmd+C"));
            m.Items.Add(Item("Paste", () => EditorCommands.Paste(), "Cmd+V", EntityClipboardService.Instance.HasContent));
            m.Items.Add(new Separator());
            if (ent.GetComponent<Camera>() != null)
            {
                m.Items.Add(Item("Look Through Camera", () => EditorCommands.LookThroughCamera(ent)));
                m.Items.Add(Item(CameraPreviewService.Instance.CurrentPreviewCamera == ent ? "Hide Camera Preview" : "Show Camera Preview", () => CameraPreviewService.Instance.TogglePreview(ent)));
                m.Items.Add(new Separator());
            }
            m.Items.Add(Item("Create Child", () => EditorCommands.CreateChild(ent)));
            m.Items.Add(Sub("Create", CreateItems(ent.Scene).ToArray()));
            m.Items.Add(new Separator());
            m.Items.Add(Item(n > 1 ? "Create Prefabs from Selection (" + n + ")…" : "Save as Prefab…", () => _ = EditorCommands.CreatePrefabFromSelection(sel)));
            var root = VortexEditor.Shell.Prefab.PrefabWorkflow.FindInstanceRoot(ent);
            if (root != null)
            {
                // prefab instance (or a part of one): the Windows editor's prefab actions, through the shared workflow
                m.Items.Add(Item("Open Prefab", () => VortexEditor.Shell.Prefab.PrefabWorkflow.OpenInEditor(root.PrefabPath)));
                m.Items.Add(Item("Select Prefab Asset", () => VortexEditor.Shell.Prefab.PrefabWorkflow.SelectAsset(root.PrefabPath)));
                m.Items.Add(Item("Apply Overrides…", async () => { await VortexEditor.Shell.Prefab.PrefabWorkflow.ApplyInteractive(ent); SceneRenderService.RuntimeDirty = true; }));
                m.Items.Add(Item("Revert Overrides…", async () => { var r = await VortexEditor.Shell.Prefab.PrefabWorkflow.RevertInteractive(ent); if (r != null) SelectEntities(new[] { r }); }));
                m.Items.Add(Item("Unpack Prefab", () => VortexEditor.Shell.Prefab.PrefabWorkflow.Unpack(ent)));
                m.Items.Add(Item("Unpack Completely", () => VortexEditor.Shell.Prefab.PrefabWorkflow.Unpack(ent, completely: true)));
            }
            m.Items.Add(new Separator());
            m.Items.Add(Item("Socket Editor…", () => EditorWindows.SocketEditor(ent)));
            m.Items.Add(Item("Collision Editor…", () => EditorWindows.CollisionEditor(ent)));
            m.Items.Add(new Separator());
            m.Items.Add(Item(ent.IsActive ? "Deactivate" : "Activate", () => EditorCommands.ToggleActive(ent)));
            m.Items.Add(Item(ent.IsHiddenInEditor ? "Show in Scene View" : "Hide in Scene View", () => EditorCommands.ToggleHiddenInEditor(ent)));
            m.Items.Add(Item("Frame in Scene View", () => { SelectionService.Instance.Select(ent); EditorCommands.FocusSelected(); }, "F"));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Rename", () => BeginRename(ent), "F2", n <= 1));
            m.Items.Add(Item(n > 1 ? "Duplicate " + n + " Entities" : "Duplicate", () => EditorCommands.DuplicateEntities(sel), "Cmd+D"));
            m.Items.Add(Item(n > 1 ? "Delete " + n + " Entities" : "Delete", () => EditorCommands.DeleteEntities(sel), "Cmd+Back"));
            return m;
        }

        private MenuFlyout BuildSceneMenu(Scene sc)
        {
            var m = new MenuFlyout();
            if (!sc.IsActive) m.Items.Add(Item("Activate Scene", () => EditorSession.Instance.ActivateScene(sc)));
            else m.Items.Add(Item("Deactivate Scene", () => { Vm.DeactivateScene(sc); SceneRenderService.RuntimeDirty = true; Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); }));
            m.Items.Add(new Separator());
            foreach (var i in CreateItems(sc)) m.Items.Add(i);
            m.Items.Add(new Separator());
            m.Items.Add(Item("Paste", () => { if (!sc.IsActive) EditorSession.Instance.ActivateScene(sc); EditorCommands.Paste(); }, "Cmd+V", EntityClipboardService.Instance.HasContent));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Save Scene", () => EditorCommands.SaveScene(sc), "Cmd+S"));
            m.Items.Add(Item("Rename Scene…", async () => { var n = await Dialogs.Prompt("Rename Scene", "New name for the scene", sc.Name, "Rename"); if (!string.IsNullOrWhiteSpace(n)) EditorCommands.RenameScene(sc, n); }));
            m.Items.Add(Item("Unload Scene", async () =>
            {
                var project = ProjectData.Current;
                if (project?.Scenes == null || project.Scenes.Count <= 1) { await Dialogs.Alert("Unload Scene", "Cannot unload the only scene."); return; }
                if (sc.IsActive) { var other = project.Scenes.FirstOrDefault(x => !ReferenceEquals(x, sc)); if (other != null) EditorSession.Instance.ActivateScene(other); }
                sc.DeactivateEntities();
                project.Scenes.Remove(sc);
                EditorCommands.Toast("Scene unloaded (the file stays on disk — File ▸ Open Scene… loads it again)");
            }));
            m.Items.Add(Item("Delete Scene…", async () =>
            {
                if (ProjectData.Current?.Scenes?.Count <= 1) { await Dialogs.Alert("Delete Scene", "Cannot delete the only scene in the project."); return; }
                if (await Dialogs.Confirm("Delete scene \"" + sc.Name + "\"?", "The scene is removed from the project (undo with ⌘Z).", "Delete", "Cancel", destructive: true))
                { Vm.SelectedScene = sc; Vm.DeleteSceneCommand.Execute(null); }
            }));
            m.Items.Add(new Separator());
            m.Items.Add(Item("New Scene", () => EditorCommands.NewScene(), "Cmd+N"));
            m.Items.Add(Item("Load Scene…", () => _ = EditorCommands.OpenScene(), "Cmd+Shift+O"));
            return m;
        }

        private MenuFlyout BuildEmptyAreaMenu()
        {
            var m = new MenuFlyout();
            m.Items.Add(Item("New Scene", () => EditorCommands.NewScene(), "Cmd+N"));
            m.Items.Add(Item("Load Scene…", () => _ = EditorCommands.OpenScene(), "Cmd+Shift+O"));
            if (ProjectData.Current?.ActiveScene != null)
            {
                m.Items.Add(new Separator());
                m.Items.Add(Item("Paste", () => EditorCommands.Paste(), "Cmd+V", EntityClipboardService.Instance.HasContent));
                m.Items.Add(Sub("Create", CreateItems(null).ToArray()));
            }
            return m;
        }

        // ================================================================ pointer: selection, drag start, context menus

        private static object RowItem(object source, out Control row)
        {
            row = null;
            var v = source as Visual;
            var tvi = v as TreeViewItem ?? v?.FindAncestorOfType<TreeViewItem>();
            if (tvi == null) return null;
            var item = tvi.DataContext;
            row = tvi.GetVisualDescendants().OfType<DockPanel>().FirstOrDefault(d => ReferenceEquals(d.DataContext, item) && (d.Classes.Contains("entityrow") || d.Classes.Contains("scenerow")));
            return item;
        }

        private static bool FromEyeToggle(object source)
        {
            for (var v = source as Visual; v != null && !(v is TreeViewItem); v = v.GetVisualParent())
                if (v is Button b && b.Classes.Contains("eyetoggle")) return true;
            return false;
        }

        private void OnTreePointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (FromEyeToggle(e.Source) || _renaming != null && (e.Source as Visual)?.FindAncestorOfType<TextBox>() != null) { _pressEntity = null; return; }
            var pt = e.GetCurrentPoint(Tree);
            var item = RowItem(e.Source, out var row);
            _deferredSelect = null;
            if (pt.Properties.IsRightButtonPressed)
            {
                e.Handled = true;   // the context menu owns this click (keeps a multi-selection that contains the row)
                if (item is GameEntity ent)
                {
                    if (!Tree.SelectedItems.Contains(ent)) SetTreeSelection(new object[] { ent });
                    SelectionService.Instance.Select(ent);
                    BuildEntityMenu(ent).ShowAt(row ?? (Control)Tree, true);
                }
                else if (item is Scene sc) BuildSceneMenu(sc).ShowAt(row ?? (Control)Tree, true);
                else BuildEmptyAreaMenu().ShowAt(Tree, true);
                return;
            }
            if (!pt.Properties.IsLeftButtonPressed) return;
            if (item is GameEntity ge)
            {
                _pressPos = e.GetPosition(Tree); _pressEntity = ge; _dragging = false;
                // pressing an entity that is part of a multi-selection keeps the selection (so it can be dragged as a
                // group); a click without dragging selects just that entity on release (Finder behaviour)
                bool mod = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
                if (!mod && Tree.SelectedItems.Count > 1 && Tree.SelectedItems.Contains(ge) && e.ClickCount == 1)
                {
                    _deferredSelect = ge;
                    e.Handled = true;
                }
            }
            else _pressEntity = null;
            if (item == null && !e.KeyModifiers.HasFlag(KeyModifiers.Meta) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                // click on empty space clears the selection
                SetTreeSelection(Array.Empty<object>());
                SelectionService.Instance.ClearSelection();
            }
        }

        private async void OnTreePointerMoved(object sender, PointerEventArgs e)
        {
            if (_pressEntity == null || _dragging) return;
            if (!e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed) { _pressEntity = null; return; }
            var p = e.GetPosition(Tree);
            if (Math.Abs(p.X - _pressPos.X) < 5 && Math.Abs(p.Y - _pressPos.Y) < 5) return;
            _dragging = true;
            _deferredSelect = null;
            var sel = SelectedTreeEntities();
            var dragged = sel.Contains(_pressEntity) ? EditorCommands.TopLevelOnly(sel) : new List<GameEntity> { _pressEntity };
            _dragEntities = dragged;
            var data = new DataObject();
            data.Set(EntityFormat, string.Join(";", dragged.Select(d => d.Id.ToString())));
            try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Move); } catch { }
            _dragEntities = null;
            _dragging = false; _pressEntity = null;
            HideDropIndicator();
        }

        private void OnTreePointerReleased(object sender, PointerReleasedEventArgs e)
        {
            if (_deferredSelect != null && !_dragging)
            {
                var d = _deferredSelect;
                _deferredSelect = null;
                SetTreeSelection(new object[] { d });
                SelectionService.Instance.Select(d);
            }
            _pressEntity = null;
        }

        private void OnTreeDoubleTapped(object sender, TappedEventArgs e)
        {
            if (FromEyeToggle(e.Source)) { e.Handled = true; return; }
            var item = RowItem(e.Source, out _);
            if (item is GameEntity ge)
            {
                if (ge.GetComponent<Camera>() != null) CameraPreviewService.Instance.ShowPreview(ge);   // WPF: double-click a camera = preview
                else { SelectionService.Instance.Select(ge); EditorCommands.FocusSelected(); }
                e.Handled = true;
            }
            else if (item is Scene sc && !sc.IsActive) { EditorSession.Instance.ActivateScene(sc); e.Handled = true; }
        }

        private void OnTreeKeyDown(object sender, KeyEventArgs e)
        {
            if (_renaming != null) return;
            if (e.Key == Key.Return && e.KeyModifiers == KeyModifiers.None && Tree.SelectedItems.Count == 1 && Tree.SelectedItems[0] is GameEntity ge)
            { BeginRename(ge); e.Handled = true; }
        }

        private void OnEyeClick(object sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is GameEntity ent) EditorCommands.ToggleHiddenInEditor(ent);
            e.Handled = true;
        }

        // ================================================================ inline rename

        /// <summary>Rename in place (↩ / F2 / context menu): Enter commits (one undo step), Esc cancels.</summary>
        public async void BeginRename(GameEntity ent)
        {
            if (ent == null) return;
            CancelRename();
            if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";
            var tvi = await RealizeContainer(ent) ?? Tree.TreeContainerFromItem(ent) as TreeViewItem;
            TextBox box = null; TextBlock label = null;
            for (int attempt = 0; attempt < 10 && tvi != null && (box == null || label == null); attempt++)
            {
                box = tvi.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("renamebox") && ReferenceEquals(t.DataContext, ent));
                label = tvi.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("entityname") && ReferenceEquals(t.DataContext, ent));
                if (box == null || label == null) await Task.Delay(30);   // the row's template is applied on the next layout pass
            }
            if (box == null || label == null)
            {
                var n = await Dialogs.Prompt("Rename", "New name for \"" + ent.Name + "\"", ent.Name, "Rename");
                if (!string.IsNullOrWhiteSpace(n)) EditorCommands.RenameEntity(ent, n);
                return;
            }
            tvi.BringIntoView();
            _renaming = ent; _renameBox = box; _renameLabel = label;
            box.Text = ent.Name;
            label.IsVisible = false;
            box.IsVisible = true;
            box.Focus();
            box.SelectAll();
        }

        /// <summary>True while an inline rename is open (smoke checks).</summary>
        public bool IsRenaming => _renaming != null;

        public void CommitRename()
        {
            var ent = _renaming; var box = _renameBox;
            if (ent == null) return;
            string text = box?.Text;
            CloseRename();
            if (!string.IsNullOrWhiteSpace(text)) EditorCommands.RenameEntity(ent, text);
            FocusTree();
        }

        public void CancelRename() { if (_renaming != null) { CloseRename(); } }

        private void CloseRename()
        {
            if (_renameBox != null) _renameBox.IsVisible = false;
            if (_renameLabel != null) _renameLabel.IsVisible = true;
            _renaming = null; _renameBox = null; _renameLabel = null;
        }

        private void OnRenameBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (_renaming == null || !ReferenceEquals(e.Source, _renameBox)) return;
            if (e.Key == Key.Return) { CommitRename(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseRename(); FocusTree(); e.Handled = true; }
        }

        private void OnRenameBoxLostFocus(object sender, RoutedEventArgs e)
        {
            if (_renaming != null && ReferenceEquals(e.Source, _renameBox)) CommitRename();
        }

        // ================================================================ drag & drop (drop side)

        private (object target, DropPosition pos, Control row) DropTarget(DragEventArgs e)
        {
            var item = RowItem(e.Source, out var row);
            if (item == null || row == null) return (null, DropPosition.Inside, null);
            if (item is Scene) return (item, DropPosition.Inside, row);
            var p = e.GetPosition(row);
            double h = Math.Max(1, row.Bounds.Height);
            var pos = p.Y < h * 0.28 ? DropPosition.Before : p.Y > h * 0.72 ? DropPosition.After : DropPosition.Inside;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) pos = DropPosition.After;       // WPF: Shift = insert after
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) pos = DropPosition.Before;   // WPF: Alt = insert before
            return (item, pos, row);
        }

        private List<GameEntity> DraggedEntities(DragEventArgs e)
        {
            if (_dragEntities != null && _dragEntities.Count > 0) return _dragEntities;
            if (e.Data.Get(EntityFormat) is string ids)
            {
                var set = new HashSet<string>(ids.Split(';', StringSplitOptions.RemoveEmptyEntries));
                var found = new List<GameEntity>();
                var project = ProjectData.Current;
                if (project?.Scenes != null) foreach (var s in project.Scenes) if (s?.Entities != null) foreach (var en in s.Entities) FindIds(en, set, found);
                return found;
            }
            return null;
        }

        private static void FindIds(GameEntity e, HashSet<string> ids, List<GameEntity> found)
        {
            if (e == null) return;
            if (ids.Contains(e.Id.ToString())) found.Add(e);
            if (e.Children != null) foreach (var c in e.Children) FindIds(c, ids, found);
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            var (target, pos, row) = DropTarget(e);
            var dragged = DraggedEntities(e);
            if (dragged != null)
            {
                bool ok = CanMove(dragged, target, pos);
                e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
                if (ok) ShowDropIndicator(row, target == null ? DropPosition.Inside : pos); else HideDropIndicator();
                return;
            }
            string path = PropertyRowsPath(e);
            if (path != null && ProjectData.Current != null)
            {
                e.DragEffects = DragDropEffects.Copy;
                ShowDropIndicator(row, pos);
                return;
            }
            e.DragEffects = DragDropEffects.None;
            HideDropIndicator();
        }

        private static string PropertyRowsPath(DragEventArgs e) => Inspector.PropertyRows.DroppedPath(e, "vortex/asset");

        private async void OnDrop(object sender, DragEventArgs e)
        {
            HideDropIndicator();
            var (target, pos, _) = DropTarget(e);
            var dragged = DraggedEntities(e);
            if (dragged != null) { MoveEntities(dragged, target, pos); e.Handled = true; return; }
            string path = PropertyRowsPath(e);
            if (path != null) { e.Handled = true; await DropAsset(path, target, pos); }
        }

        private void ShowDropIndicator(Control row, DropPosition pos)
        {
            if (row == null) { HideDropIndicator(); return; }
            var tl = row.TranslatePoint(new Point(0, 0), DropLayer);
            if (tl == null) { HideDropIndicator(); return; }
            double x = Math.Max(0, tl.Value.X - 4), w = Math.Max(20, DropLayer.Bounds.Width - x - 6);
            if (pos == DropPosition.Inside)
            {
                DropLine.IsVisible = false;
                DropFrame.Width = w; DropFrame.Height = row.Bounds.Height + 2;
                Canvas.SetLeft(DropFrame, x); Canvas.SetTop(DropFrame, tl.Value.Y - 1);
                DropFrame.IsVisible = true;
            }
            else
            {
                DropFrame.IsVisible = false;
                DropLine.Width = w;
                Canvas.SetLeft(DropLine, x); Canvas.SetTop(DropLine, tl.Value.Y + (pos == DropPosition.After ? row.Bounds.Height : 0) - 1);
                DropLine.IsVisible = true;
            }
        }

        private void HideDropIndicator() { DropLine.IsVisible = false; DropFrame.IsVisible = false; }

        private static bool IsDescendant(GameEntity candidate, GameEntity ancestor)
        {
            for (var p = candidate?.Parent; p != null; p = p.Parent) if (ReferenceEquals(p, ancestor)) return true;
            return false;
        }

        private static bool CanMove(List<GameEntity> dragged, object target, DropPosition pos)
        {
            if (dragged == null || dragged.Count == 0) return false;
            if (target is GameEntity te)
                return dragged.All(d => !ReferenceEquals(d, te) && !IsDescendant(te, d));
            return true;
        }

        /// <summary>Reparent / reorder / move-to-scene for a drop (also the smoke / API entry point). One undo step.
        /// <paramref name="target"/>: an entity (Inside = make child, Before/After = reorder next to it), a scene
        /// (move to its top level) or null (top level of the entity's own scene).</summary>
        public bool MoveEntities(IList<GameEntity> entities, object target, DropPosition pos)
        {
            var list = EditorCommands.TopLevelOnly(entities ?? Array.Empty<GameEntity>());
            if (!CanMove(list, target, pos)) return false;
            var steps = new List<Func<IUndoableCommand>>();
            if (target is GameEntity te)
            {
                if (pos == DropPosition.Inside)
                {
                    foreach (var d in list)
                    {
                        if (ReferenceEquals(d.Parent, te)) continue;
                        var dd = d;
                        if (!ReferenceEquals(d.Scene, te.Scene)) steps.Add(() => new MoveEntityToSceneCommand(dd, te.Scene));
                        steps.Add(() => new MoveEntityCommand(dd, te));
                    }
                }
                else
                {
                    var ordered = pos == DropPosition.After ? Enumerable.Reverse(list).ToList() : list;
                    foreach (var d in ordered)
                    {
                        var dd = d;
                        if (!ReferenceEquals(d.Scene, te.Scene)) { steps.Add(() => new MoveEntityToSceneCommand(dd, te.Scene)); continue; }
                        steps.Add(() => new ReorderEntityCommand(dd, te, pos == DropPosition.After));
                    }
                }
            }
            else
            {
                var sc = target as Scene;
                foreach (var d in list)
                {
                    var dd = d;
                    if (sc != null && !ReferenceEquals(d.Scene, sc)) steps.Add(() => new MoveEntityToSceneCommand(dd, sc));
                    else if (d.Parent != null) steps.Add(() => new MoveEntityCommand(dd, null));
                }
            }
            if (steps.Count == 0) return false;
            string name = list.Count == 1 ? (pos != DropPosition.Inside && target is GameEntity ? "Reorder " : "Move ") + list[0].Name : "Move " + list.Count + " Entities";
            UndoRedoManager.Instance.Execute(new SequenceCommand(name, steps));
            foreach (var d in list) { if (d.Scene != null) d.Scene.IsDirty = true; }
            if (target is GameEntity parent && pos == DropPosition.Inside) parent.IsExpanded = true;
            SceneRenderService.RuntimeDirty = true;
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            SelectEntities(list);
            return true;
        }

        /// <summary>Drop an asset (Asset Browser "vortex/asset" or a Finder file) on the hierarchy: models, prefabs and
        /// primitives are added to the scene under the row (AssetActions.AddToScene), a script is attached to the
        /// entity, a material is assigned to it, a scene file is loaded. Files from outside the project are imported first.</summary>
        public async Task<GameEntity> DropAsset(string path, object target, DropPosition pos)
        {
            var project = ProjectData.Current; if (project == null || string.IsNullOrEmpty(path)) return null;
            string full = ViewportPanel.ResolveAssetPath(path);
            string ext = full.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase) ? "" : Path.GetExtension(full).ToLowerInvariant();
            var te = target as GameEntity;
            if (ext == ".cs")
            {
                if (te == null) { EditorCommands.Toast("Drop a script onto an entity"); return null; }
                if (full.EndsWith("VortexScripting.cs", StringComparison.OrdinalIgnoreCase)) return null;
                string rel = ScriptingService.MakeRelative(project.Path, full);
                var existing = te.GetComponent<Editor.ECS.Components.Scripting.Script>();
                if (existing == null || !string.Equals(existing.ScriptPath, rel, StringComparison.OrdinalIgnoreCase))
                    te.AddComponent(new Editor.ECS.Components.Scripting.Script(te, rel));
                SelectionService.Instance.Select(te);
                EditorCommands.Window?.Inspector?.Refresh();
                EditorCommands.Toast("Script " + Path.GetFileNameWithoutExtension(full) + " → " + te.Name);
                return te;
            }
            if (ext == ".vmat")
            {
                if (te == null) { EditorCommands.Toast("Drop a material onto an entity"); return null; }
                if (ViewportPanel.ApplyMaterial(te, full)) EditorCommands.Toast("Material '" + Path.GetFileNameWithoutExtension(full) + "' → " + te.Name);
                return te;
            }
            if (ext == ".vscene") { EditorCommands.LoadSceneFile(full); return null; }
            if (!full.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase) && !ViewportPanel.IsInsideProject(full))
            {
                var imported = await ViewportPanel.ImportExternal(full);
                if (string.IsNullOrEmpty(imported)) return null;
                full = imported;
            }
            GameEntity parent = te == null ? null : pos == DropPosition.Inside ? te : te.Parent;
            if (target is Scene sc && !sc.IsActive) EditorSession.Instance.ActivateScene(sc);
            var created = VortexEditor.Services.AssetActions.AddToScene(full, null, parent);
            if (created == null) { EditorCommands.Toast("Can't add " + Path.GetFileName(full) + " to the scene"); return null; }
            if (parent != null) parent.IsExpanded = true;
            SelectEntities(new[] { created });
            return created;
        }
    }

    /// <summary>Converters for the hierarchy rows.</summary>
    public static class HierarchyConverters
    {
        private static IBrush Res(string key) => Application.Current != null && Application.Current.TryFindResource(key, Application.Current.ActualThemeVariant, out var b) && b is IBrush br ? br : Brushes.Gray;

        public static readonly IValueConverter SceneDot = new FuncValueConverter<bool, IBrush>(a => a ? Res("VxGreenBrush") : Res("VxTextTertiaryBrush"));
        public static readonly IValueConverter SceneIconBrush = new FuncValueConverter<bool, IBrush>(a => a ? Res("VxPurpleBrush") : Res("VxTextTertiaryBrush"));
        public static readonly IValueConverter SceneOpacity = new FuncValueConverter<bool, double>(a => a ? 1.0 : 0.6);
        public static readonly IValueConverter NameBrush = new FuncValueConverter<bool, IBrush>(prefab => prefab ? new SolidColorBrush(Color.Parse("#6FB0FF")) : Res("VxTextBrush"));
        public static readonly IMultiValueConverter RowOpacity = new FuncMultiValueConverter<object, double>(v =>
        {
            var l = v.ToList();
            bool active = l.Count > 0 && l[0] is bool a && a, hidden = l.Count > 1 && l[1] is bool h && h;
            return !active || hidden ? 0.45 : 1.0;
        });
        public static readonly IValueConverter Icon = new FuncValueConverter<GameEntity, string>(e => e != null && e.IsFolder ? "FolderFill" : EntityIconConverter.IconFor(e));
        public static readonly IValueConverter IconBrush = new FuncValueConverter<GameEntity, object>(e => e != null && e.IsFolder ? Res("VxYellowBrush") : EntityIconBrushConverter.Instance.Convert(e, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture));
        public static readonly IValueConverter ParentPath = new FuncValueConverter<GameEntity, string>(e =>
        {
            if (e?.Parent == null) return "";
            var names = new List<string>();
            for (var p = e.Parent; p != null; p = p.Parent) names.Insert(0, p.Name);
            return "in " + string.Join(" / ", names);
        });
    }
}
