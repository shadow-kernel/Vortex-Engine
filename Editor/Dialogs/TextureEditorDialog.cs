using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.Core.Assets;
using Editor.Core.Data;

namespace Editor.Dialogs
{
    /// <summary>
    /// Texture Editor Dialog for viewing and configuring texture import settings.
    /// Similar to Unity's Texture Import Settings.
    /// </summary>
    public class TextureEditorDialog : Window
    {
        private string _texturePath;
        private Guid _assetGuid;
        private BitmapSource _originalImage;
        private double _zoomLevel = 1.0;
        private bool _isDirty;

        // UI Elements
        private Image _texturePreviewImage;
        private Viewbox _imageViewbox;
        private TextBlock _zoomText;
        private ToggleButton _showRGB, _showR, _showG, _showB, _showA;
        private TextBlock _sizeText, _formatText, _mipmapText, _fileSizeText, _pathText;
        private ComboBox _textureTypeCombo, _wrapModeCombo, _filterModeCombo, _anisoLevelCombo, _compressionCombo;
        private StackPanel _normalMapSettings;
        private CheckBox _sRGBCheck, _generateMipmapsCheck;
        private WrapPanel _tagsPanel;
        private TextBlock _statusText;

        public TextureEditorDialog(string texturePath, Guid assetGuid = default)
        {
            _texturePath = texturePath;
            _assetGuid = assetGuid;

            Title = $"Texture Editor - {Path.GetFileName(texturePath)}";
            Width = 1000;
            Height = 700;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 750;
            MinHeight = 550;

            BuildUI();
            Loaded += (s, e) =>
            {
                if (File.Exists(_texturePath))
                {
                    LoadTexture(_texturePath);
                    LoadTags();
                }
            };
        }

        private void BuildUI()
        {
            var mainGrid = new Grid();
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Left - Preview
            var previewPanel = BuildPreviewPanel();
            mainGrid.Children.Add(previewPanel);

            // Right - Properties
            var propertiesPanel = BuildPropertiesPanel();
            Grid.SetColumn(propertiesPanel, 1);
            mainGrid.Children.Add(propertiesPanel);

            // Bottom - Status Bar
            var statusBar = BuildStatusBar();
            Grid.SetRow(statusBar, 1);
            Grid.SetColumnSpan(statusBar, 2);
            mainGrid.Children.Add(statusBar);

            Content = mainGrid;
        }

