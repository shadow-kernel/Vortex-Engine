using System;
using System.IO;
using System.Text;

namespace Editor.Core.Assets.Library
{
    /// <summary>Reads the pixel size of an image from its header (no decoding): PNG, JPEG, TGA, BMP, DDS, PSD, HDR.</summary>
    public static class ImageInfo
    {
        public static bool TryReadSize(string path, out int width, out int height)
        {
            width = height = 0;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var head = new byte[64];
                    int n = fs.Read(head, 0, head.Length);
                    if (n < 24) return false;
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    // PNG: signature + IHDR
                    if (head[0] == 0x89 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G')
                    { width = BE32(head, 16); height = BE32(head, 20); return width > 0 && height > 0; }
                    // JPEG: walk the segments to the first SOFn
                    if (head[0] == 0xFF && head[1] == 0xD8) return Jpeg(fs, out width, out height);
                    if (head[0] == (byte)'B' && head[1] == (byte)'M')
                    { width = LE32(head, 18); height = Math.Abs(LE32(head, 22)); return width > 0 && height > 0; }
                    if (head[0] == (byte)'D' && head[1] == (byte)'D' && head[2] == (byte)'S' && head[3] == (byte)' ')
                    { height = LE32(head, 12); width = LE32(head, 16); return width > 0 && height > 0; }
                    if (head[0] == (byte)'8' && head[1] == (byte)'B' && head[2] == (byte)'P' && head[3] == (byte)'S')
                    { height = BE32(head, 14); width = BE32(head, 18); return width > 0 && height > 0; }
                    if (head[0] == (byte)'#' && head[1] == (byte)'?') return Hdr(path, out width, out height);
                    if (ext == ".tga")
                    { width = head[12] | (head[13] << 8); height = head[14] | (head[15] << 8); return width > 0 && height > 0; }
                }
            }
            catch { }
            return false;
        }

        private static bool Jpeg(Stream s, out int width, out int height)
        {
            width = height = 0;
            s.Position = 2;
            var b = new byte[9];
            for (int guard = 0; guard < 512; guard++)
            {
                int m1 = s.ReadByte();
                if (m1 < 0) return false;
                if (m1 != 0xFF) continue;
                int marker = s.ReadByte();
                while (marker == 0xFF) marker = s.ReadByte();
                if (marker < 0) return false;
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                if (s.Read(b, 0, 2) < 2) return false;
                int len = (b[0] << 8) | b[1];
                bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (sof)
                {
                    if (s.Read(b, 0, 5) < 5) return false;
                    height = (b[1] << 8) | b[2]; width = (b[3] << 8) | b[4];
                    return width > 0 && height > 0;
                }
                s.Position += len - 2;
            }
            return false;
        }

        private static bool Hdr(string path, out int width, out int height)
        {
            width = height = 0;
            using (var r = new StreamReader(path, Encoding.ASCII))
            {
                for (int i = 0; i < 64; i++)
                {
                    var line = r.ReadLine();
                    if (line == null) return false;
                    var p = line.Trim().Split(' ');
                    if (p.Length == 4 && (p[0] == "-Y" || p[0] == "+Y"))
                    {
                        int.TryParse(p[1], out height); int.TryParse(p[3], out width);
                        return width > 0 && height > 0;
                    }
                }
            }
            return false;
        }

        private static int BE32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        private static int LE32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
    }
}
