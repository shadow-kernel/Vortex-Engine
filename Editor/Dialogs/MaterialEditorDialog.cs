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
        private TextBox _tilingUBox, _tilingVBox;   // UV tiling (texture repeat scale)
        private CheckBox _twoSidedCheck, _receiveShadowsCheck, _castShadowsCheck;
        private TextBlock _statusText;

        // Live preview (sphere rendered with the current material)
        private Image _previewImage;
        private bool _previewReady;
        private System.Windows.Threading.DispatcherTimer _previewTimer;
        // 360° orbit preview state
        private double _orbitYaw = 0.74, _orbitPitch = 0.62, _orbitZoom = 1.0;
        private System.Windows.Point _orbitLast;
        private bool _orbiting;

        // Texture paths and previews
        private string _albedoPath, _normalPath, _metallicPath, _roughnessPath, _aoPath, _heightPath;
        private Border _albedoPreview, _normalPreview, _metallicPreviewBorder, _roughnessPreview, _aoPreview, _heightPreview;
        private TextBlock _albedoPathText, _normalPathText, _metallicPathText, _roughnessPathText, _aoPathText, _heightPathText;
        private TextBox _heightScaleBox;   // parallax/displacement depth

        public MaterialEditorDialog()
        {
            _material = new VortexMaterial();
            InitializeWindow();
            BuildUI();
            Loaded += (s, e) => { _previewReady = true; RefreshPreview(); };
            // Shader hot-reload in the PREVIEW: when you Alt-Tab back from VS (this window regains focus), recompile
            // any changed material shader + re-render the sphere — so saving the .hlsl updates the preview live.
            Activated += (s, e) =>
            {
                if (!_previewReady) return;
                try { Editor.DllWrapper.VortexAPI.ReloadMaterialShaders(); } catch { }
                SchedulePreviewRefresh();
            };
            // This dialog's live sphere preview swaps the SHARED render queue; pause the main viewport while it's
            // open so they never contend for the engine's single render path (which crashed on nested dialogs).
            Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActivePreviewDialogs++;
            Closed += (s, e) =>
            {
                try
                {
                    Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActivePreviewDialogs--;
                    Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit();
                }
                catch { }
            };
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
            Width = 1440;
            Height = 900;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(22, 22, 24));
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 1080;
            MinHeight = 640;
        }

        private void BuildUI()
        {
            // 3-column layout like the model editor: properties left, BIG live sphere in the CENTER, textures right.
            var mainGrid = new Grid();
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });            // properties
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // CENTER preview
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });            // textures

            var props = BuildPropertiesPanel();
            Grid.SetColumn(props, 0);
            mainGrid.Children.Add(props);

            var preview = BuildPreviewPanel();
            Grid.SetColumn(preview, 1);
            mainGrid.Children.Add(preview);

            var textures = BuildTexturesPanel();
            Grid.SetColumn(textures, 2);
            mainGrid.Children.Add(textures);

            Content = mainGrid;
        }

        /// <summary>The large, centered live material sphere (orbit + zoom) — the focal point.</summary>
        private Border BuildPreviewPanel()
        {
            var outer = new Border { Background = new SolidColorBrush(Color.FromRgb(15, 15, 17)), ClipToBounds = true };
            _previewImage = new Image
            {
                Stretch = Stretch.Uniform,
                Margin = new Thickness(20),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(_previewImage, BitmapScalingMode.HighQuality);
            WireOrbit(_previewImage);

            var grid = new Grid();
            grid.Children.Add(_previewImage);
            grid.Children.Add(new TextBlock
            {
                Text = "Drag: orbit   ·   Wheel: zoom",
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 126)),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 12),
                IsHitTestVisible = false
            });
            outer.Child = grid;
            return outer;
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
            // (The live preview sphere now lives in the CENTER panel — see BuildPreviewPanel.)

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

            // Shader ASSET slot: assign a custom .vshader/.hlsl to this material, with a live graphical link + Edit-in-VS.
            stack.Children.Add(CreateLabel("Shader Asset", 12));
            stack.Children.Add(BuildShaderSlot());

            // Footstep Sound slot: assign a step clip (.wav/.mp3/.ogg/.flac or a .vsndc Sound Container) to this
            // material. The game's FootstepAudio script plays it (via Physics.GroundStepSound) when the player walks on
            // a surface using this material — footsteps are authored ENTIRELY here, no code.
            stack.Children.Add(CreateLabel("Footstep Sound", 12));
            stack.Children.Add(BuildFootstepSlot());

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

            FrameworkElement row;
            (_metallicSlider, _metallicValue, row) = CreateSliderRow("Metallic", 0, 1, _material.Metallic);
            stack.Children.Add(row);

            (_roughnessSlider, _roughnessValue, row) = CreateSliderRow("Roughness", 0, 1, _material.Roughness);
            stack.Children.Add(row);

            (_normalStrengthSlider, _normalStrengthValue, row) = CreateSliderRow("Normal Strength", 0, 2, _material.NormalStrength);
            stack.Children.Add(row);

            (_aoSlider, _aoValue, row) = CreateSliderRow("Ambient Occlusion", 0, 1, _material.AmbientOcclusion);
            stack.Children.Add(row);

            // UV Tiling (texture repeat scale) — the fix for "the texture looks stretched/blurry on a big surface":
            // set e.g. 16 to repeat the texture 16× across the mesh so it stays crisp.
            stack.Children.Add(BuildTilingRow());

            // Height Depth (parallax strength) — only visible when a Height map is assigned; ~0.02–0.08 typical.
            stack.Children.Add(BuildHeightScaleRow());

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
            // Setters keep the ABSOLUTE path in memory (matching the in-memory material convention); VortexMaterial's
            // own ResolvePathsRelative(materialDir) does the single, correct conversion to a MATERIAL-relative path on
            // save. (Storing project-relative here double-relativized it and silently lost the texture.)
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

            // Height / Displacement map (grayscale) — a "texture with depth"; parallax-mapped in the shader.
            (_heightPreview, _heightPathText) = CreateTextureSlot("Height (Displacement)", wrapPanel,
                path => { _heightPath = path; MarkDirty(); },
                () => BrowseTexture("Height"));

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
            // Stored texture paths are project-RELATIVE — resolve to absolute so the preview loads (and so a relative
            // path saved earlier still shows on reopen). A rooted path is used as-is.
            var abs = ResolveTexturePath(path);
            if (!string.IsNullOrEmpty(abs) && File.Exists(abs))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(abs);
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
            // STA FilePicker (NOT the WPF shell dialog, which deadlocks the live DX12/DXGI renderer on the UI thread —
            // that was the "browse crashes the engine" report). Starts in the project's texture folder, not C:\.
            return Editor.Core.Util.FilePicker.OpenFile(
                "Image Files|*.png;*.jpg;*.jpeg;*.tga;*.bmp;*.dds;*.hdr|All Files|*.*",
                $"Select {type} Texture", DefaultTextureDir());
        }

        /// <summary>Start folder for texture pickers: the material's own folder if known, else the project's
        /// Assets/Textures, else the project root — so the user never re-navigates from C:\.</summary>
        private string DefaultTextureDir()
        {
            var proj = Editor.Core.Data.ProjectData.Current?.Path;
            try
            {
                var matDir = string.IsNullOrEmpty(_materialPath) ? null : System.IO.Path.GetDirectoryName(_materialPath);
                if (!string.IsNullOrEmpty(matDir) && System.IO.Directory.Exists(matDir)) return matDir;
            }
            catch { }
            if (string.IsNullOrEmpty(proj)) return null;
            var tex = System.IO.Path.Combine(proj, "Assets", "Textures");
            return System.IO.Directory.Exists(tex) ? tex : proj;
        }

        /// <summary>Resolve a (possibly material-relative) texture path to absolute for loading/preview. Material paths
        /// are stored relative to the MATERIAL folder on disk; in memory they are already absolute, so a rooted path
        /// passes straight through — this only kicks in as a safety net for a relative value.</summary>
        private string ResolveTexturePath(string path)
        {
            if (string.IsNullOrEmpty(path) || System.IO.Path.IsPathRooted(path)) return path;
            try
            {
                var matDir = string.IsNullOrEmpty(_materialPath) ? null : System.IO.Path.GetDirectoryName(_materialPath);
                if (!string.IsNullOrEmpty(matDir))
                {
                    var abs = System.IO.Path.GetFullPath(System.IO.Path.Combine(matDir, path));
                    if (System.IO.File.Exists(abs)) return abs;
                }
            }
            catch { }
            var proj = Editor.Core.Data.ProjectData.Current?.Path;
            return string.IsNullOrEmpty(proj) ? path : System.IO.Path.Combine(proj, path);
        }

        /// <summary>Row with two number boxes for UV tiling (U, V). Editing marks dirty; values are read in
        /// GetMaterialFromUI and applied to the engine so the change previews live.</summary>
        private FrameworkElement BuildTilingRow()
        {
            // Display the EFFECTIVE tiling: a stored 0/negative renders as 1 (see ReadTiling + the render-path guard),
            // so show 1 too — otherwise the box would show "0" while the surface tiles 1×, and a save would silently
            // rewrite it to 1 anyway.
            var t = _material?.UVTiling;
            float u = (t != null && t.Length > 0 && t[0] > 0f) ? t[0] : 1f;
            float v = (t != null && t.Length > 1 && t[1] > 0f) ? t[1] : 1f;

            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(new TextBlock
            {
                Text = "Tiling (U, V) — texture repeats across the surface",
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)), FontSize = 11
            });
            var boxes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            TextBox MakeBox(float val)
            {
                var tb = new TextBox
                {
                    Text = val.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    Width = 70, Margin = new Thickness(0, 0, 8, 0),
                    Background = new SolidColorBrush(Color.FromRgb(20, 20, 22)),
                    Foreground = new SolidColorBrush(Color.FromRgb(233, 233, 237)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(44, 44, 50)),
                    Padding = new Thickness(6, 3, 6, 3)
                };
                tb.TextChanged += (s, e) => MarkDirty();
                return tb;
            }
            _tilingUBox = MakeBox(u);
            _tilingVBox = MakeBox(v);
            boxes.Children.Add(_tilingUBox);
            boxes.Children.Add(_tilingVBox);
            panel.Children.Add(boxes);
            return panel;
        }

        /// <summary>Row with the parallax/displacement depth for the Height map (0 = flat).</summary>
        private FrameworkElement BuildHeightScaleRow()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(new TextBlock
            {
                Text = "Height Depth (needs a Height map) — parallax strength",
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)), FontSize = 11
            });
            _heightScaleBox = new TextBox
            {
                Text = (_material?.HeightScale ?? 0.05f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(20, 20, 22)),
                Foreground = new SolidColorBrush(Color.FromRgb(233, 233, 237)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(44, 44, 50)),
                Padding = new Thickness(6, 3, 6, 3)
            };
            _heightScaleBox.TextChanged += (s, e) => MarkDirty();
            panel.Children.Add(_heightScaleBox);
            return panel;
        }

        private static float ReadHeightScale(TextBox box)
        {
            if (box != null && float.TryParse(box.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float f) && f >= 0f && !float.IsInfinity(f) && !float.IsNaN(f))
                return f;
            return 0.05f;
        }

        /// <summary>Parse a tiling box -> a positive float; blank/invalid/≤0 falls back to 1 (no tiling).</summary>
        private static float ReadTiling(TextBox box)
        {
            if (box != null && float.TryParse(box.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float f) && f > 0f && !float.IsInfinity(f))
                return f;
            return 1f;
        }

        private (Slider slider, TextBlock valueText, FrameworkElement row) CreateSliderRow(string label, double min, double max, double value)
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

            return (slider, valueText, grid);
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
            SchedulePreviewRefresh();
        }

        /// <summary>
        /// Debounced live-preview refresh so dragging a slider doesn't trigger an offscreen render
        /// on every tick.
        /// </summary>
        private void SchedulePreviewRefresh()
        {
            if (!_previewReady) return;
            if (_previewTimer == null)
            {
                _previewTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(150)
                };
                _previewTimer.Tick += (s, e) => { _previewTimer.Stop(); RefreshPreview(); };
            }
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        /// <summary>
        /// Builds a throwaway engine material from the current UI state, renders a sphere with it, and
        /// shows the result. The temp material is deleted immediately (caller-owned, not cached).
        /// </summary>
        /// <summary>Drag to orbit (yaw/pitch), scroll to zoom — re-renders the preview each change.</summary>
        private void WireOrbit(Image img)
        {
            img.Cursor = System.Windows.Input.Cursors.SizeAll;
            img.ToolTip = "Drag to orbit · scroll to zoom";
            img.MouseLeftButtonDown += (s, e) => { _orbiting = true; _orbitLast = e.GetPosition(img); img.CaptureMouse(); };
            img.MouseLeftButtonUp += (s, e) => { _orbiting = false; img.ReleaseMouseCapture(); };
            img.MouseMove += (s, e) =>
            {
                if (!_orbiting) return;
                var p = e.GetPosition(img);
                _orbitYaw += (p.X - _orbitLast.X) * 0.01;
                _orbitPitch += (p.Y - _orbitLast.Y) * 0.01;
                if (_orbitPitch > 1.5) _orbitPitch = 1.5; else if (_orbitPitch < -1.5) _orbitPitch = -1.5;
                _orbitLast = p;
                RefreshPreview();
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
            long mat = -1;
            try
            {
                var vmat = GetMaterialFromUI();
                if (!string.IsNullOrEmpty(_materialPath))
                    vmat.ResolvePathsAbsolute(Path.GetDirectoryName(_materialPath));

                mat = Core.Services.MaterialService.Instance.BuildEngineMaterial(vmat);
                if (mat >= 0)
                {
                    var img = Core.Services.Rendering.AssetPreviewRenderer.RenderMaterialSphere(mat, 768, (float)_orbitYaw, (float)_orbitPitch, (float)_orbitZoom);
                    if (img != null) _previewImage.Source = img;
                }
            }
            catch { }
            finally { if (mat >= 0) { try { VortexAPI.DeleteMaterial(mat); } catch { } } }
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
            SelectComboText(_shaderTypeCombo, _material.ShaderType);
            SelectComboText(_renderModeCombo, _material.BlendMode);
            _shaderAssetPath = _material.ShaderAsset;
            UpdateShaderSlotUi();
            _footstepSoundPath = _material.FootstepSound;
            UpdateFootstepSlotUi();
            var tl = _material.UVTiling;
            if (_tilingUBox != null) _tilingUBox.Text = ((tl != null && tl.Length > 0 && tl[0] > 0f) ? tl[0] : 1f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            if (_tilingVBox != null) _tilingVBox.Text = ((tl != null && tl.Length > 1 && tl[1] > 0f) ? tl[1] : 1f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

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
            SetTexturePreview(_material.HeightTexture, _heightPreview, _heightPathText);
            _heightPath = _material.HeightTexture;
            if (_heightScaleBox != null) _heightScaleBox.Text = _material.HeightScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

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
                HeightTexture = _heightPath,
                HeightScale = ReadHeightScale(_heightScaleBox),
                TwoSided = _twoSidedCheck.IsChecked == true,
                CastShadows = _castShadowsCheck.IsChecked == true,
                ReceiveShadows = _receiveShadowsCheck.IsChecked == true,
                // ShaderType drives the engine's lit/unlit decision (MaterialService); RenderMode is the
                // blend mode. Both combos hold ComboBoxItems, so read .Content, not ToString() (which
                // would yield "System.Windows.Controls.ComboBoxItem").
                ShaderType = GetComboText(_shaderTypeCombo, "Standard PBR"),
                BlendMode = GetComboText(_renderModeCombo, "Opaque"),
                ShaderAsset = string.IsNullOrEmpty(_shaderAssetPath) ? null : _shaderAssetPath,
                FootstepSound = string.IsNullOrEmpty(_footstepSoundPath) ? null : _footstepSoundPath,
                UVTiling = new float[] { ReadTiling(_tilingUBox), ReadTiling(_tilingVBox) }
            };

            if (_baseColorPreview.Background is SolidColorBrush brush)
                mat.SetBaseColor(brush.Color);

            return mat;
        }

        // ---- Shader Asset slot: assign a custom .vshader/.hlsl to a material, with a live graphical link ----
        private string _shaderAssetPath;
        private TextBlock _shaderAssetText;
        private Border _shaderNode;
        private TextBlock _shaderNodeLabel;
        private TextBlock _shaderWireArrow;

        private UIElement BuildShaderSlot()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            _shaderAssetText = new TextBlock
            {
                Text = "Built-in (from Shader type)",
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 155)),
                FontSize = 12, Margin = new Thickness(0, 0, 0, 6),
                TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
            };
            panel.Children.Add(_shaderAssetText);

            var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            btns.Children.Add(MiniButton("Browse…", () => BrowseShaderAsset()));
            btns.Children.Add(MiniButton("Clear", () => SetShaderAsset(null)));
            btns.Children.Add(MiniButton("Edit in VS", () => EditShaderInVs()));
            panel.Children.Add(btns);

            // graphical link: [Shader] ──▶ [Material]  (the shader node + arrow turn green when a shader is assigned)
            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            _shaderNode = LinkNode("Built-in", out _shaderNodeLabel);
            row.Children.Add(_shaderNode);
            _shaderWireArrow = new TextBlock { Text = "  ──▶  ", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 126)) };
            row.Children.Add(_shaderWireArrow);
            row.Children.Add(LinkNode("Material", out var _matLabel));
            panel.Children.Add(row);
            return panel;
        }

        private static Border LinkNode(string text, out TextBlock label)
        {
            label = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return new Border
            {
                MinWidth = 92, Height = 30, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x34)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(120, 120, 126)), BorderThickness = new Thickness(1),
                Child = label
            };
        }

        private Button MiniButton(string text, Action onClick)
        {
            var b = new Button
            {
                Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(9, 3, 9, 3),
                FontSize = 11, Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
                Foreground = Brushes.White, BorderThickness = new Thickness(0)
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        private void BrowseShaderAsset()
        {
            var proj = Editor.Core.Data.ProjectData.Current?.Path;
            string startDir = null;
            if (!string.IsNullOrEmpty(proj))
            {
                var sd = System.IO.Path.Combine(proj, "Assets", "Shaders");
                if (System.IO.Directory.Exists(sd)) startDir = sd;
            }
            // STA-thread picker — a WPF file dialog on the live UI thread deadlocks against the DX12/DXGI COM apartment.
            var picked = Editor.Core.Util.FilePicker.OpenFile("HLSL Shader (*.hlsl)|*.hlsl|Legacy shader asset (*.vshader)|*.vshader|All files|*.*", "Assign Shader", startDir);
            if (!string.IsNullOrEmpty(picked)) SetShaderAsset(picked);
        }

        private void SetShaderAsset(string path)
        {
            _shaderAssetPath = string.IsNullOrEmpty(path) ? null : MakeProjectRelative(path);
            UpdateShaderSlotUi();
            MarkDirty();   // fires the debounced preview refresh; the link + preview reflect the change live
        }

        // ---- Footstep Sound slot: assign a step clip / .vsndc container to this material (played by FootstepAudio) ----
        private string _footstepSoundPath;
        private TextBlock _footstepSoundText;

        private UIElement BuildFootstepSlot()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            _footstepSoundText = new TextBlock
            {
                Text = "None (silent when walked on)",
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 155)),
                FontSize = 12, Margin = new Thickness(0, 0, 0, 6),
                TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
            };
            panel.Children.Add(_footstepSoundText);
            var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            btns.Children.Add(MiniButton("Browse…", () => BrowseFootstepSound()));
            btns.Children.Add(MiniButton("Clear", () => SetFootstepSound(null)));
            panel.Children.Add(btns);
            return panel;
        }

        private void BrowseFootstepSound()
        {
            var proj = Editor.Core.Data.ProjectData.Current?.Path;
            string startDir = null;
            if (!string.IsNullOrEmpty(proj))
            {
                var ad = System.IO.Path.Combine(proj, "Assets", "Audio");
                if (System.IO.Directory.Exists(ad)) startDir = ad;
            }
            // STA-thread picker — a WPF file dialog on the live UI thread deadlocks against the DX12/DXGI COM apartment.
            var picked = Editor.Core.Util.FilePicker.OpenFile("Audio + Containers|*.wav;*.mp3;*.ogg;*.flac;*.vsndc|All files|*.*", "Assign Footstep Sound", startDir);
            if (!string.IsNullOrEmpty(picked)) SetFootstepSound(picked);
        }

        private void SetFootstepSound(string path)
        {
            _footstepSoundPath = string.IsNullOrEmpty(path) ? null : MakeProjectRelative(path);
            UpdateFootstepSlotUi();
            MarkDirty();
        }

        private void UpdateFootstepSlotUi()
        {
            bool set = !string.IsNullOrEmpty(_footstepSoundPath);
            if (_footstepSoundText != null)
                _footstepSoundText.Text = set ? ("Step: " + System.IO.Path.GetFileName(_footstepSoundPath)) : "None (silent when walked on)";
        }

        private void EditShaderInVs()
        {
            var hlsl = ResolveShaderHlsl(_shaderAssetPath);
            if (!string.IsNullOrEmpty(hlsl) && System.IO.File.Exists(hlsl))
            {
                try { Editor.Core.Services.ScriptingService.OpenInVisualStudio(hlsl); } catch { }
            }
            else MessageBox.Show("Assign a shader first (Browse…); then Edit in VS opens its .hlsl.", "Shader",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void UpdateShaderSlotUi()
        {
            bool linked = !string.IsNullOrEmpty(_shaderAssetPath);
            string name = linked ? System.IO.Path.GetFileNameWithoutExtension(_shaderAssetPath) : null;
            var green = new SolidColorBrush(Color.FromRgb(90, 200, 120));
            var grey = new SolidColorBrush(Color.FromRgb(120, 120, 126));
            if (_shaderAssetText != null) _shaderAssetText.Text = linked ? ("Custom: " + name) : "Built-in (from Shader type)";
            if (_shaderNodeLabel != null) _shaderNodeLabel.Text = linked ? name : "Built-in";
            if (_shaderWireArrow != null) _shaderWireArrow.Foreground = linked ? green : grey;
            if (_shaderNode != null) _shaderNode.BorderBrush = linked ? green : grey;
        }

        private static string MakeProjectRelative(string path)
        {
            var proj = Editor.Core.Data.ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(proj) || string.IsNullOrEmpty(path) || !System.IO.Path.IsPathRooted(path)) return path;
            try
            {
                var pu = new Uri(proj.EndsWith("\\") ? proj : proj + "\\");
                return Uri.UnescapeDataString(pu.MakeRelativeUri(new Uri(path)).ToString());
            }
            catch { return path; }
        }

        private static string ResolveShaderHlsl(string shaderAsset)
        {
            if (string.IsNullOrEmpty(shaderAsset)) return null;
            var proj = Editor.Core.Data.ProjectData.Current?.Path ?? "";
            string full = System.IO.Path.IsPathRooted(shaderAsset) ? shaderAsset : System.IO.Path.Combine(proj, shaderAsset);
            if (full.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase)) return full;
            try
            {
                var vs = Editor.Core.Assets.VortexShader.Load(full);
                if (vs != null && !string.IsNullOrEmpty(vs.PixelShaderPath))
                {
                    var p = vs.PixelShaderPath;
                    var h = System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(proj, p);
                    if (System.IO.File.Exists(h)) return h;
                }
            }
            catch { }
            var sib = System.IO.Path.ChangeExtension(full, ".hlsl");
            return System.IO.File.Exists(sib) ? sib : full;
        }

        private static string GetComboText(ComboBox combo, string fallback)
        {
            if (combo?.SelectedItem is ComboBoxItem item && item.Content != null)
                return item.Content.ToString();
            return combo?.SelectedItem?.ToString() ?? fallback;
        }

        private static void SelectComboText(ComboBox combo, string text)
        {
            if (combo == null || string.IsNullOrEmpty(text)) return;
            foreach (var obj in combo.Items)
            {
                if (obj is ComboBoxItem item &&
                    string.Equals(item.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
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

        /// <summary>Fired after a material is saved (Apply or Save As), with the saved .vmat path. Lets whoever opened
        /// this editor (e.g. the Model Editor) reload the .vmat and re-render its OWN preview live.</summary>
        public static event Action<string> MaterialSaved;

        /// <summary>Refresh EVERYTHING after a save so no preview is stale: drop the cached engine material, re-render
        /// this editor's own sphere, invalidate the Asset Browser thumbnails, force the live scene viewport to rebuild
        /// + redraw the material, and notify the opener (Model Editor) to reload. Called by both Apply and Save As.</summary>
        private void OnMaterialSaved()
        {
            try { Editor.Core.Services.MaterialService.Instance.InvalidateVortexMaterial(_materialPath); } catch { }
            try { Editor.Editors.WorldEditor.Components.AssetBrowser.AssetBrowserView.InvalidateMaterialThumbnails(); } catch { }
            try { RefreshPreview(); } catch { }                                                              // this editor's sphere
            try { Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit(); } catch { } // live scene
            try { MaterialSaved?.Invoke(_materialPath); } catch { }                                          // parent Model Editor
        }

        /// <summary>Ctrl+S = Apply (save), Ctrl+Shift+S = Save As — handled HERE so the keystroke saves the MATERIAL
        /// instead of bubbling up to MainWindow, which would save the SCENE (the "save doesn't refresh" report).</summary>
        protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            // Ignore OS key-repeat: holding Ctrl+S must save ONCE, not re-run the save + heavy preview re-render ~30×/sec.
            if (e.IsRepeat) return;
            if (e.Key == System.Windows.Input.Key.S && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
            {
                if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) SaveAs_Click(this, null);
                else Apply_Click(this, null);
                e.Handled = true;
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
                    OnMaterialSaved();   // preview + thumbnails + live scene + opener, all in one broadcast
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
            var proj = Editor.Core.Data.ProjectData.Current?.Path;
            var startDir = string.IsNullOrEmpty(proj) ? null : System.IO.Path.Combine(proj, "Assets", "Materials");
            // STA-thread picker — a WPF file dialog on the live UI thread deadlocks against the DX12/DXGI COM apartment.
            var savePath = Editor.Core.Util.FilePicker.SaveFile("Vortex Material|*.vmat", "Save Material", _materialNameBox.Text + ".vmat", ".vmat", startDir);
            if (!string.IsNullOrEmpty(savePath))
            {
                _materialPath = savePath;
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
                    OnMaterialSaved();
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
