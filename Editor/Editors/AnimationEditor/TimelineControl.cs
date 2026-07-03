using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Editor.Core.Animation;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;

namespace Editor.Editors.AnimationEditor
{
    /// <summary>
    /// The Keyframe Editor's dope sheet: one row per animated bone, diamonds at each distinct key
    /// time (union of a track's Pos/Rot/Scale keys — the window edits per-component values, the timeline
    /// moves/deletes whole poses). Raw Canvas + Shapes like the rest of the hand-built editors. All key
    /// mutations run through UndoRedoManager (snapshot-based ActionCommands) and raise Changed.
    /// </summary>
    public sealed class TimelineControl : Border
    {
        private const double RowH = 28, RulerH = 24, NamesW = 210;
        private const double KeySize = 11;

        private static readonly Color C_Bg = Color.FromRgb(0x16, 0x16, 0x18);
        private static readonly Color C_RowAlt = Color.FromRgb(0x1B, 0x1B, 0x1E);
        private static readonly Color C_Grid = Color.FromRgb(0x2A, 0x2A, 0x2E);
        private static readonly Color C_TextDim = Color.FromRgb(0x98, 0x98, 0x9F);
        private static readonly Color C_MicroHdr = Color.FromRgb(0x6E, 0x6E, 0x77);
        private static readonly Color C_Key = Color.FromRgb(0xE6, 0xE6, 0xEC);
        private static readonly Color C_Accent = Color.FromRgb(0x6C, 0x5C, 0xE7);
        private static readonly Color C_Event = Color.FromRgb(0xE0, 0xA5, 0x6C);

        private class Row
        {
            public string Bone;
            public AnimTrack Track; // null = selected bone without a track yet
        }

        private VortexAnimClip _clip;
        private string _selectedBone;
        private readonly List<Row> _rows = new List<Row>();

        private float _time;
        private float _duration = 1f;
        private float _pps = 120f;      // pixels per second (zoom), 40..600
        private double _scrollX;

        // selected key = (bone, time); kept as values so a rebuild re-finds it
        private string _selKeyBone;
        private float _selKeyTime = float.NaN;

        // key drag state
        private Rectangle _dragDiamond;
        private Row _dragRow;
        private float _dragOrigTime, _dragNewTime;
        private bool _draggingKey, _draggingPlayhead;

        private StackPanel _namesPanel;
        private ScrollViewer _namesScroll;
        private Canvas _ruler;
        private Canvas _rowsCanvas;
        private ScrollViewer _rowsScroll;
        private ScrollBar _hbar;

        public float SnapSeconds { get; set; }

        public float Duration
        {
            get => _duration;
            set { _duration = Math.Max(0.01f, value); UpdateScrollExtent(); RedrawTime(); }
        }

        public float Time
        {
            get => _time;
            set
            {
                float v = Math.Max(0f, Math.Min(_duration, value));
                if (v == _time) return;
                _time = v;
                RedrawPlayhead();
            }
        }

        public event Action<float> TimeChanged;
        public event Action<string> TrackSelected;
        public event Action Changed;
        /// <summary>Double-click on empty row space — the WINDOW samples the pose and writes the key.</summary>
        public event Action<string, float> KeyAddRequested;

