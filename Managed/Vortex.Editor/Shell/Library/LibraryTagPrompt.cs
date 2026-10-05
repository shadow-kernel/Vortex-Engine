using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VortexEditor.Controls;
using VortexEditor.Shell.Material;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Shell.Library
{
    /// <summary>
    /// Tag input for the library (#62): the chosen tags as removable chips, a field that autocompletes from the
    /// library's existing tags (most used first) and the most used tags as one-click suggestions.
    /// </summary>
    public sealed class LibraryTagPrompt : Window
    {
        /// <summary>Ask for tags; null when cancelled.</summary>
        public static async Task<List<string>> Ask(string title, string message, string ok, IEnumerable<string> initial = null)
        {
            var d = new LibraryTagPrompt(title, message, ok, initial);
            await LibraryUi.ShowModal(d);
            return d._ok ? d._tags.ToList() : null;
        }

        private readonly List<string> _tags = new List<string>();
        private readonly WrapPanel _chips = new WrapPanel();
        private readonly AutoCompleteBox _input = new AutoCompleteBox { Watermark = "Tag name", FilterMode = AutoCompleteFilterMode.ContainsOrdinal, MinimumPrefixLength = 0 };
        private bool _ok;

        private LibraryTagPrompt(string title, string message, string ok, IEnumerable<string> initial)
        {
            Title = title;
            Width = 460; SizeToContent = SizeToContent.Height; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            foreach (var t in initial ?? Enumerable.Empty<string>()) Add(t, rebuild: false);

            var counts = LibraryUi.Lib.TagCounts();
            _input.ItemsSource = counts.Select(c => c.tag).ToList();
            _input.KeyDown += (s, e) => { if (e.Key == Key.Enter) { AddFromInput(); e.Handled = true; } };

            var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Spacing = 6 };
            stack.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold });
            if (!string.IsNullOrEmpty(message)) stack.Children.Add(LibraryUi.Para(message));
            stack.Children.Add(new Border { Background = EditorKit.Brush("VxFieldBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 8, 2, 2), MinHeight = 40, Margin = new Thickness(0, 6, 0, 0), Child = _chips });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(_input);
            var add = Ui.Button("Add", AddFromInput);
            add.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(add, 1);
            row.Children.Add(add);
            stack.Children.Add(row);
            if (counts.Count > 0)
            {
                stack.Children.Add(LibraryUi.Small("Most used"));
                var quick = new WrapPanel();
                foreach (var (tag, count) in counts.Take(14))
                {
                    var t = tag;
                    var b = new Button { Content = t + " · " + count, Classes = { "ghost" }, FontSize = 11, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(6, 2) };
                    b.Click += (s, e) => Add(t, rebuild: true);
                    quick.Children.Add(b);
                }
                stack.Children.Add(quick);
            }
            var cancel = Ui.Button("Cancel", Close, null, null, 84);
            var okb = Ui.Button(ok, () => { AddFromInput(); _ok = true; Close(); }, null, "accent", 90);
            okb.IsDefault = false;
            stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { cancel, okb } });
            Content = stack;
            Rebuild();
            Opened += (s, e) => _input.Focus();
        }

        /// <summary>The tags chosen so far (tests).</summary>
        public IReadOnlyList<string> Tags => _tags;

        private void AddFromInput()
        {
            foreach (var t in Panels.AssetBrowser.LibraryView.SplitTags(_input.Text)) Add(t, rebuild: false);
            _input.Text = "";
            Rebuild();
        }

        private void Add(string tag, bool rebuild)
        {
            tag = (tag ?? "").Trim();
            if (tag.Length == 0 || _tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return;
            _tags.Add(tag);
            if (rebuild) Rebuild();
        }

        private void Rebuild()
        {
            _chips.Children.Clear();
            if (_tags.Count == 0) { _chips.Children.Add(new TextBlock { Text = "No tags yet", Classes = { "small", "tertiary" }, Margin = new Thickness(2, 2, 0, 6) }); return; }
            foreach (var t in _tags)
            {
                var tag = t;
                var x = new Button { Content = new VxIcon { Icon = "Close", Width = 9, Height = 9, Foreground = Brushes.White }, Classes = { "ghost" }, Padding = new Thickness(2) };
                x.Click += (s, e) => { _tags.Remove(tag); Rebuild(); };
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { new TextBlock { Text = tag, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }, x } };
                _chips.Children.Add(new Border { Background = EditorKit.Brush("VxAccentBrush"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 3, 2), Margin = new Thickness(0, 0, 5, 5), Child = sp });
            }
        }
    }
}
