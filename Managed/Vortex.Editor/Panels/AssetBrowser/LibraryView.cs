using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Core.Assets;
using Editor.Core.Assets.Library;
using Editor.Core.Data;
using Editor.Core.Services;
using VortexEditor.Controls;
using VortexEditor.Services;
using AssetActions = VortexEditor.Services.AssetActions;
using VortexEditor.Shell;
using VortexEditor.Shell.Library;
using VortexEditor.Shell.Material;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>One tile of the Library tab (a catalog entry of the global asset library).</summary>
    public sealed class LibraryTile : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public LibraryEntry Entry { get; set; }
        public long Id => Entry.Id;
        public string Name => Entry.Name;
        public string Icon { get; set; }
        public IBrush IconBrush { get; set; }
        public string Sub { get; set; }
        public string Badge => Entry.SourceLabel;
        public bool HasBadge => !string.IsNullOrEmpty(Badge);
        public bool IsAudio => Entry.Type == AssetType.Audio;
        public string ToolTip { get; set; }
        internal bool Requested;

        private Bitmap _thumb;
        public Bitmap Thumbnail
        {
            get => _thumb;
            set { if (ReferenceEquals(_thumb, value)) return; _thumb = value; Raise(); Raise(nameof(HasThumbnail)); Raise(nameof(NoThumbnail)); }
        }
        public bool HasThumbnail => _thumb != null;
        public bool NoThumbnail => _thumb == null;

        private bool _selected;
        public bool IsSelected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; Raise(); Raise(nameof(SelectionBrush)); Raise(nameof(SelectionBorder)); }
        }
        public IBrush SelectionBrush => _selected ? EditorKit.Brush("VxAccentSoftBrush") : Brushes.Transparent;
        public IBrush SelectionBorder => _selected ? EditorKit.Brush("VxAccentBrush") : Brushes.Transparent;

        private double _size = 96;
        public double TileSize
        {
            get => _size;
            set { if (_size == value) return; _size = value; Raise(); Raise(nameof(TileWidth)); Raise(nameof(IconSize)); }
        }
        public double TileWidth => _size + 14;
        public double IconSize => Math.Round(_size * 0.36);

        public override string ToString() => Name;
    }

    /// <summary>A row of tiles (the grid is virtualised by rows).</summary>
    public sealed class LibraryRow { public List<LibraryTile> Tiles { get; set; } }

    /// <summary>
    /// The Library tab of the Asset Browser (#58): every asset ever imported on this machine, from every project —
    /// thumbnail grid (virtualised by rows, so 10,000+ entries scroll smoothly), type chips, search (the browser's
    /// search box), tag filter (the browser's tag button, all tags must match), saved filters, a details pane with
    /// tags / source / license / projects using the asset, and "Add to Project" (button, double-click, context menu or
    /// drag into the viewport / an inspector slot). Audio tiles audition on click straight from the library (#61).
    /// </summary>
    public sealed class LibraryView : UserControl
    {
        public static LibraryView Current { get; private set; }

        private static readonly (string key, string label, AssetType[] types)[] TypeChips =
        {
            ("All", "All", null),
            ("Models", "Models", new[] { AssetType.Mesh }),
            ("Textures", "Textures", new[] { AssetType.Texture }),
            ("Materials", "Materials", new[] { AssetType.Material }),
            ("Audio", "Audio", new[] { AssetType.Audio }),
            ("Animations", "Animations", new[] { AssetType.Animation }),
            ("Prefabs", "Prefabs", new[] { AssetType.Prefab }),
            ("Other", "Other", new[] { AssetType.Shader, AssetType.Font, AssetType.UI, AssetType.Script, AssetType.Scene, AssetType.Unknown }),
        };

        private GlobalAssetDatabase _subscribed;
        private readonly List<LibraryTile> _tiles = new List<LibraryTile>();
        private readonly ObservableCollection<LibraryRow> _rows = new ObservableCollection<LibraryRow>();
        private readonly HashSet<long> _selected = new HashSet<long>();
        private long _anchorId;
        private string _search = "";
        private List<string> _tagFilter = new List<string>();
        private LibrarySort _sort = LibrarySort.Name;
        private bool _desc;
        private string _typeKey = "All";
        private double _tileSize = 96;
        private int _columns = 1;
        private bool _dirty = true;
        private int _total;
        private DispatcherTimer _refreshTimer, _layoutTimer;

        private readonly StackPanel _chips = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly TextBlock _count = new TextBlock { Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        private readonly ItemsControl _rowsHost = new ItemsControl();
        private readonly ScrollViewer _scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        private readonly StackPanel _details = new StackPanel { Spacing = 6, Margin = new Thickness(14, 12, 14, 14) };
        private readonly Border _detailsHost;
        private readonly StackPanel _empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 6, MaxWidth = 420 };
        private readonly FuncDataTemplate<LibraryTile> _tileTemplate;
        private Button _savedBtn, _toolsBtn;

        private LibraryTile _pressTile;
        private Point _pressPos;
        private bool _dragArmed, _dragging, _selectOnRelease;

        /// <summary>A saved filter set the search text (the browser mirrors it into its search box).</summary>
        public event Action<string> SearchApplied;
        /// <summary>Tag filter / counts changed (the browser updates its tag button).</summary>
        public event Action FiltersChanged;

        public IReadOnlyList<string> TagFilter => _tagFilter;
        public IReadOnlyList<LibraryTile> Tiles => _tiles;
        public int ResultCount => _tiles.Count;
        public int TotalCount => _total;
        public string TypeKey => _typeKey;
        public IReadOnlyList<LibraryEntry> SelectedEntries => _tiles.Where(t => _selected.Contains(t.Id)).Select(t => t.Entry).ToList();

        private static GlobalAssetDatabase Lib => GlobalAssetDatabase.Instance;
        private static string ProjectRoot => ProjectData.Current?.Path;

        // ================================================================== editor-wide hooks
        private static bool _installed;

        /// <summary>Once per editor: thumbnails for every registration (imports, indexing), library warnings → console.</summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            LibraryProjects.Registered += (path, r) =>
            {
                if (r == null) return;
                if (r.Success && !r.Skipped && r.Entry != null) LibraryThumbs.Ensure(r.Hash, path, r.Entry.Type);
                else if (!r.Success && !string.IsNullOrEmpty(r.Error))
                    Dispatcher.UIThread.Post(() => ConsoleService.Instance.LogWarning("Asset library: " + Path.GetFileName(path) + " was not added — " + r.Error));
            };
        }

        public LibraryView()
        {
            Current = this;
            Install();
            _tileTemplate = new FuncDataTemplate<LibraryTile>((t, ns) => BuildTile(), true);

            // ---- top strip: type chips | count, saved filters, tools
            var top = new DockPanel { Height = 32, Margin = new Thickness(8, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(_count);
            _savedBtn = EditorKit.IconButton("Bookmark", "Saved filters", ShowSavedMenu, 13);
            _toolsBtn = EditorKit.IconButton("More", "Library tools: add files, index projects, tags, maintenance, settings", ShowToolsMenu, 13);
            right.Children.Add(_savedBtn);
            right.Children.Add(_toolsBtn);
            DockPanel.SetDock(right, Dock.Right);
            top.Children.Add(right);
            foreach (var c in TypeChips)
            {
                var key = c.key;
                var rb = new RadioButton { GroupName = "libtype", Content = c.label, Tag = key, IsChecked = key == "All", Padding = new Thickness(6, 2) };
                rb.Click += (s, e) => SetType(key);
                _chips.Children.Add(rb);
            }
            top.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new Border { Classes = { "segmented" }, HorizontalAlignment = HorizontalAlignment.Left, Child = _chips },
            });
            var topBorder = new Border { Classes = { "hairline-bottom" }, Child = top };
            DockPanel.SetDock(topBorder, Dock.Top);

            // ---- details pane
            _detailsHost = new Border
            {
                Width = 264, BorderBrush = EditorKit.Brush("VxHairlineBrush"), BorderThickness = new Thickness(1, 0, 0, 0),
                Child = new ScrollViewer { Content = _details, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            };
            DockPanel.SetDock(_detailsHost, Dock.Right);

            // ---- grid (virtualised by rows)
            _rowsHost.ItemsPanel = new FuncTemplate<Panel>(() => new VirtualizingStackPanel());
            _rowsHost.ItemTemplate = new FuncDataTemplate<LibraryRow>((row, ns) => BuildRow(), true);
            _rowsHost.ItemsSource = _rows;
            _scroll.Content = _rowsHost;
            _scroll.Padding = new Thickness(6, 4, 6, 8);
            _scroll.PropertyChanged += (s, e) => { if (e.Property == BoundsProperty) ScheduleRelayout(); };
            _scroll.AddHandler(PointerPressedEvent, (s, e) =>
            {
                // a click on empty space clears the selection
                if (e.Source is Visual v && FindTile(v) == null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { ClearSelection(); }
            }, RoutingStrategies.Bubble);
            var body = new Grid();
            body.Children.Add(_scroll);
            body.Children.Add(_empty);

            var dock = new DockPanel();
            dock.Children.Add(topBorder);
            dock.Children.Add(_detailsHost);
            dock.Children.Add(body);
            Content = dock;
            Focusable = true;
            KeyDown += OnKeyDown;

            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, (s, e) => { e.DragEffects = AssetDragData.OsFiles(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
            AddHandler(DragDrop.DropEvent, (s, e) => { var f = AssetDragData.OsFiles(e.Data); if (f != null) _ = AddFilesToLibrary(f); e.Handled = true; });
        }

        // ================================================================== state from the browser
        public void SetSearch(string text)
        {
            text = (text ?? "").Trim();
            if (text == _search && !_dirty) return;
            _search = text; Refresh();
        }

        public void SetTagFilter(IEnumerable<string> tags)
        {
            _tagFilter = (tags ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Refresh();
            FiltersChanged?.Invoke();
        }

        public void SetSort(LibrarySort sort, bool descending) { _sort = sort; _desc = descending; Refresh(); }
        public LibrarySort Sort => _sort;
        public bool Descending => _desc;

        public void SetTileSize(double size)
        {
            _tileSize = Math.Round(size);
            foreach (var t in _tiles) t.TileSize = _tileSize;
            ScheduleRelayout();
        }

        public void SetType(string key)
        {
            if (!TypeChips.Any(c => c.key == key)) key = "All";
            _typeKey = key;
            foreach (var child in _chips.Children) if (child is RadioButton rb) rb.IsChecked = (rb.Tag as string) == key;
            Refresh();
        }

        public bool IsFiltering => !string.IsNullOrEmpty(_search) || _tagFilter.Count > 0 || _typeKey != "All";

        private LibraryQuery BuildQuery()
            => new LibraryQuery
            {
                Search = _search, Tags = new List<string>(_tagFilter), Sort = _sort, Descending = _desc,
                Types = TypeChips.First(c => c.key == _typeKey).types?.ToList(),
            };

        // ================================================================== listing
        private void EnsureSubscribed()
        {
            var lib = Lib;
            if (ReferenceEquals(lib, _subscribed)) return;
            if (_subscribed != null) _subscribed.Changed -= OnLibraryChanged;
            _subscribed = lib;
            lib.Changed += OnLibraryChanged;
            lib.Warning += w => Dispatcher.UIThread.Post(() => { ConsoleService.Instance.LogWarning(w); EditorCommands.Toast(w); });
        }

        private void OnLibraryChanged()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _dirty = true;
                if (!IsEffectivelyVisible) return;
                if (_refreshTimer == null)
                {
                    _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    _refreshTimer.Tick += (s, e) => { _refreshTimer.Stop(); Refresh(); };
                }
                _refreshTimer.Stop(); _refreshTimer.Start();
            });
        }

        /// <summary>Re-query the catalog (keeps tiles, thumbnails and the selection of unchanged entries).</summary>
        public void Refresh()
        {
            _dirty = false;
            EnsureSubscribed();
            var lib = Lib;
            if (!lib.IsAvailable)
            {
                _tiles.Clear(); _rows.Clear(); _total = 0;
                ShowEmpty("Error", "The asset library is not available", lib.LastError ?? "",
                    ("Retry", () => { lib.Reopen(); Refresh(); }), (null, null));
                UpdateCount();
                return;
            }
            List<LibraryEntry> entries;
            try { entries = lib.Query(BuildQuery()); _total = IsFiltering ? lib.Count() : entries.Count; }
            catch (Exception ex) { ConsoleService.Instance.LogWarning("Asset library: " + ex.Message); entries = new List<LibraryEntry>(); }
            var old = new Dictionary<long, LibraryTile>();
            foreach (var t in _tiles) old[t.Id] = t;
            _tiles.Clear();
            var ids = new HashSet<long>();
            foreach (var e in entries)
            {
                ids.Add(e.Id);
                if (!old.TryGetValue(e.Id, out var tile) || tile.Entry.Hash != e.Hash) tile = NewTile(e);
                else { tile.Entry = e; tile.Sub = SubText(e); tile.ToolTip = TipText(e); }
                tile.TileSize = _tileSize;
                tile.IsSelected = _selected.Contains(e.Id);
                _tiles.Add(tile);
            }
            _selected.RemoveWhere(id => !ids.Contains(id));
            RebuildRows(force: true);
            UpdateCount();
            UpdateEmptyState();
            UpdateDetails();
        }

        private static LibraryTile NewTile(LibraryEntry e)
        {
            string fake = "x" + e.Extension;
            var t = new LibraryTile
            {
                Entry = e,
                Icon = AssetPickerDialog.IconFor(fake),
                IconBrush = EditorKit.Brush(FileIconBrushConverter.BrushKeyFor(fake, false)),
            };
            t.Sub = SubText(e);
            t.ToolTip = TipText(e);
            var bmp = LibraryThumbs.TryGet(e.Hash);
            if (bmp != null) { t.Thumbnail = bmp; t.Requested = true; }
            return t;
        }

        internal static string TypeLabel(AssetType t) => t == AssetType.Mesh ? "Model" : t.ToString();

        private static string SubText(LibraryEntry e)
        {
            if (e.Type == AssetType.Audio && e.Duration.HasValue)
            {
                int s = (int)Math.Round(e.Duration.Value);
                return "Audio · " + (s / 60) + ":" + (s % 60).ToString("00");
            }
            if (e.Type == AssetType.Texture && e.Width.HasValue) return e.Width + "×" + e.Height;
            return TypeLabel(e.Type) + " · " + GlobalAssetDatabase.FormatBytes(e.Size);
        }

        private static string TipText(LibraryEntry e)
        {
            var lines = new List<string> { e.Name, TypeLabel(e.Type) + " · " + e.FileName + " · " + GlobalAssetDatabase.FormatBytes(e.Size) };
            if (!string.IsNullOrEmpty(e.SourceName)) lines.Add("From " + e.SourceName);
            if (e.Tags.Count > 0) lines.Add("Tags: " + string.Join(", ", e.Tags));
            lines.Add(e.Type == AssetType.Audio ? "Click to audition · double-click or drag to add to the project" : "Double-click or drag to add to the project");
            return string.Join("\n", lines);
        }

        private void UpdateCount()
        {
            _count.Text = !Lib.IsAvailable ? "" : IsFiltering ? _tiles.Count + " of " + _total : _tiles.Count + (_tiles.Count == 1 ? " asset" : " assets");
            FiltersChanged?.Invoke();
        }

        // ================================================================== rows (virtualisation)
        private int ColumnsFor(double width)
        {
            double tile = _tileSize + 14 + 2;   // tile width + margin
            return Math.Max(1, (int)((width - 14) / tile));
        }

        private void ScheduleRelayout()
        {
            if (_layoutTimer == null)
            {
                _layoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                _layoutTimer.Tick += (s, e) => { _layoutTimer.Stop(); RebuildRows(force: false); };
            }
            _layoutTimer.Stop(); _layoutTimer.Start();
        }

        private void RebuildRows(bool force)
        {
            int cols = ColumnsFor(_scroll.Bounds.Width > 0 ? _scroll.Bounds.Width : 600);
            if (!force && cols == _columns) return;
            _columns = cols;
            _rows.Clear();
            for (int i = 0; i < _tiles.Count; i += cols)
                _rows.Add(new LibraryRow { Tiles = _tiles.GetRange(i, Math.Min(cols, _tiles.Count - i)) });
        }

        private Control BuildRow()
        {
            var ic = new ItemsControl
            {
                ItemsPanel = new FuncTemplate<Panel>(() => new StackPanel { Orientation = Orientation.Horizontal }),
                ItemTemplate = _tileTemplate,
            };
            ic[!ItemsControl.ItemsSourceProperty] = new Binding("Tiles");
            return ic;
        }

        private Control BuildTile()
        {
            var icon = new VxIcon();
            icon[!VxIcon.IconProperty] = new Binding("Icon");
            icon[!ForegroundProperty] = new Binding("IconBrush");
            icon[!WidthProperty] = new Binding("IconSize");
            icon[!HeightProperty] = new Binding("IconSize");
            icon[!IsVisibleProperty] = new Binding("NoThumbnail");
            var img = new Image { Stretch = Stretch.UniformToFill };
            img[!Image.SourceProperty] = new Binding("Thumbnail");
            img[!IsVisibleProperty] = new Binding("HasThumbnail");
            var badgeText = new TextBlock { MaxWidth = 90, TextTrimming = TextTrimming.CharacterEllipsis };
            badgeText[!TextBlock.TextProperty] = new Binding("Badge");
            var badge = new Border { Classes = { "tilebadge" }, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 5, 5), Child = badgeText };
            badge[!IsVisibleProperty] = new Binding("HasBadge");
            var play = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xA6, 0, 0, 0)), CornerRadius = new CornerRadius(4), Padding = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(5), IsHitTestVisible = false,
                Child = new VxIcon { Icon = "Play", Width = 10, Height = 10, Foreground = Brushes.White },
            };
            play[!IsVisibleProperty] = new Binding("IsAudio");
            var square = new Border
            {
                CornerRadius = new CornerRadius(10), Background = EditorKit.Brush("VxFieldBrush"), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Panel { Children = { icon, img, badge, play } },
            };
            square[!WidthProperty] = new Binding("TileSize");
            square[!HeightProperty] = new Binding("TileSize");
            var name = new TextBlock { Classes = { "tilename" } };
            name[!TextBlock.TextProperty] = new Binding("Name");
            var sub = new TextBlock { Classes = { "tiletype" } };
            sub[!TextBlock.TextProperty] = new Binding("Sub");
            var root = new Border
            {
                Padding = new Thickness(4, 5, 4, 3), Margin = new Thickness(1), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                Child = new StackPanel { Children = { square, name, sub } },
            };
            root[!WidthProperty] = new Binding("TileWidth");
            root[!Border.BackgroundProperty] = new Binding("SelectionBrush");
            root[!Border.BorderBrushProperty] = new Binding("SelectionBorder");
            root[!ToolTip.TipProperty] = new Binding("ToolTip");
            root.DataContextChanged += (s, e) => RequestThumb(root.DataContext as LibraryTile);
            root.AttachedToVisualTree += (s, e) => RequestThumb(root.DataContext as LibraryTile);
            root.PointerPressed += OnTilePressed;
            root.PointerMoved += OnTileMoved;
            root.PointerReleased += OnTileReleased;
            root.ContextRequested += OnTileContext;
            return root;
        }

        private static void RequestThumb(LibraryTile t)
        {
            if (t == null || t.Requested) return;
            t.Requested = true;
            LibraryThumbs.Request(t.Entry, bmp => { if (bmp != null) t.Thumbnail = bmp; });
        }

        private static LibraryTile FindTile(Visual v)
        {
            for (; v != null; v = v.GetVisualParent())
                if (v is Control c && c.DataContext is LibraryTile t && c is Border) return t;
            return null;
        }

        // ================================================================== selection + pointer
        private void SetSelection(IEnumerable<long> ids)
        {
            _selected.Clear();
            foreach (var id in ids) _selected.Add(id);
            foreach (var t in _tiles) t.IsSelected = _selected.Contains(t.Id);
            UpdateDetails();
        }

        public void SelectOnly(LibraryTile t) { _anchorId = t?.Id ?? 0; SetSelection(t == null ? Array.Empty<long>() : new[] { t.Id }); }
        public void ClearSelection() => SetSelection(Array.Empty<long>());
        public void SelectAll() => SetSelection(_tiles.Select(t => t.Id));

        private void OnTilePressed(object sender, PointerPressedEventArgs e)
        {
            var tile = (sender as Control)?.DataContext as LibraryTile;
            if (tile == null) return;
            Focus();
            var p = e.GetCurrentPoint(this);
            if (p.Properties.IsRightButtonPressed) { if (!_selected.Contains(tile.Id)) SelectOnly(tile); return; }
            if (!p.Properties.IsLeftButtonPressed) return;
            if (e.ClickCount >= 2) { _ = AddToProject(SelectedOr(tile), null, place: false); e.Handled = true; return; }
            var mods = e.KeyModifiers;
            bool toggle = mods.HasFlag(KeyModifiers.Meta) || mods.HasFlag(KeyModifiers.Control);
            _selectOnRelease = false;
            if (mods.HasFlag(KeyModifiers.Shift) && _anchorId != 0)
            {
                int a = _tiles.FindIndex(x => x.Id == _anchorId), b = _tiles.IndexOf(tile);
                if (a >= 0 && b >= 0) SetSelection(_tiles.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1).Select(x => x.Id));
            }
            else if (toggle)
            {
                var ids = new HashSet<long>(_selected);
                if (!ids.Remove(tile.Id)) ids.Add(tile.Id);
                _anchorId = tile.Id;
                SetSelection(ids);
            }
            else if (_selected.Contains(tile.Id) && _selected.Count > 1) _selectOnRelease = true;   // keep the group for a drag
            else SelectOnly(tile);
            _pressTile = tile; _pressPos = p.Position; _dragArmed = true;
            if (tile.IsAudio && !toggle && !mods.HasFlag(KeyModifiers.Shift)) LibraryThumbs.Audition(tile.Entry);
        }

        private async void OnTileMoved(object sender, PointerEventArgs e)
        {
            if (!_dragArmed || _dragging || _pressTile == null) return;
            var p = e.GetCurrentPoint(this);
            if (!p.Properties.IsLeftButtonPressed) { _dragArmed = false; return; }
            var d = p.Position - _pressPos;
            if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
            _dragArmed = false; _selectOnRelease = false;
            if (ProjectRoot == null) { EditorCommands.Toast("Open a project to add library assets to it"); return; }
            // the drop targets (viewport, inspector slots, folders) speak project paths: add first, then drag the copy
            var r = AddToProjectNow(_pressTile.Entry, null, out bool added);
            if (r == null) return;
            if (added) AfterAdd(new List<string> { r }, quiet: true);
            _dragging = true;
            try { await DragDrop.DoDragDrop(e, AssetDragData.ForPath(r), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
            catch { }
            finally { _dragging = false; _pressTile = null; }
        }

        private void OnTileReleased(object sender, PointerReleasedEventArgs e)
        {
            if (_selectOnRelease && !_dragging && _pressTile != null) SelectOnly(_pressTile);
            _selectOnRelease = false; _dragArmed = false;
        }

        private List<LibraryEntry> SelectedOr(LibraryTile t)
            => t != null && !_selected.Contains(t.Id) ? new List<LibraryEntry> { t.Entry } : SelectedEntries.ToList();

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (e.Key == Key.Escape) { AssetActions.StopAudition(); ClearSelection(); e.Handled = true; }
            else if (cmd && e.Key == Key.A) { SelectAll(); e.Handled = true; }
            else if ((e.Key == Key.Delete || e.Key == Key.Back) && _selected.Count > 0) { _ = DeleteSelected(); e.Handled = true; }
            else if (e.Key == Key.Enter && _selected.Count > 0) { _ = AddToProject(SelectedEntries.ToList(), null, place: false); e.Handled = true; }
            else if (e.Key == Key.Space && _selected.Count == 1) { LibraryThumbs.Audition(SelectedEntries[0]); e.Handled = true; }
        }

        // ================================================================== context menu
        private void OnTileContext(object sender, ContextRequestedEventArgs e)
        {
            var tile = (sender as Control)?.DataContext as LibraryTile;
            if (tile == null) return;
            if (!_selected.Contains(tile.Id)) SelectOnly(tile);
            var sel = SelectedEntries.ToList();
            bool one = sel.Count == 1;
            var m = new ContextMenu();
            bool hasProject = ProjectRoot != null;
            m.Items.Add(Mi(one ? "Add to Project" : "Add " + sel.Count + " to Project", () => _ = AddToProject(sel, null, false), "Import", hasProject));
            m.Items.Add(Mi("Add to Project in Folder…", () => _ = AddToProjectInFolder(sel), "Folder", hasProject));
            if (sel.Any(x => x.Type == AssetType.Mesh || x.Type == AssetType.Prefab))
                m.Items.Add(Mi("Add to Scene", () => _ = AddToProject(sel.Where(x => x.Type == AssetType.Mesh || x.Type == AssetType.Prefab).ToList(), null, true), "Cube", hasProject && ProjectData.Current?.ActiveScene != null));
            if (one && sel[0].Type == AssetType.Audio) m.Items.Add(Mi("Play", () => LibraryThumbs.Audition(sel[0]), "Play"));
            m.Items.Add(new Separator());
            if (one)
            {
                m.Items.Add(Mi("Edit Tags…", () => _ = EditTags(sel[0]), "Tag"));
                m.Items.Add(Mi("Rename…", () => _ = Rename(sel[0]), null));
            }
            else
            {
                m.Items.Add(Mi("Add Tags…", () => _ = BulkTags(sel, add: true), "Tag"));
                m.Items.Add(Mi("Remove Tags…", () => _ = BulkTags(sel, add: false), null));
            }
            if (one) m.Items.Add(Mi("Copy Content Hash", () => _ = CopyText(sel[0].Hash), "Link"));
            m.Items.Add(Mi(one ? "Export to Bundle…" : "Export " + sel.Count + " to Bundle…", () => _ = ExportBundle(sel.Select(x => x.Id).ToList()), "Export"));
            m.Items.Add(new Separator());
            m.Items.Add(Mi(one ? "Delete from Library…" : "Delete " + sel.Count + " from Library…", () => _ = DeleteSelected(), "Trash"));
            m.Open(sender as Control);
            e.Handled = true;
        }

        private static MenuItem Mi(string header, Action click, string icon, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            if (icon != null) mi.Icon = new VxIcon { Icon = icon, Width = 14, Height = 14 };
            mi.Click += (s, e) => click();
            return mi;
        }

        private async Task CopyText(string text)
        {
            try { var cb = TopLevel.GetTopLevel(this)?.Clipboard; if (cb != null) await cb.SetTextAsync(text ?? ""); EditorCommands.Toast("Copied"); } catch { }
        }

        // ================================================================== add to project
        /// <summary>Copy one entry into the open project now (UI thread; used by drag where the drop needs the path
        /// before the gesture ends). Returns the project path (new or existing copy) or null.</summary>
        private string AddToProjectNow(LibraryEntry e, string folder, out bool added)
        {
            added = false;
            var r = LibraryProjects.AddToProject(Lib, e.Id, ProjectRoot, folder, false, ProjectData.Current?.Name);
            if (r.Status == AddToProjectStatus.Failed) { EditorCommands.Toast("Add to Project failed: " + r.Error); return null; }
            added = r.Status == AddToProjectStatus.Added;
            if (added) SeedThumb(e, r.Path);
            return r.Path;
        }

        private static void SeedThumb(LibraryEntry e, string path)
        {
            var bmp = LibraryThumbs.TryGet(e.Hash);
            if (bmp == null) return;
            ThumbnailService.Seed(path, 128, bmp);
            ThumbnailService.Seed(path, 256, bmp);
        }

        /// <summary>Add entries to the open project (#59). Content the project already has is offered as "reveal"
        /// instead of a second copy. <paramref name="place"/>: models/prefabs are placed in the active scene.</summary>
        public async Task<List<string>> AddToProject(List<LibraryEntry> entries, string folder, bool place)
        {
            var done = new List<string>();
            if (entries == null || entries.Count == 0) return done;
            string root = ProjectRoot;
            if (root == null) { EditorCommands.Toast("Open a project first"); return done; }
            string projectName = ProjectData.Current?.Name;
            string reveal = null;
            foreach (var e in entries)
            {
                var r = await Task.Run(() => LibraryProjects.AddToProject(Lib, e.Id, root, folder, false, projectName));
                if (r.Status == AddToProjectStatus.AlreadyInProject)
                {
                    if (entries.Count == 1 && !place)
                    {
                        int choice = await Dialogs.Choose("Already in this project",
                            "“" + e.Name + "” is already in the project as " + Ui.ProjectRelative(r.Path) + ".",
                            new[] { "Show It", "Add a Copy", "Cancel" });
                        if (choice == 0) { reveal = r.Path; break; }
                        if (choice != 1) break;
                        r = await Task.Run(() => LibraryProjects.AddToProject(Lib, e.Id, root, folder, true, projectName));
                    }
                    else { done.Add(r.Path); continue; }   // batch / place: reuse the existing copy
                }
                if (r.Status == AddToProjectStatus.Failed) { EditorCommands.Toast("“" + e.Name + "”: " + r.Error); continue; }
                if (r.Status == AddToProjectStatus.Added) SeedThumb(e, r.Path);
                done.Add(r.Path);
            }
            if (reveal != null) { AssetBrowserPanel.Current?.SetTab("Explorer"); AssetBrowserPanel.Current?.Reveal(reveal); return done; }
            if (done.Count == 0) return done;
            AfterAdd(done, quiet: false);
            if (place)
            {
                foreach (var p in done.Where(AssetActions.CanAddToScene))
                    try { AssetActions.AddToScene(p); } catch (Exception ex) { EditorCommands.Fail("Add to Scene", ex); }
            }
            return done;
        }

        private void AfterAdd(List<string> paths, bool quiet)
        {
            try { AssetDatabase.Instance.Refresh(); } catch { }
            try { FileExplorerRefresh(); } catch { }
            if (!quiet) EditorCommands.Toast(paths.Count == 1 ? "Added " + Path.GetFileName(paths[0]) + " to " + Path.GetDirectoryName(Ui.ProjectRelative(paths[0])) : "Added " + paths.Count + " assets to the project");
            UpdateDetails();
        }

        private static void FileExplorerRefresh()
        {
            EditorCommands.Window?.FileTree?.Reload();
        }

        private async Task AddToProjectInFolder(List<LibraryEntry> entries)
        {
            var top = TopLevel.GetTopLevel(this);
            string root = ProjectRoot;
            if (top == null || root == null) return;
            IStorageFolder start = null;
            try { start = await top.StorageProvider.TryGetFolderFromPathAsync(Path.Combine(root, "Assets")); } catch { }
            var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add to which project folder?", SuggestedStartLocation = start });
            var p = picked?.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(p)) return;
            if (Path.IsPathRooted(Ui.ProjectRelative(p))) { await Dialogs.Alert("Add to Project", "Pick a folder inside the project (" + root + ")."); return; }
            await AddToProject(entries, p, place: false);
        }

        // ================================================================== edits
        private async Task Rename(LibraryEntry e)
        {
            var name = await Dialogs.Prompt("Rename", "Library name of this asset (projects keep their file names):", e.Name, "Rename");
            if (!string.IsNullOrWhiteSpace(name)) Lib.Rename(e.Id, name);
        }

        private async Task EditTags(LibraryEntry e)
        {
            var tags = await LibraryTagPrompt.Ask("Edit Tags", "Library tags of “" + e.Name + "”.", "Save", e.Tags);
            if (tags != null) Lib.SetTags(e.Id, tags);
        }

        private async Task BulkTags(List<LibraryEntry> sel, bool add)
        {
            var initial = add ? null : sel.SelectMany(x => x.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var tags = await LibraryTagPrompt.Ask(add ? "Add Tags" : "Remove Tags",
                add ? "Tags to add to " + sel.Count + " assets." : "Tags to remove from " + sel.Count + " assets (remove the ones to keep from this list).", add ? "Add" : "Remove", initial);
            if (tags == null || tags.Count == 0) return;
            int n = add ? Lib.AddTags(sel.Select(x => x.Id), tags) : Lib.RemoveTags(sel.Select(x => x.Id), tags);
            EditorCommands.Toast((add ? "Tagged " : "Untagged ") + n + (n == 1 ? " time" : " times"));
        }

        internal static List<string> SplitTags(string text)
            => (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

        public async Task DeleteSelected()
        {
            var sel = SelectedEntries.ToList();
            if (sel.Count == 0) return;
            bool ok = await Dialogs.Confirm("Delete from Library",
                (sel.Count == 1 ? "Remove “" + sel[0].Name + "” from the asset library?" : "Remove " + sel.Count + " assets from the asset library?") +
                "\n\nProjects that use " + (sel.Count == 1 ? "it" : "them") + " keep their copies. The library deletes the stored files once nothing else refers to them.",
                "Delete", "Cancel", destructive: true);
            if (!ok) return;
            int n = await Task.Run(() => Lib.Delete(sel.Select(x => x.Id), out long freed));
            ClearSelection();
            EditorCommands.Toast("Removed " + n + " from the library");
        }

        // ================================================================== tools
        private void ShowToolsMenu()
        {
            var m = new MenuFlyout();
            m.Items.Add(Mi("Add Files to Library…", () => _ = PickFilesToLibrary(), "Plus"));
            m.Items.Add(Mi("Index Existing Projects…", () => _ = LibraryIndexDialog.Run(), "Refresh"));
            m.Items.Add(Mi("Import Bundle…", () => _ = ImportBundle(), "Import"));
            m.Items.Add(Mi("Export Results to Bundle…", () => _ = ExportBundle(_tiles.Select(t => t.Id).ToList()), "Export", _tiles.Count > 0));
            m.Items.Add(new Separator());
            m.Items.Add(Mi("Tag Manager…", () => _ = LibraryTagManager.Run(), "Tag"));
            m.Items.Add(Mi("Maintenance…", () => LibraryMaintenanceWindow.Open(), "Hammer"));
            m.Items.Add(Mi("Library Settings…", () => _ = LibrarySettingsDialog.Run(), "Gear"));
            m.Items.Add(new Separator());
            m.Items.Add(Mi("Backfill Content Hashes (Project)", () => _ = BackfillProjectHashes(), null, ProjectRoot != null));
            m.ShowAt(_toolsBtn);
        }

        private void ShowSavedMenu()
        {
            var m = new MenuFlyout();
            var saved = Lib.SavedFilters();
            if (saved.Count == 0) m.Items.Add(new MenuItem { Header = "No saved filters yet", IsEnabled = false });
            foreach (var f in saved) { var ff = f; m.Items.Add(Mi(DescribeFilter(ff), () => ApplySavedFilter(ff), null)); }
            m.Items.Add(new Separator());
            m.Items.Add(Mi("Save Current Filter…", () => _ = SaveCurrentFilter(), "Bookmark", IsFiltering));
            if (saved.Count > 0)
            {
                var del = new MenuItem { Header = "Delete Saved Filter" };
                foreach (var f in saved) { var ff = f; del.Items.Add(Mi(ff.Name, () => Lib.DeleteFilter(ff.Name), null)); }
                m.Items.Add(del);
            }
            m.ShowAt(_savedBtn);
        }

        private static string DescribeFilter(SavedFilter f)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(f.Search)) parts.Add("“" + f.Search + "”");
            if (f.Types.Count > 0) parts.Add(string.Join("/", f.Types.Select(TypeLabel)));
            if (f.Tags.Count > 0) parts.Add("#" + string.Join(" #", f.Tags));
            return f.Name + (parts.Count > 0 ? "  —  " + string.Join(" · ", parts) : "");
        }

        private async Task SaveCurrentFilter()
        {
            var name = await Dialogs.Prompt("Save Filter", "Name for this search (" + (string.IsNullOrEmpty(_search) ? "no text" : "“" + _search + "”") + ", " +
                                                           (_typeKey == "All" ? "all types" : _typeKey) + (_tagFilter.Count > 0 ? ", #" + string.Join(" #", _tagFilter) : "") + "):", "", "Save");
            if (string.IsNullOrWhiteSpace(name)) return;
            Lib.SaveFilter(new SavedFilter { Name = name.Trim(), Search = _search, Types = TypeChips.First(c => c.key == _typeKey).types?.ToList() ?? new List<AssetType>(), Tags = new List<string>(_tagFilter) });
            EditorCommands.Toast("Saved filter “" + name.Trim() + "”");
        }

        public void ApplySavedFilter(SavedFilter f)
        {
            _search = f.Search ?? "";
            _tagFilter = new List<string>(f.Tags ?? new List<string>());
            var chip = TypeChips.FirstOrDefault(c => c.types != null && f.Types.Count == c.types.Length && c.types.All(f.Types.Contains));
            _typeKey = f.Types.Count == 0 || chip.key == null ? "All" : chip.key;
            foreach (var child in _chips.Children) if (child is RadioButton rb) rb.IsChecked = (rb.Tag as string) == _typeKey;
            SearchApplied?.Invoke(_search);
            Refresh();
            FiltersChanged?.Invoke();
        }

        /// <summary>The tag menu of the browser's tag button while the Library tab is active (all checked tags must match).</summary>
        public void ShowTagMenu(Control anchor)
        {
            var m = new MenuFlyout();
            var all = new MenuItem { Header = "All tags", Icon = _tagFilter.Count == 0 ? new VxIcon { Icon = "Check", Width = 12, Height = 12 } : null };
            all.Click += (s, e) => SetTagFilter(null);
            m.Items.Add(all);
            var tags = Lib.TagCounts();
            if (tags.Count > 0) m.Items.Add(new Separator());
            foreach (var (tag, count) in tags.Take(60))
            {
                var t = tag;
                bool on = _tagFilter.Contains(t, StringComparer.OrdinalIgnoreCase);
                var mi = new MenuItem { Header = t + "  (" + count + ")", Icon = on ? new VxIcon { Icon = "Check", Width = 12, Height = 12 } : null };
                mi.Click += (s, e) =>
                {
                    var next = new List<string>(_tagFilter);
                    if (on) next.RemoveAll(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)); else next.Add(t);
                    SetTagFilter(next);
                };
                m.Items.Add(mi);
            }
            m.Items.Add(new Separator());
            m.Items.Add(Mi("Manage Tags…", () => _ = LibraryTagManager.Run(), "Tag"));
            m.ShowAt(anchor);
        }

        public async Task PickFilesToLibrary()
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Add Files to the Asset Library", AllowMultiple = true });
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToArray();
            if (paths.Length > 0) await AddFilesToLibrary(paths);
        }

        /// <summary>"Add to Library" for loose files (no project involved): explicit, so every type is accepted.</summary>
        public async Task<int> AddFilesToLibrary(IEnumerable<string> files)
        {
            var list = files.Where(File.Exists).ToList();
            if (list.Count == 0) return 0;
            EditorCommands.Toast("Adding " + list.Count + " file(s) to the library…");
            var results = await Task.Run(() => list.Select(f => (f, Lib.Register(f, new RegisterOptions { Explicit = true, SourceKind = LibrarySource.Manual }))).ToList());
            foreach (var (f, r) in results)
            {
                if (r.Success && r.Entry != null) LibraryThumbs.Ensure(r.Hash, f, r.Entry.Type);
                else if (!r.Success) ConsoleService.Instance.LogWarning("Asset library: " + Path.GetFileName(f) + " — " + r.Error);
            }
            int ok = results.Count(x => x.Item2.Success);
            int known = results.Count(x => x.Item2.EntryExisted);
            EditorCommands.Toast("Library: " + ok + " added" + (known > 0 ? " (" + known + " were already there)" : ""));
            return ok;
        }

        private async Task ExportBundle(List<long> ids)
        {
            if (ids.Count == 0) return;
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Library Bundle", SuggestedFileName = "library" + LibraryBundle.Extension, DefaultExtension = "zip",
                FileTypeChoices = new[] { new FilePickerFileType("Vortex library bundle") { Patterns = new[] { "*" + LibraryBundle.Extension, "*.zip" } } },
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;
            if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) path += LibraryBundle.Extension;
            EditorCommands.Toast("Exporting " + ids.Count + " asset(s)…");
            var rep = await Task.Run(() => LibraryBundle.Export(Lib, ids, path));
            string msg = "Exported " + rep.Entries + " asset(s), " + GlobalAssetDatabase.FormatBytes(rep.Bytes) + " → " + Path.GetFileName(path);
            if (rep.SkippedNotRedistributable.Count > 0) msg += "\n\nNot exported (license forbids redistribution): " + string.Join(", ", rep.SkippedNotRedistributable);
            if (rep.Errors.Count > 0) msg += "\n\nProblems:\n" + string.Join("\n", rep.Errors);
            await Dialogs.Alert("Library Bundle", msg);
        }

        public async Task ImportBundle()
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Library Bundle", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Vortex library bundle") { Patterns = new[] { "*.zip" } } },
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;
            EditorCommands.Toast("Importing " + Path.GetFileName(path) + "…");
            var rep = await Task.Run(() => LibraryBundle.Import(Lib, path));
            string msg = "Imported " + rep.Entries + " asset(s): " + rep.Blobs + " new file(s) (" + GlobalAssetDatabase.FormatBytes(rep.Bytes) + "), " + rep.BlobsAlreadyPresent + " already in the library.";
            if (rep.Errors.Count > 0) msg += "\n\nProblems:\n" + string.Join("\n", rep.Errors.Take(30));
            await Dialogs.Alert("Library Bundle", msg);
        }

        public async Task BackfillProjectHashes()
        {
            string root = ProjectRoot;
            if (root == null) return;
            EditorCommands.Toast("Hashing the project's assets…");
            var r = await Task.Run(() => LibraryProjects.BackfillHashes(root));
            string msg = "Content hashes: " + r.hashed + " asset(s) hashed, " + r.fresh + " already current" + (r.failed > 0 ? ", " + r.failed + " unreadable" : "") + " (" + r.time.TotalSeconds.ToString("0.0") + " s).";
            ConsoleService.Instance.Log(msg);
            EditorCommands.Toast(msg);
        }

        // ================================================================== empty state
        private void ShowEmpty(string icon, string title, string sub, (string label, Action click) a, (string label, Action click) b)
        {
            _empty.Children.Clear();
            _empty.Children.Add(new VxIcon { Icon = icon, Width = 30, Height = 30, Foreground = EditorKit.Brush("VxTextTertiaryBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            _empty.Children.Add(new TextBlock { Text = title, Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(sub))
                _empty.Children.Add(new TextBlock { Text = sub, Classes = { "tertiary", "small" }, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            if (a.label != null) buttons.Children.Add(Ui.Button(a.label, a.click, null, "accent"));
            if (b.label != null) buttons.Children.Add(Ui.Button(b.label, b.click));
            if (buttons.Children.Count > 0) _empty.Children.Add(buttons);
            _empty.IsVisible = true;
        }

        private void UpdateEmptyState()
        {
            if (_tiles.Count > 0) { _empty.IsVisible = false; return; }
            if (IsFiltering && _total > 0)
            {
                ShowEmpty("Search", !string.IsNullOrEmpty(_search) ? "No library assets match “" + _search + "”" : "No library assets match the filter",
                    "Search covers names, file names and tags of every asset on this machine.", ("Clear Filters", () => { SetType("All"); _tagFilter.Clear(); SearchApplied?.Invoke(""); _search = ""; Refresh(); }), (null, null));
                return;
            }
            ShowEmpty("Library", "Your asset library is empty",
                "Every asset you import into any project lands here automatically — stored once, searchable from every project. " +
                "Index your existing projects to fill it now, or drop files here.",
                ("Index Existing Projects…", () => _ = LibraryIndexDialog.Run()), ("Add Files…", () => _ = PickFilesToLibrary()));
        }

        // ================================================================== details pane
        private void UpdateDetails()
        {
            _details.Children.Clear();
            var sel = SelectedEntries;
            if (sel.Count == 1) BuildEntryDetails(sel[0]);
            else if (sel.Count > 1) BuildMultiDetails(sel);
            else BuildLibrarySummary();
        }

        private static TextBlock Label(string text) => new TextBlock { Text = text, Classes = { "small", "tertiary" }, Margin = new Thickness(0, 6, 0, 0) };
        private static TextBlock Value(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        private void BuildEntryDetails(LibraryEntry e)
        {
            e = Lib.Get(e.Id) ?? e;
            var preview = new Border { Height = 130, CornerRadius = new CornerRadius(10), Background = EditorKit.Brush("VxFieldBrush"), ClipToBounds = true };
            var tile = _tiles.FirstOrDefault(t => t.Id == e.Id);
            if (tile?.Thumbnail != null) preview.Child = new Image { Source = tile.Thumbnail, Stretch = Stretch.Uniform };
            else preview.Child = new VxIcon { Icon = AssetPickerDialog.IconFor("x" + e.Extension), Width = 54, Height = 54, Foreground = EditorKit.Brush(FileIconBrushConverter.BrushKeyFor("x" + e.Extension, false)) };
            _details.Children.Add(preview);

            var name = new TextBox { Text = e.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
            long id = e.Id;
            void CommitName() { var n = name.Text?.Trim(); if (!string.IsNullOrEmpty(n) && n != Lib.Get(id)?.Name) Lib.Rename(id, n); }
            name.KeyDown += (s, a) => { if (a.Key == Key.Enter) { CommitName(); a.Handled = true; } };
            name.LostFocus += (s, a) => CommitName();
            _details.Children.Add(name);

            var facts = TypeLabel(e.Type) + " · " + e.FileName + " · " + GlobalAssetDatabase.FormatBytes(e.Size);
            if (e.Width.HasValue) facts += " · " + e.Width + "×" + e.Height;
            if (e.Duration.HasValue) facts += " · " + e.Duration.Value.ToString("0.0") + " s" + (e.SampleRate.HasValue ? " · " + (e.SampleRate / 1000) + " kHz" : "") + (e.Channels == 1 ? " · mono" : e.Channels == 2 ? " · stereo" : "");
            _details.Children.Add(new TextBlock { Text = facts, Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap });

            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var add = Ui.Button("Add to Project", () => _ = AddToProject(new List<LibraryEntry> { e }, null, false), "Copy into the open project (its default folder)", "accent");
            add.IsEnabled = ProjectRoot != null && e.Stored;
            add.Margin = new Thickness(0, 0, 6, 6);
            actions.Children.Add(add);
            if (e.Type == AssetType.Audio) { var play = Ui.Button("Play", () => LibraryThumbs.Audition(e), "Audition from the library"); play.Margin = new Thickness(0, 0, 6, 6); actions.Children.Add(play); }
            if (e.Type == AssetType.Mesh || e.Type == AssetType.Prefab)
            {
                var place = Ui.Button("Add to Scene", () => _ = AddToProject(new List<LibraryEntry> { e }, null, true), "Add to the project and place it in the scene");
                place.IsEnabled = ProjectRoot != null && ProjectData.Current?.ActiveScene != null && e.Stored;
                place.Margin = new Thickness(0, 0, 6, 6);
                actions.Children.Add(place);
            }
            _details.Children.Add(actions);
            if (!e.Stored) _details.Children.Add(new TextBlock { Text = "The library lost this file's bytes — import the original again to restore it.", Classes = { "small" }, Foreground = EditorKit.Brush("VxOrangeBrush"), TextWrapping = TextWrapping.Wrap });

            // tags
            _details.Children.Add(Label("Tags"));
            var chips = new WrapPanel();
            foreach (var t in e.Tags)
            {
                var tag = t;
                var x = new Button { Content = new VxIcon { Icon = "Close", Width = 9, Height = 9, Foreground = Brushes.White }, Classes = { "ghost" }, Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
                x.Click += (s, a) => Lib.SetTags(id, e.Tags.Where(z => !string.Equals(z, tag, StringComparison.OrdinalIgnoreCase)));
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { new TextBlock { Text = t, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 }, x } };
                chips.Children.Add(new Border { Background = EditorKit.Brush("VxAccentBrush"), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 2, 1), Margin = new Thickness(0, 0, 4, 4), Child = sp });
            }
            _details.Children.Add(chips);
            var addTag = new TextBox { Watermark = "Add tag + Enter", FontSize = 12 };
            addTag.KeyDown += (s, a) =>
            {
                if (a.Key != Key.Enter) return;
                var add2 = SplitTags(addTag.Text);
                if (add2.Count > 0) Lib.AddTags(new[] { id }, add2);
                a.Handled = true;
            };
            _details.Children.Add(addTag);

            // provenance
            _details.Children.Add(Label("Source"));
            _details.Children.Add(Value(e.SourceDescription + " · " + e.Added.ToString("yyyy-MM-dd HH:mm")));
            if (!string.IsNullOrEmpty(e.SourceUrl)) _details.Children.Add(new TextBlock { Text = e.SourceUrl, Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(e.Author) || !string.IsNullOrEmpty(e.License))
            {
                _details.Children.Add(Label("License"));
                _details.Children.Add(Value((e.License ?? "unknown") + (string.IsNullOrEmpty(e.Author) ? "" : " · " + e.Author) + (e.Redistributable ? "" : " · not redistributable")));
            }

            // companions
            if (e.Companions.Count > 0)
            {
                _details.Children.Add(Label("Travels with " + e.Companions.Count + " file(s)"));
                foreach (var c in e.Companions.Take(8)) _details.Children.Add(new TextBlock { Text = c.RelPath, Classes = { "small", "secondary" }, TextTrimming = TextTrimming.CharacterEllipsis });
                if (e.Companions.Count > 8) _details.Children.Add(new TextBlock { Text = "… " + (e.Companions.Count - 8) + " more", Classes = { "small", "tertiary" } });
            }

            // usages
            var uses = Lib.Usages(e.Hash);
            _details.Children.Add(Label(uses.Count == 0 ? "Not used by any known project" : "Used in " + uses.Select(u => u.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() + " project(s)"));
            foreach (var u in uses.Take(12))
            {
                var uu = u;
                bool here = ProjectRoot != null && string.Equals(GlobalAssetDatabase.NormalizeDir(ProjectRoot), uu.ProjectPath, StringComparison.OrdinalIgnoreCase);
                var row = new TextBlock { Text = (uu.ProjectName ?? Path.GetFileName(uu.ProjectPath)) + " · " + uu.RelativePath, Classes = { "small", here ? "secondary" : "tertiary" }, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(row, Path.Combine(uu.ProjectPath, uu.RelativePath) + (here ? "\nClick to show it in the Explorer" : ""));
                if (here)
                {
                    row.Cursor = new Cursor(StandardCursorType.Hand);
                    row.PointerPressed += (s, a) => { var p = Path.Combine(uu.ProjectPath, uu.RelativePath); if (File.Exists(p)) { AssetBrowserPanel.Current?.SetTab("Explorer"); AssetBrowserPanel.Current?.Reveal(p); } };
                }
                _details.Children.Add(row);
            }

            _details.Children.Add(Label("Content hash (SHA-256)"));
            var hash = new TextBlock { Text = e.Hash.Substring(0, 16) + "…", FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 11, Classes = { "secondary" } };
            ToolTip.SetTip(hash, e.Hash + "\nClick to copy");
            hash.PointerPressed += (s, a) => _ = CopyText(e.Hash);
            _details.Children.Add(hash);

            var del = Ui.Button("Delete from Library…", () => _ = DeleteSelected(), "Projects keep their copies");
            del.Margin = new Thickness(0, 14, 0, 0);
            del.Foreground = EditorKit.Brush("VxRedBrush");
            _details.Children.Add(del);
        }

        private void BuildMultiDetails(IReadOnlyList<LibraryEntry> sel)
        {
            _details.Children.Add(new TextBlock { Text = sel.Count + " assets selected", FontSize = 14, FontWeight = FontWeight.SemiBold });
            _details.Children.Add(new TextBlock { Text = GlobalAssetDatabase.FormatBytes(sel.Sum(x => x.Size)) + " · " + string.Join(", ", sel.GroupBy(x => TypeLabel(x.Type)).Select(g => g.Count() + " " + g.Key.ToLowerInvariant())), Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap });
            var list = sel.ToList();
            var stack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
            var add = Ui.Button("Add " + sel.Count + " to Project", () => _ = AddToProject(list, null, false), null, "accent");
            add.IsEnabled = ProjectRoot != null;
            add.HorizontalAlignment = HorizontalAlignment.Stretch;
            stack.Children.Add(add);
            stack.Children.Add(Ui.Button("Add Tags…", () => _ = BulkTags(list, true)));
            stack.Children.Add(Ui.Button("Remove Tags…", () => _ = BulkTags(list, false)));
            stack.Children.Add(Ui.Button("Export to Bundle…", () => _ = ExportBundle(list.Select(x => x.Id).ToList())));
            var del = Ui.Button("Delete " + sel.Count + " from Library…", () => _ = DeleteSelected());
            del.Foreground = EditorKit.Brush("VxRedBrush");
            stack.Children.Add(del);
            foreach (var c in stack.Children) if (c is Button b) b.HorizontalAlignment = HorizontalAlignment.Stretch;
            _details.Children.Add(stack);
        }

        private void BuildLibrarySummary()
        {
            var lib = Lib;
            _details.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { new VxIcon { Icon = "Library", Width = 20, Height = 20, Foreground = EditorKit.Brush("VxAccentBrush") }, new TextBlock { Text = "Asset Library", FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } },
            });
            _details.Children.Add(new TextBlock
            {
                Text = "Every asset imported on this machine, from every project — stored once by content (SHA-256). Select an asset to see where it is used, or double-click it to add it to the open project.",
                Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap,
            });
            if (!lib.IsAvailable) return;
            var st = lib.Stats(0);
            _details.Children.Add(Label("Contents"));
            _details.Children.Add(Value(st.Entries + " assets · " + GlobalAssetDatabase.FormatBytes(st.StoredBytes) + (st.SizeCapBytes > 0 ? " of " + GlobalAssetDatabase.FormatBytes(st.SizeCapBytes) : "") + " · " + st.Projects + " project(s)"));
            if (st.OverCap) _details.Children.Add(new TextBlock { Text = "Over the size cap — clean up in Maintenance.", Classes = { "small" }, Foreground = EditorKit.Brush("VxOrangeBrush") });
            foreach (var (type, count, bytes) in st.ByType.Take(8))
                _details.Children.Add(new TextBlock { Text = TypeLabel(type) + ": " + count + " · " + GlobalAssetDatabase.FormatBytes(bytes), Classes = { "small", "tertiary" } });
            _details.Children.Add(Label("Location"));
            var loc = new TextBlock { Text = lib.Root, Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap };
            _details.Children.Add(loc);
            int pending = LibraryProjects.PendingRegistrations;
            if (pending > 0) _details.Children.Add(new TextBlock { Text = pending + " import(s) are being added…", Classes = { "small", "tertiary" } });
            var stack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
            stack.Children.Add(Ui.Button("Index Existing Projects…", () => _ = LibraryIndexDialog.Run()));
            stack.Children.Add(Ui.Button("Maintenance…", () => LibraryMaintenanceWindow.Open()));
            stack.Children.Add(Ui.Button("Library Settings…", () => _ = LibrarySettingsDialog.Run()));
            foreach (var c in stack.Children) if (c is Button b) b.HorizontalAlignment = HorizontalAlignment.Stretch;
            _details.Children.Add(stack);
        }

        private static bool _promptChecked;

        /// <summary>The tab was opened: refresh, and once per machine offer to index the existing projects when the
        /// library is still empty (#64's first-run prompt).</summary>
        public async void OnShown()
        {
            Refresh();
            if (_promptChecked) return;
            _promptChecked = true;
            var lib = Lib;
            if (!lib.IsAvailable || lib.Settings.IndexPromptShown || lib.Count() > 0) return;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VORTEX_SMOKE_FULL"))) return;   // never block a smoke run
            int projects = 0;
            try { projects = ProjectService.Instance.GetAllProjects().Values.Count(p => Directory.Exists(Path.Combine(p.Path ?? "", "Assets"))); } catch { }
            if (projects == 0) return;
            lib.Settings.IndexPromptShown = true;
            try { lib.Settings.Save(); } catch { }
            bool go = await Dialogs.Confirm("Fill your asset library",
                "The new asset library keeps every asset you import — from every project, stored once — and lets you add it to any other project.\n\n" +
                "Add the assets of your " + projects + " existing project" + (projects == 1 ? "" : "s") + " now? Nothing in the projects changes except a content hash in their .vmeta files.",
                "Index Projects…", "Not Now");
            if (go) await LibraryIndexDialog.Run();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (_dirty && IsEffectivelyVisible) Refresh();
        }

        // ================================================================== smoke
        [ModuleInitializer]
        internal static void RegisterSmoke() => SmokeRegistry.Add("asset library", LibrarySmoke.Run);
    }
}
