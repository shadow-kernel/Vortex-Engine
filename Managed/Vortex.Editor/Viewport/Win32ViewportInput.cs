using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;

namespace VortexEditor.Viewport
{
    /// <summary>
    /// Windows: the engine renders (DX12 swap chain) into the child HWND of the viewport's NativeControlHost. Without
    /// help, Windows delivers every click over the 3D view to that child — a window nobody reads input from — and the
    /// Avalonia window never sees it (no selection, no camera, no gizmos, no LButton for the game). The child and the
    /// holder windows between it and the editor window answer WM_NCHITTEST with HTTRANSPARENT, so the system routes the
    /// mouse to the editor window, where <see cref="EngineViewport"/> takes it like on macOS and Linux. Also the smoke
    /// run's probes (embedding, click routing, mouse buttons) — the Windows counterpart of <c>MacViewProbe</c>.
    /// </summary>
    internal static class Win32ViewportInput
    {
        private const int GWLP_WNDPROC = -4, GWL_STYLE = -16;
        private const uint WM_NCHITTEST = 0x0084, WM_NCDESTROY = 0x0082;
        private const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
        private const long WS_CHILD = 0x40000000L;
        private const int HTTRANSPARENT = -1;
        private const uint GA_ROOT = 2;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static readonly WndProc s_proc = HitTestTransparent;   // lives as long as the process: windows call it
        private static readonly Dictionary<IntPtr, IntPtr> s_previous = new Dictionary<IntPtr, IntPtr>();

        /// <summary>Route mouse input over <paramref name="child"/> to the top-level window: the child and every
        /// window between it and <paramref name="topLevel"/> become transparent to hit testing. Idempotent (the
        /// holder windows can change when the viewport moves between panels). Returns the windows changed now.</summary>
        public static int MakeTransparent(IntPtr child, IntPtr topLevel)
        {
            if (!OperatingSystem.IsWindows() || child == IntPtr.Zero || topLevel == IntPtr.Zero) return 0;
            int changed = 0;
            IntPtr proc = Marshal.GetFunctionPointerForDelegate(s_proc);
            for (IntPtr h = child; h != IntPtr.Zero && h != topLevel; h = GetParent(h))
            {
                if (s_previous.ContainsKey(h)) continue;
                IntPtr old = SetWindowLongPtrW(h, GWLP_WNDPROC, proc);
                if (old == IntPtr.Zero) continue;
                s_previous[h] = old;
                changed++;
            }
            return changed;
        }

        private static IntPtr HitTestTransparent(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_NCHITTEST) return (IntPtr)HTTRANSPARENT;
            if (!s_previous.TryGetValue(hwnd, out IntPtr old)) return DefWindowProcW(hwnd, msg, wParam, lParam);
            if (msg == WM_NCDESTROY)
            {
                SetWindowLongPtrW(hwnd, GWLP_WNDPROC, old);
                s_previous.Remove(hwnd);
            }
            return CallWindowProcW(old, hwnd, msg, wParam, lParam);
        }

        // ---------------------------------------------------------------- smoke probes

        /// <summary>Where the render window sits: its class, size and parent chain up to the editor window. OK when it
        /// is a child window of the editor window, as large as the viewport control, and hit-test transparent.</summary>
        public static string Describe(IntPtr child, IntPtr topLevel, EngineViewport view, out bool ok)
        {
            ok = false;
            if (child == IntPtr.Zero) return "no render window";
            var sb = new StringBuilder();
            GetClientRect(child, out RECT rc);
            sb.Append(ClassName(child)).Append(' ').Append(rc.Right - rc.Left).Append('x').Append(rc.Bottom - rc.Top);
            bool isChild = (GetWindowLongPtrW(child, GWL_STYLE).ToInt64() & WS_CHILD) != 0;
            bool reachesTop = false;
            int depth = 0;
            for (IntPtr h = GetParent(child); h != IntPtr.Zero && depth < 8; h = GetParent(h), depth++)
            {
                GetClientRect(h, out RECT r);
                sb.Append(" -> ").Append(ClassName(h)).Append(' ').Append(r.Right - r.Left).Append('x').Append(r.Bottom - r.Top);
                if (h == topLevel) { reachesTop = true; break; }
            }
            double s = Avalonia.Controls.TopLevel.GetTopLevel(view)?.RenderScaling ?? 1.0;
            int w = (int)Math.Round(view.Bounds.Width * s), hgt = (int)Math.Round(view.Bounds.Height * s);
            bool sized = Math.Abs((rc.Right - rc.Left) - w) <= 2 && Math.Abs((rc.Bottom - rc.Top) - hgt) <= 2;
            bool transparent = s_previous.ContainsKey(child);
            sb.Append(" | control ").Append(w).Append('x').Append(hgt).Append(", child=").Append(isChild).Append(", transparent=").Append(transparent);
            ok = isChild && reachesTop && sized && transparent;
            return sb.ToString();
        }

        /// <summary>The window a real click at the viewport's centre goes to (the system's own hit test, which asks
        /// this thread's windows for WM_NCHITTEST), and that point in its client coordinates.</summary>
        private static IntPtr TargetAtCenter(EngineViewport view, out POINT client)
        {
            var screen = view.PointToScreen(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2));
            client = new POINT { X = screen.X, Y = screen.Y };
            IntPtr target = WindowFromPoint(client);
            if (target != IntPtr.Zero) ScreenToClient(target, ref client);
            return target;
        }

        /// <summary>Post a left click where a real one at the viewport's centre would land. Never posts into another
        /// application's window (the editor may be covered on a desktop). Returns what it did, for the log.</summary>
        public static bool Click(EngineViewport view, IntPtr topLevel, out string what)
        {
            IntPtr target = TargetAtCenter(view, out POINT p);
            what = "target " + (target == IntPtr.Zero ? "none" : ClassName(target)) + (target == topLevel ? " (the editor window)" : target == view.NativeHandle ? " (the render window!)" : "");
            if (target == IntPtr.Zero || GetAncestor(target, GA_ROOT) != topLevel) { what += " — the viewport centre is covered by another window"; return false; }
            IntPtr lp = MakeLParam(p.X, p.Y);
            return PostMessageW(target, WM_LBUTTONDOWN, (IntPtr)1, lp) && PostMessageW(target, WM_LBUTTONUP, IntPtr.Zero, lp);
        }

        /// <summary>Press or release a mouse button over the viewport's centre (the game's LButton / RButton).</summary>
        public static bool MouseButton(EngineViewport view, IntPtr topLevel, bool right, bool down)
        {
            IntPtr target = TargetAtCenter(view, out POINT p);
            if (target == IntPtr.Zero || GetAncestor(target, GA_ROOT) != topLevel) return false;
            uint msg = right ? (down ? WM_RBUTTONDOWN : WM_RBUTTONUP) : (down ? WM_LBUTTONDOWN : WM_LBUTTONUP);
            IntPtr keys = (IntPtr)(down ? (right ? 2 : 1) : 0);
            return PostMessageW(target, msg, keys, MakeLParam(p.X, p.Y));
        }

        private static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

        private static string ClassName(IntPtr h)
        {
            var sb = new StringBuilder(128);
            return GetClassNameW(h, sb, sb.Capacity) > 0 ? sb.ToString() : "?";
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern IntPtr CallWindowProcW(IntPtr prev, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
    }
}
