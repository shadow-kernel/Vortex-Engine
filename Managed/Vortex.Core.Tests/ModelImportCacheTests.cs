using System;
using System.IO;
using Editor.Core.Services;

namespace VortexTests
{
    /// <summary>The render-side model import cache (#364 C): keyed on the model file's path + mtime + size, used only
    /// when its manifest and every submesh file are present.</summary>
    public static class ModelImportCacheTests
    {
        [Test]
        public static void KeyFollowsTheModelFile(TestContext t)
        {
            string proj = t.Path("mcache");
            Directory.CreateDirectory(proj);
            string model = Path.Combine(proj, "house.glb");
            File.WriteAllBytes(model, new byte[] { 1 });
            File.SetLastWriteTimeUtc(model, new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc));
            string a = SceneRenderService.ModelImportCache.Dir(proj, model);
            t.NotNull(a, "a model inside a project gets a cache folder");
            t.True(a.StartsWith(Path.Combine(proj, ".ve", "cache", "models"), StringComparison.Ordinal), "under .ve/cache/models");
            t.Equal(a, SceneRenderService.ModelImportCache.Dir(proj, model), "stable for an unchanged file");
            File.SetLastWriteTimeUtc(model, new DateTime(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc));
            t.True(a != SceneRenderService.ModelImportCache.Dir(proj, model), "an overwritten model gets a new key");
            t.Equal(null, SceneRenderService.ModelImportCache.Dir(null, model), "no project, no cache");
            t.Equal(null, SceneRenderService.ModelImportCache.Dir(proj, Path.Combine(proj, "missing.glb")), "no file, no key");
        }

        [Test]
        public static void OnlyACompleteCacheCounts(TestContext t)
        {
            string dir = Path.Combine(t.Path("mcache2"), "abc");
            t.Equal(-1, SceneRenderService.ModelImportCache.Count(dir), "nothing there");
            t.Equal(-1, SceneRenderService.ModelImportCache.Count(null), "null dir");

            SceneRenderService.ModelImportCache.WriteManifest(dir, 2);
            t.Equal(-1, SceneRenderService.ModelImportCache.Count(dir), "a manifest without its submesh files is not a cache");
            File.WriteAllBytes(SceneRenderService.ModelImportCache.SubmeshFile(dir, 0), new byte[] { 1 });
            t.Equal(-1, SceneRenderService.ModelImportCache.Count(dir), "still one file short");
            File.WriteAllBytes(SceneRenderService.ModelImportCache.SubmeshFile(dir, 1), new byte[] { 2 });
            t.Equal(-1, SceneRenderService.ModelImportCache.Count(dir), "the material records are missing");
            File.WriteAllBytes(SceneRenderService.ModelImportCache.Materials(dir), new byte[] { 3 });
            t.Equal(2, SceneRenderService.ModelImportCache.Count(dir), "complete: two submeshes + materials");

            File.WriteAllText(SceneRenderService.ModelImportCache.Manifest(dir), "something else\n2\n");
            t.Equal(-1, SceneRenderService.ModelImportCache.Count(dir), "a foreign manifest is ignored");
        }
    }
}
