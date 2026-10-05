using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Project.Model;

namespace Editor.Project.Projection
{
    public partial class OpenProjectView : UserControl
    {
        private OpenProjectModel _dataContextModel;

        public OpenProjectView()
        {
            InitializeComponent();
            if (DataContext == null)
            {
                DataContext = new OpenProjectModel();
            }
            _dataContextModel = DataContext as OpenProjectModel;
            
            if (_dataContextModel != null)
            {
                _dataContextModel.ProjectOpened += OnProjectOpened;
            }
        }

        private void OnProjectOpened(object sender, ProjectData project)
        {
            var window = Window.GetWindow(this) as ProjectBrowserWindow;
            if (window != null)
            {
                window.SelectedProject = project;
                window.DialogResult = true;
                window.Close();
            }
        }

        private void ExitButton_Pressed(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this) as ProjectBrowserWindow;
            if (window != null)
            {
                window.DialogResult = false;
                window.Close();
            }
        }

        private void OpenButton_Pressed(object sender, RoutedEventArgs e)
        {
            var item = ProjectsListView.SelectedItem as ProjectRef;
            if (item != null)
            {
                _dataContextModel.OpenProject(item);
            }
        }

        private void DoubleClickListItem(object sender, RoutedEventArgs e)
        {
            OpenButton_Pressed(sender, e);
        }

        private void SearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_dataContextModel != null)
            {
                var textBox = sender as TextBox;
                _dataContextModel.SearchText = textBox?.Text ?? string.Empty;
            }
        }

        /// <summary>
        /// Project list ▸ Update from Template… (#186): the template's current files into the project, after a preview
        /// and with a backup of every overwritten file (.ve/backups/template-update-&lt;time&gt;). Scenes the project already
        /// has stay as they are — replacing them would discard level edits (the Avalonia editor lets you opt in).
        /// </summary>
        private async void UpdateFromTemplate_Click(object sender, RoutedEventArgs e)
        {
            var project = ProjectsListView.SelectedItem as ProjectRef;
            if (project == null || string.IsNullOrEmpty(project.Path) || !Directory.Exists(project.Path)) return;
            const string caption = "Update from Template";
            try
            {
                var templates = ProjectTemplateService.Discover().Where(t => !t.IsEmpty).ToList();
                var template = TemplateUpdateService.GuessTemplate(project.Path, templates);
                if (template == null)
                {
                    MessageBox.Show("Could not tell which template \"" + project.Name + "\" was created from.", caption, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                string dir = template.NeedsDownload ? await TemplatePacks.EnsureAsync(template) : template.ProjectDir;
                var plan = await Task.Run(() => TemplateUpdateService.Plan(project.Path, dir));
                var chosen = plan.Changes.Where(c => c.IsNew || c.Group != "Scenes").ToList();
                int keptScenes = plan.Changes.Count - chosen.Count;
                if (chosen.Count == 0)
                {
                    MessageBox.Show("\"" + project.Name + "\" already has the current content of " + template.Name + "." +
                                    (keptScenes > 0 ? " (" + keptScenes + " of its scenes differ from the template's and stay as they are.)" : ""),
                                    caption, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                string files = string.Join("\n", chosen.Take(20).Select(c => (c.IsNew ? "+ " : "~ ") + c.Path)) + (chosen.Count > 20 ? "\n… " + (chosen.Count - 20) + " more" : "");
                var answer = MessageBox.Show(
                    "Update \"" + project.Name + "\" from " + template.Name + ":\n\n" +
                    chosen.Count(c => c.IsNew) + " new file(s), " + chosen.Count(c => !c.IsNew) + " changed file(s) — your versions are backed up first.\n" +
                    (keptScenes > 0 ? keptScenes + " scene(s) the project already has stay as they are.\n" : "") + "\n" + files,
                    caption, MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK) return;
                var result = await Task.Run(() => TemplateUpdateService.Apply(plan, chosen));
                MessageBox.Show(result.FilesCopied + " file(s) updated" + (result.ScenesAdded.Count > 0 ? ", scene(s) added: " + string.Join(", ", result.ScenesAdded) : "") +
                                ".\n\nYour previous files are in " + result.BackupDir, caption, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, caption, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var project = button?.Tag as ProjectData;

            if (project == null)
                return;

            var result = MessageBox.Show(
                $"Möchten Sie das Projekt '{project.Name}' löschen?\n\n" +
                $"Ja: Projekt aus der Liste entfernen UND alle Projektdateien löschen\n" +
                $"Nein: Nur aus der Liste entfernen (Dateien bleiben erhalten)\n" +
                $"Abbrechen: Nichts tun",
                "Projekt löschen",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
                return;

            bool deleteFiles = (result == MessageBoxResult.Yes);

            try
            {
                _dataContextModel.DeleteProject(project, deleteFiles);

                string message = deleteFiles
                    ? "Projekt wurde aus der Liste entfernt und alle Dateien wurden gelöscht."
                    : "Projekt wurde aus der Liste entfernt. Die Dateien bleiben erhalten.";

                MessageBox.Show(message, "Erfolg", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Fehler beim Löschen des Projekts: {ex.Message}",
                    "Fehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
