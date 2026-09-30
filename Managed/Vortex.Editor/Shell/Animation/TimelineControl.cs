using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Animation;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;

namespace VortexEditor.Shell.Animation
{
    /// <summary>
    /// The Keyframe Editor's dope sheet: one row per animated bone with a diamond at each distinct key time (union of a
    /// track's position / rotation / scale keys — the inspector edits per-component values, the timeline moves and
    /// deletes whole poses), a ruler with the playhead and animation-event flags. Drag the ruler = scrub, drag a diamond =
    /// move the pose (snaps to the frame grid), Delete = remove it, double-click a row = key the bone there, right-click
    /// the ruler = add / delete events, Ctrl/Cmd+wheel = zoom. Every mutation is an undoable command.
    /// </summary>
    public sealed class TimelineControl : Grid
    {
        public const double RowH = 28, RulerH = 24, NamesW = 210, KeySize = 11;
        private const float KeyTol = 0.0005f;

        private sealed class Row { public string Bone; public AnimTrack Track; public List<float> Times; }

        private readonly Surface _surface;
        private readonly PlayheadLayer _playhead;
        private readonly ScrollBar _hbar = new ScrollBar { Orientation = Orientation.Horizontal, Minimum = 0, AllowAutoHide = false };
        private readonly ScrollBar _vbar = new ScrollBar { Orientation = Orientation.Vertical, Minimum = 0, AllowAutoHide = false };
        private readonly List<Row> _rows = new List<Row>();

        private VortexAnimClip _clip;
        private string _selectedBone;
        private float _time, _duration = 1f, _pps = 120f;
        private double _scrollX, _scrollY;
        private bool _syncBars;

        private string _selKeyBone;
        private float _selKeyTime = float.NaN;

        private bool _draggingKey, _draggingPlayhead, _keyMoved;
        private Row _dragRow;
        private float _dragOrigTime, _dragNewTime;

        // hover tip
        private string _tipText, _tipCandidate;
        private Point _tipPos;
        private readonly DispatcherTimer _tipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };

        public float SnapSeconds { get; set; }
        public float Duration { get => _duration; set { _duration = Math.Max(0.01f, value); UpdateBars(); Redraw(); } }
        public float Time
        {
            get => _time;
            set { float v = Math.Max(0f, Math.Min(_duration, value)); if (v == _time) return; _time = v; _playhead.InvalidateVisual(); }
        }
        /// <summary>Pixels per second (zoom), 40..600.</summary>
        public float Zoom { get => _pps; set { _pps = Math.Max(40f, Math.Min(600f, value)); UpdateBars(); Redraw(); } }

        public event Action<float> TimeChanged;
        public event Action<string> TrackSelected;
        public event Action Changed;
        /// <summary>Double-click on empty row space — the WINDOW samples the pose and writes the key.</summary>
        public event Action<string, float> KeyAddRequested;
        /// <summary>The selected key moved / was deleted etc. (bone, time) — informational.</summary>
        public string SelectedKeyBone => _selKeyBone;
        public float SelectedKeyTime => _selKeyTime;

