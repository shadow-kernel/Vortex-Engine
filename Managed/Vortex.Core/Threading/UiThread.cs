using System;

namespace Editor.Core.Threading
{
    /// <summary>
    /// Marshals work onto the host's UI thread. Editor shells install their dispatcher here; the player (which has
    /// no UI thread besides the game loop) leaves the defaults, so work runs inline.
    /// </summary>
    public static class UiThread
    {
        /// <summary>Queue an action on the UI thread (Avalonia: Dispatcher.UIThread.Post).</summary>
        public static Action<Action> Poster;
        /// <summary>True when the caller already is on the UI thread (Avalonia: Dispatcher.UIThread.CheckAccess).</summary>
        public static Func<bool> CheckAccess;

        public static void Post(Action action)
        {
            if (action == null) return;
            var poster = Poster;
            var check = CheckAccess;
            if (poster != null && (check == null || !check())) poster(action);
            else action();
        }
    }
}
