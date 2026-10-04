using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Editor.Core.Assets;
using Editor.Core.Audio;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.DllWrapper;
using VortexEditor.Controls;
using VortexEditor.Panels.Inspector;
using VortexEditor.Services;
using VortexEditor.Shell.Material;

namespace VortexEditor.Shell
{
    /// <summary>
    /// The material editor (port of the Windows MaterialEditorDialog): every field of a .vmat — name, shader type, custom
    /// shader asset (+ new / edit in IDE), footstep sound, render mode, base colour (colour picker), opacity, alpha
    /// cutoff, metallic, roughness, normal strength / format, AO, height depth, UV tiling + offset, emission, two-sided,
    /// shadows — and every texture map (thumbnail, drag &amp; drop, browse, clear), with a large live preview on a sphere,
    /// cube or plane (orbit / pan / zoom) that re-renders while you edit. Save writes the .vmat and pushes it to every
    /// object in the open scene that uses it; Revert reloads the file; Save As forks it; Cmd+Z / Cmd+Shift+Z undo / redo
    /// edits. Modeless — one window per material.
    /// </summary>
    public sealed class MaterialEditorWindow : Window
    {
        // ================================================================= entry points
        /// <summary>Open (or bring to front) the editor for a .vmat.</summary>
        public static void Open(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) { CreateNew(); return; }
            var existing = Find(fullPath);
            if (existing != null) { existing.Activate(); return; }
            EditorWindows.Show(new MaterialEditorWindow(fullPath));
        }

        /// <summary>An untitled material (Save asks where to store it).</summary>
        public static void CreateNew() => EditorWindows.Show(new MaterialEditorWindow(null));

        /// <summary>The open editor for a .vmat, or null.</summary>
        public static MaterialEditorWindow Find(string fullPath)
            => EditorKit.OpenWindows<MaterialEditorWindow>().FirstOrDefault(w => w._path != null && EditorKit.SamePath(w._path, fullPath));

        /// <summary>Raised after a material was saved (absolute .vmat path) — other editors (model / prefab previews)
        /// reload it. The scene, the thumbnails and the material cache are already refreshed when this fires.</summary>
        public static event Action<string> MaterialSaved;

        // ================================================================= state
        private static readonly string[] ShaderTypeValues = { "StandardPBR", "Unlit", "Transparent" };
        private static readonly string[] BlendValues = { "Opaque", "AlphaTest", "AlphaBlend", "Additive" };
        private static readonly string[] ShaderPatterns = { "*.hlsl", "*.metal", "*.glsl", "*.vshader" };
        private static readonly string[] AudioPatterns = { "*.wav", "*.mp3", "*.ogg", "*.flac", "*.vsndc" };
        private static readonly JsonSerializerOptions SnapOptions = new JsonSerializerOptions { WriteIndented = false };

        private string _path;                 // absolute .vmat path, null = untitled
        private VortexMaterial _mat;          // working copy — texture paths ABSOLUTE (converted on save)
        private string _savedSnap;            // state of the file on disk
        private string _lastSnap;             // state after the last recorded edit
        private string _lastKey;
        private DateTime _lastEditAt;
        private readonly List<string> _undo = new List<string>();
        private readonly List<string> _redo = new List<string>();
        private readonly List<Action> _refreshers = new List<Action>();
        private readonly List<Action> _notes = new List<Action>();
        private bool _closeConfirmed;
        private DateTime _lastSaveAt;
        private ulong _stepVoice = VortexAudio.InvalidVoice;

