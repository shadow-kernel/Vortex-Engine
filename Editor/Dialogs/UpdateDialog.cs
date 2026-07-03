using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Editor.Core.Services.Update;

namespace Editor.Dialogs
{
    /// <summary>
    /// The auto-update dialog — cool dark Vortex design. Behaviour is gated by the semver bump:
    ///  • Patch  → no questions: shows "Installing update…" and auto-downloads + installs + restarts.
    ///  • Minor  → asks, with a "What's new" release-notes panel.
    ///  • Major  → asks, with a CRITICAL warning that older projects must be migrated first.
    /// Once the user accepts (or immediately for a patch) it downloads the installer with a progress bar and
    /// launches it silently; the app then closes and the installer relaunches the new version.
    /// </summary>
    public sealed class UpdateDialog : Window
    {
        private readonly UpdateInfo _info;
        private readonly bool _auto;
        private ProgressBar _bar;
        private TextBlock _status;
        private Button _primary, _secondary;
        private StackPanel _actions;

        private static Brush B(Color c) => new SolidColorBrush(c);

        public UpdateDialog(UpdateInfo info)
        {
            _info = info;
            _auto = info.Bump == BumpType.Patch;

            Title = "Vortex Engine — Update";
            Width = 580;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 720;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = B(DialogStyles.BackgroundColor);
            Foreground = DialogStyles.TextBrush;
            ShowInTaskbar = false;

            Content = Build();
            if (_auto) Loaded += (s, e) => StartDownload();
        }

        private UIElement Build()
        {
            var root = new StackPanel { Margin = new Thickness(24) };

            bool major = _info.Bump == BumpType.Major;
            string headline = _auto ? "Installing update" : (major ? "Major update available" : "Update available");
            var accent = major ? Color.FromRgb(0xF0, 0x8A, 0x3A) : DialogStyles.AccentColor; // amber for major

            // header: glyph + title
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            head.Children.Add(new TextBlock
            {
                Text = major ? "" : "", // warning / download glyph (Segoe MDL2)
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 20, Foreground = B(accent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
            });
            head.Children.Add(new TextBlock { Text = headline, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = DialogStyles.TextBrush, VerticalAlignment = VerticalAlignment.Center });
            root.Children.Add(head);

            root.Children.Add(new TextBlock
            {
                Text = "Vortex " + Editor.Core.EngineInfo.VersionString + "  →  " + _info.Latest.ToString(3),
                Foreground = DialogStyles.TextSecondaryBrush, FontSize = 12.5, Margin = new Thickness(30, 0, 0, 16)
            });

            if (major)
            {
                // critical warning box about project migration
                var warn = new Border
                {
                    Background = B(Color.FromRgb(0x2A, 0x20, 0x14)),
                    BorderBrush = B(accent), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 12, 12), Margin = new Thickness(0, 0, 0, 14)
                };
                warn.Child = new TextBlock
                {
                    Text = "This is a major version. Projects made with an older engine version may need to be MIGRATED before they open. " +
                           "Vortex will back up each project and update it automatically when you open it — but back up anything important first.",
                    Foreground = B(Color.FromRgb(0xF0, 0xD8, 0xB0)), FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 17
                };
                root.Children.Add(warn);
            }

            // "What's new" — the FULL changelogs of every release between the running and the latest version
            // (UpdateService aggregates them, newest first). Always shown, also while a patch auto-installs.
            root.Children.Add(new TextBlock { Text = "WHAT'S NEW", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = DialogStyles.TextSecondaryBrush, Margin = new Thickness(0, 0, 0, 6) });
            var notesBox = new Border
            {
                Background = B(DialogStyles.PanelColor), BorderBrush = DialogStyles.BorderBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 0, 16)
            };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 320 };
            scroll.Content = BuildNotesView(_info.Notes);
            notesBox.Child = scroll;
            root.Children.Add(notesBox);

