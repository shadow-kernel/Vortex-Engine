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

                // GameWindow is the app's MainWindow; ProjectData.Current reads MainWindow.DataContext,
                // so binding the project here makes Current resolve for the play pipeline.
                var gw = new Editor.PlayMode.GameWindow { DataContext = project, OwnsGameLoop = true };
                MainWindow = gw;            // app exits when the game window closes

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

                splash.FadeOutAndClose();
                gw.Show();

                Editor.Core.Services.PlayModeService.Instance.Play();
                if (scene != null) Editor.Scripting.ScriptRuntime.Instance.Begin(scene);
                // From here the GameWindow's per-frame loop (OwnsGameLoop) drives everything.
            }
            catch (Exception ex)
            {
                LogPlayerError(exeDir, "BootPlayer", ex);
                try { splash.FadeOutAndClose(); } catch { }
                MessageBox.Show("Player failed to start: " + ex.Message, "Vortex Player", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
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
