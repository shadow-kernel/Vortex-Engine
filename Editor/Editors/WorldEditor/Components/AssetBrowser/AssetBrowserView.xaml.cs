using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Editor.Core.Assets;
using Editor.Editors.WorldEditor.Components.FileExplorer.Models;
using Editor.Editors.WorldEditor.Components.FileExplorer.Services;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Dialogs;
using Editor.DllWrapper;
using Editor.ECS;

namespace Editor.Editors.WorldEditor.Components.AssetBrowser
{
    public partial class AssetBrowserView : UserControl
    {
        public enum AssetType
        {
            Explorer,   // folder browser: the current folder's subfolders + all files
            Meshes,
            Models,
            Textures,
            Materials,
            Shaders,
            Scripts
        }

        public class AssetItem : INotifyPropertyChanged
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public string TypeName { get; set; }
            public string IconCode { get; set; }
            public string IconColor { get; set; }
            public AssetType Type { get; set; }
            public string Path { get; set; }
            public Guid AssetGuid { get; set; }
            public bool IsImported { get; set; }
            public bool IsFolder { get; set; }
            public FileSystemItem Source { get; set; }   // backing file/folder for explorer items

            /// <summary>Real preview image (texture bitmap / generated color swatch / material sphere).
            /// Generated progressively AFTER the tile is shown (so the browser never freezes); when
            /// null the IconCode glyph is shown instead. Notifies so the tile updates when it lands.</summary>
            private ImageSource _thumbnail;
            public ImageSource Thumbnail
            {
                get => _thumbnail;
                set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        public ObservableCollection<AssetItem> Assets { get; } = new ObservableCollection<AssetItem>();

        public event EventHandler<AssetItem> AssetSelected;
        public event EventHandler<AssetItem> AssetDoubleClicked;

        private AssetType _currentType = AssetType.Explorer;

        // ---- search + folder sync (with the file tree, via FileExplorerService) ----
        private ICollectionView _assetsView;
        private string _searchText = "";
        private string _currentFolderRel = "Assets";   // project-relative folder currently shown
        private bool _syncingFromBrowser;               // echo guard for browser->tree

        public AssetBrowserView()
        {
            InitializeComponent();
            AssetList.ItemsSource = Assets;

            // A filtered view over the assets — drives both the search box and folder-scoping.
            _assetsView = CollectionViewSource.GetDefaultView(Assets);
            _assetsView.Filter = AssetFilterPredicate;

            // Note: MouseDoubleClick is already bound in XAML, don't add it again here
            AssetList.PreviewMouseLeftButtonDown += AssetList_PreviewMouseLeftButtonDown;
            AssetList.PreviewMouseMove += AssetList_PreviewMouseMove;
            Loaded += OnLoaded;
            Unloaded += OnUnloadedCleanup;

            // Subscribe to model import events
            ModelImportService.Instance.ModelImported += OnModelImported;

            // Subscribe to asset database changes
            AssetDatabase.Instance.AssetsChanged += OnAssetsChanged;

            // Stay in sync with the file tree's current folder + its contents.
            FileExplorerService.Instance.CurrentFolderChanged += OnExplorerFolderChanged;
            FileExplorerService.Instance.FolderContentsChanged += OnFolderContentsChanged;
        }

        private void OnUnloadedCleanup(object sender, RoutedEventArgs e)
        {
            // FileExplorerService outlives this control — don't let it keep us alive.
            try { FileExplorerService.Instance.CurrentFolderChanged -= OnExplorerFolderChanged; } catch { }
            try { FileExplorerService.Instance.FolderContentsChanged -= OnFolderContentsChanged; } catch { }
        }

        private void OnFolderContentsChanged(object sender, EventArgs e)
        {
            if (_currentType == AssetType.Explorer)
                Application.Current?.Dispatcher?.Invoke(() => RefreshAssets());
        }

        // ---- search ----
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchText = SearchBox.Text != null ? SearchBox.Text.Trim() : "";
            try { _assetsView?.Refresh(); } catch { }
        }

        private bool AssetFilterPredicate(object o)
        {
            var a = o as AssetItem;
            if (a == null) return false;
            if (!string.IsNullOrEmpty(_searchText) &&
                (a.Name ?? "").IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            return true;
        }

        private bool IsUnderCurrentFolder(string assetRelPath)
        {
            if (string.IsNullOrEmpty(_currentFolderRel) || string.IsNullOrEmpty(assetRelPath)) return true;
            var dir = (System.IO.Path.GetDirectoryName(assetRelPath) ?? "").Replace('\\', '/');
            var cur = _currentFolderRel.Replace('\\', '/').TrimEnd('/');
            if (cur.Equals("Assets", StringComparison.OrdinalIgnoreCase)) return true; // root shows everything
            return dir.Equals(cur, StringComparison.OrdinalIgnoreCase)
                || dir.StartsWith(cur + "/", StringComparison.OrdinalIgnoreCase);
        }

        // ---- folder sync (tree -> browser) ----
        private void OnExplorerFolderChanged(object sender, FileSystemItem folder)
        {
            if (_syncingFromBrowser || folder == null) return;
            Application.Current?.Dispatcher?.Invoke(() => SetCurrentFolder(folder));
        }

        private void SetCurrentFolder(FileSystemItem folder)
        {
            var root = ProjectData.Current?.Path;
            string rel = "Assets";
            try
            {
                if (!string.IsNullOrEmpty(root) && folder != null)
                {
                    var rootFull = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
                    var full = System.IO.Path.GetFullPath(folder.FullPath).TrimEnd('\\', '/');
                    rel = full.Length > rootFull.Length && full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                        ? full.Substring(rootFull.Length).TrimStart('\\', '/').Replace('\\', '/')
                        : (folder.Name ?? "Assets");
                    if (string.IsNullOrEmpty(rel)) rel = "Assets";
                }
            }
            catch { rel = folder?.Name ?? "Assets"; }

            _currentFolderRel = rel;
            UpdateBreadcrumb(rel);
            if (_currentType == AssetType.Explorer) RefreshAssets();   // reload the folder's contents
            else { try { _assetsView?.Refresh(); } catch { } }
        }

        private void UpdateBreadcrumb(string rel)
        {
            if (BreadcrumbPanel == null) return;
            BreadcrumbPanel.Children.Clear();
            BreadcrumbPanel.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#FF73737A"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            var segments = (rel ?? "Assets").Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) segments = new[] { "Assets" };
            var root = ProjectData.Current?.Path;
            string accum = root;

            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0)
                {
                    BreadcrumbPanel.Children.Add(new TextBlock
                    {
                        Text = "",
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 10,
                        Foreground = (Brush)new BrushConverter().ConvertFromString("#FF73737A"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(7, 1, 7, 0)
                    });
                }
                bool last = i == segments.Length - 1;
                accum = accum != null ? System.IO.Path.Combine(accum, segments[i]) : null;
                var seg = new TextBlock
                {
                    Text = segments[i],
                    FontSize = 12,
                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                    Foreground = (Brush)new BrushConverter().ConvertFromString(last ? "#FFE9E9ED" : "#FF9A9AA1"),
                    FontWeight = last ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand
                };
                var targetPath = accum;
                seg.MouseLeftButtonUp += (s, e) => RevealFolderInTree(targetPath);
                BreadcrumbPanel.Children.Add(seg);
            }
        }

        // ---- browser -> tree reveal ----
        private void RevealFolderInTree(string absoluteFolderPath)
        {
            if (string.IsNullOrEmpty(absoluteFolderPath)) return;
            _syncingFromBrowser = true;
            try { FileExplorerService.Instance.NavigateToPath(absoluteFolderPath); }
            finally { _syncingFromBrowser = false; }
            // we navigated ourselves; reflect it locally too
            var folder = FileExplorerService.Instance.CurrentFolder;
            if (folder != null) SetCurrentFolder(folder);
        }

        private void OnModelImported(object sender, Dialogs.ModelImportResult result)
        {
            if (result.Success)
            {
                // Refresh AssetDatabase to pick up the new files
                AssetDatabase.Instance.Refresh();
            }
        }

        private void OnAssetsChanged(object sender, EventArgs e)
        {
            // Refresh the asset list when assets change
            Application.Current.Dispatcher.Invoke(() => RefreshAssets());
        }

        // Drag and drop support
        private Point _dragStartPoint;
        private bool _isDragging;

        private void AssetList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            _isDragging = false;
        }

