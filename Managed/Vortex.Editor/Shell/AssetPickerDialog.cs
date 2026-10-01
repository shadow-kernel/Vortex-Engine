using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Editor.Core.Audio;
using Editor.Core.Data;
using Editor.DllWrapper;
using VortexEditor.Controls;
using VortexEditor.Services;
using VortexEditor.Shell.Material;

namespace VortexEditor.Shell
{
    /// <summary>What an <see cref="AssetPickerDialog"/> offers.</summary>
    public sealed class AssetPickerOptions
    {
        /// <summary>Plural kind shown in the title / search hint ("Textures", "Audio", …).</summary>
        public string Kind = "Assets";
        /// <summary>File patterns ("*.png"); null/empty = every file.</summary>
        public string[] Patterns;
        public string Title;
        /// <summary>The field's current value (project-relative or absolute) — preselected and scrolled into view.</summary>
        public string Current;
        /// <summary>Offer a "None" entry (returns an empty string = clear the reference).</summary>
        public bool AllowNone = true;
        /// <summary>Offer the built-in primitives ("Primitive:Cube", …) — mesh fields.</summary>
        public bool IncludePrimitives;
        /// <summary>Allow selecting several assets (Cmd/Shift-click).</summary>
        public bool Multiple;
    }

    /// <summary>
    /// Project asset chooser — the macOS port of both Windows pickers (the dialog with search + tag filter, and the
    /// inspector picker with typed entries): a thumbnail grid (or a compact list), search over name and path, a type
    /// filter, a tag filter, "None", keyboard navigation and audio audition. Returns a project-relative path, "" for
    /// None, null when cancelled.
    /// </summary>
    public sealed class AssetPickerDialog : Window
    {
        /// <summary>The picker that is open right now (tests / tools).</summary>
        public static AssetPickerDialog Current { get; private set; }

        public static Task<string> Pick(string kind, string[] patterns) => Pick(new AssetPickerOptions { Kind = kind, Patterns = patterns });

        public static Task<string> Pick(string kind, string[] patterns, string current) => Pick(new AssetPickerOptions { Kind = kind, Patterns = patterns, Current = current });

        public static async Task<string> Pick(AssetPickerOptions options)
        {
            var r = await Run(options ?? new AssetPickerOptions());
            return r == null ? null : r.Length == 0 ? "" : r[0];
        }

        /// <summary>Several assets at once (project-relative); null when cancelled.</summary>
        public static Task<string[]> PickMany(string kind, string[] patterns)
            => Run(new AssetPickerOptions { Kind = kind, Patterns = patterns, Multiple = true, AllowNone = false });

        private static async Task<string[]> Run(AssetPickerOptions o)
        {
            if (string.IsNullOrEmpty(ProjectData.Current?.Path)) { EditorCommands.Toast("Open a project first"); return null; }
            var win = new AssetPickerDialog(o);
            var owner = EditorKit.ActiveWindow();
            Current = win;
            try
            {
                if (owner != null) await win.ShowDialog(owner);
                else { var tcs = new TaskCompletionSource<bool>(); win.Closed += (s, e) => tcs.TrySetResult(true); win.Show(); await tcs.Task; }
            }
            finally { if (ReferenceEquals(Current, win)) Current = null; }
            return win._result;
        }

