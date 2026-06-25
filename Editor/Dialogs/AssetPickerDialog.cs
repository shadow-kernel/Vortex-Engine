using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core.Assets;
using Editor.Core.Data;

namespace Editor.Dialogs
{
    /// <summary>
    /// Dialog for selecting assets from the project's Asset system.
    /// </summary>
    public class AssetPickerDialog : Window
    {
        private readonly ObservableCollection<AssetItem> _allAssets = new ObservableCollection<AssetItem>();
        private readonly ObservableCollection<AssetItem> _filteredAssets = new ObservableCollection<AssetItem>();
        private readonly HashSet<string> _selectedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        private TextBox _searchBox;
        private ListBox _assetList;
        private WrapPanel _tagsPanel;
        private TextBlock _selectedAssetText;
        private Button _selectButton;
        
        private string[] _allowedExtensions;

        public string SelectedAssetPath { get; private set; }
        public string SelectedFullPath { get; private set; }
        public Guid SelectedAssetGuid { get; private set; }

        public class AssetItem
        {
            public Guid Guid { get; set; }
            public string Name { get; set; }
            public string RelativePath { get; set; }
            public string FullPath { get; set; }
            public List<string> Tags { get; set; } = new List<string>();
            public string Extension { get; set; }
            public string DisplayName => $"{Name} ({Extension})";
        }

        public AssetPickerDialog(string assetType = null, params string[] allowedExtensions)
        {
            _allowedExtensions = allowedExtensions?.Length > 0 ? allowedExtensions : GetExtensionsForType(assetType);
            
            Title = assetType != null ? $"Select {assetType}" : "Select Asset";
            Width = 650;
            Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(22, 22, 24));
            
            BuildUI();
            Loaded += (s, e) => LoadProjectAssets();
        }

        private string[] GetExtensionsForType(string assetType)
        {
            switch (assetType)
            {
                case "Models":
                    return new[] { ".fbx", ".obj", ".dae", ".3ds", ".blend" };
                case "Textures":
                    return new[] { ".png", ".jpg", ".jpeg", ".tga", ".dds", ".hdr", ".exr" };
                case "Materials":
                    return new[] { ".vmat" };
                default:
                    return null;
            }
        }

        private void BuildUI()
        {
            var mainGrid = new Grid { Margin = new Thickness(15) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Search box
            _searchBox = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 10)
            };
            _searchBox.TextChanged += (s, e) => ApplyFilters();
            Grid.SetRow(_searchBox, 0);
            mainGrid.Children.Add(_searchBox);

