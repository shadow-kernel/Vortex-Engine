using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Assets.Library;
using Editor.Core.Serialization;

namespace VortexTests
{
    /// <summary>Global asset library (milestone v2.9.0): hashing, catalog, blob store, projects, maintenance.</summary>
    public static class LibraryTests
    {
        private static GlobalAssetDatabase NewLib(TestContext t, string name = "lib")
        {
            var lib = new GlobalAssetDatabase(t.Path(name));
            t.True(lib.EnsureOpen(), "library opens (" + lib.LastError + ")");
            return lib;
        }

        private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

        [Test]
        public static void Sha256KnownVector(TestContext t)
        {
            t.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ContentHash.OfBytes(Bytes("abc")), "sha256(abc)");
            var f = t.Write("abc.txt", "abc");
            t.Equal(ContentHash.OfBytes(Bytes("abc")), ContentHash.OfFile(f), "file hash = bytes hash");
            var copy = t.Path("copy.txt");
            t.Equal(ContentHash.OfFile(f), ContentHash.CopyAndHash(f, copy), "copy-and-hash");
            t.True(ContentHash.IsValid(ContentHash.OfFile(copy)), "valid hash");
        }

        [Test]
        public static void VmetaContentHashRoundTrip(TestContext t)
        {
            var asset = t.Write("Assets/a.png", new byte[] { 1, 2, 3, 4 });
            var meta = new AssetMetadata(AssetType.Texture, "Assets/a.png", "a.png");
            t.True(meta.UpdateContentHash(asset), "hash computed");
            t.True(meta.HasFreshContentHash(asset), "fresh after hashing");
            DataSerializer.SaveAsJson(meta, asset + ".vmeta");
            var back = DataSerializer.LoadFromJson<AssetMetadata>(asset + ".vmeta");
            t.Equal(meta.ContentHash, back.ContentHash, "ContentHash survives .vmeta");
            t.True(back.HasFreshContentHash(asset), "still fresh after reload");
            // a rename keeps the stamp (size + mtime), so the hash stays fresh
            var moved = t.Path("Assets", "b.png");
            File.Move(asset, moved);
            t.True(back.HasFreshContentHash(moved), "rename keeps the hash");
            // editing the file makes it stale
            File.WriteAllBytes(moved, new byte[] { 9, 9, 9, 9, 9 });
            t.False(back.HasFreshContentHash(moved), "edit makes the hash stale");
            // an old .vmeta without the fields still loads
            var old = t.Write("old.png.vmeta", "{\"Guid\":\"" + Guid.NewGuid() + "\",\"Type\":3,\"RelativePath\":\"old.png\",\"FileName\":\"old.png\",\"Dependencies\":[],\"ImportSettings\":{},\"Tags\":[]}");
            var legacy = DataSerializer.LoadFromJson<AssetMetadata>(old);
            t.NotNull(legacy, "legacy .vmeta loads");
            t.True(legacy.ContentHash == null, "legacy has no hash");
        }

