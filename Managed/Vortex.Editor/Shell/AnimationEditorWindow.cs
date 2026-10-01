using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.DllWrapper;
using VortexEditor.Controls;
using VortexEditor.Shell.Animation;
using Vec3 = System.Numerics.Vector3;
using Quat = System.Numerics.Quaternion;

namespace VortexEditor.Shell
{
    /// <summary>
    /// The Keyframe Editor — authors .vanim clips against a bound model: skinned 3D preview with a bone overlay (drag a
    /// joint to pose the bone), per-bone pose inspector (position / Euler rotation / scale at the playhead, keyed with
    /// [Key Bone]), a dope-sheet timeline (scrub, move / delete / add keys) and animation events (script name, sound,
    /// routing AudioSource). Import embedded clips from the model or a standalone .vanim, export the clip, save.
    /// Every keyframe / event mutation runs through the global UndoRedoManager. Port of the WPF Keyframe Editor.
    /// </summary>
    public sealed class AnimationEditorWindow : Window
    {
        private static AnimationEditorWindow _open;

        /// <summary>Open the (single) Keyframe Editor. Already open -> offer to save its edits, load the clip, focus it.</summary>
        public static void Open(string fullPath)
        {
            if (_open != null)
            {
                var w0 = _open;
                if (string.Equals(w0._path, AnimUtil.ToAbsolute(fullPath), StringComparison.OrdinalIgnoreCase)) { w0.Activate(); return; }
                Dispatcher.UIThread.Post(async () =>
                {
                    try { if (await w0.ConfirmDiscardOrSave()) w0.LoadClip(fullPath); w0.Activate(); }
                    catch (Exception ex) { EditorCommands.Fail("Could not open clip", ex); }
                });
                return;
            }
            var w = new AnimationEditorWindow(fullPath);
            EditorWindows.Show(w);
        }

        /// <summary>The open editor (tests / other windows).</summary>
        public static AnimationEditorWindow Current => _open;

        private string _path = "";
        private VortexAnimClip _clip = new VortexAnimClip();

        private readonly SkinnedPreview _preview = new SkinnedPreview();
        private readonly TimelineControl _timeline = new TimelineControl();
        private ListBox _boneList;
        private ListBox _clipList;
        private bool _suppressClipList;
        private TextBox _boneFilter;
        private StackPanel _inspector;
        private TextBlock _modelPathText;
        private TextBox _nameBox, _durBox, _fpsBox;
        private ToggleButton _snapBtn, _loopBtn;
        private Button _playBtn, _importBtn;
        private TextBlock _timeText;
        private readonly List<Button> _transport = new List<Button>();

        private bool _playing;
        private float _time;
        private bool _dirty;
        private bool _syncingUI;
        private string _selectedBone;
        private bool _suppressList;
        private bool _closingConfirmed;

        // working pose override for the SELECTED bone while the user types or drags a joint; committed by [Key Bone],
        // discarded on bone switch. Euler kept separately so typing X never wobbles Y/Z through the quaternion round-trip.
        private bool _hasOverride;
        private Vec3 _ovPos, _ovScale, _ovEuler;
        private Quat _ovRot;
        // snapshot taken when a joint drag starts — Esc mid-drag restores it
        private string _dragSnapBone;
        private bool _dragSnapHasOverride;
        private Vec3 _dragSnapPos, _dragSnapScale, _dragSnapEuler;
        private Quat _dragSnapRot;

        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _lastSec;
        private readonly List<Action> _valueRefreshers = new List<Action>();
        private Button _keyButton;

        private static readonly string[] ModelPatterns = { "*.fbx", "*.obj", "*.gltf", "*.glb", "*.dae" };
        private static readonly string[] AudioPatterns = { "*.wav", "*.mp3", "*.ogg", "*.flac", "*.vsndc" };

        public string ClipPath => _path;
        public VortexAnimClip Document => _clip;
        public SkinnedPreview Preview => _preview;
        public TimelineControl Timeline => _timeline;
        public bool IsPlaying => _playing;
        public bool IsDirty => _dirty;
        public float PlayheadTime => _time;
        public string SelectedBone => _selectedBone;
        /// <summary>Clip documents listed in the CLIPS panel (this one + its siblings).</summary>
        public int ClipListCount => (_clipList?.ItemsSource as List<string>)?.Count ?? 0;

        public AnimationEditorWindow(string path) : this()
        {
            try { LoadClip(path); }
            catch (Exception ex) { EditorCommands.Fail("Could not open clip", ex); }
        }

        private AnimationEditorWindow()
        {
            Title = "Keyframe Editor";
            Width = 1480; Height = 920; MinWidth = 1100; MinHeight = 660;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            // render the rig at ~1.8 m whatever its authoring units (cm Mixamo rigs) — purely visual: keys stay in
            // model units and the joint-drag math is scale-free; the studio lights are tuned for metre content
            _preview.NormalizeToHuman = true;
            BuildUI();

            _preview.BeforeFrame += OnFrame;
            _preview.BoneClicked += bone => { SelectBone(bone); SnapshotDragState(bone); };
            _preview.BoneRotated += OnBoneRotated;
            _preview.ModelRebound += () => { RefreshBoneTree(); RefreshInspector(); UpdatePreview(); };
            UndoRedoManager.Instance.CommandExecuted += OnUndoRedoExecuted;
            AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

            Opened += (s, e) => { if (_open == null) _open = this; AnimUi.FitToScreen(this); };
            Closing += OnClosingGuard;
            Closed += (s, e) =>
            {
                try { UndoRedoManager.Instance.CommandExecuted -= OnUndoRedoExecuted; } catch { }
                try { _preview.Viewport.Continuous = false; _preview.Dispose(); } catch { }
                if (ReferenceEquals(_open, this)) _open = null;
            };
        }

        // ===================================================================== document

        private void LoadClip(string vanimPath)
        {
            string abs = AnimUtil.ToAbsolute(vanimPath ?? "");
            _path = abs ?? "";
            _clip = VortexAnimClip.Load(abs) ?? new VortexAnimClip { Name = Path.GetFileNameWithoutExtension(vanimPath ?? "New Clip") };
            Normalize(_clip);

            SetPlaying(false);
            _time = 0f;
            _selectedBone = null;
            _hasOverride = false;
            _dirty = false;

            RefreshToolbarFromClip();
            RebindModel();
            _timeline.SetClip(_clip);
            _timeline.SetSelectedBone(null);
            _timeline.Time = 0f;
            _timeline.SnapSeconds = _snapBtn.IsChecked == true ? 1f / Math.Max(1f, _clip.FrameRate) : 0f;
            RefreshInspector();
            UpdatePreview();
            UpdateTimeText();
            UpdateTitle();
            RefreshClipList();
        }

        /// <summary>The .vanim documents next to this one (the CLIPS list).</summary>
        private void RefreshClipList()
        {
            if (_clipList == null) return;
            _suppressClipList = true;
            try
            {
                var files = new List<string>();
                try
                {
                    string dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir, "*.vanim").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                }
                catch { }
                if (!string.IsNullOrEmpty(_path) && !files.Any(f => string.Equals(f, _path, StringComparison.OrdinalIgnoreCase))) files.Insert(0, _path);
                _clipList.ItemsSource = files;
                var cur = files.FirstOrDefault(f => string.Equals(f, _path, StringComparison.OrdinalIgnoreCase));
                _clipList.SelectedItem = cur;
                if (cur != null) _clipList.ScrollIntoView(cur);
            }
            finally { _suppressClipList = false; }
        }

