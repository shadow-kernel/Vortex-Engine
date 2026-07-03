using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using Editor.Core.Services;

namespace Editor.Editors.WorldEditor.Components.Console
{
    /// <summary>
    /// The editor's game-log Console (bottom panel, next to the Project/Explorer tab). Binds to
    /// <see cref="ConsoleService"/> — every line the running game writes shows here: script Vortex.Debug calls, script
    /// errors, and (while playing) plain Console.WriteLine. Toolbar: Clear, auto-scroll, and Info/Warn/Error filters
    /// with live counts. Built in code (no XAML) so no non-SDK csproj Page registration is needed.
    /// </summary>
    public class ConsoleView : UserControl
    {
        private readonly ListBox _list;
        private readonly ICollectionView _view;
        private readonly ToggleButton _autoScroll;
        private readonly FilterChip _info, _warn, _error;
        private bool _showInfo = true, _showWarn = true, _showError = true;

        public ConsoleView()
        {
            Background = Brush("#FF1A1A1C");

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // toolbar
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // ---- toolbar ----
            var bar = new Border
            {
                Background = Brush("#FF212124"),
                BorderBrush = Brush("#FF303036"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 5, 8, 5)
            };
            var barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "CONSOLE",
                Foreground = Brush("#FF7C7C86"),
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            };
            barGrid.Children.Add(title);

            // filter chips (centre)
            var chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chips, 1);
            _info = new FilterChip("Info", "#FFC8C8CE", () => { _showInfo = !_showInfo; _view.Refresh(); });
            _warn = new FilterChip("Warnings", "#FFE2C044", () => { _showWarn = !_showWarn; _view.Refresh(); });
            _error = new FilterChip("Errors", "#FFE06C6C", () => { _showError = !_showError; _view.Refresh(); });
            chips.Children.Add(_info); chips.Children.Add(_warn); chips.Children.Add(_error);
            barGrid.Children.Add(chips);

            // right: auto-scroll + clear
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(right, 2);
            _autoScroll = new ToggleButton { Content = "Auto-scroll", IsChecked = true };
            StyleToolButton(_autoScroll);
            var clear = new Button { Content = "Clear" };
            StyleToolButton(clear);
            clear.Click += (s, e) => ConsoleService.Instance.Clear();
            right.Children.Add(_autoScroll);
            right.Children.Add(clear);
            barGrid.Children.Add(right);

            bar.Child = barGrid;
            Grid.SetRow(bar, 0);
            root.Children.Add(bar);

            // ---- log list ----
            _list = new ListBox
            {
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 4, 0, 6),
                ItemContainerStyle = RowStyle(),
                ItemTemplate = RowTemplate()
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            _view = CollectionViewSource.GetDefaultView(ConsoleService.Instance.Entries);
            _view.Filter = FilterPredicate;
            _list.ItemsSource = _view;
            Grid.SetRow(_list, 1);
            root.Children.Add(_list);

            Content = root;

