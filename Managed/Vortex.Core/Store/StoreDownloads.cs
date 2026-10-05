using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Assets.Library;

namespace Editor.Core.Assets.Store
{
    public enum StoreJobState { Queued, Resolving, Downloading, Verifying, Importing, Done, Failed, Cancelled }

    /// <summary>One store download (one item, one variant) and what became of it.</summary>
    public sealed class StoreJob
    {
        public int Id;
        public IAssetProvider Provider;
        public StoreItem Item;
        public StoreVariant Variant;
        public bool AddToProject;
        public string ProjectRoot, ProjectName, TargetFolder;
        public bool Force;
        public StoreJobState State;
        public double Progress;
        public long BytesDone, BytesTotal;
        public string Message;
        public string Error;
        /// <summary>The bytes were already in the library — nothing was downloaded.</summary>
        public bool AlreadyInLibrary;
        public readonly List<LibraryEntry> Entries = new List<LibraryEntry>();
        public readonly List<string> ProjectPaths = new List<string>();
        public int Attempts;
        internal CancellationTokenSource Cts = new CancellationTokenSource();
        public bool IsActive => State == StoreJobState.Queued || State == StoreJobState.Resolving || State == StoreJobState.Downloading || State == StoreJobState.Verifying || State == StoreJobState.Importing;
        public string StoreKey => Item.Key + ":" + (Variant?.Id ?? "");
        public override string ToString() => Item?.Name + " — " + State;
    }

    /// <summary>
    /// The shared download → verify → import pipeline every provider feeds (#76): a queue with limited parallelism,
    /// per-item progress, cancel and retry (with back-off for transient failures and HTTP range resume of large files),
    /// MD5/SHA-256 verification where providers publish hashes, then the type-appropriate import — a model with its
    /// files, PBR maps → a generated .vmat, a single HDRI/sound, a pack whose every file becomes an entry — into the
    /// global library with source, author and license, and optionally into the open project. An item whose download
    /// is already in the library is not downloaded again.
    /// </summary>
    public static class StoreDownloads
    {
        public static int Parallelism = 2;
        private static readonly List<StoreJob> _jobs = new List<StoreJob>();
        private static SemaphoreSlim _slots;
        private static int _nextId;

        /// <summary>A job changed state or progress (any thread).</summary>
        public static event Action<StoreJob> JobChanged;
        /// <summary>A library entry was created by a download (any thread): the entry and the file it came from.</summary>
        public static event Action<LibraryEntry, string> EntryRegistered;

        public static IReadOnlyList<StoreJob> Jobs { get { lock (_jobs) return _jobs.ToList(); } }

        private static GlobalAssetDatabase Lib => GlobalAssetDatabase.Instance;

        private static void Raise(StoreJob j) { try { JobChanged?.Invoke(j); } catch { } }

        /// <summary>Queue a download. <paramref name="addToProject"/>: also copy the result into the project.</summary>
        public static StoreJob Enqueue(IAssetProvider provider, StoreItem item, StoreVariant variant, bool addToProject = false,
                                       string projectRoot = null, string projectName = null, string targetFolder = null, bool force = false)
        {
            var j = new StoreJob
            {
                Id = Interlocked.Increment(ref _nextId), Provider = provider, Item = item, Variant = variant, AddToProject = addToProject,
                ProjectRoot = projectRoot, ProjectName = projectName, TargetFolder = targetFolder, Force = force, State = StoreJobState.Queued, Message = "Waiting…",
            };
            lock (_jobs) _jobs.Add(j);
            Raise(j);
            _ = Task.Run(() => RunAsync(j));
            return j;
        }

        public static void Cancel(StoreJob j) { try { j?.Cts.Cancel(); } catch { } }

        /// <summary>Run a failed / cancelled job again (partial downloads resume).</summary>
        public static StoreJob Retry(StoreJob j)
        {
            if (j == null || j.IsActive) return j;
            lock (_jobs) _jobs.Remove(j);
            return Enqueue(j.Provider, j.Item, j.Variant, j.AddToProject, j.ProjectRoot, j.ProjectName, j.TargetFolder, j.Force);
        }

        public static void ClearFinished() { lock (_jobs) _jobs.RemoveAll(x => !x.IsActive); }

