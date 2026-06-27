using System;
using System.Collections.Generic;
using System.IO;
using Editor.Core.Services.Build;

namespace Editor.Core.Services
{
    /// <summary>
    /// In-RAM virtual file system for a shipped game. The standalone player mounts the game's .vpak at
    /// startup — every asset is decompressed into memory here — and all asset loading is served from RAM
    /// instead of disk. In the editor this is never mounted, so the editor keeps reading loose project files.
    ///
    /// Keys are project-root-relative, forward-slash, case-insensitive (e.g. "Assets/Scenes/Main.vscene",
    /// "project.vortex"). Absolute paths are resolved against the mounted root before lookup.
    /// </summary>
    public static class AssetVfs
    {
        private static Dictionary<string, byte[]> _files;
        private static string _root; // the (virtual) project root = folder the .vpak sits in

        public static bool IsMounted => _files != null;
        public static int FileCount => _files != null ? _files.Count : 0;

        /// <summary>Load a .vpak fully into RAM. Root = the pak's folder (the game/project root).</summary>
        public static void Mount(string vpakPath)
        {
            _files = VortexPak.Read(vpakPath);
            _root = Path.GetDirectoryName(Path.GetFullPath(vpakPath));
        }

        public static void Unmount()
        {
            _files = null;
            _root = null;
        }

        /// <summary>Fetch bytes for a path (absolute under the root, or already project-relative). False if not packed.</summary>
        public static bool TryGetBytes(string path, out byte[] data)
        {
            data = null;
            if (_files == null || string.IsNullOrEmpty(path)) return false;
            return _files.TryGetValue(ToKey(path), out data);
        }

        public static bool Contains(string path)
        {
            return _files != null && !string.IsNullOrEmpty(path) && _files.ContainsKey(ToKey(path));
        }

        /// <summary>True if the path is in the mounted pak OR on disk — use this for asset existence guards.</summary>
        public static bool Exists(string path)
        {
            return Contains(path) || (!string.IsNullOrEmpty(path) && File.Exists(path));
        }

        public static string GetText(string path)
        {
            if (!TryGetBytes(path, out var b)) return null;
            // BOM-aware decode so packed text matches File.ReadAllText (which strips the UTF-8 BOM).
            using (var ms = new MemoryStream(b))
            using (var sr = new StreamReader(ms, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                return sr.ReadToEnd();
        }

        private static string ToKey(string path)
        {
            string p = path;
            try
            {
                if (Path.IsPathRooted(p) && !string.IsNullOrEmpty(_root))
                {
                    var full = Path.GetFullPath(p);
                    if (full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                        p = full.Substring(_root.Length);
                }
            }
            catch { /* fall through with the raw path */ }
            return VortexPak.Normalize(p);
        }
    }
}
