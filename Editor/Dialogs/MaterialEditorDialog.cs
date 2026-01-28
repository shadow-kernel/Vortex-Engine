using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.DllWrapper;

namespace Editor.Dialogs
{
    /// <summary>
    /// Material Editor Dialog for creating and editing PBR materials.
    /// Built programmatically to avoid XAML linking issues.
    /// </summary>
    public class MaterialEditorDialog : Window
    {
        private VortexMaterial _material;
        private string _materialPath;
        private bool _isDirty;

        // UI Elements
        private TextBox _materialNameBox;
        private ComboBox _shaderTypeCombo;
        private ComboBox _renderModeCombo;
        private Border _baseColorPreview;
        private Slider _metallicSlider, _roughnessSlider, _normalStrengthSlider, _aoSlider;
        private TextBlock _metallicValue, _roughnessValue, _normalStrengthValue, _aoValue;
        private CheckBox _twoSidedCheck, _receiveShadowsCheck, _castShadowsCheck;
        private TextBlock _statusText;

        // Texture paths and previews
        private string _albedoPath, _normalPath, _metallicPath, _roughnessPath, _aoPath;
        private Border _albedoPreview, _normalPreview, _metallicPreviewBorder, _roughnessPreview, _aoPreview;
        private TextBlock _albedoPathText, _normalPathText, _metallicPathText, _roughnessPathText, _aoPathText;

        public MaterialEditorDialog()
        {
            _material = new VortexMaterial();
            InitializeWindow();
            BuildUI();
        }

        public MaterialEditorDialog(string materialPath) : this()
        {
            _materialPath = materialPath;
            Title = $"Material Editor - {Path.GetFileName(materialPath)}";
            if (File.Exists(materialPath))
                LoadMaterial(materialPath);
        }

        private void InitializeWindow()
        {
            Title = "Material Editor";
            Width = 900;
            Height = 700;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 700;
            MinHeight = 500;
        }

        private void BuildUI()
        {
            var mainGrid = new Grid();
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left Panel - Properties
            var leftPanel = BuildPropertiesPanel();
            Grid.SetColumn(leftPanel, 0);
            mainGrid.Children.Add(leftPanel);

            // Right Panel - Textures
            var rightPanel = BuildTexturesPanel();
            Grid.SetColumn(rightPanel, 1);
            mainGrid.Children.Add(rightPanel);

            Content = mainGrid;
        }

        private Border BuildPropertiesPanel()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(0, 0, 1, 0)
            };

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel { Margin = new Thickness(15) };

