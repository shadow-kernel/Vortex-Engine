using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Editor.Core.Data;
using Editor.Core.Migration;

namespace Editor.Dialogs
{
    /// <summary>
    /// Shown when a project's format doesn't match the running engine:
    ///  • NeedsMigration → older project + a breaking engine change: offers to back up + update it (Major-update flow).
    ///  • NewerThanEngine → project was saved by a newer engine: warns it may not open correctly.
    /// The static <see cref="EnsureCompatible"/> is the gate the project-open path calls before loading.
    /// </summary>
    public sealed class ProjectMigrationDialog : Window
    {
        private static Brush B(Color c) => new SolidColorBrush(c);

        private ProjectMigrationDialog(MigrationPlan plan)
        {
            bool needs = plan.Status == MigrationStatus.NeedsMigration;

            Title = "Vortex Engine — Project Compatibility";
            Width = 500;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = B(DialogStyles.BackgroundColor);
            Foreground = DialogStyles.TextBrush;
            ShowInTaskbar = false;

            var accent = Color.FromRgb(0xF0, 0x8A, 0x3A); // amber warning
            var root = new StackPanel { Margin = new Thickness(24) };

            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 20, Foreground = B(accent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            head.Children.Add(new TextBlock { Text = needs ? "This project needs updating" : "Project made with a newer engine", FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            root.Children.Add(head);

            string body = needs
                ? "This project was created with an older version of Vortex and must be updated to open with the current engine (v" + Editor.Core.EngineInfo.VersionString + ").\n\n" +
                  "Vortex will make a backup first (in the project's .ve\\backups folder) and then update it step by step. This is safe and reversible."
                : "This project was saved with a newer version of Vortex" + (string.IsNullOrEmpty(plan.SavedWithEngine) ? "" : " (v" + plan.SavedWithEngine + ")") +
                  " than the one you're running (v" + Editor.Core.EngineInfo.VersionString + ").\n\n" +
                  "Opening it is blocked: this build could misread or destroy data written by the newer version. " +
                  "Update Vortex to the latest version, then open the project again.";
            root.Children.Add(new TextBlock { Text = body, Foreground = DialogStyles.TextBrush, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(30, 0, 0, 14) });

            if (needs && plan.Steps != null && plan.Steps.Count > 0)
            {
                var stepsBox = new StackPanel { Margin = new Thickness(30, 0, 0, 14) };
                stepsBox.Children.Add(new TextBlock { Text = "Changes to apply:", Foreground = DialogStyles.TextSecondaryBrush, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
                foreach (var s in plan.Steps)
                    stepsBox.Children.Add(new TextBlock { Text = "•  " + s.Description, Foreground = DialogStyles.TextSecondaryBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
                root.Children.Add(stepsBox);
            }

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            if (needs)
            {
                var cancel = DialogStyles.CreateButton("Cancel", 90, false);
                cancel.Margin = new Thickness(0, 0, 8, 0);
                cancel.Click += (s, e) => { DialogResult = false; Close(); };
                actions.Children.Add(cancel);
                var ok = DialogStyles.CreateButton("Back up & update", 150, true);
                ok.Click += (s, e) => { DialogResult = true; Close(); };
                actions.Children.Add(ok);
            }
            else
            {
                // NewerThanEngine is a HARD BLOCK — there is no "open anyway" (a re-save from an older
                // build could silently destroy newer data). Offer the update path instead.
                var update = DialogStyles.CreateButton("Check for Updates…", 160, false);
                update.Margin = new Thickness(0, 0, 8, 0);
                update.Click += async (s, e) =>
                {
                    try
                    {
                        var info = await Editor.Core.Services.Update.UpdateService.CheckAsync();
                        if (info != null) { var u = new UpdateDialog(info) { Owner = this }; u.ShowDialog(); }
                        else MessageBox.Show(this, "No newer release is available yet.", "Check for Updates",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch { }
                };
                actions.Children.Add(update);
                var close = DialogStyles.CreateButton("Close", 90, true);
                close.Click += (s, e) => { DialogResult = false; Close(); };
                actions.Children.Add(close);
            }
            root.Children.Add(actions);

            Content = root;
        }

        /// <summary>
        /// Project-open gate (editor-only — caller must skip for the shipped game / mounted pak). Evaluates the
        /// project's format; up-to-date projects pass through silently. Otherwise prompts and either migrates
        /// (back up + run steps + re-stamp) or lets the user open-anyway / cancel. Throws to ABORT the open when
        /// the user cancels or a migration fails.
        /// </summary>
        public static void EnsureCompatible(string projectDir, ProjectManifest manifest, string manifestPath)
        {
            var plan = ProjectMigrationService.Evaluate(manifest);
            if (plan.Status == MigrationStatus.UpToDate || plan.Status == MigrationStatus.Unknown)
                return; // the overwhelmingly common path — no dialog, no cost

            bool proceed = false;
            var app = Application.Current;
            Action show = () =>
            {
                var dlg = new ProjectMigrationDialog(plan);
                if (app != null && app.MainWindow != null && app.MainWindow.IsLoaded) dlg.Owner = app.MainWindow;
                proceed = dlg.ShowDialog() == true;
            };
            if (app != null && !app.Dispatcher.CheckAccess()) app.Dispatcher.Invoke(show); else show();

            if (!proceed || plan.Status == MigrationStatus.NewerThanEngine)
                throw new Editor.Core.Exceptions.ProjectException(plan.Status == MigrationStatus.NewerThanEngine
                    ? "This project was saved with a newer Vortex version (" + (plan.SavedWithEngine ?? "?") + ") — update the engine to open it."
                    : "Opening the project was cancelled (version mismatch).");

            if (plan.Status == MigrationStatus.NeedsMigration)
            {
                // Splash-style progress window: live step-by-step log while the Trafo chain runs on a
                // background task (backup -> each step -> re-stamp). Marshal to the UI thread if needed.
                bool ok = false;
                Action run = () =>
                {
                    var owner = Application.Current != null ? Application.Current.MainWindow : null;
                    ok = MigrationProgressDialog.Run(owner != null && owner.IsLoaded ? owner : null,
                        projectDir, manifest, manifestPath, plan);
                };
                if (app != null && !app.Dispatcher.CheckAccess()) app.Dispatcher.Invoke(run); else run();

                if (!ok)
                    throw new Editor.Core.Exceptions.ProjectException("Project migration failed — the backup was restored. The project was not opened.");
            }
        }
    }
}
