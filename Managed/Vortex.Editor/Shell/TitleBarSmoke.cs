using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.VisualTree;
using Editor.Core.Services;

namespace VortexEditor.Shell
{
    /// <summary>
    /// Smoke check "title bar": the window header fits — no control clipped or under the caption buttons, as opened, at
    /// the window's minimum width and (Windows) maximized — and its empty space is the title bar the platform moves the
    /// window by: macOS drags where no visual is hit, Windows asks WM_NCHITTEST (caption there, client on the controls).
    /// </summary>
    internal static class TitleBarSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("title bar", CheckAsync);

        private static void Log(string m) => ConsoleService.Instance.Log(m);

        private static async Task<bool> CheckAsync()
        {
            var w = EditorCommands.Window;
            if (w == null) return false;
            var problems = new List<string>();
            Check(w, "as opened", problems);

            if (w.WindowState == WindowState.Normal && w.Width > w.MinWidth + 1)
            {
                double width = w.Width;
                w.Width = w.MinWidth;
                await SmokeRegistry.Settle(500);
                Check(w, "at the minimum width", problems);
                w.Width = width;
                await SmokeRegistry.Settle(300);
            }
            if (OperatingSystem.IsWindows())
            {
                var state = w.WindowState;
                w.WindowState = WindowState.Maximized;
                await SmokeRegistry.Settle(800);
                Check(w, "maximized", problems);
                OnScreen(w, problems);
                SmokeRegistry.Capture(w, "editor_maximized.png");
                w.WindowState = state;
                await SmokeRegistry.Settle(500);
            }
            foreach (var p in problems) ConsoleService.Instance.LogError("title bar: " + p);
            return problems.Count == 0;
        }

        private static Rect R(Visual v, Visual to) => new Rect(v.TranslatePoint(new Point(0, 0), to) ?? new Point(-1e4, -1e4), v.Bounds.Size);

