using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Claude;
using Editor.Core.Data;
using Editor.Core.Services;
using VortexEditor.Shell;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Editor smoke check "claude code": the sidebar's Claude Code mode runs a program in its terminal — a stand-in for
    /// <c>claude</c> (CI has no Claude sign-in) that draws a box, prints the arguments it got and echoes a typed line. The
    /// check sees the box on the terminal's screen, the editor's MCP server in the arguments (<c>--mcp-config</c>), the
    /// echoed line, and that the program runs in the project folder. With <c>VORTEX_SMOKE_REAL_CLAUDE=1</c> it then starts
    /// the real Claude Code, isolated (an empty <c>CLAUDE_CONFIG_DIR</c>: the user's own sign-in and settings are never read
    /// or written), and checks that its first screen renders. Captures claude_code.png (and claude_code_real.png).
    /// </summary>
    internal static class ClaudeCodeSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("claude code", RunAsync);

        public static async Task<bool> RunAsync()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("claude code: " + why); return false; }
            var window = EditorCommands.Window;
            var panel = ClaudePanel.Current;
            var project = ProjectData.Current;
            if (window == null || panel == null || project == null) return Fail("no editor, sidebar or project");
            var settings = ClaudePanelSettings.Current;
            string oldBackend = settings.Backend;
            bool wasOpen = window.IsPanelVisible(MainWindow.PanelClaude);
            string oldCli = Environment.GetEnvironmentVariable("VORTEX_CLAUDE_CLI");
            string oldConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            string dir = Path.Combine(Path.GetTempPath(), "vortex-smoke-claude-code-" + Environment.ProcessId);
            try
            {
                Directory.CreateDirectory(dir);
                string fake = WriteFake(dir);
                Environment.SetEnvironmentVariable("VORTEX_CLAUDE_CLI", fake);
                window.ShowPanel(MainWindow.PanelClaude);
                panel.SetBackend(true);
                var term = panel.CodePane.Terminal;
                if (!await WaitFor(() => term.Screen.ScreenText().Contains("FAKE CLAUDE READY"), 15000))
                    return Fail("the stand-in did not start: " + Short(term.Screen.ScreenText()));
                await SmokeRegistry.Settle(500);
                string screen = term.Screen.ScreenText();
                string flat = screen.Replace("\n", "");   // long lines wrap in a narrow sidebar
                if (!screen.Contains("╭") || !screen.Contains("╰")) return Fail("the box did not render: " + Short(screen));
                if (!flat.Contains("--mcp-config=") || !flat.Contains(McpHost.Url)) return Fail("the editor's MCP server was not passed: " + Short(screen));
                if (!flat.Contains("cwd=" + Path.GetFileName(project.Path.TrimEnd(Path.DirectorySeparatorChar)))) return Fail("it does not run in the project folder: " + Short(screen));
                term.SendText("hello vortex\r");
                if (!await WaitFor(() => term.Screen.ScreenText().Contains("got: hello vortex"), 8000)) return Fail("the typed line did not arrive: " + Short(term.Screen.ScreenText()));
                await SmokeRegistry.Settle(300);
                SmokeRegistry.Capture(window, "claude_code.png");
                log.Log("claude code: OK — terminal renders the program, MCP config passed, typing arrives (" + term.Columns + "×" + term.RowsCount + ")");

                if (Environment.GetEnvironmentVariable("VORTEX_SMOKE_REAL_CLAUDE") == "1")
                {
                    Environment.SetEnvironmentVariable("VORTEX_CLAUDE_CLI", oldCli);
                    if (ClaudeCode.FindCli() == null) log.Log("claude code: no real Claude Code installed — skipped");
                    else
                    {
                        string config = Path.Combine(dir, "claude-config");
                        Directory.CreateDirectory(config);
                        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", config);   // never the user's own sign-in or settings
                        panel.CodePane.Restart();
                        bool shown = await WaitFor(() => term.Screen.ScreenText().IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0, 30000);
                        await SmokeRegistry.Settle(1500);
                        SmokeRegistry.Capture(window, "claude_code_real.png");
                        log.Log("claude code (real): first screen\n" + term.Screen.ScreenText());
                        // a key through the terminal: Enter picks the highlighted theme and Claude Code draws its next step
                        string before = term.Screen.ScreenText();
                        term.SendText("\r");
                        bool moved = await WaitFor(() => term.Screen.ScreenText() != before, 15000);
                        await SmokeRegistry.Settle(2000);
                        SmokeRegistry.Capture(window, "claude_code_real2.png");
                        log.Log("claude code (real): after Enter" + (moved ? "" : " (unchanged)") + "\n" + term.Screen.ScreenText());
                        panel.CodePane.Stop();
                        if (!shown) return Fail("the real Claude Code showed nothing recognisable");
                    }
                }
                return true;
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                panel.CodePane.Stop();
                Environment.SetEnvironmentVariable("VORTEX_CLAUDE_CLI", oldCli);
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", oldConfig);
                panel.SetBackend(oldBackend == "code");
                if (oldBackend != "code") panel.CodePane.Stop();
                settings.Backend = oldBackend;
                settings.Save();
                if (!wasOpen && window.IsPanelVisible(MainWindow.PanelClaude)) window.TogglePanel(MainWindow.PanelClaude);
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static async Task<bool> WaitFor(Func<bool> ok, int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                if (ok()) return true;
                await Task.Delay(100);
            }
            return ok();
        }

        private static string Short(string s) => (s ?? "").Replace("\n", " ⏎ ").Trim().Substring(0, Math.Min(300, (s ?? "").Replace("\n", " ⏎ ").Trim().Length));

        /// <summary>The stand-in for <c>claude</c>: a box, its arguments and folder, then echo one line and wait.</summary>
        private static string WriteFake(string dir)
        {
            if (OperatingSystem.IsWindows())
            {
                string cmd = Path.Combine(dir, "claude.cmd");
                File.WriteAllText(cmd, "@echo off\r\nchcp 65001 >nul\r\necho ╭──────────────╮\r\necho │ FAKE CLAUDE READY │\r\necho ╰──────────────╯\r\n" +
                                       "echo args=%*\r\nfor %%I in (.) do echo cwd=%%~nxI\r\nset /p line=\r\necho got: %line%\r\nping -n 30 127.0.0.1 >nul\r\n");
                return cmd;
            }
            string sh = Path.Combine(dir, "claude");
            File.WriteAllText(sh, "#!/bin/sh\nprintf '\\033[38;5;208m╭──────────────────╮\\033[0m\\n'\nprintf '\\033[38;5;208m│\\033[0m \\033[1mFAKE CLAUDE READY\\033[0m \\033[38;5;208m│\\033[0m\\n'\n" +
                                  "printf '\\033[38;5;208m╰──────────────────╯\\033[0m\\n'\nprintf 'args=%s\\n' \"$*\"\nprintf 'cwd=%s\\n' \"$(basename \"$PWD\")\"\n" +
                                  "read line\nprintf 'got: %s\\n' \"$line\"\nsleep 30\n");
            File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return sh;
        }
    }
}
