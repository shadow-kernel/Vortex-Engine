using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.Services.Particles;
using Editor.Core.Services.Rendering;
using Editor.DllWrapper;
using VortexEditor.Controls;
using VortexEditor.Panels.Inspector;
using VortexEditor.Services;
using VortexEditor.Shell.Material;
using Path = System.IO.Path;
using Vector3 = System.Numerics.Vector3;

namespace VortexEditor.Shell
{
    /// <summary>
    /// VFX editor for .vfx effects: the emitter list on the left, a live particle preview in the middle (its own
    /// particle world, orbit / zoom, play / pause / restart, loop, speed, emit up or forward, a ground the sparks
    /// bounce on) and every field of the selected emitter or beam on the right — built from the .vfx data model, so
    /// each property the engine simulates is editable. Edits restart the preview; Save writes the .vfx and hot-reloads
    /// it in the scene (ParticleService) and in the Asset Browser thumbnails.
    /// </summary>
    public sealed class VfxEditorWindow : Window
    {
        // ================================================================= entry points
        /// <summary>Open (or bring to front) the editor for a .vfx.</summary>
        public static void Open(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            var existing = Find(fullPath);
            if (existing != null) { existing.Activate(); return; }
            EditorWindows.Show(new VfxEditorWindow(fullPath));
        }

        public static VfxEditorWindow Find(string fullPath)
            => EditorKit.OpenWindows<VfxEditorWindow>().FirstOrDefault(w => EditorKit.SamePath(w._path, fullPath));

