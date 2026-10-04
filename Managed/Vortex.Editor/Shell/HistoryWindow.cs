using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using VortexEditor.Controls;

namespace VortexEditor.Shell
{
    /// <summary>
    /// History (port of the WPF HistoryLogView): the undo stack as a list — redo steps greyed on top, the current state,
    /// then the undo steps (newest first). Clicking a row jumps there: "−3" undoes three steps, "+2" redoes two.
    /// Undo / Redo / Clear buttons; ⌘Z / ⇧⌘Z work in the window too.
    /// </summary>
    public sealed class HistoryWindow : Window
    {
        public sealed class Row
        {
            public string Name { get; set; }
            public int Steps { get; set; }          // <0 undo that many, >0 redo that many, 0 = current state
            public bool IsRedo => Steps > 0;
            public bool IsCurrent => Steps == 0;
            public string Icon { get; set; }
            public string StepText => Steps == 0 ? "" : (Steps > 0 ? "+" : "−") + Math.Abs(Steps);
            public IUndoableCommand Command { get; set; }
        }

        private static HistoryWindow _open;
        /// <summary>Open (or bring to the front) the History window.</summary>
        public static void Open()
        {
            if (_open != null) { _open.Activate(); return; }
            _open = new HistoryWindow();
            _open.Closed += (s, e) => _open = null;
            EditorWindows.Show(_open);
        }
        /// <summary>The open History window (null when closed).</summary>
        public static HistoryWindow Current => _open;

        private readonly ListBox _list = new ListBox();
        private readonly TextBlock _summary = new TextBlock { Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _undo = new Button { Content = "Undo" }, _redo = new Button { Content = "Redo" }, _clear = new Button { Content = "Clear History", Classes = { "ghost" } };
        private bool _filling;

        public IReadOnlyList<Row> Rows => (_list.ItemsSource as IReadOnlyList<Row>) ?? new List<Row>();

        public HistoryWindow()
        {
            Title = "History"; Width = 380; Height = 560; MinWidth = 300; MinHeight = 260; ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var head = new DockPanel { Margin = new Thickness(14, 12, 14, 6) };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(new VxIcon { Icon = "Undo", Foreground = (IBrush)Application.Current.FindResource("VxAccentBrush") });
            title.Children.Add(new TextBlock { Text = "History", FontWeight = FontWeight.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(title, Dock.Left);
            head.Children.Add(title);
            _summary.HorizontalAlignment = HorizontalAlignment.Right;
            head.Children.Add(_summary);
            var hint = new TextBlock { Text = "Click a step to jump there — −n undoes n steps, +n redoes them.", Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 0, 14, 8) };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(14, 8, 14, 12) };
            ToolTip.SetTip(_undo, "Undo (" + Keys.Chord("Z") + ")"); ToolTip.SetTip(_redo, "Redo (" + Keys.Chord("Z", shift: true) + ")"); ToolTip.SetTip(_clear, "Forget every undo / redo step");
            _undo.Click += (s, e) => { EditorCommands.Undo(); };
            _redo.Click += (s, e) => { EditorCommands.Redo(); };
            _clear.Click += async (s, e) => { if (await Dialogs.Confirm("Clear the history?", "Every undo / redo step is forgotten. The scene itself does not change.", "Clear", "Cancel", destructive: true)) UndoRedoManager.Instance.Clear(); };
            bar.Children.Add(_undo); bar.Children.Add(_redo); bar.Children.Add(_clear);
            _list.ItemTemplate = new FuncDataTemplate<Row>((r, _) => RowView(r));
            _list.SelectionChanged += (s, e) => { if (!_filling && _list.SelectedItem is Row r && !r.IsCurrent) Jump(r); };
            var dock = new DockPanel();
            DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(hint, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom);
            dock.Children.Add(head); dock.Children.Add(hint); dock.Children.Add(bar);
            dock.Children.Add(new Border { Classes = { "hairline-top" }, Child = _list });
            Content = dock;
            UndoRedoManager.Instance.StateChanged += OnStateChanged;
            Closed += (s, e) => UndoRedoManager.Instance.StateChanged -= OnStateChanged;
            KeyDown += OnKey;
            Fill();
        }

        private void OnStateChanged(object sender, EventArgs e) => Dispatcher.UIThread.Post(Fill);

        private void OnKey(object sender, KeyEventArgs e)
        {
            bool cmd = OperatingSystem.IsMacOS() ? e.KeyModifiers.HasFlag(KeyModifiers.Meta) : e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (!cmd) return;
            if (e.Key == Key.Z) { if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) EditorCommands.Redo(); else EditorCommands.Undo(); e.Handled = true; }
            else if (e.Key == Key.Y) { EditorCommands.Redo(); e.Handled = true; }
            else if (e.Key == Key.W) { Close(); e.Handled = true; }
        }