        public TimelineControl()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto");
            RowDefinitions = new RowDefinitions("*,Auto");
            ClipToBounds = true;
            _surface = new Surface(this);
            _playhead = new PlayheadLayer(this);
            Children.Add(_surface);
            Children.Add(_playhead);
            Grid.SetColumn(_vbar, 1);
            Grid.SetRow(_hbar, 1);
            Children.Add(_vbar);
            Children.Add(_hbar);
            _hbar.ValueChanged += (s, e) => { if (_syncBars) return; _scrollX = _hbar.Value; Redraw(); };
            _vbar.ValueChanged += (s, e) => { if (_syncBars) return; _scrollY = _vbar.Value; Redraw(); };
            _surface.SizeChanged += (s, e) => { UpdateBars(); Redraw(); };
            _tipTimer.Tick += (s, e) => { _tipTimer.Stop(); _tipText = _tipCandidate; _playhead.InvalidateVisual(); };
        }

        // ================================================================ public API

        public void SetClip(VortexAnimClip clip)
        {
            _clip = clip;
            _duration = Math.Max(0.01f, clip?.DurationSec ?? 1f);
            _selKeyBone = null; _selKeyTime = float.NaN;
            Refresh();
        }

        public void SetSelectedBone(string bone) { _selectedBone = bone; Refresh(); }

        /// <summary>Rebuild rows + redraw (after any external clip mutation).</summary>
        public void Refresh()
        {
            _rows.Clear();
            if (_clip?.Tracks != null)
                foreach (var t in _clip.Tracks) _rows.Add(new Row { Bone = t.Bone, Track = t, Times = DistinctKeyTimes(t) });
            // the selected bone shows a (still empty) row so a double-click can create its first key
            if (!string.IsNullOrEmpty(_selectedBone) && _clip != null && _clip.FindTrack(_selectedBone) == null)
                _rows.Add(new Row { Bone = _selectedBone, Track = null, Times = new List<float>() });
            UpdateBars();
            Redraw();
        }

        /// <summary>Scroll so the selected bone's row is visible.</summary>
        public void RevealBone(string bone)
        {
            int i = _rows.FindIndex(r => r.Bone == bone);
            if (i < 0) return;
            double h = RowsViewportHeight, top = i * RowH;
            if (top < _scrollY) _scrollY = top; else if (top + RowH > _scrollY + h) _scrollY = top + RowH - h;
            UpdateBars(); Redraw();
        }

        public int RowCount => _rows.Count;

        private void Redraw() { _surface.InvalidateVisual(); _playhead.InvalidateVisual(); }

        // ================================================================ geometry

        private double TimeAreaWidth => Math.Max(1, _surface.Bounds.Width - NamesW);
        private double RowsViewportHeight => Math.Max(1, _surface.Bounds.Height - RulerH);
        private double TimeToX(float t) => NamesW + t * _pps - _scrollX;
        private float XToTime(double x) => (float)((x - NamesW + _scrollX) / _pps);
        private int RowAt(double y) { if (y < RulerH) return -1; int r = (int)Math.Floor((y - RulerH + _scrollY) / RowH); return r >= 0 && r < _rows.Count ? r : -1; }
        private double RowTop(int r) => RulerH + r * RowH - _scrollY;

        private float Snap(float t)
        {
            if (SnapSeconds > 0f) t = (float)Math.Round(t / SnapSeconds) * SnapSeconds;
            return Math.Max(0f, Math.Min(_duration, t));
        }

        private void UpdateBars()
        {
            _syncBars = true;
            try
            {
                double vw = TimeAreaWidth, content = (_duration + 0.5f) * _pps;   // half a second of tail room
                _hbar.Maximum = Math.Max(0, content - vw);
                _hbar.ViewportSize = vw; _hbar.LargeChange = vw; _hbar.SmallChange = _pps * 0.1;
                _scrollX = Math.Max(0, Math.Min(_hbar.Maximum, _scrollX));
                _hbar.Value = _scrollX;
                _hbar.IsVisible = _hbar.Maximum > 0.5;

                double vh = RowsViewportHeight, ch = _rows.Count * RowH;
                _vbar.Maximum = Math.Max(0, ch - vh);
                _vbar.ViewportSize = vh; _vbar.LargeChange = vh; _vbar.SmallChange = RowH;
                _scrollY = Math.Max(0, Math.Min(_vbar.Maximum, _scrollY));
                _vbar.Value = _scrollY;
                _vbar.IsVisible = _vbar.Maximum > 0.5;
            }
            finally { _syncBars = false; }
        }

        /// <summary>Union of a track's Pos/Rot/Scale key times, merged within a small tolerance (sorted).</summary>
        private static List<float> DistinctKeyTimes(AnimTrack track)
        {
            var all = new List<float>();
            if (track.Pos != null) foreach (var k in track.Pos) all.Add(k.T);
            if (track.Rot != null) foreach (var k in track.Rot) all.Add(k.T);
            if (track.Scale != null) foreach (var k in track.Scale) all.Add(k.T);
            all.Sort();
            var times = new List<float>(all.Count);
            foreach (var t in all) if (times.Count == 0 || Math.Abs(times[times.Count - 1] - t) >= KeyTol) times.Add(t);
            return times;
        }

        private float MajorStep() => _pps >= 240f ? 0.1f : (_pps >= 60f ? 0.5f : 1f);

        // ================================================================ drawing

        private IBrush B(string key) => AnimUi.Res(key);
        private static readonly IBrush EventBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA5, 0x6C));
        private static readonly IBrush SoundBrush = new SolidColorBrush(Color.FromRgb(0xB5, 0x9C, 0xFF));

        private FormattedText Text(string s, double size, IBrush brush, bool bold = false, double maxWidth = 0)
        {
            var ft = new FormattedText(s ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);
            if (maxWidth > 0) { ft.MaxTextWidth = maxWidth; ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis; }
            return ft;
        }

        private static StreamGeometry DiamondGeometry(IEnumerable<Point> centers, double size)
        {
            double h = size * 0.5;
            var g = new StreamGeometry();
            using (var s = g.Open())
                foreach (var c in centers)
                {
                    s.BeginFigure(new Point(c.X, c.Y - h), true);
                    s.LineTo(new Point(c.X + h, c.Y));
                    s.LineTo(new Point(c.X, c.Y + h));
                    s.LineTo(new Point(c.X - h, c.Y));
                    s.EndFigure(true);
                }
            return g;
        }

        private void DrawStatic(DrawingContext ctx)
        {
            double w = _surface.Bounds.Width, h = _surface.Bounds.Height;
            if (w < 4 || h < 4) return;
            var bg = B("VxFieldBrush"); var alt = B("VxHoverBrush"); var hair = B("VxHairlineBrush");
            var dim = B("VxTextSecondaryBrush"); var micro = B("VxTextTertiaryBrush"); var accent = B("VxAccentBrush"); var keyBrush = B("VxTextBrush");
            var gridPen = new Pen(hair, 1); var thinPen = new Pen(hair, 0.5); var endPen = new Pen(micro, 1.5);
            ctx.FillRectangle(bg, new Rect(0, 0, w, h));

            // ---- rows (time area)
            using (ctx.PushClip(new Rect(NamesW, RulerH, Math.Max(0, w - NamesW), Math.Max(0, h - RulerH))))
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    double y = RowTop(i);
                    if (y + RowH < RulerH || y > h) continue;
                    if ((i & 1) == 1) ctx.FillRectangle(alt, new Rect(NamesW, y, w - NamesW, RowH));
                    ctx.DrawLine(thinPen, new Point(NamesW, y + RowH), new Point(w, y + RowH));
                }
                float major = MajorStep();
                float t0 = Math.Max(0f, XToTime(NamesW)), t1 = XToTime(w);
                double rowsBottom = Math.Min(h, RowTop(_rows.Count));
                for (int i = (int)Math.Floor(t0 / major); i <= (int)Math.Ceiling(t1 / major); i++)
                {
                    double x = TimeToX(i * major);
                    if (x < NamesW - 2 || x > w + 2) continue;
                    ctx.DrawLine(thinPen, new Point(x, RulerH), new Point(x, Math.Max(RulerH, rowsBottom)));
                }
                double xe = TimeToX(_duration);
                if (xe >= NamesW - 2 && xe <= w + 2) ctx.DrawLine(new Pen(micro, 1), new Point(xe, RulerH), new Point(xe, Math.Max(RulerH, rowsBottom)));

                // key diamonds (overlapping ones culled: a 30 fps clip at low zoom puts keys 4 px apart)
                for (int i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    if (row.Track == null) continue;
                    double y = RowTop(i);
                    if (y + RowH < RulerH || y > h) continue;
                    double cy = y + RowH * 0.5, lastX = double.NegativeInfinity;
                    var centers = new List<Point>();
                    Point? selected = null;
                    foreach (float t in row.Times)
                    {
                        bool isDragged = _draggingKey && ReferenceEquals(row, _dragRow) && Math.Abs(t - _dragOrigTime) < KeyTol;
                        double x = TimeToX(isDragged ? _dragNewTime : t);
                        if (x < NamesW - KeySize || x > w + KeySize) continue;
                        bool sel = row.Bone == _selKeyBone && !float.IsNaN(_selKeyTime) && Math.Abs(t - _selKeyTime) < KeyTol;
                        if (sel || isDragged) { selected = new Point(x, cy); continue; }
                        if (x - lastX < 2.5) continue;
                        centers.Add(new Point(x, cy)); lastX = x;
                    }
                    if (centers.Count > 0) ctx.DrawGeometry(keyBrush, new Pen(hair, 1), DiamondGeometry(centers, KeySize));
                    if (selected.HasValue) ctx.DrawGeometry(accent, new Pen(accent, 1), DiamondGeometry(new[] { selected.Value }, KeySize + 1));
                }
            }

            // ---- names column
            using (ctx.PushClip(new Rect(0, RulerH, NamesW, Math.Max(0, h - RulerH))))
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    double y = RowTop(i);
                    if (y + RowH < RulerH || y > h) continue;
                    var row = _rows[i];
                    bool sel = row.Bone == _selectedBone;
                    if (sel) ctx.FillRectangle(B("VxSelectionBrush"), new Rect(0, y, NamesW, RowH));
                    else if ((i & 1) == 1) ctx.FillRectangle(alt, new Rect(0, y, NamesW, RowH));
                    string label = AnimUtil.DisplayBoneName(row.Bone) + (row.Track == null ? "  (no keys)" : "");
                    var ft = Text(label, 11.5, sel ? accent : (row.Track == null ? micro : dim), sel, NamesW - 14);
                    ctx.DrawText(ft, new Point(8, y + (RowH - ft.Height) * 0.5));
                }
            }

            // ---- ruler
            using (ctx.PushClip(new Rect(0, 0, w, RulerH)))
            {
                ctx.FillRectangle(B("VxPanelBrush"), new Rect(0, 0, w, RulerH));
                var hdr = Text("TRACKS", 10.5, micro, true);
                ctx.DrawText(hdr, new Point(8, (RulerH - hdr.Height) * 0.5));
                using (ctx.PushClip(new Rect(NamesW, 0, Math.Max(0, w - NamesW), RulerH)))
                {
                    float major = MajorStep(), minor = major / 5f;
                    float t0 = Math.Max(0f, XToTime(NamesW)), t1 = XToTime(w);
                    for (int i = (int)Math.Floor(t0 / minor); i <= (int)Math.Ceiling(t1 / minor); i++)
                    {
                        float t = i * minor;
                        if (t < -0.0001f) continue;
                        double x = TimeToX(t);
                        if (x < NamesW - 2 || x > w + 2) continue;
                        bool isMajor = i % 5 == 0;
                        ctx.DrawLine(gridPen, new Point(x, isMajor ? RulerH - 12 : RulerH - 6), new Point(x, RulerH));
                        if (isMajor) ctx.DrawText(Text(t.ToString(major < 0.5f ? "0.0#" : "0.#", CultureInfo.InvariantCulture), 10, dim), new Point(x + 3, 1));
                    }
                    double xe = TimeToX(_duration);
                    if (xe >= NamesW - 2 && xe <= w + 2) ctx.DrawLine(endPen, new Point(xe, 0), new Point(xe, RulerH));
                    // event flags: sound events get a violet flag + a note glyph so they read apart from script events
                    if (_clip?.Events != null)
                        foreach (var ev in _clip.Events)
                        {
                            double x = TimeToX(ev.T);
                            if (x < NamesW - 8 || x > w + 8) continue;
                            bool isSound = !string.IsNullOrEmpty(ev.Sound) || !string.IsNullOrEmpty(ev.AudioSource);
                            var brush = isSound ? SoundBrush : EventBrush;
                            var g = new StreamGeometry();
                            using (var s = g.Open())
                            {
                                s.BeginFigure(new Point(x, 2), true);
                                s.LineTo(new Point(x + 8, 5.5)); s.LineTo(new Point(x, 9)); s.LineTo(new Point(x, 16));
                                s.EndFigure(true);
                            }
                            ctx.DrawGeometry(brush, new Pen(brush, 1), g);
                            if (isSound) ctx.DrawText(Text("♪", 9, brush), new Point(x + 7, 0));
                        }
                }
                ctx.DrawLine(gridPen, new Point(0, RulerH - 0.5), new Point(w, RulerH - 0.5));
            }
            ctx.DrawLine(gridPen, new Point(NamesW - 0.5, 0), new Point(NamesW - 0.5, h));
        }

        private void DrawPlayhead(DrawingContext ctx)
        {
            double w = _playhead.Bounds.Width, h = _playhead.Bounds.Height;
            if (w < 4) return;
            double x = TimeToX(_time);
            if (x >= NamesW - 1 && x <= w + 1)
            {
                var accent = B("VxAccentBrush");
                double bottom = Math.Min(h, Math.Max(RulerH, RowTop(_rows.Count)));
                bottom = Math.Max(bottom, RulerH);
                using (ctx.PushClip(new Rect(NamesW, 0, Math.Max(0, w - NamesW), h)))
                {
                    ctx.DrawLine(new Pen(accent, 1.5), new Point(x, 0), new Point(x, Math.Max(bottom, h)));
                    var g = new StreamGeometry();
                    using (var s = g.Open()) { s.BeginFigure(new Point(x - 5, 0), true); s.LineTo(new Point(x + 5, 0)); s.LineTo(new Point(x, 7)); s.EndFigure(true); }
                    ctx.DrawGeometry(accent, null, g);
                }
            }
            if (!string.IsNullOrEmpty(_tipText))
            {
                var ft = Text(_tipText, 11, B("VxTextBrush"));
                double tw = ft.Width + 14, th = ft.Height + 8;
                double tx = Math.Min(Math.Max(4, _tipPos.X + 12), w - tw - 4), ty = _tipPos.Y + 16;
                if (ty + th > h - 2) ty = _tipPos.Y - th - 6;
                ctx.DrawRectangle(B("VxPanelRaisedBrush"), new Pen(B("VxSeparatorBrush"), 1), new Rect(tx, ty, tw, th), 5, 5);
                ctx.DrawText(ft, new Point(tx + 7, ty + 4));
            }
        }

        // ================================================================ input

        private Row HitKeyRow(Point p, out float time)
        {
            time = float.NaN;
            int r = RowAt(p.Y);
            if (r < 0 || p.X < NamesW) return null;
            var row = _rows[r];
            if (row.Track == null) return null;
            double best = KeySize * 0.5 + 2;
            foreach (float t in row.Times)
            {
                double d = Math.Abs(TimeToX(t) - p.X);
                if (d <= best) { best = d; time = t; }
            }
            return float.IsNaN(time) ? null : row;
        }

        private AnimEvent HitEvent(Point p)
        {
            if (_clip?.Events == null || p.Y > RulerH || p.X < NamesW) return null;
            AnimEvent hit = null; double best = 9;
            foreach (var ev in _clip.Events)
            {
                double d = p.X - TimeToX(ev.T);
                if (d >= -2 && d <= 9 && Math.Abs(d - 3) < best) { best = Math.Abs(d - 3); hit = ev; }
            }
            return hit;
        }

        private void OnPressed(PointerPressedEventArgs e)
        {
            _surface.Focus();
            HideTip();
            var pt = e.GetCurrentPoint(_surface);
            var p = pt.Position;
            if (pt.Properties.IsRightButtonPressed)
            {
                if (p.X >= NamesW && p.Y < RulerH) ShowRulerMenu(p);
                else { var kr = HitKeyRow(p, out float kt); if (kr != null) ShowKeyMenu(kr, kt); }
                e.Handled = true;
                return;
            }
            if (!pt.Properties.IsLeftButtonPressed) return;
            if (p.X < NamesW)
            {
                int r = RowAt(p.Y);
                if (r >= 0) TrackSelected?.Invoke(_rows[r].Bone);
                e.Handled = true;
                return;
            }
            if (p.Y < RulerH)
            {
                _draggingPlayhead = true;
                e.Pointer.Capture(_surface);
                ScrubTo(p.X);
                e.Handled = true;
                return;
            }
            int row = RowAt(p.Y);
            if (row < 0) return;
            var hitRow = HitKeyRow(p, out float hitTime);
            if (hitRow != null)
            {
                if (e.ClickCount >= 2)
                {
                    // double-click a key = jump the playhead onto it
                    _time = hitTime; _playhead.InvalidateVisual(); TimeChanged?.Invoke(hitTime);
                    e.Handled = true;
                    return;
                }
                _selKeyBone = hitRow.Bone; _selKeyTime = hitTime;
                _dragRow = hitRow; _dragOrigTime = _dragNewTime = hitTime;
                _draggingKey = true; _keyMoved = false;
                e.Pointer.Capture(_surface);
                _surface.InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (e.ClickCount >= 2) { KeyAddRequested?.Invoke(_rows[row].Bone, Snap(XToTime(p.X))); e.Handled = true; return; }
            _selKeyBone = null; _selKeyTime = float.NaN;
            _surface.InvalidateVisual();
            TrackSelected?.Invoke(_rows[row].Bone);
            e.Handled = true;
        }

        private void OnMoved(PointerEventArgs e)
        {
            var p = e.GetPosition(_surface);
            if (_draggingPlayhead) { ScrubTo(p.X); return; }
            if (_draggingKey)
            {
                float nt = Snap(XToTime(p.X));
                if (Math.Abs(nt - _dragNewTime) > KeyTol) { _dragNewTime = nt; _keyMoved = true; _surface.InvalidateVisual(); }
                return;
            }
            UpdateHoverTip(p);
        }

        private void OnReleased(PointerReleasedEventArgs e)
        {
            if (_draggingPlayhead) { _draggingPlayhead = false; e.Pointer.Capture(null); return; }
            if (!_draggingKey) return;
            _draggingKey = false;
            e.Pointer.Capture(null);
            var row = _dragRow; _dragRow = null;
            var track = row?.Track;
            float from = _dragOrigTime, to = _dragNewTime;
            if (track == null || !_keyMoved || Math.Abs(to - from) < KeyTol)
            {
                _surface.InvalidateVisual();
                if (row != null) TrackSelected?.Invoke(row.Bone);   // plain click: select the bone
                return;
            }
            // snapshot-based command: before/after key lists keep undo exact even when the move lands on (and replaces)
            // an existing key
            var before = CloneKeys(track);
            MoveKeysAtTime(track, from, to);
            var after = CloneKeys(track);
            RestoreKeys(track, before);
            UndoRedoManager.Instance.Execute(new ActionCommand("Move Keyframe", () => RestoreKeys(track, after), () => RestoreKeys(track, before)));
            _selKeyTime = to;
            Refresh();
            Changed?.Invoke();
            TrackSelected?.Invoke(row.Bone);
        }

        private void OnWheel(PointerWheelEventArgs e)
        {
            HideTip();
            var p = e.GetPosition(_surface);
            bool zoom = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (zoom)
            {
                if (_draggingKey) return;
                float tUnder = XToTime(Math.Max(NamesW, p.X));
                _pps = Math.Max(40f, Math.Min(600f, _pps * (e.Delta.Y > 0 ? 1.15f : 1f / 1.15f)));
                UpdateBars();
                _scrollX = Math.Max(0, Math.Min(_hbar.Maximum, tUnder * _pps - (Math.Max(NamesW, p.X) - NamesW)));
                UpdateBars();
                Redraw();
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
            {
                double d = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
                _scrollX = Math.Max(0, Math.Min(_hbar.Maximum, _scrollX - d * 40));
                UpdateBars(); Redraw();
            }
            else
            {
                _scrollY = Math.Max(0, Math.Min(_vbar.Maximum, _scrollY - e.Delta.Y * RowH));
                UpdateBars(); Redraw();
            }
            e.Handled = true;
        }

        private void OnKey(KeyEventArgs e)
        {
            if ((e.Key != Key.Delete && e.Key != Key.Back) || _selKeyBone == null || float.IsNaN(_selKeyTime)) return;
            DeleteKeyAt(_selKeyBone, _selKeyTime);
            e.Handled = true;
        }

        private void DeleteKeyAt(string bone, float time)
        {
            var track = _clip?.FindTrack(bone);
            if (track == null) return;
            var before = CloneKeys(track);
            if (!RemoveKeysAtTime(track, time)) return;
            var after = CloneKeys(track);
            RestoreKeys(track, before);
            UndoRedoManager.Instance.Execute(new ActionCommand("Delete Keyframe", () => RestoreKeys(track, after), () => RestoreKeys(track, before)));
            _selKeyTime = float.NaN;
            Refresh();
            Changed?.Invoke();
        }

        private void ScrubTo(double x)
        {
            float t = Snap(XToTime(x));
            _time = t;
            _playhead.InvalidateVisual();
            TimeChanged?.Invoke(t);
        }

        // ---- hover tips
        private void UpdateHoverTip(Point p)
        {
            string tip = null;
            if (p.X < NamesW && p.Y >= RulerH) { int r = RowAt(p.Y); if (r >= 0) tip = _rows[r].Bone; }
            else if (p.Y < RulerH)
            {
                var ev = HitEvent(p);
                if (ev != null)
                {
                    bool isSound = !string.IsNullOrEmpty(ev.Sound);
                    tip = (string.IsNullOrEmpty(ev.Name) ? "(sound)" : ev.Name) + "  @ " + ev.T.ToString("0.###", CultureInfo.InvariantCulture) + "s"
                        + (isSound ? "   ♪ " + System.IO.Path.GetFileName(ev.Sound) : "") + "   (right-click to delete)";
                }
            }
            else
            {
                var row = HitKeyRow(p, out float t);
                if (row != null) tip = AnimUtil.DisplayBoneName(row.Bone) + " @ " + t.ToString("0.###", CultureInfo.InvariantCulture) + "s — drag to move, Delete removes";
            }
            if (tip == _tipCandidate) { if (_tipText != null) { _tipPos = p; _playhead.InvalidateVisual(); } return; }
            _tipCandidate = tip; _tipPos = p;
            _tipTimer.Stop();
            if (_tipText != null) { _tipText = null; _playhead.InvalidateVisual(); }
            if (tip != null) _tipTimer.Start();
        }
        private void HideTip() { _tipTimer.Stop(); _tipCandidate = null; if (_tipText != null) { _tipText = null; _playhead.InvalidateVisual(); } }

        // ---- context menus
        private void ShowRulerMenu(Point p)
        {
            if (_clip == null) return;
            if (_clip.Events == null) _clip.Events = new List<AnimEvent>();
            var menu = new MenuFlyout();
            var hit = HitEvent(p);
            var clip = _clip;
            if (hit != null)
            {
                var del = new MenuItem { Header = "Delete event \"" + hit.Name + "\"" };
                del.Click += (s, e) =>
                {
                    if (!clip.Events.Contains(hit)) return;
                    UndoRedoManager.Instance.Execute(new ActionCommand("Delete Animation Event",
                        () => clip.Events.Remove(hit),
                        () => { clip.Events.Add(hit); clip.Events.Sort((a, b) => a.T.CompareTo(b.T)); }));
                    Redraw(); Changed?.Invoke();
                };
                menu.Items.Add(del);
            }
            else
            {
                float t = Snap(XToTime(p.X));
                var add = new MenuItem { Header = "Add event here…  (" + t.ToString("0.###", CultureInfo.InvariantCulture) + "s)" };
                add.Click += async (s, e) =>
                {
                    string name = await AnimUi.Prompt(TopLevel.GetTopLevel(this) as Window, "Animation Event", "Event name (fired into scripts as OnAnimationEvent)", "");
                    if (string.IsNullOrWhiteSpace(name)) return;
                    var ev = new AnimEvent { T = t, Name = name.Trim() };
                    UndoRedoManager.Instance.Execute(new ActionCommand("Add Animation Event",
                        () => { clip.Events.Add(ev); clip.Events.Sort((a, b) => a.T.CompareTo(b.T)); },
                        () => clip.Events.Remove(ev)));
                    Redraw(); Changed?.Invoke();
                };
                menu.Items.Add(add);
            }
            menu.ShowAt(_surface, true);
        }

        private void ShowKeyMenu(Row row, float time)
        {
            var menu = new MenuFlyout();
            var go = new MenuItem { Header = "Move playhead here (" + time.ToString("0.###", CultureInfo.InvariantCulture) + "s)" };
            go.Click += (s, e) => { _time = time; _playhead.InvalidateVisual(); TimeChanged?.Invoke(time); };
            var del = new MenuItem { Header = "Delete keyframe" };
            del.Click += (s, e) => DeleteKeyAt(row.Bone, time);
            menu.Items.Add(go); menu.Items.Add(del);
            menu.ShowAt(_surface, true);
        }

        // ================================================================ key list mutation helpers (keys stay sorted by T)

        private static void MoveKeysAtTime(AnimTrack track, float from, float to)
        {
            void MoveV(List<AnimKeyVec3> keys)
            {
                if (keys == null) return;
                var moving = keys.FindAll(k => Math.Abs(k.T - from) < KeyTol);
                if (moving.Count == 0) return;
                keys.RemoveAll(k => Math.Abs(k.T - to) < KeyTol && !moving.Contains(k));   // overwrite the target
                foreach (var k in moving) k.T = to;
                keys.Sort((a, b) => a.T.CompareTo(b.T));
            }
            void MoveQ(List<AnimKeyQuat> keys)
            {
                if (keys == null) return;
                var moving = keys.FindAll(k => Math.Abs(k.T - from) < KeyTol);
                if (moving.Count == 0) return;
                keys.RemoveAll(k => Math.Abs(k.T - to) < KeyTol && !moving.Contains(k));
                foreach (var k in moving) k.T = to;
                keys.Sort((a, b) => a.T.CompareTo(b.T));
            }
            MoveV(track.Pos); MoveQ(track.Rot); MoveV(track.Scale);
        }

        private static bool RemoveKeysAtTime(AnimTrack track, float time)
        {
            int n = 0;
            if (track.Pos != null) n += track.Pos.RemoveAll(k => Math.Abs(k.T - time) < KeyTol);
            if (track.Rot != null) n += track.Rot.RemoveAll(k => Math.Abs(k.T - time) < KeyTol);
            if (track.Scale != null) n += track.Scale.RemoveAll(k => Math.Abs(k.T - time) < KeyTol);
            return n > 0;
        }

        public sealed class KeySnapshot { public List<AnimKeyVec3> Pos; public List<AnimKeyQuat> Rot; public List<AnimKeyVec3> Scale; }

        public static KeySnapshot CloneKeys(AnimTrack t)
        {
            var c = new KeySnapshot { Pos = new List<AnimKeyVec3>(), Rot = new List<AnimKeyQuat>(), Scale = new List<AnimKeyVec3>() };
            if (t.Pos != null) foreach (var k in t.Pos) c.Pos.Add(new AnimKeyVec3 { T = k.T, X = k.X, Y = k.Y, Z = k.Z });
            if (t.Rot != null) foreach (var k in t.Rot) c.Rot.Add(new AnimKeyQuat { T = k.T, X = k.X, Y = k.Y, Z = k.Z, W = k.W });
            if (t.Scale != null) foreach (var k in t.Scale) c.Scale.Add(new AnimKeyVec3 { T = k.T, X = k.X, Y = k.Y, Z = k.Z });
            return c;
        }

        public static void RestoreKeys(AnimTrack t, KeySnapshot snap)
        {
            t.Pos = new List<AnimKeyVec3>(); t.Rot = new List<AnimKeyQuat>(); t.Scale = new List<AnimKeyVec3>();
            foreach (var k in snap.Pos) t.Pos.Add(new AnimKeyVec3 { T = k.T, X = k.X, Y = k.Y, Z = k.Z });
            foreach (var k in snap.Rot) t.Rot.Add(new AnimKeyQuat { T = k.T, X = k.X, Y = k.Y, Z = k.Z, W = k.W });
            foreach (var k in snap.Scale) t.Scale.Add(new AnimKeyVec3 { T = k.T, X = k.X, Y = k.Y, Z = k.Z });
        }

        // ================================================================ layers

        private static bool _renderErrorReported;
        private static void ReportRenderError(Exception ex)
        {
            if (_renderErrorReported) return;
            _renderErrorReported = true;
            try { Editor.Core.Services.ConsoleService.Instance.LogError("[Timeline] render failed: " + ex); } catch { }
        }

        private sealed class Surface : Control, Avalonia.Rendering.ICustomHitTest
        {
            private readonly TimelineControl _o;
            public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);
            public Surface(TimelineControl o)
            {
                _o = o; Focusable = true; ClipToBounds = true;
                PointerPressed += (s, e) => _o.OnPressed(e);
                PointerMoved += (s, e) => _o.OnMoved(e);
                PointerReleased += (s, e) => _o.OnReleased(e);
                PointerWheelChanged += (s, e) => _o.OnWheel(e);
                PointerExited += (s, e) => _o.HideTip();
                KeyDown += (s, e) => _o.OnKey(e);
                PointerCaptureLost += (s, e) => { _o._draggingPlayhead = false; if (_o._draggingKey) { _o._draggingKey = false; _o._dragRow = null; InvalidateVisual(); } };
            }
            public override void Render(DrawingContext context) { try { _o.DrawStatic(context); } catch (Exception ex) { ReportRenderError(ex); } }
        }

        private sealed class PlayheadLayer : Control
        {
            private readonly TimelineControl _o;
            public PlayheadLayer(TimelineControl o) { _o = o; IsHitTestVisible = false; ClipToBounds = true; }
            public override void Render(DrawingContext context) { try { _o.DrawPlayhead(context); } catch (Exception ex) { ReportRenderError(ex); } }
        }
    }
}
