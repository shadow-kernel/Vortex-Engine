using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.AI;
using Editor.Core.Editing;
using Editor.Core.Services.AI;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using VortexEditor.Controls;
using VortexEditor.Services;
using VortexEditor.Shell.Material;
using Path = System.IO.Path;

namespace VortexEditor.Shell
{
    /// <summary>
    /// The Behavior Tree editor (#111): a <c>.vbt</c> drawn as a tree (auto-laid-out boxes, lines to the children), a
    /// palette of node kinds and task classes (the engine's built-ins and the project's own <c>BtTask</c> scripts), a
    /// property panel per node (name, task, parameters with their documented defaults, abort mode, comment), the
    /// blackboard defaults, undo / redo through the editor's UndoRedoManager, and a live view: while the game plays,
    /// the first agent running this tree tints the nodes by their last result (running / success / failure) and the
    /// panel shows its blackboard.
    /// </summary>
    public sealed class BehaviorTreeEditorWindow : Window
    {
        // ================================================================= entry points
        public static void Open(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            var existing = Find(fullPath);
            if (existing != null) { existing.Activate(); return; }
            EditorWindows.Show(new BehaviorTreeEditorWindow(fullPath));
        }

        public static BehaviorTreeEditorWindow Find(string fullPath)
            => EditorKit.OpenWindows<BehaviorTreeEditorWindow>().FirstOrDefault(w => EditorKit.SamePath(w._path, fullPath));

