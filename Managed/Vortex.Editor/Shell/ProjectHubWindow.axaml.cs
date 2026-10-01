using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;

namespace VortexEditor.Shell
{
    public partial class ProjectHubWindow : Window
    {
        public sealed class ProjectRow
        {
            public ProjectRef Ref; public string Name => Ref.Name; public string Path => Ref.Path;
            public string Modified; public Bitmap Thumbnail; public bool HasThumbnail => Thumbnail != null;
        }

        private List<ProjectRow> _all = new List<ProjectRow>();
        private List<ProjectTemplate> _templates = new List<ProjectTemplate>();

        public ProjectHubWindow(bool createTab)
        {
            InitializeComponent();
            VersionText.Text = "Version " + SafeVersion();
            AutoOpenBox.IsChecked = EditorPreferences.Current.OpenLastProjectOnStart;
            var last = EditorSession.Instance.LastProjectPath;
            if (last != null)
            {
                LastProjectCard.IsVisible = true;
                LastProjectName.Text = Path.GetFileName(last.TrimEnd('/', '\\'));
                try { var m = ProjectService.Instance.GetAllProjects().Values.FirstOrDefault(r => string.Equals(Path.GetFullPath(r.Path), Path.GetFullPath(last), StringComparison.OrdinalIgnoreCase)); if (m != null) LastProjectName.Text = m.Name; } catch { }
                LastProjectPath.Text = last;
            }
            LoadProjects();
            _templates = ProjectTemplateService.Discover();
            TemplateList.ItemsSource = _templates;
            TemplateList.SelectedItem = _templates.FirstOrDefault(t => !t.IsEmpty) ?? _templates.FirstOrDefault();
            LocationBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "VortexEngineProjects", NameBox.Text);
            // WPF: returning users land on their recent projects, brand-new users straight on the template chooser
            SelectTab(createTab || _all.Count == 0);
            bool hasProject = EditorSession.Instance.HasProject;
            ExitButton.Content = hasProject ? "Cancel" : "Quit";
            CancelCreateButton.Content = hasProject ? "Cancel" : "Quit";
            ProjectList.ContextMenu = RowMenu();
            ProjectList.KeyDown += (s, e) => { if (e.Key == Avalonia.Input.Key.Return) { OpenSelected(); e.Handled = true; } };
            KeyDown += (s, e) => { if (e.Key == Avalonia.Input.Key.Escape && EditorSession.Instance.HasProject) Close(); };
        }

        private ContextMenu RowMenu()
        {
            var m = new ContextMenu();
            var open = new MenuItem { Header = "Open" }; open.Click += (s, e) => OpenSelected();
            var reveal = new MenuItem { Header = "Show in Finder" }; reveal.Click += (s, e) => OnRevealProject(null, null);
            var remove = new MenuItem { Header = "Remove from List…" }; remove.Click += (s, e) => OnRemoveProject(null, null);
            m.Items.Add(open); m.Items.Add(reveal); m.Items.Add(new Separator()); m.Items.Add(remove);
            return m;
        }

        private void OnExit(object s, RoutedEventArgs e) => Close();
        private void OnRevealProject(object s, RoutedEventArgs e) { if (ProjectList.SelectedItem is ProjectRow row && Directory.Exists(row.Path)) EditorCommands.RevealInFinder(row.Path); }

        /// <summary>Which page is showing (smoke checks).</summary>
        public bool IsCreatePage => CreatePage.IsVisible;
        public int ProjectCount => _all.Count;

        private static string SafeVersion() { try { return Editor.Core.EngineInfo.VersionString; } catch { return ""; } }

        public void SelectTab(bool create) { Nav.SelectedIndex = create ? 1 : 0; }

        private void OnNavChanged(object s, SelectionChangedEventArgs e)
        {
            bool create = Nav.SelectedIndex == 1;
            OpenPage.IsVisible = !create; CreatePage.IsVisible = create;
        }

