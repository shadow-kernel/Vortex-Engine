using System;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Editor.Core.Native;
using Editor.Core.Threading;

namespace VortexEditor
{
    public partial class App : Avalonia.Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            // Shared-core host wiring: UI-thread marshalling + native library lookup, before any engine call.
            UiThread.Poster = a => Dispatcher.UIThread.Post(a);
            UiThread.CheckAccess = () => Dispatcher.UIThread.CheckAccess();
            NativeLoader.Register();
            // particle frame driver: effects with "Preview in editor" run in the Scene view before the first Play
            try { Editor.Core.Services.Particles.ParticleService.EnsureRegistered(); } catch { }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new Shell.MainWindow();
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
