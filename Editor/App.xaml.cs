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

        private long _playerLastTick;

        /// <summary>Boots the bundled game with NO editor UI: load project from the exe folder, activate the
        /// startup scene, show only the GameWindow in play mode, and drive the gameplay-script tick.</summary>
        private void BootPlayer(string exeDir, SplashWindow splash)
        {
            try
            {
                splash.SetStatus("Starting engine…");
                DllWrapper.VortexAPI.InitEngineRuntime();
                Editor.Core.Services.PlayModeService.Instance.IsReleaseMode = true; // shipped game: no dev banner

                var project = Editor.Core.Services.ProjectService.Instance.LoadProjectFromPath(exeDir);

                // GameWindow is the app's MainWindow; ProjectData.Current reads MainWindow.DataContext,
                // so binding the project here makes Current resolve for the play pipeline.
                var gw = new Editor.PlayMode.GameWindow { DataContext = project };
                MainWindow = gw;            // app exits when the game window closes

                var scene = project != null ? project.ActiveScene : null;
                if (scene != null)
                {
                    scene.Load();
                    scene.ActivateEntities();
                    scene.IsActive = true;
                }

                var cam = Editor.Core.Services.CameraService.Instance.GetMainCamera();
                if (cam.IsValid) Editor.Core.Services.CameraService.Instance.SetActiveCamera(cam);

                Editor.Core.Services.PlayModeService.Instance.IsExternalWindow = true;
                Editor.Core.Services.PlayModeService.Instance.SetGameView(true);

                splash.FadeOutAndClose();
                gw.Show();

                Editor.Core.Services.PlayModeService.Instance.Play();
                if (scene != null) Editor.Scripting.ScriptRuntime.Instance.Begin(scene);

                // Drive the gameplay scripts each frame (the editor's GamePreview does this; standalone has none).
                _playerLastTick = 0;
                System.Windows.Media.CompositionTarget.Rendering += PlayerTick;
            }
            catch (Exception ex)
            {
                try { splash.FadeOutAndClose(); } catch { }
                MessageBox.Show("Player failed to start: " + ex.Message, "Vortex Player", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private void PlayerTick(object sender, EventArgs e)
        {
            long now = DateTime.Now.Ticks;
            float dt = _playerLastTick == 0 ? 0.016f : (float)((now - _playerLastTick) / 1e7);
            _playerLastTick = now;
            if (dt > 0.1f) dt = 0.1f;       // clamp after stalls
            try { Editor.Scripting.ScriptRuntime.Instance.Update(dt); } catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DllWrapper.VortexAPI.ShutdownEngineRuntime();
            base.OnExit(e);
        }
    }
}
