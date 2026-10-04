using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.Services.Git;
using VortexEditor.Controls;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Project settings (WPF ProjectSettingsWindow + the rest of ProjectSettings): General (name, location, company,
    /// product, default resolution, fullscreen, v-sync, target FPS), Build (boot scene, version, bundle id, game icon),
    /// Source Control (remote, commit author, branch, LFS) and Editor (per-user preferences: reopen the last project,
    /// script IDE, appearance).
    /// </summary>
    public sealed class ProjectSettingsWindow : Window
    {
        private readonly ProjectData _project = ProjectData.Current;
        private readonly TextBox _name = new TextBox(), _remote = new TextBox { Watermark = "https://github.com/you/repo.git" }, _user = new TextBox(), _email = new TextBox();
        private readonly TextBox _company = new TextBox(), _product = new TextBox(), _width = new TextBox { Classes = { "number" }, Width = 80 }, _height = new TextBox { Classes = { "number" }, Width = 80 };
        private readonly TextBox _fps = new TextBox { Classes = { "number" }, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBox _version = new TextBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left }, _bundleId = new TextBox { Watermark = "com.company.game (derived when empty)" };
        private readonly Image _iconPreview = new Image { Width = 56, Height = 56 };
        private readonly TextBlock _iconInfo = new TextBlock { Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 160 };
        private string _pendingIconSource;   // chosen but not yet saved
        private bool _clearIcon;
        private readonly CheckBox _fullscreen = new CheckBox(), _vsync = new CheckBox(), _openLast = new CheckBox();
        private readonly ComboBox _bootScene = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly ComboBox _ide = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { "Automatic (VS Code, then Rider)", "Visual Studio Code", "JetBrains Rider" } };
        private readonly ComboBox _theme = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { "System", "Dark", "Light" } };
        private readonly TextBlock _branch = new TextBlock { Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center }, _lfs = new TextBlock { Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center }, _status = new TextBlock { Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center };
        public TabControl Tabs { get; }
        public string StatusText => _status.Text;

        public ProjectSettingsWindow()
        {
            Title = "Project Settings" + (_project != null ? " — " + _project.Name : ""); Width = 600; Height = 640; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; CanResize = false;
            var tabs = Tabs = new TabControl();

            var general = Page();
            general.Children.Add(new TextBlock { Text = "Project", Classes = { "section" } });
            general.Children.Add(Row("Name", _name));
            var loc = new DockPanel();
            var reveal = new Button { Content = "Show in Finder", Classes = { "ghost" }, VerticalAlignment = VerticalAlignment.Center };
            reveal.Click += (s, e) => { if (_project?.Path != null) EditorCommands.RevealInFinder(_project.Path); };
            DockPanel.SetDock(reveal, Dock.Right);
            loc.Children.Add(reveal);
            loc.Children.Add(new TextBlock { Text = _project?.Path ?? "", Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
            general.Children.Add(Row("Location", loc));
            general.Children.Add(new TextBlock { Text = "Game", Classes = { "section" } });
            general.Children.Add(Row("Company", _company));
            general.Children.Add(Row("Product name", _product, "The game's name — window title, app bundle and exe name."));
            var res = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; res.Children.Add(_width); res.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center }); res.Children.Add(_height);
            general.Children.Add(Row("Resolution", res, "Default window size of the exported game."));
            general.Children.Add(Row("Fullscreen", _fullscreen));
            general.Children.Add(Row("V-Sync", _vsync));
            general.Children.Add(Row("Target FPS", _fps, "Frame-rate cap of the exported game (0 = unlimited; v-sync caps at the display rate)."));
            tabs.Items.Add(new TabItem { Header = "General", Content = Scroll(general) });

            var build = Page();
            build.Children.Add(new TextBlock { Text = "Startup", Classes = { "section" } });
            build.Children.Add(Row("Boot scene", _bootScene, "The exported game always boots this scene. In the editor, Play runs the scene you currently have open."));
            build.Children.Add(new TextBlock { Text = "Identity", Classes = { "section" } });
            build.Children.Add(Row("Version", _version, "Shown in the app bundle / exe version info (e.g. 1.0.0)."));
            build.Children.Add(Row("Bundle identifier", _bundleId, "Reverse-DNS id for the exported app (macOS bundle, Linux desktop entry)."));
            var iconRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var iconFrame = new Border { Width = 60, Height = 60, CornerRadius = new CornerRadius(12), Background = (Avalonia.Media.IBrush)Application.Current.FindResource("VxPanelRaisedBrush"), Child = _iconPreview, Padding = new Thickness(2) };
            var chooseIcon = new Button { Content = "Choose…", VerticalAlignment = VerticalAlignment.Center }; chooseIcon.Click += async (s, e) => await ChooseIcon();
            var clearIcon = new Button { Content = "Use engine logo", Classes = { "ghost" }, VerticalAlignment = VerticalAlignment.Center }; clearIcon.Click += (s, e) => { _clearIcon = true; _pendingIconSource = null; ShowIcon(null); };
            iconRow.Children.Add(iconFrame); iconRow.Children.Add(chooseIcon); iconRow.Children.Add(clearIcon); iconRow.Children.Add(_iconInfo);
            build.Children.Add(Row("Icon", iconRow, "A square PNG (1024×1024 recommended). Becomes the macOS .icns, the Windows exe icon and the Linux icon on export."));
            build.Children.Add(Note("Build the game from File \u25b8 Build (" + Keys.Chord("B") + ") for macOS, Windows or Linux. Other platforms need a runtime pack (see Managed/README.md)."));
            tabs.Items.Add(new TabItem { Header = "Build", Content = Scroll(build) });

            var git = Page();
            git.Children.Add(new TextBlock { Text = "Git / source control", Classes = { "section" } });
            git.Children.Add(Row("Remote (origin)", _remote));
            git.Children.Add(Row("Author name", _user));
            git.Children.Add(Row("Author e-mail", _email));
            git.Children.Add(Row("Current branch", _branch));
            git.Children.Add(Row("Git LFS", _lfs));
            var openGit = new Button { Content = "Open Source Control…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            openGit.Click += (s, e) => { Close(); EditorCommands.GitWindow(); };
            git.Children.Add(openGit);
            tabs.Items.Add(new TabItem { Header = "Source Control", Content = Scroll(git) });

            var ed = Page();
            ed.Children.Add(new TextBlock { Text = "Editor (this Mac, all projects)", Classes = { "section" } });
            ed.Children.Add(Row("Open last", _openLast, "Skip the project hub and reopen the last project when the editor starts."));
            ed.Children.Add(Row("Script editor", _ide, "Which IDE opens scripts (double-click a script, ‘Open Scripts Project’)."));
            ed.Children.Add(Row("Appearance", _theme, "Editor colour scheme — System follows the macOS appearance."));
            var shortcuts = new Button { Content = "Keyboard Shortcuts…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            shortcuts.Click += (s, e) => _ = EditorCommands.KeyboardShortcuts();
            ed.Children.Add(shortcuts);
            tabs.Items.Add(new TabItem { Header = "Editor", Content = Scroll(ed) });

            var buttons = new DockPanel { Margin = new Thickness(14, 8, 14, 14) };
            var save = new Button { Content = "Save", Classes = { "accent" }, MinWidth = 90, IsDefault = true }; save.Click += async (s, e) => await SaveAsync();
            var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }; close.Click += (s, e) => Close();
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; right.Children.Add(close); right.Children.Add(save);
            DockPanel.SetDock(right, Dock.Right); buttons.Children.Add(right); buttons.Children.Add(_status);
            var dock = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons); dock.Children.Add(tabs);
            Content = dock;
            KeyDown += (s, e) => { bool cmd = OperatingSystem.IsMacOS() ? e.KeyModifiers.HasFlag(KeyModifiers.Meta) : e.KeyModifiers.HasFlag(KeyModifiers.Control); if (cmd && e.Key == Key.S) { _ = SaveAsync(); e.Handled = true; } else if (cmd && e.Key == Key.W) { Close(); e.Handled = true; } };
            Opened += async (s, e) => await LoadAsync();
        }

        private static StackPanel Page() => new StackPanel { Spacing = 4, Margin = new Thickness(14) };
        private static ScrollViewer Scroll(Control c) => new ScrollViewer { Content = c, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        /// <summary>Apply the saved appearance preference (System / Dark / Light) to the whole app.</summary>
        public static void ApplyTheme(string pref)
        {
            if (Application.Current == null) return;
            Application.Current.RequestedThemeVariant = pref == "Dark" ? ThemeVariant.Dark : pref == "Light" ? ThemeVariant.Light : ThemeVariant.Default;
        }

        private async Task LoadAsync()
        {
            var prefs = EditorPreferences.Current;
            _openLast.IsChecked = prefs.OpenLastProjectOnStart;
            _ide.SelectedIndex = prefs.ScriptIde == "code" ? 1 : prefs.ScriptIde == "rider" ? 2 : 0;
            _theme.SelectedIndex = prefs.Theme == "Dark" ? 1 : prefs.Theme == "Light" ? 2 : 0;
            if (_project == null) return;
            _name.Text = _project.Name ?? "";
            var st = _project.Settings;
            if (st != null) { _company.Text = st.CompanyName; _product.Text = st.ProductName; _width.Text = st.DefaultScreenWidth.ToString(); _height.Text = st.DefaultScreenHeight.ToString(); _fullscreen.IsChecked = st.FullscreenByDefault; _vsync.IsChecked = st.VSync; _fps.Text = st.TargetFPS.ToString(); _version.Text = st.ProductVersion; _bundleId.Text = st.BundleIdentifier; }
            ShowIcon(null);
            _bootScene.ItemsSource = _project.Scenes.Select(s => s.Name).ToList();
            int idx = 0;
            if (_project.StartSceneId.HasValue) for (int i = 0; i < _project.Scenes.Count; i++) if (_project.Scenes[i].Id == _project.StartSceneId.Value) idx = i;
            _bootScene.SelectedIndex = _project.Scenes.Count > 0 ? idx : -1;
            try
            {
                var repo = _project.Path;
                if (!await GitService.Instance.IsAvailableAsync()) { _branch.Text = "git is not installed"; _lfs.Text = "—"; return; }
                await GitService.Instance.EnsureRepoAsync(repo);
                _remote.Text = await GitService.Instance.GetRemoteUrlAsync(repo);
                _user.Text = await GitService.Instance.GetConfigAsync(repo, "user.name");
                _email.Text = await GitService.Instance.GetConfigAsync(repo, "user.email");
                _branch.Text = await GitService.Instance.CurrentBranchAsync(repo);
                _lfs.Text = await GitService.Instance.IsLfsAvailableAsync() ? "available" : "not installed";
            }
            catch (Exception ex) { _status.Text = "Git read failed: " + ex.Message; }
        }

        public async Task SaveAsync()
        {
            try
            {
                var prefs = EditorPreferences.Current;
                prefs.OpenLastProjectOnStart = _openLast.IsChecked == true;
                prefs.ScriptIde = _ide.SelectedIndex == 1 ? "code" : _ide.SelectedIndex == 2 ? "rider" : "";
                prefs.Theme = _theme.SelectedIndex == 1 ? "Dark" : _theme.SelectedIndex == 2 ? "Light" : "System";
                prefs.Save();
                ApplyTheme(prefs.Theme);
                if (_project == null) { _status.Text = "Saved."; return; }
                var n = (_name.Text ?? "").Trim(); if (!string.IsNullOrEmpty(n) && n != _project.Name) _project.Name = n;
                var st = _project.Settings ?? (_project.Settings = new ProjectSettings());
                st.CompanyName = _company.Text; st.ProductName = _product.Text;
                if (int.TryParse(_width.Text, out int w)) st.DefaultScreenWidth = Math.Max(320, w);
                if (int.TryParse(_height.Text, out int h)) st.DefaultScreenHeight = Math.Max(240, h);
                if (int.TryParse(_fps.Text, out int fps)) st.TargetFPS = Math.Max(0, Math.Min(1000, fps));
                st.FullscreenByDefault = _fullscreen.IsChecked == true; st.VSync = _vsync.IsChecked == true;
                st.ProductVersion = string.IsNullOrWhiteSpace(_version.Text) ? "1.0.0" : _version.Text.Trim();
                st.BundleIdentifier = (_bundleId.Text ?? "").Trim();
                ApplyIconChoice(st);
                if (_bootScene.SelectedIndex >= 0 && _bootScene.SelectedIndex < _project.Scenes.Count) _project.StartSceneId = _project.Scenes[_bootScene.SelectedIndex].Id;
                ProjectService.Instance.SaveProject(_project);
                var repo = _project.Path;
                if (await GitService.Instance.IsAvailableAsync())
                {
                    await GitService.Instance.SetRemoteUrlAsync(repo, (_remote.Text ?? "").Trim());
                    if (!string.IsNullOrWhiteSpace(_user.Text)) await GitService.Instance.SetConfigAsync(repo, "user.name", _user.Text.Trim());
                    if (!string.IsNullOrWhiteSpace(_email.Text)) await GitService.Instance.SetConfigAsync(repo, "user.email", _email.Text.Trim());
                }
                Title = "Project Settings — " + _project.Name;
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
