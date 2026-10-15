using System;
using Editor.Core.Data;
using Editor.Core.Services.AI;
using Editor.Core.Services.Physics;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.AI;
using Editor.ECS.Components.Physics;
using SysVec = System.Numerics.Vector3;

namespace VortexTests
{
    /// <summary>#434: a Nav Agent with Update Position off plans, steers and avoids, but never moves its entity — the game
    /// does (here: a "script" step that moves it by Navigation.DesiredVelocity), and the agent follows it every frame. The
    /// class name sorts it after the headless tests: it loads the native engine library (Recast). Skipped without one.</summary>
    public static class ZNavAgentFollowTests
    {
        private const float Dt = 1f / 60f;

        private static bool Native()
        {
            try { Editor.Core.Native.NativeLoader.Register(); } catch { }
            if (!NavigationNative.Available) NavigationNative.ResetProbe();   // probed before the library was registered
            if (NavigationService.Available) return true;
            Console.WriteLine("    (skipped: no navigation — " + NavigationService.UnavailableReason + ")");
            return false;
        }

        /// <summary>A 30 m floor with a 12 m wall across the middle (x = -0.5..0.5, z = -6..6), baked and loaded, and the
        /// runtime begun for <paramref name="agents"/> Nav Agents placed by the callback.</summary>
        private static Scene Level(Func<Scene, GameEntity[]> agents, out GameEntity[] made)
        {
            var s = new Scene { Name = "Follow", IsActive = true };
            PhysicsRaycastTests.Add(s, null, "Floor", new Vector3(0, -0.5f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(30f, 1f, 30f) }));
            PhysicsRaycastTests.Add(s, null, "Wall", new Vector3(0, 1f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(1f, 2f, 12f) }));
            made = agents(s);
            PhysicsRaycastTests.AsLoaded(s);
            CollisionService.Build(s);
            var bake = NavigationService.BakeScene(s, NavigationService.DefaultSettings(), save: false, load: true);
            if (!bake.Success) throw new TestFailure("the navmesh bakes: " + bake.Message);
            NavigationService.Begin(s);
            return s;
        }

        private static GameEntity Agent(Scene s, string name, Vector3 at, bool updatePosition, bool updateRotation = true)
            => PhysicsRaycastTests.Add(s, null, name, at, e => e.AddComponentDirect(new NavAgent(e)
            {
                UpdatePosition = updatePosition, UpdateRotation = updateRotation, ResolveCollisions = false, Speed = 3.5f, Radius = 0.4f
            }));

        private static SysVec Feet(GameEntity e) { var p = e.Transform.LocalPosition; return new SysVec(p.X, p.Y, p.Z); }
        private static void Put(GameEntity e, SysVec p) => e.Transform.LocalPosition = new Vector3(p.X, p.Y, p.Z);

        /// <summary>One frame of the game's own movement: the entity steps by the agent's desired velocity (on the floor).</summary>
        private static void ScriptStep(GameEntity e)
        {
            var v = NavigationService.GetDesiredVelocity(e);
            var p = Feet(e);
            Put(e, new SysVec(p.X + v.X * Dt, 0f, p.Z + v.Z * Dt));
        }

        private static void End() { NavigationService.End(); NavigationService.Unload(); CollisionService.Clear(); }

        /// <summary>Off, the service never moves the entity — not a millimetre in a second of ticks — while the agent plans
        /// the path around the wall and its desired velocity leads along it; moved by that velocity (the game's own step) the
        /// entity walks around the wall to the destination, the agent following every frame without re-planning, and arrives.</summary>
        [Test]
        public static void UpdatePositionOffNeverMovesTheEntity(TestContext t)
        {
            if (!Native()) return;
            Level(s => new[] { Agent(s, "Guard", new Vector3(-5f, 0, 0), false) }, out var a);
            var guard = a[0];
            try
            {
                t.True(NavigationService.SetDestination(guard, new Vector3(5f, 0, 0)), "the guard gets a destination behind the wall");
                for (int i = 0; i < 60; i++) NavigationService.Tick(Dt);
                var p = Feet(guard);
                t.True(p.X == -5f && p.Y == 0f && p.Z == 0f, "a second of ticks did not move the entity (" + p + ")");
                var dv = NavigationService.GetDesiredVelocity(guard);
                float speed = (float)Math.Sqrt(dv.X * dv.X + dv.Z * dv.Z);
                t.True(speed > 3f && speed < 3.6f && Math.Abs(dv.Z) > 0.5f, "its desired velocity leads along the path around the wall at full speed (" + dv + ")");
                t.Equal(NavPathStatus.Complete, NavigationService.GetPathStatus(guard), "the path is complete");
                var next = NavigationService.GetNextPosition(guard);
                t.True(Math.Abs(next.X + 5f) < 0.2f && Math.Abs(next.Z) < 0.2f, "the agent's next position stays at the entity (" + next + ")");

                float clearance = float.MaxValue;
                int arrived = -1;
                for (int i = 0; i < 60 * 10 && arrived < 0; i++)
                {
                    ScriptStep(guard);
                    var before = Feet(guard);
                    NavigationService.Tick(Dt);
                    t.True(Feet(guard) == before, "the tick never writes the entity's position (frame " + i + ")");
                    var f = Feet(guard);
                    clearance = Math.Min(clearance, Math.Max(Math.Abs(f.X) - 0.5f, Math.Abs(f.Z) - 6f));
                    if (NavigationService.HasArrived(guard)) arrived = i;
                }
                Console.WriteLine("        moved by its desired velocity: arrived after " + (arrived / 60f).ToString("0.00") + " s at " + Feet(guard) + ", wall clearance " + clearance.ToString("0.00"));
                t.True(arrived > 0 && (Feet(guard) - new SysVec(5f, 0, 0)).Length() < 0.5f, "the game's own steps bring it to the destination, and it arrives");
                t.True(clearance > 0.1f, "… around the wall, never into it");
            }
            finally { End(); }
        }

        /// <summary>The agent does not fight the entity: small moves (root motion, a push — under 5 cm a frame used to be
        /// overwritten, more a teleport) keep the path; an entity teleported through the wall takes its agent along, which
        /// keeps the destination and re-plans; Update Rotation still turns a follower toward its velocity, its position
        /// untouched; Warp moves it.</summary>
        [Test]
        public static void TheAgentFollowsWhereverTheEntityIsMoved(TestContext t)
        {
            if (!Native()) return;
            Level(s => new[] { Agent(s, "Guard", new Vector3(-5f, 0, 3f), false) }, out var a);
            var guard = a[0];
            try
            {
                NavigationService.SetDestination(guard, new Vector3(5f, 0, 3f));
                for (int i = 0; i < 10; i++) NavigationService.Tick(Dt);
                float remaining = NavigationService.RemainingDistance(guard);
                // a push sideways, 4 cm a frame for half a second (it used to be overwritten), then a 30 cm nudge
                for (int i = 0; i < 30; i++)
                {
                    var p = Feet(guard);
                    Put(guard, new SysVec(p.X, 0f, p.Z - 0.04f));
                    NavigationService.Tick(Dt);
                }
                var pushed = Feet(guard);
                t.True(Math.Abs(pushed.Z - (3f - 1.2f)) < 1e-4f && Math.Abs(pushed.X + 5f) < 1e-4f, "the pushed entity stays where it was pushed (" + pushed + ")");
                var next = NavigationService.GetNextPosition(guard);
                t.True(Math.Abs(next.Z - pushed.Z) < 0.15f, "… and its agent went with it (" + next + ")");
                t.Equal(NavPathStatus.Complete, NavigationService.GetPathStatus(guard), "… on its path");
                t.True(NavigationService.RemainingDistance(guard) > remaining - 0.5f, "the path still leads around the wall (" + NavigationService.RemainingDistance(guard) + ")");

                Put(guard, new SysVec(2f, 0f, 3f));   // through the wall, as a script teleport would
                for (int i = 0; i < 20; i++) NavigationService.Tick(Dt);
                float after = NavigationService.RemainingDistance(guard);
                t.True(after > 2f && after < 3.3f, "teleported through the wall the agent re-plans from there: ~3 m left, not around (" + after + ")");
                t.True(NavigationService.HasDestination(guard), "… its destination kept");

                // Update Rotation turns the follower toward its velocity, the position stays the game's
                for (int i = 0; i < 30; i++) { ScriptStep(guard); var before = Feet(guard); NavigationService.Tick(Dt); t.True(Feet(guard) == before, "rotation only (frame " + i + ")"); }
                float yaw = NavigationService.EntityYaw(guard);
                t.True(Math.Abs(DeltaAngle(yaw, 90f)) < 20f, "Update Rotation turned it toward +x, where it walks (" + yaw + "°)");

                t.True(NavigationService.Warp(guard, new Vector3(-8f, 0, -9f)), "Warp");
                var w = Feet(guard);
                t.True(Math.Abs(w.X + 8f) < 0.05f && Math.Abs(w.Z + 9f) < 0.05f, "Warp moves the entity even with Update Position off (" + w + ")");
            }
            finally { End(); }
        }

        /// <summary>Update Position switched at runtime: off, the entity stays where the walk left it while the agent keeps
        /// steering; on again, the walk goes on from the agent's position — no jump.</summary>
        [Test]
        public static void UpdatePositionSwitchesAtRuntime(TestContext t)
        {
            if (!Native()) return;
            Level(s => new[] { Agent(s, "Guard", new Vector3(-5f, 0, -9f), true) }, out var a);
            var guard = a[0];
            try
            {
                NavigationService.SetDestination(guard, new Vector3(5f, 0, -9f));
                for (int i = 0; i < 30; i++) NavigationService.Tick(Dt);
                var walked = Feet(guard);
                t.True(walked.X > -4.5f, "with Update Position on the service walks it (" + walked + ")");
                t.True(NavigationService.GetUpdatePosition(guard), "(on)");
                NavigationService.SetUpdatePosition(guard, false);
                t.False(NavigationService.GetUpdatePosition(guard), "switched off");
                for (int i = 0; i < 30; i++) NavigationService.Tick(Dt);
                t.True(Feet(guard) == walked, "off, the entity stays where the walk left it (" + Feet(guard) + ")");
                NavigationService.SetUpdatePosition(guard, true);
                NavigationService.Tick(Dt);
                var resumed = Feet(guard);
                t.True(resumed.X >= walked.X && resumed.X - walked.X < 0.2f, "on again, the walk goes on from there without a jump (" + resumed + ")");
                for (int i = 0; i < 45; i++) NavigationService.Tick(Dt);
                t.True(Feet(guard).X > resumed.X + 1f, "… and keeps walking (" + Feet(guard) + ")");
                t.True(guard.GetComponent<NavAgent>().UpdatePosition, "the component was never changed");
            }
            finally { End(); }
        }

        /// <summary>Two followers head-on in a lane, each moved by its own desired velocity: the crowd's avoidance is in that
        /// velocity, so they pass each other without touching.</summary>
        [Test]
        public static void FollowersAvoidEachOtherThroughTheirDesiredVelocity(TestContext t)
        {
            if (!Native()) return;
            Level(s => new[] { Agent(s, "A", new Vector3(-6f, 0, -10f), false), Agent(s, "B", new Vector3(6f, 0, -10f), false) }, out var ab);
            try
            {
                NavigationService.SetDestination(ab[0], new Vector3(6f, 0, -10f));
                NavigationService.SetDestination(ab[1], new Vector3(-6f, 0, -10f));
                float minSep = float.MaxValue;
                for (int i = 0; i < 60 * 8; i++)
                {
                    ScriptStep(ab[0]); ScriptStep(ab[1]);
                    NavigationService.Tick(Dt);
                    minSep = Math.Min(minSep, (Feet(ab[0]) - Feet(ab[1])).Length());
                }
                Console.WriteLine("        head-on followers: closest " + minSep.ToString("0.000") + " m, A at " + Feet(ab[0]) + ", B at " + Feet(ab[1]));
                t.True(minSep > 0.72f, "they never touch (closest " + minSep + " m, radii 0.8)");
                t.True((Feet(ab[0]) - new SysVec(6f, 0, -10f)).Length() < 0.6f && (Feet(ab[1]) - new SysVec(-6f, 0, -10f)).Length() < 0.6f, "both arrive");
            }
            finally { End(); }
        }

        private static float DeltaAngle(float from, float to)
        {
            float d = (to - from) % 360f;
            if (d > 180f) d -= 360f; else if (d < -180f) d += 360f;
            return d;
        }

        /// <summary>#435 review: an agent that walked with Resolve Collisions (its capsule registered) and is then switched to
        /// Update Position off leaves no capsule behind — the guard's body is where the game moves it, not where it switched.</summary>
        [Test]
        public static void SwitchingToFollowDropsTheCapsule(TestContext t)
        {
            if (!Native()) return;
            GameEntity guard = null;
            Level(s => new[] { guard = PhysicsRaycastTests.Add(s, null, "Guard", new Vector3(-10f, 0, 8f), e => e.AddComponentDirect(new NavAgent(e)
                { ResolveCollisions = true, Speed = 3.5f, Radius = 0.4f })) }, out _);
            try
            {
                NavigationService.SetDestination(guard, new Vector3(10f, 0, 8f));
                for (int i = 0; i < 60; i++) NavigationService.Tick(Dt);
                t.True(CollisionService.TryGetCharacterOf(guard, out var f0, out _, out _) && Math.Abs(f0.X - Feet(guard).X) < 0.05f, "walking, its capsule is its body");
                NavigationService.SetUpdatePosition(guard, false);
                for (int i = 0; i < 120; i++) { ScriptStep(guard); NavigationService.Tick(Dt); }
                long id = Editor.Scripting.ScriptRuntime.Instance.HandleForEntity(guard);
                t.False(CollisionService.TryGetCharacterOf(guard, out _, out _, out _), "following, its old capsule is gone");
                t.False(CollisionService.CapsuleOverlaps(new Vector3(f0.X, 0.02f, f0.Z), 0.35f, 1.78f, 0), "… nothing blocks where it switched");
            }
            finally { End(); }
        }

        /// <summary>#435 review: a follower's position is never written by the ticks — under a turned, scaled parent 3 km from
        /// the origin it used to creep (rewritten from its own world position every tick, 0.3 m in 20 s); its rotation still is.</summary>
        [Test]
        public static void AFollowerIsNeverMovedUnderATurnedParent(TestContext t)
        {
            if (!Native()) return;
            var s = new Scene { Name = "Parent", IsActive = true };
            PhysicsRaycastTests.Add(s, null, "Floor", new Vector3(3000f, -0.5f, 3000f), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(40f, 1f, 40f) }));
            var car = PhysicsRaycastTests.Add(s, null, "Car", new Vector3(3000f, 0f, 3000f), e => { e.Transform.LocalRotation = new Vector3(0f, 37.3f, 0f); e.Transform.LocalScale = new Vector3(1.3f, 1.3f, 1.3f); });
            var passenger = PhysicsRaycastTests.Add(s, car, "Passenger", new Vector3(1.234f, 0f, 2.345f), e => e.AddComponentDirect(new NavAgent(e)
                { UpdatePosition = false, UpdateRotation = true, ResolveCollisions = false }));
            PhysicsRaycastTests.AsLoaded(s);
            CollisionService.Build(s);
            NavigationService.BakeScene(s, NavigationService.DefaultSettings(), save: false, load: true);
            NavigationService.Begin(s);
            try
            {
                var before = passenger.Transform.LocalPosition;
                NavigationService.Tick(Dt);
                NavigationService.SetDestination(passenger, new Vector3(3010f, 0f, 3010f));
                for (int i = 0; i < 1200; i++) NavigationService.Tick(Dt);
                var after = passenger.Transform.LocalPosition;
                t.True(after.X == before.X && after.Y == before.Y && after.Z == before.Z, "20 s of ticks never moved it (" + before + " -> " + after + ")");
                t.True(Math.Abs(DeltaAngle(passenger.Transform.LocalRotation.Y, 0f)) > 1f, "… but turned it toward its way (" + passenger.Transform.LocalRotation + ")");
            }
            finally { End(); }
        }
    }
}
