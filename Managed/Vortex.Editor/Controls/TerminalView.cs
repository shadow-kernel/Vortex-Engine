using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Claude.Terminal;

namespace VortexEditor.Controls
{
    /// <summary>
    /// A terminal inside the editor: it runs a program in a pseudo-terminal (<see cref="Pty"/>), shows its screen
    /// (<see cref="VtScreen"/>: colours, cursor, a full-screen interface) and sends it what the user types — the way
    /// Terminal or the terminal of VS Code would. Drag to select, ⌘C / Ctrl+Shift+C copies, ⌘V / Ctrl+Shift+V pastes,
    /// the wheel scrolls back.
    /// </summary>
    public sealed class TerminalView : Control
    {
        private VtScreen _screen;
        private Pty _pty;
        private readonly object _gate = new object();
        private Typeface _font, _bold;
        private double _cellW = 7.5, _cellH = 16, _fontSize = 12.5;
        private int _scrollBack;            // lines scrolled into the history (0 = the live screen)
        private bool _repaintQueued;
        private long _shownVersion = -1;
        private bool _focused;
        // selection in screen-history coordinates: row (negative = scrollback), column
        private (int row, int col)? _selStart, _selEnd;
        private bool _selecting;

        public static readonly StyledProperty<IBrush> TerminalBackgroundProperty = AvaloniaProperty.Register<TerminalView, IBrush>(nameof(TerminalBackground));
        public IBrush TerminalBackground { get => GetValue(TerminalBackgroundProperty); set => SetValue(TerminalBackgroundProperty, value); }

        /// <summary>The program ended; the argument is its exit code (on the UI thread).</summary>
        public event Action<int> Exited;
        /// <summary>The program set the window title.</summary>
        public event Action<string> TitleChanged;

        public bool IsRunning => _pty != null && !_pty.HasExited;
        /// <summary>The screen (tests read it).</summary>
        public VtScreen Screen => _screen;
        public int Columns => _screen?.Cols ?? 0;
        public int RowsCount => _screen?.Rows ?? 0;

        public TerminalView()
        {
            Focusable = true;
            ClipToBounds = true;
            Cursor = new Cursor(StandardCursorType.Ibeam);
            _screen = new VtScreen(80, 24);
            _screen.Reply += s => _pty?.Write(s);
            _screen.TitleChanged += t => Dispatcher.UIThread.Post(() => TitleChanged?.Invoke(t));
        }

        // ================================================================== process

