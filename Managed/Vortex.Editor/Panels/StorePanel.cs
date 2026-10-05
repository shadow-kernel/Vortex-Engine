using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VortexEditor.Controls;
using VortexEditor.Panels.AssetBrowser;
using VortexEditor.Shell;
using VortexEditor.Shell.Material;

namespace VortexEditor.Panels
{
    /// <summary>
    /// The Asset Store tab of the bottom dock, next to Project, Library and Console: free asset sources (Poly Haven,
    /// ambientCG, Kenney, poly.pizza, Freesound, Sketchfab), the guided Mixamo / Sonniss flows and the Sound Studio.
    /// Downloads land in the asset library and, on request, in the open project. The header carries the search box and
    /// the tile size; providers, filters, results and the details pane are the <see cref="StoreView"/>. Nothing is
    /// requested from the internet until the tab is opened for the first time.
    /// </summary>
    public sealed class StorePanel : UserControl
    {
        public static StorePanel Current { get; private set; }

        public StoreView View { get; }

        private readonly TextBox _search = new TextBox { Classes = { "search" }, Watermark = "Search", Width = 220, VerticalAlignment = VerticalAlignment.Center };
        private readonly Slider _size = new Slider { Width = 70, Minimum = 56, Maximum = 176, VerticalAlignment = VerticalAlignment.Center };
        private DispatcherTimer _searchTimer;

        public StorePanel()
        {
            Current = this;
            View = new StoreView();

            // icon + name, then the subtitle, which gives way (ellipsis) when the controls on the right need the room
            var title = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
            title.Children.Add(new VxIcon { Icon = "World", Width = 14, Height = 14, Foreground = EditorKit.Brush("VxAccentBrush"), VerticalAlignment = VerticalAlignment.Center });
            var titleName = new TextBlock { Text = "Asset Store", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(titleName, 1);
            title.Children.Add(titleName);
            var titleSub = new TextBlock { Text = "· free assets into your library and project", Classes = { "tertiary" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(titleSub, 2);
            title.Children.Add(titleSub);

            ToolTip.SetTip(_size, "Tile size");
            _size.Value = Math.Clamp(AssetPanelSettings.Current.StoreTileSize, _size.Minimum, _size.Maximum);
            View.SetTileSize(_size.Value);
            _size.PropertyChanged += (s, e) =>
            {
                if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
                View.SetTileSize(_size.Value);
                AssetPanelSettings.Current.StoreTileSize = Math.Round(_size.Value);
                AssetPanelSettings.SaveSoon();
            };
            _search.TextChanged += (s, e) => ScheduleSearch();
            _search.AddHandler(KeyDownEvent, (s, e) =>
            {
                if (e.Key == Key.Enter) { ApplySearch(); e.Handled = true; }
                else if (e.Key == Key.Escape && !string.IsNullOrEmpty(_search.Text)) { _search.Text = ""; ApplySearch(); e.Handled = true; }
            }, RoutingStrategies.Tunnel);
            View.ProviderChanged += () =>
            {
                _search.Watermark = View.SearchHint;
                _search.IsEnabled = View.WebProvider != null;   // the guided pages and the Sound Studio have nothing to search
            };

            var library = new Button { Classes = { "ghost" }, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { new VxIcon { Icon = "Library" }, new TextBlock { Text = "Library" } } } };
            ToolTip.SetTip(library, "Show the asset library (everything you downloaded is there)");
            library.Click += (s, e) => EditorCommands.ShowLibrary();

            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            right.Children.Add(_size);
            right.Children.Add(_search);
            right.Children.Add(library);
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
        }

        /// <summary>The tab came to the front: the first time, open the first source (the first request goes out now).</summary>
        public void OnShown() => View.OnShown();

        /// <summary>Search the current source (menus, tests).</summary>
        public void SetSearch(string text)
        {
            _searchTimer?.Stop();
            _search.Text = text ?? "";
            View.SetSearch(_search.Text);
        }

        public void FocusSearch() { _search.Focus(); _search.SelectAll(); }

        private void ScheduleSearch()
        {
            if (_searchTimer == null)
            {
                _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                _searchTimer.Tick += (s, e) => ApplySearch();
            }
            _searchTimer.Stop(); _searchTimer.Start();
        }

        private void ApplySearch()
        {
            _searchTimer?.Stop();
            View.SetSearch(_search.Text);
        }
    }
}
