using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Services;
using Editor.Core.Threading;
using Editor.Core.UndoRedo;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using VortexEditor.Panels;

namespace VortexEditor.Shell
{
    public partial class MainWindow : Window
    {
        private EditorSession Session => EditorSession.Instance;
        private ProjectHubWindow _hub;
        private bool _closingConfirmed;

        public Thickness LeftInset => OperatingSystem.IsMacOS() ? new Thickness(74, 0, 0, 0) : new Thickness(8, 0, 0, 0);

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            EditorCommands.Window = this;
            Dialogs.Owner = this;
            HostShell.NotifyHandler = (cap, text, level) => Dispatcher.UIThread.Post(async () => await Dialogs.Alert(cap, text));
            HostShell.RevealInFileBrowser = EditorCommands.RevealInFinder;
            HostShell.AlertSound = () => { };
            BuildMenus();
            Opened += OnOpened;
            Activated += (s, e) => Session.OnWindowActivated();
            Closing += OnClosing;
            Session.ProjectOpened += p => Dispatcher.UIThread.Post(() => OnProjectChanged(p));
            Session.ProjectClosed += () => Dispatcher.UIThread.Post(() => OnProjectChanged(null));
            Session.Toast += m => ShowToast(m);
            Session.Error += m => Dispatcher.UIThread.Post(async () => await Dialogs.Alert("Vortex", m));
            PlayModeService.Instance.StateChanged += (s, st) => Dispatcher.UIThread.Post(SyncPlayButtons);
            PlayModeService.Instance.GameViewChanged += g => Dispatcher.UIThread.Post(() => { TabGame.IsChecked = g; TabScene.IsChecked = !g; });
            UndoRedoManager.Instance.StateChanged += (s, e) => Dispatcher.UIThread.Post(SyncUndoText);
            ViewportPanel.StatusChanged += (st, res) => { StatusText.Text = st; ResolutionText.Text = res; };
            ViewportPanel.EngineView.ToastRequested += ShowToast;
            AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
            SyncPlayButtons();
        }

        private void OnOpened(object sender, EventArgs e)
        {
            var o = Program.Options;
            Session.EnsureEngine();
            bool opened = false;
            if (!string.IsNullOrEmpty(o.ProjectPath)) opened = Session.OpenProject(o.ProjectPath);
            else if (EditorPreferences.Current.OpenLastProjectOnStart && Session.LastProjectPath != null) opened = Session.OpenProject(Session.LastProjectPath);
            if (opened && !string.IsNullOrEmpty(o.SceneName) && Session.Project?.Scenes != null)
                foreach (var s in Session.Project.Scenes) if (s != null && string.Equals(s.Name, o.SceneName, StringComparison.OrdinalIgnoreCase)) { Session.ActivateScene(s); break; }
            if (!opened) ShowProjectHub(createTab: false);
            if (o.SmokeSeconds > 0)
            {
                // Echo the editor console to stdout so a smoke run is verifiable from a terminal / CI log.
                var console = ConsoleService.Instance;
                console.EntryAdded += () => { try { if (console.Entries.Count > 0) { var le = console.Entries[console.Entries.Count - 1]; System.Console.WriteLine("[" + le.LevelTag + "] " + le.Message); } } catch { } };
                DispatcherTimer.RunOnce(SmokeInteract, TimeSpan.FromSeconds(Math.Max(1, o.SmokeSeconds - 4)));
                DispatcherTimer.RunOnce(() => SmokeCapture(o.CaptureDir), TimeSpan.FromSeconds(o.SmokeSeconds));
            }
        }