        /// <summary>Icon name (Theme/Icons.axaml) for a file type.</summary>
        public static string IconFor(string path)
        {
            if (!string.IsNullOrEmpty(path) && path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
                return path.EndsWith("Sphere", StringComparison.OrdinalIgnoreCase) ? "Sphere" : "Cube";
            switch (Path.GetExtension(path)?.ToLowerInvariant())
            {
                case ".vmat": return "Material";
                case ".png": case ".jpg": case ".jpeg": case ".tga": case ".bmp": case ".hdr": case ".dds": case ".exr": case ".gif": case ".webp": return "Image";
                case ".wav": case ".mp3": case ".ogg": case ".flac": case ".vsndc": return "Audio";
                case ".cs": case ".hlsl": case ".metal": case ".vshader": return "Script";
                case ".ventity": case ".vprefab": return "Prefab";
                case ".vscene": return "Scene";
                case ".vanim": return "Bone";
                case ".vui": return "Layers";
                case ".fbx": case ".obj": case ".gltf": case ".glb": case ".dae": case ".3ds": case ".blend": case ".vmesh": return "Cube";
                default: return "File";
            }
        }

        /// <summary>Type group of a file (the type filter): Textures, Models, Clips, Containers, …</summary>
        public static string GroupFor(string path)
        {
            if (!string.IsNullOrEmpty(path) && path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return "Primitives";
            string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
            switch (ext)
            {
                case ".png": case ".jpg": case ".jpeg": case ".tga": case ".bmp": case ".hdr": case ".dds": case ".exr": case ".gif": case ".webp": return "Textures";
                case ".fbx": case ".obj": case ".gltf": case ".glb": case ".dae": case ".3ds": case ".blend": case ".vmesh": return "Models";
                case ".wav": case ".mp3": case ".ogg": case ".flac": return "Clips";
                case ".vsndc": return "Containers";
                case ".vmat": return "Materials";
                case ".hlsl": return "HLSL";
                case ".metal": return "Metal";
                case ".vshader": return "Shader assets";
                case ".cs": return "Scripts";
                case ".ventity": case ".vprefab": return "Prefabs";
                case ".vscene": return "Scenes";
                case ".vanim": return "Animations";
                case ".vui": return "UI";
                default: return ext.Length > 1 ? ext.Substring(1).ToUpperInvariant() : "Other";
            }
        }

        // ================================================================= instance
        private sealed class Item
        {
            public string Name, Rel, Full, Group, Dir, Icon;
            public List<string> Tags = new List<string>();
            public bool IsNone, IsPrimitive;
            public Bitmap Thumb;
            public bool ThumbRequested;
            public Border View;
            public Image Image;
            public VxIcon IconCtl;
        }

        private static bool _gridMode = true;
        private readonly AssetPickerOptions _o;
        private readonly List<Item> _all = new List<Item>();
        private List<Item> _shown = new List<Item>();
        private readonly List<Item> _selected = new List<Item>();
        private readonly HashSet<string> _tagFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _groupFilter;
        private string[] _result;
        private ulong _voice = VortexAudio.InvalidVoice;

        private readonly TextBox _search = new TextBox { Classes = { "search" } };
        private readonly WrapPanel _typeChips = new WrapPanel();
        private readonly WrapPanel _tagChips = new WrapPanel();
        private readonly Panel _host = new Panel();
        private readonly ScrollViewer _scroll = new ScrollViewer();
        private readonly TextBlock _count = new TextBlock { Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _selName = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _selInfo = new TextBlock { Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center };
        private readonly Image _selThumb = new Image { Width = 36, Height = 36, Stretch = Stretch.Uniform };
        private readonly VxIcon _selIcon = new VxIcon { Width = 20, Height = 20 };
        private readonly Button _play = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Play" }, IsVisible = false };
        private readonly Button _choose = new Button { Content = "Choose", MinWidth = 90, Classes = { "accent" }, IsDefault = true, IsEnabled = false };
        private readonly RadioButton _gridBtn = new RadioButton { GroupName = "apview", Content = new VxIcon { Icon = "Grid", Width = 13, Height = 13 } };
        private readonly RadioButton _listBtn = new RadioButton { GroupName = "apview", Content = new VxIcon { Icon = "Layers", Width = 13, Height = 13 } };
        private DispatcherTimer _searchTimer;
        private bool _thumbsPending;

        /// <summary>Items currently listed (after search / filters) — tests.</summary>
        public IReadOnlyList<string> ShownPaths => _shown.Select(i => i.IsNone ? "" : i.Rel).ToList();
        /// <summary>Type groups offered by the type filter — tests.</summary>
        public IReadOnlyList<string> Groups => _all.Where(i => !i.IsNone).Select(i => i.Group).Distinct().OrderBy(g => g).ToList();
        /// <summary>How many listed items already show a thumbnail — tests.</summary>
        public int ThumbnailCount => _shown.Count(i => i.Thumb != null);

        public AssetPickerDialog() : this(new AssetPickerOptions()) { }

        public AssetPickerDialog(AssetPickerOptions o)
        {
            _o = o;
            string kind = string.IsNullOrEmpty(o.Kind) ? "Assets" : o.Kind;
            Title = o.Title ?? "Choose " + kind;
            Width = 780; Height = 580; MinWidth = 520; MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CanResize = true; ShowInTaskbar = false;

            // ---- top: search + view mode
            _search.Watermark = "Search " + kind.ToLowerInvariant() + " by name or folder";
            _search.TextChanged += (s, e) => { _searchTimer?.Stop(); _searchTimer = _searchTimer ?? new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) }; _searchTimer.Tick -= OnSearchTick; _searchTimer.Tick += OnSearchTick; _searchTimer.Start(); };
            var seg = new Border { Classes = { "segmented" }, Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _gridBtn, _listBtn } }, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(_gridBtn, "Thumbnail grid"); ToolTip.SetTip(_listBtn, "List");
            _gridBtn.IsChecked = _gridMode; _listBtn.IsChecked = !_gridMode;
            _gridBtn.IsCheckedChanged += (s, e) => { if (_gridBtn.IsChecked == true && !_gridMode) { _gridMode = true; BuildViews(); } };
            _listBtn.IsCheckedChanged += (s, e) => { if (_listBtn.IsChecked == true && _gridMode) { _gridMode = false; BuildViews(); } };
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(14, 12, 14, 6) };
            top.Children.Add(_search);
            _count.Margin = new Thickness(10, 0);
            Grid.SetColumn(_count, 1); top.Children.Add(_count);
            Grid.SetColumn(seg, 2); top.Children.Add(seg);

