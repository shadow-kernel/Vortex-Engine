using System;
using System.Collections.Generic;
using Editor.Core.Data;
using Editor.Core.Services.AI;
using Editor.Core.Services.Physics;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.AI;
using Editor.ECS.Components.Physics;
using SysVec = System.Numerics.Vector3;
using SysQuat = System.Numerics.Quaternion;

namespace VortexTests
{
    /// <summary>#435: dynamic navmesh obstacles. A 30 m floor with a 12 m wall across the middle (x = -0.5..0.5, z = -6..6):
    /// from (-5, 3) to (5, 3) the way leads around its north end — until a door in that gap shuts. The class name sorts it
    /// after the headless tests: it loads the native engine library (Recast). Skipped without one.</summary>
    public static class ZNavObstacleTests
    {
        private const float Dt = 1f / 60f;
        private static readonly Vector3 West = new Vector3(-5f, 0, 3f), East = new Vector3(5f, 0, 3f);

        private static bool Native()
        {
            try { Editor.Core.Native.NativeLoader.Register(); } catch { }
            if (!NavigationNative.Available) NavigationNative.ResetProbe();
            if (NavigationService.Available) return true;
            Console.WriteLine("    (skipped: no navigation — " + NavigationService.UnavailableReason + ")");
            return false;
        }

