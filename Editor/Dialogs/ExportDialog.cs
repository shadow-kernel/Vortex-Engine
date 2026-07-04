using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Editor.Dialogs
{
    /// <summary>
    /// Export options dialog — replaces the old Yes/No/Cancel MessageBox with a real form: choose Release vs Debug
    /// and toggle "open folder" / "run after export". Result is read from IsDebug / OpenFolder / RunAfter after a
    /// true ShowDialog().
    /// </summary>
    public sealed class ExportDialog : Window
    {
        public bool IsDebug { get; private set; }
        public bool OpenFolder { get; private set; } = true;
        public bool RunAfter { get; private set; }

        private readonly RadioButton _release;
        private readonly RadioButton _debug;
        private readonly CheckBox _open;
        private readonly CheckBox _run;

        private static Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);

        public ExportDialog(string projectName, bool defaultRun = false)
        {
            Title = "Export Game";
            Width = 560; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.NoResize;
            Background = B("#1B1B1E"); Foreground = B("#E9E9ED");
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

            var root = new StackPanel { Margin = new Thickness(22) };

            root.Children.Add(new TextBlock { Text = "Export “" + (projectName ?? "Game") + "”", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            root.Children.Add(new TextBlock { Text = "Choose the build type.", Foreground = B("#9A9AA1"), FontSize = 12, Margin = new Thickness(0, 0, 0, 16) });

            // Default to DEBUG: it's the fast test/iterate build. Release (ship build) is an explicit opt-in.
            _release = MakeChoice("Release", "Packed + obfuscated, no source, hot-reload OFF. The build to ship.", false);
            _debug = MakeChoice("Debug", "References your ORIGINAL project on disk — edit scripts/shaders there and they hot-reload live in the build. For testing; don’t distribute.", true);
            root.Children.Add(_release);
            root.Children.Add(_debug);

            _open = new CheckBox { Content = "Open the output folder when done", IsChecked = true, Foreground = B("#D6D6DB"), Margin = new Thickness(2, 14, 0, 0) };
            _run = new CheckBox { Content = "Run the game after export", IsChecked = defaultRun, Foreground = B("#D6D6DB"), Margin = new Thickness(2, 8, 0, 0) };
            root.Children.Add(_open);
            root.Children.Add(_run);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            var cancel = MakeButton("Cancel", false);
            var export = MakeButton("Export", true);
            cancel.Click += (s, e) => { DialogResult = false; };
            export.Click += (s, e) =>
            {
                IsDebug = _debug.IsChecked == true;
                OpenFolder = _open.IsChecked == true;
                RunAfter = _run.IsChecked == true;
                DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(export);
            root.Children.Add(buttons);

            Content = root;
        }

        private RadioButton MakeChoice(string title, string desc, bool isChecked)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13 });
            panel.Children.Add(new TextBlock { Text = desc, Foreground = B("#9A9AA1"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            return new RadioButton
            {
                GroupName = "buildType",
                IsChecked = isChecked,
                Content = panel,
                Foreground = B("#E9E9ED"),
                Padding = new Thickness(8, 0, 0, 0),
                Margin = new Thickness(0, 0, 0, 12),
                BorderBrush = B("#3A3A42")
            };
        }

        private Button MakeButton(string text, bool primary)
        {
            return new Button
            {
                Content = text,
                MinWidth = 96,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = primary ? Brushes.White : B("#D6D6DB"),
                Background = primary ? B("#6C5CE7") : B("#2C2C32"),
                BorderBrush = primary ? B("#6C5CE7") : B("#3A3A42"),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 13
            };
        }
    }
}
