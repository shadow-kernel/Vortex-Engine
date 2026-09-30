using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;
using Editor.Editors.WorldEditor.Components.SceneHierarchy;
using VortexEditor.Shell;

namespace VortexEditor.Panels
{
    public partial class HierarchyPanel : UserControl
    {
        private SceneHierarchyViewModel Vm => EditorSession.Instance.Hierarchy;
        private bool _syncing;
        private Point _pressPos;
        private GameEntity _pressEntity;
        private bool _dragging;

        public HierarchyPanel()
        {
            InitializeComponent();
            DragDrop.SetAllowDrop(Tree, true);
            Tree.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            Tree.AddHandler(DragDrop.DropEvent, OnDrop);
            Tree.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Tunnel);
            SelectionService.Instance.SelectionChanged += OnSelectionServiceChanged;
            EditorSession.Instance.ProjectOpened += _ => Dispatcher.UIThread.Post(Reload);
            EditorSession.Instance.ProjectClosed += () => Dispatcher.UIThread.Post(Reload);
            Reload();
        }

        public void Reload()
        {
            var project = ProjectData.Current;
            Tree.ItemsSource = project?.Scenes;
            EmptyHint.IsVisible = project == null;
            if (project?.ActiveScene != null) Dispatcher.UIThread.Post(() => ExpandScene(project.ActiveScene), DispatcherPriority.Background);
        }

        private void ExpandScene(Scene scene)
        {
            try { if (Tree.TreeContainerFromItem(scene) is TreeViewItem c) c.IsExpanded = true; } catch { }
        }

        public void FocusSearch() => SearchBox.Focus();

        // ------------------------------------------------------------ selection sync
        private void OnSelectionServiceChanged(object sender, SelectionEventArgs e)
        {
            if (_syncing) return;
            Dispatcher.UIThread.Post(() =>
            {
                _syncing = true;
                try
                {
                    var ent = e.SelectedEntity;
                    if (ent == null) { Tree.SelectedItems?.Clear(); Tree.SelectedItem = null; }
                    else if (!ReferenceEquals(Tree.SelectedItem, ent))
                    {
                        ExpandTo(ent);
                        Tree.SelectedItem = ent;
                        try { var c = Tree.TreeContainerFromItem(ent); c?.BringIntoView(); } catch { }
                    }
                }
                finally { _syncing = false; }
            });
        }

        private void ExpandTo(GameEntity ent)
        {
            var chain = new List<object>();
            var p = ent.Parent;
            while (p != null) { chain.Add(p); p = p.Parent; }
            if (ent.Scene != null) chain.Add(ent.Scene);
            chain.Reverse();
            foreach (var o in chain) { try { if (Tree.TreeContainerFromItem(o) is TreeViewItem c) c.IsExpanded = true; } catch { } }
        }

