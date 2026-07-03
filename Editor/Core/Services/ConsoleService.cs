using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Editor.Core.Services
{
    /// <summary>Severity of a console line — drives its colour + icon + the level filters.</summary>
    public enum LogLevel { Info, Warning, Error, System }

    /// <summary>One line in the in-editor Console. Immutable once added.</summary>
    public class LogEntry
    {
        public string Time { get; set; }        // "HH:mm:ss"
        public LogLevel Level { get; set; }
        public string LevelTag { get; set; }     // "INFO" / "WARN" / "ERROR"
        public string Message { get; set; }
        public Brush Color { get; set; }         // level colour (tag + message tint)
        public string Icon { get; set; }         // Segoe MDL2 glyph
    }

    /// <summary>
    /// Central sink for GAME output shown in the editor's Console panel. In-editor Play runs in the SAME process, so
    /// everything the game writes reaches here directly:
    ///  • scripts call <c>Vortex.Debug.Log/LogWarning/LogError</c>,
    ///  • the script runtime routes compile/start/update errors here,
    ///  • while playing, <see cref="BeginCapture"/> redirects <c>Console.Out</c>/<c>Console.Error</c> here so a plain
    ///    <c>Console.WriteLine</c> in a script shows up too.
    /// The Console view binds to <see cref="Entries"/>. Thread-safe: appends marshal to the UI thread.
    /// </summary>
    public sealed class ConsoleService
    {
        public static ConsoleService Instance { get; } = new ConsoleService();

        /// <summary>Live log lines the Console view binds to (oldest first).</summary>
        public ObservableCollection<LogEntry> Entries { get; } = new ObservableCollection<LogEntry>();

        /// <summary>Raised (on the UI thread) after a line is appended — lets the view auto-scroll + flash the tab.</summary>
        public event Action EntryAdded;

        // Running per-level counts, maintained on Add/evict/Clear so the view reads them in O(1) instead of
        // re-scanning all (up to 5000) entries on every appended line — that O(n)-per-line scan froze the editor
        // when a script logged from Update() every frame.
        public int InfoCount { get; private set; }
        public int WarnCount { get; private set; }
        public int ErrorCount { get; private set; }

        /// <summary>Cap so a chatty game can't grow the list unbounded; oldest lines drop off.</summary>
        public const int MaxEntries = 5000;

        private static readonly Brush InfoBrush = Freeze("#FFC8C8CE");
        private static readonly Brush WarnBrush = Freeze("#FFE2C044");
        private static readonly Brush ErrorBrush = Freeze("#FFE06C6C");
        private static readonly Brush SystemBrush = Freeze("#FF7C6CFF");

        private ConsoleService() { }

        private PlayState _lastState = PlayState.Editing;
        private bool _playHooked;

        private bool _greeted;
        /// <summary>One-time hint so the empty Console explains itself.</summary>
        public void GreetOnce()
        {
            if (_greeted) return;
            _greeted = true;
            LogSystem("Console ready — game output (Vortex.Debug.Log, Console.WriteLine, script errors) appears here on Play.");
        }

        /// <summary>Wire play start/stop: on a fresh Play, banner + start capturing Console.Out/Error; on Stop,
        /// stop capturing + banner. Idempotent — safe to call from the Console view's Loaded.</summary>
        public void AttachPlayMode()
        {
            if (_playHooked) return;
            _playHooked = true;
            try { PlayModeService.Instance.StateChanged += OnPlayStateChanged; } catch { }
        }

        private void OnPlayStateChanged(object sender, PlayState state)
        {
            var prev = _lastState;
            _lastState = state;
            if (state == PlayState.Playing && prev == PlayState.Editing)
            {
                LogSystem("──────────  Play started  ──────────");
                BeginCapture();
            }
            else if (state == PlayState.Editing && prev != PlayState.Editing)
            {
                EndCapture();
                LogSystem("──────────  Play stopped  ──────────");
            }
        }

        public void Log(string message) => Add(LogLevel.Info, "INFO", message, InfoBrush, "");
        public void LogWarning(string message) => Add(LogLevel.Warning, "WARN", message, WarnBrush, "");
        public void LogError(string message) => Add(LogLevel.Error, "ERROR", message, ErrorBrush, "");
        /// <summary>A framed engine/editor line (e.g. "Play started") — visually distinct from game logs.</summary>
        public void LogSystem(string message) => Add(LogLevel.System, "▶", message, SystemBrush, "");

        public void Clear()
        {
            OnUi(() => { Entries.Clear(); InfoCount = WarnCount = ErrorCount = 0; EntryAdded?.Invoke(); });
        }

        private void Add(LogLevel level, string tag, string message, Brush color, string icon)
        {
            if (message == null) return;
            string time;
            try { time = DateTime.Now.ToString("HH:mm:ss"); } catch { time = ""; }
            OnUi(() =>
            {
                Entries.Add(new LogEntry { Time = time, Level = level, LevelTag = tag, Message = message, Color = color, Icon = icon });
                Bump(level, +1);
                while (Entries.Count > MaxEntries) { Bump(Entries[0].Level, -1); Entries.RemoveAt(0); }
                EntryAdded?.Invoke();
            });
        }

        private void Bump(LogLevel level, int delta)
        {
            switch (level)
            {
                case LogLevel.Info: InfoCount += delta; break;
                case LogLevel.Warning: WarnCount += delta; break;
                case LogLevel.Error: ErrorCount += delta; break;
            }
        }

        private static void OnUi(Action action)
        {
            try
            {
                var app = Application.Current;
                if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                    app.Dispatcher.BeginInvoke(action);
                else
                    action();
            }
            catch { }
        }

        // ---------------- Play-time stdout/stderr capture ----------------
        private TextWriter _savedOut, _savedError;
        private ForwardingWriter _outWriter, _errWriter;
        private bool _capturing;

        /// <summary>Redirect Console.Out + Console.Error into the Console panel for the duration of a play session,
        /// so a script's plain Console.WriteLine also appears. Call <see cref="EndCapture"/> on Stop to restore.</summary>
        public void BeginCapture()
        {
            if (_capturing) return;
            try
            {
                _savedOut = Console.Out;
                _savedError = Console.Error;
                _outWriter = new ForwardingWriter(this, LogLevel.Info, _savedOut);
                _errWriter = new ForwardingWriter(this, LogLevel.Error, _savedError);
                Console.SetOut(_outWriter);
                Console.SetError(_errWriter);
                _capturing = true;
            }
            catch { }
        }

        public void EndCapture()
        {
            if (!_capturing) return;
            // Emit any final line that had no trailing newline before restoring, so a script's last
            // Console.Write(...) isn't silently dropped.
            try { _outWriter?.FlushLine(); } catch { }
            try { _errWriter?.FlushLine(); } catch { }
            try { if (_savedOut != null) Console.SetOut(_savedOut); } catch { }
            try { if (_savedError != null) Console.SetError(_savedError); } catch { }
            _outWriter = null; _errWriter = null;
            _capturing = false;
        }

        private static Brush Freeze(string hex)
        {
            var b = (Brush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        /// <summary>A TextWriter that both forwards to the original stream (so real stdout still works) AND mirrors
        /// completed lines into the Console panel. Buffers partial writes until a newline.</summary>
        private sealed class ForwardingWriter : TextWriter
        {
            private readonly ConsoleService _svc;
            private readonly LogLevel _level;
            private readonly TextWriter _inner;
            private readonly StringBuilder _line = new StringBuilder();

            public ForwardingWriter(ConsoleService svc, LogLevel level, TextWriter inner)
            { _svc = svc; _level = level; _inner = inner; }

            public override Encoding Encoding => _inner?.Encoding ?? Encoding.UTF8;

            public override void Write(char c)
            {
                try { _inner?.Write(c); } catch { }
                if (c == '\n') { FlushLine(); }
                else if (c != '\r') _line.Append(c);
            }

            public override void Write(string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                foreach (char c in value) Write(c);
            }

            public override void WriteLine(string value) { Write(value); Write('\n'); }

            /// <summary>Emit the buffered partial line (a Write without a trailing newline) — called on newline and
            /// once more on EndCapture so the last chunk isn't dropped.</summary>
            public void FlushLine()
            {
                var text = _line.ToString();
                _line.Clear();
                if (text.Length == 0) return;
                if (_level == LogLevel.Error) _svc.LogError(text); else _svc.Log(text);
            }
        }
    }
}
