using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace Editor.Core.Assets.Library
{
    /// <summary>
    /// Library bundles (<c>.vlib.zip</c>, #63): move a selection of the library to another PC. The zip holds
    /// <c>manifest.json</c> (names, types, tags, license/attribution, companions), <c>blobs/&lt;hash&gt;</c> and
    /// <c>thumbs/&lt;hash&gt;.png</c>. Importing dedupes by hash against the local library. Entries marked
    /// non-redistributable (Mixamo, Sonniss, … once the store lands) are never exported.
    /// </summary>
    public static class LibraryBundle
    {
        public const string Extension = ".vlib.zip";

        [DataContract(Name = "LibraryBundle")]
        private sealed class Manifest
        {
            [DataMember(Order = 0)] public int Version = 1;
            [DataMember(Order = 1)] public string Exported;
            [DataMember(Order = 2)] public List<Item> Entries = new List<Item>();
        }

        [DataContract(Name = "LibraryBundleEntry")]
        private sealed class Item
        {
            [DataMember(Order = 0)] public string Hash;
            [DataMember(Order = 1)] public string Name;
            [DataMember(Order = 2)] public string FileName;
            [DataMember(Order = 3)] public int Type;
            [DataMember(Order = 4)] public List<string> Tags = new List<string>();
            [DataMember(Order = 5)] public string SourceKind;
            [DataMember(Order = 6)] public string SourceName;
            [DataMember(Order = 7)] public string SourceUrl;
            [DataMember(Order = 8)] public string Author;
            [DataMember(Order = 9)] public string License;
            [DataMember(Order = 10)] public bool Redistributable = true;
            [DataMember(Order = 11)] public string Notes;
            [DataMember(Order = 12)] public List<Comp> Companions = new List<Comp>();
        }

        [DataContract(Name = "LibraryBundleCompanion")]
        private sealed class Comp
        {
            [DataMember(Order = 0)] public string RelPath;
            [DataMember(Order = 1)] public string Hash;
        }

        /// <summary>Write the entries (and their companions + thumbnails) to <paramref name="zipPath"/>.</summary>
        public static BundleReport Export(GlobalAssetDatabase lib, IEnumerable<long> ids, string zipPath, IProgress<LibraryProgress> progress = null, CancellationToken cancel = default)
        {
            var rep = new BundleReport();
            var manifest = new Manifest { Exported = DateTime.UtcNow.ToString("o") };
            var blobs = new HashSet<string>();
            foreach (var id in ids ?? Enumerable.Empty<long>())
            {
                var e = lib.Get(id);
                if (e == null) continue;
                if (!e.Redistributable) { rep.SkippedNotRedistributable.Add(e.Name); continue; }
                if (!lib.HasBlob(e.Hash)) { rep.Errors.Add(e.Name + ": bytes missing from the library"); continue; }
                var item = new Item
                {
                    Hash = e.Hash, Name = e.Name, FileName = e.FileName, Type = (int)e.Type, Tags = new List<string>(e.Tags),
                    SourceKind = e.SourceKind, SourceName = e.SourceName, SourceUrl = e.SourceUrl, Author = e.Author, License = e.License,
                    Redistributable = e.Redistributable, Notes = e.Notes,
                };
                blobs.Add(e.Hash);
                foreach (var c in e.Companions)
                {
                    if (!lib.HasBlob(c.Hash)) { rep.Errors.Add(e.Name + ": companion " + c.RelPath + " missing"); continue; }
                    item.Companions.Add(new Comp { RelPath = c.RelPath, Hash = c.Hash });
                    blobs.Add(c.Hash);
                }
                manifest.Entries.Add(item);
            }
            rep.Entries = manifest.Entries.Count;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath)));
            string tmp = zipPath + ".part";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                int i = 0;
                foreach (var h in blobs)
                {
                    cancel.ThrowIfCancellationRequested();
                    progress?.Report(new LibraryProgress("Exporting", i++, blobs.Count, h));
                    AddFile(zip, "blobs/" + h, lib.BlobPath(h), CompressionLevel.Optimal);
                    rep.Blobs++;
                    rep.Bytes += new FileInfo(lib.BlobPath(h)).Length;
                    if (lib.HasThumbnail(h)) AddFile(zip, "thumbs/" + h + ".png", lib.ThumbnailPath(h), CompressionLevel.NoCompression);
                }
                var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using (var s = entry.Open()) WriteJson(s, manifest);
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(tmp, zipPath);
            return rep;
        }

        private static void AddFile(ZipArchive zip, string name, string path, CompressionLevel level)
        {
            var entry = zip.CreateEntry(name, level);
            using (var dst = entry.Open())
            using (var src = File.OpenRead(path)) src.CopyTo(dst);
        }

        /// <summary>Import a bundle: blobs already in the library are not copied again, entries with known content merge
        /// their tags into the existing entry.</summary>
        public static BundleReport Import(GlobalAssetDatabase lib, string zipPath, IProgress<LibraryProgress> progress = null, CancellationToken cancel = default)
        {
            var rep = new BundleReport();
            string work = Path.Combine(lib.TempDir, "bundle-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var zip = ZipFile_OpenRead(zipPath))
                {
                    var me = zip.GetEntry("manifest.json");
                    if (me == null) { rep.Errors.Add("Not a Vortex library bundle (manifest.json missing)."); return rep; }
                    Manifest manifest;
                    using (var s = me.Open()) manifest = ReadJson<Manifest>(s);
                    if (manifest?.Entries == null) { rep.Errors.Add("The bundle manifest is unreadable."); return rep; }

                    for (int i = 0; i < manifest.Entries.Count; i++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var it = manifest.Entries[i];
                        progress?.Report(new LibraryProgress("Importing", i, manifest.Entries.Count, it.Name));
                        if (!ContentHash.IsValid(it.Hash)) { rep.Errors.Add((it.Name ?? "?") + ": bad hash"); continue; }
                        // rebuild the entry on disk (main file + companions at their relative paths), then register it
                        string dir = Path.Combine(work, i.ToString());
                        string main = Path.Combine(dir, LibraryProjects.SafeName(Path.GetFileName(it.FileName ?? it.Hash)));
                        if (!Extract(zip, it.Hash, main, lib, rep)) continue;
                        var comps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var compHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var c in it.Companions ?? new List<Comp>())
                        {
                            if (string.IsNullOrEmpty(c.RelPath) || c.RelPath.Contains("..") || !ContentHash.IsValid(c.Hash)) continue;
                            string cp = Path.Combine(dir, c.RelPath.Replace('/', Path.DirectorySeparatorChar));
                            if (!Extract(zip, c.Hash, cp, lib, rep)) continue;
                            comps[Path.GetFullPath(cp)] = c.RelPath;
                            compHashes[Path.GetFullPath(cp)] = c.Hash;
                        }
                        var res = lib.Register(main, new RegisterOptions
                        {
                            Name = it.Name, Tags = it.Tags ?? new List<string>(),
                            SourceKind = string.IsNullOrEmpty(it.SourceKind) ? LibrarySource.Bundle : it.SourceKind,
                            SourceName = it.SourceName, SourceUrl = it.SourceUrl, Author = it.Author, License = it.License,
                            Redistributable = it.Redistributable, Notes = it.Notes,
                            KnownHash = it.Hash, Companions = comps, CompanionHashes = compHashes, Explicit = true,
                        }, cancel);
                        if (!res.Success) { rep.Errors.Add((it.Name ?? it.Hash) + ": " + res.Error); continue; }
                        rep.Entries++;
                        var thumb = zip.GetEntry("thumbs/" + it.Hash + ".png");
                        if (thumb != null && !lib.HasThumbnail(it.Hash))
                            using (var s = thumb.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); lib.SaveThumbnail(it.Hash, ms.ToArray()); }
                    }
                }
            }
            catch (OperationCanceledException) { rep.Errors.Add("Cancelled"); }
            catch (Exception ex) { rep.Errors.Add(ex.Message); }
            finally { try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { } }
            return rep;
        }

        private static bool Extract(ZipArchive zip, string hash, string dest, GlobalAssetDatabase lib, BundleReport rep)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (lib.HasBlob(hash))
            {
                // the library has these bytes already: no need to unzip them
                File.Copy(lib.BlobPath(hash), dest, true);
                rep.BlobsAlreadyPresent++;
                return true;
            }
            var ze = zip.GetEntry("blobs/" + hash);
            if (ze == null) { rep.Errors.Add("Blob " + hash.Substring(0, 12) + "… missing from the bundle"); return false; }
            using (var s = ze.Open()) using (var f = File.Create(dest)) s.CopyTo(f);
            if (ContentHash.OfFile(dest) != hash) { rep.Errors.Add("Blob " + hash.Substring(0, 12) + "… is damaged in the bundle"); File.Delete(dest); return false; }
            rep.Blobs++;
            rep.Bytes += new FileInfo(dest).Length;
            return true;
        }

        private static ZipArchive ZipFile_OpenRead(string path) => new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read, false);

        private static void WriteJson<T>(Stream s, T value)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(s, Encoding.UTF8, false, true, "  ")) { ser.WriteObject(w, value); w.Flush(); }
        }

        private static T ReadJson<T>(Stream s) where T : class
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            return ser.ReadObject(s) as T;
        }
    }
}
