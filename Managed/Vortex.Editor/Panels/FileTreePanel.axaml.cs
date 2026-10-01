using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Editors.WorldEditor.Components.FileExplorer.Services;
using VortexEditor.Controls;
using VortexEditor.Panels.AssetBrowser;
using VortexEditor.Shell;

namespace VortexEditor.Panels
{
    /// <summary>
    /// Project folder tree (left sidebar, "Files") — port of the Windows FileTreeView: selecting a folder shows it in
    /// the Asset Browser and browsing in the Asset Browser selects + expands it here; lazy expand/collapse that
    /// survives refreshes; context menu (new folder, new script, rename, delete, reveal, copy path, import); drag
    /// folders onto folders to move them and drop browser tiles or Finder files onto a folder to move / import;
    /// refreshes by itself when the project's files change (FileSystemWatcher).
    /// </summary>
    public partial class FileTreePanel : UserControl
    {
        private readonly ObservableCollection<FolderNode> _roots = new ObservableCollection<FolderNode>();
        private FolderNode _root;
        private bool _syncing;
        private DispatcherTimer _resyncTimer, _hoverTimer;
        private FolderNode _renaming, _dropHighlight, _hoverNode;
        private FolderNode _pressNode;
        private Point _pressPos;
        private bool _dragArmed, _dragging;

        public FileTreePanel()
        {
            InitializeComponent();
            Tree.ItemsSource = _roots;

            Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
            Tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
            Tree.AddHandler(PointerReleasedEvent, (s, e) => { _dragArmed = false; _pressNode = null; }, RoutingStrategies.Tunnel);
            Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
            // Edit-menu commands (⌘⌫ / Rename arrive through the macOS menu) act on the selected folder while the tree has focus.
            EditorCommands.RegisterEditHandler(Tree, a =>
            {
                var node = SelectedNode;
                if (node == null) return false;
                switch (a)
                {
                    case EditorCommands.EditAction.Delete: _ = DeleteFolderAsync(node); return true;
                    case EditorCommands.EditAction.Rename: BeginRename(node); return true;
                    case EditorCommands.EditAction.Duplicate:
                    case EditorCommands.EditAction.SelectAll:
                    case EditorCommands.EditAction.Cut:
                    case EditorCommands.EditAction.Paste: return true;   // no folder equivalent: never fall through to the scene
                    default: return false;
                }
            });
            DragDrop.SetAllowDrop(Tree, true);
            Tree.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            Tree.AddHandler(DragDrop.DragLeaveEvent, (s, e) => SetDropHighlight(null));
            Tree.AddHandler(DragDrop.DropEvent, OnDrop);
            Reload();
        }

        // static services outlive the panel: listen only while it is on screen, catch up when it comes back
        private void OnNavigated(string path) => SelectFolder(path);
        private void OnAssetsChanged(AssetChanges ch) { if (ch.Structural || ch.Overflow) ScheduleResync(); }
        private void OnTreeStructureChanged(object sender, EventArgs e) => Dispatcher.UIThread.Post(ScheduleResync);
        private bool _wasDetached;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            AssetNavigation.Changed -= OnNavigated; AssetNavigation.Changed += OnNavigated;
            AssetWatcher.Changed -= OnAssetsChanged; AssetWatcher.Changed += OnAssetsChanged;
            FileExplorerService.Instance.TreeStructureChanged -= OnTreeStructureChanged;
            FileExplorerService.Instance.TreeStructureChanged += OnTreeStructureChanged;
            if (_wasDetached) { _wasDetached = false; Resync(); }
            SelectFolder(AssetNavigation.CurrentFolder);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            AssetNavigation.Changed -= OnNavigated;
            AssetWatcher.Changed -= OnAssetsChanged;
            FileExplorerService.Instance.TreeStructureChanged -= OnTreeStructureChanged;
            _wasDetached = true;
            base.OnDetachedFromVisualTree(e);
        }

        public FolderNode RootNode => _root;
        public FolderNode SelectedNode => Tree.SelectedItem as FolderNode;
        private static string ProjectRoot => ProjectData.Current?.Path;

