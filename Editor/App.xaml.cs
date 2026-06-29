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

            // Show the branded splash immediately. It's topmost, so it covers the (blocking) engine init,
            // the empty editor shell, and the project browser opening underneath — then fades to reveal them.
            var splash = new SplashWindow();
            splash.Show();

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
                splash.SetStatus("Starting engine…");
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
                    splash.SetStatus("Loading assets…");
                    Editor.Core.Services.AssetVfs.Mount(pak);
                    System.Diagnostics.Debug.WriteLine("[Player] mounted pak: " + Editor.Core.Services.AssetVfs.FileCount + " files");
                    if (Editor.Core.Services.AssetVfs.TryGetBytes("GameScripts.dll", out var dllBytes) && dllBytes.Length > 0)
                    {
                        try { Editor.Scripting.ScriptRuntime.Instance.PrecompiledAssembly = System.Reflection.Assembly.Load(dllBytes); }
                        catch (Exception sx) { System.Diagnostics.Debug.WriteLine("[Player] scripts DLL load failed: " + sx.Message); }
                    }
                }

                var project = Editor.Core.Services.ProjectService.Instance.LoadProjectFromPath(exeDir);

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
                    scene.Load();
                    scene.ActivateEntities();
                    scene.IsActive = true;
                    Editor.Core.Services.SceneRenderService.Instance.PreloadSceneAssets(scene);
                }

                var cam = Editor.Core.Services.CameraService.Instance.GetMainCamera();
                if (cam.IsValid) Editor.Core.Services.CameraService.Instance.SetActiveCamera(cam);

                Editor.Core.Services.PlayModeService.Instance.IsExternalWindow = true;
                Editor.Core.Services.PlayModeService.Instance.SetGameView(true);
                Editor.Core.Services.PlayModeService.Instance.Play();
                if (scene != null) Editor.Scripting.ScriptRuntime.Instance.Begin(scene);

                splash.FadeOutAndClose();

                // Native GameHost: its own native window + DX12 swapchain + uncapped one-thread loop. Each frame
                // it calls GameHostTick (scripts + camera + submit) then renders + presents. Blocks until close.
                _ghTick = GameHostTick;                                   // keep the delegate alive (GC)
                DllWrapper.VortexAPI.SetGameTickCallback(_ghTick);
                DllWrapper.VortexAPI.RunGameHost(1280, 720, "Vortex");    // BLOCKS — runs the game
                Shutdown();                                                // window closed -> exit
            }
            catch (Exception ex)
            {
                LogPlayerError(exeDir, "BootPlayer", ex);
                try { splash.FadeOutAndClose(); } catch { }
                MessageBox.Show("Player failed to start: " + ex.Message, "Vortex Player", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
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
            try
            {
                if (!_ghInit) { _ghInit = true; try { DllWrapper.VortexAPI.ShowGrid(false); DllWrapper.VortexAPI.ShowGizmos(false); } catch { } } // shipped game: no editor grid/gizmos
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

                // Mouse-look: when the game has the cursor locked, feed relative motion (delta since last frame).
                if (playing && sr.CursorLocked)
                {
                    float dxl = mx - _ghPrevMx, dyl = my - _ghPrevMy;
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
                if (pressed) GHLog("PRESS mx=" + mx + " my=" + my + " cw=" + cw + " ch=" + ch + " btnX=[" + (cw - 306) + ".." + (cw - 56) + "] IN=" + (mx >= cw - 306 && mx <= cw - 56 && my >= ch - 100 && my <= ch - 40));
                var nowT = DateTime.Now; if ((nowT - _ghT0).TotalMilliseconds >= 1000) { var s0 = Editor.Core.Data.ProjectData.Current; GHLog("FPS=" + _ghFrames + " scene=" + (s0 != null && s0.ActiveScene != null ? s0.ActiveScene.Name : "?") + " beh=" + sr.DebugBehaviourNames() + " ents=" + (s0 != null && s0.ActiveScene != null && s0.ActiveScene.Entities != null ? s0.ActiveScene.Entities.Count : -1) + " draws=" + DllWrapper.VortexAPI.DrawCalls + " drawn=" + DllWrapper.VortexAPI.InstancesDrawn + "/" + DllWrapper.VortexAPI.InstancesTested); _ghFrames = 0; _ghT0 = nowT; }

                if (playing)
                {
                    DllWrapper.VortexAPI.StepEngineRuntime(dt);
                    sr.Update(dt);
                    if (pressed) GHLog("after Update pending=" + (sr.PendingScene ?? "(null)"));
                    Editor.Core.Services.GameRuntime.ProcessPendingSceneSwitch();
                }

                var scene = Editor.Core.Data.ProjectData.Current != null ? Editor.Core.Data.ProjectData.Current.ActiveScene : null;
                Editor.Core.Services.PlayCameraHelper.ApplyMainCamera(scene);
                // Submit ONCE per scene: the native swap_render_queue reuses last frame's render queue when
                // nothing new is submitted, so a static scene re-renders every frame with only the camera
                // changing (camera is set separately, not via instance data). This removes the per-frame
                // 300-entity walk + ~470 P/Invokes — the CPU bottleneck. on_scene_switch clears the queue on a
                // transition, and the scene-reference change below re-submits the new scene exactly once.
                if (scene != null && !ReferenceEquals(scene, _ghSubmittedScene))
                {
                    Editor.Core.Services.SceneRenderService.Instance.SubmitScene(scene);
                    _ghSubmittedScene = scene;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[GameHostTick] " + ex); }
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
