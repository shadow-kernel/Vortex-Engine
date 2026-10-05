using System;
using System.Runtime.InteropServices;

namespace VortexEditor.Viewport
{
    /// <summary>
    /// The Windows twin of <see cref="MacCursor"/> and <see cref="X11Cursor"/>: global pointer position, pointer warping
    /// (mouse look recentres the pointer every frame, so fly mode does not stop at the screen edge) and the Caps Lock
    /// state, through user32. Positions are physical screen pixels, which is what Avalonia's PointToScreen reports on
    /// Windows. Hiding needs nothing extra: clicks over the render window reach the editor window
    /// (<see cref="Win32ViewportInput"/>), so the viewport's own cursor applies.
    /// </summary>
    internal static class Win32Cursor
    {
        public static bool IsWindows => OperatingSystem.IsWindows();

        public static bool TryGetPosition(out double x, out double y)
        {
            if (GetCursorPos(out POINT p)) { x = p.X; y = p.Y; return true; }
            x = y = 0;
            return false;
        }

        public static void Warp(double x, double y) => SetCursorPos((int)Math.Round(x), (int)Math.Round(y));

        public static bool CapsLockOn() => (GetKeyState(0x14) & 1) != 0;   // VK_CAPITAL toggled

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    }
}
