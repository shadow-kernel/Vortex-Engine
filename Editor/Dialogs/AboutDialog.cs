using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Editor.Dialogs
{
    /// <summary>
    /// The "About Vortex Engine" dialog — logo, live version (EngineInfo), engine blurb, website/GitHub/
    /// release links, system info (GPU), license note, and a manual "Check for Updates" button that runs
    /// the same update flow as the startup check (handy for verifying updates end-to-end).
    /// </summary>
    public sealed class AboutDialog : Window
    {
        private TextBlock _updateStatus;
        private Button _updateBtn;

        private static Brush B(Color c) => new SolidColorBrush(c);

        public static void Open(Window owner)
        {
            var dlg = new AboutDialog();
            if (owner != null && owner.IsLoaded) dlg.Owner = owner;
            dlg.ShowDialog();
        }

        private AboutDialog()
        {
            Title = "About Vortex Engine";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = B(DialogStyles.BackgroundColor);
            Foreground = DialogStyles.TextBrush;
            ShowInTaskbar = false;

            Content = Build();
        }

        private UIElement Build()
        {
            var root = new StackPanel { Margin = new Thickness(28, 26, 28, 22) };

            // Logo (app resource; skipped silently if the pack URI ever fails)
            try
            {
                var logo = new Image
                {
                    Source = new BitmapImage(new Uri("pack://application:,,,/Assets/Images/Logo.png")),
                    Width = 84, Height = 84,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
                root.Children.Add(logo);
            }
            catch { }

            root.Children.Add(new TextBlock
            {
                Text = "Vortex Engine",
                FontSize = 24, FontWeight = FontWeights.Bold,
                Foreground = DialogStyles.TextBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // Version chip
            var chip = new Border
            {
                Background = B(DialogStyles.PanelColor),
                BorderBrush = DialogStyles.BorderBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 4, 12, 5),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 14)
            };
            chip.Child = new TextBlock
            {
                Text = "Version " + Editor.Core.EngineInfo.VersionString,
                FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Foreground = DialogStyles.AccentBrush
            };
            root.Children.Add(chip);

            root.Children.Add(new TextBlock
            {
                Text = "A modern, lightweight game engine — native DirectX 12 core, clean WPF editor. " +
                       "Build worlds, import anything, press Play. Free and open source (MIT), " +
                       "free to use for anything including commercial games.",
                FontSize = 12, Foreground = DialogStyles.TextSecondaryBrush,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, LineHeight = 18,
                Margin = new Thickness(8, 0, 8, 16)
            });

            // Links row: Website · GitHub · Releases
            var links = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 18) };
            links.Children.Add(Link("engine.vortexstudio.dev", "https://engine.vortexstudio.dev"));
            links.Children.Add(Dot());
            links.Children.Add(Link("GitHub", "https://github.com/" + Editor.Core.EngineInfo.RepoOwner + "/" + Editor.Core.EngineInfo.RepoName));
            links.Children.Add(Dot());
            links.Children.Add(Link("Release Notes", "https://github.com/" + Editor.Core.EngineInfo.RepoOwner + "/" + Editor.Core.EngineInfo.RepoName + "/releases"));
            root.Children.Add(links);

            // System info box
            string gpu = "";
            try { gpu = DllWrapper.VortexAPI.GpuName(); } catch { }
            var sys = new Border
            {
                Background = B(DialogStyles.PanelColor),
                BorderBrush = DialogStyles.BorderBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 16)
            };
            var sysText = new StackPanel();
            sysText.Children.Add(SysRow("Renderer", "DirectX 12" + (string.IsNullOrEmpty(gpu) ? "" : "  ·  " + gpu)));
            sysText.Children.Add(SysRow("Editor", ".NET Framework 4.8 · WPF"));
            sysText.Children.Add(SysRow("License", "MIT — © " + DateTime.Now.Year + " Vortex Engine Team"));
            sys.Child = sysText;
            root.Children.Add(sys);

            // Update check row
            _updateStatus = new TextBlock
            {
                Text = "",
                FontSize = 11.5, Foreground = DialogStyles.TextSecondaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
                Visibility = Visibility.Collapsed
            };
            root.Children.Add(_updateStatus);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            _updateBtn = DialogStyles.CreateButton("Check for Updates", 150, false);
            _updateBtn.Margin = new Thickness(0, 0, 8, 0);
            _updateBtn.Click += async (s, e) => await CheckForUpdates();
            actions.Children.Add(_updateBtn);

            var close = DialogStyles.CreateButton("Close", 90, true);
            close.Click += (s, e) => Close();
            actions.Children.Add(close);
            root.Children.Add(actions);

            return root;
        }

        private async System.Threading.Tasks.Task CheckForUpdates()
        {
            _updateBtn.IsEnabled = false;
            _updateStatus.Visibility = Visibility.Visible;
            _updateStatus.Text = "Checking for updates…";
            try
            {
                if (!Core.Services.Update.UpdateService.IsInstalledBuild())
                {
                    _updateStatus.Text = "Updates apply to installed builds only (this is a portable/dev run).";
                    return;
                }
                var info = await Core.Services.Update.UpdateService.CheckAsync();
                if (info == null)
                {
                    _updateStatus.Text = "You're up to date — Vortex " + Core.EngineInfo.VersionString + " is the latest version. ✓";
                    return;
                }
                _updateStatus.Text = "Update found: " + info.Tag;
                var dlg = new UpdateDialog(info) { Owner = this };
                dlg.ShowDialog();
                _updateStatus.Text = "";
                _updateStatus.Visibility = Visibility.Collapsed;
            }
            catch
            {
                _updateStatus.Text = "Update check failed — please try again later.";
            }
            finally
            {
                _updateBtn.IsEnabled = true;
            }
        }

        private UIElement Link(string text, string url)
        {
            var tb = new TextBlock { FontSize = 12.5, Cursor = Cursors.Hand };
            var run = new Run(text) { Foreground = DialogStyles.AccentBrush };
            tb.Inlines.Add(new Underline(run));
            tb.MouseLeftButtonUp += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { }
            };
            tb.ToolTip = url;
            return tb;
        }

        private UIElement Dot() => new TextBlock
        {
            Text = "  ·  ", FontSize = 12.5,
            Foreground = DialogStyles.TextSecondaryBrush
        };

        private UIElement SysRow(string label, string value)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = DialogStyles.TextSecondaryBrush });
            var v = new TextBlock { Text = value, FontSize = 11.5, Foreground = DialogStyles.TextBrush, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(v, 1);
            row.Children.Add(v);
            return row;
        }
    }
}
