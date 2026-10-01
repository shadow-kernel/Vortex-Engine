using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Editor.Core.Audio;
using Editor.DllWrapper;

namespace VortexEditor.Panels.AssetBrowser
{
    /// <summary>
    /// Audio tiles (port of the WPF browser's waveform previews): the clip's peak waveform is decoded on a worker
    /// thread (at most two at a time — a folder of long files must not spawn dozens of decodes), rendered into a
    /// small bitmap on the UI thread and cached with the clip's info tooltip ("WAV · 0:02 · 44 kHz · mono").
    /// </summary>
    public static class WaveformThumbs
    {
        public const int Size = 128;
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(2);
        private static readonly Dictionary<string, (Bitmap bmp, string tip)> Cache = new Dictionary<string, (Bitmap, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary><paramref name="ready"/>(bitmap or null, tooltip or null) runs on the UI thread.</summary>
        public static void Request(string fullPath, Action<Bitmap, string> ready)
        {
            if (string.IsNullOrEmpty(fullPath) || ready == null || !File.Exists(fullPath)) return;
            string key;
            try { key = fullPath + "|" + File.GetLastWriteTimeUtc(fullPath).Ticks; } catch { key = fullPath; }
            lock (Cache)
                if (Cache.TryGetValue(key, out var hit)) { ready(hit.bmp, hit.tip); return; }
            Task.Run(async () =>
            {
                await Gate.WaitAsync();
                float[] peaks = null; string tip = null;
                try
                {
                    peaks = VortexAudio.GetWaveform(fullPath, Size);
                    if (VortexAudio.GetClipInfo(fullPath, out float duration, out int rate, out int channels))
                    {
                        int mins = (int)(duration / 60), secs = (int)(duration % 60);
                        tip = string.Format(CultureInfo.InvariantCulture, "{0} · {1}:{2:D2} · {3} kHz · {4}",
                            Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant(), mins, secs, rate / 1000, channels == 1 ? "mono" : channels + " ch");
                    }
                }
                catch { }
                finally { Gate.Release(); }
                Dispatcher.UIThread.Post(() =>
                {
                    Bitmap bmp = peaks != null ? Render(peaks) : null;
                    lock (Cache) Cache[key] = (bmp, tip);
                    try { ready(bmp, tip); } catch { }
                }, DispatcherPriority.Background);
            });
        }

        /// <summary>Tooltip of a sound container tile ("Sound Container · 3 clips · double-click to edit").</summary>
        public static string ContainerTip(string fullPath)
        {
            try { var c = SoundContainer.Load(fullPath); return "Sound Container · " + c.Entries.Count + (c.Entries.Count == 1 ? " clip" : " clips") + " · double-click to edit"; }
            catch { return "Sound Container · double-click to edit"; }
        }

        /// <summary>Centre-mirrored peak waveform as thin bars on a transparent background (reads on both themes):
        /// peaks are compressed so quiet passages stay visible and loud clips don't turn into a solid block.</summary>
        private static Bitmap Render(float[] peaks)
        {
            try
            {
                const int w = 192, h = 192, bars = 28, barW = 4, gap = 2, margin = (w - bars * (barW + gap)) / 2;
                var px = new int[w * h];
                int core = Premul(0xFF, 0xFF, 0x6B, 0x5A), outer = Premul(0xA8, 0xFF, 0x6B, 0x5A), centre = Premul(0x50, 0xFF, 0x6B, 0x5A);
                int mid = h / 2;
                for (int b = 0; b < bars; b++)
                {
                    int from = b * peaks.Length / bars, to = Math.Max(from + 1, (b + 1) * peaks.Length / bars);
                    float peak = 0f;
                    for (int i = from; i < to && i < peaks.Length; i++) peak = Math.Max(peak, peaks[i]);
                    peak = (float)Math.Pow(Math.Clamp(peak, 0f, 1f), 0.75);
                    int half = Math.Max(2, (int)(peak * h * 0.33f));
                    int x0 = margin + b * (barW + gap) + gap / 2;
                    for (int x = x0; x < x0 + barW && x < w; x++)
                        for (int y = mid - half; y <= mid + half; y++)
                            if (y >= 0 && y < h) px[y * w + x] = Math.Abs(y - mid) > half * 2 / 3 ? outer : core;
                }
                for (int x = margin; x < w - margin; x++) if (px[mid * w + x] == 0) px[mid * w + x] = centre;
                var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var fb = wb.Lock())
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(px, y * w, IntPtr.Add(fb.Address, y * fb.RowBytes), w);
                return wb;
            }
            catch { return null; }
        }

