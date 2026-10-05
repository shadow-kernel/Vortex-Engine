using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Services.Build;
using VortexEditor.Build;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Build (export) the game for a target platform: macOS (.app / .dmg), Windows (.exe folder / .zip) or Linux
    /// (folder / .zip), branded with the project's name, version, bundle identifier and icon (Project Settings).
    /// Each platform needs a runtime pack; the one for this machine is the editor's own installation, others are
    /// built on their platform (tools/make-runtime-pack) and installed here.
    /// </summary>
    public sealed class BuildWindow : Window
    {
        private sealed class PlatformRow { public ExportPlatform Platform; public RadioButton Radio; public TextBlock Status; public Button Install; public RuntimePack Pack; }

        private readonly List<PlatformRow> _rows = new List<PlatformRow>();
        private readonly RadioButton _release = new RadioButton { Content = "Release — packed assets, compiled scripts, ready to ship", IsChecked = true };
        private readonly RadioButton _debug = new RadioButton { Content = "Debug — references this project on this machine, scripts and shaders hot-reload" };
        private readonly TextBox _out = new TextBox();
        private readonly CheckBox _run = new CheckBox { Content = "Run after build" }, _reveal = new CheckBox { Content = "Reveal in Finder", IsChecked = true }, _archive = new CheckBox { Content = "Create disk image (.dmg)" };
        private readonly ProgressBar _bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6 };
        private readonly TextBlock _step = new TextBlock { Classes = { "small", "secondary" } };
        private readonly TextBlock _brand = new TextBlock { Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap };
        private readonly Image _iconPreview = new Image { Width = 40, Height = 40 };
        private readonly TextBox _log = new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 150, FontFamily = (FontFamily)Application.Current.FindResource("VxMono"), FontSize = 11 };
        private Button _build;
        private bool _running;

        public BuildWindow(bool runAfter)
        {
            Title = "Build Game"; Width = 660; SizeToContent = SizeToContent.Height; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            _run.IsChecked = runAfter;
            var p = ProjectData.Current;
            _out.Text = Path.Combine(Path.GetDirectoryName(p?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "", (p?.Name ?? "Game") + "-Build");

            var stack = new StackPanel { Spacing = 8, Margin = new Thickness(18, 16) };
            var head = new DockPanel();
            var settingsBtn = new Button { Content = "Project Settings…", Classes = { "ghost" } };
            settingsBtn.Click += (s, e) => { EditorCommands.ProjectSettings(); RefreshBranding(); };
            DockPanel.SetDock(settingsBtn, Dock.Right);
            head.Children.Add(settingsBtn);
            var headText = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            headText.Children.Add(_iconPreview);
            var titles = new StackPanel();
            titles.Children.Add(new TextBlock { Text = "Build " + (p?.Name ?? "Game"), Classes = { "title" } });
            titles.Children.Add(_brand);
            headText.Children.Add(titles);
            head.Children.Add(headText);
            stack.Children.Add(head);

            stack.Children.Add(new TextBlock { Text = "Target platform", Classes = { "label", "small" }, Margin = new Thickness(0, 6, 0, 0) });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            int i = 0;
            foreach (var plat in ExportPlatforms.All())
            {
                var row = new PlatformRow { Platform = plat };
                row.Radio = new RadioButton { Content = ExportPlatforms.DisplayName(plat), GroupName = "platform", IsChecked = plat == ExportPlatforms.Host };
                row.Radio.IsCheckedChanged += (s, e) => { if (row.Radio.IsChecked == true) OnPlatformChanged(); };
                row.Status = new TextBlock { Text = "checking…", Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(26, 0, 0, 0) };
                row.Install = new Button { Content = "Install runtime pack…", Classes = { "ghost" }, IsVisible = false, Margin = new Thickness(26, 2, 0, 0) };
                row.Install.Click += async (s, e) => await InstallPack(row);
                var cell = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 8, 6) };
                cell.Children.Add(row.Radio); cell.Children.Add(row.Status); cell.Children.Add(row.Install);
                Grid.SetColumn(cell, i % 2); Grid.SetRow(cell, i / 2);
                grid.Children.Add(cell);
                _rows.Add(row);
                i++;
            }
            stack.Children.Add(grid);

            stack.Children.Add(new TextBlock { Text = "Configuration", Classes = { "label", "small" }, Margin = new Thickness(0, 6, 0, 0) });
            stack.Children.Add(_release); stack.Children.Add(_debug);
            stack.Children.Add(new TextBlock { Text = "Output folder", Classes = { "label", "small" }, Margin = new Thickness(0, 6, 0, 0) });
            var row2 = new DockPanel();
            var browse = new Button { Content = "Choose…", Margin = new Thickness(6, 0, 0, 0) };
            browse.Click += async (s, e) => { var f = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Build output folder" }); var d = f.Count > 0 ? f[0].TryGetLocalPath() : null; if (d != null) _out.Text = Path.Combine(d, (p?.Name ?? "Game") + "-Build"); };
            DockPanel.SetDock(browse, Dock.Right); row2.Children.Add(browse); row2.Children.Add(_out);
            stack.Children.Add(row2);
            var opts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 }; opts.Children.Add(_run); opts.Children.Add(_reveal); opts.Children.Add(_archive);
            stack.Children.Add(opts);
            stack.Children.Add(_bar); stack.Children.Add(_step); stack.Children.Add(_log);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var close = new Button { Content = "Close", MinWidth = 90 }; close.Click += (s, e) => { if (!_running) Close(); };
            _build = new Button { Content = "Build", Classes = { "accent" }, MinWidth = 90, IsDefault = true }; _build.Click += async (s, e) => await Build();
            buttons.Children.Add(close); buttons.Children.Add(_build);
            stack.Children.Add(buttons);
            Content = stack;
            RefreshBranding();
            Opened += async (s, e) => await RefreshPacks();
        }

        private ExportPlatform SelectedPlatform { get { foreach (var r in _rows) if (r.Radio.IsChecked == true) return r.Platform; return ExportPlatforms.Host; } }

        private void RefreshBranding()
        {
            var p = ProjectData.Current;
            var req = new ExportRequest { ProjectRoot = p?.Path, ProjectName = p?.Name, Settings = p?.Settings };
            _brand.Text = req.ProductName + " " + req.Version + "  ·  " + req.BundleIdentifier + "  ·  by " + req.CompanyName;
            try
            {
                byte[] src = IconFactory.LoadSource(p, out _);
                using var ms = new MemoryStream(src);
                _iconPreview.Source = new Avalonia.Media.Imaging.Bitmap(ms);
            }
            catch { _iconPreview.Source = null; }
        }

        private async Task RefreshPacks()
        {
            var packs = await Task.Run(() => { var d = new Dictionary<ExportPlatform, RuntimePack>(); foreach (var pl in ExportPlatforms.All()) d[pl] = RuntimePacks.Locate(pl); return d; });
            foreach (var r in _rows)
            {
                r.Pack = packs[r.Platform];
                if (r.Pack.IsComplete) { r.Status.Text = "Runtime: " + (r.Platform == ExportPlatforms.Host && r.Pack.Source == "this installation" ? "this installation" : r.Pack.Source); r.Install.IsVisible = false; }
                else
                {
                    r.Status.Text = r.Platform == ExportPlatforms.Host ? "Runtime incomplete: " + string.Join("; ", r.Pack.Problems) : "Runtime pack not installed (build it on " + ExportPlatforms.DisplayName(r.Platform) + " with tools/make-runtime-pack, then install it here)";
                    r.Install.IsVisible = true;
                }
            }
            OnPlatformChanged();
        }

        private void OnPlatformChanged()
        {
            var plat = SelectedPlatform;
            bool host = plat == ExportPlatforms.Host;
            _debug.IsEnabled = host; if (!host) _release.IsChecked = true;
            _run.IsEnabled = host; if (!host) _run.IsChecked = false;
            _archive.Content = ExportPlatforms.IsMac(plat) ? "Create disk image (.dmg)" : "Create .zip archive";
            _reveal.Content = OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Show in folder";
        }

        private async Task InstallPack(PlatformRow row)
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Runtime pack (.zip) for " + ExportPlatforms.DisplayName(row.Platform), AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("Runtime pack") { Patterns = new[] { "*.zip" } } } });
            string path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
            if (path == null)
            {
                var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Runtime pack folder for " + ExportPlatforms.DisplayName(row.Platform) });
                path = folder.Count > 0 ? folder[0].TryGetLocalPath() : null;
            }
            if (path == null) return;
            try
            {
                var pack = await Task.Run(() => RuntimePacks.Install(row.Platform, path));
                _log.Text = pack.IsComplete ? "Runtime pack installed: " + pack.Source : "Installed, but incomplete: " + string.Join("; ", pack.Problems);
            }
            catch (Exception ex) { _log.Text = "Install failed: " + ex.Message; }
            await RefreshPacks();
        }

        private async Task Build()
        {
            var p = ProjectData.Current; if (p == null || _running) return;
            string outDir = (_out.Text ?? "").Trim(); if (string.IsNullOrEmpty(outDir)) return;
            // license check (#77): NonCommercial / NoDerivatives / ShareAlike / unknown assets need a decision first
            var audit = await Task.Run(() => Editor.Core.Assets.Library.LicenseAudit.Scan(p.Path));
            if (audit.HasWarnings && !await Dialogs.Confirm("License check",
                    "Some assets in this build have licenses that need your attention:\n\n" + Editor.Core.Assets.Library.LicenseAudit.Warnings(audit) +
                    "\n\nCC-BY assets are credited automatically in CREDITS.md.", "Build Anyway", "Cancel"))
                return;
            _running = true; _build.IsEnabled = false; _log.Text = ""; _bar.Value = 0;
            var req = new ExportRequest
            {
                ProjectRoot = p.Path, ProjectName = p.Name, Settings = p.Settings, OutputDir = outDir,
                Platform = SelectedPlatform, Debug = _debug.IsChecked == true, CreateArchive = _archive.IsChecked == true,
            };
            try
            {
                Editor.Core.Editing.EditorSession.Instance.SaveAll();
                _step.Text = "Preparing icons…";
                var icons = await Task.Run(() => IconFactory.Build(p, Path.Combine(Path.GetTempPath(), "vortex-build-icons-" + Guid.NewGuid().ToString("N"))));
                req.IcnsPath = icons.IcnsPath; req.IcoPath = icons.IcoPath; req.PngIconPath = icons.PngPath;
                var result = await Task.Run(() => GamePackager.Export(req, (f, s) => Dispatcher.UIThread.Post(() => { _bar.Value = f; _step.Text = s; })));
                string text = result.Message;
                if (icons.Warnings.Count > 0) text += "\n• Icon warnings: " + string.Join("; ", icons.Warnings);
                text += "\n• Icon source: " + icons.SourceDescription;
                _log.Text = text;
                _step.Text = result.Success ? "Done." : "Build failed.";
                if (result.Success)
                {
                    EditorCommands.Toast("Build finished");
                    if (_reveal.IsChecked == true) EditorCommands.RevealInFinder(result.OutputPath);
                    if (_run.IsChecked == true && req.Platform == ExportPlatforms.Host) { try { RunBuilt(result.OutputPath); } catch (Exception ex) { EditorCommands.Fail("Run", ex); } }
                }
            }
            catch (Exception ex) { _log.Text = ex.ToString(); _step.Text = "Build failed."; }
            finally { _running = false; _build.IsEnabled = true; }
        }

        private static void RunBuilt(string outputPath)
        {
            if (OperatingSystem.IsMacOS()) { Process.Start(new ProcessStartInfo("open", "\"" + outputPath + "\"") { UseShellExecute = false }); return; }
            string exe = null;
            foreach (var f in Directory.GetFiles(outputPath)) { string n = Path.GetFileName(f); if ((OperatingSystem.IsWindows() && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("Vortex.")) || (!OperatingSystem.IsWindows() && n == "run.sh")) { exe = f; break; } }
            if (exe != null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = outputPath });
        }
    }
}
