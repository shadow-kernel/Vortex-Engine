using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core.Animation;
using Editor.Core.UndoRedo;
using Editor.Core.UndoRedo.Commands;
using Editor.DllWrapper;
using Vec3 = System.Numerics.Vector3;
using Quat = System.Numerics.Quaternion;

namespace Editor.Editors.AnimationEditor
{
    /// <summary>
    /// The Keyframe Editor — authors .vanim clips against a bound model: skinned 3D preview with a bone
    /// overlay, per-bone pose inspector (position / Euler rotation / scale at the playhead), a dope-sheet
    /// timeline and animation-event markers. Built programmatically like the other editors; all keyframe
    /// mutations run through the global UndoRedoManager. Follows the shared preview-dialog lifecycle
    /// (ActivePreviewDialogs / RequestResubmit / DestroyPreviewTarget) exactly like CollisionEditorWindow.
    /// </summary>
    public sealed class AnimationEditorWindow : Window
    {
        private static AnimationEditorWindow _open;

        private string _path = "";
        private VortexAnimClip _clip = new VortexAnimClip();

        private AnimationPreviewControl _preview;
        private TimelineControl _timeline;
        private TreeView _boneTree;
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
        private bool _suppressTreeEvent;

        // working pose override for the SELECTED bone while the user types in the inspector; committed by
        // [Key Bone], discarded on bone switch. Euler kept separately so typing X never wobbles Y/Z through
        // the quaternion round-trip.
        private bool _hasOverride;
        private Vec3 _ovPos, _ovScale, _ovEuler;
        private Quat _ovRot;

        // override snapshot taken when a preview joint drag starts — ESC mid-drag restores it
        private string _dragSnapBone;
        private bool _dragSnapHasOverride;
        private Vec3 _dragSnapPos, _dragSnapScale, _dragSnapEuler;
        private Quat _dragSnapRot;

        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _lastSec;

        /// <summary>Open the (single) Keyframe Editor. Already open -> load the new clip into it + focus.</summary>
        public static void Open(Window owner, string vanimPath)
        {
            if (_open != null)
            {
                try
                {
                    // unsaved-edits guard: Cancel keeps the current document loaded
                    if (_open.ConfirmDiscardOrSave()) _open.LoadClip(vanimPath);
                    _open.Activate();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not open clip:\n" + ex.Message, "Keyframe Editor", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }

            var w = new AnimationEditorWindow { Owner = owner };
            _open = w;
            try
            {
                w.LoadClip(vanimPath);
                w.Show();
            }
            catch (Exception ex)
            {
                // the ctor already incremented ActivePreviewDialogs — Close() fires Closed, which
                // decrements it and clears _open, so a failed load can't wedge the main viewport
                try { w._dirty = false; w.Close(); } catch { }
                if (ReferenceEquals(_open, w)) _open = null;
                MessageBox.Show("Could not open clip:\n" + ex.Message, "Keyframe Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Unsaved-changes guard shared by Open() re-entry and window close.
        /// True = proceed (clean, saved, or explicitly discarded); false = abort.</summary>
        private bool ConfirmDiscardOrSave()
        {
            if (!_dirty) return true;
            var r = MessageBox.Show("Save changes to \"" + (_clip?.Name ?? "clip") + "\"?",
                "Keyframe Editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.Yes)
            {
                Save();
                return !_dirty; // still dirty = save failed -> abort
            }
            return true; // No = discard
        }

        private AnimationEditorWindow()
        {
            Title = "Keyframe Editor";
            Width = 1500; Height = 940; MinWidth = 1180; MinHeight = 700;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Br("#FF161618");
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

            BuildUI();

            // Borrow the shared render queue while open (the main viewport yields), like the other preview dialogs.
            Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActivePreviewDialogs++;

            CompositionTarget.Rendering += OnFrame;
            UndoRedoManager.Instance.CommandExecuted += OnUndoRedoExecuted;
            PreviewKeyDown += OnWindowKeyDown;

            Closing += (s, e) => { if (!ConfirmDiscardOrSave()) e.Cancel = true; };

            Closed += (s, e) =>
            {
                try { CompositionTarget.Rendering -= OnFrame; } catch { }
                try { UndoRedoManager.Instance.CommandExecuted -= OnUndoRedoExecuted; } catch { }
                try { _preview?.Dispose(); } catch { }
                try
                {
                    Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActivePreviewDialogs--;
                    Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.RequestResubmit();
                    // Only free the SHARED offscreen render target if no other preview dialog is still using it.
                    if (Editor.Editors.WorldEditor.Components.GamePreview.GamePreviewView.ActivePreviewDialogs <= 0)
                        Editor.Core.Services.Rendering.AssetPreviewRenderer.DestroyPreviewTarget();
                }
                catch { }
                if (ReferenceEquals(_open, this)) _open = null;
            };
        }

        // ===================== document =====================

        private void LoadClip(string vanimPath)
        {
            string abs = vanimPath ?? "";
            var root = Editor.Core.Data.ProjectData.Current?.Path;
            if (abs.Length > 0 && !Path.IsPathRooted(abs) && !string.IsNullOrEmpty(root)) abs = Path.Combine(root, abs);
            _path = abs;

            _clip = VortexAnimClip.Load(abs) ?? new VortexAnimClip
            {
                Name = Path.GetFileNameWithoutExtension(vanimPath ?? "New Clip")
            };
            // hand-edited JSON can deserialize lists as null — normalize once so the editor can assume them
            if (_clip.Tracks == null) _clip.Tracks = new List<AnimTrack>();
            if (_clip.Events == null) _clip.Events = new List<AnimEvent>();
            foreach (var tr in _clip.Tracks)
            {
                if (tr.Pos == null) tr.Pos = new List<AnimKeyVec3>();
                if (tr.Rot == null) tr.Rot = new List<AnimKeyQuat>();
                if (tr.Scale == null) tr.Scale = new List<AnimKeyVec3>();
            }

            _playing = false;
            _playBtn.Content = "▶";
            _time = 0f;
            _selectedBone = null;
            _hasOverride = false;
            _dirty = false;

            RefreshToolbarFromClip();
            RebindModel();
            _timeline.SetClip(_clip);
            _timeline.SnapSeconds = _snapBtn.IsChecked == true ? 1f / Math.Max(1f, _clip.FrameRate) : 0f;
            RefreshInspector();
            UpdatePreview();
            UpdateTimeText();
            UpdateTitle();
        }

        private void Save()
        {
            if (string.IsNullOrEmpty(_path)) return;
            if (_clip.Save(_path))
            {
                try { AnimationService.Instance.InvalidateClip(_path); } catch { }
                _dirty = false;
                UpdateTitle();
            }
            else
            {
                MessageBox.Show("Save failed — see debug output.", "Keyframe Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MarkDirty()
        {
            if (!_dirty) { _dirty = true; UpdateTitle(); }
        }

        private void UpdateTitle() => Title = "Keyframe Editor — " + (_clip?.Name ?? "?") + (_dirty ? " *" : "");

        // ===================== shell =====================

        private void BuildUI()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 240 });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // row splitter
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(300), MinHeight = 160 });  // timeline (drag to resize)

            root.Children.Add(BuildToolbar());

            var mid = new Grid { Margin = new Thickness(8, 8, 8, 0) };
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 220 });   // model + bones
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // splitter
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // preview
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // splitter
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330), MinWidth = 280 });   // key inspector
            Grid.SetRow(mid, 1);
            root.Children.Add(mid);