        public TimelineControl()
        {
            Background = new SolidColorBrush(C_Bg);
            BorderBrush = new SolidColorBrush(C_Grid);
            BorderThickness = new Thickness(0, 1, 0, 0);
            Focusable = true;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NamesW) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RulerH) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // top-left corner: micro header over the names column
            var corner = new Border
            {
                Background = new SolidColorBrush(C_RowAlt),
                BorderBrush = new SolidColorBrush(C_Grid),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock
                {
                    Text = "TRACKS",
                    Foreground = new SolidColorBrush(C_MicroHdr),
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            grid.Children.Add(corner);

            _namesPanel = new StackPanel();
            _namesScroll = new ScrollViewer
            {
                Content = _namesPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(0)
            };
            var namesBorder = new Border
            {
                BorderBrush = new SolidColorBrush(C_Grid),
                BorderThickness = new Thickness(0, 0, 1, 0),
                Child = _namesScroll
            };
            Grid.SetRow(namesBorder, 1);
            grid.Children.Add(namesBorder);

            _ruler = new Canvas { Background = new SolidColorBrush(C_RowAlt), ClipToBounds = true };
            Grid.SetColumn(_ruler, 1);
            grid.Children.Add(_ruler);
            _ruler.MouseLeftButtonDown += OnRulerDown;
            _ruler.MouseMove += OnRulerMove;
            _ruler.MouseLeftButtonUp += OnRulerUp;
            _ruler.MouseRightButtonUp += OnRulerRightClick;
            _ruler.SizeChanged += (s, e) => RedrawTime();

            _rowsCanvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
            _rowsScroll = new ScrollViewer
            {
                Content = _rowsCanvas,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(0)
            };
            _rowsScroll.ScrollChanged += (s, e) =>
            {
                if (e.VerticalChange != 0) _namesScroll.ScrollToVerticalOffset(e.VerticalOffset);
            };
            _rowsScroll.SizeChanged += (s, e) => { SizeRowsCanvas(); UpdateScrollExtent(); RedrawTime(); };
            Grid.SetRow(_rowsScroll, 1);
            Grid.SetColumn(_rowsScroll, 1);
            grid.Children.Add(_rowsScroll);
            _rowsCanvas.MouseLeftButtonDown += OnRowsDown;

            _hbar = new ScrollBar { Orientation = Orientation.Horizontal, Minimum = 0, Height = 14 };
            _hbar.ValueChanged += (s, e) => { _scrollX = _hbar.Value; RedrawTime(); };
            Grid.SetRow(_hbar, 2);
            Grid.SetColumn(_hbar, 1);
            grid.Children.Add(_hbar);

            Child = grid;

            PreviewMouseWheel += OnZoomWheel;
            PreviewMouseDown += (s, e) => Focus(); // so Del reaches KeyDown
            KeyDown += OnDeleteKeyDown;
        }

        // ===================== public API =====================

        public void SetClip(VortexAnimClip clip)
        {
            _clip = clip;
            _duration = Math.Max(0.01f, clip?.DurationSec ?? 1f);
            _selKeyBone = null; _selKeyTime = float.NaN;
            Refresh();
        }

        public void SetSelectedBone(string bone)
        {
            _selectedBone = bone;
            Refresh();
        }

        /// <summary>Rebuild rows + redraw (the window calls this after any external clip mutation).</summary>
        public void Refresh()
        {
            RebuildRows();
            SizeRowsCanvas();
            UpdateScrollExtent();
            RebuildNames();
            RedrawTime();
        }

        // ===================== rows =====================

        private void RebuildRows()
        {
            _rows.Clear();
            if (_clip?.Tracks != null)
                foreach (var t in _clip.Tracks)
                    _rows.Add(new Row { Bone = t.Bone, Track = t });
            // the selected bone shows a (still empty) row so double-click can create its first key
            if (!string.IsNullOrEmpty(_selectedBone) && _clip != null && _clip.FindTrack(_selectedBone) == null)
                _rows.Add(new Row { Bone = _selectedBone, Track = null });
        }

        private void SizeRowsCanvas()
        {
            _rowsCanvas.Height = Math.Max(_rows.Count * RowH, 0);
            _rowsCanvas.Width = Math.Max(_rowsScroll.ViewportWidth, 0);
        }

        private void RebuildNames()
        {
            _namesPanel.Children.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                bool sel = row.Bone == _selectedBone;
                var b = new Border
                {
                    Height = RowH,
                    Background = new SolidColorBrush((i & 1) == 1 ? C_RowAlt : C_Bg),
                    Child = new TextBlock
                    {
                        // display-only shortening (junk pivot suffixes + namespace prefixes) — keys and
                        // selection stay keyed by the FULL bone name (row.Bone)
                        Text = AnimationEditorWindow.DisplayBoneName(row.Bone) + (row.Track == null ? "  (no keys)" : ""),
                        ToolTip = row.Bone,
                        Foreground = new SolidColorBrush(sel ? C_Accent : (row.Track == null ? C_MicroHdr : C_TextDim)),
                        FontSize = 11.5,
                        FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal,
                        Margin = new Thickness(8, 0, 4, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    Cursor = Cursors.Hand
                };
                string bone = row.Bone;
                b.MouseLeftButtonDown += (s, e) => TrackSelected?.Invoke(bone);
                _namesPanel.Children.Add(b);
            }
        }

        // ===================== drawing =====================

        private double TimeToX(float t) => t * _pps - _scrollX;
        private float XToTime(double x) => (float)((x + _scrollX) / _pps);

        private float Snap(float t)
        {
            if (SnapSeconds > 0f) t = (float)Math.Round(t / SnapSeconds) * SnapSeconds;
            return Math.Max(0f, Math.Min(_duration, t));
        }

        private void UpdateScrollExtent()
        {
            double viewport = Math.Max(_rowsScroll.ViewportWidth, 1);
            double content = (_duration + 0.5f) * _pps; // half a second of tail room
            _hbar.Maximum = Math.Max(0, content - viewport);
            _hbar.ViewportSize = viewport;
            _hbar.LargeChange = viewport;
            _hbar.SmallChange = _pps * 0.1;
            if (_scrollX > _hbar.Maximum) { _scrollX = _hbar.Maximum; _hbar.Value = _scrollX; }
            _hbar.Visibility = _hbar.Maximum > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RedrawTime()
        {
            DrawRuler();
            DrawRows();
        }

        // major tick spacing adapts to zoom; minors subdivide by 5
        private float MajorStep() => _pps >= 240f ? 0.1f : (_pps >= 60f ? 0.5f : 1f);

        private void DrawRuler()
        {
            _ruler.Children.Clear();
            double w = _ruler.ActualWidth, h = RulerH;
            if (w < 4) return;

            var gridBrush = new SolidColorBrush(C_Grid);
            var textBrush = new SolidColorBrush(C_TextDim);
            float major = MajorStep();
            float minor = major / 5f;

            float t0 = Math.Max(0f, XToTime(0));
            float t1 = XToTime(w);
            int i0 = (int)Math.Floor(t0 / minor);
            int i1 = (int)Math.Ceiling(t1 / minor);
            for (int i = i0; i <= i1; i++)
            {
                float t = i * minor;
                if (t < -0.0001f) continue;
                double x = TimeToX(t);
                if (x < -2 || x > w + 2) continue;
                bool isMajor = i % 5 == 0;
                _ruler.Children.Add(new Line
                {
                    X1 = x, X2 = x, Y1 = isMajor ? h - 12 : h - 6, Y2 = h,
                    Stroke = gridBrush, StrokeThickness = 1, SnapsToDevicePixels = true
                });
                if (isMajor)
                {
                    var lbl = new TextBlock
                    {
                        Text = t.ToString(major < 0.5f ? "0.0#" : "0.#", CultureInfo.InvariantCulture),
                        Foreground = textBrush, FontSize = 10
                    };
                    Canvas.SetLeft(lbl, x + 3);
                    Canvas.SetTop(lbl, 1);
                    _ruler.Children.Add(lbl);
                }
            }

            // duration end-marker
            double xe = TimeToX(_duration);
            if (xe >= -2 && xe <= w + 2)
                _ruler.Children.Add(new Line
                {
                    X1 = xe, X2 = xe, Y1 = 0, Y2 = h,
                    Stroke = new SolidColorBrush(C_MicroHdr), StrokeThickness = 1.5, SnapsToDevicePixels = true
                });

            // event flags — sound events get a distinct violet flag + a ♪ tag so they read apart from script events
            if (_clip?.Events != null)
            {
                var evBrush = new SolidColorBrush(C_Event);
                var sndBrush = new SolidColorBrush(Color.FromRgb(0xB5, 0x9C, 0xFF));
                foreach (var ev in _clip.Events)
                {
                    double x = TimeToX(ev.T);
                    if (x < -8 || x > w + 8) continue;
                    bool isSound = !string.IsNullOrEmpty(ev.Sound) || !string.IsNullOrEmpty(ev.AudioSource);
                    var brush = isSound ? sndBrush : evBrush;
                    string tip = (string.IsNullOrEmpty(ev.Name) ? "(sound)" : ev.Name)
                                 + "  @ " + ev.T.ToString("0.###", CultureInfo.InvariantCulture) + "s"
                                 + (isSound && !string.IsNullOrEmpty(ev.Sound) ? ("\n♪ " + System.IO.Path.GetFileName(ev.Sound)) : "");
                    var flag = new Polygon
                    {
                        Points = new PointCollection { new Point(0, 0), new Point(8, 3.5), new Point(0, 7), new Point(0, 14) },
                        Fill = brush,
                        Stroke = brush,
                        StrokeThickness = 1,
                        Tag = ev,
                        Cursor = Cursors.Hand,
                        ToolTip = tip
                    };
                    Canvas.SetLeft(flag, x);
                    Canvas.SetTop(flag, 2);
                    flag.MouseRightButtonUp += OnEventFlagRightClick;
                    _ruler.Children.Add(flag);

                    if (isSound)
                    {
                        var note = new TextBlock
                        {
                            Text = "♪", FontSize = 9, Foreground = brush,
                            IsHitTestVisible = false, ToolTip = tip
                        };
                        Canvas.SetLeft(note, x + 6);
                        Canvas.SetTop(note, 1);
                        _ruler.Children.Add(note);
                    }
                }
            }

            DrawPlayheadOn(_ruler, RulerH);
        }

        private void DrawRows()
        {
            _rowsCanvas.Children.Clear();
            double w = _rowsCanvas.Width, h = _rows.Count * RowH;
            if (double.IsNaN(w) || w < 4) return;

            var altBrush = new SolidColorBrush(C_RowAlt);
            var gridBrush = new SolidColorBrush(C_Grid);

            for (int i = 0; i < _rows.Count; i++)
            {
                if ((i & 1) == 1)
                {
                    var bg = new Rectangle { Width = w, Height = RowH, Fill = altBrush };
                    Canvas.SetTop(bg, i * RowH);
                    _rowsCanvas.Children.Add(bg);
                }
                var sep = new Line { X1 = 0, X2 = w, Y1 = (i + 1) * RowH, Y2 = (i + 1) * RowH, Stroke = gridBrush, StrokeThickness = 0.5 };
                _rowsCanvas.Children.Add(sep);
            }

            // vertical grid at major ticks + duration end-marker across all rows
            float major = MajorStep();
            float t0 = Math.Max(0f, XToTime(0));
            float t1 = XToTime(w);
            for (int i = (int)Math.Floor(t0 / major); i <= (int)Math.Ceiling(t1 / major); i++)
            {
                double x = TimeToX(i * major);
                if (x < -2 || x > w + 2) continue;
                _rowsCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = h, Stroke = gridBrush, StrokeThickness = 0.5 });
            }
            double xe = TimeToX(_duration);
            if (xe >= -2 && xe <= w + 2)
                _rowsCanvas.Children.Add(new Line { X1 = xe, X2 = xe, Y1 = 0, Y2 = h, Stroke = new SolidColorBrush(C_MicroHdr), StrokeThickness = 1 });

            // key diamonds
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.Track == null) continue;
                foreach (float t in DistinctKeyTimes(row.Track))
                {
                    double x = TimeToX(t);
                    if (x < -KeySize || x > w + KeySize) continue;
                    bool sel = row.Bone == _selKeyBone && !float.IsNaN(_selKeyTime) && Math.Abs(t - _selKeyTime) < 0.0005f;
                    var d = MakeDiamond(sel);
                    d.Tag = new KeyRef { Row = row, Time = t };
                    Canvas.SetLeft(d, x - KeySize * 0.5);
                    Canvas.SetTop(d, i * RowH + (RowH - KeySize) * 0.5);
                    d.MouseLeftButtonDown += OnKeyDown_Diamond;
                    d.MouseMove += OnKeyMove_Diamond;
                    d.MouseLeftButtonUp += OnKeyUp_Diamond;
                    _rowsCanvas.Children.Add(d);
                }
            }

