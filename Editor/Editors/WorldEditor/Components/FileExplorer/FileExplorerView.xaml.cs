using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Editor.Core.Data;
using Editor.Editors.WorldEditor.Components.FileExplorer.Models;
using Editor.Editors.WorldEditor.Components.FileExplorer.Services;

namespace Editor.Editors.WorldEditor.Components.FileExplorer
{
    public partial class FileExplorerView : UserControl
    {
        private FileExplorerService _explorerService;
        private Point _dragStartPoint;
        private FileSystemItem _draggedItem;
        private Stack<FileSystemItem> _navigationHistory = new Stack<FileSystemItem>();

        public FileExplorerView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            
            // Keyboard shortcuts
            KeyDown += OnKeyDown;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            var selectedItem = FileList.SelectedItem as FileSystemItem;

            switch (e.Key)
            {
                case Key.F2:
                    if (selectedItem != null)
                    {
                        selectedItem.IsRenaming = true;
                        e.Handled = true;
                    }
                    break;
                case Key.Delete:
                    if (selectedItem != null)
                    {
                        _explorerService.Delete(selectedItem);
                        e.Handled = true;
                    }
                    break;
                case Key.F5:
                    _explorerService.RefreshCurrentFolderContents();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (selectedItem != null)
                    {
                        if (selectedItem.IsDirectory)
                        {
                            if (_explorerService.CurrentFolder != null)
                            {
                                _navigationHistory.Push(_explorerService.CurrentFolder);
                            }
                            _explorerService.NavigateTo(selectedItem);
                        }
                        else
                        {
                            _explorerService.OpenFile(selectedItem);
                        }
                        e.Handled = true;
                    }
                    break;
                case Key.Back:
                    OnUpClick(sender, null);
                    e.Handled = true;
                    break;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _explorerService = FileExplorerService.Instance;
            _explorerService.CurrentFolderChanged += OnCurrentFolderChanged;
            _explorerService.FolderContentsChanged += OnFolderContentsChanged;

            // Initial binding
            UpdateView();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_explorerService != null)
            {
                _explorerService.CurrentFolderChanged -= OnCurrentFolderChanged;
                _explorerService.FolderContentsChanged -= OnFolderContentsChanged;
            }
        }

        private void OnCurrentFolderChanged(object sender, FileSystemItem e)
        {
            UpdateView();
        }

        private void OnFolderContentsChanged(object sender, EventArgs e)
        {
            UpdateView();
        }

        private void UpdateView()
        {
            if (_explorerService == null) return;

            FileList.ItemsSource = _explorerService.CurrentFolderContents;
            UpdateBreadcrumb();
            UpdateEmptyState();
        }

        private void UpdateBreadcrumb()
        {
            var pathItems = new List<FileSystemItem>();
            var current = _explorerService.CurrentFolder;
            
            while (current != null)
            {
                pathItems.Insert(0, current);
                current = current.Parent;
            }

            BreadcrumbPath.ItemsSource = pathItems;
        }

        private void UpdateEmptyState()
        {
            bool isEmpty = _explorerService.CurrentFolderContents.Count == 0;
            EmptyText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        #region Navigation

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (_navigationHistory.Count > 0)
            {
                var previousFolder = _navigationHistory.Pop();
                _explorerService.NavigateTo(previousFolder);
            }
        }

        private void OnUpClick(object sender, RoutedEventArgs e)
        {
            if (_explorerService.CurrentFolder != null)
            {
                _navigationHistory.Push(_explorerService.CurrentFolder);
            }
            _explorerService.NavigateUp();
        }

        private void OnBreadcrumbClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button?.Tag as FileSystemItem;
            