            // progress (hidden until downloading)
            _status = new TextBlock { Text = _auto ? "Downloading…" : "", Foreground = DialogStyles.TextSecondaryBrush, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 6), Visibility = _auto ? Visibility.Visible : Visibility.Collapsed };
            root.Children.Add(_status);
            _bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Foreground = DialogStyles.AccentBrush, Background = B(DialogStyles.PanelColor), BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 14), Visibility = _auto ? Visibility.Visible : Visibility.Collapsed };
            root.Children.Add(_bar);

            // actions
            _actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            if (!_auto)
            {
                _secondary = DialogStyles.CreateButton("Later", 90, false);
                _secondary.Margin = new Thickness(0, 0, 8, 0);
                _secondary.Click += (s, e) => { DialogResult = false; Close(); };
                _actions.Children.Add(_secondary);

                _primary = DialogStyles.CreateButton("Update now", 130, true);
                _primary.Click += (s, e) => StartDownload();
                _actions.Children.Add(_primary);
            }
            root.Children.Add(_actions);
            return root;
        }

        /// <summary>
        /// Render the aggregated release notes as styled text (markdown-lite): '# vX.Y.Z' version headers in
        /// accent, '##'/'###' section headers bold, '*'/'-' items as bullets, bold markers and link syntax
        /// stripped so the GitHub-generated changelog reads cleanly.
        /// </summary>
        private UIElement BuildNotesView(string notes)
        {
            var panel = new StackPanel();
            if (string.IsNullOrWhiteSpace(notes))
            {
                panel.Children.Add(new TextBlock { Text = "See the release page for details.", Foreground = DialogStyles.TextSecondaryBrush, FontSize = 12 });
                return panel;
            }

            bool first = true;
            foreach (var raw in notes.Replace("\r", "").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Trim().Length == 0) continue;

                if (line.StartsWith("# "))          // version header ("# v2.4.1")
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = line.Substring(2).Trim(),
                        FontSize = 14.5, FontWeight = FontWeights.Bold,
                        Foreground = DialogStyles.AccentBrush,
                        Margin = new Thickness(0, first ? 0 : 14, 0, 4)
                    });
                }
                else if (line.StartsWith("### ") || line.StartsWith("## "))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = CleanMd(line.TrimStart('#', ' ')),
                        FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                        Foreground = DialogStyles.TextBrush,
                        Margin = new Thickness(0, 8, 0, 3)
                    });
                }
                else if (line.TrimStart().StartsWith("* ") || line.TrimStart().StartsWith("- "))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = "•  " + CleanMd(line.TrimStart(' ', '*', '-')),
                        FontSize = 12, Foreground = DialogStyles.TextBrush,
                        TextWrapping = TextWrapping.Wrap, LineHeight = 17,
                        Margin = new Thickness(8, 1, 0, 1)
                    });
                }
                else
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = CleanMd(line.Trim()),
                        FontSize = 12, Foreground = DialogStyles.TextSecondaryBrush,
                        TextWrapping = TextWrapping.Wrap, LineHeight = 17,
                        Margin = new Thickness(0, 1, 0, 1)
                    });
                }
                first = false;
            }
            return panel;
        }

        /// <summary>Strip markdown noise for plain-text display: **bold**, `code`, [text](url) -> text, bare PR links shortened.</summary>
        private static string CleanMd(string s)
        {
            s = s.Replace("**", "").Replace("`", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");   // [text](url) -> text
            s = System.Text.RegularExpressions.Regex.Replace(s, @"https://github\.com/\S+/(?:pull|compare|commit)/(\S+)", "($1)"); // shorten PR/compare links
            return s;
        }

        private async void StartDownload()
        {
            if (_primary != null) _primary.IsEnabled = false;
            if (_secondary != null) _secondary.IsEnabled = false;
            _status.Visibility = Visibility.Visible;
            _bar.Visibility = Visibility.Visible;
            _bar.IsIndeterminate = true; // until we get a real byte count (server may not send Content-Length)
            _status.Text = "Downloading " + (_info.SetupName ?? "update") + "…";

            var progress = new Progress<double>(p => { _bar.IsIndeterminate = false; _bar.Value = p; });
            string path = await UpdateService.DownloadAsync(_info, progress);

            if (string.IsNullOrEmpty(path))
            {
                _status.Text = "Download failed. Please try again later or update from the website.";
                if (_primary != null) _primary.IsEnabled = true;
                if (_secondary != null) _secondary.IsEnabled = true;
                return;
            }

            _bar.IsIndeterminate = false;
            _bar.Value = 1;
            _status.Text = "Installing… Vortex will restart.";
            // hand off to the installer (elevated, silent) — this shuts the app down.
            UpdateService.InstallAndRestart(path);

            // If we're STILL here, InstallAndRestart did NOT Environment.Exit — the installer relay failed to launch
            // (blocked powershell, temp not writable, …). The patch dialog has no buttons, and during the pre-startup
            // gate this modal blocks the whole boot; so report and auto-close so the app continues on the current
            // version instead of hanging on "Installing…" forever.
            _status.Text = "Could not start the installer. Continuing on the current version — you can update later from the website.";
            if (_primary != null) _primary.IsEnabled = true;
            if (_secondary != null) _secondary.IsEnabled = true;
            try
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t.Tick += (s, e) => { t.Stop(); try { Close(); } catch { } };
                t.Start();
            }
            catch { try { Close(); } catch { } }
        }
    }
}
