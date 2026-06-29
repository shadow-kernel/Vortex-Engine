using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.Dialogs;
using Editor.Editors.WorldEditor.Components.FileExplorer.Models;
using Editor.Editors.WorldEditor.Components.FileExplorer.Services;

namespace Editor.Editors.WorldEditor.Components.FileExplorer
{
    public partial class FileExplorerView : UserControl
    {
        private FileExplorerService _explorerService;
        private Point _dragStartPoint;
        private FileSystemItem _draggedItem;
        private List<FileSystemItem> _draggedItems = new List<FileSystemItem>();
        private Stack<FileSystemItem> _navigationHistory = new Stack<FileSystemItem>();
        
        // Clipboard for Copy/Cut operations
        private List<FileSystemItem> _clipboardItems = new List<FileSystemItem>();
        private bool _isCutOperation = false;
        
        // Marquee Selection fields
        private bool _isMarqueeSelecting = false;
        private Point _marqueeStartPoint;
        private bool _isDragDropOperation = false;

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
            var selectedItems = FileList.SelectedItems.Cast<FileSystemItem>().ToList();

            // Handle Ctrl key combinations
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.C:
                        // Copy
                        if (selectedItems.Count > 0)
                        {
                            CopyToClipboard(selectedItems, isCut: false);
                            e.Handled = true;
                        }
                        break;
                    case Key.X:
                        // Cut
                        if (selectedItems.Count > 0)
                        {
                            CopyToClipboard(selectedItems, isCut: true);
                            e.Handled = true;
                        }
                        break;
                    case Key.V:
                        // Paste
                        PasteFromClipboard();
                        e.Handled = true;
                        break;
                    case Key.A:
                        // Select All
                        FileList.SelectAll();
                        e.Handled = true;
                        break;
                }
                return;
            }

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
                // Delete all selected items
                if (selectedItems.Count > 0)
                {
                    DeleteSelectedItems(selectedItems);
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

        #region Clipboard Operations (Copy/Cut/Paste)

        private void CopyToClipboard(List<FileSystemItem> items, bool isCut)
        {
            // Clear previous cut state
            ClearCutState();
            
            _clipboardItems = new List<FileSystemItem>(items);
            _isCutOperation = isCut;
            
            // Set visual cut state
            if (isCut)
            {
                foreach (var item in items)
                {
                    item.IsCut = true;
                }
            }
            
            // Also copy to Windows clipboard for external paste
            var filePaths = items.Select(i => i.FullPath).ToArray();
            var dataObject = new DataObject();
            dataObject.SetData(DataFormats.FileDrop, filePaths);
            
            // Set preferred drop effect (Move for Cut, Copy for Copy)
            var dropEffect = isCut ? DragDropEffects.Move : DragDropEffects.Copy;
            var effectData = new System.IO.MemoryStream(BitConverter.GetBytes((int)dropEffect));
            dataObject.SetData("Preferred DropEffect", effectData);
            
            Clipboard.SetDataObject(dataObject, true);
        }

        private void ClearCutState()
        {
            // Clear visual cut state from previous items
            foreach (var item in _clipboardItems)
            {
                item.IsCut = false;
            }
        }

        private void PasteFromClipboard()
        {
            if (_explorerService.CurrentFolder == null)
                return;

            string targetFolder = _explorerService.CurrentFolder.FullPath;
            var commands = new List<IUndoableCommand>();

            // First try internal clipboard
            if (_clipboardItems.Count > 0)
            {
                foreach (var item in _clipboardItems)
                {
                    string destPath = System.IO.Path.Combine(targetFolder, item.Name);
                    
                    if (_isCutOperation)
                    {
                        // Move the item - create MoveItemCommand
                        item.IsCut = false; // Clear cut state before moving
                        destPath = GetUniqueFileName(destPath, item.IsDirectory);
                        commands.Add(new MoveItemCommand(item.FullPath, destPath, item.IsDirectory));
                    }
                    else
                    {
                        // Copy the item - create CopyFileCommand or CopyFolderCommand
                        destPath = GetUniqueFileName(destPath, item.IsDirectory);
                        if (item.IsDirectory)
                        {
                            commands.Add(new CopyFolderCommand(item.FullPath, destPath));
                        }
                        else
                        {
                            commands.Add(new CopyFileCommand(item.FullPath, destPath));
                        }
                    }
                }

                // Execute as a single undoable command
                if (commands.Count > 0)
                {
                    var pasteCommand = new PasteItemsCommand(commands, _isCutOperation);
                    UndoRedoManager.Instance.Execute(pasteCommand);
                }

                // Clear clipboard after cut operation
                if (_isCutOperation)
                {
                    _clipboardItems.Clear();
                    _isCutOperation = false;
                }

                _explorerService.RefreshCurrentFolderContents();
                return;
            }

            // Try Windows clipboard
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                bool isCut = false;

                // Check if it was a cut operation
                var dataObject = Clipboard.GetDataObject();
                if (dataObject != null && dataObject.GetDataPresent("Preferred DropEffect"))
                {
                    var effectStream = dataObject.GetData("Preferred DropEffect") as System.IO.MemoryStream;
                    if (effectStream != null)
                    {
                        var bytes = new byte[4];
                        effectStream.Read(bytes, 0, 4);
                        var effect = (DragDropEffects)BitConverter.ToInt32(bytes, 0);
                        isCut = (effect & DragDropEffects.Move) == DragDropEffects.Move;
                    }
                }

                foreach (string file in files)
                {
                    try
                    {
                        string destPath = System.IO.Path.Combine(targetFolder, System.IO.Path.GetFileName(file));
                        bool isDirectory = System.IO.Directory.Exists(file);
                        destPath = GetUniqueFileName(destPath, isDirectory);
                        
                        if (isDirectory)
                        {
                            if (isCut)
                            {
                                commands.Add(new MoveItemCommand(file, destPath, true));
                            }
                            else
                            {
                                commands.Add(new CopyFolderCommand(file, destPath));
                            }
                        }
                        else if (System.IO.File.Exists(file))
                        {
                            if (isCut)
                            {
                                commands.Add(new MoveItemCommand(file, destPath, false));
                            }
                            else
                            {
                                commands.Add(new CopyFileCommand(file, destPath));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error pasting {file}: {ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

                // Execute as a single undoable command
                if (commands.Count > 0)
                {
                    var pasteCommand = new PasteItemsCommand(commands, isCut);
                    UndoRedoManager.Instance.Execute(pasteCommand);
                }

                // Clear clipboard after cut
                if (isCut)
                {
                    Clipboard.Clear();
                }

                _explorerService.RefreshCurrentFolderContents();
            }
        }

        private void CopyItem(FileSystemItem item, string targetFolder)
        {
            try
            {
                string destPath = System.IO.Path.Combine(targetFolder, item.Name);
                
                // Handle name conflicts
                destPath = GetUniqueFileName(destPath, item.IsDirectory);

                if (item.IsDirectory)
                {
                    CopyDirectoryRecursive(item.FullPath, destPath);
                }
                else
                {
                    System.IO.File.Copy(item.FullPath, destPath, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error copying {item.Name}: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string GetUniqueFileName(string path, bool isDirectory)
        {
            if (isDirectory)
            {
                if (!System.IO.Directory.Exists(path))
                    return path;
            }
            else
            {
                if (!System.IO.File.Exists(path))
                    return path;
            }

            string directory = System.IO.Path.GetDirectoryName(path);
            string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            string extension = System.IO.Path.GetExtension(path);
            int counter = 1;

            while (true)
            {
                string newPath = System.IO.Path.Combine(directory, $"{fileName} - Copy{(counter > 1 ? $" ({counter})" : "")}{extension}");
                
                if (isDirectory)
                {
                    if (!System.IO.Directory.Exists(newPath))
                        return newPath;
                }
                else
                {
                    if (!System.IO.File.Exists(newPath))
                        return newPath;
                }
                
                counter++;
            }
        }

        private void CopyDirectoryRecursive(string sourceDir, string destDir)
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
                CopyDirectoryRecursive(dir, destSubDir);
            }
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            var selectedItems = FileList.SelectedItems.Cast<FileSystemItem>().ToList();
            if (selectedItems.Count > 0)
            {
                CopyToClipboard(selectedItems, isCut: false);
            }
        }

        private void OnCutClick(object sender, RoutedEventArgs e)
        {
            var selectedItems = FileList.SelectedItems.Cast<FileSystemItem>().ToList();
            if (selectedItems.Count > 0)
            {
                CopyToClipboard(selectedItems, isCut: true);
            }
        }

        private void OnPasteClick(object sender, RoutedEventArgs e)
        {
            PasteFromClipboard();
        }

        #endregion

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _explorerService = FileExplorerService.Instance;
            _explorerService.CurrentFolderChanged += OnCurrentFolderChanged;
            _explorerService.FolderContentsChanged += OnFolderContentsChanged;

            // Initial binding
            UpdateView();
            
            // Update WrapPanel width
            UpdateWrapPanelWidth();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_explorerService != null)
            {
                _explorerService.CurrentFolderChanged -= OnCurrentFolderChanged;
                _explorerService.FolderContentsChanged -= OnFolderContentsChanged;
            }
        }

        private void OnScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateWrapPanelWidth();
        }

        private void UpdateWrapPanelWidth()
        {
            // Find the WrapPanel and set its width to match the ScrollViewer's viewport
            if (FileScrollViewer != null && FileList != null)
            {
                var wrapPanel = FindVisualChild<WrapPanel>(FileList);
                if (wrapPanel != null)
                {
                    // Account for padding and scrollbar
                    double scrollbarWidth = SystemParameters.VerticalScrollBarWidth;
                    double availableWidth = FileScrollViewer.ActualWidth - scrollbarWidth - 16; // 16 for padding
                    if (availableWidth > 0)
                    {
                        wrapPanel.Width = availableWidth;
                    }
                }
            }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    return typedChild;
                    
                var result = FindVisualChild<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void OnFileListMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Forward mouse wheel events to the outer ScrollViewer
            if (FileScrollViewer != null)
            {
                FileScrollViewer.ScrollToVerticalOffset(FileScrollViewer.VerticalOffset - e.Delta);
                e.Handled = true;
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
                // Nach dem Refresh das neue Item ausw�hlen und umbenennen
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Item in der Liste finden und ausw�hlen
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
                // Nach dem Refresh das neue Item ausw�hlen und umbenennen
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Item in der Liste finden und ausw�hlen
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

        private void OnNewShaderClick(object sender, RoutedEventArgs e)
        {
            var newFile = _explorerService.CreateFile("NewShader.shader", GetDefaultShaderContent());
            SelectAndRenameNewItem(newFile);
        }

        private void OnNewHlslClick(object sender, RoutedEventArgs e)
        {
            var newFile = _explorerService.CreateFile("NewShader.hlsl", GetDefaultHlslContent());
            SelectAndRenameNewItem(newFile);
        }

        private void OnNewJsonClick(object sender, RoutedEventArgs e)
        {
            var newFile = _explorerService.CreateFile("NewFile.json", GetDefaultJsonContent());
            SelectAndRenameNewItem(newFile);
        }

        private void OnNewXmlClick(object sender, RoutedEventArgs e)
        {
            var newFile = _explorerService.CreateFile("NewFile.xml", GetDefaultXmlContent());
            SelectAndRenameNewItem(newFile);
        }

        private void OnNewSceneClick(object sender, RoutedEventArgs e)
        {
            var newFile = _explorerService.CreateFile("NewScene.scene", GetDefaultSceneContent());
            SelectAndRenameNewItem(newFile);
        }

        private void OnNewPrefabClick(object sender, RoutedEventArgs e)
        {
            var newFile = _explorerService.CreateFile("NewPrefab.prefab", GetDefaultPrefabContent());
            SelectAndRenameNewItem(newFile);
        }

        private void OnNewMaterialClick(object sender, RoutedEventArgs e)
        {
            var newFile = _explorerService.CreateFile("NewMaterial.mat", GetDefaultMaterialContent());
            SelectAndRenameNewItem(newFile);
        }

        private void SelectAndRenameNewItem(FileSystemItem newItem)
        {
            if (newItem != null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var item in _explorerService.CurrentFolderContents)
                    {
                        if (item.FullPath == newItem.FullPath)
                        {
                            FileList.SelectedItem = item;
                            item.IsRenaming = true;
                            break;
                        }
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private string GetDefaultShaderContent()
        {
            return @"Shader ""Custom/NewShader""
{
    Properties
    {
        _MainTex (""Texture"", 2D) = ""white"" {}
        _Color (""Color"", Color) = (1, 1, 1, 1)
    }
    
    SubShader
    {
        Pass
        {
            // Shader code here
        }
    }
}
";
        }

        private string GetDefaultHlslContent()
        {
            return @"// HLSL Shader

cbuffer ConstantBuffer : register(b0)
{
    float4x4 WorldViewProjection;
}

struct VS_INPUT
{
    float4 Position : POSITION;
    float2 TexCoord : TEXCOORD0;
};

struct PS_INPUT
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

PS_INPUT VS_Main(VS_INPUT input)
{
    PS_INPUT output;
    output.Position = mul(input.Position, WorldViewProjection);
    output.TexCoord = input.TexCoord;
    return output;
}

float4 PS_Main(PS_INPUT input) : SV_TARGET
{
    return float4(1.0, 1.0, 1.0, 1.0);
}
";
        }

        private string GetDefaultJsonContent()
        {
            return @"{
    ""name"": ""NewFile"",
    ""version"": ""1.0"",
    ""data"": {
    }
}
";
        }

        private string GetDefaultXmlContent()
        {
            return @"<?xml version=""1.0"" encoding=""utf-8""?>
<root>
    <data>
    </data>
</root>
";
        }

        private string GetDefaultSceneContent()
        {
            return @"{
    ""name"": ""NewScene"",
    ""version"": ""1.0"",
    ""entities"": [],
    ""settings"": {
        ""ambientLight"": { ""r"": 0.2, ""g"": 0.2, ""b"": 0.2, ""a"": 1.0 },
        ""gravity"": { ""x"": 0.0, ""y"": -9.81, ""z"": 0.0 }
    }
}
";
        }

        private string GetDefaultPrefabContent()
        {
            return @"{
    ""name"": ""NewPrefab"",
    ""version"": ""1.0"",
    ""components"": [],
    ""transform"": {
        ""position"": { ""x"": 0.0, ""y"": 0.0, ""z"": 0.0 },
        ""rotation"": { ""x"": 0.0, ""y"": 0.0, ""z"": 0.0, ""w"": 1.0 },
        ""scale"": { ""x"": 1.0, ""y"": 1.0, ""z"": 1.0 }
    }
}
";
        }

        private string GetDefaultMaterialContent()
        {
            return @"{
    ""name"": ""NewMaterial"",
    ""version"": ""1.0"",
    ""shader"": ""Standard"",
    ""properties"": {
        ""color"": { ""r"": 1.0, ""g"": 1.0, ""b"": 1.0, ""a"": 1.0 },
        ""metallic"": 0.0,
        ""smoothness"": 0.5
    },
    ""textures"": {
        ""albedo"": null,
        ""normal"": null,
        ""metallic"": null
    }
}
";
}

private void OnEditTagsClick(object sender, RoutedEventArgs e)
{
    var menuItem = sender as MenuItem;
    var contextMenu = menuItem?.Parent as ContextMenu;
    var item = (contextMenu?.PlacementTarget as FrameworkElement)?.DataContext as FileSystemItem;

    if (item == null || item.IsDirectory) return;

    // Load existing tags for this file
    var metaPath = item.FullPath + ".vmeta";
    Guid assetGuid = Guid.NewGuid();
    var existingTags = new List<string>();

    if (System.IO.File.Exists(metaPath))
    {
        try
        {
            var meta = AssetMetadataService.Instance.LoadMetadata(metaPath);
            if (meta != null)
            {
                assetGuid = meta.Guid;
                existingTags = meta.Tags ?? new List<string>();
            }
        }
        catch { }
    }

    // Also get tags from tag service
    var serviceTags = AssetTagService.Instance.GetTags(assetGuid);
    foreach (var tag in serviceTags)
    {
        if (!existingTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            existingTags.Add(tag);
    }

    // Show tag editor dialog
    var dialog = new AssetTagEditorDialog(assetGuid, item.Name, existingTags);
    dialog.Owner = Window.GetWindow(this);
            
    if (dialog.ShowDialog() == true)
    {
        // Save the updated tags
                AssetTagService.Instance.SetTags(assetGuid, dialog.ResultTags);
                
                // Also update vmeta file if exists
                if (System.IO.File.Exists(metaPath))
                {
                    try
                    {
                        var meta = AssetMetadataService.Instance.LoadMetadata(metaPath);
                        if (meta != null)
                        {
                            meta.Tags = dialog.ResultTags.ToList();
                            AssetMetadataService.Instance.SaveMetadata(metaPath, meta);
                        }
                    }
                    catch { }
                }
            }
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
    // Delete all selected items
    var selectedItems = FileList.SelectedItems.Cast<FileSystemItem>().ToList();
    if (selectedItems.Count > 0)
    {
        DeleteSelectedItems(selectedItems);
    }
}

/// <summary>
        /// L�scht mehrere Items als ein einzelner Undo-f�higer Command.
        /// </summary>
        private void DeleteSelectedItems(List<FileSystemItem> items)
        {
            if (items == null || items.Count == 0)
                return;

            // Best�tigungsdialog
            string message = items.Count == 1
                ? $"M�chten Sie '{items[0].Name}' wirklich l�schen?"
                : $"M�chten Sie {items.Count} Elemente wirklich l�schen?";

            var result = MessageBox.Show(
                $"{message}\n\nDiese Aktion kann mit Strg+Z r�ckg�ngig gemacht werden.",
                "L�schen best�tigen",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                var commands = new List<IUndoableCommand>();

                foreach (var item in items)
                {
                    if (item.IsDirectory)
                    {
                        commands.Add(new DeleteFolderCommand(item.FullPath));
                    }
                    else
                    {
                        commands.Add(new DeleteFileCommand(item.FullPath));
                    }
                }

                // Als ein einzelner Command ausf�hren
                if (commands.Count == 1)
                {
                    UndoRedoManager.Instance.Execute(commands[0]);
                }
                else if (commands.Count > 1)
                {
                    var compositeCommand = new DeleteItemsCommand(commands, items.Count);
                    UndoRedoManager.Instance.Execute(compositeCommand);
                }

                _explorerService.RefreshCurrentFolderContents();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim L�schen: {ex.Message}",
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            try { Services.FileExplorerService.Instance.ApplyFilter(TreeSearchBox.Text); } catch { }
        }

        private void OnClearSearchClick(object sender, RoutedEventArgs e)
        {
            if (TreeSearchBox != null) TreeSearchBox.Text = ""; // triggers OnSearchTextChanged -> reset
        }

        #endregion

        #region Rename Handling

        private void OnRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null && (bool)e.NewValue)
            {
                // TextBox wurde sichtbar - fokussieren und Text ausw�hlen
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
                // Urspr�nglichen Namen wiederherstellen
                item.Name = System.IO.Path.GetFileName(item.FullPath);
                item.IsRenaming = false;
                e.Handled = true;
            }
        }

        private void FinishRename(FileSystemItem item, string newName)
        {
            item.IsRenaming = false;
            
            if (string.IsNullOrWhiteSpace(newName))
            {
                // Bei leerem Namen: Originalnamen wiederherstellen
                item.Name = System.IO.Path.GetFileName(item.FullPath);
                return;
            }

            // Den echten aktuellen Namen aus dem Pfad holen
            string currentActualName = System.IO.Path.GetFileName(item.FullPath);
            
            if (newName == currentActualName)
            {
                // Keine �nderung - Name wiederherstellen falls Binding ihn ge�ndert hat
                item.Name = currentActualName;
                return;
            }

            // Umbenennung durchf�hren
            bool success = _explorerService.Rename(item, newName);
            
            if (!success)
            {
                // Bei Fehler: Originalnamen wiederherstellen
                item.Name = System.IO.Path.GetFileName(item.FullPath);
            }
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
                
                // Collect all selected items for multi-drag
                // If the clicked item is already selected, drag all selected items
                // If not selected, only drag the clicked item
                if (_draggedItem != null)
                {
                    if (FileList.SelectedItems.Contains(_draggedItem))
                    {
                        _draggedItems = FileList.SelectedItems.Cast<FileSystemItem>().ToList();
                    }
                    else
                    {
                        _draggedItems = new List<FileSystemItem> { _draggedItem };
                    }
                }
            }
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);

            if (e.LeftButton != MouseButtonState.Pressed || _draggedItem == null || _draggedItems.Count == 0)
                return;

            Point currentPosition = e.GetPosition(null);
            Vector diff = _dragStartPoint - currentPosition;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                // Use a list for multi-item drag
                var data = new DataObject("FileSystemItems", _draggedItems);
                System.Windows.DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
                _draggedItem = null;
                _draggedItems.Clear();
            }
        }

        private void OnItemDragOver(object sender, DragEventArgs e)
        {
            var targetItem = (sender as ListBoxItem)?.DataContext as FileSystemItem;
            var sourceItems = e.Data.GetData("FileSystemItems") as List<FileSystemItem>;

            if (targetItem == null || sourceItems == null || sourceItems.Count == 0 || !targetItem.IsDirectory)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // Check if any source item is invalid for the drop
            foreach (var sourceItem in sourceItems)
            {
                if (targetItem == sourceItem || 
                    targetItem.FullPath.StartsWith(sourceItem.FullPath + System.IO.Path.DirectorySeparatorChar))
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }
            }

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void OnItemDrop(object sender, DragEventArgs e)
        {
            var targetItem = (sender as ListBoxItem)?.DataContext as FileSystemItem;
            var sourceItems = e.Data.GetData("FileSystemItems") as List<FileSystemItem>;

            if (targetItem != null && sourceItems != null && targetItem.IsDirectory)
            {
                // Move all selected items to the target folder
                foreach (var sourceItem in sourceItems)
                {
                    if (sourceItem != targetItem)
                    {
                        _explorerService.MoveItem(sourceItem, targetItem);
                    }
                }
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
                                CopyDirectoryRecursive(file, destPath);
                            }
                            else if (IsModelDropFile(file))
                            {
                                // Models NEVER get a flat copy: route through ModelImportService so a clean folder
                                // structure (<name>/ + materials/.vmat + extracted textures) is created in the
                                // CURRENT explorer folder.
                                Editor.Core.Services.ModelImportService.Instance.ImportModel(file, ModelTargetFolderForCurrent());
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

        /// <summary>True for 3D-model files that must be imported via ModelImportService, not flat-copied.</summary>
        private static bool IsModelDropFile(string file)
        {
            if (!System.IO.File.Exists(file)) return false;
            var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            return ext == ".fbx" || ext == ".obj" || ext == ".gltf" || ext == ".glb" ||
                   ext == ".dae" || ext == ".3ds" || ext == ".blend" || ext == ".vmesh";
        }

        /// <summary>The current explorer folder expressed relative to Assets (e.g. "Models/fnaf"), for ModelImportService.</summary>
        private string ModelTargetFolderForCurrent()
        {
            try
            {
                var projectPath = Editor.Core.Data.ProjectData.Current?.Path;
                var cur = _explorerService?.CurrentFolder?.FullPath;
                if (!string.IsNullOrEmpty(projectPath) && !string.IsNullOrEmpty(cur))
                {
                    var assetsRoot = System.IO.Path.Combine(projectPath, "Assets");
                    if (cur.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        var rel = cur.Substring(assetsRoot.Length).Trim('\\', '/').Replace('\\', '/');
                        if (!string.IsNullOrEmpty(rel)) return rel;
                    }
                }
            }
            catch { }
            return "Models";
        }

                    #endregion

                    #region Marquee Selection (Drag Selection Rectangle)

                    private void OnScrollViewerMouseDown(object sender, MouseButtonEventArgs e)
                    {
                        // Check if clicking on the scrollbar - don't start marquee selection
                        Point positionInScrollViewer = e.GetPosition(FileScrollViewer);
                        if (IsClickOnScrollbar(positionInScrollViewer))
                        {
                            return; // Let scrollbar handle the click
                        }

                        // Only start marquee selection if clicking on empty area (not on an item)
                        Point positionInFileList = e.GetPosition(FileList);
                        var hitTestResult = VisualTreeHelper.HitTest(FileList, positionInFileList);
            
                        if (hitTestResult != null)
                        {
                            var listBoxItem = FindParent<ListBoxItem>(hitTestResult.VisualHit);
                            if (listBoxItem != null)
                            {
                                // Clicked on an item - allow normal selection/drag behavior
                                _isDragDropOperation = true;
                                return;
                            }
                        }

                        // Start marquee selection
                        _isDragDropOperation = false;
                        _isMarqueeSelecting = true;
            
                        // Get position relative to the Grid that contains both ScrollViewer and Canvas
                        var parentGrid = SelectionCanvas.Parent as Grid;
                        _marqueeStartPoint = e.GetPosition(parentGrid);
            
                        // Clear selection if Ctrl is not pressed
                        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                        {
                            FileList.SelectedItems.Clear();
                        }

                        // Capture mouse
                        Mouse.Capture(parentGrid, CaptureMode.SubTree);
            
                        // Initialize selection rectangle
                        SelectionRectangle.Visibility = Visibility.Visible;
                        Canvas.SetLeft(SelectionRectangle, _marqueeStartPoint.X);
                        Canvas.SetTop(SelectionRectangle, _marqueeStartPoint.Y);
                        SelectionRectangle.Width = 0;
                        SelectionRectangle.Height = 0;

                        e.Handled = true;
                    }

                    private bool IsClickOnScrollbar(Point positionInScrollViewer)
                    {
                        // Check if click is in the scrollbar area (right side)
                        double scrollbarWidth = SystemParameters.VerticalScrollBarWidth;
                        double contentWidth = FileScrollViewer.ActualWidth - scrollbarWidth;
                        
                        // If clicking on the right edge where the scrollbar would be
                        if (positionInScrollViewer.X >= contentWidth)
                        {
                            return true;
                        }
                        
                        return false;
                    }

                    private void OnScrollViewerMouseMove(object sender, MouseEventArgs e)
                    {
                        if (!_isMarqueeSelecting || _isDragDropOperation)
                            return;

                        if (e.LeftButton != MouseButtonState.Pressed)
                        {
                            // Mouse was released outside - end selection
                            EndMarqueeSelection();
                            return;
                        }

                        var parentGrid = SelectionCanvas.Parent as Grid;
                        Point currentPoint = e.GetPosition(parentGrid);

                        // Clamp to grid bounds
                        currentPoint.X = Math.Max(0, Math.Min(currentPoint.X, parentGrid.ActualWidth));
                        currentPoint.Y = Math.Max(0, Math.Min(currentPoint.Y, parentGrid.ActualHeight));

                        // Calculate rectangle bounds
                        double x = Math.Min(_marqueeStartPoint.X, currentPoint.X);
                        double y = Math.Min(_marqueeStartPoint.Y, currentPoint.Y);
                        double width = Math.Abs(currentPoint.X - _marqueeStartPoint.X);
                        double height = Math.Abs(currentPoint.Y - _marqueeStartPoint.Y);

                        // Update selection rectangle
                        Canvas.SetLeft(SelectionRectangle, x);
                        Canvas.SetTop(SelectionRectangle, y);
                        SelectionRectangle.Width = width;
                        SelectionRectangle.Height = height;

                        // Create the selection rect for hit testing (in Grid coordinates)
                        Rect selectionRect = new Rect(x, y, width, height);

                        // Select items within the rectangle
                        SelectItemsInRect(selectionRect);
                    }

                    private void OnScrollViewerMouseUp(object sender, MouseButtonEventArgs e)
                    {
                        if (_isMarqueeSelecting)
                        {
                            EndMarqueeSelection();
                            e.Handled = true;
                        }
                        _isDragDropOperation = false;
                    }

                    private void EndMarqueeSelection()
                    {
                        _isMarqueeSelecting = false;
                        SelectionRectangle.Visibility = Visibility.Collapsed;
                        Mouse.Capture(null);
                    }

                    private void SelectItemsInRect(Rect selectionRect)
                    {
                        // Get all ListBoxItems and check if they intersect with the selection rectangle
                        var itemsToSelect = new List<FileSystemItem>();
                        var parentGrid = SelectionCanvas.Parent as Grid;
            
                        foreach (var item in FileList.Items)
                        {
                            var listBoxItem = FileList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
                            if (listBoxItem == null) continue;

                            // Get the bounds of the ListBoxItem relative to the parent Grid
                            var itemBounds = GetBoundsRelativeToAncestor(listBoxItem, parentGrid);
                
                            if (itemBounds.HasValue && selectionRect.IntersectsWith(itemBounds.Value))
                            {
                                itemsToSelect.Add(item as FileSystemItem);
                            }
                        }

                        // If Ctrl is pressed, add to existing selection; otherwise replace
                        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                        {
                            FileList.SelectedItems.Clear();
                        }

                        foreach (var item in itemsToSelect)
                        {
                            if (item != null && !FileList.SelectedItems.Contains(item))
                            {
                                FileList.SelectedItems.Add(item);
                            }
                        }
                    }

                    private Rect? GetBoundsRelativeToAncestor(FrameworkElement element, Visual ancestor)
                    {
                        try
                        {
                            var transform = element.TransformToAncestor(ancestor);
                            var topLeft = transform.Transform(new Point(0, 0));
                            var bottomRight = transform.Transform(new Point(element.ActualWidth, element.ActualHeight));
                            return new Rect(topLeft, bottomRight);
                        }
                        catch
                        {
                            return null;
                        }
                    }

                    private T FindParent<T>(DependencyObject child) where T : DependencyObject
                    {
                        var parent = VisualTreeHelper.GetParent(child);
            
                        while (parent != null)
                        {
                            if (parent is T typedParent)
                                return typedParent;
                    
                            parent = VisualTreeHelper.GetParent(parent);
                        }
            
                        return null;
                    }

                    #endregion
                }
            }
