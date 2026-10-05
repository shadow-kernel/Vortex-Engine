using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Editor.Core.Assets;
using Editor.Core.Assets.Library;
using VortexEditor.Panels.AssetBrowser;

namespace VortexEditor.Services
{
    /// <summary>
    /// Previews for global-library entries (#60, #61). Thumbnails are cached in the library itself
    /// (<c>thumbs/ab/&lt;hash&gt;.png</c>, one per content), so the Library tab shows them instantly in every project and
    /// session. A missing thumbnail is made from a file with the entry's real extension: a project file that still holds
    /// these bytes, or the blob (+ companions) unpacked under the library's tmp/preview folder — then rendered by the
    /// same machinery as the Asset Browser (image decode, 3D preview, waveform) and stored. Callbacks run on the UI thread.
    /// </summary>
    public static class LibraryThumbs
    {
        public const int Size = 256;
        private static readonly Dictionary<string, Bitmap> Memory = new Dictionary<string, Bitmap>();
        private static readonly Dictionary<string, List<Action<Bitmap>>> Pending = new Dictionary<string, List<Action<Bitmap>>>();
        private static readonly Dictionary<string, string> PreviewPaths = new Dictionary<string, string>();

        private static GlobalAssetDatabase Lib => GlobalAssetDatabase.Instance;

        /// <summary>Can this type get a picture at all (else the tile keeps its type icon)?</summary>
        public static bool HasVisual(AssetType t)
            => t == AssetType.Texture || t == AssetType.Mesh || t == AssetType.Material || t == AssetType.Prefab || t == AssetType.Audio;

        public static Bitmap TryGet(string hash) { lock (Memory) return Memory.TryGetValue(hash ?? "", out var b) ? b : null; }

        /// <summary>The entry's thumbnail: memory → library cache → generated (and cached).</summary>
        public static void Request(LibraryEntry e, Action<Bitmap> ready)
        {
            if (e == null || ready == null || !ContentHash.IsValid(e.Hash) || !HasVisual(e.Type)) return;
            var hit = TryGet(e.Hash);
            if (hit != null) { ready(hit); return; }
            lock (Pending)
            {
                if (Pending.TryGetValue(e.Hash, out var waiting)) { waiting.Add(ready); return; }
                Pending[e.Hash] = new List<Action<Bitmap>> { ready };
            }
            var entry = e;
            Task.Run(() =>
            {
                Bitmap bmp = null;
                try
                {
                    string cached = Lib.ThumbnailPath(entry.Hash);
                    if (File.Exists(cached)) using (var s = File.OpenRead(cached)) bmp = new Bitmap(s);
                }
                catch { bmp = null; }
                if (bmp != null) { Complete(entry.Hash, bmp); return; }
                string path = null;
                try { path = PreviewPath(entry); } catch { }
                if (path == null) { Complete(entry.Hash, null); return; }
                Generate(entry.Hash, entry.Type, path, b => Complete(entry.Hash, b));
            });
        }

        /// <summary>After a registration (import / index): make the thumbnail from the project file now, unless cached.</summary>
        public static void Ensure(string hash, string sourcePath, AssetType type)
        {
            if (!ContentHash.IsValid(hash) || string.IsNullOrEmpty(sourcePath) || !HasVisual(type) || Lib.HasThumbnail(hash)) return;
            lock (Pending) { if (Pending.ContainsKey(hash)) return; Pending[hash] = new List<Action<Bitmap>>(); }
            Generate(hash, type, sourcePath, b => Complete(hash, b));
        }

