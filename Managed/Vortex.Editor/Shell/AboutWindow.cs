using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VortexEditor.Controls;

namespace VortexEditor.Shell
{
    /// <summary>About Vortex Engine (port of the WPF AboutDialog): logo, version, links, system info, update check.</summary>
    public sealed class AboutWindow : Window
    {
        private readonly TextBlock _update = new TextBlock { Classes = { "small", "secondary" }, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, IsVisible = false, MaxWidth = 360 };
        private readonly Button _check = new Button { Content = "Check for Updates" };

        public AboutWindow()
        {
            Title = "About Vortex Engine"; Width = 440; SizeToContent = SizeToContent.Height; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            string version = ""; try { version = Editor.Core.EngineInfo.VersionString; } catch { }
            string gpu = ""; try { gpu = Editor.DllWrapper.VortexAPI.GpuName(); } catch { }
            string repo = "https://github.com/" + Editor.Core.EngineInfo.RepoOwner + "/" + Editor.Core.EngineInfo.RepoName;
            var stack = new StackPanel { Spacing = 6, Margin = new Thickness(28, 28, 28, 22) };
            stack.Children.Add(new VxIcon { Icon = "Vortex", Width = 64, Height = 64, Foreground = (IBrush)Application.Current.FindResource("VxAccentBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = "Vortex Engine", Classes = { "large" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
            stack.Children.Add(new TextBlock { Text = "Version " + version, Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = "A modern, lightweight game engine — native C++20 core, .NET 10 editor and player.", Classes = { "small", "secondary" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 2), MaxWidth = 360 });

            var links = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4, Margin = new Thickness(0, 4, 0, 8) };
            links.Children.Add(Link("engine.vortexstudio.dev", "https://engine.vortexstudio.dev"));
            links.Children.Add(new TextBlock { Text = "·", Classes = { "tertiary" }, VerticalAlignment = VerticalAlignment.Center });
            links.Children.Add(Link("GitHub", repo));
            links.Children.Add(new TextBlock { Text = "·", Classes = { "tertiary" }, VerticalAlignment = VerticalAlignment.Center });
            links.Children.Add(Link("Release Notes", repo + "/releases"));
            stack.Children.Add(links);

            var sys = new StackPanel { Spacing = 3 };
            string renderer = OperatingSystem.IsMacOS() ? "Metal via SDL GPU" : OperatingSystem.IsWindows() ? "DirectX 12" : "Vulkan via SDL GPU";
            string os = OperatingSystem.IsMacOS() ? "macOS " + Environment.OSVersion.Version.ToString(2) : Environment.OSVersion.VersionString;
            sys.Children.Add(SysRow("Renderer", renderer + (string.IsNullOrEmpty(gpu) ? "" : "  ·  " + gpu)));
            sys.Children.Add(SysRow("Editor", ".NET " + Environment.Version.ToString(2) + " · Avalonia"));
            sys.Children.Add(SysRow("System", os + " · " + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture));
            sys.Children.Add(SysRow("License", "MIT — © " + DateTime.Now.Year + " Vortex Engine Team (shadow-kernel)"));
            stack.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(14, 10), Margin = new Thickness(0, 4, 0, 10), Child = sys });
            stack.Children.Add(_update);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            _check.Click += async (s, e) => await CheckUpdates();
            var docs = new Button { Content = "Documentation" }; docs.Click += (s, e) => EditorCommands.Documentation();
            var ok = new Button { Content = "Close", Classes = { "accent" }, MinWidth = 90, IsDefault = true, IsCancel = true }; ok.Click += (s, e) => Close();
            row.Children.Add(_check); row.Children.Add(docs); row.Children.Add(ok);
            stack.Children.Add(row);
            Content = stack;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private async System.Threading.Tasks.Task CheckUpdates()
        {
            _check.IsEnabled = false;
            _update.IsVisible = true;
            _update.Text = "Checking for updates…";
            try
            {
                var r = await EditorCommands.LatestRelease();
                if (r.tag == null) _update.Text = "Update check failed — please try again later.";
                else if (EditorCommands.IsNewer(r.tag, Editor.Core.EngineInfo.VersionString))
                {
                    _update.Text = "Update found: " + r.tag + " — opening the release page.";
                    EditorCommands.OpenUrl(r.url ?? "https://github.com/" + Editor.Core.EngineInfo.RepoOwner + "/" + Editor.Core.EngineInfo.RepoName + "/releases");
                }
                else _update.Text = "You're up to date — Vortex " + Editor.Core.EngineInfo.VersionString + " (latest release " + r.tag + ") ✓";
            }
            finally { _check.IsEnabled = true; }
        }

        private static Control Link(string text, string url)
        {
            var b = new Button { Content = text, Classes = { "link" }, Padding = new Thickness(2, 0) };
            ToolTip.SetTip(b, url);
            b.Click += (s, e) => EditorCommands.OpenUrl(url);
            return b;
        }

        private static Control SysRow(string label, string value)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("76,*") };
            g.Children.Add(new TextBlock { Text = label, Classes = { "small", "tertiary" } });
            var v = new TextBlock { Text = value, Classes = { "small" }, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(v, 1); g.Children.Add(v);
            return g;
        }
    }
}
