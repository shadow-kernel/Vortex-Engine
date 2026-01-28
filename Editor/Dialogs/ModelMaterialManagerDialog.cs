using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.DllWrapper;

namespace Editor.Dialogs
{
    /// <summary>
    /// Represents a texture slot that can be assigned to a material.
    /// </summary>
    public class TextureSlot : INotifyPropertyChanged
    {
        private string _path;
        private BitmapSource _preview;
        private bool _isAssigned;

        public string SlotName { get; set; } // e.g., "Albedo", "Normal", "Metallic"
        public string SlotType { get; set; } // e.g., "albedo", "normal", "metallic", "roughness", "ao", "emissive", "custom"
        
        public string Path
        {
            get => _path;
            set { _path = value; OnPropertyChanged(nameof(Path)); OnPropertyChanged(nameof(FileName)); OnPropertyChanged(nameof(IsAssigned)); }
        }
        
        public string FileName => string.IsNullOrEmpty(_path) ? "None" : System.IO.Path.GetFileName(_path);
        public bool IsAssigned => !string.IsNullOrEmpty(_path);
        
        public BitmapSource Preview
        {
            get => _preview;
            set { _preview = value; OnPropertyChanged(nameof(Preview)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Represents a material configuration for a submesh.
    /// </summary>
    public class SubmeshMaterial : INotifyPropertyChanged
    {
        private string _name;
        private Color _baseColor = Colors.White;
        private float _metallic = 0f;
        private float _roughness = 0.5f;
        private float _normalStrength = 1f;
        private bool _isExpanded = false;

        public int SubmeshIndex { get; set; }
        public string Name { get => _name; set { _name = value; OnPropertyChanged(nameof(Name)); } }
        public long MeshId { get; set; } = -1;
        public long MaterialId { get; set; } = -1;
        
        public Color BaseColor { get => _baseColor; set { _baseColor = value; OnPropertyChanged(nameof(BaseColor)); OnPropertyChanged(nameof(BaseColorBrush)); } }
        public SolidColorBrush BaseColorBrush => new SolidColorBrush(BaseColor);
        
        public float Metallic { get => _metallic; set { _metallic = value; OnPropertyChanged(nameof(Metallic)); } }
        public float Roughness { get => _roughness; set { _roughness = value; OnPropertyChanged(nameof(Roughness)); } }
        public float NormalStrength { get => _normalStrength; set { _normalStrength = value; OnPropertyChanged(nameof(NormalStrength)); } }
        
        public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); } }
        
        // All texture slots for this material
        public ObservableCollection<TextureSlot> TextureSlots { get; } = new ObservableCollection<TextureSlot>();
        
        public int TextureCount => TextureSlots.Count(t => t.IsAssigned);
        public string TextureInfo => $"{TextureCount} textures";

        public SubmeshMaterial()
        {
            // Standard PBR slots
            TextureSlots.Add(new TextureSlot { SlotName = "Albedo", SlotType = "albedo" });
            TextureSlots.Add(new TextureSlot { SlotName = "Normal", SlotType = "normal" });
            TextureSlots.Add(new TextureSlot { SlotName = "Metallic", SlotType = "metallic" });
            TextureSlots.Add(new TextureSlot { SlotName = "Roughness", SlotType = "roughness" });
            TextureSlots.Add(new TextureSlot { SlotName = "AO", SlotType = "ao" });
        }

        public void AddCustomTextureSlot(string name)
        {
            TextureSlots.Add(new TextureSlot { SlotName = name, SlotType = "custom" });
            OnPropertyChanged(nameof(TextureCount));
            OnPropertyChanged(nameof(TextureInfo));
        }

        public TextureSlot GetSlot(string type) => TextureSlots.FirstOrDefault(t => t.SlotType == type);

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Improved Model Editor with support for multiple submeshes and materials.
    /// Handles models with many textures (10-100+) efficiently.
    /// </summary>
    public class ModelMaterialManagerDialog : Window
    {
        private string _modelPath;
        private string _modelDirectory;
        private ObservableCollection<SubmeshMaterial> _submeshMaterials = new ObservableCollection<SubmeshMaterial>();
        private SubmeshMaterial _selectedMaterial;
        private List<string> _allTexturesInDirectory = new List<string>();

        // UI Elements
        private ListBox _submeshList;
        private StackPanel _materialPropertiesPanel;
        private ItemsControl _texturesGrid;
        private TextBlock _statusText;
        private TextBlock _statsText;
        private WrapPanel _allTexturesPanel;
        private TabControl _mainTabs;

        public ModelMaterialManagerDialog(string modelPath)
        {
            _modelPath = modelPath;
            _modelDirectory = Path.GetDirectoryName(modelPath);

            Title = $"Model Material Manager - {Path.GetFileName(modelPath)}";
            Width = 1200;
            Height = 800;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 900;
            MinHeight = 600;

            BuildUI();
            Loaded += (s, e) => LoadModel();
        }

        private void BuildUI()
        {
            var mainGrid = new Grid();
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Left: Submesh List
            var leftPanel = BuildSubmeshListPanel();
            mainGrid.Children.Add(leftPanel);

            // Right: Tabs for Material Properties / All Textures
            _mainTabs = new TabControl
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                BorderThickness = new Thickness(0)
            };
            Grid.SetColumn(_mainTabs, 1);

            // Tab 1: Material Properties
            var materialTab = new TabItem { Header = "Material Properties" };
            materialTab.Content = BuildMaterialPropertiesPanel();
            _mainTabs.Items.Add(materialTab);

            // Tab 2: All Textures Overview
            var texturesTab = new TabItem { Header = "All Textures" };
            texturesTab.Content = BuildAllTexturesPanel();
            _mainTabs.Items.Add(texturesTab);

            // Tab 3: Texture Library (from folder)
            var libraryTab = new TabItem { Header = "Texture Library" };
            libraryTab.Content = BuildTextureLibraryPanel();
            _mainTabs.Items.Add(libraryTab);

            mainGrid.Children.Add(_mainTabs);

            // Bottom: Status Bar
            var statusBar = BuildStatusBar();
            Grid.SetRow(statusBar, 1);
            Grid.SetColumnSpan(statusBar, 2);
            mainGrid.Children.Add(statusBar);

            Content = mainGrid;
        }

        private Border BuildSubmeshListPanel()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(0, 0, 1, 0)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header
            var header = new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            var headerStack = new StackPanel();
            headerStack.Children.Add(new TextBlock
            {
                Text = "Submeshes & Materials",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            });
            _statsText = new TextBlock
            {
                Text = "0 submeshes, 0 textures",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(157, 157, 157)),
                Margin = new Thickness(0, 3, 0, 0)
            };
            headerStack.Children.Add(_statsText);
            header.Child = headerStack;
            grid.Children.Add(header);

