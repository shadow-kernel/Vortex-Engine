using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Editor.Core.Data;
using VortexEditor.Controls;
using VortexEditor.Panels.Inspector;

namespace VortexEditor.Shell.Material
{
    /// <summary>
    /// Small UI + path helpers shared by the material / colour / asset picker / tag / audio tool windows
    /// (one place so the windows look and behave alike).
    /// </summary>
    internal static class EditorKit
    {
        // ---------------------------------------------------------------- theme
        public static IBrush Brush(string key)
            => Application.Current != null && Application.Current.TryFindResource(key, out var b) && b is IBrush br ? br : Brushes.Gray;

        public static TextBlock Section(string text, double top = 12)
            => new TextBlock { Text = text, Classes = { "section" }, Margin = new Thickness(0, top, 0, 4) };

        public static TextBlock Hint(string text)
            => new TextBlock { Text = text, Classes = { "small", "tertiary" }, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };

        public static Button IconButton(string icon, string tip, Action click, double size = 14)
        {
            var b = new Button { Classes = { "icon" }, Content = new VxIcon { Icon = icon, Width = size, Height = size } };
            if (tip != null) ToolTip.SetTip(b, tip);
            if (click != null) b.Click += (s, e) => click();
            return b;
        }

        public static Button SmallButton(string text, Action click, string tip = null)
        {
            var b = new Button { Content = text, Padding = new Thickness(9, 2), MinHeight = 22, FontSize = 12 };
            if (tip != null) ToolTip.SetTip(b, tip);
            if (click != null) b.Click += (s, e) => click();
            return b;
        }