            Loaded += (s, e) =>
            {
                ConsoleService.Instance.AttachPlayMode();
                ConsoleService.Instance.GreetOnce();
                // -= before += : AvalonDock can raise Loaded again without an intervening Unloaded (tab activation),
                // and a double-subscribed handler would multiply the per-line work.
                ConsoleService.Instance.EntryAdded -= OnEntryAdded;
                ConsoleService.Instance.EntryAdded += OnEntryAdded;
                RefreshCounts();
                ScrollToEnd();
            };
            Unloaded += (s, e) => { ConsoleService.Instance.EntryAdded -= OnEntryAdded; };
        }

        private bool FilterPredicate(object o)
        {
            if (!(o is LogEntry le)) return true;
            switch (le.Level)
            {
                case LogLevel.Warning: return _showWarn;
                case LogLevel.Error: return _showError;
                case LogLevel.Info: return _showInfo;
                default: return true;   // System lines always show
            }
        }

        private void OnEntryAdded()
        {
            RefreshCounts();
            if (_autoScroll.IsChecked == true) ScrollToEnd();
        }

        private void ScrollToEnd()
        {
            try
            {
                if (_list.Items.Count > 0)
                    _list.ScrollIntoView(_list.Items[_list.Items.Count - 1]);
            }
            catch { }
        }

        private void RefreshCounts()
        {
            // O(1): read the running counters the service maintains, instead of re-scanning all entries per line.
            var svc = ConsoleService.Instance;
            _info.SetCount(svc.InfoCount); _warn.SetCount(svc.WarnCount); _error.SetCount(svc.ErrorCount);
        }

        // ---- styling helpers ----
        private static Brush Brush(string hex)
        {
            var b = (Brush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        private static void StyleToolButton(ButtonBase btn)
        {
            btn.Margin = new Thickness(6, 0, 0, 0);
            btn.Padding = new Thickness(10, 3, 10, 3);
            btn.FontSize = 11.5;
            btn.Foreground = Brush("#FFC8C8CE");
            btn.Background = Brush("#FF2A2A30");
            btn.BorderThickness = new Thickness(0);
            btn.Cursor = System.Windows.Input.Cursors.Hand;
            var tpl = new ControlTemplate(btn is ToggleButton ? typeof(ToggleButton) : typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bd.SetValue(Border.PaddingProperty, new Thickness(10, 3, 10, 3));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            btn.Template = tpl;
        }

        /// <summary>Minimal row container: no selection chrome, subtle hover, tight rows.</summary>
        private static Style RowStyle()
        {
            var st = new Style(typeof(ListBoxItem));
            st.Setters.Add(new Setter(BackgroundProperty, System.Windows.Media.Brushes.Transparent));
            st.Setters.Add(new Setter(PaddingProperty, new Thickness(0, 1, 0, 1)));
            st.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
            var tpl = new ControlTemplate(typeof(ListBoxItem));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            bd.Name = "Bd";
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(BackgroundProperty, Brush("#14FFFFFF"), "Bd"));
            tpl.Triggers.Add(hover);
            st.Setters.Add(new Setter(TemplateProperty, tpl));
            return st;
        }

        private static DataTemplate RowTemplate()
        {
            const string xaml =
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                "  <Grid Margin='10,0.5,12,0.5'>" +
                "    <Grid.ColumnDefinitions>" +
                "      <ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/>" +
                "    </Grid.ColumnDefinitions>" +
                "    <TextBlock Grid.Column='0' Text='{Binding Time}' Foreground='#55FFFFFF' FontFamily='Cascadia Mono, Consolas, Courier New' FontSize='11' Margin='0,1,10,0' VerticalAlignment='Top'/>" +
                "    <TextBlock Grid.Column='1' Text='{Binding LevelTag}' Foreground='{Binding Color}' FontSize='10' FontWeight='SemiBold' Margin='0,1.5,10,0' VerticalAlignment='Top'/>" +
                "    <TextBlock Grid.Column='2' Text='{Binding Message}' Foreground='{Binding Color}' FontFamily='Cascadia Mono, Consolas, Courier New' FontSize='12' TextWrapping='Wrap'/>" +
                "  </Grid>" +
                "</DataTemplate>";
            return (DataTemplate)XamlReader.Parse(xaml);
        }

        /// <summary>A pill toggle with a coloured dot + count that filters one level.</summary>
        private sealed class FilterChip : ToggleButton
        {
            private readonly TextBlock _count;
            public FilterChip(string label, string colorHex, Action onToggle)
            {
                IsChecked = true;
                Cursor = System.Windows.Input.Cursors.Hand;
                Margin = new Thickness(0, 0, 6, 0);
                Background = System.Windows.Media.Brushes.Transparent;
                BorderThickness = new Thickness(0);
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Brush(colorHex), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                sp.Children.Add(new TextBlock { Text = label, Foreground = Brush("#FFB4B4BC"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
                _count = new TextBlock { Text = "0", Foreground = Brush("#FF7C7C86"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
                sp.Children.Add(_count);
                Content = sp;
                var tpl = new ControlTemplate(typeof(ToggleButton));
                var bd = new FrameworkElementFactory(typeof(Border));
                bd.Name = "Bd";
                bd.SetValue(Border.BackgroundProperty, Brush("#FF262629"));
                bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
                bd.SetValue(Border.PaddingProperty, new Thickness(9, 3, 9, 3));
                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                bd.AppendChild(cp);
                tpl.VisualTree = bd;
                var off = new Trigger { Property = IsCheckedProperty, Value = false };
                off.Setters.Add(new Setter(OpacityProperty, 0.4));
                tpl.Triggers.Add(off);
                Template = tpl;
                Checked += (s, e) => onToggle();
                Unchecked += (s, e) => onToggle();
            }
            public void SetCount(int n) { _count.Text = n.ToString(); }
        }
    }
}
