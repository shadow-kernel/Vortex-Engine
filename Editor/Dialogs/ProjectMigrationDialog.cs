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
                  "Vortex will make a backup first (in the project's .ve\\backups folder) and then update it automatically. This is safe and reversible."
                : "This project was saved with a newer version of Vortex" + (string.IsNullOrEmpty(plan.SavedWithEngine) ? "" : " (v" + plan.SavedWithEngine + ")") +
                  " than the one you're running (v" + Editor.Core.EngineInfo.VersionString + ").\n\n" +
                  "It may not open correctly, and saving could lose newer data. Update Vortex to the latest version, or open at your own risk.";
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
            var cancel = DialogStyles.CreateButton("Cancel", 90, false);
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (s, e) => { DialogResult = false; Close(); };
            actions.Children.Add(cancel);
            var ok = DialogStyles.CreateButton(needs ? "Back up & update" : "Open anyway", 150, true);
            ok.Click += (s, e) => { DialogResult = true; Close(); };
            actions.Children.Add(ok);
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

            if (!proceed)
                throw new Editor.Core.Exceptions.ProjectException("Opening the project was cancelled (version mismatch).");

            if (plan.Status == MigrationStatus.NeedsMigration)
            {
                bool ok = ProjectMigrationService.Migrate(projectDir, manifest, manifestPath,
                    m => System.Diagnostics.Debug.WriteLine("[Migrate] " + m));
                if (!ok)
                    throw new Editor.Core.Exceptions.ProjectException("Project migration failed — the backup was restored. The project was not opened.");
            }
            // NewerThanEngine + proceed == "open anyway": just return and let it load.
        }
    }
}
