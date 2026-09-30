using System;

namespace Editor.Core.Threading
{
    /// <summary>
    /// Small host-shell services the shared core needs from whatever UI shell is running (alert sound, message
    /// boxes, revealing a path in the OS file browser). Editors install their implementation; the defaults are
    /// safe no-ops (a confirmation without a host answers "yes", so headless tools keep working).
    /// </summary>
    public static class HostShell
    {
        public static Action AlertSound;
        public static Action<string> RevealInFileBrowser;
        /// <summary>(caption, text) → true for Yes/OK. Synchronous by contract; UI shells confirm before calling the core instead.</summary>
        public static Func<string, string, bool> ConfirmHandler;
        /// <summary>(caption, text, level 0 info / 1 warning / 2 error).</summary>
        public static Action<string, string, int> NotifyHandler;

        public static void PlayAlertSound() { try { AlertSound?.Invoke(); } catch { } }
        public static void Reveal(string path) { try { RevealInFileBrowser?.Invoke(path); } catch { } }
        public static bool Confirm(string caption, string text) { var h = ConfirmHandler; return h == null || h(caption, text); }
        public static void Notify(string caption, string text, int level)
        {
            var h = NotifyHandler;
            if (h != null) { try { h(caption, text, level); return; } catch { } }
            try
            {
                var c = Editor.Core.Services.ConsoleService.Instance;
                if (level >= 2) c.LogError(caption + ": " + text); else if (level == 1) c.LogWarning(caption + ": " + text); else c.Log(caption + ": " + text);
            }
            catch { }
        }
    }
}
