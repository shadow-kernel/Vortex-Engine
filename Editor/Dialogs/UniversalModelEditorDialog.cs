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
using Microsoft.Win32;

namespace Editor.Dialogs
{
    /// <summary>
    /// Universal Model Editor Dialog.
    /// Supports all 3D formats with clean submesh and material management.
    /// </summary>
    public class UniversalModelEditorDialog : Window
    {
        private readonly UniversalModelData _modelData;
        private UniversalMaterial _selectedMaterial;
        private SubmeshData _selectedSubmesh;

        // UI Elements
        private ListBox _submeshList;
        private ListBox _materialList;
        private StackPanel _propertiesPanel;
        private WrapPanel _textureLibraryPanel;
        private TextBlock _statusText;
        private TextBlock _statsText;
        private TabControl _rightTabs;

        // Live 3D preview (model rendered with the current material edits)
        private Image _previewImage;
        private long[] _previewMeshIds;
        private bool _previewReady;
        private System.Windows.Threading.DispatcherTimer _previewTimer;
        // 360° orbit preview state
        private double _orbitYaw = 0.74, _orbitPitch = 0.62, _orbitZoom = 1.0;
        private System.Windows.Point _orbitLast;
        private bool _orbiting;
        private DateTime _lastOrbitRender = DateTime.MinValue;

        #region Constructor

