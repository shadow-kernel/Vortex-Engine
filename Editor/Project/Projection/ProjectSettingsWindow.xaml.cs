using System;
using System.Threading.Tasks;
using System.Windows;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Git;

namespace Editor.Project.Projection
{
    /// <summary>Central project settings — general (name/location) + Git/Source Control
    /// (remote origin, commit identity, branch, LFS). The one place for default project + git config.</summary>
    public partial class ProjectSettingsWindow : Window
    {
        private readonly ProjectData _project;
        private string _repo;

        public ProjectSettingsWindow()
        {
            InitializeComponent();
            _project = ProjectData.Current;
            _repo = _project != null ? _project.Path : null;
            ProjLabel.Text = _project != null ? "— " + _project.Name : "— (no project)";
            Loaded += async (s, e) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (_project != null)
            {
                NameBox.Text = _project.Name ?? "";
                PathBox.Text = _project.Path ?? "";

                // Build: default (boot) scene. The exported game boots this; the editor's Play uses the open scene.
                DefaultSceneBox.ItemsSource = _project.Scenes;
                Scene boot = null;
                if (_project.StartSceneId.HasValue)
                    foreach (var s in _project.Scenes)
                        if (s != null && s.Id == _project.StartSceneId.Value) { boot = s; break; }
                if (boot == null && _project.Scenes.Count > 0) boot = _project.Scenes[0]; // mirrors the load-time fallback
                DefaultSceneBox.SelectedItem = boot;
            }
            if (string.IsNullOrEmpty(_repo)) { StatusText.Text = "No project open."; return; }

            try
            {
                await GitService.Instance.EnsureRepoAsync(_repo);
                RemoteBox.Text = await GitService.Instance.GetRemoteUrlAsync(_repo);
                UserNameBox.Text = await GitService.Instance.GetConfigAsync(_repo, "user.name");
                UserEmailBox.Text = await GitService.Instance.GetConfigAsync(_repo, "user.email");
                BranchLabel.Text = await GitService.Instance.CurrentBranchAsync(_repo);
                LfsLabel.Text = await GitService.Instance.IsLfsAvailableAsync() ? "available" : "not installed";
            }
            catch (Exception ex) { StatusText.Text = "Git read failed: " + ex.Message; }
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // General
                if (_project != null)
                {
                    var newName = (NameBox.Text ?? "").Trim();
                    if (!string.IsNullOrEmpty(newName) && newName != _project.Name)
                        _project.Name = newName;

                    // Build: persist the chosen default/boot scene (the exported game starts here).
                    var boot = DefaultSceneBox.SelectedItem as Scene;
                    if (boot != null) _project.StartSceneId = boot.Id;

                    ProjectService.Instance.SaveProject(_project);
                }

                // Git
                if (!string.IsNullOrEmpty(_repo))
                {
                    await GitService.Instance.SetRemoteUrlAsync(_repo, (RemoteBox.Text ?? "").Trim());
                    var n = (UserNameBox.Text ?? "").Trim();
                    var em = (UserEmailBox.Text ?? "").Trim();
                    if (!string.IsNullOrEmpty(n)) await GitService.Instance.SetConfigAsync(_repo, "user.name", n);
                    if (!string.IsNullOrEmpty(em)) await GitService.Instance.SetConfigAsync(_repo, "user.email", em);
                }

                ProjLabel.Text = _project != null ? "— " + _project.Name : "";
                StatusText.Text = "Saved.";
            }
            catch (Exception ex) { StatusText.Text = "Save failed: " + ex.Message; }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