        /// <summary>Smoke test: select an entity, switch the tool, open the console — so the capture shows real panels.</summary>
        private void SmokeInteract()
        {
            try
            {
                var scene = Session.Project?.ActiveScene;
                GameEntity pick = null;
                if (scene?.Entities != null) foreach (var e in scene.Entities) { if (e.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>() != null && e.GetComponent<Light>() == null) { pick = e; if (e.Name.Contains("Crate")) break; } }
                if (pick != null) SelectionService.Instance.Select(pick);
                TransformGizmoService.Instance.SetRotateMode();
                var log = ConsoleService.Instance;
                ConsoleService.Instance.Log("Smoke test: selected " + (pick?.Name ?? "nothing"));
                if (OperatingSystem.IsMacOS())
                {
                    // The 3D view must live inside the viewport panel (not cover the window) and clicks over it must
                    // still reach the toolkit: inspect the native view and post a click through AppKit's event path.
                    var ev = ViewportPanel.EngineView;
                    string desc = VortexEditor.Viewport.MacViewProbe.Describe(ev.NativeHandle, out bool embedOk);
                    var tl = TopLevel.GetTopLevel(ev);
                    var tv = tl != null ? ev.TransformToVisual(tl) : null;
                    desc += $" | control {ev.Bounds.Width:0}x{ev.Bounds.Height:0} at {(tv.HasValue ? new Point(0, 0).Transform(tv.Value).ToString() : "?")}, visible={ev.IsEffectivelyVisible}, panel {ViewportPanel.Bounds.Width:0}x{ViewportPanel.Bounds.Height:0}";
                    if (embedOk) log.Log("SMOKE OK   viewport embedding: " + desc); else log.LogError("SMOKE FAIL viewport embedding: " + desc);
                    // Give the toolkit a moment to process the synthetic click before the heavier checks below keep
                    // the UI thread busy. (No Activate() here: an activation in flight makes AppKit drop the event.)
                    int presses = ev.PointerPressCount;
                    bool posted = VortexEditor.Viewport.MacViewProbe.Click(ev.NativeHandle, ev.Bounds.Width / 2, ev.Bounds.Height / 2);
                    DispatcherTimer.RunOnce(() =>
                    {
                        if (posted && ev.PointerPressCount > presses) log.Log("SMOKE OK   viewport click routed to the toolkit");
                        else log.LogError("SMOKE FAIL viewport click not received (posted=" + posted + ", presses=" + ev.PointerPressCount + ")");
                        if (System.Environment.GetEnvironmentVariable("VORTEX_SMOKE_FULL") == "1") SmokeFull(scene);
                    }, TimeSpan.FromMilliseconds(400));
                }
                else if (System.Environment.GetEnvironmentVariable("VORTEX_SMOKE_FULL") == "1") SmokeFull(scene);
                if (System.Environment.GetEnvironmentVariable("VORTEX_SMOKE_FX") == "1" && scene?.Settings != null)
                {
                    // Same path as the Environment panel: SSAO + bloom + vignette, previewed in the build viewport.
                    var st = scene.Settings;
                    st.AoEnabled = true; st.AoRadius = 0.7f; st.AoIntensity = 1.4f;
                    st.BloomEnabled = true; st.BloomThreshold = 0.55f; st.BloomIntensity = 1.0f; st.BloomScatter = 0.7f;
                    st.VignetteEnabled = true; st.VignetteIntensity = 0.6f;
                    st.Apply();
                    VortexAPI.SetPostMainView(true);
                    SelectionService.Instance.ClearSelection();
                    log.Log("Smoke test: post effects enabled (SSAO, bloom, vignette)");
                }
                BottomTabs.SelectedIndex = 0;
            }
            catch (Exception ex) { System.Console.WriteLine("smoke interact failed: " + ex); }
        }