            // Submesh List
            _submeshList = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8),
                ItemsSource = _submeshMaterials
            };
            _submeshList.SelectionChanged += SubmeshList_SelectionChanged;
            
            // Custom item template
            var itemTemplate = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.PaddingProperty, new Thickness(8, 6, 8, 6));
            factory.SetValue(Border.MarginProperty, new Thickness(0, 2, 0, 2));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            factory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 48)));
            
            var stackFactory = new FrameworkElementFactory(typeof(StackPanel));
            
            var nameFactory = new FrameworkElementFactory(typeof(TextBlock));
            nameFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
            nameFactory.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            nameFactory.SetValue(TextBlock.FontSizeProperty, 12.0);
            stackFactory.AppendChild(nameFactory);
            
            var infoFactory = new FrameworkElementFactory(typeof(TextBlock));
            infoFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("TextureInfo"));
            infoFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(157, 157, 157)));
            infoFactory.SetValue(TextBlock.FontSizeProperty, 10.0);
            stackFactory.AppendChild(infoFactory);
            
            factory.AppendChild(stackFactory);
            itemTemplate.VisualTree = factory;
            _submeshList.ItemTemplate = itemTemplate;
            
            Grid.SetRow(_submeshList, 1);
            grid.Children.Add(_submeshList);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(12, 8, 12, 8)
            };
            var autoAssignBtn = CreateButton("Auto-Assign All", 100);
            autoAssignBtn.Click += AutoAssignAll_Click;
            buttonPanel.Children.Add(autoAssignBtn);
            var clearAllBtn = CreateButton("Clear All", 70);
            clearAllBtn.Margin = new Thickness(8, 0, 0, 0);
            clearAllBtn.Click += ClearAllTextures_Click;
            buttonPanel.Children.Add(clearAllBtn);
            Grid.SetRow(buttonPanel, 2);
            grid.Children.Add(buttonPanel);

            border.Child = grid;
            return border;
        }

        private ScrollViewer BuildMaterialPropertiesPanel()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _materialPropertiesPanel = new StackPanel { Margin = new Thickness(15) };

            // Placeholder when nothing selected
            _materialPropertiesPanel.Children.Add(new TextBlock
            {
                Text = "Select a submesh to edit its material",
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 20, 0, 0)
            });

            scroll.Content = _materialPropertiesPanel;
            return scroll;
        }

        private ScrollViewer BuildAllTexturesPanel()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel { Margin = new Thickness(15) };

            stack.Children.Add(new TextBlock
            {
                Text = "All Assigned Textures",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 15)
            });

            _allTexturesPanel = new WrapPanel();
            stack.Children.Add(_allTexturesPanel);

            scroll.Content = stack;
            return scroll;
        }

        private Grid BuildTextureLibraryPanel()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Header with path
            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                Padding = new Thickness(15, 10, 15, 10)
            };
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            
            var pathText = new TextBlock
            {
                Text = $"Textures from: {_modelDirectory}",
                Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            headerGrid.Children.Add(pathText);
            
            var refreshBtn = CreateButton("Refresh", 70);
            refreshBtn.Click += (s, e) => LoadTextureLibrary();
            Grid.SetColumn(refreshBtn, 1);
            headerGrid.Children.Add(refreshBtn);
            
            header.Child = headerGrid;
            grid.Children.Add(header);

            // Texture grid
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _texturesGrid = new ItemsControl();
            _texturesGrid.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
            _texturesGrid.Margin = new Thickness(15);
            scroll.Content = _texturesGrid;
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            return grid;
        }

        private Border BuildStatusBar()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(15, 8, 15, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _statusText = new TextBlock
            {
                Text = "Ready",
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(_statusText);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var saveBtn = CreateButton("Save Materials", 100);
            saveBtn.Background = new SolidColorBrush(Color.FromRgb(0, 120, 212));
            saveBtn.Click += SaveMaterials_Click;
            buttonPanel.Children.Add(saveBtn);
            var closeBtn = CreateButton("Close", 80);
            closeBtn.Margin = new Thickness(10, 0, 0, 0);
            closeBtn.Click += (s, e) => Close();
            buttonPanel.Children.Add(closeBtn);
            Grid.SetColumn(buttonPanel, 1);
            grid.Children.Add(buttonPanel);

            border.Child = grid;
            return border;
        }

        private void LoadModel()
        {
            try
            {
                _statusText.Text = "Loading model...";
                
                // For OBJ files: Parse the OBJ and MTL files directly
                if (_modelPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                {
                    LoadObjModel();
                }
                else
                {
                    // For FBX and other formats: Use engine API
                    LoadGenericModel();
                }
                
                // Load texture library for manual assignment
                LoadTextureLibrary();
                
                UpdateAllTexturesDisplay();
                UpdateStats();
                
                if (_submeshMaterials.Count > 0)
                    _submeshList.SelectedIndex = 0;

                _statusText.Text = $"Loaded: {Path.GetFileName(_modelPath)} ({_submeshMaterials.Count} materials)";
            }
            catch (Exception ex)
            {
                _statusText.Text = $"Error: {ex.Message}";
                MessageBox.Show($"Error loading model:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Loads an OBJ model by parsing the OBJ and MTL files directly.
        /// </summary>
        private void LoadObjModel()
        {
            // Parse MTL file to get material definitions with textures
            var mtlMaterials = ParseMtlFile(_modelPath);
            
            if (mtlMaterials.Count == 0)
            {
                // Fallback: create a single default material
                _submeshMaterials.Add(new SubmeshMaterial
                {
                    SubmeshIndex = 0,
                    Name = Path.GetFileNameWithoutExtension(_modelPath)
                });
                return;
            }
            
            // Create SubmeshMaterial for each material in the MTL file
            for (int i = 0; i < mtlMaterials.Count; i++)
            {
                var mtlMat = mtlMaterials[i];
                
                var submeshMat = new SubmeshMaterial
                {
                    SubmeshIndex = i,
                    Name = mtlMat.Name,
                    Roughness = mtlMat.Roughness,
                    Metallic = mtlMat.Metallic
                };
                
                // Apply diffuse color
                if (mtlMat.DiffuseColor != null && mtlMat.DiffuseColor.Length >= 3)
                {
                    submeshMat.BaseColor = Color.FromScRgb(1f, 
                        mtlMat.DiffuseColor[0], 
                        mtlMat.DiffuseColor[1], 
                        mtlMat.DiffuseColor[2]);
                }
                
                // Apply textures from MTL
                if (!string.IsNullOrEmpty(mtlMat.AlbedoTexture))
                {
                    var slot = submeshMat.GetSlot("albedo");
                    if (slot != null)
                    {
                        slot.Path = mtlMat.AlbedoTexture;
                        LoadTexturePreview(slot);
                    }
                }
                
                if (!string.IsNullOrEmpty(mtlMat.NormalTexture))
                {
                    var slot = submeshMat.GetSlot("normal");
                    if (slot != null)
                    {
                        slot.Path = mtlMat.NormalTexture;
                        LoadTexturePreview(slot);
                    }
                }
                
                if (!string.IsNullOrEmpty(mtlMat.RoughnessTexture))
                {
                    var slot = submeshMat.GetSlot("roughness");
                    if (slot != null)
                    {
                        slot.Path = mtlMat.RoughnessTexture;
                        LoadTexturePreview(slot);
                    }
                }
                else if (!string.IsNullOrEmpty(mtlMat.SpecularTexture))
                {
                    // Use specular as roughness fallback
                    var slot = submeshMat.GetSlot("roughness");
                    if (slot != null)
                    {
                        slot.Path = mtlMat.SpecularTexture;
                        LoadTexturePreview(slot);
                    }
                }
                
                if (!string.IsNullOrEmpty(mtlMat.MetallicTexture))
                {
                    var slot = submeshMat.GetSlot("metallic");
                    if (slot != null)
                    {
                        slot.Path = mtlMat.MetallicTexture;
                        LoadTexturePreview(slot);
                    }
                }
                
                if (!string.IsNullOrEmpty(mtlMat.AOTexture))
                {
                    var slot = submeshMat.GetSlot("ao");
                    if (slot != null)
                    {
                        slot.Path = mtlMat.AOTexture;
                        LoadTexturePreview(slot);
                    }
                }
                
                _submeshMaterials.Add(submeshMat);
            }
        }

        /// <summary>
        /// Loads a generic model (FBX, GLTF, etc.) using the engine API.
        /// </summary>
        private void LoadGenericModel()
        {
            // Try to get submesh names from engine
            string[] submeshNames = null;
            try
            {
                submeshNames = VortexAPI.GetSubmeshNames(_modelPath);
            }
            catch { }
            
            // Try to import the model with materials
            VortexAPI.SubmeshImportData[] submeshData = null;
            try
            {
                submeshData = VortexAPI.ImportModelWithMaterialsFromFile(_modelPath);
            }
            catch { }

            // Determine material count
            int materialCount = 1;
            if (submeshData != null && submeshData.Length > 0)
                materialCount = submeshData.Length;
            else if (submeshNames != null && submeshNames.Length > 0)
                materialCount = submeshNames.Length;

            for (int i = 0; i < materialCount; i++)
            {
                string materialName = $"Material_{i}";
                
                if (submeshNames != null && i < submeshNames.Length && 
                    !string.IsNullOrEmpty(submeshNames[i]) && 
                    submeshNames[i] != "DefaultMaterial")
                {
                    materialName = submeshNames[i];
                }
                
                var material = new SubmeshMaterial
                {
                    SubmeshIndex = i,
                    Name = materialName,
                    MeshId = submeshData != null && i < submeshData.Length ? submeshData[i].MeshId : -1,
                    MaterialId = submeshData != null && i < submeshData.Length ? submeshData[i].MaterialId : -1
                };
                _submeshMaterials.Add(material);
            }
        }

        /// <summary>
        /// Parsed material data from MTL file
        /// </summary>
        private class MtlMaterialData
        {
            public string Name { get; set; }
            public string AlbedoTexture { get; set; }      // map_Kd
            public string NormalTexture { get; set; }      // map_Bump, bump, norm
            public string SpecularTexture { get; set; }    // map_Ks
            public string RoughnessTexture { get; set; }   // map_Pr, map_Ns
            public string MetallicTexture { get; set; }    // map_Pm
            public string AOTexture { get; set; }          // map_Ka
            public string EmissiveTexture { get; set; }    // map_Ke
            public float[] DiffuseColor { get; set; }      // Kd
            public float[] SpecularColor { get; set; }     // Ks
            public float Roughness { get; set; } = 0.5f;   // Ns (inverted)
            public float Metallic { get; set; } = 0f;      // Pm
        }

        /// <summary>
        /// Parses an MTL file and returns all material definitions with their textures.
        /// </summary>
        private List<MtlMaterialData> ParseMtlFile(string objPath)
        {
            var materials = new List<MtlMaterialData>();
            
            try
            {
                // Find the MTL file
                string mtlPath = FindMtlFile(objPath);
                if (mtlPath == null || !File.Exists(mtlPath))
                    return materials;
                
                var mtlLines = File.ReadAllLines(mtlPath);
                var mtlDirectory = Path.GetDirectoryName(mtlPath);
                MtlMaterialData currentMaterial = null;
                
                foreach (var rawLine in mtlLines)
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                        continue;
                    
                    // New material definition
                    if (line.StartsWith("newmtl ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (currentMaterial != null)
                            materials.Add(currentMaterial);
                        
                        currentMaterial = new MtlMaterialData
                        {
                            Name = line.Substring(7).Trim()
                        };
                    }
                    else if (currentMaterial != null)
                    {
                        // Parse texture maps and properties
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) continue;
                        
                        var key = parts[0].ToLowerInvariant();
                        var value = string.Join(" ", parts.Skip(1)).Trim();
                        
                        // Resolve relative texture path to absolute
                        string ResolveTexturePath(string texName)
                        {
                            if (string.IsNullOrEmpty(texName)) return null;
                            
                            // Try relative to MTL file
                            var path1 = Path.Combine(mtlDirectory, texName);
                            if (File.Exists(path1)) return path1;
                            
                            // Try relative to model directory
                            var path2 = Path.Combine(_modelDirectory, texName);
                            if (File.Exists(path2)) return path2;
                            
                            // Try just the filename in model directory
                            var justFileName = Path.GetFileName(texName);
                            var path3 = Path.Combine(_modelDirectory, justFileName);
                            if (File.Exists(path3)) return path3;
                            
                            return null;
                        }
                        
                        switch (key)
                        {
                            // Diffuse/Albedo texture
                            case "map_kd":
                                currentMaterial.AlbedoTexture = ResolveTexturePath(value);
                                break;
                            
                            // Normal/Bump map
                            case "map_bump":
                            case "bump":
                            case "map_kn":
                            case "norm":
                                currentMaterial.NormalTexture = ResolveTexturePath(value);
                                break;
                            
                            // Specular map
                            case "map_ks":
                                currentMaterial.SpecularTexture = ResolveTexturePath(value);
                                break;
                            
                            // Roughness (PBR extension)
                            case "map_pr":
                            case "map_ns":
                                currentMaterial.RoughnessTexture = ResolveTexturePath(value);
                                break;
                            
                            // Metallic (PBR extension)
                            case "map_pm":
                                currentMaterial.MetallicTexture = ResolveTexturePath(value);
                                break;
                            
                            // Ambient/AO map
                            case "map_ka":
                                currentMaterial.AOTexture = ResolveTexturePath(value);
                                break;
                            
                            // Emissive map
                            case "map_ke":
                                currentMaterial.EmissiveTexture = ResolveTexturePath(value);
                                break;
                            
                            // Diffuse color
                            case "kd":
                                if (parts.Length >= 4)
                                {
                                    currentMaterial.DiffuseColor = new float[]
                                    {
                                        float.TryParse(parts[1], out float r) ? r : 1f,
                                        float.TryParse(parts[2], out float g) ? g : 1f,
                                        float.TryParse(parts[3], out float b) ? b : 1f
                                    };
                                }
                                break;
                            
                            // Specular exponent -> Roughness
                            case "ns":
                                if (float.TryParse(parts[1], out float ns))
                                {
                                    // Ns typically 0-1000, convert to roughness 0-1
                                    currentMaterial.Roughness = 1f - Math.Min(ns / 1000f, 1f);
                                }
                                break;
                        }
                    }
                }
                
                // Don't forget the last material
                if (currentMaterial != null)
                    materials.Add(currentMaterial);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing MTL file: {ex.Message}");
            }
            
            return materials;
        }

        /// <summary>
        /// Finds the MTL file associated with an OBJ file.
        /// </summary>
        private string FindMtlFile(string objPath)
        {
            try
            {
                // First, read the OBJ to find mtllib reference
                var objLines = File.ReadAllLines(objPath);
                foreach (var line in objLines)
                {
                    if (line.StartsWith("mtllib ", StringComparison.OrdinalIgnoreCase))
                    {
                        var mtlFileName = line.Substring(7).Trim();
                        var mtlPath = Path.Combine(_modelDirectory, mtlFileName);
                        if (File.Exists(mtlPath))
                            return mtlPath;
                    }
                }
                
                // If no mtllib found, look for MTL files in the directory
                var possibleMtlFiles = Directory.GetFiles(_modelDirectory, "*.mtl");
                if (possibleMtlFiles.Length > 0)
                {
                    // Prefer one with similar name to the OBJ
                    var baseName = Path.GetFileNameWithoutExtension(objPath).ToLowerInvariant();
                    var match = possibleMtlFiles.FirstOrDefault(f => 
                        Path.GetFileNameWithoutExtension(f).ToLowerInvariant().Contains(baseName) ||
                        baseName.Contains(Path.GetFileNameWithoutExtension(f).ToLowerInvariant()));
                    
                    return match ?? possibleMtlFiles[0];
                }
            }
            catch { }
            
            return null;
        }

        /// <summary>
        /// Reads material names from an OBJ's associated MTL file.
        /// </summary>
        private List<string> ReadMaterialNamesFromMtl(string objPath)
        {
            var mtlData = ParseMtlFile(objPath);
            return mtlData.Select(m => m.Name).ToList();
        }

        /// <summary>
        /// Applies parsed MTL data to the submesh materials.
        /// </summary>
        private void ApplyMtlDataToMaterials(List<MtlMaterialData> mtlData)
        {
            foreach (var mtlMat in mtlData)
            {
                // Find the corresponding SubmeshMaterial
                var submeshMat = _submeshMaterials.FirstOrDefault(m => 
                    m.Name.Equals(mtlMat.Name, StringComparison.OrdinalIgnoreCase));
                
                if (submeshMat == null) continue;
                
                // Apply textures
                if (!string.IsNullOrEmpty(mtlMat.AlbedoTexture))
                {
                    var slot = submeshMat.GetSlot("albedo");
                    if (slot != null)
                    {
                        slot.Path = mtlMat.AlbedoTexture;
                        LoadTexturePreview(slot);
                    }
                }
                
                if (!string.IsNullOrEmpty(mtlMat.NormalTexture))
                {
                    var slot = submeshMat.GetSlot("normal");
                    if (slot != null)
                    {
                        slot.Path = mtlMat.NormalTexture;
                        LoadTexturePreview(slot);
                    }
                }
                
                if (!string.IsNullOrEmpty(mtlMat.RoughnessTexture))
                {
                    var slot = submeshMat.GetSlot("roughness");
                    if (slot != null)
                    {
                        slot.Path = mtlMat.RoughnessTexture;
                        LoadTexturePreview(slot);
                    }
                }
                else if (!string.IsNullOrEmpty(mtlMat.SpecularTexture))
                {
                    // Use specular as roughness if no roughness map
                    var slot = submeshMat.GetSlot("roughness");
                    if (slot != null)
                    {
                        slot.Path = mtlMat.SpecularTexture;
                        LoadTexturePreview(slot);
                    }
                }
                
                if (!string.IsNullOrEmpty(mtlMat.MetallicTexture))
                {
                    var slot = submeshMat.GetSlot("metallic");
                    if (slot != null)
                    {
                        slot.Path = mtlMat.MetallicTexture;
                        LoadTexturePreview(slot);
                    }
                }
                
                if (!string.IsNullOrEmpty(mtlMat.AOTexture))
                {
                    var slot = submeshMat.GetSlot("ao");
                    if (slot != null)
                    {
                        slot.Path = mtlMat.AOTexture;
                        LoadTexturePreview(slot);
                    }
                }
                
                // Apply properties
                submeshMat.Roughness = mtlMat.Roughness;
                submeshMat.Metallic = mtlMat.Metallic;
                
                if (mtlMat.DiffuseColor != null && mtlMat.DiffuseColor.Length >= 3)
                {
                    submeshMat.BaseColor = Color.FromScRgb(1f, 
                        mtlMat.DiffuseColor[0], 
                        mtlMat.DiffuseColor[1], 
                        mtlMat.DiffuseColor[2]);
                }
            }
        }

        private void LoadTextureLibrary()
        {
            _allTexturesInDirectory.Clear();
            _texturesGrid.Items.Clear();

            if (string.IsNullOrEmpty(_modelDirectory))
            {
                _statusText.Text = "Error: Model directory is empty";
                return;
            }
            
            if (!Directory.Exists(_modelDirectory))
            {
                _statusText.Text = $"Error: Directory not found: {_modelDirectory}";
                return;
            }

            var imageExtensions = new[] { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds", ".hdr" };
            
            // Search in model directory and common subdirectories
            var searchDirs = new List<string> { _modelDirectory };
            var subDirs = new[] { "textures", "Textures", "tex", "maps", "Materials", "Texture", "texture" };
            foreach (var sub in subDirs)
            {
                var subPath = Path.Combine(_modelDirectory, sub);
                if (Directory.Exists(subPath))
                    searchDirs.Add(subPath);
            }
            
            // Also search parent directory for textures
            var parentDir = Path.GetDirectoryName(_modelDirectory);
            if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
            {
                searchDirs.Add(parentDir);
            }

            foreach (var dir in searchDirs)
            {
                try
                {
                    var files = Directory.GetFiles(dir)
                        .Where(f => imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
                    _allTexturesInDirectory.AddRange(files);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error reading directory {dir}: {ex.Message}");
                }
            }

            // Build texture library UI
            foreach (var texPath in _allTexturesInDirectory.Distinct())
            {
                var item = CreateTextureLibraryItem(texPath);
                _texturesGrid.Items.Add(item);
            }
            
            _statusText.Text = $"Found {_allTexturesInDirectory.Count} texture(s) in {_modelDirectory}";
        }

        private Border CreateTextureLibraryItem(string texturePath)
        {
            var border = new Border
            {
                Width = 120,
                Margin = new Thickness(0, 0, 10, 10),
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                Cursor = Cursors.Hand
            };

            var stack = new StackPanel();

            // Preview
            var preview = new Border
            {
                Height = 80,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };
            
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(texturePath);
                bitmap.DecodePixelWidth = 80;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                preview.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            }
            catch { }

            stack.Children.Add(preview);

            // Name
            stack.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(texturePath),
                Foreground = Brushes.White,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 5, 0, 0),
                ToolTip = texturePath
            });

            // Type detection
            var fileName = Path.GetFileName(texturePath).ToLowerInvariant();
            string detectedType = "Unknown";
            if (fileName.Contains("albedo") || fileName.Contains("diffuse") || fileName.Contains("color") || fileName.Contains("_d."))
                detectedType = "Albedo";
            else if (fileName.Contains("normal") || fileName.Contains("_n.") || fileName.Contains("_norm"))
                detectedType = "Normal";
            else if (fileName.Contains("metallic") || fileName.Contains("_m.") || fileName.Contains("metal"))
                detectedType = "Metallic";
            else if (fileName.Contains("roughness") || fileName.Contains("_r.") || fileName.Contains("rough"))
                detectedType = "Roughness";
            else if (fileName.Contains("ao") || fileName.Contains("occlusion") || fileName.Contains("_ao"))
                detectedType = "AO";
            else if (fileName.Contains("emissive") || fileName.Contains("emission"))
                detectedType = "Emissive";

            stack.Children.Add(new TextBlock
            {
                Text = detectedType,
                Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                FontSize = 9
            });

            border.Child = stack;

            // Drag support
            border.MouseMove += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    var data = new DataObject(DataFormats.FileDrop, new[] { texturePath });
                    DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
                }
            };

            return border;
        }

        private void AutoAssignTextures()
        {
            foreach (var mat in _submeshMaterials)
            {
                AutoAssignTexturesForMaterial(mat);
            }
            UpdateAllTexturesDisplay();
            UpdateStats();
        }

        private void AutoAssignTexturesForMaterial(SubmeshMaterial material)
        {
            var matName = material.Name.ToLowerInvariant();
            
            foreach (var texPath in _allTexturesInDirectory)
            {
                var fileName = Path.GetFileName(texPath).ToLowerInvariant();
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(texPath).ToLowerInvariant();
                
                // Try to match by material/submesh name
                bool nameMatch = fileName.Contains(matName) || 
                                 matName.Contains(fileNameWithoutExt) ||
                                 fileNameWithoutExt.Contains(matName);
                
                // Determine slot type based on common naming conventions
                string slotType = null;
                
                // Albedo / Diffuse / Color / BaseColor
                if (fileName.Contains("albedo") || fileName.Contains("diffuse") || 
                    fileName.Contains("_color") || fileName.Contains("color.") ||
                    fileName.Contains("_d.") || fileName.Contains("_diff") ||
                    fileName.Contains("basecolor") || fileName.Contains("_col.") ||
                    fileName.EndsWith("_color.jpg") || fileName.EndsWith("_color.png"))
                    slotType = "albedo";
                    
                // Normal Map
                else if (fileName.Contains("normal") || fileName.Contains("_n.") || 
                         fileName.Contains("_norm") || fileName.Contains("_nrm") ||
                         fileName.Contains("nmap") || fileName.Contains("_normal"))
                    slotType = "normal";
                    
                // Metallic / Metalness
                else if (fileName.Contains("metallic") || fileName.Contains("_m.") || 
                         fileName.Contains("_met") || fileName.Contains("metal") ||
                         fileName.Contains("metalness"))
                    slotType = "metallic";
                    
                // Roughness
                else if (fileName.Contains("roughness") || fileName.Contains("_r.") || 
                         fileName.Contains("_rough") || fileName.Contains("_rgh") ||
                         fileName.Contains("rough."))
                    slotType = "roughness";
                    
                // Ambient Occlusion
                else if (fileName.Contains("_ao") || fileName.Contains("occlusion") || 
                         fileName.Contains("ambient") || fileName.Contains("ao."))
                    slotType = "ao";

                if (slotType != null)
                {
                    var slot = material.GetSlot(slotType);
                    if (slot != null && !slot.IsAssigned)
                    {
                        // Assign if:
                        // 1. Name matches the material
                        // 2. Only one material (single mesh model)
                        // 3. All materials have same name (like "DefaultMaterial")
                        bool allSameName = _submeshMaterials.All(m => m.Name == material.Name);
                        
                        if (nameMatch || _submeshMaterials.Count == 1 || allSameName)
                        {
                            slot.Path = texPath;
                            LoadTexturePreview(slot);
                        }
                    }
                }
            }
        }

        private void LoadTexturePreview(TextureSlot slot)
        {
            if (string.IsNullOrEmpty(slot.Path) || !File.Exists(slot.Path))
            {
                slot.Preview = null;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(slot.Path);
                bitmap.DecodePixelWidth = 64;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                slot.Preview = bitmap;
            }
            catch
            {
                slot.Preview = null;
            }
        }

        private void UpdateStats()
        {
            int totalTextures = _submeshMaterials.Sum(m => m.TextureCount);
            _statsText.Text = $"{_submeshMaterials.Count} submesh(es), {totalTextures} texture(s)";
        }

        private void UpdateAllTexturesDisplay()
        {
            _allTexturesPanel.Children.Clear();

            foreach (var mat in _submeshMaterials)
            {
                foreach (var slot in mat.TextureSlots.Where(s => s.IsAssigned))
                {
                    var item = new Border
                    {
                        Width = 150,
                        Margin = new Thickness(0, 0, 10, 10),
                        Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8)
                    };

                    var stack = new StackPanel();

                    // Preview
                    var preview = new Border { Height = 100, CornerRadius = new CornerRadius(4) };
                    if (slot.Preview != null)
                        preview.Background = new ImageBrush(slot.Preview) { Stretch = Stretch.UniformToFill };
                    else
                        preview.Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                    stack.Children.Add(preview);

                    // Info
                    stack.Children.Add(new TextBlock
                    {
                        Text = $"{mat.Name} - {slot.SlotName}",
                        Foreground = Brushes.White,
                        FontSize = 10,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 5, 0, 0)
                    });
                    stack.Children.Add(new TextBlock
                    {
                        Text = slot.FileName,
                        Foreground = new SolidColorBrush(Color.FromRgb(157, 157, 157)),
                        FontSize = 9,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });

                    item.Child = stack;
                    _allTexturesPanel.Children.Add(item);
                }
            }
        }

        private void SubmeshList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedMaterial = _submeshList.SelectedItem as SubmeshMaterial;
            UpdateMaterialPropertiesPanel();
        }

        private void UpdateMaterialPropertiesPanel()
        {
            _materialPropertiesPanel.Children.Clear();

            if (_selectedMaterial == null)
            {
                _materialPropertiesPanel.Children.Add(new TextBlock
                {
                    Text = "Select a submesh to edit its material",
                    Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                    FontStyle = FontStyles.Italic
                });
                return;
            }

            // Header
            _materialPropertiesPanel.Children.Add(new TextBlock
            {
                Text = _selectedMaterial.Name,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 15)
            });

            // Base Color
            _materialPropertiesPanel.Children.Add(CreateSectionHeader("BASE COLOR"));
            var colorPreview = new Border
            {
                Height = 32,
                Background = _selectedMaterial.BaseColorBrush,
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 15)
            };
            colorPreview.MouseLeftButtonUp += (s, e) => PickColor(_selectedMaterial);
            _materialPropertiesPanel.Children.Add(colorPreview);

            // PBR Sliders
            _materialPropertiesPanel.Children.Add(CreateSectionHeader("PBR PROPERTIES"));
            _materialPropertiesPanel.Children.Add(CreateSlider("Metallic", _selectedMaterial.Metallic, 0, 1, v => _selectedMaterial.Metallic = (float)v));
            _materialPropertiesPanel.Children.Add(CreateSlider("Roughness", _selectedMaterial.Roughness, 0, 1, v => _selectedMaterial.Roughness = (float)v));
            _materialPropertiesPanel.Children.Add(CreateSlider("Normal Strength", _selectedMaterial.NormalStrength, 0, 2, v => _selectedMaterial.NormalStrength = (float)v));

            // Textures
            _materialPropertiesPanel.Children.Add(CreateSectionHeader("TEXTURES"));
            
            var autoDetectBtn = CreateButton("Auto-Detect Textures", 150);
            autoDetectBtn.HorizontalAlignment = HorizontalAlignment.Left;
            autoDetectBtn.Click += (s, e) => { AutoAssignTexturesForMaterial(_selectedMaterial); UpdateMaterialPropertiesPanel(); };
            _materialPropertiesPanel.Children.Add(autoDetectBtn);
            _materialPropertiesPanel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10), Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)) });

            foreach (var slot in _selectedMaterial.TextureSlots)
            {
                _materialPropertiesPanel.Children.Add(CreateTextureSlotUI(slot));
            }

            // Add custom slot button
            var addSlotBtn = CreateButton("+ Add Custom Texture Slot", 180);
            addSlotBtn.HorizontalAlignment = HorizontalAlignment.Left;
            addSlotBtn.Margin = new Thickness(0, 10, 0, 0);
            addSlotBtn.Click += (s, e) =>
            {
                _selectedMaterial.AddCustomTextureSlot($"Custom_{_selectedMaterial.TextureSlots.Count}");
                UpdateMaterialPropertiesPanel();
            };
            _materialPropertiesPanel.Children.Add(addSlotBtn);
        }

        private UIElement CreateTextureSlotUI(TextureSlot slot)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Preview with drop support
            var preview = new Border
            {
                Width = 48,
                Height = 48,
                CornerRadius = new CornerRadius(4),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1),
                AllowDrop = true
            };
            
            if (slot.Preview != null)
                preview.Background = new ImageBrush(slot.Preview) { Stretch = Stretch.UniformToFill };
            else
                preview.Background = new SolidColorBrush(Color.FromRgb(45, 45, 48));

            preview.DragOver += (s, e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            preview.Drop += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files?.Length > 0)
                    {
                        slot.Path = files[0];
                        LoadTexturePreview(slot);
                        UpdateMaterialPropertiesPanel();
                        UpdateAllTexturesDisplay();
                        UpdateStats();
                    }
                }
            };
            grid.Children.Add(preview);

            // Info
            var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            infoStack.Children.Add(new TextBlock { Text = slot.SlotName, Foreground = Brushes.White, FontSize = 12 });
            infoStack.Children.Add(new TextBlock { Text = slot.FileName, Foreground = new SolidColorBrush(Color.FromRgb(157, 157, 157)), FontSize = 10 });
            Grid.SetColumn(infoStack, 1);
            grid.Children.Add(infoStack);

            // Buttons
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var browseBtn = CreateButton("Browse", 60);
            browseBtn.FontSize = 11;
            browseBtn.Click += (s, e) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = $"Select {slot.SlotName} Texture",
                    Filter = "Images|*.png;*.jpg;*.jpeg;*.tga;*.bmp;*.dds;*.hdr|All|*.*",
                    InitialDirectory = _modelDirectory
                };
                if (dialog.ShowDialog() == true)
                {
                    slot.Path = dialog.FileName;
                    LoadTexturePreview(slot);
                    UpdateMaterialPropertiesPanel();
                    UpdateAllTexturesDisplay();
                    UpdateStats();
                }
            };
            btnPanel.Children.Add(browseBtn);
            
            if (slot.IsAssigned)
            {
                var clearBtn = CreateButton("×", 30);
                clearBtn.FontSize = 14;
                clearBtn.Margin = new Thickness(4, 0, 0, 0);
                clearBtn.Click += (s, e) =>
                {
                    slot.Path = null;
                    slot.Preview = null;
                    UpdateMaterialPropertiesPanel();
                    UpdateAllTexturesDisplay();
                    UpdateStats();
                };
                btnPanel.Children.Add(clearBtn);
            }
            
            Grid.SetColumn(btnPanel, 2);
            grid.Children.Add(btnPanel);

            return grid;
        }

        private UIElement CreateSlider(string label, double value, double min, double max, Action<double> onChanged)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

            grid.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(157, 157, 157)), VerticalAlignment = VerticalAlignment.Center });

            var slider = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(slider, 1);
            grid.Children.Add(slider);

            var valueText = new TextBlock { Text = value.ToString("F2"), Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            Grid.SetColumn(valueText, 2);
            grid.Children.Add(valueText);

            slider.ValueChanged += (s, e) =>
            {
                valueText.Text = e.NewValue.ToString("F2");
                onChanged(e.NewValue);
            };

            return grid;
        }

        private TextBlock CreateSectionHeader(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(157, 157, 157)),
                Margin = new Thickness(0, 10, 0, 8)
            };
        }

        private Button CreateButton(string text, double width)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Background = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 6, 12, 6),
                Cursor = Cursors.Hand
            };
        }

        private void PickColor(SubmeshMaterial material)
        {
            var picker = new ColorPickerDialog(material.BaseColor) { Owner = this };
            if (picker.ShowDialog() == true)
            {
                material.BaseColor = picker.SelectedColor;
                UpdateMaterialPropertiesPanel();
            }
        }

        private void AutoAssignAll_Click(object sender, RoutedEventArgs e)
        {
            AutoAssignTextures();
            UpdateMaterialPropertiesPanel();
            UpdateStats();
            _statusText.Text = "Auto-assigned textures to all materials";
        }

        private void ClearAllTextures_Click(object sender, RoutedEventArgs e)
        {
            foreach (var mat in _submeshMaterials)
            {
                foreach (var slot in mat.TextureSlots)
                {
                    slot.Path = null;
                    slot.Preview = null;
                }
            }
            UpdateMaterialPropertiesPanel();
            UpdateAllTexturesDisplay();
            UpdateStats();
            _statusText.Text = "Cleared all textures";
        }

        private void SaveMaterials_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                foreach (var mat in _submeshMaterials)
                {
                    var vmat = new VortexMaterial
                    {
                        Name = mat.Name,
                        Metallic = mat.Metallic,
                        Roughness = mat.Roughness,
                        NormalStrength = mat.NormalStrength,
                        AlbedoTexture = mat.GetSlot("albedo")?.Path,
                        NormalTexture = mat.GetSlot("normal")?.Path,
                        MetallicTexture = mat.GetSlot("metallic")?.Path,
                        RoughnessTexture = mat.GetSlot("roughness")?.Path,
                        AOTexture = mat.GetSlot("ao")?.Path
                    };
                    vmat.SetBaseColor(mat.BaseColor);

                    var matPath = Path.Combine(_modelDirectory, $"{mat.Name}.vmat");
                    vmat.MakePathsRelative(_modelDirectory);
                    vmat.Save(matPath);
                }

                AssetDatabase.Instance.Refresh();
                _statusText.Text = $"Saved {_submeshMaterials.Count} material(s)";
            }
            catch (Exception ex)
            {
                _statusText.Text = $"Error saving: {ex.Message}";
            }
        }

        /// <summary>
        /// Opens the model material manager for a given model file.
        /// Note: Consider using UniversalModelEditorDialog.OpenForModel for the new improved editor.
        /// </summary>
        public static void OpenForModel(Window owner, string modelPath)
        {
            // Redirect to new universal editor
            UniversalModelEditorDialog.OpenForModel(owner, modelPath);
        }
    }
}
