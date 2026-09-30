using System;
using System.Runtime.InteropServices;

namespace VortexEditor.Viewport
{
    /// <summary>
    /// Cursor control the editor viewport needs beyond what the UI framework offers (hide, warp to a point,
    /// Caps Lock state). macOS via CoreGraphics; other platforms are no-ops here (Windows uses user32).
    /// </summary>
    internal static class MacCursor
    {
        private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }

        [DllImport(AppServices)] private static extern IntPtr CGEventCreate(IntPtr source);
        [DllImport(AppServices)] private static extern CGPoint CGEventGetLocation(IntPtr evt);
        [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr obj);
        [DllImport(AppServices)] private static extern int CGWarpMouseCursorPosition(CGPoint point);
        [DllImport(AppServices)] private static extern int CGAssociateMouseAndMouseCursorPosition(bool connected);
        [DllImport(AppServices)] private static extern int CGSetLocalEventsSuppressionInterval(double seconds);
        [DllImport(AppServices)] private static extern uint CGMainDisplayID();
        [DllImport(AppServices)] private static extern int CGDisplayHideCursor(uint display);
        [DllImport(AppServices)] private static extern int CGDisplayShowCursor(uint display);
        [DllImport(AppServices)] private static extern ulong CGEventSourceFlagsState(int stateId);

        private static bool _hidden;
        private static bool _suppressionOff;
        public static bool IsMac => OperatingSystem.IsMacOS();

        public static void SetHidden(bool hidden)
        {
            if (!IsMac || _hidden == hidden) return;
            _hidden = hidden;
            try { if (hidden) CGDisplayHideCursor(CGMainDisplayID()); else CGDisplayShowCursor(CGMainDisplayID()); } catch { }
        }

        /// <summary>Current cursor position in global display points (origin top-left of the main display).</summary>
        public static bool TryGetPosition(out double x, out double y)
        {
            x = y = 0;
            if (!IsMac) return false;
            try
            {
                IntPtr e = CGEventCreate(IntPtr.Zero);
                if (e == IntPtr.Zero) return false;
                var p = CGEventGetLocation(e);
                CFRelease(e);
                x = p.X; y = p.Y;
                return true;
            }
            catch { return false; }
        }

        public static void Warp(double x, double y)
        {
            if (!IsMac) return;
            try
            {
                if (!_suppressionOff) { CGSetLocalEventsSuppressionInterval(0.0); _suppressionOff = true; }
                CGWarpMouseCursorPosition(new CGPoint { X = x, Y = y });
                CGAssociateMouseAndMouseCursorPosition(true);
            }
            catch { }
        }

        public static bool CapsLockOn()
        {
            if (!IsMac) return false;
            try { return (CGEventSourceFlagsState(0 /* kCGEventSourceStateCombinedSessionState */) & (1UL << 16)) != 0; }
            catch { return false; }
        }
    }
}