        private Border BuildPreviewPanel()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(13, 13, 13)),
                Margin = new Thickness(15)
            };

            var grid = new Grid();

            // Checkerboard background
            grid.Background = CreateCheckerboardBrush();

            // Image - Use Viewbox directly for auto-scaling without scrollbars
            _imageViewbox = new Viewbox 
            { 
                Stretch = Stretch.Uniform, 
                HorizontalAlignment = HorizontalAlignment.Center, 
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20)
            };
            _texturePreviewImage = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(_texturePreviewImage, BitmapScalingMode.HighQuality);
            _imageViewbox.Child = _texturePreviewImage;
            grid.Children.Add(_imageViewbox);

            // Zoom controls
            var zoomPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
                Margin = new Thickness(10)
            };
            var zoomOutBtn = CreateButton("-", 30);
            zoomOutBtn.Click += ZoomOut_Click;
            zoomPanel.Children.Add(zoomOutBtn);
            _zoomText = new TextBlock { Text = "100%", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            zoomPanel.Children.Add(_zoomText);
            var zoomInBtn = CreateButton("+", 30);
            zoomInBtn.Click += ZoomIn_Click;
            zoomPanel.Children.Add(zoomInBtn);
            var fitBtn = CreateButton("Fit", 40);
            fitBtn.Margin = new Thickness(10, 0, 0, 0);
            fitBtn.Click += ZoomFit_Click;
            zoomPanel.Children.Add(fitBtn);
            grid.Children.Add(zoomPanel);

            // Channel buttons
            var channelPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
                Margin = new Thickness(10)
            };
            _showRGB = CreateChannelButton("RGB", Brushes.White, true);
            _showR = CreateChannelButton("R", new SolidColorBrush(Color.FromRgb(255, 85, 85)), false);
            _showG = CreateChannelButton("G", new SolidColorBrush(Color.FromRgb(85, 255, 85)), false);
            _showB = CreateChannelButton("B", new SolidColorBrush(Color.FromRgb(85, 85, 255)), false);
            _showA = CreateChannelButton("A", Brushes.White, false);
            channelPanel.Children.Add(_showRGB);
            channelPanel.Children.Add(_showR);
            channelPanel.Children.Add(_showG);
            channelPanel.Children.Add(_showB);
            channelPanel.Children.Add(_showA);
            grid.Children.Add(channelPanel);

            border.Child = grid;
            return border;
        }

        private ToggleButton CreateChannelButton(string content, Brush foreground, bool isChecked)
        {
            var btn = new ToggleButton
            {
                Content = content,
                Foreground = foreground,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 2, 0),
                IsChecked = isChecked
            };
            btn.Click += ChannelToggle_Click;
            return btn;
        }

        private Brush CreateCheckerboardBrush()
        {
            var drawingBrush = new DrawingBrush
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 16, 16),
                ViewportUnits = BrushMappingMode.Absolute
            };

            var drawingGroup = new DrawingGroup();
            drawingGroup.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(26, 26, 26)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            var geometryGroup = new GeometryGroup();
            geometryGroup.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8)));
            geometryGroup.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
            drawingGroup.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(37, 37, 37)), null, geometryGroup));
            drawingBrush.Drawing = drawingGroup;

            return drawingBrush;
        }

        private Border BuildPropertiesPanel()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1, 0, 0, 0)
            };

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel { Margin = new Thickness(15) };

            stack.Children.Add(new TextBlock { Text = "Texture Properties", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 15) });

            // File Information
            var infoPanel = BuildInfoSection();
            stack.Children.Add(infoPanel);

            // Texture Type
            stack.Children.Add(CreateLabel("Texture Type", 15));
            _textureTypeCombo = CreateComboBox(new[] { "Default", "Normal Map", "Sprite (UI)", "Cursor", "Lightmap", "Single Channel" });
            _textureTypeCombo.SelectionChanged += TextureType_Changed;
            stack.Children.Add(_textureTypeCombo);

            // Normal Map Settings (hidden by default)
            _normalMapSettings = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 15) };
            _normalMapSettings.Children.Add(CreateLabel("Normal Map Format"));
            var radioPanel = new StackPanel { Orientation = Orientation.Horizontal };
            radioPanel.Children.Add(new RadioButton { Content = "DirectX", Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)), IsChecked = true, Margin = new Thickness(0, 0, 15, 0) });
            radioPanel.Children.Add(new RadioButton { Content = "OpenGL", Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)) });
            _normalMapSettings.Children.Add(radioPanel);
            stack.Children.Add(_normalMapSettings);

            stack.Children.Add(new Separator { Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)), Margin = new Thickness(0, 0, 0, 15) });

            // Import Settings
            stack.Children.Add(CreateSectionHeader("IMPORT SETTINGS"));
            _sRGBCheck = new CheckBox { Content = "sRGB (Color Texture)", Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)), IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
            _generateMipmapsCheck = new CheckBox { Content = "Generate Mipmaps", Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)), IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
            stack.Children.Add(_sRGBCheck);
            stack.Children.Add(_generateMipmapsCheck);

            stack.Children.Add(new Separator { Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)), Margin = new Thickness(0, 10, 0, 15) });

            // Wrap Mode
            stack.Children.Add(CreateLabel("Wrap Mode"));
            _wrapModeCombo = CreateComboBox(new[] { "Repeat", "Clamp", "Mirror", "Mirror Once" });
            stack.Children.Add(_wrapModeCombo);

            // Filter Mode
            stack.Children.Add(CreateLabel("Filter Mode", 10));
            _filterModeCombo = CreateComboBox(new[] { "Point (No Filter)", "Bilinear", "Trilinear" });
            _filterModeCombo.SelectedIndex = 1;
            stack.Children.Add(_filterModeCombo);

            // Aniso Level
            stack.Children.Add(CreateLabel("Anisotropic Filtering", 10));
            _anisoLevelCombo = CreateComboBox(new[] { "Disabled", "2x", "4x", "8x", "16x" });
            _anisoLevelCombo.SelectedIndex = 1;
            stack.Children.Add(_anisoLevelCombo);

            stack.Children.Add(new Separator { Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)), Margin = new Thickness(0, 15, 0, 15) });

            // Compression
            stack.Children.Add(CreateSectionHeader("COMPRESSION"));
            stack.Children.Add(CreateLabel("Compression Quality"));
            _compressionCombo = CreateComboBox(new[] { "None", "Low Quality", "Normal Quality", "High Quality" });
            _compressionCombo.SelectedIndex = 2;
            stack.Children.Add(_compressionCombo);

            stack.Children.Add(new Separator { Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)), Margin = new Thickness(0, 15, 0, 15) });

            // Tags
            stack.Children.Add(CreateSectionHeader("TAGS"));
            _tagsPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            stack.Children.Add(_tagsPanel);
            var editTagsBtn = CreateButton("Edit Tags...", 100);
            editTagsBtn.HorizontalAlignment = HorizontalAlignment.Left;
            editTagsBtn.Click += EditTags_Click;
            stack.Children.Add(editTagsBtn);

            scroll.Content = stack;
            border.Child = scroll;
            return border;
        }

        private Border BuildInfoSection()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 15)
            };

            var stack = new StackPanel();
            stack.Children.Add(CreateSectionHeader("FILE INFORMATION"));

            var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition());

            AddInfoRow(grid, 0, "Size:", out _sizeText, "1024 x 1024");
            AddInfoRow(grid, 1, "Format:", out _formatText, "RGBA8");
            AddInfoRow(grid, 2, "Mipmaps:", out _mipmapText, "10 levels");
            AddInfoRow(grid, 3, "File Size:", out _fileSizeText, "4.0 MB");
            AddInfoRow(grid, 4, "Path:", out _pathText, "Assets/Textures/...");

            stack.Children.Add(grid);
            border.Child = stack;
            return border;
        }

        private void AddInfoRow(Grid grid, int row, string label, out TextBlock valueText, string defaultValue)
        {
            var labelBlock = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)) };
            Grid.SetRow(labelBlock, row);
            grid.Children.Add(labelBlock);

            valueText = new TextBlock { Text = defaultValue, Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(valueText, row);
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(valueText);
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

            _statusText = new TextBlock { Text = "Ready", Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)), VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(_statusText);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var applyBtn = CreateButton("Apply", 80);
            applyBtn.Click += Apply_Click;
            buttonPanel.Children.Add(applyBtn);
            var revertBtn = CreateButton("Revert", 80);
            revertBtn.Margin = new Thickness(10, 0, 0, 0);
            revertBtn.Click += Revert_Click;
            buttonPanel.Children.Add(revertBtn);
            var closeBtn = CreateButton("Close", 80);
            closeBtn.Margin = new Thickness(10, 0, 0, 0);
            closeBtn.Click += (s, e) => Close();
            buttonPanel.Children.Add(closeBtn);
            Grid.SetColumn(buttonPanel, 1);
            grid.Children.Add(buttonPanel);

            border.Child = grid;
            return border;
        }

        private TextBlock CreateLabel(string text, double topMargin = 0)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                FontSize = 11,
                Margin = new Thickness(0, topMargin, 0, 4)
            };
        }

        private TextBlock CreateSectionHeader(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
        }

        private ComboBox CreateComboBox(string[] items)
        {
            return DialogStyles.CreateComboBox(items, 0);
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

        private void LoadTexture(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                _originalImage = bitmap;
                _texturePreviewImage.Source = bitmap;

                _sizeText.Text = $"{bitmap.PixelWidth} x {bitmap.PixelHeight}";
                _formatText.Text = bitmap.Format.ToString();

                int maxDim = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
                int mipmapCount = (int)Math.Floor(Math.Log(maxDim, 2)) + 1;
                _mipmapText.Text = $"{mipmapCount} levels";

                var fileInfo = new FileInfo(path);
                _fileSizeText.Text = FormatFileSize(fileInfo.Length);

                var projectPath = ProjectData.Current?.Path ?? "";
                _pathText.Text = path.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase)
                    ? path.Substring(projectPath.Length).TrimStart('\\', '/')
                    : path;

                AutoDetectTextureType(Path.GetFileName(path));
                _statusText.Text = $"Loaded: {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                _statusText.Text = $"Error: {ex.Message}";
            }
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F2} KB";
            return $"{bytes} bytes";
        }

        private void AutoDetectTextureType(string fileName)
        {
            var lowerName = fileName.ToLowerInvariant();
            if (lowerName.Contains("normal") || lowerName.Contains("_n."))
            {
                _textureTypeCombo.SelectedIndex = 1;
                _sRGBCheck.IsChecked = false;
            }
            else if (lowerName.Contains("_ao") || lowerName.Contains("roughness") || lowerName.Contains("metallic"))
            {
                _textureTypeCombo.SelectedIndex = 5;
                _sRGBCheck.IsChecked = false;
            }
            else if (lowerName.Contains("sprite") || lowerName.Contains("ui"))
            {
                _textureTypeCombo.SelectedIndex = 2;
            }
        }

        private void LoadTags()
        {
            _tagsPanel.Children.Clear();
            if (_assetGuid != Guid.Empty)
            {
                foreach (var tag in AssetTagService.Instance.GetTags(_assetGuid))
                {
                    var tagBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(8, 4, 8, 4),
                        Margin = new Thickness(0, 0, 5, 5)
                    };
                    tagBorder.Child = new TextBlock { Text = tag, Foreground = Brushes.White, FontSize = 11 };
                    _tagsPanel.Children.Add(tagBorder);
                }
            }
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _zoomLevel = Math.Min(_zoomLevel * 1.25, 8.0);
            UpdateZoom();
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _zoomLevel = Math.Max(_zoomLevel / 1.25, 0.1);
            UpdateZoom();
        }

        private void ZoomFit_Click(object sender, RoutedEventArgs e)
        {
            _zoomLevel = 1.0;
            _imageViewbox.Stretch = Stretch.Uniform;
            _imageViewbox.Width = double.NaN;
            _imageViewbox.Height = double.NaN;
            _zoomText.Text = "Fit";
        }

        private void UpdateZoom()
        {
            if (_originalImage == null) return;
            _imageViewbox.Width = _originalImage.PixelWidth * _zoomLevel;
            _imageViewbox.Height = _originalImage.PixelHeight * _zoomLevel;
            _imageViewbox.Stretch = Stretch.Fill;
            _zoomText.Text = $"{_zoomLevel * 100:F0}%";
        }

        private void ChannelToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender == _showRGB && _showRGB.IsChecked == true)
            {
                _showR.IsChecked = _showG.IsChecked = _showB.IsChecked = _showA.IsChecked = false;
            }
            else if (sender != _showRGB && ((ToggleButton)sender).IsChecked == true)
            {
                _showRGB.IsChecked = false;
            }
            else if (!(_showRGB.IsChecked == true || _showR.IsChecked == true || _showG.IsChecked == true || _showB.IsChecked == true || _showA.IsChecked == true))
            {
                _showRGB.IsChecked = true;
            }
        }

        private void TextureType_Changed(object sender, SelectionChangedEventArgs e)
        {
            _normalMapSettings.Visibility = _textureTypeCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            _sRGBCheck.IsChecked = _textureTypeCombo.SelectedIndex != 1 && _textureTypeCombo.SelectedIndex != 5;
            _isDirty = true;
        }

        private void EditTags_Click(object sender, RoutedEventArgs e)
        {
            if (_assetGuid == Guid.Empty)
                _assetGuid = Guid.NewGuid();

            var dialog = new AssetTagEditorDialog(_assetGuid, Path.GetFileName(_texturePath)) { Owner = this };
            if (dialog.ShowDialog() == true)
                LoadTags();
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            _isDirty = false;
            _statusText.Text = "Settings applied.";
        }

        private void Revert_Click(object sender, RoutedEventArgs e)
        {
            LoadTexture(_texturePath);
            LoadTags();
            _isDirty = false;
            _statusText.Text = "Settings reverted.";
        }

        public static void OpenTexture(Window owner, string texturePath, Guid assetGuid = default)
        {
            try
            {
                var dialog = new TextureEditorDialog(texturePath, assetGuid);
                if (owner != null)
                    dialog.Owner = owner;
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open texture editor:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
