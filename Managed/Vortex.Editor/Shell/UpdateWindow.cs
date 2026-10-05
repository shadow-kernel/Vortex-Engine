using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Editing;
using Editor.Core.Services.Update;
using VortexEditor.Shell.Library;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Shell
{
    /// <summary>
    /// A newer Vortex on GitHub (Windows installs): what changed since this version, then download the installer and let
    /// it update the editor and start it again — the cross-platform editor's side of the shared <see cref="UpdateService"/>
    /// (the WPF editor has its own dialog). A patch release installs right away, as in the WPF editor.
    /// </summary>
    public sealed class UpdateWindow : Window
    {
        private readonly UpdateInfo _info;
        private readonly ProgressBar _bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        private readonly Button _install, _later;

        /// <summary>Wire the updater to this host (Windows): end the editor the way its own shutdown does.</summary>
        public static void InstallHooks()
        {
            if (!OperatingSystem.IsWindows()) return;
            UpdateService.BeforeExit = () =>
            {
                try { _ = Claude.McpHost.StopAsync(); } catch { }
                try { EditorSession.Instance.ShutdownEngine(); } catch { }
            };
            UpdateService.ExitProcess = () => VortexEditor.Program.EndProcessNow(0);
        }

        public static void Show(UpdateInfo info, bool installNow)
        {
            var w = new UpdateWindow(info);
            var owner = EditorCommands.Window;
            if (owner != null) w.Show(owner); else w.Show();
            if (installNow) _ = w.InstallAsync();
        }

        /// <summary>On start (installed Windows builds): look for a newer release in the background.</summary>
        public static async Task CheckAtStartupAsync()
        {
            try
            {
                if (!OperatingSystem.IsWindows() || !UpdateService.IsInstalledBuild()) return;
                await Task.Delay(TimeSpan.FromSeconds(3));
                var info = await UpdateService.CheckAsync();
                if (info == null || info.Bump == BumpType.None) return;
                Dispatcher.UIThread.Post(() => Show(info, installNow: info.Bump == BumpType.Patch));
            }
            catch { /* an update check never gets in the way */ }
        }

        private UpdateWindow(UpdateInfo info)
        {
            _info = info;
            Title = "Update Available";
            Width = 640; Height = 600; MinWidth = 480; MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Spacing = 10 };
            stack.Children.Add(LibraryUi.Title("Vortex " + Editor.Core.EngineInfo.VersionString + "  →  " + info.Latest.ToString(3)));
            stack.Children.Add(LibraryUi.Para(info.Bump == BumpType.Major
                ? "A major version. Vortex closes, installs the update and opens again; open your projects once afterwards — the editor upgrades them when needed and keeps a backup."
                : "Vortex closes, installs the update and opens again. Open work is saved first."));
            stack.Children.Add(Ui.Header("What's new"));
            stack.Children.Add(new SelectableTextBlock { Text = PlainNotes(info.Notes), TextWrapping = TextWrapping.Wrap, FontSize = 12 });

            _install = Ui.Button("Install and Restart", () => _ = InstallAsync(), null, "accent", 160);
            _later = Ui.Button("Later", Close, null, null, 84);
            var footerLeft = new StackPanel { Spacing = 6, Width = 300, VerticalAlignment = VerticalAlignment.Center, Children = { _status, _bar } };
            var footer = LibraryUi.Footer(footerLeft, _later, _install);
            Content = LibraryUi.Layout(stack, footer);
        }

        private async Task InstallAsync()
        {
            _install.IsEnabled = false;
            _later.IsEnabled = false;
            // the editor closes for the install: keep the user's work
            try { EditorCommands.SaveAll(); } catch { }
            _bar.IsVisible = true;
            _bar.Value = 0;
            _status.Text = "Downloading " + (_info.SetupName ?? "the update") + "…";
            string path = await UpdateService.DownloadAsync(_info, new Progress<double>(v => Dispatcher.UIThread.Post(() => _bar.Value = v)));
            if (path == null)
            {
                _status.Text = "The download failed. Try again later, or get the installer from the release page.";
                _install.IsEnabled = _later.IsEnabled = true;
                _bar.IsVisible = false;
                return;
            }
            _status.Text = "Installing — Vortex starts again in a moment…";
            await Task.Delay(150);   // let the status paint before the process ends
            UpdateService.InstallAndRestart(path);   // ends the process when the installer took over
            _status.Text = "The installer could not be started. You stay on this version; the release page has the installer.";
            _install.IsEnabled = _later.IsEnabled = true;
            _bar.IsVisible = false;
        }

        // ------------------------------------------------------------------ smoke check "update window"

        [ModuleInitializer]
        internal static void RegisterSmoke() => SmokeRegistry.Add("update window", SmokeAsync);

        private static async Task<bool> SmokeAsync()
        {
            var cur = Editor.Core.EngineInfo.Version;
            var info = new UpdateInfo
            {
                Latest = new Version(cur.Major, cur.Minor, cur.Build + 1), Tag = "v-smoke", Bump = BumpType.Patch, SetupName = "VortexEngine-Setup.exe",
                Notes = "## Fixes\n- **Audio** follows its parents\n- `Scene.Destroy` stops sounds",
            };
            var w = new UpdateWindow(info);
            w.Show(EditorCommands.Window);
            await SmokeRegistry.Settle(400);
            SmokeRegistry.Capture(w, "update_window.png");
            string notes = PlainNotes(info.Notes);
            bool ok = notes.Contains("FIXES") && notes.Contains("•  Audio follows its parents") && !notes.Contains("**") && !notes.Contains("`") && w._install.IsEnabled;
            w.Close();
            if (!ok) Editor.Core.Services.ConsoleService.Instance.LogError("update window: notes rendered as\n" + notes);
            return ok;
        }

        /// <summary>Release notes (markdown) as plain lines: headings, bullets and emphasis without their marks.</summary>
        internal static string PlainNotes(string md)
        {
            if (string.IsNullOrWhiteSpace(md)) return "See the release page for details.";
            var sb = new StringBuilder();
            foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                string t = line.TrimStart();
                if (t.StartsWith("#")) { sb.AppendLine(); sb.AppendLine(t.TrimStart('#', ' ').ToUpperInvariant()); continue; }
                if (t.StartsWith("- ") || t.StartsWith("* ")) line = new string(' ', line.Length - t.Length) + "•  " + t.Substring(2);
                line = line.Replace("**", "").Replace("`", "");
                sb.AppendLine(line);
            }
            return sb.ToString().Trim();
        }
    }
}