        /// <summary>Make a thumbnail from <paramref name="path"/> (any thread) and store it in the library.</summary>
        private static void Generate(string hash, AssetType type, string path, Action<Bitmap> done)
        {
            void Store(Bitmap b)
            {
                if (b == null) { done(null); return; }
                try { using (var ms = new MemoryStream()) { b.Save(ms); Lib.SaveThumbnail(hash, ms.ToArray()); } } catch { }
                done(b);
            }
            if (type == AssetType.Texture)
            {
                Task.Run(() =>
                {
                    Bitmap b = null;
                    try { using (var s = File.OpenRead(path)) b = Bitmap.DecodeToWidth(s, Size); } catch { b = null; }   // TGA/PSD/HDR/DDS: no decoder → icon
                    Store(b);
                });
                return;
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (type == AssetType.Audio)
                    WaveformThumbs.Request(path, (b, tip) => Task.Run(() => Store(b)));
                else if (ThumbnailService.KindOf(path) != ThumbnailService.Kind.None)
                    ThumbnailService.Request(path, Size, b =>
                    {
                        // a library preview copy must not stay in the open project's thumbnail cache
                        if (IsPreviewCopy(path)) ThumbnailService.Invalidate(path);
                        Task.Run(() => Store(b));
                    });
                else done(null);
            });
        }

        private static void Complete(string hash, Bitmap bmp)
        {
            List<Action<Bitmap>> callbacks;
            lock (Pending) { Pending.TryGetValue(hash, out callbacks); Pending.Remove(hash); }
            if (bmp != null) lock (Memory) Memory[hash] = bmp;
            if (bmp == null || callbacks == null || callbacks.Count == 0) return;
            Dispatcher.UIThread.Post(() => { foreach (var cb in callbacks) { try { cb(bmp); } catch { } } });
        }

        /// <summary>Forget everything (library moved / cleared).</summary>
        public static void Clear()
        {
            lock (Memory) Memory.Clear();
            lock (PreviewPaths) PreviewPaths.Clear();
        }

        private static bool IsPreviewCopy(string path)
            => path.StartsWith(Path.Combine(Lib.TempDir, "preview"), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A file with the entry's bytes AND its real extension (renderers and the audio engine pick decoders by
        /// extension): a project file recorded as a usage that still has these bytes, else the blob and its companions
        /// unpacked into tmp/preview/&lt;hash&gt;/. Null when the library lost the bytes.
        /// </summary>
        public static string PreviewPath(LibraryEntry e)
        {
            if (e == null || !ContentHash.IsValid(e.Hash)) return null;
            lock (PreviewPaths)
                if (PreviewPaths.TryGetValue(e.Hash, out var known) && File.Exists(known)) return known;
            string found = null;
            foreach (var u in Lib.Usages(e.Hash))
            {
                string p = Path.IsPathRooted(u.RelativePath) ? u.RelativePath : Path.Combine(u.ProjectPath, u.RelativePath);
                try { if (File.Exists(p) && new FileInfo(p).Length == e.Size && LibraryProjects.HashOf(p, false) == e.Hash) { found = p; break; } } catch { }
            }
            if (found == null && Lib.HasBlob(e.Hash))
            {
                var full = e.Companions.Count > 0 ? e : (Lib.Get(e.Id) ?? e);
                string dir = Path.Combine(Lib.TempDir, "preview", e.Hash);
                string main = Path.Combine(dir, LibraryProjects.SafeName(full.FileName ?? (e.Hash + ".bin")));
                Directory.CreateDirectory(dir);
                if (!File.Exists(main)) File.Copy(Lib.BlobPath(e.Hash), main, true);
                foreach (var c in full.Companions)
                {
                    if (string.IsNullOrEmpty(c.RelPath) || c.RelPath.Contains("..") || !Lib.HasBlob(c.Hash)) continue;
                    string cp = Path.Combine(dir, c.RelPath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(cp));
                    if (!File.Exists(cp)) File.Copy(Lib.BlobPath(c.Hash), cp, true);
                }
                found = main;
            }
            if (found != null) lock (PreviewPaths) PreviewPaths[e.Hash] = found;
            return found;
        }

        /// <summary>Play an audio entry (straight from the library, no project needed). Replaces the previous preview.</summary>
        public static void Audition(LibraryEntry e)
        {
            if (e == null || e.Type != AssetType.Audio) return;
            var entry = e;
            Task.Run(() =>
            {
                string p = null;
                try { p = PreviewPath(entry); } catch { }
                if (p != null) Dispatcher.UIThread.Post(() => AssetActions.Audition(p));
            });
        }
    }
}