        private void AssetList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _isDragging = false;
                return;
            }

            var currentPos = e.GetPosition(null);
            var diff = _dragStartPoint - currentPos;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (!_isDragging && AssetList.SelectedItem is AssetItem item)
                {
                    _isDragging = true;
                    
                    var data = new DataObject();
                    data.SetData("AssetItem", item);
                    data.SetData("AssetGuid", item.AssetGuid.ToString());
                    data.SetData("AssetPath", item.Path);
                    
                    System.Windows.DragDrop.DoDragDrop(AssetList, data, DragDropEffects.Copy);
                    _isDragging = false;
                }
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshAssets();
            // initial breadcrumb / folder scope from the shared navigation state
            var cur = FileExplorerService.Instance.CurrentFolder;
            if (cur != null) SetCurrentFolder(cur);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshAssets();
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            // Check Assimp availability for model import
            if ((_currentType == AssetType.Models || _currentType == AssetType.Meshes) && !VortexAPI.IsAssimpAvailable())
            {
                var result = MessageBox.Show(
                    "Model import requires Assimp library.\n\n" +
                    "To enable model import:\n" +
                    "1. Install Assimp NuGet package (version 3.0.0) in Engine project\n" +
                    "2. Add VORTEX_USE_ASSIMP to preprocessor definitions\n" +
                    "3. Rebuild the Engine\n\n" +
                    "See BUILD_SETUP.md and NUGET_TROUBLESHOOTING.md for detailed instructions.\n\n" +
                    "You can still use .vmesh files and textures.\n\n" +
                    "Continue to file picker anyway?",
                    "Assimp Not Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                    
                if (result != MessageBoxResult.Yes)
                    return;
            }
            
            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Multiselect = true; // Allow multiple file selection
            
            switch (_currentType)
            {
                case AssetType.Meshes:
                    dialog.Filter = "3D Models|*.obj;*.fbx;*.gltf;*.glb|All Files|*.*";
                    dialog.Title = "Import Mesh";
                    break;
                case AssetType.Models:
                    dialog.Filter = "3D Models|*.obj;*.fbx;*.gltf;*.glb;*.vmesh|All Files|*.*";
                    dialog.Title = "Import Model";
                    break;
                case AssetType.Textures:
                    dialog.Filter = "Images|*.png;*.jpg;*.jpeg;*.tga;*.bmp;*.hdr;*.dds|All Files|*.*";
                    dialog.Title = "Import Texture";
                    break;
                case AssetType.Materials:
                    dialog.Filter = "Materials|*.vmat;*.mat|All Files|*.*";
                    dialog.Title = "Import Material";
                    break;
                case AssetType.Shaders:
                    dialog.Filter = "Shaders|*.hlsl;*.glsl;*.shader;*.vshader|All Files|*.*";
                    dialog.Title = "Import Shader";
                    break;
            }

            if (dialog.ShowDialog() == true)
            {
                bool isModelType = _currentType == AssetType.Models || _currentType == AssetType.Meshes;
                // Models/meshes ALWAYS go through ModelImportService (folder + materials/.vmat) and land in the
                // CURRENT explorer folder, not a fixed "Models" — so importing while inside a subfolder keeps the
                // model there.
                if (isModelType)
                {
                    string targetFolder = CurrentImportFolder();
                    foreach (var file in dialog.FileNames)
                    {
                        try
                        {
                            var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                            if (ext == ".obj" || ext == ".fbx" || ext == ".gltf" || ext == ".glb" || ext == ".dae" || ext == ".3ds" || ext == ".blend" || ext == ".vmesh")
                                ModelImportService.Instance.ImportModel(file, targetFolder);
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Import] {ex.Message}"); }
                    }
                    RefreshAssets();
                    return;
                }

                // Use the new AssetImportDialog for single file with tag support
                if (dialog.FileNames.Length == 1)
                {
                    var importType = GetImportAssetType(_currentType);
                    var importResult = Dialogs.AssetImportDialog.ShowImportDialog(
                        Window.GetWindow(this),
                        dialog.FileName,
                        importType);

                    if (importResult.Success)
                    {
                        RefreshAssets();
                    }
                }
                else
                {
                    // Batch import without individual dialogs
                    ImportMultipleFiles(dialog.FileNames);
                }
            }
        }

        /// <summary>The folder (relative to Assets) where an import should land — the CURRENT explorer folder,
        /// or "Models" when sitting at the Assets root.</summary>
        private string CurrentImportFolder()
        {
            var rel = (_currentFolderRel ?? "Assets").Replace('\\', '/').Trim('/');
            if (rel.Equals("Assets", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(rel))
                return "Models";
            if (rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring("Assets/".Length);
            return rel;
        }
        
        private Dialogs.AssetImportDialog.ImportAssetType GetImportAssetType(AssetType type)
        {
            return type switch
            {
                AssetType.Meshes => Dialogs.AssetImportDialog.ImportAssetType.Model,
                AssetType.Models => Dialogs.AssetImportDialog.ImportAssetType.Model,
                AssetType.Textures => Dialogs.AssetImportDialog.ImportAssetType.Texture,
                AssetType.Materials => Dialogs.AssetImportDialog.ImportAssetType.Material,
                AssetType.Shaders => Dialogs.AssetImportDialog.ImportAssetType.Shader,
                _ => Dialogs.AssetImportDialog.ImportAssetType.Model
            };
        }
        
        private void ImportMultipleFiles(string[] filePaths)
        {
            var projectPath = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(projectPath)) return;
            
            int successCount = 0;
            int failCount = 0;
            
            foreach (var filePath in filePaths)
            {
                try
                {
                    var extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                    var fileName = System.IO.Path.GetFileName(filePath);
                    var targetFolder = GetDefaultTargetFolder(_currentType);
                    var targetPath = System.IO.Path.Combine(projectPath, targetFolder, fileName);
                    
                    // Ensure directory exists
                    var targetDir = System.IO.Path.GetDirectoryName(targetPath);
                    if (!System.IO.Directory.Exists(targetDir))
                        System.IO.Directory.CreateDirectory(targetDir);
                    
                    // Copy file
                    System.IO.File.Copy(filePath, targetPath, true);
                    
                    // Auto-tag as "Imported"
                    var assetType = DetermineAssetTypeFromExtension(extension);
                    var metadata = new AssetMetadata(assetType, 
                        System.IO.Path.Combine(targetFolder, fileName), fileName);
                    AssetDatabase.Instance.SaveMetadata(metadata, targetPath + AssetDatabase.MetaFileExtension);
                    AssetTagService.Instance.AddTag(metadata.Guid, "Imported");
                    
                    successCount++;
                }
                catch
                {
                    failCount++;
                }
            }
            
            RefreshAssets();
            
            MessageBox.Show(
                $"Import complete.\n\nSuccessful: {successCount}\nFailed: {failCount}",
                "Batch Import",
                MessageBoxButton.OK,
                successCount > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        
        private string GetDefaultTargetFolder(AssetType type)
        {
            return type switch
            {
                AssetType.Meshes => "Assets/Models",
                AssetType.Models => "Assets/Models",
                AssetType.Textures => "Assets/Textures",
                AssetType.Materials => "Assets/Materials",
                AssetType.Shaders => "Assets/Shaders",
                _ => "Assets"
            };
        }
        
        private Core.Assets.AssetType DetermineAssetTypeFromExtension(string extension)
        {
            return extension.ToLowerInvariant() switch
            {
                ".fbx" or ".obj" or ".gltf" or ".glb" or ".vmesh" => Core.Assets.AssetType.Mesh,
                ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".hdr" or ".dds" => Core.Assets.AssetType.Texture,
                ".vmat" or ".mat" => Core.Assets.AssetType.Material,
                ".hlsl" or ".glsl" or ".vshader" => Core.Assets.AssetType.Shader,
                _ => Core.Assets.AssetType.Mesh
            };
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            var contextMenu = new ContextMenu();
            
            switch (_currentType)
            {
                case AssetType.Materials:
                    AddMenuItem(contextMenu, "Standard PBR Material", () => CreateNewMaterial("Opaque"));
                    AddMenuItem(contextMenu, "Unlit Material", () => CreateNewMaterial("Unlit"));
                    AddMenuItem(contextMenu, "Transparent Material", () => CreateNewMaterial("Transparent"));
                    break;
                case AssetType.Shaders:
                    AddMenuItem(contextMenu, "Standard PBR Shader", () => CreateNewShader("VertFrag"));
                    AddMenuItem(contextMenu, "Unlit Shader", () => CreateNewShader("Unlit"));
                    AddMenuItem(contextMenu, "Transparent Shader", () => CreateNewShader("Transparent"));
                    break;
                default:
                    MessageBox.Show("Create new assets from the Materials or Shaders tab.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
            }
            
            contextMenu.PlacementTarget = sender as Button;
            contextMenu.IsOpen = true;
        }

        private void AddMenuItem(ContextMenu menu, string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => action();
            menu.Items.Add(item);
        }

        private void CreateNewMaterial(string type)
        {
            // Show save dialog for material file location
            var projectPath = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(projectPath))
            {
                MessageBox.Show("Please open a project first.", "No Project", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Material",
                Filter = "Vortex Material|*.vmat",
                DefaultExt = ".vmat",
                InitialDirectory = System.IO.Path.Combine(projectPath, "Materials"),
                FileName = $"New{type}Material.vmat"
            };

            // Ensure Materials folder exists
            var materialsDir = System.IO.Path.Combine(projectPath, "Materials");
            if (!System.IO.Directory.Exists(materialsDir))
                System.IO.Directory.CreateDirectory(materialsDir);

            if (saveDialog.ShowDialog() == true)
            {
                var material = new VortexMaterial
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(saveDialog.FileName),
                    BlendMode = type
                };
                
                if (type == "Transparent")
                {
                    material.BaseColor = new float[] { 1f, 1f, 1f, 0.5f };
                }
                
                if (material.Save(saveDialog.FileName))
                {
                    var materialId = VortexAPI.CreateNewMaterial();
                    Assets.Add(new AssetItem
                    {
                        Id = materialId,
                        Name = material.Name,
                        TypeName = $"{type} Material",
                        IconCode = "\uE91B",
                        IconColor = "#FFBD63C5",
                        Type = AssetType.Materials,
                        Path = saveDialog.FileName
                    });
                    
                    AssetDatabase.Instance.Refresh();
                }
            }
        }

        private void CreateNewShader(string type)
        {
            // Show save dialog for shader file location
            var projectPath = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(projectPath))
            {
                MessageBox.Show("Please open a project first.", "No Project", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Shader",
                Filter = "Vortex Shader|*.vshader",
                DefaultExt = ".vshader",
                InitialDirectory = System.IO.Path.Combine(projectPath, "Shaders"),
                FileName = $"New{type}Shader.vshader"
            };

            // Ensure Shaders folder exists
            var shadersDir = System.IO.Path.Combine(projectPath, "Shaders");
            if (!System.IO.Directory.Exists(shadersDir))
                System.IO.Directory.CreateDirectory(shadersDir);

            if (saveDialog.ShowDialog() == true)
            {
                VortexShader shader;
                switch (type)
                {
                    case "Unlit":
                        shader = VortexShader.CreateUnlit();
                        break;
                    case "Transparent":
                        shader = VortexShader.CreateTransparent();
                        break;
                    case "VertFrag":
                    default:
                        shader = VortexShader.CreateStandardPBR();
                        break;
                }
                
                shader.Name = System.IO.Path.GetFileNameWithoutExtension(saveDialog.FileName);
                
                if (shader.Save(saveDialog.FileName))
                {
                    Assets.Add(new AssetItem
                    {
                        Id = Assets.Count,
                        Name = shader.Name,
                        TypeName = $"{shader.ShaderType} Shader",
                        IconCode = "\uE9F5",
                        IconColor = "#FF569CD6",
                        Type = AssetType.Shaders,
                        Path = saveDialog.FileName
                    });
                    
                    AssetDatabase.Instance.Refresh();
                    MessageBox.Show($"Shader created: {shader.Name}\n\nYou can now assign this shader to materials.", 
                        "Shader Created", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }


        private void AssetTypeTab_Checked(object sender, RoutedEventArgs e)
        {
            var radioButton = sender as RadioButton;
            if (radioButton == null) return;

            switch (radioButton.Name)
            {
                case "ExplorerTab": _currentType = AssetType.Explorer; break;
                case "MeshesTab": _currentType = AssetType.Meshes; break;
                case "ModelsTab": _currentType = AssetType.Models; break;
                case "TexturesTab": _currentType = AssetType.Textures; break;
                case "MaterialsTab": _currentType = AssetType.Materials; break;
                case "ShadersTab": _currentType = AssetType.Shaders; break;
                case "ScriptsTab": _currentType = AssetType.Scripts; break;
            }

            RefreshAssets();
        }

        private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AssetList.SelectedItem is AssetItem item)
            {
                AssetSelected?.Invoke(this, item);
            }
        }

        private void AssetList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (AssetList.SelectedItem is AssetItem item)
            {
                // Folder tile -> navigate into it (syncs the tree via CurrentFolderChanged).
                if (item.IsFolder && item.Source != null)
                {
                    FileExplorerService.Instance.NavigateTo(item.Source);
                    return;
                }
                AssetDoubleClicked?.Invoke(this, item);
                // reveal the asset's folder in the file tree (browser -> tree sync)
                if (item.IsImported && !string.IsNullOrEmpty(item.Path))
                {
                    var root = ProjectData.Current?.Path;
                    var dir = System.IO.Path.GetDirectoryName(item.Path);
                    if (!string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(dir))
                        RevealFolderInTree(System.IO.Path.Combine(root, dir));
                }

                // Double-click behaviour for MODELS/MESHES (Unity/Unreal-style):
                //   plain  -> add the model to the scene
                //   Shift  -> open the Model Editor (materials/textures)
                //   Ctrl   -> open a standalone Model Viewer to inspect + fly around it
                // (this also fixes the old bug where double-click opened the editor whose preview hijacked the
                //  shared render queue, so the main viewport showed the model instead of the scene.)
                bool isModel = item.Type == AssetType.Meshes || item.Type == AssetType.Models;
                var mods = System.Windows.Input.Keyboard.Modifiers;
                if (isModel && !string.IsNullOrEmpty(item.Path) && !item.Path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
                {
                    if ((mods & System.Windows.Input.ModifierKeys.Shift) != 0) OpenAssetInEditor(item);
                    else if ((mods & System.Windows.Input.ModifierKeys.Control) != 0) OpenModelViewer(item);
                    else AddAssetToScene(item);
                }
                else
                {
                    OpenOrPlaceAsset(item); // primitives, textures, materials, scripts -> their normal open/edit
                }
            }
        }

        // ---- right-click context menu (built dynamically per selection) ----
        private void AssetList_RightDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as System.Windows.DependencyObject;
            while (dep != null && !(dep is ListBoxItem)) dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
            if (dep is ListBoxItem lbi) { lbi.IsSelected = true; AssetList.SelectedItem = lbi.DataContext; }
            else AssetList.SelectedItem = null;
            AssetList.ContextMenu = BuildAssetContextMenu(AssetList.SelectedItem as AssetItem);
        }

        /// <summary>Open the stress-test panel for a model (enter any copy count + live FPS/draw-call stats).</summary>
        private void StartStress(AssetItem item)
        {
            try
            {
                var projectPath = ProjectData.Current?.Path ?? "";
                string fullPath = item.Path;
                if (!string.IsNullOrEmpty(fullPath) && !System.IO.Path.IsPathRooted(fullPath))
                    fullPath = System.IO.Path.Combine(projectPath, item.Path);
                if (!System.IO.File.Exists(fullPath)) { MessageBox.Show("Model file not found.", "Stress Test", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

                var win = new Dialogs.StressTestDialog(fullPath, item.Name) { Owner = Window.GetWindow(this) };
                win.Show(); // non-modal: the viewport keeps rendering the crowd while this stays open
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[StressTest] {ex.Message}"); }
        }

        private ContextMenu BuildAssetContextMenu(AssetItem item)
        {
            var menu = new ContextMenu { Background = Br("#FF161618"), BorderBrush = Br("#FF3A3A3E") };
            if (item != null)
            {
                if (item.IsFolder)
                    AddMenu(menu, "Open Folder", () => { if (item.Source != null) FileExplorerService.Instance.NavigateTo(item.Source); }, 0xE8B7, "#FFE6B422");
                else
                    AddMenu(menu, "Open / Edit", () => OpenOrPlaceAsset(item), 0xE70F, "#FF9C8CFF");

                if (item.Type == AssetType.Meshes || item.Type == AssetType.Models)
                {
                    AddMenu(menu, "Add to Scene", () => AddAssetToScene(item), 0xE710, "#FF4EC9B0");
                    AddMenu(menu, "Stress Test…", () => StartStress(item), 0xE9D9, "#FFE6B422");
                }
                if (!string.IsNullOrEmpty(item.Path) && item.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    AddMenu(menu, "Assign to selected entity", () => AssignScriptToSelected(item), 0xE71B, "#FF569CD6");

                menu.Items.Add(new Separator());
                AddMenu(menu, "Rename", () => RenameItem(item), 0xE8AC, "#FF9A9AA1");
                AddMenu(menu, "Delete", () => DeleteItem(item), 0xE74D, "#FFCE9178");
                AddMenu(menu, "Show in Explorer", () => { if (item.Source != null) FileExplorerService.Instance.OpenInExplorer(item.Source); }, 0xEC50, "#FF569CD6");
                menu.Items.Add(new Separator());
            }

            AddMenu(menu, "New Folder", () => FileExplorerService.Instance.CreateFolder(), 0xE8F4, "#FFE6B422");
            AddMenu(menu, "New Script", () => CreateScriptHere(), 0xE943, "#FF9B59B6");
            AddMenu(menu, "New Material", () => CreateNewMaterial("Opaque"), 0xE91B, "#FFBD63C5");
            AddMenu(menu, "New Shader", () => CreateNewShader("VertFrag"), 0xE9F5, "#FF569CD6");
            menu.Items.Add(new Separator());
            AddMenu(menu, "Refresh", () => { FileExplorerService.Instance.RefreshCurrentFolderContents(); RefreshAssets(); }, 0xE72C, "#FF9A9AA1");
            return menu;
        }

        private void AddMenu(ContextMenu m, string header, Action act, int glyph = 0, string glyphColor = "#FFC8C8CE")
        {
            var mi = new MenuItem { Header = header, Foreground = Br("#FFE9E9ED") };
            if (glyph != 0)
            {
                mi.Icon = new TextBlock
                {
                    Text = ((char)glyph).ToString(),
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12.5,
                    Foreground = Br(glyphColor),
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            mi.Click += (s, e) => { try { act(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[AssetBrowser] " + ex.Message); } };
            m.Items.Add(mi);
        }

        private void CreateScriptHere()
        {
            try
            {
                var path = Editor.Core.Services.ScriptingService.CreateScript("NewBehaviour");
                Editor.Core.Services.ScriptingService.OpenInVisualStudio(path);
                // reveal the Scripts folder + refresh
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) FileExplorerService.Instance.NavigateToPath(dir);
                AssetDatabase.Instance.Refresh();
                RefreshAssets();
            }
            catch (Exception ex) { MessageBox.Show("Could not create script: " + ex.Message, "Script", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void AssignScriptToSelected(AssetItem item)
        {
            var ent = SelectionService.Instance.SelectedEntity;
            if (ent == null) { MessageBox.Show("Select an entity in the scene first, then assign the script.", "Assign Script", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var rel = Editor.Core.Services.ScriptingService.MakeRelative(ProjectData.Current?.Path, item.Path);
            ent.AddComponent(new Editor.ECS.Components.Scripting.Script(ent, rel));
            SelectionService.Instance.Select(ent); // refresh the inspector
        }

        private void RenameItem(AssetItem item)
        {
            if (item.Source == null) return;
            var name = PromptName("Rename", item.Name);
            if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
            FileExplorerService.Instance.Rename(item.Source, name.Trim());
            RefreshAssets();
        }

        private void DeleteItem(AssetItem item)
        {
            if (item.Source == null) return;
            if (MessageBox.Show("Delete '" + item.Name + "'?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            FileExplorerService.Instance.Delete(item.Source);
            RefreshAssets();
        }

        private string PromptName(string title, string initial)
        {
            string result = null;
            var dlg = new Window
            {
                Width = 360, Height = 150, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this),
                ShowInTaskbar = false, Background = Br("#FF1A1A1C")
            };
            var outer = new Border { BorderBrush = Br("#FF3A3A42"), BorderThickness = new Thickness(1), Padding = new Thickness(16) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, Foreground = Br("#FFF5F5F7"), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
            var tb = new TextBox { Text = initial ?? "", Background = Br("#FF202023"), Foreground = Br("#FFF5F5F7"), BorderBrush = Br("#FF3A3A42"), Padding = new Thickness(8, 6, 8, 6), Height = 30, CaretBrush = Br("#FF6C5CE7") };
            sp.Children.Add(tb);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var ok = new Button { Content = "OK", Width = 76, Height = 28, Background = Br("#FF6C5CE7"), Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(8, 0, 0, 0) };
            var cancel = new Button { Content = "Cancel", Width = 76, Height = 28, Background = Br("#FF26262B"), Foreground = Br("#FFE9E9ED"), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            cancel.Click += (s, e) => dlg.DialogResult = false;
            ok.Click += (s, e) => { result = tb.Text; dlg.DialogResult = true; };
            row.Children.Add(cancel); row.Children.Add(ok);
            sp.Children.Add(row); outer.Child = sp; dlg.Content = outer;
            tb.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
            tb.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { result = tb.Text; dlg.DialogResult = true; } else if (e.Key == System.Windows.Input.Key.Escape) dlg.DialogResult = false; };
            return dlg.ShowDialog() == true ? result : null;
        }

        private static System.Windows.Media.Brush Br(string hex)
            => (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex);

        private void ContextMenu_Open_Click(object sender, RoutedEventArgs e)
        {
            if (AssetList.SelectedItem is AssetItem item)
                OpenOrPlaceAsset(item);
        }

        /// <summary>
        /// Opens the asset in its dedicated editor; if there is none (built-in/primitive), meshes are
        /// added to the scene and everything else shows explicit feedback — so an open gesture is
        /// never a silent no-op. Shared by double-click and the context-menu "Open / Edit" item.
        /// </summary>
        /// <summary>Ctrl+double-click: open a dedicated Model-Viewer WINDOW that renders ONLY this model, isolated
        /// and large, with an orbit/zoom/keyboard camera. A floating window (not an AvalonDock tab) so it has a
        /// normal close button and never disturbs the Scene viewport's native render surface.</summary>
        private void OpenModelViewer(AssetItem item)
        {
            try
            {
                var projectPath = ProjectData.Current?.Path ?? "";
                string fullPath = item.Path;
                if (!string.IsNullOrEmpty(fullPath) && !System.IO.Path.IsPathRooted(fullPath))
                    fullPath = System.IO.Path.Combine(projectPath, item.Path);

                if (string.IsNullOrEmpty(fullPath) || !System.IO.File.Exists(fullPath))
                {
                    OpenAssetInEditor(item); // fall back to the editor if the file can't be resolved
                    return;
                }

                var win = new Window
                {
                    Title = "Model Viewer - " + item.Name,
                    Width = 1100,
                    Height = 760,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 22, 24)),
                    Content = new Editor.Editors.WorldEditor.Components.ModelViewer.ModelViewerControl(fullPath, item.Name),
                    Owner = Window.GetWindow(this),
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                win.Show(); // non-modal: inspect while keeping the editor usable
            }
            catch
            {
                OpenAssetInEditor(item);
            }
        }

        private void OpenOrPlaceAsset(AssetItem item)
        {
            if (item == null) return;

            if (OpenAssetInEditor(item))
                return;

            // Open/Edit NEVER auto-places into the scene (that's the explicit "Add to Scene" action).
            if (item.Source != null && !item.IsFolder && System.IO.File.Exists(item.Source.FullPath))
            {
                // A real file with no dedicated editor: open with the OS default handler.
                FileExplorerService.Instance.OpenFile(item.Source);
            }
            else
            {
                MessageBox.Show($"'{item.Name}' has no editable file. For built-in meshes use right-click → 'Add to Scene'.",
                    "Nothing to open", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// Routes an asset to its editor by type. Returns true if an editor was opened, false if the
        /// asset has no dedicated editor / no backing file (built-in or primitive).
        /// </summary>
        private bool OpenAssetInEditor(AssetItem item)
        {
            if (item == null) return false;

            var projectPath = ProjectData.Current?.Path ?? "";
            string fullPath = item.Path;
            if (!string.IsNullOrEmpty(item.Path) && !System.IO.Path.IsPathRooted(item.Path))
                fullPath = System.IO.Path.Combine(projectPath, item.Path);

            var extension = !string.IsNullOrEmpty(item.Path)
                ? System.IO.Path.GetExtension(item.Path)?.ToLowerInvariant()
                : "";

            switch (item.Type)
            {
                case AssetType.Textures:
                    if (System.IO.File.Exists(fullPath))
                    {
                        Dialogs.TextureEditorDialog.OpenTexture(Window.GetWindow(this), fullPath, item.AssetGuid);
                        return true;
                    }
                    return false;

                case AssetType.Materials:
                    if (System.IO.File.Exists(fullPath))
                    {
                        try { Dialogs.MaterialEditorDialog.OpenMaterial(Window.GetWindow(this), fullPath); }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error opening Material Editor: {ex.Message}");
                            MessageBox.Show("Could not open the Material Editor:\n" + ex.Message, "Material Editor",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                        return true;
                    }
                    return false;

                case AssetType.Meshes:
                case AssetType.Models:
                    // Built-in primitive ("Primitive:Cube") has no file to edit — on an Open gesture,
                    // tell the user to use "Add to Scene" instead of silently placing it.
                    if (!string.IsNullOrEmpty(item.Path) &&
                        item.Path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show($"'{item.Name}' is a built-in primitive. Use right-click → 'Add to Scene' to place it.",
                            "Built-in primitive", MessageBoxButton.OK, MessageBoxImage.Information);
                        return true; // handled — never falls through to Add-to-Scene
                    }
                    // Any real mesh/model file (.vmesh, .fbx, .obj, .gltf, .glb, .dae, .3ds, …) -> live editor.
                    if (System.IO.File.Exists(fullPath))
                    {
                        try { Dialogs.UniversalModelEditorDialog.OpenForModel(Window.GetWindow(this), fullPath); }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error opening Model Editor: {ex.Message}");
                            MessageBox.Show($"Could not open model: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                        return true;
                    }
                    return false;

                case AssetType.Scripts:
                    if (System.IO.File.Exists(fullPath))
                    {
                        Editor.Core.Services.ScriptingService.OpenInVisualStudio(fullPath);
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Adds the selected asset to the current scene.
        /// For multi-material models, creates parent entity with child submeshes.
        /// </summary>
        private void AddAssetToScene(AssetItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Path))
                return;

            // Skip placeholder items
            if (item.Id < 0)
                return;

            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null)
            {
                MessageBox.Show("No active scene. Please open or create a scene first.", 
                    "No Scene", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                // Check if this is a model file that may have submeshes
                var extension = System.IO.Path.GetExtension(item.Path)?.ToLowerInvariant();
                bool isModelFile = extension == ".fbx" || extension == ".obj" || extension == ".gltf" || 
                                   extension == ".glb" || extension == ".dae" || extension == ".3ds" || 
                                   extension == ".blend";

                if (isModelFile)
                {
                    // Get full path to the model file
                    var projectPath = ProjectData.Current?.Path ?? "";
                    string fullPath = item.Path;
                    if (!System.IO.Path.IsPathRooted(item.Path))
                    {
                        fullPath = System.IO.Path.Combine(projectPath, item.Path);
                    }

                    if (System.IO.File.Exists(fullPath))
                    {
                        // Check how many submeshes the model has
                        int submeshCount = VortexAPI.GetSubmeshCount(fullPath);
                        
                        if (submeshCount > 1)
                        {
                            // Multi-material model - create parent with child entities
                            var result = VortexAPI.ImportModelWithMaterialsFromFile(fullPath);
                            if (result != null && result.Length > 0)
                            {
                                CreateMultiMaterialEntity(scene, item.Name, item.Path, result, projectPath);
                                return;
                            }
                        }
                        else if (submeshCount == 1)
                        {
                                    // Single submesh - still load textures
                                    var result = VortexAPI.ImportModelWithMaterialsFromFile(fullPath);
                                    if (result != null && result.Length > 0)
                                    {
                                        var texturePaths = FindTexturesForModel(fullPath);
                                        var entity = new ECS.GameEntity(scene, item.Name);
                                        var meshRenderer = new ECS.Components.Rendering.MeshRenderer(entity)
                                        {
                                            MeshPath = item.Path,
                                            MaterialHandle = result[0].MaterialId
                                        };

                                        // Bind the per-submesh .vmat (single source of truth) so the engine renders
                                        // FROM it (base color + textures) and it survives a restart.
                                        try
                                        {
                                            string modelDir = System.IO.Path.GetDirectoryName(item.Path) ?? "";
                                            string vmatRel = System.IO.Path.Combine(modelDir, "materials", "submesh_0.vmat").Replace('\\', '/');
                                            if (System.IO.File.Exists(System.IO.Path.Combine(projectPath, vmatRel)))
                                                meshRenderer.MaterialPath = vmatRel;
                                        }
                                        catch { }

                                        if (string.IsNullOrEmpty(meshRenderer.MaterialPath) && texturePaths.Count > 0)
                                        {
                                            string texPath = texturePaths[0];
                                    
                                            // Load texture into engine and bind to material
                                            try
                                            {
                                                long texId = VortexAPI.LoadTextureResource(texPath);
                                                if (texId >= 0 && result[0].MaterialId >= 0)
                                                {
                                                    VortexAPI.SetMaterialAlbedoTexture(result[0].MaterialId, texId);
                                                    System.Diagnostics.Debug.WriteLine($"[AssetBrowser] Bound texture to single mesh material: {texPath}");
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                System.Diagnostics.Debug.WriteLine($"[AssetBrowser] Error binding texture: {ex.Message}");
                                            }
                                    
                                            if (!string.IsNullOrEmpty(projectPath) && texPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                                            {
                                                texPath = texPath.Substring(projectPath.Length)
                                                    .TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                                            }
                                            meshRenderer.TexturePath = texPath;
                                        }
                                
                                        if (result[0].MaterialId >= 0)
                                        {
                                            Core.Services.SceneRenderService.RegisterMaterialForMeshPath(item.Path, result[0].MaterialId);
                                        }
                                
                                        entity.AddComponent(meshRenderer);
                                        entity.Transform.LocalPosition = new ECS.Vector3(0, 0, 0);
                                        scene.AddEntity(entity);
                                        SelectionService.Instance.Select(entity);
                                        return;
                                    }
                                }
                            }
                        }

                // Single mesh or primitive - create simple entity (fallback)
                var fallbackEntity = new ECS.GameEntity(scene, item.Name);
                var fallbackMeshRenderer = new ECS.Components.Rendering.MeshRenderer(fallbackEntity)
                {
                    MeshPath = item.Path
                };
                fallbackEntity.AddComponent(fallbackMeshRenderer);
                fallbackEntity.Transform.LocalPosition = new ECS.Vector3(0, 0, 0);
                scene.AddEntity(fallbackEntity);

                // Select the new entity
                SelectionService.Instance.Select(fallbackEntity);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to add asset to scene:\n{ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Creates a multi-material entity with child entities for each submesh.
        /// </summary>
        private void CreateMultiMaterialEntity(Core.Data.Scene scene, string modelName, string relativePath, 
            VortexAPI.SubmeshImportData[] submeshes, string projectPath)
        {
            // Find texture paths in the model directory
            string fullModelPath = relativePath;
            if (!System.IO.Path.IsPathRooted(relativePath))
            {
                fullModelPath = System.IO.Path.Combine(projectPath, relativePath);
            }
            var texturePaths = FindTexturesForModel(fullModelPath);
            
            // Get submesh names from the model
            string[] submeshNames = VortexAPI.GetSubmeshNames(fullModelPath, submeshes.Length);

            // Create parent container entity
            var parentEntity = new ECS.GameEntity(scene, modelName);
            parentEntity.Transform.LocalPosition = new ECS.Vector3(0, 0, 0);
            scene.AddEntity(parentEntity);

            // Create child entity for each submesh
            for (int i = 0; i < submeshes.Length; i++)
            {
                var submesh = submeshes[i];
                string childName = i < submeshNames.Length && !string.IsNullOrEmpty(submeshNames[i]) 
                    ? submeshNames[i] 
                    : $"Submesh_{i}";
                
                var childEntity = new ECS.GameEntity(scene, childName);
                
                // Mark child as locked to parent (can't be moved individually)
                childEntity.IsLockedToParent = true;
                
                // Use submesh-specific mesh path
                string submeshPath = $"{relativePath}#submesh{i}";
                var meshRenderer = new ECS.Components.Rendering.MeshRenderer(childEntity)
                {
                    MeshPath = submeshPath,
                    MaterialHandle = submesh.MaterialId
                };

                // Bind the per-submesh .vmat (the single source of truth): the engine renders FROM it (base color
                // + textures) and it survives a restart, instead of the volatile texture-guess fallback below.
                try
                {
                    string modelDir = System.IO.Path.GetDirectoryName(relativePath) ?? "";
                    string vmatRel = System.IO.Path.Combine(modelDir, "materials", $"submesh_{i}.vmat").Replace('\\', '/');
                    if (System.IO.File.Exists(System.IO.Path.Combine(projectPath, vmatRel)))
                        meshRenderer.MaterialPath = vmatRel;
                }
                catch { }

                // Fallback only when there is no .vmat (older imports): guess a texture from the model folder.
                if (string.IsNullOrEmpty(meshRenderer.MaterialPath) && texturePaths.Count > 0)
                {
                    string texPath = FindTextureForSubmesh(childName, texturePaths);
                    if (!string.IsNullOrEmpty(texPath))
                    {
                        // Load texture into engine and bind to material
                        try
                        {
                            long texId = VortexAPI.LoadTextureResource(texPath);
                            if (texId >= 0 && submesh.MaterialId >= 0)
                            {
                                VortexAPI.SetMaterialAlbedoTexture(submesh.MaterialId, texId);
                                System.Diagnostics.Debug.WriteLine($"[AssetBrowser] Bound albedo texture to material {submesh.MaterialId}: {texPath}");
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[AssetBrowser] Error binding texture: {ex.Message}");
                        }
                        
                        // Also set the TexturePath for the mesh renderer
                        if (!string.IsNullOrEmpty(projectPath) && texPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            texPath = texPath.Substring(projectPath.Length)
                                .TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                        }
                        meshRenderer.TexturePath = texPath;
                    }
                }

                // Register material in SceneRenderService for consistent rendering
                if (submesh.MaterialId >= 0)
                {
                    Core.Services.SceneRenderService.RegisterMaterialForMeshPath(submeshPath, submesh.MaterialId);
                }

                childEntity.AddComponent(meshRenderer);
                childEntity.Transform.LocalPosition = new ECS.Vector3(0, 0, 0);
                parentEntity.AddChild(childEntity);
            }

            // Select the parent entity
            SelectionService.Instance.Select(parentEntity);
        }

        /// <summary>
        /// Finds texture files associated with a model file.
        /// Returns a list of potential color/albedo texture paths.
        /// </summary>
        private List<string> FindTexturesForModel(string modelPath)
        {
            var result = new List<string>();
            
            if (string.IsNullOrEmpty(modelPath) || !System.IO.File.Exists(modelPath))
                return result;

            var dir = System.IO.Path.GetDirectoryName(modelPath);
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                return result;

            // Look for texture files in the same directory
            var textureExtensions = new[] { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.dds" };
            
            // First pass: find explicit color textures
            var colorTextures = new List<string>();
            var otherTextures = new List<string>();
            
            foreach (var ext in textureExtensions)
            {
                try
                {
                    var files = System.IO.Directory.GetFiles(dir, ext);
                    foreach (var file in files)
                    {
                        var fileName = System.IO.Path.GetFileName(file).ToLowerInvariant();
                        
                        // Exclude non-color textures
                        bool isUnwanted = fileName.Contains("_nor") || fileName.Contains("_normal") ||
                                          fileName.Contains("_nrm") || fileName.Contains("normal.") ||
                                          fileName.Contains("_ao") || fileName.Contains("_occ") ||
                                          fileName.Contains("occlusion") ||
                                          fileName.Contains("_rough") || fileName.Contains("roughness") ||
                                          fileName.Contains("_metal") || fileName.Contains("metallic") ||
                                          fileName.Contains("_spec") || fileName.Contains("specular") ||
                                          fileName.Contains("_height") || fileName.Contains("_disp") ||
                                          fileName.Contains("_emis") || fileName.Contains("emission");
                        
                        if (isUnwanted) continue;
                        
                        // Check for explicit color patterns
                        bool isColorTexture = fileName.Contains("_col") || fileName.Contains("col.") ||
                                              fileName.Contains("_color") || fileName.Contains("color.") ||
                                              fileName.Contains("_diffuse") || fileName.Contains("diffuse.") ||
                                              fileName.Contains("_albedo") || fileName.Contains("albedo.") ||
                                              fileName.Contains("_base") || fileName.Contains("basecolor");
                        
                        if (isColorTexture)
                        {
                            colorTextures.Add(file);
                        }
                        else
                        {
                            otherTextures.Add(file);
                        }
                    }
                }
                catch { /* Ignore errors */ }
            }
            
            // Return color textures first, then other textures as fallback
            result.AddRange(colorTextures);
            result.AddRange(otherTextures);
            
            System.Diagnostics.Debug.WriteLine($"[FindTexturesForModel] Found {colorTextures.Count} color + {otherTextures.Count} other textures in {dir}");

            return result;
        }

        /// <summary>
        /// Finds the best matching texture for a given submesh name.
        /// </summary>
        private string FindTextureForSubmesh(string submeshName, List<string> availableTextures)
        {
            if (string.IsNullOrEmpty(submeshName) || availableTextures == null || availableTextures.Count == 0)
                return availableTextures?.FirstOrDefault() ?? "";

            // Normalize the submesh name (lowercase, replace spaces with underscores)
            string normalizedName = submeshName.ToLowerInvariant().Replace(" ", "_");

            // Find texture that matches the submesh name
            foreach (var texPath in availableTextures)
            {
                string texFileName = System.IO.Path.GetFileName(texPath).ToLowerInvariant();
                if (texFileName.Contains(normalizedName))
                {
                    return texPath;
                }
            }

            // If no exact match, return the first available texture
            return availableTextures.FirstOrDefault() ?? "";
        }

        public void RefreshAssets()
        {
            Assets.Clear();

            switch (_currentType)
            {
                case AssetType.Explorer:
                    LoadCurrentFolder();
                    break;
                case AssetType.Meshes:
                    LoadDefaultMeshes();
                    break;
                case AssetType.Models:
                    LoadImportedModels();
                    break;
                case AssetType.Textures:
                    LoadDefaultTextures();
                    break;
                case AssetType.Materials:
                    LoadDefaultMaterials();
                    break;
                case AssetType.Shaders:
                    LoadDefaultShaders();
                    break;
                case AssetType.Scripts:
                    LoadScripts();
                    break;
            }

            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            // EmptyState visibility is handled in XAML when available
        }

        /// <summary>
        /// Generates a thumbnail OFF the synchronous load path \u2014 on a Background-priority dispatcher
        /// tick \u2014 so populating the browser never blocks the UI thread (mouse stays responsive). Each
        /// build (engine offscreen render / Assimp import / bitmap decode) runs on its own tick and the
        /// tile updates via INotifyPropertyChanged when the image lands. This (with SwapRenderQueue
        /// replacing the per-thumbnail RenderOnce) is what makes opening the Asset Browser smooth.
        /// </summary>
        private void QueueThumbnail(AssetItem item, Func<ImageSource> builder)
        {
            Application.Current?.Dispatcher?.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { try { item.Thumbnail = builder(); } catch { } }));
        }

        /// <summary>Explorer tab: the current folder's subfolders + ALL files (a real file browser).</summary>
        private void LoadCurrentFolder()
        {
            var folder = FileExplorerService.Instance.CurrentFolder;
            if (folder == null) return;
            var contents = FileExplorerService.Instance.CurrentFolderContents;

            // folders first
            foreach (var fsi in contents)
            {
                if (fsi == null || !fsi.IsDirectory) continue;
                Assets.Add(new AssetItem
                {
                    Name = fsi.Name, Path = fsi.FullPath, Source = fsi, IsFolder = true, IsImported = true,
                    TypeName = "Folder", IconCode = "", IconColor = "#FFE6B422"
                });
            }
            // then files
            foreach (var fsi in contents)
            {
                if (fsi == null || fsi.IsDirectory) continue;
                Assets.Add(BuildFileItem(fsi));
            }
        }

        private AssetItem BuildFileItem(FileSystemItem fsi)
        {
            var ext = (System.IO.Path.GetExtension(fsi.FullPath) ?? "").ToLowerInvariant();
            var item = new AssetItem
            {
                Name = System.IO.Path.GetFileName(fsi.FullPath), Path = fsi.FullPath, Source = fsi, IsImported = true
            };
            switch (ext)
            {
                case ".cs":
                    item.Type = AssetType.Scripts; item.TypeName = "Script"; item.IconCode = ""; item.IconColor = "#FF9B59B6"; break;
                case ".vmat": case ".mat":
                    item.Type = AssetType.Materials; item.TypeName = "Material"; item.IconCode = ""; item.IconColor = "#FFBD63C5";
                    QueueThumbnail(item, () => GetOrBuildMaterialThumb(fsi.FullPath)); break;
                case ".vshader": case ".hlsl": case ".glsl":
                    item.Type = AssetType.Shaders; item.TypeName = "Shader"; item.IconCode = ""; item.IconColor = "#FF569CD6"; break;
                case ".vmesh": case ".fbx": case ".obj": case ".gltf": case ".glb": case ".dae": case ".3ds":
                    item.Type = AssetType.Models; item.TypeName = "Model"; item.IconCode = ""; item.IconColor = "#FFCE9178";
                    QueueThumbnail(item, () => GetOrBuildModelThumb(fsi.FullPath)); break;
                case ".png": case ".jpg": case ".jpeg": case ".bmp": case ".gif":
                    item.Type = AssetType.Textures; item.TypeName = "Texture"; item.IconCode = ""; item.IconColor = "#FF4EC9B0";
                    QueueThumbnail(item, () => BuildImageThumb(fsi.FullPath)); break;
                case ".tga": case ".dds": case ".hdr":
                    item.Type = AssetType.Textures; item.TypeName = "Texture"; item.IconCode = ""; item.IconColor = "#FF4EC9B0"; break;
                case ".vscene":
                    item.Type = AssetType.Explorer; item.TypeName = "Scene"; item.IconCode = ""; item.IconColor = "#FF6C5CE7"; break;
                default:
                    item.Type = AssetType.Explorer;
                    item.TypeName = string.IsNullOrEmpty(ext) ? "File" : (ext.TrimStart('.').ToUpperInvariant() + " File");
                    item.IconCode = ""; item.IconColor = "#FF9A9AA1"; break;
            }
            return item;
        }

        private static ImageSource BuildImageThumb(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 96;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        /// <summary>Scripts tab: every .cs in the project (project-wide), regardless of folder.</summary>
        private void LoadScripts()
        {
            try
            {
                var root = ProjectData.Current?.Path;
                foreach (var rel in Editor.Core.Services.ScriptingService.EnumerateScripts())
                {
                    var abs = !string.IsNullOrEmpty(root)
                        ? System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)) : rel;
                    Assets.Add(new AssetItem
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(rel), Path = abs, IsImported = true,
                        Type = AssetType.Scripts, TypeName = "Script", IconCode = "", IconColor = "#FF9B59B6"
                    });
                }
            }
            catch { }
        }

        private void LoadDefaultMeshes()
        {
            string[] primitives = { "Cube", "Sphere", "Plane", "Cylinder", "Cone", "Capsule", "Torus" };

            for (int i = 0; i < primitives.Length; i++)
            {
                string prim = primitives[i];
                var item = new AssetItem
                {
                    Id = i,
                    Name = prim,
                    TypeName = "Primitive",
                    IconCode = "\uF158",
                    IconColor = "#4EC9B0",
                    Type = AssetType.Meshes,
                    Path = $"Primitive:{prim}"
                };
                Assets.Add(item);
                QueueThumbnail(item, () => GetOrBuildPrimitiveThumb(prim));
            }

            // ALSO list every model file actually on disk so imported models show up under Meshes (the user's
            // "no meshes found although there are many models" — they were only registered, if at all, under Models).
            AddModelFilesFromDisk();
        }

        /// <summary>Scans the project's Assets folder (recursively) for model files and adds them as Models tiles.
        /// Filesystem-based so it works even if a model was never registered in the AssetDatabase.</summary>
        private void AddModelFilesFromDisk()
        {
            var projectPath = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(projectPath)) return;
            var assetsDir = System.IO.Path.Combine(projectPath, "Assets");
            if (!System.IO.Directory.Exists(assetsDir)) return;

            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".glb", ".gltf", ".fbx", ".obj", ".dae", ".3ds", ".blend", ".vmesh" };
            string[] files;
            try { files = System.IO.Directory.GetFiles(assetsDir, "*.*", System.IO.SearchOption.AllDirectories); }
            catch { return; }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in Assets) if (!string.IsNullOrEmpty(a.Path)) seen.Add(a.Path.Replace('\\', '/'));

            foreach (var f in files)
            {
                var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                if (!exts.Contains(ext)) continue;
                var rel = f.Substring(projectPath.Length).TrimStart('\\', '/').Replace('\\', '/');
                if (!seen.Add(rel)) continue;
                var typeName = (ext == ".glb" || ext == ".gltf") ? "GLTF Model" : ext.TrimStart('.').ToUpperInvariant() + " Model";
                var item = new AssetItem
                {
                    Id = Assets.Count,
                    Name = System.IO.Path.GetFileNameWithoutExtension(f),
                    TypeName = typeName,
                    IconCode = "",
                    IconColor = "#CE9178",
                    Type = AssetType.Models,
                    Path = rel,
                    IsImported = true
                };
                Assets.Add(item);
                QueueThumbnail(item, () => GetOrBuildModelThumb(f));
            }
        }

        private void LoadImportedModels()
        {
            // Load models from AssetDatabase
            var assetDb = AssetDatabase.Instance;
            if (assetDb == null || string.IsNullOrEmpty(assetDb.ProjectPath))
                return;

            var meshAssets = assetDb.GetAssetsByType(Core.Assets.AssetType.Mesh);
            
            foreach (var asset in meshAssets)
            {
                // Skip primitive-style paths
                if (asset.RelativePath?.StartsWith("Primitive:") == true)
                    continue;

                var ext = System.IO.Path.GetExtension(asset.FileName)?.ToLowerInvariant();
                var typeName = ext switch
                {
                    ".vmesh" => "Binary Mesh",
                    ".fbx" => "FBX Model",
                    ".obj" => "OBJ Model",
                    ".gltf" or ".glb" => "GLTF Model",
                    ".dae" => "Collada Model",
                    ".blend" => "Blender File",
                    _ => "3D Model"
                };

                Assets.Add(new AssetItem
                {
                    Id = Assets.Count,
                    Name = System.IO.Path.GetFileNameWithoutExtension(asset.FileName),
                    TypeName = typeName,
                    IconCode = "\uF158",
                    IconColor = "#CE9178",
                    Type = AssetType.Models,
                    Path = asset.RelativePath,
                    AssetGuid = asset.Guid,
                    IsImported = true
                });
                // Defer the heavy Assimp import + offscreen render off the UI thread.
                QueueThumbnail(Assets[Assets.Count - 1],
                    () => GetOrBuildModelThumb(System.IO.Path.Combine(assetDb.ProjectPath, asset.RelativePath ?? asset.FileName)));
            }

            // The AssetDatabase may not have every model registered — also list whatever is actually on disk.
            AddModelFilesFromDisk();

            // Show hint if no models
            if (Assets.Count == 0)
            {
                // Add placeholder
                Assets.Add(new AssetItem
                {
                    Id = -1,
                    Name = "Drag & drop models here or use Import",
                    TypeName = "No models imported yet",
                    IconCode = "\uE946",
                    IconColor = "#666666",
                    Type = AssetType.Models,
                    Path = "",
                    IsImported = false
                });
            }
        }

        private void LoadDefaultTextures()
        {
            // Add default/built-in textures first
            Assets.Add(new AssetItem { Id = 0, Name = "White", TypeName = "Solid Color", IconCode = "\uEB9F", IconColor = "#FFFFFF", Type = AssetType.Textures, Path = "Texture:White", Thumbnail = MakeSolidSwatch("#FFFFFF") });
            Assets.Add(new AssetItem { Id = 1, Name = "Black", TypeName = "Solid Color", IconCode = "\uEB9F", IconColor = "#333333", Type = AssetType.Textures, Path = "Texture:Black", Thumbnail = MakeSolidSwatch("#333333") });
            Assets.Add(new AssetItem { Id = 2, Name = "Normal", TypeName = "Normal Map", IconCode = "\uEB9F", IconColor = "#8080FF", Type = AssetType.Textures, Path = "Texture:Normal", Thumbnail = MakeSolidSwatch("#8080FF") });
            Assets.Add(new AssetItem { Id = 3, Name = "Checker", TypeName = "Pattern", IconCode = "\uEB9F", IconColor = "#808080", Type = AssetType.Textures, Path = "Texture:Checker", Thumbnail = MakeChecker() });

            // Load imported textures from AssetDatabase
            var assetDb = AssetDatabase.Instance;
            if (assetDb == null || string.IsNullOrEmpty(assetDb.ProjectPath))
                return;

            var textureAssets = assetDb.GetAssetsByType(Core.Assets.AssetType.Texture);
            
            foreach (var asset in textureAssets)
            {
                // Skip built-in textures
                if (asset.RelativePath?.StartsWith("Texture:") == true)
                    continue;

                var ext = System.IO.Path.GetExtension(asset.FileName)?.ToLowerInvariant();
                var typeName = ext switch
                {
                    ".png" => "PNG Image",
                    ".jpg" or ".jpeg" => "JPEG Image",
                    ".tga" => "TGA Image",
                    ".bmp" => "Bitmap",
                    ".dds" => "DDS Texture",
                    ".hdr" => "HDR Image",
                    _ => "Texture"
                };

                Assets.Add(new AssetItem
                {
                    Id = Assets.Count,
                    Name = System.IO.Path.GetFileNameWithoutExtension(asset.FileName),
                    TypeName = typeName,
                    IconCode = "\uEB9F",
                    IconColor = "#E6B422",
                    Type = AssetType.Textures,
                    Path = asset.RelativePath,
                    AssetGuid = asset.Guid,
                    IsImported = true
                });
                // Defer bitmap decode off the UI thread.
                QueueThumbnail(Assets[Assets.Count - 1],
                    () => MakeTextureThumb(System.IO.Path.Combine(assetDb.ProjectPath, asset.RelativePath ?? asset.FileName)));
            }
        }

        private void LoadDefaultMaterials()
        {
            // Add default/built-in materials first
            Assets.Add(new AssetItem { Id = 0, Name = "Default", TypeName = "Standard", IconCode = "\uE91B", IconColor = "#BD63C5", Type = AssetType.Materials, Path = "Material:Default", Thumbnail = MakeSphereSwatch("#BD63C5") });
            Assets.Add(new AssetItem { Id = 1, Name = "Unlit White", TypeName = "Unlit", IconCode = "\uE91B", IconColor = "#FFFFFF", Type = AssetType.Materials, Path = "Material:UnlitWhite", Thumbnail = MakeSphereSwatch("#FFFFFF") });
            Assets.Add(new AssetItem { Id = 2, Name = "Grid", TypeName = "Standard", IconCode = "\uE91B", IconColor = "#4EC9B0", Type = AssetType.Materials, Path = "Material:Grid", Thumbnail = MakeSphereSwatch("#4EC9B0") });

            // Load imported materials from AssetDatabase
            var assetDb = AssetDatabase.Instance;
            if (assetDb == null || string.IsNullOrEmpty(assetDb.ProjectPath))
                return;

            var materialAssets = assetDb.GetAssetsByType(Core.Assets.AssetType.Material);
            
            foreach (var asset in materialAssets)
            {
                // Skip built-in materials
                if (asset.RelativePath?.StartsWith("Material:") == true)
                    continue;

                Assets.Add(new AssetItem
                {
                    Id = Assets.Count,
                    Name = System.IO.Path.GetFileNameWithoutExtension(asset.FileName),
                    TypeName = "Imported Material",
                    IconCode = "\uE91B",
                    IconColor = "#CE9178",
                    Type = AssetType.Materials,
                    Path = asset.RelativePath,
                    AssetGuid = asset.Guid,
                    IsImported = true,
                    Thumbnail = MakeSphereSwatch("#CE9178")  // instant placeholder; replaced by the live sphere below
                });
                // Render the REAL material on a sphere (live from the .vmat), not a flat swatch.
                var matAbs = System.IO.Path.IsPathRooted(asset.RelativePath)
                    ? asset.RelativePath
                    : System.IO.Path.Combine(assetDb.ProjectPath, asset.RelativePath ?? asset.FileName);
                QueueThumbnail(Assets[Assets.Count - 1], () => GetOrBuildMaterialThumb(matAbs));
            }
        }

        private void LoadDefaultShaders()
        {
            Assets.Add(new AssetItem { Id = 0, Name = "Standard", TypeName = "PBR Shader", IconCode = "\uE9F5", IconColor = "#569CD6", Type = AssetType.Shaders, Path = "Shader:Standard" });
            Assets.Add(new AssetItem { Id = 1, Name = "Unlit", TypeName = "Basic Shader", IconCode = "\uE9F5", IconColor = "#569CD6", Type = AssetType.Shaders, Path = "Shader:Unlit" });
            Assets.Add(new AssetItem { Id = 2, Name = "Wireframe", TypeName = "Debug Shader", IconCode = "\uE9F5", IconColor = "#4EC9B0", Type = AssetType.Shaders, Path = "Shader:Wireframe" });
            Assets.Add(new AssetItem { Id = 3, Name = "Grid", TypeName = "Editor Shader", IconCode = "\uE9F5", IconColor = "#DCDCAA", Type = AssetType.Shaders, Path = "Shader:Grid" });
        }

        #region Thumbnail generation

        private static Color ParseColor(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return Color.FromRgb(0x80, 0x80, 0x80); }
        }

        private static Color Mix(Color a, Color b, double t)
        {
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        private static ImageSource Render(int size, Action<DrawingContext> draw)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) { draw(dc); }
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        /// <summary>Flat color tile (built-in solid-color textures).</summary>
        private static ImageSource MakeSolidSwatch(string hex) => Render(64, dc =>
            dc.DrawRectangle(new SolidColorBrush(ParseColor(hex)), null, new Rect(0, 0, 64, 64)));

        /// <summary>Classic checkerboard preview.</summary>
        private static ImageSource MakeChecker() => Render(64, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), null, new Rect(0, 0, 64, 64));
            var dark = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
            const int n = 8; double s = 64.0 / n;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    if (((x + y) & 1) == 0) dc.DrawRectangle(dark, null, new Rect(x * s, y * s, s, s));
        });

        /// <summary>Shaded sphere on a dark tile \u2014 the material preview look.</summary>
        private static ImageSource MakeSphereSwatch(string hex) => Render(64, dc =>
        {
            var c = ParseColor(hex);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x19)), null, new Rect(0, 0, 64, 64));
            var rg = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.36, 0.30),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.62, RadiusY = 0.62
            };
            rg.GradientStops.Add(new GradientStop(Mix(c, Colors.White, 0.55), 0.0));
            rg.GradientStops.Add(new GradientStop(c, 0.55));
            rg.GradientStops.Add(new GradientStop(Mix(c, Colors.Black, 0.6), 1.0));
            rg.Freeze();
            dc.DrawEllipse(rg, null, new Point(32, 33), 23, 23);
        });

        /// <summary>Decode a real image file to a small thumbnail (null if unsupported, e.g. .dds/.tga).</summary>
        private static ImageSource MakeTextureThumb(string fullPath)
        {
            try
            {
                if (string.IsNullOrEmpty(fullPath) || !System.IO.File.Exists(fullPath)) return null;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.DecodePixelWidth = 96;
                bi.UriSource = new Uri(fullPath);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        // --- Real 3D mesh thumbnails: render the mesh offscreen via the engine ---

        private static readonly Dictionary<string, ImageSource> _thumbCache = new Dictionary<string, ImageSource>();
        private static long _defaultThumbMat = -1;

        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, int count);

        private static long EnsureDefaultThumbMaterial()
        {
            if (_defaultThumbMat >= 0) return _defaultThumbMat;
            try
            {
                long m = VortexAPI.CreateNewMaterial();
                VortexAPI.SetMaterialBaseColor(m, 0.62f, 0.64f, 0.68f, 1f);
                VortexAPI.SetMaterialMetallicValue(m, 0.10f);
                VortexAPI.SetMaterialRoughnessValue(m, 0.55f);
                _defaultThumbMat = m;
            }
            catch { _defaultThumbMat = -1; }
            return _defaultThumbMat;
        }

        /// <summary>
        /// Render one or more submeshes to an offscreen target and return a frozen bitmap.
        /// Returns null if the engine/viewport isn't ready yet (caller falls back to a glyph).
        /// </summary>
        private static ImageSource BuildMeshThumbnail(long[] meshIds, long[] materialIds, int size)
        {
            if (meshIds == null || meshIds.Length == 0) return null;
            uint rt = VortexAPI.CreateSecondaryRenderTarget((uint)size, (uint)size);
            if (rt == 0) return null; // renderer not initialized -> glyph fallback
            try
            {
                // Combined bounds for camera framing: bounding-sphere radius (handles any size).
                float radius = 0.4f, cx = 0, cy = 0, cz = 0; bool gotCenter = false;
                foreach (var m in meshIds)
                {
                    if (VortexAPI.GetMeshBounds(m, out float sx, out float sy, out float sz))
                    {
                        float rr = 0.5f * (float)System.Math.Sqrt(sx * sx + sy * sy + sz * sz);
                        if (rr > radius) radius = rr;
                    }
                    if (!gotCenter && VortexAPI.GetMeshBoundsCenter(m, out float bx, out float by, out float bz))
                    { cx = bx; cy = by; cz = bz; gotCenter = true; }
                }

                // Neutral studio lighting (global state is rebuilt by the scene each frame).
                // Key light from the upper-front-right (camera side) so the form reads clearly.
                VortexAPI.ClearAllLights();
                VortexAPI.SetAmbientLightStrength(0.32f);
                VortexAPI.SetDirectionalLightParams(-0.45f, -0.6f, -0.65f, 1f, 0.98f, 0.92f, 3.0f);

                // 3/4 perspective camera; distance derived from the bounding radius + a fill ratio
                // so the mesh occupies ~60% of the frame regardless of its modeled size.
                const float fov = 35f;
                float fovHalf = fov * 0.5f * (float)System.Math.PI / 180f;
                float dist = radius / (0.58f * (float)System.Math.Tan(fovHalf));
                double dl = System.Math.Sqrt(0.9 * 0.9 + 0.7 * 0.7 + 1.1 * 1.1);
                float px = cx + (float)(0.9 / dl) * dist;
                float py = cy + (float)(0.7 / dl) * dist;
                float pz = cz + (float)(1.1 / dl) * dist;
                var cam = VortexAPI.ViewportCameraDesc.CreatePerspective(
                    px, py, pz, cx, cy, cz, 0, 1, 0, fov,
                    System.Math.Max(0.02f, dist * 0.01f), dist * 4f + 50f);

                float[] idm = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
                for (int i = 0; i < meshIds.Length; i++)
                {
                    long mat = (materialIds != null && i < materialIds.Length && materialIds[i] >= 0)
                        ? materialIds[i] : EnsureDefaultThumbMaterial();
                    VortexAPI.SubmitMeshForRendering(meshIds[i], mat, idm);
                }

                // Swap the just-submitted item into the active render queue WITHOUT presenting to the
                // main swapchain (RenderOnce/render_frame presents — doing that per-thumbnail flashed
                // the editor viewport white). Then render only into the offscreen target.
                VortexAPI.SwapRenderQueue();
                VortexAPI.RenderToSecondaryTarget(rt, cam, false);
                if (!VortexAPI.PrepareSecondaryRenderTargetReadback(rt)) return null;
                return ReadTargetToBitmap(rt);
            }
            catch { return null; }
            finally
            {
                VortexAPI.DestroySecondaryRenderTarget(rt);
                // This thumbnail submitted a mesh + SwapRenderQueue'd the SHARED render queue. Tell the main
                // viewport to re-submit its scene so the previewed model never lingers in the freecam — THE
                // "open a folder -> a model is rendered huge in the viewport" bug.
                try { Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit(); } catch { }
            }
        }

        private static ImageSource ReadTargetToBitmap(uint rt)
        {
            IntPtr src = VortexAPI.ReadSecondaryRenderTargetPixels(rt, out uint w, out uint h, out uint pitch);
            if (src == IntPtr.Zero || w == 0 || h == 0) { VortexAPI.ReleaseSecondaryRenderTargetPixels(rt); return null; }
            try
            {
                var wb = new WriteableBitmap((int)w, (int)h, 96, 96, PixelFormats.Bgra32, null);
                wb.Lock();
                int copyW = (int)w * 4;
                for (int y = 0; y < (int)h; y++)
                {
                    IntPtr s = IntPtr.Add(src, y * (int)pitch);
                    IntPtr d = IntPtr.Add(wb.BackBuffer, y * wb.BackBufferStride);
                    CopyMemory(d, s, copyW);
                }
                wb.AddDirtyRect(new Int32Rect(0, 0, (int)w, (int)h));
                wb.Unlock();
                wb.Freeze();
                return wb;
            }
            finally { VortexAPI.ReleaseSecondaryRenderTargetPixels(rt); }
        }

        private static ImageSource GetOrBuildPrimitiveThumb(string name)
        {
            string key = "prim:" + name;
            if (_thumbCache.TryGetValue(key, out var cached)) return cached;
            long mesh;
            switch (name)
            {
                case "Cube": mesh = VortexAPI.CreateCubeMesh(1f); break;
                case "Sphere": mesh = VortexAPI.CreateSphereMesh(0.62f); break;
                case "Plane": mesh = VortexAPI.CreatePlaneMesh(1.5f, 1.5f); break;
                case "Cylinder": mesh = VortexAPI.CreateCylinderMesh(0.5f, 1.1f); break;
                case "Cone": mesh = VortexAPI.CreateConeMesh(0.5f, 1.1f); break;
                default: return null; // Capsule/Torus: no primitive available -> glyph
            }
            if (mesh < 0) return null;
            var img = BuildMeshThumbnail(new[] { mesh }, new[] { EnsureDefaultThumbMaterial() }, 256);
            try { VortexAPI.DeleteMesh(mesh); } catch { }
            if (img != null) _thumbCache[key] = img;
            return img;
        }

        private static ImageSource GetOrBuildModelThumb(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !System.IO.File.Exists(fullPath)) return null;
            string key = "model:" + fullPath;
            if (_thumbCache.TryGetValue(key, out var cached)) return cached;
            try
            {
                var subs = VortexAPI.ImportModelWithMaterialsFromFile(fullPath);
                if (subs == null || subs.Length == 0) return null;
                var meshes = new long[subs.Length];
                var mats = new long[subs.Length];
                for (int i = 0; i < subs.Length; i++) { meshes[i] = subs[i].MeshId; mats[i] = subs[i].MaterialId; }
                var img = BuildMeshThumbnail(meshes, mats, 256);
                if (img != null) _thumbCache[key] = img;
                return img;
            }
            catch { return null; }
        }

        /// <summary>Renders the REAL material from a .vmat onto a sphere (live — re-rendered when the .vmat
        /// changes), so material tiles show their actual look instead of a flat purple swatch.</summary>
        private static ImageSource GetOrBuildMaterialThumb(string vmatPath)
        {
            if (string.IsNullOrEmpty(vmatPath) || !System.IO.File.Exists(vmatPath)) return null;
            // Key includes the last-write time so editing the material re-renders the sphere (not a stale icon).
            string key;
            try { key = "mat:" + vmatPath + ":" + System.IO.File.GetLastWriteTimeUtc(vmatPath).Ticks; }
            catch { key = "mat:" + vmatPath; }
            if (_thumbCache.TryGetValue(key, out var cached)) return cached;
            try
            {
                long mat = Editor.Core.Services.MaterialService.Instance.GetOrBuildVortexMaterial(vmatPath);
                if (mat >= 0)
                {
                    var img = Core.Services.Rendering.AssetPreviewRenderer.RenderMaterialSphere(mat, 256);
                    if (img != null) _thumbCache[key] = img;
                    return img;
                }
            }
            catch { }
            return null;
        }

        #endregion

        /// <summary>
        /// Get the currently selected asset.
        /// </summary>
        public AssetItem SelectedAsset => AssetList.SelectedItem as AssetItem;

        /// <summary>
        /// Create a primitive mesh and return its ID.
        /// </summary>
        public static long CreatePrimitiveMesh(string primitiveType, float size = 1.0f)
        {
            switch (primitiveType.ToLower())
            {
                case "cube":
                    return VortexAPI.CreateCubeMesh(size);
                case "sphere":
                    return VortexAPI.CreateSphereMesh(size * 0.5f);
                case "plane":
                    return VortexAPI.CreatePlaneMesh(size, size);
                case "cylinder":
                    return VortexAPI.CreateCylinderMesh(size * 0.5f, size);
                default:
                    return -1;
            }
        }

        #region Context Menu Handlers

        private void ContextMenu_AddToScene_Click(object sender, RoutedEventArgs e)
        {
            if (AssetList.SelectedItem is AssetItem item)
            {
                if (item.Type == AssetType.Meshes || item.Type == AssetType.Models)
                {
                    AddAssetToScene(item);
                }
                else
                {
                    MessageBox.Show("Only meshes and models can be added to the scene.", 
                        "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void ContextMenu_OpenInBrowser_Click(object sender, RoutedEventArgs e)
        {
            if (AssetList.SelectedItem is AssetItem item && !string.IsNullOrEmpty(item.Path))
            {
                try
                {
                    var projectPath = ProjectData.Current?.Path;
                    if (!string.IsNullOrEmpty(projectPath))
                    {
                        var fullPath = System.IO.Path.Combine(projectPath, item.Path);
                        var directory = System.IO.Path.GetDirectoryName(fullPath);
                        if (System.IO.Directory.Exists(directory))
                        {
                            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open location:\n{ex.Message}", 
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ContextMenu_Delete_Click(object sender, RoutedEventArgs e)
        {
            if (AssetList.SelectedItem is AssetItem item)
            {
                // Don't allow deleting built-in assets
                if (item.Path?.StartsWith("Primitive:") == true || 
                    item.Path?.StartsWith("Texture:") == true ||
                    item.Path?.StartsWith("Material:") == true ||
                    item.Path?.StartsWith("Shader:") == true)
                {
                    MessageBox.Show("Built-in assets cannot be deleted.", 
                        "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var result = MessageBox.Show(
                    $"Are you sure you want to delete '{item.Name}'?\n\nThis action cannot be undone.",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        var projectPath = ProjectData.Current?.Path;
                        if (!string.IsNullOrEmpty(projectPath) && !string.IsNullOrEmpty(item.Path))
                        {
                            var fullPath = System.IO.Path.Combine(projectPath, item.Path);
                            if (System.IO.File.Exists(fullPath))
                            {
                                System.IO.File.Delete(fullPath);
                                
                                // Also delete meta file if exists
                                var metaPath = fullPath + AssetDatabase.MetaFileExtension;
                                if (System.IO.File.Exists(metaPath))
                                {
                                    System.IO.File.Delete(metaPath);
                                }

                                // Refresh
                                AssetDatabase.Instance.Refresh();
                                RefreshAssets();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to delete asset:\n{ex.Message}", 
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        #endregion
    }
}
