using System;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Editors.WorldEditor.Components.FileExplorer.Models;
using Editor.Editors.WorldEditor.Components.FileExplorer.Services;
using VortexEditor.Shell;

namespace VortexEditor.Panels
{
    /// <summary>Project folder tree (left sidebar); selecting a folder navigates the Project browser.</summary>
    public partial class FileTreePanel : UserControl
    {
        private bool _syncing;

        public FileTreePanel()
        {
            InitializeComponent();
            FileExplorerService.Instance.TreeStructureChanged += (s, e) => Dispatcher.UIThread.Post(Reload);
            FileExplorerService.Instance.CurrentFolderChanged += (s, f) => Dispatcher.UIThread.Post(() => SelectFolder(f));
            Reload();
        }

        public void Reload()
        {
            var root = ProjectData.Current != null ? FileExplorerService.Instance.RootItem : null;
            EmptyHint.IsVisible = root == null;
            _syncing = true;
            Tree.ItemsSource = root != null ? new ObservableCollection<FileSystemItem> { root } : null;
            _syncing = false;
            if (root != null) Dispatcher.UIThread.Post(() => { try { if (Tree.TreeContainerFromItem(root) is TreeViewItem c) c.IsExpanded = true; } catch { } }, DispatcherPriority.Background);
        }

        private void SelectFolder(FileSystemItem folder)
        {
            if (folder == null || _syncing) return;
            _syncing = true;
            try { if (!ReferenceEquals(Tree.SelectedItem, folder)) Tree.SelectedItem = folder; } finally { _syncing = false; }
        }

        private void OnSelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (_syncing || !(Tree.SelectedItem is FileSystemItem item)) return;
            if (item.IsDirectory) FileExplorerService.Instance.NavigateTo(item);
            else { FileExplorerService.Instance.NavigateToPath(Path.GetDirectoryName(item.FullPath)); EditorCommands.Window?.AssetBrowser?.SelectPath(item.FullPath); }
        }

        private void OnItemPressed(object s, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
            if (!((s as Control)?.DataContext is FileSystemItem item)) return;
            Tree.SelectedItem = item;
            var m = new MenuFlyout();
            m.Items.Add(Item("New Folder", () => Create(item)));
            m.Items.Add(Item("Reveal in Finder", () => EditorCommands.RevealInFinder(item.FullPath)));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Rename…", async () => { var n = await Dialogs.Prompt("Rename", "New name", item.Name, "Rename"); if (!string.IsNullOrWhiteSpace(n) && n != item.Name) FileExplorerService.Instance.Rename(item, n.Trim()); }));
            m.Items.Add(Item("Delete", async () => { if (await Dialogs.Confirm("Delete \"" + item.Name + "\"?", "The file is moved to the Trash where possible.", "Delete", "Cancel", destructive: true)) FileExplorerService.Instance.Delete(item); }));
            m.ShowAt(s as Control, true);
            e.Handled = true;
        }

        private static MenuItem Item(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (s, e) => a(); return mi; }
        private static MenuItem Item(string h, Func<System.Threading.Tasks.Task> a) { var mi = new MenuItem { Header = h }; mi.Click += (s, e) => _ = a(); return mi; }

        private void Create(FileSystemItem parent)
        {
            if (parent != null && parent.IsDirectory) FileExplorerService.Instance.NavigateTo(parent);
            FileExplorerService.Instance.CreateFolder();
        }

        private void OnNewFolder(object s, RoutedEventArgs e) => Create(Tree.SelectedItem as FileSystemItem);
        private void OnReveal(object s, RoutedEventArgs e) { var p = (Tree.SelectedItem as FileSystemItem)?.FullPath ?? ProjectData.Current?.Path; if (p != null) EditorCommands.RevealInFinder(p); }
        private void OnRefresh(object s, RoutedEventArgs e) { if (ProjectData.Current != null) { FileExplorerService.Instance.Initialize(ProjectData.Current.Path); Reload(); } }
    }
}