        /// <summary>Exercise the main command paths (create, undo/redo, components, save, secondary view, export) and log the outcome.</summary>
        private void SmokeFull(Scene scene)
        {
            var log = ConsoleService.Instance;
            void Check(string what, Func<bool> f) { bool ok = false; string err = null; try { ok = f(); } catch (Exception ex) { err = ex.Message; } if (ok) log.Log("SMOKE OK   " + what); else log.LogError("SMOKE FAIL " + what + (err != null ? ": " + err : "")); }
            int before = scene?.Entities?.Count ?? 0;
            Check("create cube", () => { EditorCommands.CreatePrimitive(PrimitiveType.Cube); return scene.Entities.Count == before + 1; });
            Check("undo create", () => { EditorCommands.Undo(); return scene.Entities.Count == before; });
            Check("redo create", () => { EditorCommands.Redo(); return scene.Entities.Count == before + 1; });
            Check("duplicate", () => { EditorCommands.Duplicate(); return scene.Entities.Count == before + 2; });
            Check("delete", () => { EditorCommands.Delete(); return scene.Entities.Count == before + 1; });
            Check("create light", () => { EditorCommands.CreateLight(LightType.Point); return SelectionService.Instance.SelectedEntity?.GetComponent<Light>() != null; });
            Check("add component", () => { EditorCommands.AddComponent(new Editor.ECS.Components.Physics.Rigidbody(SelectionService.Instance.SelectedEntity)); return SelectionService.Instance.SelectedEntity.GetComponent<Editor.ECS.Components.Physics.Rigidbody>() != null; });
            Check("create material", () => { var p = Editor.Core.Assets.AssetActions.CreateMaterial(System.IO.Path.Combine(Session.Project.Path, "Materials", "SmokeMaterial.vmat"), "Standard"); return File.Exists(p); });
            Check("create script", () => { var p = ScriptingService.CreateScript("SmokeBehaviour"); return File.Exists(p); });
            Check("save all", () => { Session.SaveAll(); return true; });
            Check("toggle grid", () => { EditorCommands.ToggleGrid(); EditorCommands.ToggleGrid(); return true; });
            Check("split view", () => { ViewportPanel.SetLayout(2); return true; });
            Check("play + stop", () => { EditorCommands.Play(); bool playing = PlayModeService.Instance.State == PlayState.Playing; EditorCommands.Stop(); return playing && PlayModeService.Instance.State == PlayState.Editing; });
            Check("export (release, branded)", () =>
            {
                // The full path of the Build dialog: icons from the project (or the engine logo), product name /
                // version / bundle id from the settings, host platform. Verified on the produced bundle.
                string outDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vortex-smoke-build");
                var p = Session.Project;
                var icons = VortexEditor.Build.IconFactory.Build(p, System.IO.Path.Combine(outDir, "icons"));
                var req = new Editor.Core.Services.Build.ExportRequest { ProjectRoot = p.Path, ProjectName = p.Name, Settings = p.Settings, OutputDir = outDir, IcnsPath = icons.IcnsPath, IcoPath = icons.IcoPath, PngIconPath = icons.PngPath };
                var r = Editor.Core.Services.Build.GamePackager.Export(req, null);
                if (!r.Success) log.LogWarning(r.Message);
                bool ok = r.Success;
                if (ok && OperatingSystem.IsMacOS())
                {
                    string exe = System.IO.Path.Combine(r.OutputPath, "Contents", "MacOS", Editor.Core.Services.Build.GamePackager.SanitizeName(req.ProductName));
                    string plist = System.IO.Path.Combine(r.OutputPath, "Contents", "Info.plist");
                    string icns = System.IO.Path.Combine(r.OutputPath, "Contents", "Resources", "AppIcon.icns");
                    ok = File.Exists(exe) && File.Exists(icns) && File.Exists(plist) && File.ReadAllText(plist).Contains("<string>" + req.ProductName + "</string>") && File.ReadAllText(plist).Contains(req.BundleIdentifier);
                    log.Log("Smoke export: " + r.OutputPath + " exe=" + File.Exists(exe) + " icns=" + File.Exists(icns) + " version=" + req.Version + " id=" + req.BundleIdentifier + (icons.Warnings.Count > 0 ? " warnings: " + string.Join("; ", icons.Warnings) : ""));
                }
                if (System.Environment.GetEnvironmentVariable("VORTEX_SMOKE_KEEP_EXPORT") != "1") { try { Directory.Delete(outDir, true); } catch { } }   // never leave a stray game bundle behind
                return ok;
            });
            if (System.Environment.GetEnvironmentVariable("VORTEX_SMOKE_EXPORT_WIN") == "1")
                Check("export (windows, runtime pack)", () =>
                {
                    string outDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vortex-smoke-build-win");
                    var p = Session.Project;
                    var icons = VortexEditor.Build.IconFactory.Build(p, System.IO.Path.Combine(outDir, "icons"));
                    var req = new Editor.Core.Services.Build.ExportRequest { ProjectRoot = p.Path, ProjectName = p.Name, Settings = p.Settings, OutputDir = outDir, Platform = Editor.Core.Services.Build.ExportPlatform.WindowsX64, IcoPath = icons.IcoPath, CreateArchive = true };
                    var r = Editor.Core.Services.Build.GamePackager.Export(req, null);
                    if (!r.Success) log.LogWarning(r.Message);
                    bool ok = r.Success;
                    if (ok)
                    {
                        string exe = System.IO.Path.Combine(r.OutputPath, Editor.Core.Services.Build.GamePackager.SanitizeName(req.ProductName) + ".exe");
                        string res = File.Exists(exe) ? Editor.Core.Services.Build.PeResourceWriter.Describe(exe) : "(no exe)";
                        ok = File.Exists(exe) && res.Contains("#14") && res.Contains("#16") && File.Exists(System.IO.Path.Combine(r.OutputPath, "Assets.vpak")) && Directory.GetFiles(outDir, "*.zip").Length == 1;
                        log.Log("Smoke export (win): " + r.OutputPath + " resources:\n" + res);
                    }
                    if (System.Environment.GetEnvironmentVariable("VORTEX_SMOKE_KEEP_EXPORT") != "1") { try { Directory.Delete(outDir, true); } catch { } }
                    return ok;
                });
            Check("scene switch", () => { var other = Session.Project.Scenes.FirstOrDefault(sc => !ReferenceEquals(sc, scene)); if (other == null) return true; Session.ActivateScene(other); Session.ActivateScene(scene); return ReferenceEquals(Session.Project.ActiveScene, scene); });
        }

