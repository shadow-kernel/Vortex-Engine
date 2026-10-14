using System;
using System.Collections.Generic;
using System.IO;

namespace Editor.Core.Assets
{
    /// <summary>
    /// A cache of assets loaded from files that notices when a file changes on disk (#419). Every entry keeps the size and
    /// last-write time the file had when it was loaded; <see cref="Get"/> compares them with the file at most once per
    /// <see cref="RecheckMs"/> — a failed load (file missing or unreadable) too, so a rigid model asked for its skeleton
    /// every frame costs no stat per frame — and reloads a changed file. So an asset rewritten by a script, git or a DCC
    /// export is picked up under its old name, and a file created after a failed request loads once it exists.
    /// <see cref="MarkAllStale"/> makes the next Get of every entry compare (play start), <see cref="Invalidate"/> drops
    /// the entries of one file (the asset watcher saw it change or appear). While <paramref name="immutable"/> says so — a
    /// shipped game reads the read-only asset pak — entries are kept as loaded, failed loads included.
    /// </summary>
    internal sealed class FileStampCache<T> where T : class
    {
        private sealed class Entry   // a class: a hit updates it in place, no dictionary write per request
        {
            public string FullPath;   // normalised once, for Invalidate (the watcher spells the same file its own way)
            public T Value;
            public long Length;       // -1 = no file
            public long WriteTicks;
            public long CheckedAt;    // Environment.TickCount64 of the last comparison
            public bool Stale;        // MarkAllStale: compare on the next Get, however recent CheckedAt is
        }

        // keyed by the path as the caller passes it — no normalisation per request
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, T> _load;
        private readonly Func<bool> _immutable;

        /// <summary>Milliseconds an entry — loaded or failed — is trusted before its file is compared again.</summary>
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
            long now = Environment.TickCount64;
            if (_entries.TryGetValue(path, out var e))
            {
                if (_immutable != null && _immutable()) return e.Value;
                if (!e.Stale && now - e.CheckedAt < RecheckMs) return e.Value;
                Stamp(path, out long length, out long ticks);
                e.CheckedAt = now;
                e.Stale = false;
                if (length == e.Length && ticks == e.WriteTicks) return e.Value;
                // stamp BEFORE loading: a write that lands while the file is read then still differs from the stamp next time
                e.Length = length;
                e.WriteTicks = ticks;
                e.Value = _load(path);
                return e.Value;
            }
            Stamp(path, out long len, out long writeTicks);
            var entry = new Entry { FullPath = Normalize(path), Length = len, WriteTicks = writeTicks, CheckedAt = now };
            entry.Value = _load(path);
            _entries[path] = entry;
            return entry.Value;
        }

        /// <summary>Drop every entry of the file at <paramref name="path"/>, however it was spelled when requested: the next
        /// <see cref="Get"/> loads it again. The dropped assets are added to <paramref name="removed"/>. True when there was one.</summary>
        public bool Invalidate(string path, List<T> removed = null)
        {
            if (string.IsNullOrEmpty(path) || _entries.Count == 0) return false;
            string full = Normalize(path);
            List<string> keys = null;
            foreach (var kv in _entries)
                if (string.Equals(kv.Value.FullPath, full, StringComparison.OrdinalIgnoreCase)) (keys ??= new List<string>()).Add(kv.Key);
            if (keys == null) return false;
            foreach (var k in keys)
            {
                var value = _entries[k].Value;
                if (removed != null && value != null && !removed.Contains(value)) removed.Add(value);
                _entries.Remove(k);
            }
            return true;
        }

        /// <summary>Make the next <see cref="Get"/> of every entry compare its file, whenever it was last checked.</summary>
        public void MarkAllStale()
        {
            foreach (var e in _entries.Values) e.Stale = true;
        }

        public void Clear() => _entries.Clear();

        /// <summary>One spelling per file: rooted paths are normalised ("a/./b", "a/x/../b", '\\' on Windows), relative
        /// ones (VFS keys) kept. Called once per new entry and per invalidation, never per request.</summary>
        private static string Normalize(string path)
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