        // ---------------------------------------------------------------- open
        private void LoadProjects()
        {
            _all.Clear();
            foreach (var r in ProjectService.Instance.GetAllProjects().Values)
            {
                if (r == null || string.IsNullOrEmpty(r.Path)) continue;
                var row = new ProjectRow { Ref = r };
                try
                {
                    if (Directory.Exists(r.Path))
                    {
                        row.Modified = Directory.GetLastWriteTime(r.Path).ToString("d MMM yyyy, HH:mm");
                        string thumb = Path.Combine(r.Path, ".ve", "icon.png");
                        if (File.Exists(thumb)) row.Thumbnail = new Bitmap(thumb);
                    }
                    else row.Modified = "missing";
                }
                catch { }
                _all.Add(row);
            }
            _all = _all.OrderByDescending(p => Directory.Exists(p.Path) ? Directory.GetLastWriteTime(p.Path) : DateTime.MinValue).ToList();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string q = SearchBox.Text?.Trim();
            ProjectList.ItemsSource = string.IsNullOrEmpty(q) ? _all : _all.Where(p => p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || p.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        private void OnSearchChanged(object s, TextChangedEventArgs e) => ApplyFilter();
        private void OnContinueLast(object s, RoutedEventArgs e) { var last = EditorSession.Instance.LastProjectPath; if (last != null && EditorSession.Instance.OpenProject(last)) Close(); }
        private void OnAutoOpenChanged(object s, RoutedEventArgs e) { EditorPreferences.Current.OpenLastProjectOnStart = AutoOpenBox.IsChecked == true; EditorPreferences.Current.Save(); }
        private void OnProjectDoubleTapped(object s, Avalonia.Input.TappedEventArgs e) => OpenSelected();
        private void OnOpenSelected(object s, RoutedEventArgs e) => OpenSelected();

        private void OpenSelected()
        {
            if (!(ProjectList.SelectedItem is ProjectRow row)) return;
            if (!Directory.Exists(row.Path)) { _ = Dialogs.Alert("Project not found", "The folder " + row.Path + " no longer exists."); return; }
            if (EditorSession.Instance.OpenProject(row.Path)) Close();
        }

        private async void OnBrowseProject(object s, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open Vortex Project" });
            var dir = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(dir)) return;
            if (!File.Exists(Path.Combine(dir, ProjectService.ManifestFileName)) && !File.Exists(Path.Combine(dir, ProjectService.LegacyManifestPath)))
            { await Dialogs.Alert("Not a Vortex project", "The folder has no project.vortex file."); return; }
            if (EditorSession.Instance.OpenProject(dir)) Close();
        }

        private async void OnRemoveProject(object s, RoutedEventArgs e)
        {
            if (!(ProjectList.SelectedItem is ProjectRow row)) return;
            if (!await Dialogs.Confirm("Remove \"" + row.Name + "\" from the list?", "The project files stay on disk.", "Remove", "Cancel")) return;
            try { ProjectService.Instance.UnregisterProject(row.Ref.Id); } catch { }
            LoadProjects();
        }

        // ---------------------------------------------------------------- create
        private void OnTemplateChanged(object s, SelectionChangedEventArgs e)
        {
            var t = TemplateList.SelectedItem as ProjectTemplate;
            TemplateName.Text = t?.Name ?? "";
            TemplateDescription.Text = t?.Description ?? "";
            PreviewImage.Source = null;
            try { if (!string.IsNullOrEmpty(t?.PreviewImagePath) && File.Exists(t.PreviewImagePath)) PreviewImage.Source = new Bitmap(t.PreviewImagePath); } catch { }
            PreviewFallback.IsVisible = PreviewImage.Source == null;
        }

        private void OnNameChanged(object s, TextChangedEventArgs e)
        {
            try { var dir = Path.GetDirectoryName(LocationBox.Text ?? ""); if (!string.IsNullOrEmpty(dir) && !string.IsNullOrWhiteSpace(NameBox.Text)) LocationBox.Text = Path.Combine(dir, NameBox.Text.Trim()); } catch { }
        }

        private async void OnBrowseLocation(object s, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the parent folder" });
            var dir = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            if (!string.IsNullOrEmpty(dir)) LocationBox.Text = Path.Combine(dir, (NameBox.Text ?? "My Game").Trim());
        }

        private async void OnCreate(object s, RoutedEventArgs e)
        {
            string name = (NameBox.Text ?? "").Trim();
            string path = (LocationBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name)) { await Dialogs.Alert("Name required", "Give the project a name."); return; }
            if (string.IsNullOrEmpty(path)) { await Dialogs.Alert("Location required", "Choose where the project folder is created."); return; }
            try
            {
                var project = EditorSession.Instance.CreateProject(name, path, TemplateList.SelectedItem as ProjectTemplate);
                if (project != null && EditorSession.Instance.OpenProject(project)) Close();
            }
            catch (Exception ex) { await Dialogs.Alert("Could not create the project", ex.Message); }
        }
    }
}
