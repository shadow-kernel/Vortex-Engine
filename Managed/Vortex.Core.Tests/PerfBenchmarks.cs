using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Editor.Core.Animation;
using Editor.Core.Data;
using Editor.ECS;
using Editor.ECS.Components.Scripting;
using Editor.Scripting;

namespace VortexTests
{
    /// <summary>#361 measurements (points 5 and 6), run on demand: <c>VORTEX_BENCH=1 dotnet run -c Release --project
    /// Managed/Vortex.Core.Tests -- PerfBenchmarks</c>. Skipped otherwise (they take seconds and measure, not check).</summary>
    public static class PerfBenchmarks
    {
        private static bool Enabled()
        {
            if (Environment.GetEnvironmentVariable("VORTEX_BENCH") == "1") return true;
            Console.WriteLine("        (skipped: set VORTEX_BENCH=1 to measure)");
            return false;
        }

        private const string Scripts = @"
using Vortex;
public class Idle : VortexBehaviour { }
";

        /// <summary>10,000 entities: 200 roots with 49 children each, names E0..E9999, every 100th tagged "Enemy".</summary>
        internal static Scene BigScene(out List<GameEntity> all)
        {
            var s = new Scene { Name = "Big" };
            all = new List<GameEntity>(10000);
            int n = 0;
            for (int r = 0; r < 200; r++)
            {
                var root = new GameEntity(s, "E" + n++);
                s.Entities.Add(root);
                all.Add(root);
                for (int c = 0; c < 49; c++)
                {
                    var child = new GameEntity(s, "E" + n++);
                    if (n % 100 == 0) child.Tag = "Enemy";
                    child.Parent = root; root.Children.Add(child);
                    all.Add(child);
                }
            }
            return s;
        }

        private static (double us, long bytes) Measure(int calls, Action a)
        {
            // warm-up long enough for the JIT's optimized tier (a method is re-jitted after 30 calls and a 100 ms pause in new
            // jitting): the steady state is measured, not the first-tier code of a path the run had not taken yet
            var warm = Stopwatch.StartNew();
            for (int n = 0; n < 3 || (warm.ElapsedMilliseconds < 400 && n < 100000); n++) a();
            GC.Collect(); GC.WaitForPendingFinalizers();
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < calls; i++) a();
            sw.Stop();
            long b1 = GC.GetAllocatedBytesForCurrentThread();
            return (sw.Elapsed.TotalMilliseconds * 1000.0 / calls, (b1 - b0) / calls);
        }

        [Test]
        public static void SceneFindOnTenThousandEntities(TestContext t)
        {
            if (!Enabled()) return;
            var file = t.Write("Scripts/Idle.cs", Scripts);
            var asm = RoslynScriptCompiler.Compile(new[] { file }, out _);
            var scene = BigScene(out var all);
            all[0].AddComponentDirect(new Script(all[0], "Assets/Scripts/Idle.cs"));
            var rt = ScriptRuntime.Instance;
            try
            {
                rt.Begin(scene, asm);
                var last = Measure(2000, () => Vortex.Scene.Find("E9999"));
                var mid = Measure(2000, () => Vortex.Scene.Find("E5000"));
                var miss = Measure(2000, () => Vortex.Scene.Find("Nobody"));
                var tag = Measure(2000, () => Vortex.Scene.FindByTag("Enemy"));
                var into = new List<long>();
                var tagList = Measure(2000, () => Vortex.Scene.FindByTag("Enemy", into));
                var renamed = Measure(200, () => { all[5000].Name = all[5000].Name == "E5000" ? "E5000b" : "E5000"; Vortex.Scene.Find("E9999"); });
                var retagged = Measure(200, () => { all[7001].Tag = all[7001].Tag == "Enemy" ? "Dead" : "Enemy"; Vortex.Scene.FindByTag("Enemy"); });
                var extra = new GameEntity(scene, "Extra");
                void Toggle() { if (scene.Entities.Contains(extra)) scene.Entities.Remove(extra); else scene.Entities.Add(extra); }
                var outside = Measure(200, () => { Toggle(); Vortex.Scene.Find("E9999"); });
                int builds0 = rt.IndexBuilds, rounds = 0;
                var settled = Measure(50, () => { rounds++; Toggle(); for (int i = 0; i <= ScriptRuntime.WalksPerRebuild; i++) Vortex.Scene.Find("E9999"); });
                int rebuilds = rt.IndexBuilds - builds0;
                Console.WriteLine("        Scene.Find last of 10,000:   " + F(last.us) + " us/call, " + last.bytes + " B/call");
                Console.WriteLine("        Scene.Find middle:            " + F(mid.us) + " us/call, " + mid.bytes + " B/call");
                Console.WriteLine("        Scene.Find no match:          " + F(miss.us) + " us/call, " + miss.bytes + " B/call");
                Console.WriteLine("        Scene.FindByTag (100 hits):   " + F(tag.us) + " us/call, " + tag.bytes + " B/call");
                Console.WriteLine("        Scene.FindByTag into a list:  " + F(tagList.us) + " us/call, " + tagList.bytes + " B/call (" + into.Count + " hits)");
                Console.WriteLine("        rename + Find:                " + F(renamed.us) + " us/call, " + renamed.bytes + " B/call");
                Console.WriteLine("        retag + FindByTag:            " + F(retagged.us) + " us/call, " + retagged.bytes + " B/call");
                Console.WriteLine("        root added by the editor + Find (the scene walked): " + F(outside.us) + " us/call, " + outside.bytes + " B/call");
                Console.WriteLine("        root added by the editor + " + (ScriptRuntime.WalksPerRebuild + 1) + " Finds (" + ScriptRuntime.WalksPerRebuild
                    + " walks, then the rebuild): " + F(settled.us) + " us, " + settled.bytes + " B (" + rebuilds + " rebuilds in " + rounds + " rounds)");
            }
            finally { rt.End(); }
        }

