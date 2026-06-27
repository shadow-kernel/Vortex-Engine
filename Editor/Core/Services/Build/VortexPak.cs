using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Editor.Core.Services.Build
{
    /// <summary>
    /// Vortex binary asset package (.vpak). Every asset (scenes, materials, meshes, textures, the project
    /// manifest, the compiled gameplay DLL) is stored as one opaque, compressed + obfuscated blob so a
    /// shipped game contains NO readable/editable source files (no plain-text .obj/.vscene/.cs). The player
    /// loads the whole pak into RAM at startup and serves bytes from memory — nothing is written to disk.
    ///
    /// Layout (little-endian):
    ///   'V''P''A''K' | int version | int flags | int entryCount
    ///   per entry: int pathLen, pathBytes(UTF8), int rawLen, int storedLen, storedBytes
    ///   storedBytes = XOR-obfuscate( Deflate(rawBytes) , entryIndex )
    ///
    /// The XOR keystream is derived from an embedded key + the entry index. This is obfuscation (defeats
    /// casual reading/editing), not bank-grade crypto — like every client-side asset, a determined attacker
    /// with the binary can recover the key; the point is that you can't just open the .obj in a text editor.
    /// </summary>
    public static class VortexPak
    {
        private static readonly byte[] Magic = { (byte)'V', (byte)'P', (byte)'A', (byte)'K' };
        private const int Version = 1;
        private const int FlagDeflateXor = 1;

        // Embedded obfuscation key.
        private static readonly byte[] Key =
        {
            0x56,0x6F,0x72,0x74,0x65,0x78,0x45,0x6E,0x67,0x69,0x6E,0x65,0x50,0x61,0x6B,0x21,
            0x9C,0x3B,0xE7,0x14,0xA2,0x5D,0xF0,0x88
        };

        /// <summary>Write a pak from (relativePath -> rawBytes) entries.</summary>
        public static void Write(string vpakPath, IEnumerable<KeyValuePair<string, byte[]>> entries)
        {
            var list = entries.ToList();
            using (var fs = File.Create(vpakPath))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Magic);
                bw.Write(Version);
                bw.Write(FlagDeflateXor);
                bw.Write(list.Count);

                for (int i = 0; i < list.Count; i++)
                {
                    var rel = Normalize(list[i].Key);
                    var raw = list[i].Value ?? new byte[0];
                    var pathBytes = Encoding.UTF8.GetBytes(rel);
                    var stored = Xor(Deflate(raw), i);

                    bw.Write(pathBytes.Length);
                    bw.Write(pathBytes);
                    bw.Write(raw.Length);
                    bw.Write(stored.Length);
                    bw.Write(stored);
                }
            }
        }

        /// <summary>Read a pak fully into RAM as (relativePath -> rawBytes), case-insensitive keys.</summary>
        public static Dictionary<string, byte[]> Read(string vpakPath)
        {
            var dict = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using (var fs = File.OpenRead(vpakPath))
            using (var br = new BinaryReader(fs))
            {
                var magic = br.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] ||
                    magic[2] != Magic[2] || magic[3] != Magic[3])
                    throw new InvalidDataException("Not a Vortex asset pak.");

                int version = br.ReadInt32();
                int flags = br.ReadInt32();
                int count = br.ReadInt32();
                if (version != Version) throw new InvalidDataException("Unsupported vpak version " + version);

                for (int i = 0; i < count; i++)
                {
                    int pathLen = br.ReadInt32();
                    var rel = Encoding.UTF8.GetString(br.ReadBytes(pathLen));
                    int rawLen = br.ReadInt32();
                    int storedLen = br.ReadInt32();
                    var stored = br.ReadBytes(storedLen);
                    var raw = Inflate(Xor(stored, i), rawLen);
                    dict[Normalize(rel)] = raw;
                }
            }
            return dict;
        }

        public static string Normalize(string p)
        {
            if (string.IsNullOrEmpty(p)) return string.Empty;
            return p.Replace('\\', '/').TrimStart('/');
        }

        // --- Deflate / XOR helpers ---

        private static byte[] Deflate(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true))
                    ds.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        private static byte[] Inflate(byte[] data, int rawLen)
        {
            var outBuf = new byte[rawLen];
            using (var ms = new MemoryStream(data))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            {
                int off = 0, r;
                while (off < rawLen && (r = ds.Read(outBuf, off, rawLen - off)) > 0) off += r;
            }
            return outBuf;
        }

        // Symmetric: XOR with a keystream mixed from the embedded key + entry index + position.
        private static byte[] Xor(byte[] data, int entryIndex)
        {
            var o = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                byte k = (byte)(Key[i % Key.Length] ^ (byte)(entryIndex * 131 + i * 31 + (i >> 5)));
                o[i] = (byte)(data[i] ^ k);
            }
            return o;
        }
    }
}