        private void OnTreeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                var items = Tree.SelectedItems?.Cast<object>().ToList() ?? new List<object>();
                var entities = items.OfType<GameEntity>().ToList();
                Vm.SelectedEntities = new ObservableCollection<GameEntity>(entities);
                var last = Tree.SelectedItem;
                if (last is GameEntity ge) { Vm.SelectedEntity = ge; }
                else if (last is Scene sc) { Vm.SelectedScene = sc; SelectionService.Instance.Select(sc); }
                else if (entities.Count == 0) SelectionService.Instance.ClearSelection();
            }
            finally { _syncing = false; }
        }

        private void OnTreeDoubleTapped(object sender, TappedEventArgs e)
        {
            if (Tree.SelectedItem is GameEntity ge) { SelectionService.Instance.RequestFocus(ge); }
            else if (Tree.SelectedItem is Scene sc && !sc.IsActive) EditorSession.Instance.ActivateScene(sc);
        }

        private void OnSearchResultSelected(object sender, SelectionChangedEventArgs e)
        {
            if (SearchResults.SelectedItem is GameEntity ge) SelectionService.Instance.Select(ge);
        }

        // ------------------------------------------------------------ search
        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            string q = SearchBox.Text?.Trim();
            bool searching = !string.IsNullOrEmpty(q);
            SearchResults.IsVisible = searching;
            Tree.IsVisible = !searching;
            if (!searching) return;
            var hits = new List<GameEntity>();
            var project = ProjectData.Current;
            if (project?.Scenes != null)
                foreach (var s in project.Scenes) if (s.Entities != null) foreach (var en in s.Entities) Collect(en, q, hits);
            SearchResults.ItemsSource = hits;
        }

        private static void Collect(GameEntity e, string q, List<GameEntity> hits)
        {
            if (e == null) return;
            if (e.Name != null && e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(e);
            if (e.Children != null) foreach (var c in e.Children) Collect(c, q, hits);
        }

        // ------------------------------------------------------------ toolbar
        private void OnLocateClick(object sender, RoutedEventArgs e)
        {
            var ent = SelectionService.Instance.SelectedEntity;
            if (ent != null) { ExpandTo(ent); Tree.SelectedItem = ent; try { Tree.TreeContainerFromItem(ent)?.BringIntoView(); } catch { } }
        }

        private void OnNewSceneClick(object sender, RoutedEventArgs e) => Vm.CreateSceneCommand.Execute(null);

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            var m = new MenuFlyout();
            m.Items.Add(Item("Create Empty", () => EditorCommands.CreateEmpty()));
            m.Items.Add(Item("Player", () => EditorCommands.CreatePlayer()));
            m.Items.Add(Item("Folder", () => EditorCommands.CreateFolder()));
            m.Items.Add(new Separator());
            var obj = new MenuItem { Header = "3D Object" };
            foreach (var t in new[] { PrimitiveType.Cube, PrimitiveType.Sphere, PrimitiveType.Capsule, PrimitiveType.Cylinder, PrimitiveType.Plane, PrimitiveType.Quad })
            { var tt = t; obj.Items.Add(Item(t.ToString(), () => EditorCommands.CreatePrimitive(tt))); }
            m.Items.Add(obj);
            var light = new MenuItem { Header = "Light" };
            light.Items.Add(Item("Directional Light", () => EditorCommands.CreateLight(LightType.Directional)));
            light.Items.Add(Item("Point Light", () => EditorCommands.CreateLight(LightType.Point)));
            light.Items.Add(Item("Spot Light", () => EditorCommands.CreateLight(LightType.Spot)));
            light.Items.Add(Item("Skybox", () => EditorCommands.CreateSkybox()));
            m.Items.Add(light);
            m.Items.Add(Item("Camera", () => EditorCommands.CreateCamera()));
            var audio = new MenuItem { Header = "Audio" };
            audio.Items.Add(Item("Audio Source", () => EditorCommands.CreateAudioSource()));
            audio.Items.Add(Item("Reverb Zone", () => EditorCommands.CreateReverbZone()));
            m.Items.Add(audio);
            var ui = new MenuItem { Header = "UI" };
            foreach (var k in new[] { "Canvas", "Text", "Image", "Button" }) { var kk = k; ui.Items.Add(Item(k, () => EditorCommands.CreateUI(kk))); }
            m.Items.Add(ui);
            m.Items.Add(new Separator());
            m.Items.Add(Item("Instantiate Prefab…", async () => await InstantiatePrefab()));
            m.ShowAt(AddButton);
        }

        private static MenuItem Item(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (s, e) => a(); return mi; }

        private async System.Threading.Tasks.Task InstantiatePrefab()
        {
            var scene = ProjectData.Current?.ActiveScene; if (scene == null) return;
            var top = TopLevel.GetTopLevel(this); if (top == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Instantiate Prefab",
                FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Vortex Prefab") { Patterns = new[] { "*.ventity" } } }
            });
            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(path)) return;
            var ent = PrefabService.Instance.InstantiatePrefab(path, scene, Tree.SelectedItem as GameEntity);
            if (ent != null) { SelectionService.Instance.Select(ent); SelectionService.Instance.RequestFocus(ent); SceneRenderService.RuntimeDirty = true; }
        }

        // ------------------------------------------------------------ context menus
        private void OnItemPointerPressed(object sender, PointerPressedEventArgs e)
        {
            var ctx = (sender as Control)?.DataContext;
            var pt = e.GetCurrentPoint(this);
            if (pt.Properties.IsLeftButtonPressed && ctx is GameEntity ge) { _pressPos = e.GetPosition(this); _pressEntity = ge; _dragging = false; }
            if (pt.Properties.IsRightButtonPressed)
            {
                if (ctx is GameEntity ent)
                {
                    if (!ReferenceEquals(Tree.SelectedItem, ent)) { Tree.SelectedItem = ent; }
                    BuildEntityMenu(ent).ShowAt(sender as Control, true);
                }
                else if (ctx is Scene sc) BuildSceneMenu(sc).ShowAt(sender as Control, true);
                e.Handled = true;
            }
        }

        private MenuFlyout BuildEntityMenu(GameEntity ent)
        {
            var m = new MenuFlyout();
            m.Items.Add(Item("Cut", () => EditorCommands.Cut()));
            m.Items.Add(Item("Copy", () => EditorCommands.Copy()));
            m.Items.Add(Item("Paste", () => EditorCommands.Paste()));
            m.Items.Add(new Separator());
            if (ent.GetComponent<Camera>() != null)
            {
                m.Items.Add(Item("Look Through Camera", () => Editor.Core.Viewport.EditorViewportSession.Main?.ViewThroughCamera(ent)));
                m.Items.Add(Item("Show Camera Preview", () => CameraPreviewService.Instance.TogglePreview(ent)));
                m.Items.Add(new Separator());
            }
            m.Items.Add(Item("Create Child", () => { Vm.SelectedEntity = ent; Vm.CreateChildEntityCommand.Execute(null); SceneRenderService.RuntimeDirty = true; }));
            m.Items.Add(Item("Save as Prefab…", () => { try { var p = PrefabService.Instance.SaveAsPrefab(ent); EditorCommands.Toast("Prefab saved: " + System.IO.Path.GetFileName(p)); EditorCommands.Window?.AssetBrowser?.Refresh(); } catch (Exception ex) { EditorCommands.Fail("Save prefab", ex); } }));
            m.Items.Add(Item("Apply Instance → Prefab", () => { try { PrefabService.Instance.ApplyToPrefab(ent); EditorCommands.Toast("Prefab updated"); } catch (Exception ex) { EditorCommands.Fail("Apply to prefab", ex); } }));
            m.Items.Add(Item("Revert Instance ← Prefab", () => { try { var r = PrefabService.Instance.RevertInstance(ent); if (r != null) SelectionService.Instance.Select(r); SceneRenderService.RuntimeDirty = true; } catch (Exception ex) { EditorCommands.Fail("Revert instance", ex); } }));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Rename…", async () => await RenameEntity(ent)));
            m.Items.Add(Item("Duplicate", () => EditorCommands.Duplicate()));
            m.Items.Add(Item("Delete", () => EditorCommands.Delete()));
            return m;
        }

        private MenuFlyout BuildSceneMenu(Scene sc)
        {
            var m = new MenuFlyout();
            m.Items.Add(Item(sc.IsActive ? "Active Scene" : "Activate Scene", () => { if (!sc.IsActive) EditorSession.Instance.ActivateScene(sc); }));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Create Empty", () => EditorCommands.CreateEmpty()));
            m.Items.Add(Item("Instantiate Prefab…", async () => await InstantiatePrefab()));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Save Scene", () => { try { SceneService.Instance.SaveScene(sc); EditorCommands.Toast("Scene saved"); } catch (Exception ex) { EditorCommands.Fail("Save scene", ex); } }));
            m.Items.Add(Item("Rename Scene…", async () => { var n = await Dialogs.Prompt("Rename Scene", "New name for the scene", sc.Name, "Rename"); if (!string.IsNullOrWhiteSpace(n)) { sc.Name = n.Trim(); } }));
            m.Items.Add(Item("Delete Scene", async () =>
            {
                if (ProjectData.Current?.Scenes?.Count <= 1) { await Dialogs.Alert("Cannot delete", "A project needs at least one scene."); return; }
                if (await Dialogs.Confirm("Delete scene \"" + sc.Name + "\"?", "The scene file is removed from the project.", "Delete", "Cancel", destructive: true))
                { Vm.SelectedScene = sc; Vm.DeleteSceneCommand.Execute(null); }
            }));
            return m;
        }

        public async System.Threading.Tasks.Task RenameEntity(GameEntity ent)
        {
            if (ent == null) return;
            var n = await Dialogs.Prompt("Rename", "New name for \"" + ent.Name + "\"", ent.Name, "Rename");
            if (!string.IsNullOrWhiteSpace(n)) ent.Name = n.Trim();
        }

        // ------------------------------------------------------------ keyboard
        private async void OnTreeKeyDown(object sender, KeyEventArgs e)
        {
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            switch (e.Key)
            {
                case Key.Delete: case Key.Back: EditorCommands.Delete(); e.Handled = true; break;
                case Key.F2: await RenameEntity(Tree.SelectedItem as GameEntity); e.Handled = true; break;
                case Key.Return: if (Tree.SelectedItem is GameEntity ge) { await RenameEntity(ge); e.Handled = true; } break;
                case Key.D: if (cmd) { EditorCommands.Duplicate(); e.Handled = true; } break;
                case Key.C: if (cmd) { EditorCommands.Copy(); e.Handled = true; } break;
                case Key.X: if (cmd) { EditorCommands.Cut(); e.Handled = true; } break;
                case Key.V: if (cmd) { EditorCommands.Paste(); e.Handled = true; } break;
                case Key.A: if (cmd) { EditorCommands.SelectAll(); e.Handled = true; } break;
                case Key.F: if (!cmd) { EditorCommands.FocusSelected(); e.Handled = true; } break;
            }
        }

        // ------------------------------------------------------------ drag & drop (reparent / reorder)
        private async void OnItemPointerMoved(object sender, PointerEventArgs e)
        {
            if (_pressEntity == null || _dragging) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressEntity = null; return; }
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressPos.X) < 6 && Math.Abs(p.Y - _pressPos.Y) < 6) return;
            _dragging = true;
            var data = new DataObject();
            data.Set("vortex/entity", _pressEntity);
            try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Move); } catch { }
            _dragging = false; _pressEntity = null;
        }

        private void OnTreePointerReleased(object sender, PointerReleasedEventArgs e) { _pressEntity = null; }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.DragEffects = e.Data.Contains("vortex/entity") || e.Data.Contains(DataFormats.Files) || e.Data.Contains("vortex/asset") ? DragDropEffects.Move : DragDropEffects.None;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            var target = (e.Source as Control)?.DataContext;
            if (e.Data.Get("vortex/entity") is GameEntity dragged)
            {
                if (target is GameEntity te && !ReferenceEquals(te, dragged) && !IsDescendant(te, dragged))
                {
                    bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                    if (shift || alt) Vm.MoveEntityToPosition(dragged, te, insertAfter: shift);
                    else Vm.MoveEntityToParent(dragged, te);
                }
                else if (target is Scene ts) Vm.MoveEntityToScene(dragged, ts);
                SceneRenderService.RuntimeDirty = true;
                e.Handled = true;
                return;
            }
            string path = e.Data.Get("vortex/asset") as string;
            if (path == null && e.Data.Contains(DataFormats.Files))
            {
                var f = e.Data.GetFiles()?.FirstOrDefault();
                path = f?.TryGetLocalPath();
            }
            if (!string.IsNullOrEmpty(path))
            {
                var scene = ProjectData.Current?.ActiveScene; if (scene == null) return;
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".ventity")
                {
                    var ent = PrefabService.Instance.InstantiatePrefab(path, scene, target as GameEntity);
                    if (ent != null) SelectionService.Instance.Select(ent);
                }
                else if (ext == ".cs" && target is GameEntity te2)
                {
                    string rel = ScriptingService.MakeRelative(ProjectData.Current.Path, path);
                    te2.AddComponent(new Editor.ECS.Components.Scripting.Script(te2, rel));
                    SelectionService.Instance.Select(te2);
                    EditorCommands.Window?.Inspector?.Refresh();
                }
                SceneRenderService.RuntimeDirty = true;
                e.Handled = true;
            }
        }

        private static bool IsDescendant(GameEntity candidate, GameEntity ancestor)
        {
            var p = candidate?.Parent;
            while (p != null) { if (ReferenceEquals(p, ancestor)) return true; p = p.Parent; }
            return false;
        }
    }
}
