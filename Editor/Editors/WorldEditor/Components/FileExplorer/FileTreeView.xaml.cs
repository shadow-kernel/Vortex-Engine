using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Editor.Core.Data;
using Editor.Editors.WorldEditor.Components.FileExplorer.Models;
using Editor.Editors.WorldEditor.Components.FileExplorer.Services;

namespace Editor.Editors.WorldEditor.Components.FileExplorer
{
    public partial class FileTreeView : UserControl
    {
        private FileExplorerService _explorerService;
        private Point _dragStartPoint;
        private FileSystemItem _draggedItem;
        private bool _isUpdatingFromExplorer;

        public FileTreeView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _explorerService = FileExplorerService.Instance;
            _explorerService.TreeStructureChanged += OnTreeStructureChanged;
            _explorerService.CurrentFolderChanged += OnCurrentFolderChangedFromExplorer;

            // Projekt aus DataContext laden
            InitializeFromProject();

            // DataContext Änderungen überwachen
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.DataContextChanged += OnWindowDataContextChanged;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_explorerService != null)
            {
                _explorerService.TreeStructureChanged -= OnTreeStructureChanged;
                _explorerService.CurrentFolderChanged -= OnCurrentFolderChangedFromExplorer;
            }

            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.DataContextChanged -= OnWindowDataContextChanged;
            }
        }

        private void OnWindowDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            InitializeFromProject();
        }

        private void InitializeFromProject()
        {
            var project = GetCurrentProject();
            if (project != null && !string.IsNullOrEmpty(project.Path))
            {
                _explorerService.Initialize(project.Path);
                DataContext = _explorerService.RootItem;
                FolderTree.Visibility = Visibility.Visible;
                EmptyText.Visibility = Visibility.Collapsed;
            }
            else
            {
                DataContext = null;
                FolderTree.Visibility = Visibility.Collapsed;
                EmptyText.Visibility = Visibility.Visible;
            }
        }

        private ProjectData GetCurrentProject()
        {
            var window = Window.GetWindow(this);
            return window?.DataContext as ProjectData;
        }

        private void OnTreeStructureChanged(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                DataContext = null;
                DataContext = _explorerService.RootItem;
            });
        }

        /// <summary>
        /// Synchronisiert den Tree wenn ein Ordner in der Detail-Ansicht geöffnet wird.
        /// </summary>
        private void OnCurrentFolderChangedFromExplorer(object sender, FileSystemItem folder)
        {
            if (folder == null) return;

            _isUpdatingFromExplorer = true;
            
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // Pfad zum Ordner aufklappen
                    ExpandPathToItem(folder);
                }
                finally
                {
                    _isUpdatingFromExplorer = false;
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Klappt alle Ordner im Pfad zum angegebenen Item auf.
        /// </summary>
        private void ExpandPathToItem(FileSystemItem targetItem)
        {
            if (targetItem == null || _explorerService.RootItem == null) return;

            // Baue den Pfad vom Root zum Ziel
            var pathParts = new System.Collections.Generic.List<string>();
            var targetPath = targetItem.FullPath;
            var rootPath = _explorerService.RootItem.FullPath;
            
            if (!targetPath.StartsWith(rootPath))
                return;

            // Navigiere vom Root zum Ziel
            var currentItem = _explorerService.RootItem;
            var relativePath = targetPath.Substring(rootPath.Length).TrimStart(System.IO.Path.DirectorySeparatorChar);
            
            if (string.IsNullOrEmpty(relativePath))
            {
                // Ziel ist der Root
                currentItem.IsExpanded = true;
                currentItem.IsSelected = true;
                return;
            }

            var parts = relativePath.Split(System.IO.Path.DirectorySeparatorChar);
            
            foreach (var part in parts)
            {
                // Lade Kinder falls nötig
                if (currentItem.Children.Count == 0)
                {
                    currentItem.LoadDirectoriesOnly();
                }
                
                // Expandiere den aktuellen Ordner
                currentItem.IsExpanded = true;
                
                // Finde das Kind mit dem passenden Namen
                FileSystemItem foundChild = null;
                foreach (var child in currentItem.Children)
                {
                    if (child.Name.Equals(part, System.StringComparison.OrdinalIgnoreCase))
                    {
                        foundChild = child;
                        break;
                    }
                }
                
                if (foundChild != null)
                {
                    currentItem = foundChild;
                }
                else
                {
                    // Kind nicht gefunden, abbrechen
                    break;
                }
            }

            // Wähle das gefundene Item aus UND klappe es auf
            currentItem.IsExpanded = true;
            currentItem.IsSelected = true;
        }

        private void OnSelectedFolderChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // Verhindere Rekursion wenn das Update von der Explorer-Ansicht kommt
            if (_isUpdatingFromExplorer) return;
            
            var selectedItem = e.NewValue as FileSystemItem;
            if (selectedItem != null && selectedItem.IsDirectory)
            {
                _explorerService.NavigateTo(selectedItem);
            }
        }

        /// <summary>
        /// Lädt Unterordner wenn ein TreeViewItem aufgeklappt wird.
        /// </summary>
        private void OnTreeViewItemExpanded(object sender, RoutedEventArgs e)
        {
            var treeViewItem = e.OriginalSource as TreeViewItem;
            var item = treeViewItem?.DataContext as FileSystemItem;
            
            if (item != null && item.IsDirectory)
            {
                // Lade Unterordner für alle Kinder (für das Anzeigen der Expander)
                foreach (var child in item.Children)
                {
                    if (child.IsDirectory && child.Children.Count == 0 && child.HasSubDirectories)
                    {
                        child.LoadDirectoriesOnly();
                    }
                }
            }
            
            e.Handled = true; // Verhindere Bubble-Up
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            var project = GetCurrentProject();
            if (project != null)
            {
                _explorerService.Initialize(project.Path);
                DataContext = _explorerService.RootItem;
            }
        }

        private void OnCollapseAllClick(object sender, RoutedEventArgs e)
        {
            CollapseAllItems(_explorerService.RootItem);
        }

        private void CollapseAllItems(FileSystemItem item)
        {
            if (item == null) return;
            
            item.IsExpanded = false;
            foreach (var child in item.Children)
            {
                CollapseAllItems(child);
            }
        }

        #region Context Menu

        private void OnNewFolderClick(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem?.Parent as ContextMenu;
            var item = (contextMenu?.PlacementTarget as FrameworkElement)?.DataContext as FileSystemItem;
            
            if (item != null && item.IsDirectory)
            {
                _explorerService.NavigateTo(item);
            }
            
            var newFolder = _explorerService.CreateFolder();
            if (newFolder != null)
            {
                newFolder.IsRenaming = true;
            }
        }

        private void OnNewScriptClick(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem?.Parent as ContextMenu;
            var item = (contextMenu?.PlacementTarget as FrameworkElement)?.DataContext as FileSystemItem;
            
            if (item != null && item.IsDirectory)
            {
                _explorerService.NavigateTo(item);
            }
            
            var newScript = _explorerService.CreateFile("NewScript.cs", GetDefaultScriptContent("NewScript"));
            if (newScript != null)
            {
                newScript.IsRenaming = true;
            }
        }

        private string GetDefaultScriptContent(string className)
        {
            return $@"using System;

namespace Game
{{
    public class {className}
    {{
        public void Start()
        {{
            // Called when the game starts
        }}

        public void Update(float deltaTime)
        {{
            // Called every frame
        }}
    }}
}}
";
        }

        private void OnRenameClick(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem?.Parent as ContextMenu;
            var item = (contextMenu?.PlacementTarget as FrameworkElement)?.DataContext as FileSystemItem;
            
            if (item != null)
            {
                item.IsRenaming = true;
            }
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem?.Parent as ContextMenu;
            var item = (contextMenu?.PlacementTarget as FrameworkElement)?.DataContext as FileSystemItem;
            
            if (item != null)
            {
                _explorerService.Delete(item);
            }
        }

        private void OnShowInExplorerClick(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem?.Parent as ContextMenu;
            var item = (contextMenu?.PlacementTarget as FrameworkElement)?.DataContext as FileSystemItem;
            
            if (item != null)
            {
                _explorerService.OpenInExplorer(item);
            }
        }

        #endregion

        #region Drag and Drop

        private void OnTreeViewItemMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _dragStartPoint = e.GetPosition(null);
                var treeViewItem = sender as TreeViewItem;
                _draggedItem = treeViewItem?.DataContext as FileSystemItem;
            }
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);

            if (e.LeftButton != MouseButtonState.Pressed || _draggedItem == null)
                return;

            Point currentPosition = e.GetPosition(null);
            Vector diff = _dragStartPoint - currentPosition;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                var data = new DataObject("FileSystemItem", _draggedItem);
                DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
                _draggedItem = null;
            }
        }

        private void OnTreeViewItemDragOver(object sender, DragEventArgs e)
        {
            var targetItem = (sender as TreeViewItem)?.DataContext as FileSystemItem;
            var sourceItem = e.Data.GetData("FileSystemItem") as FileSystemItem;

            if (targetItem == null || sourceItem == null || !targetItem.IsDirectory ||
                targetItem == sourceItem || targetItem.FullPath.StartsWith(sourceItem.FullPath + System.IO.Path.DirectorySeparatorChar))
            {
                e.Effects = DragDropEffects.None;
            }
            else
            {
                e.Effects = DragDropEffects.Move;
            }
            e.Handled = true;
        }

        private void OnTreeViewItemDrop(object sender, DragEventArgs e)
        {
            var targetItem = (sender as TreeViewItem)?.DataContext as FileSystemItem;
            var sourceItem = e.Data.GetData("FileSystemItem") as FileSystemItem;

            if (targetItem != null && sourceItem != null && targetItem.IsDirectory)
            {
                _explorerService.MoveItem(sourceItem, targetItem);
            }
            e.Handled = true;
        }

        #endregion
    }
}
