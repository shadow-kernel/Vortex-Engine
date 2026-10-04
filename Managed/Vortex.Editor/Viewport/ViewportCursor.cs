using System;

namespace VortexEditor.Viewport
{
    /// <summary>
    /// The cursor control the editor viewport needs beyond what the UI framework offers — hide, warp to a
    /// point, Caps Lock state — routed to the platform that can do it: CoreGraphics on macOS
    /// (<see cref="MacCursor"/>), libX11 on Linux (<see cref="X11Cursor"/>).
    ///
    /// <see cref="CanWarp"/> is what mouse look keys off: where the pointer cannot be warped, the viewport
    /// keeps using raw pointer deltas instead of recentring.
    /// </summary>
    internal static class ViewportCursor
    {
        public static bool CanWarp => MacCursor.IsMac || X11Cursor.IsSupported;

        public static void SetHidden(bool hidden)
        {
            if (MacCursor.IsMac) MacCursor.SetHidden(hidden);
            else if (X11Cursor.IsSupported) X11Cursor.SetHidden(hidden);
        }

        public static bool TryGetPosition(out double x, out double y)
        {
            if (MacCursor.IsMac) return MacCursor.TryGetPosition(out x, out y);
            if (X11Cursor.IsSupported) return X11Cursor.TryGetPosition(out x, out y);
            x = y = 0;
            return false;
        }

        public static void Warp(double x, double y)
        {
            if (MacCursor.IsMac) MacCursor.Warp(x, y);
            else if (X11Cursor.IsSupported) X11Cursor.Warp(x, y);
        }

        public static bool CapsLockOn()
        {
            if (MacCursor.IsMac) return MacCursor.CapsLockOn();
            if (X11Cursor.IsSupported) return X11Cursor.CapsLockOn();
            return false;
        }
    }
}