        private MaterialPreviewPane _preview;
        private readonly TextBlock _status = new TextBlock { Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _users = new TextBlock { Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        private readonly TextBlock _fileText = new TextBlock { Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center };
        private Button _revertButton;

        /// <summary>The .vmat being edited (null while untitled).</summary>
        public string MaterialPath => _path;
        public bool IsDirty => Snapshot() != _savedSnap;
        internal MaterialPreviewPane PreviewPane => _preview;
        internal VortexMaterial Working => _mat;

        public MaterialEditorWindow() : this(null) { }

        public MaterialEditorWindow(string vmatPath)
        {
            _path = string.IsNullOrEmpty(vmatPath) ? null : Path.GetFullPath(vmatPath);
            Width = 1360; Height = 860; MinWidth = 1040; MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            bool readError = LoadMaterial();
            Content = BuildLayout();
            UpdateTitle();
            UpdateUsers();
            if (readError) SetStatus("Could not read " + Path.GetFileName(_path) + " — showing defaults (saving overwrites it).", true);
            else SetStatus(_path == null ? "New material — Save asks where to store it." : "Loaded " + Path.GetFileName(_path));

            AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
            Activated += (s, e) => { _preview?.ReloadShadersIfChanged(); UpdateUsers(); };   // hot-reload edited shaders, re-count scene users
            Closing += OnClosing;
            Closed += OnClosed;
            Opened += OnOpened;
            EditorSession.Instance.ProjectClosed += OnProjectClosed;
        }

        // ================================================================= load / snapshot / undo
        /// <summary>Load the file into the working copy. True when the file exists but could not be parsed.</summary>
        private bool LoadMaterial()
        {
            VortexMaterial m = null;
            bool exists = _path != null && File.Exists(_path);
            if (exists) m = VortexMaterial.Load(_path);
            bool readError = exists && m == null;
            if (m == null) m = new VortexMaterial { Name = _path != null ? Path.GetFileNameWithoutExtension(_path) : "New Material" };
            if (_path != null) { try { m.ResolvePathsAbsolute(Path.GetDirectoryName(_path)); } catch { } }
            Normalize(m);
            _mat = m;
            _savedSnap = _lastSnap = Snapshot();
            _lastKey = null;
            return readError;
        }

        private static void Normalize(VortexMaterial m)
        {
            m.BaseColor = Pad(m.BaseColor, 4, 1f);
            m.EmissiveColor = Pad(m.EmissiveColor, 3, 0f);
            m.UVTiling = Pad(m.UVTiling, 2, 1f);
            for (int i = 0; i < 2; i++) if (!(m.UVTiling[i] > 0f) || float.IsInfinity(m.UVTiling[i])) m.UVTiling[i] = 1f;   // 0 renders as 1 — show the truth
            m.UVOffset = Pad(m.UVOffset, 2, 0f);
            // The Windows editor wrote its combo labels ("Transparent", "Cutout") — the renderer only knows the canonical names.
            if (string.Equals(m.BlendMode, "Transparent", StringComparison.OrdinalIgnoreCase)) m.BlendMode = "AlphaBlend";
            else if (string.Equals(m.BlendMode, "Cutout", StringComparison.OrdinalIgnoreCase)) m.BlendMode = "AlphaTest";
            if (string.IsNullOrWhiteSpace(m.BlendMode)) m.BlendMode = "Opaque";
            if (string.IsNullOrWhiteSpace(m.ShaderType)) m.ShaderType = "StandardPBR";
            if (string.IsNullOrWhiteSpace(m.ShaderAsset)) m.ShaderAsset = null;
            if (string.IsNullOrWhiteSpace(m.FootstepSound)) m.FootstepSound = null;
        }

        private static float[] Pad(float[] a, int n, float fill)
        {
            if (a != null && a.Length == n) return a;
            var r = new float[n];
            for (int i = 0; i < n; i++) r[i] = a != null && i < a.Length ? a[i] : fill;
            return r;
        }

        private string Snapshot() => JsonSerializer.Serialize(_mat, SnapOptions);
        private static VortexMaterial FromSnapshot(string s) => JsonSerializer.Deserialize<VortexMaterial>(s, SnapOptions);

        /// <summary>Apply one edit: records undo (consecutive edits of the same field within a moment merge, so a slider
        /// drag is one step), refreshes the preview and the title.</summary>
        private void Edit(string key, Action apply)
        {
            string before = _lastSnap;
            apply();
            string after = Snapshot();
            if (after == before) return;
            bool merge = key != null && key == _lastKey && (DateTime.UtcNow - _lastEditAt).TotalMilliseconds < 800;
            if (!merge) { _undo.Add(before); if (_undo.Count > 200) _undo.RemoveAt(0); }
            _redo.Clear();
            _lastKey = key; _lastEditAt = DateTime.UtcNow; _lastSnap = after;
            Changed();
        }

        /// <summary>Apply an edit from code (tools / tests) and refresh every control.</summary>
        internal void ApplyEdit(string key, Action<VortexMaterial> apply) { Edit(key, () => apply(_mat)); RefreshControls(); }

        public void Undo()
        {
            if (_undo.Count == 0) { SetStatus("Nothing to undo"); return; }
            _redo.Add(_lastSnap);
            Restore(_undo[_undo.Count - 1]); _undo.RemoveAt(_undo.Count - 1);
            SetStatus("Undo");
        }

        public void Redo()
        {
            if (_redo.Count == 0) { SetStatus("Nothing to redo"); return; }
            _undo.Add(_lastSnap);
            Restore(_redo[_redo.Count - 1]); _redo.RemoveAt(_redo.Count - 1);
            SetStatus("Redo");
        }

        private void Restore(string snap)
        {
            var m = FromSnapshot(snap);
            if (m == null) return;
            _mat = m;
            _lastSnap = snap; _lastKey = null;
            RefreshControls();
            Changed();
        }

        private void Changed()
        {
            _preview?.MarkDirty();
            foreach (var n in _notes) n();
            UpdateTitle();
        }

        private void RefreshControls()
        {
            foreach (var r in _refreshers.ToList()) { try { r(); } catch { } }
        }

        // ================================================================= layout
        private Control BuildLayout()
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("352,*,384"), RowDefinitions = new RowDefinitions("*,Auto") };

            var left = new Border { Classes = { "sidebar", "hairline-right" }, Child = new ScrollViewer { Content = BuildProperties() } };
            grid.Children.Add(left);

            _preview = new MaterialPreviewPane(PreviewMaterial);
            Grid.SetColumn(_preview, 1);
            grid.Children.Add(_preview);

            var right = new Border { Classes = { "sidebar", "hairline-left" }, Child = new ScrollViewer { Content = BuildTextures() } };
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            var footer = BuildFooter();
            Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 3);
            grid.Children.Add(footer);
            return grid;
        }

        /// <summary>What the preview renders: the working copy (absolute texture paths already).</summary>
        private VortexMaterial PreviewMaterial() => FromSnapshot(Snapshot());

        private Control BuildProperties()
        {
            var s = new StackPanel { Margin = new Thickness(14, 8, 14, 18), Spacing = 3 };
            using (new EditorKit.RefresherScope(_refreshers))
            {
                s.Children.Add(new TextBlock { Text = "Material Properties", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 6) });
                s.Children.Add(PropertyRows.Row("Name", PropertyRows.Text(() => _mat.Name, v => Edit("name", () => _mat.Name = v), "Material name")));
                var fileRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                fileRow.Children.Add(_fileText);
                var reveal = EditorKit.IconButton("Folder", "Reveal in Finder", () => { if (_path != null) EditorCommands.RevealInFinder(_path); }, 13);
                Grid.SetColumn(reveal, 1); fileRow.Children.Add(reveal);
                s.Children.Add(PropertyRows.Row("File", fileRow));

                s.Children.Add(EditorKit.Section("Shader"));
                s.Children.Add(PropertyRows.Row("Shader type", PropertyRows.Choice(() => ShaderTypeIndex(_mat.ShaderType), v => Edit("shadertype", () => _mat.ShaderType = ShaderTypeValues[v]), "Standard PBR", "Unlit", "Transparent"), "Lighting model of the built-in shader (Unlit ignores lights)"));
                s.Children.Add(PropertyRows.Row("Shader asset", BuildShaderSlot(), "A custom shader file for this material"));

                s.Children.Add(EditorKit.Section("Surface"));
                s.Children.Add(PropertyRows.Row("Render mode", PropertyRows.Choice(() => BlendIndex(_mat.BlendMode), v => Edit("blend", () => _mat.BlendMode = BlendValues[v]), "Opaque", "Cutout (alpha test)", "Transparent (alpha blend)", "Additive")));
                s.Children.Add(PropertyRows.Row("Base color", ColorEditor("basecolor", "Base Color",
                    () => Color.FromRgb(ColorMath.B(_mat.BaseColor[0]), ColorMath.B(_mat.BaseColor[1]), ColorMath.B(_mat.BaseColor[2])),
                    c => { _mat.BaseColor[0] = c.R / 255f; _mat.BaseColor[1] = c.G / 255f; _mat.BaseColor[2] = c.B / 255f; })));
                s.Children.Add(MapNote(() => !string.IsNullOrEmpty(_mat.AlbedoTexture), "The Albedo map replaces this colour (maps are not tinted)."));
                s.Children.Add(PropertyRows.Row("Opacity", PropertyRows.SliderRow(() => _mat.BaseColor[3], v => Edit("opacity", () => _mat.BaseColor[3] = v), 0, 1), "Base colour alpha (used by Transparent / Cutout)"));
                s.Children.Add(PropertyRows.Row("Alpha cutoff", PropertyRows.SliderRow(() => _mat.AlphaCutoff, v => Edit("cutoff", () => _mat.AlphaCutoff = v), 0, 1), "Cutout: pixels below this alpha are discarded"));

                s.Children.Add(EditorKit.Section("PBR"));
                s.Children.Add(PropertyRows.Row("Metallic", PropertyRows.SliderRow(() => _mat.Metallic, v => Edit("metallic", () => _mat.Metallic = v), 0, 1)));
                s.Children.Add(MapNote(() => !string.IsNullOrEmpty(_mat.MetallicTexture), "The Metallic map replaces this value."));
                s.Children.Add(PropertyRows.Row("Roughness", PropertyRows.SliderRow(() => _mat.Roughness, v => Edit("roughness", () => _mat.Roughness = v), 0, 1)));
                s.Children.Add(MapNote(() => !string.IsNullOrEmpty(_mat.RoughnessTexture), "The Roughness map replaces this value."));
                s.Children.Add(PropertyRows.Row("Normal strength", PropertyRows.SliderRow(() => _mat.NormalStrength, v => Edit("normal", () => _mat.NormalStrength = v), 0, 2)));
                s.Children.Add(PropertyRows.Row("Normal format", PropertyRows.Choice(() => _mat.UseDirectXNormals ? 0 : 1, v => Edit("normalfmt", () => _mat.UseDirectXNormals = v == 0), "DirectX (Y−)", "OpenGL (Y+)"), "Green-channel convention of the normal map"));
                s.Children.Add(PropertyRows.Row("Ambient occl.", PropertyRows.SliderRow(() => _mat.AmbientOcclusion, v => Edit("ao", () => _mat.AmbientOcclusion = v), 0, 1), "Ambient occlusion strength"));
                s.Children.Add(MapNote(() => !string.IsNullOrEmpty(_mat.AOTexture), "The Ambient Occlusion map replaces this value."));
                s.Children.Add(PropertyRows.Row("Height depth", PropertyRows.SliderRow(() => _mat.HeightScale, v => Edit("height", () => _mat.HeightScale = v), 0, 0.2, "0.###"), "Parallax depth of the Height map (0 = flat, 0.02–0.08 typical)"));
                s.Children.Add(MapNote(() => string.IsNullOrEmpty(_mat.HeightTexture), "Needs a Height map (Texture Maps → Height)."));

                s.Children.Add(EditorKit.Section("UV"));
                s.Children.Add(PropertyRows.Row("Tiling", Pair(
                    PropertyRows.FloatBox(() => _mat.UVTiling[0], v => Edit("tilingu", () => _mat.UVTiling[0] = v), 0.1, 0.001f),
                    PropertyRows.FloatBox(() => _mat.UVTiling[1], v => Edit("tilingv", () => _mat.UVTiling[1] = v), 0.1, 0.001f)), "Texture repeats across the surface (U, V) — e.g. 8 keeps a floor texture crisp"));
                s.Children.Add(PropertyRows.Row("Offset", Pair(
                    PropertyRows.FloatBox(() => _mat.UVOffset[0], v => Edit("offsetu", () => _mat.UVOffset[0] = v), 0.05),
                    PropertyRows.FloatBox(() => _mat.UVOffset[1], v => Edit("offsetv", () => _mat.UVOffset[1] = v), 0.05)), "Texture offset (U, V)"));

                s.Children.Add(EditorKit.Section("Emission"));
                s.Children.Add(PropertyRows.Row("Color", ColorEditor("emissive", "Emissive Color",
                    () => Color.FromRgb(ColorMath.B(_mat.EmissiveColor[0]), ColorMath.B(_mat.EmissiveColor[1]), ColorMath.B(_mat.EmissiveColor[2])),
                    c => { _mat.EmissiveColor[0] = c.R / 255f; _mat.EmissiveColor[1] = c.G / 255f; _mat.EmissiveColor[2] = c.B / 255f; })));
                s.Children.Add(PropertyRows.Row("Strength", PropertyRows.SliderRow(() => _mat.EmissiveStrength, v => Edit("emissivestr", () => _mat.EmissiveStrength = v), 0, 10)));
                s.Children.Add(PropertyRows.Note("Strength sets the brightness of Unlit materials; the colour is saved with the material."));

                s.Children.Add(EditorKit.Section("Rendering"));
                s.Children.Add(PropertyRows.Row("Two sided", PropertyRows.Bool(() => _mat.TwoSided, v => Edit("twosided", () => _mat.TwoSided = v))));
                s.Children.Add(PropertyRows.Row("Cast shadows", PropertyRows.Bool(() => _mat.CastShadows, v => Edit("cast", () => _mat.CastShadows = v))));
                s.Children.Add(PropertyRows.Row("Receive shadows", PropertyRows.Bool(() => _mat.ReceiveShadows, v => Edit("receive", () => _mat.ReceiveShadows = v))));

                s.Children.Add(EditorKit.Section("Footstep sound"));
                s.Children.Add(BuildFootstepSlot());
            }
            EditorKit.CenterTextBoxes(s);
            return s;
        }

        /// <summary>A hint under a row that shows only while <paramref name="show"/> holds (re-evaluated on every edit).</summary>
        private TextBlock MapNote(Func<bool> show, string text)
        {
            var note = PropertyRows.Note(text);
            void Sync() => note.IsVisible = show();
            _notes.Add(Sync);
            Sync();
            return note;
        }

        private static Control Pair(Control a, Control b)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*") };
            var la = new TextBlock { Text = "U", Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
            var lb = new TextBlock { Text = "V", Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 5, 0) };
            Grid.SetColumn(a, 1); Grid.SetColumn(lb, 2); Grid.SetColumn(b, 3);
            g.Children.Add(la); g.Children.Add(a); g.Children.Add(lb); g.Children.Add(b);
            return g;
        }

        private static int ShaderTypeIndex(string t)
            => string.Equals(t, "Unlit", StringComparison.OrdinalIgnoreCase) ? 1 : string.Equals(t, "Transparent", StringComparison.OrdinalIgnoreCase) ? 2 : 0;

        private static int BlendIndex(string b)
        {
            if (string.Equals(b, "AlphaTest", StringComparison.OrdinalIgnoreCase) || string.Equals(b, "Cutout", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(b, "AlphaBlend", StringComparison.OrdinalIgnoreCase) || string.Equals(b, "Transparent", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(b, "Additive", StringComparison.OrdinalIgnoreCase)) return 3;
            return 0;
        }

        // ---------------------------------------------------------------- colour fields (swatch + hex + picker)
        private Control ColorEditor(string key, string title, Func<Color> get, Action<Color> set)
        {
            var swatch = new ColorSwatch { Width = 60, Height = 22, Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(swatch, "Click to pick a colour");
            var hex = new TextBox { MinWidth = 84, MinHeight = 22, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            if (Application.Current != null && Application.Current.TryFindResource("VxMono", out var mono) && mono is FontFamily ff) hex.FontFamily = ff;
            var pick = EditorKit.SmallButton("Pick…", null, "Open the colour picker");
            pick.Margin = new Thickness(6, 0, 0, 0);
            void Sync() { var c = get(); swatch.Color = c; if (!hex.IsFocused) hex.Text = ColorMath.ToHex(c, false); }
            void CommitHex()
            {
                if (ColorMath.TryParseHex(hex.Text, out var c)) Edit(key, () => set(Color.FromRgb(c.R, c.G, c.B)));
                Sync();
            }
            async Task OpenPicker()
            {
                var before = get();
                var result = await ColorPickerDialog.Pick(before, title, false, live: c => { set(c); swatch.Color = c; _preview?.MarkDirty(); }, owner: this);
                set(before);   // live preview values are not undo steps: restore, then record the final pick once
                if (result.HasValue) Edit(key, () => set(result.Value));
                Sync();
                _preview?.MarkDirty();
            }
            swatch.PointerPressed += (s, e) => { if (e.GetCurrentPoint(swatch).Properties.IsLeftButtonPressed) _ = OpenPicker(); };
            pick.Click += (s, e) => _ = OpenPicker();
            hex.LostFocus += (s, e) => CommitHex();
            hex.KeyDown += (s, e) => { if (e.Key == Key.Return) { CommitHex(); e.Handled = true; } };
            _refreshers.Add(Sync);
            Sync();
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, hex, pick } };
        }

        // ---------------------------------------------------------------- shader asset slot
        private const string NewShaderTag = "__new_shader__";
        private ComboBox _shaderCombo;
        private bool _shaderSync;
        private TextBlock _shaderNodeLabel, _shaderArrow, _shaderHint;
        private Border _shaderNode;

        private Control BuildShaderSlot()
        {
            var panel = new StackPanel { Spacing = 6 };
            _shaderCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 22 };
            _shaderCombo.SelectionChanged += (s, e) =>
            {
                if (_shaderSync || !(_shaderCombo.SelectedItem is ComboBoxItem item)) return;
                var tag = item.Tag as string;
                if (tag == NewShaderTag) { _ = CreateNewShader(); return; }
                SetShaderAsset(tag);
            };
            panel.Children.Add(_shaderCombo);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            buttons.Children.Add(EditorKit.SmallButton("Browse…", () => _ = BrowseShader(), "Choose a shader file of the project"));
            buttons.Children.Add(EditorKit.SmallButton("Clear", () => SetShaderAsset(null), "Use the built-in shader"));
            buttons.Children.Add(EditorKit.SmallButton(OperatingSystem.IsWindows() ? "Edit in VS" : "Edit in IDE", EditShader, "Open the shader source in your code editor"));
            panel.Children.Add(buttons);

            // graphical link: [Shader] ──▶ [Material] (green when a custom shader is assigned)
            var link = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            _shaderNode = LinkNode("Built-in", out _shaderNodeLabel);
            link.Children.Add(_shaderNode);
            _shaderArrow = new TextBlock { Text = " ──▶ ", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            ToolTip.SetTip(link, "Which shader renders this material");
            link.Children.Add(_shaderArrow);
            link.Children.Add(LinkNode("Material", out _));
            panel.Children.Add(link);
            _shaderHint = new TextBlock { Classes = { "small" }, Foreground = EditorKit.Brush("VxOrangeBrush"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, IsVisible = false };
            panel.Children.Add(_shaderHint);

            RefreshShaderCombo();
            _refreshers.Add(UpdateShaderSlot);
            UpdateShaderSlot();
            return panel;
        }

        private static Border LinkNode(string text, out TextBlock label)
        {
            label = new TextBlock { Text = text, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 96 };
            return new Border { MinWidth = 66, Height = 24, CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 0), Background = EditorKit.Brush("VxControlBrush"), BorderBrush = EditorKit.Brush("VxTextTertiaryBrush"), BorderThickness = new Thickness(1), Child = label };
        }

        /// <summary>Project shader files: every .hlsl (Windows editor list) plus every .metal (the Metal backend).</summary>
        private static List<string> ProjectShaders()
        {
            var list = new List<string>();
            try { list.AddRange(ShaderAssetService.EnumerateProjectShaders()); } catch { }
            string root = EditorKit.ProjectRoot;
            if (!string.IsNullOrEmpty(root) && Directory.Exists(Path.Combine(root, "Assets")))
                foreach (var f in AssetPickerDialog.Scan(Path.Combine(root, "Assets"), new[] { "*.metal" }))
                {
                    string rel = EditorKit.ToProjectRelative(f);
                    if (!list.Contains(rel, StringComparer.OrdinalIgnoreCase)) list.Add(rel);
                }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        private void RefreshShaderCombo()
        {
            _shaderSync = true;
            try
            {
                _shaderCombo.Items.Clear();
                _shaderCombo.Items.Add(new ComboBoxItem { Content = "Built-in (from shader type)", Tag = null });
                foreach (var rel in ProjectShaders()) _shaderCombo.Items.Add(new ComboBoxItem { Content = rel, Tag = rel });
                _shaderCombo.Items.Add(new ComboBoxItem { Content = "New Shader…", Tag = NewShaderTag });
            }
            finally { _shaderSync = false; }
            SyncShaderCombo();
        }

        private void SyncShaderCombo()
        {
            if (_shaderCombo == null) return;
            _shaderSync = true;
            try
            {
                string want = _mat.ShaderAsset?.Replace('\\', '/');
                if (string.IsNullOrEmpty(want)) { _shaderCombo.SelectedIndex = 0; return; }
                foreach (var o in _shaderCombo.Items)
                    if (o is ComboBoxItem ci && ci.Tag is string t && t != NewShaderTag && string.Equals(t.Replace('\\', '/'), want, StringComparison.OrdinalIgnoreCase)) { _shaderCombo.SelectedItem = ci; return; }
                // assigned file outside the list (legacy .vshader, outside Assets/) — show it anyway
                var extra = new ComboBoxItem { Content = want, Tag = want };
                _shaderCombo.Items.Insert(Math.Max(0, _shaderCombo.Items.Count - 1), extra);
                _shaderCombo.SelectedItem = extra;
            }
            finally { _shaderSync = false; }
        }

        private void SetShaderAsset(string path)
        {
            string rel = string.IsNullOrEmpty(path) ? null : EditorKit.ToProjectRelative(path);
            Edit("shader", () => _mat.ShaderAsset = rel);
            UpdateShaderSlot();
        }

        private void UpdateShaderSlot()
        {
            bool linked = !string.IsNullOrEmpty(_mat.ShaderAsset);
            string name = linked ? Path.GetFileNameWithoutExtension(_mat.ShaderAsset) : null;
            var on = EditorKit.Brush("VxGreenBrush"); var off = EditorKit.Brush("VxTextTertiaryBrush");
            _shaderNodeLabel.Text = linked ? name : "Built-in";
            ToolTip.SetTip(_shaderNode, linked ? "Custom shader: " + _mat.ShaderAsset : "Built-in shader (from the shader type)");
            _shaderArrow.Foreground = linked ? on : off;
            _shaderNode.BorderBrush = linked ? on : off;
            string compiled = linked ? MaterialLive.ResolveShaderFile(_mat.ShaderAsset) : null;
            _shaderHint.IsVisible = linked && compiled == null;
            _shaderHint.Text = OperatingSystem.IsWindows()
                ? "The shader file was not found — the built-in shader renders."
                : "No Metal version of this shader (" + name + ".metal) — the Metal renderer shows the built-in shader.";
            SyncShaderCombo();
        }

        private async Task BrowseShader()
        {
            var r = await AssetPickerDialog.Pick(new AssetPickerOptions { Kind = "Shaders", Title = "Choose a shader", Patterns = ShaderPatterns, Current = _mat.ShaderAsset, AllowNone = true });
            if (r == null) return;
            SetShaderAsset(r.Length == 0 ? null : r);
        }

        private async Task CreateNewShader()
        {
            SyncShaderCombo();   // restore the selection while the name is asked
            string root = EditorKit.ProjectRoot;
            if (string.IsNullOrEmpty(root)) { EditorCommands.Toast("Open a project first"); return; }
            var name = await Dialogs.Prompt("New Shader", "Name of the new shader (it is assigned to this material and opened in your code editor)", "NewShader", "Create");
            if (string.IsNullOrWhiteSpace(name)) return;
            string ext = OperatingSystem.IsWindows() ? ".hlsl" : ".metal";
            string file = name.Trim();
            if (!file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) file += ext;
            string path = Path.Combine(root, "Assets", "Shaders", file);
            if (File.Exists(path)) { await Dialogs.Alert("New Shader", Path.GetFileName(path) + " already exists."); return; }
            try
            {
                Editor.Core.Assets.AssetActions.CreateShader(path, _mat.ShaderType == "Unlit" ? "Unlit" : _mat.ShaderType == "Transparent" ? "Transparent" : "Standard");
                RefreshShaderCombo();
                SetShaderAsset(path);
                EditorCommands.OpenInIde(path);
            }
            catch (Exception ex) { EditorCommands.Fail("New shader", ex); }
        }

        private void EditShader()
        {
            if (string.IsNullOrEmpty(_mat.ShaderAsset)) { _ = Dialogs.Alert("Shader", "Assign a shader first (Browse… or New Shader…); then this opens its source."); return; }
            string file = MaterialLive.ResolveShaderFile(_mat.ShaderAsset);
            if (file == null) { string abs = EditorKit.ToAbsolute(_mat.ShaderAsset); if (File.Exists(abs)) file = abs; }
            if (file != null) EditorCommands.OpenInIde(file);
            else _ = Dialogs.Alert("Shader", "The shader file " + _mat.ShaderAsset + " was not found.");
        }

        // ---------------------------------------------------------------- footstep sound slot
        private TextBlock _stepText;
        private Button _stepClear;

        private Control BuildFootstepSlot()
        {
            var panel = new StackPanel { Spacing = 6 };
            _stepText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var box = new Border
            {
                Background = EditorKit.Brush("VxFieldBrush"), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6),
                BorderBrush = EditorKit.Brush("VxHairlineBrush"), BorderThickness = new Thickness(1),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new VxIcon { Icon = "Audio", Foreground = EditorKit.Brush("VxGreenBrush") }, _stepText } }
            };
            ToolTip.SetTip(box, "Drop a clip or a .vsndc sound container here");
            DragDrop.SetAllowDrop(box, true);
            void Over(object s, DragEventArgs e)
            {
                bool ok = EditorKit.DroppedFiles(e).Any(f => EditorKit.HasExtension(f, AudioPatterns) && File.Exists(f));
                e.DragEffects = ok ? DragDropEffects.Link | DragDropEffects.Copy : DragDropEffects.None;
                box.BorderBrush = ok ? EditorKit.Brush("VxAccentBrush") : EditorKit.Brush("VxHairlineBrush");
                e.Handled = true;
            }
            box.AddHandler(DragDrop.DragEnterEvent, Over);
            box.AddHandler(DragDrop.DragOverEvent, Over);
            box.AddHandler(DragDrop.DragLeaveEvent, (s, e) => box.BorderBrush = EditorKit.Brush("VxHairlineBrush"));
            box.AddHandler(DragDrop.DropEvent, (s, e) =>
            {
                box.BorderBrush = EditorKit.Brush("VxHairlineBrush");
                var f = EditorKit.DroppedFiles(e).FirstOrDefault(x => EditorKit.HasExtension(x, AudioPatterns) && File.Exists(x));
                if (f == null) return;
                try { SetFootstep(EditorKit.ImportIntoProject(f, "Audio")); } catch (Exception ex) { EditorCommands.Fail("Footstep sound", ex); }
                e.Handled = true;
            });
            panel.Children.Add(box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            buttons.Children.Add(EditorKit.SmallButton("Browse…", () => _ = BrowseFootstep(), "Choose a clip or a sound container"));
            _stepClear = EditorKit.SmallButton("Clear", () => SetFootstep(null), "Silent when walked on");
            buttons.Children.Add(_stepClear);
            buttons.Children.Add(EditorKit.IconButton("Play", "Audition", AuditionFootstep, 13));
            buttons.Children.Add(EditorKit.IconButton("Stop", "Stop", StopFootstep, 13));
            panel.Children.Add(buttons);
            panel.Children.Add(EditorKit.Hint("Played when the player walks on a surface with this material (FootstepAudio). A .vsndc container adds variation."));
            _refreshers.Add(UpdateFootstep);
            UpdateFootstep();
            return panel;
        }

        private void UpdateFootstep()
        {
            bool set = !string.IsNullOrEmpty(_mat.FootstepSound);
            _stepText.Text = set ? "Step: " + Path.GetFileName(_mat.FootstepSound) : "None (silent when walked on)";
            _stepText.Foreground = set ? EditorKit.Brush("VxTextBrush") : EditorKit.Brush("VxTextTertiaryBrush");
            ToolTip.SetTip(_stepText, set ? _mat.FootstepSound : null);
            _stepClear.IsEnabled = set;
        }

        private void SetFootstep(string path)
        {
            string rel = string.IsNullOrEmpty(path) ? null : EditorKit.ToProjectRelative(path);
            Edit("footstep", () => _mat.FootstepSound = rel);
            UpdateFootstep();
        }

        private async Task BrowseFootstep()
        {
            var r = await AssetPickerDialog.Pick(new AssetPickerOptions { Kind = "Audio", Title = "Choose the footstep sound", Patterns = AudioPatterns, Current = _mat.FootstepSound, AllowNone = true });
            if (r == null) return;
            SetFootstep(r.Length == 0 ? null : r);
        }

        private void AuditionFootstep()
        {
            StopFootstep();
            if (string.IsNullOrEmpty(_mat.FootstepSound)) return;
            try
            {
                string full = EditorKit.ToAbsolute(_mat.FootstepSound);
                float vol = 1f, pitch = 1f;
                if (SoundContainerService.IsContainerPath(full))
                {
                    if (!SoundContainerService.Resolve(full, out var rolled)) { EditorCommands.Toast("The container has no playable clips"); return; }
                    full = EditorKit.ToAbsolute(rolled.ClipPath); vol = rolled.VolumeScale; pitch = rolled.PitchScale;
                }
                _stepVoice = VortexAudio.PlayVoice(full, vol, pitch, 0f, false, 0, true);
            }
            catch (Exception ex) { EditorCommands.Fail("Audition", ex); }
        }

        private void StopFootstep()
        {
            if (_stepVoice != VortexAudio.InvalidVoice) { try { VortexAudio.StopVoice(_stepVoice); } catch { } _stepVoice = VortexAudio.InvalidVoice; }
        }

        // ---------------------------------------------------------------- texture maps
        private readonly List<TextureSlotCard> _slots = new List<TextureSlotCard>();

        private Control BuildTextures()
        {
            var s = new StackPanel { Margin = new Thickness(14, 8, 6, 14) };
            s.Children.Add(new TextBlock { Text = "Texture Maps", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
            s.Children.Add(EditorKit.Hint("Drop textures from the Asset Browser or Finder, or Browse…. Double-click a map to open it in the Texture Editor."));
            var wrap = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            const string stored = "Saved with the material — the renderer does not sample this map yet";
            Slot(wrap, "albedo", "Albedo (Base Color)", null, () => _mat.AlbedoTexture, v => _mat.AlbedoTexture = v);
            Slot(wrap, "normal", "Normal Map", null, () => _mat.NormalTexture, v => _mat.NormalTexture = v);
            Slot(wrap, "metallic", "Metallic Map", null, () => _mat.MetallicTexture, v => _mat.MetallicTexture = v);
            Slot(wrap, "roughness", "Roughness Map", null, () => _mat.RoughnessTexture, v => _mat.RoughnessTexture = v);
            Slot(wrap, "ao", "Ambient Occlusion", null, () => _mat.AOTexture, v => _mat.AOTexture = v);
            Slot(wrap, "height", "Height (Displacement)", null, () => _mat.HeightTexture, v => _mat.HeightTexture = v);
            s.Children.Add(wrap);
            s.Children.Add(EditorKit.Section("More maps", 6));
            var more = new WrapPanel();
            Slot(more, "emissive", "Emissive", stored, () => _mat.EmissiveTexture, v => _mat.EmissiveTexture = v);
            Slot(more, "opacity", "Opacity", stored, () => _mat.OpacityTexture, v => _mat.OpacityTexture = v);
            Slot(more, "mr", "Metallic-Roughness", stored, () => _mat.MetallicRoughnessTexture, v => _mat.MetallicRoughnessTexture = v);
            Slot(more, "orm", "ORM (Occl-Rough-Metal)", stored, () => _mat.OcclusionRoughnessMetallicTexture, v => _mat.OcclusionRoughnessMetallicTexture = v);
            s.Children.Add(more);
            return s;
        }

        private void Slot(Panel host, string key, string title, string note, Func<string> get, Action<string> set)
        {
            var card = new TextureSlotCard(title, note, get, v => Edit("tex:" + key, () => set(v)));
            _slots.Add(card);
            _refreshers.Add(card.Refresh);
            host.Children.Add(card);
        }

        // ---------------------------------------------------------------- footer
        private Control BuildFooter()
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var info = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _status, _users } };
            g.Children.Add(info);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var newBtn = new Button { Content = "New…", MinWidth = 70, Classes = { "ghost" } };
            ToolTip.SetTip(newBtn, "Create a new material in Assets/Materials and open it");
            newBtn.Click += (s, e) => _ = CreateMaterialFile();
            _revertButton = new Button { Content = "Revert", MinWidth = 80 };
            ToolTip.SetTip(_revertButton, "Discard the changes and reload the saved file");
            _revertButton.Click += (s, e) => Revert();
            var saveAs = new Button { Content = "Save As…", MinWidth = 90 };
            ToolTip.SetTip(saveAs, "Save a copy under a new name (" + Keys.Chord("S", shift: true) + ")");
            saveAs.Click += (s, e) => _ = SaveAs();
            var close = new Button { Content = "Close", MinWidth = 80 };
            close.Click += (s, e) => Close();
            var save = new Button { Content = "Save", MinWidth = 96, Classes = { "accent" } };
            ToolTip.SetTip(save, "Save the .vmat and apply it to the scene (" + Keys.Chord("S") + ")");
            save.Click += (s, e) => Save();
            buttons.Children.Add(newBtn); buttons.Children.Add(_revertButton); buttons.Children.Add(saveAs); buttons.Children.Add(close); buttons.Children.Add(save);
            Grid.SetColumn(buttons, 2);
            g.Children.Add(buttons);
            return new Border { Classes = { "hairline-top" }, Background = EditorKit.Brush("VxToolbarBrush"), Padding = new Thickness(14, 9), Child = g };
        }

        private void SetStatus(string text, bool error = false)
        {
            _status.Text = text;
            _status.Foreground = error ? EditorKit.Brush("VxRedBrush") : EditorKit.Brush("VxTextSecondaryBrush");
        }

        private void UpdateTitle()
        {
            string name = _path != null ? Path.GetFileName(_path) : "Untitled";
            bool dirty = IsDirty;
            Title = "Material Editor — " + name + (dirty ? " *" : "");
            _fileText.Text = _path != null ? EditorKit.ToProjectRelative(_path) : "Not saved yet";
            ToolTip.SetTip(_fileText, _path);
            if (_revertButton != null) _revertButton.IsEnabled = dirty && _path != null && File.Exists(_path);
        }

        private void UpdateUsers()
        {
            int n = _path != null ? MaterialLive.CountUsers(_path) : 0;
            _users.Text = n == 0 ? "Not used in the open scene" : "Used by " + n + (n == 1 ? " object" : " objects") + " in the open scene";
        }

        // ================================================================= save / revert / new
        /// <summary>Save to the .vmat (Save As for an untitled material) and push the material to the scene.</summary>
        public bool Save()
        {
            CommitFocusedField();
            if (_path == null) { _ = SaveAs(); return false; }
            return SaveTo(_path);
        }

        public async Task<bool> SaveAs()
        {
            CommitFocusedField();
            string root = EditorKit.ProjectRoot;
            string startDir = _path != null ? Path.GetDirectoryName(_path) : root != null ? Path.Combine(root, "Assets", "Materials") : null;
            IStorageFolder start = null;
            try { if (startDir != null) { Directory.CreateDirectory(startDir); start = await StorageProvider.TryGetFolderFromPathAsync(startDir); } } catch { }
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Material",
                SuggestedFileName = (string.IsNullOrWhiteSpace(_mat.Name) ? "NewMaterial" : _mat.Name.Trim()) + ".vmat",
                DefaultExtension = "vmat",
                ShowOverwritePrompt = true,
                SuggestedStartLocation = start,
                FileTypeChoices = new[] { new FilePickerFileType("Vortex Material") { Patterns = new[] { "*.vmat" } } }
            });
            string p = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(p)) return false;
            if (!p.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase)) p += ".vmat";
            if (root != null && !EditorKit.IsInsideProject(p)) EditorCommands.Toast("Saved outside the project — the scene cannot reference it");
            return SaveTo(p);
        }

        private bool SaveTo(string path)
        {
            if ((DateTime.UtcNow - _lastSaveAt).TotalMilliseconds < 300 && _path != null && EditorKit.SamePath(path, _path) && !IsDirty) return true;   // key repeat
            try
            {
                string full = Path.GetFullPath(path);
                var copy = FromSnapshot(Snapshot());
                copy.ResolvePathsRelative(Path.GetDirectoryName(full));   // .vmat texture paths are relative to the material
                if (!copy.Save(full)) { SetStatus("Could not write " + Path.GetFileName(full), true); return false; }
                bool newFile = _path == null || !EditorKit.SamePath(_path, full);
                _path = full;
                _savedSnap = Snapshot();
                _lastSaveAt = DateTime.UtcNow;
                if (newFile) { try { AssetDatabase.Instance.Refresh(); } catch { } }
                int users = MaterialLive.PushToScene(full, copy);
                ThumbnailService.Invalidate(full);
                try { MaterialSaved?.Invoke(full); } catch { }
                _preview?.MarkDirty();
                UpdateTitle();
                UpdateUsers();
                SetStatus((newFile ? "Saved " + Path.GetFileName(full) : "Material saved") + (users > 0 ? " — updated " + users + (users == 1 ? " object" : " objects") + " in the scene." : "."));
                EditorCommands.Toast("Material saved");
                return true;
            }
            catch (Exception ex) { SetStatus("Error: " + ex.Message, true); return false; }
        }

        /// <summary>Discard the edits and reload the saved file (undoable).</summary>
        public void Revert()
        {
            if (_path == null || !File.Exists(_path)) return;
            string before = _lastSnap;
            LoadMaterial();
            if (before != _lastSnap) { _undo.Add(before); _redo.Clear(); }
            RefreshControls();
            Changed();
            SetStatus("Reverted to the saved file");
        }

        private async Task CreateMaterialFile()
        {
            string root = EditorKit.ProjectRoot;
            if (string.IsNullOrEmpty(root)) { EditorCommands.Toast("Open a project first"); return; }
            var name = await Dialogs.Prompt("New Material", "Name of the new material (created in Assets/Materials)", "NewMaterial", "Create");
            if (string.IsNullOrWhiteSpace(name)) return;
            string file = name.Trim();
            if (!file.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase)) file += ".vmat";
            string path = Path.Combine(root, "Assets", "Materials", file);
            if (File.Exists(path)) { Open(path); return; }
            try { Open(Editor.Core.Assets.AssetActions.CreateMaterial(path, "Standard")); }
            catch (Exception ex) { EditorCommands.Fail("New material", ex); }
        }

        /// <summary>A number box still holding typed text commits on focus loss — do that before saving.</summary>
        private void CommitFocusedField()
        {
            try { if (FocusManager?.GetFocusedElement() is TextBox) FocusManager.ClearFocus(); } catch { }
        }

        // ================================================================= window plumbing
        private void OnKey(object sender, KeyEventArgs e)
        {
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (!cmd) return;
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool inText = FocusManager?.GetFocusedElement() is TextBox;
            switch (e.Key)
            {
                case Key.S: if (shift) _ = SaveAs(); else Save(); e.Handled = true; break;
                case Key.Z: if (inText) return; if (shift) Redo(); else Undo(); e.Handled = true; break;
                case Key.Y: if (inText) return; Redo(); e.Handled = true; break;
                case Key.W: Close(); e.Handled = true; break;
            }
        }

        private void OnOpened(object sender, EventArgs e)
        {
            EditorKit.FitToScreen(this);
            // MainWindow.OpenMaterialEditor constructs editors directly: keep one window per material anyway.
            if (_path == null) return;
            var other = EditorKit.OpenWindows<MaterialEditorWindow>().FirstOrDefault(w => !ReferenceEquals(w, this) && w._path != null && EditorKit.SamePath(w._path, _path));
            if (other != null) { _closeConfirmed = true; Dispatcher.UIThread.Post(() => { Close(); other.Activate(); }); }
        }

        private async void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (_closeConfirmed || !IsDirty) return;
            // the main window is quitting (it already asked about unsaved changes) — never block that
            if (e.CloseReason == WindowCloseReason.OwnerWindowClosing || e.CloseReason == WindowCloseReason.ApplicationShutdown || e.CloseReason == WindowCloseReason.OSShutdown) return;
            e.Cancel = true;
            string name = _path != null ? Path.GetFileName(_path) : "the new material";
            int r = await EditorKit.Choose(this, "Save changes to " + name + "?", "Your changes are lost if you don't save them.", "Save", "Don't Save", "Cancel");
            if (r == 2) return;
            if (r == 0 && !(_path != null ? SaveTo(_path) : await SaveAs())) return;
            _closeConfirmed = true;
            Close();
        }

        /// <summary>Close without asking about unsaved changes (tests / project closed).</summary>
        internal void CloseDiscarding() { _closeConfirmed = true; Close(); }

        private void OnProjectClosed() => Dispatcher.UIThread.Post(CloseDiscarding);

        private void OnClosed(object sender, EventArgs e)
        {
            EditorSession.Instance.ProjectClosed -= OnProjectClosed;
            StopFootstep();
            _preview?.Release();
        }
    }
}
