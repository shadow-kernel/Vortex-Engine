using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.AI;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.AI;
using Editor.ECS.Components.Physics;
using VortexEditor.Shell.Material;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Window ▸ Navigation (GitHub #109): bake the active scene's navmesh (Recast/Detour) — agent size (radius, height,
    /// step height, max slope), voxel resolution, region / polygon tuning and which geometry to read (colliders, static
    /// render meshes) — with progress and cancel on a worker thread, then save it as <c>&lt;Scene&gt;.vnav</c> next to the
    /// scene and load it. "Show navmesh" draws the walkable surface as a tinted net in the viewport (and, while an entity
    /// with a Patrol Path / AI Perception is selected, its route / vision cone). Stats of the loaded navmesh at the top.
    /// </summary>
    public sealed class NavigationWindow : Window
    {
        /// <summary>Open the window (one per editor — an open one is brought to front).</summary>
        public static void Open()
        {
            if (string.IsNullOrEmpty(ProjectData.Current?.Path)) { EditorCommands.Toast("Open a project first"); return; }
            var existing = EditorKit.OpenWindows<NavigationWindow>().FirstOrDefault();
            if (existing != null) { existing.Activate(); return; }
            EditorWindows.Show(new NavigationWindow());
        }

        private Scene _scene;
        private NavBakeSettings _settings;
        private readonly TextBlock _sceneLine = new TextBlock { Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _stats = new TextBlock { Classes = { "small" }, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _status = new TextBlock { Classes = { "small", "secondary" }, TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar _bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
        private readonly CheckBox _show = new CheckBox { Content = "Show navmesh in the viewport" };
        private readonly StackPanel _fields = new StackPanel { Spacing = 4 };
        private Button _bake, _cancel, _clear;
        private DispatcherTimer _progressTimer;
        private bool _baking;
        private readonly List<Action> _refreshers = new List<Action>();

        /// <summary>The last finished bake (smoke checks / tools).</summary>
        internal NavBakeResult LastResult { get; private set; }

        public NavigationWindow()
        {
            Title = "Navigation";
            Width = 560; Height = 760; MinWidth = 480; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            _scene = ProjectData.Current?.ActiveScene;
            _settings = NavigationService.SettingsFor(_scene);

            var root = new DockPanel();
            var header = new Border { Classes = { "hairline-bottom" }, Background = EditorKit.Brush("VxToolbarBrush"), Padding = new Thickness(16, 12) };
            var hs = new StackPanel { Spacing = 4 };
            hs.Children.Add(new TextBlock { Text = "Navigation", FontSize = 15, FontWeight = FontWeight.SemiBold });
            hs.Children.Add(_sceneLine);
            hs.Children.Add(_stats);
            header.Child = hs;
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

            var footer = new Border { Classes = { "hairline-top" }, Background = EditorKit.Brush("VxToolbarBrush"), Padding = new Thickness(16, 10) };
            var fs = new StackPanel { Spacing = 8 };
            _show.IsChecked = NavMeshOverlay.Enabled;
            _show.IsCheckedChanged += (s, e) => NavMeshOverlay.Enabled = _show.IsChecked == true;
            fs.Children.Add(_show);
            fs.Children.Add(_bar);
            fs.Children.Add(_status);
            var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            _clear = new Button { Content = "Clear", MinWidth = 80, Classes = { "ghost" } };
            ToolTip.SetTip(_clear, "Delete the scene's baked navmesh (.vnav)");
            _clear.Click += async (s, e) => await ClearNavMesh();
            _cancel = new Button { Content = "Cancel", MinWidth = 80, IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
            _cancel.Click += (s, e) => NavigationService.CancelBake();
            _bake = new Button { Content = "Bake", MinWidth = 100, Classes = { "accent" }, IsDefault = true };
            _bake.Click += async (s, e) => await BakeAsync();
            Grid.SetColumn(_cancel, 2); Grid.SetColumn(_bake, 3);
            buttons.Children.Add(_clear); buttons.Children.Add(_cancel); buttons.Children.Add(_bake);
            fs.Children.Add(buttons);
            footer.Child = fs;
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

            BuildFields();
            root.Children.Add(new ScrollViewer { Content = new Border { Padding = new Thickness(16, 12, 16, 16), Child = _fields } });
            Content = root;
            RefreshInfo();
            if (!NavigationService.Available) SetStatus("Navigation is unavailable in this engine build: " + NavigationService.UnavailableReason, true);
            Closed += (s, e) => { if (_baking) NavigationService.CancelBake(); _progressTimer?.Stop(); };
        }

        // ------------------------------------------------------------------------------------------ fields

        private void BuildFields()
        {
            _fields.Children.Clear();
            _refreshers.Clear();

            _fields.Children.Add(EditorKit.Section("Agent", 0));
            _fields.Children.Add(EditorKit.Hint("The size of the agents this navmesh is for: the walkable area keeps Radius away from walls, needs Height of headroom, climbs steps up to Step Height and slopes up to Max Slope."));
            _fields.Children.Add(Num("Radius (m)", () => _settings.AgentRadius, v => _settings.AgentRadius = v, 0f, 5f));
            _fields.Children.Add(Num("Height (m)", () => _settings.AgentHeight, v => _settings.AgentHeight = v, 0.1f, 20f));
            _fields.Children.Add(Num("Step height (m)", () => _settings.AgentMaxClimb, v => _settings.AgentMaxClimb = v, 0f, 5f));
            _fields.Children.Add(Num("Max slope (°)", () => _settings.AgentMaxSlope, v => _settings.AgentMaxSlope = v, 0f, 89f));

            _fields.Children.Add(EditorKit.Section("Resolution"));
            _fields.Children.Add(EditorKit.Hint("Voxel size of the bake. Smaller cells follow the geometry closer but bake slower (a cell size of Radius / 2 is a good start). Large levels are split into tiles of Tile Size cells."));
            _fields.Children.Add(Num("Cell size (m)", () => _settings.CellSize, v => _settings.CellSize = v, 0.02f, 5f));
            _fields.Children.Add(Num("Cell height (m)", () => _settings.CellHeight, v => _settings.CellHeight = v, 0.02f, 5f));
            _fields.Children.Add(Int("Tile size (cells)", () => _settings.TileSize, v => _settings.TileSize = v, 16, 1024));

            _fields.Children.Add(EditorKit.Section("Geometry"));
            _fields.Children.Add(EditorKit.Hint("Moving things (dynamic / kinematic Rigidbody, joints), characters (Animator, Nav Agent, tag Player) and everything tagged \"" + NavigationService.IgnoreTag + "\" are left out."));
            _fields.Children.Add(Flag("Colliders (box / sphere / capsule / mesh)", NavGeometrySource.Colliders));
            _fields.Children.Add(Flag("Render meshes of entities marked Static", NavGeometrySource.StaticRenderMeshes));
            _fields.Children.Add(Flag("Render meshes of every non-moving entity", NavGeometrySource.AllRenderMeshes));

            var adv = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
            adv.Children.Add(Num("Min region size", () => _settings.RegionMinSize, v => _settings.RegionMinSize = v, 0f, 1000f, "Islands smaller than this many cells (side) are removed"));
            adv.Children.Add(Num("Merge region size", () => _settings.RegionMergeSize, v => _settings.RegionMergeSize = v, 0f, 1000f, "Regions smaller than this are merged into their neighbours"));
            adv.Children.Add(Num("Max edge length (m)", () => _settings.EdgeMaxLen, v => _settings.EdgeMaxLen = v, 0f, 1000f, "0 = unlimited"));
            adv.Children.Add(Num("Max edge error (cells)", () => _settings.EdgeMaxError, v => _settings.EdgeMaxError = v, 0.1f, 10f, "How far the simplified outline may deviate from the voxels"));
            adv.Children.Add(Int("Verts per polygon", () => _settings.VertsPerPoly, v => _settings.VertsPerPoly = v, 3, 6));
            adv.Children.Add(Num("Detail sample dist", () => _settings.DetailSampleDist, v => _settings.DetailSampleDist = v, 0f, 100f, "Height detail sampling in cells (0 = none)"));
            adv.Children.Add(Num("Detail max error", () => _settings.DetailSampleMaxError, v => _settings.DetailSampleMaxError = v, 0f, 100f, "Height error in cells"));
            var part = new ComboBox { MinHeight = 22, MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
            part.Items.Add("Watershed (best)"); part.Items.Add("Monotone (fastest)"); part.Items.Add("Layers (stacked floors)");
            part.SelectedIndex = Math.Max(0, Math.Min(2, _settings.Partition));
            part.SelectionChanged += (s, e) => { if (part.SelectedIndex >= 0) _settings.Partition = part.SelectedIndex; };
            _refreshers.Add(() => part.SelectedIndex = Math.Max(0, Math.Min(2, _settings.Partition)));
            adv.Children.Add(Row("Partitioning", part));
            adv.Children.Add(Filter("Walk over low obstacles (curbs)", 1));
            adv.Children.Add(Filter("No navmesh over ledges", 2));
            adv.Children.Add(Filter("No navmesh under low ceilings", 4));
            var reset = new Button { Content = "Reset to defaults", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            reset.Click += (s, e) => { int flags = _settings.SourceFlags; _settings = NavigationService.DefaultSettings(); _settings.SourceFlags = flags; foreach (var r in _refreshers) r(); };
            adv.Children.Add(reset);
            _fields.Children.Add(new Expander { Header = "Advanced", Content = adv, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0) });
        }

        private Control Row(string label, Control editor, string tip = null)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Classes = { "label" } };
            if (tip != null) ToolTip.SetTip(l, tip);
            g.Children.Add(l);
            Grid.SetColumn(editor, 1);
            editor.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(editor);
            return g;
        }

        private Control Num(string label, Func<float> get, Action<float> set, float min, float max, string tip = null)
        {
            var box = new TextBox { MinHeight = 22, Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Text = Fmt(get()) };
            void Commit()
            {
                if (float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) set(Math.Max(min, Math.Min(max, v)));
                box.Text = Fmt(get());
            }
            box.LostFocus += (s, e) => Commit();
            box.KeyDown += (s, e) => { if (e.Key == Avalonia.Input.Key.Return) { Commit(); e.Handled = true; } };
            _refreshers.Add(() => box.Text = Fmt(get()));
            return Row(label, box, tip);
        }

        private Control Int(string label, Func<int> get, Action<int> set, int min, int max)
        {
            var box = new TextBox { MinHeight = 22, Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Text = get().ToString(CultureInfo.InvariantCulture) };
            void Commit()
            {
                if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) set(Math.Max(min, Math.Min(max, v)));
                box.Text = get().ToString(CultureInfo.InvariantCulture);
            }
            box.LostFocus += (s, e) => Commit();
            box.KeyDown += (s, e) => { if (e.Key == Avalonia.Input.Key.Return) { Commit(); e.Handled = true; } };
            _refreshers.Add(() => box.Text = get().ToString(CultureInfo.InvariantCulture));
            return Row(label, box);
        }

        private Control Flag(string label, NavGeometrySource flag)
        {
            var cb = new CheckBox { Content = label, IsChecked = (_settings.SourceFlags & (int)flag) != 0 };
            cb.IsCheckedChanged += (s, e) => { if (cb.IsChecked == true) _settings.SourceFlags |= (int)flag; else _settings.SourceFlags &= ~(int)flag; };
            _refreshers.Add(() => cb.IsChecked = (_settings.SourceFlags & (int)flag) != 0);
            return cb;
        }

        private Control Filter(string label, int bit)
        {
            var cb = new CheckBox { Content = label, IsChecked = (_settings.FilterFlags & bit) != 0 };
            cb.IsCheckedChanged += (s, e) => { if (cb.IsChecked == true) _settings.FilterFlags |= bit; else _settings.FilterFlags &= ~bit; };
            _refreshers.Add(() => cb.IsChecked = (_settings.FilterFlags & bit) != 0);
            return cb;
        }

        private static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------------------------------ info

        private void RefreshInfo()
        {
            _scene = ProjectData.Current?.ActiveScene;
            string file = NavigationService.NavMeshPathFor(_scene);
            string rel = file != null ? EditorKit.ToProjectRelative(file) : "?";
            _sceneLine.Text = _scene == null ? "No active scene." : "Scene “" + _scene.Name + "” — navmesh file " + rel;
            if (_scene != null && !ReferenceEquals(NavigationService.LoadedScene, _scene) && NavigationService.HasNavMeshFile(_scene) && !NavigationService.IsRunning)
                NavigationService.LoadForScene(_scene);
            if (ReferenceEquals(NavigationService.LoadedScene, _scene) && NavigationService.TryGetInfo(out var s, out var st))
            {
                _stats.Text = string.Format(CultureInfo.InvariantCulture, "Baked: {0:N0} polygons in {1} tiles ({2}×{3}), agent radius {4:0.##} m, height {5:0.##} m · {6:N0} KB",
                    st.PolyCount, st.TileCount, st.TilesX, st.TilesZ, s.AgentRadius, s.AgentHeight, Math.Max(1, st.DataSize / 1024));
                _stats.Foreground = EditorKit.Brush("VxTextBrush");
            }
            else
            {
                _stats.Text = NavigationService.HasNavMeshFile(_scene) ? "A navmesh file exists but could not be loaded — rebake it." : "Not baked yet — Nav Agents stand still until the scene has a navmesh.";
                _stats.Foreground = EditorKit.Brush("VxTextSecondaryBrush");
            }
            _clear.IsEnabled = NavigationService.HasNavMeshFile(_scene) && !_baking;
        }

        private void SetStatus(string text, bool error = false)
        {
            _status.Text = text ?? "";
            _status.Foreground = EditorKit.Brush(error ? "VxRedBrush" : "VxTextSecondaryBrush");
        }

        // ------------------------------------------------------------------------------------------ bake

        /// <summary>Gather the scene geometry, bake on a worker thread with progress, save + load the result.</summary>
        internal async Task<NavBakeResult> BakeAsync()
        {
            if (_baking) return null;
            _scene = ProjectData.Current?.ActiveScene;
            if (_scene == null) { SetStatus("Open a scene first.", true); return null; }
            if (!NavigationService.Available) { SetStatus(NavigationService.ErrorText(-6), true); return null; }
            FocusManager?.ClearFocus();   // commit a number box that still has focus
            _baking = true;
            _bake.IsEnabled = false; _cancel.IsEnabled = true; _clear.IsEnabled = false;
            _bar.IsVisible = true; _bar.Value = 0;
            SetStatus("Collecting geometry…");
            NavBakeResult result = null;
            try
            {
                await Task.Yield();
                var settings = _settings;
                var geo = NavigationService.CollectGeometry(_scene, settings.SourceFlags);
                SetStatus(string.Format(CultureInfo.InvariantCulture, "Baking {0:N0} triangles + {1:N0} solid boxes from {2} entities…", geo.TriangleCount, geo.BoxCount, geo.EntityCount));
                _progressTimer?.Stop();
                _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (s, e) => _bar.Value = NavigationService.BakeProgress);
                _progressTimer.Start();
                result = await Task.Run(() => NavigationService.Bake(geo, settings));
                _progressTimer.Stop();
                if (result.Success)
                {
                    _bar.Value = 1;
                    if (NavigationService.Save(_scene, result.Data, out var path)) result.Path = path;
                    NavigationService.LoadData(result.Data, _scene);
                    string notes = geo.Notes.Count > 0 ? "  (" + geo.Notes.Count + " note(s) in the Console)" : "";
                    foreach (var n in geo.Notes) ConsoleService.Instance.LogWarning("[Navigation] " + n);
                    SetStatus("Baked " + result.Message + (result.Path != null ? " → " + EditorKit.ToProjectRelative(result.Path) : " (NOT saved)") + notes, result.Path == null);
                    ConsoleService.Instance.Log("[Navigation] baked '" + _scene.Name + "': " + result.Message);
                    EditorCommands.Toast("NavMesh baked");
                }
                else SetStatus(result.Message, result.Error != -3);
            }
            catch (Exception ex) { SetStatus("Bake failed: " + ex.Message, true); }
            finally
            {
                _progressTimer?.Stop();
                _baking = false;
                _bake.IsEnabled = true; _cancel.IsEnabled = false;
                _bar.IsVisible = false;
                LastResult = result;
                RefreshInfo();
            }
            return result;
        }

        private async Task ClearNavMesh()
        {
            var path = NavigationService.NavMeshPathFor(_scene);
            if (path == null || !File.Exists(path)) return;
            if (await EditorKit.Choose(this, "Clear navmesh", "Delete " + Path.GetFileName(path) + "? Nav Agents in this scene stand still until you bake again.", "Delete", "Cancel") != 0) return;
            try { File.Delete(path); } catch (Exception ex) { SetStatus("Could not delete: " + ex.Message, true); return; }
            if (ReferenceEquals(NavigationService.LoadedScene, _scene)) NavigationService.Unload();
            SetStatus("Navmesh deleted.");
            RefreshInfo();
        }

        // ------------------------------------------------------------------------------------------ smoke

        [ModuleInitializer]
        internal static void RegisterSmoke()
        {
            SmokeRegistry.Add("navigation: bake an in-memory test level + path around a wall", BakeTestLevel);
            SmokeRegistry.Add("navigation: bake the active scene + a path between two reachable points", BakeActiveScene);
            SmokeRegistry.Add("navigation window: opens, bakes, captures", WindowSmoke);
        }

        /// <summary>A floor (box collider) with a wall (box collider): the path from one side to the other goes around it.</summary>
        private static bool BakeTestLevel()
        {
            if (!NavigationService.Available) { ConsoleService.Instance.LogWarning("[smoke] navigation unavailable: " + NavigationService.UnavailableReason); return false; }
            var scene = new Scene();
            GameEntity Box(string name, Editor.ECS.Vector3 pos, Editor.ECS.Vector3 size)
            {
                var e = new GameEntity(name);
                e.Transform.LocalPosition = pos;
                var bc = new BoxCollider(e) { Size = size };
                e.Components.Add(bc);
                scene.Entities.Add(e);
                return e;
            }
            Box("Floor", new Editor.ECS.Vector3(0, -0.5f, 0), new Editor.ECS.Vector3(20, 1, 20));
            Box("Wall", new Editor.ECS.Vector3(0, 1, 0), new Editor.ECS.Vector3(1, 2, 12));
            var agent = new GameEntity("Agent");
            agent.Components.Add(new NavAgent(agent));
            scene.Entities.Add(agent);   // agents are never baked
            var settings = NavigationService.DefaultSettings();
            var geo = NavigationService.CollectGeometry(scene, settings.SourceFlags);
            if (geo.BoxCount != 2) return false;
            var r = NavigationService.Bake(geo, settings);
            if (!r.Success) { ConsoleService.Instance.LogError("[smoke] bake failed: " + r.Message); return false; }
            var previous = NavigationService.LoadedScene;
            bool ok = NavigationService.LoadData(r.Data, scene);
            var corners = new List<Editor.ECS.Vector3>();
            var status = NavigationService.CalculatePath(new Editor.ECS.Vector3(-5, 0, 0), new Editor.ECS.Vector3(5, 0, 0), corners);
            float maxZ = 0f;
            foreach (var c in corners) maxZ = Math.Max(maxZ, Math.Abs(c.Z));
            ConsoleService.Instance.Log("[smoke] test level: " + r.Message + ", path " + status + " with " + corners.Count + " corners, max |z| " + maxZ.ToString("0.00", CultureInfo.InvariantCulture));
            ok &= status == NavPathStatus.Complete && corners.Count >= 3 && maxZ > 6f;
            // leave the editor's own navmesh as it was
            if (previous != null) NavigationService.LoadForScene(previous); else NavigationService.Unload();
            return ok;
        }

        private static bool BakeActiveScene()
        {
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null || !NavigationService.Available) return false;
            var settings = NavigationService.SettingsFor(scene);
            var geo = NavigationService.CollectGeometry(scene, settings.SourceFlags);
            var r = NavigationService.Bake(geo, settings);
            ConsoleService.Instance.Log("[smoke] active scene '" + scene.Name + "': " + (r.Success ? r.Message : "FAILED " + r.Message));
            if (!r.Success) return false;
            var previous = NavigationService.LoadedScene;
            NavigationService.LoadData(r.Data, scene);
            bool ok = false;
            // Two reachable points: a random point, then another one reachable from it within 30 m.
            for (int attempt = 0; attempt < 10 && !ok; attempt++)
            {
                if (!NavigationService.RandomPoint(out var a)) continue;
                if (!NavigationService.RandomPointAround(a, 30f, out var b)) continue;
                if ((a - b).Magnitude < 2f) continue;
                var corners = new List<Editor.ECS.Vector3>();
                var status = NavigationService.CalculatePath(a, b, corners);
                if (status == NavPathStatus.Complete && corners.Count >= 2)
                {
                    ok = true;
                    ConsoleService.Instance.Log("[smoke] path " + a + " -> " + b + ": " + corners.Count + " corners");
                }
            }
            if (previous != null) NavigationService.LoadForScene(previous); else NavigationService.Unload();
            return ok;
        }

        /// <summary>Opens the window and bakes the active scene through it (worker thread + progress + save + load) — the smoke
        /// run works on a copy of a project, so the .vnav it writes is disposable.</summary>
        private static async Task<bool> WindowSmoke()
        {
            if (ProjectData.Current?.ActiveScene == null) return false;
            var w = new NavigationWindow();
            EditorWindows.Show(w);
            await SmokeRegistry.Settle(500);
            bool built = w._fields.Children.Count > 8 && w._bake != null;
            var r = await w.BakeAsync();
            bool baked = r != null && r.Success && r.Path != null && File.Exists(r.Path) && NavigationService.IsLoaded
                && ReferenceEquals(NavigationService.LoadedScene, ProjectData.Current.ActiveScene);
            ConsoleService.Instance.Log("[smoke] navigation window bake: " + (r != null ? r.Message : "no result") + (r?.Path != null ? " -> " + r.Path : ""));
            w._show.IsChecked = true;
            await SmokeRegistry.Settle(400);
            SmokeRegistry.Capture(w, "navigation_window.png");
            if (!string.IsNullOrEmpty(SmokeRegistry.CaptureDir))
            {
                try { VortexAPI.CaptureFrame(Path.Combine(SmokeRegistry.CaptureDir, "navigation_viewport.bmp")); } catch { }
            }
            await SmokeRegistry.Settle(400);
            w._show.IsChecked = false;
            w.Close();
            return built && baked;
        }
    }

    /// <summary>
    /// Edit-mode navigation overlay: while <see cref="Enabled"/>, every frame submits the navmesh surface of the active scene
    /// (loading its .vnav on demand) and — for the selected entity — its patrol route and vision cone through the gizmo
    /// pass. Driven by the main window's animation frame (once per compositor frame, like the viewport). In play mode the
    /// AI runtime draws its own debug layers.
    /// </summary>
    public static class NavMeshOverlay
    {
        private static bool _enabled, _pumping;

        /// <summary>Draw the navmesh (and the selection's route / cone) in the editor viewport.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                NavigationService.ShowNavMesh = value;
                if (value && !_pumping) Pump();
            }
        }

        private static void Pump()
        {
            var top = EditorCommands.Window;
            if (top == null) { _pumping = false; return; }
            _pumping = true;
            top.RequestAnimationFrame(_ =>
            {
                if (!_enabled) { _pumping = false; return; }
                try { Frame(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Navigation] overlay: " + ex.Message); }
                Pump();
            });
        }

        private static void Frame()
        {
            bool playing = PlayModeService.Instance.IsPlaying;
            if (!playing) AiRuntime.EndIfStale();
            if (playing && AiRuntime.IsRunning) return;   // the runtime draws while the game runs
            var scene = ProjectData.Current?.ActiveScene;
            if (scene != null && !ReferenceEquals(NavigationService.LoadedScene, scene) && !NavigationService.IsRunning && NavigationService.HasNavMeshFile(scene))
                NavigationService.LoadForScene(scene);
            NavigationService.ShowNavMesh = _enabled;
            NavigationService.SubmitDebugDraw();
            var sel = SelectionService.Instance.SelectedEntity;
            if (sel == null) return;
            var path = sel.GetComponent<PatrolPath>() ?? sel.Parent?.GetComponent<PatrolPath>();
            if (path != null && path.IsEnabled) PatrolPathService.Submit(path, true);
            if (sel.GetComponent<AIPerception>() is AIPerception p && p.IsEnabled)
            {
                var cone = PerceptionService.GetConeDebugLines(sel);
                if (cone.Length >= 6) VortexAPI.RenderNavigationLines(cone, cone.Length, 0.95f, 0.25f, 0.3f, 0.015f, 60);
            }
        }
    }
}
