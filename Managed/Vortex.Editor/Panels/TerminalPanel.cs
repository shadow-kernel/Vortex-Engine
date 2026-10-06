using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core.Claude;
using Editor.Core.Claude.Terminal;
using Editor.Core.Data;
using VortexEditor.Controls;
using VortexEditor.Shell;
using VortexEditor.Shell.Material;

namespace VortexEditor.Panels
{
    /// <summary>
    /// The Terminal tab (as in VS Code or JetBrains): the user's own shell in the project folder — zsh / bash on macOS and
    /// Linux (a login shell, like Terminal), PowerShell on Windows — in as many sessions as they like (+), each a
    /// <see cref="TerminalView"/>. A session ends when its shell ends.
    /// </summary>
    public sealed class TerminalPanel : UserControl
    {
        private sealed class Session
        {
            public TerminalView View;
            public ToggleButton Tab;
            public string Name;
        }

        private readonly List<Session> _sessions = new List<Session>();
        private Session _active;
        private readonly StackPanel _tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        private readonly ContentControl _host = new ContentControl();
        private readonly Control _empty;
        private int _counter;

        public static TerminalPanel Current { get; private set; }
        /// <summary>The terminal that shows (tests).</summary>
        public TerminalView ActiveView => _active?.View;
        public int SessionCount => _sessions.Count;

        public TerminalPanel()
        {
            Current = this;
            var add = IconButton("Plus", "New terminal", () => NewSession());
            var kill = IconButton("Trash", "Close this terminal (ends its shell)", () => { if (_active != null) Close(_active); });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(add);
            buttons.Children.Add(kill);
            var bar = new DockPanel { Margin = new Thickness(8, 0, 4, 0) };
            DockPanel.SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
            bar.Children.Add(new ScrollViewer { Content = _tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var header = new Border { Height = 30, Classes = { "hairline-bottom" }, Child = bar };
            DockPanel.SetDock(header, Dock.Top);

            var sp = new StackPanel { Spacing = 8, Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = "No terminal open", Foreground = EditorKit.Brush("VxTextSecondaryBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            var open = new Button { Content = "New Terminal", HorizontalAlignment = HorizontalAlignment.Center };
            open.Click += (s, e) => NewSession();
            sp.Children.Add(open);
            _empty = sp;
            _host.Content = _empty;

            var root = new DockPanel();
            root.Children.Add(header);
            root.Children.Add(_host);
            Content = root;
        }

        /// <summary>The tab came to the front: open the first terminal, then put the keyboard in it.</summary>
        public void OnShown()
        {
            if (_sessions.Count == 0) NewSession();
            else _active?.View.Focus();
        }

        /// <summary>A new shell in the project folder.</summary>
        public TerminalView NewSession()
        {
            var (file, args, name) = Shell();
            string cwd = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var view = new TerminalView { TerminalBackground = EditorKit.Brush("VxFieldBrush") };
            var session = new Session { View = view, Name = name + " " + (++_counter) };
            session.Tab = new ToggleButton { Content = session.Name, Padding = new Thickness(10, 2), MinHeight = 22, FontSize = 11.5 };
            session.Tab.Click += (s, e) => Activate(session);
            view.Exited += code => Close(session);
            view.TitleChanged += t => { if (!string.IsNullOrWhiteSpace(t)) ToolTip.SetTip(session.Tab, t); };
            _sessions.Add(session);
            _tabs.Children.Add(session.Tab);
            Activate(session);
            try
            {
                var env = Pty.TerminalEnvironment(new Dictionary<string, string> { ["PATH"] = PathFor() });
                view.Start(file, args, cwd, env);
            }
            catch (Exception ex)
            {
                Close(session);
                EditorCommands.Toast("The terminal did not start: " + ex.Message);
            }
            return view;
        }

        private static string _path;
        private static string PathFor()
        {
            // the login shell sets its own PATH on macOS / Linux; Windows terminals take the editor's
            if (_path != null) return _path;
            return _path = Environment.GetEnvironmentVariable("PATH") ?? "";
        }

        private void Activate(Session s)
        {
            _active = s;
            foreach (var x in _sessions) x.Tab.IsChecked = ReferenceEquals(x, s);
            _host.Content = s.View;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => s.View.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        }

        private void Close(Session s)
        {
            int i = _sessions.IndexOf(s);
            if (i < 0) return;
            s.View.Stop();
            _sessions.RemoveAt(i);
            _tabs.Children.Remove(s.Tab);
            if (ReferenceEquals(_active, s))
            {
                _active = null;
                if (_sessions.Count > 0) Activate(_sessions[Math.Min(i, _sessions.Count - 1)]);
                else _host.Content = _empty;
            }
        }

        /// <summary>End every shell (the editor quits).</summary>
        public void CloseAll()
        {
            foreach (var s in _sessions.ToList()) Close(s);
        }

        /// <summary>The user's shell: $SHELL as a login shell on macOS / Linux, PowerShell on Windows.</summary>
        private static (string file, string[] args, string name) Shell()
        {
            if (OperatingSystem.IsWindows())
            {
                string pwsh = ProcessTools.FindExecutable("pwsh.exe", new[] { @"C:\Program Files\PowerShell\7\pwsh.exe" });
                if (pwsh != null) return (pwsh, new[] { "-NoLogo" }, "pwsh");
                string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                if (File.Exists(ps)) return (ps, new[] { "-NoLogo" }, "powershell");
                return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", Array.Empty<string>(), "cmd");
            }
            string shell = Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrEmpty(shell) || !File.Exists(shell)) shell = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/bash";
            return (shell, new[] { "-l" }, Path.GetFileName(shell));
        }

        private static Button IconButton(string icon, string tip, Action click)
        {
            var b = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = icon, Width = 13, Height = 13 } };
            ToolTip.SetTip(b, tip);
            b.Click += (s, e) => click();
            return b;
        }
    }
}