        public UniversalModelEditorDialog(string modelPath)
        {
            // Parse the model
            try
            {
                _modelData = UniversalModelParser.Instance.ParseModel(modelPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load model:\n{ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                _modelData = new UniversalModelData { FilePath = modelPath };
            }

            InitializeWindow();
            BuildUI();
            Loaded += OnLoaded;
            // The live preview swaps the SHARED render queue, which would otherwise make the main editor viewport
            // show THIS model instead of the scene. Hold the viewport in re-submit-every-frame mode while this
            // dialog is open, and force one more re-submit on close so the scene is restored.
            Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActivePreviewDialogs++;
            Closed += (s, e) => { try { Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActivePreviewDialogs--; Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit(); } catch { } };
        }

        public UniversalModelEditorDialog(UniversalModelData modelData)
        {
            _modelData = modelData ?? throw new ArgumentNullException(nameof(modelData));
            InitializeWindow();
            BuildUI();
            Loaded += OnLoaded;
            // The live preview swaps the SHARED render queue, which would otherwise make the main editor viewport
            // show THIS model instead of the scene. Hold the viewport in re-submit-every-frame mode while this
            // dialog is open, and force one more re-submit on close so the scene is restored.
            Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActivePreviewDialogs++;
            Closed += (s, e) => { try { Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActivePreviewDialogs--; Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit(); } catch { } };
        }

        private void InitializeWindow()
        {
            Title = $"Model Editor - {_modelData.FileName}";
            Width = 1500;
            Height = 920;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(22, 22, 24));
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 1100;
            MinHeight = 680;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateStats();
            
            // Select first material
            if (_modelData.Materials.Count > 0)
            {
                _materialList.SelectedIndex = 0;
            }
            
            // Select first submesh
            if (_modelData.Submeshes.Count > 0)
            {
                _submeshList.SelectedIndex = 0;
            }

            _previewReady = true;
            RefreshPreview();
        }

        #endregion

        #region UI Building

        private void BuildUI()
        {
            // 3-column layout with the live PREVIEW in the CENTER (the focal point):
            //   [ structure: submeshes + materials ] [ BIG PREVIEW ] [ properties + textures ]
            var mainGrid = new Grid();
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });            // structure
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // CENTER preview
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });            // properties
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Status bar

            var structure = BuildStructurePanel();
            Grid.SetColumn(structure, 0);
            mainGrid.Children.Add(structure);

            var preview = BuildPreviewPanel();
            Grid.SetColumn(preview, 1);
            mainGrid.Children.Add(preview);

            _rightTabs = BuildRightPanel();
            Grid.SetColumn(_rightTabs, 2);
            mainGrid.Children.Add(_rightTabs);

            var statusBar = BuildStatusBar();
            Grid.SetRow(statusBar, 1);
            Grid.SetColumnSpan(statusBar, 3);
            mainGrid.Children.Add(statusBar);

            Content = mainGrid;
        }

        /// <summary>The large, centered live 3D preview — fills its column and scales with the window.</summary>
        private Border BuildPreviewPanel()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 15, 17)),
                ClipToBounds = true
            };
            _previewImage = new Image
            {
                Stretch = Stretch.Uniform,
                Margin = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(_previewImage, BitmapScalingMode.HighQuality);
            border.Child = _previewImage;
            WireOrbit(_previewImage);

            // Subtle hint at the bottom so the controls are discoverable.
            var grid = new Grid();
            grid.Children.Add(border);
            var hint = new TextBlock
            {
                Text = "Drag: orbit   ·   Wheel: zoom",
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 126)),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 10),
                IsHitTestVisible = false
            };
            grid.Children.Add(hint);
            var outer = new Border { Child = grid };
            return outer;
        }

        /// <summary>Left column: model header + the submesh list + the material list.</summary>
        private Border BuildStructurePanel()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(0, 0, 1, 0)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Submeshes
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Materials header
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Materials

            var header = CreateHeader();
            grid.Children.Add(header);

            var submeshSection = BuildSubmeshSection();
            Grid.SetRow(submeshSection, 1);
            grid.Children.Add(submeshSection);

            var materialsHeader = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                Padding = new Thickness(12, 8, 12, 8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(0, 1, 0, 0)
            };
            materialsHeader.Child = new TextBlock
            {
                Text = "MATERIALS",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(157, 157, 157))
            };
            Grid.SetRow(materialsHeader, 2);
            grid.Children.Add(materialsHeader);

            var materialsSection = BuildMaterialsSection();
            Grid.SetRow(materialsSection, 3);
            grid.Children.Add(materialsSection);

            border.Child = grid;
            return border;
        }

        private Border CreateHeader()
        {
            var header = new Border
            {
                Padding = new Thickness(12, 12, 12, 12),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            var stack = new StackPanel();
            
            stack.Children.Add(new TextBlock
            {
                Text = _modelData.FileName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            stack.Children.Add(new TextBlock
            {
                Text = _modelData.FormatName,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(108, 92, 231)),
                Margin = new Thickness(0, 2, 0, 0)
            });

            _statsText = new TextBlock
            {
                Text = _modelData.StatsSummary,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(157, 157, 157)),
                Margin = new Thickness(0, 4, 0, 0)
            };
            stack.Children.Add(_statsText);

            header.Child = stack;
            return header;
        }

        private Grid BuildSubmeshSection()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Submeshes Header
            var submeshHeader = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                Padding = new Thickness(12, 8, 12, 8)
            };
            submeshHeader.Child = new TextBlock
            {
                Text = "SUBMESHES",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(157, 157, 157))
            };
            grid.Children.Add(submeshHeader);

            // Submesh List
            _submeshList = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4),
                ItemsSource = _modelData.Submeshes
            };
            _submeshList.SelectionChanged += SubmeshList_SelectionChanged;
            // Double-click a submesh -> open the dedicated Mesh Editor window (live preview + geometry breakdown).
            _submeshList.MouseDoubleClick += (s, e) =>
            {
                try
                {
                    if (_modelData != null && !string.IsNullOrEmpty(_modelData.FilePath) && System.IO.File.Exists(_modelData.FilePath))
                        MeshEditorDialog.OpenMesh(Window.GetWindow(this), _modelData.FilePath);
                }
                catch { }
            };
            
            // Item template
            _submeshList.ItemTemplate = CreateSubmeshItemTemplate();
            _submeshList.ItemContainerStyle = BuildCardItemStyle(Color.FromRgb(108, 92, 231));
            Grid.SetRow(_submeshList, 1);
            grid.Children.Add(_submeshList);

            return grid;
        }

        private DataTemplate CreateSubmeshItemTemplate()
        {
            var template = new DataTemplate();
            var grid = new FrameworkElementFactory(typeof(Grid));
            var c1 = new FrameworkElementFactory(typeof(ColumnDefinition));
            c1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            var c2 = new FrameworkElementFactory(typeof(ColumnDefinition));
            c2.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            grid.AppendChild(c1);
            grid.AppendChild(c2);

            // Accent chip (a little rounded square) so each submesh reads as a card with a leading visual.
            var chip = new FrameworkElementFactory(typeof(Border));
            chip.SetValue(Border.WidthProperty, 30.0);
            chip.SetValue(Border.HeightProperty, 30.0);
            chip.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            chip.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(60, 56, 86)));
            chip.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            var glyph = new FrameworkElementFactory(typeof(TextBlock));
            glyph.SetValue(TextBlock.TextProperty, "◈");
            glyph.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 135, 255)));
            glyph.SetValue(TextBlock.FontSizeProperty, 14.0);
            glyph.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            glyph.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            chip.AppendChild(glyph);

            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(StackPanel.MarginProperty, new Thickness(12, 0, 0, 0));
            stack.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);
            stack.SetValue(Grid.ColumnProperty, 1);

            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DisplayName"));
            name.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            name.SetValue(TextBlock.FontSizeProperty, 13.0);
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            stack.AppendChild(name);

            var info = new FrameworkElementFactory(typeof(TextBlock));
            info.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("GeometryInfo"));
            info.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 150, 158)));
            info.SetValue(TextBlock.FontSizeProperty, 11.0);
            info.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 0));
            stack.AppendChild(info);

            grid.AppendChild(chip);
            grid.AppendChild(stack);
            template.VisualTree = grid;
            return template;
        }

        private Grid BuildMaterialsSection()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Material List
            _materialList = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4),
                ItemsSource = _modelData.Materials
            };
            _materialList.SelectionChanged += MaterialList_SelectionChanged;
            // Double-click a material -> open it in the full Material Editor.
            _materialList.MouseDoubleClick += (s, e) => { if (_selectedMaterial != null) OpenSelectedMaterialInEditor(); };
            _materialList.ItemTemplate = CreateMaterialItemTemplate();
            _materialList.ItemContainerStyle = BuildCardItemStyle(Color.FromRgb(108, 92, 231));
            grid.Children.Add(_materialList);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 8, 8, 8)
            };

            var autoAssignBtn = CreateButton("Auto-Assign All", 110);
            autoAssignBtn.Click += AutoAssignAll_Click;
            buttonPanel.Children.Add(autoAssignBtn);

            var clearAllBtn = CreateButton("Clear All", 70);
            clearAllBtn.Margin = new Thickness(8, 0, 0, 0);
            clearAllBtn.Click += ClearAllTextures_Click;
            buttonPanel.Children.Add(clearAllBtn);

            Grid.SetRow(buttonPanel, 1);
            grid.Children.Add(buttonPanel);

            return grid;
        }

        private DataTemplate CreateMaterialItemTemplate()
        {
            var template = new DataTemplate();
            var grid = new FrameworkElementFactory(typeof(Grid));
            var c1 = new FrameworkElementFactory(typeof(ColumnDefinition));
            c1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            var c2 = new FrameworkElementFactory(typeof(ColumnDefinition));
            c2.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            grid.AppendChild(c1);
            grid.AppendChild(c2);

            // Round color swatch (the material's base color)
            var swatch = new FrameworkElementFactory(typeof(Border));
            swatch.SetValue(Border.WidthProperty, 32.0);
            swatch.SetValue(Border.HeightProperty, 32.0);
            swatch.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
            swatch.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            swatch.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)));
            swatch.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            swatch.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("BaseColorBrush"));

            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(StackPanel.MarginProperty, new Thickness(12, 0, 0, 0));
            stack.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);
            stack.SetValue(Grid.ColumnProperty, 1);

            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
            name.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            name.SetValue(TextBlock.FontSizeProperty, 13.0);
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            stack.AppendChild(name);

            var info = new FrameworkElementFactory(typeof(TextBlock));
            info.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("TextureSummary"));
            info.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 150, 158)));
            info.SetValue(TextBlock.FontSizeProperty, 11.0);
            info.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 0));
            stack.AppendChild(info);

            grid.AppendChild(swatch);
            grid.AppendChild(stack);
            template.VisualTree = grid;
            return template;
        }

        /// <summary>A modern card style for the submesh/material lists: rounded, with hover + accent-on-select.</summary>
        private static Style BuildCardItemStyle(Color accent)
        {
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6, 3, 6, 3)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.SnapsToDevicePixelsProperty, true));

            var tpl = new ControlTemplate(typeof(ListBoxItem));
            var card = new FrameworkElementFactory(typeof(Border));
            card.Name = "Card";
            card.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            card.SetValue(Border.PaddingProperty, new Thickness(12, 10, 12, 10));
            card.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(43, 43, 49)));
            card.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            card.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(55, 55, 63)));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            card.AppendChild(cp);
            tpl.VisualTree = card;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(54, 54, 62)), "Card"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(80, 80, 92)), "Card"));
            tpl.Triggers.Add(hover);

            var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            sel.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(50, 47, 66)), "Card"));
            sel.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(accent), "Card"));
            sel.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "Card"));
            tpl.Triggers.Add(sel);

            style.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return style;
        }

        private TabControl BuildRightPanel()
        {
            var tabs = new TabControl
            {
                Background = new SolidColorBrush(Color.FromRgb(22, 22, 24)),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0)
            };

            // Tab 1: Material Properties
            var propertiesTab = new TabItem { Header = "Material Properties" };
            propertiesTab.Content = BuildPropertiesTab();
            tabs.Items.Add(propertiesTab);

            // Tab 2: Texture Library
            var libraryTab = new TabItem { Header = "Texture Library" };
            libraryTab.Content = BuildTextureLibraryTab();
            tabs.Items.Add(libraryTab);

            // Tab 3: All Textures Overview
            var overviewTab = new TabItem { Header = "Assigned Textures" };
            overviewTab.Content = BuildTexturesOverviewTab();
            tabs.Items.Add(overviewTab);

            return tabs;
        }

        private ScrollViewer BuildPropertiesTab()
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0)
            };

            _propertiesPanel = new StackPanel { Margin = new Thickness(15) };
            
            // Placeholder
            _propertiesPanel.Children.Add(new TextBlock
            {
                Text = "Select a material to edit its properties",
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 20, 0, 0)
            });

            scroll.Content = _propertiesPanel;
            return scroll;
        }

        private Grid BuildTextureLibraryTab()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Header
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
                Text = $"Textures from: {_modelData.Directory}",
                Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            headerGrid.Children.Add(pathText);

            var countText = new TextBlock
            {
                Text = $"{_modelData.DiscoveredTextures.Count} texture(s) found",
                Foreground = new SolidColorBrush(Color.FromRgb(157, 157, 157)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(countText, 1);
            headerGrid.Children.Add(countText);

            header.Child = headerGrid;
            grid.Children.Add(header);

            // Texture Grid
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _textureLibraryPanel = new WrapPanel { Margin = new Thickness(15) };

            foreach (var texture in _modelData.DiscoveredTextures)
            {
                _textureLibraryPanel.Children.Add(CreateTextureLibraryItem(texture));
            }

            scroll.Content = _textureLibraryPanel;
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            return grid;
        }

        private Border CreateTextureLibraryItem(DiscoveredTexture texture)
        {
            var border = new Border
            {
                Width = 130,
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
                Height = 90,
                CornerRadius = new CornerRadius(4),
                Background = texture.Preview != null
                    ? new ImageBrush(texture.Preview) { Stretch = Stretch.UniformToFill }
                    : new SolidColorBrush(Color.FromRgb(22, 22, 24))
            };
            stack.Children.Add(preview);

            // File name
            stack.Children.Add(new TextBlock
            {
                Text = texture.FileName,
                Foreground = Brushes.White,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 5, 0, 0),
                ToolTip = texture.FilePath
            });

            // Detected type
            stack.Children.Add(new TextBlock
            {
                Text = texture.DetectedType.ToString(),
                Foreground = new SolidColorBrush(Color.FromRgb(108, 92, 231)),
                FontSize = 9
            });

            // File size
            stack.Children.Add(new TextBlock
            {
                Text = texture.FileSizeText,
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                FontSize = 9
            });

            border.Child = stack;

            // Drag support
            border.MouseMove += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    var data = new DataObject(DataFormats.FileDrop, new[] { texture.FilePath });
                    DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
                }
            };

            return border;
        }

        private ScrollViewer BuildTexturesOverviewTab()
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

            var wrap = new WrapPanel();

            foreach (var material in _modelData.Materials)
            {
                foreach (var slot in material.TextureMaps.Where(s => s.IsAssigned))
                {
                    var item = new Border
                    {
                        Width = 160,
                        Margin = new Thickness(0, 0, 10, 10),
                        Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8)
                    };

                    var itemStack = new StackPanel();

                    var preview = new Border
                    {
                        Height = 100,
                        CornerRadius = new CornerRadius(4),
                        Background = slot.Preview != null
                            ? new ImageBrush(slot.Preview) { Stretch = Stretch.UniformToFill }
                            : new SolidColorBrush(Color.FromRgb(22, 22, 24))
                    };
                    itemStack.Children.Add(preview);

                    itemStack.Children.Add(new TextBlock
                    {
                        Text = $"{material.Name} - {slot.DisplayName}",
                        Foreground = Brushes.White,
                        FontSize = 10,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 5, 0, 0)
                    });

                    itemStack.Children.Add(new TextBlock
                    {
                        Text = slot.FileName,
                        Foreground = new SolidColorBrush(Color.FromRgb(157, 157, 157)),
                        FontSize = 9,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });

                    item.Child = itemStack;
                    wrap.Children.Add(item);
                }
            }

            if (wrap.Children.Count == 0)
            {
                wrap.Children.Add(new TextBlock
                {
                    Text = "No textures assigned yet.\nUse Auto-Assign or drag textures from the Texture Library.",
                    Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                    FontStyle = FontStyles.Italic
                });
            }

            stack.Children.Add(wrap);
            scroll.Content = stack;
            return scroll;
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

            var saveBtn = CreateButton("Save Materials", 110);
            saveBtn.Background = new SolidColorBrush(Color.FromRgb(108, 92, 231));
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

        #endregion

        #region Material Properties Panel

        /// <summary>The per-submesh .vmat file (materials/submesh_&lt;i&gt;.vmat) for a material — the same file
        /// scene-add binds. Returns the first submesh's .vmat that uses this material.</summary>
        private string ResolveMaterialVmatPath(UniversalMaterial mat)
        {
            if (mat == null || _modelData == null || string.IsNullOrEmpty(_modelData.Directory)) return null;
            int matIdx = _modelData.Materials.IndexOf(mat);
            if (matIdx < 0) return null;
            for (int i = 0; i < _modelData.Submeshes.Count; i++)
                if (_modelData.Submeshes[i].MaterialIndex == matIdx)
                    return System.IO.Path.Combine(_modelData.Directory, "materials", $"submesh_{i}.vmat");
            return null;
        }

        /// <summary>Jump to the full Material Editor for the selected material (saving first if its .vmat is missing).</summary>
        private void OpenSelectedMaterialInEditor()
        {
            if (_selectedMaterial == null) return;
            string vmat = ResolveMaterialVmatPath(_selectedMaterial);
            if (string.IsNullOrEmpty(vmat) || !System.IO.File.Exists(vmat))
            {
                try { SaveMaterials_Click(null, null); } catch { }
                vmat = ResolveMaterialVmatPath(_selectedMaterial);
            }
            if (!string.IsNullOrEmpty(vmat) && System.IO.File.Exists(vmat))
            {
                try { MaterialEditorDialog.OpenMaterial(Window.GetWindow(this), vmat); }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not open the Material Editor:\n" + ex.Message, "Material Editor",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
                MessageBox.Show("Save the model's materials first (Save Materials).", "Material Editor",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Jump to the Texture Editor for an assigned texture slot.</summary>
        private void OpenTextureInEditor(string texturePath)
        {
            if (string.IsNullOrEmpty(texturePath)) return;
            string full = texturePath;
            if (!System.IO.Path.IsPathRooted(full) && !string.IsNullOrEmpty(_modelData?.Directory))
                full = System.IO.Path.Combine(_modelData.Directory, texturePath);
            if (!System.IO.File.Exists(full)) { MessageBox.Show("Texture file not found:\n" + full, "Texture Editor", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            try { new TextureEditorDialog(full) { Owner = Window.GetWindow(this) }.ShowDialog(); }
            catch (Exception ex) { MessageBox.Show("Could not open texture editor:\n" + ex.Message, "Texture Editor", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void UpdatePropertiesPanel()
        {
            _propertiesPanel.Children.Clear();
            SchedulePreviewRefresh();

            if (_selectedMaterial == null)
            {
                _propertiesPanel.Children.Add(new TextBlock
                {
                    Text = "Select a material to edit its properties",
                    Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 20, 0, 0)
                });
                return;
            }

            // Header: material name + a jump to the full Material Editor (edits the SAME .vmat scene-add binds).
            var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 18) };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.Children.Add(new TextBlock
            {
                Text = _selectedMaterial.Name,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var openMatBtn = CreateButton("⧉ Material Editor", 150);
            openMatBtn.VerticalAlignment = VerticalAlignment.Center;
            openMatBtn.Click += (s, e) => OpenSelectedMaterialInEditor();
            Grid.SetColumn(openMatBtn, 1);
            headerRow.Children.Add(openMatBtn);
            _propertiesPanel.Children.Add(headerRow);

            // Base Color Section
            _propertiesPanel.Children.Add(CreateSectionHeader("BASE COLOR"));
            var colorPreview = new Border
            {
                Height = 36,
                Background = _selectedMaterial.BaseColorBrush,
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 20)
            };
            colorPreview.MouseLeftButtonUp += (s, e) =>
            {
                var picker = new ColorPickerDialog(_selectedMaterial.BaseColor) { Owner = this };
                if (picker.ShowDialog() == true)
                {
                    _selectedMaterial.BaseColor = picker.SelectedColor;
                    UpdatePropertiesPanel();
                }
            };
            _propertiesPanel.Children.Add(colorPreview);

            // PBR Properties Section
            _propertiesPanel.Children.Add(CreateSectionHeader("PBR PROPERTIES"));
            _propertiesPanel.Children.Add(CreateSliderRow("Metallic", _selectedMaterial.Metallic, 0, 1, v => _selectedMaterial.Metallic = (float)v));
            _propertiesPanel.Children.Add(CreateSliderRow("Roughness", _selectedMaterial.Roughness, 0, 1, v => _selectedMaterial.Roughness = (float)v));
            _propertiesPanel.Children.Add(CreateSliderRow("Normal Strength", _selectedMaterial.NormalStrength, 0, 2, v => _selectedMaterial.NormalStrength = (float)v));
            _propertiesPanel.Children.Add(CreateSliderRow("AO Strength", _selectedMaterial.AOStrength, 0, 1, v => _selectedMaterial.AOStrength = (float)v));

            // Textures Section
            _propertiesPanel.Children.Add(CreateSectionHeader("TEXTURE MAPS"));

            var autoDetectBtn = CreateButton("Auto-Detect Textures", 160);
            autoDetectBtn.HorizontalAlignment = HorizontalAlignment.Left;
            autoDetectBtn.Click += (s, e) =>
            {
                UniversalModelParser.Instance.AutoAssignTexturesForMaterial(_modelData, _selectedMaterial);
                UpdatePropertiesPanel();
                UpdateStats();
            };
            _propertiesPanel.Children.Add(autoDetectBtn);

            _propertiesPanel.Children.Add(new Separator
            {
                Margin = new Thickness(0, 15, 0, 10),
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60))
            });

            foreach (var slot in _selectedMaterial.TextureMaps)
            {
                _propertiesPanel.Children.Add(CreateTextureSlotUI(slot));
            }

            // "+ Add Map" — slots are dynamic (only the maps the import found are shown). This lets the user add
            // a slot for a standard map the model didn't ship with, or a custom map.
            var addSlotBtn = CreateButton("+ Add Map", 200);
            addSlotBtn.HorizontalAlignment = HorizontalAlignment.Left;
            addSlotBtn.Margin = new Thickness(0, 15, 0, 0);
            addSlotBtn.Click += (s, e) =>
            {
                var menu = new System.Windows.Controls.ContextMenu();
                foreach (var t in UniversalMaterial.StandardMapTypes)
                {
                    if (_selectedMaterial.GetTextureSlot(t) != null) continue; // already present
                    var mi = new System.Windows.Controls.MenuItem { Header = t.ToString() };
                    var captured = t;
                    mi.Click += (s2, e2) => { _selectedMaterial.AddStandardSlot(captured); UpdatePropertiesPanel(); UpdateStats(); };
                    menu.Items.Add(mi);
                }
                if (menu.Items.Count > 0) menu.Items.Add(new System.Windows.Controls.Separator());
                var custom = new System.Windows.Controls.MenuItem { Header = "Custom…" };
                custom.Click += (s2, e2) => { _selectedMaterial.AddCustomSlot($"Custom_{_selectedMaterial.TextureMaps.Count}"); UpdatePropertiesPanel(); UpdateStats(); };
                menu.Items.Add(custom);
                menu.PlacementTarget = addSlotBtn;
                menu.IsOpen = true;
            };
            _propertiesPanel.Children.Add(addSlotBtn);

            // When the material has no maps at all (base-color/PBR-only model), make that explicit instead of an
            // empty area — the slots being absent is correct, not a bug.
            if (_selectedMaterial.TextureMaps.Count == 0)
            {
                _propertiesPanel.Children.Insert(_propertiesPanel.Children.Count - 1, new TextBlock
                {
                    Text = "This material has no texture maps from the import (base color / PBR only). Use “+ Add Map” to assign one.",
                    Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                    FontStyle = FontStyles.Italic,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
        }

        private UIElement CreateTextureSlotUI(TextureMapData slot)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Preview with drop support
            var preview = new Border
            {
                Width = 52,
                Height = 52,
                CornerRadius = new CornerRadius(4),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1),
                AllowDrop = true,
                Background = slot.Preview != null
                    ? new ImageBrush(slot.Preview) { Stretch = Stretch.UniformToFill }
                    : new SolidColorBrush(Color.FromRgb(45, 45, 48))
            };

            if (slot.Preview == null)
            {
                preview.Child = new TextBlock
                {
                    Text = "Drop",
                    Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            preview.DragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            preview.Drop += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files?.Length > 0)
                    {
                        slot.FilePath = files[0];
                        UpdatePropertiesPanel();
                        UpdateStats();
                    }
                }
            };
            grid.Children.Add(preview);

            // Info
            var infoStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(infoStack, 1);

            infoStack.Children.Add(new TextBlock
            {
                Text = slot.DisplayName,
                Foreground = Brushes.White,
                FontSize = 12
            });

            infoStack.Children.Add(new TextBlock
            {
                Text = slot.StatusText,
                Foreground = slot.FileExists 
                    ? new SolidColorBrush(Color.FromRgb(157, 157, 157))
                    : new SolidColorBrush(Color.FromRgb(255, 100, 100)),
                FontSize = 10
            });

            grid.Children.Add(infoStack);

            // Buttons
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(buttonPanel, 2);

            var browseBtn = CreateButton("Browse", 60);
            browseBtn.FontSize = 11;
            browseBtn.Click += (s, e) =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = $"Select {slot.DisplayName} Texture",
                    Filter = "Images|*.png;*.jpg;*.jpeg;*.tga;*.bmp;*.dds;*.hdr|All|*.*",
                    InitialDirectory = _modelData.Directory
                };
                if (dialog.ShowDialog() == true)
                {
                    slot.FilePath = dialog.FileName;
                    UpdatePropertiesPanel();
                    UpdateStats();
                }
            };
            buttonPanel.Children.Add(browseBtn);

            if (slot.IsAssigned)
            {
                var editBtn = CreateButton("Edit", 44);
                editBtn.FontSize = 11;
                editBtn.Margin = new Thickness(4, 0, 0, 0);
                editBtn.ToolTip = "Open in Texture Editor";
                var slotPath = slot.FilePath;
                editBtn.Click += (s, e) => OpenTextureInEditor(slotPath);
                buttonPanel.Children.Add(editBtn);

                var clearBtn = CreateButton("✕", 30);
                clearBtn.FontSize = 12;
                clearBtn.Margin = new Thickness(4, 0, 0, 0);
                clearBtn.ToolTip = "Clear";
                clearBtn.Click += (s, e) =>
                {
                    slot.Clear();
                    UpdatePropertiesPanel();
                    UpdateStats();
                };
                buttonPanel.Children.Add(clearBtn);
            }

            grid.Children.Add(buttonPanel);

            return grid;
        }

        private UIElement CreateSliderRow(string label, double value, double min, double max, Action<double> onChanged)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });

            grid.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(157, 157, 157)),
                VerticalAlignment = VerticalAlignment.Center
            });

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(slider, 1);
            grid.Children.Add(slider);

            var valueText = new TextBlock
            {
                Text = value.ToString("F2"),
                Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right
            };
            Grid.SetColumn(valueText, 2);
            grid.Children.Add(valueText);

            slider.ValueChanged += (s, e) =>
            {
                valueText.Text = e.NewValue.ToString("F2");
                onChanged?.Invoke(e.NewValue);
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

        #endregion

        #region Event Handlers

        private void SubmeshList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedSubmesh = _submeshList.SelectedItem as SubmeshData;
            
            // Auto-select the submesh's material
            if (_selectedSubmesh != null && _selectedSubmesh.MaterialIndex >= 0 
                && _selectedSubmesh.MaterialIndex < _modelData.Materials.Count)
            {
                _materialList.SelectedIndex = _selectedSubmesh.MaterialIndex;
            }
        }

        private void MaterialList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedMaterial = _materialList.SelectedItem as UniversalMaterial;
            UpdatePropertiesPanel();
        }

        private void AutoAssignAll_Click(object sender, RoutedEventArgs e)
        {
            UniversalModelParser.Instance.AutoAssignTextures(_modelData);
            UpdatePropertiesPanel();
            UpdateStats();
            _statusText.Text = "Auto-assigned textures to all materials";
        }

        private void ClearAllTextures_Click(object sender, RoutedEventArgs e)
        {
            foreach (var material in _modelData.Materials)
            {
                foreach (var slot in material.TextureMaps)
                {
                    slot.Clear();
                }
                material.RefreshStats();
            }
            UpdatePropertiesPanel();
            UpdateStats();
            _statusText.Text = "Cleared all texture assignments";
        }

        private void SaveMaterials_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int savedCount = 0;
                // Write ONE .vmat per submesh into the model's materials/ folder, named submesh_<i>.vmat — the
                // EXACT files the scene-add binds (MeshRenderer.MaterialPath). So editing here updates what the
                // placed model renders, and the .vmat stays the single source of truth (FromUniversalMaterial
                // carries every map the material actually has, not a fixed subset). Submeshes that share a
                // material all get that material's data.
                var matDir = Path.Combine(_modelData.Directory, "materials");
                Directory.CreateDirectory(matDir);
                for (int i = 0; i < _modelData.Submeshes.Count; i++)
                {
                    var sub = _modelData.Submeshes[i];
                    UniversalMaterial material = (sub.MaterialIndex >= 0 && sub.MaterialIndex < _modelData.Materials.Count)
                        ? _modelData.Materials[sub.MaterialIndex]
                        : (_modelData.Materials.Count > 0 ? _modelData.Materials[0] : null);
                    if (material == null) continue;

                    var vmat = VortexMaterial.FromUniversalMaterial(material);
                    vmat.MakePathsRelative(matDir);
                    if (vmat.Save(Path.Combine(matDir, $"submesh_{i}.vmat")))
                        savedCount++;
                }

                AssetDatabase.Instance.Refresh();

                // Propagate edits to instances of this model already placed in the scene: re-apply
                // each material's edited PBR values + texture maps onto the SAME engine material the
                // placed submeshes already render (found via the import-time mesh-path key
                // "{modelRelPath}#submesh{i}"). Mutating in place avoids any material-ownership /
                // double-free issues, and is a safe no-op if the model isn't in the scene.
                PropagateMaterialsToScene();

                _statusText.Text = $"Saved {savedCount} material(s)";
            }
            catch (Exception ex)
            {
                _statusText.Text = $"Error saving materials: {ex.Message}";
                MessageBox.Show($"Failed to save materials:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Debounced live-preview refresh so editing sliders/textures doesn't render every tick.
        /// </summary>
        private void SchedulePreviewRefresh()
        {
            if (!_previewReady) return;
            if (_previewTimer == null)
            {
                _previewTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(180)
                };
                _previewTimer.Tick += (s, e) => { _previewTimer.Stop(); RefreshPreview(); };
            }
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        /// <summary>
        /// Renders the model's submeshes with engine materials built from the CURRENT UI edits, so the
        /// preview reflects edits live. The submesh meshes are imported once and cached; the throwaway
        /// engine materials are deleted after each render (caller-owned, not cached).
        /// </summary>
        /// <summary>Drag to orbit (yaw/pitch), scroll to zoom — re-renders the model preview each change.</summary>
        private void WireOrbit(Image img)
        {
            img.Cursor = System.Windows.Input.Cursors.SizeAll;
            img.ToolTip = "Drag to orbit · scroll to zoom";
            img.MouseLeftButtonDown += (s, e) => { _orbiting = true; _orbitLast = e.GetPosition(img); img.CaptureMouse(); };
            img.MouseLeftButtonUp += (s, e) => { _orbiting = false; img.ReleaseMouseCapture(); RefreshPreview(); /* settle at the final angle */ };
            img.MouseMove += (s, e) =>
            {
                if (!_orbiting) return;
                var p = e.GetPosition(img);
                _orbitYaw += (p.X - _orbitLast.X) * 0.01;
                _orbitPitch += (p.Y - _orbitLast.Y) * 0.01;
                if (_orbitPitch > 1.5) _orbitPitch = 1.5; else if (_orbitPitch < -1.5) _orbitPitch = -1.5;
                _orbitLast = p;
                // Throttle to ~25 FPS — the MouseMove event fires far faster than a full model re-render can
                // keep up, which is what dragged the preview down to ~0.01 FPS. The final angle settles on mouse-up.
                var now = DateTime.UtcNow;
                if ((now - _lastOrbitRender).TotalMilliseconds >= 40) { _lastOrbitRender = now; RefreshPreview(); }
            };
            img.MouseWheel += (s, e) =>
            {
                _orbitZoom *= e.Delta > 0 ? 0.9 : 1.1;
                if (_orbitZoom < 0.2) _orbitZoom = 0.2; else if (_orbitZoom > 5.0) _orbitZoom = 5.0;
                RefreshPreview();
            };
        }

        private void RefreshPreview()
        {
            if (!_previewReady || _previewImage == null) return;
            try
            {
                if (_previewMeshIds == null)
                {
                    var subs = Editor.DllWrapper.VortexAPI.ImportModelWithMaterialsFromFile(_modelData.FilePath);
                    if (subs == null || subs.Length == 0) return;
                    _previewMeshIds = subs.Select(s => s.MeshId).ToArray();
                }

                var mats = new long[_previewMeshIds.Length];
                var built = new System.Collections.Generic.List<long>();
                for (int i = 0; i < _previewMeshIds.Length; i++)
                {
                    int matIdx = (i < _modelData.Submeshes.Count) ? _modelData.Submeshes[i].MaterialIndex : 0;
                    long em = -1;
                    if (matIdx >= 0 && matIdx < _modelData.Materials.Count)
                        em = Core.Services.MaterialService.Instance.BuildEngineMaterial(ToVortexMaterial(_modelData.Materials[matIdx]));
                    mats[i] = em;
                    if (em >= 0) built.Add(em);
                }

                var img = Core.Services.Rendering.AssetPreviewRenderer.RenderMeshes(_previewMeshIds, mats, 896, (float)_orbitYaw, (float)_orbitPitch, (float)_orbitZoom);
                if (img != null) _previewImage.Source = img;

                foreach (var b in built) { try { Editor.DllWrapper.VortexAPI.DeleteMaterial(b); } catch { } }
            }
            catch { }
        }

        /// <summary>
        /// Builds a VortexMaterial (absolute texture paths) from a UniversalMaterial for previewing.
        /// </summary>
        private VortexMaterial ToVortexMaterial(UniversalMaterial m)
        {
            var vmat = new VortexMaterial
            {
                Name = m.Name,
                Metallic = m.Metallic,
                Roughness = m.Roughness,
                NormalStrength = m.NormalStrength,
                AmbientOcclusion = m.AOStrength,
                AlbedoTexture = m.GetTextureSlot(TextureMapType.Albedo)?.FilePath,
                NormalTexture = m.GetTextureSlot(TextureMapType.Normal)?.FilePath,
                MetallicTexture = m.GetTextureSlot(TextureMapType.Metallic)?.FilePath,
                RoughnessTexture = m.GetTextureSlot(TextureMapType.Roughness)?.FilePath,
                AOTexture = m.GetTextureSlot(TextureMapType.AmbientOcclusion)?.FilePath,
                EmissiveStrength = m.EmissiveStrength,
                TwoSided = m.TwoSided
            };
            vmat.SetBaseColor(m.BaseColor);
            vmat.ResolvePathsAbsolute(_modelData.Directory);
            return vmat;
        }

        private void PropagateMaterialsToScene()
        {
            var projectPath = Core.Data.ProjectData.Current?.Path ?? "";
            string relPath = _modelData.FilePath ?? "";
            if (!string.IsNullOrEmpty(projectPath) && relPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                relPath = relPath.Substring(projectPath.Length).TrimStart('\\', '/');

            for (int i = 0; i < _modelData.Submeshes.Count; i++)
            {
                int matIdx = _modelData.Submeshes[i].MaterialIndex;
                if (matIdx < 0 || matIdx >= _modelData.Materials.Count) continue;

                long liveId = Core.Services.SceneRenderService.GetMaterialForMeshPath($"{relPath}#submesh{i}");
                if (liveId < 0) continue; // not placed in the scene (or path mismatch) -> safe skip

                var m = _modelData.Materials[matIdx];
                var c = m.BaseColor;
                Editor.DllWrapper.VortexAPI.SetMaterialBaseColor(liveId, c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
                Editor.DllWrapper.VortexAPI.SetMaterialMetallicValue(liveId, m.Metallic);
                Editor.DllWrapper.VortexAPI.SetMaterialRoughnessValue(liveId, m.Roughness);
                Editor.DllWrapper.VortexAPI.SetMaterialAOValue(liveId, m.AOStrength);
                Editor.DllWrapper.VortexAPI.SetMaterialNormalStrengthValue(liveId, m.NormalStrength);

                BindLiveMap(liveId, m.GetTextureSlot(TextureMapType.Albedo)?.FilePath, Editor.DllWrapper.VortexAPI.SetMaterialAlbedoTexture);
                BindLiveMap(liveId, m.GetTextureSlot(TextureMapType.Normal)?.FilePath, Editor.DllWrapper.VortexAPI.SetMaterialNormalMap);
                BindLiveMap(liveId, m.GetTextureSlot(TextureMapType.Metallic)?.FilePath, Editor.DllWrapper.VortexAPI.SetMaterialMetallicMap);
                BindLiveMap(liveId, m.GetTextureSlot(TextureMapType.Roughness)?.FilePath, Editor.DllWrapper.VortexAPI.SetMaterialRoughnessMap);
                BindLiveMap(liveId, m.GetTextureSlot(TextureMapType.AmbientOcclusion)?.FilePath, Editor.DllWrapper.VortexAPI.SetMaterialAOMap);
            }
        }

        private void BindLiveMap(long materialId, string texturePath, Action<long, long> setter)
        {
            if (string.IsNullOrEmpty(texturePath)) return;
            string full = texturePath;
            if (!Path.IsPathRooted(full))
                full = Path.Combine(_modelData.Directory, full);
            if (!File.Exists(full)) return;
            long tex = Editor.DllWrapper.VortexAPI.ImportTextureFromFile(full);
            if (tex >= 0) setter(materialId, tex);
        }

        private void UpdateStats()
        {
            _modelData.RefreshStats();
            _statsText.Text = _modelData.StatsSummary;
            
            foreach (var material in _modelData.Materials)
            {
                material.RefreshStats();
            }
        }

        #endregion

        #region Static Methods

        /// <summary>
        /// Opens the model editor for the specified model path.
        /// </summary>
        public static void OpenForModel(Window owner, string modelPath)
        {
            try
            {
                var dialog = new UniversalModelEditorDialog(modelPath);
                if (owner != null)
                    dialog.Owner = owner;
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open model editor:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Opens the model editor with pre-parsed model data.
        /// </summary>
        public static void OpenForModel(Window owner, UniversalModelData modelData)
        {
            try
            {
                var dialog = new UniversalModelEditorDialog(modelData);
                if (owner != null)
                    dialog.Owner = owner;
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open model editor:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}
