using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Editor.Core.Claude.Terminal
{
    /// <summary>Text attributes of a terminal cell.</summary>
    [Flags]
    public enum CellFlags : ushort
    {
        None = 0, Bold = 1, Dim = 2, Italic = 4, Underline = 8, Inverse = 16, Hidden = 32, Strike = 64,
        /// <summary>The right half of a double-width character (its text is in the cell before).</summary>
        WideTail = 128,
    }

    /// <summary>One cell of the screen: its text (a grapheme, "" for empty), colours and attributes.</summary>
    public struct Cell
    {
        public string Text;
        /// <summary>Colour: -1 = default, 0–255 = palette index, otherwise 0x01RRGGBB (true colour).</summary>
        public int Fg, Bg;
        public CellFlags Flags;

        public static Cell Blank(int bg = -1) => new Cell { Text = "", Fg = -1, Bg = bg };
        public bool IsBlank => string.IsNullOrEmpty(Text) || Text == " ";
    }

    /// <summary>
    /// A VT100 / xterm terminal screen: it parses what a program writes to its pseudo-terminal (text, C0 controls, ESC,
    /// CSI, OSC and DCS sequences) into a grid of cells with a cursor, scrollback, an alternate screen and the modes a
    /// full-screen program sets — enough for Claude Code's interface and ordinary shells. Replies the program asks for
    /// (cursor position, device attributes, colours) go out through <see cref="Reply"/>. Not thread-safe: feed and read
    /// on one thread (the view locks around both).
    /// </summary>
    public sealed class VtScreen
    {
        public int Cols { get; private set; }
        public int Rows { get; private set; }
        /// <summary>Lines that scrolled off the top of the main screen (oldest first).</summary>
        public int ScrollbackLimit = 5000;

        private List<Cell[]> _lines;              // the visible screen
        private readonly List<Cell[]> _scrollback = new List<Cell[]>();
        private List<Cell[]> _savedMain;          // the main screen while the alternate one shows
        private bool _alt;

        public int CursorX { get; private set; }
        public int CursorY { get; private set; }
        public bool CursorVisible { get; private set; } = true;
        private bool _wrapPending;
        private int _top, _bottom;                // scroll region (inclusive)
        private bool[] _tabs;

        // current attributes
        private int _fg = -1, _bg = -1;
        private CellFlags _flags;
        private (int x, int y, int fg, int bg, CellFlags flags, bool origin) _saved;

        // modes
        public bool ApplicationCursorKeys { get; private set; }
        public bool BracketedPaste { get; private set; }
        public bool FocusReporting { get; private set; }
        /// <summary>The program tracks the mouse (1000 / 1002 / 1003) with SGR coordinates (1006).</summary>
        public int MouseTracking { get; private set; }
        public bool MouseSgr { get; private set; }
        private bool _autoWrap = true, _originMode, _insertMode, _lineFeedNewLine;
        private bool _lineDrawing;                // ESC ( 0: DEC special graphics

        public string Title { get; private set; } = "";
        /// <summary>Bytes the terminal answers with (the view writes them to the program).</summary>
        public event Action<string> Reply;
        public event Action<string> TitleChanged;
        public event Action Bell;

        /// <summary>Default colours the program may ask for (OSC 10 / 11), as 0xRRGGBB.</summary>
        public int DefaultForeground = 0xF2F2F4, DefaultBackground = 0x1E1E20;

        /// <summary>Bumped on every change (the view repaints when it differs).</summary>
        public long Version { get; private set; }

        public VtScreen(int cols, int rows)
        {
            Cols = Math.Max(2, cols);
            Rows = Math.Max(1, rows);
            _lines = NewLines(Rows);
            _top = 0; _bottom = Rows - 1;
            ResetTabs();
        }

        public bool IsAlternateScreen => _alt;
        public int ScrollbackCount => _alt ? 0 : _scrollback.Count;

        /// <summary>A line to show: negative rows reach into the scrollback (-1 = the newest scrolled-off line).</summary>
        public Cell[] Line(int row)
        {
            if (row >= 0) return row < _lines.Count ? _lines[row] : null;
            if (_alt) return null;
            int i = _scrollback.Count + row;
            return i >= 0 && i < _scrollback.Count ? _scrollback[i] : null;
        }

        // ================================================================== size

        public void Resize(int cols, int rows)
        {
            cols = Math.Max(2, cols);
            rows = Math.Max(1, rows);
            if (cols == Cols && rows == Rows) return;
            ResizeBuffer(_lines, cols, rows, keepBottom: true);
            if (_savedMain != null) ResizeBuffer(_savedMain, cols, rows, keepBottom: true);
            for (int i = 0; i < _scrollback.Count; i++) _scrollback[i] = Fit(_scrollback[i], cols);
            Cols = cols;
            Rows = rows;
            _top = 0; _bottom = Rows - 1;
            CursorX = Math.Min(CursorX, Cols - 1);
            CursorY = Math.Min(CursorY, Rows - 1);
            _wrapPending = false;
            ResetTabs();
            Version++;
        }

        private void ResizeBuffer(List<Cell[]> lines, int cols, int rows, bool keepBottom)
        {
            for (int i = 0; i < lines.Count; i++) lines[i] = Fit(lines[i], cols);
            // fewer rows: lines below the cursor go first, then the top lines scroll into the scrollback
            while (lines.Count > rows)
            {
                if (ReferenceEquals(lines, _lines) && lines.Count - 1 > CursorY && IsEmpty(lines[lines.Count - 1])) { lines.RemoveAt(lines.Count - 1); continue; }
                if (ReferenceEquals(lines, _lines) && !_alt) PushScrollback(lines[0]);
                lines.RemoveAt(0);
                if (ReferenceEquals(lines, _lines)) CursorY = Math.Max(0, CursorY - 1);
            }
            while (lines.Count < rows)
            {
                // more rows: bring lines back from the scrollback while there are any
                if (keepBottom && ReferenceEquals(lines, _lines) && !_alt && _scrollback.Count > 0)
                {
                    lines.Insert(0, Fit(_scrollback[_scrollback.Count - 1], cols));
                    _scrollback.RemoveAt(_scrollback.Count - 1);
                    CursorY++;
                }
                else lines.Add(NewLine(cols));
            }
        }

        private static bool IsEmpty(Cell[] line)
        {
            foreach (var c in line) if (!c.IsBlank || c.Bg != -1) return false;
            return true;
        }

        private static Cell[] Fit(Cell[] line, int cols)
        {
            if (line.Length == cols) return line;
            var n = new Cell[cols];
            for (int i = 0; i < cols; i++) n[i] = i < line.Length ? line[i] : Cell.Blank();
            // a wide character cut in half at the new edge
            if (cols < line.Length && cols > 0 && cols < line.Length && line[cols].Flags.HasFlag(CellFlags.WideTail)) n[cols - 1] = Cell.Blank();
            return n;
        }

        private List<Cell[]> NewLines(int rows)
        {
            var l = new List<Cell[]>(rows);
            for (int i = 0; i < rows; i++) l.Add(NewLine(Cols));
            return l;
        }

        private Cell[] NewLine(int cols, int bg = -1)
        {
            var line = new Cell[cols];
            for (int i = 0; i < cols; i++) line[i] = Cell.Blank(bg);
            return line;
        }

        private void ResetTabs()
        {
            _tabs = new bool[Cols];
            for (int i = 8; i < Cols; i += 8) _tabs[i] = true;
        }

        private void PushScrollback(Cell[] line)
        {
            _scrollback.Add(line);
            if (_scrollback.Count > ScrollbackLimit) _scrollback.RemoveRange(0, _scrollback.Count - ScrollbackLimit);
        }

        // ================================================================== parser

        private enum State { Ground, Escape, EscapeIntermediate, Csi, Osc, Dcs, DcsEscape, OscEscape, Charset }
        private State _state;
        private readonly StringBuilder _seq = new StringBuilder();
        private char _charsetSlot;
        private readonly Decoder _utf8 = new UTF8Encoding(false).GetDecoder();
        private char[] _chars = new char[4096];
        private char _pendingHigh;                // a high surrogate waiting for its low half

        /// <summary>Feed bytes the program wrote.</summary>
        public void Feed(byte[] data, int offset, int count)
        {
            int need = _utf8.GetCharCount(data, offset, count, false);
            if (_chars.Length < need) _chars = new char[need * 2];
            int n = _utf8.GetChars(data, offset, count, _chars, 0, false);
            for (int i = 0; i < n; i++) Put(_chars[i]);
            Version++;
        }

        /// <summary>Feed text (tests).</summary>
        public void Feed(string text)
        {
            foreach (char c in text) Put(c);
            Version++;
        }

        private void Put(char c)
        {
            switch (_state)
            {
                case State.Ground: Ground(c); return;
                case State.Escape: Escape(c); return;
                case State.EscapeIntermediate: _state = State.Ground; return;   // ESC # 8, ESC % G … — ignored
                case State.Charset:
                    if (_charsetSlot == '(') _lineDrawing = c == '0';
                    _state = State.Ground;
                    return;
                case State.Csi:
                    if (c >= 0x40 && c <= 0x7E) { Csi(_seq.ToString(), c); _state = State.Ground; }
                    else if (c == 0x1B) { _state = State.Escape; }
                    else if (c < 0x20) Control(c);   // C0 inside a CSI executes
                    else if (_seq.Length < 256) _seq.Append(c);
                    return;
                case State.Osc:
                    if (c == 0x07) { Osc(_seq.ToString()); _state = State.Ground; }
                    else if (c == 0x1B) _state = State.OscEscape;
                    else if (_seq.Length < 4096) _seq.Append(c);
                    return;
                case State.OscEscape:
                    // ESC \ ends the string; anything else ends it too and starts a new escape
                    Osc(_seq.ToString());
                    if (c == '\\') _state = State.Ground; else { _state = State.Escape; Escape(c); }
                    return;
                case State.Dcs:
                    if (c == 0x1B) _state = State.DcsEscape;
                    else if (c == 0x07) _state = State.Ground;
                    else if (_seq.Length < 4096) _seq.Append(c);
                    return;
                case State.DcsEscape:
                    Dcs(_seq.ToString());
                    if (c == '\\') _state = State.Ground; else { _state = State.Escape; Escape(c); }
                    return;
            }
        }

        private void Ground(char c)
        {
            if (c < 0x20 || c == 0x7F) { Control(c); return; }
            if (char.IsHighSurrogate(c)) { _pendingHigh = c; return; }
            string text;
            if (char.IsLowSurrogate(c) && _pendingHigh != 0) { text = new string(new[] { _pendingHigh, c }); _pendingHigh = '\0'; }
            else { _pendingHigh = '\0'; text = c.ToString(); }
            Print(text);
        }

        private void Control(char c)
        {
            switch (c)
            {
                case '\x07': try { Bell?.Invoke(); } catch { } break;
                case '\b': if (CursorX > 0) CursorX--; _wrapPending = false; break;
                case '\t': Tab(); break;
                case '\n': case '\x0B': case '\x0C': LineFeed(); if (_lineFeedNewLine) CursorX = 0; break;
                case '\r': CursorX = 0; _wrapPending = false; break;
                case '\x1B': _state = State.Escape; _seq.Clear(); break;
                case '\x0E': case '\x0F': break;   // shift out / in
            }
        }

        private void Escape(char c)
        {
            _state = State.Ground;
            switch (c)
            {
                case '[': _state = State.Csi; _seq.Clear(); break;
                case ']': _state = State.Osc; _seq.Clear(); break;
                case 'P': _state = State.Dcs; _seq.Clear(); break;
                case '_': case '^': case 'X': _state = State.Dcs; _seq.Clear(); break;   // APC, PM, SOS: ignored like DCS
                case '(': case ')': case '*': case '+': _charsetSlot = c; _state = State.Charset; break;
                case '#': case '%': case ' ': _state = State.EscapeIntermediate; break;
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case 'D': LineFeed(); break;
                case 'E': LineFeed(); CursorX = 0; break;
                case 'M': ReverseIndex(); break;
                case 'H': if (CursorX < Cols) _tabs[CursorX] = true; break;
                case 'c': FullReset(); break;
                case '=': case '>': break;   // keypad modes
                case '\\': break;            // a stray string terminator
            }
        }

        // ================================================================== printing

        private void Print(string text)
        {
            if (_lineDrawing && text.Length == 1) text = LineDrawing(text[0]);
            int width = CharWidth(text);
            if (width == 0)
            {
                // a combining mark or a zero-width joiner part: joins the previous cell
                int px = _wrapPending ? CursorX : CursorX - 1;
                if (px >= 0 && px < Cols)
                {
                    ref var prev = ref _lines[CursorY][px];
                    if (prev.Flags.HasFlag(CellFlags.WideTail) && px > 0) prev = ref _lines[CursorY][px - 1];
                    prev.Text += text;
                }
                return;
            }
            if (_wrapPending)
            {
                if (_autoWrap) { CursorX = 0; LineFeed(); }
                _wrapPending = false;
            }
            if (width == 2 && CursorX == Cols - 1)
            {
                // no room for a wide character at the right edge: it wraps
                _lines[CursorY][CursorX] = Blank();
                if (_autoWrap) { CursorX = 0; LineFeed(); } else return;
            }
            var line = _lines[CursorY];
            if (_insertMode) InsertCells(width);
            ClearWideAt(line, CursorX);
            line[CursorX] = new Cell { Text = text, Fg = _fg, Bg = _bg, Flags = _flags };
            if (width == 2 && CursorX + 1 < Cols)
            {
                ClearWideAt(line, CursorX + 1);
                line[CursorX + 1] = new Cell { Text = "", Fg = _fg, Bg = _bg, Flags = _flags | CellFlags.WideTail };
            }
            CursorX += width;
            if (CursorX >= Cols) { CursorX = Cols - 1; _wrapPending = true; }
        }

        /// <summary>Overwriting half of a wide character blanks its other half.</summary>
        private static void ClearWideAt(Cell[] line, int x)
        {
            if (x < 0 || x >= line.Length) return;
            if (line[x].Flags.HasFlag(CellFlags.WideTail) && x > 0) line[x - 1] = Cell.Blank(line[x - 1].Bg);
            else if (x + 1 < line.Length && line[x + 1].Flags.HasFlag(CellFlags.WideTail)) line[x + 1] = Cell.Blank(line[x + 1].Bg);
        }

        private Cell Blank() => Cell.Blank(_bg);

        /// <summary>Columns a grapheme takes: 0 for combining marks, 2 for East Asian wide characters and emoji.</summary>
        public static int CharWidth(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int cp = char.ConvertToUtf32(s, 0);
            if (cp == 0x200D || (cp >= 0xFE00 && cp <= 0xFE0F) || (cp >= 0xE0100 && cp <= 0xE01EF)) return 0;   // ZWJ, variation selectors
            var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
            if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.EnclosingMark || cat == UnicodeCategory.Format) return 0;
            if (cp >= 0x1F000 && cp <= 0x1FAFF) return 2;                                    // emoji, symbols
            if ((cp >= 0x1100 && cp <= 0x115F) || (cp >= 0x2E80 && cp <= 0xA4CF && cp != 0x303F) || (cp >= 0xAC00 && cp <= 0xD7A3) ||
                (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0xFE30 && cp <= 0xFE4F) || (cp >= 0xFF00 && cp <= 0xFF60) || (cp >= 0xFFE0 && cp <= 0xFFE6) ||
                (cp >= 0x20000 && cp <= 0x3FFFD)) return 2;
            if (cp == 0x231A || cp == 0x231B || cp == 0x23E9 || cp == 0x23EA || cp == 0x23EB || cp == 0x23EC || cp == 0x23F0 || cp == 0x23F3 ||
                (cp >= 0x25FD && cp <= 0x25FE) || (cp >= 0x2614 && cp <= 0x2615) || (cp >= 0x2648 && cp <= 0x2653) || cp == 0x267F || cp == 0x2693 ||
                cp == 0x26A1 || (cp >= 0x26AA && cp <= 0x26AB) || (cp >= 0x26BD && cp <= 0x26BE) || (cp >= 0x26C4 && cp <= 0x26C5) || cp == 0x26CE ||
                cp == 0x26D4 || cp == 0x26EA || (cp >= 0x26F2 && cp <= 0x26F3) || cp == 0x26F5 || cp == 0x26FA || cp == 0x26FD || cp == 0x2705 ||
                (cp >= 0x270A && cp <= 0x270B) || cp == 0x2728 || cp == 0x274C || cp == 0x274E || (cp >= 0x2753 && cp <= 0x2755) || cp == 0x2757 ||
                (cp >= 0x2795 && cp <= 0x2797) || cp == 0x27B0 || cp == 0x27BF || (cp >= 0x2B1B && cp <= 0x2B1C) || cp == 0x2B50 || cp == 0x2B55) return 2;
            return 1;
        }

        private static string LineDrawing(char c)
        {
            const string from = "`afgjklmnopqrstuvwxyz{|}~";
            const string to = "◆▒°±┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";
            int i = from.IndexOf(c);
            return i >= 0 ? to[i].ToString() : c.ToString();
        }

        // ================================================================== cursor and scrolling

        private void Tab()
        {
            int x = CursorX + 1;
            while (x < Cols - 1 && !_tabs[x]) x++;
            CursorX = Math.Min(x, Cols - 1);
            _wrapPending = false;
        }

        private void LineFeed()
        {
            _wrapPending = false;
            if (CursorY == _bottom) ScrollUp(1);
            else if (CursorY < Rows - 1) CursorY++;
        }

        private void ReverseIndex()
        {
            _wrapPending = false;
            if (CursorY == _top) ScrollDown(1);
            else if (CursorY > 0) CursorY--;
        }

        private void ScrollUp(int n)
        {
            for (int i = 0; i < n; i++)
            {
                var gone = _lines[_top];
                _lines.RemoveAt(_top);
                _lines.Insert(_bottom, NewLine(Cols, _bg));
                // only the whole screen's top line goes into the scrollback (not a region's, not the alternate screen's)
                if (_top == 0 && !_alt) PushScrollback(gone);
            }
        }

        private void ScrollDown(int n)
        {
            for (int i = 0; i < n; i++)
            {
                _lines.RemoveAt(_bottom);
                _lines.Insert(_top, NewLine(Cols, _bg));
            }
        }

        private void SetCursor(int x, int y)
        {
            int top = _originMode ? _top : 0, bottom = _originMode ? _bottom : Rows - 1;
            CursorX = Math.Clamp(x, 0, Cols - 1);
            CursorY = Math.Clamp(y + top, top, bottom);
            _wrapPending = false;
        }

        private void SaveCursor() => _saved = (CursorX, CursorY, _fg, _bg, _flags, _originMode);

        private void RestoreCursor()
        {
            CursorX = Math.Min(_saved.x, Cols - 1);
            CursorY = Math.Min(_saved.y, Rows - 1);
            _fg = _saved.fg; _bg = _saved.bg; _flags = _saved.flags; _originMode = _saved.origin;
            _wrapPending = false;
        }

        private void FullReset()
        {
            _lines = NewLines(Rows);
            _scrollback.Clear();
            _alt = false; _savedMain = null;
            CursorX = CursorY = 0; CursorVisible = true; _wrapPending = false;
            _top = 0; _bottom = Rows - 1;
            _fg = _bg = -1; _flags = 0;
            ApplicationCursorKeys = BracketedPaste = FocusReporting = MouseSgr = false; MouseTracking = 0;
            _autoWrap = true; _originMode = _insertMode = _lineFeedNewLine = _lineDrawing = false;
            ResetTabs();
        }

        // ================================================================== CSI

        private void Csi(string body, char final)
        {
            char prefix = body.Length > 0 && (body[0] == '?' || body[0] == '>' || body[0] == '<' || body[0] == '=') ? body[0] : '\0';
            string rest = prefix != '\0' ? body.Substring(1) : body;
            // intermediates (space, $, ", ', …) end the parameters
            string intermediates = "";
            int cut = rest.Length;
            while (cut > 0 && rest[cut - 1] >= 0x20 && rest[cut - 1] <= 0x2F) cut--;
            if (cut < rest.Length) { intermediates = rest.Substring(cut); rest = rest.Substring(0, cut); }
            var p = Params(rest);
            int P(int i, int def) => i < p.Count && p[i] > 0 ? p[i] : def;

            if (intermediates.Length > 0)
            {
                if (final == 'q' && intermediates == " ") return;            // cursor style
                if (final == 'p' && intermediates == "$") return;            // mode report request
                return;
            }
            if (prefix == '?')
            {
                switch (final)
                {
                    case 'h': foreach (var m in p) DecMode(m, true); return;
                    case 'l': foreach (var m in p) DecMode(m, false); return;
                    case 'u': Send("\x1b[?0u"); return;                       // kitty keyboard query: legacy keys only
                    case 'n': if (P(0, 0) == 6) Send("\x1b[?" + (CursorY + 1) + ";" + (CursorX + 1) + "R"); return;
                    default: return;
                }
            }
            if (prefix == '>')
            {
                if (final == 'c') Send("\x1b[>0;10;1c");                      // secondary device attributes
                return;                                                       // > u (kitty push), > m (modify keys) …
            }
            if (prefix != '\0') return;

            switch (final)
            {
                case 'A': CursorY = Math.Max(CursorY >= _top ? _top : 0, CursorY - P(0, 1)); _wrapPending = false; break;
                case 'B': CursorY = Math.Min(CursorY <= _bottom ? _bottom : Rows - 1, CursorY + P(0, 1)); _wrapPending = false; break;
                case 'C': case 'a': CursorX = Math.Min(Cols - 1, CursorX + P(0, 1)); _wrapPending = false; break;
                case 'D': CursorX = Math.Max(0, CursorX - P(0, 1)); _wrapPending = false; break;
                case 'E': CursorY = Math.Min(_bottom, CursorY + P(0, 1)); CursorX = 0; _wrapPending = false; break;
                case 'F': CursorY = Math.Max(_top, CursorY - P(0, 1)); CursorX = 0; _wrapPending = false; break;
                case 'G': case '`': CursorX = Math.Clamp(P(0, 1) - 1, 0, Cols - 1); _wrapPending = false; break;
                case 'd': SetCursor(CursorX, P(0, 1) - 1); break;
                case 'H': case 'f': SetCursor(P(1, 1) - 1, P(0, 1) - 1); break;
                case 'J': EraseDisplay(P(0, 0)); break;
                case 'K': EraseLine(P(0, 0)); break;
                case 'X': { var line = _lines[CursorY]; int n = P(0, 1); for (int x = CursorX; x < Math.Min(Cols, CursorX + n); x++) line[x] = Blank(); break; }
                case 'P': DeleteCells(P(0, 1)); break;
                case '@': InsertCells(P(0, 1)); break;
                case 'L': if (CursorY >= _top && CursorY <= _bottom) for (int i = 0; i < P(0, 1); i++) { _lines.RemoveAt(_bottom); _lines.Insert(CursorY, NewLine(Cols, _bg)); } CursorX = 0; break;
                case 'M': if (CursorY >= _top && CursorY <= _bottom) for (int i = 0; i < P(0, 1); i++) { _lines.RemoveAt(CursorY); _lines.Insert(_bottom, NewLine(Cols, _bg)); } CursorX = 0; break;
                case 'S': ScrollUp(P(0, 1)); break;
                case 'T': ScrollDown(P(0, 1)); break;
                case 'm': Sgr(rest); break;
                case 'r':
                    {
                        int top = P(0, 1) - 1, bottom = P(1, Rows) - 1;
                        if (top < bottom && bottom < Rows) { _top = top; _bottom = bottom; }
                        else { _top = 0; _bottom = Rows - 1; }
                        SetCursor(0, 0);
                        break;
                    }
                case 's': SaveCursor(); break;
                case 'u': RestoreCursor(); break;
                case 'h': foreach (var m in p) AnsiMode(m, true); break;
                case 'l': foreach (var m in p) AnsiMode(m, false); break;
                case 'n':
                    if (P(0, 0) == 5) Send("\x1b[0n");
                    else if (P(0, 0) == 6) Send("\x1b[" + (CursorY + 1) + ";" + (CursorX + 1) + "R");
                    break;
                case 'c': if (P(0, 0) == 0) Send("\x1b[?62;22c"); break;   // primary device attributes: VT220 with colour
                case 'g': if (P(0, 0) == 0) { if (CursorX < Cols) _tabs[CursorX] = false; } else if (P(0, 0) == 3) _tabs = new bool[Cols]; break;
                case 't':
                    if (P(0, 0) == 18) Send("\x1b[8;" + Rows + ";" + Cols + "t");
                    break;
                case 'b':
                    {
                        // repeat the last printed character
                        int x = CursorX - 1;
                        if (x >= 0) { string t = _lines[CursorY][x].Text; for (int i = 0; i < P(0, 1) && !string.IsNullOrEmpty(t); i++) Print(t); }
                        break;
                    }
            }
        }

        private static List<int> Params(string s)
        {
            var list = new List<int>();
            if (s.Length == 0) return list;
            int v = 0; bool any = false;
            foreach (char c in s)
            {
                if (c >= '0' && c <= '9') { v = Math.Min(v * 10 + (c - '0'), 99999); any = true; }
                else if (c == ';') { list.Add(any ? v : 0); v = 0; any = false; }
                else if (c == ':') { list.Add(any ? v : 0); v = 0; any = false; }
            }
            list.Add(any ? v : 0);
            return list;
        }

        private void DecMode(int m, bool on)
        {
            switch (m)
            {
                case 1: ApplicationCursorKeys = on; break;
                case 6: _originMode = on; SetCursor(0, 0); break;
                case 7: _autoWrap = on; break;
                case 25: CursorVisible = on; break;
                case 47: case 1047: SwitchScreen(on, clear: m == 1047); break;
                case 1049: if (on) { SaveCursor(); SwitchScreen(true, clear: true); } else { SwitchScreen(false, clear: false); RestoreCursor(); } break;
                case 1000: case 1002: case 1003: MouseTracking = on ? m : 0; break;
                case 1006: MouseSgr = on; break;
                case 1004: FocusReporting = on; break;
                case 2004: BracketedPaste = on; break;
            }
        }

        private void AnsiMode(int m, bool on)
        {
            if (m == 4) _insertMode = on;
            else if (m == 20) _lineFeedNewLine = on;
        }

        private void SwitchScreen(bool alt, bool clear)
        {
            if (alt == _alt) { if (alt && clear) EraseDisplay(2); return; }
            if (alt)
            {
                _savedMain = _lines;
                _lines = NewLines(Rows);
                _alt = true;
            }
            else
            {
                _lines = _savedMain ?? NewLines(Rows);
                _savedMain = null;
                _alt = false;
            }
            _top = 0; _bottom = Rows - 1;
            _wrapPending = false;
        }

        private void EraseDisplay(int mode)
        {
            switch (mode)
            {
                case 0:
                    EraseLine(0);
                    for (int y = CursorY + 1; y < Rows; y++) _lines[y] = NewLine(Cols, _bg);
                    break;
                case 1:
                    EraseLine(1);
                    for (int y = 0; y < CursorY; y++) _lines[y] = NewLine(Cols, _bg);
                    break;
                case 2:
                    for (int y = 0; y < Rows; y++) _lines[y] = NewLine(Cols, _bg);
                    break;
                case 3:
                    _scrollback.Clear();
                    break;
            }
            _wrapPending = false;
        }

        private void EraseLine(int mode)
        {
            var line = _lines[CursorY];
            int from = mode == 0 ? CursorX : 0, to = mode == 1 ? CursorX : Cols - 1;
            for (int x = from; x <= to && x < Cols; x++) line[x] = Blank();
            if (from > 0) ClearWideAt(line, from);
            _wrapPending = false;
        }

        private void DeleteCells(int n)
        {
            var line = _lines[CursorY];
            n = Math.Min(n, Cols - CursorX);
            Array.Copy(line, CursorX + n, line, CursorX, Cols - CursorX - n);
            for (int x = Cols - n; x < Cols; x++) line[x] = Blank();
            _wrapPending = false;
        }

        private void InsertCells(int n)
        {
            var line = _lines[CursorY];
            n = Math.Min(n, Cols - CursorX);
            Array.Copy(line, CursorX, line, CursorX + n, Cols - CursorX - n);
            for (int x = CursorX; x < CursorX + n; x++) line[x] = Blank();
            _wrapPending = false;
        }

        // ================================================================== SGR

        private void Sgr(string s)
        {
            // keep the sub-parameter structure: "38:2::255:0:0" and "38;2;255;0;0" both set a true colour
            if (s.Length == 0) { _fg = _bg = -1; _flags = 0; return; }
            var groups = s.Split(';');
            for (int i = 0; i < groups.Length; i++)
            {
                var sub = groups[i].Split(':');
                int code = sub[0].Length == 0 ? 0 : Num(sub[0]);
                switch (code)
                {
                    case 0: _fg = _bg = -1; _flags = 0; break;
                    case 1: _flags |= CellFlags.Bold; break;
                    case 2: _flags |= CellFlags.Dim; break;
                    case 3: _flags |= CellFlags.Italic; break;
                    case 4: if (sub.Length > 1 && Num(sub[1]) == 0) _flags &= ~CellFlags.Underline; else _flags |= CellFlags.Underline; break;
                    case 7: _flags |= CellFlags.Inverse; break;
                    case 8: _flags |= CellFlags.Hidden; break;
                    case 9: _flags |= CellFlags.Strike; break;
                    case 21: _flags |= CellFlags.Underline; break;
                    case 22: _flags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                    case 23: _flags &= ~CellFlags.Italic; break;
                    case 24: _flags &= ~CellFlags.Underline; break;
                    case 27: _flags &= ~CellFlags.Inverse; break;
                    case 28: _flags &= ~CellFlags.Hidden; break;
                    case 29: _flags &= ~CellFlags.Strike; break;
                    case 39: _fg = -1; break;
                    case 49: _bg = -1; break;
                    case 38: case 48: case 58:
                        {
                            int color;
                            if (sub.Length > 1) color = ExtendedColor(sub, 1);
                            else
                            {
                                // the semicolon form takes the following parameters
                                int kind = i + 1 < groups.Length ? Num(groups[i + 1]) : -1;
                                if (kind == 5 && i + 2 < groups.Length) { color = Num(groups[i + 2]) & 0xFF; i += 2; }
                                else if (kind == 2 && i + 4 < groups.Length) { color = 0x01000000 | (Num(groups[i + 2]) & 0xFF) << 16 | (Num(groups[i + 3]) & 0xFF) << 8 | (Num(groups[i + 4]) & 0xFF); i += 4; }
                                else { color = -1; i = groups.Length; }
                            }
                            if (code == 38) _fg = color; else if (code == 48) _bg = color;   // 58 = underline colour: ignored
                            break;
                        }
                    default:
                        if (code >= 30 && code <= 37) _fg = code - 30;
                        else if (code >= 40 && code <= 47) _bg = code - 40;
                        else if (code >= 90 && code <= 97) _fg = code - 90 + 8;
                        else if (code >= 100 && code <= 107) _bg = code - 100 + 8;
                        break;
                }
            }
        }

        private static int ExtendedColor(string[] sub, int at)
        {
            int kind = Num(sub[at]);
            if (kind == 5 && sub.Length > at + 1) return Num(sub[at + 1]) & 0xFF;
            if (kind == 2)
            {
                // "2:<colourspace>:r:g:b" or "2:r:g:b"
                int r = sub.Length >= at + 5 ? at + 2 : at + 1;
                if (sub.Length > r + 2) return 0x01000000 | (Num(sub[r]) & 0xFF) << 16 | (Num(sub[r + 1]) & 0xFF) << 8 | (Num(sub[r + 2]) & 0xFF);
            }
            return -1;
        }

        private static int Num(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

        // ================================================================== OSC / DCS

        private void Osc(string s)
        {
            int semi = s.IndexOf(';');
            string code = semi < 0 ? s : s.Substring(0, semi), arg = semi < 0 ? "" : s.Substring(semi + 1);
            switch (code)
            {
                case "0": case "2":
                    Title = arg;
                    try { TitleChanged?.Invoke(arg); } catch { }
                    break;
                case "10": if (arg == "?") Send("\x1b]10;" + Rgb(DefaultForeground) + "\x1b\\"); break;
                case "11": if (arg == "?") Send("\x1b]11;" + Rgb(DefaultBackground) + "\x1b\\"); break;
                // 4 (palette), 8 (hyperlinks: the text is printed normally), 52 (clipboard), 133 (prompt marks): ignored
            }
        }

        private static string Rgb(int c)
        {
            int r = c >> 16 & 0xFF, g = c >> 8 & 0xFF, b = c & 0xFF;
            return "rgb:" + (r * 257).ToString("x4") + "/" + (g * 257).ToString("x4") + "/" + (b * 257).ToString("x4");
        }

        private void Dcs(string s)
        {
            // XTGETTCAP ("+q…") and DECRQSS ("$q…"): answer "not supported", programs fall back
            if (s.StartsWith("+q", StringComparison.Ordinal)) Send("\x1bP0+r\x1b\\");
            else if (s.StartsWith("$q", StringComparison.Ordinal)) Send("\x1bP0$r\x1b\\");
        }

        private void Send(string s)
        {
            try { Reply?.Invoke(s); } catch { }
        }

        // ================================================================== reading back (tests, selection)

        /// <summary>The text of a row (negative = scrollback), trailing blanks trimmed.</summary>
        public string RowText(int row)
        {
            var line = Line(row);
            if (line == null) return "";
            var sb = new StringBuilder();
            foreach (var c in line)
            {
                if (c.Flags.HasFlag(CellFlags.WideTail)) continue;
                sb.Append(string.IsNullOrEmpty(c.Text) ? " " : c.Text);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>All of the screen's text, one line per row (tests).</summary>
        public string ScreenText()
        {
            var sb = new StringBuilder();
            for (int y = 0; y < Rows; y++) sb.Append(RowText(y)).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>The cell at a position of the visible screen.</summary>
        public Cell CellAt(int x, int y) => _lines[y][x];
    }
}
