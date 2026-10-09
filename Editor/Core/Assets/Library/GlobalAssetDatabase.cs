using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Editor.Core.Assets.Library
{
    /// <summary>
    /// The machine-wide asset library (milestone v2.9.0, design: https://engine.vortexstudio.dev/docs/#/design-asset-database).
    /// <para>
    /// <b>Catalog</b> — SQLite (<c>catalog.db</c>, WAL, so several editor instances can read and write at once):
    /// blobs (one row per content hash), assets (named, tagged entries that point at a blob), tags, companions (extra
    /// files of a model), usages (which project files use a blob) and saved filters.
    /// <b>Blob store</b> — <c>blobs/ab/abcdef…</c>: every file stored once under its SHA-256, written atomically
    /// (temp file + rename), never named after a project. <b>Thumbnails</b> — <c>thumbs/ab/&lt;hash&gt;.png</c>, so
    /// identical assets share one preview and a stale thumbnail is impossible by construction.
    /// </para>
    /// Projects always receive real copies ("Add to Project"), so a project stays self-contained and shippable, and
    /// deleting a project never touches the library. Editor-only: shipped games never open the library.
    /// All members are thread-safe; long work (hashing, copying) runs outside the catalog lock.
    /// </summary>
    public sealed class GlobalAssetDatabase : IDisposable
    {
        private static GlobalAssetDatabase _instance;
        private static readonly object InstanceGate = new object();

        /// <summary>The library of this machine (opened on first use).</summary>
        public static GlobalAssetDatabase Instance
        {
            get
            {
                if (_instance != null) return _instance;
                lock (InstanceGate) return _instance ?? (_instance = new GlobalAssetDatabase());
            }
        }

        /// <summary>Drop the shared instance (tests, after a library move or a settings change of the root).</summary>
        public static void ResetInstance()
        {
            lock (InstanceGate) { _instance?.Dispose(); _instance = null; }
        }

        private readonly object _lock = new object();
        private SqliteDb _db;
        private bool _openAttempted;
        private bool _capWarned;

        public LibrarySettings Settings { get; private set; }
        public string Root { get; private set; }
        public string LastError { get; private set; }

        /// <summary>Raised after the catalog changed (any thread).</summary>
        public event Action Changed;
        /// <summary>Raised for problems the user should see (any thread): over the size cap, a failed registration.</summary>
        public event Action<string> Warning;

        public GlobalAssetDatabase() : this(null) { }

        /// <summary>A library at <paramref name="root"/> (null = from the settings). Tests open private libraries this way.</summary>
        public GlobalAssetDatabase(string root)
        {
            Settings = LibrarySettings.Load();
            Root = string.IsNullOrEmpty(root) ? Settings.EffectiveRoot : root;
        }

        public string CatalogPath => Path.Combine(Root, "catalog.db");
        public string BlobDir => Path.Combine(Root, "blobs");
        public string ThumbDir => Path.Combine(Root, "thumbs");
        public string TempDir => Path.Combine(Root, "tmp");

        public string BlobPath(string hash) => Path.Combine(BlobDir, hash.Substring(0, 2), hash);
        public string ThumbnailPath(string hash) => Path.Combine(ThumbDir, hash.Substring(0, 2), hash + ".png");
        public bool HasBlob(string hash) => ContentHash.IsValid(hash) && File.Exists(BlobPath(hash));
        public bool HasThumbnail(string hash) => ContentHash.IsValid(hash) && File.Exists(ThumbnailPath(hash));

        /// <summary>True when the catalog is open (or could be opened now).</summary>
        public bool IsAvailable => EnsureOpen();

        // ================================================================== open / schema

        public bool EnsureOpen()
        {
            lock (_lock)
            {
                if (_db != null) return true;
                if (_openAttempted && LastError != null) return false;
                _openAttempted = true;
                try
                {
                    FinishPendingMove();
                    Directory.CreateDirectory(Root);
                    Directory.CreateDirectory(BlobDir);
                    Directory.CreateDirectory(ThumbDir);
                    Directory.CreateDirectory(TempDir);
                    _db = SqliteDb.Open(CatalogPath);
                    _db.ExecScript(Schema);
                    Migrate(_db);
                    LastError = null;
                    CleanTemp();
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = "Asset library unavailable: " + ex.Message;
                    try { _db?.Dispose(); } catch { }
                    _db = null;
                    return false;
                }
            }
        }

        /// <summary>Retry opening after an error (e.g. the drive came back).</summary>
        public bool Reopen()
        {
            lock (_lock) { _db?.Dispose(); _db = null; _openAttempted = false; LastError = null; }
            return EnsureOpen();
        }

        private const string Schema = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA foreign_keys=ON;
CREATE TABLE IF NOT EXISTS info(key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE IF NOT EXISTS blobs(
  hash TEXT PRIMARY KEY,
  size INTEGER NOT NULL,
  ext TEXT NOT NULL DEFAULT '',
  stored INTEGER NOT NULL DEFAULT 0,
  added INTEGER NOT NULL,
  verified INTEGER);
CREATE TABLE IF NOT EXISTS assets(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  hash TEXT NOT NULL REFERENCES blobs(hash),
  name TEXT NOT NULL,
  file_name TEXT NOT NULL,
  type INTEGER NOT NULL,
  added INTEGER NOT NULL,
  updated INTEGER NOT NULL,
  source_kind TEXT NOT NULL DEFAULT 'project',
  source_name TEXT,
  source_url TEXT,
  author TEXT,
  license TEXT,
  redistributable INTEGER NOT NULL DEFAULT 1,
  duration REAL,
  channels INTEGER,
  sample_rate INTEGER,
  width INTEGER,
  height INTEGER,
  notes TEXT);
CREATE INDEX IF NOT EXISTS ix_assets_hash ON assets(hash);
CREATE INDEX IF NOT EXISTS ix_assets_type ON assets(type);
CREATE INDEX IF NOT EXISTS ix_assets_name ON assets(name COLLATE NOCASE);
CREATE TABLE IF NOT EXISTS tags(
  asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  tag TEXT NOT NULL COLLATE NOCASE,
  PRIMARY KEY(asset_id, tag));
CREATE INDEX IF NOT EXISTS ix_tags_tag ON tags(tag);
CREATE TABLE IF NOT EXISTS companions(
  asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  rel_path TEXT NOT NULL,
  hash TEXT NOT NULL REFERENCES blobs(hash),
  PRIMARY KEY(asset_id, rel_path));
CREATE INDEX IF NOT EXISTS ix_companions_hash ON companions(hash);
CREATE TABLE IF NOT EXISTS usages(
  hash TEXT NOT NULL,
  project TEXT NOT NULL COLLATE NOCASE,
  project_name TEXT,
  rel_path TEXT NOT NULL COLLATE NOCASE,
  guid TEXT,
  seen INTEGER NOT NULL,
  PRIMARY KEY(hash, project, rel_path));
CREATE INDEX IF NOT EXISTS ix_usages_project ON usages(project);
CREATE TABLE IF NOT EXISTS filters(
  name TEXT PRIMARY KEY COLLATE NOCASE,
  search TEXT,
  types TEXT,
  tags TEXT,
  created INTEGER NOT NULL);
INSERT OR IGNORE INTO info(key, value) VALUES('schema', '1');
";

        /// <summary>Schema upgrades of older catalogs (v1 → v2: the store key of an asset store download).</summary>
        private static void Migrate(SqliteDb db)
        {
            var cols = db.Query("PRAGMA table_info(assets)", s => s.Text(1));
            if (!cols.Contains("store_key"))
            {
                db.ExecScript("ALTER TABLE assets ADD COLUMN store_key TEXT;");
                db.Execute("UPDATE info SET value = '2' WHERE key = 'schema'");
            }
            db.ExecScript("CREATE INDEX IF NOT EXISTS ix_assets_store_key ON assets(store_key);");
            // v2 → v3: the generation recipe of a Sound Studio take (#83)
            if (!cols.Contains("recipe"))
            {
                db.ExecScript("ALTER TABLE assets ADD COLUMN recipe TEXT;");
                db.Execute("UPDATE info SET value = '3' WHERE key = 'schema'");
            }
        }

        private void CleanTemp()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(TempDir, "*", SearchOption.AllDirectories))
                    try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-6)) File.Delete(f); } catch { }
            }
            catch { }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_db == null) return;
                try { _db.ExecScript("PRAGMA wal_checkpoint(TRUNCATE);"); } catch { }
                _db.Dispose();
                _db = null;
            }
        }

        private void RaiseChanged() { try { Changed?.Invoke(); } catch { } }
        private void RaiseWarning(string text) { try { Warning?.Invoke(text); } catch { } }

        private T Locked<T>(Func<SqliteDb, T> body, T fallback)
        {
            if (!EnsureOpen()) return fallback;
            lock (_lock)
            {
                if (_db == null) return fallback;
                return body(_db);
            }
        }

        internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        internal static DateTime Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

        // ================================================================== blobs

        /// <summary>A file staged for the blob store: hashed copy in tmp/ (or nothing to copy when the blob exists).</summary>
        private sealed class Staged
        {
            public string Source, Hash, Temp, Ext;
            public long Size;
            public bool Existed;
        }

        /// <summary>Hash <paramref name="source"/> (unless <paramref name="knownHash"/>) and copy it to tmp/ when the
        /// blob is not stored yet. Runs outside the lock; <see cref="Commit"/> moves it into place.</summary>
        private Staged Stage(string source, string knownHash, CancellationToken cancel)
        {
            var st = new Staged { Source = source, Ext = Path.GetExtension(source).ToLowerInvariant() };
            st.Size = new FileInfo(source).Length;
            st.Hash = ContentHash.IsValid(knownHash) ? knownHash : ContentHash.OfFile(source, cancel);
            if (st.Hash == null) throw new IOException("Can't read " + source);
            if (HasBlob(st.Hash)) { st.Existed = true; return st; }
            Directory.CreateDirectory(TempDir);
            st.Temp = Path.Combine(TempDir, Guid.NewGuid().ToString("N") + ".part");
            string copied = ContentHash.CopyAndHash(source, st.Temp, cancel);
            // the file changed between hashing and copying: the copy is the truth
            if (copied != st.Hash) { st.Hash = copied; st.Size = new FileInfo(st.Temp).Length; }
            return st;
        }

        /// <summary>Inside the write lock: move a staged copy into the store (or drop it when another writer was faster)
        /// and make sure the blob row exists and says "stored".</summary>
        private long Commit(SqliteDb db, Staged st)
        {
            long written = 0;
            string final = BlobPath(st.Hash);
            if (st.Temp != null)
            {
                if (!File.Exists(final))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(final));
                    File.Move(st.Temp, final);
                    written = st.Size;
                }
                else { try { File.Delete(st.Temp); } catch { } }
                st.Temp = null;
            }
            // (INSERT OR IGNORE + UPDATE instead of an UPSERT: winsqlite3 on older Windows 10 builds predates UPSERT)
            db.Execute("INSERT OR IGNORE INTO blobs(hash, size, ext, stored, added) VALUES(?1, ?2, ?3, 1, ?4)", st.Hash, st.Size, st.Ext, Now());
            db.Execute("UPDATE blobs SET stored = 1 WHERE hash = ?1 AND stored = 0", st.Hash);
            return written;
        }

        private void Discard(IEnumerable<Staged> staged)
        {
            foreach (var s in staged) if (s?.Temp != null) { try { File.Delete(s.Temp); } catch { } s.Temp = null; }
        }

        // ================================================================== registration

        /// <summary>
        /// Put a file into the library: store its bytes (once per hash), create or update its catalog entry, keep its
        /// companions (a model's folder / referenced files) and remember which project uses it. A hash that is already
        /// known adds a usage and merges tags instead of creating a duplicate (unless
        /// <see cref="RegisterOptions.ForceNewEntry"/>). Never throws; failures come back in the result.
        /// </summary>
        public RegisterResult Register(string fullPath, RegisterOptions o = null, CancellationToken cancel = default)
        {
            o = o ?? new RegisterOptions();
            var r = new RegisterResult();
            var staged = new List<Staged>();
            try
            {
                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) { r.Error = "File not found: " + fullPath; return r; }
                var type = AssetDatabase.TypeForExtension(Path.GetExtension(fullPath));
                if (!o.Explicit && (!Settings.Includes(type) || (o.IsAutomatic && !Settings.AutoRegisterImports))) { r.Skipped = true; r.Success = true; return r; }
                if (!EnsureOpen()) { r.Error = LastError; return r; }

                var main = Stage(fullPath, o.KnownHash, cancel);
                staged.Add(main);
                r.Hash = main.Hash;
                var companions = new List<(string rel, Staged st)>();
                var comp = o.Companions ?? LibraryCompanions.Detect(fullPath);
                foreach (var kv in comp)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (!File.Exists(kv.Key)) continue;
                    string known = null;
                    if (o.CompanionHashes != null) o.CompanionHashes.TryGetValue(kv.Key, out known);
                    var cs = Stage(kv.Key, known, cancel);
                    staged.Add(cs);
                    companions.Add((kv.Value, cs));
                }

                // facts about the file, read outside the lock
                int? w = null, h = null, ch = null, rate = null; double? dur = null;
                if (type == AssetType.Texture && ImageInfo.TryReadSize(fullPath, out int iw, out int ih)) { w = iw; h = ih; }
                if (type == AssetType.Audio && TryAudioInfo(fullPath, out float d, out int sr, out int c)) { dur = d; rate = sr; ch = c; }

                string name = string.IsNullOrWhiteSpace(o.Name) ? Path.GetFileNameWithoutExtension(fullPath) : o.Name.Trim();
                string fileName = Path.GetFileName(fullPath);
                long written = 0;
                LibraryEntry entry = null;
                lock (_lock)
                {
                    if (_db == null) { r.Error = LastError ?? "Asset library closed"; return r; }
                    _db.Write(() =>
                    {
                        r.BlobExisted = main.Existed;
                        foreach (var s in staged) written += Commit(_db, s);
                        long now = Now();
                        long id = 0;
                        if (!o.ForceNewEntry)
                            id = _db.ScalarLong("SELECT id FROM assets WHERE hash = ?1 ORDER BY id LIMIT 1", main.Hash);
                        if (id != 0)
                        {
                            r.EntryExisted = true;
                            _db.Execute("UPDATE assets SET updated = ?2, " +
                                        "author = COALESCE(author, ?3), license = COALESCE(license, ?4), source_url = COALESCE(source_url, ?5), " +
                                        "duration = COALESCE(duration, ?6), channels = COALESCE(channels, ?7), sample_rate = COALESCE(sample_rate, ?8), " +
                                        "width = COALESCE(width, ?9), height = COALESCE(height, ?10), store_key = COALESCE(store_key, ?11), recipe = COALESCE(recipe, ?12) WHERE id = ?1",
                                        id, now, o.Author, o.License, o.SourceUrl, dur, ch, rate, w, h, o.StoreKey, o.Recipe);
                        }
                        else
                        {
                            _db.Execute("INSERT INTO assets(hash, name, file_name, type, added, updated, source_kind, source_name, source_url, author, license, " +
                                        "redistributable, duration, channels, sample_rate, width, height, notes, store_key, recipe) " +
                                        "VALUES(?1, ?2, ?3, ?4, ?5, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19)",
                                        main.Hash, name, fileName, (int)type, now, o.SourceKind ?? LibrarySource.Project, o.SourceName, o.SourceUrl,
                                        o.Author, o.License, o.Redistributable, dur, ch, rate, w, h, o.Notes, o.StoreKey, o.Recipe);
                            id = _db.LastInsertRowId;
                        }
                        foreach (var t in CleanTags(o.Tags)) _db.Execute("INSERT OR IGNORE INTO tags(asset_id, tag) VALUES(?1, ?2)", id, t);
                        foreach (var c in companions)
                            _db.Execute("INSERT OR REPLACE INTO companions(asset_id, rel_path, hash) VALUES(?1, ?2, ?3)", id, c.rel, c.st.Hash);
                        if (!string.IsNullOrEmpty(o.ProjectPath))
                            AddUsageLocked(_db, main.Hash, o.ProjectPath, o.ProjectName, fullPath, o.AssetGuid);
                        entry = GetLocked(_db, id, true);
                    });
                }
                r.BytesWritten = written;
                r.Entry = entry;
                r.Success = entry != null;
                RaiseChanged();
                CheckCap();
                return r;
            }
            catch (OperationCanceledException) { r.Error = "Cancelled"; return r; }
            catch (Exception ex) { r.Error = ex.Message; RaiseWarning("Asset library: registering " + Path.GetFileName(fullPath) + " failed — " + ex.Message); return r; }
            finally { Discard(staged); }
        }

        private static bool TryAudioInfo(string path, out float duration, out int rate, out int channels)
        {
            duration = 0; rate = 0; channels = 0;
            try { return Editor.DllWrapper.VortexAudio.GetClipInfo(path, out duration, out rate, out channels); }
            catch { return false; }   // the native engine is not loaded (tools, tests)
        }

        private void CheckCap()
        {
            long cap = Settings.SizeCapBytes;
            if (cap <= 0 || _capWarned) return;
            long used = Locked(db => db.ScalarLong("SELECT COALESCE(SUM(size), 0) FROM blobs WHERE stored = 1"), 0L);
            if (used <= cap) return;
            _capWarned = true;
            RaiseWarning(string.Format("The asset library uses {0} — more than its {1} cap. Open Library → Maintenance to clean it up.",
                FormatBytes(used), FormatBytes(cap)));
        }

        internal static IEnumerable<string> CleanTags(IEnumerable<string> tags)
        {
            if (tags == null) yield break;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tags)
            {
                var s = (t ?? "").Trim();
                if (s.Length == 0 || s.Length > 64 || !seen.Add(s)) continue;
                yield return s;
            }
        }

        // ================================================================== queries

        private const string EntryColumns =
            "a.id, a.hash, a.name, a.file_name, a.type, b.size, b.stored, a.added, a.updated, a.source_kind, a.source_name, a.source_url, " +
            "a.author, a.license, a.redistributable, a.duration, a.channels, a.sample_rate, a.width, a.height, a.notes, " +
            "(SELECT group_concat(t.tag, char(31)) FROM tags t WHERE t.asset_id = a.id), a.store_key, a.recipe ";

        private static LibraryEntry ReadEntry(SqliteStmt s)
        {
            var e = new LibraryEntry
            {
                Id = s.Long(0), Hash = s.Text(1), Name = s.Text(2), FileName = s.Text(3), Type = (AssetType)s.Int(4),
                Size = s.Long(5), Stored = s.Bool(6), Added = Time(s.Long(7)), Updated = Time(s.Long(8)),
                SourceKind = s.Text(9), SourceName = s.Text(10), SourceUrl = s.Text(11), Author = s.Text(12), License = s.Text(13),
                Redistributable = s.Bool(14), Duration = s.DoubleOrNull(15), Channels = s.IntOrNull(16), SampleRate = s.IntOrNull(17),
                Width = s.IntOrNull(18), Height = s.IntOrNull(19), Notes = s.Text(20),
            };
            var tags = s.Text(21);
            if (!string.IsNullOrEmpty(tags)) e.Tags = tags.Split('\u001f').OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            e.StoreKey = s.Text(22);
            e.Recipe = s.Text(23);
            return e;
        }

        private static LibraryEntry GetLocked(SqliteDb db, long id, bool companions)
        {
            var e = db.Query("SELECT " + EntryColumns + "FROM assets a JOIN blobs b ON b.hash = a.hash WHERE a.id = ?1", ReadEntry, id).FirstOrDefault();
            if (e != null && companions)
                e.Companions = db.Query("SELECT c.rel_path, c.hash, b.size FROM companions c JOIN blobs b ON b.hash = c.hash WHERE c.asset_id = ?1 ORDER BY c.rel_path",
                    s => new LibraryCompanion { RelPath = s.Text(0), Hash = s.Text(1), Size = s.Long(2) }, id);
            return e;
        }

        /// <summary>One entry with its companions, or null.</summary>
        public LibraryEntry Get(long id) => Locked(db => GetLocked(db, id, true), null);

        /// <summary>Every entry that points at <paramref name="hash"/> (oldest first).</summary>
        public List<LibraryEntry> FindByHash(string hash)
        {
            if (!ContentHash.IsValid(hash)) return new List<LibraryEntry>();
            return Locked(db => db.Query("SELECT " + EntryColumns + "FROM assets a JOIN blobs b ON b.hash = a.hash WHERE a.hash = ?1 ORDER BY a.id", ReadEntry, hash),
                new List<LibraryEntry>());
        }

        /// <summary>Entries downloaded from an asset store item ("provider:id:variant"), newest first.</summary>
        public List<LibraryEntry> FindByStoreKey(string storeKey)
        {
            if (string.IsNullOrEmpty(storeKey)) return new List<LibraryEntry>();
            return Locked(db => db.Query("SELECT " + EntryColumns + "FROM assets a JOIN blobs b ON b.hash = a.hash WHERE a.store_key = ?1 ORDER BY a.id DESC", ReadEntry, storeKey),
                new List<LibraryEntry>());
        }

        /// <summary>"provider:id" of every store item of <paramref name="providerId"/> that is in the library.</summary>
        public HashSet<string> StoreItemsInLibrary(string providerId)
        {
            var keys = Locked(db => db.Query("SELECT DISTINCT store_key FROM assets WHERE store_key LIKE ?1", s => s.Text(0), (providerId ?? "") + ":%"), new List<string>());
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in keys)
            {
                if (k == null) continue;
                int last = k.LastIndexOf(':');
                set.Add(last > 0 ? k.Substring(0, last) : k);
            }
            return set;
        }

        /// <summary>Is this file's content already in the library? (hashes the file)</summary>
        public LibraryEntry FindByFile(string fullPath, out string hash)
        {
            hash = ContentHash.OfFile(fullPath);
            return hash == null ? null : FindByHash(hash).FirstOrDefault();
        }

        private static string Where(LibraryQuery q, List<object> args)
        {
            var sb = new StringBuilder(" WHERE 1 = 1");
            if (!string.IsNullOrWhiteSpace(q.Search))
            {
                args.Add("%" + EscapeLike(q.Search.Trim()) + "%");
                int p = args.Count;
                sb.Append(" AND (a.name LIKE ?").Append(p).Append(" ESCAPE '\\' OR a.file_name LIKE ?").Append(p)
                  .Append(" ESCAPE '\\' OR EXISTS(SELECT 1 FROM tags t WHERE t.asset_id = a.id AND t.tag LIKE ?").Append(p).Append(" ESCAPE '\\'))");
            }
            if (q.Types != null && q.Types.Count > 0)
                sb.Append(" AND a.type IN (").Append(string.Join(",", q.Types.Select(t => ((int)t).ToString()))).Append(")");
            var tags = CleanTags(q.Tags).ToList();
            if (tags.Count > 0)
            {
                sb.Append(" AND (SELECT COUNT(DISTINCT t.tag) FROM tags t WHERE t.asset_id = a.id AND t.tag IN (");
                for (int i = 0; i < tags.Count; i++) { args.Add(tags[i]); sb.Append(i > 0 ? ", ?" : "?").Append(args.Count); }
                sb.Append(")) = ").Append(tags.Count);
            }
            if (!string.IsNullOrEmpty(q.SourceName)) { args.Add(q.SourceName); sb.Append(" AND a.source_name = ?").Append(args.Count); }
            return sb.ToString();
        }

        private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        /// <summary>Search the catalog.</summary>
        public List<LibraryEntry> Query(LibraryQuery q)
        {
            q = q ?? new LibraryQuery();
            var args = new List<object>();
            string sql = "SELECT " + EntryColumns + "FROM assets a JOIN blobs b ON b.hash = a.hash" + Where(q, args);
            string dir = q.Descending ? " DESC" : "";
            switch (q.Sort)
            {
                case LibrarySort.Type: sql += " ORDER BY a.type" + dir + ", a.name COLLATE NOCASE"; break;
                case LibrarySort.Added: sql += " ORDER BY a.added" + (q.Descending ? "" : " DESC") + ", a.id DESC"; break;
                case LibrarySort.Size: sql += " ORDER BY b.size" + (q.Descending ? "" : " DESC") + ", a.name COLLATE NOCASE"; break;
                default: sql += " ORDER BY a.name COLLATE NOCASE" + dir + ", a.id"; break;
            }
            if (q.Limit > 0) sql += " LIMIT " + q.Limit + " OFFSET " + Math.Max(0, q.Offset);
            return Locked(db => db.Query(sql, ReadEntry, args.ToArray()), new List<LibraryEntry>());
        }

        public int Count(LibraryQuery q = null)
        {
            q = q ?? new LibraryQuery();
            var args = new List<object>();
            string sql = "SELECT COUNT(*) FROM assets a JOIN blobs b ON b.hash = a.hash" + Where(q, args);
            return (int)Locked(db => db.ScalarLong(sql, args.ToArray()), 0L);
        }

        /// <summary>Every tag with its usage count (most used first).</summary>
        public List<(string tag, int count)> TagCounts()
            => Locked(db => db.Query("SELECT tag, COUNT(*) FROM tags GROUP BY tag COLLATE NOCASE ORDER BY COUNT(*) DESC, tag COLLATE NOCASE",
                s => (s.Text(0), s.Int(1))), new List<(string, int)>());

        public List<string> AllTags() => TagCounts().Select(t => t.tag).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Distinct source names (projects / providers).</summary>
        public List<string> SourceNames()
            => Locked(db => db.Query("SELECT DISTINCT source_name FROM assets WHERE source_name IS NOT NULL ORDER BY source_name COLLATE NOCASE", s => s.Text(0)),
                new List<string>());

        // ================================================================== edits

        public bool Rename(long id, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            bool ok = Locked(db => db.Execute("UPDATE assets SET name = ?2, updated = ?3 WHERE id = ?1", id, name.Trim(), Now()) > 0, false);
            if (ok) RaiseChanged();
            return ok;
        }

        /// <summary>License/attribution facts (filled by store providers; editable in the details pane).</summary>
        public bool SetAttribution(long id, string author, string license, string sourceUrl, bool redistributable)
        {
            bool ok = Locked(db => db.Execute("UPDATE assets SET author = ?2, license = ?3, source_url = ?4, redistributable = ?5, updated = ?6 WHERE id = ?1",
                id, author, license, sourceUrl, redistributable, Now()) > 0, false);
            if (ok) RaiseChanged();
            return ok;
        }

        public void SetTags(long id, IEnumerable<string> tags)
        {
            var clean = CleanTags(tags).ToList();
            Locked(db => { db.Write(() =>
            {
                db.Execute("DELETE FROM tags WHERE asset_id = ?1", id);
                foreach (var t in clean) db.Execute("INSERT OR IGNORE INTO tags(asset_id, tag) VALUES(?1, ?2)", id, t);
                db.Execute("UPDATE assets SET updated = ?2 WHERE id = ?1", id, Now());
            }); return 0; }, 0);
            RaiseChanged();
        }

        /// <summary>Bulk: add every tag to every entry. Returns the number of new (entry, tag) pairs.</summary>
        public int AddTags(IEnumerable<long> ids, IEnumerable<string> tags)
        {
            var idl = ids?.Distinct().ToList() ?? new List<long>();
            var tl = CleanTags(tags).ToList();
            if (idl.Count == 0 || tl.Count == 0) return 0;
            int n = Locked(db => db.Write(() =>
            {
                int added = 0;
                foreach (var id in idl) foreach (var t in tl) added += db.Execute("INSERT OR IGNORE INTO tags(asset_id, tag) VALUES(?1, ?2)", id, t);
                return added;
            }), 0);
            if (n > 0) RaiseChanged();
            return n;
        }

        public int RemoveTags(IEnumerable<long> ids, IEnumerable<string> tags)
        {
            var idl = ids?.Distinct().ToList() ?? new List<long>();
            var tl = CleanTags(tags).ToList();
            if (idl.Count == 0 || tl.Count == 0) return 0;
            int n = Locked(db => db.Write(() =>
            {
                int removed = 0;
                foreach (var id in idl) foreach (var t in tl) removed += db.Execute("DELETE FROM tags WHERE asset_id = ?1 AND tag = ?2", id, t);
                return removed;
            }), 0);
            if (n > 0) RaiseChanged();
            return n;
        }

        /// <summary>Rename a tag everywhere; renaming onto an existing tag merges the two. Returns affected entries.</summary>
        public int RenameTag(string from, string to)
        {
            from = (from ?? "").Trim(); to = (to ?? "").Trim();
            if (from.Length == 0 || to.Length == 0) return 0;
            int n = Locked(db => db.Write(() =>
            {
                int moved = db.Execute("INSERT OR IGNORE INTO tags(asset_id, tag) SELECT asset_id, ?2 FROM tags WHERE tag = ?1", from, to);
                // a case-only rename (scary → Scary) hits the NOCASE key: rewrite in place
                if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return db.Execute("UPDATE tags SET tag = ?2 WHERE tag = ?1", from, to);
                db.Execute("DELETE FROM tags WHERE tag = ?1", from);
                return Math.Max(moved, db.Changes);
            }), 0);
            RaiseChanged();
            return n;
        }

        public int MergeTags(string from, string into) => RenameTag(from, into);

        public int DeleteTag(string tag)
        {
            int n = Locked(db => db.Execute("DELETE FROM tags WHERE tag = ?1", (tag ?? "").Trim()), 0);
            if (n > 0) RaiseChanged();
            return n;
        }

        // ================================================================== saved filters

        public List<SavedFilter> SavedFilters()
            => Locked(db => db.Query("SELECT name, search, types, tags, created FROM filters ORDER BY name COLLATE NOCASE", s => new SavedFilter
            {
                Name = s.Text(0),
                Search = s.Text(1),
                Types = (s.Text(2) ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out int v) ? (AssetType)v : AssetType.Unknown).ToList(),
                Tags = (s.Text(3) ?? "").Split(new[] { '\u001f' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
                Created = Time(s.Long(4)),
            }), new List<SavedFilter>());

        public void SaveFilter(SavedFilter f)
        {
            if (f == null || string.IsNullOrWhiteSpace(f.Name)) return;
            Locked(db => db.Execute("INSERT OR REPLACE INTO filters(name, search, types, tags, created) VALUES(?1, ?2, ?3, ?4, ?5)",
                f.Name.Trim(), f.Search, string.Join(",", (f.Types ?? new List<AssetType>()).Select(t => ((int)t).ToString())),
                string.Join("\u001f", CleanTags(f.Tags)), Now()), 0);
            RaiseChanged();
        }

        public void DeleteFilter(string name)
        {
            Locked(db => db.Execute("DELETE FROM filters WHERE name = ?1", name ?? ""), 0);
            RaiseChanged();
        }

        // ================================================================== usages

        private static void AddUsageLocked(SqliteDb db, string hash, string projectPath, string projectName, string fullPath, string guid)
        {
            string proj = NormalizeDir(projectPath);
            string rel = LibraryCompanions.Rel(proj, fullPath);
            if (rel.StartsWith("../", StringComparison.Ordinal)) rel = fullPath;
            long now = Now();
            if (db.Execute("UPDATE usages SET seen = ?6, guid = COALESCE(?5, guid), project_name = COALESCE(?3, project_name) " +
                           "WHERE hash = ?1 AND project = ?2 AND rel_path = ?4", hash, proj, projectName, rel, guid, now) == 0)
                db.Execute("INSERT OR IGNORE INTO usages(hash, project, project_name, rel_path, guid, seen) VALUES(?1, ?2, ?3, ?4, ?5, ?6)",
                           hash, proj, projectName ?? Path.GetFileName(proj), rel, guid, now);
        }

        /// <summary>Remember that <paramref name="fullPath"/> in a project uses <paramref name="hash"/>.</summary>
        public void AddUsage(string hash, string projectPath, string projectName, string fullPath, string assetGuid)
        {
            if (!ContentHash.IsValid(hash) || string.IsNullOrEmpty(projectPath)) return;
            Locked(db => { AddUsageLocked(db, hash, projectPath, projectName, fullPath, assetGuid); return 0; }, 0);
        }

        public List<LibraryUsage> Usages(string hash)
            => Locked(db => db.Query("SELECT hash, project, project_name, rel_path, guid, seen FROM usages WHERE hash = ?1 ORDER BY project_name COLLATE NOCASE, rel_path",
                s => new LibraryUsage { Hash = s.Text(0), ProjectPath = s.Text(1), ProjectName = s.Text(2), RelativePath = s.Text(3), AssetGuid = s.Text(4), Seen = Time(s.Long(5)) }, hash),
                new List<LibraryUsage>());

        /// <summary>Usages recorded for one project (path → hash).</summary>
        public List<LibraryUsage> UsagesInProject(string projectPath)
            => Locked(db => db.Query("SELECT hash, project, project_name, rel_path, guid, seen FROM usages WHERE project = ?1",
                s => new LibraryUsage { Hash = s.Text(0), ProjectPath = s.Text(1), ProjectName = s.Text(2), RelativePath = s.Text(3), AssetGuid = s.Text(4), Seen = Time(s.Long(5)) },
                NormalizeDir(projectPath)), new List<LibraryUsage>());

        internal static string NormalizeDir(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            try { return Path.GetFullPath(p).TrimEnd('/', '\\'); } catch { return p.TrimEnd('/', '\\'); }
        }

        // ================================================================== thumbnails

        /// <summary>Store a PNG thumbnail for <paramref name="hash"/> (atomic; last writer wins — they are identical).</summary>
        public bool SaveThumbnail(string hash, byte[] png)
        {
            if (!ContentHash.IsValid(hash) || png == null || png.Length == 0) return false;
            try
            {
                string final = ThumbnailPath(hash);
                Directory.CreateDirectory(Path.GetDirectoryName(final));
                Directory.CreateDirectory(TempDir);
                string tmp = Path.Combine(TempDir, Guid.NewGuid().ToString("N") + ".png");
                File.WriteAllBytes(tmp, png);
                if (File.Exists(final)) File.Delete(final);
                File.Move(tmp, final);
                return true;
            }
            catch { return false; }
        }

        // ================================================================== maintenance

        public LibraryStats Stats(int largest = 10)
        {
            var st = new LibraryStats { SizeCapBytes = Settings.SizeCapBytes };
            Locked(db =>
            {
                st.Entries = (int)db.ScalarLong("SELECT COUNT(*) FROM assets");
                st.Blobs = (int)db.ScalarLong("SELECT COUNT(*) FROM blobs WHERE stored = 1");
                st.StoredBytes = db.ScalarLong("SELECT COALESCE(SUM(size), 0) FROM blobs WHERE stored = 1");
                st.Tags = (int)db.ScalarLong("SELECT COUNT(DISTINCT tag) FROM tags");
                st.Projects = (int)db.ScalarLong("SELECT COUNT(DISTINCT project) FROM usages");
                st.ByType.AddRange(db.Query("SELECT a.type, COUNT(*), COALESCE(SUM(b.size), 0) FROM assets a JOIN blobs b ON b.hash = a.hash GROUP BY a.type ORDER BY 3 DESC",
                    s => ((AssetType)s.Int(0), s.Int(1), s.Long(2))));
                st.BySource.AddRange(db.Query("SELECT COALESCE(a.source_name, a.source_kind), COUNT(*), COALESCE(SUM(b.size), 0) FROM assets a JOIN blobs b ON b.hash = a.hash " +
                    "GROUP BY 1 ORDER BY 3 DESC", s => (s.Text(0), s.Int(1), s.Long(2))));
                st.Largest.AddRange(db.Query("SELECT " + EntryColumns + "FROM assets a JOIN blobs b ON b.hash = a.hash ORDER BY b.size DESC LIMIT ?1", ReadEntry, largest));
                return 0;
            }, 0);
            try { if (Directory.Exists(ThumbDir)) st.ThumbnailBytes = Directory.EnumerateFiles(ThumbDir, "*.png", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); } catch { }
            return st;
        }

        /// <summary>Remove entries from the library. Their blobs are deleted when nothing references them any more
        /// (projects that copied an asset keep their copy). Returns the number of entries removed.</summary>
        public int Delete(IEnumerable<long> ids, out long reclaimedBytes)
        {
            reclaimedBytes = 0;
            var idl = ids?.Distinct().ToList() ?? new List<long>();
            if (idl.Count == 0) return 0;
            int n = Locked(db => db.Write(() =>
            {
                int removed = 0;
                foreach (var id in idl) removed += db.Execute("DELETE FROM assets WHERE id = ?1", id);
                return removed;
            }), 0);
            reclaimedBytes = CollectGarbage();
            RaiseChanged();
            return n;
        }

        /// <summary>Delete blobs (rows, files, thumbnails) no entry or companion references. Returns the bytes freed.</summary>
        public long CollectGarbage()
        {
            var dead = Locked(db => db.Write(() =>
            {
                var list = db.Query("SELECT hash, size FROM blobs b WHERE NOT EXISTS(SELECT 1 FROM assets a WHERE a.hash = b.hash) " +
                                    "AND NOT EXISTS(SELECT 1 FROM companions c WHERE c.hash = b.hash)", s => (hash: s.Text(0), size: s.Long(1)));
                foreach (var d in list)
                {
                    db.Execute("DELETE FROM blobs WHERE hash = ?1", d.hash);
                    // files go while we still hold the write lock: a concurrent registration of the same bytes waits
                    try { File.Delete(BlobPath(d.hash)); } catch { }
                    try { File.Delete(ThumbnailPath(d.hash)); } catch { }
                }
                return list;
            }), new List<(string hash, long size)>());
            return dead.Sum(d => d.size);
        }

        /// <summary>Re-hash every stored blob: reports missing files and files whose bytes no longer match their hash.</summary>
        public VerifyReport Verify(IProgress<LibraryProgress> progress = null, CancellationToken cancel = default)
        {
            var rep = new VerifyReport();
            var started = DateTime.UtcNow;
            var blobs = Locked(db => db.Query("SELECT hash FROM blobs WHERE stored = 1 ORDER BY hash", s => s.Text(0)), new List<string>());
            for (int i = 0; i < blobs.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                string h = blobs[i];
                progress?.Report(new LibraryProgress("Verifying", i, blobs.Count, h));
                string path = BlobPath(h);
                if (!File.Exists(path)) { rep.Missing.Add(h); continue; }
                string actual = ContentHash.OfFile(path, cancel);
                if (actual != h) rep.Corrupt.Add(h);
                else Locked(db => db.Execute("UPDATE blobs SET verified = ?2 WHERE hash = ?1", h, Now()), 0);
                rep.Checked++;
            }
            progress?.Report(new LibraryProgress("Verifying", blobs.Count, blobs.Count, null));
            rep.Duration = DateTime.UtcNow - started;
            return rep;
        }

        /// <summary>Find blob↔catalog mismatches; with <paramref name="fix"/> repair them (untracked and unreferenced
        /// blobs are deleted, missing blobs are marked "not stored", stale thumbnails and temp files removed). Entries
        /// with missing bytes are only reported — re-importing the original file restores them.</summary>
        public OrphanReport ScanOrphans(bool fix)
        {
            var rep = new OrphanReport();
            if (!EnsureOpen()) return rep;
            var known = new HashSet<string>(Locked(db => db.Query("SELECT hash FROM blobs", s => s.Text(0)), new List<string>()));
            var stored = new HashSet<string>(Locked(db => db.Query("SELECT hash FROM blobs WHERE stored = 1", s => s.Text(0)), new List<string>()));
            try
            {
                if (Directory.Exists(BlobDir))
                    foreach (var f in Directory.EnumerateFiles(BlobDir, "*", SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileName(f);
                        if (!known.Contains(name)) { rep.UntrackedFiles.Add(f); try { rep.ReclaimableBytes += new FileInfo(f).Length; } catch { } }
                    }
            }
            catch { }
            foreach (var h in stored) if (!File.Exists(BlobPath(h))) rep.MissingFiles.Add(h);
            Locked(db =>
            {
                foreach (var row in db.Query("SELECT hash, size FROM blobs b WHERE NOT EXISTS(SELECT 1 FROM assets a WHERE a.hash = b.hash) " +
                                             "AND NOT EXISTS(SELECT 1 FROM companions c WHERE c.hash = b.hash)", s => (s.Text(0), s.Long(1))))
                { rep.UnreferencedBlobs.Add(row.Item1); rep.ReclaimableBytes += row.Item2; }
                return 0;
            }, 0);
            var missing = new HashSet<string>(rep.MissingFiles);
            foreach (var e in Query(new LibraryQuery())) if (!e.Stored || missing.Contains(e.Hash)) rep.BrokenEntries.Add(e);
            try
            {
                if (Directory.Exists(ThumbDir))
                    foreach (var f in Directory.EnumerateFiles(ThumbDir, "*.png", SearchOption.AllDirectories))
                        if (!known.Contains(Path.GetFileNameWithoutExtension(f))) rep.StaleThumbnails.Add(f);
            }
            catch { }

            if (fix)
            {
                foreach (var f in rep.UntrackedFiles) { try { File.Delete(f); } catch { } }
                foreach (var f in rep.StaleThumbnails) { try { File.Delete(f); } catch { } }
                Locked(db => { foreach (var h in rep.MissingFiles) db.Execute("UPDATE blobs SET stored = 0 WHERE hash = ?1", h); return 0; }, 0);
                CollectGarbage();
                CleanTemp();
                rep.Fixed = true;
                RaiseChanged();
            }
            return rep;
        }

        // ================================================================== relocation

        /// <summary>
        /// Move the whole library (catalog, blobs, thumbnails) to <paramref name="newRoot"/>: copy, verify every blob's
        /// hash at the destination, switch the setting, then delete the old copy. An interrupted move is finished (or
        /// left on the old root) by the next start. Close other editor instances first.
        /// </summary>
        public bool MoveLibrary(string newRoot, IProgress<LibraryProgress> progress, CancellationToken cancel, out string error)
        {
            error = null;
            try
            {
                newRoot = Path.GetFullPath(newRoot);
                string oldRoot = Path.GetFullPath(Root);
                if (string.Equals(newRoot.TrimEnd('/', '\\'), oldRoot.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)) { error = "That is the current library folder."; return false; }
                if (newRoot.StartsWith(oldRoot.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { error = "The new folder can't be inside the current library."; return false; }
                if (Directory.Exists(newRoot) && Directory.EnumerateFileSystemEntries(newRoot).Any(x => !Path.GetFileName(x).StartsWith(".", StringComparison.Ordinal)))
                { error = "The new folder must be empty."; return false; }

                Dispose();   // checkpoint WAL + close: catalog.db is a single consistent file now
                var files = Directory.Exists(oldRoot)
                    ? Directory.EnumerateFiles(oldRoot, "*", SearchOption.AllDirectories)
                        .Where(f => !f.StartsWith(Path.Combine(oldRoot, "tmp"), StringComparison.OrdinalIgnoreCase)
                                 && !f.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) && !f.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)).ToList()
                    : new List<string>();
                Directory.CreateDirectory(newRoot);
                for (int i = 0; i < files.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    string rel = files[i].Substring(oldRoot.TrimEnd('/', '\\').Length).TrimStart('/', '\\');
                    progress?.Report(new LibraryProgress("Copying", i, files.Count, rel));
                    string dst = Path.Combine(newRoot, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    string h = ContentHash.CopyAndHash(files[i], dst, cancel);
                    // blobs are named by their hash: verify the copy
                    string name = Path.GetFileName(rel);
                    if (rel.Replace('\\', '/').StartsWith("blobs/", StringComparison.Ordinal) && ContentHash.IsValid(name) && h != name)
                        throw new IOException("Copy verification failed for blob " + name);
                }
                Settings.Root = newRoot;
                Settings.PendingMoveFrom = oldRoot;
                Settings.Save();
                Root = newRoot;
                progress?.Report(new LibraryProgress("Removing the old copy", files.Count, files.Count, null));
                DeleteOldRoot(oldRoot);
                Settings.PendingMoveFrom = null;
                Settings.Save();
                lock (_lock) { _openAttempted = false; LastError = null; }
                EnsureOpen();
                RaiseChanged();
                return true;
            }
            catch (OperationCanceledException) { error = "Cancelled — the library stays where it was."; Reopen(); return false; }
            catch (Exception ex) { error = ex.Message; Reopen(); return false; }
        }

        private void FinishPendingMove()
        {
            var from = Settings.PendingMoveFrom;
            if (string.IsNullOrEmpty(from)) return;
            // the switch happened (we are on the new root): finish deleting the old copy
            if (File.Exists(CatalogPath)) DeleteOldRoot(from);
            Settings.PendingMoveFrom = null;
            try { Settings.Save(); } catch { }
        }

        private static void DeleteOldRoot(string oldRoot)
        {
            foreach (var sub in new[] { "blobs", "thumbs", "tmp" })
                try { var p = Path.Combine(oldRoot, sub); if (Directory.Exists(p)) Directory.Delete(p, true); } catch { }
            foreach (var f in new[] { "catalog.db", "catalog.db-wal", "catalog.db-shm" })
                try { var p = Path.Combine(oldRoot, f); if (File.Exists(p)) File.Delete(p); } catch { }
            try { if (Directory.Exists(oldRoot) && !Directory.EnumerateFileSystemEntries(oldRoot).Any()) Directory.Delete(oldRoot); } catch { }
        }

        /// <summary>Apply changed settings (size cap, type rules). A changed root needs <see cref="MoveLibrary"/>.</summary>
        public void SaveSettings()
        {
            Settings.Save();
            _capWarned = false;
            RaiseChanged();
        }

        public static string FormatBytes(long bytes)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? v.ToString("0") : v.ToString(v < 10 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture)) + " " + u[i];
        }
    }
}