            mid.Children.Add(BuildLeftPanel());
            mid.Children.Add(ColumnSplitter(1));
            mid.Children.Add(ColumnSplitter(3));

            // CENTER: the deep preview well
            _preview = new AnimationPreviewControl();
            _preview.BoneClicked += bone =>
            {
                SelectBone(bone);        // may clear the override (bone switch)
                SnapshotDragState(bone); // ESC during the joint drag restores this state
            };
            _preview.BoneRotated += OnBoneRotated;
            var well = new Border
            {
                Background = Br("#FF0F0F12"),
                BorderBrush = Br("#FF3A3A3E"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = _preview
            };
            Grid.SetColumn(well, 2);
            mid.Children.Add(well);

            mid.Children.Add(BuildRightPanel());

            var rowSplit = new GridSplitter
            {
                Height = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                ResizeDirection = GridResizeDirection.Rows,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                Background = Brushes.Transparent,
                Margin = new Thickness(8, 0, 8, 0)
            };
            Grid.SetRow(rowSplit, 2);
            root.Children.Add(rowSplit);

            _timeline = new TimelineControl { Margin = new Thickness(8, 0, 8, 8) };
            _timeline.TimeChanged += t =>
            {
                _time = t;
                UpdateTimeText();
                UpdatePreview();
                if (!_playing) RefreshInspector();
            };
            _timeline.TrackSelected += bone => SelectBone(bone);
            _timeline.Changed += () =>
            {
                MarkDirty();
                UpdatePreview();
                if (!_playing) RefreshInspector();
            };
            _timeline.KeyAddRequested += (bone, t) => KeyBoneAt(bone, t, useOverride: false);
            Grid.SetRow(_timeline, 3);
            root.Children.Add(_timeline);

            Content = root;
        }

        private static GridSplitter ColumnSplitter(int column)
        {
            var gs = new GridSplitter
            {
                Width = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                Background = Brushes.Transparent
            };
            Grid.SetColumn(gs, column);
            return gs;
        }

        private UIElement BuildToolbar()
        {
            var bar = new Border
            {
                Background = Br("#FF1B1B1E"),
                BorderBrush = Br("#FF2C2C32"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 0, 10, 0)
            };
            var dock = new DockPanel { LastChildFill = false };

            var save = Dialogs.DialogStyles.CreateButton("Save", 92, isPrimary: true);
            save.VerticalAlignment = VerticalAlignment.Center;
            save.ToolTip = "Save clip (Ctrl+S)";
            save.Click += (s, e) => Save();
            DockPanel.SetDock(save, Dock.Right);
            dock.Children.Add(save);

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            dock.Children.Add(left);

            left.Children.Add(ToolLabel("CLIP"));
            _nameBox = ToolTextBox(140, v => { _clip.Name = v; MarkDirty(); UpdateTitle(); });
            left.Children.Add(_nameBox);

            left.Children.Add(ToolLabel("DUR"));
            _durBox = ToolTextBox(52, v =>
            {
                if (!TryParseF(v, out float f)) { RefreshToolbarFromClip(); return; } // restore valid text
                _clip.DurationSec = Math.Max(0.01f, f);
                _timeline.Duration = _clip.DurationSec;
                if (_time > _clip.DurationSec) SetTime(_clip.DurationSec);
                MarkDirty();
                UpdateTimeText();
            });
            left.Children.Add(_durBox);

            left.Children.Add(ToolLabel("FPS"));
            _fpsBox = ToolTextBox(44, v =>
            {
                if (!TryParseF(v, out float f)) { RefreshToolbarFromClip(); return; } // restore valid text
                _clip.FrameRate = Math.Max(1f, Math.Min(240f, f));
                if (_snapBtn.IsChecked == true) _timeline.SnapSeconds = 1f / _clip.FrameRate;
                MarkDirty();
            });
            left.Children.Add(_fpsBox);

            _snapBtn = ToolToggle("Snap", true, on => _timeline.SnapSeconds = on ? 1f / Math.Max(1f, _clip.FrameRate) : 0f);
            _snapBtn.ToolTip = "Snap timeline edits to the frame grid (1/FPS)";
            left.Children.Add(_snapBtn);

            left.Children.Add(ToolSeparator());

            var toStart = ToolButton("⏮", "Jump to start", () => SetTime(0f));
            var prev = ToolButton("◀", "Previous frame", () => SetTime(_time - 1f / Math.Max(1f, _clip.FrameRate)));
            _playBtn = ToolButton("▶", "Play / pause (Space)", TogglePlay);
            var next = ToolButton("⏭", "Next frame", () => SetTime(_time + 1f / Math.Max(1f, _clip.FrameRate)));
            _transport.AddRange(new[] { toStart, prev, _playBtn, next });
            left.Children.Add(toStart);
            left.Children.Add(prev);
            left.Children.Add(_playBtn);
            left.Children.Add(next);

            _loopBtn = ToolToggle("Loop", true, on => { if (!_syncingUI) { _clip.Loop = on; MarkDirty(); } });
            left.Children.Add(_loopBtn);

            _timeText = new TextBlock
            {
                Text = "0.00 / 1.00 s",
                Foreground = Br("#FF98989F"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0),
                MinWidth = 92
            };
            left.Children.Add(_timeText);

            left.Children.Add(ToolSeparator());

            _importBtn = ToolButton("Import from model ▾", "Copy an FBX-embedded clip into this document", ShowImportMenu);
            _importBtn.Padding = new Thickness(12, 5, 12, 5);
            left.Children.Add(_importBtn);

            bar.Child = dock;
            return bar;
        }

