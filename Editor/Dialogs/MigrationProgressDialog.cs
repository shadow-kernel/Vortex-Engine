using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.Core.Data;
using Editor.Core.Migration;

namespace Editor.Dialogs
{
    /// <summary>
    /// Splash-style progress window shown WHILE a project migrates: logo + version transition, a live
    /// step-by-step log of everything the migration does (backup, each Trafo step, re-stamp), and a
    /// progress bar. Runs the migration on a background task; the window closes itself on completion.
    /// Use the static <see cref="Run"/> — returns true when the migration succeeded.
    /// </summary>
    public sealed class MigrationProgressDialog : Window
    {
        private readonly StackPanel _log;
        private readonly ScrollViewer _scroll;
        private readonly ProgressBar _bar;
        private readonly TextBlock _status;
        private bool _done;

        private static Brush B(Color c) => new SolidColorBrush(c);

        public static bool Run(Window owner, string projectDir, ProjectManifest manifest, string manifestPath, MigrationPlan plan)
        {
            var dlg = new MigrationProgressDialog(plan);
            if (owner != null && owner.IsLoaded) dlg.Owner = owner;

            bool ok = false;
            dlg.Loaded += async (s, e) =>
            {
                // Background migration; log lines marshal back onto the UI thread.
                ok = await Task.Run(() => ProjectMigrationService.Migrate(projectDir, manifest, manifestPath,
                    line => dlg.Dispatcher.BeginInvoke(new Action(() => dlg.AppendLine(line)))));

                dlg._done = true;
                dlg._bar.IsIndeterminate = false;
                dlg._bar.Value = 1;
                if (ok)
                {
                    dlg._status.Text = "Project updated successfully.";
                    await Task.Delay(900);   // let the user see the final state
                    dlg.Close();
                }
                else
                {
                    dlg._status.Text = "Migration failed — the backup was restored. The project was not changed.";
                    dlg._status.Foreground = B(Color.FromRgb(0xFF, 0x6B, 0x6B));
                    dlg.AppendCloseButton();
                }
            };
            dlg.ShowDialog();
            return ok;
        }

        private MigrationProgressDialog(MigrationPlan plan)
        {
            Title = "Vortex Engine — Updating Project";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;                       // splash look: borderless card
            Background = B(DialogStyles.BackgroundColor);
            Foreground = DialogStyles.TextBrush;
            BorderBrush = DialogStyles.BorderBrush;
            BorderThickness = new Thickness(1);
            ShowInTaskbar = true;

            var root = new StackPanel { Margin = new Thickness(28, 24, 28, 22) };

            var head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
            try
            {
                var logo = new Image
                {
                    Source = new BitmapImage(new Uri("pack://application:,,,/Assets/Images/Logo.png")),
                    Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
                head.Children.Add(logo);
            }
            catch { }
            head.Children.Add(new TextBlock
            {
                Text = "Updating project",
                FontSize = 19, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            root.Children.Add(head);

            root.Children.Add(new TextBlock
            {
                Text = "Format v" + plan.From + "  →  v" + plan.To +
                       (string.IsNullOrEmpty(plan.SavedWithEngine) ? "" : "   (last saved with Vortex " + plan.SavedWithEngine + ")"),
                FontSize = 12, Foreground = DialogStyles.TextSecondaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14)
            });

            // live migration log
            var box = new Border
            {
                Background = B(Color.FromRgb(0x0F, 0x0F, 0x12)),
                BorderBrush = DialogStyles.BorderBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 12), Height = 190
            };
            _log = new StackPanel();
            _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _log };
            box.Child = _scroll;
            root.Children.Add(box);

            _status = new TextBlock
            {
                Text = "Working — do not close the project folder…",
                FontSize = 11.5, Foreground = DialogStyles.TextSecondaryBrush,
                Margin = new Thickness(0, 0, 0, 6)
            };
            root.Children.Add(_status);

            _bar = new ProgressBar
            {
                Height = 6, Minimum = 0, Maximum = 1, IsIndeterminate = true,
                Foreground = DialogStyles.AccentBrush, Background = B(DialogStyles.PanelColor),
                BorderThickness = new Thickness(0)
            };
            root.Children.Add(_bar);

            Content = root;
            Closing += (s, e) => { if (!_done) e.Cancel = true; };   // not interruptible mid-migration
        }

        private void AppendLine(string line)
        {
            _log.Children.Add(new TextBlock
            {
                Text = "›  " + line,
                FontSize = 11.5, Foreground = DialogStyles.TextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 1)
            });
            _scroll.ScrollToEnd();
        }

        private void AppendCloseButton()
        {
            var close = DialogStyles.CreateButton("Close", 90, true);
            close.Margin = new Thickness(0, 12, 0, 0);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Click += (s, e) => Close();
            ((StackPanel)Content).Children.Add(close);
        }
    }
}
