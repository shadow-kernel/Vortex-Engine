using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Serialization;

namespace Editor.Core.Assets.Library
{
    /// <summary>
    /// Where the global library meets projects: content hashes in .vmeta files (#54), the import hook that registers
    /// every import (#55), "Add to Project" (#59), the duplicate check against a project and the migration scan that
    /// indexes existing projects (#64). Projects are never modified except for the ContentHash fields of their .vmeta
    /// files and the files "Add to Project" copies in.
    /// </summary>
    public static class LibraryProjects
    {
        /// <summary>The folder an asset type lands in (same defaults as the import dialogs).</summary>
        public static string DefaultFolderFor(AssetType type)
        {
            switch (type)
            {
                case AssetType.Mesh: return "Assets/Models";
                case AssetType.Texture: return "Assets/Textures";
                case AssetType.Material: return "Assets/Materials";
                case AssetType.Shader: return "Assets/Shaders";
                case AssetType.Audio: return "Assets/Audio";
                case AssetType.Script: return "Assets/Scripts";
                case AssetType.Prefab: return "Assets/Prefabs";
                case AssetType.Scene: return "Assets/Scenes";
                case AssetType.Animation: return "Assets/Animations";
                case AssetType.Font: return "Assets/Fonts";
                case AssetType.UI: return "Assets/UI";
                default: return "Assets";
            }
        }

        // ================================================================== .vmeta content hashes (#54)

        private static AssetMetadata LoadMeta(string fullPath)
        {
            string metaPath = fullPath + AssetDatabase.MetaFileExtension;
            if (!File.Exists(metaPath)) return null;
            try { return DataSerializer.LoadFromJson<AssetMetadata>(metaPath); } catch { return null; }   // unreadable/legacy XML: leave it alone
        }

        private static void SaveMeta(string fullPath, AssetMetadata meta) => DataSerializer.SaveAsJson(meta, fullPath + AssetDatabase.MetaFileExtension);

        /// <summary>The file's content hash: the .vmeta's when still fresh, else computed now (and written into an
        /// existing .vmeta when <paramref name="writeVmeta"/>). Never creates a .vmeta.</summary>
        public static string HashOf(string fullPath, bool writeVmeta, string knownHash = null)
        {
            var meta = LoadMeta(fullPath);
            if (meta != null && meta.HasFreshContentHash(fullPath)) return meta.ContentHash;
            string hash = ContentHash.IsValid(knownHash) ? knownHash : ContentHash.OfFile(fullPath);
            if (hash != null && meta != null && writeVmeta)
            {
                meta.SetContentHash(hash, fullPath);
                try { SaveMeta(fullPath, meta); } catch { }
            }
            return hash;
        }