        /// <summary>Create a new .vbt with a Selector root in <paramref name="folder"/> and return its path.</summary>
        public static string CreateNew(string folder, string name = "New Behavior Tree")
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name + BehaviorTreeAsset.Extension);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, name + " " + i + BehaviorTreeAsset.Extension);
            var a = BehaviorTreeAsset.NewDefault(Path.GetFileNameWithoutExtension(path));
            // a starter the user can play with at once: wait, then log
            var seq = new BtNodeData { Kind = BtNodeKind.Sequence, Name = "Idle" };
            seq.Children.Add(new BtNodeData { Kind = BtNodeKind.Task, Task = "Wait", Params = { ["seconds"] = "2" } });
            seq.Children.Add(new BtNodeData { Kind = BtNodeKind.Task, Task = "Log", Params = { ["message"] = "idle" } });
            a.Root.Children.Add(seq);
            a.Save(path);
            return path;
        }

        // ================================================================= state
        private readonly string _path;
        private BehaviorTreeAsset _asset;
        private string _savedJson;
        private string _selectedId;
        private string _clipboard;
        private bool _closeConfirmed;
        private bool _live = true;
        private readonly Canvas _canvas = new Canvas { Background = Brushes.Transparent };
        private readonly StackPanel _props = new StackPanel { Spacing = 4 };
        private readonly StackPanel _blackboardPanel = new StackPanel { Spacing = 2 };
        private readonly TextBlock _title = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _status = new TextBlock { Opacity = 0.7, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        private readonly Dictionary<string, Border> _boxes = new Dictionary<string, Border>();
        private readonly DispatcherTimer _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        private readonly List<BtDebugNode> _snapshot = new List<BtDebugNode>();
        private BehaviorTreeRunner _liveRunner;
        private int _liveBlackboardVersion = -1;

        private const double BoxW = 158, BoxH = 46, GapX = 18, GapY = 54, Pad = 24;

        public string Path_ => _path;
        public BehaviorTreeAsset Asset => _asset;
        public bool IsDirty => _asset != null && _asset.ToJson() != _savedJson;

        private BehaviorTreeEditorWindow(string path)
        {
            _path = path;
            _asset = BehaviorTreeAsset.Load(path) ?? BehaviorTreeAsset.NewDefault(Path.GetFileNameWithoutExtension(path));
            _asset.Normalize();
            _savedJson = _asset.ToJson();
            _selectedId = _asset.Root.Id;
            Width = 1320; Height = 820; MinWidth = 900; MinHeight = 520;
            Background = EditorKit.Brush("VxPanelBrush") ?? Brushes.Black;
            Content = BuildLayout();
            KeyDown += OnKeyDown;
            Closing += OnClosing;
            Closed += (s, e) => { _liveTimer.Stop(); EditorSession.Instance.ProjectClosed -= OnProjectClosed; };
            EditorSession.Instance.ProjectClosed += OnProjectClosed;
            _liveTimer.Tick += (s, e) => PollLive();
            _liveTimer.Start();
            Rebuild();
            UpdateTitle();
            EditorKit.FitToScreen(this);
        }

        // ================================================================= layout
        private Control BuildLayout()
        {
            var root = new DockPanel();

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 8, 12, 8) };
            bar.Children.Add(new VxIcon { Icon = "Flow", Width = 18, Height = 18, Margin = new Thickness(0, 0, 4, 0) });
            bar.Children.Add(_title);
            bar.Children.Add(new Border { Width = 18 });
            bar.Children.Add(Tool("Save", () => Save(), "Save the tree (" + Keys.Chord("S") + ")", "Save"));
            bar.Children.Add(Tool("Revert", Revert, "Discard the changes since the last save", "Undo"));
            bar.Children.Add(new Border { Width = 12 });
            var add = Tool("Add Child", null, "Add a node under the selected one", "Plus");
            add.Click += (s, e) => { var f = BuildPalette(); f.ShowAt(add); };
            bar.Children.Add(add);
            bar.Children.Add(Tool("Delete", DeleteSelected, "Delete the selected node and its subtree (Delete)", "Delete"));
            bar.Children.Add(Tool("▲", () => MoveSelected(-1), "Move the node up among its siblings", "ChevronUp"));
            bar.Children.Add(Tool("▼", () => MoveSelected(1), "Move the node down among its siblings", "ChevronDown"));
            bar.Children.Add(Tool("Cut", () => CutCopy(true), "Cut the subtree (" + Keys.Chord("X") + ")", "Cut"));
            bar.Children.Add(Tool("Copy", () => CutCopy(false), "Copy the subtree (" + Keys.Chord("C") + ")", "Copy"));
            bar.Children.Add(Tool("Paste", Paste, "Paste as a child of the selected node (" + Keys.Chord("V") + ")", "Paste"));
            bar.Children.Add(new Border { Width = 12 });
            var live = new CheckBox { Content = "Live", IsChecked = _live, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(live, "While the game plays: tint the nodes by the first agent running this tree and show its blackboard");
            live.IsCheckedChanged += (s, e) => { _live = live.IsChecked == true; if (!_live) ClearLive(); };
            bar.Children.Add(live);
            bar.Children.Add(new Border { Width = 12 });
            bar.Children.Add(_status);
            var barBorder = new Border { Child = bar, BorderBrush = EditorKit.Brush("VxSeparatorBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };
            DockPanel.SetDock(barBorder, Dock.Top);
            root.Children.Add(barBorder);

            // properties (right)
            var right = new StackPanel { Spacing = 10 };
            right.Children.Add(_props);
            var bbHead = new TextBlock { Text = "BLACKBOARD DEFAULTS", FontSize = 11, Opacity = 0.6, Margin = new Thickness(2, 8, 0, 0) };
            right.Children.Add(bbHead);
            right.Children.Add(_blackboardPanel);
            var scroll = new ScrollViewer { Content = right, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0) };
            var rightBorder = new Border { Width = 400, Margin = new Thickness(6, 10, 10, 10), Child = scroll };
            DockPanel.SetDock(rightBorder, Dock.Right);
            root.Children.Add(rightBorder);

            // the tree (centre)
            var canvasScroll = new ScrollViewer { Content = _canvas, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            var view = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(10, 10, 0, 10), Background = EditorKit.Brush("VxCanvasBrush") ?? new SolidColorBrush(Color.FromRgb(24, 26, 30)), Child = canvasScroll };
            _canvas.PointerPressed += (s, e) => { if (e.Source == _canvas) Select(_asset.Root.Id); };
            root.Children.Add(view);
            return root;
        }

        private static Button Tool(string text, Action click, string tip, string icon)
        {
            var b = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { new VxIcon { Icon = icon, Width = 13, Height = 13 }, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } }, Padding = new Thickness(8, 4) };
            ToolTip.SetTip(b, tip);
            if (click != null) b.Click += (s, e) => click();
            return b;
        }

        // ================================================================= palette
        private MenuFlyout BuildPalette()
        {
            var f = new MenuFlyout();
            var composites = new MenuItem { Header = "Composite" };
            foreach (var k in new[] { BtNodeKind.Selector, BtNodeKind.Sequence, BtNodeKind.Parallel }) composites.Items.Add(Item(k.ToString(), KindHelp(k), () => AddChild(new BtNodeData { Kind = k })));
            f.Items.Add(composites);
            var decorators = new MenuItem { Header = "Decorator" };
            foreach (var k in new[] { BtNodeKind.Inverter, BtNodeKind.Succeeder, BtNodeKind.Repeat, BtNodeKind.Cooldown }) decorators.Items.Add(Item(k.ToString(), KindHelp(k), () => AddChild(new BtNodeData { Kind = k })));
            f.Items.Add(decorators);
            var types = BehaviorTreeService.AvailableTaskTypes();
            var tasks = new MenuItem { Header = "Task" };
            var conditions = new MenuItem { Header = "Condition" };
            var builtinTasks = new MenuItem { Header = "Built-in" };
            var builtinConds = new MenuItem { Header = "Built-in" };
            var scriptTasks = new MenuItem { Header = "Project scripts" };
            var scriptConds = new MenuItem { Header = "Project scripts" };
            foreach (var t in types.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var tt = t;
                var probe = BehaviorTreeService.Describe(t);
                string help = probe != null ? probe.Description : "";
                bool builtin = BehaviorTreeService.Builtins.ContainsKey(t.Name);
                (builtin ? builtinTasks : scriptTasks).Items.Add(Item(t.Name, help, () => AddChild(NewTaskNode(BtNodeKind.Task, tt))));
                (builtin ? builtinConds : scriptConds).Items.Add(Item(t.Name, help, () => AddChild(NewTaskNode(BtNodeKind.Condition, tt))));
            }
            if (scriptTasks.Items.Count == 0) { scriptTasks.Items.Add(new MenuItem { Header = "(no BtTask classes in the project's scripts yet)", IsEnabled = false }); scriptConds.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false }); }
            tasks.Items.Add(builtinTasks); tasks.Items.Add(scriptTasks);
            conditions.Items.Add(builtinConds); conditions.Items.Add(scriptConds);
            conditions.Items.Add(new Separator());
            conditions.Items.Add(Item("Empty condition (pick the task later)", "A guard without a task fails until a task is chosen", () => AddChild(new BtNodeData { Kind = BtNodeKind.Condition })));
            f.Items.Add(tasks);
            f.Items.Add(conditions);
            return f;
        }

        private static MenuItem Item(string header, string tip, Action click)
        {
            var m = new MenuItem { Header = header };
            if (!string.IsNullOrEmpty(tip)) ToolTip.SetTip(m, tip);
            m.Click += (s, e) => click();
            return m;
        }

        private static BtNodeData NewTaskNode(BtNodeKind kind, Type taskType)
        {
            var n = new BtNodeData { Kind = kind, Task = taskType.Name };
            var probe = BehaviorTreeService.Describe(taskType);
            if (probe != null) foreach (var p in probe.DescribeParams()) if (!string.IsNullOrEmpty(p.Default)) n.Params[p.Name] = p.Default;
            return n;
        }

        private static string KindHelp(BtNodeKind k)
        {
            switch (k)
            {
                case BtNodeKind.Selector: return "Runs children in order until one succeeds";
                case BtNodeKind.Sequence: return "Runs children in order until one fails";
                case BtNodeKind.Parallel: return "Runs every child each tick; fails when one fails";
                case BtNodeKind.Inverter: return "Swaps Success and Failure of its child";
                case BtNodeKind.Succeeder: return "Always Success once the child finished";
                case BtNodeKind.Repeat: return "Repeats its child (count, or until it fails)";
                case BtNodeKind.Cooldown: return "After the child finished, Failure for a while";
                case BtNodeKind.Condition: return "A guard: the child runs only while the condition task succeeds";
                default: return "";
            }
        }

        // ================================================================= edits (undoable)
        private void Edit(string name, Action mutate)
        {
            string before = _asset.ToJson();
            mutate();
            _asset.Normalize();
            string after = _asset.ToJson();
            if (before == after) { Rebuild(); return; }
            string keep = _selectedId;
            UndoRedoManager.Instance.Execute(new ActionCommand("Behavior Tree: " + name, () => Apply(after, keep), () => Apply(before, keep)), execute: false);
            Rebuild();
        }

        private void Apply(string json, string selected)
        {
            var a = BehaviorTreeAsset.FromJson(json);
            if (a == null) return;
            _asset = a; _asset.Normalize();
            _selectedId = _asset.Find(selected) != null ? selected : _asset.Root.Id;
            Rebuild();
        }

        private BtNodeData Selected => _asset.Find(_selectedId) ?? _asset.Root;

        private void AddChild(BtNodeData child)
        {
            var parent = Selected;
            if (parent.Kind == BtNodeKind.Task) { EditorCommands.Toast("A task has no children — select a composite or a decorator"); return; }
            if (BehaviorTreeAsset.IsDecorator(parent.Kind) && parent.Children.Count > 0) { EditorCommands.Toast("A decorator holds one child — replace it or add under a composite"); return; }
            Edit("add " + child.Label, () => { parent.Children.Add(child); _selectedId = child.Id; });
        }

        private void DeleteSelected()
        {
            var n = Selected;
            if (n == _asset.Root) { EditorCommands.Toast("The root stays — delete its children instead"); return; }
            var parent = _asset.ParentOf(n.Id);
            if (parent == null) return;
            Edit("delete " + n.Label, () => { parent.Children.Remove(n); _selectedId = parent.Id; });
        }

        private void MoveSelected(int delta)
        {
            var n = Selected;
            var parent = _asset.ParentOf(n.Id);
            if (parent == null) return;
            int i = parent.Children.IndexOf(n), j = i + delta;
            if (i < 0 || j < 0 || j >= parent.Children.Count) return;
            Edit("reorder", () => { parent.Children.RemoveAt(i); parent.Children.Insert(j, n); });
        }

        private void CutCopy(bool cut)
        {
            var n = Selected;
            if (n == _asset.Root) { if (cut) { EditorCommands.Toast("The root cannot be cut"); return; } }
            _clipboard = System.Text.Json.JsonSerializer.Serialize(n, BehaviorTreeAsset.JsonOptions);
            if (cut) DeleteSelected();
            EditorCommands.Toast((cut ? "Cut " : "Copied ") + n.Label);
        }

        private void Paste()
        {
            if (string.IsNullOrEmpty(_clipboard)) return;
            var node = BehaviorTreeAsset.FromJson(_clipboard, typeof(BtNodeData)) as BtNodeData;
            if (node == null) return;
            foreach (var x in Walk(node)) x.Id = "";   // fresh ids
            AddChild(node);
        }

        private static IEnumerable<BtNodeData> Walk(BtNodeData n)
        {
            yield return n;
            if (n.Children == null) yield break;
            foreach (var c in n.Children) foreach (var d in Walk(c)) yield return d;
        }

        private void SetParam(BtNodeData n, string key, string value)
        {
            Edit("set " + key, () => { if (string.IsNullOrEmpty(value)) n.Params.Remove(key); else n.Params[key] = value; });
        }

        // ================================================================= drawing
        private void Rebuild()
        {
            _canvas.Children.Clear();
            _boxes.Clear();
            var pos = new Dictionary<string, Point>();
            double width = Layout(_asset.Root, Pad, Pad, pos);
            double height = (Depth(_asset.Root) + 1) * (BoxH + GapY) + Pad;
            _canvas.Width = Math.Max(width + Pad, 400);
            _canvas.Height = Math.Max(height, 300);
            DrawLines(_asset.Root, pos);
            foreach (var n in _asset.AllNodes()) DrawBox(n, pos[n.Id]);
            RefreshProps();
            RefreshBlackboard();
            UpdateTitle();
        }

        /// <summary>Top-down layout: a node sits centred over its children; returns the subtree's right edge.</summary>
        private double Layout(BtNodeData n, double x, double y, Dictionary<string, Point> pos)
        {
            double childX = x, childRight = x;
            var kids = n.Children ?? new List<BtNodeData>();
            foreach (var c in kids)
            {
                childRight = Layout(c, childX, y + BoxH + GapY, pos);
                childX = childRight + GapX;
            }
            double right;
            if (kids.Count == 0) { pos[n.Id] = new Point(x, y); right = x + BoxW; }
            else
            {
                double first = pos[kids[0].Id].X, last = pos[kids[kids.Count - 1].Id].X;
                double cx = (first + last) / 2;
                pos[n.Id] = new Point(cx, y);
                right = Math.Max(childRight, cx + BoxW);
            }
            return right;
        }

        private static int Depth(BtNodeData n) { int d = 0; foreach (var c in n.Children) d = Math.Max(d, Depth(c) + 1); return d; }

        private void DrawLines(BtNodeData n, Dictionary<string, Point> pos)
        {
            var p = pos[n.Id];
            foreach (var c in n.Children)
            {
                var q = pos[c.Id];
                _canvas.Children.Add(new Line
                {
                    StartPoint = new Point(p.X + BoxW / 2, p.Y + BoxH), EndPoint = new Point(q.X + BoxW / 2, q.Y),
                    Stroke = new SolidColorBrush(Color.FromRgb(110, 116, 128)), StrokeThickness = 1.5
                });
                DrawLines(c, pos);
            }
        }

        private void DrawBox(BtNodeData n, Point p)
        {
            bool selected = n.Id == _selectedId;
            var text = new StackPanel { Spacing = 1, Margin = new Thickness(8, 4) };
            text.Children.Add(new TextBlock { Text = n.Label, FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White });
            string sub = Subtitle(n);
            text.Children.Add(new TextBlock { Text = sub, FontSize = 10, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White });
            var box = new Border
            {
                Width = BoxW, Height = BoxH, CornerRadius = new CornerRadius(6), Child = text,
                Background = KindBrush(n.Kind), BorderBrush = selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
                BorderThickness = new Thickness(selected ? 2 : 1), Tag = n.Id, Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(box, Tip(n));
            box.PointerPressed += (s, e) => { Select(n.Id); e.Handled = true; };
            box.DoubleTapped += (s, e) => { OpenTaskSource(n); e.Handled = true; };
            Canvas.SetLeft(box, p.X); Canvas.SetTop(box, p.Y);
            _canvas.Children.Add(box);
            _boxes[n.Id] = box;
        }

        private static string Subtitle(BtNodeData n)
        {
            switch (n.Kind)
            {
                case BtNodeKind.Task: return "task" + ParamSummary(n);
                case BtNodeKind.Condition: return "if " + (string.IsNullOrEmpty(n.Task) ? "?" : n.Task) + (n.Abort != BtAbortMode.None ? " · abort " + n.Abort : "") + (n.Param("invert") == "true" ? " · not" : "");
                case BtNodeKind.Repeat: return n.Param("untilFailure") == "true" ? "until failure" : (n.Param("count", "0") == "0" ? "forever" : n.Param("count") + "×");
                case BtNodeKind.Cooldown: return n.Param("seconds", "1") + " s";
                case BtNodeKind.Parallel: return n.Param("any") == "true" ? "any succeeds" : "all succeed";
                default: return n.Kind.ToString().ToLowerInvariant() + (n.Children.Count > 0 ? " · " + n.Children.Count : "");
            }
        }

        private static string ParamSummary(BtNodeData n)
        {
            if (n.Params == null || n.Params.Count == 0) return "";
            return " · " + string.Join(", ", n.Params.Take(2).Select(kv => kv.Key + "=" + kv.Value));
        }

        private static string Tip(BtNodeData n)
        {
            var s = n.Kind + (string.IsNullOrEmpty(n.Task) ? "" : " " + n.Task);
            if (n.Params != null && n.Params.Count > 0) s += "\n" + string.Join("\n", n.Params.Select(kv => kv.Key + " = " + kv.Value));
            if (!string.IsNullOrEmpty(n.Comment)) s += "\n— " + n.Comment;
            return s;
        }

        private static IBrush KindBrush(BtNodeKind k)
        {
            switch (k)
            {
                case BtNodeKind.Selector: return new SolidColorBrush(Color.FromRgb(58, 88, 140));
                case BtNodeKind.Sequence: return new SolidColorBrush(Color.FromRgb(52, 104, 120));
                case BtNodeKind.Parallel: return new SolidColorBrush(Color.FromRgb(72, 92, 150));
                case BtNodeKind.Condition: return new SolidColorBrush(Color.FromRgb(150, 110, 40));
                case BtNodeKind.Task: return new SolidColorBrush(Color.FromRgb(46, 120, 96));
                default: return new SolidColorBrush(Color.FromRgb(110, 78, 140));   // decorators
            }
        }

        private void Select(string id)
        {
            if (_selectedId == id) return;
            Border old, cur;
            if (_boxes.TryGetValue(_selectedId ?? "", out old)) { old.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)); old.BorderThickness = new Thickness(1); }
            _selectedId = id;
            if (_boxes.TryGetValue(id, out cur)) { cur.BorderBrush = Brushes.White; cur.BorderThickness = new Thickness(2); }
            RefreshProps();
        }

        private void OpenTaskSource(BtNodeData n)
        {
            if (n.Kind != BtNodeKind.Task && n.Kind != BtNodeKind.Condition) return;
            string root = Editor.Core.Data.ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(n.Task)) return;
            string scripts = Path.Combine(root, "Assets", "Scripts");
            if (!Directory.Exists(scripts)) return;
            var file = Directory.EnumerateFiles(scripts, "*.cs", SearchOption.AllDirectories).FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == n.Task);
            if (file != null) EditorCommands.OpenInIde(file);
        }

        // ================================================================= properties
        private void RefreshProps()
        {
            _props.Children.Clear();
            var n = Selected;
            _props.Children.Add(new TextBlock { Text = n.Kind.ToString().ToUpperInvariant() + (n == _asset.Root ? " (ROOT)" : ""), FontSize = 11, Opacity = 0.6, Margin = new Thickness(2, 0, 0, 2) });
            _props.Children.Add(new TextBlock { Text = KindHelp(n.Kind), FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 6) });
            _props.Children.Add(Row("Name", TextEdit(n.Name, v => Edit("rename", () => n.Name = v)), "Label shown in the tree (empty = the kind / task)"));

            if (n.Kind == BtNodeKind.Task || n.Kind == BtNodeKind.Condition)
            {
                var types = BehaviorTreeService.AvailableTaskTypes().Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (!string.IsNullOrEmpty(n.Task) && !types.Contains(n.Task)) types.Insert(0, n.Task);
                var combo = new ComboBox { ItemsSource = types, SelectedItem = string.IsNullOrEmpty(n.Task) ? null : n.Task, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "choose a task class" };
                combo.SelectionChanged += (s, e) => { var v = combo.SelectedItem as string; if (!string.IsNullOrEmpty(v) && v != n.Task) Edit("task", () => { n.Task = v; FillDefaults(n); }); };
                _props.Children.Add(Row("Task", combo, "The BtTask class: a built-in or one of the project's scripts (double-click the node to open its source)"));
                var type = BehaviorTreeService.ResolveTask(n.Task);
                var probe = BehaviorTreeService.Describe(type);
                if (type == null && !string.IsNullOrEmpty(n.Task))
                    _props.Children.Add(new TextBlock { Text = "No class '" + n.Task + "' — the node fails at runtime. Define it in Assets/Scripts (public class " + n.Task + " : BtTask) or pick a built-in.", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(240, 160, 90)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 4) });
                if (probe != null && !string.IsNullOrEmpty(probe.Description))
                    _props.Children.Add(new TextBlock { Text = probe.Description, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 4) });
                if (n.Kind == BtNodeKind.Condition)
                {
                    var abort = new ComboBox { ItemsSource = Enum.GetNames(typeof(BtAbortMode)), SelectedIndex = (int)n.Abort, HorizontalAlignment = HorizontalAlignment.Stretch };
                    abort.SelectionChanged += (s, e) => { var v = (BtAbortMode)abort.SelectedIndex; if (v != n.Abort) Edit("abort mode", () => n.Abort = v); };
                    _props.Children.Add(Row("Abort", abort, "None: checked on entry. Self: aborts its own child when it fails. LowerPriority: a passing condition aborts a running later sibling (Selector) / a failing one fails the Sequence. Both: both."));
                    _props.Children.Add(Row("Invert", BoolEdit(n.Param("invert") == "true", v => SetParam(n, "invert", v ? "true" : "")), "Succeed when the task fails"));
                }
                _props.Children.Add(new TextBlock { Text = "PARAMETERS", FontSize = 11, Opacity = 0.6, Margin = new Thickness(2, 8, 0, 2) });
                var shown = new HashSet<string>(StringComparer.Ordinal);
                if (probe != null)
                    foreach (var p in probe.DescribeParams())
                    {
                        shown.Add(p.Name);
                        var pp = p;
                        _props.Children.Add(Row(p.Name, TextEdit(n.Param(p.Name, ""), v => SetParam(n, pp.Name, v), p.Default), p.Help + (string.IsNullOrEmpty(p.Default) ? "" : " (default " + p.Default + ")")));
                    }
                foreach (var kv in n.Params.ToList())
                {
                    if (shown.Contains(kv.Key) || kv.Key == "invert") continue;
                    var key = kv.Key;
                    _props.Children.Add(Row(key, TextEdit(kv.Value, v => SetParam(n, key, v)), "Extra parameter (read with Param(\"" + key + "\") in the task)"));
                }
                var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
                var newKey = new TextBox { Watermark = "new parameter", Width = 160, FontSize = 12 };
                addRow.Children.Add(newKey);
                addRow.Children.Add(EditorKit.SmallButton("+ Add", () => { string k = (newKey.Text ?? "").Trim(); if (k.Length > 0 && !n.Params.ContainsKey(k)) SetParam(n, k, " "); }, "Add a parameter the task reads with Param(name)"));
                _props.Children.Add(addRow);
            }
            else if (n.Kind == BtNodeKind.Repeat)
            {
                _props.Children.Add(Row("Count", TextEdit(n.Param("count", "0"), v => SetParam(n, "count", v)), "How many times (0 = forever)"));
                _props.Children.Add(Row("Until failure", BoolEdit(n.Param("untilFailure") == "true", v => SetParam(n, "untilFailure", v ? "true" : "")), "Success as soon as the child fails"));
            }
            else if (n.Kind == BtNodeKind.Cooldown)
                _props.Children.Add(Row("Seconds", TextEdit(n.Param("seconds", "1"), v => SetParam(n, "seconds", v)), "Failure for this long after the child finished"));
            else if (n.Kind == BtNodeKind.Parallel)
                _props.Children.Add(Row("Any succeeds", BoolEdit(n.Param("any") == "true", v => SetParam(n, "any", v ? "true" : "")), "Success as soon as one child succeeds (default: all must)"));

            _props.Children.Add(Row("Comment", TextEdit(n.Comment ?? "", v => Edit("comment", () => n.Comment = string.IsNullOrEmpty(v) ? null : v)), "A note for the tree's readers"));
            _props.Children.Add(new TextBlock { Text = n.Children.Count + " child" + (n.Children.Count == 1 ? "" : "ren") + " · id " + n.Id, FontSize = 10, Opacity = 0.5, Margin = new Thickness(2, 6, 0, 0) });
        }

        private static void FillDefaults(BtNodeData n)
        {
            var probe = BehaviorTreeService.Describe(BehaviorTreeService.ResolveTask(n.Task));
            if (probe == null) return;
            foreach (var p in probe.DescribeParams()) if (!n.Params.ContainsKey(p.Name) && !string.IsNullOrEmpty(p.Default)) n.Params[p.Name] = p.Default;
        }

        private static Control Row(string label, Control editor, string tip)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), Margin = new Thickness(0, 1) };
            var l = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
            if (!string.IsNullOrEmpty(tip)) { ToolTip.SetTip(l, tip); ToolTip.SetTip(editor, tip); }
            Grid.SetColumn(editor, 1);
            g.Children.Add(l); g.Children.Add(editor);
            return g;
        }

        private static TextBox TextEdit(string value, Action<string> commit, string watermark = null)
        {
            var tb = new TextBox { Text = value ?? "", FontSize = 12, Watermark = watermark ?? "", HorizontalAlignment = HorizontalAlignment.Stretch };
            string last = value ?? "";
            void Commit() { string v = (tb.Text ?? "").Trim(); if (v == last) return; last = v; commit(v); }
            tb.LostFocus += (s, e) => Commit();
            tb.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            return tb;
        }

        private static CheckBox BoolEdit(bool value, Action<bool> commit)
        {
            var c = new CheckBox { IsChecked = value };
            c.IsCheckedChanged += (s, e) => commit(c.IsChecked == true);
            return c;
        }

        private void RefreshBlackboard()
        {
            _blackboardPanel.Children.Clear();
            foreach (var kv in _asset.Blackboard.ToList())
            {
                var key = kv.Key;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*,auto"), Margin = new Thickness(0, 1) };
                var l = new TextBlock { Text = key, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
                var tb = TextEdit(kv.Value, v => Edit("blackboard " + key, () => _asset.Blackboard[key] = v));
                var del = EditorKit.SmallButton("×", () => Edit("remove " + key, () => _asset.Blackboard.Remove(key)), "Remove the default");
                Grid.SetColumn(tb, 1); Grid.SetColumn(del, 2);
                row.Children.Add(l); row.Children.Add(tb); row.Children.Add(del);
                _blackboardPanel.Children.Add(row);
            }
            var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
            var newKey = new TextBox { Watermark = "key", Width = 120, FontSize = 12 };
            var newVal = new TextBox { Watermark = "value", Width = 150, FontSize = 12 };
            addRow.Children.Add(newKey); addRow.Children.Add(newVal);
            addRow.Children.Add(EditorKit.SmallButton("+ Add", () => { string k = (newKey.Text ?? "").Trim(); if (k.Length > 0) Edit("blackboard " + k, () => _asset.Blackboard[k] = (newVal.Text ?? "").Trim()); }, "A value every run of this tree starts with (tasks read it typed)"));
            _blackboardPanel.Children.Add(addRow);
            _blackboardPanel.Children.Add(new TextBlock { Text = "Tasks read keys typed (GetFloat, GetEntity, …) and write their results here; {key} in a parameter inserts a value.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) });
        }

        // ================================================================= live view
        private void PollLive()
        {
            if (!_live) return;
            BehaviorTreeRunner runner = null;
            try { runner = BehaviorTreeService.IsRunning ? BehaviorTreeService.FirstRunnerOf(BehaviorTreeService.Relative(_path)) : null; } catch { }
            if (runner == null)
            {
                if (_liveRunner != null) ClearLive();
                return;
            }
            if (!ReferenceEquals(runner, _liveRunner)) { _liveRunner = runner; _liveBlackboardVersion = -1; }
            runner.Snapshot(_snapshot);
            int now = runner.TickIndex;
            foreach (var d in _snapshot)
            {
                Border box;
                if (!_boxes.TryGetValue(d.Id, out box)) continue;
                bool recent = d.LastTick >= 0 && now - d.LastTick <= 1;
                if (d.Running) box.BorderBrush = Brushes.Gold;
                else if (recent) box.BorderBrush = d.Status == Vortex.BtStatus.Success ? Brushes.LimeGreen : Brushes.OrangeRed;
                else box.BorderBrush = d.Id == _selectedId ? Brushes.White : new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
                box.BorderThickness = new Thickness(d.Running || recent || d.Id == _selectedId ? 2 : 1);
            }
            string who = "";
            try { foreach (var kv in BehaviorTreeService.Runners()) if (ReferenceEquals(kv.Value, runner)) { who = kv.Key.Name; break; } } catch { }
            string active = runner.ActiveTaskLabel;
            _status.Text = "Live: " + who + (string.IsNullOrEmpty(active) ? "" : " · " + active) + " · tick " + now;
            if (runner.Blackboard.Version != _liveBlackboardVersion)
            {
                _liveBlackboardVersion = runner.Blackboard.Version;
                ShowLiveBlackboard(runner);
            }
        }

        private void ShowLiveBlackboard(BehaviorTreeRunner runner)
        {
            _blackboardPanel.Children.Clear();
            _blackboardPanel.Children.Add(new TextBlock { Text = "live values of the running agent (read-only)", FontSize = 11, Opacity = 0.6, Margin = new Thickness(2, 0, 0, 4) });
            foreach (var kv in runner.Blackboard.Dump().OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*") };
                var l = new TextBlock { Text = kv.Key, FontSize = 12, Opacity = 0.85 };
                var v = new TextBlock { Text = kv.Value, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(v, 1);
                row.Children.Add(l); row.Children.Add(v);
                _blackboardPanel.Children.Add(row);
            }
        }

        private void ClearLive()
        {
            _liveRunner = null;
            _status.Text = "";
            foreach (var kv in _boxes)
            {
                bool sel = kv.Key == _selectedId;
                kv.Value.BorderBrush = sel ? Brushes.White : new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
                kv.Value.BorderThickness = new Thickness(sel ? 2 : 1);
            }
            RefreshBlackboard();
        }

        // ================================================================= file
        private bool Save()
        {
            try
            {
                if (!_asset.Save(_path)) { EditorCommands.Toast("Could not save " + Path.GetFileName(_path)); return false; }
                _savedJson = _asset.ToJson();
                UpdateTitle();
                EditorCommands.Toast("Saved " + Path.GetFileName(_path));
                return true;
            }
            catch (Exception ex) { EditorCommands.Fail("Save behavior tree", ex); return false; }
        }

        private void Revert()
        {
            var a = BehaviorTreeAsset.Load(_path);
            if (a == null) return;
            Edit("revert", () => { _asset = a; _asset.Normalize(); if (_asset.Find(_selectedId) == null) _selectedId = _asset.Root.Id; });
        }

        private void UpdateTitle()
        {
            string name = Path.GetFileNameWithoutExtension(_path);
            _title.Text = name + (IsDirty ? " •" : "");
            Title = "Behavior Tree — " + name + (IsDirty ? " •" : "");
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            bool cmd = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            if (e.Source is TextBox) { if (!(cmd && e.Key == Key.S)) return; }
            if (cmd && e.Key == Key.S) { Save(); e.Handled = true; }
            else if (cmd && e.Key == Key.Z && (e.KeyModifiers & KeyModifiers.Shift) != 0) { UndoRedoManager.Instance.Redo(); e.Handled = true; }
            else if (cmd && e.Key == Key.Z) { UndoRedoManager.Instance.Undo(); e.Handled = true; }
            else if (cmd && e.Key == Key.Y) { UndoRedoManager.Instance.Redo(); e.Handled = true; }
            else if (cmd && e.Key == Key.X) { CutCopy(true); e.Handled = true; }
            else if (cmd && e.Key == Key.C) { CutCopy(false); e.Handled = true; }
            else if (cmd && e.Key == Key.V) { Paste(); e.Handled = true; }
            else if (cmd && e.Key == Key.D) { CutCopy(false); var parent = _asset.ParentOf(_selectedId); if (parent != null) { string keep = _selectedId; _selectedId = parent.Id; Paste(); if (_asset.Find(_selectedId) == null) _selectedId = keep; } e.Handled = true; }
            else if (e.Key == Key.Delete || e.Key == Key.Back) { DeleteSelected(); e.Handled = true; }
        }

        private async void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (_closeConfirmed || !IsDirty) return;
            if (e.CloseReason == WindowCloseReason.OwnerWindowClosing || e.CloseReason == WindowCloseReason.ApplicationShutdown || e.CloseReason == WindowCloseReason.OSShutdown) return;
            e.Cancel = true;
            int r = await EditorKit.Choose(this, "Save changes to " + Path.GetFileName(_path) + "?", "Your changes are lost if you don't save them.", "Save", "Don't Save", "Cancel");
            if (r == 2) return;
            if (r == 0 && !Save()) return;
            _closeConfirmed = true;
            Close();
        }

        internal void CloseDiscarding() { _closeConfirmed = true; Close(); }
        private void OnProjectClosed() => Dispatcher.UIThread.Post(CloseDiscarding);
    }
}