        [Test]
        public static void RegisterDedupesByContent(TestContext t)
        {
            var lib = NewLib(t);
            var a = t.Write("p1/Assets/Audio/creak.wav", "RIFF-fake-creak");
            var b = t.Write("p2/Assets/Sfx/door_creak.wav", "RIFF-fake-creak");   // same bytes, other project + name
            var r1 = lib.Register(a, new RegisterOptions { Tags = { "door", "Horror" }, ProjectPath = t.Path("p1"), ProjectName = "P1", Explicit = true });
            t.True(r1.Success, "first registration: " + r1.Error);
            t.False(r1.EntryExisted, "new entry");
            var r2 = lib.Register(b, new RegisterOptions { Tags = { "creak" }, ProjectPath = t.Path("p2"), ProjectName = "P2", Explicit = true });
            t.True(r2.Success, "second registration: " + r2.Error);
            t.True(r2.EntryExisted && r2.BlobExisted, "same bytes → existing entry + blob");
            t.Equal(0L, r2.BytesWritten, "no second write");
            t.Equal(1, lib.Count(), "one entry");
            var e = lib.Get(r1.Entry.Id);
            t.Equal(AssetType.Audio, e.Type, "type from extension");
            t.True(e.Tags.Contains("creak") && e.Tags.Contains("door") && e.Tags.Contains("Horror"), "tags merged: " + string.Join(",", e.Tags));
            t.Equal(2, lib.Usages(e.Hash).Count, "two project usages");
            var blobs = Directory.GetFiles(lib.BlobDir, "*", SearchOption.AllDirectories);
            t.Equal(1, blobs.Length, "one blob on disk");
            // "Import as new": a second name for the same bytes, still one blob
            var r3 = lib.Register(b, new RegisterOptions { Name = "Door Creak Alt", ForceNewEntry = true, Explicit = true });
            t.True(r3.Success && !r3.EntryExisted, "forced alias");
            t.Equal(2, lib.Count(), "two entries");
            t.Equal(1, Directory.GetFiles(lib.BlobDir, "*", SearchOption.AllDirectories).Length, "still one blob");
            t.Equal(2, lib.FindByHash(e.Hash).Count, "both entries share the hash");
        }

