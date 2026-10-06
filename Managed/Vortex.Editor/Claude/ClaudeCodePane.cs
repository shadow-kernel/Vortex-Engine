using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Claude;
using Editor.Core.Claude.Terminal;
using Editor.Core.Data;
using VortexEditor.Controls;
using VortexEditor.Shell;
using VortexEditor.Shell.Material;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Claude Code itself inside the sidebar: the user's own installed Claude Code (<c>claude</c>), unmodified, in a terminal
    /// on the open project, connected to this editor's MCP server. Claude Code signs in by itself — with a Claude Pro or
    /// Max plan, too: the editor never sees or uses those credentials (Anthropic allows a plan's sign-in only in Claude
    /// Code and claude.ai), it only hosts the terminal, as Terminal or VS Code would.
    /// </summary>
    internal sealed class ClaudeCodePane : UserControl
    {
        private readonly TerminalView _terminal = new TerminalView();
        private readonly Border _bar = new Border { Padding = new Thickness(10, 6), IsVisible = false };
        private readonly TextBlock _barText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _barButton = new Button { Content = "Start Claude Code", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        private readonly ContentControl _body = new ContentControl();
        private string _startedFor;

        /// <summary>The terminal (tests).</summary>
        internal TerminalView Terminal => _terminal;

        public ClaudeCodePane()
        {
            _terminal.TerminalBackground = EditorKit.Brush("VxFieldBrush");
            _terminal.Exited += code => ShowBar("Claude Code ended" + (code != 0 ? " (exit code " + code + ")" : "") + ".", "Start again");
            var barRow = new DockPanel();
            DockPanel.SetDock(_barButton, Dock.Right);
            barRow.Children.Add(_barButton);
            barRow.Children.Add(_barText);
            _bar.Child = barRow;
            _bar.Background = EditorKit.Brush("VxToolbarBrush");
            _barButton.Click += (s, e) => { _startedFor = null; _ = StartAsync(); };
            DockPanel.SetDock(_bar, Dock.Top);
            var root = new DockPanel();
            root.Children.Add(_bar);
            root.Children.Add(_body);
            _body.Content = _terminal;
            Content = root;
        }

        /// <summary>The pane is showing: start Claude Code on the open project (once per project), then focus it.</summary>
        public async Task ShowAsync()
        {
            string project = ProjectData.Current?.Path;
            if (!_terminal.IsRunning || !string.Equals(_startedFor, project, StringComparison.Ordinal)) await StartAsync();
            _terminal.Focus();
        }

        /// <summary>End Claude Code (the editor closes, or the user starts over).</summary>
        public void Stop()
        {
            _terminal.Stop();
            _startedFor = null;
        }

        public void Restart()
        {
            Stop();
            _ = StartAsync();
        }

        private async Task StartAsync()
        {
            var project = ProjectData.Current;
            if (project == null) { ShowMessage("Open a project first — Claude Code works on the open project.", null); return; }
            string claude = ClaudeCode.FindCli();
            if (claude == null) { ShowInstall(); return; }
            if (!McpHost.IsRunning && !await McpHost.SetEnabledAsync(true))
            {
                ShowMessage("The editor's MCP server did not start (" + McpHost.LastError + ") — Claude Code would not reach the editor.", "Try again");
                return;
            }
            _body.Content = _terminal;
            _bar.IsVisible = false;
            var mcp = JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["vortex"] = new { type = "http", url = McpHost.Url } } });
            var args = new List<string> { "--mcp-config=" + mcp };
            string file = claude;
            if (OperatingSystem.IsWindows() && (claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || claude.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            {
                // an npm install: a batch file runs through cmd
                args.InsertRange(0, new[] { "/c", claude });
                file = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            }
            try
            {
                string path = await Task.Run(ProcessTools.UserPath);   // asks the login shell once (up to a few seconds)
                var env = Pty.TerminalEnvironment(new Dictionary<string, string> { ["PATH"] = path });
                _terminal.Start(file, args, project.Path, env);
                _startedFor = project.Path;
            }
            catch (Exception ex)
            {
                ShowMessage("Claude Code did not start: " + ex.Message, "Try again");
            }
        }

        private void ShowBar(string text, string button)
        {
            _barText.Text = text;
            _barButton.Content = button;
            _barButton.IsVisible = button != null;
            _bar.IsVisible = true;
        }

        private void ShowMessage(string text, string button)
        {
            var sp = new StackPanel { Spacing = 10, Margin = new Thickness(18, 20, 18, 10) };
            sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = EditorKit.Brush("VxTextSecondaryBrush") });
            if (button != null)
            {
                var b = new Button { Content = button, HorizontalAlignment = HorizontalAlignment.Left };
                b.Click += (s, e) => _ = StartAsync();
                sp.Children.Add(b);
            }
            _body.Content = sp;
            _bar.IsVisible = false;
        }

        private void ShowInstall()
        {
            var sp = new StackPanel { Spacing = 10, Margin = new Thickness(18, 20, 18, 10) };
            sp.Children.Add(new TextBlock { Text = "Install Claude Code", FontSize = 15, FontWeight = FontWeight.SemiBold });
            sp.Children.Add(new TextBlock
            {
                Text = "Claude Code runs here with your own Claude plan (Pro or Max) or Console account — it signs in by itself the first time. Install it once:",
                TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = EditorKit.Brush("VxTextSecondaryBrush"),
            });
            string cmd = OperatingSystem.IsWindows() ? "irm https://claude.ai/install.ps1 | iex" : "curl -fsSL https://claude.ai/install.sh | bash";
            var box = new TextBox { Text = cmd, IsReadOnly = true, FontSize = 12 };
            if (Application.Current != null && Application.Current.TryFindResource("VxMono", out var mono) && mono is FontFamily ff) box.FontFamily = ff;
            var copy = new Button { Content = "Copy" };
            copy.Click += async (s, e) => { try { var clip = TopLevel.GetTopLevel(this)?.Clipboard; if (clip != null) await clip.SetTextAsync(cmd); copy.Content = "Copied"; } catch { } };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
            row.Children.Add(box);
            Grid.SetColumn(copy, 1);
            row.Children.Add(copy);
            sp.Children.Add(row);
            sp.Children.Add(new TextBlock
            {
                Text = (OperatingSystem.IsWindows() ? "Run it in PowerShell" : "Run it in Terminal") + ", then press Check again.",
                TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = EditorKit.Brush("VxTextTertiaryBrush"),
            });
            var again = new Button { Content = "Check again", HorizontalAlignment = HorizontalAlignment.Left };
            again.Click += (s, e) => _ = StartAsync();
            sp.Children.Add(again);
            var docs = new Button { Classes = { "link" }, Content = "Claude Code setup guide", HorizontalAlignment = HorizontalAlignment.Left, FontSize = 12 };
            docs.Click += (s, e) => EditorCommands.OpenUrl(ClaudeCode.InstallUrl);
            sp.Children.Add(docs);
            _body.Content = new ScrollViewer { Content = sp };
            _bar.IsVisible = false;
        }
    }
}