        /// <summary>Pill-shaped tag chip with an optional remove (×) button.</summary>
        public static Border Chip(string text, Action remove, bool accent = true)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            sp.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = accent ? Brush("VxTextOnAccentBrush") : Brush("VxTextBrush") });
            if (remove != null)
            {
                var x = new Button { Classes = { "icon" }, Width = 16, Height = 16, MinHeight = 16, Padding = new Thickness(0), Content = new VxIcon { Icon = "Close", Width = 9, Height = 9, Foreground = accent ? Brush("VxTextOnAccentBrush") : Brush("VxTextSecondaryBrush") } };
                ToolTip.SetTip(x, "Remove " + text);
                x.Click += (s, e) => remove();
                sp.Children.Add(x);
            }
            return new Border { Background = accent ? Brush("VxAccentBrush") : Brush("VxControlBrush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, remove != null ? 4 : 9, 2), Margin = new Thickness(0, 0, 6, 6), Child = sp };
        }

        /// <summary>Number / text boxes inside taller rows (a slider row) stretch and show their text at the top —
        /// centre them vertically instead.</summary>
        public static void CenterTextBoxes(Control c)
        {
            switch (c)
            {
                case TextBox tb: if (tb.VerticalAlignment == VerticalAlignment.Stretch) tb.VerticalAlignment = VerticalAlignment.Center; break;
                case Panel p: foreach (var ch in p.Children) CenterTextBoxes(ch); break;
                case Decorator d when d.Child != null: CenterTextBoxes(d.Child); break;
                case ContentControl cc when cc.Content is Control inner: CenterTextBoxes(inner); break;
            }
        }

        /// <summary>Exact extension match against "*.ext" patterns (files without an extension never match).</summary>
        public static bool MatchesPatterns(string path, string[] patterns)
        {
            if (patterns == null || patterns.Length == 0) return true;
            string ext = Path.GetExtension(path ?? "");
            if (string.IsNullOrEmpty(ext)) return false;
            foreach (var p in patterns)
            {
                if (p == "*" || p == "*.*") return true;
                string pe = p.StartsWith("*") ? p.Substring(1) : p;
                if (string.Equals(pe, ext, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- windows
        /// <summary>The window a modal picker should belong to: the active editor window, else the main window.</summary>
        public static Window ActiveWindow()
        {
            try
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life)
                {
                    var active = life.Windows.FirstOrDefault(w => w.IsActive && w.IsVisible);
                    if (active != null) return active;
                }
            }
            catch { }
            return Dialogs.Owner ?? EditorWindows.Owner;
        }

        /// <summary>Shrink (and re-centre) a large tool window that does not fit the screen it opened on.</summary>
        public static void FitToScreen(Window w)
        {
            try
            {
                var scr = w.Screens?.ScreenFromVisual(w) ?? w.Screens?.Primary;
                if (scr == null) return;
                double scale = scr.Scaling > 0 ? scr.Scaling : 1;
                var area = scr.WorkingArea;
                double maxW = area.Width / scale * 0.96, maxH = area.Height / scale * 0.94;
                if (w.Bounds.Width <= maxW && w.Bounds.Height <= maxH) return;
                w.MinWidth = Math.Min(w.MinWidth, maxW); w.MinHeight = Math.Min(w.MinHeight, maxH);
                w.Width = Math.Min(w.Bounds.Width, maxW); w.Height = Math.Min(w.Bounds.Height, maxH);
                w.Position = new PixelPoint(area.X + (int)((area.Width - w.Width * scale) / 2), area.Y + (int)((area.Height - w.Height * scale) / 2));
            }
            catch { }
        }

        public static IEnumerable<T> OpenWindows<T>() where T : Window
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life)
                return life.Windows.OfType<T>().ToList();
            return Array.Empty<T>();
        }

        /// <summary>Three-way sheet (e.g. Save / Don't Save / Cancel). Returns the clicked index; the last button cancels.</summary>
        public static async Task<int> Choose(Window owner, string title, string message, params string[] buttons)
        {
            int result = buttons.Length - 1;
            var win = new Window
            {
                Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, SystemDecorations = SystemDecorations.BorderOnly,
                ShowInTaskbar = false, Background = Brushes.Transparent, TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }
            };
            var panel = new StackPanel { Spacing = 8, Margin = new Thickness(22, 18, 22, 16) };
            panel.Children.Add(new PathIcon { Data = (Geometry)Application.Current.FindResource("IconVortex"), Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brush("VxAccentBrush") });
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center });
            if (!string.IsNullOrEmpty(message))
                panel.Children.Add(new TextBlock { Text = message, Classes = { "secondary" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 360 });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var b = new Button { Content = buttons[i], MinWidth = 84 };
                if (i == 0) { b.Classes.Add("accent"); b.IsDefault = true; }
                if (i == buttons.Length - 1) b.IsCancel = true;
                b.Click += (s, e) => { result = idx; win.Close(); };
                row.Children.Add(b);
            }
            panel.Children.Add(row);
            win.Content = new Border { Background = Brush("VxPanelRaisedBrush"), BorderBrush = Brush("VxSeparatorBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = panel };
            owner = owner ?? ActiveWindow();
            if (owner != null) await win.ShowDialog(owner);
            else { var tcs = new TaskCompletionSource<bool>(); win.Closed += (s, e) => tcs.TrySetResult(true); win.Show(); await tcs.Task; }
            return result;
        }

        // ---------------------------------------------------------------- paths
        public static string ProjectRoot => ProjectData.Current?.Path;

        /// <summary>Project-relative, forward-slash form of a path (unchanged when outside the project / not rooted).</summary>
        public static string ToProjectRelative(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return path;
            var root = ProjectRoot;
            if (string.IsNullOrEmpty(root) || !Path.IsPathRooted(path)) return path.Replace('\\', '/');
            try
            {
                string full = Path.GetFullPath(path);
                string r = Path.GetFullPath(root).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
                if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return full.Substring(r.Length).Replace('\\', '/');
            }
            catch { }
            return path;
        }

        /// <summary>Absolute path of a project-relative (or already absolute) path.</summary>
        public static string ToAbsolute(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase)) return path;
            string p = Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', '/');
            if (Path.IsPathRooted(p)) return p;
            var root = ProjectRoot;
            return string.IsNullOrEmpty(root) ? p : Path.GetFullPath(Path.Combine(root, p));
        }

        public static bool IsInsideProject(string fullPath)
        {
            var root = ProjectRoot;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath)) return false;
            try { return Path.GetFullPath(fullPath).StartsWith(Path.GetFullPath(root).TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(ToAbsolute(a)), Path.GetFullPath(ToAbsolute(b)), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>A file dragged from the Asset Browser ("vortex/asset", project-relative or absolute) or from Finder.</summary>
        public static List<string> DroppedFiles(DragEventArgs e)
        {
            var list = new List<string>();
            try
            {
                if (e.Data.Get("vortex/asset") is string s && !string.IsNullOrEmpty(s)) list.Add(ToAbsolute(s));
                if (e.Data.Contains(DataFormats.Files))
                    foreach (var f in e.Data.GetFiles() ?? Array.Empty<Avalonia.Platform.Storage.IStorageItem>())
                    {
                        var p = f.TryGetLocalPath();
                        if (!string.IsNullOrEmpty(p)) list.Add(p);
                    }
            }
            catch { }
            return list;
        }

        public static bool HasExtension(string path, IEnumerable<string> extensions)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return extensions.Any(x => string.Equals(x.TrimStart('*'), ext, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Copy a file from outside the project into Assets/&lt;folder&gt; (unique name) and return the copy's path.</summary>
        public static string ImportIntoProject(string sourceFullPath, string assetsSubfolder)
        {
            var root = ProjectRoot;
            if (string.IsNullOrEmpty(root) || IsInsideProject(sourceFullPath)) return sourceFullPath;
            string dir = Path.Combine(root, "Assets", assetsSubfolder);
            Directory.CreateDirectory(dir);
            string name = Path.GetFileNameWithoutExtension(sourceFullPath), ext = Path.GetExtension(sourceFullPath);
            string dest = Path.Combine(dir, name + ext);
            for (int n = 1; File.Exists(dest); n++) dest = Path.Combine(dir, name + "_" + n + ext);
            File.Copy(sourceFullPath, dest);
            try { Editor.Core.Assets.AssetDatabase.Instance.Refresh(); } catch { }
            return dest;
        }

        // ---------------------------------------------------------------- property-row refreshers
        /// <summary>
        /// Build controls with <see cref="PropertyRows"/> helpers but keep their refreshers private to the window: the
        /// inspector clears the global refresher table on every rebuild, and a closed tool window must not stay
        /// referenced from it. Dispose moves every refresher registered inside the scope into <paramref name="target"/>.
        /// </summary>
        public sealed class RefresherScope : IDisposable
        {
            private readonly HashSet<Control> _before;
            private readonly List<Action> _target;
            public RefresherScope(List<Action> target) { _target = target; _before = new HashSet<Control>(PropertyRows.Refreshers.Keys); }
            public void Dispose()
            {
                foreach (var kv in PropertyRows.Refreshers.ToList())
                {
                    if (_before.Contains(kv.Key)) continue;
                    _target.Add(kv.Value);
                    PropertyRows.Refreshers.Remove(kv.Key);
                }
            }
        }
    }

    /// <summary>HSV ↔ RGB and hex helpers.</summary>
    internal static class ColorMath
    {
        public static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 0) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
            if (h < 0) h += 360;
        }

        public static Color FromHsv(double h, double s, double v, byte a = 255)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c, r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromArgb(a, B(r + m), B(g + m), B(b + m));
        }

        public static byte B(double v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v * 255.0)));

        public static string ToHex(Color c, bool alpha) => alpha ? $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>#RGB, #RRGGBB or #RRGGBBAA (the # is optional).</summary>
        public static bool TryParseHex(string text, out Color c)
        {
            c = default;
            string h = (text ?? "").Trim().TrimStart('#');
            try
            {
                if (h.Length == 3) h = new string(new[] { h[0], h[0], h[1], h[1], h[2], h[2] });
                if (h.Length != 6 && h.Length != 8) return false;
                byte r = Convert.ToByte(h.Substring(0, 2), 16), g = Convert.ToByte(h.Substring(2, 2), 16), b = Convert.ToByte(h.Substring(4, 2), 16);
                byte a = h.Length == 8 ? Convert.ToByte(h.Substring(6, 2), 16) : (byte)255;
                c = Color.FromArgb(a, r, g, b);
                return true;
            }
            catch { return false; }
        }

        public static Color FromFloats(float[] rgba, int count)
        {
            float F(int i, float d) => rgba != null && i < rgba.Length ? rgba[i] : d;
            return Color.FromArgb(count >= 4 ? B(F(3, 1f)) : (byte)255, B(F(0, 1f)), B(F(1, 1f)), B(F(2, 1f)));
        }
    }

    /// <summary>Colour swatch (checkerboard behind translucent colours), used by the material editor and the picker.</summary>
    internal sealed class ColorSwatch : Control
    {
        private Color _color = Colors.White;
        public Color Color { get => _color; set { _color = value; InvalidateVisual(); } }
        public double Radius { get; set; } = 5;
        /// <summary>Optional second colour drawn in the lower half (the picker's "before" colour).</summary>
        public Color? Compare { get; set; }

        public override void Render(DrawingContext ctx)
        {
            var r = new Rect(Bounds.Size);
            if (r.Width < 1 || r.Height < 1) return;
            using (ctx.PushClip(new RoundedRect(r, Radius)))
            {
                Checker.Draw(ctx, r, 6);
                if (Compare.HasValue)
                {
                    ctx.FillRectangle(new SolidColorBrush(_color), new Rect(0, 0, r.Width, r.Height / 2));
                    ctx.FillRectangle(new SolidColorBrush(Compare.Value), new Rect(0, r.Height / 2, r.Width, r.Height / 2));
                }
                else ctx.FillRectangle(new SolidColorBrush(_color), r);
            }
            ctx.DrawRectangle(null, new Pen(EditorKit.Brush("VxControlBorderBrush"), 1), r.Deflate(0.5), Radius, Radius);
        }
    }

    internal static class Checker
    {
        private static readonly IBrush Light = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        private static readonly IBrush Dark = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        public static void Draw(DrawingContext ctx, Rect r, double cell)
        {
            ctx.FillRectangle(Light, r);
            for (double y = r.Y; y < r.Bottom; y += cell)
                for (double x = r.X + (((int)((y - r.Y) / cell)) % 2 == 0 ? cell : 0); x < r.Right; x += cell * 2)
                    ctx.FillRectangle(Dark, new Rect(x, y, Math.Min(cell, r.Right - x), Math.Min(cell, r.Bottom - y)));
        }
    }
}