        [Test]
        public static void TypeRulesSkipExcluded(TestContext t)
        {
            var lib = NewLib(t);
            var cs = t.Write("Assets/Scripts/Player.cs", "class Player {}");
            var r = lib.Register(cs, new RegisterOptions());
            t.True(r.Success && r.Skipped, "scripts are excluded by default");
            var r2 = lib.Register(cs, new RegisterOptions { Explicit = true });
            t.True(r2.Success && !r2.Skipped, "explicit Add to Library ignores the rules");
            lib.Settings.AutoRegisterImports = false;
            var png = t.Write("Assets/x.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0 });
            t.True(lib.Register(png, new RegisterOptions { IsAutomatic = true }).Skipped, "auto-registration off");
            var r3 = lib.Register(png, new RegisterOptions());
            t.True(r3.Success && !r3.Skipped, "manual registration still works");
            t.Equal(2, r3.Entry.Width ?? 0, "png width from header");
            t.Equal(3, r3.Entry.Height ?? 0, "png height from header");
        }

        [Test]
        public static void QueryFiltersAndSorts(TestContext t)
        {
            var lib = NewLib(t);
            lib.Register(t.Write("a/stone_wall.png", "img-1"), new RegisterOptions { Explicit = true, Tags = { "PBR", "Wall" } });
            lib.Register(t.Write("a/wood_floor.png", "img-22"), new RegisterOptions { Explicit = true, Tags = { "PBR" } });
            lib.Register(t.Write("a/scream.ogg", "snd-333"), new RegisterOptions { Explicit = true, Tags = { "Horror" } });
            lib.Register(t.Write("a/crate.vmat", "mat-4444"), new RegisterOptions { Explicit = true, Tags = { "Wall_Prop" } });
            t.Equal(4, lib.Count(), "all");
            t.Equal(1, lib.Count(new LibraryQuery { Search = "STONE" }), "search is case-insensitive");
            t.Equal(1, lib.Count(new LibraryQuery { Search = "horror" }), "search matches tags");
            t.Equal(2, lib.Count(new LibraryQuery { Types = new List<AssetType> { AssetType.Texture } }), "type filter");
            t.Equal(1, lib.Count(new LibraryQuery { Tags = new List<string> { "pbr", "wall" } }), "all tags must match (case-insensitive)");
            t.Equal(3, lib.Count(new LibraryQuery { Search = "_" }), "'_' is literal (2 names + 1 tag), not a LIKE wildcard");
            t.Equal(0, lib.Count(new LibraryQuery { Search = "%" }), "'%' is literal");
            var bySize = lib.Query(new LibraryQuery { Sort = LibrarySort.Size });
            t.Equal("crate", bySize[0].Name, "size sort: biggest first");
            var byName = lib.Query(new LibraryQuery { Sort = LibrarySort.Name });
            t.Equal("crate", byName[0].Name, "name sort");
            t.Equal(2, lib.Query(new LibraryQuery { Limit = 2, Offset = 1 }).Count, "paging");
        }

        [Test]
        public static void TagManagement(TestContext t)
        {
            var lib = NewLib(t);
            var e1 = lib.Register(t.Write("x/1.png", "1"), new RegisterOptions { Explicit = true, Tags = { "scary" } }).Entry;
            var e2 = lib.Register(t.Write("x/2.png", "22"), new RegisterOptions { Explicit = true, Tags = { "Creepy" } }).Entry;
            t.Equal(2, lib.AddTags(new[] { e1.Id, e2.Id }, new[] { "Batch" }), "bulk add");
            t.Equal(1, lib.RemoveTags(new[] { e2.Id }, new[] { "batch" }), "bulk remove (case-insensitive)");
            lib.RenameTag("scary", "Scary");
            t.True(lib.Get(e1.Id).Tags.Contains("Scary"), "case-only rename");
            lib.MergeTags("Creepy", "Scary");
            t.Equal(2, lib.TagCounts().First(x => x.tag == "Scary").count, "merged into Scary");
            t.False(lib.AllTags().Contains("Creepy"), "old tag gone");
            lib.DeleteTag("Batch");
            t.False(lib.AllTags().Contains("Batch"), "deleted");
            lib.SetTags(e2.Id, new[] { "a", "b", "a", " " });
            t.Equal(2, lib.Get(e2.Id).Tags.Count, "SetTags cleans duplicates/blanks");
        }

        [Test]
        public static void SavedFilters(TestContext t)
        {
            var lib = NewLib(t);
            lib.SaveFilter(new SavedFilter { Name = "Horror SFX", Search = "creak", Types = { AssetType.Audio }, Tags = { "Horror", "Door" } });
            var f = lib.SavedFilters().Single();
            t.Equal("creak", f.Search, "search kept");
            t.Equal(AssetType.Audio, f.Types.Single(), "types kept");
            t.Equal(2, f.Tags.Count, "tags kept");
            lib.DeleteFilter("horror sfx");
            t.Equal(0, lib.SavedFilters().Count, "deleted (case-insensitive name)");
        }

        /// <summary>A small glTF in its own folder (the import pipeline's layout).</summary>
        private static string MakeModel(TestContext t, string projectRel)
        {
            string dir = projectRel + "/Assets/Models/Crate";
            t.Write(dir + "/crate.bin", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            t.Write(dir + "/textures/crate_albedo.png", "albedo-bytes");
            t.Write(dir + "/materials/submesh_0.vmat", "{\"Name\":\"crate\"}");
            t.Write(dir + "/crate.gltf.vmeta", "{}");   // never travels
            return t.Write(dir + "/crate.gltf", "{\"buffers\":[{\"uri\":\"crate.bin\",\"byteLength\":8}],\"images\":[{\"uri\":\"textures/crate_albedo.png\"}]}");
        }

        [Test]
        public static void ModelCompanionsAndAddToProject(TestContext t)
        {
            var lib = NewLib(t);
            var model = MakeModel(t, "src");
            var comps = LibraryCompanions.Detect(model);
            t.Equal(3, comps.Count, "bin + texture + material (no .vmeta)");
            t.True(comps.Values.Contains("textures/crate_albedo.png"), "relative companion path");
            var r = lib.Register(model, new RegisterOptions { Explicit = true, Tags = { "Prop" } });
            t.True(r.Success, "model registered: " + r.Error);
            t.Equal(3, lib.Get(r.Entry.Id).Companions.Count, "companions stored");

            // add to another project
            string proj = t.Path("game");
            Directory.CreateDirectory(Path.Combine(proj, "Assets"));
            var add = LibraryProjects.AddToProject(lib, r.Entry.Id, proj);
            t.Equal(AddToProjectStatus.Added, add.Status, "added: " + add.Error);
            t.True(File.Exists(Path.Combine(proj, "Assets", "Models", "crate", "crate.gltf")), "model in its own folder");
            t.True(File.Exists(Path.Combine(proj, "Assets", "Models", "crate", "textures", "crate_albedo.png")), "companion restored");
            var meta = DataSerializer.LoadFromJson<AssetMetadata>(add.Path + ".vmeta");
            t.Equal(r.Hash, meta.ContentHash, ".vmeta carries the library hash");
            t.True(meta.HasFreshContentHash(add.Path), "hash fresh for the copy");
            t.True(meta.Tags.Contains("Prop"), "tags copied");
            t.Equal(ContentHash.OfFile(model), ContentHash.OfFile(add.Path), "byte-identical copy");
            // second add: detected
            var again = LibraryProjects.AddToProject(lib, r.Entry.Id, proj);
            t.Equal(AddToProjectStatus.AlreadyInProject, again.Status, "duplicate detected");
            t.Equal(Path.GetFullPath(add.Path), Path.GetFullPath(again.Path), "points at the existing file");
            var dup = LibraryProjects.AddToProject(lib, r.Entry.Id, proj, allowDuplicate: true);
            t.Equal(AddToProjectStatus.Added, dup.Status, "explicit duplicate");
            t.True(dup.Path.Contains("crate_1"), "keep-both naming: " + dup.Path);
        }

        [Test]
        public static void PreviewLaysOutCompanionsWithoutTouchingBlobs(TestContext t)
        {
            var lib = NewLib(t);
            var model = MakeModel(t, "src");
            var shared = t.Write("src/shared/grime.png", new byte[] { 7, 7, 7 });
            var comps = LibraryCompanions.Detect(model);
            comps[Path.GetFullPath(shared)] = "../shared/grime.png";   // a companion above the model's folder
            var r = lib.Register(model, new RegisterOptions { Explicit = true, Companions = comps });
            t.True(r.Success, "registered: " + r.Error);
            var e = lib.Get(r.Entry.Id);
            string main = LibraryPreview.Materialize(lib, e);
            t.True(main.StartsWith(Path.GetFullPath(lib.TempDir), StringComparison.Ordinal), "preview lives in the library's temp folder: " + main);
            t.Equal(ContentHash.OfFile(model), ContentHash.OfFile(main), "main file byte-identical");
            string dir = Path.GetDirectoryName(main);
            t.True(File.Exists(Path.Combine(dir, "textures", "crate_albedo.png")), "companion at its relative path");
            t.True(File.Exists(Path.GetFullPath(Path.Combine(dir, "..", "shared", "grime.png"))), "companion above the model folder is laid out too");
            t.True(Path.GetFullPath(Path.Combine(dir, "..")).StartsWith(Path.GetFullPath(Path.Combine(lib.TempDir, "preview")), StringComparison.Ordinal), "… and stays inside the preview folder");
            // a viewer that writes to the preview copy can't change the content-addressed blob
            File.WriteAllText(main, "changed by a viewer");
            t.Equal(r.Hash, ContentHash.OfFile(lib.BlobPath(r.Hash)), "blob untouched");
            string again = LibraryPreview.Materialize(lib, e);
            t.Equal(main, again, "same place on the second preview");
            t.Equal(0, LibraryPreview.LeadingUps("textures/a.png"), "no ups");
            t.Equal(2, LibraryPreview.LeadingUps("../../a.png"), "two ups");
        }

        [Test]
        public static void DeleteCollectsUnreferencedBlobs(TestContext t)
        {
            var lib = NewLib(t);
            var a = lib.Register(t.Write("x/a.png", "shared"), new RegisterOptions { Explicit = true }).Entry;
            var alias = lib.Register(t.Write("x/b.png", "shared"), new RegisterOptions { Explicit = true, ForceNewEntry = true }).Entry;
            var solo = lib.Register(t.Write("x/c.png", "solo-bytes"), new RegisterOptions { Explicit = true }).Entry;
            lib.SaveThumbnail(solo.Hash, new byte[] { 1, 2, 3 });
            t.Equal(1, lib.Delete(new[] { a.Id }, out long freed1), "deleted one alias");
            t.Equal(0L, freed1, "shared blob kept while the alias exists");
            t.True(lib.HasBlob(alias.Hash), "blob still there");
            lib.Delete(new[] { alias.Id, solo.Id }, out long freed2);
            t.Equal((long)("shared".Length + "solo-bytes".Length), freed2, "both blobs freed");
            t.False(lib.HasBlob(alias.Hash) || lib.HasBlob(solo.Hash), "blob files gone");
            t.False(lib.HasThumbnail(solo.Hash), "thumbnail gone");
            t.Equal(0, lib.Stats().Blobs, "no blobs left");
        }

        [Test]
        public static void VerifyAndOrphans(TestContext t)
        {
            var lib = NewLib(t);
            var e = lib.Register(t.Write("x/a.wav", "audio-bytes"), new RegisterOptions { Explicit = true }).Entry;
            var e2 = lib.Register(t.Write("x/b.wav", "other-audio"), new RegisterOptions { Explicit = true }).Entry;
            t.True(lib.Verify().Ok, "clean library verifies");
            File.WriteAllText(lib.BlobPath(e.Hash), "tampered");
            File.Delete(lib.BlobPath(e2.Hash));
            var v = lib.Verify();
            t.Equal(1, v.Corrupt.Count, "corrupt blob found");
            t.Equal(1, v.Missing.Count, "missing blob found");
            // an untracked blob file + a stale thumbnail
            string fake = new string('a', 64);
            Directory.CreateDirectory(Path.GetDirectoryName(lib.BlobPath(fake)));
            File.WriteAllText(lib.BlobPath(fake), "stray");
            lib.SaveThumbnail(new string('b', 64), new byte[] { 1 });
            var dry = lib.ScanOrphans(false);
            t.Equal(1, dry.UntrackedFiles.Count, "untracked file");
            t.Equal(1, dry.MissingFiles.Count, "missing file");
            t.Equal(1, dry.StaleThumbnails.Count, "stale thumbnail");
            t.Equal(1, dry.BrokenEntries.Count, "entry without bytes");
            t.True(File.Exists(lib.BlobPath(fake)), "dry run changes nothing");
            var fix = lib.ScanOrphans(true);
            t.True(fix.Fixed, "fixed");
            t.False(File.Exists(lib.BlobPath(fake)), "untracked file removed");
            var after = lib.ScanOrphans(false);
            t.Equal(0, after.UntrackedFiles.Count + after.StaleThumbnails.Count + after.MissingFiles.Count, "clean after fix");
            t.Equal(1, after.BrokenEntries.Count, "broken entry stays for the user to decide");
        }

        [Test]
        public static void BundleExportImport(TestContext t)
        {
            var src = NewLib(t, "libA");
            var model = MakeModel(t, "src");
            var m = src.Register(model, new RegisterOptions { Explicit = true, Tags = { "Prop" }, Author = "Me", License = "CC0" }).Entry;
            var s = src.Register(t.Write("x/rain.ogg", "rain-loop"), new RegisterOptions { Explicit = true, Tags = { "Ambience" } }).Entry;
            var locked = src.Register(t.Write("x/mixamo.fbx", "mixamo-anim"), new RegisterOptions { Explicit = true, Redistributable = false }).Entry;
            src.SaveThumbnail(s.Hash, new byte[] { 7, 7, 7 });
            string zip = t.Path("out", "export" + LibraryBundle.Extension);
            var ex = LibraryBundle.Export(src, new[] { m.Id, s.Id, locked.Id }, zip);
            t.Equal(2, ex.Entries, "two entries exported");
            t.Equal(1, ex.SkippedNotRedistributable.Count, "non-redistributable skipped");
            t.Equal(5, ex.Blobs, "model + 3 companions + sound");

            var dst = NewLib(t, "libB");
            var im = LibraryBundle.Import(dst, zip);
            t.Equal(0, im.Errors.Count, "no errors: " + string.Join("; ", im.Errors));
            t.Equal(2, im.Entries, "two entries imported");
            var got = dst.FindByHash(m.Hash).Single();
            t.Equal(3, dst.Get(got.Id).Companions.Count, "companions imported");
            t.Equal("CC0", got.License, "license kept");
            t.True(dst.HasThumbnail(s.Hash), "thumbnail imported");
            var again = LibraryBundle.Import(dst, zip);
            t.Equal(5, again.BlobsAlreadyPresent, "re-import dedupes by hash");
            t.Equal(2, dst.Count(), "no duplicate entries");
        }

        [Test]
        public static void ConcurrentWritersShareOneCatalog(TestContext t)
        {
            // two connections = two editor instances (SQLite WAL locking is per connection)
            var a = NewLib(t);
            var b = new GlobalAssetDatabase(t.Path("lib"));
            t.True(b.EnsureOpen(), "second instance opens");
            var files = Enumerable.Range(0, 40).Select(i => t.Write("f/" + i + ".png", "bytes-" + (i % 20))).ToList();   // 20 distinct contents
            var tasks = new List<Task>();
            int errors = 0;
            for (int w = 0; w < 4; w++)
            {
                var lib = w % 2 == 0 ? a : b;
                int start = w;
                tasks.Add(Task.Run(() =>
                {
                    for (int i = start; i < files.Count; i += 4)
                        if (!lib.Register(files[i], new RegisterOptions { Explicit = true, Tags = { "w" + start } }).Success) Interlocked.Increment(ref errors);
                }));
            }
            Task.WaitAll(tasks.ToArray());
            t.Equal(0, errors, "no failed registrations");
            t.Equal(20, a.Count(), "one entry per distinct content");
            t.Equal(20, b.Stats().Blobs, "one blob per distinct content");
            b.Dispose();
        }

        [Test]
        public static void MoveLibraryKeepsEverything(TestContext t)
        {
            var lib = NewLib(t, "old");
            var e = lib.Register(t.Write("x/a.png", "move-me"), new RegisterOptions { Explicit = true, Tags = { "keep" } }).Entry;
            lib.SaveThumbnail(e.Hash, new byte[] { 1, 2 });
            string newRoot = t.Path("new-place");
            t.True(lib.MoveLibrary(newRoot, null, CancellationToken.None, out string err), "moved: " + err);
            t.Equal(Path.GetFullPath(newRoot), Path.GetFullPath(lib.Root), "root switched");
            t.True(lib.HasBlob(e.Hash) && lib.HasThumbnail(e.Hash), "blob + thumbnail at the new place");
            t.True(lib.Get(e.Id).Tags.Contains("keep"), "catalog moved");
            t.False(File.Exists(Path.Combine(t.Path("old"), "catalog.db")), "old catalog removed");
            t.False(lib.MoveLibrary(newRoot, null, CancellationToken.None, out err), "moving onto itself is refused");
            // the setting was written (this test's private appdata)
            t.Equal(Path.GetFullPath(newRoot), Path.GetFullPath(LibrarySettings.Load().Root), "setting persisted");
            var s = LibrarySettings.Load(); s.Root = null; s.Save();
        }

        [Test]
        public static void IndexProjectsCollapsesDuplicates(TestContext t)
        {
            var lib = NewLib(t);
            MakeModel(t, "p1");
            t.Write("p1/Assets/Audio/step.wav", "step");
            var tex = t.Write("p1/Assets/Textures/brick.png", "brick");
            DataSerializer.SaveAsJson(new AssetMetadata(AssetType.Texture, "Assets/Textures/brick.png", "brick.png") { Tags = { "Wall" } }, tex + ".vmeta");
            t.Write("p1/Assets/Scripts/Game.cs", "class Game {}");   // excluded type
            t.Write("p2/Assets/Sounds/footstep.wav", "step");      // same bytes as p1's step.wav
            var seen = new List<string>();
            var rep = LibraryProjects.IndexProjects(lib, new[] { (t.Path("p1"), "P1"), (t.Path("p2"), "P2") }, (h, f, ty) => seen.Add(f));
            t.Equal(0, rep.Errors.Count, "no errors: " + string.Join("; ", rep.Errors));
            t.Equal(2, rep.Projects, "two projects");
            t.Equal(3, rep.Registered, "model + sound + texture");
            t.Equal(1, rep.DuplicatesCollapsed, "the second footstep collapses");
            t.Equal(1, rep.Skipped, "the script is skipped");
            t.Equal(3, lib.Count(), "three entries");
            t.True(lib.Query(new LibraryQuery { Search = "brick" }).Single().Tags.Contains("Wall"), ".vmeta tags carried over");
            var brickMeta = DataSerializer.LoadFromJson<AssetMetadata>(tex + ".vmeta");
            t.True(brickMeta.HasFreshContentHash(tex), "existing .vmeta got its ContentHash");
            t.False(File.Exists(t.Path("p2", "Assets", "Sounds", "footstep.wav.vmeta")), "no .vmeta is created");
            var again = LibraryProjects.IndexProjects(lib, new[] { (t.Path("p1"), "P1") });
            t.Equal(0, again.Registered, "re-run registers nothing new");
            t.Equal(3, again.AlreadyKnown, "re-run sees known content");
        }

        [Test]
        public static void QueryScalesToAThousandPlus(TestContext t)
        {
            // the Library tab queries the whole result set at once (virtualised grid): thousands of entries must stay fast
            var lib = NewLib(t);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 1200; i++)
                lib.Register(t.Write("many/asset_" + i + (i % 3 == 0 ? ".png" : i % 3 == 1 ? ".wav" : ".vmat"), "content-" + i),
                             new RegisterOptions { Explicit = true, Tags = { "bulk", "t" + (i % 10) } });
            long registerMs = sw.ElapsedMilliseconds;
            sw.Restart();
            var all = lib.Query(new LibraryQuery());
            long queryMs = sw.ElapsedMilliseconds;
            sw.Restart();
            int some = lib.Count(new LibraryQuery { Search = "asset_12", Tags = new List<string> { "bulk" } });
            long searchMs = sw.ElapsedMilliseconds;
            Console.WriteLine("        1,200 registrations " + registerMs + " ms, full query " + queryMs + " ms, tag+search count " + searchMs + " ms");
            t.Equal(1200, all.Count, "all entries");
            t.True(some >= 11, "search hits");
            t.True(queryMs < 1000, "full query under a second (" + queryMs + " ms)");
        }

        [Test]
        public static void ImportHookHashesAndRegisters(TestContext t)
        {
            // the import dialogs call QueueImported; the global instance lives in VORTEX_ASSETDB_DIR
            string libDir = t.Path("hooklib");
            Environment.SetEnvironmentVariable("VORTEX_ASSETDB_DIR", libDir);
            try
            {
                GlobalAssetDatabase.ResetInstance();
                var tex = t.Write("game/Assets/Textures/moss.png", "moss");
                DataSerializer.SaveAsJson(new AssetMetadata(AssetType.Texture, "Assets/Textures/moss.png", "moss.png"), tex + ".vmeta");
                RegisterResult got = null;
                Action<string, RegisterResult> h = (f, r) => got = r;
                LibraryProjects.Registered += h;
                LibraryProjects.QueueImported(tex, new[] { "Nature" }, t.Path("game"), "Game");
                t.True(LibraryProjects.WaitForQueue(10000), "queue drained");
                LibraryProjects.Registered -= h;
                t.NotNull(got, "registration reported");
                t.True(got.Success, "registered: " + got.Error);
                var meta = DataSerializer.LoadFromJson<AssetMetadata>(tex + ".vmeta");
                t.Equal(got.Hash, meta.ContentHash, "import wrote the hash into the .vmeta");
                t.Equal(Path.GetFullPath(libDir), Path.GetFullPath(GlobalAssetDatabase.Instance.Root), "instance follows VORTEX_ASSETDB_DIR");
                t.True(GlobalAssetDatabase.Instance.FindByHash(got.Hash).Single().Tags.Contains("Nature"), "import tags");
            }
            finally { GlobalAssetDatabase.ResetInstance(); }
        }
    }
}
