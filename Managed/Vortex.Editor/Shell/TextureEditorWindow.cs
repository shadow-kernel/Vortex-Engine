using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Serialization;
using VortexEditor.Shell.AssetImport;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Texture Editor (port of the Windows TextureEditorDialog, Unity-style import settings): the texture over a
    /// checkerboard with zoom / pan and R / G / B / A channel views, its file information, the texture type (normal maps
    /// get the DirectX / OpenGL convention), sRGB, mipmaps, wrap / filter / anisotropy, compression quality and the asset's
    /// tags. Apply stores the settings in the asset's .vmeta (ImportSettings, same keys as the Windows editor); Revert
    /// re-reads them.
    /// </summary>
    public sealed class TextureEditorWindow : Window
    {
        public static void Open(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) { EditorCommands.Toast("Texture file not found"); return; }
            EditorWindows.Show(new TextureEditorWindow(fullPath));
        }

        public static readonly string[] TextureTypes = { "Default", "Normal Map", "Sprite (UI)", "Cursor", "Lightmap", "Single Channel" };
        public static readonly string[] WrapModes = { "Repeat", "Clamp", "Mirror", "Mirror Once" };
        public static readonly string[] FilterModes = { "Point (No Filter)", "Bilinear", "Trilinear" };
        public static readonly string[] AnisoLevels = { "Disabled", "2x", "4x", "8x", "16x" };
        public static readonly string[] Compressions = { "None", "Low Quality", "Normal Quality", "High Quality" };

        private readonly string _path;
        private TextureImage _image;
        private bool _dirty, _loading;
        private readonly TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>();

        private readonly TextureViewer _viewer = new TextureViewer { Background = Ui.PreviewBg };
        private readonly TextBlock _size = Value(), _formatText = Value(), _mips = Value(), _fileSize = Value(), _pathText = Value(), _alpha = Value();
        private readonly ComboBox _type = Combo(TextureTypes, 0), _wrap = Combo(WrapModes, 0), _filter = Combo(FilterModes, 1), _aniso = Combo(AnisoLevels, 1), _compression = Combo(Compressions, 2);
        private readonly CheckBox _srgb = new CheckBox { Content = "sRGB (Color Texture)", IsChecked = true };
        private readonly CheckBox _mipmaps = new CheckBox { Content = "Generate Mipmaps", IsChecked = true };
        private readonly RadioButton _dx = new RadioButton { Content = "DirectX", IsChecked = true, GroupName = "nfmt", Margin = new Thickness(0, 0, 16, 0) };
        private readonly RadioButton _gl = new RadioButton { Content = "OpenGL", GroupName = "nfmt" };
        private readonly StackPanel _normalSettings = new StackPanel { IsVisible = false, Margin = new Thickness(0, 6, 0, 4) };
        private readonly WrapPanel _tags = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        private readonly TextBlock _status = new TextBlock { Text = "Ready", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private Button _apply;

        public Task<bool> WhenReady => _ready.Task;
        public TextureViewer Viewer => _viewer;
        public bool IsDirty => _dirty;

        public TextureEditorWindow(string texturePath)
        {
            _path = texturePath;
            Title = "Texture Editor — " + Path.GetFileName(texturePath);
            Width = 1060; Height = 720; MinWidth = 760; MinHeight = 540;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            _status.Foreground = Ui.Brush("VxTextSecondaryBrush");

            var main = new Grid { ColumnDefinitions = new ColumnDefinitions("*,300"), RowDefinitions = new RowDefinitions("*,Auto") };
            var previewHost = new Border { Margin = new Thickness(14), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _viewer };
            main.Children.Add(previewHost);
            var props = BuildProperties();
            Grid.SetColumn(props, 1); main.Children.Add(props);
            var status = BuildStatusBar();
            Grid.SetRow(status, 1); Grid.SetColumnSpan(status, 2); main.Children.Add(status);
            Content = main;

            _type.SelectionChanged += (s, e) =>
            {
                _normalSettings.IsVisible = _type.SelectedIndex == 1;
                if (!_loading) { _srgb.IsChecked = _type.SelectedIndex != 1 && _type.SelectedIndex != 5; MarkDirty(); }
            };
            foreach (var c in new[] { _wrap, _filter, _aniso, _compression }) c.SelectionChanged += (s, e) => MarkDirty();
            _srgb.IsCheckedChanged += (s, e) => MarkDirty();
            _mipmaps.IsCheckedChanged += (s, e) => { MarkDirty(); UpdateMipText(); };
            _dx.IsCheckedChanged += (s, e) => MarkDirty();
            Activated += (s, e) => LoadTags();   // the tag editor may have changed them
            KeyDown += (s, e) => { if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Meta)) { Apply(); e.Handled = true; } };
            Opened += (s, e) => Dispatcher.UIThread.Post(() => _ = LoadAsync(), DispatcherPriority.Background);
        }

        private static TextBlock Value() => new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };

        private static ComboBox Combo(string[] items, int index)
        {
            var c = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 24 };
            foreach (var i in items) c.Items.Add(i);
            c.SelectedIndex = index;
            return c;
        }

        private static TextBlock Label(string text, double top = 0)
            => new TextBlock { Text = text, FontSize = 11, Foreground = Ui.Brush("VxTextSecondaryBrush"), Margin = new Thickness(0, top, 0, 4) };

        private Control BuildProperties()
        {
            var stack = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };
            stack.Children.Add(new TextBlock { Text = "Texture Properties", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 12) });

            var info = new Grid { ColumnDefinitions = new ColumnDefinitions("74,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"), Margin = new Thickness(0, 6, 0, 0) };
            void Info(int row, string label, TextBlock value)
            {
                var l = new TextBlock { Text = label, FontSize = 12, Foreground = Ui.Brush("VxTextTertiaryBrush"), Margin = new Thickness(0, 1, 0, 1) };
                Grid.SetRow(l, row); info.Children.Add(l);
                Grid.SetRow(value, row); Grid.SetColumn(value, 1); value.Margin = new Thickness(0, 1, 0, 1); info.Children.Add(value);
            }
            Info(0, "Size:", _size); Info(1, "Format:", _formatText); Info(2, "Alpha:", _alpha); Info(3, "Mipmaps:", _mips); Info(4, "File Size:", _fileSize); Info(5, "Path:", _pathText);
            var infoStack = new StackPanel();
            infoStack.Children.Add(Ui.Header("File information", new Thickness(0)));
            infoStack.Children.Add(info);
            stack.Children.Add(new Border { Background = Ui.Brush("VxFieldBrush"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 14), Child = infoStack });

            stack.Children.Add(Label("Texture Type"));
            stack.Children.Add(_type);
            _normalSettings.Children.Add(Label("Normal Map Format"));
            var radios = new StackPanel { Orientation = Orientation.Horizontal };
            radios.Children.Add(_dx); radios.Children.Add(_gl);
            ToolTip.SetTip(_dx, "Green channel points down (Y−), the engine's convention");
            ToolTip.SetTip(_gl, "Green channel points up (Y+) — Blender / Substance OpenGL exports");
            _normalSettings.Children.Add(radios);
            stack.Children.Add(_normalSettings);

            stack.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 4) });
            stack.Children.Add(Ui.Header("Import settings"));
            stack.Children.Add(_srgb);
            stack.Children.Add(_mipmaps);
            stack.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 4) });
            stack.Children.Add(Label("Wrap Mode", 6)); stack.Children.Add(_wrap);
            stack.Children.Add(Label("Filter Mode", 10)); stack.Children.Add(_filter);
            stack.Children.Add(Label("Anisotropic Filtering", 10)); stack.Children.Add(_aniso);

            stack.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 4) });
            stack.Children.Add(Ui.Header("Compression"));
            stack.Children.Add(Label("Compression Quality")); stack.Children.Add(_compression);

            stack.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 4) });
            stack.Children.Add(Ui.Header("Tags"));
            stack.Children.Add(_tags);
            var edit = Ui.Button("Edit Tags…", () => EditorWindows.AssetTags(_path), "Add or remove this texture's tags");
            edit.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(edit);

            return new Border { Background = Ui.Brush("VxPanelBrush"), BorderBrush = Ui.Brush("VxHairlineBrush"), BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = stack } };
        }

        private Control BuildStatusBar()
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            g.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _apply = Ui.Button("Apply", Apply, "Save the import settings to the texture's .vmeta (" + Keys.Chord("S") + ")", "accent", 80);
            buttons.Children.Add(_apply);
            buttons.Children.Add(Ui.Button("Revert", () => _ = RevertAsync(), "Reload the texture and its saved settings", null, 80));
            buttons.Children.Add(Ui.Button("Close", Close, null, null, 80));
            Grid.SetColumn(buttons, 1); g.Children.Add(buttons);
            return new Border { Background = Ui.Brush("VxToolbarBrush"), BorderBrush = Ui.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 8), Child = g };
        }

        // ================================================================== load / settings

        private async Task LoadAsync()
        {
            _loading = true;
            try
            {
                _viewer.ShowLoading();
                _image = await TextureImage.LoadAsync(_path);
                _viewer.Image = _image;
                _size.Text = _image.Width > 0 ? _image.Width + " x " + _image.Height : "—";
                _formatText.Text = _image.FormatName + (string.IsNullOrEmpty(_image.PixelFormat) ? "" : " · " + _image.PixelFormat);
                ToolTip.SetTip(_formatText, _formatText.Text);
                _alpha.Text = _image.CanPreview ? (_image.HasAlpha ? "Yes (transparent pixels)" : "No (opaque)") : "—";
                _fileSize.Text = ModelDocument.FormatBytes(_image.FileSize);
                _pathText.Text = Ui.ProjectRelative(_path);
                ToolTip.SetTip(_pathText, _path);
                AutoDetectTextureType(Path.GetFileName(_path));
                LoadImportSettings();
                UpdateMipText();
                LoadTags();
                SetDirty(false);
                _status.Text = _image.CanPreview ? "Loaded: " + Path.GetFileName(_path) : (_image.Error ?? "Loaded (no preview)");
                _ready.TrySetResult(true);
            }
            catch (Exception ex) { _status.Text = "Error: " + ex.Message; _ready.TrySetResult(false); }
            finally { _loading = false; }
        }

        private void UpdateMipText()
        {
            if (_image == null) return;
            if (_image.StoredMipLevels > 1) _mips.Text = _image.StoredMipLevels + " levels (stored)";
            else if (_mipmaps.IsChecked == true) _mips.Text = _image.FullMipChain + " levels";
            else _mips.Text = "1 level (mipmaps off)";
        }

        /// <summary>Guess the type from the file name (normal / roughness / metallic / AO / sprite) — saved settings win.</summary>
        private void AutoDetectTextureType(string fileName)
        {
            var n = fileName.ToLowerInvariant();
            if (n.Contains("normal") || n.Contains("_n.") || n.Contains("_nor") || n.Contains("_nrm")) { _type.SelectedIndex = 1; _srgb.IsChecked = false; }
            else if (n.Contains("_ao") || n.Contains("roughness") || n.Contains("_rough") || n.Contains("metallic") || n.Contains("_metal")) { _type.SelectedIndex = 5; _srgb.IsChecked = false; }
            else if (n.Contains("sprite") || n.Contains("ui")) _type.SelectedIndex = 2;
            else { _type.SelectedIndex = 0; _srgb.IsChecked = true; }
        }

        private string MetaPath => _path + AssetDatabase.MetaFileExtension;

        /// <summary>The asset's .vmeta (JSON, as the asset database writes it; older XML files are read too).</summary>
        private AssetMetadata LoadMeta()
        {
            if (!File.Exists(MetaPath)) return null;
            try { var m = DataSerializer.LoadFromJson<AssetMetadata>(MetaPath); if (m != null) return m; } catch { }
            try { return AssetMetadataService.Instance.LoadMetadata(MetaPath); } catch { return null; }
        }

        private void LoadImportSettings()
        {
            var s = LoadMeta()?.ImportSettings;
            if (s == null || s.Count == 0) return;
            bool was = _loading; _loading = true;
            try
            {
                void Idx(ComboBox c, string key) { if (s.TryGetValue(key, out var v) && int.TryParse(v, out var i) && i >= 0 && i < c.ItemCount) c.SelectedIndex = i; }
                void Chk(CheckBox c, string key) { if (s.TryGetValue(key, out var v) && bool.TryParse(v, out var b)) c.IsChecked = b; }
                Idx(_type, "textureType");       // first: the type resets sRGB
                Chk(_srgb, "sRGB");
                Chk(_mipmaps, "generateMipmaps");
                Idx(_wrap, "wrapMode");
                Idx(_filter, "filterMode");
                Idx(_aniso, "anisoLevel");
                Idx(_compression, "compression");
                if (s.TryGetValue("normalMapFormat", out var nf)) { _gl.IsChecked = string.Equals(nf, "OpenGL", StringComparison.OrdinalIgnoreCase); _dx.IsChecked = !_gl.IsChecked; }
                _normalSettings.IsVisible = _type.SelectedIndex == 1;
            }
            finally { _loading = was; }
        }

        private void LoadTags()
        {
            _tags.Children.Clear();
            var meta = LoadMeta();
            var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (meta != null)
            {
                try { foreach (var t in AssetTagService.Instance.GetTags(meta.Guid)) tags.Add(t); } catch { }
                if (meta.Tags != null) foreach (var t in meta.Tags) tags.Add(t);
            }
            foreach (var t in tags)
                _tags.Children.Add(new Border { Background = Ui.Brush("VxAccentBrush"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 5, 5), Child = new TextBlock { Text = t, FontSize = 11, Foreground = Brushes.White } });
            if (tags.Count == 0) _tags.Children.Add(new TextBlock { Text = "No tags", FontSize = 11, FontStyle = FontStyle.Italic, Foreground = Ui.Brush("VxTextTertiaryBrush") });
        }

        private void MarkDirty() { if (!_loading) SetDirty(true); }

        private void SetDirty(bool d)
        {
            _dirty = d;
            Title = "Texture Editor — " + Path.GetFileName(_path) + (d ? " •" : "");
            if (d) _status.Text = "Unsaved changes — Apply stores them in the .vmeta";
        }

        /// <summary>Store the import settings in the texture's .vmeta (keeping its GUID and tags).</summary>
        public void Apply()
        {
            try
            {
                var meta = LoadMeta();
                if (meta == null)
                {
                    ModelImportPipeline.WriteMeta(_path, AssetType.Texture);
                    meta = LoadMeta();
                }
                if (meta == null) { _status.Text = "Could not resolve asset metadata."; return; }
                if (meta.ImportSettings == null) meta.ImportSettings = new Dictionary<string, string>();
                var s = meta.ImportSettings;
                s["textureType"] = _type.SelectedIndex.ToString();
                s["sRGB"] = (_srgb.IsChecked == true).ToString();
                s["generateMipmaps"] = (_mipmaps.IsChecked == true).ToString();
                s["wrapMode"] = _wrap.SelectedIndex.ToString();
                s["filterMode"] = _filter.SelectedIndex.ToString();
                s["anisoLevel"] = _aniso.SelectedIndex.ToString();
                s["compression"] = _compression.SelectedIndex.ToString();
                s["normalMapFormat"] = _gl.IsChecked == true ? "OpenGL" : "DirectX";
                try { var fi = new FileInfo(_path); meta.LastModified = fi.LastWriteTime; meta.FileSize = fi.Length; } catch { }
                AssetDatabase.Instance.SaveMetadata(meta, MetaPath);
                SetDirty(false);
                _status.Text = "Import settings saved.";
            }
            catch (Exception ex) { _status.Text = "Error: " + ex.Message; }
        }

        private async Task RevertAsync()
        {
            var ch = _viewer.Channel;
            await LoadAsync();
            _viewer.SetChannel(ch);
            _status.Text = "Settings reverted.";
        }

        // ================================================================== test hooks
        internal void SetTextureType(int i) => _type.SelectedIndex = i;
        internal int TextureTypeIndex => _type.SelectedIndex;
        internal bool Srgb => _srgb.IsChecked == true;
        internal string SizeText => _size.Text;
    }
}
