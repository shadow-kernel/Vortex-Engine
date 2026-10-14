using System;
using System.Collections.Generic;
using System.IO;

namespace Editor.Core.Assets
{
    /// <summary>
    /// A cache of assets loaded from files that notices when a file changes on disk (#419). Every entry keeps the size and
    /// last-write time the file had when it was loaded; <see cref="Get"/> compares them with the file again and reloads a
    /// changed one — a loaded entry at most once per <see cref="RecheckMs"/>, a failed load (file missing or unreadable) on
    /// every call, which costs one stat, the same as the loader's own existence check. So an asset rewritten by a script,
    /// git or a DCC export is picked up under its old name, and a file created after a failed request loads on the next
    /// one. <see cref="MarkAllStale"/> makes the next Get of every entry compare (play start), <see cref="Invalidate"/>
    /// drops one entry (the asset watcher saw its file change). While <paramref name="immutable"/> says so — a shipped game
    /// reads the read-only asset pak — entries are kept as loaded, failed loads included.
    /// </summary>
    internal sealed class FileStampCache<T> where T : class
    {
        private struct Entry
        {
            public T Value;
            public long Length;       // -1 = no file
            public long WriteTicks;
            public long CheckedAt;    // Environment.TickCount64 of the last comparison
            public bool Stale;        // MarkAllStale: compare on the next Get, however recent CheckedAt is
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, T> _load;
        private readonly Func<bool> _immutable;

        /// <summary>Milliseconds a loaded entry is trusted before its file is compared again.</summary>
        public long RecheckMs = 1000;

        /// <param name="load">Loads the asset from a path (null on failure).</param>
        /// <param name="immutable">True while files cannot change (asset pak mounted); null = never.</param>
        public FileStampCache(Func<string, T> load, Func<bool> immutable = null)
        {
            _load = load ?? throw new ArgumentNullException(nameof(load));
            _immutable = immutable;
        }

        public int Count => _entries.Count;

        /// <summary>The asset at <paramref name="path"/>, loaded once and again whenever its file changed. Null when it
        /// cannot be loaded.</summary>
        public T Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = Key(path);
            long now = Environment.TickCount64;
            if (_entries.TryGetValue(key, out var e))
            {
                if (_immutable != null && _immutable()) return e.Value;
                if (e.Value != null && !e.Stale && now - e.CheckedAt < RecheckMs) return e.Value;
                Stamp(path, out long length, out long ticks);
                if (length == e.Length && ticks == e.WriteTicks)
                {
                    e.CheckedAt = now;
                    e.Stale = false;
                    _entries[key] = e;
                    return e.Value;
                }
            }
            // stamp BEFORE loading: a write that lands while the file is read then still differs from the stamp next time
            Stamp(path, out long len, out long writeTicks);
            T value = _load(path);
            _entries[key] = new Entry { Value = value, Length = len, WriteTicks = writeTicks, CheckedAt = now };
            return value;
        }

        /// <summary>The cached asset without touching the disk (null when absent or a failed load).</summary>
        public T Peek(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return _entries.TryGetValue(Key(path), out var e) ? e.Value : null;
        }

        /// <summary>Drop one entry: the next <see cref="Get"/> loads the file again. True when there was one.</summary>
        public bool Invalidate(string path) => !string.IsNullOrEmpty(path) && _entries.Remove(Key(path));

        /// <summary>Make the next <see cref="Get"/> of every entry compare its file, whenever it was last checked.</summary>
        public void MarkAllStale()
        {
            if (_entries.Count == 0) return;
            foreach (var key in new List<string>(_entries.Keys))
            {
                var e = _entries[key];
                e.Stale = true;
                _entries[key] = e;
            }
        }

        public void Clear() => _entries.Clear();

        /// <summary>One key per file: rooted paths are normalised ("a/./b", "a/x/../b"), relative ones (VFS keys) kept.</summary>
        private static string Key(string path)
        {
            try { return Path.IsPathRooted(path) ? Path.GetFullPath(path) : path; }
            catch { return path; }
        }

        private static void Stamp(string path, out long length, out long writeTicks)
        {
            length = -1; writeTicks = 0;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return;
                length = fi.Length;
                writeTicks = fi.LastWriteTimeUtc.Ticks;
            }
            catch { length = -1; writeTicks = 0; }
        }
    }
}
