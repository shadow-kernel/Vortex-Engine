using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Git;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Shell
{
    /// <summary>General / Build / Source-control settings of the open project.</summary>
    public sealed class ProjectSettingsWindow : Window
    {
        private readonly ProjectData _project = ProjectData.Current;
        private readonly TextBox _name = new TextBox(), _remote = new TextBox(), _user = new TextBox(), _email = new TextBox();
        private readonly TextBox _company = new TextBox(), _product = new TextBox(), _width = new TextBox { Classes = { "number" } }, _height = new TextBox { Classes = { "number" } };
        private readonly TextBox _version = new TextBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left }, _bundleId = new TextBox { Watermark = "com.company.game (derived when empty)" };
        private readonly Image _iconPreview = new Image { Width = 56, Height = 56 };
        private readonly TextBlock _iconInfo = new TextBlock { Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center };
        private string _pendingIconSource;   // chosen but not yet saved
        private bool _clearIcon;
        private readonly CheckBox _fullscreen = new CheckBox(), _vsync = new CheckBox();
        private readonly ComboBox _bootScene = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBlock _branch = new TextBlock { Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center }, _lfs = new TextBlock { Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center }, _status = new TextBlock { Classes = { "small", "secondary" } };

        public ProjectSettingsWindow()
        {
            Title = "Project Settings"; Width = 560; Height = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; CanResize = false;
            var tabs = new TabControl();
            var general = new StackPanel { Spacing = 4, Margin = new Thickness(14) };
            general.Children.Add(new TextBlock { Text = "Project", Classes = { "section" } });
            general.Children.Add(Row("Name", _name));
            general.Children.Add(Row("Location", new TextBlock { Text = _project?.Path ?? "", Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center }));
            general.Children.Add(new TextBlock { Text = "Game", Classes = { "section" } });
            general.Children.Add(Row("Company", _company));
            general.Children.Add(Row("Product name", _product));
            var res = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; res.Children.Add(_width); res.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center }); res.Children.Add(_height);
            general.Children.Add(Row("Default resolution", res));
            general.Children.Add(Row("Fullscreen", _fullscreen));
            general.Children.Add(Row("V-Sync", _vsync));
            tabs.Items.Add(new TabItem { Header = "General", Content = general });

            var build = new StackPanel { Spacing = 4, Margin = new Thickness(14) };
            build.Children.Add(new TextBlock { Text = "Startup", Classes = { "section" } });
            build.Children.Add(Row("Boot scene", _bootScene, "The exported game starts in this scene; Play in the editor uses the open scene."));
            build.Children.Add(new TextBlock { Text = "Identity", Classes = { "section" } });
            build.Children.Add(Row("Version", _version, "Shown in the app bundle / exe version info (e.g. 1.0.0)."));
            build.Children.Add(Row("Bundle identifier", _bundleId, "Reverse-DNS id for the exported app (macOS bundle, Linux desktop entry)."));
            var iconRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var iconFrame = new Border { Width = 60, Height = 60, CornerRadius = new CornerRadius(12), Background = (Avalonia.Media.IBrush)Application.Current.FindResource("VxPanelRaisedBrush"), Child = _iconPreview, Padding = new Thickness(2) };
            var chooseIcon = new Button { Content = "Choose…", VerticalAlignment = VerticalAlignment.Center }; chooseIcon.Click += async (s, e) => await ChooseIcon();
            var clearIcon = new Button { Content = "Use engine logo", Classes = { "ghost" }, VerticalAlignment = VerticalAlignment.Center }; clearIcon.Click += (s, e) => { _clearIcon = true; _pendingIconSource = null; ShowIcon(null); };
            iconRow.Children.Add(iconFrame); iconRow.Children.Add(chooseIcon); iconRow.Children.Add(clearIcon); iconRow.Children.Add(_iconInfo);
            build.Children.Add(Row("Icon", iconRow, "A square PNG (1024×1024 recommended). Becomes the macOS .icns, the Windows exe icon and the Linux icon on export."));
            build.Children.Add(Note("Build the game from File ▸ Build (⌘B) for macOS, Windows or Linux. Other platforms need a runtime pack (see Managed/README.md)."));
            tabs.Items.Add(new TabItem { Header = "Build", Content = build });

            var git = new StackPanel { Spacing = 4, Margin = new Thickness(14) };
            git.Children.Add(new TextBlock { Text = "Source control", Classes = { "section" } });
            git.Children.Add(Row("Remote (origin)", _remote));
            git.Children.Add(Row("Author name", _user));
            git.Children.Add(Row("Author e-mail", _email));
            git.Children.Add(Row("Branch", _branch));
            git.Children.Add(Row("Git LFS", _lfs));
            tabs.Items.Add(new TabItem { Header = "Source Control", Content = git });

            var buttons = new DockPanel { Margin = new Thickness(14, 8, 14, 14) };
            var save = new Button { Content = "Save", Classes = { "accent" }, MinWidth = 90, IsDefault = true }; save.Click += async (s, e) => await SaveAsync();
            var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }; close.Click += (s, e) => Close();
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; right.Children.Add(close); right.Children.Add(save);
            DockPanel.SetDock(right, Dock.Right); buttons.Children.Add(right); buttons.Children.Add(_status);
            var dock = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons); dock.Children.Add(tabs);
            Content = dock;
            Opened += async (s, e) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (_project == null) return;
            _name.Text = _project.Name ?? "";
            var st = _project.Settings;
            if (st != null) { _company.Text = st.CompanyName; _product.Text = st.ProductName; _width.Text = st.DefaultScreenWidth.ToString(); _height.Text = st.DefaultScreenHeight.ToString(); _fullscreen.IsChecked = st.FullscreenByDefault; _vsync.IsChecked = st.VSync; _version.Text = st.ProductVersion; _bundleId.Text = st.BundleIdentifier; }
            ShowIcon(null);
            foreach (var s in _project.Scenes) _bootScene.Items.Add(s.Name);
            int idx = 0;
            if (_project.StartSceneId.HasValue) for (int i = 0; i < _project.Scenes.Count; i++) if (_project.Scenes[i].Id == _project.StartSceneId.Value) idx = i;
            _bootScene.SelectedIndex = _project.Scenes.Count > 0 ? idx : -1;
            try
            {
                var repo = _project.Path;
                await GitService.Instance.EnsureRepoAsync(repo);
                _remote.Text = await GitService.Instance.GetRemoteUrlAsync(repo);
                _user.Text = await GitService.Instance.GetConfigAsync(repo, "user.name");
                _email.Text = await GitService.Instance.GetConfigAsync(repo, "user.email");
                _branch.Text = await GitService.Instance.CurrentBranchAsync(repo);
                _lfs.Text = await GitService.Instance.IsLfsAvailableAsync() ? "available" : "not installed";
            }
            catch (Exception ex) { _status.Text = "Git: " + ex.Message; }
        }

        private async Task SaveAsync()
        {
            if (_project == null) return;
            try
            {
                var n = (_name.Text ?? "").Trim(); if (!string.IsNullOrEmpty(n) && n != _project.Name) _project.Name = n;
                var st = _project.Settings ?? (_project.Settings = new ProjectSettings());
                st.CompanyName = _company.Text; st.ProductName = _product.Text;
                if (int.TryParse(_width.Text, out int w)) st.DefaultScreenWidth = Math.Max(320, w);
                if (int.TryParse(_height.Text, out int h)) st.DefaultScreenHeight = Math.Max(240, h);
                st.FullscreenByDefault = _fullscreen.IsChecked == true; st.VSync = _vsync.IsChecked == true;
                st.ProductVersion = string.IsNullOrWhiteSpace(_version.Text) ? "1.0.0" : _version.Text.Trim();
                st.BundleIdentifier = (_bundleId.Text ?? "").Trim();
                ApplyIconChoice(st);
                if (_bootScene.SelectedIndex >= 0 && _bootScene.SelectedIndex < _project.Scenes.Count) _project.StartSceneId = _project.Scenes[_bootScene.SelectedIndex].Id;
                ProjectService.Instance.SaveProject(_project);
                var repo = _project.Path;
                await GitService.Instance.SetRemoteUrlAsync(repo, (_remote.Text ?? "").Trim());
                if (!string.IsNullOrWhiteSpace(_user.Text)) await GitService.Instance.SetConfigAsync(repo, "user.name", _user.Text.Trim());
                if (!string.IsNullOrWhiteSpace(_email.Text)) await GitService.Instance.SetConfigAsync(repo, "user.email", _email.Text.Trim());
                _status.Text = "Saved.";
                EditorCommands.Toast("Project settings saved");
            }
            catch (Exception ex) { _status.Text = "Save failed: " + ex.Message; }
        }

        // ---------------------------------------------------------------- game icon

        private async Task ChooseIcon()
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Game icon (PNG or JPEG)", AllowMultiple = false,
                FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg" } } }
            });
            string path = picked.Count > 0 ? Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(picked[0]) : null;
            if (path == null) return;
            _pendingIconSource = path; _clearIcon = false;
            ShowIcon(path);
        }

        private void ShowIcon(string overridePath)
        {
            try
            {
                byte[] bytes;
                string desc;
                if (overridePath != null) { bytes = File.ReadAllBytes(overridePath); desc = Path.GetFileName(overridePath) + " (not saved yet)"; }
                else bytes = VortexEditor.Build.IconFactory.LoadSource(_clearIcon ? null : _project, out desc);
                using var ms = new MemoryStream(bytes);
                _iconPreview.Source = new Avalonia.Media.Imaging.Bitmap(ms);
                _iconInfo.Text = desc;
            }
            catch (Exception ex) { _iconPreview.Source = null; _iconInfo.Text = "icon not readable: " + ex.Message; }
        }

        /// <summary>Copy the chosen image into ProjectSettings/GameIcon.png (as a 1024px PNG) and point IconPath at it.</summary>
        private void ApplyIconChoice(ProjectSettings st)
        {
            if (_clearIcon)
            {
                st.IconPath = "";
                try { File.Delete(Path.Combine(_project.Path, "ProjectSettings", "GameIcon.png")); } catch { }
                _clearIcon = false;
            }
            if (_pendingIconSource == null) return;
            string dir = Path.Combine(_project.Path, "ProjectSettings");
            Directory.CreateDirectory(dir);
            byte[] png = VortexEditor.Build.IconFactory.EncodePng(File.ReadAllBytes(_pendingIconSource), 1024);
            File.WriteAllBytes(Path.Combine(dir, "GameIcon.png"), png);
            st.IconPath = "ProjectSettings/GameIcon.png";
            _pendingIconSource = null;
            ShowIcon(null);
        }
    }
}
