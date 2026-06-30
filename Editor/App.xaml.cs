using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Editor.DllWrapper;

namespace Editor
{
    /// <summary>
    /// Interaktionslogik für "App.xaml"
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Standalone PLAYER mode: a 'player.vortex' marker next to the exe (written by Export Game)
            // or a --play arg boots straight into the game (no editor UI).
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            bool playerMode = (e.Args != null && System.Array.IndexOf(e.Args, "--play") >= 0)
                              || System.IO.File.Exists(System.IO.Path.Combine(exeDir, "player.vortex"));

            // Rendering STRESS-TEST mode: launched as a separate process by the editor with
            //   --stress="<abs model path>" --count=<N>
            // Boots straight into the native GameHost (uncapped FPS) rendering N instanced copies of the model
            // with a free-fly camera + on-screen stats — the real render path, NOT the 60fps editor viewport.
            if (e.Args != null)
            {
                foreach (var a in e.Args)
                {
                    if (a.StartsWith("--stress=", StringComparison.OrdinalIgnoreCase))
                        _stressModelArg = a.Substring("--stress=".Length).Trim('"');
                    else if (a.StartsWith("--count=", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(a.Substring("--count=".Length), out _stressCountArg);
                    else if (a.StartsWith("--benchmark=", StringComparison.OrdinalIgnoreCase))
                        _benchmarkDirArg = a.Substring("--benchmark=".Length).Trim('"');
                    else if (a.StartsWith("--vuitest=", StringComparison.OrdinalIgnoreCase))
                        _vuiTestArg = a.Substring("--vuitest=".Length).Trim('"');   // dev hook: load + drive a .vui in the player
                    else if (a.StartsWith("--project=", StringComparison.OrdinalIgnoreCase))
                        _projectArg = a.Substring("--project=".Length).Trim('"');   // dev: play a project's active scene from disk
                    else if (a.StartsWith("--scene=", StringComparison.OrdinalIgnoreCase))
                        _sceneArg = a.Substring("--scene=".Length).Trim('"');       // dev: force which scene to play
                    else if (a.StartsWith("--renderscale=", StringComparison.OrdinalIgnoreCase))
                        float.TryParse(a.Substring("--renderscale=".Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _renderScaleArg); // dev: force render-scale
                    else if (a.StartsWith("--dlss=", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(a.Substring("--dlss=".Length), out _dlssArg); // dev: force DLSS mode (0..4)
                    else if (a.StartsWith("--fg=", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(a.Substring("--fg=".Length), out _fgArg); // dev: force Frame-Gen mode (0..3 = off/x2/x3/x4)
                }
            }
            if (!string.IsNullOrEmpty(_projectArg)) playerMode = true;
            _stressMode = !string.IsNullOrEmpty(_stressModelArg) || !string.IsNullOrEmpty(_benchmarkDirArg);

            // Show the branded splash immediately. It's topmost, so it covers the (blocking) engine init,
            // the empty editor shell, and the project browser opening underneath — then fades to reveal them.
            var splash = new SplashWindow();
            splash.Show();

            if (_stressMode)
            {
                string logDir = exeDir;
                AppDomain.CurrentDomain.UnhandledException += (s, ev) => LogPlayerError(logDir, "AppDomain", ev.ExceptionObject as Exception);
                DispatcherUnhandledException += (s, ev) => LogPlayerError(logDir, "Dispatcher", ev.Exception);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => BootStressPlayer(exeDir, splash)));
                return;
            }

            if (playerMode)
            {
                string logDir = exeDir;
                AppDomain.CurrentDomain.UnhandledException += (s, ev) => LogPlayerError(logDir, "AppDomain", ev.ExceptionObject as Exception);
                DispatcherUnhandledException += (s, ev) => LogPlayerError(logDir, "Dispatcher", ev.Exception);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => BootPlayer(exeDir, splash)));
                return;
            }

            // Defer the heavy work to Background priority so the splash paints + animates first.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                DllWrapper.VortexAPI.InitEngineRuntime();

                var main = new MainWindow();
                MainWindow = main;
                main.Show(); // -> MainWindow.Loaded loads the last project or opens the project browser

                // Keep the brand visible briefly after the workspace is up, then fade the splash out.
                var hold = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(850)
                };
                hold.Tick += (s, ev) => { hold.Stop(); splash.FadeOutAndClose(); };
                hold.Start();
            }));
        }

        /// <summary>Boots the bundled game with NO editor UI: load project from the exe folder, activate the
        /// startup scene, and show only the GameWindow in play mode. The GameWindow owns the whole game loop
        /// (step engine + run scripts + submit scene + render) since there's no editor tick behind it.</summary>
        private void BootPlayer(string exeDir, SplashWindow splash)
        {
            try
            {
                splash.SetProgress(0.10, "Starting engine…");
                DllWrapper.VortexAPI.InitEngineRuntime();
                Editor.Core.Services.PlayModeService.Instance.IsReleaseMode = true;       // shipped game: no dev banner
                try { Editor.Core.Services.EditorViewportService.Instance.AreGizmosVisible = false; } catch { } // no editor gizmos/icons in the game

                // Mount the binary asset pak INTO RAM (assets + manifest + compiled scripts, decompressed) so
                // everything loads from memory — nothing readable/editable on disk. The renamed game exe keeps
                // the editor assembly's identity, so resolve the gameplay DLL's reference back to it.
                AppDomain.CurrentDomain.AssemblyResolve += PlayerAssemblyResolve;
                var pak = System.IO.Path.Combine(exeDir, "Assets.vpak");
                if (System.IO.File.Exists(pak))
                {
                    splash.SetProgress(0.30, "Loading assets…");
                    Editor.Core.Services.AssetVfs.Mount(pak);
                    System.Diagnostics.Debug.WriteLine("[Player] mounted pak: " + Editor.Core.Services.AssetVfs.FileCount + " files");
                    if (Editor.Core.Services.AssetVfs.TryGetBytes("GameScripts.dll", out var dllBytes) && dllBytes.Length > 0)
                    {
                        try { Editor.Scripting.ScriptRuntime.Instance.PrecompiledAssembly = System.Reflection.Assembly.Load(dllBytes); }
                        catch (Exception sx) { System.Diagnostics.Debug.WriteLine("[Player] scripts DLL load failed: " + sx.Message); }
                    }
                }

                // --project: play a project straight from disk (dev/testing) instead of the exe-dir export layout.
                string projDir = !string.IsNullOrEmpty(_projectArg) ? _projectArg : exeDir;
                if (!string.IsNullOrEmpty(_projectArg)) Editor.Core.Services.PlayModeService.Instance.IsReleaseMode = false;
                splash.SetProgress(0.45, "Loading project…");
                var project = Editor.Core.Services.ProjectService.Instance.LoadProjectFromPath(projDir);

                // --scene: force a specific scene (e.g. the Lobby) instead of the project's saved active scene.
                if (project != null && !string.IsNullOrEmpty(_sceneArg) && project.Scenes != null)
                {
                    foreach (var s in project.Scenes)
                        if (s != null && string.Equals(s.Name, _sceneArg, StringComparison.OrdinalIgnoreCase)) { project.ActiveScene = s; break; }
                }

                // A hidden window holds the project as DataContext so ProjectData.Current resolves
                // (Current reads Application.MainWindow.DataContext). The VISIBLE game is the native GameHost
                // window below — not a WPF window — which is what kills the render-thread Present-freeze.
                var holder = new System.Windows.Window { DataContext = project, Width = 0, Height = 0,
                    WindowStyle = System.Windows.WindowStyle.None, ShowInTaskbar = false,
                    ShowActivated = false, Visibility = System.Windows.Visibility.Hidden };
                MainWindow = holder;

                var scene = project != null ? project.ActiveScene : null;
                if (scene != null)
                {
                    splash.SetProgress(0.62, "Loading scene…");
                    scene.Load();
                    scene.ActivateEntities();
                    scene.IsActive = true;
                    splash.SetProgress(0.80, "Preloading assets…");
                    Editor.Core.Services.SceneRenderService.Instance.PreloadSceneAssets(scene);
                }

                var cam = Editor.Core.Services.CameraService.Instance.GetMainCamera();
                if (cam.IsValid) Editor.Core.Services.CameraService.Instance.SetActiveCamera(cam);

                Editor.Core.Services.PlayModeService.Instance.IsExternalWindow = true;
                Editor.Core.Services.PlayModeService.Instance.SetGameView(true);
                Editor.Core.Services.PlayModeService.Instance.Play();
                if (scene != null) Editor.Scripting.ScriptRuntime.Instance.Begin(scene);

                // Keep the splash up — DON'T fade here. The native window is created hidden and revealed only once
                // its first frame is rendered; GameHostTick closes this splash right after, so there's no black flash.
                splash.SetProgress(0.95, "Starting renderer…");
                _bootSplash = splash;

                // Native GameHost: its own native window + DX12 swapchain + uncapped one-thread loop. Each frame
                // it calls GameHostTick (scripts + camera + submit) then renders + presents. Blocks until close.
                _ghTick = GameHostTick;                                   // keep the delegate alive (GC)
                DllWrapper.VortexAPI.SetGameTickCallback(_ghTick);
                if (_renderScaleArg > 0f && _renderScaleArg < 0.999f)     // dev: --renderscale=<f> (the scaled path)
                    try { DllWrapper.VortexAPI.SetRenderScale(_renderScaleArg); } catch { }
                if (_dlssArg > 0)                                          // dev: --dlss=<0..4> (force a DLSS mode)
                    try { DllWrapper.VortexAPI.SetDlssMode(_dlssArg); } catch { }
                Editor.Core.Services.PlayModeService.Instance.NativeGameHostRunning = true; // this thread is now in the native loop
                DllWrapper.VortexAPI.RunGameHost(1280, 720, "Vortex");    // BLOCKS — runs the game
                Editor.Core.Services.PlayModeService.Instance.NativeGameHostRunning = false;
                Shutdown();                                                // window closed (or QuitGame->RequestGameHostExit) -> exit
            }
            catch (Exception ex)
            {
                LogPlayerError(exeDir, "BootPlayer", ex);
                try { splash.FadeOutAndClose(); } catch { }
                MessageBox.Show("Player failed to start: " + ex.Message, "Vortex Player", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        // ===== Rendering stress-test player (separate process, native GameHost, uncapped) =====
        private static bool _stressMode;
        private static string _stressModelArg;
        private static string _benchmarkDirArg;   // --benchmark="<assets dir>": generated multi-model scene
        private static string _vuiTestArg;         // --vuitest="<.vui>": dev hook to render a retained-UI screen
        private static string _projectArg;         // --project="<dir>": dev hook to play a project's active scene from disk
        private static string _sceneArg;           // --scene="<name>": dev hook to force which scene plays
        private static float  _renderScaleArg = 1f; // --renderscale=<f>: dev hook to force render-scale (verify the scaled path)
        private static int    _dlssArg = 0;         // --dlss=<0..4>: dev hook to force a DLSS mode (verify the eval path)
        private static int    _fgArg = 0;           // --fg=<0..3>: dev hook to force DLSS Frame-Gen (verify the present hook)
        private SplashWindow  _bootSplash;          // kept up until the game's first frame is on screen, then closed (no black flash)
        private int           _bootFrames;          // GameHostTick counter, used to time the splash close
        private static Vortex.VuiHandle _vuiTestHandle; private static bool _vuiPrevDown;
        private static int _stressCountArg = 1000;
        private static bool _stressInit;
        private static float _scx, _scy, _scz, _syaw, _spitch;   // free-fly camera state
        private static int _stressFrames; private static DateTime _stressT0 = DateTime.MinValue;
        private static bool _stressFree, _stressEscPrev;          // ESC frees the mouse so the window can be closed

        /// <summary>Boots ONLY the native GameHost rendering a stress crowd of the given model — no project, no
        /// vpak. The crowd + free-fly camera + stats overlay are driven by StressTick (via GameHostTick).</summary>
        private void BootStressPlayer(string exeDir, SplashWindow splash)
        {
            try
            {
                splash.SetStatus("Starting stress test…");
                DllWrapper.VortexAPI.InitEngineRuntime();
                try { Editor.Core.Services.EditorViewportService.Instance.AreGizmosVisible = false; } catch { }
                splash.FadeOutAndClose();
                _ghTick = GameHostTick;
                DllWrapper.VortexAPI.SetGameTickCallback(_ghTick);
                Editor.Core.Services.PlayModeService.Instance.NativeGameHostRunning = true;
                DllWrapper.VortexAPI.RunGameHost(1280, 720, "Vortex Stress Test"); // BLOCKS
                Editor.Core.Services.PlayModeService.Instance.NativeGameHostRunning = false;
                Shutdown();
            }
            catch (Exception ex)
            {
                LogPlayerError(exeDir, "BootStressPlayer", ex);
                try { splash.FadeOutAndClose(); } catch { }
                Shutdown();
            }
        }

        /// <summary>One GameHost frame in stress mode: free-fly camera + submit the instanced crowd + stats HUD.</summary>
        private void StressTick(float dt)
        {
            try
            {
                int cw = DllWrapper.VortexAPI.GameHostClientWidth(); if (cw < 1) cw = 1;
                int ch = DllWrapper.VortexAPI.GameHostClientHeight(); if (ch < 1) ch = 1;

                if (!_stressInit)
                {
                    _stressInit = true;
                    try { DllWrapper.VortexAPI.ShowGrid(false); DllWrapper.VortexAPI.ShowGizmos(false); } catch { }
                    // Force the multithreaded cull+pack ON — at hundreds of thousands of instances the per-instance
                    // frustum test + instance-VB pack is real CPU work; parallelizing it lifts the frame rate.
                    try { DllWrapper.VortexAPI.Multithreading(true); DllWrapper.VortexAPI.MultithreadingForce(true); } catch { }
                    // GEOMETRIC LOD: the WHOLE crowd stays visible + intact (no holes); distant copies draw a
                    // decimated low-poly mesh (full detail < 60u, LOD1 60-130u, LOD2/3 beyond). Far fewer verts at
                    // full visibility — the proper fix for the "broken far field".
                    try { DllWrapper.VortexAPI.Lod(false, 0f, 0f); DllWrapper.VortexAPI.RenderDistance(0f); DllWrapper.VortexAPI.GeometricLod(true, 45f, 110f); } catch { }
                    // Bright lighting (the GameHost main path uploads these correctly).
                    DllWrapper.VortexAPI.ClearAllLights();
                    DllWrapper.VortexAPI.SetAmbientLightStrength(0.55f);
                    DllWrapper.VortexAPI.SetDirectionalLightParams(-0.4f, -0.6f, -0.6f, 1f, 1f, 0.97f, 3.5f);
                    if (!string.IsNullOrEmpty(_benchmarkDirArg))
                    {
                        // BENCHMARK SCENE: many DIFFERENT models, each spawned <count> times, spread near→far with
                        // varied scale/rotation. Exercises instancing (per model), geometric LOD (all distances)
                        // and frustum culling together. Enumerate distinct model files under the assets dir.
                        var paths = new System.Collections.Generic.List<string>();
                        try
                        {
                            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            string[] exts = { "*.glb", "*.gltf", "*.obj", "*.fbx" };
                            foreach (var ext in exts)
                                foreach (var f in System.IO.Directory.EnumerateFiles(_benchmarkDirArg, ext, System.IO.SearchOption.AllDirectories))
                                {
                                    string key = System.IO.Path.GetFileNameWithoutExtension(f);
                                    if (seen.Add(key)) paths.Add(f);
                                    if (paths.Count >= 8) break;
                                }
                        }
                        catch { }
                        int perModel = _stressCountArg > 0 ? _stressCountArg : 1500;
                        Editor.Core.Services.StressTestService.StartBenchmark(paths, perModel);
                        // Sit back + elevated so both the near cluster and the far field are in view.
                        _scx = 0f; _scy = 25f; _scz = -45f;
                        _syaw = 0f; _spitch = 0.22f;
                    }
                    else
                    {
                        // Single-model crowd (imports the model from disk + lays out the grid).
                        Editor.Core.Services.StressTestService.Start(_stressModelArg, _stressCountArg);
                        // Start INSIDE the crowd (slightly elevated) so the field of copies surrounds you.
                        _scx = 0f; _scy = 15f; _scz = 0f;
                        _syaw = 0f; _spitch = 0.15f;
                    }
                }

                // ESC toggles the mouse free so you can close/Alt-Tab the window (mouse-look hides+clips the cursor).
                bool esc = DllWrapper.VortexAPI.GameHostKeyDown(0x1B);
                if (esc && !_stressEscPrev) _stressFree = !_stressFree;
                _stressEscPrev = esc;

                // Free-fly camera (WASD + QE + mouse-look; Shift = faster) — only while the mouse is captured.
                DllWrapper.VortexAPI.SetGameHostMouseCaptured(!_stressFree);
                double cyp = Math.Cos(_spitch), syp = Math.Sin(_spitch), cya = Math.Cos(_syaw), sya = Math.Sin(_syaw);
                float fx = (float)(sya * cyp), fy = (float)(-syp), fz = (float)(cya * cyp);
                if (!_stressFree)
                {
                    float ddx = DllWrapper.VortexAPI.GameHostMouseDX(), ddy = DllWrapper.VortexAPI.GameHostMouseDY();
                    if (ddx > 200f) ddx = 200f; else if (ddx < -200f) ddx = -200f;
                    if (ddy > 200f) ddy = 200f; else if (ddy < -200f) ddy = -200f;
                    _syaw += ddx * 0.0035f; _spitch += ddy * 0.0035f;
                    if (_spitch > 1.5f) _spitch = 1.5f; else if (_spitch < -1.5f) _spitch = -1.5f;
                    cyp = Math.Cos(_spitch); syp = Math.Sin(_spitch); cya = Math.Cos(_syaw); sya = Math.Sin(_syaw);
                    fx = (float)(sya * cyp); fy = (float)(-syp); fz = (float)(cya * cyp);
                    float rx = (float)cya, rz = (float)(-sya);
                    float speed = (DllWrapper.VortexAPI.GameHostKeyDown(0x10) ? 90f : 28f) * dt;
                    if (DllWrapper.VortexAPI.GameHostKeyDown(0x57)) { _scx += fx * speed; _scy += fy * speed; _scz += fz * speed; } // W
                    if (DllWrapper.VortexAPI.GameHostKeyDown(0x53)) { _scx -= fx * speed; _scy -= fy * speed; _scz -= fz * speed; } // S
                    if (DllWrapper.VortexAPI.GameHostKeyDown(0x44)) { _scx += rx * speed; _scz += rz * speed; }                     // D
                    if (DllWrapper.VortexAPI.GameHostKeyDown(0x41)) { _scx -= rx * speed; _scz -= rz * speed; }                     // A
                    if (DllWrapper.VortexAPI.GameHostKeyDown(0x45)) _scy += speed;                                                  // E up
                    if (DllWrapper.VortexAPI.GameHostKeyDown(0x51)) _scy -= speed;                                                  // Q down
                }
                DllWrapper.VortexAPI.SetViewCamera(_scx, _scy, _scz, _scx + fx, _scy + fy, _scz + fz, 0f, 1f, 0f);

                // Submit the instanced crowd once (and whenever it changes).
                if (Editor.Core.Services.StressTestService.Dirty)
                {
                    Editor.Core.Services.StressTestService.Submit();
                    Editor.Core.Services.StressTestService.ClearDirty();
                }

                // Stats HUD.
                DllWrapper.VortexAPI.UIBegin(cw, ch);
                DllWrapper.VortexAPI.UIText(16, 14, 700, 32, "STRESS TEST  ·  " + Editor.Core.Services.StressTestService.ModelName + "  x" + Editor.Core.Services.StressTestService.Count.ToString("N0"), 20, 1f, 1f, 1f, 1f, 0, 700);
                DllWrapper.VortexAPI.UIText(16, 50, 800, 26, "FPS " + DllWrapper.VortexAPI.CurrentFPS + "    Draw calls " + DllWrapper.VortexAPI.DrawCalls.ToString("N0") + "    Instances " + DllWrapper.VortexAPI.InstancesDrawn.ToString("N0") + "    Verts " + DllWrapper.VortexAPI.VertexCount.ToString("N0"), 15, 0.8f, 0.86f, 0.92f, 1f, 0, 600);
                DllWrapper.VortexAPI.UIText(16, ch - 30, 800, 24, "WASD/QE fly  ·  Shift = faster  ·  mouse look  ·  F12 screenshot", 13, 0.6f, 0.6f, 0.66f, 1f, 0, 500);

                // Dev hook (--vuitest="<.vui>"): load a hand-written screen and tick the retained UI into this same
                // frame, so a .vui can be previewed in the real GameHost without the editor.
                if (_vuiTestArg != null)
                {
                    if (_vuiTestHandle == null) { _vuiTestHandle = Vortex.Gui.Load(_vuiTestArg); if (_vuiTestHandle != null && _vuiTestHandle.IsValid) _vuiTestHandle.Show(); }
                    if (_vuiTestHandle != null && _vuiTestHandle.IsValid)
                    {
                        float vmx = DllWrapper.VortexAPI.GameHostMouseX(), vmy = DllWrapper.VortexAPI.GameHostMouseY();
                        bool vdn = DllWrapper.VortexAPI.GameHostMouseDown();
                        bool vpr = vdn && !_vuiPrevDown; _vuiPrevDown = vdn;
                        Editor.UI.Vui.VuiStack.Instance.TickAll(cw, ch, BuildVuiInput(vmx, vmy, vdn, vpr));
                    }
                }

                // Per-second telemetry so the instancing scaling is verifiable from the log.
                _stressFrames++;
                if (_stressT0 == DateTime.MinValue) _stressT0 = DateTime.Now;
                var n = DateTime.Now;
                if ((n - _stressT0).TotalMilliseconds >= 1000)
                {
                    GHLog("STRESS " + Editor.Core.Services.StressTestService.ModelName + " x" + Editor.Core.Services.StressTestService.Count
                        + " FPS=" + DllWrapper.VortexAPI.CurrentFPS + " draws=" + DllWrapper.VortexAPI.DrawCalls
                        + " inst=" + DllWrapper.VortexAPI.InstancesDrawn + "/" + DllWrapper.VortexAPI.InstancesTested
                        + " mt=" + (DllWrapper.VortexAPI.MultithreadingActive ? 1 : 0));
                    _stressFrames = 0; _stressT0 = n;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[StressTick] " + ex); }
        }

        // Kept alive for the lifetime of the native callback (else the GC collects the thunk).
        private static DllWrapper.VortexAPI.GameTickDelegate _ghTick;
        private static object _ghSubmitted;
        private static bool _ghLmbPrev;
        private static float _ghPrevMx, _ghPrevMy;

        /// <summary>Called by the native GameHost loop each frame (on this thread): advance the game one frame
        /// — feed input/UI, step engine + scripts, apply the camera, submit the scene. The host renders +
        /// presents right after.</summary>
        private static readonly string _ghLog = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vortex_gamehost.log");
        private static void GHLog(string m) { try { System.IO.File.AppendAllText(_ghLog, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + m + "\r\n"); } catch { } }
        private static int _ghFrames; private static DateTime _ghT0 = DateTime.MinValue;
        private static bool _ghInit;
        private static bool _ghF12Prev;
        private static object _ghSubmittedScene;   // submit-once guard: re-submit only when the active scene changes

        private void GameHostTick(float dt)
        {
            if (_stressMode) { StressTick(dt); return; }
            try
            {
                // Close the boot splash once the native window has revealed its first rendered frame (it shows after
                // render_frame of the very first tick), so the splash hands off to an already-rendered game — no black.
                _bootFrames++;
                if (_bootFrames >= 2 && _bootSplash != null) { try { _bootSplash.Close(); } catch { } _bootSplash = null; }

                if (!_ghInit) { _ghInit = true; try { DllWrapper.VortexAPI.ShowGrid(false); DllWrapper.VortexAPI.ShowGizmos(false); } catch { }
                    try { GHLog("GPU=" + DllWrapper.VortexAPI.GpuName() + " vendor=0x" + DllWrapper.VortexAPI.GpuVendorId().ToString("X4") + " dlssCapable=" + DllWrapper.VortexAPI.GpuSupportsDlss() + " renderScale=" + DllWrapper.VortexAPI.GetRenderScale()); } catch { }
                    if (_fgArg > 0) { try { DllWrapper.VortexAPI.SetFrameGenMode(_fgArg); GHLog("FrameGen forced x" + (_fgArg + 1)); } catch { } } } // dev: --fg=<0..3> (swapchain now exists)
                var sr = Editor.Scripting.ScriptRuntime.Instance;
                bool playing = Editor.Core.Services.PlayModeService.Instance.State == Editor.Core.Services.PlayState.Playing;

                int cw = DllWrapper.VortexAPI.GameHostClientWidth();
                int ch = DllWrapper.VortexAPI.GameHostClientHeight();
                if (cw < 1) cw = 1;
                if (ch < 1) ch = 1;
                float mx = DllWrapper.VortexAPI.GameHostMouseX();
                float my = DllWrapper.VortexAPI.GameHostMouseY();
                bool down = DllWrapper.VortexAPI.GameHostMouseDown();
                bool pressed = down && !_ghLmbPrev; _ghLmbPrev = down;

                // F12 = screenshot: write the actual rendered back buffer to a BMP (reliable on the
                // flip-model swapchain, unlike GDI window capture). Edge-triggered.
                bool f12 = DllWrapper.VortexAPI.GameHostKeyDown(0x7B);
                if (f12 && !_ghF12Prev)
                {
                    try
                    {
                        string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                        string path = System.IO.Path.Combine(dir, "vortex_screenshot.bmp");
                        DllWrapper.VortexAPI.CaptureFrame(path);
                        GHLog("SCREENSHOT -> " + path);
                    }
                    catch { }
                }
                _ghF12Prev = f12;

                // Mouse-look: when the game locks the cursor, the native host CAPTURES it (hides + re-centers
                // every frame) so it never leaves the window and look is unbounded; we read the per-frame delta
                // straight from the host. When unlocked (menu/ESC), the cursor is freed + visible for the UI.
                // Cursor-lock authority: when retained-UI screens are up, the topmost screen decides (a menu unlocks
                // the cursor, the HUD keeps mouse-look locked); otherwise fall back to the legacy script flag.
                bool wantCapture = playing && Editor.UI.Vui.VuiStack.Instance.WantsCursorCapture(sr.CursorLocked);
                DllWrapper.VortexAPI.SetGameHostMouseCaptured(wantCapture);
                if (wantCapture)
                {
                    float dxl = DllWrapper.VortexAPI.GameHostMouseDX();
                    float dyl = DllWrapper.VortexAPI.GameHostMouseDY();
                    if (dxl > 200f) dxl = 200f; else if (dxl < -200f) dxl = -200f;
                    if (dyl > 200f) dyl = 200f; else if (dyl < -200f) dyl = -200f;
                    Vortex.Input.MouseDeltaX = dxl; Vortex.Input.MouseDeltaY = dyl;
                }
                else { Vortex.Input.MouseDeltaX = 0f; Vortex.Input.MouseDeltaY = 0f; }
                _ghPrevMx = mx; _ghPrevMy = my;

                sr.SetUIFrame(cw, ch, mx, my, down, pressed);
                DllWrapper.VortexAPI.UIBegin(cw, ch);
                if (_ghT0 == DateTime.MinValue) _ghT0 = DateTime.Now;
                _ghFrames++;
                if (pressed) GHLog("PRESS mx=" + mx + " my=" + my + " cw=" + cw + " ch=" + ch + " vuiActive=" + Editor.UI.Vui.VuiStack.Instance.HasActiveScreens + " captured=" + DllWrapper.VortexAPI.GameHostMouseCaptured());
                var nowT = DateTime.Now; if ((nowT - _ghT0).TotalMilliseconds >= 1000) { var s0 = Editor.Core.Data.ProjectData.Current; GHLog("FPS=" + _ghFrames + " scene=" + (s0 != null && s0.ActiveScene != null ? s0.ActiveScene.Name : "?") + " beh=" + sr.DebugBehaviourNames() + " ents=" + (s0 != null && s0.ActiveScene != null && s0.ActiveScene.Entities != null ? s0.ActiveScene.Entities.Count : -1) + " draws=" + DllWrapper.VortexAPI.DrawCalls + " drawn=" + DllWrapper.VortexAPI.InstancesDrawn + "/" + DllWrapper.VortexAPI.InstancesTested + " mt=" + (DllWrapper.VortexAPI.MultithreadingActive ? 1 : 0)); _ghFrames = 0; _ghT0 = nowT; }

                if (playing)
                {
                    DllWrapper.VortexAPI.StepEngineRuntime(dt);
                    sr.Update(dt);
                    if (pressed) GHLog("after Update pending=" + (sr.PendingScene ?? "(null)"));
                    Editor.Core.Services.GameRuntime.ProcessPendingSceneSwitch();
                }

                // Retained UI: AFTER scripts mutated slots/screens, BEFORE the 3D submit. Emits into the same single
                // UIBegin frame; the native overlay replays it after the 3D pass, before Present.
                if (Editor.UI.Vui.VuiStack.Instance.HasActiveScreens)
                {
                    Editor.UI.Vui.VuiStack.Instance.TickAll(cw, ch, BuildVuiInput(mx, my, down, pressed));
                    var acts = Editor.UI.Vui.VuiStack.Instance.ConsumeFiredActions();
                    if (acts != null) sr.InvokeUiActions(acts);   // button -> bound C# method
                }

                var scene = Editor.Core.Data.ProjectData.Current != null ? Editor.Core.Data.ProjectData.Current.ActiveScene : null;
                Editor.Core.Services.PlayCameraHelper.ApplyMainCamera(scene);
                // Submit ONCE per scene: the native swap_render_queue reuses last frame's render queue when
                // nothing new is submitted, so a static scene re-renders every frame with only the camera
                // changing (camera is set separately, not via instance data). This removes the per-frame
                // 300-entity walk + ~470 P/Invokes — the CPU bottleneck. on_scene_switch clears the queue on a
                // transition, and the scene-reference change below re-submits the new scene exactly once.
                // Re-submit when the scene changes OR a script added/changed world geometry (Vortex.World). The
                // scene + the script-built world are submitted together so submit-once keeps both.
                if (scene != null && (!ReferenceEquals(scene, _ghSubmittedScene) || Editor.Core.Services.WorldService.Dirty))
                {
                    Editor.Core.Services.SceneRenderService.Instance.SubmitScene(scene);
                    Editor.Core.Services.WorldService.Submit();
                    Editor.Core.Services.WorldService.ClearDirty();
                    _ghSubmittedScene = scene;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[GameHostTick] " + ex); }
        }

        // Drain the host input event queues into reusable buffers + build the per-frame VUI input snapshot (no
        // per-frame allocation beyond the small struct). Shared by GameHostTick and StressTick.
        private static readonly char[] _vuiCharBuf = new char[64];
        private static readonly int[] _vuiKeyBuf = new int[64];
        private static Editor.UI.Vui.VuiInput BuildVuiInput(float mx, float my, bool down, bool pressed)
        {
            int wheel = DllWrapper.VortexAPI.GameHostMouseWheel();
            int cc = 0; for (int c; cc < _vuiCharBuf.Length && (c = DllWrapper.VortexAPI.GameHostNextChar()) >= 0;) _vuiCharBuf[cc++] = (char)c;
            int kc = 0; for (int k; kc < _vuiKeyBuf.Length && (k = DllWrapper.VortexAPI.GameHostNextKeyPressed()) > 0;) _vuiKeyBuf[kc++] = k;
            return new Editor.UI.Vui.VuiInput
            {
                Mx = mx, My = my, Down = down, Pressed = pressed, Wheel = wheel,
                Chars = _vuiCharBuf, CharCount = cc, KeyEvents = _vuiKeyBuf, KeyCount = kc
            };
        }

        private static void LogPlayerError(string dir, string src, Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "player_error.log"),
                    DateTime.Now + " [" + src + "]\r\n" + (ex != null ? ex.ToString() : "(null)") + "\r\n\r\n");
            }
            catch { }
        }

        /// <summary>The exported game exe is a renamed copy of the editor assembly; the packed GameScripts.dll
        /// was compiled against that assembly by its name, so resolve that reference back to the running exe.</summary>
        private static System.Reflection.Assembly PlayerAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var running = System.Reflection.Assembly.GetExecutingAssembly();
            try
            {
                var requested = new System.Reflection.AssemblyName(args.Name).Name;
                if (string.Equals(requested, running.GetName().Name, StringComparison.OrdinalIgnoreCase))
                    return running;
            }
            catch { }
            return null;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DllWrapper.VortexAPI.ShutdownEngineRuntime();
            base.OnExit(e);
        }
    }
}
