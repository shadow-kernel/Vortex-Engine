using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Editor.Core.Claude.Terminal;

namespace VortexTests
{
    /// <summary>The terminal that hosts Claude Code in the sidebar (v3.0.4): the VT/xterm screen and the pseudo-terminal.</summary>
    public static class TerminalTests
    {
        [Test]
        public static void TextWrapsAndScrollsIntoTheHistory(TestContext t)
        {
            var s = new VtScreen(10, 3);
            s.Feed("hello\r\nworld\r\n0123456789AB");
            t.Equal("world", s.RowText(0), "row 0 after one line scrolled off");
            t.Equal("0123456789", s.RowText(1), "a full row");
            t.Equal("AB", s.RowText(2), "the rest wrapped");
            t.Equal(1, s.ScrollbackCount, "one line in the history");
            t.Equal("hello", s.RowText(-1), "the history keeps it");
            // a full row does not wrap until the next character arrives
            s.Feed("\x1b[2;1H0123456789");
            t.Equal(9, s.CursorX, "the cursor waits at the last column");
            t.Equal(1, s.CursorY, "on the same row");
        }

        [Test]
        public static void WrappedLinksStayWhole(TestContext t)
        {
            var s = new VtScreen(20, 4);
            s.Feed("go to https://claude.ai/oauth/authorize?code=abc123 now\r\nnext");
            t.True(s.IsWrapped(0), "the long line wrapped");
            t.False(s.IsWrapped(2), "the line ended with a real new line");
            t.Equal("https://claude.ai/oauth/authorize?code=abc123", s.LinkAt(1, 3), "the link, read across the wrap");
            t.Equal("https://claude.ai/oauth/authorize?code=abc123", s.LinkAt(0, 8), "from its first row too");
            t.Equal(null, s.LinkAt(3, 1), "no link elsewhere");
        }

        [Test]
        public static void CursorMovesEraseAndInsert(TestContext t)
        {
            var s = new VtScreen(20, 5);
            s.Feed("abcdefghij\x1b[1;4H");
            t.Equal(3, s.CursorX, "CUP column (1-based)");
            s.Feed("\x1b[K");
            t.Equal("abc", s.RowText(0), "erase to the end of the line");
            s.Feed("\x1b[2;1Hxyz\x1b[2D\x1b[@-");
            t.Equal("x-yz", s.RowText(1), "insert a blank, then print over it");
            s.Feed("\x1b[1P");
            t.Equal("x-z", s.RowText(1), "delete a character");
            s.Feed("\x1b[3;5Hmid\x1b[1J");
            t.Equal("", s.RowText(0), "erase above clears earlier rows");
            t.Equal("", s.RowText(2).Trim(), "and the start of this row up to the cursor");
            s.Feed("\x1b[2J\x1b[H");
            t.Equal("", s.ScreenText(), "erase the display");
            s.Feed("one\x1b[2Etwo\x1b[1Fthree");
            t.Equal("three", s.RowText(1), "CNL / CPL move to column 0");
            t.Equal("two", s.RowText(2), "the line below");
        }

        [Test]
        public static void ColoursAndAttributes(TestContext t)
        {
            var s = new VtScreen(30, 3);
            s.Feed("\x1b[1;31mR\x1b[0m\x1b[38;5;208mO\x1b[38;2;10;20;30mT\x1b[38:2::1:2:3mC\x1b[7;44mI\x1b[0mn");
            var r = s.CellAt(0, 0);
            t.Equal(1, r.Fg, "red");
            t.True(r.Flags.HasFlag(CellFlags.Bold), "bold");
            t.Equal(208, s.CellAt(1, 0).Fg, "256-colour");
            t.Equal(0x01000000 | 10 << 16 | 20 << 8 | 30, s.CellAt(2, 0).Fg, "true colour (semicolons)");
            t.Equal(0x01000000 | 1 << 16 | 2 << 8 | 3, s.CellAt(3, 0).Fg, "true colour (colons)");
            t.True(s.CellAt(4, 0).Flags.HasFlag(CellFlags.Inverse), "inverse");
            t.Equal(4, s.CellAt(4, 0).Bg, "blue background");
            t.Equal(-1, s.CellAt(5, 0).Fg, "reset");
            t.Equal(CellFlags.None, s.CellAt(5, 0).Flags, "no attributes after the reset");
        }