            // Tags panel
            var tagsBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var tagsStack = new StackPanel();
            tagsStack.Children.Add(new TextBlock 
            { 
                Text = "Filter by Tags:", 
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8)
            });
            _tagsPanel = new WrapPanel();
            tagsStack.Children.Add(_tagsPanel);
            tagsBorder.Child = tagsStack;
            Grid.SetRow(tagsBorder, 1);
            mainGrid.Children.Add(tagsBorder);

            // Asset list
            var listBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                CornerRadius = new CornerRadius(4),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1)
            };
            
            _assetList = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemsSource = _filteredAssets,
                Foreground = Brushes.White,
                Padding = new Thickness(5),
                DisplayMemberPath = "DisplayName"  // Show the DisplayName property
            };
            _assetList.SelectionChanged += AssetList_SelectionChanged;
            _assetList.MouseDoubleClick += (s, e) => 
            { 
                if (_assetList.SelectedItem != null) 
                { 
                    DialogResult = true; 
                    Close(); 
                } 
            };
            
            listBorder.Child = _assetList;
            Grid.SetRow(listBorder, 2);
            mainGrid.Children.Add(listBorder);


            // Selected info
            var infoBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 10, 0, 0)
            };
            var infoStack = new StackPanel { Orientation = Orientation.Horizontal };
            infoStack.Children.Add(new TextBlock 
            { 
                Text = "Selected: ", 
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                VerticalAlignment = VerticalAlignment.Center
            });
            _selectedAssetText = new TextBlock 
            { 
                Text = "(None)", 
                Foreground = Brushes.White, 
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            infoStack.Children.Add(_selectedAssetText);
            infoBorder.Child = infoStack;
            Grid.SetRow(infoBorder, 3);
            mainGrid.Children.Add(infoBorder);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 15, 0, 0)
            };
            
            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 90,
                Padding = new Thickness(0, 8, 0, 8),
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85))
            };
            cancelButton.Click += (s, e) => { DialogResult = false; Close(); };
            buttonPanel.Children.Add(cancelButton);

            _selectButton = new Button
            {
                Content = "Select",
                Width = 90,
                Padding = new Thickness(0, 8, 0, 8),
                IsEnabled = false,
                Background = new SolidColorBrush(Color.FromRgb(108, 92, 231)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(108, 92, 231))
            };
            _selectButton.Click += (s, e) => { DialogResult = true; Close(); };
            buttonPanel.Children.Add(_selectButton);

            Grid.SetRow(buttonPanel, 4);
            mainGrid.Children.Add(buttonPanel);

            Content = mainGrid;
        }

        private void LoadProjectAssets()
        {
            _allAssets.Clear();
            
            var project = ProjectData.Current;
            if (project == null || string.IsNullOrEmpty(project.Path)) return;

            var assetsPath = Path.Combine(project.Path, "Assets");
            if (!Directory.Exists(assetsPath)) return;

            ScanProjectAssets(assetsPath, project.Path);
            
            // Load tags AFTER scanning assets, so we only show used tags
            LoadTags();
            
            ApplyFilters();
        }

        private void ScanProjectAssets(string directory, string projectPath)
        {
            try
            {
                foreach (var file in Directory.GetFiles(directory))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".vmeta" || ext == ".meta") continue;

                    if (_allowedExtensions != null && _allowedExtensions.Length > 0)
                    {
                        if (!_allowedExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                            continue;
                    }

                    var relativePath = file.Substring(projectPath.Length).TrimStart('\\', '/');
                    var name = Path.GetFileNameWithoutExtension(file);

                    Guid assetGuid = Guid.NewGuid();
                    var tags = new List<string>();
                    
                    var metaPath = file + ".vmeta";
                    if (File.Exists(metaPath))
                    {
                        try
                        {
                            var meta = AssetMetadataService.Instance.LoadMetadata(metaPath);
                            if (meta != null)
                            {
                                assetGuid = meta.Guid;
                                tags = meta.Tags ?? new List<string>();
                            }
                        }
                        catch { }
                    }


                    var serviceTags = AssetTagService.Instance.GetTags(assetGuid);
                    foreach (var tag in serviceTags)
                    {
                        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                            tags.Add(tag);
                    }

                    _allAssets.Add(new AssetItem
                    {
                        Guid = assetGuid,
                        Name = name,
                        RelativePath = relativePath,
                        FullPath = file,
                        Extension = ext,
                        Tags = tags
                    });
                }

                foreach (var subDir in Directory.GetDirectories(directory))
                {
                    ScanProjectAssets(subDir, projectPath);
                }
            }
            catch { }
        }

        private void LoadTags()
        {
            _tagsPanel.Children.Clear();

            // Collect all tags that are actually used by the loaded assets
            var usedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in _allAssets)
            {
                foreach (var tag in asset.Tags)
                {
                    usedTags.Add(tag);
                }
            }

            // Only show tags that are actually used
            if (usedTags.Count > 0)
            {
                foreach (var tag in usedTags.OrderBy(t => t))
                {
                    AddTagToggle(tag);
                }
            }
            else
            {
                // Show message that no tags are defined
                _tagsPanel.Children.Add(new TextBlock
                {
                    Text = "(No tags defined yet - right-click assets in File Explorer to add tags)",
                    Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                    FontStyle = FontStyles.Italic,
                    FontSize = 11
                });
            }
        }

        private void AddTagToggle(string tag)
        {
            var toggle = new CheckBox
            {
                Content = tag,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                Margin = new Thickness(0, 0, 12, 6),
                Tag = tag,
                FontSize = 12
            };
            toggle.Checked += (s, e) => { _selectedTags.Add(tag); ApplyFilters(); };
            toggle.Unchecked += (s, e) => { _selectedTags.Remove(tag); ApplyFilters(); };
            _tagsPanel.Children.Add(toggle);
        }

        private void ApplyFilters()
        {
            _filteredAssets.Clear();
            var searchText = _searchBox?.Text?.ToLowerInvariant() ?? "";

            foreach (var asset in _allAssets)
            {
                if (!string.IsNullOrEmpty(searchText))
                {
                    if (!asset.Name.ToLowerInvariant().Contains(searchText) &&
                        !asset.RelativePath.ToLowerInvariant().Contains(searchText))
                        continue;
                }

                if (_selectedTags.Count > 0)
                {
                    bool hasAllTags = _selectedTags.All(tag => 
                        asset.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
                    if (!hasAllTags) continue;
                }

                _filteredAssets.Add(asset);
            }
        }

        private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = _assetList.SelectedItem as AssetItem;
            if (selected != null && !string.IsNullOrEmpty(selected.FullPath))
            {
                SelectedAssetPath = selected.RelativePath;
                SelectedFullPath = selected.FullPath;
                SelectedAssetGuid = selected.Guid;
                _selectedAssetText.Text = selected.DisplayName;
                _selectButton.IsEnabled = true;
            }
            else
            {
                SelectedAssetPath = null;
                SelectedFullPath = null;
                SelectedAssetGuid = Guid.Empty;
                _selectedAssetText.Text = "(None)";
                _selectButton.IsEnabled = false;
            }
        }
    }
}