            // Header
            stack.Children.Add(new TextBlock
            {
                Text = "Material Properties",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 15)
            });

            // Name
            stack.Children.Add(CreateLabel("Name"));
            _materialNameBox = CreateTextBox(_material.Name);
            _materialNameBox.TextChanged += (s, e) => MarkDirty();
            stack.Children.Add(_materialNameBox);

            // Shader Type
            stack.Children.Add(CreateLabel("Shader", 15));
            _shaderTypeCombo = DialogStyles.CreateComboBox(new[] { "Standard PBR", "Unlit", "Transparent" }, 0);
            _shaderTypeCombo.SelectionChanged += (s, e) => MarkDirty();
            stack.Children.Add(_shaderTypeCombo);

            // Render Mode
            stack.Children.Add(CreateLabel("Render Mode"));
            _renderModeCombo = DialogStyles.CreateComboBox(new[] { "Opaque", "Cutout", "Transparent" }, 0);
            stack.Children.Add(_renderModeCombo);

            // Separator
            stack.Children.Add(new Separator { Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)), Margin = new Thickness(0, 5, 0, 15) });

            // Base Color
            stack.Children.Add(CreateLabel("Base Color"));
            var colorPanel = new Grid { Margin = new Thickness(0, 0, 0, 15) };
            colorPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            colorPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _baseColorPreview = new Border
            {
                Background = Brushes.White,
                Height = 28,
                CornerRadius = new CornerRadius(4),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            _baseColorPreview.MouseLeftButtonUp += PickBaseColor_Click;
            colorPanel.Children.Add(_baseColorPreview);
            var pickBtn = CreateButton("Pick", 50);
            pickBtn.Click += PickBaseColor_Click;
            pickBtn.Margin = new Thickness(5, 0, 0, 0);
            Grid.SetColumn(pickBtn, 1);
            colorPanel.Children.Add(pickBtn);
            stack.Children.Add(colorPanel);

            // PBR Properties
            stack.Children.Add(new TextBlock
            {
                Text = "PBR Properties",
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                Margin = new Thickness(0, 0, 0, 10)
            });

            (_metallicSlider, _metallicValue) = CreateSliderRow("Metallic", 0, 1, _material.Metallic);
            stack.Children.Add(_metallicSlider.Parent as UIElement);

            (_roughnessSlider, _roughnessValue) = CreateSliderRow("Roughness", 0, 1, _material.Roughness);
            stack.Children.Add(_roughnessSlider.Parent as UIElement);

            (_normalStrengthSlider, _normalStrengthValue) = CreateSliderRow("Normal Strength", 0, 2, _material.NormalStrength);
            stack.Children.Add(_normalStrengthSlider.Parent as UIElement);

            (_aoSlider, _aoValue) = CreateSliderRow("Ambient Occlusion", 0, 1, _material.AmbientOcclusion);
            stack.Children.Add(_aoSlider.Parent as UIElement);

            // Separator
            stack.Children.Add(new Separator { Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)), Margin = new Thickness(0, 15, 0, 15) });

            // Checkboxes
            _twoSidedCheck = new CheckBox { Content = "Two Sided", Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)), Margin = new Thickness(0, 0, 0, 5) };
            _receiveShadowsCheck = new CheckBox { Content = "Receive Shadows", Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)), IsChecked = true, Margin = new Thickness(0, 0, 0, 5) };
            _castShadowsCheck = new CheckBox { Content = "Cast Shadows", Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)), IsChecked = true };
            stack.Children.Add(_twoSidedCheck);
            stack.Children.Add(_receiveShadowsCheck);
            stack.Children.Add(_castShadowsCheck);

            scroll.Content = stack;
            border.Child = scroll;
            return border;
        }

        private Grid BuildTexturesPanel()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(15) };
            var stack = new StackPanel();

            stack.Children.Add(new TextBlock
            {
                Text = "Texture Maps",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 15)
            });

            var wrapPanel = new WrapPanel();

            // Create texture slots
            (_albedoPreview, _albedoPathText) = CreateTextureSlot("Albedo (Base Color)", wrapPanel, 
                path => { _albedoPath = path; MarkDirty(); }, 
                () => BrowseTexture("Albedo"));
            
            (_normalPreview, _normalPathText) = CreateTextureSlot("Normal Map", wrapPanel,
                path => { _normalPath = path; MarkDirty(); },
                () => BrowseTexture("Normal"));
            
            (_metallicPreviewBorder, _metallicPathText) = CreateTextureSlot("Metallic Map", wrapPanel,
                path => { _metallicPath = path; MarkDirty(); },
                () => BrowseTexture("Metallic"));
            
            (_roughnessPreview, _roughnessPathText) = CreateTextureSlot("Roughness Map", wrapPanel,
                path => { _roughnessPath = path; MarkDirty(); },
                () => BrowseTexture("Roughness"));
            
            (_aoPreview, _aoPathText) = CreateTextureSlot("Ambient Occlusion", wrapPanel,
                path => { _aoPath = path; MarkDirty(); },
                () => BrowseTexture("AO"));

            stack.Children.Add(wrapPanel);
            scroll.Content = stack;
            grid.Children.Add(scroll);

            // Footer
            var footer = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(15, 8, 15, 8)
            };
            Grid.SetRow(footer, 1);

            var footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _statusText = new TextBlock { Text = "Ready", Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)), VerticalAlignment = VerticalAlignment.Center };
            footerGrid.Children.Add(_statusText);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var applyBtn = CreateButton("Apply", 80);
            applyBtn.Click += Apply_Click;
            buttonPanel.Children.Add(applyBtn);
            var saveAsBtn = CreateButton("Save As...", 80);
            saveAsBtn.Margin = new Thickness(10, 0, 0, 0);
            saveAsBtn.Click += SaveAs_Click;
            buttonPanel.Children.Add(saveAsBtn);
            var closeBtn = CreateButton("Close", 80);
            closeBtn.Margin = new Thickness(10, 0, 0, 0);
            closeBtn.Click += (s, e) => Close();
            buttonPanel.Children.Add(closeBtn);
            Grid.SetColumn(buttonPanel, 1);
            footerGrid.Children.Add(buttonPanel);

            footer.Child = footerGrid;
            grid.Children.Add(footer);

            return grid;
        }

        private (Border preview, TextBlock pathText) CreateTextureSlot(string title, WrapPanel parent, Action<string> onPathChanged, Func<string> browseAction)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 10, 10),
                Width = 180,
                Padding = new Thickness(10)
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var preview = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 26, 26)),
                Height = 120,
                CornerRadius = new CornerRadius(4),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1),
                AllowDrop = true
            };
            preview.Child = new TextBlock
            {
                Text = "Drop Texture",
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(preview);

            var pathText = new TextBlock
            {
                Text = "None",
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                FontSize = 10,
                Margin = new Thickness(0, 5, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            stack.Children.Add(pathText);

            // Set up drag-drop after pathText is declared
            preview.DragOver += (s, e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            preview.Drop += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files?.Length > 0)
                    {
                        SetTexturePreview(files[0], preview, pathText);
                        onPathChanged(files[0]);
                    }
                }
            };

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
            var browseBtn = CreateButton("Browse", 60);
            browseBtn.FontSize = 11;
            browseBtn.Padding = new Thickness(8, 4, 8, 4);
            browseBtn.Click += (s, e) =>
            {
                var path = browseAction();
                if (path != null)
                {
                    SetTexturePreview(path, preview, pathText);
                    onPathChanged(path);
                }
            };
            buttonPanel.Children.Add(browseBtn);
            var clearBtn = CreateButton("Clear", 50);
            clearBtn.FontSize = 11;
            clearBtn.Padding = new Thickness(8, 4, 8, 4);
            clearBtn.Margin = new Thickness(5, 0, 0, 0);
            clearBtn.Click += (s, e) =>
            {
                SetTexturePreview(null, preview, pathText);
                onPathChanged(null);
            };
            buttonPanel.Children.Add(clearBtn);
            stack.Children.Add(buttonPanel);

            border.Child = stack;
            parent.Children.Add(border);

            return (preview, pathText);
        }

        private void SetTexturePreview(string path, Border preview, TextBlock pathText)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(path);
                    bitmap.DecodePixelWidth = 120;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    preview.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                    preview.Child = null;
                    pathText.Text = Path.GetFileName(path);
                }
                catch
                {
                    preview.Background = new SolidColorBrush(Color.FromRgb(26, 26, 26));
                    pathText.Text = "Error";
                }
            }
            else
            {
                preview.Background = new SolidColorBrush(Color.FromRgb(26, 26, 26));
                preview.Child = new TextBlock
                {
                    Text = "Drop Texture",
                    Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                pathText.Text = "None";
            }
        }

        private string BrowseTexture(string type)
        {
            var dialog = new OpenFileDialog
            {
                Title = $"Select {type} Texture",
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.tga;*.bmp;*.dds;*.hdr|All Files|*.*"
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        private (Slider slider, TextBlock valueText) CreateSliderRow(string label, double min, double max, double value)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                FontSize = 11
            });
            var slider = new Slider { Minimum = min, Maximum = max, Value = value };
            stack.Children.Add(slider);
            grid.Children.Add(stack);

            var valueText = new TextBlock
            {
                Text = value.ToString("F2"),
                Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                VerticalAlignment = VerticalAlignment.Bottom,
                TextAlignment = TextAlignment.Right
            };
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(valueText);

            slider.ValueChanged += (s, e) =>
            {
                valueText.Text = e.NewValue.ToString("F2");
                MarkDirty();
            };

            return (slider, valueText);
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

        private TextBox CreateTextBox(string text)
        {
            return new TextBox
            {
                Text = text,
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Padding = new Thickness(8, 6, 8, 6),
                CaretBrush = Brushes.White,
                Margin = new Thickness(0, 0, 0, 15)
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

        private void MarkDirty()
        {
            _isDirty = true;
            if (!Title.EndsWith("*")) Title += "*";
        }

        private void LoadMaterial(string path)
        {
            try
            {
                _material = VortexMaterial.Load(path);
                if (_material != null)
                {
                    var directory = Path.GetDirectoryName(path);
                    _material.ResolvePathsAbsolute(directory);
                    LoadMaterialToUI();
                    _statusText.Text = $"Loaded: {Path.GetFileName(path)}";
                }
            }
            catch (Exception ex)
            {
                _statusText.Text = $"Error: {ex.Message}";
            }
        }

        private void LoadMaterialToUI()
        {
            _materialNameBox.Text = _material.Name;
            _baseColorPreview.Background = new SolidColorBrush(_material.GetBaseColor());
            _metallicSlider.Value = _material.Metallic;
            _roughnessSlider.Value = _material.Roughness;
            _normalStrengthSlider.Value = _material.NormalStrength;
            _aoSlider.Value = _material.AmbientOcclusion;
            _twoSidedCheck.IsChecked = _material.TwoSided;
            _castShadowsCheck.IsChecked = _material.CastShadows;
            _receiveShadowsCheck.IsChecked = _material.ReceiveShadows;

            SetTexturePreview(_material.AlbedoTexture, _albedoPreview, _albedoPathText);
            _albedoPath = _material.AlbedoTexture;
            SetTexturePreview(_material.NormalTexture, _normalPreview, _normalPathText);
            _normalPath = _material.NormalTexture;
            SetTexturePreview(_material.MetallicTexture, _metallicPreviewBorder, _metallicPathText);
            _metallicPath = _material.MetallicTexture;
            SetTexturePreview(_material.RoughnessTexture, _roughnessPreview, _roughnessPathText);
            _roughnessPath = _material.RoughnessTexture;
            SetTexturePreview(_material.AOTexture, _aoPreview, _aoPathText);
            _aoPath = _material.AOTexture;

            _isDirty = false;
        }

        private VortexMaterial GetMaterialFromUI()
        {
            var mat = new VortexMaterial
            {
                Name = _materialNameBox.Text,
                Metallic = (float)_metallicSlider.Value,
                Roughness = (float)_roughnessSlider.Value,
                NormalStrength = (float)_normalStrengthSlider.Value,
                AmbientOcclusion = (float)_aoSlider.Value,
                AlbedoTexture = _albedoPath,
                NormalTexture = _normalPath,
                MetallicTexture = _metallicPath,
                RoughnessTexture = _roughnessPath,
                AOTexture = _aoPath,
                TwoSided = _twoSidedCheck.IsChecked == true,
                CastShadows = _castShadowsCheck.IsChecked == true,
                ReceiveShadows = _receiveShadowsCheck.IsChecked == true,
                BlendMode = _shaderTypeCombo.SelectedItem?.ToString() ?? "Opaque"
            };

            if (_baseColorPreview.Background is SolidColorBrush brush)
                mat.SetBaseColor(brush.Color);

            return mat;
        }

        private void PickBaseColor_Click(object sender, RoutedEventArgs e)
        {
            var currentColor = Colors.White;
            if (_baseColorPreview.Background is SolidColorBrush brush)
                currentColor = brush.Color;

            var colorPicker = new ColorPickerDialog(currentColor) { Owner = this };
            if (colorPicker.ShowDialog() == true)
            {
                _baseColorPreview.Background = new SolidColorBrush(colorPicker.SelectedColor);
                MarkDirty();
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            _material = GetMaterialFromUI();
            if (!string.IsNullOrEmpty(_materialPath))
            {
                try
                {
                    var directory = Path.GetDirectoryName(_materialPath);
                    _material.ResolvePathsRelative(directory);
                    _material.Save(_materialPath);
                    _isDirty = false;
                    Title = $"Material Editor - {_material.Name}";
                    _statusText.Text = "Material saved.";
                }
                catch (Exception ex)
                {
                    _statusText.Text = $"Error: {ex.Message}";
                }
            }
            else
            {
                SaveAs_Click(sender, e);
            }
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save Material",
                Filter = "Vortex Material|*.vmat",
                DefaultExt = ".vmat",
                FileName = _materialNameBox.Text + ".vmat"
            };

            if (dialog.ShowDialog() == true)
            {
                _materialPath = dialog.FileName;
                _material = GetMaterialFromUI();
                try
                {
                    var directory = Path.GetDirectoryName(_materialPath);
                    _material.ResolvePathsRelative(directory);
                    _material.Save(_materialPath);
                    _isDirty = false;
                    Title = $"Material Editor - {_material.Name}";
                    _statusText.Text = $"Saved: {Path.GetFileName(_materialPath)}";
                    AssetDatabase.Instance.Refresh();
                }
                catch (Exception ex)
                {
                    _statusText.Text = $"Error: {ex.Message}";
                }
            }
        }

        public static void OpenMaterial(Window owner, string materialPath)
        {
            var dialog = new MaterialEditorDialog(materialPath) { Owner = owner };
            dialog.ShowDialog();
        }

        public static VortexMaterial CreateNewMaterial(Window owner)
        {
            var dialog = new MaterialEditorDialog { Owner = owner };
            dialog.ShowDialog();
            return dialog._material;
        }
    }
}