        /// <summary>Create a new .vfx with one soft emitter in <paramref name="folder"/> and return its path.</summary>
        public static string CreateNew(string folder, string name = "New Effect")
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name + VfxAsset.Extension);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, name + " " + i + VfxAsset.Extension);
            var a = new VfxAsset { Name = Path.GetFileNameWithoutExtension(path) };
            var e = new VfxEmitter { Name = "Particles", Rate = 20f, Lifetime = new[] { 1f, 1.6f }, Speed = new[] { 0.5f, 1.2f }, Size = new[] { 0.08f, 0.14f } };
            e.Shape = new VfxShape { Type = VfxShapeType.Cone, Angle = 20f, Radius = 0.05f };
            e.ColorGradient = VfxGradient.Of(new VfxGradientKey(0f, 1f, 1f, 1f, 0f), new VfxGradientKey(0.15f, 1f, 1f, 1f, 1f), new VfxGradientKey(1f, 1f, 1f, 1f, 0f));
            e.Render = new VfxRender { Blend = VfxBlend.Additive, Emissive = 2f };
            a.Emitters.Add(e);
            a.Save(path);
            return path;
        }

        // ================================================================= state
        private readonly string _path;
        private VfxAsset _asset;
        private string _savedJson;
        private int _selected;                           // emitter index; -1 = the beam
        private readonly ParticleService.Preview _preview = new ParticleService.Preview();
        private readonly PreviewViewport _viewport = new PreviewViewport { Continuous = true };
        private readonly PreviewScene _scene = new PreviewScene { AllowEmpty = true, Bounds = new[] { 0f, 0.6f, 0f, 1.4f } };
        private PreviewModel _ground;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastTick;
        private bool _paused, _loop = true, _emitUp = true, _showGround = true;
        private float _speed = 1f;
        private readonly ListBox _list = new ListBox { SelectionMode = SelectionMode.Single };
        private readonly StackPanel _props = new StackPanel { Spacing = 2 };
        private readonly TextBlock _stats = new TextBlock { Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        private readonly TextBlock _title = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly List<Action> _refreshers = new List<Action>();
        private readonly DispatcherTimer _rebuild = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        private Button _playButton;
        private bool _closeConfirmed;

        public string Path_ => _path;
        public VfxAsset Asset => _asset;
        public bool IsDirty => _asset != null && _asset.ToJson() != _savedJson;
        public PreviewViewport Viewport => _viewport;
        public int PreviewAliveCount => _preview.AliveCount;

        private VfxEditorWindow(string path)
        {
            _path = path;
            _asset = VfxAsset.Load(path) ?? new VfxAsset { Name = Path.GetFileNameWithoutExtension(path) };
            _asset.Normalize();
            _savedJson = _asset.ToJson();
            Width = 1380; Height = 860; MinWidth = 960; MinHeight = 560;
            Background = EditorKit.Brush("VxPanelBrush") ?? Brushes.Black;
            _rebuild.Tick += (s, e) => { _rebuild.Stop(); ReloadPreview(); };

            _scene.BeforeTargetRender = () => _preview.BindForNextRender();
            _viewport.Scene = _scene;
            _viewport.BeforeRender = Step;
            _viewport.Camera = new PreviewCamera { Yaw = 0.6f, Pitch = 0.32f, DistScale = 1.1f, FovDeg = 40f };
            BuildGround();

            Content = BuildLayout();
            KeyDown += OnKeyDown;
            Closing += OnClosing;
            Closed += (s, e) => { _rebuild.Stop(); _preview.Dispose(); _ground?.Dispose(); EditorSession.Instance.ProjectClosed -= OnProjectClosed; };
            EditorSession.Instance.ProjectClosed += OnProjectClosed;

            RefreshList();
            Select(_asset.Emitters.Count > 0 ? 0 : (_asset.Beam != null ? -1 : 0));
            ApplyPose();
            ReloadPreview();
            UpdateTitle();
            EditorKit.FitToScreen(this);
        }

        // ================================================================= layout
        private Control BuildLayout()
        {
            var root = new DockPanel();

            // toolbar
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 8, 12, 8) };
            bar.Children.Add(new VxIcon { Icon = "Sparkle", Width = 18, Height = 18, Margin = new Thickness(0, 0, 4, 0) });
            bar.Children.Add(_title);
            bar.Children.Add(new Border { Width = 18 });
            bar.Children.Add(Tool("Save", () => Save(), "Save the effect (⌘S)", "Save"));
            bar.Children.Add(Tool("Revert", Revert, "Discard the changes since the last save", "Undo"));
            bar.Children.Add(new Border { Width = 12 });
            _playButton = Tool("Pause", TogglePause, "Pause / resume the preview (Space)", "Pause");
            bar.Children.Add(_playButton);
            bar.Children.Add(Tool("Restart", () => { _preview.Restart(); }, "Restart the effect (R)", "Refresh"));
            bar.Children.Add(Toggle("Loop", () => _loop, v => { _loop = v; ReloadPreview(); }, "Replay one-shot effects (bursts, impacts) continuously"));
            var speed = new Slider { Minimum = 0.05, Maximum = 2, Value = 1, Width = 110, VerticalAlignment = VerticalAlignment.Center };
            var speedText = new TextBlock { Text = "1.00×", Width = 44, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            speed.PropertyChanged += (s, e) => { if (e.Property == Slider.ValueProperty) { _speed = (float)speed.Value; speedText.Text = _speed.ToString("0.00", CultureInfo.InvariantCulture) + "×"; } };
            ToolTip.SetTip(speed, "Preview speed (slow motion to inspect fast effects)");
            bar.Children.Add(speed); bar.Children.Add(speedText);
            bar.Children.Add(new Border { Width = 12 });
            var dir = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center, ItemsSource = new[] { "Emit up (impact)", "Emit forward (muzzle)" }, SelectedIndex = 0 };
            dir.SelectionChanged += (s, e) => { _emitUp = dir.SelectedIndex == 0; ApplyPose(); };
            ToolTip.SetTip(dir, "Which way the effect's +Z (emission axis) points in the preview: up like a hit on the floor, or forward like a muzzle");
            bar.Children.Add(dir);
            bar.Children.Add(Toggle("Ground", () => _showGround, v => { _showGround = v; BuildGround(); }, "A floor under the effect (depth collision bounces on it)"));
            bar.Children.Add(new Border { Width = 12 });
            bar.Children.Add(Tool("Place in Scene", PlaceInScene, "Add an entity with this effect (Particle System) in front of the camera", "Plus"));
            bar.Children.Add(new Border { Width = 12 });
            bar.Children.Add(_stats);
            var barBorder = new Border { Child = bar, BorderBrush = EditorKit.Brush("VxSeparatorBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };
            DockPanel.SetDock(barBorder, Dock.Top);
            root.Children.Add(barBorder);

            // emitter list (left)
            var left = new DockPanel { Width = 230, Margin = new Thickness(10, 10, 6, 10) };
            var head = new TextBlock { Text = "EMITTERS", FontSize = 11, Opacity = 0.6, Margin = new Thickness(2, 0, 0, 6) };
            DockPanel.SetDock(head, Dock.Top); left.Children.Add(head);
            var btns = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            btns.Children.Add(EditorKit.SmallButton("+ Emitter", AddEmitter, "Add an emitter"));
            btns.Children.Add(EditorKit.SmallButton("Duplicate", DuplicateEmitter, "Copy the selected emitter"));
            btns.Children.Add(EditorKit.SmallButton("Delete", DeleteSelected, "Remove the selected emitter / beam"));
            btns.Children.Add(EditorKit.SmallButton("▲", () => MoveEmitter(-1), "Move up (draw order)"));
            btns.Children.Add(EditorKit.SmallButton("▼", () => MoveEmitter(1), "Move down"));
            btns.Children.Add(EditorKit.SmallButton("+ Beam", AddBeam, "Add a beam / tracer section (Vfx.Beam)"));
            DockPanel.SetDock(btns, Dock.Bottom); left.Children.Add(btns);
            _list.SelectionChanged += (s, e) => { if (_suppressList) return; int i = _list.SelectedIndex; if (i < 0) return; Select(i < _asset.Emitters.Count ? i : -1); };
            left.Children.Add(_list);
            DockPanel.SetDock(left, Dock.Left);
            root.Children.Add(left);

            // properties (right)
            var scroll = new ScrollViewer { Content = _props, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 12, 0) };
            var right = new Border { Width = 420, Margin = new Thickness(6, 10, 10, 10), Child = scroll };
            DockPanel.SetDock(right, Dock.Right);
            root.Children.Add(right);

            // preview (centre)
            var view = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(0, 10, 0, 10), Child = _viewport };
            root.Children.Add(view);
            return root;
        }

        private static Button Tool(string text, Action click, string tip, string icon)
        {
            var b = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { new VxIcon { Icon = icon, Width = 13, Height = 13 }, new TextBlock { Text = text } } }, Padding = new Thickness(9, 4) };
            ToolTip.SetTip(b, tip);
            b.Click += (s, e) => click();
            return b;
        }

        private static CheckBox Toggle(string text, Func<bool> get, Action<bool> set, string tip)
        {
            var c = new CheckBox { Content = text, IsChecked = get(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
            ToolTip.SetTip(c, tip);
            c.IsCheckedChanged += (s, e) => set(c.IsChecked == true);
            return c;
        }

        // ================================================================= preview
        private void BuildGround()
        {
            _scene.Items.Clear();
            _ground?.Dispose(); _ground = null;
            if (!_showGround) { _viewport.Invalidate(); return; }
            try
            {
                long mesh = PreviewModel.CreatePrimitive("Plane");
                long mat = VortexAPI.CreateNewMaterial();
                VortexAPI.SetMaterialBaseColor(mat, 0.16f, 0.16f, 0.17f, 1f);
                VortexAPI.SetMaterialRoughnessValue(mat, 0.92f);
                VortexAPI.SetMaterialMetallicValue(mat, 0f);
                _ground = PreviewModel.FromOwned(new[] { mesh }, new[] { mat });
                float s = 6f;
                foreach (var it in _ground.Scene.Items) { it.World = new[] { s, 0, 0, 0, 0, 1, 0, 0, 0, 0, s, 0, 0, 0, 0, 1 }; _scene.Items.Add(it); }
            }
            catch { }
            _viewport.Invalidate();
        }

        /// <summary>+Z of the effect up (impact on the floor) or forward along +X (muzzle, seen from the side).</summary>
        private void ApplyPose()
        {
            var world = _emitUp
                ? Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, 0.02f, 0f)
                : Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(-0.6f, 0.9f, 0f);
            _preview.BeamFrom = new Vector3(-3f, 0.9f, 0f);
            _preview.BeamTo = new Vector3(3f, 0.9f, 0f);
            _preview.SetTransform(world);
            _preview.Restart();
        }

        private void ReloadPreview()
        {
            if (!_preview.IsValid) { _stats.Text = "Particles are not available in this engine build"; return; }
            try { _preview.Load(_asset, _path, _loop); ApplyPose(); }
            catch (Exception ex) { ConsoleService.Instance.LogWarning("VFX editor: " + ex.Message); }
        }

        private void ScheduleReload() { _rebuild.Stop(); _rebuild.Start(); }

        private void Step()
        {
            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.1, Math.Max(0.0, now - _lastTick));
            _lastTick = now;
            if (!_paused) _preview.Step(dt * _speed);
            _stats.Text = _preview.AliveCount.ToString("N0", CultureInfo.InvariantCulture) + " particles · " + _preview.Time.ToString("0.00", CultureInfo.InvariantCulture) + " s";
        }

        private void TogglePause()
        {
            _paused = !_paused;
            _playButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { new VxIcon { Icon = _paused ? "Play" : "Pause", Width = 13, Height = 13 }, new TextBlock { Text = _paused ? "Play" : "Pause" } } };
        }

        // ================================================================= emitter list
        private bool _suppressList;

        private void RefreshList()
        {
            _suppressList = true;
            try
            {
                var items = _asset.Emitters.Select(e => (e.Enabled ? "" : "◌ ") + (string.IsNullOrWhiteSpace(e.Name) ? "Emitter" : e.Name)).ToList();
                if (_asset.Beam != null) items.Add("⟶ Beam");
                _list.ItemsSource = items;
                int idx = _selected >= 0 ? _selected : _asset.Emitters.Count;
                _list.SelectedIndex = idx < items.Count ? idx : (items.Count > 0 ? 0 : -1);
            }
            finally { _suppressList = false; }
        }

        private void Select(int index)
        {
            if (index >= _asset.Emitters.Count) index = _asset.Emitters.Count - 1;
            if (index < 0 && _asset.Beam == null) index = _asset.Emitters.Count > 0 ? 0 : -2;
            _selected = index;
            BuildProperties();
        }

        private void AddEmitter()
        {
            _asset.Emitters.Add(new VfxEmitter { Name = "Emitter " + (_asset.Emitters.Count + 1), Render = new VfxRender { Blend = VfxBlend.Additive } });
            _asset.Normalize();
            _selected = _asset.Emitters.Count - 1;
            Changed(structure: true);
        }

        private void DuplicateEmitter()
        {
            if (_selected < 0 || _selected >= _asset.Emitters.Count) return;
            var copy = VfxAsset.CloneEmitter(_asset.Emitters[_selected]);
            copy.Name = (copy.Name ?? "Emitter") + " copy";
            _asset.Emitters.Insert(_selected + 1, copy);
            _selected++;
            Changed(structure: true);
        }

        private void DeleteSelected()
        {
            if (_selected == -1) { _asset.Beam = null; _selected = _asset.Emitters.Count > 0 ? 0 : -2; Changed(structure: true); return; }
            if (_selected < 0 || _selected >= _asset.Emitters.Count) return;
            _asset.Emitters.RemoveAt(_selected);
            _selected = Math.Min(_selected, _asset.Emitters.Count - 1);
            if (_selected < 0 && _asset.Beam != null) _selected = -1;
            Changed(structure: true);
        }

        private void MoveEmitter(int delta)
        {
            int i = _selected, j = i + delta;
            if (i < 0 || j < 0 || i >= _asset.Emitters.Count || j >= _asset.Emitters.Count) return;
            var e = _asset.Emitters[i]; _asset.Emitters.RemoveAt(i); _asset.Emitters.Insert(j, e);
            _selected = j;
            Changed(structure: true);
        }

        private void AddBeam()
        {
            if (_asset.Beam == null) _asset.Beam = new VfxBeam { Width = 0.02f, Color = new[] { 1f, 0.82f, 0.5f, 1f }, Emissive = 6f, Speed = 300f, Length = 4f, Duration = 0f };
            _asset.Normalize();
            _selected = -1;
            Changed(structure: true);
        }

        // ================================================================= edits
        private void Changed(bool structure = false)
        {
            if (structure) { RefreshList(); BuildProperties(); ReloadPreview(); }
            else ScheduleReload();
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            string name = Path.GetFileName(_path);
            _title.Text = name + (IsDirty ? "  •" : "");
            Title = "VFX Editor — " + name + (IsDirty ? " (edited)" : "");
        }

        private bool Save()
        {
            try
            {
                if (!_asset.Save(_path)) { EditorCommands.Toast("Could not save " + Path.GetFileName(_path)); return false; }
                _savedJson = _asset.ToJson();
                ParticleService.NotifyAssetChanged(_path);
                ThumbnailService.Invalidate(_path);
                UpdateTitle();
                EditorCommands.Toast("Saved " + Path.GetFileName(_path));
                return true;
            }
            catch (Exception ex) { EditorCommands.Fail("Save effect", ex); return false; }
        }

        private void Revert()
        {
            _asset = VfxAsset.Load(_path) ?? _asset;
            _asset.Normalize();
            _savedJson = _asset.ToJson();
            _selected = Math.Min(Math.Max(_selected, -1), _asset.Emitters.Count - 1);
            Changed(structure: true);
        }

        private void PlaceInScene()
        {
            var e = VortexEditor.Services.AssetActions.AddToScene(_path);
            if (e != null) EditorCommands.Toast("Added " + e.Name + " to the scene");
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Source is TextBox) return;
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (cmd && e.Key == Key.S) { Save(); e.Handled = true; }
            else if (!cmd && e.Key == Key.Space) { TogglePause(); e.Handled = true; }
            else if (!cmd && e.Key == Key.R) { _preview.Restart(); e.Handled = true; }
        }

        private async void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (_closeConfirmed || !IsDirty) return;
            if (e.CloseReason == WindowCloseReason.OwnerWindowClosing || e.CloseReason == WindowCloseReason.ApplicationShutdown || e.CloseReason == WindowCloseReason.OSShutdown) return;
            e.Cancel = true;
            int r = await EditorKit.Choose(this, "Save changes to " + Path.GetFileName(_path) + "?", "Your changes are lost if you don't save them.", "Save", "Don't Save", "Cancel");
            if (r == 2) return;
            if (r == 0 && !Save()) return;
            _closeConfirmed = true;
            Close();
        }

        internal void CloseDiscarding() { _closeConfirmed = true; Close(); }
        private void OnProjectClosed() => Dispatcher.UIThread.Post(CloseDiscarding);

        // ================================================================= properties (built from the data model)
        private static readonly Dictionary<string, string> Sections = new Dictionary<string, string>
        {
            ["Name"] = "Main", ["Enabled"] = "Main", ["MaxParticles"] = "Main", ["Duration"] = "Main", ["Looping"] = "Main", ["Prewarm"] = "Main",
            ["StartDelay"] = "Main", ["SimulationSpace"] = "Main", ["SimulationSpeed"] = "Main", ["Seed"] = "Main",
            ["Rate"] = "Emission", ["RateOverDistance"] = "Emission", ["Bursts"] = "Emission",
            ["Lifetime"] = "Start", ["Speed"] = "Start", ["Size"] = "Start", ["Rotation"] = "Start", ["RotationSpeed"] = "Start", ["Color"] = "Start", ["Color2"] = "Start",
            ["Gravity"] = "Forces", ["Drag"] = "Forces", ["InheritVelocity"] = "Forces", ["Velocity"] = "Forces", ["VelocitySpace"] = "Forces",
            ["VelocityCurve"] = "Over lifetime", ["SpeedCurve"] = "Over lifetime", ["SizeCurve"] = "Over lifetime", ["ColorGradient"] = "Over lifetime",
        };

        /// <summary>Fields that may go negative (everything else is clamped at 0).</summary>
        private static readonly HashSet<string> Signed = new HashSet<string>(StringComparer.Ordinal)
        { "Gravity", "Velocity", "Offset", "Rotation", "RotationSpeed", "ScrollSpeed", "UvScroll", "InheritVelocity", "StartFrame", "V" };

        private static readonly string[] ImagePatterns = { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.dds" };

        private void BuildProperties()
        {
            _refreshers.Clear();
            _props.Children.Clear();
            using (PropertyRows.CaptureRefreshers(_refreshers))
            {
                if (_selected == -1 && _asset.Beam != null)
                {
                    _props.Children.Add(Header("Beam", "A line between two points — Vfx.Beam(from, to, \"effect.vfx\"). Speed > 0 makes it a travelling bullet tracer."));
                    BuildObject(_props, _asset.Beam, top: true);
                    return;
                }
                if (_selected < 0 || _selected >= _asset.Emitters.Count)
                {
                    _props.Children.Add(PropertyRows.Note("No emitter — add one with “+ Emitter”."));
                    return;
                }
                var em = _asset.Emitters[_selected];
                _props.Children.Add(Header(string.IsNullOrWhiteSpace(em.Name) ? "Emitter" : em.Name, "Emits along its local +Z (the cone axis). Spawn muzzle effects with the barrel's forward, impacts with the surface normal."));
                BuildObject(_props, em, top: true);
            }
        }

        private static Control Header(string title, string hint)
        {
            var s = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 6) };
            s.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
            s.Children.Add(new TextBlock { Text = hint, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            return s;
        }

        private void BuildObject(Panel host, object obj, bool top)
        {
            var groups = new Dictionary<string, StackPanel>();
            StackPanel Group(string name)
            {
                if (groups.TryGetValue(name, out var g)) return g;
                g = new StackPanel { Spacing = 2 };
                var exp = new Expander { Header = name, IsExpanded = name == "Main" || name == "Emission" || name == "Start" || name == "Render" || !top, Content = g, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 2) };
                host.Children.Add(exp);
                groups[name] = g;
                return g;
            }

            foreach (var pi in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length > 0) continue;
                if (pi.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;
                var t = pi.PropertyType;
                // nested blocks (Shape, Noise, Flipbook, Render, Collision, Trails) get their own section
                if (t.IsClass && t != typeof(string) && t.Namespace == typeof(VfxAsset).Namespace && t != typeof(VfxCurve) && t != typeof(VfxGradient))
                {
                    var sub = pi.GetValue(obj);
                    if (sub == null) { sub = Activator.CreateInstance(t); pi.SetValue(obj, sub); }
                    BuildObject(Group(PropertyRows.Pretty(pi.Name)), sub, top: false);
                    continue;
                }
                Panel target = top ? Group(Sections.TryGetValue(pi.Name, out var sec) ? sec : "Other") : host;
                var editor = EditorFor(obj, pi);
                if (editor == null) continue;
                bool tall = t == typeof(VfxCurve) || t == typeof(VfxGradient) || t == typeof(List<VfxBurst>);
                string label = PropertyRows.Pretty(pi.Name);
                target.Children.Add(tall ? PropertyRows.RowTop(label, editor) : PropertyRows.Row(label, editor));
            }
        }

        private Control EditorFor(object obj, PropertyInfo pi)
        {
            var t = pi.PropertyType;
            string n = pi.Name;
            float? min = Signed.Contains(n) ? (float?)null : 0f;
            if (t == typeof(float))
                return PropertyRows.FloatBox(() => (float)pi.GetValue(obj), v => { pi.SetValue(obj, v); Changed(); }, StepFor(n), min);
            if (t == typeof(int))
                return PropertyRows.IntBox(() => (int)pi.GetValue(obj), v => { pi.SetValue(obj, v); Changed(); }, 0);
            if (t == typeof(uint))
                return PropertyRows.IntBox(() => (int)(uint)pi.GetValue(obj), v => { pi.SetValue(obj, (uint)Math.Max(0, v)); Changed(); }, 0);
            if (t == typeof(bool))
                return PropertyRows.Bool(() => (bool)pi.GetValue(obj), v => { pi.SetValue(obj, v); Changed(n == "Enabled"); });
            if (t.IsEnum)
            {
                var names = Enum.GetNames(t);
                var values = Enum.GetValues(t);
                return PropertyRows.Choice(() => Array.IndexOf(names, pi.GetValue(obj).ToString()), i => { pi.SetValue(obj, values.GetValue(i)); Changed(); }, names.Select(PropertyRows.Pretty).ToArray());
            }
            if (t == typeof(string))
            {
                if (n.IndexOf("Texture", StringComparison.Ordinal) >= 0)
                    return PropertyRows.AssetPath(() => (string)pi.GetValue(obj), v => { pi.SetValue(obj, TexturePath(v)); Changed(); }, "Texture", ImagePatterns,
                        () => AssetPickerDialog.Pick("Texture", ImagePatterns, ResolveTexture((string)pi.GetValue(obj))));
                return PropertyRows.Text(() => (string)pi.GetValue(obj), v => { pi.SetValue(obj, v); Changed(n == "Name"); });
            }
            if (t == typeof(float[]))
            {
                var arr = (float[])pi.GetValue(obj) ?? new float[0];
                if (n.StartsWith("Color", StringComparison.Ordinal) && arr.Length >= 4) return ColorAlpha(arr);
                return FloatArray(arr, n);
            }
            if (t == typeof(VfxCurve)) return CurveEditor((VfxCurve)pi.GetValue(obj));
            if (t == typeof(VfxGradient)) return GradientEditor((VfxGradient)pi.GetValue(obj));
            if (t == typeof(List<VfxBurst>)) return BurstEditor((List<VfxBurst>)pi.GetValue(obj));
            return null;
        }

        private static double StepFor(string name)
        {
            switch (name)
            {
                case "Rate": case "Speed": case "Length": return 1;
                case "Angle": case "Arc": return 1;
                case "Emissive": case "Fps": return 0.25;
                default: return 0.01;
            }
        }

        /// <summary>min / max pairs ("random between"), xyz vectors, rgba without "Color" in the name.</summary>
        private Control FloatArray(float[] arr, string name)
        {
            string[] labels = arr.Length == 2 ? new[] { "min", "max" } : arr.Length == 3 ? new[] { "x", "y", "z" } : arr.Select((_, i) => i.ToString()).ToArray();
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", arr.Length))) };
            for (int i = 0; i < arr.Length; i++)
            {
                int k = i;
                var box = PropertyRows.FloatBox(() => arr[k], v => { arr[k] = v; Changed(); }, StepFor(name), Signed.Contains(name) ? (float?)null : 0f);
                var cell = new DockPanel { Margin = new Thickness(k == 0 ? 0 : 4, 0, 0, 0) };
                var l = new TextBlock { Text = labels[k], FontSize = 10, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) };
                DockPanel.SetDock(l, Dock.Left);
                cell.Children.Add(l); cell.Children.Add(box);
                Grid.SetColumn(cell, k);
                g.Children.Add(cell);
            }
            return g;
        }

        private Control ColorAlpha(float[] rgba)
        {
            var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            s.Children.Add(PropertyRows.Color(() => (rgba[0], rgba[1], rgba[2]), (r, g, b) => { rgba[0] = r; rgba[1] = g; rgba[2] = b; Changed(); }));
            s.Children.Add(new TextBlock { Text = "alpha", FontSize = 10, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center });
            var a = PropertyRows.FloatBox(() => rgba[3], v => { rgba[3] = Math.Max(0f, Math.Min(1f, v)); Changed(); }, 0.05, 0f, 1f);
            a.Width = 60;
            s.Children.Add(a);
            return s;
        }

        // ---------------------------------------------------------------- curve (0..1 keys, piecewise linear)
        private Control CurveEditor(VfxCurve curve)
        {
            if (curve == null) return null;
            var root = new StackPanel { Spacing = 3 };
            var graph = new CurveGraph(curve) { Height = 54, Margin = new Thickness(0, 2, 0, 2) };
            root.Children.Add(graph);
            var rows = new StackPanel { Spacing = 2 };
            root.Children.Add(rows);
            void Rebuild()
            {
                rows.Children.Clear();
                if (curve.Keys.Count == 0) rows.Children.Add(new TextBlock { Text = "Constant 1 — add keys to shape it", FontSize = 11, Opacity = 0.55 });
                for (int i = 0; i < curve.Keys.Count; i++)
                {
                    var key = curve.Keys[i];
                    var r = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    r.Children.Add(new TextBlock { Text = "t", FontSize = 10, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center });
                    var tb = PropertyRows.FloatBox(() => key.T, v => { key.T = Math.Max(0f, Math.Min(1f, v)); curve.Sort(); graph.InvalidateVisual(); Changed(); }, 0.05, 0f, 1f); tb.Width = 64;
                    r.Children.Add(tb);
                    r.Children.Add(new TextBlock { Text = "value", FontSize = 10, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center });
                    var vb = PropertyRows.FloatBox(() => key.V, v => { key.V = v; graph.InvalidateVisual(); Changed(); }, 0.05); vb.Width = 72;
                    r.Children.Add(vb);
                    r.Children.Add(EditorKit.IconButton("Close", "Remove key", () => { curve.Keys.Remove(key); Rebuild(); graph.InvalidateVisual(); Changed(); }));
                    rows.Children.Add(r);
                }
                var add = EditorKit.SmallButton("+ Key", () =>
                {
                    float t = curve.Keys.Count == 0 ? 0f : curve.Keys.Count == 1 ? 1f : 0.5f;
                    curve.Keys.Add(new VfxCurveKey(t, curve.IsEmpty ? 1f : curve.Evaluate(t)));
                    curve.Sort(); Rebuild(); graph.InvalidateVisual(); Changed();
                }, "Add a key (time 0..1 over the lifetime)");
                rows.Children.Add(add);
            }
            Rebuild();
            return root;
        }

        private sealed class CurveGraph : Control
        {
            private readonly VfxCurve _c;
            public CurveGraph(VfxCurve c) { _c = c; }
            public override void Render(DrawingContext ctx)
            {
                var b = Bounds;
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), new Rect(0, 0, b.Width, b.Height), 4);
                float lo = 0f, hi = 1f;
                foreach (var k in _c.Keys) { lo = Math.Min(lo, k.V); hi = Math.Max(hi, k.V); }
                if (hi - lo < 1e-4f) hi = lo + 1f;
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x5b, 0x9b, 0xff)), 1.6);
                Point P(float t, float v) => new Point(4 + t * (b.Width - 8), b.Height - 4 - (v - lo) / (hi - lo) * (b.Height - 8));
                Point prev = P(0f, _c.Evaluate(0f));
                for (int i = 1; i <= 40; i++) { float t = i / 40f; var p = P(t, _c.Evaluate(t)); ctx.DrawLine(pen, prev, p); prev = p; }
                var dot = new SolidColorBrush(Colors.White);
                foreach (var k in _c.Keys) ctx.DrawEllipse(dot, null, P(k.T, k.V), 2.6, 2.6);
            }
        }

        // ---------------------------------------------------------------- gradient (colour + alpha over the lifetime)
        private Control GradientEditor(VfxGradient gradient)
        {
            if (gradient == null) return null;
            var root = new StackPanel { Spacing = 3 };
            var bar = new Border { Height = 18, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 2, 0, 2) };
            void Paint()
            {
                if (gradient.Keys.Count == 0) { bar.Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)); return; }
                var lg = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative) };
                foreach (var k in gradient.Keys)
                    lg.GradientStops.Add(new GradientStop(Color.FromArgb(PropertyRows.B(k.A), PropertyRows.B(k.R), PropertyRows.B(k.G), PropertyRows.B(k.B)), k.T));
                bar.Background = lg;
            }
            root.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x30)), Child = bar });
            var rows = new StackPanel { Spacing = 2 };
            root.Children.Add(rows);
            void Rebuild()
            {
                rows.Children.Clear();
                if (gradient.Keys.Count == 0) rows.Children.Add(new TextBlock { Text = "White, opaque — add keys to fade / tint", FontSize = 11, Opacity = 0.55 });
                foreach (var key in gradient.Keys.ToList())
                {
                    var k = key;
                    var r = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    var tb = PropertyRows.FloatBox(() => k.T, v => { k.T = Math.Max(0f, Math.Min(1f, v)); gradient.Sort(); Paint(); Changed(); }, 0.05, 0f, 1f); tb.Width = 54;
                    r.Children.Add(tb);
                    r.Children.Add(PropertyRows.Color(() => (k.R, k.G, k.B), (rr, gg, bb) => { k.R = rr; k.G = gg; k.B = bb; Paint(); Changed(); }));
                    r.Children.Add(new TextBlock { Text = "α", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
                    var ab = PropertyRows.FloatBox(() => k.A, v => { k.A = Math.Max(0f, Math.Min(1f, v)); Paint(); Changed(); }, 0.05, 0f, 1f); ab.Width = 50;
                    r.Children.Add(ab);
                    r.Children.Add(EditorKit.IconButton("Close", "Remove key", () => { gradient.Keys.Remove(k); Rebuild(); Paint(); Changed(); }));
                    rows.Children.Add(r);
                }
                rows.Children.Add(EditorKit.SmallButton("+ Key", () =>
                {
                    float t = gradient.Keys.Count == 0 ? 0f : gradient.Keys.Count == 1 ? 1f : 0.5f;
                    gradient.Keys.Add(new VfxGradientKey(t, 1f, 1f, 1f, 1f));
                    gradient.Sort(); Rebuild(); Paint(); Changed();
                }, "Add a colour / alpha key (time 0..1 over the lifetime)"));
            }
            Rebuild(); Paint();
            return root;
        }

        // ---------------------------------------------------------------- bursts
        private Control BurstEditor(List<VfxBurst> bursts)
        {
            if (bursts == null) return null;
            var root = new StackPanel { Spacing = 4 };
            void Rebuild()
            {
                root.Children.Clear();
                foreach (var burst in bursts.ToList())
                {
                    var b = burst;
                    var card = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(6), Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)) };
                    var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
                    void Cell(int row, int col, string label, Control c)
                    {
                        var l = new TextBlock { Text = label, FontSize = 10, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(col == 0 ? 0 : 8, 2, 4, 2) };
                        Grid.SetRow(l, row); Grid.SetColumn(l, col); g.Children.Add(l);
                        Grid.SetRow(c, row); Grid.SetColumn(c, col + 1); c.Margin = new Thickness(0, 2); g.Children.Add(c);
                    }
                    Cell(0, 0, "time", PropertyRows.FloatBox(() => b.Time, v => { b.Time = v; Changed(); }, 0.01, 0f));
                    Cell(0, 2, "cycles", PropertyRows.IntBox(() => b.Cycles, v => { b.Cycles = v; Changed(); }, 0));
                    Cell(1, 0, "count", PropertyRows.IntBox(() => b.Count, v => { b.Count = v; if (b.CountMax < v) b.CountMax = v; Changed(); }, 0));
                    Cell(1, 2, "max", PropertyRows.IntBox(() => b.CountMax, v => { b.CountMax = Math.Max(v, b.Count); Changed(); }, 0));
                    Cell(2, 0, "interval", PropertyRows.FloatBox(() => b.Interval, v => { b.Interval = v; Changed(); }, 0.01, 0f));
                    Cell(2, 2, "chance", PropertyRows.FloatBox(() => b.Probability, v => { b.Probability = Math.Max(0f, Math.Min(1f, v)); Changed(); }, 0.05, 0f, 1f));
                    var rm = EditorKit.IconButton("Close", "Remove burst", () => { bursts.Remove(b); Rebuild(); Changed(); });
                    Grid.SetColumn(rm, 4); Grid.SetRow(rm, 0); g.Children.Add(rm);
                    card.Child = g;
                    root.Children.Add(card);
                }
                root.Children.Add(EditorKit.SmallButton("+ Burst", () => { bursts.Add(new VfxBurst { Time = 0f, Count = 10, CountMax = 10 }); Rebuild(); Changed(); }, "A burst emits Count..Max particles at a time (cycles × interval repeats)"));
            }
            Rebuild();
            return root;
        }

        // ---------------------------------------------------------------- textures
        private string TexturePath(string chosen)
        {
            if (string.IsNullOrEmpty(chosen)) return null;
            string full = EditorKit.ToAbsolute(chosen);
            return VfxAsset.MakeTexturePath(full, _path, ProjectData.Current?.Path) ?? chosen;
        }

        private string ResolveTexture(string stored) => VfxAsset.ResolveTexture(stored, _path, ProjectData.Current?.Path);

        // ================================================================= smoke
        [ModuleInitializer]
        internal static void RegisterSmoke()
        {
            SmokeRegistry.Add("vfx editor: live particle preview, edit re-simulates, save + hot reload", async () =>
            {
                string root = ProjectData.Current?.Path;
                if (root == null) return false;
                string dir = Path.Combine(root, "Assets", "VFX");
                var source = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.vfx").OrderBy(f => f).FirstOrDefault(f => f.Contains("Impact_Metal")) ?? Directory.GetFiles(dir, "*.vfx").FirstOrDefault() : null;
                string path = CreateNew(Path.Combine(root, "Assets", "VFX"), "SmokeTest");
                try
                {
                    if (source != null) File.Copy(source, path, true);
                    Open(path);
                    var w = Find(path);
                    if (w == null) return false;
                    await SmokeRegistry.Settle(1500);
                    int alive = w.PreviewAliveCount;
                    var img0 = w.Viewport.LastImage;
                    // edit: bigger, brighter first emitter -> the preview re-simulates
                    var em = w.Asset.Emitters[0];
                    em.Size = new[] { em.Size[0] * 3f, em.Size[1] * 3f };
                    w.Changed();
                    await SmokeRegistry.Settle(900);
                    bool dirty = w.IsDirty;
                    SmokeRegistry.Capture(w, "vfx_editor.png");
                    bool saved = w.Save();
                    var reloaded = VfxAsset.Load(path);
                    bool roundTrip = reloaded != null && Math.Abs(reloaded.Emitters[0].Size[1] - em.Size[1]) < 1e-4f;
                    ConsoleService.Instance.Log("  vfx editor smoke: " + Path.GetFileName(source ?? path) + " alive=" + alive + " → " + w.PreviewAliveCount + " image=" + (img0 != null) + " dirty=" + dirty + " saved=" + saved + " roundTrip=" + roundTrip);
                    w.CloseDiscarding();
                    return alive > 0 && img0 != null && dirty && saved && roundTrip;
                }
                finally { try { File.Delete(path); File.Delete(path + ".vmeta"); } catch { } }
            });
        }
    }
}