        private static SemaphoreSlim Slots
        {
            get { lock (_jobs) return _slots ?? (_slots = new SemaphoreSlim(Math.Max(1, Parallelism))); }
        }

        /// <summary>The worker body (tests await it directly).</summary>
        public static async Task<StoreJob> RunAsync(StoreJob j)
        {
            var ct = j.Cts.Token;
            bool slot = false;
            try
            {
                await Slots.WaitAsync(ct).ConfigureAwait(false);
                slot = true;
                j.Attempts++;
                var lib = Lib;
                if (!lib.IsAvailable) throw new InvalidOperationException(lib.LastError ?? "The asset library is not available.");

                var known = j.Force ? new List<LibraryEntry>() : lib.FindByStoreKey(j.StoreKey).Where(e => lib.HasBlob(e.Hash)).ToList();
                if (known.Count > 0)
                {
                    j.AlreadyInLibrary = true;
                    j.Entries.AddRange(known);
                    j.Progress = 1;
                    j.Message = "Already in your library";
                }
                else
                {
                    j.State = StoreJobState.Resolving; j.Message = "Asking " + j.Provider.Name + "…"; Raise(j);
                    var plan = await j.Provider.ResolveAsync(j.Item, j.Variant, ct).ConfigureAwait(false);
                    string work = WorkDir(j);
                    string dl = Path.Combine(work, "download"), pkg = Path.Combine(work, "package");
                    Directory.CreateDirectory(dl);
                    if (Directory.Exists(pkg)) Directory.Delete(pkg, true);
                    Directory.CreateDirectory(pkg);
                    await DownloadAll(j, plan, dl, pkg, ct).ConfigureAwait(false);

                    j.State = StoreJobState.Importing; j.Message = "Adding to the library…"; Raise(j);
                    var entries = await Task.Run(() => Import(j, plan, pkg, ct), ct).ConfigureAwait(false);
                    j.Entries.AddRange(entries);
                    if (entries.Count == 0) throw new InvalidDataException("The download contained nothing Vortex can import.");
                    await SaveStoreThumbnail(j, entries[0], ct).ConfigureAwait(false);
                    try { Directory.Delete(work, true); } catch { }
                    j.Message = entries.Count == 1 ? "Added to your library" : "Added " + entries.Count + " assets to your library";
                }

                if (j.AddToProject && !string.IsNullOrEmpty(j.ProjectRoot))
                {
                    foreach (var e in j.Entries.Take(j.Item.Kind == StoreKind.Pack ? 0 : 1))   // packs: add single assets from the Library
                    {
                        var r = LibraryProjects.AddToProject(lib, e.Id, j.ProjectRoot, j.TargetFolder, false, j.ProjectName);
                        if (r.Status == AddToProjectStatus.Failed) throw new IOException("Add to Project: " + r.Error);
                        j.ProjectPaths.Add(r.Path);
                    }
                    if (j.ProjectPaths.Count > 0) j.Message += j.AlreadyInLibrary ? " · added to the project" : " and the project";
                }
                j.Progress = 1;
                j.State = StoreJobState.Done;
            }
            catch (OperationCanceledException) { j.State = StoreJobState.Cancelled; j.Message = "Cancelled"; }
            catch (Exception ex) { j.State = StoreJobState.Failed; j.Error = ex.Message; j.Message = ex.Message; }
            finally
            {
                if (slot) Slots.Release();
                Raise(j);
            }
            return j;
        }

        /// <summary>Stable per item + variant, so a retry resumes the partial files of the previous attempt.</summary>
        private static string WorkDir(StoreJob j)
            => Path.Combine(Lib.TempDir, "store", j.Provider.Id, LibraryProjects.SafeName(j.Item.Id + "_" + (j.Variant?.Id ?? "default")));

        private sealed class ChecksumException : IOException { public ChecksumException(string m) : base(m) { } }

        private static bool Transient(Exception ex)
        {
            if (ex is ChecksumException) return false;
            if (ex is StoreHttp.StoreHttpException h) return h.Status == HttpStatusCode.TooManyRequests || (int)h.Status >= 500;
            return ex is HttpRequestException || ex is IOException || (ex is TaskCanceledException && !(ex.InnerException is OperationCanceledException));
        }

