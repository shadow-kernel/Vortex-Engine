using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace VortexEditor.Shell.ModelTools
{
    /// <summary>Which channel(s) of a texture a view shows: the image itself, or one channel as greyscale.</summary>
    public enum ChannelView { RGB, R, G, B, A }

    /// <summary>
    /// A texture file decoded to 8-bit straight-alpha RGBA (for the texture editor / asset viewer: zoom, channel views,
    /// alpha over a checkerboard) plus what the file header says about it (format, source channels, bit depth, mips).
    /// PNG / JPEG / BMP / GIF / WebP / ICO decode through Skia, TGA and Radiance HDR through small built-in decoders;
    /// DDS / EXR / other formats report their header info with <see cref="Error"/> set (no preview).
    /// </summary>
    public sealed class TextureImage
    {
        public int Width, Height;
        /// <summary>Straight-alpha RGBA8, top row first (null when the format can't be decoded).</summary>
        public byte[] Rgba;
        public string FormatName = "Unknown";
        /// <summary>Human description of the stored pixel format ("RGBA 8-bit", "Grey 16-bit", "BC7", …).</summary>
        public string PixelFormat = "";
        public int SourceChannels;
        public bool HasAlpha;
        public int StoredMipLevels;   // DDS only (0 = unknown)
        public long FileSize;
        public string Error;

        public bool CanPreview => Rgba != null && Width > 0 && Height > 0;
        /// <summary>Levels a full mip chain would have (floor(log2(max dim)) + 1).</summary>
        public int FullMipChain => Width > 0 && Height > 0 ? (int)Math.Floor(Math.Log(Math.Max(Width, Height), 2)) + 1 : 0;

        public static Task<TextureImage> LoadAsync(string path) => Task.Run(() => Load(path));

        public static TextureImage Load(string path)
        {
            var img = new TextureImage();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { img.Error = "File not found"; return img; }
                img.FileSize = new FileInfo(path).Length;
                string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
                byte[] head = ReadHead(path, 256);
                Sniff(head, ext, img);
                if (ext == ".tga") DecodeTga(File.ReadAllBytes(path), img);
                else if (ext == ".hdr" || StartsWith(head, "#?RADIANCE") || StartsWith(head, "#?RGBE")) DecodeHdr(File.ReadAllBytes(path), img);
                else if (ext == ".dds" || ext == ".exr" || ext == ".psd" || ext == ".tif" || ext == ".tiff")
                {
                    if (!DecodeSkia(path, img)) img.Error = "No preview for " + img.FormatName + " files (header information only).";
                }
                else if (!DecodeSkia(path, img) && img.Error == null) img.Error = "This image could not be decoded.";
                if (img.Rgba != null && !img.HasAlpha) img.HasAlpha = ScanAlpha(img.Rgba);
            }
            catch (Exception ex) { img.Error = ex.Message; img.Rgba = null; }
            return img;
        }

        // ------------------------------------------------------------------ display bitmaps

        /// <summary>A premultiplied BGRA bitmap of the chosen view (single channels as opaque greyscale). UI thread.</summary>
        public WriteableBitmap ToBitmap(ChannelView view)
        {
            if (!CanPreview) return null;
            var wb = new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = wb.Lock())
            {
                var row = new byte[Width * 4];
                for (int y = 0; y < Height; y++)
                {
                    int s = y * Width * 4;
                    for (int x = 0; x < Width; x++, s += 4)
                    {
                        byte r = Rgba[s], g = Rgba[s + 1], b = Rgba[s + 2], a = Rgba[s + 3];
                        int d = x * 4;
                        switch (view)
                        {
                            case ChannelView.R: row[d] = row[d + 1] = row[d + 2] = r; row[d + 3] = 255; break;
                            case ChannelView.G: row[d] = row[d + 1] = row[d + 2] = g; row[d + 3] = 255; break;
                            case ChannelView.B: row[d] = row[d + 1] = row[d + 2] = b; row[d + 3] = 255; break;
                            case ChannelView.A: row[d] = row[d + 1] = row[d + 2] = a; row[d + 3] = 255; break;
                            default:
                                row[d] = (byte)(b * a / 255); row[d + 1] = (byte)(g * a / 255); row[d + 2] = (byte)(r * a / 255); row[d + 3] = a;
                                break;
                        }
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, IntPtr.Add(fb.Address, y * fb.RowBytes), row.Length);
                }
            }
            return wb;
        }

        // ------------------------------------------------------------------ Skia (PNG / JPEG / BMP / GIF / WebP / ICO)

        private static bool DecodeSkia(string path, TextureImage img)
        {
            using (var codec = SKCodec.Create(path))
            {
                if (codec == null) return false;
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                using (var bmp = new SKBitmap(info))
                {
                    var r = codec.GetPixels(info, bmp.GetPixels());
                    if (r != SKCodecResult.Success && r != SKCodecResult.IncompleteInput) return false;
                    img.Width = info.Width; img.Height = info.Height;
                    img.Rgba = bmp.Bytes;
                    if (img.FormatName == "Unknown") img.FormatName = codec.EncodedFormat.ToString().ToUpperInvariant();
                    if (string.IsNullOrEmpty(img.PixelFormat))
                    {
                        bool alpha = codec.Info.AlphaType != SKAlphaType.Opaque;
                        img.SourceChannels = codec.Info.ColorType == SKColorType.Gray8 ? 1 : alpha ? 4 : 3;
                        img.PixelFormat = (img.SourceChannels == 1 ? "Grey" : alpha ? "RGBA" : "RGB") + " 8-bit";
                    }
                    return true;
                }
            }
        }

        // ------------------------------------------------------------------ header sniffing

        private static byte[] ReadHead(string path, int n)
        {
            using (var fs = File.OpenRead(path))
            {
                var b = new byte[(int)Math.Min(n, fs.Length)];
                int read = 0; while (read < b.Length) { int k = fs.Read(b, read, b.Length - read); if (k <= 0) break; read += k; }
                return b;
            }
        }

        private static bool StartsWith(byte[] b, string ascii)
        {
            if (b.Length < ascii.Length) return false;
            for (int i = 0; i < ascii.Length; i++) if (b[i] != (byte)ascii[i]) return false;
            return true;
        }

        private static int BE16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
        private static int BE32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        private static int LE16(byte[] b, int o) => b[o] | (b[o + 1] << 8);
        private static int LE32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

        private static void Sniff(byte[] h, string ext, TextureImage img)
        {
            if (h.Length >= 26 && h[0] == 0x89 && h[1] == (byte)'P' && h[2] == (byte)'N' && h[3] == (byte)'G')
            {
                img.FormatName = "PNG";
                img.Width = BE32(h, 16); img.Height = BE32(h, 20);
                int depth = h[24], type = h[25];
                switch (type)
                {
                    case 0: img.SourceChannels = 1; img.PixelFormat = "Grey " + depth + "-bit"; break;
                    case 2: img.SourceChannels = 3; img.PixelFormat = "RGB " + depth + "-bit"; break;
                    case 3: img.SourceChannels = 3; img.PixelFormat = "Indexed " + depth + "-bit"; break;
                    case 4: img.SourceChannels = 2; img.PixelFormat = "Grey + alpha " + depth + "-bit"; img.HasAlpha = true; break;
                    case 6: img.SourceChannels = 4; img.PixelFormat = "RGBA " + depth + "-bit"; break;
                }
                return;
            }
            if (h.Length >= 4 && h[0] == 0xFF && h[1] == 0xD8)
            {
                img.FormatName = "JPEG";
                for (int o = 2; o + 9 < h.Length;)
                {
                    if (h[o] != 0xFF) { o++; continue; }
                    int m = h[o + 1];
                    if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
                    {
                        int prec = h[o + 4]; img.Height = BE16(h, o + 5); img.Width = BE16(h, o + 7);
                        img.SourceChannels = o + 9 < h.Length ? h[o + 9] : 3;
                        img.PixelFormat = (img.SourceChannels == 1 ? "Grey" : img.SourceChannels == 4 ? "CMYK" : "RGB") + " " + prec + "-bit";
                        break;
                    }
                    if (m == 0xD8 || (m >= 0xD0 && m <= 0xD7)) { o += 2; continue; }
                    o += 2 + BE16(h, o + 2);
                }
                return;
            }
            if (h.Length >= 30 && h[0] == (byte)'B' && h[1] == (byte)'M')
            {
                img.FormatName = "BMP"; img.Width = LE32(h, 18); img.Height = Math.Abs(LE32(h, 22));
                int bpp = LE16(h, 28); img.SourceChannels = bpp == 32 ? 4 : 3; img.PixelFormat = bpp + " bpp";
                return;
            }
            if (h.Length >= 10 && StartsWith(h, "GIF8")) { img.FormatName = "GIF"; img.Width = LE16(h, 6); img.Height = LE16(h, 8); img.SourceChannels = 4; img.PixelFormat = "Indexed 8-bit"; return; }
            if (h.Length >= 12 && StartsWith(h, "RIFF") && h[8] == (byte)'W' && h[9] == (byte)'E' && h[10] == (byte)'B' && h[11] == (byte)'P') { img.FormatName = "WEBP"; return; }
            if (h.Length >= 128 && StartsWith(h, "DDS "))
            {
                img.FormatName = "DDS"; img.Height = LE32(h, 12); img.Width = LE32(h, 16); img.StoredMipLevels = Math.Max(1, LE32(h, 28));
                string fourCC = Encoding.ASCII.GetString(h, 84, 4).TrimEnd('\0');
                if (fourCC == "DX10" && h.Length >= 132)
                {
                    int dxgi = LE32(h, 128);
                    img.PixelFormat = DxgiName(dxgi);
                }
                else img.PixelFormat = string.IsNullOrWhiteSpace(fourCC) ? LE32(h, 88) + " bpp (uncompressed)" : fourCC;
                img.SourceChannels = 4;
                return;
            }
            if (StartsWith(h, "#?RADIANCE") || StartsWith(h, "#?RGBE")) { img.FormatName = "Radiance HDR"; img.PixelFormat = "RGBE (HDR, 32-bit float)"; img.SourceChannels = 3; return; }
            if (h.Length >= 4 && h[0] == 0x76 && h[1] == 0x2F && h[2] == 0x31 && h[3] == 0x01) { img.FormatName = "OpenEXR"; img.PixelFormat = "Float (HDR)"; return; }
            if (h.Length >= 4 && StartsWith(h, "8BPS")) { img.FormatName = "PSD"; img.Height = BE32(h, 14); img.Width = BE32(h, 18); return; }
            if (ext == ".tga" && h.Length >= 18)
            {
                img.FormatName = "TGA"; img.Width = LE16(h, 12); img.Height = LE16(h, 14);
                int bpp = h[16], type = h[2];
                img.SourceChannels = bpp == 32 ? 4 : bpp == 8 ? 1 : 3;
                img.PixelFormat = (type == 3 || type == 11 ? "Grey" : bpp == 32 ? "RGBA" : "RGB") + " 8-bit" + (type >= 9 ? " (RLE)" : "");
                return;
            }
            if (ext == ".tif" || ext == ".tiff") img.FormatName = "TIFF";
        }

        private static string DxgiName(int f)
        {
            switch (f)
            {
                case 71: case 72: return "BC1";
                case 74: case 75: return "BC2";
                case 77: case 78: return "BC3";
                case 80: return "BC4";
                case 83: return "BC5";
                case 95: case 96: return "BC6H";
                case 98: case 99: return "BC7";
                case 28: case 29: return "RGBA 8-bit";
                case 10: return "RGBA 16-bit float";
                case 2: return "RGBA 32-bit float";
                default: return "DXGI format " + f;
            }
        }

        private static bool ScanAlpha(byte[] rgba)
        {
            for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] != 255) return true;
            return false;
        }

        // ------------------------------------------------------------------ TGA (types 2, 3, 10, 11; 8/16/24/32 bpp)

        private static void DecodeTga(byte[] d, TextureImage img)
        {
            if (d.Length < 18) { img.Error = "Truncated TGA"; return; }
            int idLen = d[0], cmapType = d[1], type = d[2];
            int cmapLen = LE16(d, 5), cmapBits = d[7];
            int w = LE16(d, 12), h = LE16(d, 14), bpp = d[16], desc = d[17];
            if (cmapType != 0 || (type != 2 && type != 3 && type != 10 && type != 11) || w <= 0 || h <= 0) { img.Error = "Unsupported TGA variant (colour-mapped)"; return; }
            int bytesPer = bpp / 8;
            if (bytesPer < 1 || bytesPer > 4) { img.Error = "Unsupported TGA bit depth"; return; }
            int o = 18 + idLen + cmapLen * ((cmapBits + 7) / 8);
            var rgba = new byte[w * h * 4];
            bool topDown = (desc & 0x20) != 0;
            bool rle = type >= 9;
            int total = w * h, px = 0;
            void Put(int i, int so)
            {
                int y = i / w, x = i % w;
                int row = topDown ? y : h - 1 - y;
                int dd = (row * w + x) * 4;
                if (bytesPer == 1) { rgba[dd] = rgba[dd + 1] = rgba[dd + 2] = d[so]; rgba[dd + 3] = 255; }
                else if (bytesPer == 2) { int v = LE16(d, so); rgba[dd] = (byte)(((v >> 10) & 31) * 255 / 31); rgba[dd + 1] = (byte)(((v >> 5) & 31) * 255 / 31); rgba[dd + 2] = (byte)((v & 31) * 255 / 31); rgba[dd + 3] = 255; }
                else { rgba[dd] = d[so + 2]; rgba[dd + 1] = d[so + 1]; rgba[dd + 2] = d[so]; rgba[dd + 3] = bytesPer == 4 ? d[so + 3] : (byte)255; }
            }
            while (px < total && o < d.Length)
            {
                if (!rle) { if (o + bytesPer > d.Length) break; Put(px++, o); o += bytesPer; continue; }
                int hdr = d[o++];
                int count = (hdr & 0x7F) + 1;
                if ((hdr & 0x80) != 0)
                {
                    if (o + bytesPer > d.Length) break;
                    for (int k = 0; k < count && px < total; k++) Put(px++, o);
                    o += bytesPer;
                }
                else
                {
                    for (int k = 0; k < count && px < total; k++) { if (o + bytesPer > d.Length) break; Put(px++, o); o += bytesPer; }
                }
            }
            img.Width = w; img.Height = h; img.Rgba = rgba; img.FormatName = "TGA";
        }

        // ------------------------------------------------------------------ Radiance HDR (RGBE, flat + new RLE), tone-mapped for display

        private static void DecodeHdr(byte[] d, TextureImage img)
        {
            int o = 0; int w = 0, h = 0; bool flipY = false;
            string ReadLine()
            {
                var sb = new StringBuilder();
                while (o < d.Length && d[o] != (byte)'\n') sb.Append((char)d[o++]);
                o++;
                return sb.ToString();
            }
            string line;
            while (o < d.Length && (line = ReadLine()).Length > 0) { }   // header lines end with an empty line
            line = ReadLine();
            var parts = line.Split(' ');
            if (parts.Length == 4)
            {
                int.TryParse(parts[1], out int a); int.TryParse(parts[3], out int b);
                if (parts[0].EndsWith("Y")) { h = a; w = b; flipY = parts[0][0] == '+'; } else { w = a; h = b; }
            }
            if (w <= 0 || h <= 0) { img.Error = "Invalid HDR header"; return; }
            var rgbe = new byte[w * h * 4];
            var scan = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                if (o + 4 <= d.Length && d[o] == 2 && d[o + 1] == 2 && ((d[o + 2] << 8) | d[o + 3]) == w && w >= 8 && w < 32768)
                {
                    o += 4;
                    for (int c = 0; c < 4; c++)
                    {
                        int x = 0;
                        while (x < w && o < d.Length)
                        {
                            int n = d[o++];
                            if (n > 128) { n -= 128; byte v = o < d.Length ? d[o++] : (byte)0; for (int k = 0; k < n && x < w; k++) scan[(x++) * 4 + c] = v; }
                            else for (int k = 0; k < n && x < w && o < d.Length; k++) scan[(x++) * 4 + c] = d[o++];
                        }
                    }
                }
                else
                {
                    int n = Math.Min(scan.Length, d.Length - o);
                    if (n <= 0) break;
                    Buffer.BlockCopy(d, o, scan, 0, n); o += n;
                }
                int row = flipY ? h - 1 - y : y;
                Buffer.BlockCopy(scan, 0, rgbe, row * w * 4, scan.Length);
            }
            var rgba = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                int e = rgbe[i * 4 + 3];
                float f = e == 0 ? 0f : (float)Math.Pow(2, e - 136);
                for (int c = 0; c < 3; c++)
                {
                    float v = rgbe[i * 4 + c] * f;
                    v = v / (1f + v);                                   // Reinhard
                    rgba[i * 4 + c] = (byte)Math.Max(0, Math.Min(255, (int)(Math.Pow(v, 1 / 2.2) * 255f + 0.5f)));
                }
                rgba[i * 4 + 3] = 255;
            }
            img.Width = w; img.Height = h; img.Rgba = rgba;
            img.FormatName = "Radiance HDR"; img.PixelFormat = "RGBE (HDR, tone-mapped preview)"; img.SourceChannels = 3;
        }
    }
}