        [Test]
        public static void ScrollRegionAndAlternateScreen(TestContext t)
        {
            var s = new VtScreen(10, 5);
            s.Feed("top\r\n1\r\n2\r\n3\r\nbottom");
            s.Feed("\x1b[2;4r\x1b[4;1H\nnew");   // region rows 2–4, cursor on its last row, line feed scrolls only the region
            t.Equal("top", s.RowText(0), "above the region stays");
            t.Equal("2", s.RowText(1), "the region scrolled up");
            t.Equal("new", s.RowText(3), "the new line at the bottom of the region");
            t.Equal("bottom", s.RowText(4), "below the region stays");
            t.Equal(0, s.ScrollbackCount, "a region scroll keeps nothing");
            s.Feed("\x1b[r\x1b[?1049h");
            t.True(s.IsAlternateScreen, "alternate screen on");
            t.Equal("", s.ScreenText(), "it starts empty");
            s.Feed("full screen app");
            s.Feed("\x1b[?1049l");
            t.False(s.IsAlternateScreen, "back to the main screen");
            t.Equal("top", s.RowText(0), "the main screen came back");
            t.Equal("bottom", s.RowText(4), "with its last row");
        }

        [Test]
        public static void WideCharactersAndCombiningMarks(TestContext t)
        {
            var s = new VtScreen(10, 2);
            s.Feed("a界b");
            t.Equal("界", s.CellAt(1, 0).Text, "a wide character");
            t.True(s.CellAt(2, 0).Flags.HasFlag(CellFlags.WideTail), "takes two cells");
            t.Equal(4, s.CursorX, "the cursor moved by three");
            s.Feed("e\u0301");
            t.Equal("e\u0301", s.CellAt(4, 0).Text, "a combining accent joins its letter");
            s.Feed("\r\n🙂x");
            t.Equal("🙂", s.CellAt(0, 1).Text, "an emoji (surrogate pair) in one cell");
            t.Equal("x", s.CellAt(2, 1).Text, "after two columns");
        }

        [Test]
        public static void AnswersWhatTheProgramAsks(TestContext t)
        {
            var s = new VtScreen(40, 10);
            var replies = new List<string>();
            s.Reply += r => replies.Add(r);
            s.Feed("\x1b[5;7H\x1b[6n\x1b[c\x1b[>c\x1b]11;?\x07\x1b[18t\x1b[?u");
            t.Equal("\x1b[5;7R", replies[0], "cursor position report");
            t.Equal("\x1b[?62;22c", replies[1], "device attributes");
            t.True(replies[2].StartsWith("\x1b[>"), "secondary attributes");
            t.True(replies[3].StartsWith("\x1b]11;rgb:"), "background colour");
            t.Equal("\x1b[8;10;40t", replies[4], "text area size");
            t.Equal("\x1b[?0u", replies[5], "no kitty keyboard protocol");
            s.Feed("\x1b[?2004h\x1b[?1h\x1b[?25l\x1b]0;Claude Code\x07");
            t.True(s.BracketedPaste, "bracketed paste");
            t.True(s.ApplicationCursorKeys, "application cursor keys");
            t.False(s.CursorVisible, "hidden cursor");
            t.Equal("Claude Code", s.Title, "window title");
        }

        [Test]
        public static void ResizeKeepsTheBottom(TestContext t)
        {
            var s = new VtScreen(10, 4);
            s.Feed("1\r\n2\r\n3\r\n4");
            s.Resize(10, 2);
            t.Equal("3", s.RowText(0), "shrinking keeps the rows at the cursor");
            t.Equal("4", s.RowText(1), "the last row");
            t.Equal(2, s.ScrollbackCount, "the rest went into the history");
            s.Resize(10, 4);
            t.Equal("1", s.RowText(0), "growing brings history rows back");
            t.Equal(3, s.CursorY, "the cursor stays on its line");
            s.Resize(5, 4);
            t.Equal(5, s.Line(0).Length, "rows are cut to the new width");
        }

        [Test]
        public static void PseudoTerminalRunsAProgram(TestContext t)
        {
            if (OperatingSystem.IsWindows()) { Console.WriteLine("        skipped: the ConPTY path is checked by the editor smoke on Windows"); return; }
            var output = new StringBuilder();
            var exited = new ManualResetEventSlim();
            int code = -1;
            using var pty = Pty.Start("/bin/sh", new[] { "-c", "printf 'size=%s\\n' \"$(stty size)\"; read line; printf 'got=%s\\n' \"$line\"; exit 3" },
                                      t.Path(""), Pty.TerminalEnvironment(), 77, 21);
            pty.Output += (b, n) => { lock (output) output.Append(Encoding.UTF8.GetString(b, 0, n)); };
            pty.Exited += c => { code = c; exited.Set(); };
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000) { lock (output) if (output.ToString().Contains("size=")) break; Thread.Sleep(20); }
            pty.Write("vortex\r");
            t.True(exited.Wait(5000), "the program ended");
            string text;
            lock (output) text = output.ToString();
            t.True(text.Contains("size=21 77"), "it saw the terminal's size: " + text.Replace("\r", "\\r").Replace("\n", "\\n"));
            t.True(text.Contains("got=vortex"), "it read the typed line");
            t.Equal(3, code, "its exit code");
        }
    }
}
