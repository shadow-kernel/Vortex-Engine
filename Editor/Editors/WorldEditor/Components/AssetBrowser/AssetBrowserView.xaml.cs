using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Editor.Core.Assets;
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
            Meshes,
            Models,
            Textures,
            Materials,
            Shaders
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

        private AssetType _currentType = AssetType.Meshes;

        public AssetBrowserView()
        {
            InitializeComponent();
            AssetList.ItemsSource = Assets;
            // Note: MouseDoubleClick is already bound in XAML, don't add it again here
            AssetList.PreviewMouseLeftButtonDown += AssetList_PreviewMouseLeftButtonDown;
            AssetList.PreviewMouseMove += AssetList_PreviewMouseMove;
            Loaded += OnLoaded;
            
            // Subscribe to model import events
            ModelImportService.Instance.ModelImported += OnModelImported;
            
            // Subscribe to asset database changes
            AssetDatabase.Instance.AssetsChanged += OnAssetsChanged;
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
                        
                        // For models, optionally show the model editor
                        if (importType == Dialogs.AssetImportDialog.ImportAssetType.Model)
                        {
                            var openEditor = MessageBox.Show(
                                "Model imported successfully. Open in Model Editor?",
                                "Import Complete",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question);
                                
                            if (openEditor == MessageBoxResult.Yes)
                            {
                                Dialogs.UniversalModelEditorDialog.OpenForModel(Window.GetWindow(this), importResult.TargetPath);
                            }
                        }
                    }
                }
                else
                {
                    // Batch import without individual dialogs
                    ImportMultipleFiles(dialog.FileNames);
                }
            }
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

            if (radioButton.Name == "MeshesTab" || (MeshesTab?.IsChecked == true))
                _currentType = AssetType.Meshes;
            else if (radioButton.Name == "ModelsTab" || (ModelsTab?.IsChecked == true))
                _currentType = AssetType.Models;
            else if (radioButton.Name == "TexturesTab" || (TexturesTab?.IsChecked == true))
                _currentType = AssetType.Textures;
            else if (radioButton.Name == "MaterialsTab" || (MaterialsTab?.IsChecked == true))
                _currentType = AssetType.Materials;
            else if (radioButton.Name == "ShadersTab")
                _currentType = AssetType.Shaders;

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
                AssetDoubleClicked?.Invoke(this, item);
                OpenOrPlaceAsset(item);
            }
        }

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
        private void OpenOrPlaceAsset(AssetItem item)
        {
            if (item == null) return;

            if (OpenAssetInEditor(item))
                return;

            if (item.Type == AssetType.Meshes || item.Type == AssetType.Models)
            {
                AddAssetToScene(item);
            }
            else
            {
                MessageBox.Show($"'{item.Name}' is a built-in {item.Type} asset and has no editable file.",
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
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error opening Material Editor: {ex.Message}"); }
                        return true;
                    }
                    return false;

                case AssetType.Meshes:
                case AssetType.Models:
                    bool isModelFile = extension == ".fbx" || extension == ".obj" || extension == ".gltf" ||
                                       extension == ".glb" || extension == ".dae" || extension == ".3ds";
                    if (isModelFile && System.IO.File.Exists(fullPath))
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
                                
                                        if (texturePaths.Count > 0)
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

                // Find the best matching texture for this submesh
                if (texturePaths.Count > 0)
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
                    Thumbnail = MakeSphereSwatch("#CE9178")
                });
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
            finally { VortexAPI.DestroySecondaryRenderTarget(rt); }
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
            var img = BuildMeshThumbnail(new[] { mesh }, new[] { EnsureDefaultThumbMaterial() }, 128);
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
                var img = BuildMeshThumbnail(meshes, mats, 128);
                if (img != null) _thumbCache[key] = img;
                return img;
            }
            catch { return null; }
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
