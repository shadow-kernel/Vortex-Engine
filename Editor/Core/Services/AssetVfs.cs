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
        private static Dictionary<string, byte[]> _files;   // core pak (manifest + shared assets + scripts)
        private static string _root; // the (virtual) project root = folder the .vpak sits in
        // Additive scene packs, mounted on demand so a 100-scene game doesn't load every scene at boot — each
        // scene ships in its own Scenes/<name>.vpak and is mounted only when that scene is (about to be) loaded.
        private static readonly Dictionary<string, Dictionary<string, byte[]>> _layers =
            new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);

        public static bool IsMounted => _files != null;
        public static int FileCount => _files != null ? _files.Count : 0;
        /// <summary>The mounted game root (folder the core .vpak sits in) — used to locate scene packs.</summary>
        public static string Root => _root;

        /// <summary>Load the core .vpak fully into RAM. Root = the pak's folder (the game/project root).</summary>
        public static void Mount(string vpakPath)
        {
            _files = VortexPak.Read(vpakPath);
            _root = Path.GetDirectoryName(Path.GetFullPath(vpakPath));
        }

        /// <summary>Mount an extra pak layer (a scene pack) under a name, on demand. Idempotent + never throws.</summary>
        public static void MountLayer(string name, string vpakPath)
        {
            if (string.IsNullOrEmpty(name) || _layers.ContainsKey(name)) return;
            try { if (File.Exists(vpakPath)) _layers[name] = VortexPak.Read(vpakPath); }
            catch { /* a missing/damaged scene pack falls back to the core pak / disk */ }
        }

        /// <summary>Free a scene pack's bytes (its scene is already deserialized into RAM entities).</summary>
        public static void UnmountLayer(string name)
        {
            if (!string.IsNullOrEmpty(name)) _layers.Remove(name);
        }

        public static void Unmount()
        {
            _files = null;
            _root = null;
            _layers.Clear();
        }

        /// <summary>Fetch bytes for a path (absolute under the root, or already project-relative) — searches the
        /// core pak then any mounted scene packs. False if not packed anywhere.</summary>
        public static bool TryGetBytes(string path, out byte[] data)
        {
            data = null;
            if (string.IsNullOrEmpty(path)) return false;
            var key = ToKey(path);
            if (_files != null && _files.TryGetValue(key, out data)) return true;
            foreach (var layer in _layers.Values)
                if (layer.TryGetValue(key, out data)) return true;
            return false;
        }

        public static bool Contains(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var key = ToKey(path);
            if (_files != null && _files.ContainsKey(key)) return true;
            foreach (var layer in _layers.Values)
                if (layer.ContainsKey(key)) return true;
            return false;
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
