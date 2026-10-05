using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Assets.Library;
using Editor.Core.Services;
using VortexEditor.Controls;
using VortexEditor.Panels.AssetBrowser;
using VortexEditor.Shell;
using VortexEditor.Shell.Material;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Panels
{
    /// <summary>Tile size and sort order of the Library and Store tabs (<c>&lt;VortexAppData&gt;/asset-panels.json</c>).</summary>
    public sealed class AssetPanelSettings
    {
        public double LibraryTileSize { get; set; } = 96;
        public double StoreTileSize { get; set; } = 96;
        public string LibrarySort { get; set; } = "Name";
        public bool LibraryDescending { get; set; }

        private static string FilePath => Path.Combine(EditorPaths.VortexAppData, "asset-panels.json");
        private static AssetPanelSettings _current;

        public static AssetPanelSettings Current
        {
            get
            {
                if (_current != null) return _current;
                try { if (File.Exists(FilePath)) _current = JsonSerializer.Deserialize<AssetPanelSettings>(File.ReadAllText(FilePath)); } catch { }
                return _current ?? (_current = new AssetPanelSettings());
            }
        }

        private static DispatcherTimer _saveTimer;

        /// <summary>Save a moment later (slider drags change the size many times a second).</summary>
        public static void SaveSoon()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _saveTimer.Tick += (s, e) =>
                {
                    _saveTimer.Stop();
                    try { Directory.CreateDirectory(EditorPaths.VortexAppData); File.WriteAllText(FilePath, JsonSerializer.Serialize(Current)); } catch { }
                };
            }
            _saveTimer.Stop(); _saveTimer.Start();
        }
    }

    /// <summary>
    /// The Library tab of the bottom dock, next to Project and Console: the machine-wide asset library — every asset
    /// imported on this machine, from every project, stored once. Its header carries the search box, the tag filter,
    /// the sort order and the tile size; the grid, type chips, details pane and tools are the <see cref="LibraryView"/>.
    /// Shift- or ⌘-double-click previews an asset straight from the library; a plain double-click adds it to the project.
    /// </summary>
    public sealed class LibraryPanel : UserControl
    {
        public static LibraryPanel Current { get; private set; }

        public LibraryView View { get; }

        private readonly TextBox _search = new TextBox { Classes = { "search" }, Watermark = "Search the library", Width = 200, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _tagButton = new Button { Classes = { "ghost" } };
        private readonly TextBlock _tagLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false };
        private readonly VxIcon _tagIcon = new VxIcon { Icon = "Tag", Width = 13, Height = 13 };
        private readonly Button _sortButton = new Button { Classes = { "ghost" } };
        private readonly TextBlock _sortLabel = new TextBlock { Text = "Name", VerticalAlignment = VerticalAlignment.Center };
        private readonly VxIcon _sortArrow = new VxIcon { Icon = "ChevronDown", Width = 11, Height = 11 };
        private readonly Slider _size = new Slider { Width = 70, Minimum = 56, Maximum = 176, VerticalAlignment = VerticalAlignment.Center };
        private DispatcherTimer _searchTimer;
        private bool _shownOnce;

        public LibraryPanel()
        {
            Current = this;
            View = new LibraryView();
            var settings = AssetPanelSettings.Current;
            if (Enum.TryParse<LibrarySort>(settings.LibrarySort, out var sort)) View.SetSort(sort, settings.LibraryDescending);

            // ---- header: title | tags, sort, size, search, add files, refresh
            // icon + name, then the subtitle, which gives way (ellipsis) when the controls on the right need the room
            var title = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
            title.Children.Add(new VxIcon { Icon = "Library", Width = 14, Height = 14, Foreground = EditorKit.Brush("VxAccentBrush"), VerticalAlignment = VerticalAlignment.Center });
            var titleName = new TextBlock { Text = "Asset Library", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(titleName, 1);
            title.Children.Add(titleName);
            var titleSub = new TextBlock { Text = "· every asset on this machine, from all projects", Classes = { "tertiary" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(titleSub, 2);
            title.Children.Add(titleSub);

            _tagButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _tagIcon, _tagLabel } };
            _tagButton.Click += (s, e) => View.ShowTagMenu(_tagButton);
            _sortButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _sortLabel, _sortArrow } };
            ToolTip.SetTip(_sortButton, "Sort order");
            _sortButton.Click += (s, e) => ShowSortMenu();
            ToolTip.SetTip(_size, "Tile size");
            _size.Value = Math.Clamp(settings.LibraryTileSize, _size.Minimum, _size.Maximum);
            View.SetTileSize(_size.Value);
            _size.PropertyChanged += (s, e) =>
            {
                if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
                View.SetTileSize(_size.Value);
                AssetPanelSettings.Current.LibraryTileSize = Math.Round(_size.Value);
                AssetPanelSettings.SaveSoon();
            };
            _search.TextChanged += (s, e) => ScheduleSearch();
            _search.AddHandler(KeyDownEvent, (s, e) =>
            {
                if (e.Key == Key.Enter) { ApplySearch(); e.Handled = true; }
                else if (e.Key == Key.Escape && !string.IsNullOrEmpty(_search.Text)) { _search.Text = ""; ApplySearch(); e.Handled = true; }
                else if (e.Key == Key.Down) { View.Focus(); e.Handled = true; }
            }, RoutingStrategies.Tunnel);

            var add = new Button { Classes = { "ghost" }, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { new VxIcon { Icon = "Plus" }, new TextBlock { Text = "Add Files" } } } };
            ToolTip.SetTip(add, "Add files to the library (no project needed) — or drop them on the grid");
            add.Click += async (s, e) => await View.PickFilesToLibrary();
            var store = new Button { Classes = { "ghost" }, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { new VxIcon { Icon = "World" }, new TextBlock { Text = "Asset Store" } } } };
            ToolTip.SetTip(store, "Get free assets (Poly Haven, ambientCG, Kenney, Sketchfab, Freesound …) into the library");
            store.Click += (s, e) => EditorCommands.ShowStore();
            var refresh = EditorKit.IconButton("Refresh", "Refresh", () => View.Refresh(), 14);

            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            right.Children.Add(_tagButton);
            right.Children.Add(_sortButton);
            right.Children.Add(_size);
            right.Children.Add(_search);
            right.Children.Add(add);
            right.Children.Add(store);
            right.Children.Add(refresh);
            var header = new DockPanel { Height = 36 };
            DockPanel.SetDock(right, Dock.Right);
            header.Children.Add(right);
            header.Children.Add(title);
            var headerBorder = new Border { Classes = { "panelheader" }, Child = header };
            DockPanel.SetDock(headerBorder, Dock.Top);

            var root = new DockPanel();
            root.Children.Add(headerBorder);
            root.Children.Add(View);
            Content = root;

            View.SearchApplied += text =>
            {
                _searchTimer?.Stop();
                if ((_search.Text ?? "") != (text ?? "")) _search.Text = text ?? "";
            };
            View.FiltersChanged += UpdateTagButton;
            UpdateTagButton();
            UpdateSortLabel();
        }

        /// <summary>The tab came to the front: refresh, and on the first time offer to index existing projects.</summary>
        public void OnShown()
        {
            if (!_shownOnce) { _shownOnce = true; View.OnShown(); return; }
            View.Refresh();
        }

        /// <summary>Search the library (menus, the Store's "Show in the Library", tests).</summary>
        public void SetSearch(string text)
        {
            _searchTimer?.Stop();
            _search.Text = text ?? "";
            View.SetSearch(_search.Text);
        }

        public string Search => _search.Text ?? "";

        public void FocusSearch() { _search.Focus(); _search.SelectAll(); }

        private void ScheduleSearch()
        {
            if (_searchTimer == null)
            {
                _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
                _searchTimer.Tick += (s, e) => ApplySearch();
            }
            _searchTimer.Stop(); _searchTimer.Start();
        }

        private void ApplySearch()
        {
            _searchTimer?.Stop();
            View.SetSearch(_search.Text);
        }

        private void UpdateTagButton()
        {
            var tags = View.TagFilter;
            _tagLabel.Text = tags.Count == 0 ? "" : string.Join(" + ", tags);
            _tagLabel.IsVisible = tags.Count > 0;
            if (tags.Count > 0) _tagIcon.Foreground = EditorKit.Brush("VxAccentBrush"); else _tagIcon.ClearValue(VxIcon.ForegroundProperty);
            ToolTip.SetTip(_tagButton, tags.Count > 0 ? "Library assets tagged " + string.Join(" and ", tags) + " — click to change" : "Show only library assets with tags");
        }

        private void ShowSortMenu()
        {
            var m = new MenuFlyout();
            foreach (var (key, label) in new[] { (LibrarySort.Name, "Name"), (LibrarySort.Type, "Type"), (LibrarySort.Added, "Date Added"), (LibrarySort.Size, "Size") })
            {
                var k = key;
                m.Items.Add(Check(label, View.Sort == k, () => SetSort(k, View.Descending)));
            }
            m.Items.Add(new Separator());
            m.Items.Add(Check("Ascending", !View.Descending, () => SetSort(View.Sort, false)));
            m.Items.Add(Check("Descending", View.Descending, () => SetSort(View.Sort, true)));
            m.ShowAt(_sortButton);
        }

        private void SetSort(LibrarySort sort, bool descending)
        {
            View.SetSort(sort, descending);
            AssetPanelSettings.Current.LibrarySort = sort.ToString();
            AssetPanelSettings.Current.LibraryDescending = descending;
            AssetPanelSettings.SaveSoon();
            UpdateSortLabel();
        }

        private void UpdateSortLabel()
        {
            _sortLabel.Text = View.Sort == LibrarySort.Added ? "Date" : View.Sort.ToString();
            _sortArrow.RenderTransform = View.Descending ? new RotateTransform(180) : null;
        }

        private static MenuItem Check(string header, bool on, Action click)
        {
            var mi = new MenuItem { Header = header, Icon = on ? new VxIcon { Icon = "Check", Width = 12, Height = 12 } : null };
            mi.Click += (s, e) => click();
            return mi;
        }
    }
}
