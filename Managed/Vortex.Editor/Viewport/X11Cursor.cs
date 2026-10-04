using System;
using System.Runtime.InteropServices;

namespace VortexEditor.Viewport
{
    /// <summary>
    /// The X11 twin of <see cref="MacCursor"/>: global pointer position, pointer warping and the Caps Lock
    /// state, through libX11. Mouse look in the editor viewport recentres the pointer every frame, which needs
    /// a warp the UI framework does not expose.
    ///
    /// Avalonia's Linux backend is X11 (and X11 under XWayland on a Wayland session), so the viewport's own
    /// window is an X11 window and XWarpPointer applies to it. On a native Wayland toolkit there is no pointer
    /// warp at all by design — <see cref="IsSupported"/> is then false and mouse look falls back to plain
    /// pointer deltas.
    /// </summary>
    internal static class X11Cursor
    {
        private const string X11 = "libX11.so.6";
        private const string XFixes = "libXfixes.so.3";

        [DllImport(X11)] private static extern IntPtr XOpenDisplay(IntPtr display);
        [DllImport(X11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport(X11)] private static extern int XWarpPointer(IntPtr display, IntPtr srcWindow, IntPtr destWindow,
            int srcX, int srcY, uint srcWidth, uint srcHeight, int destX, int destY);
        [DllImport(X11)] private static extern int XQueryPointer(IntPtr display, IntPtr window,
            out IntPtr rootReturn, out IntPtr childReturn, out int rootX, out int rootY,
            out int winX, out int winY, out uint maskReturn);
        [DllImport(X11)] private static extern int XFlush(IntPtr display);
        [DllImport(XFixes)] private static extern void XFixesHideCursor(IntPtr display, IntPtr window);
        [DllImport(XFixes)] private static extern void XFixesShowCursor(IntPtr display, IntPtr window);

        private const uint LockMask = 1u << 1;   // Caps Lock, per X.h

        private static IntPtr _display;
        private static IntPtr _root;
        private static bool _probed;
        private static bool _ok;
        private static bool _hidden;
        private static bool _xfixesOk = true;

        public static bool IsSupported
        {
            get
            {
                if (_probed) return _ok;
                _probed = true;
                if (!OperatingSystem.IsLinux()) return _ok = false;
                try
                {
                    _display = XOpenDisplay(IntPtr.Zero);
                    if (_display == IntPtr.Zero) return _ok = false;
                    _root = XDefaultRootWindow(_display);
                    _ok = _root != IntPtr.Zero;
                }
                catch { _ok = false; }   // no libX11 (a pure Wayland session without Xlib)
                return _ok;
            }
        }

        /// <summary>Hides/shows the hardware cursor. Avalonia's StandardCursorType.None already covers the
        /// viewport itself; XFixes additionally keeps it hidden while the pointer is warped across the screen.</summary>
        public static void SetHidden(bool hidden)
        {
            if (!IsSupported || _hidden == hidden || !_xfixesOk) return;
            try
            {
                if (hidden) XFixesHideCursor(_display, _root); else XFixesShowCursor(_display, _root);
                XFlush(_display);
                _hidden = hidden;
            }
            catch { _xfixesOk = false; }   // Xfixes missing: Avalonia's own cursor hiding still applies
        }

        /// <summary>Pointer position in root-window (device) pixels.</summary>
        public static bool TryGetPosition(out double x, out double y)
        {
            x = y = 0;
            if (!IsSupported) return false;
            try
            {
                if (XQueryPointer(_display, _root, out _, out _, out int rx, out int ry, out _, out _, out _) == 0) return false;
                x = rx; y = ry;
                return true;
            }
            catch { return false; }
        }

        public static void Warp(double x, double y)
        {
            if (!IsSupported) return;
            try
            {
                XWarpPointer(_display, IntPtr.Zero, _root, 0, 0, 0, 0, (int)Math.Round(x), (int)Math.Round(y));
                XFlush(_display);
            }
            catch { }
        }

        public static bool CapsLockOn()
        {
            if (!IsSupported) return false;
            try
            {
                if (XQueryPointer(_display, _root, out _, out _, out _, out _, out _, out _, out uint mask) == 0) return false;
                return (mask & LockMask) != 0;
            }
            catch { return false; }
        }
    }
}