        /// <summary>New empty clip (bound to this clip's model) or a duplicate of this one, next to it; then switch to it.</summary>
        private async System.Threading.Tasks.Task CreateClip(bool duplicate)
        {
            string dir = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(dir)) dir = AnimUtil.ProjectDir("Assets", "Animations");
            if (string.IsNullOrEmpty(dir)) return;
            string suggestion = duplicate ? (_clip?.Name ?? "clip") + "_copy" : "new_clip";
            var name = await AnimUi.Prompt(this, duplicate ? "Duplicate clip" : "New clip", "Clip name (the .vanim file is created in " + AnimUtil.ToRelative(dir) + ")", suggestion, duplicate ? "Duplicate" : "Create");
            if (string.IsNullOrWhiteSpace(name)) return;
            string safe = string.Concat(name.Trim().Split(Path.GetInvalidFileNameChars()));
            if (safe.Length == 0) return;
            string file = Path.Combine(dir, safe + ".vanim");
            for (int n = 2; File.Exists(file); n++) file = Path.Combine(dir, safe + "_" + n + ".vanim");
            if (!await ConfirmDiscardOrSave()) return;
            VortexAnimClip doc;
            if (duplicate)
            {
                // a deep copy through the serializer (tracks / keys / events are reference types)
                string tmp = Path.Combine(Path.GetTempPath(), "vortex_clip_" + Guid.NewGuid().ToString("N") + ".vanim");
                _clip.Save(tmp);
                doc = VortexAnimClip.Load(tmp) ?? new VortexAnimClip();
                try { File.Delete(tmp); } catch { }
                doc.Name = safe;
            }
            else doc = new VortexAnimClip { Name = safe, Model = _clip?.Model ?? "", FrameRate = _clip?.FrameRate ?? 30f, DurationSec = 1f, Loop = true };
            if (!doc.Save(file)) { await AnimUi.Alert(this, "Keyframe Editor", "Could not create " + file); return; }
            try { Editor.Core.Assets.AssetDatabase.Instance.Refresh(); } catch { }
            try { EditorCommands.Window?.AssetBrowser?.Refresh(); } catch { }
            LoadClip(file);
        }

        /// <summary>Hand-edited JSON can deserialize lists as null — normalize once so the editor can assume them.</summary>
        private static void Normalize(VortexAnimClip clip)
        {
            if (clip.Tracks == null) clip.Tracks = new List<AnimTrack>();
            if (clip.Events == null) clip.Events = new List<AnimEvent>();
            foreach (var tr in clip.Tracks)
            {
                if (tr.Pos == null) tr.Pos = new List<AnimKeyVec3>();
                if (tr.Rot == null) tr.Rot = new List<AnimKeyQuat>();
                if (tr.Scale == null) tr.Scale = new List<AnimKeyVec3>();
            }
        }

        /// <summary>Write the clip. False (and still dirty) when the file could not be written.</summary>
        public bool Save()
        {
            if (string.IsNullOrEmpty(_path)) return false;
            if (_clip.Save(_path))
            {
                try { AnimationService.Instance.InvalidateClip(_path); } catch { }
                _dirty = false;
                UpdateTitle();
                EditorCommands.Toast("Saved " + Path.GetFileName(_path));
                return true;
            }
            _ = AnimUi.Alert(this, "Keyframe Editor", "Save failed — the file could not be written:\n" + _path);
            return false;
        }

        private void MarkDirty() { if (!_dirty) { _dirty = true; UpdateTitle(); } }
        private void UpdateTitle() => Title = "Keyframe Editor — " + (_clip?.Name ?? "?") + (_dirty ? " *" : "");

        /// <summary>Unsaved-changes guard (re-open + close). True = proceed (clean, saved or discarded).</summary>
        private async System.Threading.Tasks.Task<bool> ConfirmDiscardOrSave()
        {
            if (!_dirty) return true;
            var r = await AnimUi.Ask(this, "Save changes to \"" + (_clip?.Name ?? "clip") + "\"?", "Your changes are lost if you don't save them.", new[] { "Save", "Don't Save", "Cancel" });
            if (r.button == 2) return false;
            if (r.button == 0) return Save();
            return true;
        }

        private async void OnClosingGuard(object sender, WindowClosingEventArgs e)
        {
            if (_closingConfirmed || !_dirty) return;
            if (e.CloseReason == WindowCloseReason.OwnerWindowClosing || e.CloseReason == WindowCloseReason.ApplicationShutdown || e.CloseReason == WindowCloseReason.OSShutdown)
            {
                WriteRecoveryCopy();   // can't ask while the app goes down: keep the edits next to the project, untouched original
                return;
            }
            e.Cancel = true;
            if (await ConfirmDiscardOrSave()) { _closingConfirmed = true; Close(); }
        }

        private void WriteRecoveryCopy()
        {
            try
            {
                var root = ProjectData.Current?.Path;
                string dir = string.IsNullOrEmpty(root) ? Path.GetTempPath() : Path.Combine(root, ".ve", "recovery");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, Path.GetFileNameWithoutExtension(_path) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".vanim");
                if (_clip.Save(file)) Editor.Core.Services.ConsoleService.Instance.LogWarning("Keyframe Editor closed with unsaved changes — they were written to " + file);
            }
            catch { }
        }

        /// <summary>Close without the unsaved-changes prompt (tests).</summary>
        public void CloseDiscarding() { _closingConfirmed = true; Close(); }

        // ===================================================================== shell

        private void BuildUI()
        {
            var root = new DockPanel();
            var toolbar = BuildToolbar();
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(toolbar);

            var body = new Grid { RowDefinitions = new RowDefinitions("*,6,300"), Margin = new Thickness(8, 8, 8, 8) };
            body.RowDefinitions[0].MinHeight = 240;
            body.RowDefinitions[2].MinHeight = 140;

            var mid = new Grid { ColumnDefinitions = new ColumnDefinitions("280,6,*,6,330") };
            mid.ColumnDefinitions[0].MinWidth = 200;
            mid.ColumnDefinitions[4].MinWidth = 260;
            mid.Children.Add(BuildLeftPanel());
            var s1 = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent }; Grid.SetColumn(s1, 1); mid.Children.Add(s1);
            var well = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = AnimUi.Res("VxHairlineBrush"), BorderThickness = new Thickness(1), Child = _preview };
            Grid.SetColumn(well, 2); mid.Children.Add(well);
            var s2 = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent }; Grid.SetColumn(s2, 3); mid.Children.Add(s2);
            var right = BuildRightPanel(); Grid.SetColumn(right, 4); mid.Children.Add(right);
            body.Children.Add(mid);

            var rs = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brushes.Transparent }; Grid.SetRow(rs, 1); body.Children.Add(rs);

            var tlBorder = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = AnimUi.Res("VxHairlineBrush"), BorderThickness = new Thickness(1), Child = _timeline };
            Grid.SetRow(tlBorder, 2); body.Children.Add(tlBorder);
            _timeline.TimeChanged += t =>
            {
                _time = t;
                UpdateTimeText();
                UpdatePreview();
                if (!_playing) RefreshInspectorValues();
            };
            _timeline.TrackSelected += bone => SelectBone(bone);
            _timeline.Changed += () =>
            {
                MarkDirty();
                RefreshBoneTree();
                UpdatePreview();
                if (!_playing) RefreshInspector();
            };
            _timeline.KeyAddRequested += (bone, t) => KeyBoneAt(bone, t, useOverride: false);

            root.Children.Add(body);
            Content = root;
        }

        private Control BuildToolbar()
        {
            var bar = new Border { Background = AnimUi.Res("VxToolbarBrush"), BorderBrush = AnimUi.Res("VxHairlineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10, 8) };
            var dock = new DockPanel();
            var save = new Button { Content = "Save", Classes = { "accent" }, MinWidth = 92, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(save, "Save clip (Cmd+S)");
            save.Click += (s, e) => Save();
            DockPanel.SetDock(save, Dock.Right);
            dock.Children.Add(save);

            var left = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(AnimUi.ToolLabel("CLIP"));
            _nameBox = AnimUi.ToolTextBox(150, v => { if (v == _clip.Name) return; _clip.Name = v; MarkDirty(); UpdateTitle(); }, () => _syncingUI);
            left.Children.Add(_nameBox);
            left.Children.Add(AnimUi.ToolLabel("DUR"));
            _durBox = AnimUi.ToolTextBox(62, v =>
            {
                if (!PropertyRowsParse(v, out float f)) { RefreshToolbarFromClip(); return; }
                f = Math.Max(0.01f, f);
                if (Math.Abs(f - _clip.DurationSec) < 1e-6f) return;
                _clip.DurationSec = f;
                _timeline.Duration = f;
                if (_time > f) SetTime(f);
                MarkDirty(); UpdateTimeText();
            }, () => _syncingUI);
            left.Children.Add(_durBox);
            left.Children.Add(AnimUi.ToolLabel("FPS"));
            _fpsBox = AnimUi.ToolTextBox(52, v =>
            {
                if (!PropertyRowsParse(v, out float f)) { RefreshToolbarFromClip(); return; }
                f = Math.Max(1f, Math.Min(240f, f));
                if (Math.Abs(f - _clip.FrameRate) < 1e-6f) return;
                _clip.FrameRate = f;
                if (_snapBtn.IsChecked == true) _timeline.SnapSeconds = 1f / f;
                MarkDirty();
            }, () => _syncingUI);
            left.Children.Add(_fpsBox);
            _snapBtn = AnimUi.ToolToggle("Snap", true, "Snap timeline edits to the frame grid (1/FPS)", on => _timeline.SnapSeconds = on ? 1f / Math.Max(1f, _clip.FrameRate) : 0f);
            left.Children.Add(_snapBtn);
            left.Children.Add(AnimUi.ToolSeparator());

            var toStart = AnimUi.ToolButton(AnimUi.JumpStartIcon, null, "Jump to start (Home)", () => SetTime(0f));
            var prev = AnimUi.ToolButton(AnimUi.StepBackIcon, null, "Previous frame (←)", () => SetTime(_time - 1f / Math.Max(1f, _clip.FrameRate)));
            _playBtn = AnimUi.ToolButton("Play", null, "Play / pause (Space)", TogglePlay);
            var next = AnimUi.ToolButton(AnimUi.StepForwardIcon, null, "Next frame (→)", () => SetTime(_time + 1f / Math.Max(1f, _clip.FrameRate)));
            var toEnd = AnimUi.ToolButton(AnimUi.JumpEndIcon, null, "Jump to end (End)", () => SetTime(_clip.DurationSec));
            _transport.AddRange(new[] { toStart, prev, _playBtn, next, toEnd });
            foreach (var b in _transport) left.Children.Add(b);
            _loopBtn = AnimUi.ToolToggle("Loop", true, "Loop the clip (saved with it)", on => { if (!_syncingUI && _clip.Loop != on) { _clip.Loop = on; MarkDirty(); } });
            left.Children.Add(_loopBtn);
            _timeText = new TextBlock { Text = "0.00 / 1.00 s", Classes = { "mono", "secondary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0), MinWidth = 104 };
            left.Children.Add(_timeText);
            left.Children.Add(AnimUi.ToolSeparator());
            _importBtn = AnimUi.ToolButton("Import", "From model", "Copy a clip embedded in the bound model into this document", ShowImportMenu);
            left.Children.Add(_importBtn);
            left.Children.Add(AnimUi.ToolButton("Import", "Import .vanim", "Load a standalone .vanim clip from disk into this document (animation only — no character)", ImportClipFromFile));
            left.Children.Add(AnimUi.ToolButton("Export", "Export .vanim", "Save this clip to a standalone .vanim file (animation only — reusable on any compatible skeleton)", ExportClipToFile));
            dock.Children.Add(left);
            bar.Child = dock;
            return bar;
        }

        private static bool PropertyRowsParse(string s, out float f) => Panels.Inspector.PropertyRows.TryParse(s, out f);

        private Control BuildLeftPanel()
        {
            var panel = new Border { Classes = { "card" }, Padding = new Thickness(10) };
            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            var sec = new StackPanel();
            sec.Children.Add(AnimUi.MicroHeader("MODEL"));
            _modelPathText = new TextBlock { Text = "No model bound", Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            sec.Children.Add(_modelPathText);
            var bind = new Button { Content = "Bind / Change…", HorizontalAlignment = HorizontalAlignment.Left };
            ToolTip.SetTip(bind, "Choose the model (skeleton) this clip is authored against — also accepts a model dropped here");
            bind.Click += async (s, e) =>
            {
                var picked = await AssetPickerDialog.Pick("Models", ModelPatterns);
                if (!string.IsNullOrEmpty(picked)) BindModelPath(picked);
            };
            sec.Children.Add(bind);
            DragDrop.SetAllowDrop(sec, true);
            sec.AddHandler(DragDrop.DragOverEvent, (s, e) => { var p = Panels.Inspector.PropertyRows.DroppedPath(e, "vortex/asset"); e.DragEffects = p != null && Panels.Inspector.PropertyRows.Matches(p, ModelPatterns) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; });
            sec.AddHandler(DragDrop.DropEvent, (s, e) => { var p = Panels.Inspector.PropertyRows.DroppedPath(e, "vortex/asset"); if (p != null && Panels.Inspector.PropertyRows.Matches(p, ModelPatterns)) { BindModelPath(p); e.Handled = true; } });

            // CLIPS: the clip documents next to this one (switch with the unsaved-changes guard), new / duplicate
            var clipsHead = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            var clipBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            var newClip = new Button { Classes = { "icon", "small" }, Content = AnimUi.Icon("Plus", 12) };
            ToolTip.SetTip(newClip, "New clip in this folder (bound to the same model)");
            newClip.Click += async (s, e) => await CreateClip(duplicate: false);
            var dupClip = new Button { Classes = { "icon", "small" }, Content = AnimUi.Icon("Layers", 12) };
            ToolTip.SetTip(dupClip, "Duplicate this clip (with its current, unsaved edits)");
            dupClip.Click += async (s, e) => await CreateClip(duplicate: true);
            clipBtns.Children.Add(newClip); clipBtns.Children.Add(dupClip);
            DockPanel.SetDock(clipBtns, Dock.Right);
            clipsHead.Children.Add(clipBtns);
            clipsHead.Children.Add(AnimUi.MicroHeader("CLIPS", 2));
            sec.Children.Add(clipsHead);
            _clipList = new ListBox { MaxHeight = 150, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 4) };
            ScrollViewer.SetHorizontalScrollBarVisibility(_clipList, ScrollBarVisibility.Disabled);
            _clipList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((p, _) =>
            {
                bool cur = string.Equals(p, _path, StringComparison.OrdinalIgnoreCase);
                var tb = new TextBlock { Text = Path.GetFileNameWithoutExtension(p), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = cur ? FontWeight.SemiBold : FontWeight.Normal };
                ToolTip.SetTip(tb, AnimUtil.ToRelative(p));
                return tb;
            });
            _clipList.SelectionChanged += async (s, e) =>
            {
                if (_suppressClipList || !(_clipList.SelectedItem is string p) || string.Equals(p, _path, StringComparison.OrdinalIgnoreCase)) return;
                if (await ConfirmDiscardOrSave()) LoadClip(p); else RefreshClipList();
            };
            sec.Children.Add(_clipList);

            sec.Children.Add(AnimUi.MicroHeader("BONES", 10));
            _boneFilter = new TextBox { Classes = { "search" }, Watermark = "Filter bones…", Margin = new Thickness(0, 0, 0, 6) };
            _boneFilter.TextChanged += (s, e) => RefreshBoneTree();
            sec.Children.Add(_boneFilter);
            grid.Children.Add(sec);

            _boneList = new ListBox { Background = Brushes.Transparent };
            ScrollViewer.SetHorizontalScrollBarVisibility(_boneList, ScrollBarVisibility.Disabled);
            _boneList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<BoneItem>((b, _) =>
            {
                if (b == null) return new TextBlock();
                var tb = new TextBlock
                {
                    Text = b.Display, Margin = new Thickness(b.Depth * 11, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12,
                    FontWeight = b.Tracked ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = b.Tracked ? AnimUi.Res("VxAccentBrush") : AnimUi.Res("VxTextBrush")
                };
                ToolTip.SetTip(tb, b.Name + (b.Tracked ? "  (has keys)" : ""));
                return tb;
            });
            _boneList.SelectionChanged += (s, e) =>
            {
                if (_suppressList) return;
                if (_boneList.SelectedItem is BoneItem b) SelectBone(b.Name);
            };
            Grid.SetRow(_boneList, 1);
            grid.Children.Add(_boneList);
            panel.Child = grid;
            return panel;
        }

        private sealed class BoneItem
        {
            public string Name, Display;
            public int Depth;
            public bool Tracked;
        }

        private Control BuildRightPanel()
        {
            var panel = new Border { Classes = { "card" }, Padding = new Thickness(12, 10) };
            _inspector = new StackPanel();
            panel.Child = new ScrollViewer { Content = _inspector, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            return panel;
        }

        // ===================================================================== playback

        private void OnFrame()
        {
            if (!_playing) return;
            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)(now - _lastSec);
            _lastSec = now;
            if (dt <= 0f) return;
            dt = Math.Min(dt, 0.25f);
            _time += dt;
            float dur = Math.Max(_clip.DurationSec, 0.0001f);
            if (_time >= dur)
            {
                if (_clip.Loop) _time %= dur;
                else { _time = dur; Dispatcher.UIThread.Post(() => SetPlaying(false)); }
            }
            _timeline.Time = _time;
            UpdateTimeText();
            UpdatePreview();
        }

        public void TogglePlay() => SetPlaying(!_playing);

        public void SetPlaying(bool playing)
        {
            if (playing && !HasModel()) playing = false;
            if (playing && _time >= _clip.DurationSec - 0.0001f) _time = 0f;
            _playing = playing;
            if (_playBtn != null) _playBtn.Content = AnimUi.Icon(playing ? "Pause" : "Play");
            _lastSec = _clock.Elapsed.TotalSeconds;
            _preview.Viewport.Continuous = playing;
            if (!playing) { RefreshInspectorValues(); _preview.Invalidate(); }
        }

        public void SetTime(float t)
        {
            _time = Math.Max(0f, Math.Min(_clip.DurationSec, t));
            _timeline.Time = _time;
            UpdateTimeText();
            UpdatePreview();
            if (!_playing) RefreshInspectorValues();
        }

        private void UpdateTimeText()
            => _timeText.Text = _time.ToString("0.00", CultureInfo.InvariantCulture) + " / " + _clip.DurationSec.ToString("0.00", CultureInfo.InvariantCulture) + " s";

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool inText = e.Source is TextBox || (e.Source as Visual)?.FindAncestorOfType<TextBox>() != null;
            if (cmd && e.Key == Key.S) { Save(); e.Handled = true; return; }
            if (inText) return;
            if (cmd && e.Key == Key.Z) { if (shift) UndoRedoManager.Instance.Redo(); else UndoRedoManager.Instance.Undo(); e.Handled = true; }
            else if (cmd && e.Key == Key.Y) { UndoRedoManager.Instance.Redo(); e.Handled = true; }
            else if (e.Key == Key.Space && HasModel()) { TogglePlay(); e.Handled = true; }
            else if (e.Key == Key.F && !cmd)
            {
                // F = focus the selected bone; Shift+F (or F with nothing selected) = reset the view
                if (shift || string.IsNullOrEmpty(_selectedBone)) _preview.ResetFocus(); else _preview.FocusSelectedBone();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _preview.IsBoneDragActive) { CancelBoneDragAndRestore(); e.Handled = true; }
            else if (e.Key == Key.Home) { SetTime(0f); e.Handled = true; }
            else if (e.Key == Key.End) { SetTime(_clip.DurationSec); e.Handled = true; }
            else if ((e.Key == Key.Left || e.Key == Key.Right) && !(e.Source is ListBoxItem) && !(e.Source is ListBox))
            {
                SetTime(_time + (e.Key == Key.Left ? -1f : 1f) / Math.Max(1f, _clip.FrameRate));
                e.Handled = true;
            }
        }

        private void OnUndoRedoExecuted(object sender, CommandExecutedEventArgs e)
        {
            // refresh after a global undo / redo — our own Execute paths refresh explicitly. NO dirty marking here: this
            // fires for EVERY command in the editor (scene edits included).
            if (e.ExecutionType == CommandExecutionType.Execute) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible) return;
                RefreshToolbarFromClip();
                RebindModel();          // "Bind model" is undoable too (a no-op when the model didn't change)
                _timeline.Refresh();
                RefreshBoneTree();
                UpdatePreview();
                if (!_playing) RefreshInspector();
                UpdateTitle();
            });
        }

        // ===================================================================== model binding

        private bool HasModel()
        {
            string full = ResolveModelFullPath();
            return full != null && File.Exists(full) && _preview.HasMeshes;
        }

        private string ResolveModelFullPath() => string.IsNullOrEmpty(_clip?.Model) ? null : AnimUtil.ToAbsolute(_clip.Model);

        private void BindModelPath(string picked)
        {
            string rel = AnimUtil.ToRelative(picked);
            if (rel == _clip.Model) return;
            string old = _clip.Model;
            var clip = _clip;
            UndoRedoManager.Instance.Execute(new ActionCommand("Bind model", () => clip.Model = rel, () => clip.Model = old));
            MarkDirty();
            RebindModel();
            RefreshInspector();
            UpdatePreview();
        }

        private void RebindModel()
        {
            string full = ResolveModelFullPath();
            bool exists = full != null && File.Exists(full);
            _modelPathText.Text = string.IsNullOrEmpty(_clip.Model) ? "No model bound" : _clip.Model;
            ToolTip.SetTip(_modelPathText, full);
            _preview.SetEmptyHint(string.IsNullOrEmpty(_clip.Model) ? "Bind a model to begin" : !exists ? "Model not found:  " + _clip.Model : "Loading model…");
            _preview.BindModel(exists ? full : null);
            foreach (var b in _transport) b.IsEnabled = exists;
            if (!exists) SetPlaying(false);
            RefreshBoneTree();
        }

        // ===================================================================== bone tree

        public void RefreshBoneTree()
        {
            var skel = _preview.Skeleton;
            var items = new List<BoneItem>();
            if (skel?.Nodes != null && skel.Nodes.Length > 0)
            {
                int n = skel.Nodes.Length;
                string filter = _boneFilter?.Text?.Trim() ?? "";
                if (filter.Length > 0)
                {
                    // FLAT filtered list (case-insensitive substring on the full name)
                    for (int i = 0; i < n; i++)
                    {
                        string name = skel.Nodes[i].Name;
                        if (AnimUtil.IsHiddenNode(name) || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        items.Add(MakeBoneItem(name, 0));
                    }
                }
                else
                {
                    // hierarchy with hidden pivot nodes collapsed onto their nearest visible ancestor, DFS order, indented
                    var kids = new List<int>[n];
                    var roots = new List<int>();
                    for (int i = 0; i < n; i++)
                    {
                        if (AnimUtil.IsHiddenNode(skel.Nodes[i].Name)) continue;
                        int p = skel.Nodes[i].Parent, guard = 0;
                        while (p >= 0 && p < n && AnimUtil.IsHiddenNode(skel.Nodes[p].Name) && guard++ < n) p = skel.Nodes[p].Parent;
                        if (p >= 0 && p < n && p != i) (kids[p] = kids[p] ?? new List<int>()).Add(i);
                        else roots.Add(i);
                    }
                    void Add(int idx, int depth)
                    {
                        items.Add(MakeBoneItem(skel.Nodes[idx].Name, depth));
                        if (kids[idx] != null) foreach (int c in kids[idx]) Add(c, depth + 1);
                    }
                    foreach (int r in roots) Add(r, 0);
                }
            }
            _suppressList = true;
            try
            {
                _boneList.ItemsSource = items;
                var sel = items.FirstOrDefault(b => b.Name == _selectedBone);
                _boneList.SelectedItem = sel;
                if (sel != null) _boneList.ScrollIntoView(sel);
            }
            finally { _suppressList = false; }
        }

        private BoneItem MakeBoneItem(string name, int depth)
            => new BoneItem { Name = name, Display = AnimUtil.DisplayBoneName(name), Depth = depth, Tracked = _clip?.FindTrack(name) != null };

        private void SelectBoneInList(string bone)
        {
            _suppressList = true;
            try
            {
                if (_boneList.ItemsSource is List<BoneItem> items)
                {
                    var it = items.FirstOrDefault(b => b.Name == bone);
                    _boneList.SelectedItem = it;
                    if (it != null) _boneList.ScrollIntoView(it);
                }
            }
            finally { _suppressList = false; }
        }

        // ===================================================================== selection + pose override

        public void SelectBone(string bone)
        {
            if (bone == _selectedBone) return;
            _selectedBone = bone;
            _hasOverride = false;   // the typed pose belongs to the previous bone
            _preview.SetSelectedBone(bone);
            _timeline.SetSelectedBone(bone);
            _timeline.RevealBone(bone);
            SelectBoneInList(bone);
            RefreshInspector();
            UpdatePreview();
        }

        private (Vec3 pos, Quat rot, Vec3 scale)? OverrideFor(string bone)
            => _hasOverride && bone == _selectedBone ? (_ovPos, _ovRot, _ovScale) : ((Vec3, Quat, Vec3)?)null;

        private void SnapshotDragState(string bone)
        {
            _dragSnapBone = bone;
            _dragSnapHasOverride = _hasOverride;
            _dragSnapPos = _ovPos; _dragSnapRot = _ovRot; _dragSnapScale = _ovScale; _dragSnapEuler = _ovEuler;
        }

        /// <summary>Joint drag: compose the LOCAL rotation delta onto the working override — the same "modified pose" a
        /// typed edit produces, so [Key Bone] commits it unchanged.</summary>
        private void OnBoneRotated(string bone, Quat localDelta)
        {
            if (string.IsNullOrEmpty(bone)) return;
            if (bone != _selectedBone) SelectBone(bone);
            EnsureOverride();
            _ovRot = Quat.Normalize(Quat.Concatenate(_ovRot, localDelta));
            _ovEuler = AnimUtil.ToEulerDeg(_ovRot);
            UpdatePreview();
            if (!_playing) RefreshInspectorValues();
        }

        /// <summary>The same path a joint drag in the preview takes (tests / scripted posing): compose a LOCAL rotation
        /// delta onto the selected bone's working pose.</summary>
        public void ApplyBoneRotation(string bone, Quat localDelta) => OnBoneRotated(bone, localDelta);

        /// <summary>A typed / dragged pose is pending for the selected bone (committed by Key Bone).</summary>
        public bool HasPoseOverride => _hasOverride;

        private void CancelBoneDragAndRestore()
        {
            _preview.CancelBoneDrag();
            if (_dragSnapBone == _selectedBone)
            {
                _hasOverride = _dragSnapHasOverride;
                _ovPos = _dragSnapPos; _ovRot = _dragSnapRot; _ovScale = _dragSnapScale; _ovEuler = _dragSnapEuler;
            }
            else _hasOverride = false;
            UpdatePreview();
            if (!_playing) RefreshInspectorValues();
        }

        private void UpdatePreview() => _preview.SetPose(_clip, _time, OverrideFor);

        private void EnsureOverride()
        {
            if (_hasOverride) return;
            SamplePoseAt(_selectedBone, _time, out _ovPos, out _ovRot, out _ovScale);
            _ovEuler = AnimUtil.ToEulerDeg(_ovRot);
            _hasOverride = true;
        }

        /// <summary>Bone's local TRS at `time`: keyed values with per-component bind-pose fallback.</summary>
        private void SamplePoseAt(string bone, float time, out Vec3 pos, out Quat rot, out Vec3 scale)
        {
            pos = Vec3.Zero; rot = Quat.Identity; scale = Vec3.One;
            var skel = _preview.Skeleton;
            int node = skel != null ? skel.FindNode(bone) : -1;
            if (node >= 0) { var n = skel.Nodes[node]; pos = n.BindTranslation; rot = n.BindRotation; scale = n.BindScale; }
            var track = _clip?.FindTrack(bone);
            if (track != null)
            {
                if (track.Pos != null && track.Pos.Count > 0) pos = AnimationService.SampleVec3(track.Pos, time);
                if (track.Rot != null && track.Rot.Count > 0) rot = AnimationService.SampleQuat(track.Rot, time);
                if (track.Scale != null && track.Scale.Count > 0) scale = AnimationService.SampleVec3(track.Scale, time);
            }
        }

        private Vec3 CurPos() { if (_hasOverride) return _ovPos; SamplePoseAt(_selectedBone, _time, out var p, out _, out _); return p; }
        private Vec3 CurScale() { if (_hasOverride) return _ovScale; SamplePoseAt(_selectedBone, _time, out _, out _, out var s); return s; }
        private Vec3 CurEuler() { if (_hasOverride) return _ovEuler; SamplePoseAt(_selectedBone, _time, out _, out var r, out _); return AnimUtil.ToEulerDeg(r); }

        // ===================================================================== inspector

        public void RefreshInspector()
        {
            _inspector.Children.Clear();
            _valueRefreshers.Clear();
            _keyButton = null;
            _inspector.Children.Add(AnimUi.MicroHeader("KEY INSPECTOR"));
            var skel = _preview.Skeleton;
            if (skel == null || !skel.IsValid)
            {
                _inspector.Children.Add(AnimUi.NoteBox(string.IsNullOrEmpty(_clip?.Model) ? "Bind a model with a skeleton to pose bones (Bind / Change… on the left)." : "Bind a model with a skeleton to pose bones."));
                BuildEventsSection();
                return;
            }
            if (string.IsNullOrEmpty(_selectedBone))
            {
                _inspector.Children.Add(AnimUi.NoteBox("Select a bone — click it in the BONES list or a joint in the preview. Drag a joint to rotate the bone, "
                    + "Shift+drag or right/middle-drag to pan, wheel to zoom, double-click a joint (or F) to focus it, Shift+F to reset the view. "
                    + "Double-click a timeline row to key the bone at that time."));
                BuildEventsSection();
                return;
            }
            var title = new TextBlock { Text = AnimUtil.DisplayBoneName(_selectedBone), FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 2, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(title, _selectedBone);
            _inspector.Children.Add(title);
            var sub = new TextBlock { Text = _selectedBone, Classes = { "small", "tertiary" }, Margin = new Thickness(0, 0, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis };
            _inspector.Children.Add(sub);

            _inspector.Children.Add(AnimUi.MicroHeader("POSITION"));
            _inspector.Children.Add(AnimUi.LiveVec3(CurPos, v => { EnsureOverride(); _ovPos = v; UpdatePreview(); }, 0.01, out var rp));
            _valueRefreshers.Add(rp);
            _inspector.Children.Add(AnimUi.MicroHeader("ROTATION  (EULER °)"));
            _inspector.Children.Add(AnimUi.LiveVec3(CurEuler, v => { EnsureOverride(); _ovEuler = v; _ovRot = AnimUtil.FromEulerDeg(v); UpdatePreview(); }, 1, out var rr));
            _valueRefreshers.Add(rr);
            _inspector.Children.Add(AnimUi.MicroHeader("SCALE"));
            _inspector.Children.Add(AnimUi.LiveVec3(CurScale, v => { EnsureOverride(); _ovScale = v; UpdatePreview(); }, 0.01, out var rs));
            _valueRefreshers.Add(rs);

            _keyButton = new Button { Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            ToolTip.SetTip(_keyButton, "Write position, rotation and scale keys for this bone at the playhead (the edited pose, or the pose shown)");
            _keyButton.Click += (s, e) => KeyBoneAt(_selectedBone, _time, useOverride: true);
            _inspector.Children.Add(_keyButton);
            var del = new Button { Content = "Delete Keys @ Time", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            del.Click += (s, e) => DeleteKeysAtTime(_selectedBone, _time);
            _inspector.Children.Add(del);
            var revert = new Button { Content = "Discard Pose Edit", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            ToolTip.SetTip(revert, "Forget the typed / dragged pose and show the keyed pose again");
            revert.Click += (s, e) => { _hasOverride = false; UpdatePreview(); RefreshInspectorValues(); };
            _inspector.Children.Add(revert);
            _valueRefreshers.Add(() => { if (_keyButton != null) _keyButton.Content = "Key Bone @ " + _time.ToString("0.00", CultureInfo.InvariantCulture) + "s" + (_hasOverride ? "  •" : ""); revert.IsEnabled = _hasOverride; });
            RefreshInspectorValues();
            BuildEventsSection();
        }

        /// <summary>Re-read pose values + the key button label without rebuilding (scrub, drag, playback stop).</summary>
        private void RefreshInspectorValues() { foreach (var r in _valueRefreshers) { try { r(); } catch { } } }

        private void BuildEventsSection()
        {
            _inspector.Children.Add(new Border { Height = 14 });
            _inspector.Children.Add(AnimUi.MicroHeader("EVENTS"));
            if (_clip?.Events != null && _clip.Events.Count == 0)
                _inspector.Children.Add(new TextBlock { Text = "No events. Events fire OnAnimationEvent in scripts and can play a sound when the playhead crosses them.", Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            if (_clip?.Events != null)
                foreach (var ev in _clip.Events.ToList()) _inspector.Children.Add(EventRow(ev));
            var add = new Button { Content = "+ Add Event @ Playhead", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            add.Click += (s, e) =>
            {
                var clip = _clip;
                var ev = new AnimEvent { T = _time, Name = "Event" };
                UndoRedoManager.Instance.Execute(new ActionCommand("Add Animation Event",
                    () => { clip.Events.Add(ev); clip.Events.Sort((a, b) => a.T.CompareTo(b.T)); },
                    () => clip.Events.Remove(ev)));
                MarkDirty();
                _timeline.Refresh();
                RefreshInspector();
            };
            _inspector.Children.Add(add);
        }

        private Control EventRow(AnimEvent evRef)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("60,*,Auto"), Margin = new Thickness(0, 0, 0, 4) };
            var time = AnimUi.ToolTextBox(60, v =>
            {
                if (!PropertyRowsParse(v, out float f)) return;
                f = Math.Max(0f, Math.Min(_clip.DurationSec, f));
                if (Math.Abs(f - evRef.T) < 1e-6f) return;
                evRef.T = f;
                _clip.Events.Sort((a, b) => a.T.CompareTo(b.T));
                MarkDirty(); _timeline.Refresh();
            });
            time.Width = double.NaN; time.Classes.Add("number");
            time.Text = evRef.T.ToString("0.###", CultureInfo.InvariantCulture);
            ToolTip.SetTip(time, "Time (s)");
            row.Children.Add(time);
            var name = AnimUi.ToolTextBox(0, v => { if (v == evRef.Name) return; evRef.Name = v; MarkDirty(); _timeline.Refresh(); });
            name.Width = double.NaN; name.Text = evRef.Name ?? ""; name.Watermark = "event name"; name.Margin = new Thickness(4, 0);
            ToolTip.SetTip(name, "Name dispatched to scripts (OnAnimationEvent)");
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var x = new Button { Classes = { "icon" }, Content = AnimUi.Icon("Close", 12) };
            ToolTip.SetTip(x, "Delete event");
            x.Click += (s, e) =>
            {
                var clip = _clip;
                UndoRedoManager.Instance.Execute(new ActionCommand("Delete Animation Event",
                    () => clip.Events.Remove(evRef),
                    () => { clip.Events.Add(evRef); clip.Events.Sort((a, b) => a.T.CompareTo(b.T)); }));
                MarkDirty(); _timeline.Refresh(); RefreshInspector();
            };
            Grid.SetColumn(x, 2); row.Children.Add(x);
            box.Children.Add(row);

            // sound slot: a clip played AUTOMATICALLY when the playhead crosses this event
            bool hasSound = !string.IsNullOrEmpty(evRef.Sound);
            var sRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var sBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new TextBlock { Text = hasSound ? "♪  " + Path.GetFileName(evRef.Sound) : "+ Add sound…", TextTrimming = TextTrimming.CharacterEllipsis, Foreground = hasSound ? AnimUi.Res("VxPurpleBrush") : AnimUi.Res("VxTextSecondaryBrush") }
            };
            ToolTip.SetTip(sBtn, hasSound ? evRef.Sound : "Play a sound automatically when the playhead reaches this event");
            sBtn.Click += async (s, e) =>
            {
                var picked = await AssetPickerDialog.Pick("Audio", AudioPatterns);
                if (string.IsNullOrEmpty(picked)) return;
                evRef.Sound = AnimUtil.ToRelative(picked);
                MarkDirty(); _timeline.Refresh(); RefreshInspector();
            };
            DragDrop.SetAllowDrop(sBtn, true);
            sBtn.AddHandler(DragDrop.DragOverEvent, (s, e) => { var p = Panels.Inspector.PropertyRows.DroppedPath(e, "vortex/asset"); e.DragEffects = p != null && Panels.Inspector.PropertyRows.Matches(p, AudioPatterns) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; });
            sBtn.AddHandler(DragDrop.DropEvent, (s, e) => { var p = Panels.Inspector.PropertyRows.DroppedPath(e, "vortex/asset"); if (p != null && Panels.Inspector.PropertyRows.Matches(p, AudioPatterns)) { evRef.Sound = AnimUtil.ToRelative(p); MarkDirty(); _timeline.Refresh(); RefreshInspector(); e.Handled = true; } });
            sRow.Children.Add(sBtn);
            if (hasSound)
            {
                var clr = new Button { Classes = { "icon" }, Content = AnimUi.Icon("Close", 12), Margin = new Thickness(4, 0, 0, 0) };
                ToolTip.SetTip(clr, "Remove sound");
                clr.Click += (s, e) => { evRef.Sound = null; MarkDirty(); _timeline.Refresh(); RefreshInspector(); };
                Grid.SetColumn(clr, 1); sRow.Children.Add(clr);
            }
            box.Children.Add(sRow);
            if (hasSound)
            {
                // optional: route through a named AudioSource on the entity (its Volume / Pitch / 3D settings shape it)
                var via = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,56"), Margin = new Thickness(0, 4, 0, 0) };
                via.Children.Add(new TextBlock { Text = "via source", Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) });
                var viaBox = AnimUi.ToolTextBox(0, v => { string nv = string.IsNullOrWhiteSpace(v) ? null : v.Trim(); if (nv == evRef.AudioSource) return; evRef.AudioSource = nv; MarkDirty(); _timeline.Refresh(); });
                viaBox.Width = double.NaN; viaBox.Text = evRef.AudioSource ?? ""; viaBox.Watermark = "(plain 2D one-shot)";
                ToolTip.SetTip(viaBox, "Optional: the NAME of an AudioSource on this entity (or a child) to route through — its Volume / Pitch / 3D settings shape the sound. Empty = a plain 2D one-shot.");
                Grid.SetColumn(viaBox, 1); via.Children.Add(viaBox);
                var vl = new TextBlock { Text = "vol", Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
                Grid.SetColumn(vl, 2); via.Children.Add(vl);
                var vol = AnimUi.ToolTextBox(56, v => { if (PropertyRowsParse(v, out float f)) { f = Math.Max(0f, f); if (Math.Abs(f - evRef.Volume) > 1e-6f) { evRef.Volume = f; MarkDirty(); } } });
                vol.Classes.Add("number"); vol.Text = evRef.Volume.ToString("0.##", CultureInfo.InvariantCulture);
                ToolTip.SetTip(vol, "Extra volume multiplier on top of the source (1 = unchanged)");
                Grid.SetColumn(vol, 3); via.Children.Add(vol);
                box.Children.Add(via);
            }
            return box;
        }

        // ===================================================================== keyframe mutations (undoable)

        /// <summary>Write pos + rot + scale keys for <paramref name="bone"/> at <paramref name="time"/> — the edited pose when
        /// the inspector asks for it, else the pose sampled from the clip (timeline double-click). Replaces keys within half
        /// a frame. Undo removes the track again when this created it.</summary>
        public void KeyBoneAt(string bone, float time, bool useOverride)
        {
            if (string.IsNullOrEmpty(bone) || _clip == null) return;
            Vec3 pos, scale; Quat rot;
            if (useOverride && _hasOverride && bone == _selectedBone) { pos = _ovPos; rot = _ovRot; scale = _ovScale; }
            else SamplePoseAt(bone, time, out pos, out rot, out scale);

            var clip = _clip;
            var existing = clip.FindTrack(bone);
            var before = existing != null ? TimelineControl.CloneKeys(existing) : null;
            float tol = 0.5f / Math.Max(1f, clip.FrameRate);
            float t = time;
            Vec3 p = pos, sc = scale; Quat r = rot;
            // redo must re-add the SAME track instance (later commands capture track references)
            AnimTrack created = null;
            UndoRedoManager.Instance.Execute(new ActionCommand("Key " + AnimUtil.DisplayBoneName(bone),
                () =>
                {
                    var tr = clip.FindTrack(bone);
                    if (tr == null) { if (created == null) created = new AnimTrack { Bone = bone }; tr = created; clip.Tracks.Add(tr); }
                    UpsertVec3(tr.Pos, t, p, tol);
                    UpsertQuat(tr.Rot, t, r, tol);
                    UpsertVec3(tr.Scale, t, sc, tol);
                },
                () =>
                {
                    var tr = clip.FindTrack(bone);
                    if (tr == null) return;
                    if (before == null) clip.Tracks.Remove(tr); else TimelineControl.RestoreKeys(tr, before);
                }));
            _hasOverride = false;
            AfterClipMutation();
        }

        public void DeleteKeysAtTime(string bone, float time)
        {
            var track = _clip?.FindTrack(bone);
            if (track == null) return;
            float tol = 0.5f / Math.Max(1f, _clip.FrameRate);
            var before = TimelineControl.CloneKeys(track);
            int removed = track.Pos.RemoveAll(k => Math.Abs(k.T - time) < tol) + track.Rot.RemoveAll(k => Math.Abs(k.T - time) < tol) + track.Scale.RemoveAll(k => Math.Abs(k.T - time) < tol);
            if (removed == 0) return;
            var after = TimelineControl.CloneKeys(track);
            TimelineControl.RestoreKeys(track, before);   // the command's Execute performs the mutation
            UndoRedoManager.Instance.Execute(new ActionCommand("Delete Keys " + AnimUtil.DisplayBoneName(bone), () => TimelineControl.RestoreKeys(track, after), () => TimelineControl.RestoreKeys(track, before)));
            AfterClipMutation();
        }

        private void AfterClipMutation()
        {
            MarkDirty();
            _timeline.Refresh();
            RefreshBoneTree();
            UpdatePreview();
            if (!_playing) RefreshInspector();
        }

        private static void UpsertVec3(List<AnimKeyVec3> keys, float t, Vec3 v, float tol)
        {
            keys.RemoveAll(k => Math.Abs(k.T - t) < tol);
            keys.Add(new AnimKeyVec3 { T = t, X = v.X, Y = v.Y, Z = v.Z });
            keys.Sort((a, b) => a.T.CompareTo(b.T));
        }

        private static void UpsertQuat(List<AnimKeyQuat> keys, float t, Quat q, float tol)
        {
            keys.RemoveAll(k => Math.Abs(k.T - t) < tol);
            keys.Add(new AnimKeyQuat { T = t, X = q.X, Y = q.Y, Z = q.Z, W = q.W });
            keys.Sort((a, b) => a.T.CompareTo(b.T));
        }

        // ===================================================================== import / export

        private void ShowImportMenu()
        {
            string full = ResolveModelFullPath();
            bool has = full != null && File.Exists(full);
            var menu = new MenuFlyout();
            int count = 0;
            try { count = has ? VortexAPI.GetAnimationCount(full) : 0; } catch { }
            if (count <= 0) menu.Items.Add(new MenuItem { Header = has ? "No embedded clips in the model" : "Bind a model first", IsEnabled = false });
            for (int i = 0; i < count; i++)
            {
                if (!VortexAPI.GetAnimationInfo(full, i, out string name, out float dur)) continue;
                int idx = i; string nm = name; float d = dur;
                var mi = new MenuItem { Header = nm + "   (" + d.ToString("0.##", CultureInfo.InvariantCulture) + "s)" };
                mi.Click += async (s, e) => await ImportEmbedded(full, idx, nm, d);
                menu.Items.Add(mi);
            }
            menu.ShowAt(_importBtn);
        }

        public async System.Threading.Tasks.Task ImportEmbedded(string full, int index, string name, float durationSec, bool confirm = true)
        {
            if (confirm && _clip.Tracks.Count > 0 && !await AnimUi.Confirm(this, "Import from model", "Replace the current tracks with the embedded clip \"" + name + "\"?", "Replace", "Cancel"))
                return;
            var data = VortexAPI.GetAnimationData(full, index);
            var nodes = VortexAPI.GetSkeletonNodes(full);
            var imported = AnimationService.ClipFromModelData(name, durationSec, data, nodes);
            if (imported == null || imported.Tracks.Count == 0) { await AnimUi.Alert(this, "Import from model", "Could not read that embedded clip."); return; }
            var clip = _clip;
            var oldTracks = clip.Tracks; float oldDur = clip.DurationSec; string oldName = clip.Name;
            UndoRedoManager.Instance.Execute(new ActionCommand("Import clip " + name,
                () => { clip.Tracks = imported.Tracks; clip.DurationSec = imported.DurationSec; clip.Name = imported.Name; },
                () => { clip.Tracks = oldTracks; clip.DurationSec = oldDur; clip.Name = oldName; }));
            AfterWholeClipChange();
        }

        private async void ImportClipFromFile()
        {
            string picked = await AnimUtil.OpenFile(this, "Import animation clip", AnimUtil.ProjectDir("Assets", "Animations"), "Vortex Animation", "*.vanim");
            if (string.IsNullOrEmpty(picked)) return;
            var imported = VortexAnimClip.Load(picked);
            if (imported == null || imported.Tracks == null || imported.Tracks.Count == 0) { await AnimUi.Alert(this, "Import animation", "Could not read that .vanim (no animation tracks)."); return; }
            Normalize(imported);
            if (_clip.Tracks.Count > 0 && !await AnimUi.Confirm(this, "Import animation", "Replace the current tracks with \"" + (imported.Name ?? Path.GetFileNameWithoutExtension(picked)) + "\"?", "Replace", "Cancel"))
                return;
            var clip = _clip;
            var oldTracks = clip.Tracks; float oldDur = clip.DurationSec; string oldName = clip.Name;
            float oldFps = clip.FrameRate; bool oldLoop = clip.Loop; var oldEvents = clip.Events;
            UndoRedoManager.Instance.Execute(new ActionCommand("Import clip " + (imported.Name ?? ""),
                () => { clip.Tracks = imported.Tracks; clip.DurationSec = imported.DurationSec; clip.FrameRate = imported.FrameRate; clip.Loop = imported.Loop; clip.Name = imported.Name; clip.Events = imported.Events; },
                () => { clip.Tracks = oldTracks; clip.DurationSec = oldDur; clip.FrameRate = oldFps; clip.Loop = oldLoop; clip.Name = oldName; clip.Events = oldEvents; }));
            AfterWholeClipChange();
        }

        private void AfterWholeClipChange()
        {
            _time = 0f;
            RefreshToolbarFromClip();
            _timeline.SetClip(_clip);
            _timeline.Time = 0f;
            AfterClipMutation();
            UpdateTimeText();
            UpdateTitle();
        }

        private async void ExportClipToFile()
        {
            string suggested = (string.IsNullOrWhiteSpace(_clip?.Name) ? "clip" : _clip.Name) + ".vanim";
            string picked = await AnimUtil.SaveFile(this, "Export animation clip", AnimUtil.ProjectDir("Assets", "Animations"), suggested, "Vortex Animation", ".vanim");
            if (string.IsNullOrEmpty(picked)) return;
            if (!picked.EndsWith(".vanim", StringComparison.OrdinalIgnoreCase)) picked += ".vanim";
            if (_clip.Save(picked))
            {
                try { AnimationService.Instance.InvalidateClip(picked); } catch { }
                try { Editor.Core.Assets.AssetDatabase.Instance.Refresh(); } catch { }
                try { EditorCommands.Window?.AssetBrowser?.Refresh(); } catch { }
                await AnimUi.Alert(this, "Keyframe Editor", "Exported animation to:\n" + picked);
            }
            else await AnimUi.Alert(this, "Keyframe Editor", "Export failed — the file could not be written.");
        }

        // ===================================================================== toolbar sync

        private void RefreshToolbarFromClip()
        {
            _syncingUI = true;
            try
            {
                _nameBox.Text = _clip.Name ?? "";
                _durBox.Text = _clip.DurationSec.ToString("0.###", CultureInfo.InvariantCulture);
                _fpsBox.Text = _clip.FrameRate.ToString("0.#", CultureInfo.InvariantCulture);
                _loopBtn.IsChecked = _clip.Loop;
                _timeline.Duration = _clip.DurationSec;
                _timeline.SnapSeconds = _snapBtn.IsChecked == true ? 1f / Math.Max(1f, _clip.FrameRate) : 0f;
            }
            finally { _syncingUI = false; }
        }
    }
}
