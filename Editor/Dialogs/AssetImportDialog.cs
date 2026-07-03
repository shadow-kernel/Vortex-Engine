using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core.Assets;
using Editor.Core.Data;

namespace Editor.Dialogs
{
    /// <summary>
    /// Dialog for importing assets with tag support.
    /// Supports Model, Texture, and Material imports.
    /// </summary>
    public class AssetImportDialog : Window
    {
        public enum ImportAssetType
        {
            Model,
            Texture,
            Material,
            Shader,
            Audio
        }

        private string _sourcePath;
        private ImportAssetType _assetType;
        private HashSet<string> _selectedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, ToggleButton> _predefinedTagButtons = new Dictionary<string, ToggleButton>();

        // UI Elements
        private TextBlock _filePathText;
        private TextBox _assetNameBox;
        private TextBlock _assetTypeText;
        private TextBox _targetFolderBox;
        private WrapPanel _predefinedTagsPanel;
        private WrapPanel _selectedTagsPanel;
        private TextBlock _noTagsHint;
        private TextBox _customTagBox;
        private CheckBox _copyToProjectCheck;
        private CheckBox _generateMetaCheck;
        private CheckBox _autoDetectTexturesCheck;

        public AssetImportResult Result { get; private set; }

        private readonly string _defaultFolder;   // project-relative folder to pre-fill (the Explorer's current folder)

        public AssetImportDialog(string sourcePath, ImportAssetType type, string defaultFolder = null)
        {
            _sourcePath = sourcePath;
            _assetType = type;
            _defaultFolder = defaultFolder;

            Title = "Import Asset";
            Width = 600;
            Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(22, 22, 24));
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 500;
            MinHeight = 400;

            BuildUI();
            PopulatePredefinedTags();
            AutoSelectTagsForType(type);
        }

        private void BuildUI()
        {
            var mainStack = new StackPanel { Margin = new Thickness(20) };

            // Header
            mainStack.Children.Add(new TextBlock
            {
                Text = "Import Asset",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 5)
            });

            _assetTypeText = new TextBlock
            {
                Text = $"Type: {_assetType}",
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                Margin = new Thickness(0, 0, 0, 15)
            };
            mainStack.Children.Add(_assetTypeText);

            // File Info Grid
            var fileInfoGrid = new Grid { Margin = new Thickness(0, 0, 0, 15) };
            fileInfoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            fileInfoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fileInfoGrid.RowDefinitions.Add(new RowDefinition());
            fileInfoGrid.RowDefinitions.Add(new RowDefinition());
            fileInfoGrid.RowDefinitions.Add(new RowDefinition());

            // File
            AddGridLabel(fileInfoGrid, "File:", 0);
            _filePathText = new TextBlock
            {
                Text = Path.GetFileName(_sourcePath),
                Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(_filePathText, 0);
            Grid.SetColumn(_filePathText, 1);
            fileInfoGrid.Children.Add(_filePathText);

            // Name
            AddGridLabel(fileInfoGrid, "Name:", 1);
            _assetNameBox = CreateTextBox(Path.GetFileNameWithoutExtension(_sourcePath));
            _assetNameBox.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(_assetNameBox, 1);
            Grid.SetColumn(_assetNameBox, 1);
            fileInfoGrid.Children.Add(_assetNameBox);

            // Target Folder
            AddGridLabel(fileInfoGrid, "Target Folder:", 2);
            var folderPanel = new Grid();
            folderPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            folderPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            
            // Pre-fill with the folder the user is browsing (so importing while inside Models/abc lands there), else
            // the asset type's default folder.
            _targetFolderBox = CreateTextBox(string.IsNullOrEmpty(_defaultFolder) ? GetDefaultFolder(_assetType) : _defaultFolder);
            folderPanel.Children.Add(_targetFolderBox);
            
            var browseBtn = CreateButton("...", 30);
            browseBtn.Click += BrowseFolder_Click;
            browseBtn.Margin = new Thickness(5, 0, 0, 0);
            Grid.SetColumn(browseBtn, 1);
            folderPanel.Children.Add(browseBtn);
            
            Grid.SetRow(folderPanel, 2);
            Grid.SetColumn(folderPanel, 1);
            fileInfoGrid.Children.Add(folderPanel);

            mainStack.Children.Add(fileInfoGrid);

            // Tags Section
            mainStack.Children.Add(new TextBlock
            {
                Text = "Tags",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 8)
            });

