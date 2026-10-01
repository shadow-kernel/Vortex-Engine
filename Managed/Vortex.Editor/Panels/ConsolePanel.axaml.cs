using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Services;
using VortexEditor.Shell;

namespace VortexEditor.Panels
{
    /// <summary>One visible console line (or a collapsed group of identical lines).</summary>
    public sealed class ConsoleRow : INotifyPropertyChanged
    {
        private static readonly Dictionary<string, IBrush> _brushes = new Dictionary<string, IBrush>();
        private int _count = 1;

        public LogEntry Entry { get; }
        public string Time => Entry.Time;
        public string LevelTag => Entry.LevelTag;
        public string Message => Entry.Message;
        public LogLevel Level => Entry.Level;
        // info lines use the theme's text colours (the core's light grey would vanish on the light theme)
        public IBrush Brush => Entry.Level == LogLevel.Info ? Res("VxTextSecondaryBrush") : BrushFor(Entry.Color);
        public IBrush MessageBrush => Entry.Level == LogLevel.Info ? Res("VxTextBrush") : BrushFor(Entry.Color);
        private static IBrush Res(string key) => Application.Current != null && Application.Current.TryFindResource(key, Application.Current.ActualThemeVariant, out var b) ? b as IBrush : null;
        public int Count { get => _count; set { if (_count == value) return; _count = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCount))); } }
        public string CountText => _count > 999 ? "999+" : _count.ToString();
        public bool HasCount => _count > 1;
        public string SourceFile { get; }
        public int SourceLine { get; }
        public bool HasSource => SourceFile != null;
        public string SourceHint => SourceFile != null ? "Double-click to open " + Path.GetFileName(SourceFile) + (SourceLine > 0 ? ":" + SourceLine : "") : null;
        public event PropertyChangedEventHandler PropertyChanged;

        public ConsoleRow(LogEntry e)
        {
            Entry = e;
            if (ConsolePanel.TryParseSource(e.Message, out var f, out int line)) { SourceFile = f; SourceLine = line; }
        }

        private static IBrush BrushFor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            if (_brushes.TryGetValue(hex, out var b)) return b;
            try { b = new SolidColorBrush(Color.Parse(hex)); } catch { b = null; }
            _brushes[hex] = b;
            return b;
        }

        public string ToText() => "[" + Time + "] " + LevelTag + "  " + Message + (_count > 1 ? "  (x" + _count + ")" : "");
    }

    /// <summary>
    /// The game-log console (WPF ConsoleView parity + extras): Info / Warning / Error filters with live counts (engine
    /// "▶" lines always show), text search, auto-scroll, clear, collapse identical lines, copy (⌘C in the list or the
    /// Copy button), and double-click to open a script error / stack-trace location in the IDE.
    /// </summary>
    public partial class ConsolePanel : UserControl
    {
        private readonly ObservableCollection<ConsoleRow> _rows = new ObservableCollection<ConsoleRow>();
        private readonly Dictionary<string, ConsoleRow> _groups = new Dictionary<string, ConsoleRow>();
        private readonly List<LogEntry> _pending = new List<LogEntry>();
        private readonly DispatcherTimer _flush;
        private bool _rebuildPending;

        public ConsolePanel()
        {
            InitializeComponent();
            List.ItemsSource = _rows;
            ConsoleService.Instance.Entries.CollectionChanged += OnEntriesChanged;
            _flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _flush.Tick += (s, e) => Flush();
            List.DoubleTapped += (s, e) => { if (List.SelectedItem is ConsoleRow r && r.HasSource) OpenSource(r); };
            List.ContextRequested += OnContextRequested;
            List.KeyDown += (s, e) =>
            {
                bool cmd = OperatingSystem.IsMacOS() ? e.KeyModifiers.HasFlag(KeyModifiers.Meta) : e.KeyModifiers.HasFlag(KeyModifiers.Control);
                if (cmd && e.Key == Key.C) { _ = CopyAsync(); e.Handled = true; }
                else if (e.Key == Key.Return && List.SelectedItem is ConsoleRow r && r.HasSource) { OpenSource(r); e.Handled = true; }
            };
            // ⌘C / ⌘A from the menu bar act on the log while it has focus (not on scene entities)
            EditorCommands.RegisterEditHandler(List, a =>
            {
                if (a == EditorCommands.EditAction.Copy) { _ = CopyAsync(); return true; }
                if (a == EditorCommands.EditAction.SelectAll) { List.SelectAll(); return true; }
                return a == EditorCommands.EditAction.Cut || a == EditorCommands.EditAction.Paste || a == EditorCommands.EditAction.Delete || a == EditorCommands.EditAction.Duplicate;
            });
            AttachedToVisualTree += (s, e) =>
            {
                ConsoleService.Instance.AttachPlayMode();   // "Play started / stopped" banners + Console.WriteLine capture while playing
                ConsoleService.Instance.GreetOnce();
                Rebuild();
            };
            Rebuild();
        }

        /// <summary>Rows currently shown (smoke checks).</summary>
        public IReadOnlyList<ConsoleRow> VisibleRows => _rows;
        public bool Collapse { get => CollapseToggle.IsChecked == true; set { CollapseToggle.IsChecked = value; Rebuild(); } }
        public string SearchText { get => Filter.Text; set => Filter.Text = value; }
        public void SetLevelVisible(LogLevel level, bool on)
        {
            if (level == LogLevel.Error) ChipError.IsChecked = on; else if (level == LogLevel.Warning) ChipWarn.IsChecked = on; else ChipInfo.IsChecked = on;
            Rebuild();
        }

        private void OnEntriesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (LogEntry le in e.NewItems) _pending.Add(le);
            }
            else _rebuildPending = true;   // clear / trim of the oldest lines
            if (!_flush.IsEnabled) _flush.Start();
        }

        /// <summary>Apply the queued console lines (batched: a script that logs every frame costs one UI update per tick).</summary>
        public void Flush()
        {
            _flush.Stop();
            if (_rebuildPending) { _rebuildPending = false; _pending.Clear(); Rebuild(); return; }
            if (_pending.Count == 0) { UpdateCounts(); return; }
            foreach (var le in _pending) Add(le);
            _pending.Clear();
            while (_rows.Count > ConsoleService.MaxEntries) RemoveRow(_rows[0]);
            UpdateCounts();
            ScrollToEnd();
        }

        private void Add(LogEntry le)
        {
            if (!Passes(le)) return;
            if (Collapse)
            {
                string key = GroupKey(le);
                if (_groups.TryGetValue(key, out var g)) { g.Count++; return; }
                var row = new ConsoleRow(le);
                _groups[key] = row;
                _rows.Add(row);
            }
            else _rows.Add(new ConsoleRow(le));
        }

        private void RemoveRow(ConsoleRow r)
        {
            _rows.Remove(r);
            if (Collapse) _groups.Remove(GroupKey(r.Entry));
        }

        private static string GroupKey(LogEntry le) => ((int)le.Level) + "\u0001" + le.Message;

        private bool Passes(LogEntry e)
        {
            if (e == null) return false;
            switch (e.Level)
            {
                case LogLevel.Error: if (ChipError.IsChecked != true) return false; break;
                case LogLevel.Warning: if (ChipWarn.IsChecked != true) return false; break;
                case LogLevel.Info: if (ChipInfo.IsChecked != true) return false; break;
                default: break;   // "▶" engine lines always show (WPF parity)
            }
            string f = Filter.Text;
            return string.IsNullOrEmpty(f) || (e.Message != null && e.Message.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public void Rebuild()
        {
            _rows.Clear();
            _groups.Clear();
            foreach (var e in ConsoleService.Instance.Entries.ToList()) Add(e);
            UpdateCounts();
            ScrollToEnd();
        }

        private void ScrollToEnd()
        {
            if (AutoScroll.IsChecked == true && _rows.Count > 0) { try { List.ScrollIntoView(_rows[_rows.Count - 1]); } catch { } }
        }

        private void UpdateCounts()
        {
            var c = ConsoleService.Instance;
            InfoCount.Text = c.InfoCount.ToString();
            WarnCount.Text = c.WarnCount.ToString();
            ErrorCount.Text = c.ErrorCount.ToString();
        }

        private void OnChip(object sender, RoutedEventArgs e) => Rebuild();
        private void OnCollapse(object sender, RoutedEventArgs e) => Rebuild();
        private void OnFilterChanged(object sender, TextChangedEventArgs e) => Rebuild();
        private void OnClear(object sender, RoutedEventArgs e) { ConsoleService.Instance.Clear(); _pending.Clear(); _rows.Clear(); _groups.Clear(); UpdateCounts(); }
        private void OnCopy(object sender, RoutedEventArgs e) => _ = CopyAsync();

        /// <summary>Copy the selected lines, or every visible line when nothing is selected.</summary>
        public async Task<string> CopyAsync()
        {
            var sel = List.SelectedItems?.OfType<ConsoleRow>().ToList();
            var rows = sel != null && sel.Count > 0 ? _rows.Where(sel.Contains).ToList() : _rows.ToList();
            var sb = new StringBuilder();
            foreach (var r in rows) sb.AppendLine(r.ToText());
            string text = sb.ToString();
            try { var cb = TopLevel.GetTopLevel(this)?.Clipboard; if (cb != null) await cb.SetTextAsync(text); } catch { }
            EditorCommands.Toast(rows.Count == 1 ? "Copied 1 line" : "Copied " + rows.Count + " lines");
            return text;
        }

        private void OnContextRequested(object sender, ContextRequestedEventArgs e)
        {
            var m = new ContextMenu();
            var row = List.SelectedItem as ConsoleRow;
            if (row != null && row.HasSource)
            {
                var open = new MenuItem { Header = "Open " + Path.GetFileName(row.SourceFile) + (row.SourceLine > 0 ? ":" + row.SourceLine : "") };
                open.Click += (s, a) => OpenSource(row);
                m.Items.Add(open);
                m.Items.Add(new Separator());
            }
            var copy = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) };
            copy.Click += (s, a) => _ = CopyAsync();
            var all = new MenuItem { Header = "Select All" };
            all.Click += (s, a) => List.SelectAll();
            var clear = new MenuItem { Header = "Clear" };
            clear.Click += (s, a) => OnClear(null, null);
            m.Items.Add(copy); m.Items.Add(all); m.Items.Add(new Separator()); m.Items.Add(clear);
            m.Open(List);
            e.Handled = true;
        }

        // ------------------------------------------------------------ source links

        // "Assets/Scripts/Foo.cs(12,5): error CS1002", "... in /path/Foo.cs:line 42", "Foo.cs:42"
        private static readonly Regex SourceRx = new Regex(@"(?<file>(?:[A-Za-z]:)?[^\s""'()\[\]:]*?\.cs)(?:\((?<l1>\d+)(?:,\d+)?\)|:line (?<l2>\d+)|:(?<l3>\d+))", RegexOptions.Compiled);

        /// <summary>Find a C# source location in a log line and resolve it to an existing file of the project.</summary>
        public static bool TryParseSource(string message, out string file, out int line)
        {
            file = null; line = 0;
            if (string.IsNullOrEmpty(message) || message.IndexOf(".cs", StringComparison.OrdinalIgnoreCase) < 0) return false;
            var m = SourceRx.Match(message);
            if (!m.Success) return false;
            string raw = m.Groups["file"].Value;
            int.TryParse(m.Groups["l1"].Success ? m.Groups["l1"].Value : m.Groups["l2"].Success ? m.Groups["l2"].Value : m.Groups["l3"].Value, out line);
            file = ResolveSource(raw);
            return file != null;
        }

        private static string ResolveSource(string raw)
        {
            try
            {
                if (Path.IsPathRooted(raw) && File.Exists(raw)) return raw;
                var root = ProjectData.Current?.Path;
                if (string.IsNullOrEmpty(root)) return null;
                var p = Path.Combine(root, raw.Replace('\\', '/'));
                if (File.Exists(p)) return p;
                string name = Path.GetFileName(raw);
                var assets = Path.Combine(root, "Assets");
                if (Directory.Exists(assets))
                    return Directory.EnumerateFiles(assets, name, SearchOption.AllDirectories).FirstOrDefault();
            }
            catch { }
            return null;
        }

        private static void OpenSource(ConsoleRow r) => EditorCommands.OpenInIde(r.SourceFile, r.SourceLine);
    }
}
