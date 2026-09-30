using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Animation;
using VortexEditor.Controls;
using static VortexEditor.Panels.Inspector.PropertyRows;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Animation clip editor (.vanim): clip settings, bone tracks with their keys (position / rotation / scale),
    /// animation events (name, sound, time) and a scrub/play preview on the selected animated entity.
    /// </summary>
    public sealed class AnimationEditorWindow : Window
    {
        private readonly string _path;
        private readonly VortexAnimClip _clip;
        private readonly ListBox _tracks = new ListBox();
        private readonly StackPanel _keys = new StackPanel { Spacing = 2 };
        private readonly StackPanel _events = new StackPanel { Spacing = 2 };
        private readonly Slider _scrub = new Slider { Minimum = 0, Maximum = 1 };
        private readonly TextBlock _time = new TextBlock { Classes = { "mono", "secondary" }, VerticalAlignment = VerticalAlignment.Center, MinWidth = 90 };
        private readonly TextBlock _previewStatus = new TextBlock { Classes = { "small", "secondary" } };
        private readonly DispatcherTimer _timer;
        private bool _playing;
        private GameEntity _previewEntity;
        private AnimTrack _track;

        public AnimationEditorWindow(string path)
        {
            _path = path;
            _clip = VortexAnimClip.Load(path) ?? new VortexAnimClip { Name = Path.GetFileNameWithoutExtension(path) };
            Title = "Animation — " + _clip.Name; Width = 960; Height = 680; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            var root = new DockPanel();

            // ---- transport ----
            var transport = new DockPanel { Margin = new Thickness(12, 10, 12, 4) };
            var tl = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var play = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Play" } }; ToolTip.SetTip(play, "Play / pause preview on the selected entity");
            play.Click += (s, e) => TogglePlay();
            var stop = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Stop" } }; stop.Click += (s, e) => { _playing = false; _scrub.Value = 0; ApplyPose(); };
            tl.Children.Add(play); tl.Children.Add(stop); tl.Children.Add(_time);
            var tr = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            var save = new Button { Content = "Save", Classes = { "accent" }, MinWidth = 90 }; save.Click += (s, e) => Save();
            tr.Children.Add(_previewStatus); tr.Children.Add(save);
            DockPanel.SetDock(tl, Dock.Left); DockPanel.SetDock(tr, Dock.Right);
            transport.Children.Add(tl); transport.Children.Add(tr);
            _scrub.Margin = new Thickness(8, 0); _scrub.ValueChanged += (s, e) => { _time.Text = Fmt((float)_scrub.Value, "0.00") + " s"; if (!_playing) ApplyPose(); };
            transport.Children.Add(_scrub);
            DockPanel.SetDock(transport, Dock.Top); root.Children.Add(transport);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,5,*"), RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(12, 4, 12, 12) };
            // ---- clip settings ----
            var settings = new Border { Classes = { "card" }, Margin = new Thickness(0, 0, 0, 8) };
            var sst = new StackPanel { Spacing = 2 };
            sst.Children.Add(new TextBlock { Text = "Clip", Classes = { "section" }, Margin = new Thickness(0, 0, 0, 4) });
            var row1 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };
            row1.Children.Add(Row("Name", Text(() => _clip.Name, v => { _clip.Name = v; Title = "Animation — " + v; })));
            var c2 = Row("Duration (s)", FloatBox(() => _clip.DurationSec, v => { _clip.DurationSec = Math.Max(0.01f, v); _scrub.Maximum = _clip.DurationSec; }, 0.1, 0.01f)); Grid.SetColumn(c2, 1); row1.Children.Add(c2);
            var c3 = Row("Frame rate", FloatBox(() => _clip.FrameRate, v => _clip.FrameRate = Math.Max(1, v), 1, 1)); Grid.SetColumn(c3, 2); row1.Children.Add(c3);
            sst.Children.Add(row1);
            var row2 = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*"), ColumnSpacing = 12 };
            row2.Children.Add(Row("Model", AssetPath(() => _clip.Model, v => _clip.Model = v ?? "", "Model", new[] { "*.fbx", "*.gltf", "*.glb", "*.dae" }, () => AssetPickerDialog.Pick("Models", new[] { "*.fbx", "*.gltf", "*.glb", "*.dae" }))));
            var l2 = Row("Loop", Bool(() => _clip.Loop, v => _clip.Loop = v)); Grid.SetColumn(l2, 1); row2.Children.Add(l2);
            sst.Children.Add(row2);
            settings.Child = sst;
            Grid.SetColumnSpan(settings, 3); grid.Children.Add(settings);

            // ---- tracks ----
            var trackCard = new Border { Classes = { "card" }, Padding = new Thickness(6) };
            var tdock = new DockPanel();
            var th = new DockPanel { Margin = new Thickness(4, 0, 4, 4) };
            var addTrack = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Plus" } }; ToolTip.SetTip(addTrack, "Add bone track");
            addTrack.Click += async (s, e) => { var n = await Dialogs.Prompt("New track", "Bone name", "", "Add"); if (!string.IsNullOrWhiteSpace(n)) { _clip.GetOrAddTrack(n.Trim()); RebuildTracks(); } };
            var delTrack = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = "Minus" } }; delTrack.Click += (s, e) => { if (_track != null) { _clip.Tracks.Remove(_track); _track = null; RebuildTracks(); } };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 }; btns.Children.Add(addTrack); btns.Children.Add(delTrack);
            DockPanel.SetDock(btns, Dock.Right); th.Children.Add(btns);
            th.Children.Add(new TextBlock { Text = "Bone tracks", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(th, Dock.Top); tdock.Children.Add(th);
            _tracks.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<AnimTrack>((t, _) => new TextBlock { Text = t.Bone + "   (" + (t.Pos.Count + t.Rot.Count + t.Scale.Count) + " keys)" });
            _tracks.SelectionChanged += (s, e) => { _track = _tracks.SelectedItem as AnimTrack; RebuildKeys(); };
            tdock.Children.Add(_tracks);
            trackCard.Child = tdock;
            Grid.SetRow(trackCard, 1); grid.Children.Add(trackCard);
            var split = new GridSplitter { ResizeDirection = GridResizeDirection.Columns }; Grid.SetRow(split, 1); Grid.SetColumn(split, 1); grid.Children.Add(split);

            // ---- keys + events ----
            var right = new Border { Classes = { "card" }, Padding = new Thickness(10) };
            var rst = new StackPanel { Spacing = 6 };
            rst.Children.Add(new TextBlock { Text = "Keys of the selected track", FontWeight = FontWeight.SemiBold });
            rst.Children.Add(_keys);
            rst.Children.Add(new TextBlock { Text = "Events", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
            rst.Children.Add(_events);
            right.Child = new ScrollViewer { Content = rst };
            Grid.SetRow(right, 1); Grid.SetColumn(right, 2); grid.Children.Add(right);
            root.Children.Add(grid);
            Content = root;

            _scrub.Maximum = Math.Max(0.01f, _clip.DurationSec);
            _time.Text = "0.00 s";
            RebuildTracks(); RebuildEvents();
            ResolvePreviewEntity();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (s, e) => { if (_playing) { double t = _scrub.Value + 0.016; if (t > _scrub.Maximum) t = _clip.Loop ? 0 : _scrub.Maximum; _scrub.Value = t; ApplyPose(); } };
            _timer.Start();
            Closed += (s, e) => { _timer.Stop(); StopPreview(); };
        }

        private void RebuildTracks() { _tracks.ItemsSource = null; _tracks.ItemsSource = _clip.Tracks.ToList(); if (_track != null) _tracks.SelectedItem = _clip.Tracks.FirstOrDefault(t => ReferenceEquals(t, _track)); RebuildKeys(); }

        private void RebuildKeys()
        {
            _keys.Children.Clear();
            if (_track == null) { _keys.Children.Add(new TextBlock { Text = "Select a track.", Classes = { "secondary" } }); return; }
            void Section<TKey>(string title, List<TKey> keys, Func<TKey> make, Func<TKey, Control> editor)
            {
                var head = new DockPanel();
                var add = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = "Plus" } }; add.Click += (s, e) => { keys.Add(make()); keys.Sort((a, b) => KeyTime(a).CompareTo(KeyTime(b))); RebuildKeys(); };
                DockPanel.SetDock(add, Dock.Right); head.Children.Add(add);
                head.Children.Add(new TextBlock { Text = title + "  (" + keys.Count + ")", Classes = { "small", "secondary" }, VerticalAlignment = VerticalAlignment.Center });
                _keys.Children.Add(head);
                foreach (var k in keys)
                {
                    var kk = k;
                    var line = new DockPanel();
                    var del = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = "Minus" } }; del.Click += (s, e) => { keys.Remove(kk); RebuildKeys(); };
                    DockPanel.SetDock(del, Dock.Right); line.Children.Add(del);
                    line.Children.Add(editor(kk));
                    _keys.Children.Add(line);
                }
            }
            float T() => (float)_scrub.Value;
            Section("Position", _track.Pos, () => new AnimKeyVec3 { T = T() }, k => Vec3Key(() => k.T, v => k.T = v, () => k.X, v => k.X = v, () => k.Y, v => k.Y = v, () => k.Z, v => k.Z = v));
            Section("Rotation (quaternion)", _track.Rot, () => new AnimKeyQuat { T = T() }, k => QuatKey(k));
            Section("Scale", _track.Scale, () => new AnimKeyVec3 { T = T(), X = 1, Y = 1, Z = 1 }, k => Vec3Key(() => k.T, v => k.T = v, () => k.X, v => k.X = v, () => k.Y, v => k.Y = v, () => k.Z, v => k.Z = v));
        }

        private static float KeyTime(object k) => k is AnimKeyVec3 v ? v.T : k is AnimKeyQuat q ? q.T : 0f;

        private Control Vec3Key(Func<float> gt, Action<float> st, Func<float> gx, Action<float> sx, Func<float> gy, Action<float> sy, Func<float> gz, Action<float> sz)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            sp.Children.Add(new TextBlock { Text = "t", Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(FloatBox(gt, v => { st(Math.Max(0, v)); ApplyPose(); }, 0.05, 0));
            foreach (var (l, g, s) in new[] { ("X", gx, sx), ("Y", gy, sy), ("Z", gz, sz) })
            {
                sp.Children.Add(new TextBlock { Text = l, Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(FloatBox(g, v => { s(v); ApplyPose(); }, 0.05));
            }
            return sp;
        }

        private Control QuatKey(AnimKeyQuat k)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            sp.Children.Add(new TextBlock { Text = "t", Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(FloatBox(() => k.T, v => { k.T = Math.Max(0, v); ApplyPose(); }, 0.05, 0));
            // edit as Euler degrees, stored as quaternion
            var e = ToEuler(k);
            float[] eul = { e.X, e.Y, e.Z };
            for (int i = 0; i < 3; i++)
            {
                int ii = i;
                sp.Children.Add(new TextBlock { Text = new[] { "X°", "Y°", "Z°" }[i], Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(FloatBox(() => eul[ii], v => { eul[ii] = v; FromEuler(k, eul); ApplyPose(); }, 1, null, null, "0.#"));
            }
            return sp;
        }

        private static System.Numerics.Vector3 ToEuler(AnimKeyQuat k)
        {
            var q = new System.Numerics.Quaternion(k.X, k.Y, k.Z, k.W);
            float sinr = 2 * (q.W * q.X + q.Y * q.Z), cosr = 1 - 2 * (q.X * q.X + q.Y * q.Y);
            float roll = MathF.Atan2(sinr, cosr);
            float sinp = 2 * (q.W * q.Y - q.Z * q.X);
            float pitch = MathF.Abs(sinp) >= 1 ? MathF.CopySign(MathF.PI / 2, sinp) : MathF.Asin(sinp);
            float siny = 2 * (q.W * q.Z + q.X * q.Y), cosy = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
            float yaw = MathF.Atan2(siny, cosy);
            const float d = 180f / MathF.PI;
            return new System.Numerics.Vector3(roll * d, pitch * d, yaw * d);
        }
        private static void FromEuler(AnimKeyQuat k, float[] eul)
        {
            const float r = MathF.PI / 180f;
            var q = System.Numerics.Quaternion.CreateFromYawPitchRoll(eul[2] * r, eul[1] * r, eul[0] * r);
            k.X = q.X; k.Y = q.Y; k.Z = q.Z; k.W = q.W;
        }

        private void RebuildEvents()
        {
            _events.Children.Clear();
            foreach (var ev in _clip.Events)
            {
                var e2 = ev;
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                line.Children.Add(new TextBlock { Text = "t", Classes = { "small", "tertiary" }, VerticalAlignment = VerticalAlignment.Center });
                line.Children.Add(FloatBox(() => e2.T, v => e2.T = Math.Max(0, v), 0.05, 0));
                var name = Text(() => e2.Name, v => e2.Name = v, "event name"); name.MinWidth = 120; line.Children.Add(name);
                var snd = AssetPath(() => e2.Sound, v => e2.Sound = v, "Audio", new[] { "*.wav", "*.mp3", "*.ogg", "*.vsndc" }, () => AssetPickerDialog.Pick("Audio", new[] { "*.wav", "*.mp3", "*.ogg", "*.vsndc" })); snd.MinWidth = 220; line.Children.Add(snd);
                var del = new Button { Classes = { "icon", "small" }, Content = new VxIcon { Icon = "Minus" } }; del.Click += (s, e) => { _clip.Events.Remove(e2); RebuildEvents(); };
                line.Children.Add(del);
                _events.Children.Add(line);
            }
            var add = new Button { Content = "Add event at current time", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left };
            add.Click += (s, e) => { _clip.Events.Add(new AnimEvent { T = (float)_scrub.Value, Name = "Event" }); RebuildEvents(); };
            _events.Children.Add(add);
        }

        // ---- preview on the selected entity ----
        private void ResolvePreviewEntity()
        {
            var sel = SelectionService.Instance.SelectedEntity;
            _previewEntity = sel != null && sel.GetComponent<Animator>() != null ? sel : null;
            _previewStatus.Text = _previewEntity != null ? "Preview on " + _previewEntity.Name : "Select an entity with an Animator to preview";
        }

        private void TogglePlay()
        {
            ResolvePreviewEntity();
            if (_previewEntity == null) { EditorCommands.Toast("Select an animated entity in the scene to preview"); return; }
            _playing = !_playing;
            if (_playing)
            {
                try { AnimationService.Instance.Play(_previewEntity, _path); AnimationService.Instance.SetSpeed(_previewEntity, 0f); } catch (Exception ex) { EditorCommands.Fail("Preview", ex); _playing = false; }
            }
        }

        private void ApplyPose()
        {
            if (_previewEntity == null) return;
            try
            {
                // Evaluate the clip at the scrub time and feed the palette through the animation service by
                // (re)starting the clip and stepping to the requested time.
                if (!AnimationService.Instance.IsPlaying(_previewEntity, _path)) AnimationService.Instance.Play(_previewEntity, _path);
                AnimationService.Instance.SetSpeed(_previewEntity, 0f);
                var scene = ProjectData.Current?.ActiveScene;
                float target = (float)_scrub.Value, now = AnimationService.Instance.GetTime(_previewEntity);
                if (scene != null && target != now)
                {
                    AnimationService.Instance.SetSpeed(_previewEntity, 1f);
                    float dt = target - now; if (dt < 0) dt += Math.Max(0.01f, _clip.DurationSec);
                    AnimationService.Instance.Step(scene, dt);
                    AnimationService.Instance.SetSpeed(_previewEntity, 0f);
                }
                SceneRenderService.RuntimeDirty = true;
            }
            catch { }
        }

        private void StopPreview()
        {
            if (_previewEntity == null) return;
            try { AnimationService.Instance.Stop(_previewEntity); AnimationService.Instance.InvalidateClip(_path); SceneRenderService.RuntimeDirty = true; } catch { }
        }

        private void Save()
        {
            try { AnimationService.Instance.InvalidateClip(_path); _clip.Save(_path); EditorCommands.Toast("Animation saved"); }
            catch (Exception ex) { EditorCommands.Fail("Save animation", ex); }
        }
    }
}