            // Predefined Tags
            var predefinedBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var predefinedStack = new StackPanel();
            predefinedStack.Children.Add(new TextBlock
            {
                Text = "Quick Tags",
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8)
            });
            _predefinedTagsPanel = new WrapPanel();
            predefinedStack.Children.Add(_predefinedTagsPanel);
            predefinedBorder.Child = predefinedStack;
            mainStack.Children.Add(predefinedBorder);

            // Selected Tags
            var selectedBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                MinHeight = 60,
                MaxHeight = 100
            };
            var selectedStack = new StackPanel();
            selectedStack.Children.Add(new TextBlock
            {
                Text = "Selected Tags",
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8)
            });
            _selectedTagsPanel = new WrapPanel();
            selectedStack.Children.Add(_selectedTagsPanel);
            _noTagsHint = new TextBlock
            {
                Text = "No tags selected. Click quick tags above or add custom tags below.",
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 5, 0, 0)
            };
            selectedStack.Children.Add(_noTagsHint);
            selectedBorder.Child = selectedStack;
            mainStack.Children.Add(selectedBorder);

            // Custom Tag Input
            var customTagPanel = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            customTagPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            customTagPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _customTagBox = CreateTextBox("");
            _customTagBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) AddCustomTagFromInput(); };
            customTagPanel.Children.Add(_customTagBox);
            var addTagBtn = CreateButton("Add Tag", 70);
            addTagBtn.Click += (s, e) => AddCustomTagFromInput();
            addTagBtn.Margin = new Thickness(5, 0, 0, 0);
            Grid.SetColumn(addTagBtn, 1);
            customTagPanel.Children.Add(addTagBtn);
            mainStack.Children.Add(customTagPanel);

            // Import Options
            var optionsBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 15, 0, 0)
            };
            var optionsStack = new StackPanel();
            optionsStack.Children.Add(new TextBlock
            {
                Text = "Import Options",
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 10)
            });
            _copyToProjectCheck = new CheckBox { Content = "Copy file to project folder", Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)), IsChecked = true, Margin = new Thickness(0, 0, 0, 5) };
            _generateMetaCheck = new CheckBox { Content = "Generate metadata file (.vmeta)", Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)), IsChecked = true, Margin = new Thickness(0, 0, 0, 5) };
            _autoDetectTexturesCheck = new CheckBox { Content = "Auto-detect related textures", Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)), IsChecked = true };
            _autoDetectTexturesCheck.Visibility = _assetType == ImportAssetType.Model ? Visibility.Visible : Visibility.Collapsed;
            optionsStack.Children.Add(_copyToProjectCheck);
            optionsStack.Children.Add(_generateMetaCheck);
            optionsStack.Children.Add(_autoDetectTexturesCheck);
            optionsBorder.Child = optionsStack;
            mainStack.Children.Add(optionsBorder);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            var cancelBtn = CreateButton("Cancel", 80);
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
            buttonPanel.Children.Add(cancelBtn);
            var importBtn = CreateButton("Import", 100);
            importBtn.Background = new SolidColorBrush(Color.FromRgb(108, 92, 231));
            importBtn.Margin = new Thickness(10, 0, 0, 0);
            importBtn.Click += Import_Click;
            buttonPanel.Children.Add(importBtn);
            mainStack.Children.Add(buttonPanel);

            Content = new ScrollViewer { Content = mainStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private void AddGridLabel(Grid grid, string text, int row)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);
        }

        private TextBox CreateTextBox(string text)
        {
            return new TextBox
            {
                Text = text,
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Padding = new Thickness(8, 6, 8, 6),
                CaretBrush = Brushes.White
            };
        }

        private Button CreateButton(string text, double width)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 6, 12, 6),
                Cursor = Cursors.Hand
            };
        }

        private string GetDefaultFolder(ImportAssetType type)
        {
            return type switch
            {
                ImportAssetType.Model => "Assets/Models",
                ImportAssetType.Texture => "Assets/Textures",
                ImportAssetType.Material => "Assets/Materials",
                ImportAssetType.Shader => "Assets/Shaders",
                ImportAssetType.Audio => "Assets/Audio",
                _ => "Assets"
            };
        }

        private void AutoSelectTagsForType(ImportAssetType type)
        {
            AddTag("Imported");

            var fileName = Path.GetFileNameWithoutExtension(_sourcePath).ToLowerInvariant();
            if (fileName.Contains("character") || fileName.Contains("player")) AddTag("Character");
            else if (fileName.Contains("skybox") || fileName.Contains("sky")) AddTag("Skybox");
            else if (fileName.Contains("ui")) AddTag("UI");
            else if (fileName.Contains("prop")) AddTag("Prop");
            else if (fileName.Contains("env") || fileName.Contains("terrain")) AddTag("Environment");
        }

        private void PopulatePredefinedTags()
        {
            _predefinedTagsPanel.Children.Clear();

            foreach (var tag in AssetTagService.PredefinedTags)
            {
                var button = new ToggleButton
                {
                    Content = tag,
                    Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                    Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(76, 76, 76)),
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(0, 0, 4, 4),
                    Cursor = Cursors.Hand
                };
                button.Checked += (s, e) => AddTag(tag);
                button.Unchecked += (s, e) => RemoveTag(tag);

                _predefinedTagButtons[tag] = button;
                _predefinedTagsPanel.Children.Add(button);
            }
        }

        private void AddTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return;
            tag = tag.Trim();

            if (_selectedTags.Add(tag))
            {
                if (_predefinedTagButtons.TryGetValue(tag, out var button))
                    button.IsChecked = true;
                UpdateSelectedTagsDisplay();
            }
        }

        private void RemoveTag(string tag)
        {
            if (_selectedTags.Remove(tag))
            {
                if (_predefinedTagButtons.TryGetValue(tag, out var button))
                    button.IsChecked = false;
                UpdateSelectedTagsDisplay();
            }
        }

        private void UpdateSelectedTagsDisplay()
        {
            _selectedTagsPanel.Children.Clear();

            foreach (var tag in _selectedTags.OrderBy(t => t))
            {
                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(108, 92, 231)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(8, 4, 4, 4),
                    Margin = new Thickness(0, 0, 5, 5)
                };

                var innerPanel = new StackPanel { Orientation = Orientation.Horizontal };
                innerPanel.Children.Add(new TextBlock
                {
                    Text = tag,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });

                var removeBtn = new Button
                {
                    Content = "�",
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(4, 0, 4, 0)
                };
                string tagCopy = tag;
                removeBtn.Click += (s, e) => RemoveTag(tagCopy);
                innerPanel.Children.Add(removeBtn);

                border.Child = innerPanel;
                _selectedTagsPanel.Children.Add(border);
            }

            _noTagsHint.Visibility = _selectedTags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddCustomTagFromInput()
        {
            var tag = _customTagBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(tag))
            {
                AddTag(tag);
                _customTagBox.Text = "";
                _customTagBox.Focus();
            }
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var projectPath = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(projectPath)) return;

            // Proper folder browser on an STA thread (a WPF file dialog on the live UI thread deadlocks the renderer).
            var selectedPath = Editor.Core.Util.FilePicker.PickFolder("Select the target folder inside the project", Path.Combine(projectPath, _targetFolderBox.Text));
            if (selectedPath != null && selectedPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
            {
                _targetFolderBox.Text = selectedPath.Substring(projectPath.Length).TrimStart('\\', '/');
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var projectPath = ProjectData.Current?.Path;
                if (string.IsNullOrEmpty(projectPath))
                {
                    MessageBox.Show("No project is open.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var assetName = _assetNameBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(assetName))
                {
                    MessageBox.Show("Please enter an asset name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var targetFolder = Path.Combine(projectPath, _targetFolderBox.Text);
                if (!Directory.Exists(targetFolder))
                    Directory.CreateDirectory(targetFolder);

                var extension = Path.GetExtension(_sourcePath);
                var targetPath = Path.Combine(targetFolder, assetName + extension);

                if (_copyToProjectCheck.IsChecked == true)
                {
                    if (File.Exists(targetPath))
                    {
                        var overwrite = MessageBox.Show($"File '{assetName}{extension}' already exists. Overwrite?",
                            "File Exists", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (overwrite != MessageBoxResult.Yes) return;
                    }
                    File.Copy(_sourcePath, targetPath, true);
                }
                else
                {
                    targetPath = _sourcePath;
                }

                Guid assetGuid = Guid.NewGuid();
                if (_generateMetaCheck.IsChecked == true)
                {
                    var relativePath = targetPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase)
                        ? targetPath.Substring(projectPath.Length).TrimStart('\\', '/')
                        : targetPath;

                    var assetType = ConvertToAssetType(_assetType);
                    var metadata = new AssetMetadata(assetType, relativePath, assetName + extension);
                    assetGuid = metadata.Guid;
                    AssetDatabase.Instance.SaveMetadata(metadata, targetPath + AssetDatabase.MetaFileExtension);
                }

                foreach (var tag in _selectedTags)
                    AssetTagService.Instance.AddTag(assetGuid, tag);

                Result = new AssetImportResult
                {
                    Success = true,
                    AssetGuid = assetGuid,
                    AssetName = assetName,
                    TargetPath = targetPath,
                    SourcePath = _sourcePath,
                    Type = _assetType,
                    Tags = _selectedTags.ToList(),
                    AutoDetectTextures = _autoDetectTexturesCheck.IsChecked == true
                };

                AssetDatabase.Instance.Refresh();
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private AssetType ConvertToAssetType(ImportAssetType type)
        {
            return type switch
            {
                ImportAssetType.Model => AssetType.Mesh,
                ImportAssetType.Texture => AssetType.Texture,
                ImportAssetType.Material => AssetType.Material,
                ImportAssetType.Shader => AssetType.Shader,
                ImportAssetType.Audio => AssetType.Audio,
                _ => AssetType.Unknown
            };
        }

        public static AssetImportResult ShowImportDialog(Window owner, string filePath, ImportAssetType type, string defaultFolder = null)
        {
            var dialog = new AssetImportDialog(filePath, type, defaultFolder) { Owner = owner };
            if (dialog.ShowDialog() == true)
                return dialog.Result;
            return new AssetImportResult { Success = false };
        }
    }

    public class AssetImportResult
    {
        public bool Success { get; set; }
        public Guid AssetGuid { get; set; }
        public string AssetName { get; set; }
        public string TargetPath { get; set; }
        public string SourcePath { get; set; }
        public AssetImportDialog.ImportAssetType Type { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public bool AutoDetectTextures { get; set; }
    }
}
