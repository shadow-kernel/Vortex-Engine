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

            // Show the branded splash immediately. It's topmost, so it covers the (blocking) engine init,
            // the empty editor shell, and the project browser opening underneath — then fades to reveal them.
            var splash = new SplashWindow();
            splash.Show();

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

        protected override void OnExit(ExitEventArgs e)
        {
            DllWrapper.VortexAPI.ShutdownEngineRuntime();
            base.OnExit(e);
        }
    }
}