        private UIElement BuildLeftPanel()
        {
            var panel = new Border
            {
                Background = Br("#FF202023"),
                BorderBrush = Br("#FF3A3A3E"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10)
            };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var modelSec = new StackPanel();
            modelSec.Children.Add(MicroHeader("MODEL"));
            _modelPathText = new TextBlock
            {
                Text = "No model bound",
                Foreground = Br("#FF98989F"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            modelSec.Children.Add(_modelPathText);
            var bind = Dialogs.DialogStyles.CreateButton("Bind / Change…");
            bind.HorizontalAlignment = HorizontalAlignment.Left;
            bind.Click += (s, e) => BindModelDialog();
            modelSec.Children.Add(bind);
            modelSec.Children.Add(new Border { Height = 10 });
            modelSec.Children.Add(MicroHeader("BONES"));

            // filter box with a programmatic placeholder: non-empty filter flips the tree to a FLAT list
            // of matching bones (the fast way to find "LeftHand" in a 65-bone rig)
            var filterHost = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            _boneFilter = new TextBox
            {
                Background = Br("#FF202023"),
                Foreground = Br("#FFF0F0F3"),
                BorderBrush = Br("#FF34343C"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4),
                CaretBrush = Br("#FF6C5CE7"),
                FontSize = 11.5
            };
            var filterHint = new TextBlock
            {
                Text = "Filter bones…",
                Foreground = Br("#FF6E6E77"),
                FontSize = 11.5,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            _boneFilter.TextChanged += (s, e) =>
            {
                filterHint.Visibility = _boneFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                RefreshBoneTree();
            };
            filterHost.Children.Add(_boneFilter);
            filterHost.Children.Add(filterHint);
            modelSec.Children.Add(filterHost);
            grid.Children.Add(modelSec);

            _boneTree = new TreeView
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Br("#FFF5F5F7")
            };
            // items are FLAT (indent drawn via header margin, 11px/level) — disable horizontal scrolling
            // so long names ellipsis-trim to the panel width instead of growing a scrollbar
            ScrollViewer.SetHorizontalScrollBarVisibility(_boneTree, ScrollBarVisibility.Disabled);
            _boneTree.SelectedItemChanged += (s, e) =>
            {
                if (_suppressTreeEvent) return;
                if (_boneTree.SelectedItem is TreeViewItem tvi && tvi.Tag is string bone)
                    SelectBone(bone);
            };
            Grid.SetRow(_boneTree, 1);
            grid.Children.Add(_boneTree);

            panel.Child = grid;
            return panel;
        }

        private UIElement BuildRightPanel()
        {
            var panel = new Border
            {
                Background = Br("#FF202023"),
                BorderBrush = Br("#FF3A3A3E"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10)
            };
            Grid.SetColumn(panel, 4);
            _inspector = new StackPanel();
            panel.Child = new ScrollViewer
            {
                Content = _inspector,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0)
            };
            return panel;
        }

        // ===================== playback =====================

        private void OnFrame(object sender, EventArgs e)
        {
            if (!_playing) return;
            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)(now - _lastSec);
            _lastSec = now;
            if (dt <= 0f) return;

            _time += dt;
            float dur = Math.Max(_clip.DurationSec, 0.0001f);
            if (_time >= dur)
            {
                if (_clip.Loop) _time %= dur;
                else { _time = dur; SetPlaying(false); }
            }
            _timeline.Time = _time;
            UpdateTimeText();
            UpdatePreview();
        }

        private void TogglePlay() => SetPlaying(!_playing);

        private void SetPlaying(bool playing)
        {
            if (playing && _time >= _clip.DurationSec - 0.0001f) _time = 0f;
            _playing = playing;
            _playBtn.Content = playing ? "⏸" : "▶";
            _lastSec = _clock.Elapsed.TotalSeconds;
            if (!playing) RefreshInspector();
        }

        private void SetTime(float t)
        {
            _time = Math.Max(0f, Math.Min(_clip.DurationSec, t));
            _timeline.Time = _time;
            UpdateTimeText();
            UpdatePreview();
            if (!_playing) RefreshInspector();
        }

        private void UpdateTimeText()
            => _timeText.Text = _time.ToString("0.00") + " / " + _clip.DurationSec.ToString("0.00") + " s";

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool inTextBox = e.OriginalSource is TextBox;
            if (ctrl && e.Key == Key.S) { Save(); e.Handled = true; }
            else if (ctrl && e.Key == Key.Z && !inTextBox) { UndoRedoManager.Instance.Undo(); e.Handled = true; }
            else if (ctrl && e.Key == Key.Y && !inTextBox) { UndoRedoManager.Instance.Redo(); e.Handled = true; }
            else if (e.Key == Key.Space && !inTextBox && HasModel()) { TogglePlay(); e.Handled = true; }
            else if (e.Key == Key.F && !inTextBox)
            {
                // F = focus the selected bone; Shift+F (or F with nothing selected) = reset the view
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                if (shift || string.IsNullOrEmpty(_selectedBone)) _preview.ResetFocus();
                else _preview.FocusSelectedBone();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _preview.IsBoneDragActive)
            {
                CancelBoneDragAndRestore();
                e.Handled = true;
            }
        }

