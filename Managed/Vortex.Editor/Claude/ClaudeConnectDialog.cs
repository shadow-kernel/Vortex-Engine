using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Claude;
using Editor.Core.Data;
using VortexEditor.Shell;
using VortexEditor.Shell.Library;
using VortexEditor.Shell.Material;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Tools ▸ Claude ▸ Connect Claude… (#93): the MCP server's state, port and on/off switch, the one command that
    /// registers it with Claude Code (live port, copy button), "Write .mcp.json" for the open project, the Claude
    /// Desktop setup, and the list of tools.
    /// </summary>
    public sealed class ClaudeConnectDialog : Window
    {
        public static async Task Run() => await LibraryUi.ShowModal(new ClaudeConnectDialog());

        private readonly Ellipse _dot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _state = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        private readonly Button _toggle;
        private readonly TextBox _port = new TextBox { Width = 76, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBox _command = Code();
        private readonly TextBox _desktop = Code();
        private readonly TextBlock _mcpJsonNote = LibraryUi.Small("");
        private readonly Button _mcpJson;

        private ClaudeConnectDialog()
        {
            Title = "Connect Claude";
            Width = 640; Height = 720; MinHeight = 420; MinWidth = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

            var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Spacing = 8 };
            stack.Children.Add(LibraryUi.Title("Connect Claude"));
            stack.Children.Add(LibraryUi.Para("Claude Code and Claude Desktop can operate this editor through the Vortex MCP server: build and edit scenes, " +
                                              "set up lights, materials and components, look at the viewport, run the game and read the console. " +
                                              "Every change Claude makes is one undo step (\"Claude: …\") — Ctrl/⌘+Z takes it back."));

            // ---- server
            stack.Children.Add(Ui.Header("MCP server"));
            _toggle = Ui.Button("Start", () => _ = Toggle(), null, "accent", 84);
            _port.Text = McpHost.ConfiguredPort.ToString(CultureInfo.InvariantCulture);
            ToolTip.SetTip(_port, "Port on 127.0.0.1 (default " + McpHost.DefaultPort + "). Changing it means re-registering the server in Claude.");
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 8 };
            row.Children.Add(_dot);
            Grid.SetColumn(_state, 1); row.Children.Add(_state);
            var portLabel = new TextBlock { Text = "Port", VerticalAlignment = VerticalAlignment.Center, Classes = { "secondary" } };
            Grid.SetColumn(portLabel, 2); row.Children.Add(portLabel);
            Grid.SetColumn(_port, 3); row.Children.Add(_port);
            Grid.SetColumn(_toggle, 4); row.Children.Add(_toggle);
            stack.Children.Add(row);
            stack.Children.Add(LibraryUi.Small("Listens on 127.0.0.1 only: programs on this computer can connect, web pages and other machines cannot. " +
                                               "The server starts with the editor once you turned it on."));

            // ---- Claude Code
            stack.Children.Add(Ui.Header("Claude Code"));
            stack.Children.Add(LibraryUi.Para("Run this once in a terminal — then ask Claude to work in the editor (\"build a corridor with flickering lights\"):"));
            stack.Children.Add(WithCopy(_command));
            _mcpJson = Ui.Button("Write .mcp.json to Project", WriteMcpJson, "Adds the vortex server to <project>/.mcp.json (other servers in the file are kept) — " +
                                 "Claude Code started in the project folder offers it automatically; commit it to share it with your team.");
            stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _mcpJson, _mcpJsonNote } });
            _mcpJsonNote.VerticalAlignment = VerticalAlignment.Center;

            // ---- Claude Desktop
            stack.Children.Add(Ui.Header("Claude Desktop"));
            stack.Children.Add(LibraryUi.Para(DesktopHelp));
            stack.Children.Add(WithCopy(_desktop));

            // ---- tools
            var tools = ToolCatalog.All;
            stack.Children.Add(Ui.Header(tools.Count + " tools"));
            foreach (var g in tools.GroupBy(t => t.Category))
                stack.Children.Add(new SelectableTextBlock
                {
                    Text = g.Key + ": " + string.Join(", ", g.Select(t => t.Name)),
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = EditorKit.Brush("VxTextSecondaryBrush"),
                });

            var docs = Ui.Button("Documentation", () => EditorCommands.OpenUrl("https://github.com/shadow-kernel/Vortex-Engine/wiki/Claude-Integration"));
            var close = Ui.Button("Close", Close, null, null, 84);
            Content = LibraryUi.Layout(stack, LibraryUi.Footer(docs, close));

            McpHost.StatusChanged += Sync;
            Closed += (s, e) => McpHost.StatusChanged -= Sync;
            Sync();
        }

        internal const string DesktopHelp =
            "Claude Desktop starts local MCP servers from its config file (Settings ▸ Developer ▸ Edit Config). Add this entry " +
            "(it bridges to the editor with the mcp-remote package and needs Node.js), save, and restart Claude Desktop:";

        private void Sync()
        {
            int port = McpHost.IsRunning ? McpHost.Port : McpHost.ConfiguredPort;
            string url = "http://127.0.0.1:" + port + McpHost.Path;
            switch (McpHost.State)
            {
                case McpServerState.Running:
                    _dot.Fill = new SolidColorBrush(Color.FromRgb(0x3f, 0xb9, 0x50));
                    _state.Text = "Running — " + McpHost.Url + (McpHost.Calls > 0 ? "  (" + McpHost.Calls + " calls)" : "");
                    _toggle.Content = "Stop";
                    break;
                case McpServerState.Starting:
                    _dot.Fill = new SolidColorBrush(Color.FromRgb(0xd8, 0xa1, 0x2b));
                    _state.Text = "Starting…";
                    _toggle.Content = "Stop";
                    break;
                case McpServerState.Failed:
                    _dot.Fill = new SolidColorBrush(Color.FromRgb(0xe5, 0x48, 0x4d));
                    _state.Text = "Not running — " + McpHost.LastError;
                    _toggle.Content = "Start";
                    break;
                default:
                    _dot.Fill = EditorKit.Brush("VxTextTertiaryBrush");
                    _state.Text = "Off — start it to let Claude connect";
                    _toggle.Content = "Start";
                    break;
            }
            _command.Text = "claude mcp add --transport http vortex " + url;
            _desktop.Text = "\"vortex\": {\n  \"command\": \"npx\",\n  \"args\": [\"-y\", \"mcp-remote\", \"" + url + "\"]\n}";
            var project = ProjectData.Current;
            _mcpJson.IsEnabled = project != null;
            if (project == null) _mcpJsonNote.Text = "Open a project first.";
            else if (McpProjectConfig.IsConfigured(project.Path, "vortex", url)) _mcpJsonNote.Text = "✓ .mcp.json points at this server.";
            else if (_mcpJsonNote.Text.StartsWith("✓", StringComparison.Ordinal)) _mcpJsonNote.Text = "";
        }

        private async Task Toggle()
        {
            if (McpHost.IsRunning || McpHost.State == McpServerState.Starting) { await McpHost.SetEnabledAsync(false); return; }
            if (!int.TryParse(_port.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port < 1024 || port > 65535)
            {
                _state.Text = "Pick a port between 1024 and 65535.";
                return;
            }
            await McpHost.SetEnabledAsync(true, port);
            Sync();
        }

        private void WriteMcpJson()
        {
            var project = ProjectData.Current;
            if (project == null) return;
            int port = McpHost.IsRunning ? McpHost.Port : McpHost.ConfiguredPort;
            try
            {
                var outcome = McpProjectConfig.Write(project.Path, "vortex", "http://127.0.0.1:" + port + McpHost.Path);
                _mcpJsonNote.Text = outcome == McpProjectConfig.Outcome.Created ? "✓ Created .mcp.json in the project."
                                  : outcome == McpProjectConfig.Outcome.Updated ? "✓ Updated .mcp.json (other servers kept)."
                                  : "✓ .mcp.json already points at this server.";
            }
            catch (Exception ex) { _mcpJsonNote.Text = ex.Message; }
        }

        private static TextBox Code()
        {
            var box = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            if (Application.Current != null && Application.Current.TryFindResource("VxMono", out var mono) && mono is FontFamily ff) box.FontFamily = ff;
            return box;
        }

        private Control WithCopy(TextBox box)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            g.Children.Add(box);
            var copy = Ui.Button("Copy", async () =>
            {
                try { var cb = TopLevel.GetTopLevel(this)?.Clipboard; if (cb != null) await cb.SetTextAsync(box.Text); EditorCommands.Toast("Copied"); } catch { }
            });
            copy.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(copy, 1);
            g.Children.Add(copy);
            return g;
        }
    }
}