        private static void Check(MainWindow w, string when, List<string> problems)
        {
            var frame = w.Root.Bounds;   // what is on screen (a maximized Windows window reaches past the screen edges)
            Rect Of(Visual v) => R(v, w);
            var left = Of(w.LeftGroup); var centre = Of(w.CenterGroup); var right = Of(w.RightGroup); var bar = Of(w.TitleBar);
            var cap = w.GetVisualDescendants().OfType<CaptionButtons>().FirstOrDefault(c => c.IsEffectivelyVisible && c.Bounds.Width > 0);
            Rect? caption = cap != null ? Of(cap) : (Rect?)null;
            var menu = w.MenuRow.IsVisible ? Of(w.MenuBarHost) : (Rect?)null;
            Log($"title bar {when}: {frame.Width:0} px; left {left.X:0}–{left.Right:0}, centre {centre.X:0}–{centre.Right:0}, right {right.X:0}–{right.Right:0}"
                + (menu != null ? $"; menu {menu.Value.X:0}–{menu.Value.Right:0}" : "") + (caption != null ? $"; caption buttons {caption.Value.X:0}–{caption.Value.Right:0} × {caption.Value.Height:0}" : ""));

            if (OperatingSystem.IsWindows() && caption == null) problems.Add(when + ": no caption buttons");
            if (!OperatingSystem.IsMacOS())
            {
                int items = w.MenuBarHost.GetVisualDescendants().OfType<MenuItem>().Count(i => i.IsEffectivelyVisible);
                if (menu == null || items < 9) problems.Add(when + ": the menu bar shows " + items + " menus");
            }
            if (left.Right > centre.X + 0.5 || centre.Right > right.X + 0.5) problems.Add(when + ": the toolbar groups overlap");

            var controls = new List<(string name, Rect r)>();
            foreach (var g in new[] { w.CenterGroup, w.RightGroup })
                foreach (var b in g.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
                    controls.Add((b.Name ?? (ToolTip.GetTip(b) as string ?? "button"), Of(b)));
            if (menu != null) controls.Add(("menu bar", menu.Value));
            foreach (var (name, r) in controls)
            {
                if (r.X < frame.X - 0.5 || r.Right > frame.Right + 0.5 || r.Y < frame.Y - 0.5 || r.Bottom > bar.Bottom + 0.5) problems.Add(when + ": " + Short(name) + " is cut off");
                else if (caption != null && r.Intersects(caption.Value)) problems.Add(when + ": " + Short(name) + " is under the caption buttons");
            }

            // where a press lands: empty header space moves the window, controls take the click
            var empty = new List<(string what, Point p)> { ("the project title", Of(w.ProjectTitle).Center) };
            double y = bar.Center.Y;
            if (centre.X - left.Right > 8) empty.Add(("the toolbar left of the view tabs", new Point((left.Right + centre.X) / 2, y)));
            if (right.X - centre.Right > 8) empty.Add(("the toolbar right of the play controls", new Point((centre.Right + right.X) / 2, y)));
            if (menu != null)
            {
                double x = menu.Value.Right + 40;
                if (caption == null || x < caption.Value.X - 10) empty.Add(("the menu row", new Point(x, Of(w.MenuRow).Center.Y)));
            }
            var hits = new List<(string what, Point p)> { ("Play", Of(w.PlayButton).Center), ("Scene", Of(w.TabScene).Center) };
            var settings = w.RightGroup.GetVisualDescendants().OfType<Button>().LastOrDefault();
            if (settings != null) hits.Add(("Settings", Of(settings).Center));
            var file = w.MenuBarHost.GetVisualDescendants().OfType<MenuItem>().FirstOrDefault();
            if (menu != null && file != null) hits.Add(("the File menu", Of(file).Center));
            foreach (var (what, p) in empty)
            {
                string r = PressAt(w, p);
                if (r != "caption" && r != null) problems.Add(when + ": a press on " + what + " is " + r + " (does not move the window)");
            }
            foreach (var (what, p) in hits)
            {
                string r = PressAt(w, p);
                if (r != "control" && r != null) problems.Add(when + ": a press on " + what + " is " + r + " (not a click)");
            }
        }

        private static string Short(string s) => s.Length > 40 ? s.Substring(0, 40) + "…" : s;

        /// <summary>What the platform makes of a press at <paramref name="p"/> (window coordinates): "caption" (moves the
        /// window), "control", something else that is wrong — or null where the window manager decides (Linux).</summary>
        private static string PressAt(MainWindow w, Point p)
        {
            if (OperatingSystem.IsWindows())
            {
                IntPtr hwnd = w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (hwnd == IntPtr.Zero) return "no window handle";
                var s = w.PointToScreen(p);
                var lParam = (IntPtr)unchecked((int)((((uint)s.Y & 0xFFFF) << 16) | ((uint)s.X & 0xFFFF)));
                long r = (long)SendMessageW(hwnd, WM_NCHITTEST, IntPtr.Zero, lParam);
                return r == HTCAPTION ? "caption" : r == HTCLIENT ? "control" : "hit-test code " + r;
            }
            if (OperatingSystem.IsMacOS())
            {
                // Avalonia's macOS chrome hit test: a press where no visual is hit drags the window (a double-click zooms)
                var v = w.GetVisualAt(p, x => !(x is IInputElement ie) || (ie.IsHitTestVisible && ie.IsEffectivelyVisible));
                return v == null ? "caption" : "control";
            }
            return null;
        }

        /// <summary>Maximized, the whole editor must be on the screen (Windows: the window itself reaches past the edges).</summary>
        private static void OnScreen(MainWindow w, List<string> problems)
        {
            var screen = w.Screens.ScreenFromWindow(w);
            if (screen == null) return;
            var area = screen.WorkingArea;
            var tl = w.Root.PointToScreen(new Point(0, 0));
            var br = w.Root.PointToScreen(new Point(w.Root.Bounds.Width, w.Root.Bounds.Height));
            Log($"title bar maximized: off-screen margin {w.OffScreenMargin}, editor {tl}–{br}, work area {area}");
            if (tl.X < area.X - 1 || tl.Y < area.Y - 1 || br.X > area.Right + 1 || br.Y > area.Bottom + 1)
                problems.Add("maximized, the editor reaches past the screen (" + tl + "–" + br + ", work area " + area + ")");
        }

        private const uint WM_NCHITTEST = 0x0084;
        private const long HTCLIENT = 1, HTCAPTION = 2;
        [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