            _typeChips.Margin = new Thickness(14, 0, 14, 2);
            _tagChips.Margin = new Thickness(14, 0, 14, 4);

            // ---- body
            _scroll.Content = _host;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.ScrollChanged += (s, e) => ScheduleThumbs();
            var body = new Border { Classes = { "panel" }, BorderBrush = EditorKit.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 1), Child = _scroll, Margin = new Thickness(0, 4, 0, 0) };
            DragDrop.SetAllowDrop(body, false);

            // ---- footer: selection + buttons
            var selBox = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(6), Background = EditorKit.Brush("VxFieldBrush"), ClipToBounds = true, Child = new Grid { Children = { _selIcon, _selThumb } } };
            _selIcon.HorizontalAlignment = HorizontalAlignment.Center; _selIcon.VerticalAlignment = VerticalAlignment.Center;
            var selText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Children = { _selName, _selInfo } };
            ToolTip.SetTip(_play, "Audition");
            _play.Click += (s, e) => Audition();
            var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
            cancel.Click += (s, e) => { _result = null; Close(); };
            _choose.Click += (s, e) => Choose();
            var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(14, 10, 14, 12) };
            footer.Children.Add(selBox);
            Grid.SetColumn(selText, 1); footer.Children.Add(selText);
            Grid.SetColumn(_play, 2); _play.Margin = new Thickness(6, 0); footer.Children.Add(_play);
            Grid.SetColumn(cancel, 3); cancel.Margin = new Thickness(6, 0); footer.Children.Add(cancel);
            Grid.SetColumn(_choose, 4); footer.Children.Add(_choose);

            var dock = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top);
            DockPanel.SetDock(_typeChips, Dock.Top); dock.Children.Add(_typeChips);
            DockPanel.SetDock(_tagChips, Dock.Top); dock.Children.Add(_tagChips);
            DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
            dock.Children.Add(body);
            Content = dock;

            AddHandler(KeyDownEvent, OnPreviewKey, RoutingStrategies.Tunnel);
            SizeChanged += (s, e) => ScheduleThumbs();
            Closed += (s, e) => { StopAudition(); _searchTimer?.Stop(); };
            Opened += (s, e) => { _search.Focus(); RevealSelection(); };

            Load();
        }

        // ---------------------------------------------------------------- data
        private void Load()
        {
            _all.Clear();
            if (_o.AllowNone) _all.Add(new Item { Name = "None", IsNone = true, Icon = "Close", Group = "", Dir = "Clear the reference" });
            if (_o.IncludePrimitives)
                foreach (var p in Enum.GetNames(typeof(PrimitiveType)))   // the primitives the scene can create
                    _all.Add(new Item { Name = p, Rel = "Primitive:" + p, Full = "Primitive:" + p, IsPrimitive = true, Group = "Primitives", Dir = "Built-in primitive", Icon = IconFor("Primitive:" + p) });
            string root = ProjectData.Current?.Path;
            foreach (var full in Scan(root, _o.Patterns))
            {
                string rel = Path.GetRelativePath(root, full).Replace('\\', '/');
                var it = new Item { Name = Path.GetFileName(full), Rel = rel, Full = full, Group = GroupFor(full), Icon = IconFor(full) };
                string dir = Path.GetDirectoryName(rel)?.Replace('\\', '/');
                it.Dir = string.IsNullOrEmpty(dir) ? "(project root)" : dir;
                try { it.Tags = AssetTagEditorDialog.GetTags(full).ToList(); } catch { }
                _all.Add(it);
            }
            BuildChips();
            ApplyFilter();
            string cur = string.IsNullOrEmpty(_o.Current) ? null : EditorKit.ToProjectRelative(_o.Current);
            var pre = cur == null ? null : _all.FirstOrDefault(i => !i.IsNone && string.Equals(i.Rel, cur, StringComparison.OrdinalIgnoreCase));
            if (pre != null) Select(pre, KeyModifiers.None);
        }

        /// <summary>Every project file matching the patterns (hidden folders, build output and Library skipped).</summary>
        internal static List<string> Scan(string root, string[] patterns)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return list;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        string name = Path.GetFileName(f);
                        if (name.StartsWith(".") || name.EndsWith(".vmeta", StringComparison.OrdinalIgnoreCase)) continue;
                        if (EditorKit.MatchesPatterns(f, patterns)) list.Add(f);
                    }
                    foreach (var d in Directory.EnumerateDirectories(dir))
                    {
                        string n = Path.GetFileName(d);
                        if (n.StartsWith(".") || n == "obj" || n == "bin" || n == "Library") continue;
                        stack.Push(d);
                    }
                }
                catch { }
            }
            list.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private void BuildChips()
        {
            _typeChips.Children.Clear();
            var groups = _all.Where(i => !i.IsNone).GroupBy(i => i.Group).OrderBy(g => g.Key).ToList();
            if (groups.Count > 1)
            {
                _typeChips.Children.Add(new TextBlock { Text = "Type", Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
                _typeChips.Children.Add(FilterChip("All (" + groups.Sum(g => g.Count()) + ")", () => _groupFilter == null, () => { _groupFilter = null; BuildChips(); ApplyFilter(); }));
                foreach (var g in groups)
                {
                    string key = g.Key;
                    _typeChips.Children.Add(FilterChip(key + " (" + g.Count() + ")", () => _groupFilter == key, () => { _groupFilter = _groupFilter == key ? null : key; BuildChips(); ApplyFilter(); }));
                }
            }
            _typeChips.IsVisible = _typeChips.Children.Count > 0;

            _tagChips.Children.Clear();
            var tags = _all.SelectMany(i => i.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            if (tags.Count > 0)
            {
                _tagChips.Children.Add(new TextBlock { Text = "Tags", Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
                foreach (var t in tags)
                {
                    string tag = t;
                    _tagChips.Children.Add(FilterChip(tag, () => _tagFilter.Contains(tag), () => { if (!_tagFilter.Remove(tag)) _tagFilter.Add(tag); BuildChips(); ApplyFilter(); }));
                }
                if (_tagFilter.Count > 0)
                {
                    var clear = new Button { Content = "Clear tags", Classes = { "link" }, Padding = new Thickness(4, 0), Margin = new Thickness(0, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center };
                    clear.Click += (s, e) => { _tagFilter.Clear(); BuildChips(); ApplyFilter(); };
                    _tagChips.Children.Add(clear);
                }
            }
            _tagChips.IsVisible = _tagChips.Children.Count > 0;
        }

        private static Control FilterChip(string text, Func<bool> isOn, Action toggle)
        {
            bool on = isOn();
            var b = new ToggleButton { Content = text, IsChecked = on, Padding = new Thickness(9, 1), MinHeight = 20, FontSize = 11.5, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(10) };
            if (on) b.Classes.Add("accentcheck");
            b.Click += (s, e) => toggle();
            return b;
        }

        private void OnSearchTick(object sender, EventArgs e) { _searchTimer?.Stop(); ApplyFilter(); }

        /// <summary>Set the search text and apply it immediately (tests / tools).</summary>
        public void SetSearch(string text) { _search.Text = text ?? ""; _searchTimer?.Stop(); ApplyFilter(); }

        /// <summary>Thumbnail grid (true) or compact list (false) — tests / tools.</summary>
        public void SetGridMode(bool grid)
        {
            if (grid) _gridBtn.IsChecked = true; else _listBtn.IsChecked = true;
        }

        /// <summary>Restrict to one type group (null = all) — tests / tools.</summary>
        public void SetTypeFilter(string group) { _groupFilter = group; BuildChips(); ApplyFilter(); }

        private void ApplyFilter()
        {
            string q = _search.Text?.Trim() ?? "";
            _shown = _all.Where(i =>
            {
                if (i.IsNone) return q.Length == 0 && _groupFilter == null && _tagFilter.Count == 0;
                if (_groupFilter != null && i.Group != _groupFilter) return false;
                if (_tagFilter.Count > 0 && !_tagFilter.All(t => i.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) return false;
                if (q.Length > 0 && i.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && (i.Rel ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && !i.Tags.Any(t => t.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
                return true;
            }).ToList();
            int n = _shown.Count(i => !i.IsNone);
            _count.Text = n == 1 ? "1 item" : n + " items";
            _selected.RemoveAll(i => !_shown.Contains(i));
            BuildViews();
            UpdateSelectionUi();
        }

        // ---------------------------------------------------------------- views
        private void BuildViews()
        {
            foreach (var i in _all) { i.View = null; i.Image = null; i.IconCtl = null; }
            Panel panel = _gridMode ? new WrapPanel { Margin = new Thickness(8) } : (Panel)new StackPanel { Margin = new Thickness(6, 4), Spacing = 1 };
            foreach (var it in _shown) panel.Children.Add(_gridMode ? MakeTile(it) : MakeRow(it));
            if (_shown.Count == 0)
                panel.Children.Add(new TextBlock { Text = _all.Count(i => !i.IsNone) == 0 ? "No " + (_o.Kind ?? "assets").ToLowerInvariant() + " in this project yet." : "Nothing matches the filter.", Classes = { "secondary" }, Margin = new Thickness(16) });
            _host.Children.Clear();
            _host.Children.Add(panel);
            foreach (var s in _selected) Highlight(s, true);
            ScheduleThumbs();
        }

        private Border MakeTile(Item it)
        {
            var icon = new VxIcon { Icon = it.Icon, Width = 34, Height = 34, Foreground = IconBrush(it), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var img = new Image { Stretch = Stretch.Uniform, IsVisible = false };
            var box = new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(8), Background = EditorKit.Brush("VxFieldBrush"), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, Child = new Grid { Children = { icon, img } } };
            // two lines (long texture names like concrete_floor_02_diff_2k stay readable), fixed height keeps the grid even
            var name = new TextBlock { Text = it.IsNone || it.IsPrimitive ? it.Name : Path.GetFileNameWithoutExtension(it.Name), FontSize = 11.5, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Width = 104, Height = 30, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
            var kind = new TextBlock { Text = it.IsNone ? "clear" : it.IsPrimitive ? "primitive" : Path.GetExtension(it.Name).TrimStart('.').ToUpperInvariant(), FontSize = 10, Classes = { "tertiary" }, HorizontalAlignment = HorizontalAlignment.Center };
            var tile = new Border { Width = 116, Padding = new Thickness(6, 6, 6, 4), Margin = new Thickness(3), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Child = new StackPanel { Spacing = 3, Children = { box, name, kind } } };
            ToolTip.SetTip(tile, it.IsNone ? "None — clear the reference" : it.Rel + (it.Tags.Count > 0 ? "\nTags: " + string.Join(", ", it.Tags) : ""));
            Wire(tile, it);
            it.View = tile; it.Image = img; it.IconCtl = icon;
            if (it.Thumb != null) ShowThumb(it);
            return tile;
        }

        private Border MakeRow(Item it)
        {
            var icon = new VxIcon { Icon = it.Icon, Width = 16, Height = 16, Foreground = IconBrush(it), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var img = new Image { Stretch = Stretch.Uniform, IsVisible = false };
            var box = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(5), Background = EditorKit.Brush("VxFieldBrush"), ClipToBounds = true, Child = new Grid { Children = { icon, img } } };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            text.Children.Add(new TextBlock { Text = it.Name, FontWeight = FontWeight.Medium });
            text.Children.Add(new TextBlock { Text = it.Dir, Classes = { "small", "tertiary" } });
            var badge = new Border { Classes = { "badge" }, VerticalAlignment = VerticalAlignment.Center, IsVisible = !it.IsNone, Child = new TextBlock { Text = it.Group ?? "" } };
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            g.Children.Add(box);
            Grid.SetColumn(text, 1); g.Children.Add(text);
            Grid.SetColumn(badge, 2); g.Children.Add(badge);
            var row = new Border { Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Child = g };
            ToolTip.SetTip(row, it.IsNone ? "None — clear the reference" : it.Rel);
            Wire(row, it);
            it.View = row; it.Image = img; it.IconCtl = icon;
            if (it.Thumb != null) ShowThumb(it);
            return row;
        }

        private void Wire(Border view, Item it)
        {
            view.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(view).Properties.IsLeftButtonPressed) return;
                if (e.ClickCount >= 2) { Select(it, KeyModifiers.None); Choose(); return; }
                Select(it, e.KeyModifiers);
            };
            view.PointerEntered += (s, e) => { if (!_selected.Contains(it)) view.Background = EditorKit.Brush("VxHoverBrush"); };
            view.PointerExited += (s, e) => { if (!_selected.Contains(it)) view.Background = Brushes.Transparent; };
        }

        private static IBrush IconBrush(Item it)
            => it.IsNone ? EditorKit.Brush("VxTextTertiaryBrush") : it.IsPrimitive ? EditorKit.Brush("VxAccentBrush") : EditorKit.Brush(FileIconBrushConverter.BrushKeyFor(it.Full, false));

        // ---------------------------------------------------------------- thumbnails (only for tiles on screen)
        private void ScheduleThumbs()
        {
            if (_thumbsPending) return;
            _thumbsPending = true;
            Dispatcher.UIThread.Post(() => { _thumbsPending = false; RequestVisibleThumbs(); }, DispatcherPriority.Background);
        }

        private void RequestVisibleThumbs()
        {
            double vh = _scroll.Viewport.Height;
            if (vh <= 0) vh = Bounds.Height;
            foreach (var it in _shown)
            {
                if (it.ThumbRequested || it.IsNone || it.View == null) continue;
                if (ThumbnailService.KindOf(it.Full) == ThumbnailService.Kind.None) { it.ThumbRequested = true; continue; }
                var p = it.View.TranslatePoint(new Point(0, 0), _scroll);
                if (p == null) continue;
                if (p.Value.Y + it.View.Bounds.Height < -80 || p.Value.Y > vh + 80) continue;
                it.ThumbRequested = true;
                var item = it;
                ThumbnailService.Request(it.Full, 128, bmp => { item.Thumb = bmp; ShowThumb(item); if (_selected.Count == 1 && _selected[0] == item) UpdateSelectionUi(); });
            }
        }

        private static void ShowThumb(Item it)
        {
            if (it.Image == null || it.Thumb == null) return;
            it.Image.Source = it.Thumb;
            it.Image.IsVisible = true;
            if (it.IconCtl != null) it.IconCtl.IsVisible = false;
        }

        // ---------------------------------------------------------------- selection
        private void Select(Item it, KeyModifiers mods)
        {
            bool additive = _o.Multiple && (mods.HasFlag(KeyModifiers.Meta) || mods.HasFlag(KeyModifiers.Control));
            bool range = _o.Multiple && mods.HasFlag(KeyModifiers.Shift) && _selected.Count > 0;
            if (range)
            {
                int a = _shown.IndexOf(_selected[_selected.Count - 1]), b = _shown.IndexOf(it);
                if (a >= 0 && b >= 0)
                    for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) if (!_selected.Contains(_shown[i]) && !_shown[i].IsNone) { _selected.Add(_shown[i]); Highlight(_shown[i], true); }
            }
            else if (additive && !it.IsNone)
            {
                if (_selected.Remove(it)) Highlight(it, false); else { _selected.Add(it); Highlight(it, true); }
                foreach (var n in _selected.Where(x => x.IsNone).ToList()) { _selected.Remove(n); Highlight(n, false); }
            }
            else
            {
                foreach (var s in _selected) Highlight(s, false);
                _selected.Clear();
                _selected.Add(it);
                Highlight(it, true);
            }
            UpdateSelectionUi();
            it.View?.BringIntoView();
        }

        private static void Highlight(Item it, bool on)
        {
            if (it.View == null) return;
            it.View.Background = on ? EditorKit.Brush("VxSelectionBrush") : Brushes.Transparent;
            it.View.BorderBrush = on ? EditorKit.Brush("VxAccentBrush") : null;
            it.View.BorderThickness = new Thickness(on ? 1 : 0);
        }

        private void RevealSelection()
        {
            if (_selected.Count > 0) Dispatcher.UIThread.Post(() => _selected[0].View?.BringIntoView(), DispatcherPriority.Background);
        }

        private void UpdateSelectionUi()
        {
            StopAudition();
            _choose.IsEnabled = _selected.Count > 0;
            if (_selected.Count == 0)
            {
                _selName.Text = "Nothing selected"; _selInfo.Text = _o.Multiple ? "Cmd-click or Shift-click to select several" : "Double-click to choose";
                _selIcon.Icon = "File"; _selIcon.IsVisible = true; _selThumb.IsVisible = false; _play.IsVisible = false;
                return;
            }
            if (_selected.Count > 1)
            {
                _selName.Text = _selected.Count + " selected"; _selInfo.Text = string.Join(", ", _selected.Take(4).Select(s => s.Name)) + (_selected.Count > 4 ? ", …" : "");
                _selIcon.Icon = "Layers"; _selIcon.IsVisible = true; _selThumb.IsVisible = false; _play.IsVisible = false;
                return;
            }
            var it = _selected[0];
            _selName.Text = it.IsNone ? "None" : it.Name;
            string info = it.IsNone ? "Clears the field" : it.IsPrimitive ? "Built-in primitive" : it.Rel;
            if (!it.IsNone && !it.IsPrimitive)
            {
                try { var fi = new FileInfo(it.Full); info += "  ·  " + Size(fi.Length); } catch { }
                if (it.Group == "Clips" && VortexAudio.GetClipInfo(it.Full, out float dur, out int rate, out int ch)) info += "  ·  " + dur.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s, " + rate + " Hz, " + (ch == 1 ? "mono" : ch == 2 ? "stereo" : ch + " ch");
                if (it.Tags.Count > 0) info += "  ·  " + string.Join(", ", it.Tags);
            }
            _selInfo.Text = info;
            _selIcon.Icon = it.Icon; _selIcon.Foreground = IconBrush(it);
            _selThumb.Source = it.Thumb; _selThumb.IsVisible = it.Thumb != null; _selIcon.IsVisible = it.Thumb == null;
            _play.IsVisible = it.Group == "Clips" || it.Group == "Containers";
        }

        private static string Size(long bytes) => bytes < 1024 ? bytes + " B" : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.#") + " KB" : (bytes / 1048576.0).ToString("0.#") + " MB";

        /// <summary>Choose the current selection and close (double-click / Enter / Choose).</summary>
        public void Choose()
        {
            if (_selected.Count == 0) return;
            _result = _selected.Any(s => s.IsNone) ? Array.Empty<string>() : _selected.Select(s => s.Rel).ToArray();
            Close();
        }

        /// <summary>Select a listed item by project-relative path (tests / tools). False when it is not listed.</summary>
        public bool SelectPath(string rel)
        {
            var it = _shown.FirstOrDefault(i => !i.IsNone && string.Equals(i.Rel, rel, StringComparison.OrdinalIgnoreCase));
            if (it == null) return false;
            Select(it, KeyModifiers.None);
            return true;
        }

        public void CancelPick() { _result = null; Close(); }

        // ---------------------------------------------------------------- audio
        private void Audition()
        {
            if (_selected.Count != 1) return;
            var it = _selected[0];
            StopAudition();
            try
            {
                string clip = it.Full; float vol = 1f, pitch = 1f;
                if (it.Group == "Containers")
                {
                    if (!SoundContainerService.Resolve(it.Full, out var rolled)) { EditorCommands.Toast("The container has no playable clips"); return; }
                    clip = EditorKit.ToAbsolute(rolled.ClipPath); vol = rolled.VolumeScale; pitch = rolled.PitchScale;
                }
                _voice = VortexAudio.PlayVoice(clip, vol, pitch, 0f, false, 0, true);
            }
            catch (Exception ex) { EditorCommands.Fail("Audition", ex); }
        }

        private void StopAudition()
        {
            if (_voice != VortexAudio.InvalidVoice) { try { VortexAudio.StopVoice(_voice); } catch { } _voice = VortexAudio.InvalidVoice; }
        }

        // ---------------------------------------------------------------- keyboard
        private void OnPreviewKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape: _result = null; Close(); e.Handled = true; return;
                case Key.Return: if (_selected.Count > 0) { Choose(); e.Handled = true; } return;
                case Key.Down: Move(_gridMode ? Columns() : 1); e.Handled = true; return;
                case Key.Up: Move(_gridMode ? -Columns() : -1); e.Handled = true; return;
                case Key.Left: if (_gridMode && (!_search.IsFocused || string.IsNullOrEmpty(_search.Text))) { Move(-1); e.Handled = true; } return;
                case Key.Right: if (_gridMode && (!_search.IsFocused || string.IsNullOrEmpty(_search.Text))) { Move(1); e.Handled = true; } return;
            }
        }

        private int Columns()
        {
            double w = _host.Bounds.Width - 16;
            return Math.Max(1, (int)(w / 122));
        }

        private void Move(int delta)
        {
            if (_shown.Count == 0) return;
            int i = _selected.Count > 0 ? _shown.IndexOf(_selected[_selected.Count - 1]) : -1;
            int n = i < 0 ? 0 : Math.Max(0, Math.Min(_shown.Count - 1, i + delta));
            Select(_shown[n], KeyModifiers.None);
        }
    }
}