        private static async Task DownloadAll(StoreJob j, DownloadPlan plan, string dl, string pkg, CancellationToken ct)
        {
            j.State = StoreJobState.Downloading;
            j.BytesTotal = plan.Files.Sum(f => Math.Max(0, f.Size));
            long before = 0;
            for (int i = 0; i < plan.Files.Count; i++)
            {
                var f = plan.Files[i];
                string rel = (f.RelPath ?? ("file" + i)).Replace('/', Path.DirectorySeparatorChar);
                if (rel.Contains("..")) throw new InvalidDataException("Unsafe file path in the download: " + f.RelPath);
                string target = Path.Combine(f.Unzip ? dl : pkg, rel);
                j.Message = "Downloading " + Path.GetFileName(rel) + (plan.Files.Count > 1 ? " (" + (i + 1) + "/" + plan.Files.Count + ")" : "");
                Raise(j);
                long fileBefore = before;
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (!(File.Exists(target) && Matches(target, f)))
                            await StoreHttp.DownloadAsync(j.Provider, f.Url, target, f.Authenticated, (done, total) =>
                            {
                                j.BytesDone = fileBefore + done;
                                if (j.BytesTotal <= 0 && total > 0 && plan.Files.Count == 1) j.BytesTotal = total;
                                j.Progress = j.BytesTotal > 0 ? Math.Min(0.95, 0.95 * j.BytesDone / j.BytesTotal) : 0;
                                Raise(j);
                            }, ct).ConfigureAwait(false);
                        j.State = StoreJobState.Verifying; Raise(j);
                        if (!Matches(target, f))
                        {
                            try { File.Delete(target); } catch { }
                            throw new ChecksumException("The downloaded " + Path.GetFileName(rel) + " does not match the checksum " + j.Provider.Name + " publishes.");
                        }
                        j.State = StoreJobState.Downloading;
                        break;
                    }
                    catch (ChecksumException) when (attempt == 1 && !ct.IsCancellationRequested)
                    {
                        j.Message = "Checksum mismatch — downloading " + Path.GetFileName(rel) + " again…"; Raise(j);   // once, right away
                    }
                    catch (Exception ex) when (attempt < 3 && Transient(ex) && !ct.IsCancellationRequested)
                    {
                        j.Message = "Retrying " + Path.GetFileName(rel) + " (" + ex.Message + ")…"; Raise(j);
                        await Task.Delay(TimeSpan.FromSeconds(attempt * attempt), ct).ConfigureAwait(false);
                    }
                }
                before += new FileInfo(target).Length;
                j.BytesDone = before;
                if (f.Unzip)
                {
                    j.Message = "Unpacking " + Path.GetFileName(rel) + "…"; Raise(j);
                    ZipFile.ExtractToDirectory(target, pkg, overwriteFiles: true);   // throws on entries that escape the folder
                }
            }
            j.Progress = 0.95;
        }

        private static bool Matches(string path, DownloadFile f)
        {
            if (!File.Exists(path)) return false;
            if (f.Size > 0 && new FileInfo(path).Length != f.Size && string.IsNullOrEmpty(f.Md5) && string.IsNullOrEmpty(f.Sha256)) return false;
            if (!string.IsNullOrEmpty(f.Sha256)) return string.Equals(ContentHash.OfFile(path), f.Sha256, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(f.Md5)) return string.Equals(StoreHttp.Md5(path), f.Md5, StringComparison.OrdinalIgnoreCase);
            return true;
        }

        // ================================================================== import

        private static RegisterOptions Options(StoreJob j, string name, IEnumerable<string> extraTags = null)
        {
            var it = j.Item;
            var tags = new List<string>();
            foreach (var t in it.Categories.Concat(it.Tags).Concat(extraTags ?? Enumerable.Empty<string>()))
                if (!string.IsNullOrWhiteSpace(t) && t.Length <= 32 && !tags.Contains(t, StringComparer.OrdinalIgnoreCase) && tags.Count < 14) tags.Add(t);
            return new RegisterOptions
            {
                Name = name, Tags = tags, Explicit = true,
                SourceKind = LibrarySource.Store, SourceName = j.Provider.Name, SourceUrl = it.PageUrl,
                Author = it.Author, License = it.License?.Id, Redistributable = it.License?.Redistributable ?? true,
                StoreKey = j.StoreKey, Notes = it.Description != null && it.Description.Length > 400 ? it.Description.Substring(0, 400) : it.Description,
            };
        }

        private static List<LibraryEntry> Import(StoreJob j, DownloadPlan plan, string pkg, CancellationToken ct)
        {
            switch (plan.Artifact)
            {
                case StoreArtifact.Model: return ImportModel(j, plan, pkg, ct);
                case StoreArtifact.Material: return ImportMaterial(j, plan, pkg, ct);
                case StoreArtifact.Pack: return ImportPack(j, pkg, ct);
                default: return ImportSingle(j, plan, pkg, ct);
            }
        }

        private static readonly string[] ModelPreference = { ".glb", ".gltf", ".fbx", ".obj", ".dae", ".blend", ".3ds" };

        private static List<string> AllFiles(string dir)
            => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "__MACOSX" + Path.DirectorySeparatorChar) && !Path.GetFileName(f).StartsWith(".", StringComparison.Ordinal))
                        .ToList();

        private static LibraryEntry Register(StoreJob j, string file, RegisterOptions o, CancellationToken ct)
        {
            var r = Lib.Register(file, o, ct);
            if (!r.Success) throw new IOException("Library: " + r.Error);
            try { EntryRegistered?.Invoke(r.Entry, file); } catch { }
            return r.Entry;
        }

        private static List<LibraryEntry> ImportModel(StoreJob j, DownloadPlan plan, string pkg, CancellationToken ct)
        {
            var files = AllFiles(pkg);
            string main = plan.MainFile != null ? Path.Combine(pkg, plan.MainFile.Replace('/', Path.DirectorySeparatorChar)) : null;
            if (main == null || !File.Exists(main))
                main = ModelPreference.Select(ext => files.FirstOrDefault(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(f => f != null);
            if (main == null) throw new InvalidDataException("No model file in the download.");
            string dir = Path.GetDirectoryName(main);
            var comps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                if (string.Equals(f, main, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = LibraryCompanions.Rel(dir, f);
                if (rel.StartsWith("../", StringComparison.Ordinal)) continue;   // outside the model's folder (licence files at the zip root …)
                if (LibraryCompanions.IsModelFile(f)) continue;
                comps[Path.GetFullPath(f)] = rel;
            }
            var o = Options(j, j.Item.Name);
            o.Companions = comps;
            return new List<LibraryEntry> { Register(j, main, o, ct) };
        }

        private static List<LibraryEntry> ImportMaterial(StoreJob j, DownloadPlan plan, string pkg, CancellationToken ct)
        {
            string name = LibraryProjects.SafeName(j.Item.Name).Replace(' ', '_');
            var images = AllFiles(pkg).Where(MaterialBuilder.IsImage).ToList();
            var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in plan.MapRoles) roles[Path.GetFullPath(Path.Combine(pkg, kv.Key.Replace('/', Path.DirectorySeparatorChar)))] = kv.Value;
            foreach (var img in images) if (!roles.ContainsKey(Path.GetFullPath(img))) { var r = MaterialBuilder.RoleOf(img); if (r != null) roles[Path.GetFullPath(img)] = r; }
            var mat = MaterialBuilder.Build(j.Item.Name, pkg, images.Select(Path.GetFullPath), roles);
            if (mat == null) throw new InvalidDataException("No colour or normal map found in the download.");
            // lay the material out cleanly: <name>/<name>.vmat + textures/<the maps it uses> (previews, DX duplicates, .usdc … stay behind)
            string matDir = Path.Combine(Path.GetDirectoryName(pkg), "material", name);
            if (Directory.Exists(matDir)) Directory.Delete(matDir, true);
            string texDir = Path.Combine(matDir, "textures");
            Directory.CreateDirectory(texDir);
            var comps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in MaterialBuilder.TexturePaths(mat))
            {
                string src = Path.Combine(pkg, rel.Replace('/', Path.DirectorySeparatorChar));
                string dst = Path.Combine(texDir, Path.GetFileName(src));
                File.Copy(src, dst, true);
                moved[rel] = "textures/" + Path.GetFileName(src);
                comps[Path.GetFullPath(dst)] = moved[rel];
            }
            MaterialBuilder.MapPaths(mat, p => moved.TryGetValue(p, out var n) ? n : p);
            string vmat = Path.Combine(matDir, name + ".vmat");
            if (!mat.Save(vmat)) throw new IOException("Could not write " + Path.GetFileName(vmat));
            var o = Options(j, j.Item.Name, new[] { "PBR", "Material" });
            o.Companions = comps;
            return new List<LibraryEntry> { Register(j, vmat, o, ct) };
        }

        private static List<LibraryEntry> ImportSingle(StoreJob j, DownloadPlan plan, string pkg, CancellationToken ct)
        {
            string main = plan.MainFile != null ? Path.Combine(pkg, plan.MainFile.Replace('/', Path.DirectorySeparatorChar)) : AllFiles(pkg).FirstOrDefault();
            if (main == null || !File.Exists(main)) throw new InvalidDataException("The download is empty.");
            var o = Options(j, j.Item.Name, j.Item.Kind == StoreKind.Hdri ? new[] { "HDRI", "Skybox" } : j.Item.Kind == StoreKind.Sound ? new[] { "SFX" } : null);
            o.Companions = new Dictionary<string, string>();
            return new List<LibraryEntry> { Register(j, main, o, ct) };
        }

        private static readonly string[] PackSkip = { "preview", "sample", "license", "readme", "thumbnail", "cover" };

        private static List<LibraryEntry> ImportPack(StoreJob j, string pkg, CancellationToken ct)
        {
            var files = AllFiles(pkg).Where(f =>
            {
                string n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                return !PackSkip.Any(s => n == s || n.StartsWith(s + "_", StringComparison.Ordinal)) && AssetDatabase.TypeForExtension(Path.GetExtension(f)) != AssetType.Unknown;
            }).ToList();
            // a kit ships GLB + FBX + OBJ of the same models: take the self-contained GLBs when there are any
            bool hasGlb = files.Any(f => f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase));
            files = files.Where(f => !LibraryCompanions.IsModelFile(f) || !hasGlb || f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)).ToList();
            var entries = new List<LibraryEntry>();
            string pack = j.Item.Name;
            foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var o = Options(j, Path.GetFileNameWithoutExtension(f), new[] { pack });
                o.Companions = LibraryCompanions.IsModelFile(f) && f.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ? null : new Dictionary<string, string>();
                entries.Add(Register(j, f, o, ct));
                j.Message = "Adding " + entries.Count + " / " + files.Count + "…";
                j.Progress = 0.95 + 0.05 * entries.Count / Math.Max(1, files.Count);
                if (entries.Count % 20 == 0) Raise(j);
            }
            return entries;
        }

        /// <summary>The store's own preview becomes the library thumbnail of the (main) entry.</summary>
        private static async Task SaveStoreThumbnail(StoreJob j, LibraryEntry e, CancellationToken ct)
        {
            try
            {
                if (e == null || Lib.HasThumbnail(e.Hash) || j.Item.Kind == StoreKind.Pack) return;
                var url = await j.Provider.ThumbnailUrlAsync(j.Item, ct).ConfigureAwait(false);
                var file = await StoreHttp.GetImageFileAsync(url, ct).ConfigureAwait(false);
                if (file != null) Lib.SaveThumbnail(e.Hash, File.ReadAllBytes(file));
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
    }

    /// <summary>All providers of the Store tab, in display order.</summary>
    public static class StoreProviders
    {
        public static readonly IReadOnlyList<IAssetProvider> Web = new IAssetProvider[]
        {
            new PolyHavenProvider(), new AmbientCgProvider(), new KenneyProvider(), new PolyPizzaProvider(), new FreesoundProvider(), new SketchfabProvider(),
        };

        public static readonly IReadOnlyList<IGuidedProvider> Guided = new IGuidedProvider[] { new MixamoProvider(), new SonnissProvider() };

        public static IAssetProvider Get(string id) => Web.FirstOrDefault(p => p.Id == id);
    }
}
