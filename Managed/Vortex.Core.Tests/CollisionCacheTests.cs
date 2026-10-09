using System;
using System.IO;
using Editor.Core.Services.Physics;

namespace VortexTests
{
    /// <summary>The on-disk collision triangle cache (#364 C): round-trips the triangles, rejects damaged files, and
    /// keys on the model file's path + mtime + size so an overwritten model re-parses.</summary>
    public static class CollisionCacheTests
    {
        [Test]
        public static void RoundTripsTriangles(TestContext t)
        {
            string proj = t.Path("cacheproj");
            Directory.CreateDirectory(proj);
            string model = Path.Combine(proj, "terrain.glb");
            File.WriteAllBytes(model, new byte[] { 1, 2, 3 });
            string file = CollisionTriangleCache.FileFor(proj, model);
            t.NotNull(file, "a model inside a project gets a cache file");
            t.True(file.StartsWith(CollisionTriangleCache.CacheDir(proj), StringComparison.Ordinal), "under .ve/cache/collision");
            t.Equal(null, CollisionTriangleCache.TryRead(file), "nothing cached yet");

            var tris = new float[18];
            for (int i = 0; i < tris.Length; i++) tris[i] = i * 0.5f - 3f;
            t.True(CollisionTriangleCache.Write(file, tris), "written");
            var back = CollisionTriangleCache.TryRead(file);
            t.NotNull(back, "read back");
            t.Equal(tris.Length, back.Length, "same count");
            for (int i = 0; i < tris.Length; i++) t.Equal(tris[i], back[i], "float " + i);
        }

        [Test]
        public static void RejectsDamagedAndOddFiles(TestContext t)
        {
            string proj = t.Path("cacheproj2");
            Directory.CreateDirectory(CollisionTriangleCache.CacheDir(proj));
            string bad = Path.Combine(CollisionTriangleCache.CacheDir(proj), "bad.tris");
            File.WriteAllBytes(bad, new byte[] { 0, 1, 2, 3, 4, 5 });
            t.Equal(null, CollisionTriangleCache.TryRead(bad), "garbage is not a cache");
            t.False(CollisionTriangleCache.Write(bad, new float[10]), "a count that is not a multiple of 9 is refused");
            t.Equal(null, CollisionTriangleCache.FileFor(null, "x.glb"), "no project, no cache");
            t.Equal(null, CollisionTriangleCache.FileFor(proj, Path.Combine(proj, "missing.glb")), "a missing model has no key");
            t.Equal(null, CollisionTriangleCache.TryRead(null), "null file");
        }

        [Test]
        public static void KeyChangesWhenTheModelChanges(TestContext t)
        {
            string proj = t.Path("cacheproj3");
            Directory.CreateDirectory(proj);
            string model = Path.Combine(proj, "car.fbx");
            File.WriteAllBytes(model, new byte[] { 1 });
            File.SetLastWriteTimeUtc(model, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            string a = CollisionTriangleCache.FileFor(proj, model);
            File.WriteAllBytes(model, new byte[] { 1, 2 });             // size changed
            File.SetLastWriteTimeUtc(model, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            string b = CollisionTriangleCache.FileFor(proj, model);
            t.True(a != b, "a different size is a different key");
            File.SetLastWriteTimeUtc(model, new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc));   // mtime changed
            string c = CollisionTriangleCache.FileFor(proj, model);
            t.True(b != c, "a different mtime is a different key");
            t.Equal(c, CollisionTriangleCache.FileFor(proj, model), "stable for an unchanged file");
        }
    }
}
