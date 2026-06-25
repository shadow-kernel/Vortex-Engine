using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Editor.Editors.WorldEditor.Components.AssetBrowser;

namespace Editor.Editors.WorldEditor.Components.Inspector
{
    public partial class AssetPickerDialog : Window
    {
        public class AssetPickerItem
        {
            public string Name { get; set; }
            public string TypeName { get; set; }
            public string IconCode { get; set; }
            public string IconColor { get; set; }
            public string Path { get; set; }
        }

        private List<AssetPickerItem> _allAssets;
        
        public AssetPickerItem SelectedAsset { get; private set; }

        public AssetPickerDialog()
        {
            InitializeComponent();
            _allAssets = new List<AssetPickerItem>();
        }

        public static AssetPickerDialog CreateForMeshes()
        {
            var dialog = new AssetPickerDialog();
            dialog.Title = "Select Mesh";
            dialog.LoadMeshAssets();
            return dialog;
        }

        public static AssetPickerDialog CreateForMaterials()
        {
            var dialog = new AssetPickerDialog();
            dialog.Title = "Select Material";
            dialog.LoadMaterialAssets();
            return dialog;
        }

        public static AssetPickerDialog CreateForTextures()
        {
            var dialog = new AssetPickerDialog();
            dialog.Title = "Select Texture";
            dialog.LoadTextureAssets();
            return dialog;
        }

        public static AssetPickerDialog CreateForShaders()
        {
            var dialog = new AssetPickerDialog();
            dialog.Title = "Select Shader";
            dialog.LoadShaderAssets();
            return dialog;
        }

        private void LoadMeshAssets()
        {
            _allAssets.Clear();
            
            // Add "None" option
            _allAssets.Add(new AssetPickerItem
            {
                Name = "None",
                TypeName = "No Mesh",
                IconCode = "\uE711",
                IconColor = "#808080",
                Path = null
            });

            // Add primitive meshes
            string[] primitives = { "Cube", "Sphere", "Plane", "Cylinder", "Cone", "Capsule", "Torus" };
            foreach (var prim in primitives)
            {
                _allAssets.Add(new AssetPickerItem
                {
                    Name = prim,
                    TypeName = "Primitive Mesh",
                    IconCode = "\uF158",
                    IconColor = "#4EC9B0",
                    Path = $"Primitive:{prim}"
                });
            }

            RefreshList();
        }

        private void LoadMaterialAssets()
        {
            _allAssets.Clear();

            // Built-in fallbacks so there is always a sane choice even in an empty project.
            _allAssets.Add(new AssetPickerItem { Name = "Default", TypeName = "Standard Material", IconCode = "\uE91B", IconColor = "#BD63C5", Path = "Material:Default" });
            _allAssets.Add(new AssetPickerItem { Name = "Unlit White", TypeName = "Unlit Material", IconCode = "\uE91B", IconColor = "#FFFFFF", Path = "Material:UnlitWhite" });

            // Real .vmat assets from the project. This is what makes an edited material assignable to
            // a mesh \u2014 the path is the project-relative .vmat that SceneRenderService.GetOrCreateMaterial
            // resolves and builds via MaterialService.GetOrBuildVortexMaterial, so it renders live.
            try
            {
                foreach (var asset in Editor.Core.Assets.AssetDatabase.Instance.GetAssetsByType(Editor.Core.Assets.AssetType.Material))
                {
                    if (asset == null || string.IsNullOrEmpty(asset.RelativePath)) continue;
                    _allAssets.Add(new AssetPickerItem
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(asset.FileName ?? asset.RelativePath),
                        TypeName = "Material (.vmat)",
                        IconCode = "\uE91B",
                        IconColor = "#BD63C5",
                        Path = asset.RelativePath
                    });
                }
            }
            catch
            {
                // No active project / database unavailable \u2014 fall back to the built-ins above.
            }

            RefreshList();
        }

        private void LoadTextureAssets()
        {
            _allAssets.Clear();

            _allAssets.Add(new AssetPickerItem { Name = "None", TypeName = "No Texture", IconCode = "\uE711", IconColor = "#808080", Path = null });
            _allAssets.Add(new AssetPickerItem { Name = "White", TypeName = "Solid Color", IconCode = "\uEB9F", IconColor = "#FFFFFF", Path = "Texture:White" });
            _allAssets.Add(new AssetPickerItem { Name = "Black", TypeName = "Solid Color", IconCode = "\uEB9F", IconColor = "#333333", Path = "Texture:Black" });
            _allAssets.Add(new AssetPickerItem { Name = "Normal", TypeName = "Normal Map", IconCode = "\uEB9F", IconColor = "#8080FF", Path = "Texture:Normal" });

            RefreshList();
        }

        private void LoadShaderAssets()
        {
            _allAssets.Clear();

            _allAssets.Add(new AssetPickerItem { Name = "Standard", TypeName = "PBR Shader", IconCode = "\uE9F5", IconColor = "#569CD6", Path = "Shader:Standard" });
            _allAssets.Add(new AssetPickerItem { Name = "Unlit", TypeName = "Basic Shader", IconCode = "\uE9F5", IconColor = "#569CD6", Path = "Shader:Unlit" });
            _allAssets.Add(new AssetPickerItem { Name = "Wireframe", TypeName = "Debug Shader", IconCode = "\uE9F5", IconColor = "#4EC9B0", Path = "Shader:Wireframe" });

            RefreshList();
        }

        private void RefreshList()
        {
            var filter = SearchBox?.Text?.ToLower() ?? "";
            
            var filtered = string.IsNullOrEmpty(filter) 
                ? _allAssets 
                : _allAssets.Where(a => a.Name.ToLower().Contains(filter) || 
                                        a.TypeName.ToLower().Contains(filter)).ToList();
            
            AssetListBox.ItemsSource = filtered;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshList();
        }

        private void AssetListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectButton.IsEnabled = AssetListBox.SelectedItem != null;
        }

        private void AssetListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (AssetListBox.SelectedItem is AssetPickerItem item)
            {
                SelectedAsset = item;
                DialogResult = true;
                Close();
            }
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (AssetListBox.SelectedItem is AssetPickerItem item)
            {
                SelectedAsset = item;
                DialogResult = true;
                Close();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