        private void SmokeCapture(string dir)
        {
            try
            {
                dir = string.IsNullOrEmpty(dir) ? Path.GetTempPath() : dir;
                Directory.CreateDirectory(dir);
                VortexAPI.CaptureFrame(Path.Combine(dir, "editor_viewport.bmp"));
                var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)(Bounds.Width * RenderScaling), (int)(Bounds.Height * RenderScaling)), new Vector(96 * RenderScaling, 96 * RenderScaling));
                bmp.Render(this);
                bmp.Save(Path.Combine(dir, "editor_window.png"));
                if (_hub != null)
                {
                    var hb = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)(_hub.Bounds.Width * RenderScaling), (int)(_hub.Bounds.Height * RenderScaling)), new Vector(96 * RenderScaling, 96 * RenderScaling));
                    hb.Render(_hub);
                    hb.Save(Path.Combine(dir, "editor_hub.png"));
                }
                System.Console.WriteLine("smoke: captured to " + dir);
            }
            catch (Exception ex) { System.Console.WriteLine("smoke capture failed: " + ex.Message); }
            _closingConfirmed = true;
            Close();
        }

        private async void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (_closingConfirmed) { Session.ShutdownEngine(); return; }
            e.Cancel = true;
            if (Session.HasProject && !await Dialogs.Confirm("Quit Vortex Editor?", "Unsaved changes will be lost.", "Quit", "Cancel", destructive: true)) return;
            _closingConfirmed = true;
            Session.ShutdownEngine();
            Close();
        }

        private void OnProjectChanged(ProjectData p)
        {
            ProjectTitle.Text = p?.Name ?? "Vortex";
            SceneSubtitle.Text = p?.ActiveScene?.Name ?? "No project";
            Title = p != null ? "Vortex Editor — " + p.Name : "Vortex Editor";
            if (p != null) p.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(ProjectData.ActiveScene)) Dispatcher.UIThread.Post(() => SceneSubtitle.Text = p.ActiveScene?.Name ?? ""); };
            Hierarchy.Reload();
            FileTree.Reload();
            AssetBrowser.Reload();
            Environment.Refresh();
            ViewportPanel.RefreshCameraList();
            Inspector.Refresh();
            SyncUndoText();
        }

        // ---------------------------------------------------------------- menus (macOS menu bar / in-window on other OSes)
        private void BuildMenus()
        {
            var menu = new NativeMenu();
            var file = Sub("File",
                Item("New Project…", "Cmd+Shift+N", () => EditorCommands.NewProject()),
                Item("Open Project…", "Cmd+O", () => EditorCommands.OpenProject()),
                Sep(),
                Item("Save", "Cmd+S", () => EditorCommands.SaveProject()),
                Item("Save All", "Cmd+Shift+S", () => EditorCommands.SaveAll()),
                Sep(),
                Item("Project Settings…", "Cmd+,", () => EditorCommands.ProjectSettings()),
                Item("Close Project", null, async () => await EditorCommands.CloseProject()),
                Sep(),
                Item("Build…", "Cmd+B", () => EditorCommands.Build()),
                Item("Build and Run", "Cmd+R", () => EditorCommands.BuildAndRun()));
            var edit = Sub("Edit",
                Item("Undo", "Cmd+Z", () => EditorCommands.Undo()),
                Item("Redo", "Cmd+Shift+Z", () => EditorCommands.Redo()),
                Sep(),
                Item("Cut", "Cmd+X", () => EditorCommands.Cut()),
                Item("Copy", "Cmd+C", () => EditorCommands.Copy()),
                Item("Paste", "Cmd+V", () => EditorCommands.Paste()),
                Item("Duplicate", "Cmd+D", () => EditorCommands.Duplicate()),
                Item("Delete", null, () => EditorCommands.Delete()),
                Item("Rename…", null, () => EditorCommands.Rename()),
                Sep(),
                Item("Select All", "Cmd+A", () => EditorCommands.SelectAll()),
                Item("Find Entity…", "Cmd+F", () => EditorCommands.Find()));
            var view = Sub("View",
                Item("Toggle Grid", "G", () => EditorCommands.ToggleGrid()),
                Item("Snap to Grid", null, () => EditorCommands.ToggleSnap()),
                Item("Toggle Gizmos", null, () => EditorCommands.ToggleGizmos()),
                Item("Toggle Colliders", null, () => EditorCommands.ToggleColliders()),
                Item("Toggle Physics Debug (play)", null, () => EditorCommands.TogglePhysicsDebug()),
                Sep(),
                Item("Focus Selection", "F", () => EditorCommands.FocusSelected()),
                Item("Reset Camera", "Home", () => EditorCommands.ResetCamera()),
                Sep(),
                Item("Release Mode (hide play banner)", null, () => EditorCommands.ToggleReleaseMode()));
            var assets = Sub("Assets",
                Item("Import Asset…", "Cmd+I", async () => await EditorCommands.ImportAsset()),
                Item("Export Selected Asset…", null, () => EditorCommands.ExportAsset()),
                Sep(),
                Item("Create Material", null, () => EditorCommands.CreateMaterial()),
                Item("Create Shader", null, () => EditorCommands.CreateShader()),
                Item("Create Script", null, () => EditorCommands.CreateScript()),
                Sep(),
                Item("Open Scripts Project in IDE", null, () => EditorCommands.OpenScriptsProject()));
            var go = Sub("GameObject",
                Item("Create Empty", "Cmd+Shift+E", () => EditorCommands.CreateEmpty()),
                Item("Create Player", null, () => EditorCommands.CreatePlayer()),
                Sub("3D Object",
                    Item("Cube", null, () => EditorCommands.CreatePrimitive(PrimitiveType.Cube)),
                    Item("Sphere", null, () => EditorCommands.CreatePrimitive(PrimitiveType.Sphere)),
                    Item("Capsule", null, () => EditorCommands.CreatePrimitive(PrimitiveType.Capsule)),
                    Item("Cylinder", null, () => EditorCommands.CreatePrimitive(PrimitiveType.Cylinder)),
                    Item("Plane", null, () => EditorCommands.CreatePrimitive(PrimitiveType.Plane)),
                    Item("Quad", null, () => EditorCommands.CreatePrimitive(PrimitiveType.Quad))),
                Sub("Light",
                    Item("Directional Light", null, () => EditorCommands.CreateLight(LightType.Directional)),
                    Item("Point Light", null, () => EditorCommands.CreateLight(LightType.Point)),
                    Item("Spot Light", null, () => EditorCommands.CreateLight(LightType.Spot)),
                    Item("Skybox", null, () => EditorCommands.CreateSkybox())),
                Item("Camera", null, () => EditorCommands.CreateCamera()),
                Sub("Audio",
                    Item("Audio Source", null, () => EditorCommands.CreateAudioSource()),
                    Item("Reverb Zone", null, () => EditorCommands.CreateReverbZone())),
                Sub("UI",
                    Item("Canvas", null, () => EditorCommands.CreateUI("Canvas")),
                    Item("Text", null, () => EditorCommands.CreateUI("Text")),
                    Item("Image", null, () => EditorCommands.CreateUI("Image")),
                    Item("Button", null, () => EditorCommands.CreateUI("Button"))));
            var component = Sub("Component",
                Item("Mesh Renderer", null, () => AddComp(e => new Editor.ECS.Components.Rendering.MeshRenderer(e))),
                Item("Camera", null, () => AddComp(e => new Editor.ECS.Components.Rendering.Camera(e))),
                Item("Light", null, () => AddComp(e => new Light(e))),
                Sub("Physics",
                    Item("Rigidbody", null, () => AddComp(e => new Editor.ECS.Components.Physics.Rigidbody(e))),
                    Item("Box Collider", null, () => AddComp(e => new Editor.ECS.Components.Physics.BoxCollider(e))),
                    Item("Sphere Collider", null, () => AddComp(e => new Editor.ECS.Components.Physics.SphereCollider(e))),
                    Item("Capsule Collider", null, () => AddComp(e => new Editor.ECS.Components.Physics.CapsuleCollider(e)))),
                Sub("Audio",
                    Item("Audio Source", null, () => AddComp(e => new Editor.ECS.Components.Audio.AudioSource(e))),
                    Item("Audio Listener", null, () => AddComp(e => new Editor.ECS.Components.Audio.AudioListener(e)))),
                Item("New Script…", null, () => { var e = SelectionService.Instance.SelectedEntity; if (e == null) return; var p = ScriptingService.CreateScript("NewBehaviour"); e.AddComponent(new Editor.ECS.Components.Scripting.Script(e, ScriptingService.MakeRelative(ProjectData.Current?.Path ?? "", p))); EditorCommands.OpenInIde(p); Inspector.Refresh(); }));
            var window = Sub("Window",
                Item("Scene Hierarchy", "Cmd+1", () => TogglePanel("Hierarchy")),
                Item("File System", "Cmd+2", () => TogglePanel("Files")),
                Item("Inspector", "Cmd+3", () => TogglePanel("Inspector")),
                Item("Project", "Cmd+4", () => { BottomTabs.SelectedIndex = 0; TogglePanel("Bottom", forceShow: true); }),
                Item("Console", "Cmd+5", () => { BottomTabs.SelectedIndex = 1; TogglePanel("Bottom", forceShow: true); }),
                Sep(),
                Item("Audio Mixer…", null, () => EditorCommands.AudioMixer()),
                Item("Source Control…", null, () => EditorCommands.GitWindow()),
                Sep(),
                Item("Reset Layout", null, () => EditorCommands.ResetLayout()));
            var help = Sub("Help",
                Item("Documentation", null, () => EditorCommands.Documentation()),
                Item("Scripting API Reference", null, () => EditorCommands.ApiReference()),
                Item("Check for Updates…", null, () => EditorCommands.CheckForUpdates()),
                Sep(),
                Item("About Vortex Engine", null, () => EditorCommands.About()));
            foreach (var m in new[] { file, edit, view, assets, go, component, window, help }) menu.Items.Add(m);
            NativeMenu.SetMenu(this, menu);
            if (!OperatingSystem.IsMacOS()) { MenuBarHost.IsVisible = true; }
        }

        private static NativeMenuItem Sub(string header, params NativeMenuItemBase[] items) { var mi = new NativeMenuItem(header) { Menu = new NativeMenu() }; foreach (var i in items) mi.Menu.Items.Add(i); return mi; }
        private static NativeMenuItem Item(string header, string gesture, Action action)
        {
            var mi = new NativeMenuItem(header);
            if (!string.IsNullOrEmpty(gesture)) { try { mi.Gesture = KeyGesture.Parse(gesture); } catch { } }
            mi.Click += (s, e) => action();
            return mi;
        }
        private static NativeMenuItem Item(string header, string gesture, Func<Task> action) => Item(header, gesture, () => { _ = action(); });
        private static NativeMenuItemSeparator Sep() => new NativeMenuItemSeparator();
        private void AddComp(Func<GameEntity, Editor.ECS.Component> make) { var e = SelectionService.Instance.SelectedEntity; if (e != null) EditorCommands.AddComponent(make(e)); }

        // ---------------------------------------------------------------- global shortcuts (in-window; the native menu handles most on macOS)
        private void OnGlobalKeyDown(object sender, KeyEventArgs e)
        {
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (!cmd) return;
            switch (e.Key)
            {
                case Key.S: if (shift) EditorCommands.SaveAll(); else EditorCommands.SaveProject(); e.Handled = true; break;
                case Key.Z: if (shift) EditorCommands.Redo(); else EditorCommands.Undo(); e.Handled = true; break;
                case Key.Y: EditorCommands.Redo(); e.Handled = true; break;
                case Key.P: EditorCommands.TogglePlay(); e.Handled = true; break;
                case Key.B: EditorCommands.Build(); e.Handled = true; break;
                case Key.D1: TogglePanel("Hierarchy"); e.Handled = true; break;
                case Key.D2: TogglePanel("Files"); e.Handled = true; break;
                case Key.D3: TogglePanel("Inspector"); e.Handled = true; break;
                case Key.D4: BottomTabs.SelectedIndex = 0; TogglePanel("Bottom", forceShow: true); e.Handled = true; break;
                case Key.D5: BottomTabs.SelectedIndex = 1; TogglePanel("Bottom", forceShow: true); e.Handled = true; break;
            }
        }

        // ---------------------------------------------------------------- toolbar
        private void OnSceneTab(object s, RoutedEventArgs e) { if (PlayModeService.Instance.IsPlaying) EditorCommands.Stop(); PlayModeService.Instance.SetGameView(false); }
        private void OnGameTab(object s, RoutedEventArgs e) => PlayModeService.Instance.SetGameView(true);
        private void OnPlay(object s, RoutedEventArgs e) { if (!Session.HasProject) { ShowToast("Open a project first"); return; } if (PlayModeService.Instance.State == PlayState.Playing) EditorCommands.Stop(); else EditorCommands.Play(); }
        private void OnPause(object s, RoutedEventArgs e) { var pms = PlayModeService.Instance; if (pms.State == PlayState.Playing) pms.Pause(); else if (pms.State == PlayState.Paused) pms.Resume(); }
        private void OnStop(object s, RoutedEventArgs e) => EditorCommands.Stop();
        private void OnPlayMenu(object s, RoutedEventArgs e)
        {
            var m = new MenuFlyout();
            var a = new MenuItem { Header = "Play in Viewport" }; a.Click += (x, y) => EditorCommands.Play();
            var b = new MenuItem { Header = "Play in Standalone Player" }; b.Click += (x, y) => EditorCommands.PlayInNewWindow();
            var c = new MenuItem { Header = "Game View (without playing)" }; c.Click += (x, y) => PlayModeService.Instance.SetGameView(true);
            m.Items.Add(a); m.Items.Add(b); m.Items.Add(new Separator()); m.Items.Add(c);
            m.ShowAt(PlayMenuButton);
        }
        private void OnBuildClick(object s, RoutedEventArgs e) => EditorCommands.Build();
        private void OnGitClick(object s, RoutedEventArgs e) => EditorCommands.GitWindow();
        private void OnSettingsClick(object s, RoutedEventArgs e) => EditorCommands.ProjectSettings();

        private void SyncPlayButtons()
        {
            var st = PlayModeService.Instance.State;
            PlayIcon.Icon = st == PlayState.Playing ? "Stop" : "Play";
            PlayIcon.Foreground = (Avalonia.Media.IBrush)this.FindResource(st == PlayState.Playing ? "VxRedBrush" : "VxGreenBrush");
            StopButton.IsEnabled = st != PlayState.Editing;
            PauseButton.IsEnabled = st != PlayState.Editing;
            ToolTip.SetTip(PlayButton, st == PlayState.Playing ? "Stop (⌘P)" : "Play (⌘P)");
        }

        private void SyncUndoText()
        {
            var u = UndoRedoManager.Instance;
            UndoText.Text = u.CanUndo ? "Undo: " + u.UndoName : "";
        }

        // ---------------------------------------------------------------- panels
        private bool _hierarchyVisible = true, _filesVisible = true, _inspectorVisible = true, _bottomVisible = true;
        private void TogglePanel(string name, bool forceShow = false)
        {
            switch (name)
            {
                case "Hierarchy": _hierarchyVisible = forceShow || !_hierarchyVisible; break;
                case "Files": _filesVisible = forceShow || !_filesVisible; break;
                case "Inspector": _inspectorVisible = forceShow || !_inspectorVisible; break;
                case "Bottom": _bottomVisible = forceShow || !_bottomVisible; break;
            }
            ApplyLayout();
        }
        public void ResetLayout() { _hierarchyVisible = _filesVisible = _inspectorVisible = _bottomVisible = true; Workspace.ColumnDefinitions[0].Width = new GridLength(260); Workspace.ColumnDefinitions[4].Width = new GridLength(330); CenterColumn.RowDefinitions[2].Height = new GridLength(300); LeftColumn.RowDefinitions[0].Height = new GridLength(3, GridUnitType.Star); LeftColumn.RowDefinitions[2].Height = new GridLength(2, GridUnitType.Star); ApplyLayout(); }
        private void ApplyLayout()
        {
            bool left = _hierarchyVisible || _filesVisible;
            Workspace.ColumnDefinitions[0].Width = left ? (Workspace.ColumnDefinitions[0].Width.Value > 10 ? Workspace.ColumnDefinitions[0].Width : new GridLength(260)) : new GridLength(0);
            Workspace.ColumnDefinitions[1].Width = new GridLength(left ? 5 : 0);
            LeftColumn.RowDefinitions[0].Height = _hierarchyVisible ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
            LeftColumn.RowDefinitions[1].Height = new GridLength(_hierarchyVisible && _filesVisible ? 5 : 0);
            LeftColumn.RowDefinitions[2].Height = _filesVisible ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
            Workspace.ColumnDefinitions[4].Width = _inspectorVisible ? (Workspace.ColumnDefinitions[4].Width.Value > 10 ? Workspace.ColumnDefinitions[4].Width : new GridLength(330)) : new GridLength(0);
            Workspace.ColumnDefinitions[3].Width = new GridLength(_inspectorVisible ? 5 : 0);
            CenterColumn.RowDefinitions[2].Height = _bottomVisible ? (CenterColumn.RowDefinitions[2].Height.Value > 10 ? CenterColumn.RowDefinitions[2].Height : new GridLength(300)) : new GridLength(0);
            CenterColumn.RowDefinitions[1].Height = new GridLength(_bottomVisible ? 5 : 0);
        }

        // ---------------------------------------------------------------- windows & dialogs
        public void ShowToast(string message) => Toast.Show(message);
        public void FocusHierarchySearch() => Hierarchy.FocusSearch();
        public void RenameSelected() { var e = SelectionService.Instance.SelectedEntity; if (e != null) _ = Hierarchy.RenameEntity(e); }

        public void ShowProjectHub(bool createTab)
        {
            if (_hub != null) { _hub.Activate(); _hub.SelectTab(createTab); return; }
            if (o_SmokeHubOnly()) { }
            _hub = new ProjectHubWindow(createTab);
            _hub.Closed += (s, e) => { _hub = null; if (!Session.HasProject && !_closingConfirmed) { _closingConfirmed = true; Close(); } };
            _hub.Show(this);
        }

        private static bool o_SmokeHubOnly() => false;

        public void OpenProjectSettings() { if (Session.HasProject) new ProjectSettingsWindow().ShowDialog(this); else ShowToast("Open a project first"); }
        public void OpenAbout() => new AboutWindow().ShowDialog(this);
        public void OpenAudioMixer() { if (Session.HasProject) new AudioMixerWindow().Show(this); else ShowToast("Open a project first"); }
        public void OpenGit() { if (Session.HasProject) new GitWindow().Show(this); else ShowToast("Open a project first"); }
        public void OpenBuildDialog(bool runAfter) { if (Session.HasProject) new BuildWindow(runAfter).ShowDialog(this); else ShowToast("Open a project first"); }
        public void OpenMaterialEditor(string vmatPath) => new MaterialEditorWindow(vmatPath).Show(this);
        public void OpenCollisionEditor(GameEntity e) { if (e != null) { SelectionService.Instance.Select(e); new CollisionEditorWindow(e).Show(this); } }
        public void OpenSocketEditor(GameEntity e) { if (e != null) { SelectionService.Instance.Select(e); new SocketEditorWindow(e).Show(this); } }
        public void OpenSoundContainerEditor(string path) => new SoundContainerEditorWindow(path).Show(this);
        public void OpenUiEditor(string path) => new UiEditorWindow(path).Show(this);
        public void OpenAnimationEditor(string path) => new AnimationEditorWindow(path).Show(this);

        /// <summary>Run the current project in the standalone player process (saves first).</summary>
        public void LaunchStandalonePlayer()
        {
            var p = Session.Project; if (p == null) { ShowToast("Open a project first"); return; }
            try
            {
                Session.SaveAll();
                string exe = FindPlayerExecutable();
                if (exe == null) { ShowToast("Vortex.Player not found next to the editor (build Managed/Vortex.Player)"); return; }
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                psi.ArgumentList.Add("--project=" + p.Path);
                if (p.ActiveScene != null) psi.ArgumentList.Add("--scene=" + p.ActiveScene.Name);
                Process.Start(psi);
                PlayModeService.Instance.IsExternalWindow = true;
                ShowToast("Player started in its own window");
            }
            catch (Exception ex) { EditorCommands.Fail("Could not start the player", ex); }
        }

        private static string FindPlayerExecutable()
        {
            string baseDir = AppContext.BaseDirectory;
            string[] names = OperatingSystem.IsWindows() ? new[] { "Vortex.Player.exe" } : new[] { "Vortex.Player" };
            foreach (var dir in new[] { baseDir, Path.Combine(baseDir, "..", "Resources", "Player"), Path.Combine(baseDir, "player"), Path.Combine(baseDir, "..", "..", "..", "..", "Vortex.Player", "bin", "Debug", "net10.0"), Path.Combine(baseDir, "..", "..", "..", "..", "Vortex.Player", "bin", "Release", "net10.0") })
                foreach (var n in names) { var f = Path.GetFullPath(Path.Combine(dir, n)); if (File.Exists(f)) return f; }
            return null;
        }
    }
}
