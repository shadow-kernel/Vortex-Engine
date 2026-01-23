using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Editor.DllWrapper;

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

        public class AssetItem
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public string TypeName { get; set; }
            public string IconCode { get; set; }
            public string IconColor { get; set; }
            public AssetType Type { get; set; }
            public string Path { get; set; }
        }

        public ObservableCollection<AssetItem> Assets { get; } = new ObservableCollection<AssetItem>();

        public event EventHandler<AssetItem> AssetSelected;
        public event EventHandler<AssetItem> AssetDoubleClicked;

        private AssetType _currentType = AssetType.Meshes;

        public AssetBrowserView()
        {
            InitializeComponent();
            AssetList.ItemsSource = Assets;
            AssetList.MouseDoubleClick += AssetList_MouseDoubleClick;
            Loaded += OnLoaded;
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
            if (_currentType == AssetType.Models && !VortexAPI.IsAssimpAvailable())
            {
                var result = MessageBox.Show(
                    "Model import requires Assimp library.\n\n" +
                    "To enable model import:\n" +
                    "1. Install Assimp NuGet package in Engine project\n" +
                    "2. Add VORTEX_USE_ASSIMP to preprocessor definitions\n" +
                    "3. Rebuild the Engine\n\n" +
                    "See BUILD_SETUP.md for detailed instructions.\n\n" +
                    "You can still use .vmesh files and textures.\n\n" +
                    "Continue to file picker anyway?",
                    "Assimp Not Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                    
                if (result != MessageBoxResult.Yes)
                    return;
            }
            
            var dialog = new Microsoft.Win32.OpenFileDialog();
            
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
                    dialog.Filter = "Images|*.png;*.jpg;*.jpeg;*.tga;*.bmp|All Files|*.*";
                    dialog.Title = "Import Texture";
                    break;
                case AssetType.Materials:
                    dialog.Filter = "Materials|*.mat|All Files|*.*";
                    dialog.Title = "Import Material";
                    break;
                case AssetType.Shaders:
                    dialog.Filter = "Shaders|*.hlsl;*.glsl;*.shader|All Files|*.*";
                    dialog.Title = "Import Shader";
                    break;
            }

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    long assetId = -1;
                    string assetName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
                    
                    switch (_currentType)
                    {
                        case AssetType.Meshes:
                        case AssetType.Models:
                            if (dialog.FileName.EndsWith(".vmesh", StringComparison.OrdinalIgnoreCase))
                            {
                                assetId = VortexAPI.LoadVMeshFromFile(dialog.FileName);
                            }
                            else
                            {
                                assetId = VortexAPI.ImportModelFromFile(dialog.FileName);
                            }
                            
                            if (assetId >= 0)
                            {
                                Assets.Add(new AssetItem
                                {
                                    Id = assetId,
                                    Name = assetName,
                                    TypeName = _currentType == AssetType.Meshes ? "Imported Mesh" : "Imported Model",
                                    IconCode = "\uF158",
                                    IconColor = "#4EC9B0",
                                    Type = _currentType,
                                    Path = dialog.FileName
                                });
                                MessageBox.Show($"Successfully imported {(_currentType == AssetType.Meshes ? "mesh" : "model")}: {assetName}", "Import Complete", 
                                    MessageBoxButton.OK, MessageBoxImage.Information);
                            }
                            else
                            {
                                MessageBox.Show($"Failed to import {(_currentType == AssetType.Meshes ? "mesh" : "model")}: {dialog.FileName}", "Import Error", 
                                    MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                            break;
                            
                        case AssetType.Textures:
                            assetId = VortexAPI.ImportTextureFromFile(dialog.FileName);
                            if (assetId >= 0)
                            {
                                Assets.Add(new AssetItem
                                {
                                    Id = assetId,
                                    Name = assetName,
                                    TypeName = "Imported Texture",
                                    IconCode = "\uEB9F",
                                    IconColor = "#FFFFFF",
                                    Type = AssetType.Textures,
                                    Path = dialog.FileName
                                });
                                MessageBox.Show($"Successfully imported texture: {assetName}", "Import Complete", 
                                    MessageBoxButton.OK, MessageBoxImage.Information);
                            }
                            else
                            {
                                MessageBox.Show($"Failed to import texture: {dialog.FileName}", "Import Error", 
                                    MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                            break;
                            
                        default:
                            MessageBox.Show($"Import not yet implemented for {_currentType}", "Info", 
                                MessageBoxButton.OK, MessageBoxImage.Information);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error importing asset: {ex.Message}", "Import Error", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                
                RefreshAssets();
            }
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            var contextMenu = new ContextMenu();
            
            switch (_currentType)
            {
                case AssetType.Materials:
                    AddMenuItem(contextMenu, "Standard Material", () => CreateNewMaterial("Standard"));
                    AddMenuItem(contextMenu, "Unlit Material", () => CreateNewMaterial("Unlit"));
                    AddMenuItem(contextMenu, "Transparent Material", () => CreateNewMaterial("Transparent"));
                    break;
                case AssetType.Shaders:
                    AddMenuItem(contextMenu, "Vertex/Fragment Shader", () => CreateNewShader("VertFrag"));
                    AddMenuItem(contextMenu, "Compute Shader", () => CreateNewShader("Compute"));
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
            var materialId = VortexAPI.CreateNewMaterial();
            if (materialId >= 0)
            {
                Assets.Add(new AssetItem
                {
                    Id = materialId,
                    Name = $"New {type} Material",
                    TypeName = $"{type} Material",
                    IconCode = "\uE91B",
                    IconColor = "#FFBD63C5",
                    Type = AssetType.Materials,
                    Path = $"Material:Custom_{materialId}"
                });
            }
        }

        private void CreateNewShader(string type)
        {
            Assets.Add(new AssetItem
            {
                Id = Assets.Count,
                Name = $"New {type} Shader",
                TypeName = $"{type} Shader",
                IconCode = "\uE9F5",
                IconColor = "#FF569CD6",
                Type = AssetType.Shaders,
                Path = $"Shader:Custom_{Assets.Count}"
            });
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
            }
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

        private void LoadDefaultMeshes()
        {
            string[] primitives = { "Cube", "Sphere", "Plane", "Cylinder", "Cone", "Capsule", "Torus" };
            
            for (int i = 0; i < primitives.Length; i++)
            {
                Assets.Add(new AssetItem
                {
                    Id = i,
                    Name = primitives[i],
                    TypeName = "Primitive",
                    IconCode = "\uF158",
                    IconColor = "#4EC9B0",
                    Type = AssetType.Meshes,
                    Path = $"Primitive:{primitives[i]}"
                });
            }
        }

        private void LoadImportedModels()
        {
            // Initially empty - models are added through import
            // Could be extended to scan a models directory
        }

        private void LoadDefaultTextures()
        {
            Assets.Add(new AssetItem { Id = 0, Name = "White", TypeName = "Solid Color", IconCode = "\uEB9F", IconColor = "#FFFFFF", Type = AssetType.Textures, Path = "Texture:White" });
            Assets.Add(new AssetItem { Id = 1, Name = "Black", TypeName = "Solid Color", IconCode = "\uEB9F", IconColor = "#333333", Type = AssetType.Textures, Path = "Texture:Black" });
            Assets.Add(new AssetItem { Id = 2, Name = "Normal", TypeName = "Normal Map", IconCode = "\uEB9F", IconColor = "#8080FF", Type = AssetType.Textures, Path = "Texture:Normal" });
            Assets.Add(new AssetItem { Id = 3, Name = "Checker", TypeName = "Pattern", IconCode = "\uEB9F", IconColor = "#808080", Type = AssetType.Textures, Path = "Texture:Checker" });
        }

        private void LoadDefaultMaterials()
        {
            Assets.Add(new AssetItem { Id = 0, Name = "Default", TypeName = "Standard", IconCode = "\uE91B", IconColor = "#BD63C5", Type = AssetType.Materials, Path = "Material:Default" });
            Assets.Add(new AssetItem { Id = 1, Name = "Unlit White", TypeName = "Unlit", IconCode = "\uE91B", IconColor = "#FFFFFF", Type = AssetType.Materials, Path = "Material:UnlitWhite" });
            Assets.Add(new AssetItem { Id = 2, Name = "Grid", TypeName = "Standard", IconCode = "\uE91B", IconColor = "#4EC9B0", Type = AssetType.Materials, Path = "Material:Grid" });
        }

        private void LoadDefaultShaders()
        {
            Assets.Add(new AssetItem { Id = 0, Name = "Standard", TypeName = "PBR Shader", IconCode = "\uE9F5", IconColor = "#569CD6", Type = AssetType.Shaders, Path = "Shader:Standard" });
            Assets.Add(new AssetItem { Id = 1, Name = "Unlit", TypeName = "Basic Shader", IconCode = "\uE9F5", IconColor = "#569CD6", Type = AssetType.Shaders, Path = "Shader:Unlit" });
            Assets.Add(new AssetItem { Id = 2, Name = "Wireframe", TypeName = "Debug Shader", IconCode = "\uE9F5", IconColor = "#4EC9B0", Type = AssetType.Shaders, Path = "Shader:Wireframe" });
            Assets.Add(new AssetItem { Id = 3, Name = "Grid", TypeName = "Editor Shader", IconCode = "\uE9F5", IconColor = "#DCDCAA", Type = AssetType.Shaders, Path = "Shader:Grid" });
        }

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
    }
}
