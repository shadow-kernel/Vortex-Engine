using System;
using System.Runtime.InteropServices;

namespace VortexEditor.Viewport
{
    /// <summary>
    /// Diagnostics for the macOS viewport embedding (smoke runs): inspects the NSView the engine renders into and
    /// can post a synthetic click through AppKit's normal event path, so a headless run proves that the Metal
    /// layer sits inside the viewport panel and that pointer events over it still reach the UI toolkit.
    /// </summary>
    internal static class MacViewProbe
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

        [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct CGRect { public double X, Y, W, H; }

        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr object_getClassName(IntPtr obj);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgId(IntPtr self, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void MsgVoidId(IntPtr self, IntPtr sel, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long MsgLong(IntPtr self, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgIdULong(IntPtr self, IntPtr sel, ulong arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect MsgRect(IntPtr self, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr MsgMouseEvent(IntPtr cls, IntPtr sel, ulong type, CGPoint location, ulong modifierFlags, double timestamp,
            long windowNumber, IntPtr context, long eventNumber, long clickCount, float pressure);
        [DllImport(AppKit)] private static extern IntPtr NSSelectorFromString(IntPtr s);

        private static IntPtr S(string s) => sel_registerName(s);
        private static string ClassName(IntPtr obj) => obj == IntPtr.Zero ? "(nil)" : Marshal.PtrToStringAnsi(object_getClassName(obj));

        /// <summary>Human-readable description of the host view: its size, its first subview (SDL's Metal view) and
        /// its next responder. Used by the smoke run to assert the embedding.</summary>
        public static string Describe(IntPtr hostView, out bool ok)
        {
            ok = false;
            if (!OperatingSystem.IsMacOS() || hostView == IntPtr.Zero) return "not on macOS / no host view";
            try
            {
                var bounds = MsgRect(hostView, S("bounds"));
                var subviews = MsgId(hostView, S("subviews"));
                long count = subviews != IntPtr.Zero ? MsgLong(subviews, S("count")) : 0;
                IntPtr metal = count > 0 ? MsgIdULong(subviews, S("objectAtIndex:"), 0) : IntPtr.Zero;
                string metalName = ClassName(metal);
                var mf = metal != IntPtr.Zero ? MsgRect(metal, S("frame")) : default;
                string responder = ClassName(MsgId(hostView, S("nextResponder")));
                bool metalOk = metalName.Contains("cocoametalview") && Math.Abs(mf.W - bounds.W) < 0.5 && Math.Abs(mf.H - bounds.H) < 0.5 && Math.Abs(mf.X) < 0.5 && Math.Abs(mf.Y) < 0.5;
                bool responderOk = !responder.Contains("SDL");
                ok = metalOk && responderOk;
                var sb = new System.Text.StringBuilder();
                sb.Append($"host {ClassName(hostView)} {bounds.W:0}x{bounds.H:0}, subs={count}, first={metalName} {mf.W:0}x{mf.H:0}@{mf.X:0},{mf.Y:0}, resp={responder}");
                sb.Append(" | ancestry:");
                IntPtr v = hostView; int guard = 0;
                while (v != IntPtr.Zero && guard++ < 12)
                {
                    var f = MsgRect(v, S("frame"));
                    sb.Append($" -> {ClassName(v)} {f.W:0}x{f.H:0}@{f.X:0},{f.Y:0}");
                    v = MsgId(v, S("superview"));
                }
                IntPtr cv = MsgId(MsgId(hostView, S("window")), S("contentView"));
                IntPtr cvsubs = cv != IntPtr.Zero ? MsgId(cv, S("subviews")) : IntPtr.Zero;
                long cvn = cvsubs != IntPtr.Zero ? MsgLong(cvsubs, S("count")) : 0;
                sb.Append($" | contentView {ClassName(cv)} has {cvn} subs:");
                for (long i = 0; i < cvn && i < 6; i++) { IntPtr sv = MsgIdULong(cvsubs, S("objectAtIndex:"), (ulong)i); var f = MsgRect(sv, S("frame")); sb.Append($" [{ClassName(sv)} {f.W:0}x{f.H:0}@{f.X:0},{f.Y:0}]"); }
                return sb.ToString();
            }
            catch (Exception ex) { return "probe failed: " + ex.Message; }
        }

        /// <summary>Post a left click (down + up) at a point inside the host view through [NSApp sendEvent:], i.e.
        /// the same path a real mouse click takes (hit-test, responder chain).</summary>
        public static bool Click(IntPtr hostView, double localX, double localY)
        {
            if (!OperatingSystem.IsMacOS() || hostView == IntPtr.Zero) return false;
            try
            {
                IntPtr window = MsgId(hostView, S("window"));
                if (window == IntPtr.Zero) return false;
                long windowNumber = MsgLong(window, S("windowNumber"));
                // view-local (top-left origin, as the toolkit reports it) -> window coordinates (bottom-left origin)
                var bounds = MsgRect(hostView, S("bounds"));
                var frameInWindow = ConvertRectToWindow(hostView, bounds);
                var p = new CGPoint { X = frameInWindow.X + localX, Y = frameInWindow.Y + (bounds.H - localY) };
                System.Console.WriteLine("smoke: synthetic click window point " + p.X + "," + p.Y + " host frame in window " + frameInWindow.X + "," + frameInWindow.Y + " " + frameInWindow.W + "x" + frameInWindow.H + " window=" + ClassName(window));
                MsgVoidId(window, S("makeKeyAndOrderFront:"), IntPtr.Zero);   // a real click would make the window key first
                IntPtr nsEvent = objc_getClass("NSEvent");
                IntPtr sel = S("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:");
                IntPtr app = MsgId(objc_getClass("NSApplication"), S("sharedApplication"));
                const ulong LeftDown = 1, LeftUp = 2;
                double t = Environment.TickCount64 / 1000.0;
                IntPtr down = MsgMouseEvent(nsEvent, sel, LeftDown, p, 0, t, windowNumber, IntPtr.Zero, 0, 1, 1f);
                IntPtr up = MsgMouseEvent(nsEvent, sel, LeftUp, p, 0, t + 0.05, windowNumber, IntPtr.Zero, 0, 1, 0f);
                if (down == IntPtr.Zero || up == IntPtr.Zero) return false;
                MsgVoidId(app, S("sendEvent:"), down);
                MsgVoidId(app, S("sendEvent:"), up);
                return true;
            }
            catch (Exception ex) { System.Console.WriteLine("synthetic click failed: " + ex.Message); return false; }
        }

        /// <summary>Post ONE mouse-button event (left/right, down or up) at a point inside the host view through
        /// [NSApp sendEvent:] — lets the smoke run hold a button down and check the game's input state in between.</summary>
        public static bool MouseButton(IntPtr hostView, double localX, double localY, bool right, bool down)
        {
            if (!OperatingSystem.IsMacOS() || hostView == IntPtr.Zero) return false;
            try
            {
                IntPtr window = MsgId(hostView, S("window"));
                if (window == IntPtr.Zero) return false;
                long windowNumber = MsgLong(window, S("windowNumber"));
                var bounds = MsgRect(hostView, S("bounds"));
                var frameInWindow = ConvertRectToWindow(hostView, bounds);
                var p = new CGPoint { X = frameInWindow.X + localX, Y = frameInWindow.Y + (bounds.H - localY) };
                IntPtr nsEvent = objc_getClass("NSEvent");
                IntPtr sel = S("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:");
                IntPtr app = MsgId(objc_getClass("NSApplication"), S("sharedApplication"));
                // NSEventType: 1 LeftMouseDown, 2 LeftMouseUp, 3 RightMouseDown, 4 RightMouseUp
                ulong type = right ? (down ? 3UL : 4UL) : (down ? 1UL : 2UL);
                IntPtr ev = MsgMouseEvent(nsEvent, sel, type, p, 0, Environment.TickCount64 / 1000.0, windowNumber, IntPtr.Zero, 0, 1, down ? 1f : 0f);
                if (ev == IntPtr.Zero) return false;
                MsgVoidId(app, S("sendEvent:"), ev);
                return true;
            }
            catch (Exception ex) { System.Console.WriteLine("synthetic mouse button failed: " + ex.Message); return false; }
        }

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect MsgRectRectId(IntPtr self, IntPtr sel, CGRect rect, IntPtr view);
        private static CGRect ConvertRectToWindow(IntPtr view, CGRect rect) => MsgRectRectId(view, S("convertRect:toView:"), rect, IntPtr.Zero);
    }
}