        private static Control RowView(Row r)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*,Auto"), Height = 24, Background = Brushes.Transparent };
            if (r == null) return g;   // containers are re-templated with null while the list is recycled
            if (r.IsCurrent)
            {
                var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = (IBrush)Application.Current.FindResource("VxGreenBrush"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                g.Children.Add(dot);
                var t = new TextBlock { Text = r.Name, FontWeight = FontWeight.SemiBold, Foreground = (IBrush)Application.Current.FindResource("VxGreenBrush"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(t, 1); g.Children.Add(t);
                return g;
            }
            var icon = new VxIcon { Icon = r.Icon, Width = 13, Height = 13, Foreground = (IBrush)Application.Current.FindResource(r.IsRedo ? "VxTextTertiaryBrush" : "VxAccentBrush") };
            g.Children.Add(icon);
            var name = new TextBlock { Text = r.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = r.IsRedo ? 0.55 : 1 };
            Grid.SetColumn(name, 1); g.Children.Add(name);
            var step = new TextBlock { Text = r.StepText, Classes = { "mono", "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) };
            Grid.SetColumn(step, 2); g.Children.Add(step);
            ToolTip.SetTip(g, r.IsRedo ? "Redo " + r.Steps + " step(s) up to here" : "Undo " + (-r.Steps) + " step(s) — back to before this action");
            return g;
        }

        /// <summary>Rebuild the list from the undo manager.</summary>
        public void Fill()
        {
            _filling = true;
            try
            {
                var um = UndoRedoManager.Instance;
                var undo = um.GetUndoHistory();   // newest first
                var redo = um.GetRedoHistory();   // next redo first
                var rows = new List<Row>();
                for (int i = redo.Count - 1; i >= 0; i--) rows.Add(new Row { Name = redo[i].Name, Steps = i + 1, Icon = IconFor(redo[i].Name), Command = redo[i] });
                rows.Add(new Row { Name = undo.Count == 0 && redo.Count == 0 ? "Current state (nothing to undo)" : "Current state", Steps = 0 });
                for (int i = 0; i < undo.Count; i++) rows.Add(new Row { Name = undo[i].Name, Steps = -(i + 1), Icon = IconFor(undo[i].Name), Command = undo[i] });
                _list.ItemsSource = rows;
                _list.SelectedItem = rows.FirstOrDefault(r => r.IsCurrent);
                _summary.Text = undo.Count + " undo · " + redo.Count + " redo";
                _undo.IsEnabled = um.CanUndo; _redo.IsEnabled = um.CanRedo; _clear.IsEnabled = um.CanUndo || um.CanRedo;
                var current = rows.FirstOrDefault(r => r.IsCurrent);
                if (current != null) Dispatcher.UIThread.Post(() => { try { _list.ScrollIntoView(current); } catch { } }, DispatcherPriority.Background);
            }
            finally { _filling = false; }
        }

        /// <summary>Jump to a row: undo or redo the number of steps it stands for.</summary>
        public void Jump(Row r)
        {
            if (r == null || r.IsCurrent) return;
            var um = UndoRedoManager.Instance;
            if (r.Steps < 0) um.UndoMultiple(-r.Steps); else um.RedoMultiple(r.Steps);
            SceneRenderService.RuntimeDirty = true;
            Editor.Core.Viewport.EditorViewportSession.RequestResubmit();
            EditorCommands.Window?.Inspector?.Refresh();
            Fill();
        }

        private static string IconFor(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Undo";
            if (name.Contains("Delete") || name.Contains("Remove") || name.StartsWith("Cut")) return "Trash";
            if (name.Contains("Move") || name.Contains("Reorder")) return "Move";
            if (name.Contains("Duplicate") || name.Contains("Paste") || name.Contains("Copy")) return "Layers";
            if (name.Contains("Add") || name.Contains("Create") || name.Contains("Instantiate")) return "Plus";
            if (name.Contains("Rename") || name.Contains("Change") || name.Contains("Set")) return "Gear";
            if (name.Contains("Material")) return "Material";
            return "Undo";
        }
    }
}