        private static int Premul(byte a, byte r, byte g, byte b)
            => unchecked((int)(((uint)a << 24) | ((uint)(r * a / 255) << 16) | ((uint)(g * a / 255) << 8) | (uint)(b * a / 255)));
    }

    /// <summary>
    /// Previews for the Windows editor's built-in pseudo assets listed in the Textures / Materials tabs
    /// ("Texture:White", "Material:Default", …): flat colour tiles, a checkerboard and shaded spheres.
    /// </summary>
    public static class BuiltInSwatches
    {
        public static readonly (string path, string name, string label, string hex)[] Materials =
        {
            ("Material:Default", "Default", "Standard", "#BD63C5"),
            ("Material:UnlitWhite", "Unlit White", "Unlit", "#FFFFFF"),
            ("Material:Grid", "Grid", "Standard", "#4EC9B0"),
        };
        public static readonly (string path, string name, string label, string hex)[] Textures =
        {
            ("Texture:White", "White", "Solid Color", "#FFFFFF"),
            ("Texture:Black", "Black", "Solid Color", "#333333"),
            ("Texture:Normal", "Normal", "Normal Map", "#8080FF"),
            ("Texture:Checker", "Checker", "Pattern", "#808080"),
        };

        private static readonly Dictionary<string, Bitmap> Cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The swatch for a built-in pseudo path (UI thread), or null.</summary>
        public static Bitmap For(string pseudoPath)
        {
            if (string.IsNullOrEmpty(pseudoPath)) return null;
            if (Cache.TryGetValue(pseudoPath, out var b)) return b;
            Bitmap bmp = null;
            foreach (var m in Materials) if (m.path == pseudoPath) bmp = Sphere(Color.Parse(m.hex));
            foreach (var t in Textures) if (t.path == pseudoPath) bmp = t.name == "Checker" ? Checker() : Flat(Color.Parse(t.hex));
            if (bmp != null) Cache[pseudoPath] = bmp;
            return bmp;
        }

        private const int S = 128;

        private static Bitmap Draw(Action<DrawingContext> draw)
        {
            var rtb = new RenderTargetBitmap(new PixelSize(S, S), new Vector(96, 96));
            using (var dc = rtb.CreateDrawingContext()) draw(dc);
            return rtb;
        }

        private static Bitmap Flat(Color c) => Draw(dc => dc.FillRectangle(new SolidColorBrush(c), new Rect(0, 0, S, S)));

        private static Bitmap Checker() => Draw(dc =>
        {
            dc.FillRectangle(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), new Rect(0, 0, S, S));
            var dark = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
            const int n = 8; double s = (double)S / n;
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) if (((x + y) & 1) == 0) dc.FillRectangle(dark, new Rect(x * s, y * s, s, s));
        });

        private static Bitmap Sphere(Color c) => Draw(dc =>
        {
            dc.FillRectangle(new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x19)), new Rect(0, 0, S, S));
            var brush = new RadialGradientBrush
            {
                GradientOrigin = new RelativePoint(0.36, 0.30, RelativeUnit.Relative),
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.62, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.62, RelativeUnit.Relative),
            };
            brush.GradientStops.Add(new GradientStop(Mix(c, Colors.White, 0.55), 0.0));
            brush.GradientStops.Add(new GradientStop(c, 0.55));
            brush.GradientStops.Add(new GradientStop(Mix(c, Colors.Black, 0.6), 1.0));
            dc.DrawEllipse(brush, null, new Point(S / 2.0, S / 2.0 + 2), S * 0.36, S * 0.36);
        });

        private static Color Mix(Color a, Color b, double t)
            => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }
}
