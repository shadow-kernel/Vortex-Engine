using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VortexEditor.Shell;
using VortexEditor.Shell.Library;
using VortexEditor.Shell.Material;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Claude
{
    /// <summary>
    /// Tools ▸ Claude ▸ Operations… (#94): every tool call Claude made in this session — time, client, tool, input,
    /// result — with Revert per operation (the newest like Undo, an older one out of order after a warning when later
    /// operations touched the same entities or files) and the diff of every file it wrote.
    /// </summary>
    public sealed class ClaudeOperationsWindow : Window
    {
        private static ClaudeOperationsWindow _open;

        public static void Open(Window owner = null)
        {
            if (_open != null) { _open.Activate(); return; }
            _open = new ClaudeOperationsWindow();
            _open.Closed += (s, e) => _open = null;
            var o = owner ?? EditorKit.ActiveWindow();
            if (o != null) _open.Show(o); else _open.Show();
        }

        private readonly StackPanel _rows = new StackPanel { Spacing = 4, Margin = new Thickness(14, 10, 14, 14) };
        private readonly TextBox _filter = new TextBox { Classes = { "search" }, Watermark = "Filter", Width = 200 };
        private readonly CheckBox _changesOnly = new CheckBox { Content = "Changes only", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };

        private ClaudeOperationsWindow()
        {
            Title = "Claude Operations";
            Width = 860; Height = 620; MinWidth = 560; MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var top = new DockPanel { Margin = new Thickness(14, 12, 14, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            right.Children.Add(_changesOnly);
            right.Children.Add(_filter);
            DockPanel.SetDock(right, Dock.Right);
            top.Children.Add(right);
            top.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = "Claude Operations", FontSize = 16, FontWeight = FontWeight.SemiBold },
                    LibraryUi.Small("Every tool call of this session, newest first. Each change is one undo step; Revert takes one back."),
                },
            });
            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            root.Children.Add(new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            Content = root;

            _filter.TextChanged += (s, e) => Rebuild();
            _changesOnly.IsCheckedChanged += (s, e) => Rebuild();
            Action changed = () => Dispatcher.UIThread.Post(Rebuild);
            OperationLog.Changed += changed;
            Editor.Core.UndoRedo.UndoRedoManager.Instance.StateChanged += OnUndo;
            Closed += (s, e) => { OperationLog.Changed -= changed; Editor.Core.UndoRedo.UndoRedoManager.Instance.StateChanged -= OnUndo; };
            Rebuild();
        }

        private void OnUndo(object s, EventArgs e) => Dispatcher.UIThread.Post(Rebuild);

        private void Rebuild()
        {
            _rows.Children.Clear();
            string f = _filter.Text?.Trim();
            var ops = OperationLog.Items.Reverse()
                .Where(o => _changesOnly.IsChecked != true || o.UndoStep != null || o.DryRun || o.IsError && o.Tool != null && ToolCatalog.Find(o.Tool)?.ReadOnly == false)
                .Where(o => string.IsNullOrEmpty(f) || (o.Tool + " " + o.Arguments + " " + o.Result).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(300).ToList();
            if (ops.Count == 0)
            {
                _rows.Children.Add(Ui.Muted(OperationLog.Items.Count == 0 ? "No Claude operations yet in this session." : "Nothing matches."));
                return;
            }
            foreach (var op in ops) _rows.Children.Add(Row(op));
        }

        private Control Row(ToolOperation op)
        {
            var def = ToolCatalog.Find(op.Tool);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
            var when = new StackPanel { Width = 74 };
            when.Children.Add(new TextBlock { Text = op.Time.ToString("HH:mm:ss"), FontSize = 12 });
            when.Children.Add(new TextBlock { Text = op.Origin == "panel" ? "Panel" : op.Origin == "mcp" ? "MCP" : op.Origin ?? "", FontSize = 10, Foreground = EditorKit.Brush("VxTextTertiaryBrush") });
            grid.Children.Add(when);

            var mid = new StackPanel { Spacing = 2 };
            string title = op.UndoStep?.Name ?? def?.Title ?? op.Tool;
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (op.DryRun) head.Children.Add(Badge("dry run", "VxTextTertiaryBrush"));
            if (op.Reverted || op.UndoStep != null && !op.CanRevert) head.Children.Add(Badge("reverted", "VxTextTertiaryBrush"));
            if (op.IsError) head.Children.Add(Badge("failed", "VxRedBrush"));
            mid.Children.Add(head);
            mid.Children.Add(new TextBlock { Text = op.Tool + " " + op.Arguments, FontSize = 11, Foreground = EditorKit.Brush("VxTextTertiaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            mid.Children.Add(new TextBlock { Text = op.Result, FontSize = 11, Foreground = EditorKit.Brush(op.IsError ? "VxRedBrush" : "VxTextSecondaryBrush"), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
            if (op.Files.Count > 0)
                mid.Children.Add(new TextBlock { Text = string.Join(", ", op.Files.Select(x => (x.Created ? "+ " : "~ ") + SafeRel(x.Path))), FontSize = 11, Foreground = EditorKit.Brush("VxTextSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(mid, 1);
            grid.Children.Add(mid);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
            if (op.Files.Count > 0) buttons.Children.Add(Ui.Button("Diff", () => DiffWindow.Show(op, this)));
            if (op.CanRevert) buttons.Children.Add(Ui.Button("Revert", () => _ = Revert(op)));
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);
            return new Border
            {
                Child = grid, Padding = new Thickness(10, 7), CornerRadius = new CornerRadius(6),
                Background = EditorKit.Brush("VxToolbarBrush"), BorderBrush = EditorKit.Brush("VxHairlineBrush"), BorderThickness = new Thickness(1),
                Opacity = op.Reverted ? 0.6 : 1,
            };
        }

        private async Task Revert(ToolOperation op)
        {
            var deps = OperationLog.DependentsOf(op);
            bool newest = Editor.Core.UndoRedo.UndoRedoManager.Instance.GetUndoHistory().FirstOrDefault() == op.UndoStep;
            if (deps.Count > 0 || !newest)
            {
                string msg = (deps.Count > 0
                                 ? "Later operations changed the same entities or files:\n" + string.Join("\n", deps.Take(8).Select(d => "• " + (d.UndoStep?.Name ?? d.Tool))) + (deps.Count > 8 ? "\n…" : "") + "\n\n"
                                 : "") +
                             (newest ? "" : "This is not the newest step: it is taken back out of order, and the redo history is cleared. Your own later edits are not checked.\n\n") +
                             "Revert \"" + (op.UndoStep?.Name ?? op.Tool) + "\"?";
                if (!await Dialogs.Confirm("Revert an earlier operation?", msg, "Revert", "Cancel", destructive: true)) return;
            }
            if (!OperationLog.Revert(op)) EditorCommands.Toast("That step can no longer be reverted");
            else EditorCommands.Toast("Reverted: " + (op.UndoStep?.Name ?? op.Tool));
            Rebuild();
        }

        private static Control Badge(string text, string brush) => new Border
        {
            CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0), BorderThickness = new Thickness(1), BorderBrush = EditorKit.Brush(brush),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 10, Foreground = EditorKit.Brush(brush) },
        };

        private static string SafeRel(string full) { try { return ProjectFiles.Rel(full); } catch { return full; } }

        /// <summary>Line diff of the files one operation wrote.</summary>
        private sealed class DiffWindow : Window
        {
            public static void Show(ToolOperation op, Window owner)
            {
                var w = new DiffWindow(op);
                w.Show(owner);
            }

            private DiffWindow(ToolOperation op)
            {
                Title = "Changes — " + (op.UndoStep?.Name ?? op.Tool);
                Width = 820; Height = 640;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                var stack = new StackPanel { Margin = new Thickness(14), Spacing = 10 };
                FontFamily mono = Application.Current != null && Application.Current.TryFindResource("VxMono", out var m) && m is FontFamily ff ? ff : FontFamily.Default;
                foreach (var f in op.Files)
                {
                    stack.Children.Add(new TextBlock { Text = (f.Created ? "Created " : "Changed ") + SafeRel(f.Path), FontWeight = FontWeight.SemiBold });
                    var lines = new StackPanel();
                    foreach (var (kind, text) in LineDiff.Compute(f.Before ?? "", f.After ?? "", context: 3))
                    {
                        lines.Children.Add(new TextBlock
                        {
                            Text = (kind == '+' ? "+ " : kind == '-' ? "- " : kind == '~' ? "  " : "  ") + text,
                            FontFamily = mono, FontSize = 11, TextWrapping = TextWrapping.NoWrap,
                            Foreground = EditorKit.Brush(kind == '+' ? "VxGreenBrush" : kind == '-' ? "VxRedBrush" : kind == '~' ? "VxTextTertiaryBrush" : "VxTextSecondaryBrush"),
                        });
                    }
                    stack.Children.Add(new Border { Child = lines, Padding = new Thickness(8), CornerRadius = new CornerRadius(4), Background = EditorKit.Brush("VxToolbarBrush") });
                }
                Content = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            }
        }
    }

    /// <summary>A small line diff (longest common subsequence) with context, for the Operations window.</summary>
    public static class LineDiff
    {
        /// <summary>(kind, text): ' ' unchanged, '+' added, '-' removed, '~' "… n unchanged lines".</summary>
        public static List<(char kind, string text)> Compute(string before, string after, int context = 3)
        {
            var a = before.Replace("\r\n", "\n").Split('\n');
            var b = after.Replace("\r\n", "\n").Split('\n');
            var raw = new List<(char, string)>();
            if ((long)a.Length * b.Length > 4_000_000)
            {
                // too big for the table: show it as replaced
                raw.AddRange(a.Select(x => ('-', x)));
                raw.AddRange(b.Select(x => ('+', x)));
            }
            else
            {
                var lcs = new int[a.Length + 1, b.Length + 1];
                for (int i = a.Length - 1; i >= 0; i--)
                    for (int j = b.Length - 1; j >= 0; j--)
                        lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                int x = 0, y = 0;
                while (x < a.Length && y < b.Length)
                {
                    if (a[x] == b[y]) { raw.Add((' ', a[x])); x++; y++; }
                    else if (lcs[x + 1, y] >= lcs[x, y + 1]) raw.Add(('-', a[x++]));
                    else raw.Add(('+', b[y++]));
                }
                while (x < a.Length) raw.Add(('-', a[x++]));
                while (y < b.Length) raw.Add(('+', b[y++]));
            }
            // keep changes with a few lines of context around them
            var keep = new bool[raw.Count];
            for (int i = 0; i < raw.Count; i++)
                if (raw[i].Item1 != ' ')
                    for (int k = Math.Max(0, i - context); k <= Math.Min(raw.Count - 1, i + context); k++) keep[k] = true;
            var result = new List<(char, string)>();
            int skipped = 0;
            for (int i = 0; i < raw.Count; i++)
            {
                if (keep[i])
                {
                    if (skipped > 0) { result.Add(('~', "… " + skipped + " unchanged line" + (skipped == 1 ? "" : "s"))); skipped = 0; }
                    result.Add(raw[i]);
                }
                else skipped++;
            }
            if (skipped > 0 && result.Count > 0) result.Add(('~', "… " + skipped + " unchanged line" + (skipped == 1 ? "" : "s")));
            return result;
        }
    }
}
