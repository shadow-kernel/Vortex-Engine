using System;
using System.IO;
using Editor.Core.Animation;
using Editor.Core.Assets;

namespace VortexTests
{
    /// <summary>The clip (.vanim) and skeleton caches follow the files on disk (#419): an edited clip plays from the next
    /// run on under its old name, a clip created after a failed request loads, and a cache entry is re-read when its
    /// file changes — not only after an editor restart.</summary>
    public static class AnimationCacheTests
    {
        private static void WriteClip(string path, float x, DateTime stamp)
        {
            var clip = new VortexAnimClip { Name = "Walk", DurationSec = 1f };
            var track = new AnimTrack { Bone = "Hips" };
            track.Pos.Add(new AnimKeyVec3 { T = 0f, X = x });
            clip.Tracks.Add(track);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!clip.Save(path)) throw new IOException("could not write " + path);
            File.SetLastWriteTimeUtc(path, stamp);   // a distinct time even on a file system with a coarse clock
        }

        private static float KeyX(VortexAnimClip clip) => clip.Tracks[0].Pos[0].X;

        [Test]
        public static void AnEditedClipPlaysFromTheNextRun(TestContext t)
        {
            var svc = AnimationService.Instance;
            string path = t.Path("Assets", "Hero", "animations", "Walk.vanim");
            WriteClip(path, 1f, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
            var first = svc.GetClip(path);
            t.NotNull(first, "the clip loads");
            t.Equal(1f, KeyX(first), "its keys");
            t.True(ReferenceEquals(first, svc.GetClip(path)), "cached: the same object again");

            // overwritten by a script / git / an exporter — no Keyframe Editor involved
            WriteClip(path, 2f, new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc));
            svc.ResetStates();   // Play pressed again
            var second = svc.GetClip(path);
            t.Equal(2f, KeyX(second), "the next run reads the new keys under the same file name");
            t.True(ReferenceEquals(second, svc.GetClip(path)), "and caches them");

            // the asset watcher reports the change while the editor runs
            WriteClip(path, 3f, new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc));
            svc.InvalidateClip(path);
            t.Equal(3f, KeyX(svc.GetClip(path)), "an invalidated clip is read again at once");
        }

        [Test]
        public static void AMissingClipLoadsOnceItExists(TestContext t)
        {
            var svc = AnimationService.Instance;
            string path = t.Path("Assets", "Crowd", "Wave.vanim");
            t.Equal(null, svc.GetClip(path), "not there yet");
            t.Equal(null, svc.GetClip(path), "still not there");
            WriteClip(path, 5f, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            var clip = svc.GetClip(path);
            t.NotNull(clip, "created after the failed request: the next one loads it (no restart)");
            t.Equal(5f, KeyX(clip), "with its keys");

            // a half-written file fails to parse; once it is complete it loads
            string partial = t.Path("Assets", "Crowd", "Run.vanim");
            File.WriteAllText(partial, "{ \"name\": \"Run\", \"tracks\": [");
            File.SetLastWriteTimeUtc(partial, new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc));
            t.Equal(null, svc.GetClip(partial), "an unreadable clip is no clip");
            WriteClip(partial, 7f, new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc));
            t.Equal(7f, KeyX(svc.GetClip(partial)), "the finished file loads");
        }

        [Test]
        public static void StampCacheReloadsChangedFiles(TestContext t)
        {
            // the cache behind both the clips and the skeletons (a skeleton needs the native importer, so a counting loader
            // stands in for it): one load per file version
            string model = t.Write("Assets/Hero.glb", "rig v1");
            File.SetLastWriteTimeUtc(model, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
            int loads = 0;
            var cache = new FileStampCache<string>(p => { loads++; return File.Exists(p) ? File.ReadAllText(p) : null; }) { RecheckMs = long.MaxValue };
            t.Equal("rig v1", cache.Get(model), "first load");
            t.Equal("rig v1", cache.Get(model), "cached");
            t.Equal(1, loads, "one load");

            File.WriteAllText(model, "rig v2, more bones");
            File.SetLastWriteTimeUtc(model, new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc));
            t.Equal("rig v1", cache.Get(model), "within the re-check interval the entry is trusted");
            cache.MarkAllStale();
            t.Equal("rig v2, more bones", cache.Get(model), "a stale entry compares its file and reloads the new one");
            t.Equal(2, loads, "one load per version");
            t.Equal("rig v2, more bones", cache.Get(model), "and is trusted again");
            cache.MarkAllStale();
            t.Equal("rig v2, more bones", cache.Get(model), "an unchanged file is not read again");
            t.Equal(2, loads, "still two loads");

            cache.RecheckMs = 0;   // compare on every request
            File.WriteAllText(model, "rig v3");
            File.SetLastWriteTimeUtc(model, new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc));
            t.Equal("rig v3", cache.Get(model), "the file changed: reloaded without being told");
            t.Equal(3, loads, "three versions, three loads");

            t.True(cache.Invalidate(Path.Combine(Path.GetDirectoryName(model), ".", "Hero.glb")), "one entry per file, however the path is spelled");
            t.Equal("rig v3", cache.Get(model), "an invalidated entry loads again");
            t.Equal(4, loads, "from the file");

            string gone = t.Path("Assets", "Gone.glb");
            t.Equal(null, cache.Get(gone), "missing");
            File.WriteAllText(gone, "late rig");
            t.Equal("late rig", cache.Get(gone), "a failed load is retried once the file exists");
        }

        [Test]
        public static void APakNeverChanges(TestContext t)
        {
            // a shipped game reads the read-only pak: what was loaded (or not found) stays, no file is compared
            string clip = t.Write("Assets/Idle.vanim", "v1");
            int loads = 0;
            var cache = new FileStampCache<string>(p => { loads++; return File.Exists(p) ? File.ReadAllText(p) : null; }, () => true) { RecheckMs = 0 };
            t.Equal("v1", cache.Get(clip), "loaded");
            File.WriteAllText(clip, "v2");
            File.SetLastWriteTimeUtc(clip, DateTime.UtcNow.AddHours(1));
            cache.MarkAllStale();
            t.Equal("v1", cache.Get(clip), "kept as loaded");
            string missing = t.Path("Assets", "Missing.vanim");
            t.Equal(null, cache.Get(missing), "not packed");
            File.WriteAllText(missing, "x");
            t.Equal(null, cache.Get(missing), "a miss stays a miss");
            t.Equal(2, loads, "one load per path");
        }
    }
}