        /// <summary>The level, baked and loaded, the runtime begun. <paramref name="extra"/> adds entities before the bake.</summary>
        private static Scene Level(Action<Scene> extra, out NavBakeResult bake)
        {
            var s = new Scene { Name = "Obstacles", IsActive = true };
            PhysicsRaycastTests.Add(s, null, "Floor", new Vector3(0, -0.5f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(30f, 1f, 30f) }));
            PhysicsRaycastTests.Add(s, null, "Wall", new Vector3(0, 1f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(1f, 2f, 12f) }));
            extra?.Invoke(s);
            PhysicsRaycastTests.AsLoaded(s);
            CollisionService.Build(s);
            bake = NavigationService.BakeScene(s, NavigationService.DefaultSettings(), save: false, load: true);
            if (!bake.Success) throw new TestFailure("the navmesh bakes: " + bake.Message);
            NavigationService.Begin(s);
            return s;
        }

        /// <summary>A door in the north gap (z 6.2 .. 14.8): a box collider and a carving Nav Mesh Obstacle of its size.</summary>
        private static GameEntity Door(Scene s, bool enabled = true)
            => PhysicsRaycastTests.Add(s, null, "Door", new Vector3(0, 1f, 10.5f), e =>
            {
                e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(1f, 2f, 8.6f) });
                e.AddComponentDirect(new NavMeshObstacle(e) { Size = new Vector3(1f, 2f, 8.6f), IsEnabled = enabled });
            });

        /// <summary>The highest / lowest z of the path from West to East (0 without a complete path).</summary>
        private static float PathZ(bool max)
        {
            var corners = new List<Vector3>();
            if (NavigationService.CalculatePath(West, East, corners) != NavPathStatus.Complete) return 0f;
            float z = max ? float.MinValue : float.MaxValue;
            foreach (var c in corners) z = max ? Math.Max(z, c.Z) : Math.Min(z, c.Z);
            return z;
        }

        private static int Corners(Vector3 a, Vector3 b)
        {
            var corners = new List<Vector3>();
            return NavigationService.CalculatePath(a, b, corners) == NavPathStatus.Complete ? corners.Count : -1;
        }

        private static void Ticks(int n) { for (int i = 0; i < n; i++) NavigationService.Tick(Dt); }

        private static SysVec Feet(GameEntity e) { var p = e.Transform.LocalPosition; return new SysVec(p.X, p.Y, p.Z); }

        private static void End() { NavigationService.End(); NavigationService.Unload(); CollisionService.Clear(); }

        /// <summary>The door's own entity is left out of the bake (the navmesh runs through the doorway); in play its obstacle
        /// carves the gap shut once its tiles are rebuilt, and switching it off and on frees and blocks the gap at once, without
        /// a rebuild. The scene geometry was handed over once; the times are printed.</summary>
        [Test]
        public static void ADoorBlocksTheGapAndOpensAndShutsAtOnce(TestContext t)
        {
            if (!Native()) return;
            GameEntity door = null;
            Level(s => door = Door(s), out var bake);
            try
            {
                t.Equal(2, bake.Stats.InputBoxes, "the bake took the floor and the wall, not the door with its obstacle");
                Ticks(1);
                t.True(NavigationService.FlushRebuilds(), "the door's tiles are rebuilt");
                var st = NavigationService.GetRebuildStats();
                Console.WriteLine("        door carved: " + st.TilesSwapped + " tiles, slowest build " + st.BuildMsMax.ToString("0.00") + " ms (worker), slowest swap "
                    + st.SwapMsMax.ToString("0.00") + " ms (game thread); scene geometry gathered in " + NavigationService.SourceGatherMs.ToString("0.0")
                    + " ms, " + st.SourceKBytes + " KB kept");
                t.True(st.SourceBoxes == 2 && st.TilesSwapped > 0 && st.TilesFailed == 0, "the geometry was handed over and the tiles swapped in");
                t.True(NavigationService.IsObstacleEnabled(door, 0) && NavigationService.IsObstacleReady(door, 0), "the door is on and carved");
                t.True(PathZ(false) < -6f && PathZ(true) < 6f, "with the door shut the way leads around the south end (" + PathZ(false) + ")");

                int built = st.TilesBuilt;
                NavigationService.SetObstacleEnabled(door, 0, false);
                t.True(PathZ(true) > 6f, "opened (switched off), the north gap is free at once (" + PathZ(true) + ")");
                NavigationService.SetObstacleEnabled(door, 0, true);
                t.True(PathZ(false) < -6f, "shut again, blocked at once");
                Ticks(3);
                t.Equal(built, NavigationService.GetRebuildStats().TilesBuilt, "opening and shutting rebuilt no tile");
                t.Equal(0, NavigationService.PendingRebuilds, "nothing is pending");

                door.GetComponent<NavMeshObstacle>().IsEnabled = false;   // the component itself, as the inspector would
                Ticks(1);
                t.True(PathZ(true) > 6f, "the component switched off frees the gap too");
                door.GetComponent<NavMeshObstacle>().IsEnabled = true;
                Ticks(1);
                t.True(PathZ(false) < -6f, "… and on again blocks it");
            }
            finally { End(); }
        }

        /// <summary>An agent on its way through the open doorway turns around when the door shuts in front of it and arrives
        /// by the south end, never stepping into the doorway.</summary>
        [Test]
        public static void AnAgentRePathsWhenTheDoorShuts(TestContext t)
        {
            if (!Native()) return;
            GameEntity door = null, guard = null;
            Level(s =>
            {
                door = Door(s);
                guard = PhysicsRaycastTests.Add(s, null, "Guard", West, e => e.AddComponentDirect(new NavAgent(e) { ResolveCollisions = false, Speed = 3.5f, Radius = 0.4f }));
            }, out _);
            try
            {
                NavigationService.SetObstacleEnabled(door, 0, false);   // open at the start: carved anyway (switched off), so shutting it is instant
                Ticks(1);
                t.True(NavigationService.FlushRebuilds(), "the open door's tiles are rebuilt");
                t.True(NavigationService.IsObstacleReady(door, 0) && !NavigationService.IsObstacleEnabled(door, 0), "the open door is carved, switched off");
                t.True(NavigationService.SetDestination(guard, East), "the guard gets a destination behind the wall");
                Ticks(40);
                var at = Feet(guard);
                t.True(at.Z > 3.3f, "it heads for the north gap (" + at + ")");
                NavigationService.SetObstacleEnabled(door, 0, true);
                float maxZ = float.MinValue;
                int arrived = -1;
                for (int i = 0; i < 60 * 15 && arrived < 0; i++)
                {
                    NavigationService.Tick(Dt);
                    maxZ = Math.Max(maxZ, Feet(guard).Z);
                    if (NavigationService.HasArrived(guard)) arrived = i;
                }
                Console.WriteLine("        door shut with the guard at " + at + ": it got no further north than z " + maxZ.ToString("0.00") + ", arrived after "
                    + (arrived / 60f).ToString("0.00") + " s at " + Feet(guard));
                t.True(arrived > 0 && (Feet(guard) - new SysVec(5f, 0, 3f)).Length() < 0.6f, "it arrives");
                t.True(maxZ < 6.2f, "it turned around before the doorway (" + maxZ + ")");
            }
            finally { End(); }
        }

        /// <summary>Carve Only Stationary: a crate pushed across the north area is only steered around while it moves (no
        /// rebuilds every frame) and carves once it stood still for Time To Stationary — where it stopped, not where it was.</summary>
        [Test]
        public static void AMovingObstacleCarvesWhereItStops(TestContext t)
        {
            if (!Native()) return;
            GameEntity crate = null;
            Level(s => crate = PhysicsRaycastTests.Add(s, null, "Crate", new Vector3(-8f, 1f, 10f), e =>
                e.AddComponentDirect(new NavMeshObstacle(e) { Size = new Vector3(2f, 2f, 2f), TimeToStationary = 0.25f })), out _);
            try
            {
                Ticks(1);
                NavigationService.FlushRebuilds();
                var probe = new Vector3(-8f, 0, 10f);
                t.False(NavigationService.SamplePosition(probe, 0.1f, out var near) && Math.Abs(near.X - probe.X) < 0.05f && Math.Abs(near.Z - probe.Z) < 0.05f,
                    "carved where it stands");
                int built0 = NavigationService.GetRebuildStats().TilesBuilt;
                for (int i = 0; i < 60; i++)   // pushed 6 m east over a second
                {
                    var p = crate.Transform.LocalPosition;
                    crate.Transform.LocalPosition = new Vector3(p.X + 0.1f, p.Y, p.Z);
                    NavigationService.Tick(Dt);
                }
                NavigationService.FlushRebuilds();
                int builtMoving = NavigationService.GetRebuildStats().TilesBuilt - built0;
                Ticks(30);   // standing still for half a second
                t.True(NavigationService.FlushRebuilds(), "its tiles are rebuilt once it stands");
                var st = NavigationService.GetRebuildStats();
                Console.WriteLine("        crate pushed for a second: " + builtMoving + " tile builds while it moved (its old carve removed), "
                    + (st.TilesBuilt - built0) + " with the carve where it stopped");
                t.True(builtMoving <= 8, "moving, it did not rebuild tiles every frame (" + builtMoving + ")");
                bool OnMesh(Vector3 q) => NavigationService.SamplePosition(q, 0.1f, out var h) && Math.Abs(h.X - q.X) < 0.05f && Math.Abs(h.Z - q.Z) < 0.05f;
                t.False(OnMesh(new Vector3(-2f, 0, 10f)), "where it stopped is carved");
                t.True(OnMesh(probe), "where it stood is free again");
            }
            finally { End(); }
        }

        /// <summary>Script obstacles: a carving box added in the north gap sends the path south once its tiles are in; moved
        /// away it frees the gap; removed, nothing is left. An avoiding one leaves the navmesh alone.</summary>
        [Test]
        public static void ScriptObstaclesCarveMoveAndGo(TestContext t)
        {
            if (!Native()) return;
            Level(null, out _);
            try
            {
                t.True(PathZ(true) > 6f, "without obstacles the way leads north");
                long box = NavigationService.AddObstacle(new SysVec(0f, 1f, 10.5f), new SysVec(0.5f, 1f, 4.3f), SysQuat.Identity, false, true);
                t.True(box >= NavigationService.ScriptObstacleBase, "a script obstacle id (" + box + ")");
                Ticks(1);
                t.True(NavigationService.FlushRebuilds() && NavigationService.IsObstacleReady(null, box), "it is carved");
                t.True(PathZ(false) < -6f, "the path goes south");
                t.True(NavigationService.MoveObstacle(box, new SysVec(8f, 1f, -10f), SysQuat.Identity), "moved away");
                Ticks(1);
                NavigationService.FlushRebuilds();
                t.True(PathZ(true) > 6f, "the gap is free again");
                NavigationService.RemoveObstacle(box);
                t.False(NavigationService.IsObstacleReady(null, box), "removed, it is gone");
                Ticks(1);
                NavigationService.FlushRebuilds();

                long car = NavigationService.AddObstacle(new SysVec(0f, 0.75f, 10.5f), new SysVec(0.5f, 0.75f, 4.3f), SysQuat.Identity, false, false);
                Ticks(2);
                t.True(PathZ(true) > 6f && NavigationService.PendingRebuilds == 0, "an avoiding obstacle leaves the navmesh as it is");
                t.True(NavigationService.IsObstacleReady(null, car), "… and is ready at once");
            }
            finally { End(); }
        }

        /// <summary>Destruction: the wall's entity switched off and Rebake over it — the path goes straight through.</summary>
        [Test]
        public static void RebakeAfterTheWallIsGone(TestContext t)
        {
            if (!Native()) return;
            var s = Level(null, out _);
            try
            {
                t.True(Corners(new Vector3(-5f, 0, 0), new Vector3(5f, 0, 0)) > 2, "the path bends around the wall");
                GameEntity wall = null;
                foreach (var e in s.Entities) if (e.Name == "Wall") wall = e;
                wall.IsActive = false;
                t.True(NavigationService.Rebake(new SysVec(0f, 1f, 0f), new SysVec(0.5f, 1f, 6f)), "re-baked over the wall");
                Ticks(1);
                t.True(NavigationService.FlushRebuilds(), "its tiles are rebuilt");
                t.Equal(2, Corners(new Vector3(-5f, 0, 0), new Vector3(5f, 0, 0)), "the path goes straight through where the wall was");
            }
            finally { End(); }
        }
    
        /// <summary>#435 review: a script obstacle moved a little every frame beside the agent's way (on the far side of a
        /// wall, inside the box from the agent to its target) is only steered around while it moves — no tile rebuilds every
        /// frame — and the agent is never re-planned into a standstill: it arrives as soon as without the obstacle.</summary>
        [Test]
        public static void AScriptObstacleMovedEveryFrameDoesNotStopAnAgent(TestContext t)
        {
            if (!Native()) return;
            float Run(bool withObstacle)
            {
                GameEntity runner = null;
                Level(s => runner = PhysicsRaycastTests.Add(s, null, "Runner", West, e => e.AddComponentDirect(new NavAgent(e) { ResolveCollisions = false, Speed = 5f, Radius = 0.4f })), out _);
                try
                {
                    Ticks(1);
                    NavigationService.SetDestination(runner, East);
                    long cart = withObstacle ? NavigationService.AddObstacle(new SysVec(2.5f, 1f, -0.5f), new SysVec(0.4f, 1f, 0.4f), SysQuat.Identity, false, true) : 0;
                    int built0 = NavigationService.GetRebuildStats().TilesBuilt;
                    for (int i = 0; i < 60 * 30; i++)
                    {
                        if (cart != 0) NavigationService.MoveObstacle(cart, new SysVec(2.5f + (float)Math.Sin(i * 0.02), 1f, -0.5f), SysQuat.Identity);   // a cart pushed to and fro
                        NavigationService.Tick(Dt);
                        if (NavigationService.HasArrived(runner))
                        {
                            if (withObstacle)
                                Console.WriteLine("        a script obstacle moved every frame beside the way: arrived after " + (i * Dt).ToString("0.00") + " s, "
                                    + (NavigationService.GetRebuildStats().TilesBuilt - built0) + " tile builds");
                            return i * Dt;
                        }
                    }
                    return float.PositiveInfinity;
                }
                finally { End(); }
            }
            float alone = Run(false), beside = Run(true);
            t.True(!float.IsInfinity(beside) && beside < alone + 1.5f, "it arrives about as soon as without one, the cart steered around (" + beside + " s vs " + alone + " s)");
        }

        /// <summary>#435 review: Carve Only Stationary measures from where the obstacle came to rest — a physics prop settling
        /// (a jitter under Move Threshold every frame) carves; moving more than that, it is only steered around.</summary>
        [Test]
        public static void AJitteringObstacleStillCarves(TestContext t)
        {
            if (!Native()) return;
            GameEntity crate = null;
            Level(s => crate = PhysicsRaycastTests.Add(s, null, "Crate", new Vector3(-8f, 1f, 10f), e =>
                e.AddComponentDirect(new NavMeshObstacle(e) { Size = new Vector3(2f, 2f, 2f), TimeToStationary = 0.25f, MoveThreshold = 0.1f })), out _);
            try
            {
                var rng = new Random(435);
                bool OnMesh(Vector3 q) => NavigationService.SamplePosition(q, 0.1f, out var h) && Math.Abs(h.X - q.X) < 0.05f && Math.Abs(h.Z - q.Z) < 0.05f;
                for (int i = 0; i < 60; i++)
                {
                    // 1 cm of jitter every frame (a prop settling on the physics world)
                    crate.Transform.LocalPosition = new Vector3(-8f + 0.01f * (float)(rng.NextDouble() - 0.5), 1f, 10f + 0.01f * (float)(rng.NextDouble() - 0.5));
                    NavigationService.Tick(Dt);
                }
                NavigationService.FlushRebuilds();
                t.False(OnMesh(new Vector3(-8f, 0, 10f)), "jittering under its Move Threshold it is carved");
            }
            finally { End(); }
        }

        /// <summary>#435 review: only an enabled carving obstacle leaves its entity out of the bake; a disabled one, or one that
        /// only steers agents around it, is level geometry like any other. The rebuild budget is back to 2 ms after play.</summary>
        [Test]
        public static void OnlyCarvingObstaclesLeaveTheBake(TestContext t)
        {
            if (!Native()) return;
            foreach (var (carve, enabled, boxes) in new[] { (true, true, 2), (true, false, 3), (false, true, 3) })
            {
                Level(s => PhysicsRaycastTests.Add(s, null, "Prop", new Vector3(8f, 1f, -8f), e =>
                {
                    e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(1f, 2f, 1f) });
                    e.AddComponentDirect(new NavMeshObstacle(e) { Carve = carve, IsEnabled = enabled });
                }), out var bake);
                try { t.Equal(boxes, bake.Stats.InputBoxes, "carve " + carve + ", enabled " + enabled + ": " + boxes + " boxes baked"); }
                finally { End(); }
            }
            Level(null, out _);
            NavigationService.RebuildBudgetMs = 7f;
            End();
            t.Equal(2f, NavigationService.RebuildBudgetMs, "RebuildBudgetMs is back to its default after play");
        }
    }
}
