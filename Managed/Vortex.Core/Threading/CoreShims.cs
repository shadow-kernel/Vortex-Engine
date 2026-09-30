using System;

namespace Editor.Core.Threading
{
    // Minimal stand-ins for the WPF Application/Dispatcher/MessageBox surface used by a few shared services,
    // so their source stays byte-identical on Windows. They route to the host hooks in HostShell / UiThread.

    public enum CoreDispatcherPriority { Background, Normal, Render, Send }
    public enum MessageBoxButton { OK, OKCancel, YesNo, YesNoCancel }
    public enum MessageBoxImage { None, Error, Question, Warning, Information }
    public enum MessageBoxResult { None, OK, Cancel, Yes, No }

    public sealed class CoreDispatcher
    {
        public void BeginInvoke(Delegate d, CoreDispatcherPriority priority = CoreDispatcherPriority.Normal)
            => UiThread.Post(() => d.DynamicInvoke());
        public void BeginInvoke(Action a) => UiThread.Post(a);
        public void Invoke(Action a) => UiThread.Post(a);
        public bool CheckAccess() { var c = UiThread.CheckAccess; return c == null || c(); }
    }

    public sealed class CoreApplication
    {
        public static CoreApplication Current { get; } = new CoreApplication();
        public CoreDispatcher Dispatcher { get; } = new CoreDispatcher();
    }

    public static class CoreMessageBox
    {
        public static MessageBoxResult Show(string text) => Show(text, "Vortex", MessageBoxButton.OK, MessageBoxImage.Information);
        public static MessageBoxResult Show(string text, string caption) => Show(text, caption, MessageBoxButton.OK, MessageBoxImage.Information);
        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button) => Show(text, caption, button, MessageBoxImage.Information);
        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage image)
        {
            if (button == MessageBoxButton.YesNo || button == MessageBoxButton.YesNoCancel || button == MessageBoxButton.OKCancel)
            {
                bool yes = HostShell.Confirm(caption, text);
                if (button == MessageBoxButton.OKCancel) return yes ? MessageBoxResult.OK : MessageBoxResult.Cancel;
                return yes ? MessageBoxResult.Yes : MessageBoxResult.No;
            }
            HostShell.Notify(caption, text, image == MessageBoxImage.Error ? 2 : image == MessageBoxImage.Warning ? 1 : 0);
            return MessageBoxResult.OK;
        }
    }
}
