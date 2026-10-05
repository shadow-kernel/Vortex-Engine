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
using Editor.Core.Assets.Library;
using Editor.Core.Assets.Store;
using Editor.Core.Data;
using Editor.Core.Services;
using VortexEditor.Controls;
using VortexEditor.Services;
using VortexEditor.Shell;
using VortexEditor.Shell.Library;
using VortexEditor.Shell.Material;
using VortexEditor.Shell.ModelTools;
using AssetActions = VortexEditor.Services.AssetActions;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>One store result tile.</summary>
    public sealed class StoreTile : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public StoreItem Item { get; set; }
        public string Name => Item.Name;
        public string Sub => string.IsNullOrEmpty(Item.Author) ? Item.Kind.ToString() : Item.Author;
        public string LicenseBadge => Item.License?.Badge ?? "?";
        public IBrush LicenseBrush => StoreView.LicenseBrush(Item.License);
        public string Icon { get; set; }
        public string ToolTip { get; set; }
        internal bool Requested;

        private bool _inLibrary;
        public bool InLibrary { get => _inLibrary; set { if (_inLibrary == value) return; _inLibrary = value; Raise(); } }

        private Bitmap _thumb;
        public Bitmap Thumbnail { get => _thumb; set { if (ReferenceEquals(_thumb, value)) return; _thumb = value; Raise(); Raise(nameof(HasThumbnail)); Raise(nameof(NoThumbnail)); } }
        public bool HasThumbnail => _thumb != null;
        public bool NoThumbnail => _thumb == null;

        private bool _selected;
        public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; Raise(); Raise(nameof(SelectionBrush)); Raise(nameof(SelectionBorder)); } }
        public IBrush SelectionBrush => _selected ? EditorKit.Brush("VxAccentSoftBrush") : Brushes.Transparent;
        public IBrush SelectionBorder => _selected ? EditorKit.Brush("VxAccentBrush") : Brushes.Transparent;

        private double _size = 96;
        public double TileSize { get => _size; set { if (_size == value) return; _size = value; Raise(); Raise(nameof(TileWidth)); Raise(nameof(IconSize)); } }
        public double TileWidth => _size + 14;
        public double IconSize => Math.Round(_size * 0.36);
    }

    /// <summary>A row of the downloads strip.</summary>
    public sealed class StoreJobRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public StoreJob Job { get; }
        public StoreJobRow(StoreJob j) { Job = j; }
        public string Title => Job.Item.Name + (Job.Variant != null && !string.IsNullOrEmpty(Job.Variant.Label) ? " · " + Job.Variant.Label : "");
        public string Status => Job.State == StoreJobState.Downloading && Job.BytesTotal > 0
            ? GlobalAssetDatabase.FormatBytes(Job.BytesDone) + " / " + GlobalAssetDatabase.FormatBytes(Job.BytesTotal)
            : Job.Message ?? Job.State.ToString();
        public double Progress => Job.Progress * 100;
        public bool IsActive => Job.IsActive;
        public bool CanRetry => Job.State == StoreJobState.Failed || Job.State == StoreJobState.Cancelled;
        public bool IsDone => Job.State == StoreJobState.Done;
        public IBrush StatusBrush => Job.State == StoreJobState.Failed ? EditorKit.Brush("VxRedBrush") : Job.State == StoreJobState.Done ? EditorKit.Brush("VxGreenBrush") : EditorKit.Brush("VxTextSecondaryBrush");
        public void Update()
        {
            foreach (var n in new[] { nameof(Status), nameof(Progress), nameof(IsActive), nameof(CanRetry), nameof(IsDone), nameof(StatusBrush) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }

    /// <summary>
    /// The Store tab of the Asset Browser (#67): free asset sources — Poly Haven, ambientCG, Kenney, poly.pizza, Freesound,
    /// Sketchfab — with search (the browser's search box), kinds, categories, a license filter, thumbnails with license
    /// badges, a detail panel (license text + link, author, source, sizes and formats before downloading) and a downloads
    /// strip with progress, cancel and retry. Downloads land in the asset library and, on request, in the open project.
    /// Mixamo and Sonniss have no API: their pages explain the guided flow and take dropped / watched / indexed files.
    /// </summary>
    public sealed class StoreView : UserControl
    {
        public static StoreView Current { get; private set; }

        private object _provider;   // IAssetProvider or IGuidedProvider
        private StoreKind? _kind;
        private string _category;
        private string _search = "";
        private bool _includeNc;
        private int _page;
        private bool _hasMore;
        private CancellationTokenSource _cts;
        private readonly ObservableCollection<StoreTile> _tiles = new ObservableCollection<StoreTile>();
        private readonly ObservableCollection<StoreJobRow> _jobs = new ObservableCollection<StoreJobRow>();
        private StoreTile _selected;
        private double _tileSize = 96;
        private FileSystemWatcher _downloadsWatcher;

        private readonly ListBox _providers = new ListBox { SelectionMode = SelectionMode.Single };
        private readonly StackPanel _kindChips = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly ComboBox _categories = new ComboBox { MinWidth = 140, PlaceholderText = "All categories" };
        private readonly CheckBox _nc = new CheckBox { Content = "NC / ND", FontSize = 11 };
        private readonly Button _keyButton;
        private readonly TextBlock _status = new TextBlock { Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
        private readonly ItemsControl _grid = new ItemsControl();
        private readonly ScrollViewer _gridScroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        private readonly Button _more;
        private readonly TextBlock _attribution = new TextBlock { Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 4, 10, 6) };
        private readonly StackPanel _empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 6, MaxWidth = 440 };
        private readonly StackPanel _details = new StackPanel { Spacing = 6, Margin = new Thickness(14, 12, 14, 14) };
        private readonly Grid _main = new Grid();
        private readonly Border _guidedHost = new Border { IsVisible = false };
        private readonly Border _downloadsHost;
        private readonly ItemsControl _downloads = new ItemsControl();
        private readonly Border _topBar;

        public IReadOnlyList<StoreTile> Tiles => _tiles;
        public IAssetProvider WebProvider => _provider as IAssetProvider;
        public string ProviderId => (_provider as IAssetProvider)?.Id ?? (_provider as IGuidedProvider)?.Id;
        public bool Loading { get; private set; }
        public string LastError { get; private set; }

        public StoreView()
        {
            Current = this;
            // ---- providers (left)
            var provItems = new List<object>();
            foreach (var p in StoreProviders.Web) provItems.Add(ProviderRow(p.Id, p.Name, p.Tagline, p.Access == ProviderAccess.Anonymous ? "free" : StoreKeys.Has(p.Id) ? "key ✓" : "key", p));
            provItems.Add(new TextBlock { Text = "GUIDED", Classes = { "small", "tertiary" }, Margin = new Thickness(8, 10, 0, 2), IsHitTestVisible = false });
            foreach (var g in StoreProviders.Guided) provItems.Add(ProviderRow(g.Id, g.Name, g.Tagline, "guided", g));
            _providers.ItemsSource = provItems;
            _providers.SelectionChanged += (s, e) =>
            {
                if (_providers.SelectedItem is Control c && c.Tag != null) SelectProvider(c.Tag);
            };
            var left = new Border
            {
                Width = 210, BorderBrush = EditorKit.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 0, 1, 0),
                Child = new ScrollViewer { Content = _providers },
            };
            DockPanel.SetDock(left, Dock.Left);

            // ---- top bar
            _keyButton = Ui.Button("API Key…", () => _ = StoreKeysDialog.Run(ProviderId), "Your own key for this source (stored only on this machine)");
            _keyButton.Margin = new Thickness(4, 0, 0, 0);
            _categories.SelectionChanged += (s, e) => { var c = _categories.SelectedItem as string; _category = c == "All categories" ? null : c; NewSearch(); };
            _nc.IsCheckedChanged += (s, e) => { _includeNc = _nc.IsChecked == true; NewSearch(); };
            ToolTip.SetTip(_nc, "Also show NonCommercial (NC) and NoDerivatives (ND) licenses — NC assets can't ship in a game you sell, ND assets may not be modified (re-texturing, cutting, re-rigging)");
            var bar = new DockPanel { Height = 32, Margin = new Thickness(8, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(_status);
            right.Children.Add(_nc);
            right.Children.Add(_keyButton);
            right.Children.Add(EditorKit.IconButton("Refresh", "Search again (skip the cache)", () => { if (WebProvider != null) StoreHttp.ClearResponses(WebProvider.Id); NewSearch(); }, 13));
            right.Children.Add(EditorKit.IconButton("Gear", "Store API keys", () => _ = StoreKeysDialog.Run(null), 13));
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
            var leftBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            leftBar.Children.Add(new Border { Classes = { "segmented" }, Child = _kindChips });
            leftBar.Children.Add(_categories);
            bar.Children.Add(leftBar);
            _topBar = new Border { Classes = { "hairline-bottom" }, Child = bar };
            DockPanel.SetDock(_topBar, Dock.Top);

            // ---- grid
            _grid.ItemsPanel = new FuncTemplate<Panel>(() => new WrapPanel { Orientation = Orientation.Horizontal });
            _grid.ItemTemplate = new FuncDataTemplate<StoreTile>((t, ns) => BuildTile(), true);
            _grid.ItemsSource = _tiles;
            _more = Ui.Button("Load More", () => _ = Load(append: true), "Next page of results");
            _more.HorizontalAlignment = HorizontalAlignment.Center;
            _more.Margin = new Thickness(0, 8, 0, 12);
            _more.IsVisible = false;
            _gridScroll.Content = new StackPanel { Children = { _grid, _more } };
            _gridScroll.Padding = new Thickness(6, 4);
            _gridScroll.ScrollChanged += (s, e) =>
            {
                if (_hasMore && !Loading && _gridScroll.Offset.Y + _gridScroll.Viewport.Height > _gridScroll.Extent.Height - 120) _ = Load(append: true);
            };
            var center = new DockPanel();
            DockPanel.SetDock(_attribution, Dock.Bottom);
            center.Children.Add(_attribution);
            var gridHost = new Grid();
            gridHost.Children.Add(_gridScroll);
            gridHost.Children.Add(_empty);
            center.Children.Add(gridHost);

            var detailsHost = new Border
            {
                Width = 270, BorderBrush = EditorKit.Brush("VxHairlineBrush"), BorderThickness = new Thickness(1, 0, 0, 0),
                Child = new ScrollViewer { Content = _details, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            };
            DockPanel.SetDock(detailsHost, Dock.Right);

            // ---- downloads strip
            _downloads.ItemTemplate = new FuncDataTemplate<StoreJobRow>((r, ns) => BuildJobRow(), true);
            _downloads.ItemsSource = _jobs;
            var dlHead = new DockPanel { Margin = new Thickness(10, 4, 10, 2) };
            var clear = Ui.Button("Clear Finished", () => { StoreDownloads.ClearFinished(); SyncJobs(); });
            clear.Classes.Add("ghost");
            DockPanel.SetDock(clear, Dock.Right);
            dlHead.Children.Add(clear);
            dlHead.Children.Add(new TextBlock { Text = "Downloads", Classes = { "small", "secondary" }, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            _downloadsHost = new Border
            {
                BorderBrush = EditorKit.Brush("VxHairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), MaxHeight = 150, IsVisible = false,
                Child = new DockPanel { Children = { dlHead, new ScrollViewer { Content = _downloads } } },
            };
            DockPanel.SetDock(dlHead, Dock.Top);
            DockPanel.SetDock(_downloadsHost, Dock.Bottom);

            var webDock = new DockPanel();
            webDock.Children.Add(_topBar);
            webDock.Children.Add(detailsHost);
            webDock.Children.Add(center);
            _main.Children.Add(webDock);
            _main.Children.Add(_guidedHost);

            var root = new DockPanel();
            root.Children.Add(left);
            root.Children.Add(_downloadsHost);
            root.Children.Add(_main);
            Content = root;

            StoreDownloads.JobChanged += j => Dispatcher.UIThread.Post(() => OnJobChanged(j));
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, (s, e) => { e.DragEffects = _provider is IGuidedProvider && AssetDragData.OsFiles(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
            AddHandler(DragDrop.DropEvent, (s, e) => { var f = AssetDragData.OsFiles(e.Data); if (f != null && _provider is IGuidedProvider g) _ = GuidedImport(g, f); e.Handled = true; });
            _providers.SelectedIndex = 0;
        }

        private static Control ProviderRow(string id, string name, string tagline, string badge, object tag)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Tag = tag, Margin = new Thickness(2, 3) };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold });
            text.Children.Add(new TextBlock { Text = tagline, Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 150 });
            g.Children.Add(text);
            var b = new Border
            {
                Background = EditorKit.Brush(badge == "free" ? "VxGreenBrush" : badge == "guided" ? "VxOrangeBrush" : "VxAccentBrush"), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 0, 5, 1), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 2, 0, 0),
                Child = new TextBlock { Text = badge, FontSize = 9.5, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold },
            };
            Grid.SetColumn(b, 1);
            g.Children.Add(b);
            return g;
        }

        // ================================================================== providers
        public void SelectProvider(object p)
        {
            _provider = p;
            _selected = null; _details_current = null;
            AssetActions.StopAudition();
            if (p is IGuidedProvider guided)
            {
                _main.Children[0].IsVisible = false;
                _guidedHost.IsVisible = true;
                _guidedHost.Child = BuildGuided(guided);
                return;
            }
            _main.Children[0].IsVisible = true;
            _guidedHost.IsVisible = false;
            var web = (IAssetProvider)p;
            _kind = web.Kinds.FirstOrDefault();
            _category = null;
            _kindChips.Children.Clear();
            foreach (var k in web.Kinds)
            {
                var kind = k;
                var rb = new RadioButton { GroupName = "storekind", Content = KindLabel(k), IsChecked = k == _kind, Padding = new Thickness(6, 2) };
                rb.Click += (s, e) => { _kind = kind; _category = null; _ = LoadCategories(); NewSearch(); };
                _kindChips.Children.Add(rb);
            }
            if (_kindChips.Parent is Control chipHost) chipHost.IsVisible = web.Kinds.Count > 1;
            _nc.IsVisible = web.HasLicenseFilter;
            _keyButton.IsVisible = web.Access == ProviderAccess.ApiKey;
            _keyButton.Content = StoreKeys.Has(web.Id) ? "API Key ✓" : "API Key…";
            _attribution.Text = web.Attribution;
            _ = LoadCategories();
            NewSearch();
        }

        public void SelectProvider(string id)
        {
            object p = (object)StoreProviders.Get(id) ?? StoreProviders.Guided.FirstOrDefault(g => g.Id == id);
            if (p == null) return;
            foreach (var item in _providers.ItemsSource.Cast<object>())
                if (item is Control c && ReferenceEquals(c.Tag, p)) { _providers.SelectedItem = item; return; }
        }

        private static string KindLabel(StoreKind k) => k == StoreKind.Hdri ? "HDRIs" : k == StoreKind.Material ? "Materials" : k == StoreKind.Model ? "Models" : k == StoreKind.Sound ? "Sounds" : k == StoreKind.Pack ? "Packs" : k.ToString();

        private async Task LoadCategories()
        {
            var web = WebProvider;
            if (web == null) return;
            IReadOnlyList<string> cats = Array.Empty<string>();
            try { cats = await web.CategoriesAsync(_kind ?? web.Kinds[0], CancellationToken.None); } catch { }
            if (!ReferenceEquals(web, WebProvider)) return;
            var list = new List<string> { "All categories" };
            list.AddRange(cats.Take(60));
            _categories.ItemsSource = list;
            _categories.IsVisible = cats.Count > 0;
        }

        // ================================================================== search
        public void SetSearch(string text)
        {
            text = (text ?? "").Trim();
            if (text == _search) return;
            _search = text;
            if (WebProvider != null) NewSearch();
        }

        public string Search => _search;

        private void NewSearch() { _page = 0; _ = Load(append: false); }

        /// <summary>Run the current search (tests await it).</summary>
        public async Task Load(bool append)
        {
            var web = WebProvider;
            if (web == null) return;
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            if (!append) { _page = 0; _tiles.Clear(); _empty.IsVisible = false; }
            else _page++;
            Loading = true; LastError = null;
            _status.Text = "Searching…";
            _more.IsVisible = false;
            var q = new StoreQuery { Text = _search, Kind = _kind, Category = _category, Page = _page, PageSize = 40, IncludeNonCommercial = _includeNc, IncludeNoDerivatives = _includeNc };
            StorePage page;
            try { page = await Task.Run(() => web.SearchAsync(q, cts.Token)); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { page = new StorePage { Error = ex.Message }; }
            if (cts.IsCancellationRequested || !ReferenceEquals(web, WebProvider)) return;
            Loading = false;
            if (page.NeedsKey) { _status.Text = ""; ShowNeedsKey(web); return; }
            if (page.Error != null)
            {
                LastError = page.Error; _status.Text = "";
                if (_tiles.Count == 0) ShowEmpty("Warning", web.Name + " didn't answer", page.Error, ("Try Again", NewSearch), ("Open " + Host(web.HomeUrl), () => Open(web.HomeUrl)));
                return;
            }
            var inLib = await Task.Run(() => GlobalAssetDatabase.Instance.IsAvailable ? GlobalAssetDatabase.Instance.StoreItemsInLibrary(web.Id) : new HashSet<string>());
            foreach (var it in page.Items)
            {
                var t = new StoreTile { Item = it, TileSize = _tileSize, Icon = IconFor(it.Kind), InLibrary = inLib.Contains(it.Key) };
                t.ToolTip = it.Name + (string.IsNullOrEmpty(it.Author) ? "" : "\nby " + it.Author) + "\n" + (it.License?.Name ?? "") + (t.InLibrary ? "\nAlready in your library" : "");
                _tiles.Add(t);
            }
            _hasMore = page.HasMore;
            _more.IsVisible = page.HasMore;
            _status.Text = page.Total >= 0 ? _tiles.Count + " of " + page.Total : _tiles.Count + " results";
            if (_tiles.Count == 0)
                ShowEmpty("Search", string.IsNullOrEmpty(_search) ? "Nothing here yet" : "Nothing found for “" + _search + "”",
                    web.HasLicenseFilter && !_includeNc ? "NonCommercial and NoDerivatives results are hidden — tick NC / ND to see them." : "Try another word or category.", (null, null), (null, null));
            else _empty.IsVisible = false;
        }

        private static string IconFor(StoreKind k) => k == StoreKind.Sound ? "Audio" : k == StoreKind.Material ? "Material" : k == StoreKind.Hdri ? "Sun" : k == StoreKind.Pack ? "Layers" : "Cube";
        private static string Host(string url) { try { return new Uri(url).Host; } catch { return url; } }

        public static IBrush LicenseBrush(StoreLicense l)
        {
            if (l == null) return EditorKit.Brush("VxTextTertiaryBrush");
            if (!l.Commercial || l.Id == "Unknown") return EditorKit.Brush("VxRedBrush");
            if (l.NoDerivatives || l.ShareAlike || !l.Redistributable) return EditorKit.Brush("VxOrangeBrush");
            if (l.Attribution) return EditorKit.Brush("VxAccentBrush");
            return EditorKit.Brush("VxGreenBrush");
        }

        public void SetTileSize(double size) { _tileSize = Math.Round(size); foreach (var t in _tiles) t.TileSize = _tileSize; }

        private Control BuildTile()
        {
            var icon = new VxIcon();
            icon[!VxIcon.IconProperty] = new Binding("Icon");
            icon[!WidthProperty] = new Binding("IconSize");
            icon[!HeightProperty] = new Binding("IconSize");
            icon[!IsVisibleProperty] = new Binding("NoThumbnail");
            icon.Foreground = EditorKit.Brush("VxTextTertiaryBrush");
            var img = new Image { Stretch = Stretch.UniformToFill };
            img[!Image.SourceProperty] = new Binding("Thumbnail");
            img[!IsVisibleProperty] = new Binding("HasThumbnail");
            var licText = new TextBlock();
            licText[!TextBlock.TextProperty] = new Binding("LicenseBadge");
            var lic = new Border { Classes = { "tilebadge" }, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 5, 5), Child = licText };
            lic[!Border.BackgroundProperty] = new Binding("LicenseBrush");
            var inLib = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xA6, 0, 0, 0)), CornerRadius = new CornerRadius(4), Padding = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(5), IsHitTestVisible = false,
                Child = new VxIcon { Icon = "Library", Width = 10, Height = 10, Foreground = Brushes.White },
            };
            inLib[!IsVisibleProperty] = new Binding("InLibrary");
            var square = new Border
            {
                CornerRadius = new CornerRadius(10), Background = EditorKit.Brush("VxFieldBrush"), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Panel { Children = { icon, img, lic, inLib } },
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
            root.DataContextChanged += (s, e) => RequestThumb(root.DataContext as StoreTile);
            root.AttachedToVisualTree += (s, e) => RequestThumb(root.DataContext as StoreTile);
            root.PointerPressed += (s, e) =>
            {
                if (!(root.DataContext is StoreTile t)) return;
                Select(t);
                if (e.ClickCount >= 2) _ = Download(addToProject: false);
                else if (t.Item.Kind == StoreKind.Sound) _ = PlayPreview(t.Item);
            };
            return root;
        }

        private void RequestThumb(StoreTile t)
        {
            if (t == null || t.Requested) return;
            t.Requested = true;
            var web = WebProvider;
            var item = t.Item;
            _ = Task.Run(async () =>
            {
                try
                {
                    var p = StoreProviders.Get(item.ProviderId) ?? web;
                    string url = p != null ? await p.ThumbnailUrlAsync(item, CancellationToken.None) : item.ThumbnailUrl;
                    string file = await StoreHttp.GetImageFileAsync(url, CancellationToken.None);
                    if (file == null) return;
                    Bitmap bmp;
                    using (var s = File.OpenRead(file)) bmp = Bitmap.DecodeToWidth(s, 256);
                    Dispatcher.UIThread.Post(() => t.Thumbnail = bmp);
                }
                catch { }
            });
        }

        // ================================================================== selection + details
        public void Select(StoreTile t)
        {
            if (_selected != null) _selected.IsSelected = false;
            _selected = t;
            if (t != null) t.IsSelected = true;
            _ = ShowDetails(t);
        }

        private async Task ShowDetails(StoreTile t)
        {
            _details.Children.Clear();
            _details_variants = null;
            if (t == null || WebProvider == null) { BuildProviderSummary(); return; }
            var web = WebProvider;
            var it = t.Item;
            var preview = new Border { Height = 150, CornerRadius = new CornerRadius(10), Background = EditorKit.Brush("VxFieldBrush"), ClipToBounds = true };
            preview.Child = t.Thumbnail != null ? (Control)new Image { Source = t.Thumbnail, Stretch = Stretch.Uniform } : new VxIcon { Icon = IconFor(it.Kind), Width = 48, Height = 48 };
            _details.Children.Add(preview);
            _details.Children.Add(new TextBlock { Text = it.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            if (!string.IsNullOrEmpty(it.Author)) _details.Children.Add(new TextBlock { Text = "by " + it.Author + " · " + web.Name, Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap });

            // license, prominent
            var l = it.License ?? StoreLicense.Get("Unknown");
            var licRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
            licRow.Children.Add(new Border { Background = LicenseBrush(l), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Child = new TextBlock { Text = l.Badge, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, FontSize = 11 } });
            if (!string.IsNullOrEmpty(l.Url))
            {
                var link = new TextBlock { Text = "License text", Foreground = EditorKit.Brush("VxAccentBrush"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand) };
                link.PointerPressed += (s, e) => Open(l.Url);
                licRow.Children.Add(link);
            }
            _details.Children.Add(licRow);
            _details.Children.Add(new TextBlock { Text = l.Name + (l.Attribution ? " — credit the author (CREDITS.md is generated on export)." : ".") + (!l.Commercial ? " Not allowed in a game you sell." : "") + (string.IsNullOrEmpty(l.Notes) ? "" : " " + l.Notes), Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap });

            var loading = new TextBlock { Text = "Loading details…", Classes = { "small", "tertiary" } };
            _details.Children.Add(loading);
            StoreDetails d;
            try { d = await Task.Run(() => web.DetailsAsync(it, CancellationToken.None)); }
            catch (Exception ex) { loading.Text = "Details unavailable: " + ex.Message; return; }
            if (!ReferenceEquals(_selected, t)) return;
            _details_current = d;
            _details.Children.Remove(loading);
            foreach (var f in d.Facts) _details.Children.Add(new TextBlock { Text = f, Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(d.Description)) _details.Children.Add(new TextBlock { Text = d.Description.Length > 280 ? d.Description.Substring(0, 280) + "…" : d.Description, Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap });
            if (d.Variants.Count > 0)
            {
                _details.Children.Add(new TextBlock { Text = "Download", Classes = { "small", "tertiary" }, Margin = new Thickness(0, 8, 0, 0) });
                _details_variants = new ComboBox { ItemsSource = d.Variants, HorizontalAlignment = HorizontalAlignment.Stretch, SelectedItem = d.Variants.FirstOrDefault(v => v.Id == d.DefaultVariant) ?? d.Variants[0] };
                _details.Children.Add(_details_variants);
            }
            var buttons = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            bool inLib = t.InLibrary;
            var dl = Ui.Button(inLib ? "In Library — Add to Project" : "Download + Add to Project", () => _ = Download(addToProject: true), "Download into the library and copy into the open project", "accent");
            dl.IsEnabled = ProjectData.Current != null;
            var lib = Ui.Button(inLib ? "Download Again" : "Download to Library", () => _ = Download(addToProject: false, force: inLib), "Into the machine-wide library only");
            buttons.Children.Add(dl); buttons.Children.Add(lib);
            if (it.Kind == StoreKind.Sound && it.PreviewUrl != null) buttons.Children.Add(Ui.Button("Play Preview", () => _ = PlayPreview(it)));
            if (!string.IsNullOrEmpty(it.PageUrl)) buttons.Children.Add(Ui.Button("Open on " + web.Name, () => Open(it.PageUrl)));
            foreach (var c in buttons.Children) if (c is Button b) b.HorizontalAlignment = HorizontalAlignment.Stretch;
            _details.Children.Add(buttons);
            if (web.Access == ProviderAccess.ApiKey && !StoreKeys.Has(web.Id))
                _details.Children.Add(new TextBlock { Text = "Downloading needs your " + web.Name + " " + web.KeyName + " — click API Key… above.", Classes = { "small" }, Foreground = EditorKit.Brush("VxOrangeBrush"), TextWrapping = TextWrapping.Wrap });
        }

        private ComboBox _details_variants;
        private StoreDetails _details_current;

        private void BuildProviderSummary()
        {
            var web = WebProvider;
            if (web == null) return;
            _details.Children.Add(new TextBlock { Text = web.Name, FontSize = 15, FontWeight = FontWeight.SemiBold });
            _details.Children.Add(new TextBlock { Text = web.Tagline, Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap });
            _details.Children.Add(new TextBlock { Text = web.Attribution, Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            _details.Children.Add(new TextBlock
            {
                Text = "Click a result for its license, sizes and formats. Downloads go into your asset library (stored once, with source, author and license) and — if you like — straight into the open project.",
                Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
            var open = Ui.Button("Open " + Host(web.HomeUrl), () => Open(web.HomeUrl));
            open.HorizontalAlignment = HorizontalAlignment.Stretch;
            open.Margin = new Thickness(0, 8, 0, 0);
            _details.Children.Add(open);
        }

        /// <summary>Download the selected result (tests call it with a tile selected).</summary>
        public async Task<StoreJob> Download(bool addToProject, bool force = false)
        {
            var web = WebProvider;
            var t = _selected;
            if (web == null || t == null) return null;
            var variant = _details_variants?.SelectedItem as StoreVariant;
            if (variant == null)
            {
                try { var d = _details_current ?? await Task.Run(() => web.DetailsAsync(t.Item, CancellationToken.None)); variant = d.Variants.FirstOrDefault(v => v.Id == d.DefaultVariant) ?? d.Variants.FirstOrDefault(); }
                catch (Exception ex) { EditorCommands.Toast(ex.Message); return null; }
            }
            var p = ProjectData.Current;
            var job = StoreDownloads.Enqueue(web, t.Item, variant, addToProject && p != null, p?.Path, p?.Name, null, force);
            SyncJobs();
            _downloadsHost.IsVisible = true;
            return job;
        }

        private async Task PlayPreview(StoreItem it)
        {
            if (string.IsNullOrEmpty(it.PreviewUrl)) return;
            var file = await Task.Run(() => StoreHttp.GetCachedFileAsync(it.PreviewUrl, Path.GetExtension(new Uri(it.PreviewUrl).AbsolutePath) is string e && e.Length > 1 ? e : ".mp3", CancellationToken.None));
            if (file != null) AssetActions.Audition(file);
        }

        private void Open(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url)); } catch { }
        }

        // ================================================================== downloads strip
        private void SyncJobs()
        {
            var jobs = StoreDownloads.Jobs;
            foreach (var row in _jobs.ToList()) if (!jobs.Contains(row.Job)) _jobs.Remove(row);
            foreach (var j in jobs) if (!_jobs.Any(r => ReferenceEquals(r.Job, j))) _jobs.Insert(0, new StoreJobRow(j));
            _downloadsHost.IsVisible = _jobs.Count > 0;
        }

        private void OnJobChanged(StoreJob j)
        {
            var row = _jobs.FirstOrDefault(r => ReferenceEquals(r.Job, j));
            if (row == null) { SyncJobs(); row = _jobs.FirstOrDefault(r => ReferenceEquals(r.Job, j)); }
            row?.Update();
            if (!j.IsActive)
            {
                if (j.State == StoreJobState.Done)
                {
                    foreach (var t in _tiles.Where(x => x.Item.Key == j.Item.Key)) t.InLibrary = true;
                    if (j.ProjectPaths.Count > 0) { try { Editor.Core.Assets.AssetDatabase.Instance.Refresh(); } catch { } EditorCommands.Window?.FileTree?.Reload(); }
                    EditorCommands.Toast(j.Item.Name + ": " + j.Message);
                }
                else if (j.State == StoreJobState.Failed) ConsoleService.Instance.LogWarning("Store: " + j.Item.Name + " — " + j.Error);
            }
        }

        private Control BuildJobRow()
        {
            var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
            title[!TextBlock.TextProperty] = new Binding("Title");
            var status = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            status[!TextBlock.TextProperty] = new Binding("Status");
            status[!TextBlock.ForegroundProperty] = new Binding("StatusBrush");
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, MinWidth = 80 };
            bar[!ProgressBar.ValueProperty] = new Binding("Progress");
            bar[!IsVisibleProperty] = new Binding("IsActive");
            var cancel = EditorKit.IconButton("Close", "Cancel", null, 11);
            cancel[!IsVisibleProperty] = new Binding("IsActive");
            var retry = EditorKit.IconButton("Refresh", "Retry (resumes partial downloads)", null, 11);
            retry[!IsVisibleProperty] = new Binding("CanRetry");
            var show = EditorKit.IconButton("Library", "Show in the Library", null, 11);
            show[!IsVisibleProperty] = new Binding("IsDone");
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*,Auto,Auto,Auto,Auto"), Margin = new Thickness(10, 1) };
            g.Children.Add(title);
            Grid.SetColumn(status, 1); status.Margin = new Thickness(8, 0); g.Children.Add(status);
            Grid.SetColumn(bar, 2); bar.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(bar);
            Grid.SetColumn(cancel, 3); g.Children.Add(cancel);
            Grid.SetColumn(retry, 4); g.Children.Add(retry);
            Grid.SetColumn(show, 5); g.Children.Add(show);
            cancel.Click += (s, e) => { if (g.DataContext is StoreJobRow r) StoreDownloads.Cancel(r.Job); };
            retry.Click += (s, e) => { if (g.DataContext is StoreJobRow r) { StoreDownloads.Retry(r.Job); SyncJobs(); } };
            show.Click += (s, e) =>
            {
                if (!(g.DataContext is StoreJobRow r) || r.Job.Entries.Count == 0) return;
                var panel = AssetBrowserPanel.Current;
                if (r.Job.ProjectPaths.Count > 0) { panel?.SetTab("Explorer"); panel?.Reveal(r.Job.ProjectPaths[0]); return; }
                panel?.SetTab("Library");
                panel?.SetSearch(r.Job.Entries[0].Name);
            };
            return g;
        }

        // ================================================================== empty states
        private void ShowEmpty(string icon, string title, string sub, (string label, Action click) a, (string label, Action click) b)
        {
            _empty.Children.Clear();
            _empty.Children.Add(new VxIcon { Icon = icon, Width = 30, Height = 30, Foreground = EditorKit.Brush("VxTextTertiaryBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            _empty.Children.Add(new TextBlock { Text = title, Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(sub)) _empty.Children.Add(new TextBlock { Text = sub, Classes = { "tertiary", "small" }, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            if (a.label != null) row.Children.Add(Ui.Button(a.label, a.click, null, "accent"));
            if (b.label != null) row.Children.Add(Ui.Button(b.label, b.click));
            if (row.Children.Count > 0) _empty.Children.Add(row);
            _empty.IsVisible = true;
        }

        private void ShowNeedsKey(IAssetProvider web)
        {
            _empty.Children.Clear();
            _empty.Children.Add(new VxIcon { Icon = "Link", Width = 30, Height = 30, Foreground = EditorKit.Brush("VxTextTertiaryBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            _empty.Children.Add(new TextBlock { Text = web.Name + " needs your own " + web.KeyName, Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Center });
            _empty.Children.Add(new TextBlock
            {
                Text = "It's free: create one on " + Host(web.KeyHelpUrl) + " and paste it here. It is stored only on this machine — the editor never ships a shared key.",
                Classes = { "tertiary", "small" }, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            });
            var box = new TextBox { Watermark = web.KeyName, PasswordChar = '•', Width = 280, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            _empty.Children.Add(box);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(Ui.Button("Save Key", () => { if (!string.IsNullOrWhiteSpace(box.Text)) { StoreKeys.Set(web.Id, box.Text); _keyButton.Content = "API Key ✓"; NewSearch(); } }, null, "accent"));
            row.Children.Add(Ui.Button("Get a Key", () => Open(web.KeyHelpUrl)));
            _empty.Children.Add(row);
            _empty.IsVisible = true;
        }

        // ================================================================== guided providers
        private Control BuildGuided(IGuidedProvider g)
        {
            var stack = new StackPanel { Margin = new Thickness(24, 18, 24, 18), Spacing = 8, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
            stack.Children.Add(new TextBlock { Text = g.Name, FontSize = 18, FontWeight = FontWeight.SemiBold });
            stack.Children.Add(new TextBlock { Text = g.Tagline, Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap });
            var lic = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            lic.Children.Add(new Border { Background = LicenseBrush(g.License), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Child = new TextBlock { Text = g.License.Badge, Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeight.SemiBold } });
            lic.Children.Add(new TextBlock { Text = g.License.Notes, Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(lic);
            int n = 1;
            foreach (var step in g.Steps)
                stack.Children.Add(new TextBlock { Text = (n++) + ".  " + step, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            void B(string text, Action a, string cls = null) { var b = Ui.Button(text, a, null, cls); b.Margin = new Thickness(0, 0, 8, 8); buttons.Children.Add(b); }
            B("Open " + Host(g.HomeUrl), () => Open(g.HomeUrl), "accent");
            var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
            var status = new TextBlock { Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap };
            if (g is SonnissProvider)
                B("Index a Folder…", async () =>
                {
                    var picked = await TopLevel.GetTopLevel(this).StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder with the extracted Sonniss WAVs" });
                    var dir = picked?.FirstOrDefault()?.TryGetLocalPath();
                    if (string.IsNullOrEmpty(dir)) return;
                    var files = await Task.Run(() => SonnissProvider.FindWavs(dir).ToList());
                    await RunGuided(g, files, progress, status);
                });
            else
            {
                B("Choose FBX Files…", async () =>
                {
                    var files = await TopLevel.GetTopLevel(this).StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Mixamo FBX files", AllowMultiple = true, FileTypeFilter = new[] { new FilePickerFileType("FBX") { Patterns = new[] { "*.fbx" } } } });
                    var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToList();
                    if (paths.Count > 0) await RunGuided(g, paths, progress, status);
                });
                var watch = new CheckBox { Content = "Watch my Downloads folder for new FBX files", IsChecked = _downloadsWatcher != null };
                watch.IsCheckedChanged += (s, e) => SetWatchDownloads(g, watch.IsChecked == true, status);
                buttons.Children.Add(watch);
            }
            stack.Children.Add(buttons);
            stack.Children.Add(progress);
            stack.Children.Add(status);
            stack.Children.Add(new Border
            {
                Margin = new Thickness(0, 10, 0, 0), Height = 90, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5),
                BorderBrush = EditorKit.Brush("VxHairlineBrush"), Background = EditorKit.Brush("VxFieldBrush"),
                Child = new TextBlock { Text = "Drop " + string.Join(" / ", g.FilePatterns) + " files here", Classes = { "tertiary" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            return new ScrollViewer { Content = stack };
        }

        private Task GuidedImport(IGuidedProvider g, IEnumerable<string> files) => RunGuided(g, files.ToList(), null, null);

        private async Task RunGuided(IGuidedProvider g, List<string> files, ProgressBar bar, TextBlock status)
        {
            if (files.Count == 0) { if (status != null) status.Text = "No " + string.Join("/", g.FilePatterns) + " files found."; return; }
            if (bar != null) { bar.IsVisible = true; bar.Value = 0; }
            var progress = new Progress<LibraryProgress>(p => { if (bar != null) bar.Value = p.Fraction; if (status != null) status.Text = p.Phase + " " + p.Done + " / " + p.Total; });
            List<RegisterResult> results;
            try { results = await g.ImportAsync(files, progress, CancellationToken.None); }
            catch (Exception ex) { if (status != null) status.Text = ex.Message; return; }
            finally { if (bar != null) bar.IsVisible = false; }
            int ok = results.Count(r => r.Success && !r.Skipped), known = results.Count(r => r.EntryExisted);
            string msg = g.Name + ": " + ok + " file(s) in your library" + (known > 0 ? " (" + known + " were already there)" : "") + ".";
            if (status != null) status.Text = msg;
            EditorCommands.Toast(msg);
        }

        private void SetWatchDownloads(IGuidedProvider g, bool on, TextBlock status)
        {
            _downloadsWatcher?.Dispose();
            _downloadsWatcher = null;
            if (!on) { status.Text = "Not watching."; return; }
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dir)) { status.Text = "No Downloads folder at " + dir; return; }
            var w = new FileSystemWatcher(dir, "*.fbx") { IncludeSubdirectories = false, EnableRaisingEvents = true };
            void Arrived(string path) => _ = Task.Run(async () =>
            {
                // wait until the browser finished writing it
                long last = -1;
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(500);
                    long len; try { len = new FileInfo(path).Length; } catch { continue; }
                    if (len > 0 && len == last) break;
                    last = len;
                }
                Dispatcher.UIThread.Post(() => _ = RunGuided(g, new List<string> { path }, null, status));
            });
            w.Created += (s, e) => Arrived(e.FullPath);
            w.Renamed += (s, e) => { if (e.FullPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) Arrived(e.FullPath); };
            _downloadsWatcher = w;
            status.Text = "Watching " + dir + " — new FBX files are added to the library automatically.";
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _downloadsWatcher?.Dispose();
            _downloadsWatcher = null;
        }
    }

    /// <summary>The user's API keys for keyed providers (poly.pizza, Freesound, Sketchfab).</summary>
    public sealed class StoreKeysDialog : Window
    {
        public static async Task Run(string focusProvider)
        {
            var d = new StoreKeysDialog(focusProvider);
            await LibraryUi.ShowModal(d);
            if (d._saved && StoreView.Current?.ProviderId != null) StoreView.Current.SelectProvider(StoreView.Current.ProviderId);
        }

        private readonly Dictionary<string, TextBox> _boxes = new Dictionary<string, TextBox>();
        private bool _saved;

        private StoreKeysDialog(string focus)
        {
            Title = "API Keys";
            Width = 540; Height = 740; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Spacing = 6 };
            stack.Children.Add(new TextBlock { Text = "API keys", FontSize = 16, FontWeight = FontWeight.SemiBold });
            stack.Children.Add(LibraryUi.Para("Some sources need your own free key. Keys are stored only on this machine (" + StoreKeys.FilePath + ") — the editor never ships a shared key."));
            foreach (var p in StoreProviders.Web.Where(x => x.Access == ProviderAccess.ApiKey))
            {
                var prov = p;
                stack.Children.Add(new TextBlock { Text = p.Name + " — " + p.KeyName, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var box = new TextBox { Text = StoreKeys.Get(p.Id) ?? "", PasswordChar = '•', Watermark = "paste your key" };
                _boxes[p.Id] = box;
                row.Children.Add(box);
                var get = Ui.Button("Get a Key", () => { try { _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(prov.KeyHelpUrl)); } catch { } });
                get.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(get, 1);
                row.Children.Add(get);
                stack.Children.Add(row);
                if (p.Id == focus) Opened += (s, e) => box.Focus();
            }
            stack.Children.Add(new TextBlock { Text = "Sound Studio", FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 0) });
            foreach (var (id, name, url) in new[]
            {
                ("elevenlabs", "ElevenLabs — API key (sound effects)", "https://elevenlabs.io/app/settings/api-keys"),
                ("fal", "fal.ai — API key (Stable Audio Open, CassetteAI)", "https://fal.ai/dashboard/keys"),
                ("stability", "Stability AI — API key (Stable Audio 2.5 / 3)", "https://platform.stability.ai/account/keys"),
                ("anthropic", "Anthropic — API key (Claude designs the prompts)", "https://console.anthropic.com/settings/keys"),
            })
            {
                string link = url;
                stack.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var box = new TextBox { Text = StoreKeys.Get(id) ?? "", PasswordChar = '•', Watermark = "paste your key" };
                _boxes[id] = box;
                row.Children.Add(box);
                var get = Ui.Button("Get a Key", () => { try { _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(link)); } catch { } });
                get.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(get, 1);
                row.Children.Add(get);
                stack.Children.Add(row);
            }
            var cancel = Ui.Button("Cancel", Close, null, null, 84);
            var save = Ui.Button("Save", () => { foreach (var kv in _boxes) StoreKeys.Set(kv.Key, kv.Value.Text); _saved = true; Close(); }, null, "accent", 90);
            stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { cancel, save } });
            Content = new ScrollViewer { Content = stack };
        }
    }
}