        /// <summary>Project opened / closed / switched: rebuild the tree (kept when it's the same project).</summary>
        public void Reload()
        {
            CancelRename();
            string root = ProjectRoot;
            if (root == null || !Directory.Exists(root))
            {
                _root = null; _roots.Clear();
                EmptyHint.IsVisible = true;
                return;
            }
            EmptyHint.IsVisible = false;
            AssetNavigation.EnsureExplorer();
            if (_root == null || !AssetFileOps.PathsEqual(_root.FullPath, root))
            {
                _root = new FolderNode(root, null, ProjectData.Current?.Name ?? Path.GetFileName(root.TrimEnd('/', '\\')));
                _roots.Clear();
                _roots.Add(_root);
                _root.IsExpanded = true;
                var assets = _root.Find(Path.Combine(root, "Assets"), expandAlongTheWay: false);
                if (assets != null) assets.IsExpanded = true;
            }
            else Resync();
            SelectFolder(AssetNavigation.CurrentFolder);
        }

        // ================================================================ sync with the browser
        /// <summary>Select + reveal the node of <paramref name="path"/> (expanding its ancestors).</summary>
        public void SelectFolder(string path)
        {
            if (_root == null || string.IsNullOrEmpty(path)) return;
            var node = _root.Find(path, expandAlongTheWay: true);
            if (node == null || ReferenceEquals(Tree.SelectedItem, node)) return;
            _syncing = true;
            try { Tree.SelectedItem = node; } finally { _syncing = false; }
            Dispatcher.UIThread.Post(() => (Tree.TreeContainerFromItem(node) as Control)?.BringIntoView(), DispatcherPriority.Background);
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || !(Tree.SelectedItem is FolderNode node) || node.IsPlaceholder) return;
            if (_renaming != null && !ReferenceEquals(_renaming, node)) CommitRename();
            ShowInBrowser(node.FullPath);
        }

        private static void ShowInBrowser(string folder)
        {
            // an explicit folder pick shows it in the browser's Explorer tab (the browser syncs back via AssetNavigation)
            if (AssetBrowserPanel.Current != null) AssetBrowserPanel.Current.Navigate(folder);
            else AssetNavigation.NavigateTo(folder);
        }

        private void ScheduleResync()
        {
            if (_resyncTimer == null)
            {
                _resyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _resyncTimer.Tick += (s, e) => { _resyncTimer.Stop(); Resync(); };
            }
            _resyncTimer.Stop(); _resyncTimer.Start();
        }

        /// <summary>Re-read the folders in place (keeps expansion / selection).</summary>
        public void Resync()
        {
            if (_root == null) return;
            if (!Directory.Exists(_root.FullPath)) { Reload(); return; }
            var selected = SelectedNode?.FullPath;
            _root.Sync(recursive: true);
            string want = AssetNavigation.CurrentFolder ?? selected;
            if (want != null && Tree.SelectedItem is FolderNode n && !AssetFileOps.PathsEqual(n.FullPath, want)) SelectFolder(want);
            else if (want != null && !(Tree.SelectedItem is FolderNode)) SelectFolder(want);
        }

        // ================================================================ header buttons
        private void OnNewFolder(object sender, RoutedEventArgs e) => NewFolder(SelectedNode ?? _root);
        private void OnCollapseAll(object sender, RoutedEventArgs e) { _root?.CollapseAll(); if (_root != null) _root.IsExpanded = true; }
        private void OnReveal(object sender, RoutedEventArgs e) { var p = SelectedNode?.FullPath ?? ProjectRoot; if (p != null) AssetFileOps.Reveal(p); }
        private void OnRefresh(object sender, RoutedEventArgs e) { Resync(); AssetBrowserPanel.Current?.Refresh(); }

        // ================================================================ commands
        /// <summary>New folder inside <paramref name="parent"/> (undoable), then rename it inline.</summary>
        public string NewFolder(FolderNode parent, bool rename = true)
        {
            if (parent == null || parent.IsPlaceholder) return null;
            string path = AssetFileOps.CreateFolder(parent.FullPath);
            if (path == null) return null;
            parent.IsExpanded = true;
            parent.Sync(recursive: false);
            var node = parent.Children.FirstOrDefault(c => AssetFileOps.PathsEqual(c.FullPath, path));
            if (node != null && rename) Dispatcher.UIThread.Post(() => BeginRename(node), DispatcherPriority.Background);
            AssetBrowserPanel.Current?.Refresh();
            return path;
        }

        private async Task NewScript(FolderNode node)
        {
            if (ProjectRoot == null) return;
            var name = await Dialogs.Prompt("New Script", "Name", "NewBehaviour", "Create");
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                string path = ScriptingService.CreateScript(Path.GetFileNameWithoutExtension(name.Trim()));
                string scripts = ScriptingService.ScriptsDir;
                // scripts compile from Assets/Scripts (recursive): use the clicked folder when it is inside it
                if (node != null && scripts != null && AssetFileOps.IsWithin(node.FullPath, scripts) && !AssetFileOps.PathsEqual(node.FullPath, scripts))
                {
                    string dest = Path.Combine(node.FullPath, Path.GetFileName(path));
                    if (!File.Exists(dest)) { File.Move(path, dest); path = dest; }
                }
                try { Editor.Core.Assets.AssetDatabase.Instance.Refresh(); } catch { }
                EditorCommands.Toast("Script created: " + Path.GetFileName(path));
                AssetBrowserPanel.Current?.Reveal(path);
                EditorCommands.OpenInIde(path);
            }
            catch (Exception ex) { EditorCommands.Fail("Create script", ex); }
        }

        /// <summary>Delete a folder after confirmation (moved to the Trash; undoable).</summary>
        public async Task DeleteFolderAsync(FolderNode node, bool confirm = true)
        {
            if (node == null || node.IsRoot || node.IsPlaceholder) return;
            if (AssetFileOps.PathsEqual(node.FullPath, Path.Combine(ProjectRoot ?? "", "Assets"))) { EditorCommands.Toast("The Assets folder can't be deleted"); return; }
            if (confirm && !await Dialogs.Confirm("Delete “" + node.Name + "”?", (MacTrash.IsSupported ? "The folder and everything in it is moved to the Trash." : "The folder and everything in it is deleted.") + " Undo (⌘Z) restores it.", "Delete", "Cancel", destructive: true)) return;
            string cur = AssetNavigation.CurrentFolder;
            string parent = Path.GetDirectoryName(node.FullPath);
            List<string> deleted;
            if (AssetBrowserPanel.Current != null) deleted = AssetBrowserPanel.Current.DeletePaths(new List<string> { node.FullPath });
            else
            {
                deleted = AssetFileOps.Delete(new[] { node.FullPath });
                try { PrefabService.Instance.OnPrefabDeleted(node.FullPath, true); } catch { }
            }
            if (deleted.Count > 0 && cur != null && AssetFileOps.IsWithin(cur, node.FullPath)) ShowInBrowser(parent);
            Resync();
        }

        public void BeginRename(FolderNode node)
        {
            if (node == null || node.IsRoot || node.IsPlaceholder) return;
            if (_renaming != null && !ReferenceEquals(_renaming, node)) CommitRename();
            node.EditName = node.Name;
            node.IsRenaming = true;
            _renaming = node;
            Dispatcher.UIThread.Post(() =>
            {
                var box = (Tree.TreeContainerFromItem(node) as Control)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.IsVisible);
                if (box == null) return;
                box.Focus();
                box.SelectAll();
            }, DispatcherPriority.Background);
        }

        private void OnRenameKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitRename(); e.Handled = true; Tree.Focus(); }
            else if (e.Key == Key.Escape) { CancelRename(); e.Handled = true; Tree.Focus(); }
        }

        private void OnRenameLostFocus(object sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is FolderNode n && ReferenceEquals(n, _renaming)) CommitRename();
        }

        internal void CommitRename()
        {
            var node = _renaming;
            if (node == null) return;
            _renaming = null;
            node.IsRenaming = false;
            string name = node.EditName?.Trim();
            if (string.IsNullOrEmpty(name) || name == node.Name) return;
            string old = node.FullPath;
            string cur = AssetNavigation.CurrentFolder;
            try
            {
                string np = AssetFileOps.Rename(old, name);
                try { Editor.Core.Assets.AssetDatabase.Instance.Refresh(); } catch { }
                node.Parent?.Sync(recursive: false);
                // keep showing the same folder when it (or a folder above it) was renamed
                if (cur != null && AssetFileOps.IsWithin(cur, old))
                    ShowInBrowser(Rebase(cur, old, np));
                else SelectFolder(cur);
                AssetBrowserPanel.Current?.Refresh();
            }
            catch (Exception ex) { EditorCommands.Toast(ex.Message); }
        }

        /// <summary><paramref name="path"/> (inside <paramref name="oldRoot"/>) expressed under <paramref name="newRoot"/>.</summary>
        private static string Rebase(string path, string oldRoot, string newRoot)
        {
            if (AssetFileOps.PathsEqual(path, oldRoot)) return newRoot;
            return Path.Combine(newRoot, Path.GetRelativePath(oldRoot, path));
        }

        private void CancelRename()
        {
            var n = _renaming;
            _renaming = null;
            if (n != null) n.IsRenaming = false;
        }

        // ================================================================ keyboard
        private async void OnTreeKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Source is TextBox) return;
            var node = SelectedNode;
            if (node == null) return;
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            switch (e.Key)
            {
                case Key.F2: case Key.Enter: e.Handled = true; BeginRename(node); break;
                case Key.Delete: e.Handled = true; await DeleteFolderAsync(node); break;
                case Key.Back: if (cmd) { e.Handled = true; await DeleteFolderAsync(node); } break;
            }
        }

        // ================================================================ context menu
        private FolderNode NodeFromSource(object source)
        {
            for (var v = source as Visual; v != null && !ReferenceEquals(v, Tree); v = v.GetVisualParent())
                if (v is TreeViewItem tvi) return tvi.DataContext as FolderNode;
            return null;
        }

        private void OnContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (e.Source is TextBox) return;
            var node = NodeFromSource(e.Source);
            if (node == null || node.IsPlaceholder) return;
            e.Handled = true;
            var m = BuildContextMenu(node);
            Control anchor = Tree.TreeContainerFromItem(node) as Control ?? Tree;
            m.ShowAt(anchor, showAtPointer: e.TryGetPosition(anchor, out _));
        }

        internal MenuFlyout BuildContextMenu(FolderNode node)
        {
            var cmdKey = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            bool isAssets = AssetFileOps.PathsEqual(node.FullPath, Path.Combine(ProjectRoot ?? "", "Assets"));
            var m = new MenuFlyout();
            m.Items.Add(Mi("New Folder", () => NewFolder(node), "FolderFill"));
            m.Items.Add(Mi("New Script…", () => _ = NewScript(node), "Script"));
            m.Items.Add(new Separator());
            m.Items.Add(Mi("Rename", () => BeginRename(node), "Tag", new KeyGesture(Key.F2), enabled: !node.IsRoot));
            m.Items.Add(Mi("Delete…", () => _ = DeleteFolderAsync(node), "Trash", new KeyGesture(Key.Back, cmdKey), enabled: !node.IsRoot && !isAssets));
            m.Items.Add(new Separator());
            m.Items.Add(Mi(OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Show in Explorer", () => AssetFileOps.Reveal(node.FullPath), "Folder"));
            m.Items.Add(Mi("Copy Path", () => _ = CopyText(AssetFileOps.ToRelative(node.FullPath)), "Link"));
            m.Items.Add(Mi("Import Files Here…", () => _ = ImportInto(node), "Import"));
            m.Items.Add(new Separator());
            m.Items.Add(Mi("Collapse All", () => { _root?.CollapseAll(); if (_root != null) _root.IsExpanded = true; }, "Minus"));
            return m;
        }

        private static MenuItem Mi(string header, Action action, string icon = null, KeyGesture gesture = null, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            if (icon != null) mi.Icon = new VxIcon { Icon = icon, Width = 14, Height = 14 };
            if (gesture != null) mi.InputGesture = gesture;
            mi.Click += (s, e) => { try { action(); } catch (Exception ex) { EditorCommands.Fail(header.TrimEnd('…'), ex); } };
            return mi;
        }

        private async Task CopyText(string text)
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb == null) return;
            await cb.SetTextAsync(text);
            EditorCommands.Toast("Copied " + text);
        }

        private async Task ImportInto(FolderNode node)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null || AssetBrowserPanel.Current == null) return;
            IStorageFolder start = null;
            try { start = await top.StorageProvider.TryGetFolderFromPathAsync(node.FullPath); } catch { }
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import into " + node.Name, AllowMultiple = true, SuggestedStartLocation = start });
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToArray();
            if (paths.Length > 0) await AssetBrowserPanel.Current.ImportFiles(paths, node.FullPath);
        }

        // ================================================================ drag & drop
        private void OnTreePointerPressed(object sender, PointerPressedEventArgs e)
        {
            _dragArmed = false; _pressNode = null;
            if (e.Source is TextBox) return;
            var p = e.GetCurrentPoint(Tree);
            if (!p.Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
            var node = NodeFromSource(e.Source);
            if (node == null || node.IsRoot || node.IsPlaceholder) return;
            if (_renaming != null && !ReferenceEquals(_renaming, node)) CommitRename();
            _pressNode = node; _pressPos = p.Position; _dragArmed = true;
        }

        private async void OnTreePointerMoved(object sender, PointerEventArgs e)
        {
            if (!_dragArmed || _dragging || _pressNode == null) return;
            var p = e.GetCurrentPoint(Tree);
            if (!p.Properties.IsLeftButtonPressed) { _dragArmed = false; return; }
            var d = p.Position - _pressPos;
            if (Math.Abs(d.X) < 5 && Math.Abs(d.Y) < 5) return;
            var node = _pressNode;
            _dragArmed = false; _dragging = true;
            try { await DragDrop.DoDragDrop(e, AssetDragData.ForPath(node.FullPath), DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link); }
            catch { }
            finally { _dragging = false; _pressNode = null; SetDropHighlight(null); }
        }

        private void SetDropHighlight(FolderNode node)
        {
            if (ReferenceEquals(_dropHighlight, node)) return;
            if (_dropHighlight != null) _dropHighlight.IsDropTarget = false;
            _dropHighlight = node;
            if (node != null) node.IsDropTarget = true;
            // hovering a collapsed folder during a drag opens it after a moment
            _hoverTimer?.Stop();
            _hoverNode = node;
            if (node != null && !node.IsExpanded && node.Children.Count > 0)
            {
                if (_hoverTimer == null)
                {
                    _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                    _hoverTimer.Tick += (s, e) => { _hoverTimer.Stop(); if (_hoverNode != null) _hoverNode.IsExpanded = true; };
                }
                _hoverTimer.Start();
            }
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            var node = NodeFromSource(e.Source);
            e.DragEffects = DragDropEffects.None;
            if (node != null && !node.IsPlaceholder)
            {
                var moves = AssetDragData.MovePaths(e.Data);
                if (moves != null)
                {
                    if (AssetFileOps.CanMoveInto(moves, node.FullPath)) e.DragEffects = DragDropEffects.Move;
                }
                else if (e.Data.Contains(DataFormats.Files)) e.DragEffects = DragDropEffects.Copy;
            }
            SetDropHighlight(e.DragEffects != DragDropEffects.None ? node : null);
            e.Handled = true;
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            var node = NodeFromSource(e.Source);
            SetDropHighlight(null);
            if (node == null || node.IsPlaceholder) return;
            e.Handled = true;
            var moves = AssetDragData.MovePaths(e.Data);
            if (moves != null)
            {
                string cur = AssetNavigation.CurrentFolder;
                var plan = AssetFileOps.PlanMoves(moves, node.FullPath);
                if (AssetBrowserPanel.Current != null) AssetBrowserPanel.Current.MoveInto(moves, node.FullPath);
                else AssetFileOps.Move(moves, node.FullPath);
                // the browsed folder itself was moved: follow it
                foreach (var mv in plan)
                    if (mv.IsDir && cur != null && AssetFileOps.IsWithin(cur, mv.Src) && Directory.Exists(mv.Dst))
                    { ShowInBrowser(Rebase(cur, mv.Src, mv.Dst)); break; }
                node.IsExpanded = true;
                Resync();
                return;
            }
            var files = AssetDragData.OsFiles(e.Data);
            if (files != null && AssetBrowserPanel.Current != null) await AssetBrowserPanel.Current.ImportFiles(files, node.FullPath);
        }
    }
}