        /// <summary>A 60-node rig (a spine chain with branches) and a looping clip that rotates every node — the pose cost of a
        /// game character without a model file.</summary>
        internal static SkeletonDef Rig60()
        {
            var nodes = new SkeletonNode[60];
            var bones = new SkeletonBone[60];
            for (int i = 0; i < 60; i++)
            {
                int parent = i == 0 ? -1 : (i < 20 ? i - 1 : (i % 20) * 1);
                var tr = new System.Numerics.Vector3(0f, i == 0 ? 0f : 0.1f, 0f);
                nodes[i] = new SkeletonNode { Name = "B" + i, Parent = parent, BindTranslation = tr, BindRotation = System.Numerics.Quaternion.Identity, BindScale = System.Numerics.Vector3.One, LocalBind = Matrix4x4.CreateTranslation(tr) };
                bones[i] = new SkeletonBone { NodeIndex = i };
            }
            return new SkeletonDef { Nodes = nodes, Bones = bones };
        }

        internal static VortexAnimClip Clip60()
        {
            var clip = new VortexAnimClip { Name = "walk", DurationSec = 1f, Loop = true };
            for (int b = 0; b < 60; b++)
            {
                var track = new AnimTrack { Bone = "B" + b };
                for (int k = 0; k <= 30; k++)
                {
                    float a = (float)Math.Sin(k / 30f * Math.PI * 2 + b) * 0.3f;
                    var q = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, a);
                    track.Rot.Add(new AnimKeyQuat { T = k / 30f, X = q.X, Y = q.Y, Z = q.Z, W = q.W });
                }
                clip.Tracks.Add(track);
            }
            return clip;
        }

        /// <summary>100 animated characters in a 10,000-entity scene: 10 ahead of the camera (30 m), 90 behind it (30–100 m).</summary>
        internal static Scene CrowdScene(out List<GameEntity> characters)
        {
            var s = BigScene(out _);
            characters = new List<GameEntity>();
            var rnd = new Random(361);
            for (int i = 0; i < 100; i++)
            {
                var c = TimeTests.Rigged(s, "Character" + i);
                float dist = 30f + (float)rnd.NextDouble() * 70f;
                float x = ((float)rnd.NextDouble() - 0.5f) * 10f;
                c.Transform.LocalPosition = i < 10 ? new Editor.ECS.Vector3(x, 0f, 30f) : new Editor.ECS.Vector3(x, 0f, -dist);
                characters.Add(c);
            }
            return s;
        }

        [Test]
        public static void AnimatorsWithNinetyOffScreen(TestContext t)
        {
            if (!Enabled()) return;
            var scene = CrowdScene(out var characters);
            var svc = AnimationService.Instance;
            var rig = Rig60(); var clip = Clip60();
            try
            {
                svc.ResetStates();
                foreach (var c in characters) svc.PlayWithRig(c, rig, clip);
                var all = Measure(600, () => svc.Step(scene, 1f / 60f, 1f / 60f));
                Console.WriteLine("        AnimationService.Step, 100 characters in 10,000 entities, every pose: " + F(all.us / 1000.0) + " ms/frame, " + all.bytes + " B/frame");
                // the camera at the origin looking down +Z (60° high, 16:9): 10 characters ahead, 90 behind it
                var view = new AnimationView(System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitZ, 60f, 16f / 9f);
                var lod = Measure(600, () => svc.Step(scene, 1f / 60f, 1f / 60f, view));
                Console.WriteLine("        AnimationService.Step, the same with 90 off-screen (level of detail): " + F(lod.us / 1000.0) + " ms/frame, " + lod.bytes
                    + " B/frame — " + svc.PosesEvaluated + " poses evaluated, " + svc.PosesSkipped + " skipped");
                // one off-screen animator 100 m away (the #361 acceptance): its whole step
                var lone = new Scene { Name = "Lone" };
                var far = TimeTests.Rigged(lone, "Far");
                far.Transform.LocalPosition = new Editor.ECS.Vector3(0f, 0f, -100f);
                svc.ResetStates();
                svc.PlayWithRig(far, rig, clip);
                var one = Measure(5000, () => svc.Step(lone, 1f / 60f, 1f / 60f, view));
                var oneAll = Measure(5000, () => svc.Step(lone, 1f / 60f, 1f / 60f));
                Console.WriteLine("        one animator 100 m behind the camera: " + F(one.us) + " us/frame skipped (" + F(oneAll.us) + " us posed), " + one.bytes + " B/frame");
            }
            finally { svc.ResetStates(); }
        }

        private static string F(double v) => v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }
}
