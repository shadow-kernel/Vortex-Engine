using System;
using System.Collections.Generic;
using System.Linq;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;
using System.Reflection;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;
using Editor.ECS;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>#361 points 5 and 6: Scene.Find / FindByTag answer from an index kept up to date entry by entry (rename, retag,
    /// spawn, destroy) — with the walk's results and order; after other hierarchy changes the queries walk the scene until a
    /// rebuild pays off, so a hierarchy changing before every query never rebuilds per change; the animators come from a list
    /// kept with the hierarchy, and the level of detail skips or thins out the poses of off-screen and far animators while their
    /// clocks, events and root motion run on.</summary>
    public static class SceneQueryLodTests
    {
        private const string Scripts = @"
using Vortex;
public class Idle : VortexBehaviour { }
";

        private static Assembly Compile(TestContext t)
        {
            var file = t.Write("Scripts/Idle.cs", Scripts);
            var asm = RoslynScriptCompiler.Compile(new[] { file }, out var log);
            if (asm == null) throw new TestFailure("the test scripts do not compile: " + log);
            return asm;
        }

        private static GameEntity Add(Scene s, GameEntity parent, string name, string tag = null)
        {
            var e = new GameEntity(s, name);
            if (tag != null) e.Tag = tag;
            if (parent != null) { e.Parent = parent; parent.Children.Add(e); } else s.Entities.Add(e);
            return e;
        }

        /// <summary>Run <paramref name="query"/> until the index has been rebuilt since <paramref name="buildsBefore"/> builds —
        /// the walks of a stale index add up to what a build takes (a time, so the count varies). Returns the queries it took.</summary>
        private static int QueryUntilRebuilt(TestContext t, ScriptRuntime rt, int buildsBefore, Action query)
        {
            int n = 0;
            while (rt.IndexBuilds == buildsBefore && n < 1_000_000) { query(); n++; }
            t.True(rt.IndexBuilds > buildsBefore, "the stale index was rebuilt after " + n + " queries");
            return n;
        }

        // ------------------------------------------------------------------ point 6: the name / tag index

        [Test]
        public static void FindAndFindByTagKeepTheirResultsAndOrder(TestContext t)
        {
            var asm = Compile(t);
            var s = new Scene { Name = "Find" };
            var a = Add(s, null, "Door", "Prop");
            var a1 = Add(s, a, "Handle", "Prop");
            var b = Add(s, null, "Guard", "Enemy");
            var b1 = Add(s, b, "Door", "Prop");         // a second "Door", later in hierarchy order
            var c = Add(s, null, "Guard", "Enemy");
            c.SetActiveFlagOnly(false);                  // inactive entities are found, as before
            a.AddComponentDirect(new Script(a, "Assets/Scripts/Idle.cs"));
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(s, asm);
                long Id(GameEntity e) => rt.HandleForEntity(e);
                t.Equal(Id(a), Vortex.Scene.Find("Door"), "the first 'Door' in hierarchy order");
                t.Equal(Id(b), Vortex.Scene.Find("Guard"), "the first 'Guard'");
                t.Equal(0L, Vortex.Scene.Find("Nobody"), "no match: 0");
                t.Equal(0L, Vortex.Scene.Find(""), "an empty name: 0");
                t.Equal(string.Join(",", new[] { a, a1, b1 }.Select(Id)), string.Join(",", Vortex.Scene.FindByTag("Prop")), "FindByTag in hierarchy order");
                t.Equal(string.Join(",", new[] { b, c }.Select(Id)), string.Join(",", Vortex.Scene.FindByTag("Enemy")), "an inactive entity is found too");
                var list = new List<long> { 42 };
                t.Equal(3, Vortex.Scene.FindByTag("Prop", list), "the list overload counts");
                t.Equal(string.Join(",", new[] { a, a1, b1 }.Select(Id)), string.Join(",", list), "… and fills the list (cleared first)");
                int builds = rt.IndexBuilds;

                // rename / retag: that entry moves, no rebuild, the hierarchy order kept
                a.Name = "Gate";
                t.Equal(Id(b1), Vortex.Scene.Find("Door"), "renamed away: the next 'Door' is found");
                t.Equal(Id(a), Vortex.Scene.Find("Gate"), "… and it under its new name");
                a.Name = "Door";
                t.Equal(Id(a), Vortex.Scene.Find("Door"), "renamed back: first again (its place in hierarchy order)");
                b1.Tag = "Enemy";
                t.Equal(string.Join(",", new[] { b, b1, c }.Select(Id)), string.Join(",", Vortex.Scene.FindByTag("Enemy")), "retagged: in its hierarchy place among the others");
                t.Equal(string.Join(",", new[] { a, a1 }.Select(Id)), string.Join(",", Vortex.Scene.FindByTag("Prop")), "… and out of its old tag");
                t.Equal(builds, rt.IndexBuilds, "renames and retags patch the index, they do not rebuild it");

                // Scene.Destroy: the subtree's entries go, no rebuild
                Vortex.Scene.Destroy(Id(b));
                t.True(Vortex.Scene.Find("Handle") != 0, "an unrelated entity is still found");
                t.Equal(Id(c), Vortex.Scene.Find("Guard"), "the destroyed guard is gone, the other one found");
                t.Equal(string.Join(",", new[] { c }.Select(Id)), string.Join(",", Vortex.Scene.FindByTag("Enemy")), "its child's tag entry went too");
                t.Equal(builds, rt.IndexBuilds, "a destroy patches the index");

                // a hierarchy change the runtime did not make (the editor, a tool): the queries walk the scene — right at once —
                // until the walks have cost what a build did, then the index is rebuilt
                var late = Add(s, null, "Late", "Enemy");
                int walks = rt.IndexWalks;
                t.Equal(Id(late), Vortex.Scene.Find("Late"), "an entity the editor added is found at once");
                t.True(rt.IndexWalks == walks + 1 && rt.IndexBuilds == builds, "… by walking the scene, not by a rebuild");
                t.Equal(string.Join(",", new[] { c, late }.Select(Id)), string.Join(",", Vortex.Scene.FindByTag("Enemy")), "… by its tag too, in hierarchy order");
                int queries = QueryUntilRebuilt(t, rt, builds, () => t.Equal(Id(late), Vortex.Scene.Find("Late"), "found while walking"));
                t.Equal(builds + 1, rt.IndexBuilds, "once the walks cost what a build took, the index is rebuilt — once (after " + queries + " more queries)");
                walks = rt.IndexWalks;
                t.Equal(Id(late), Vortex.Scene.Find("Late"), "the rebuilt index finds it");
                t.Equal(walks, rt.IndexWalks, "… without a walk");
                late.Name = "Later";
                t.Equal(Id(late), Vortex.Scene.Find("Later"), "the rebuilt index follows renames again");
                t.Equal(builds + 1, rt.IndexBuilds, "… without another rebuild");
                late.AddComponentDirect(new Editor.ECS.Components.Lighting.Light(late));
                Vortex.Scene.Find("Later");
                t.Equal(builds + 1, rt.IndexBuilds, "a component change does not touch the name index");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void AHierarchyChangingBeforeEveryQueryIsWalkedNotRebuilt(TestContext t)
        {
            var asm = Compile(t);
            var s = new Scene { Name = "Churn" };
            var host = Add(s, null, "Host");
            host.AddComponentDirect(new Script(host, "Assets/Scripts/Idle.cs"));
            for (int i = 0; i < 50; i++) Add(s, null, "Box" + i, "Box");
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(s, asm);
                t.True(Vortex.Scene.Find("Box0") != 0, "indexed");
                int builds = rt.IndexBuilds, walks = rt.IndexWalks;
                for (int i = 0; i < 40; i++)
                {
                    var e = Add(s, null, "Fresh" + i, "Box");   // the editor (or a tool) adds an entity before every query
                    t.Equal(rt.HandleForEntity(e), Vortex.Scene.Find("Fresh" + i), "the new entity is found at once (" + i + ")");
                    t.Equal(51 + i, Vortex.Scene.FindByTag("Box").Length, "… and counted by its tag (" + i + ")");
                }
                int rebuilds = rt.IndexBuilds - builds;
                t.True(rt.IndexWalks - walks >= 40, "the queries on the changed hierarchy walked it (" + (rt.IndexWalks - walks) + " walks)");
                t.True(rebuilds <= 20, "a hierarchy that changes before every query is not rebuilt per change (" + rebuilds + " rebuilds for 40 changes)");
            }
            finally { rt.End(); }
        }

        /// <summary>#361 review: after one change the index does not follow, a game that spawns (Scene.Instantiate) before every
        /// query still gets its index back — the walks add up from the moment it went stale, the spawns do not reset that — and
        /// from then on its spawns patch it: no more walks, no more rebuilds.</summary>
        [Test]
        public static void SpawningEveryFrameAfterAnUntrackedChangeGetsTheIndexBack(TestContext t)
        {
            var asm = Compile(t);
            string prefab = t.Write("Bullet.ventity", DataSerializer.ToJson(new GameEntity("Bullet")));
            var s = new Scene { Name = "Bullets" };
            var host = Add(s, null, "Host");
            host.AddComponentDirect(new Script(host, "Assets/Scripts/Idle.cs"));
            for (int i = 0; i < 200; i++) Add(s, null, "E" + i);
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(s, asm);
                long target = Vortex.Scene.Find("E199");
                t.True(target != 0, "indexed");
                Add(s, null, "FromTheEditor");   // one change the runtime did not make
                int builds = rt.IndexBuilds;
                void Frame()
                {
                    t.True(Vortex.Scene.Instantiate(prefab, new Vortex.Vector3(0f, 0f, 0f)) != 0, "spawned");
                    for (int q = 0; q < 3; q++) t.Equal(target, Vortex.Scene.Find("E199"), "found");
                }
                int frames = 0;
                while (rt.IndexBuilds == builds && frames < 5000) { Frame(); frames++; }
                t.True(rt.IndexBuilds == builds + 1, "rebuilt once though a spawn came before every query (after " + frames + " frames)");
                int walks = rt.IndexWalks;
                for (int f = 0; f < 50; f++) Frame();
                t.Equal(walks, rt.IndexWalks, "from then on the spawns patch the index: no walks");
                t.Equal(builds + 1, rt.IndexBuilds, "… and no rebuilds");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void MoreRenamesThanRememberedStillFindTheRightEntities(TestContext t)
        {
            var asm = Compile(t);
            var s = new Scene { Name = "Renames" };
            var host = Add(s, null, "Host");
            host.AddComponentDirect(new Script(host, "Assets/Scripts/Idle.cs"));
            int n = ScriptRuntime.MaxPendingRenames + 44;
            var many = new List<GameEntity>();
            for (int i = 0; i < n; i++) many.Add(Add(s, null, "Old" + i));
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(s, asm);
                t.True(Vortex.Scene.Find("Old7") != 0, "indexed");
                int builds = rt.IndexBuilds, walks = rt.IndexWalks;
                for (int i = 0; i < n; i++) many[i].Name = "New" + i;   // more renames between two queries than the index remembers
                t.Equal(0L, Vortex.Scene.Find("Old0"), "the old name is gone");
                t.True(rt.IndexWalks == walks + 1 && rt.IndexBuilds == builds, "… told by a walk (the index lost track of the renames)");
                int k = 0;
                QueryUntilRebuilt(t, rt, builds, () =>
                {
                    int i = (k++ * 37) % n;
                    t.Equal(0L, Vortex.Scene.Find("Old" + i), "the old name is gone (" + i + ")");
                    t.Equal(rt.HandleForEntity(many[i]), Vortex.Scene.Find("New" + i), "the new one is found (" + i + ")");
                });
                t.Equal(builds + 1, rt.IndexBuilds, "walked first, then rebuilt once");
                t.Equal(rt.HandleForEntity(many[n - 1]), Vortex.Scene.Find("New" + (n - 1)), "the last rename too");
                t.Equal(0L, Vortex.Scene.Find("Old3"), "the rebuilt index has the new names only");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void NamesNobodyCarriesDoNotPileUp(TestContext t)
        {
            var asm = Compile(t);
            var s = new Scene { Name = "Keys" };
            var host = Add(s, null, "Host");
            host.AddComponentDirect(new Script(host, "Assets/Scripts/Idle.cs"));
            var bullet = Add(s, null, "Bullet", "Shot");
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(s, asm);
                Vortex.Scene.Find("Bullet");
                int keys = rt.IndexKeys;
                for (int i = 0; i < 500; i++) { bullet.Name = "Bullet" + i; bullet.Tag = "Shot" + i; Vortex.Scene.Find("Bullet" + i); }
                t.Equal(keys, rt.IndexKeys, "renamed 500 times: the old names and tags left the index");
                var temp = Add(s, null, "Temp", "Temporary");   // the editor adds one and takes it away again
                QueryUntilRebuilt(t, rt, rt.IndexBuilds, () => Vortex.Scene.Find("Temp"));
                t.Equal(keys + 2, rt.IndexKeys, "rebuilt with it: its name and tag");
                s.Entities.Remove(temp);
                QueryUntilRebuilt(t, rt, rt.IndexBuilds, () => t.Equal(0L, Vortex.Scene.Find("Temp"), "gone"));
                t.Equal(keys, rt.IndexKeys, "rebuilt without it: its name and tag left the index");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void ASpawnedPrefabJoinsTheIndexWithoutARebuild(TestContext t)
        {
            var asm = Compile(t);
            var template = new GameEntity("Crate");
            template.Tag = "Loot";
            var inner = new GameEntity("CrateLid"); inner.Parent = template; template.Children.Add(inner);
            string prefab = t.Write("Crate.ventity", DataSerializer.ToJson(template));
            var s = new Scene { Name = "Spawn" };
            var first = Add(s, null, "Crate", "Loot");
            first.AddComponentDirect(new Script(first, "Assets/Scripts/Idle.cs"));
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(s, asm);
                Vortex.Scene.Find("Crate");
                int builds = rt.IndexBuilds;
                long spawned = Vortex.Scene.Instantiate(prefab, new Vortex.Vector3(0f, 0f, 0f));
                t.True(spawned != 0, "spawned");
                t.Equal(rt.HandleForEntity(first), Vortex.Scene.Find("Crate"), "the authored crate stays first");
                var loot = Vortex.Scene.FindByTag("Loot");
                t.True(loot.Length == 2 && loot[1] == spawned, "the spawn comes last in hierarchy order");
                t.True(Vortex.Scene.Find("CrateLid") != 0, "its child is found");
                t.Equal(builds, rt.IndexBuilds, "no rebuild for a spawn");
                Vortex.Scene.Destroy(spawned);
                t.Equal(0L, Vortex.Scene.Find("CrateLid"), "destroyed again: gone");
                t.Equal(builds, rt.IndexBuilds, "no rebuild for its destroy either");
            }
            finally { rt.End(); }
        }

        [Test]
        public static void TheMainCameraLookupIsCachedAndFollowsChanges(TestContext t)
        {
            var s = new Scene { Name = "Cams" };
            var a = Add(s, null, "CamA");
            var camA = new Editor.ECS.Components.Rendering.Camera(a) { IsMainCamera = true };
            a.AddComponentDirect(camA);
            t.Equal(a, PlayCameraHelper.FindMainCameraEntity(s), "the main camera");
            var b = Add(s, null, "CamB");
            var camB = new Editor.ECS.Components.Rendering.Camera(b) { IsMainCamera = false };
            b.AddComponentDirect(camB);
            t.Equal(a, PlayCameraHelper.FindMainCameraEntity(s), "a second, non-main camera changes nothing");
            camA.IsMainCamera = false; camB.IsMainCamera = true;
            t.Equal(b, PlayCameraHelper.FindMainCameraEntity(s), "the main flag moved: found again");
            s.Entities.Remove(b);
            t.True(PlayCameraHelper.FindMainCameraEntity(s) == null, "the main camera left the scene: none");
        }

        // ------------------------------------------------------------------ point 5: the animators

        private static readonly AnimationView Ahead = new AnimationView(Vector3.Zero, Vector3.UnitZ, 60f, 16f / 9f);

        [Test]
        public static void ThePoseRateFollowsDistanceAndView(TestContext t)
        {
            var lod = new AnimationLod();
            float R(Vector3 p, float r = 2f) => AnimationService.PoseRate(lod, Ahead, p, r);
            t.Equal(-1f, R(new Vector3(0, 0, 10)), "close and in view: every frame");
            t.Equal(-1f, R(new Vector3(0, 0, -10)), "close BEHIND the camera: still every frame (the near radius)");
            t.Equal(-1f, R(new Vector3(0, 0, 40)), "in view at 40 m: every frame");
            t.Equal(0f, R(new Vector3(0, 0, -40)), "behind the camera at 40 m: no pose");
            t.Equal(0f, R(new Vector3(60, 0, 1)), "far to the side: no pose");
            t.Equal(-1f, R(new Vector3(30, 0, 30)), "45° to the side, inside the 16:9 frustum's diagonal: every frame");
            t.Equal(30f, R(new Vector3(0, 0, 80)), "in view beyond 50 m: 30 per second");
            t.Equal(15f, R(new Vector3(0, 0, 150)), "beyond twice that: 15 per second");
            t.Equal(-1f, R(new Vector3(0, 0, -150), 200f), "a huge reach that touches the view: every frame");
            t.Equal(-1f, AnimationService.PoseRate(lod, AnimationView.Unknown, new Vector3(0, 0, -40), 2f), "no view known: every frame");
            lod.Enabled = false;
            t.Equal(-1f, R(new Vector3(0, 0, -40)), "the level of detail off: every frame");
        }

        private static VortexAnimClip StepClip(bool withEvent)
        {
            var clip = new VortexAnimClip { Name = "loop", DurationSec = 4f, Loop = true };   // no wrap within a test
            var track = new AnimTrack { Bone = "Hips" };
            for (int k = 0; k <= 4; k++)
            {
                var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, k * 0.4f);
                track.Rot.Add(new AnimKeyQuat { T = k * 1f, X = q.X, Y = q.Y, Z = q.Z, W = q.W });
            }
            clip.Tracks.Add(track);
            if (withEvent) clip.Events.Add(new AnimEvent { T = 0.5f, Name = "step" });
            return clip;
        }

        [Test]
        public static void OffScreenAnimatorsSkipThePoseButKeepTheClock(TestContext t)
        {
            var s = new Scene { Name = "Lod" };
            var seen = TimeTests.Rigged(s, "Seen"); seen.Transform.LocalPosition = new Editor.ECS.Vector3(0, 0, 30);
            var behind = TimeTests.Rigged(s, "Behind"); behind.Transform.LocalPosition = new Editor.ECS.Vector3(0, 0, -40);
            var always = TimeTests.Rigged(s, "Always"); always.Transform.LocalPosition = new Editor.ECS.Vector3(0, 0, -40);
            always.GetComponent<Editor.ECS.Components.Animation.Animator>().AlwaysAnimate = true;
            var far = TimeTests.Rigged(s, "Far"); far.Transform.LocalPosition = new Editor.ECS.Vector3(0, 0, 80);
            var svc = AnimationService.Instance;
            var events = new List<string>();
            void OnEvent(GameEntity e, string name) => events.Add(e.Name + ":" + name);
            svc.AnimationEvent += OnEvent;
            try
            {
                svc.ResetStates();
                var clip = StepClip(withEvent: true);
                foreach (var e in new[] { seen, behind, always, far }) svc.PlayWithRig(e, TimeTests.TestRig(), clip);
                svc.Step(s, 1f / 60f, 1f / 60f, Ahead);   // the first step poses every animator
                t.Equal(4, svc.PosesEvaluated, "the first step poses all of them");
                var frozen = (float[])svc.CurrentNodeWorlds(behind)?.Select(m => m.M22).ToArray().Clone();

                for (int i = 0; i < 59; i++) svc.Step(s, 1f / 60f, 1f / 60f, Ahead);   // one second in all
                t.True(Math.Abs(svc.GetTime(behind) - svc.GetTime(seen)) < 1e-4f, "the skipped animator's clock runs like the seen one's (" + svc.GetTime(behind) + ")");
                t.True(events.Contains("Behind:step") && events.Contains("Seen:step"), "its animation events fire: " + string.Join(", ", events));
                var still = svc.CurrentNodeWorlds(behind)?.Select(m => m.M22).ToArray();
                t.True(frozen != null && still != null && frozen.SequenceEqual(still), "… but its pose waited (off-screen)");

                // one more frame: who poses
                svc.Step(s, 1f / 60f, 1f / 60f, Ahead);
                t.True(svc.PosesSkipped >= 1, "the one behind the camera is skipped (" + svc.PosesSkipped + ")");
                int evaluated = svc.PosesEvaluated;
                t.True(evaluated >= 2 && evaluated <= 3, "seen + always animate pose; the far one every other frame (" + evaluated + ")");

                // a bone query takes this frame's pose
                svc.EnsurePose(behind);
                var now = svc.CurrentNodeWorlds(behind)?.Select(m => m.M22).ToArray();
                t.False(now.SequenceEqual(still), "EnsurePose (a script's bone query) evaluates the skipped pose now");

                // the far animator poses about 30 times a second at 60 fps
                int farPoses = 0;
                for (int i = 0; i < 60; i++)
                {
                    var before = svc.CurrentNodeWorlds(far);
                    svc.Step(s, 1f / 60f, 1f / 60f, Ahead);
                    if (!ReferenceEquals(before, svc.CurrentNodeWorlds(far))) farPoses++;
                }
                t.True(farPoses >= 28 && farPoses <= 31, "beyond 50 m: ~30 poses a second (" + farPoses + ")");

                // switched off: everything poses every frame
                Vortex.Animation.SetLod(false);
                svc.Step(s, 1f / 60f, 1f / 60f, Ahead);
                t.Equal(4, svc.PosesEvaluated, "Animation.SetLod(false): every animator poses");
                t.Equal(0, svc.PosesSkipped, "… none skipped");
            }
            finally { svc.AnimationEvent -= OnEvent; svc.ResetStates(); }
        }

        [Test]
        public static void TheAnimatorListFollowsTheHierarchy(TestContext t)
        {
            var s = new Scene { Name = "List" };
            var a = TimeTests.Rigged(s, "A");
            var svc = AnimationService.Instance;
            try
            {
                svc.ResetStates();
                var clip = StepClip(withEvent: false);
                svc.PlayWithRig(a, TimeTests.TestRig(), clip);
                svc.Step(s, 0.1f, 0.1f);
                t.True(svc.GetTime(a) > 0.09f, "stepped");
                var b = TimeTests.Rigged(s, "B");   // an animator added while running
                svc.PlayWithRig(b, TimeTests.TestRig(), clip);
                svc.Step(s, 0.1f, 0.1f);
                t.True(svc.GetTime(b) > 0.09f, "a new animator is stepped from the next frame");
                a.SetActiveFlagOnly(false);
                float ta = svc.GetTime(a);
                svc.Step(s, 0.1f, 0.1f);
                t.Equal(ta, svc.GetTime(a), "an inactive one is not stepped");
                var comp = b.GetComponent<Editor.ECS.Components.Animation.Animator>();
                b.Components.Remove(comp);   // the component taken off
                float tb = svc.GetTime(b);
                svc.Step(s, 0.1f, 0.1f);
                t.Equal(tb, svc.GetTime(b), "an entity whose Animator went is not stepped any more");
            }
            finally { svc.ResetStates(); }
        }
    }
}