        /// <summary>Start <paramref name="file"/> in the terminal (the previous program, if any, is ended first).</summary>
        public void Start(string file, IReadOnlyList<string> args, string cwd, IDictionary<string, string> env = null)
        {
            Stop();
            MeasureCell();
            var (cols, rows) = GridSize(Bounds.Size);
            lock (_gate)
            {
                _screen = new VtScreen(cols, rows) { DefaultForeground = Rgb(Foreground()), DefaultBackground = Rgb(Background()) };
                _screen.Reply += s => _pty?.Write(s);
                _screen.TitleChanged += t => Dispatcher.UIThread.Post(() => TitleChanged?.Invoke(t));
                _scrollBack = 0;
                _selStart = _selEnd = null;
            }
            var pty = Pty.Start(file, args, cwd, env ?? Pty.TerminalEnvironment(), cols, rows);
            pty.Output += (buf, n) =>
            {
                lock (_gate) _screen.Feed(buf, 0, n);
                QueueRepaint();
            };
            pty.Exited += code => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_pty, pty)) Exited?.Invoke(code); });
            _pty = pty;
            QueueRepaint();
        }

        /// <summary>End the program (as closing a terminal window would).</summary>
        public void Stop()
        {
            var p = _pty;
            _pty = null;
            if (p != null) { try { p.Dispose(); } catch { } }
        }

        /// <summary>Type text into the program (tests, "send to Claude Code").</summary>
        public void SendText(string text) => _pty?.Write(text);

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            // the panel can be re-attached (sidebar closed and opened): the program keeps running
        }

        // ================================================================== layout

        private void MeasureCell()
        {
            var family = Application.Current != null && Application.Current.TryFindResource("VxMono", out var f) && f is FontFamily ff ? ff : new FontFamily("Menlo, Consolas, monospace");
            _font = new Typeface(family);
            _bold = new Typeface(family, FontStyle.Normal, FontWeight.Bold);
            var probe = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _font, _fontSize, Brushes.White);
            _cellW = Math.Max(4, probe.WidthIncludingTrailingWhitespace / 10.0);
            _cellH = Math.Ceiling(Math.Max(10, probe.Height * 1.08));
        }

        private (int cols, int rows) GridSize(Size size)
        {
            int cols = Math.Max(20, (int)Math.Floor((size.Width - 8) / _cellW));
            int rows = Math.Max(5, (int)Math.Floor((size.Height - 6) / _cellH));
            return (cols, rows);
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (_font.FontFamily == null) MeasureCell();
            var (cols, rows) = GridSize(e.NewSize);
            lock (_gate)
            {
                if (cols == _screen.Cols && rows == _screen.Rows) return;
                _screen.Resize(cols, rows);
            }
            _pty?.Resize(cols, rows);
            QueueRepaint();
        }

        private void QueueRepaint()
        {
            if (_repaintQueued) return;
            _repaintQueued = true;
            // at most one repaint per frame, however much output arrives
            Dispatcher.UIThread.Post(() => { _repaintQueued = false; InvalidateVisual(); }, DispatcherPriority.Render);
        }

        // ================================================================== drawing

        private static readonly uint[] Palette =
        {
            0xFF1E1E20, 0xFFFF5F58, 0xFF5FD068, 0xFFE5C07B, 0xFF5AA9FF, 0xFFC678DD, 0xFF56B6C2, 0xFFD0D0D4,
            0xFF6E6E74, 0xFFFF7B74, 0xFF7EE28A, 0xFFF2D58F, 0xFF7CBCFF, 0xFFD89BE8, 0xFF7ACFD9, 0xFFFFFFFF,
        };

        private Color Foreground() => Resource("VxTextBrush", Color.FromRgb(0xF2, 0xF2, 0xF4));
        private Color Background() => TerminalBackground is ISolidColorBrush b ? b.Color : Resource("VxFieldBrush", Color.FromRgb(0x1A, 0x1A, 0x1C));

        private Color Resource(string key, Color fallback) =>
            Application.Current != null && Application.Current.TryFindResource(key, Application.Current.ActualThemeVariant, out var r) && r is ISolidColorBrush b ? b.Color : fallback;

        private static int Rgb(Color c) => c.R << 16 | c.G << 8 | c.B;

        private Color ColorOf(int c, bool fg)
        {
            if (c < 0) return fg ? Foreground() : Background();
            if (c >= 0x01000000) return Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
            if (c < 16) return Color.FromUInt32(Palette[c]);
            if (c < 232)
            {
                int i = c - 16, r = i / 36, g = i / 6 % 6, b = i % 6;
                byte L(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
                return Color.FromRgb(L(r), L(g), L(b));
            }
            byte gray = (byte)(8 + (c - 232) * 10);
            return Color.FromRgb(gray, gray, gray);
        }

        public override void Render(DrawingContext ctx)
        {
            var bg = Background();
            ctx.FillRectangle(new SolidColorBrush(bg), new Rect(Bounds.Size));
            if (_font.FontFamily == null) MeasureCell();
            lock (_gate)
            {
                var s = _screen;
                _shownVersion = s.Version;
                int history = s.ScrollbackCount;
                _scrollBack = Math.Clamp(_scrollBack, 0, history);
                const double padX = 4, padY = 3;
                var sel = Selection();
                for (int y = 0; y < s.Rows; y++)
                {
                    int row = y - _scrollBack;
                    var line = s.Line(row);
                    if (line == null) continue;
                    double top = padY + y * _cellH;
                    int x = 0;
                    while (x < line.Length)
                    {
                        // a run of cells that look the same (and plain ASCII): one text draw
                        var c = line[x];
                        bool selected = InSelection(sel, row, x);
                        Attributes(c, selected, out var fgc, out var bgc);
                        int end = x + 1;
                        bool simple = IsSimple(c);
                        if (simple)
                            while (end < line.Length && IsSimple(line[end]) && Same(line[end], c) && InSelection(sel, row, end) == selected) end++;
                        double left = padX + x * _cellW;
                        int width = end - x;
                        if (!simple && end < line.Length && line[end].Flags.HasFlag(CellFlags.WideTail)) width = 2;
                        if (bgc != bg) ctx.FillRectangle(new SolidColorBrush(bgc), new Rect(left, top, width * _cellW, _cellH));
                        if (!c.Flags.HasFlag(CellFlags.Hidden))
                        {
                            string text = simple ? Text(line, x, end) : c.Text;
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                                           c.Flags.HasFlag(CellFlags.Bold) ? _bold : _font, _fontSize, new SolidColorBrush(fgc));
                                if (c.Flags.HasFlag(CellFlags.Italic)) ft.SetFontStyle(FontStyle.Italic);
                                if (c.Flags.HasFlag(CellFlags.Underline)) ft.SetTextDecorations(TextDecorations.Underline);
                                if (c.Flags.HasFlag(CellFlags.Strike)) ft.SetTextDecorations(TextDecorations.Strikethrough);
                                // a glyph from a fallback font may be wider than a cell: keep it centred in its cells
                                double dx = simple ? 0 : Math.Max(0, (width * _cellW - ft.WidthIncludingTrailingWhitespace) / 2);
                                ctx.DrawText(ft, new Point(left + dx, top + (_cellH - ft.Height) / 2));
                            }
                        }
                        x = simple ? end : x + Math.Max(1, width);
                    }
                }
                // the cursor (not while looking at the history)
                if (s.CursorVisible && _scrollBack == 0 && _pty != null)
                {
                    var r = new Rect(padX + s.CursorX * _cellW, padY + s.CursorY * _cellH, _cellW, _cellH);
                    var fg = Foreground();
                    if (_focused)
                    {
                        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(200, fg.R, fg.G, fg.B)), r);
                        var cell = s.CellAt(s.CursorX, s.CursorY);
                        if (!cell.IsBlank)
                            ctx.DrawText(new FormattedText(cell.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _font, _fontSize, new SolidColorBrush(bg)),
                                         new Point(r.X, r.Y + (_cellH - _cellH / 1.08) / 2));
                    }
                    else ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(160, fg.R, fg.G, fg.B)), 1), r.Deflate(0.5));
                }
            }
        }

        private void Attributes(Cell c, bool selected, out Color fg, out Color bg)
        {
            fg = ColorOf(c.Fg, true);
            bg = ColorOf(c.Bg, false);
            if (c.Flags.HasFlag(CellFlags.Inverse)) (fg, bg) = (bg, fg);
            if (c.Flags.HasFlag(CellFlags.Dim)) fg = Color.FromArgb(150, fg.R, fg.G, fg.B);
            if (selected)
            {
                var a = Resource("VxAccentBrush", Color.FromRgb(0x0A, 0x84, 0xFF));
                bg = Color.FromRgb((byte)((a.R + bg.R * 2) / 3), (byte)((a.G + bg.G * 2) / 3), (byte)((a.B + bg.B * 2) / 3));
            }
        }

        private static bool IsSimple(Cell c) => string.IsNullOrEmpty(c.Text) || (c.Text.Length == 1 && c.Text[0] < 0x7F && !c.Flags.HasFlag(CellFlags.WideTail));
        private static bool Same(Cell a, Cell b) => a.Fg == b.Fg && a.Bg == b.Bg && (a.Flags & ~CellFlags.WideTail) == (b.Flags & ~CellFlags.WideTail);

        private static string Text(Cell[] line, int from, int to)
        {
            var sb = new StringBuilder(to - from);
            for (int i = from; i < to; i++) sb.Append(string.IsNullOrEmpty(line[i].Text) ? " " : line[i].Text);
            return sb.ToString();
        }

        // ================================================================== keyboard

        protected override void OnGotFocus(GotFocusEventArgs e) { base.OnGotFocus(e); _focused = true; if (_screen.FocusReporting) _pty?.Write("\x1b[I"); InvalidateVisual(); }
        protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e) { base.OnLostFocus(e); _focused = false; if (_screen.FocusReporting) _pty?.Write("\x1b[O"); InvalidateVisual(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_pty == null) return;
            bool mac = OperatingSystem.IsMacOS();
            var m = e.KeyModifiers;
            bool ctrl = m.HasFlag(KeyModifiers.Control), alt = m.HasFlag(KeyModifiers.Alt), shift = m.HasFlag(KeyModifiers.Shift), meta = m.HasFlag(KeyModifiers.Meta);

            // copy / paste: ⌘C ⌘V on macOS, Ctrl+Shift+C / V elsewhere
            bool copyCombo = mac ? meta && e.Key == Key.C : ctrl && shift && e.Key == Key.C;
            bool pasteCombo = mac ? meta && e.Key == Key.V : ctrl && shift && e.Key == Key.V;
            if (copyCombo) { _ = CopySelection(); e.Handled = true; return; }
            if (pasteCombo) { _ = Paste(); e.Handled = true; return; }
            if (mac && meta && e.Key == Key.A) { SelectAll(); e.Handled = true; return; }
            if (meta) return;   // other ⌘ shortcuts belong to the editor

            string seq = KeySequence(e.Key, ctrl, alt, shift);
            if (seq != null)
            {
                Send(seq);
                e.Handled = true;
            }
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (_pty == null || string.IsNullOrEmpty(e.Text)) return;
            Send(e.Text);
            e.Handled = true;
        }

        private void Send(string s)
        {
            if (_scrollBack != 0) { _scrollBack = 0; InvalidateVisual(); }
            _selStart = _selEnd = null;
            _pty?.Write(s);
        }

        private string KeySequence(Key key, bool ctrl, bool alt, bool shift)
        {
            bool app = _screen.ApplicationCursorKeys;
            int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
            string Cursor(char c) => mod > 1 ? "\x1b[1;" + mod + c : (app ? "\x1bO" : "\x1b[") + c;
            string Tilde(int n) => mod > 1 ? "\x1b[" + n + ";" + mod + "~" : "\x1b[" + n + "~";
            switch (key)
            {
                case Key.Enter: return shift || alt ? "\x1b\r" : "\r";   // Shift / Option+Enter: a new line in Claude Code
                case Key.Back: return alt ? "\x1b\x7f" : ctrl ? "\x08" : "\x7f";
                case Key.Tab: return shift ? "\x1b[Z" : "\t";
                case Key.Escape: return "\x1b";
                case Key.Up: return Cursor('A');
                case Key.Down: return Cursor('B');
                case Key.Right: return Cursor('C');
                case Key.Left: return Cursor('D');
                case Key.Home: return mod > 1 ? "\x1b[1;" + mod + "H" : app ? "\x1bOH" : "\x1b[H";
                case Key.End: return mod > 1 ? "\x1b[1;" + mod + "F" : app ? "\x1bOF" : "\x1b[F";
                case Key.PageUp: return Tilde(5);
                case Key.PageDown: return Tilde(6);
                case Key.Delete: return Tilde(3);
                case Key.Insert: return Tilde(2);
                case Key.F1: return "\x1bOP";
                case Key.F2: return "\x1bOQ";
                case Key.F3: return "\x1bOR";
                case Key.F4: return "\x1bOS";
                case Key.F5: return Tilde(15);
                case Key.F6: return Tilde(17);
                case Key.F7: return Tilde(18);
                case Key.F8: return Tilde(19);
                case Key.F9: return Tilde(20);
                case Key.F10: return Tilde(21);
                case Key.F11: return Tilde(23);
                case Key.F12: return Tilde(24);
            }
            if (ctrl && !alt)
            {
                // Ctrl+letter → its control character (Ctrl+C interrupts, Ctrl+D ends input …)
                if (key >= Key.A && key <= Key.Z) return ((char)(key - Key.A + 1)).ToString();
                switch (key)
                {
                    case Key.Space: case Key.D2: return "\0";
                    case Key.OemOpenBrackets: return "\x1b";
                    case Key.OemPipe: case Key.OemBackslash: return "\x1c";
                    case Key.OemCloseBrackets: return "\x1d";
                    case Key.OemMinus: return "\x1f";
                }
            }
            return null;   // printable text arrives through OnTextInput
        }

        // ================================================================== mouse: focus, selection, scrolling

        private (int row, int col) CellAtPoint(Point p)
        {
            int y = (int)Math.Floor((p.Y - 3) / _cellH), x = (int)Math.Floor((p.X - 4) / _cellW);
            y = Math.Clamp(y, 0, _screen.Rows - 1);
            x = Math.Clamp(x, 0, _screen.Cols - 1);
            return (y - _scrollBack, x);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var p = e.GetCurrentPoint(this);
            if (!p.Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) { SelectWord(CellAtPoint(p.Position)); e.Handled = true; return; }
            _selStart = _selEnd = CellAtPoint(p.Position);
            _selecting = true;
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_selecting) return;
            _selEnd = CellAtPoint(e.GetPosition(this));
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_selecting) return;
            _selecting = false;
            e.Pointer.Capture(null);
            if (_selStart == _selEnd) _selStart = _selEnd = null;   // a click, not a selection
            InvalidateVisual();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            int lines = (int)Math.Round(e.Delta.Y * 3);
            if (lines == 0) return;
            if (_screen.MouseTracking != 0 && _screen.MouseSgr)
            {
                // the program scrolls itself (it asked for mouse reports)
                var (row, col) = CellAtPoint(e.GetPosition(this));
                for (int i = 0; i < Math.Abs(lines); i++) _pty?.Write("\x1b[<" + (lines > 0 ? 64 : 65) + ";" + (col + 1) + ";" + (row + _scrollBack + 1) + "M");
            }
            else if (_screen.IsAlternateScreen)
            {
                // a full-screen program without mouse reports: arrow keys, as other terminals do
                for (int i = 0; i < Math.Abs(lines); i++) _pty?.Write(lines > 0 ? "\x1b[A" : "\x1b[B");
            }
            else
            {
                lock (_gate) _scrollBack = Math.Clamp(_scrollBack + lines, 0, _screen.ScrollbackCount);
                InvalidateVisual();
            }
            e.Handled = true;
        }

        private ((int row, int col) a, (int row, int col) b)? Selection()
        {
            if (_selStart == null || _selEnd == null) return null;
            var a = _selStart.Value; var b = _selEnd.Value;
            if (b.row < a.row || (b.row == a.row && b.col < a.col)) (a, b) = (b, a);
            return (a, b);
        }

        private static bool InSelection(((int row, int col) a, (int row, int col) b)? sel, int row, int col)
        {
            if (sel == null) return false;
            var (a, b) = sel.Value;
            if (row < a.row || row > b.row) return false;
            if (row == a.row && col < a.col) return false;
            if (row == b.row && col > b.col) return false;
            return true;
        }

        private void SelectWord((int row, int col) at)
        {
            lock (_gate)
            {
                var line = _screen.Line(at.row);
                if (line == null) return;
                bool Word(int i) => i >= 0 && i < line.Length && !line[i].IsBlank;
                if (!Word(at.col)) return;
                int a = at.col, b = at.col;
                while (Word(a - 1)) a--;
                while (Word(b + 1)) b++;
                _selStart = (at.row, a); _selEnd = (at.row, b);
            }
            InvalidateVisual();
        }

        private void SelectAll()
        {
            lock (_gate) { _selStart = (-_screen.ScrollbackCount, 0); _selEnd = (_screen.Rows - 1, _screen.Cols - 1); }
            InvalidateVisual();
        }

        /// <summary>The selected text (rows joined with new lines, trailing blanks trimmed).</summary>
        public string SelectedText()
        {
            var sel = Selection();
            if (sel == null) return "";
            var (a, b) = sel.Value;
            var sb = new StringBuilder();
            lock (_gate)
            {
                for (int row = a.row; row <= b.row; row++)
                {
                    var line = _screen.Line(row);
                    if (line == null) continue;
                    int from = row == a.row ? a.col : 0, to = row == b.row ? b.col : line.Length - 1;
                    var part = new StringBuilder();
                    for (int x = from; x <= to && x < line.Length; x++)
                        if (!line[x].Flags.HasFlag(CellFlags.WideTail)) part.Append(string.IsNullOrEmpty(line[x].Text) ? " " : line[x].Text);
                    sb.Append(part.ToString().TrimEnd());
                    if (row < b.row) sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        private async System.Threading.Tasks.Task CopySelection()
        {
            string text = SelectedText();
            if (text.Length == 0) return;
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip != null) await clip.SetTextAsync(text);
        }

        private async System.Threading.Tasks.Task Paste()
        {
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            string text = clip != null ? await clip.GetTextAsync() : null;
            if (string.IsNullOrEmpty(text)) return;
            text = text.Replace("\r\n", "\r").Replace('\n', '\r');
            Send(_screen.BracketedPaste ? "\x1b[200~" + text + "\x1b[201~" : text);
        }
    }
}