            DrawPlayheadOn(_rowsCanvas, h);
        }

        private class KeyRef { public Row Row; public float Time; }

        private Rectangle MakeDiamond(bool selected)
        {
            return new Rectangle
            {
                Width = KeySize, Height = KeySize,
                Fill = new SolidColorBrush(selected ? C_Accent : C_Key),
                Stroke = new SolidColorBrush(selected ? C_Accent : C_Grid),
                StrokeThickness = 1,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(45),
                Cursor = Cursors.SizeWE
            };
        }

        /// <summary>Union of a track's Pos/Rot/Scale key times, grouped within a small tolerance.</summary>
        private static List<float> DistinctKeyTimes(AnimTrack track)
        {
            var times = new List<float>();
            void Add(float t)
            {
                foreach (var e in times)
                    if (Math.Abs(e - t) < 0.0005f) return;
                times.Add(t);
            }
            if (track.Pos != null) foreach (var k in track.Pos) Add(k.T);
            if (track.Rot != null) foreach (var k in track.Rot) Add(k.T);
            if (track.Scale != null) foreach (var k in track.Scale) Add(k.T);
            times.Sort();
            return times;
        }

        private const string PlayheadTag = "playhead";

        private void DrawPlayheadOn(Canvas canvas, double height)
        {
            var line = new Line
            {
                X1 = TimeToX(_time), X2 = TimeToX(_time), Y1 = 0, Y2 = height,
                Stroke = new SolidColorBrush(C_Accent),
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
                Tag = PlayheadTag
            };
            canvas.Children.Add(line);
        }

        // playhead moves every frame during playback — reposition just the lines instead of full redraws
        private void RedrawPlayhead()
        {
            RemovePlayheadLines(_ruler, RulerH);
            RemovePlayheadLines(_rowsCanvas, _rows.Count * RowH);
        }

        private void RemovePlayheadLines(Canvas canvas, double height)
        {
            for (int i = canvas.Children.Count - 1; i >= 0; i--)
                if (canvas.Children[i] is Line l && Equals(l.Tag, PlayheadTag))
                    canvas.Children.RemoveAt(i);
            DrawPlayheadOn(canvas, height);
        }

        // ===================== scrub (ruler) =====================

        private void OnRulerDown(object s, MouseButtonEventArgs e)
        {
            _draggingPlayhead = true;
            _ruler.CaptureMouse();
            ScrubTo(e.GetPosition(_ruler).X);
        }

        private void OnRulerMove(object s, MouseEventArgs e)
        {
            if (_draggingPlayhead) ScrubTo(e.GetPosition(_ruler).X);
        }

        private void OnRulerUp(object s, MouseButtonEventArgs e)
        {
            if (!_draggingPlayhead) return;
            _draggingPlayhead = false;
            _ruler.ReleaseMouseCapture();
        }

        private void ScrubTo(double x)
        {
            float t = Snap(XToTime(x));
            _time = t;
            RedrawPlayhead();
            TimeChanged?.Invoke(t);
        }

        // ===================== keys: select / drag / delete / add =====================

        private void OnKeyDown_Diamond(object s, MouseButtonEventArgs e)
        {
            var d = (Rectangle)s;
            var kr = (KeyRef)d.Tag;
            _selKeyBone = kr.Row.Bone;
            _selKeyTime = kr.Time;
            _dragDiamond = d;
            _dragRow = kr.Row;
            _dragOrigTime = _dragNewTime = kr.Time;
            _draggingKey = true;
            d.CaptureMouse();
            Focus();
            // MUST NOT rebuild the canvas here: DrawRows() would Clear() the children and destroy the
            // just-captured Rectangle, killing the drag before any Move/Up arrives. Repaint selection
            // fills in place instead; TrackSelected (the window rebuilds panes on it) waits for mouse-up.
            UpdateDiamondSelection();
            e.Handled = true;
        }

        /// <summary>Repaint every diamond's selected/normal fill without touching the visual tree.</summary>
        private void UpdateDiamondSelection()
        {
            foreach (var child in _rowsCanvas.Children)
            {
                if (!(child is Rectangle r) || !(r.Tag is KeyRef kr)) continue;
                bool sel = kr.Row.Bone == _selKeyBone && !float.IsNaN(_selKeyTime) && Math.Abs(kr.Time - _selKeyTime) < 0.0005f;
                r.Fill = new SolidColorBrush(sel ? C_Accent : C_Key);
                r.Stroke = new SolidColorBrush(sel ? C_Accent : C_Grid);
            }
        }

        private void OnKeyMove_Diamond(object s, MouseEventArgs e)
        {
            if (!_draggingKey || !ReferenceEquals(s, _dragDiamond)) return;
            double x = e.GetPosition(_rowsCanvas).X;
            _dragNewTime = Snap(XToTime(x));
            Canvas.SetLeft(_dragDiamond, TimeToX(_dragNewTime) - KeySize * 0.5);
        }

        private void OnKeyUp_Diamond(object s, MouseButtonEventArgs e)
        {
            if (!_draggingKey || !ReferenceEquals(s, _dragDiamond)) return;
            _draggingKey = false;
            _dragDiamond.ReleaseMouseCapture();
            var track = _dragRow.Track;
            string bone = _dragRow.Bone;
            float from = _dragOrigTime, to = _dragNewTime;
            _dragDiamond = null;
            _dragRow = null;
            if (track == null || Math.Abs(to - from) < 0.0005f)
            {
                // plain click: selection fills were already updated in place on mouse-down
                TrackSelected?.Invoke(bone);
                return;
            }

            // snapshot-based command: capturing before/after key lists keeps undo exact even when the
            // move lands on (and replaces) an existing key
            var before = CloneTrackKeys(track);
            MoveKeysAtTime(track, from, to);
            var after = CloneTrackKeys(track);
            RestoreTrackKeys(track, before); // command Execute performs the actual mutation
            UndoRedoManager.Instance.Execute(new ActionCommand("Move Keyframe",
                () => RestoreTrackKeys(track, after),
                () => RestoreTrackKeys(track, before)));
            _selKeyTime = to;
            Changed?.Invoke();
            RedrawTime();
            TrackSelected?.Invoke(bone); // safe now — capture is released
        }

        private void OnRowsDown(object s, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            var p = e.GetPosition(_rowsCanvas);
            int rowIdx = (int)(p.Y / RowH);
            if (rowIdx < 0 || rowIdx >= _rows.Count) return;
            float t = Snap(XToTime(p.X));
            KeyAddRequested?.Invoke(_rows[rowIdx].Bone, t);
            e.Handled = true;
        }

        private void OnDeleteKeyDown(object s, KeyEventArgs e)
        {
            if (e.Key != Key.Delete || _selKeyBone == null || float.IsNaN(_selKeyTime)) return;
            var track = _clip?.FindTrack(_selKeyBone);
            if (track == null) return;
            var before = CloneTrackKeys(track);
            if (!RemoveKeysAtTime(track, _selKeyTime)) return;
            var after = CloneTrackKeys(track);
            RestoreTrackKeys(track, before);
            UndoRedoManager.Instance.Execute(new ActionCommand("Delete Keyframe",
                () => RestoreTrackKeys(track, after),
                () => RestoreTrackKeys(track, before)));
            _selKeyTime = float.NaN;
            Changed?.Invoke();
            RedrawTime();
            e.Handled = true;
        }

        // ---- key list mutation helpers (keys MUST stay sorted ascending by T) ----

        private const float KeyTol = 0.0005f;

        private static void MoveKeysAtTime(AnimTrack track, float from, float to)
        {
            void MoveV(List<AnimKeyVec3> keys)
            {
                if (keys == null) return;
                var moving = keys.FindAll(k => Math.Abs(k.T - from) < KeyTol);
                if (moving.Count == 0) return;
                keys.RemoveAll(k => Math.Abs(k.T - to) < KeyTol && !moving.Contains(k)); // overwrite target
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

        private class TrackKeys
        {
            public List<AnimKeyVec3> Pos;
            public List<AnimKeyQuat> Rot;
            public List<AnimKeyVec3> Scale;
        }

        private static TrackKeys CloneTrackKeys(AnimTrack t)
        {
            var c = new TrackKeys { Pos = new List<AnimKeyVec3>(), Rot = new List<AnimKeyQuat>(), Scale = new List<AnimKeyVec3>() };
            if (t.Pos != null) foreach (var k in t.Pos) c.Pos.Add(new AnimKeyVec3 { T = k.T, X = k.X, Y = k.Y, Z = k.Z });
            if (t.Rot != null) foreach (var k in t.Rot) c.Rot.Add(new AnimKeyQuat { T = k.T, X = k.X, Y = k.Y, Z = k.Z, W = k.W });
            if (t.Scale != null) foreach (var k in t.Scale) c.Scale.Add(new AnimKeyVec3 { T = k.T, X = k.X, Y = k.Y, Z = k.Z });
            return c;
        }

        private static void RestoreTrackKeys(AnimTrack t, TrackKeys snap)
        {
            t.Pos = new List<AnimKeyVec3>();
            t.Rot = new List<AnimKeyQuat>();
            t.Scale = new List<AnimKeyVec3>();
            foreach (var k in snap.Pos) t.Pos.Add(new AnimKeyVec3 { T = k.T, X = k.X, Y = k.Y, Z = k.Z });
            foreach (var k in snap.Rot) t.Rot.Add(new AnimKeyQuat { T = k.T, X = k.X, Y = k.Y, Z = k.Z, W = k.W });
            foreach (var k in snap.Scale) t.Scale.Add(new AnimKeyVec3 { T = k.T, X = k.X, Y = k.Y, Z = k.Z });
        }

        // ===================== events (ruler flags) =====================

        private void OnRulerRightClick(object s, MouseButtonEventArgs e)
        {
            if (_clip == null || e.OriginalSource is Polygon) return; // flags handle their own menu
            if (_clip.Events == null) _clip.Events = new List<AnimEvent>();
            float t = Snap(XToTime(e.GetPosition(_ruler).X));
            var menu = new ContextMenu();
            var add = new MenuItem { Header = "Add event here…  (" + t.ToString("0.###", CultureInfo.InvariantCulture) + "s)" };
            add.Click += (ms, me) =>
            {
                string name = PromptEventName("");
                if (string.IsNullOrWhiteSpace(name)) return;
                var ev = new AnimEvent { T = t, Name = name.Trim() };
                var clip = _clip;
                UndoRedoManager.Instance.Execute(new ActionCommand("Add Animation Event",
                    () => { clip.Events.Add(ev); clip.Events.Sort((a, b) => a.T.CompareTo(b.T)); },
                    () => clip.Events.Remove(ev)));
                Changed?.Invoke();
                RedrawTime();
            };
            menu.Items.Add(add);
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void OnEventFlagRightClick(object s, MouseButtonEventArgs e)
        {
            if (_clip == null || !((s as Polygon)?.Tag is AnimEvent ev)) return;
            var menu = new ContextMenu();
            var del = new MenuItem { Header = "Delete event \"" + ev.Name + "\"" };
            del.Click += (ms, me) =>
            {
                var clip = _clip;
                int idx = clip.Events.IndexOf(ev);
                if (idx < 0) return;
                UndoRedoManager.Instance.Execute(new ActionCommand("Delete Animation Event",
                    () => clip.Events.Remove(ev),
                    () => { clip.Events.Add(ev); clip.Events.Sort((a, b) => a.T.CompareTo(b.T)); }));
                Changed?.Invoke();
                RedrawTime();
            };
            menu.Items.Add(del);
            menu.IsOpen = true;
            e.Handled = true;
        }

        /// <summary>Tiny programmatic name prompt (house rule: no XAML, no new dialogs elsewhere).</summary>
        private string PromptEventName(string initial)
        {
            var dlg = new Window
            {
                Title = "Animation Event",
                Width = 320, Height = 140,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = Dialogs.DialogStyles.BackgroundBrush,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow
            };
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(Dialogs.DialogStyles.CreateLabel("Event name (fired into scripts)"));
            var box = Dialogs.DialogStyles.CreateTextBox(initial);
            panel.Children.Add(box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = Dialogs.DialogStyles.CreateButton("Add", 72, isPrimary: true);
            var cancel = Dialogs.DialogStyles.CreateButton("Cancel", 72);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            ok.Click += (s, e) => { dlg.DialogResult = true; };
            cancel.Click += (s, e) => { dlg.DialogResult = false; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            box.Focus();
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) dlg.DialogResult = true; };
            return dlg.ShowDialog() == true ? box.Text : null;
        }

        // ===================== zoom =====================

        private void OnZoomWheel(object s, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            if (_draggingKey) return; // zooming rebuilds the canvas — would destroy the captured diamond
            double mouseX = e.GetPosition(_rowsCanvas).X;
            float tUnder = XToTime(mouseX);
            _pps = Math.Max(40f, Math.Min(600f, _pps * (e.Delta > 0 ? 1.15f : 1f / 1.15f)));
            UpdateScrollExtent();
            _scrollX = Math.Max(0, Math.Min(_hbar.Maximum, tUnder * _pps - mouseX));
            _hbar.Value = _scrollX;
            RedrawTime();
            e.Handled = true;
        }
    }
}
