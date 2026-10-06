using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using VortexEditor.Panels;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Smoke check "terminal": the Terminal tab (Ctrl+`) opens the user's shell in the project folder, runs a command (the
    /// shell's own arithmetic, so the result is not just the typed echo), a second session opens with +, and `exit` closes
    /// it again. Captures terminal.png.
    /// </summary>
    internal static class TerminalSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("terminal", RunAsync);

        private static async Task<bool> RunAsync()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("terminal: " + why); return false; }
            var w = EditorCommands.Window;
            if (w == null || ProjectData.Current == null) return Fail("no editor or project");
            var panel = w.TerminalPanel;
            bool hidden = !w.IsPanelVisible(MainWindow.PanelTerminal);
            var front = w.BottomTabs.SelectedItem;
            try
            {
                w.ToggleTerminal();
                var view = panel.ActiveView;
                if (view == null) return Fail("no terminal opened");
                bool win = OperatingSystem.IsWindows();
                // wait for the prompt (a login shell reads the user's profile first)
                await WaitFor(() => view.Screen.ScreenText().Trim().Length > 0, 20000);
                view.SendText(win ? "echo \"VORTEX_TERMINAL_$(40+2)\"; (Get-Location).Path\r" : "echo VORTEX_TERMINAL_$((40+2)); pwd\r");
                if (!await WaitFor(() => view.Screen.ScreenText().Contains("VORTEX_TERMINAL_42"), 20000)) return Fail("the command did not run: " + Short(view.Screen.ScreenText()));
                string folder = Path.GetFileName(ProjectData.Current.Path.TrimEnd(Path.DirectorySeparatorChar));
                if (!await WaitFor(() => view.Screen.ScreenText().Contains(folder), 5000)) return Fail("the shell is not in the project folder: " + Short(view.Screen.ScreenText()));
                await SmokeRegistry.Settle(300);
                SmokeRegistry.Capture(w, "terminal.png");

                int before = panel.SessionCount;
                var second = panel.NewSession();
                if (panel.SessionCount != before + 1) return Fail("+ did not open a second terminal");
                await WaitFor(() => second.Screen.ScreenText().Trim().Length > 0, 20000);
                second.SendText("exit\r");
                if (!await WaitFor(() => panel.SessionCount == before, 10000)) return Fail("exit did not close the terminal");
                log.Log("terminal: OK — " + (win ? "PowerShell" : Environment.GetEnvironmentVariable("SHELL") ?? "shell") + " in the project folder, a command ran, a second session opened and closed (" +
                        view.Columns + "×" + view.RowsCount + ")");
                return true;
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                panel.CloseAll();
                if (front != null) w.BottomTabs.SelectedItem = front;
                if (hidden && w.IsPanelVisible(MainWindow.PanelTerminal)) w.TogglePanel(MainWindow.PanelTerminal);
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

        private static string Short(string s)
        {
            s = (s ?? "").Replace("\n", " ⏎ ").Trim();
            return s.Length > 300 ? s.Substring(s.Length - 300) : s;
        }
    }
}
