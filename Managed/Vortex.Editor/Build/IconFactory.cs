using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Editor.Core.Data;
using Editor.Core.Services.Build;

namespace VortexEditor.Build
{
    /// <summary>
    /// Turns the project's game icon (a PNG) into the per-platform icon files the packager needs: .icns for macOS
    /// (via iconutil), .ico for Windows (built in managed code), sized PNGs for Linux. Falls back to the engine
    /// logo when the project has no icon. Image work goes through Avalonia's bitmap decoder (cross-platform).
    /// </summary>
    public static class IconFactory
    {
        public sealed class Result { public string IcnsPath, IcoPath, PngPath, SourceDescription; public List<string> Warnings = new List<string>(); }

        /// <summary>Resolve the project's icon source: Settings.IconPath, else the project thumbnail (.ve/icon.png), else the engine logo.</summary>
        public static byte[] LoadSource(ProjectData project, out string description)
        {
            description = "engine logo";
            try
            {
                var st = project?.Settings;
                if (project != null && st != null && !string.IsNullOrWhiteSpace(st.IconPath))
                {
                    string p = Path.IsPathRooted(st.IconPath) ? st.IconPath : Path.Combine(project.Path, st.IconPath);
                    if (File.Exists(p)) { description = st.IconPath; return File.ReadAllBytes(p); }
                }
                if (project != null)
                {
                    string thumb = Path.Combine(project.Path, ".ve", "icon.png");
                    if (File.Exists(thumb)) { description = ".ve/icon.png (project thumbnail)"; return File.ReadAllBytes(thumb); }
                }
            }
            catch { }
            using var s = AssetLoader.Open(new Uri("avares://Vortex.Editor/Assets/Logo.png"));
            using var ms = new MemoryStream(); s.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>Build every icon flavour into <paramref name="workDir"/>.</summary>
        public static Result Build(ProjectData project, string workDir)
        {
            var r = new Result();
            Directory.CreateDirectory(workDir);
            byte[] src = LoadSource(project, out r.SourceDescription);
            try
            {
                r.PngPath = Path.Combine(workDir, "icon.png");
                File.WriteAllBytes(r.PngPath, EncodePng(src, 512));
            }
            catch (Exception ex) { r.Warnings.Add("PNG icon: " + ex.Message); r.PngPath = null; }
            try { r.IcoPath = Path.Combine(workDir, "icon.ico"); File.WriteAllBytes(r.IcoPath, BuildIco(src)); }
            catch (Exception ex) { r.Warnings.Add("Windows icon: " + ex.Message); r.IcoPath = null; }
            if (OperatingSystem.IsMacOS())
            {
                try { r.IcnsPath = BuildIcns(src, workDir, r.Warnings); }
                catch (Exception ex) { r.Warnings.Add("macOS icon: " + ex.Message); r.IcnsPath = null; }
            }
            return r;
        }

        /// <summary>Decode + scale to a square PNG of the given size.</summary>
        public static byte[] EncodePng(byte[] source, int size)
        {
            using var bmp = Decode(source, size);
            using var ms = new MemoryStream();
            bmp.Save(ms);
            return ms.ToArray();
        }

        public static byte[] BuildIco(byte[] source)
        {
            var bitmaps = new List<IconContainer.Bitmap32>();
            foreach (int size in new[] { 16, 24, 32, 48, 64, 128 })
            {
                using var bmp = Decode(source, size);
                bitmaps.Add(new IconContainer.Bitmap32 { Width = bmp.PixelSize.Width, Height = bmp.PixelSize.Height, Bgra = Pixels(bmp) });
            }
            return IconContainer.BuildIco(bitmaps, EncodePng(source, 256));
        }

        private static string BuildIcns(byte[] source, string workDir, List<string> warnings)
        {
            string iconset = Path.Combine(workDir, "AppIcon.iconset");
            if (Directory.Exists(iconset)) Directory.Delete(iconset, true);
            Directory.CreateDirectory(iconset);
            foreach (int s in new[] { 16, 32, 128, 256, 512 })
            {
                File.WriteAllBytes(Path.Combine(iconset, "icon_" + s + "x" + s + ".png"), EncodePng(source, s));
                File.WriteAllBytes(Path.Combine(iconset, "icon_" + s + "x" + s + "@2x.png"), EncodePng(source, s * 2));
            }
            string icns = Path.Combine(workDir, "AppIcon.icns");
            try { File.Delete(icns); } catch { }
            var psi = new ProcessStartInfo("iconutil", "-c icns \"" + iconset + "\" -o \"" + icns + "\"") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd(); p.WaitForExit(60000);
                if (p.ExitCode != 0 || !File.Exists(icns)) { warnings.Add("iconutil failed: " + err.Trim()); return null; }
            }
            return icns;
        }

        private static Bitmap Decode(byte[] source, int size)
        {
            using var ms = new MemoryStream(source);
            var bmp = Bitmap.DecodeToWidth(ms, size, BitmapInterpolationMode.HighQuality);
            if (bmp.PixelSize.Width == size && bmp.PixelSize.Height == size) return bmp;
            // non-square or off-by-one after scaling: letterbox onto a square canvas
            var square = new RenderTargetBitmap(new Avalonia.PixelSize(size, size), new Avalonia.Vector(96, 96));
            using (var ctx = square.CreateDrawingContext())
            {
                double sw = bmp.PixelSize.Width, sh = bmp.PixelSize.Height, k = Math.Min(size / sw, size / sh);
                double w = sw * k, h = sh * k;
                ctx.DrawImage(bmp, new Avalonia.Rect(0, 0, sw, sh), new Avalonia.Rect((size - w) / 2, (size - h) / 2, w, h));
            }
            bmp.Dispose();
            return square;
        }

        private static byte[] Pixels(Bitmap bmp)
        {
            int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height, stride = w * 4;
            var buf = new byte[stride * h];
            unsafe
            {
                fixed (byte* p = buf) bmp.CopyPixels(new Avalonia.PixelRect(0, 0, w, h), (IntPtr)p, buf.Length, stride);
            }
            // Avalonia hands back premultiplied BGRA on most backends; un-premultiply so the DIB alpha reads right.
            for (int i = 0; i < buf.Length; i += 4)
            {
                int a = buf[i + 3];
                if (a > 0 && a < 255) { buf[i] = (byte)Math.Min(255, buf[i] * 255 / a); buf[i + 1] = (byte)Math.Min(255, buf[i + 1] * 255 / a); buf[i + 2] = (byte)Math.Min(255, buf[i + 2] * 255 / a); }
            }
            return buf;
        }
    }
}