            if (item != null && item != _explorerService.CurrentFolder)
            {
                if (_explorerService.CurrentFolder != null)
                {
                    _navigationHistory.Push(_explorerService.CurrentFolder);
                }
                _explorerService.NavigateTo(item);
            }
        }

        private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var listBoxItem = sender as ListBoxItem;
            var item = listBoxItem?.DataContext as FileSystemItem;

            if (item == null) return;

            if (item.IsDirectory)
            {
                if (_explorerService.CurrentFolder != null)
                {
                    _navigationHistory.Push(_explorerService.CurrentFolder);
                }
                _explorerService.NavigateTo(item);
            }
            else
            {
                _explorerService.OpenFile(item);
            }
        }

        #endregion

        #region Context Menu Actions

        private void OnOpenClick(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem?.Parent as ContextMenu;
            var item = (contextMenu?.PlacementTarget as FrameworkElement)?.DataContext as FileSystemItem;

            if (item == null) return;

            if (item.IsDirectory)
            {
                _explorerService.NavigateTo(item);
            }
            else
            {
                _explorerService.OpenFile(item);
            }
        }

        private void OnNewFolderClick(object sender, RoutedEventArgs e)
        {
            var newFolder = _explorerService.CreateFolder();
            if (newFolder != null)
            {
                // Nach dem Refresh das neue Item auswählen und umbenennen
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Item in der Liste finden und auswählen
                    foreach (var item in _explorerService.CurrentFolderContents)
                    {
                        if (item.FullPath == newFolder.FullPath)
                        {
                            FileList.SelectedItem = item;
                            item.IsRenaming = true;
                            break;
                        }
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void OnNewScriptClick(object sender, RoutedEventArgs e)
        {
            var newScript = _explorerService.CreateFile("NewScript.cs", GetDefaultScriptContent("NewScript"));
            if (newScript != null)
            {
                // Nach dem Refresh das neue Item auswählen und umbenennen
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Item in der Liste finden und auswählen
                    foreach (var item in _explorerService.CurrentFolderContents)
                    {
                        if (item.FullPath == newScript.FullPath)
                        {
                            FileList.SelectedItem = item;
                            item.IsRenaming = true;
                            break;
                        }
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
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

        private void OnShowCurrentInExplorerClick(object sender, RoutedEventArgs e)
        {
            if (_explorerService.CurrentFolder != null)
            {
                _explorerService.OpenInExplorer(_explorerService.CurrentFolder);
            }
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            _explorerService.RefreshCurrentFolderContents();
        }

        private void OnSearchClick(object sender, RoutedEventArgs e)
        {
            // TODO: Implement search functionality
            MessageBox.Show("Search functionality coming soon!", "Search", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        #region Rename Handling

        private void OnRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null && (bool)e.NewValue)
            {
                // TextBox wurde sichtbar - fokussieren und Text auswählen
                textBox.Dispatcher.BeginInvoke(new Action(() =>
                {
                    textBox.Focus();
                    textBox.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void OnRenameBoxLostFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            var item = textBox?.DataContext as FileSystemItem;

            if (item != null && item.IsRenaming)
            {
                FinishRename(item, textBox.Text);
            }
        }

        private void OnRenameBoxKeyDown(object sender, KeyEventArgs e)
        {
            var textBox = sender as TextBox;
            var item = textBox?.DataContext as FileSystemItem;

            if (item == null) return;

            if (e.Key == Key.Enter)
            {
                FinishRename(item, textBox.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                item.IsRenaming = false;
                e.Handled = true;
            }
        }

        private void FinishRename(FileSystemItem item, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName) || newName == item.Name)
            {
                item.IsRenaming = false;
                return;
            }

            _explorerService.Rename(item, newName);
            item.IsRenaming = false;
        }

        #endregion

        #region Drag and Drop

        private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _dragStartPoint = e.GetPosition(null);
                var listBoxItem = sender as ListBoxItem;
                _draggedItem = listBoxItem?.DataContext as FileSystemItem;
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

        private void OnItemDragOver(object sender, DragEventArgs e)
        {
            var targetItem = (sender as ListBoxItem)?.DataContext as FileSystemItem;
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

        private void OnItemDrop(object sender, DragEventArgs e)
        {
            var targetItem = (sender as ListBoxItem)?.DataContext as FileSystemItem;
            var sourceItem = e.Data.GetData("FileSystemItem") as FileSystemItem;

            if (targetItem != null && sourceItem != null && targetItem.IsDirectory)
            {
                _explorerService.MoveItem(sourceItem, targetItem);
            }
            e.Handled = true;
        }

        private void OnEmptyAreaDragOver(object sender, DragEventArgs e)
        {
            // Allow dropping files from external sources
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void OnEmptyAreaDrop(object sender, DragEventArgs e)
        {
            // Handle files dropped from external sources (like Windows Explorer)
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && _explorerService.CurrentFolder != null)
                {
                    foreach (var file in files)
                    {
                        try
                        {
                            string destPath = System.IO.Path.Combine(_explorerService.CurrentFolder.FullPath, 
                                System.IO.Path.GetFileName(file));
                            
                            if (System.IO.Directory.Exists(file))
                            {
                                CopyDirectory(file, destPath);
                            }
                            else if (System.IO.File.Exists(file))
                            {
                                System.IO.File.Copy(file, destPath, false);
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Error copying {file}: {ex.Message}", "Error", 
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }
            }
            e.Handled = true;
        }

        private void CopyDirectory(string sourceDir, string destDir)
        {
            System.IO.Directory.CreateDirectory(destDir);

            foreach (var file in System.IO.Directory.GetFiles(sourceDir))
            {
                string destFile = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(file));
                System.IO.File.Copy(file, destFile, false);
            }

            foreach (var dir in System.IO.Directory.GetDirectories(sourceDir))
            {
                string destSubDir = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir);
            }
        }

        #endregion
    }
}
