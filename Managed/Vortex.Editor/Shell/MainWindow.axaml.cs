using System;
using System.Collections.Generic;
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
        private int _unseenErrors;

        public Thickness LeftInset => OperatingSystem.IsMacOS() ? new Thickness(74, 0, 0, 0) : new Thickness(8, 0, 0, 0);

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            try { ProjectSettingsWindow.ApplyTheme(EditorPreferences.Current.Theme); } catch { }
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
            ConsoleService.Instance.EntryAdded += OnConsoleEntry;
            BottomTabs.SelectionChanged += (s, e) => { if (IsConsoleShowing) ClearConsoleBadge(); };
            AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
            BuildButton.ContextMenu = BuildContextMenu();
            SyncPlayButtons();
            SyncUndoText();
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
                // Echo the editor console to stdout so a smoke run is verifiable from a terminal / CI log. Write to the
                // ORIGINAL stdout: during play the console captures Console.Out, and echoing through it would loop.
                var console = ConsoleService.Instance;
                var stdout = System.Console.Out;
                console.EntryAdded += () => { try { if (console.Entries.Count > 0) { var le = console.Entries[console.Entries.Count - 1]; stdout.WriteLine("[" + le.LevelTag + "] " + le.Message); stdout.Flush(); } } catch { } };
                SmokeRegistry.CaptureDir = o.CaptureDir;
                DispatcherTimer.RunOnce(SmokeInteract, TimeSpan.FromSeconds(Math.Max(1, o.SmokeSeconds - 4)));
                // the end-to-end play check (VORTEX_SMOKE_PLAYFIRE) compiles scripts + waits for the weapon draw: give it time
                double captureAt = o.SmokeSeconds + (System.Environment.GetEnvironmentVariable("VORTEX_SMOKE_PLAYFIRE") == "1" ? 30 : 0);
                DispatcherTimer.RunOnce(() => SmokeCapture(o.CaptureDir), TimeSpan.FromSeconds(captureAt));
            }
        }

        /// <summary>End-to-end gameplay input check (a project with the Horror Starter player): press Play, hold the LEFT
        /// mouse button through AppKit's real event path -> the weapon must fire (ammo drops); click the RIGHT button ->
        /// the player must aim (ADS toggle). Reads PlayerRig from the running script assembly.</summary>
        private void SmokePlayFire(VortexEditor.Viewport.EngineViewport ev)
        {
            var log = ConsoleService.Instance;
            object Rig(string field)
            {
                var t = Editor.Scripting.ScriptRuntime.Instance.ScriptAssembly?.GetType("PlayerRig");
                var f = t?.GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return f?.GetValue(null);
            }
            try { Activate(); } catch { }
            EditorCommands.Play();
            double cx = ev.Bounds.Width / 2, cy = ev.Bounds.Height / 2;
            int waited = 0;
            void WaitReady()
            {
                bool ready = Rig("Ready") is bool r && r && Rig("Switching") is bool sw && !sw && Rig("ActiveWeapon") != null;
                if (!ready && waited < 40) { waited++; DispatcherTimer.RunOnce(WaitReady, TimeSpan.FromMilliseconds(500)); return; }
                if (!ready) { log.LogError("SMOKE FAIL play: the player never became ready (" + waited / 2 + " s)"); EditorCommands.Stop(); return; }
                int ammo0 = Rig("Ammo") is int a0 ? a0 : -1;
                VortexEditor.Viewport.MacViewProbe.MouseButton(ev.NativeHandle, cx, cy, false, true);
                DispatcherTimer.RunOnce(() =>
                {
                    VortexEditor.Viewport.MacViewProbe.MouseButton(ev.NativeHandle, cx, cy, false, false);
                    int ammo1 = Rig("Ammo") is int a1 ? a1 : -1;
                    VortexEditor.Viewport.MacViewProbe.MouseButton(ev.NativeHandle, cx, cy, true, true);
                    VortexEditor.Viewport.MacViewProbe.MouseButton(ev.NativeHandle, cx, cy, true, false);
                    DispatcherTimer.RunOnce(() =>
                    {
                        bool ads = Rig("Ads") is bool b && b;
                        string r = "ammo " + ammo0 + " -> " + ammo1 + ", ADS after right click = " + ads + ", window active = " + IsActive;
                        if (ammo1 >= 0 && ammo1 < ammo0 && ads) log.Log("SMOKE OK   play: left mouse fires, right mouse aims (" + r + ")");
                        else log.LogError("SMOKE FAIL play: mouse fire/aim (" + r + ")");
                        EditorCommands.Stop();
                    }, TimeSpan.FromMilliseconds(700));
                }, TimeSpan.FromMilliseconds(700));
            }
            DispatcherTimer.RunOnce(WaitReady, TimeSpan.FromMilliseconds(1500));
        }

        /// <summary>Smoke test: select an entity, switch the tool, click into the viewport, check the mouse buttons,
        /// then run the full check list (VORTEX_SMOKE_FULL) or the play fire/aim check (VORTEX_SMOKE_PLAYFIRE).</summary>
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
                        // Mouse buttons must reach the game's key state (Input.GetKey("LButton"/"RButton") = fire / aim).
                        double cx = ev.Bounds.Width / 2, cy = ev.Bounds.Height / 2;
                        VortexEditor.Viewport.MacViewProbe.MouseButton(ev.NativeHandle, cx, cy, false, true);
                        DispatcherTimer.RunOnce(() =>
                        {
                            bool lDown = VortexEditor.Viewport.EngineViewport.IsVirtualKeyDown(0x01) && Editor.Core.Input.HostInput.IsKeyDown(0x01);
                            VortexEditor.Viewport.MacViewProbe.MouseButton(ev.NativeHandle, cx, cy, false, false);
                            VortexEditor.Viewport.MacViewProbe.MouseButton(ev.NativeHandle, cx, cy, true, true);
                            DispatcherTimer.RunOnce(() =>
                            {
                                bool lUp = !Editor.Core.Input.HostInput.IsKeyDown(0x01);
                                bool rDown = Editor.Core.Input.HostInput.IsKeyDown(0x02);
                                VortexEditor.Viewport.MacViewProbe.MouseButton(ev.NativeHandle, cx, cy, true, false);
                                DispatcherTimer.RunOnce(() =>
                                {
                                    bool rUp = !Editor.Core.Input.HostInput.IsKeyDown(0x02);
                                    string r = "LButton down=" + lDown + " up=" + lUp + ", RButton down=" + rDown + " up=" + rUp;
                                    if (lDown && lUp && rDown && rUp) log.Log("SMOKE OK   mouse buttons reach Input.GetKey (" + r + ")");
                                    else log.LogError("SMOKE FAIL mouse buttons do not reach Input.GetKey (" + r + ")");
                                    if (System.Environment.GetEnvironmentVariable("VORTEX_SMOKE_PLAYFIRE") == "1") SmokePlayFire(ev);
                                    else if (System.Environment.GetEnvironmentVariable("VORTEX_SMOKE_FULL") == "1") SmokeFull(scene);
                                }, TimeSpan.FromMilliseconds(200));
                            }, TimeSpan.FromMilliseconds(200));
                        }, TimeSpan.FromMilliseconds(200));
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
            // self-registered checks of every editor window / panel (SmokeRegistry)
            _ = SmokeRegistry.RunAll();
        }

        private void SmokeCapture(string dir)
        {
            // registered window checks may still be running (they open windows, render previews): wait for them
            if (SmokeRegistry.Running) { DispatcherTimer.RunOnce(() => SmokeCapture(dir), TimeSpan.FromMilliseconds(500)); return; }
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
            CloseConfirmed();
        }

        /// <summary>Close the editor without asking again (the caller already confirmed).</summary>
        public void CloseConfirmed()
        {
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
            NativeMenu.SetMenu(this, EditorMenus.Build(this));
            if (!OperatingSystem.IsMacOS()) MenuBarHost.IsVisible = true;
        }

        private ContextMenu BuildContextMenu()
        {
            var m = new ContextMenu();
            var a = new MenuItem { Header = "Build (export only)…" }; a.Click += (s, e) => EditorCommands.Build();
            var b = new MenuItem { Header = "Build & Run…" }; b.Click += (s, e) => EditorCommands.BuildAndRun();
            var c = new MenuItem { Header = "Build Settings / Target…" }; c.Click += (s, e) => EditorCommands.Build();
            m.Items.Add(a); m.Items.Add(b); m.Items.Add(new Separator()); m.Items.Add(c);
            return m;
        }

        // ---------------------------------------------------------------- keyboard
        /// <summary>Window-level shortcuts. On macOS the ⌘ shortcuts arrive through the native menu (it consumes them
        /// before this handler), so here: the ⌘/Ctrl shortcuts for the other platforms and the plain-key shortcuts
        /// (W/E/R/X/G/F/Home, ⌫/Delete, F2, Esc) — never while typing in a text field, flying the camera or playing.</summary>
        private void OnGlobalKeyDown(object sender, KeyEventArgs e)
        {
            var m = e.KeyModifiers;
            bool mac = OperatingSystem.IsMacOS();
            bool cmd = mac ? m.HasFlag(KeyModifiers.Meta) : m.HasFlag(KeyModifiers.Control);
            bool shift = m.HasFlag(KeyModifiers.Shift);
            if (cmd) { if (HandleCommandKey(e.Key, shift)) e.Handled = true; return; }
            if (m.HasFlag(KeyModifiers.Alt) || m.HasFlag(KeyModifiers.Control) || m.HasFlag(KeyModifiers.Meta)) return;
            if (HandlePlainKey(e.Key, shift)) e.Handled = true;
        }

        /// <summary>⌘/Ctrl shortcut (the same table as the menu gestures). Returns true when handled.</summary>
        internal bool HandleCommandKey(Key key, bool shift)
        {
            if (EditorCommands.FocusedTextBox() != null && (key == Key.C || key == Key.V || key == Key.X || key == Key.A || key == Key.Z || key == Key.Y || key == Key.Back || key == Key.Delete || key == Key.D))
                return false;   // the text field handles its own editing keys natively
            switch (key)
            {
                case Key.S: if (shift) EditorCommands.SaveAll(); else EditorCommands.SaveProject(); return true;
                case Key.Z: EditorCommands.EditCommand(shift ? EditorCommands.EditAction.Redo : EditorCommands.EditAction.Undo); return true;
                case Key.Y: EditorCommands.EditCommand(EditorCommands.EditAction.Redo); return true;
                case Key.X: EditorCommands.EditCommand(EditorCommands.EditAction.Cut); return true;
                case Key.C: EditorCommands.EditCommand(EditorCommands.EditAction.Copy); return true;
                case Key.V: EditorCommands.EditCommand(EditorCommands.EditAction.Paste); return true;
                case Key.D: EditorCommands.EditCommand(EditorCommands.EditAction.Duplicate); return true;
                case Key.A: EditorCommands.EditCommand(EditorCommands.EditAction.SelectAll); return true;
                case Key.Back: case Key.Delete: EditorCommands.EditCommand(EditorCommands.EditAction.Delete); return true;
                case Key.P: if (shift) EditorCommands.Pause(); else EditorCommands.TogglePlay(); return true;
                case Key.B: EditorCommands.Build(); return true;
                case Key.R: EditorCommands.BuildAndRun(); return true;
                case Key.N: if (shift) EditorCommands.CreateEmpty(); else EditorCommands.NewScene(); return true;
                case Key.O: if (shift) _ = EditorCommands.OpenScene(); else EditorCommands.OpenProject(); return true;
                case Key.F: EditorCommands.Find(); return true;
                case Key.I: _ = EditorCommands.ImportAsset(); return true;
                case Key.H: if (shift) { EditorCommands.History(); return true; } return false;
                case Key.OemComma: EditorCommands.ProjectSettings(); return true;
                case Key.D1: TogglePanel(PanelHierarchy); return true;
                case Key.D2: TogglePanel(PanelFiles); return true;
                case Key.D3: TogglePanel(PanelInspector); return true;
                case Key.D4: TogglePanel(PanelProject); return true;
                case Key.D5: TogglePanel(PanelConsole); return true;
                case Key.D6: TogglePanel(PanelEnvironment); return true;
            }
            return false;
        }

        /// <summary>Plain-key shortcut (no modifier). Returns true when handled.</summary>
        internal bool HandlePlainKey(Key key, bool shift)
        {
            if (!Session.HasProject) return false;
            if (EditorCommands.FocusedTextBox() != null) return false;                      // typing
            if (PlayModeService.Instance.IsPlaying) return false;                            // game input
            var vs = Editor.Core.Viewport.EditorViewportSession.Main;
            if (vs != null && (vs.IsFlyMode || vs.IsViewingThroughGameCamera)) return false; // WASD/QE fly the camera
            switch (key)
            {
                case Key.W: EditorCommands.MoveTool(); return true;
                case Key.E: EditorCommands.RotateTool(); return true;
                case Key.R: EditorCommands.ScaleTool(); return true;
                case Key.X: EditorCommands.ToggleGizmoSpace(); return true;
                case Key.G: EditorCommands.ToggleGrid(); return true;
                case Key.F:
                    EditorCommands.FocusSelected();
                    var sel = SelectionService.Instance.SelectedEntity;
                    if (sel != null) Hierarchy.Reveal(sel);
                    return true;
                case Key.Home: EditorCommands.ResetCamera(); return true;
                case Key.F2: if (SelectionService.Instance.SelectedEntity != null) { EditorCommands.Rename(); return true; } return false;
                case Key.Back:
                case Key.Delete:
                    if (!SceneKeyContext()) return false;
                    EditorCommands.EditCommand(EditorCommands.EditAction.Delete);
                    return true;
                case Key.Escape:
                    if (!SceneKeyContext()) return false;
                    Session.Hierarchy.ClearSelection();
                    SelectionService.Instance.ClearSelection();
                    return true;
            }
            return false;
        }

        /// <summary>Delete / Esc act on the scene only while focus is in the hierarchy, the 3D view or nowhere —
        /// other panels (Asset Browser, lists) keep those keys.</summary>
        private bool SceneKeyContext()
        {
            var f = EditorCommands.FocusedElement();
            if (f == null || ReferenceEquals(f, this)) return true;
            if (f is VortexEditor.Viewport.EngineViewport) return true;
            return EditorCommands.FocusWithin(Hierarchy) || EditorCommands.FocusWithin(ViewportPanel);
        }

        // ---------------------------------------------------------------- toolbar
        private void OnSceneTab(object s, RoutedEventArgs e) { if (PlayModeService.Instance.IsPlaying) EditorCommands.Stop(); PlayModeService.Instance.SetGameView(false); }
        private void OnGameTab(object s, RoutedEventArgs e) => PlayModeService.Instance.SetGameView(true);
        private void OnScriptClick(object s, RoutedEventArgs e) => EditorCommands.OpenScriptsProject();
        private void OnPlay(object s, RoutedEventArgs e)
        {
            if (!Session.HasProject) { ShowToast("Open a project first"); return; }
            if (PlayModeService.Instance.State == PlayState.Playing) EditorCommands.Stop();
            else
            {
                // ▶ plays in the viewport and switches to the Game view (WPF parity); ▶ while paused resumes.
                if (PlayModeService.Instance.State == PlayState.Editing) { PlayModeService.Instance.IsExternalWindow = false; PlayModeService.Instance.SetGameView(true); }
                EditorCommands.Play();
            }
        }
        private void OnPause(object s, RoutedEventArgs e) => EditorCommands.Pause();
        private void OnStop(object s, RoutedEventArgs e) => EditorCommands.Stop();
        private void OnPlayMenu(object s, RoutedEventArgs e)
        {
            var m = new MenuFlyout();
            var a = new MenuItem { Header = "Play in Viewport" }; a.Click += (x, y) => { if (PlayModeService.Instance.IsPlaying) EditorCommands.Stop(); PlayModeService.Instance.IsExternalWindow = false; PlayModeService.Instance.SetGameView(true); EditorCommands.Play(); };
            var b = new MenuItem { Header = "Play in Standalone Player" }; b.Click += (x, y) => EditorCommands.PlayInNewWindow();
            var c = new MenuItem { Header = "Game View (without playing)" }; c.Click += (x, y) => PlayModeService.Instance.SetGameView(true);
            var d = new MenuItem { Header = "Release Mode (hide the play banner)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = PlayModeService.Instance.IsReleaseMode }; d.Click += (x, y) => EditorCommands.ToggleReleaseMode();
            m.Items.Add(a); m.Items.Add(b); m.Items.Add(new Separator()); m.Items.Add(c); m.Items.Add(d);
            m.ShowAt(PlayMenuButton);
        }
        private void OnBuildClick(object s, RoutedEventArgs e) => EditorCommands.Build();
        private void OnGitClick(object s, RoutedEventArgs e) => EditorCommands.GitWindow();
        private void OnHistoryClick(object s, RoutedEventArgs e) => EditorCommands.History();
        private void OnSettingsClick(object s, RoutedEventArgs e) => EditorCommands.ProjectSettings();

        private void SyncPlayButtons()
        {
            var st = PlayModeService.Instance.State;
            PlayIcon.Icon = st == PlayState.Playing ? "Stop" : "Play";
            PlayIcon.Foreground = (Avalonia.Media.IBrush)this.FindResource(st == PlayState.Playing ? "VxRedBrush" : "VxGreenBrush");
            PauseIcon.Icon = st == PlayState.Paused ? "Play" : "Pause";
            PauseIcon.Foreground = st == PlayState.Paused ? (Avalonia.Media.IBrush)this.FindResource("VxAccentBrush") : (Avalonia.Media.IBrush)this.FindResource("VxTextBrush");
            StopButton.IsEnabled = st != PlayState.Editing;
            PauseButton.IsEnabled = st != PlayState.Editing;
            ToolTip.SetTip(PlayButton, st == PlayState.Playing ? "Stop — back to the build view (⌘P)" : "Play (⌘P)");
            ToolTip.SetTip(PauseButton, st == PlayState.Paused ? "Resume (⇧⌘P)" : "Pause (⇧⌘P)");
        }

        private void SyncUndoText()
        {
            var u = UndoRedoManager.Instance;
            UndoText.Text = u.CanUndo ? "Undo: " + u.UndoName + (u.UndoCount > 1 ? "  (+" + (u.UndoCount - 1) + ")" : "") : "";
            UndoStatus.IsVisible = u.CanUndo;
        }

        // ---------------------------------------------------------------- console badge
        private bool IsConsoleShowing => IsPanelVisible(PanelConsole) && ReferenceEquals(BottomTabs.SelectedItem, ConsoleTab);
        private void OnConsoleEntry()
        {
            var c = ConsoleService.Instance;
            if (c.Entries.Count == 0) { ClearConsoleBadge(); return; }
            var last = c.Entries[c.Entries.Count - 1];
            if (last.Level != LogLevel.Error || IsConsoleShowing) return;
            _unseenErrors++;
            ConsoleErrorCount.Text = _unseenErrors > 99 ? "99+" : _unseenErrors.ToString();
            ConsoleErrorBadge.IsVisible = true;
        }
        private void ClearConsoleBadge() { _unseenErrors = 0; ConsoleErrorBadge.IsVisible = false; }

        // ---------------------------------------------------------------- panels
        public const string PanelHierarchy = "Hierarchy", PanelFiles = "Files", PanelInspector = "Inspector", PanelEnvironment = "Environment", PanelProject = "Project", PanelConsole = "Console";
        private readonly Dictionary<string, bool> _panels = new Dictionary<string, bool>
        {
            [PanelHierarchy] = true, [PanelFiles] = true, [PanelInspector] = true, [PanelEnvironment] = true, [PanelProject] = true, [PanelConsole] = true,
        };
        private GridLength _leftWidth = new GridLength(260), _rightWidth = new GridLength(330), _bottomHeight = new GridLength(300);

        public bool IsPanelVisible(string name) => _panels.TryGetValue(name, out bool v) && v;

        /// <summary>Window ▸ panel: hidden → show and bring to front; showing but behind another tab → bring to
        /// front; in front → hide (WPF: the panel check items).</summary>
        public void TogglePanel(string name, bool forceShow = false)
        {
            if (!_panels.ContainsKey(name)) return;
            bool visible = _panels[name];
            TabItem tab = TabFor(name, out TabControl tabs);
            bool inFront = tab == null || ReferenceEquals(tabs.SelectedItem, tab);
            if (!visible || forceShow) _panels[name] = true;
            else if (!inFront) { /* just bring it to the front */ }
            else _panels[name] = false;
            ApplyLayout();
            if (_panels[name] && tab != null) tabs.SelectedItem = tab;
            if (name == PanelConsole && _panels[name]) ClearConsoleBadge();
            if (name == PanelHierarchy && _panels[name] && forceShow) Hierarchy.FocusTree();
            EditorMenus.Refresh();
        }

        public void ShowPanel(string name) => TogglePanel(name, forceShow: true);

        private TabItem TabFor(string name, out TabControl tabs)
        {
            tabs = null;
            switch (name)
            {
                case PanelInspector: tabs = RightTabs; return InspectorTab;
                case PanelEnvironment: tabs = RightTabs; return EnvironmentTab;
                case PanelProject: tabs = BottomTabs; return ProjectTab;
                case PanelConsole: tabs = BottomTabs; return ConsoleTab;
            }
            return null;
        }

        public void ResetLayout()
        {
            foreach (var k in _panels.Keys.ToList()) _panels[k] = true;
            _leftWidth = new GridLength(260); _rightWidth = new GridLength(330); _bottomHeight = new GridLength(300);
            Workspace.ColumnDefinitions[0].Width = _leftWidth; Workspace.ColumnDefinitions[4].Width = _rightWidth; CenterColumn.RowDefinitions[2].Height = _bottomHeight;
            LeftColumn.RowDefinitions[0].Height = new GridLength(3, GridUnitType.Star); LeftColumn.RowDefinitions[2].Height = new GridLength(2, GridUnitType.Star);
            ApplyLayout();
            EditorMenus.Refresh();
        }

        private void ApplyLayout()
        {
            // remember sizes the user dragged before collapsing a column
            if (Workspace.ColumnDefinitions[0].Width.Value > 10) _leftWidth = Workspace.ColumnDefinitions[0].Width;
            if (Workspace.ColumnDefinitions[4].Width.Value > 10) _rightWidth = Workspace.ColumnDefinitions[4].Width;
            if (CenterColumn.RowDefinitions[2].Height.Value > 10) _bottomHeight = CenterColumn.RowDefinitions[2].Height;

            bool hier = _panels[PanelHierarchy], files = _panels[PanelFiles], left = hier || files;
            Workspace.ColumnDefinitions[0].Width = left ? _leftWidth : new GridLength(0);
            Workspace.ColumnDefinitions[1].Width = new GridLength(left ? 5 : 0);
            LeftColumn.RowDefinitions[0].Height = hier ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
            LeftColumn.RowDefinitions[1].Height = new GridLength(hier && files ? 5 : 0);
            LeftColumn.RowDefinitions[2].Height = files ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
            HierarchyHost.IsVisible = hier; FilesHost.IsVisible = files;

            ApplyTabs(RightTabs, (InspectorTab, _panels[PanelInspector]), (EnvironmentTab, _panels[PanelEnvironment]));
            bool right = _panels[PanelInspector] || _panels[PanelEnvironment];
            Workspace.ColumnDefinitions[4].Width = right ? _rightWidth : new GridLength(0);
            Workspace.ColumnDefinitions[3].Width = new GridLength(right ? 5 : 0);
            RightColumn.IsVisible = right;

            ApplyTabs(BottomTabs, (ProjectTab, _panels[PanelProject]), (ConsoleTab, _panels[PanelConsole]));
            bool bottom = _panels[PanelProject] || _panels[PanelConsole];
            CenterColumn.RowDefinitions[2].Height = bottom ? _bottomHeight : new GridLength(0);
            CenterColumn.RowDefinitions[1].Height = new GridLength(bottom ? 5 : 0);
            BottomDock.IsVisible = bottom;
        }

        private static void ApplyTabs(TabControl tabs, params (TabItem tab, bool visible)[] items)
        {
            foreach (var (tab, visible) in items) tab.IsVisible = visible;
            if (tabs.SelectedItem is TabItem sel && !sel.IsVisible)
            {
                var other = items.FirstOrDefault(i => i.visible).tab;
                if (other != null) tabs.SelectedItem = other;
            }
        }

        // ---------------------------------------------------------------- windows & dialogs
        public void ShowToast(string message) => Toast.Show(message);
        public string LastToast => Toast.LastMessage;
        public void FocusHierarchySearch() { ShowPanel(PanelHierarchy); Hierarchy.FocusSearch(); }
        public void RenameSelected() { var e = SelectionService.Instance.SelectedEntity; if (e != null) Hierarchy.BeginRename(e); }

        public void ShowProjectHub(bool createTab)
        {
            if (_hub != null) { _hub.Activate(); _hub.SelectTab(createTab); return; }
            _hub = new ProjectHubWindow(createTab);
            _hub.Closed += (s, e) => { _hub = null; if (!Session.HasProject && !_closingConfirmed) { _closingConfirmed = true; Close(); } };
            _hub.Show(this);
        }

        /// <summary>The open project hub (smoke checks capture and close it); null when none is showing.</summary>
        internal ProjectHubWindow Hub => _hub;

        public void OpenProjectSettings() { if (Session.HasProject) new ProjectSettingsWindow().ShowDialog(this); else ShowToast("Open a project first"); }
        public void OpenAbout() => new AboutWindow().ShowDialog(this);
        public void OpenAudioMixer() { if (Session.HasProject) EditorWindows.AudioMixer(); else ShowToast("Open a project first"); }
        public void OpenGit() => EditorCommands.GitWindow();
        public void OpenBuildDialog(bool runAfter) { if (Session.HasProject) new BuildWindow(runAfter).ShowDialog(this); else ShowToast("Open a project first"); }
        // Kept for the panels / editors that open these through the main window.
        public void OpenMaterialEditor(string vmatPath) => EditorWindows.MaterialEditor(vmatPath);
        public void OpenCollisionEditor(GameEntity e) { if (e != null) { SelectionService.Instance.Select(e); EditorWindows.CollisionEditor(e); } }
        public void OpenSocketEditor(GameEntity e) { if (e != null) { SelectionService.Instance.Select(e); EditorWindows.SocketEditor(e); } }
        public void OpenSoundContainerEditor(string path) => EditorWindows.SoundContainerEditor(path);
        public void OpenUiEditor(string path) => EditorWindows.UiEditor(path);
        public void OpenAnimationEditor(string path) => EditorWindows.AnimationEditor(path);

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
