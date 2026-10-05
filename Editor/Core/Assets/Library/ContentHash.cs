using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace Editor.Core.Assets.Library
{
    /// <summary>
    /// SHA-256 content hashes (#54): lowercase hex of the raw source-file bytes — the identity of an asset in the global
    /// library and the join key between a project's .vmeta and the library catalog. Streams the file (FBX/WAV files can
    /// be hundreds of MB). Poly Haven publishes SHA-256 per file too, so store downloads can be verified for free.
    /// </summary>
    public static class ContentHash
    {
        private const int BufferSize = 1 << 20;

        /// <summary>Hash of a file's bytes, or null when the file can't be read.</summary>
        public static string OfFile(string path, CancellationToken cancel = default)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan))
                    return OfStream(fs, cancel);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        public static string OfStream(Stream s, CancellationToken cancel = default)
        {
            using (var sha = SHA256.Create())
            {
                var buf = new byte[BufferSize];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    sha.TransformBlock(buf, 0, n, null, 0);
                }
                sha.TransformFinalBlock(buf, 0, 0);
                return Hex(sha.Hash);
            }
        }

        public static string OfBytes(byte[] data)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data ?? new byte[0]));
        }

        /// <summary>Copy <paramref name="source"/> to <paramref name="destination"/> and hash the bytes on the way (one read).
        /// Returns the hash of what was written.</summary>
        public static string CopyAndHash(string source, string destination, CancellationToken cancel = default)
        {
            using (var sha = SHA256.Create())
            using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan))
            using (var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                var buf = new byte[BufferSize];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    sha.TransformBlock(buf, 0, n, null, 0);
                    dst.Write(buf, 0, n);
                }
                sha.TransformFinalBlock(buf, 0, 0);
                dst.Flush(true);
                return Hex(sha.Hash);
            }
        }

        public static bool IsValid(string hash)
        {
            if (hash == null || hash.Length != 64) return false;
            foreach (char c in hash) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        public static string Hex(byte[] bytes)
        {
            const string digits = "0123456789abcdef";
            var c = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++) { c[i * 2] = digits[bytes[i] >> 4]; c[i * 2 + 1] = digits[bytes[i] & 15]; }
            return new string(c);
        }

        /// <summary>File stamp the hash was computed from: size + last write time (UTC ticks). A different stamp means the
        /// stored hash is stale; a rename/move keeps the stamp, so it keeps the hash.</summary>
        public static bool TryStamp(string path, out long size, out long writeTicksUtc)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { size = 0; writeTicksUtc = 0; return false; }
                size = fi.Length; writeTicksUtc = fi.LastWriteTimeUtc.Ticks;
                return true;
            }
            catch { size = 0; writeTicksUtc = 0; return false; }
        }
    }
}