        /// <summary>Every asset file of a project (Assets/**, no .vmeta / hidden / editor clutter).</summary>
        public static IEnumerable<string> ProjectAssetFiles(string projectRoot)
        {
            string assets = Path.Combine(projectRoot ?? "", "Assets");
            if (!Directory.Exists(assets)) yield break;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories).ToList(); } catch { yield break; }
            foreach (var f in files)
                if (!LibraryCompanions.IsIgnored(assets, f)) yield return f;
        }

        /// <summary>Hash every asset of a project into its .vmeta ("Backfill Content Hashes"). Fresh hashes are kept.</summary>
        public static (int hashed, int fresh, int failed, TimeSpan time) BackfillHashes(string projectRoot, IProgress<LibraryProgress> progress = null, CancellationToken cancel = default)
        {
            var started = DateTime.UtcNow;
            int hashed = 0, fresh = 0, failed = 0;
            var files = ProjectAssetFiles(projectRoot).ToList();
            for (int i = 0; i < files.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report(new LibraryProgress("Hashing", i, files.Count, files[i]));
                var meta = LoadMeta(files[i]);
                if (meta == null) continue;   // the asset database writes one on the next scan; hashed then by the next backfill
                if (meta.HasFreshContentHash(files[i])) { fresh++; continue; }
                if (meta.UpdateContentHash(files[i])) { try { SaveMeta(files[i], meta); hashed++; } catch { failed++; } }
                else failed++;
            }
            progress?.Report(new LibraryProgress("Hashing", files.Count, files.Count, null));
            return (hashed, fresh, failed, DateTime.UtcNow - started);
        }

        /// <summary>A file in the project with exactly these bytes (recorded usages first, then same-size files), or null.</summary>
        public static string FindInProject(GlobalAssetDatabase lib, string projectRoot, string hash, long size, string extension)
        {
            if (string.IsNullOrEmpty(projectRoot) || !ContentHash.IsValid(hash)) return null;
            try
            {
                foreach (var u in lib?.UsagesInProject(projectRoot) ?? new List<LibraryUsage>())
                {
                    if (u.Hash != hash) continue;
                    string p = Path.IsPathRooted(u.RelativePath) ? u.RelativePath : Path.Combine(projectRoot, u.RelativePath);
                    if (File.Exists(p) && HashOf(p, false) == hash) return p;
                }
                foreach (var f in ProjectAssetFiles(projectRoot))
                {
                    if (!string.IsNullOrEmpty(extension) && !string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase)) continue;
                    long len; try { len = new FileInfo(f).Length; } catch { continue; }
                    if (len != size) continue;
                    if (HashOf(f, false) == hash) return f;
                }
            }
            catch { }
            return null;
        }

        // ================================================================== import hook (#55)

        private static readonly object QueueGate = new object();
        private static Task _queue = Task.FromResult(0);
        private static int _pending;

        /// <summary>Imports waiting for their library registration.</summary>
        public static int PendingRegistrations => _pending;
        /// <summary>A registration finished (any thread): the file and what happened.</summary>
        public static event Action<string, RegisterResult> Registered;

        /// <summary>
        /// Called by the import dialogs after a file landed in a project: hash it (and its companions) into their
        /// .vmeta files and register it in the library — on a background queue, one file at a time, so importing never
        /// waits for the library. A failing library never fails the import (the warning goes to the console).
        /// </summary>
        /// <param name="libraryName">Name for a NEW catalog entry (null = the file name).</param>
        /// <param name="forceNewEntry">"Import as new": a new catalog entry even when the content is known (still one blob).</param>
        public static void QueueImported(string fullPath, IEnumerable<string> tags, string projectRoot, string projectName,
                                         string libraryName = null, bool forceNewEntry = false)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            var tagList = (tags ?? Enumerable.Empty<string>()).ToList();
            Interlocked.Increment(ref _pending);
            lock (QueueGate)
                _queue = _queue.ContinueWith(_ =>
                {
                    RegisterResult r = null;
                    try { r = RegisterImported(fullPath, tagList, projectRoot, projectName, libraryName, forceNewEntry); }
                    catch (Exception ex) { r = new RegisterResult { Error = ex.Message }; }
                    finally { Interlocked.Decrement(ref _pending); }
                    try { Registered?.Invoke(fullPath, r); } catch { }
                }, TaskScheduler.Default);
        }

        /// <summary>Wait for queued registrations (tests, shutdown).</summary>
        public static bool WaitForQueue(int timeoutMs)
        {
            Task t; lock (QueueGate) t = _queue;
            try { return t.Wait(timeoutMs); } catch { return false; }
        }

        /// <summary>The synchronous part of <see cref="QueueImported"/>.</summary>
        public static RegisterResult RegisterImported(string fullPath, List<string> tags, string projectRoot, string projectName,
                                                      string libraryName = null, bool forceNewEntry = false)
        {
            if (!File.Exists(fullPath)) return new RegisterResult { Error = "File not found: " + fullPath };
            string hash = HashOf(fullPath, true);
            var companions = LibraryCompanions.Detect(fullPath);
            var compHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in companions.Keys) { var h = HashOf(c, true); if (h != null) compHashes[c] = h; }
            var meta = LoadMeta(fullPath);
            var o = new RegisterOptions
            {
                Tags = tags ?? new List<string>(),
                ProjectPath = projectRoot,
                ProjectName = projectName ?? (string.IsNullOrEmpty(projectRoot) ? null : Path.GetFileName(projectRoot.TrimEnd('/', '\\'))),
                SourceKind = LibrarySource.Project,
                KnownHash = hash,
                Companions = companions,
                CompanionHashes = compHashes,
                AssetGuid = meta?.Guid.ToString(),
                IsAutomatic = true,
                Name = libraryName,
                ForceNewEntry = forceNewEntry,
            };
            o.SourceName = o.ProjectName;
            return GlobalAssetDatabase.Instance.Register(fullPath, o);
        }

        // ================================================================== Add to Project (#59)

        /// <summary>
        /// Copy a library entry into a project: the blob (and a model's companions, rebuilding its folder) lands in
        /// <paramref name="targetFolder"/> (null = the type's default folder), gets a .vmeta with a fresh GUID, the entry's
        /// tags and the library's content hash, and the library records the usage. When the project already holds these
        /// exact bytes the result is <see cref="AddToProjectStatus.AlreadyInProject"/> with that file's path (unless
        /// <paramref name="allowDuplicate"/>).
        /// </summary>
        public static AddToProjectResult AddToProject(GlobalAssetDatabase lib, long entryId, string projectRoot, string targetFolder = null,
                                                      bool allowDuplicate = false, string projectName = null)
        {
            var r = new AddToProjectResult();
            try
            {
                if (string.IsNullOrEmpty(projectRoot) || !Directory.Exists(projectRoot)) { r.Status = AddToProjectStatus.Failed; r.Error = "No project is open."; return r; }
                var e = lib.Get(entryId);
                if (e == null) { r.Status = AddToProjectStatus.Failed; r.Error = "The library entry no longer exists."; return r; }
                if (!lib.HasBlob(e.Hash)) { r.Status = AddToProjectStatus.Failed; r.Error = "The library no longer has this file's bytes (run Maintenance → Verify)."; return r; }
                if (!allowDuplicate)
                {
                    string existing = FindInProject(lib, projectRoot, e.Hash, e.Size, e.Extension);
                    if (existing != null)
                    {
                        r.Status = AddToProjectStatus.AlreadyInProject; r.Path = existing;
                        var m = LoadMeta(existing); if (m != null) r.AssetGuid = m.Guid;
                        return r;
                    }
                }

                string folder = string.IsNullOrEmpty(targetFolder) ? Path.Combine(projectRoot, DefaultFolderFor(e.Type).Replace('/', Path.DirectorySeparatorChar)) : targetFolder;
                string ext = Path.GetExtension(e.FileName ?? "");
                string stem = SafeName(e.Name);
                string main;
                if (e.Companions.Count > 0)
                {
                    string modelFolder = UniqueDirectory(Path.Combine(folder, stem));
                    Directory.CreateDirectory(modelFolder);
                    main = Path.Combine(modelFolder, stem + ext);
                }
                else
                {
                    Directory.CreateDirectory(folder);
                    main = UniqueFile(Path.Combine(folder, stem + ext));
                }

                string copied = ContentHash.CopyAndHash(lib.BlobPath(e.Hash), main);
                if (copied != e.Hash)
                {
                    try { File.Delete(main); } catch { }
                    r.Status = AddToProjectStatus.Failed; r.Error = "The library copy of this file is damaged (run Maintenance → Verify).";
                    return r;
                }
                r.Files.Add(main);
                string baseDir = Path.GetDirectoryName(main);
                foreach (var c in e.Companions)
                {
                    if (string.IsNullOrEmpty(c.RelPath) || c.RelPath.Contains("..")) continue;
                    string dst = Path.Combine(baseDir, c.RelPath.Replace('/', Path.DirectorySeparatorChar));
                    if (!lib.HasBlob(c.Hash) || File.Exists(dst)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(lib.BlobPath(c.Hash), dst, false);
                    r.Files.Add(dst);
                    var ct = AssetDatabase.TypeForExtension(Path.GetExtension(dst));
                    if (ct != AssetType.Unknown) WriteNewMeta(projectRoot, dst, ct, null, c.Hash, e);
                }

                var meta = WriteNewMeta(projectRoot, main, e.Type, e.Tags, e.Hash, e);
                r.AssetGuid = meta.Guid;
                r.Path = main;
                r.Status = AddToProjectStatus.Added;

                // the open project's tag index (Library/AssetTags.xml) learns the tags too
                if (e.Tags.Count > 0 && SamePath(AssetDatabase.Instance.ProjectPath, projectRoot))
                    foreach (var t in e.Tags) { try { AssetTagService.Instance.AddTag(meta.Guid, t); } catch { } }
                lib.AddUsage(e.Hash, projectRoot, projectName, main, meta.Guid.ToString());
                return r;
            }
            catch (Exception ex) { r.Status = AddToProjectStatus.Failed; r.Error = ex.Message; return r; }
        }

        private static AssetMetadata WriteNewMeta(string projectRoot, string fullPath, AssetType type, List<string> tags, string hash, LibraryEntry from)
        {
            string rel = LibraryCompanions.Rel(projectRoot, fullPath).Replace('/', Path.DirectorySeparatorChar);
            var meta = new AssetMetadata(type, rel, Path.GetFileName(fullPath));
            if (tags != null) meta.Tags = new List<string>(tags);
            // license + credit travel with the file (CREDITS.md at export, #77); own project assets have none
            if (from != null && from.SourceKind != LibrarySource.Project && from.SourceKind != LibrarySource.Manual)
            {
                meta.License = from.License; meta.Author = from.Author; meta.SourceUrl = from.SourceUrl; meta.Source = from.SourceName;
            }
            else if (from != null && !string.IsNullOrEmpty(from.License)) { meta.License = from.License; meta.Author = from.Author; meta.SourceUrl = from.SourceUrl; }
            try { var fi = new FileInfo(fullPath); meta.LastModified = fi.LastWriteTime; meta.FileSize = fi.Length; } catch { }
            meta.SetContentHash(hash, fullPath);
            SaveMeta(fullPath, meta);
            return meta;
        }

        private static bool SamePath(string a, string b)
            => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
               && string.Equals(GlobalAssetDatabase.NormalizeDir(a), GlobalAssetDatabase.NormalizeDir(b), StringComparison.OrdinalIgnoreCase);

        public static string SafeName(string name)
        {
            var s = (name ?? "asset").Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Length == 0 ? "asset" : s;
        }

        /// <summary>"name.ext" → "name_1.ext", "name_2.ext" … until free (the import pipeline's keep-both rule).</summary>
        public static string UniqueFile(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 1; ; i++) { string p = Path.Combine(dir, stem + "_" + i + ext); if (!File.Exists(p)) return p; }
        }

        public static string UniqueDirectory(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
            for (int i = 1; ; i++) { string p = path + "_" + i; if (!Directory.Exists(p) && !File.Exists(p)) return p; }
        }

        // ================================================================== migration scan (#64)

        /// <summary>
        /// Index existing projects: hash every asset (writing ContentHash into existing .vmeta files only), store the
        /// bytes once, register one entry per content (duplicates across projects collapse into one entry with several
        /// usages), carry .vmeta tags over. Models that own their folder take the folder along as companions instead of
        /// registering each texture separately. Idempotent: re-running skips what is known. <paramref name="registered"/>
        /// (hash, file, type) lets the editor queue thumbnails.
        /// </summary>
        public static IndexReport IndexProjects(GlobalAssetDatabase lib, IEnumerable<(string path, string name)> projects,
                                                Action<string, string, AssetType> registered = null,
                                                IProgress<LibraryProgress> progress = null, CancellationToken cancel = default)
        {
            var rep = new IndexReport();
            var started = DateTime.UtcNow;
            var seenThisRun = new HashSet<string>();
            try
            {
                foreach (var (projPath, projName) in projects ?? Enumerable.Empty<(string, string)>())
                {
                    cancel.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(projPath) || !Directory.Exists(Path.Combine(projPath, "Assets")))
                    { rep.Errors.Add("Skipped (no Assets folder): " + projPath); continue; }
                    rep.Projects++;
                    string name = string.IsNullOrEmpty(projName) ? Path.GetFileName(projPath.TrimEnd('/', '\\')) : projName;
                    var files = ProjectAssetFiles(projPath).ToList();
                    rep.Files += files.Count;

                    // models first: what they own is not registered on its own
                    var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var models = files.Where(LibraryCompanions.IsModelFile).ToList();
                    var compOf = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in models)
                    {
                        var c = LibraryCompanions.Detect(m);
                        compOf[m] = c;
                        foreach (var k in c.Keys) claimed.Add(k);
                    }
                    var order = models.Concat(files.Where(f => !LibraryCompanions.IsModelFile(f) && !claimed.Contains(Path.GetFullPath(f)))).ToList();

                    for (int i = 0; i < order.Count; i++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        string f = order[i];
                        progress?.Report(new LibraryProgress("Indexing " + name, i, order.Count, LibraryCompanions.Rel(projPath, f)));
                        var type = AssetDatabase.TypeForExtension(Path.GetExtension(f));
                        if (!lib.Settings.Includes(type)) { rep.Skipped++; continue; }
                        var meta = LoadMeta(f);
                        bool hadFresh = meta != null && meta.HasFreshContentHash(f);
                        string hash = HashOf(f, true);
                        if (hash == null) { rep.Errors.Add("Can't read " + f); continue; }
                        if (meta != null && !hadFresh) rep.VmetaUpdated++;
                        Dictionary<string, string> comps;
                        compOf.TryGetValue(f, out comps);
                        var compHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (comps != null) foreach (var c in comps.Keys) { var ch = HashOf(c, true); if (ch != null) compHashes[c] = ch; }
                        var res = lib.Register(f, new RegisterOptions
                        {
                            Tags = meta?.Tags ?? new List<string>(),
                            ProjectPath = projPath, ProjectName = name,
                            SourceKind = LibrarySource.Project, SourceName = name,
                            KnownHash = hash,
                            Companions = comps ?? new Dictionary<string, string>(),
                            CompanionHashes = compHashes,
                            AssetGuid = meta?.Guid.ToString(),
                        }, cancel);
                        if (!res.Success) { rep.Errors.Add(Path.GetFileName(f) + ": " + res.Error); continue; }
                        if (res.Skipped) { rep.Skipped++; continue; }
                        if (res.EntryExisted) { if (seenThisRun.Contains(hash)) rep.DuplicatesCollapsed++; else rep.AlreadyKnown++; }
                        else rep.Registered++;
                        seenThisRun.Add(hash);
                        rep.BytesStored += res.BytesWritten;
                        try { registered?.Invoke(hash, f, type); } catch { }
                    }
                }
            }
            catch (OperationCanceledException) { rep.Cancelled = true; }
            rep.Duration = DateTime.UtcNow - started;
            return rep;
        }
    }
}
