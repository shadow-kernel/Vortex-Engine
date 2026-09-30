using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Animation;
using VortexEditor.Shell.Animation;
using static VortexEditor.Panels.Inspector.PropertyRows;
using Vec3 = System.Numerics.Vector3;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Socket / Attachment editor: a skinned CHARACTER posed by a clip (so you tune in a real pose), an ATTACHMENT
    /// (weapon / accessory), the socket BONE and the bone-local position / rotation / scale offset — with a LIVE 3D
    /// preview composed exactly like the game (placement here IS placement in-game). Bound to a scene entity it edits
    /// that entity's Bone Attachment component (Save writes bone + offset + render layer back and snaps the entity);
    /// opened on a model asset it saves a reusable ".vsocket" next to the attachment. Click a joint to pick the bone;
    /// with the 3D view focused, arrows / PgUp / PgDn move and I/K J/L U/O rotate (Shift ×10, Alt ×0.1).
    /// </summary>
    public sealed class SocketEditorWindow : Window
    {
        private static SocketEditorWindow _open;

        /// <summary>Open the editor bound to an entity's Bone Attachment (added on Save when missing).</summary>
        public static void Open(Editor.ECS.GameEntity entity)
        {
            if (entity == null) return;
            if (_open != null && ReferenceEquals(_open._entity, entity)) { _open.Activate(); return; }
            _open?.Close();
            EditorWindows.Show(new SocketEditorWindow(entity));
        }

        /// <summary>Open on a model / prefab asset (Asset Browser "Socket Editor…"): Save writes "&lt;attachment&gt;.vsocket".</summary>
        public static void OpenForModel(string attachmentFullPath, string characterFullPath = null)
        {
            _open?.Close();
            EditorWindows.Show(new SocketEditorWindow(null, attachmentFullPath, characterFullPath));
        }

        public static SocketEditorWindow Current => _open;

        private readonly GameEntity _entity;            // bound mode (null = .vsocket mode)
        private readonly SkinnedPreview _preview = new SkinnedPreview();
        private string _charPath, _attPath, _posePath;
        private VortexAnimClip _poseClip;
        private float _poseTime;
        private bool _posePlaying;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _lastSec;

        private string _bone;
        private Vec3 _pos, _rot;
        private float _scale = 1f;
        private int _layer = -1;

        private TextBlock _status, _charText, _attText;
        private ComboBox _poseBox;
        private Slider _poseSlider;
        private Button _posePlay;
        private readonly List<Action> _refreshers = new List<Action>();
        private readonly List<string> _poseFiles = new List<string>();
        private bool _syncing;

        public SkinnedPreview Preview => _preview;
        public string BoneName => _bone;
        public GameEntity BoundEntity => _entity;

        /// <summary>Bound mode: edit <paramref name="entity"/>'s Bone Attachment.</summary>
        public SocketEditorWindow(GameEntity entity) : this(entity, null, null) { }

        private SocketEditorWindow(GameEntity entity, string attachmentPath, string characterPath)
        {
            _entity = entity;
            Title = entity != null ? "Socket Editor — " + entity.Name : "Socket / Attachment Editor";
            Width = 1240; Height = 800; MinWidth = 940; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            _preview.NormalizeToHuman = true;
            _preview.AllowBoneRotate = false;
            _preview.ShowSocketAxes = true;
            _preview.HelpText = "Click a joint: socket bone · Drag: orbit · Right/Shift-drag: pan · Wheel: zoom · F: frame bone · Shift+F: reset view";
            _preview.BoneClicked += b => SetBone(b);
            _preview.BeforeFrame += OnFrame;
            _preview.ModelRebound += () => { Apply(); Refresh(); };
            BuildUI();
            AddHandler(KeyDownEvent, OnNudgeKey, RoutingStrategies.Tunnel);
            Opened += (s, e) => { _open = this; AnimUi.FitToScreen(this); _preview.Focus(); };
            Closed += (s, e) =>
            {
                try { _preview.Viewport.Continuous = false; _preview.Dispose(); } catch { }
                if (ReferenceEquals(_open, this)) _open = null;
                EditorCommands.Window?.Inspector?.Refresh();
            };

            string proj = ProjectData.Current?.Path ?? "";
            try
            {
                if (entity != null)
                {
                    var att = entity.GetComponent<BoneAttachment>();
                    // the socket's ACTUAL skeletal target, so the preview rig == the in-game rig
                    string chr = ResolveTargetCharacterMesh(entity) ?? DefaultPrefab(proj, "soldier.ventity");
                    string attSrc = att != null && !string.IsNullOrEmpty(att.SocketPrefabPath) ? AnimUtil.ToAbsolute(att.SocketPrefabPath)
                                  : AnimUtil.ModelFile(AnimUtil.FindModelMeshInSubtree(entity)) ?? DefaultPrefab(proj, "npc_gun.ventity");
                    LoadCharacter(chr, null);
                    LoadAttachment(attSrc);
                    if (att != null)
                    {
                        if (!string.IsNullOrEmpty(att.BoneName)) _bone = att.BoneName;
                        _pos = new Vec3(att.OffsetPosition.X, att.OffsetPosition.Y, att.OffsetPosition.Z);
                        _rot = new Vec3(att.OffsetRotation.X, att.OffsetRotation.Y, att.OffsetRotation.Z);
                        float s = att.OffsetScale.X; _scale = s <= 0.0001f ? 1f : s;
                        _layer = att.SocketRenderLayer;
                    }
                }
                else
                {
                    LoadCharacter(characterPath ?? DefaultPrefab(proj, "soldier.ventity"), null);
                    LoadAttachment(attachmentPath ?? DefaultPrefab(proj, "npc_gun.ventity"));
                }
            }
            catch (Exception ex) { SetStatus("Load error: " + ex.Message); }
            Apply();
            Refresh();
        }

        private static string DefaultPrefab(string proj, string name)
        {
            if (string.IsNullOrEmpty(proj)) return null;
            string p = Path.Combine(proj, "Assets", "Prefabs", name);
            return File.Exists(p) ? p : null;
        }

        /// <summary>The model of the character this entity's socket attaches to in the scene (null when unresolved).</summary>
        private static string ResolveTargetCharacterMesh(GameEntity e)
        {
            try
            {
                var scene = e?.Scene ?? ProjectData.Current?.ActiveScene;
                var target = BoneSocketService.Instance.ResolveTargetOf(scene, e);
                return target == null ? null : AnimUtil.ModelFile(AnimUtil.FindModelMeshInSubtree(target));
            }
            catch { return null; }
        }

        // ===================================================================== UI

        private void BuildUI()
        {
            var root = new Grid { ColumnDefinitions = new ColumnDefinitions("340,*") };
            var panel = new StackPanel { Margin = new Thickness(14, 12), Spacing = 2 };
            panel.Children.Add(new TextBlock { Text = _entity != null ? "Bone Attachment · " + _entity.Name : "Socket / Attachment", FontSize = 15, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(new TextBlock
            {
                Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
                Text = "Seat the attachment on a bone and position it precisely. Click a joint to pick the bone. Click the 3D view, then move with ← → · PgUp/PgDn · ↑ ↓ and rotate with I/K · J/L · U/O (Shift ×10, Alt ×0.1) — or type exact values. "
                     + (_entity != null ? "Save writes bone + offset into the Bone Attachment component." : "Save writes a .vsocket next to the attachment that WeaponMount reads — placement here IS placement in-game.")
            });

            panel.Children.Add(AnimUi.MicroHeader(_entity != null ? "CHARACTER (the socket's target)" : "ENTITY (attach ONTO this)", 8));
            _charText = new TextBlock { Classes = { "small", "secondary" }, TextTrimming = TextTrimming.CharacterEllipsis };
            panel.Children.Add(_charText);
            var charBtn = new Button { Content = "Choose character model / prefab…", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 0) };
            charBtn.Click += async (s, e) => { var p = await AssetPickerDialog.Pick("Characters", new[] { "*.ventity", "*.glb", "*.gltf", "*.fbx" }); if (!string.IsNullOrEmpty(p)) { LoadCharacter(AnimUtil.ToAbsolute(p), null); Apply(); Refresh(); } };
            panel.Children.Add(charBtn);

            panel.Children.Add(AnimUi.MicroHeader("ATTACHMENT", 10));
            _attText = new TextBlock { Classes = { "small", "secondary" }, TextTrimming = TextTrimming.CharacterEllipsis };
            panel.Children.Add(_attText);
            var attBtn = new Button { Content = "Choose attachment model / prefab…", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 0) };
            attBtn.Click += async (s, e) => { var p = await AssetPickerDialog.Pick("Attachments", new[] { "*.ventity", "*.glb", "*.gltf", "*.fbx" }); if (!string.IsNullOrEmpty(p)) { LoadAttachment(AnimUtil.ToAbsolute(p)); Apply(); Refresh(); } };
            panel.Children.Add(attBtn);

            panel.Children.Add(AnimUi.MicroHeader("POSE", 10));
            var poseRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            _poseBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 24 };
            ToolTip.SetTip(_poseBox, "Clip the character is posed with (from the model's animations folder)");
            _poseBox.SelectionChanged += (s, e) =>
            {
                if (_syncing) return;
                int i = _poseBox.SelectedIndex;
                SetPose(i <= 0 || i - 1 >= _poseFiles.Count ? null : _poseFiles[i - 1]);
            };
            poseRow.Children.Add(_poseBox);
            _posePlay = new Button { Classes = { "icon" }, Content = AnimUi.Icon("Play"), Margin = new Thickness(4, 0, 0, 0) };
            ToolTip.SetTip(_posePlay, "Play the pose clip (see the attachment follow the animation)");
            _posePlay.Click += (s, e) => SetPosePlaying(!_posePlaying);
            Grid.SetColumn(_posePlay, 1); poseRow.Children.Add(_posePlay);
            panel.Children.Add(poseRow);
            _poseSlider = new Slider { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 2, 0, 0) };
            ToolTip.SetTip(_poseSlider, "Pose time");
            _poseSlider.ValueChanged += (s, e) => { if (_syncing) return; _poseTime = (float)_poseSlider.Value; UpdatePose(); };
            panel.Children.Add(_poseSlider);

            panel.Children.Add(AnimUi.MicroHeader("SOCKET BONE", 8));
            Control bonePicker; TextBox scale;
            using (CaptureRefreshers(_refreshers))
            {
                bonePicker = SearchPicker(() => _bone, b => SetBone(b), () => BoneNames(), "Pick a bone", AnimUtil.DisplayBoneName, false, "Click a joint in the preview to pick it there");
                scale = FloatBox(() => _scale, v => { _scale = v <= 0.0001f ? 1f : v; Apply(); }, 0.01, 0.0001f);
            }
            panel.Children.Add(bonePicker);

            panel.Children.Add(AnimUi.MicroHeader("POSITION (m, bone space)", 12));
            panel.Children.Add(AnimUi.LiveVec3(() => _pos, v => { _pos = v; Apply(); }, 0.005, out var rp)); _refreshers.Add(rp);
            panel.Children.Add(AnimUi.MicroHeader("ROTATION (°, ZXY)"));
            panel.Children.Add(AnimUi.LiveVec3(() => _rot, v => { _rot = v; Apply(); }, 1, out var rr)); _refreshers.Add(rr);
            panel.Children.Add(AnimUi.MicroHeader("SCALE (uniform)"));
            scale.Width = 100; scale.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(scale);

            if (_entity != null)
            {
                panel.Children.Add(AnimUi.MicroHeader("RENDER LAYER (spawned socket prefab)", 12));
                var layer = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 24 };
                foreach (var l in new[] { "Keep prefab", "World", "First-Person (viewmodel)", "Third-Person only" }) layer.Items.Add(l);
                layer.SelectedIndex = Math.Max(0, Math.Min(3, _layer + 1));
                layer.SelectionChanged += (s, e) => { if (layer.SelectedIndex >= 0) _layer = layer.SelectedIndex - 1; };
                _refreshers.Add(() => layer.SelectedIndex = Math.Max(0, Math.Min(3, _layer + 1)));
                ToolTip.SetTip(layer, "Forced onto every mesh of the prefab spawned at this socket at play start (-1 keeps what the prefab authored). A weapon on a Third-Person-only body must be Third-Person only too.");
                panel.Children.Add(layer);
            }

            var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            var reset = new Button { Content = "Reset offsets", Margin = new Thickness(0, 0, 6, 6) };
            reset.Click += (s, e) => { _pos = Vec3.Zero; _rot = Vec3.Zero; _scale = 1f; Apply(); Refresh(); };
            var frame = new Button { Content = "Frame bone", Margin = new Thickness(0, 0, 6, 6) };
            ToolTip.SetTip(frame, "Focus the camera on the socket bone (F)");
            frame.Click += (s, e) => FocusBone();
            row.Children.Add(reset); row.Children.Add(frame);
            panel.Children.Add(row);

            var save = new Button { Content = _entity != null ? "Save to Bone Attachment" : "Save .vsocket", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 4) };
            save.Click += (s, e) => Save();
            panel.Children.Add(save);
            _status = new TextBlock { Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            panel.Children.Add(_status);

            var scroll = new ScrollViewer { Content = panel, Background = AnimUi.Res("VxSidebarBrush"), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            root.Children.Add(scroll);
            var host = new Border { Child = _preview };
            Grid.SetColumn(host, 1);
            root.Children.Add(host);
            Content = root;
        }

        private IEnumerable<string> BoneNames()
        {
            var sk = _preview.Skeleton;
            if (sk?.Nodes == null) return Enumerable.Empty<string>();
            return sk.Nodes.Where(n => !string.IsNullOrEmpty(n.Name) && !AnimUtil.IsHiddenNode(n.Name)).Select(n => n.Name);
        }

        private void Refresh()
        {
            _syncing = true;
            try
            {
                foreach (var r in _refreshers) { try { r(); } catch { } }
                _charText.Text = _charPath != null ? Path.GetFileName(_charPath) + "  (" + (_preview.Skeleton?.Nodes?.Length ?? 0) + " bones)" : "none";
                ToolTip.SetTip(_charText, _charPath);
                _attText.Text = _attPath != null ? Path.GetFileName(_attPath) : "none";
                ToolTip.SetTip(_attText, _attPath);
                _poseBox.Items.Clear();
                _poseBox.Items.Add("Bind pose");
                foreach (var f in _poseFiles) _poseBox.Items.Add(Path.GetFileNameWithoutExtension(f));
                int pi = _posePath != null ? _poseFiles.FindIndex(f => string.Equals(f, _posePath, StringComparison.OrdinalIgnoreCase)) : -1;
                _poseBox.SelectedIndex = pi + 1;
                _poseSlider.Maximum = Math.Max(0.01, _poseClip?.DurationSec ?? 1f);
                _poseSlider.Value = _poseTime;
                _poseSlider.IsEnabled = _poseClip != null;
                _posePlay.IsEnabled = _poseClip != null;
            }
            finally { _syncing = false; }
        }

        private void SetStatus(string s) { if (_status != null) _status.Text = s ?? ""; }

        // ===================================================================== data

        private void LoadCharacter(string path, string posePath)
        {
            path = SkinnedPreview.ResolvePrefabMesh(path);
            _charPath = string.IsNullOrEmpty(path) ? null : path;
            _preview.SetEmptyHint(_charPath == null ? "Choose the character (a rigged model or prefab)" : "Loading " + Path.GetFileName(_charPath) + "…");
            _preview.BindModel(_charPath);
            // poses live next to the character mesh in an "animations" folder (rifle_idle by default, like the game)
            _poseFiles.Clear();
            try
            {
                string dir = _charPath != null ? Path.Combine(Path.GetDirectoryName(_charPath) ?? "", "animations") : null;
                if (dir != null && Directory.Exists(dir)) _poseFiles.AddRange(Directory.GetFiles(dir, "*.vanim").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            catch { }
            string pose = !string.IsNullOrEmpty(posePath) && File.Exists(posePath) ? posePath
                        : _poseFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals("rifle_idle", StringComparison.OrdinalIgnoreCase));
            SetPose(pose, refresh: false);
            // default to a hand bone when the current one doesn't exist on this rig
            var sk = _preview.Skeleton;
            if (sk?.Nodes != null && sk.Nodes.Length > 0 && (string.IsNullOrEmpty(_bone) || sk.FindNode(_bone) < 0))
            {
                string hand = sk.Nodes.Select(n => n.Name).FirstOrDefault(n => n != null && n.EndsWith("RightHand", StringComparison.OrdinalIgnoreCase));
                _bone = hand ?? sk.Nodes.Select(n => n.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n) && !AnimUtil.IsHiddenNode(n));
            }
            SetStatus(_charPath != null ? "Character: " + Path.GetFileName(_charPath) + " (" + (sk?.Nodes?.Length ?? 0) + " bones)" : "No character model.");
        }

        private void LoadAttachment(string path)
        {
            _preview.BindAttachment(path);
            _attPath = _preview.AttachmentPath;
            // .vsocket mode: seed the offset from an existing .vsocket next to the attachment
            if (_entity == null && _attPath != null && File.Exists(_attPath + ".vsocket")) TryLoadSocket(_attPath + ".vsocket");
        }

        private void SetPose(string file, bool refresh = true)
        {
            _posePath = file;
            _poseClip = !string.IsNullOrEmpty(file) && File.Exists(file) ? VortexAnimClip.Load(file) : null;
            _poseTime = 0f;
            if (_poseClip == null) SetPosePlaying(false);
            UpdatePose();
            if (refresh) Refresh();
        }

        private void SetPosePlaying(bool on)
        {
            _posePlaying = on && _poseClip != null;
            _lastSec = _clock.Elapsed.TotalSeconds;
            _preview.Viewport.Continuous = _posePlaying;
            if (_posePlay != null) _posePlay.Content = AnimUi.Icon(_posePlaying ? "Pause" : "Play");
        }

        private void OnFrame()
        {
            if (!_posePlaying || _poseClip == null) return;
            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.25, now - _lastSec); _lastSec = now;
            float dur = Math.Max(0.0001f, _poseClip.DurationSec);
            _poseTime = (_poseTime + dt) % dur;
            _syncing = true; _poseSlider.Value = _poseTime; _syncing = false;
            UpdatePose();
        }

        private void UpdatePose() => _preview.SetPose(_poseClip, _poseTime, null);

        private void SetBone(string b)
        {
            if (string.IsNullOrEmpty(b)) return;
            _bone = b;
            _preview.SetSelectedBone(b);
            Apply();
            Refresh();
        }

        private void Apply()
        {
            _preview.SetSelectedBone(_bone);
            _preview.SetSocket(_bone, _pos, _rot, _scale);
        }

        private void FocusBone()
        {
            if (_preview.TryGetBonePosition(_bone, out var p)) _preview.FocusOn(p, 0.25f); else _preview.ResetFocus();
        }

        // In-game-style live nudging while the 3D view (not a text field / list / slider) has focus.
        private void OnNudgeKey(object sender, KeyEventArgs e)
        {
            var src = e.Source as Visual;
            if (src is TextBox || src?.FindAncestorOfType<TextBox>() != null) return;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (e.Key == Key.S) { Save(); e.Handled = true; }
                return;
            }
            bool arrow = e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down;
            bool listLike = src is ComboBox || src is ListBox || src is ListBoxItem || src is Slider || src?.FindAncestorOfType<Slider>() != null || src?.FindAncestorOfType<ListBox>() != null;
            if (arrow && listLike) return;   // arrows keep their meaning inside combos / lists / sliders
            float k = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10f : e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? 0.1f : 1f;
            float p = 0.005f * k, r = 1f * k;
            switch (e.Key)
            {
                case Key.Left: _pos.X -= p; break;
                case Key.Right: _pos.X += p; break;
                case Key.PageDown: _pos.Y -= p; break;
                case Key.PageUp: _pos.Y += p; break;
                case Key.Down: _pos.Z -= p; break;
                case Key.Up: _pos.Z += p; break;
                case Key.K: _rot.X -= r; break;
                case Key.I: _rot.X += r; break;
                case Key.J: _rot.Y -= r; break;
                case Key.L: _rot.Y += r; break;
                case Key.U: _rot.Z -= r; break;
                case Key.O: _rot.Z += r; break;
                case Key.F: if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _preview.ResetFocus(); else FocusBone(); e.Handled = true; return;
                default: return;
            }
            e.Handled = true;
            Apply();
            Refresh();
        }

        /// <summary>Nudge the offset like the arrow / IJKL keys (tests, scripted tuning).</summary>
        public void Nudge(Vec3 dPos, Vec3 dRotDeg) { _pos += dPos; _rot += dRotDeg; Apply(); Refresh(); }

        /// <summary>Bound: write bone + offset + layer into the Bone Attachment (undoable, added when missing) and snap the
        /// entity onto the bone. Asset mode: write "&lt;attachment&gt;.vsocket".</summary>
        public bool Save()
        {
            if (_entity != null)
            {
                var att = _entity.GetComponent<BoneAttachment>();
                if (att == null) { att = new BoneAttachment(_entity); _entity.AddComponent(att); }
                if (!string.IsNullOrEmpty(_bone)) att.BoneName = _bone;
                att.OffsetPosition = new Editor.ECS.Vector3(_pos.X, _pos.Y, _pos.Z);
                att.OffsetRotation = new Editor.ECS.Vector3(_rot.X, _rot.Y, _rot.Z);
                att.OffsetScale = new Editor.ECS.Vector3(_scale, _scale, _scale);
                att.SocketRenderLayer = _layer;
                bool snapped = false;
                try { snapped = BoneSocketService.Instance.ApplyOne(_entity.Scene ?? ProjectData.Current?.ActiveScene, _entity); } catch { }
                SceneRenderService.RuntimeDirty = true;
                EditorCommands.Window?.Inspector?.Refresh();
                SetStatus("Saved to the Bone Attachment of " + _entity.Name + (snapped ? " — snapped onto the bone." : string.IsNullOrEmpty(att.SocketPrefabPath) ? " (the bone does not resolve in the scene yet — check the target)." : " — the socket prefab spawns here at play."));
                return true;
            }
            if (string.IsNullOrEmpty(_attPath)) { SetStatus("No attachment loaded."); return false; }
            string json = "{\n" +
                "  \"bone\": \"" + (_bone ?? "").Replace("\"", "\\\"") + "\",\n" +
                "  \"posX\": " + G(_pos.X) + ", \"posY\": " + G(_pos.Y) + ", \"posZ\": " + G(_pos.Z) + ",\n" +
                "  \"rotX\": " + G(_rot.X) + ", \"rotY\": " + G(_rot.Y) + ", \"rotZ\": " + G(_rot.Z) + ",\n" +
                "  \"scale\": " + G(_scale) + "\n}\n";
            try { File.WriteAllText(_attPath + ".vsocket", json); SetStatus("Saved " + Path.GetFileName(_attPath) + ".vsocket"); return true; }
            catch (Exception ex) { SetStatus("Save failed: " + ex.Message); return false; }
        }

        private void TryLoadSocket(string path)
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
                {
                    var r = doc.RootElement;
                    float N(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? (float)v.GetDouble() : 0f;
                    _pos = new Vec3(N("posX"), N("posY"), N("posZ"));
                    _rot = new Vec3(N("rotX"), N("rotY"), N("rotZ"));
                    float s = N("scale"); _scale = s <= 0.0001f ? 1f : s;
                    if (r.TryGetProperty("bone", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(b.GetString())) _bone = b.GetString();
                }
                SetStatus("Loaded " + Path.GetFileName(path));
            }
            catch { }
        }

        private static string G(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
    }
}