        private void OnUndoRedoExecuted(object sender, CommandExecutedEventArgs e)
        {
            // refresh after global Ctrl+Z/Ctrl+Y — our own Execute paths already refresh explicitly.
            // NO dirty-marking here: this fires for EVERY command in the editor (scene edits included),
            // and flagging the clip dirty on unrelated commands would fake unsaved changes.
            if (e.ExecutionType == CommandExecutionType.Execute) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshToolbarFromClip();
                _timeline.Refresh();
                RefreshBoneTree();
                UpdatePreview();
                if (!_playing) RefreshInspector();
                UpdateTitle(); // clip name may have been restored by an import undo
            }));
        }

        // ===================== model binding =====================

        private bool HasModel()
        {
            string full = ResolveModelFullPath();
            return full != null && File.Exists(full);
        }

        private string ResolveModelFullPath()
        {
            if (string.IsNullOrEmpty(_clip?.Model)) return null;
            try
            {
                string p = _clip.Model;
                if (!Path.IsPathRooted(p))
                {
                    var root = Editor.Core.Data.ProjectData.Current?.Path;
                    if (!string.IsNullOrEmpty(root)) p = Path.Combine(root, p);
                }
                return p;
            }
            catch { return null; } // illegal path chars in a hand-edited .vanim `model` field
        }

        private void BindModelDialog()
        {
            var root = Editor.Core.Data.ProjectData.Current?.Path;
            // STA-thread picker — a WPF file dialog on the live UI thread deadlocks against the DX12/DXGI COM apartment.
            string picked = Editor.Core.Util.FilePicker.OpenFile("3D Models|*.fbx;*.obj;*.gltf;*.glb;*.dae|All Files|*.*", "Bind model", root);
            if (string.IsNullOrEmpty(picked)) return;
            string rel = picked;
            if (!string.IsNullOrEmpty(root) && picked.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                rel = picked.Substring(root.Length).TrimStart('\\', '/');
            _clip.Model = rel;
            MarkDirty();
            RebindModel();
            UpdatePreview();
        }

        private void RebindModel()
        {
            string full = ResolveModelFullPath();
            bool has = full != null && File.Exists(full);
            _modelPathText.Text = string.IsNullOrEmpty(_clip.Model) ? "No model bound" : _clip.Model;

            if (has)
            {
                _preview.BindModel(full);
                if (_preview.Skeleton == null || !_preview.Skeleton.IsValid)
                    _preview.SetHint("Model has no skeleton");
            }
            else
            {
                _preview.BindModel(null);
                _preview.SetHint(string.IsNullOrEmpty(_clip.Model) ? "Bind a model to begin" : "Model not found:  " + _clip.Model);
            }

            foreach (var b in _transport) b.IsEnabled = has;
            if (!has) SetPlaying(false);
            RefreshBoneTree();
        }

        // ===================== bone tree =====================

        /// <summary>Display-only: hide importer pivot pseudo-nodes (Assimp FBX `$AssimpFbx$` chains — the
        /// importer now collapses them, but other formats can produce junk too). Selection/keys always use
        /// REAL node names; hidden nodes' children re-parent to the nearest visible ancestor.</summary>
        private static bool IsHiddenNode(string name)
            => name != null && name.IndexOf("$AssimpFbx$", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Display-only name shortening: strip `_$AssimpFbx$_Xxx` suffixes and namespace prefixes
        /// ("mixamorig:LeftHand" -> "LeftHand"). Full name lives in the ToolTip and in Tag/keys.</summary>
        internal static string DisplayBoneName(string bone)
        {
            string n = bone ?? "";
            int fx = n.IndexOf("_$AssimpFbx$", StringComparison.OrdinalIgnoreCase);
            if (fx > 0) n = n.Substring(0, fx);
            int c = n.LastIndexOf(':');
            if (c >= 0 && c + 1 < n.Length) n = n.Substring(c + 1);
            return n;
        }

        private void RefreshBoneTree()
        {
            _boneTree.Items.Clear();
            var skel = _preview.Skeleton;
            if (skel == null || skel.Nodes == null || skel.Nodes.Length == 0) return;

            int n = skel.Nodes.Length;
            string filter = _boneFilter != null ? _boneFilter.Text.Trim() : "";

            _suppressTreeEvent = true;
            try
            {
                if (filter.Length > 0)
                {
                    // FLAT filtered list (case-insensitive substring on the full name)
                    for (int i = 0; i < n; i++)
                    {
                        string name = skel.Nodes[i].Name;
                        if (IsHiddenNode(name)) continue;
                        if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        _boneTree.Items.Add(MakeBoneItem(name, 0));
                    }
                    return;
                }

                // hierarchy with hidden pivot nodes collapsed: each visible node's effective parent is its
                // nearest visible ancestor; items are added flat in DFS order, indented by depth
                var visKids = new List<int>[n];
                var roots = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    if (IsHiddenNode(skel.Nodes[i].Name)) continue;
                    int p = skel.Nodes[i].Parent;
                    int guard = 0;
                    while (p >= 0 && p < n && IsHiddenNode(skel.Nodes[p].Name) && guard++ < n)
                        p = skel.Nodes[p].Parent;
                    if (p >= 0 && p < n && p != i)
                    {
                        if (visKids[p] == null) visKids[p] = new List<int>();
                        visKids[p].Add(i);
                    }
                    else roots.Add(i);
                }
                foreach (int r in roots) AddBoneItemsRecursive(skel, visKids, r, 0);
            }
            finally { _suppressTreeEvent = false; }
        }

        private void AddBoneItemsRecursive(SkeletonDef skel, List<int>[] kids, int index, int depth)
        {
            _boneTree.Items.Add(MakeBoneItem(skel.Nodes[index].Name, depth));
            if (kids[index] != null)
                foreach (int c in kids[index]) AddBoneItemsRecursive(skel, kids, c, depth + 1);
        }

        private TreeViewItem MakeBoneItem(string fullName, int depth)
        {
            bool tracked = _clip?.FindTrack(fullName) != null;
            var tvi = new TreeViewItem
            {
                Header = new TextBlock
                {
                    Text = DisplayBoneName(fullName),
                    Foreground = tracked ? Br("#FF9C8FF0") : Br("#FFC8C8CE"),
                    FontWeight = tracked ? FontWeights.SemiBold : FontWeights.Normal,
                    FontSize = 12,
                    Margin = new Thickness(depth * 11, 0, 0, 0), // compact indent (default nesting is ~19px/level)
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = fullName
                },
                Tag = fullName,
                Padding = new Thickness(2, 1, 2, 1)
            };
            if (fullName == _selectedBone) tvi.IsSelected = true;
            return tvi;
        }

        private void SelectBoneInTree(string bone)
        {
            _suppressTreeEvent = true;
            try
            {
                foreach (var it in _boneTree.Items)
                {
                    if (it is TreeViewItem tvi && Equals(tvi.Tag, bone))
                    {
                        tvi.IsSelected = true;
                        tvi.BringIntoView();
                        break;
                    }
                }
            }
            finally { _suppressTreeEvent = false; }
        }

        // ===================== selection + pose override =====================

        private void SelectBone(string bone)
        {
            if (bone == _selectedBone) return;
            _selectedBone = bone;
            _hasOverride = false; // the typed pose belongs to the previous bone
            _preview.SetSelectedBone(bone);
            _timeline.SetSelectedBone(bone);
            SelectBoneInTree(bone);
            RefreshInspector();
            UpdatePreview();
        }

        private (Vec3 pos, Quat rot, Vec3 scale)? OverrideFor(string bone)
        {
            if (_hasOverride && bone == _selectedBone) return (_ovPos, _ovRot, _ovScale);
            return null;
        }

        // ---- mouse posing (preview joint drag) ----

        private void SnapshotDragState(string bone)
        {
            _dragSnapBone = bone;
            _dragSnapHasOverride = _hasOverride;
            _dragSnapPos = _ovPos; _dragSnapRot = _ovRot; _dragSnapScale = _ovScale; _dragSnapEuler = _ovEuler;
        }

        /// <summary>Preview joint drag: compose the LOCAL rotation delta onto the working override — the
        /// exact same "modified pose" state a typed edit produces, so [Key Bone] commits it unchanged.</summary>
        private void OnBoneRotated(string bone, Quat localDelta)
        {
            if (string.IsNullOrEmpty(bone)) return;
            if (bone != _selectedBone) SelectBone(bone); // safety — BoneClicked normally selected it already
            EnsureOverride();                            // seeds pos/rot/scale from the pose at the playhead
            _ovRot = Quat.Normalize(Quat.Concatenate(_ovRot, localDelta));
            _ovEuler = ToEulerDeg(_ovRot);
            UpdatePreview();
            if (!_playing) RefreshInspector();           // rotation fields track the drag live
        }

        /// <summary>ESC pressed while a preview joint drag is active: cancel it and restore the override
        /// exactly as it was when the drag started.</summary>
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
            if (!_playing) RefreshInspector();
        }

        private void UpdatePreview() => _preview.SetPose(_clip, _time, OverrideFor);

        private void EnsureOverride()
        {
            if (_hasOverride) return;
            SamplePoseAt(_selectedBone, _time, out _ovPos, out _ovRot, out _ovScale);
            _ovEuler = ToEulerDeg(_ovRot);
            _hasOverride = true;
        }

        /// <summary>Bone's local TRS at `time`: keyed values with per-component bind-pose fallback.</summary>
        private void SamplePoseAt(string bone, float time, out Vec3 pos, out Quat rot, out Vec3 scale)
        {
            pos = Vec3.Zero; rot = Quat.Identity; scale = Vec3.One;
            var skel = _preview.Skeleton;
            int node = skel != null ? skel.FindNode(bone) : -1;
            if (node >= 0)
            {
                var n = skel.Nodes[node];
                pos = n.BindTranslation; rot = n.BindRotation; scale = n.BindScale;
            }
            var track = _clip?.FindTrack(bone);
            if (track != null)
            {
                if (track.Pos != null && track.Pos.Count > 0) pos = AnimationService.SampleVec3(track.Pos, time);
                if (track.Rot != null && track.Rot.Count > 0) rot = AnimationService.SampleQuat(track.Rot, time);
                if (track.Scale != null && track.Scale.Count > 0) scale = AnimationService.SampleVec3(track.Scale, time);
            }
        }

        // ===================== inspector =====================

        private void RefreshInspector()
        {
            _inspector.Children.Clear();
            _inspector.Children.Add(MicroHeader("KEY INSPECTOR"));

            var skel = _preview.Skeleton;
            if (skel == null || !skel.IsValid)
            {
                _inspector.Children.Add(Note("Bind a model with a skeleton to pose bones."));
                BuildEventsSection();
                return;
            }
            if (string.IsNullOrEmpty(_selectedBone))
            {
                _inspector.Children.Add(Note("Select a bone — click it in the BONES tree or a joint in the preview. " +
                    "Drag a joint to rotate the bone, Shift+drag or middle-drag to pan, wheel to zoom, " +
                    "double-click a joint (or F) to focus it, Shift+F to reset the view."));
                BuildEventsSection();
                return;
            }

            _inspector.Children.Add(new TextBlock
            {
                Text = _selectedBone,
                Foreground = Br("#FFF5F5F7"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 10),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            Vec3 pos, scale, euler;
            if (_hasOverride) { pos = _ovPos; scale = _ovScale; euler = _ovEuler; }
            else
            {
                SamplePoseAt(_selectedBone, _time, out pos, out Quat rot, out scale);
                euler = ToEulerDeg(rot);
            }

            _inspector.Children.Add(MicroHeader("POSITION"));
            _inspector.Children.Add(Vec3Row(pos, v => { EnsureOverride(); _ovPos = v; UpdatePreview(); }));

            _inspector.Children.Add(MicroHeader("ROTATION  (EULER °)"));
            _inspector.Children.Add(Vec3Row(euler, v =>
            {
                EnsureOverride();
                _ovEuler = v;
                _ovRot = FromEulerDeg(v);
                UpdatePreview();
            }));

            _inspector.Children.Add(MicroHeader("SCALE"));
            _inspector.Children.Add(Vec3Row(scale, v => { EnsureOverride(); _ovScale = v; UpdatePreview(); }));

            var key = Dialogs.DialogStyles.CreateButton("Key Bone @ " + _time.ToString("0.00") + "s", isPrimary: true);
            key.HorizontalAlignment = HorizontalAlignment.Stretch;
            key.Margin = new Thickness(0, 6, 0, 0);
            key.Click += (s, e) => KeyBoneAt(_selectedBone, _time, useOverride: true);
            _inspector.Children.Add(key);

            var del = Dialogs.DialogStyles.CreateButton("Delete Keys @ Time");
            del.HorizontalAlignment = HorizontalAlignment.Stretch;
            del.Margin = new Thickness(0, 6, 0, 0);
            del.Click += (s, e) => DeleteKeysAtTime(_selectedBone, _time);
            _inspector.Children.Add(del);

            BuildEventsSection();
        }

        private void BuildEventsSection()
        {
            _inspector.Children.Add(new Border { Height = 14 });
            _inspector.Children.Add(MicroHeader("EVENTS"));

            if (_clip?.Events != null)
            {
                foreach (var ev in _clip.Events)
                {
                    var evRef = ev;
                    var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var timeBox = SmallBox(evRef.T.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), v =>
                    {
                        if (!TryParseF(v, out float f)) return;
                        evRef.T = Math.Max(0f, Math.Min(_clip.DurationSec, f));
                        _clip.Events.Sort((a, b) => a.T.CompareTo(b.T));
                        MarkDirty();
                        _timeline.Refresh();
                    });
                    row.Children.Add(timeBox);

                    var nameBox = SmallBox(evRef.Name, v => { evRef.Name = v; MarkDirty(); _timeline.Refresh(); });
                    nameBox.Margin = new Thickness(4, 0, 4, 0);
                    Grid.SetColumn(nameBox, 1);
                    row.Children.Add(nameBox);

                    var x = new Button
                    {
                        Content = "✕", Width = 24, Height = 24,
                        Background = Br("#FF26262B"), Foreground = Br("#FF98989F"),
                        BorderBrush = Br("#FF3A3A42"), BorderThickness = new Thickness(1),
                        Cursor = Cursors.Hand
                    };
                    x.Click += (s, e) =>
                    {
                        var clip = _clip;
                        UndoRedoManager.Instance.Execute(new ActionCommand("Delete Animation Event",
                            () => clip.Events.Remove(evRef),
                            () => { clip.Events.Add(evRef); clip.Events.Sort((a, b) => a.T.CompareTo(b.T)); }));
                        MarkDirty();
                        _timeline.Refresh();
                        RefreshInspector();
                    };
                    Grid.SetColumn(x, 2);
                    row.Children.Add(x);

                    _inspector.Children.Add(row);

                    // --- Sound slot: a clip played AUTOMATICALLY when the playhead crosses this frame ---------------
                    bool hasSound = !string.IsNullOrEmpty(evRef.Sound);
                    var sRow = new Grid { Margin = new Thickness(0, 0, 0, hasSound ? 2 : 10) };
                    sRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    sRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var sBtn = new Button
                    {
                        Content = hasSound ? ("♪  " + System.IO.Path.GetFileName(evRef.Sound)) : "＋ Add sound…",
                        Background = Br(hasSound ? "#FF2A2440" : "#FF202023"),
                        Foreground = Br(hasSound ? "#FFC9B8FF" : "#FF8A8A92"),
                        BorderBrush = Br("#FF34343C"), BorderThickness = new Thickness(1),
                        Padding = new Thickness(7, 3, 7, 3), Cursor = Cursors.Hand,
                        HorizontalContentAlignment = HorizontalAlignment.Left, FontSize = 11.5,
                        ToolTip = hasSound ? evRef.Sound : "Play a sound automatically when the playhead reaches this frame"
                    };
                    sBtn.Click += (s, e) =>
                    {
                        var picked = Editor.Core.Util.FilePicker.OpenFile(
                            "Audio|*.wav;*.mp3;*.ogg;*.flac|All files|*.*", "Pick a sound for this animation event", DefaultAudioDir());
                        if (string.IsNullOrEmpty(picked)) return;
                        evRef.Sound = MakeProjectRelative(picked);
                        MarkDirty(); _timeline.Refresh(); RefreshInspector();
                    };
                    sRow.Children.Add(sBtn);

                    if (hasSound)
                    {
                        var clr = new Button
                        {
                            Content = "✕", Width = 24, Height = 24, Margin = new Thickness(4, 0, 0, 0),
                            Background = Br("#FF26262B"), Foreground = Br("#FF98989F"),
                            BorderBrush = Br("#FF3A3A42"), BorderThickness = new Thickness(1),
                            Cursor = Cursors.Hand, ToolTip = "Remove sound"
                        };
                        clr.Click += (s, e) => { evRef.Sound = null; MarkDirty(); _timeline.Refresh(); RefreshInspector(); };
                        Grid.SetColumn(clr, 1);
                        sRow.Children.Add(clr);
                    }
                    _inspector.Children.Add(sRow);

                    if (hasSound)
                    {
                        // Optional: route the sound through a named AudioSource on the entity (its Volume/Pitch/3D shape it).
                        var viaRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                        viaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        viaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        viaRow.Children.Add(new TextBlock
                        {
                            Text = "via source", Foreground = Br("#FF6C6C74"), FontSize = 10.5,
                            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0)
                        });
                        var viaBox = SmallBox(evRef.AudioSource, v => { evRef.AudioSource = string.IsNullOrWhiteSpace(v) ? null : v.Trim(); MarkDirty(); });
                        viaBox.ToolTip = "Optional: the NAME of an AudioSource on this entity (or a child) to route through — its Volume / Pitch / 3D settings shape the sound. Empty = a plain 2D one-shot.";
                        Grid.SetColumn(viaBox, 1);
                        viaRow.Children.Add(viaBox);
                        _inspector.Children.Add(viaRow);
                    }
                }
            }

            var add = Dialogs.DialogStyles.CreateButton("+ Add Event @ Playhead");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(0, 4, 0, 0);
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

        // ===================== keyframe mutations (undoable) =====================

        /// <summary>Write pos+rot+scale keys for `bone` at `time` — the typed override when the inspector
        /// requested it, else the pose sampled from the clip (timeline double-click). Replaces keys within
        /// half a frame of `time`. Undoable; undo removes the track again if this created it.</summary>
        private void KeyBoneAt(string bone, float time, bool useOverride)
        {
            if (string.IsNullOrEmpty(bone) || _clip == null) return;
            Vec3 pos, scale; Quat rot;
            if (useOverride && _hasOverride && bone == _selectedBone) { pos = _ovPos; rot = _ovRot; scale = _ovScale; }
            else SamplePoseAt(bone, time, out pos, out rot, out scale);

            var clip = _clip;
            var existing = clip.FindTrack(bone);
            var before = existing != null ? CloneKeys(existing) : null;
            float tol = 0.5f / Math.Max(1f, clip.FrameRate);
            float t = time;
            Vec3 p = pos, sc = scale; Quat r = rot;

            // if this command creates the track, redo must re-Add the SAME AnimTrack instance (not
            // GetOrAddTrack a fresh one) — later commands capture track references, and a new instance
            // would leave them mutating a detached object across undo/redo cycles
            AnimTrack created = null;
            UndoRedoManager.Instance.Execute(new ActionCommand("Key " + bone,
                () =>
                {
                    var tr = clip.FindTrack(bone);
                    if (tr == null)
                    {
                        if (created == null) created = new AnimTrack { Bone = bone };
                        tr = created;
                        clip.Tracks.Add(tr);
                    }
                    UpsertVec3(tr.Pos, t, p, tol);
                    UpsertQuat(tr.Rot, t, r, tol);
                    UpsertVec3(tr.Scale, t, sc, tol);
                },
                () =>
                {
                    var tr = clip.FindTrack(bone);
                    if (tr == null) return;
                    if (before == null) clip.Tracks.Remove(tr);
                    else RestoreKeys(tr, before);
                }));

            _hasOverride = false;
            AfterClipMutation();
        }

        private void DeleteKeysAtTime(string bone, float time)
        {
            var track = _clip?.FindTrack(bone);
            if (track == null) return;
            float tol = 0.5f / Math.Max(1f, _clip.FrameRate);
            var before = CloneKeys(track);
            int removed = 0;
            if (track.Pos != null) removed += track.Pos.RemoveAll(k => Math.Abs(k.T - time) < tol);
            if (track.Rot != null) removed += track.Rot.RemoveAll(k => Math.Abs(k.T - time) < tol);
            if (track.Scale != null) removed += track.Scale.RemoveAll(k => Math.Abs(k.T - time) < tol);
            if (removed == 0) return;
            var after = CloneKeys(track);
            RestoreKeys(track, before); // the command's Execute performs the actual mutation
            UndoRedoManager.Instance.Execute(new ActionCommand("Delete Keys " + bone,
                () => RestoreKeys(track, after),
                () => RestoreKeys(track, before)));
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

        // ---- sorted-key helpers (key lists MUST stay sorted ascending by T) ----

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

        private class KeySnapshot
        {
            public List<AnimKeyVec3> Pos;
            public List<AnimKeyQuat> Rot;
            public List<AnimKeyVec3> Scale;
        }

        private static KeySnapshot CloneKeys(AnimTrack t)
        {
            var c = new KeySnapshot { Pos = new List<AnimKeyVec3>(), Rot = new List<AnimKeyQuat>(), Scale = new List<AnimKeyVec3>() };
            if (t.Pos != null) foreach (var k in t.Pos) c.Pos.Add(new AnimKeyVec3 { T = k.T, X = k.X, Y = k.Y, Z = k.Z });
            if (t.Rot != null) foreach (var k in t.Rot) c.Rot.Add(new AnimKeyQuat { T = k.T, X = k.X, Y = k.Y, Z = k.Z, W = k.W });
            if (t.Scale != null) foreach (var k in t.Scale) c.Scale.Add(new AnimKeyVec3 { T = k.T, X = k.X, Y = k.Y, Z = k.Z });
            return c;
        }

        private static void RestoreKeys(AnimTrack t, KeySnapshot snap)
        {
            t.Pos = new List<AnimKeyVec3>();
            t.Rot = new List<AnimKeyQuat>();
            t.Scale = new List<AnimKeyVec3>();
            foreach (var k in snap.Pos) t.Pos.Add(new AnimKeyVec3 { T = k.T, X = k.X, Y = k.Y, Z = k.Z });
            foreach (var k in snap.Rot) t.Rot.Add(new AnimKeyQuat { T = k.T, X = k.X, Y = k.Y, Z = k.Z, W = k.W });
            foreach (var k in snap.Scale) t.Scale.Add(new AnimKeyVec3 { T = k.T, X = k.X, Y = k.Y, Z = k.Z });
        }

        // ===================== import from model =====================

        private void ShowImportMenu()
        {
            string full = ResolveModelFullPath();
            bool has = full != null && File.Exists(full);
            var menu = new ContextMenu { PlacementTarget = _importBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

            int count = has ? VortexAPI.GetAnimationCount(full) : 0;
            if (count <= 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = has ? "No embedded clips in the model" : "Bind a model first",
                    IsEnabled = false
                });
            }
            for (int i = 0; i < count; i++)
            {
                if (!VortexAPI.GetAnimationInfo(full, i, out string name, out float dur)) continue;
                int idx = i; string nm = name; float d = dur;
                var mi = new MenuItem { Header = nm + "   (" + d.ToString("0.##") + "s)" };
                mi.Click += (s, e) => ImportEmbedded(full, idx, nm, d);
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        }

        private void ImportEmbedded(string full, int index, string name, float durationSec)
        {
            if (_clip.Tracks.Count > 0 &&
                MessageBox.Show("Replace the current tracks with the embedded clip \"" + name + "\"?",
                    "Import from model", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var data = VortexAPI.GetAnimationData(full, index);
            var nodes = VortexAPI.GetSkeletonNodes(full);
            var imported = AnimationService.ClipFromModelData(name, durationSec, data, nodes);
            if (imported == null || imported.Tracks.Count == 0)
            {
                MessageBox.Show("Could not read that embedded clip.", "Import from model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var clip = _clip;
            var oldTracks = clip.Tracks;
            float oldDur = clip.DurationSec;
            string oldName = clip.Name;
            UndoRedoManager.Instance.Execute(new ActionCommand("Import clip " + name,
                () => { clip.Tracks = imported.Tracks; clip.DurationSec = imported.DurationSec; clip.Name = imported.Name; },
                () => { clip.Tracks = oldTracks; clip.DurationSec = oldDur; clip.Name = oldName; }));

            _time = 0f;
            RefreshToolbarFromClip();
            _timeline.SetClip(clip);
            AfterClipMutation();
            UpdateTimeText();
            UpdateTitle();
        }

        // ===================== toolbar sync =====================

        private void RefreshToolbarFromClip()
        {
            _syncingUI = true;
            try
            {
                _nameBox.Text = _clip.Name ?? "";
                _durBox.Text = _clip.DurationSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                _fpsBox.Text = _clip.FrameRate.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                _loopBtn.IsChecked = _clip.Loop;
                _timeline.Duration = _clip.DurationSec;
            }
            finally { _syncingUI = false; }
        }

        // ===================== math =====================

        // Euler order matches Quaternion.CreateFromYawPitchRoll: yaw = Y, pitch = X, roll = Z.
        private static Vec3 ToEulerDeg(Quat q)
        {
            var m = System.Numerics.Matrix4x4.CreateFromQuaternion(Quat.Normalize(q));
            float sp = Math.Max(-1f, Math.Min(1f, -m.M32));
            float pitch = (float)Math.Asin(sp);
            float yaw, roll;
            if (Math.Abs(sp) > 0.9999f) // gimbal: roll folds into yaw
            {
                yaw = (float)Math.Atan2(-m.M13, m.M11);
                roll = 0f;
            }
            else
            {
                yaw = (float)Math.Atan2(m.M31, m.M33);
                roll = (float)Math.Atan2(m.M12, m.M22);
            }
            const float toDeg = 180f / (float)Math.PI;
            return new Vec3(pitch * toDeg, yaw * toDeg, roll * toDeg);
        }

        private static Quat FromEulerDeg(Vec3 e)
        {
            const float toRad = (float)Math.PI / 180f;
            return Quat.CreateFromYawPitchRoll(e.Y * toRad, e.X * toRad, e.Z * toRad);
        }

        // ===================== styled UI helpers =====================

        private static Brush Br(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);

        /// <summary>Start folder for the sound picker: the project's Assets/Audio if it exists, else the project root.</summary>
        private static string DefaultAudioDir()
        {
            var proj = Editor.Core.Data.ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(proj)) return null;
            var audio = System.IO.Path.Combine(proj, "Assets", "Audio");
            return System.IO.Directory.Exists(audio) ? audio : proj;
        }

        /// <summary>Convert an absolute picked path into a project-relative one (so clips resolve in the exported game).</summary>
        private static string MakeProjectRelative(string path)
        {
            var proj = Editor.Core.Data.ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(proj) || string.IsNullOrEmpty(path) || !System.IO.Path.IsPathRooted(path)) return path;
            try
            {
                var pu = new Uri(proj.EndsWith("\\") ? proj : proj + "\\");
                return Uri.UnescapeDataString(pu.MakeRelativeUri(new Uri(path)).ToString());
            }
            catch { return path; }
        }

        // net48 float.TryParse accepts "NaN"/"Infinity" — reject non-finite values or they corrupt the
        // clip (unsaveable JSON, runaway playhead, scrollbar-extent exceptions)
        private static bool TryParseF(string s, out float f)
            => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)
               && !float.IsNaN(f) && !float.IsInfinity(f);

        private static TextBlock MicroHeader(string t) => new TextBlock
        {
            Text = t,
            Foreground = Br("#FF6E6E77"),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 6)
        };

        private static UIElement Note(string t) => new Border
        {
            Background = Br("#FF1E1E22"),
            BorderBrush = Br("#FF2C2C32"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(11, 9, 11, 10),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock
            {
                Text = t,
                Foreground = Br("#FFA9A9B2"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16
            }
        };

        private static TextBlock ToolLabel(string t) => new TextBlock
        {
            Text = t,
            Foreground = Br("#FF6E6E77"),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 5, 0)
        };

        private static UIElement ToolSeparator() => new Border
        {
            Width = 1,
            Height = 22,
            Background = Br("#FF2C2C32"),
            Margin = new Thickness(10, 0, 4, 0)
        };

        private TextBox ToolTextBox(double width, Action<string> commit)
        {
            var tb = new TextBox
            {
                Width = width,
                Background = Br("#FF202023"),
                Foreground = Br("#FFF0F0F3"),
                BorderBrush = Br("#FF34343C"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4),
                CaretBrush = Br("#FF6C5CE7"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Action doCommit = () => { if (!_syncingUI) { try { commit(tb.Text); } catch { } } };
            tb.LostFocus += (s, e) => doCommit();
            tb.KeyDown += (s, e) => { if (e.Key == Key.Enter) doCommit(); };
            return tb;
        }

        private Button ToolButton(string glyph, string tooltip, Action onClick)
        {
            var b = new Button
            {
                Content = glyph,
                MinWidth = 34,
                Height = 28,
                Margin = new Thickness(3, 0, 3, 0),
                Background = Br("#FF26262B"),
                Foreground = Br("#FFE9E9ED"),
                BorderBrush = Br("#FF3A3A42"),
                BorderThickness = new Thickness(1),
                FontSize = 12.5,
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                VerticalAlignment = VerticalAlignment.Center
            };
            b.Click += (s, e) => { try { onClick(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[AnimationEditor] " + ex.Message); } };
            return b;
        }

        private ToggleButton ToolToggle(string text, bool initial, Action<bool> changed)
        {
            var t = new ToggleButton
            {
                Content = text,
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(6, 0, 0, 0),
                IsChecked = initial,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            Action restyle = () =>
            {
                bool on = t.IsChecked == true;
                t.Background = on ? Br("#FF6C5CE7") : Br("#FF212127");
                t.BorderBrush = on ? Br("#FF6C5CE7") : Br("#FF2C2C32");
                t.Foreground = on ? Brushes.White : Br("#FFC8C8CE");
            };
            restyle();
            t.Checked += (s, e) => { restyle(); changed(true); };
            t.Unchecked += (s, e) => { restyle(); changed(false); };
            return t;
        }

        private TextBox SmallBox(string text, Action<string> commit)
        {
            var tb = new TextBox
            {
                Text = text ?? "",
                Background = Br("#FF202023"),
                Foreground = Br("#FFF0F0F3"),
                BorderBrush = Br("#FF34343C"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 3, 5, 3),
                CaretBrush = Br("#FF6C5CE7"),
                FontSize = 11.5
            };
            Action doCommit = () => { try { commit(tb.Text); } catch { } };
            tb.LostFocus += (s, e) => doCommit();
            tb.KeyDown += (s, e) => { if (e.Key == Key.Enter) doCommit(); };
            return tb;
        }

        /// <summary>X/Y/Z numeric row with LIVE commit (TextChanged) — typing pushes the pose override
        /// straight into the preview, matching the CollisionEditorWindow idiom.</summary>
        private UIElement Vec3Row(Vec3 v, Action<Vec3> set)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
            float x = v.X, y = v.Y, z = v.Z;
            var fx = NumBox(x, nv => { x = nv; set(new Vec3(x, y, z)); }, "X", "#FFE06C6C");
            var fy = NumBox(y, nv => { y = nv; set(new Vec3(x, y, z)); }, "Y", "#FF7CE0A3");
            var fz = NumBox(z, nv => { z = nv; set(new Vec3(x, y, z)); }, "Z", "#FF6C9CE0");
            Grid.SetColumn(fx, 0); Grid.SetColumn(fy, 1); Grid.SetColumn(fz, 2);
            g.Children.Add(fx); g.Children.Add(fy); g.Children.Add(fz);
            return g;
        }

        private UIElement NumBox(float value, Action<float> set, string tag, string tagColor)
        {
            var wrap = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
            wrap.Children.Add(new TextBlock
            {
                Text = tag,
                Foreground = Br(tagColor),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Width = 12
            });
            var tb = new TextBox
            {
                Text = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                Background = Br("#FF202023"),
                Foreground = Br("#FFF0F0F3"),
                BorderBrush = Br("#FF34343C"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 5, 7, 5),
                MinWidth = 56,
                CaretBrush = Br("#FF6C5CE7")
            };
            tb.TextChanged += (s, e) => { if (TryParseF(tb.Text, out float nv)) set(nv); };
            wrap.Children.Add(tb);
            return wrap;
        }
    }
}
