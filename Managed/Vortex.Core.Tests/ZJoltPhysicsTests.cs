using System;
using Editor.Core.Data;
using Editor.Core.Services.AI;
using Editor.Core.Services.Physics;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Physics;

namespace VortexTests
{
    /// <summary>The Jolt side of the physics fixes, through PhysicsService against the real native world. The class name
    /// sorts it LAST on purpose: it loads the native engine library into the test process, and every test before it keeps
    /// running without one (as it always has). Skipped when no engine library with Jolt is found (VORTEX_NATIVE_DIR, or the
    /// repository's CMake build tree).</summary>
    public static class ZJoltPhysicsTests
    {
        private static bool Jolt()
        {
            try { Editor.Core.Native.NativeLoader.Register(); } catch { }
            if (PhysicsNative.Available) return true;
            Console.WriteLine("    (skipped: no native physics — " + PhysicsNative.Reason + ")");
            return false;
        }

        /// <summary>Both worlds the way play start builds them (the character world first, then the Jolt bodies), plus one
        /// step so the static broadphase is current.</summary>
        private static void BuildWorlds(Scene s)
        {
            CollisionService.Build(s);
            PhysicsService.Build(s);
            PhysicsService.Step(PhysicsService.FixedStep);
        }

        private static void ClearWorlds()
        {
            PhysicsService.Clear();
            CollisionService.Clear();
            CollisionService.CharacterOwnerResolver = null;
            PhysicsNative.Shutdown();   // no native worker threads left behind
        }

        private static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        private static Vector3 Dir(Vector3 from, Vector3 to)
        {
            var d = new Vector3(to.X - from.X, to.Y - from.Y, to.Z - from.Z);
            float l = (float)Math.Sqrt(Dot(d, d));
            return new Vector3(d.X / l, d.Y / l, d.Z / l);
        }

        /// <summary>#418: Physics.Raycast(…) (Jolt) and Physics.Raycast(…, explicit mask) (the managed world) answer the
        /// same for rays that start inside a box, a sphere, a capsule and a mesh, from outside, and with an ignored entity.</summary>
        [Test]
        public static void RaysAgreeOnTheJoltAndTheManagedPath(TestContext t)
        {
            if (!Jolt()) return;
            var scene = PhysicsRaycastTests.RayScene(out var crate, out var walls, out var wall, out var ball, out var pillar, out var room);
            var oldProvider = CollisionService.MeshTriangleProvider;
            CollisionService.MeshTriangleProvider = PhysicsRaycastTests.RoomProvider;
            try
            {
                BuildWorlds(scene);
                t.True(PhysicsService.IsBuilt && PhysicsService.BodyCount == 5, "the Jolt world has the five colliders (" + PhysicsService.BodyCount + ")");
                var fwd = new Vector3(0, 0, 1);
                var rays = new (Vector3 o, Vector3 d, GameEntity ignore, GameEntity expect, string what)[]
                {
                    (new Vector3(0, 0.5f, 0), fwd, null, wall, "from inside the crate"),
                    (new Vector3(0, 0.5f, -2f), fwd, null, crate, "at the crate from outside"),
                    (new Vector3(0, 0.5f, -2f), fwd, crate, wall, "through the ignored crate"),
                    (new Vector3(0, 0.5f, 0.45f), fwd, crate, wall, "from deep inside the ignored crate"),
                    (new Vector3(0, 0.5f, 1f), fwd, walls, null, "ignoring the wall's group"),
                    (new Vector3(5f, 1f, 0), fwd, null, null, "from inside the ball"),
                    (new Vector3(5f, 1f, -3f), fwd, null, ball, "at the ball"),
                    (new Vector3(10f, 1f, 0), fwd, null, null, "from inside the capsule"),
                    (new Vector3(10f, 1f, -5f), fwd, null, pillar, "at the capsule's side"),
                    (new Vector3(10.3f, 1.88f, -5f), fwd, null, null, "past the capsule's rounded top"),
                    (new Vector3(15f, 0.7f, 0.2f), fwd, null, room, "from inside the mesh room"),
                    (new Vector3(15f, 5f, 0.2f), new Vector3(0, -1, 0), null, room, "down onto the mesh room"),
                };
                foreach (var r in rays)
                {
                    bool j = PhysicsService.Raycast(r.o, r.d, 50f, out _, out var jn, out var je, out float jd, PhysicsService.SolidLayerMask, r.ignore);
                    bool m = CollisionService.Raycast(r.o, r.d, 50f, ~0, out _, out var mn, out var me, out float md, r.ignore);
                    t.Equal(r.expect != null, j, r.what + ": Jolt hit");
                    t.Equal(r.expect != null, m, r.what + ": managed hit");
                    if (r.expect == null) continue;
                    t.True(je == r.expect && me == r.expect, r.what + ": both report " + r.expect.Name + " (Jolt " + je?.Name + ", managed " + me?.Name + ")");
                    t.True(Math.Abs(jd - md) < 0.01f && jd > 0f, r.what + ": the same distance (Jolt " + jd + ", managed " + md + ")");
                    t.True(Dot(jn, r.d) < 0f && Dot(mn, r.d) < 0f, r.what + ": both normals face the origin");
                }
            }
            finally { ClearWorlds(); CollisionService.MeshTriangleProvider = oldProvider; }
        }

        /// <summary>#498: a box with pitch, yaw and roll (and one under a rotated parent) is the same box in the character
        /// world as the Jolt body — rays from above, below and the side meet both at the same distance.</summary>
        [Test]
        public static void RotatedBoxesMatchTheJoltBodies(TestContext t)
        {
            if (!Jolt()) return;
            var s = new Scene { Name = "Rotated" };
            var ramp = PhysicsRaycastTests.Add(s, null, "Ramp", new Vector3(0, 2f, 0), e =>
            {
                e.Transform.LocalRotation = new Vector3(25f, 30f, 10f);
                e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(3f, 0.5f, 12f) });
            });
            var parent = PhysicsRaycastTests.Add(s, null, "Tilted", new Vector3(20f, 1f, 0), e => e.Transform.LocalRotation = new Vector3(-15f, 45f, 20f));
            var child = PhysicsRaycastTests.Add(s, parent, "Plank", new Vector3(0, 0.5f, 3f), e =>
            {
                e.Transform.LocalRotation = new Vector3(0, 0, -30f);
                e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(4f, 0.3f, 1f) });
            });
            PhysicsRaycastTests.AsLoaded(s);
            try
            {
                BuildWorlds(s);
                t.Equal(2, PhysicsService.BodyCount, "two box bodies");
                int compared = 0;
                for (int i = -4; i <= 4; i++)
                    for (int k = -4; k <= 4; k++)
                    {
                        foreach (var (o, d) in new[]
                        {
                            (new Vector3(i * 0.5f, 20f, k * 1.5f), new Vector3(0, -1, 0)),
                            (new Vector3(i * 0.5f, -10f, k * 1.5f), new Vector3(0, 1, 0)),
                            (new Vector3(20f + i * 0.5f, 20f, k * 0.6f), new Vector3(0, -1, 0)),
                            (new Vector3(-10f, 2f + i * 0.4f, k * 1.5f), new Vector3(1, 0, 0)),
                        })
                        {
                            bool j = PhysicsService.Raycast(o, d, 40f, out _, out _, out var je, out float jd);
                            bool m = CollisionService.Raycast(o, d, 40f, ~0, out _, out _, out var me, out float md);
                            t.Equal(j, m, "Jolt and the character world agree on a hit from " + o.X + ", " + o.Y + ", " + o.Z);
                            if (!j) continue;
                            compared++;
                            t.True(je == me && Math.Abs(jd - md) < 0.01f, "… on " + je?.Name + " at the same distance (Jolt " + jd + ", managed " + md + ")");
                        }
                    }
                t.True(compared > 60, "most rays hit the rotated boxes (" + compared + ")");
            }
            finally { ClearWorlds(); }
        }

        /// <summary>#418: the opt-in "is this point inside something?" query.</summary>
        [Test]
        public static void OverlapPointFindsWhatContainsThePoint(TestContext t)
        {
            if (!Jolt()) return;
            var scene = PhysicsRaycastTests.RayScene(out var crate, out _, out _, out _, out var pillar, out var room);
            var oldProvider = CollisionService.MeshTriangleProvider;
            CollisionService.MeshTriangleProvider = PhysicsRaycastTests.RoomProvider;
            try
            {
                BuildWorlds(scene);
                var inCrate = PhysicsService.OverlapPoint(new Vector3(0.2f, 0.4f, -0.1f));
                t.True(inCrate.Count == 1 && inCrate[0] == crate, "a point in the crate reports the crate");
                var inPillar = PhysicsService.OverlapPoint(new Vector3(10f, 1.5f, 0.1f));
                t.True(inPillar.Count == 1 && inPillar[0] == pillar, "a point in the capsule reports it");
                var inRoom = PhysicsService.OverlapPoint(new Vector3(15f, 0.7f, 0.2f));
                t.True(inRoom.Count == 1 && inRoom[0] == room, "a point in the closed mesh reports it");
                t.Equal(0, PhysicsService.OverlapPoint(new Vector3(2f, 0.5f, 0f)).Count, "a point in open air reports nothing");
            }
            finally { ClearWorlds(); CollisionService.MeshTriangleProvider = oldProvider; }
        }

        /// <summary>#406: with Physics.SetCharacterController("jolt") the character capsules are bodies — a ray that hits one
        /// reports the entity whose handle moved it, an eye ray from inside the own capsule (or with the own entity ignored)
        /// sees the other character, and the AI's line of sight works between two Jolt characters. With the managed
        /// controller rays never hit characters (unchanged).</summary>
        [Test]
        public static void JoltCharacterCapsulesReportTheirEntity(TestContext t)
        {
            if (!Jolt()) return;
            var s = new Scene { Name = "Guards" };
            var floor = PhysicsRaycastTests.Add(s, null, "Floor", new Vector3(0, -0.5f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(40f, 1f, 40f) }));
            var a = PhysicsRaycastTests.Add(s, null, "GuardA", new Vector3(0, 0, 0), null);
            var b = PhysicsRaycastTests.Add(s, null, "GuardB", new Vector3(0, 0, 6f), null);
            PhysicsRaycastTests.AsLoaded(s);   // every entity reads engine id 0, as in the player
            const long ha = 11, hb = 12;   // their script handles
            try
            {
                BuildWorlds(s);
                CollisionService.CharacterOwnerResolver = id => id == ha ? a : id == hb ? b : null;
                PhysicsService.JoltCharacters = true;
                var down = new Vector3(0, -0.05f, 0);
                var pa = PhysicsService.MoveCharacterJolt(new Vector3(0, 0, 0), 0.35f, 1.8f, down, PhysicsService.FixedStep, out _, ha, 0.35f, 50f);
                var pb = PhysicsService.MoveCharacterJolt(new Vector3(0, 0, 6f), 0.35f, 1.8f, down, PhysicsService.FixedStep, out _, hb, 0.35f, 50f);
                PhysicsService.Step(PhysicsService.FixedStep);
                t.Equal(2, PhysicsService.JoltCharacterCount, "two Jolt characters");

                var eye = new Vector3(pa.X, pa.Y + 1.6f, pa.Z);
                var chestB = new Vector3(pb.X, pb.Y + 1.0f, pb.Z);
                var dir = Dir(eye, chestB);
                t.True(PhysicsService.Raycast(eye, dir, 20f, out _, out _, out var e, out float d, PhysicsService.SolidLayerMask, a) && e == b && d > 5f,
                       "A's eye ray with A ignored hits B and reports B (" + e?.Name + " at " + d + ")");
                t.True(PhysicsService.Raycast(eye, dir, 20f, out _, out _, out e, out d) && e == b && d > 5f,
                       "the eye ray starts inside A's capsule, so A is not hit even without ignoring it (" + e?.Name + " at " + d + ")");
                var behindA = new Vector3(0, 1f, -3f);
                t.True(PhysicsService.Raycast(behindA, new Vector3(0, 0, 1), 20f, out _, out _, out e, out d) && e == a && Math.Abs(d - 2.65f) < 0.02f,
                       "a ray from behind hits A's capsule and reports A (" + e?.Name + " at " + d + ")");
                t.True(PhysicsService.Raycast(behindA, new Vector3(0, 0, 1), 20f, out _, out _, out e, out d, PhysicsService.SolidLayerMask, a) && e == b && Math.Abs(d - 8.65f) < 0.02f,
                       "… and with A ignored it passes through A to B (" + e?.Name + " at " + d + ")");
                t.True(PerceptionService.HasLineOfSight(eye, chestB, a, b), "A (a Jolt character) has line of sight to B (a Jolt character) in open space");
                t.True(PhysicsService.OverlapSphere(chestB, 0.2f).Contains(b), "OverlapSphere reports B");
                t.True(PhysicsService.OverlapPoint(new Vector3(pa.X, pa.Y + 1f, pa.Z)).Contains(a), "OverlapPoint inside A's capsule reports A");
            }
            finally { ClearWorlds(); }

            // the managed controller: characters are no bodies, the same ray from behind passes both
            try
            {
                BuildWorlds(s);
                CollisionService.CharacterOwnerResolver = id => id == ha ? a : id == hb ? b : null;
                t.False(PhysicsService.JoltCharacters, "(managed controller)");
                CollisionService.MoveCharacter(new Vector3(0, 0, 0), 0.35f, 1.8f, new Vector3(0, -0.05f, 0), out _, ha);
                CollisionService.MoveCharacter(new Vector3(0, 0, 6f), 0.35f, 1.8f, new Vector3(0, -0.05f, 0), out _, hb);
                t.False(PhysicsService.Raycast(new Vector3(0, 1f, -3f), new Vector3(0, 0, 1), 20f, out _, out _, out _, out _),
                        "with the managed controller a ray never hits a character");
            }
            finally { ClearWorlds(); }
        }

        /// <summary>#406: a character whose id names no entity reports no entity, and a ray that ignores another character's
        /// entity still hits the anonymous capsule — with every entity reading engine id 0, as loaded from a scene file.</summary>
        [Test]
        public static void AnonymousJoltCharactersReportNoEntity(TestContext t)
        {
            if (!Jolt()) return;
            var s = new Scene { Name = "Anonymous" };
            var floor = PhysicsRaycastTests.Add(s, null, "Floor", new Vector3(0, -0.5f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(40f, 1f, 40f) }));
            var first = PhysicsRaycastTests.Add(s, null, "EntityZero", new Vector3(0, 0, 0), null);
            PhysicsRaycastTests.AsLoaded(s);
            const long hFirst = 51, hAnon = 999;
            try
            {
                BuildWorlds(s);
                CollisionService.CharacterOwnerResolver = id => id == hFirst ? first : null;
                PhysicsService.JoltCharacters = true;
                PhysicsService.MoveCharacterJolt(new Vector3(0, 0, 0), 0.35f, 1.8f, new Vector3(0, -0.05f, 0), PhysicsService.FixedStep, out _, hFirst, 0.35f, 50f);
                PhysicsService.MoveCharacterJolt(new Vector3(0, 0, 4f), 0.35f, 1.8f, new Vector3(0, -0.05f, 0), PhysicsService.FixedStep, out _, hAnon, 0.35f, 50f);
                PhysicsService.Step(PhysicsService.FixedStep);
                var o = new Vector3(0, 1f, 2f);
                t.True(PhysicsService.Raycast(o, new Vector3(0, 0, -1), 10f, out _, out _, out var e, out _) && e == first, "the capsule of entity 0 reports entity 0");
                t.True(PhysicsService.Raycast(o, new Vector3(0, 0, 1), 10f, out _, out _, out e, out float d) && e == null && Math.Abs(d - 1.65f) < 0.02f,
                       "the anonymous capsule is hit but reports no entity (" + e?.Name + ")");
                t.True(PhysicsService.Raycast(o, new Vector3(0, 0, 1), 10f, out _, out _, out e, out d, PhysicsService.SolidLayerMask, first) && Math.Abs(d - 1.65f) < 0.02f,
                       "ignoring entity 0 does not pass through the anonymous capsule (" + d + ")");
            }
            finally { ClearWorlds(); }
        }

        /// <summary>#407 with the "jolt" controller: removing a character (Physics.RemoveCharacter, a destroyed / deactivated
        /// subtree, a ragdoll of the model under the root) destroys its Jolt character — JoltCharacterCount drops and a ray
        /// through its spot no longer hits — and the next MoveCharacter creates it again.</summary>
        [Test]
        public static void RemovedJoltCharactersLeaveTheWorld(TestContext t)
        {
            if (!Jolt()) return;
            var s = new Scene { Name = "Lifetime" };
            var floor = PhysicsRaycastTests.Add(s, null, "Floor", new Vector3(0, -0.5f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(40f, 1f, 40f) }));
            var a = PhysicsRaycastTests.Add(s, null, "GuardA", new Vector3(0, 0, 0), null);
            var model = PhysicsRaycastTests.Add(s, a, "GuardAModel", new Vector3(0, 0, 0), null);
            var b = PhysicsRaycastTests.Add(s, null, "GuardB", new Vector3(0, 0, 6f), null);
            PhysicsRaycastTests.AsLoaded(s);
            const long ha = 41, hb = 42;
            var behindA = new Vector3(0, 1f, -3f);
            var fwd = new Vector3(0, 0, 1);
            GameEntity FirstHit() => PhysicsService.Raycast(behindA, fwd, 20f, out _, out _, out var e, out _) ? e : null;
            void Move(long id, Vector3 feet)
            {
                PhysicsService.MoveCharacterJolt(feet, 0.35f, 1.8f, new Vector3(0, -0.05f, 0), PhysicsService.FixedStep, out _, id, 0.35f, 50f);
                PhysicsService.Step(PhysicsService.FixedStep);
            }
            try
            {
                BuildWorlds(s);
                CollisionService.CharacterOwnerResolver = id => id == ha ? a : id == hb ? b : null;
                PhysicsService.JoltCharacters = true;
                Move(ha, new Vector3(0, 0, 0));
                Move(hb, new Vector3(0, 0, 6f));
                t.Equal(2, PhysicsService.JoltCharacterCount, "two Jolt characters");
                t.True(FirstHit() == a, "the ray from behind hits A");

                t.True(PhysicsService.RemoveCharacter(ha), "Physics.RemoveCharacter(A)");
                PhysicsService.Step(PhysicsService.FixedStep);
                t.Equal(1, PhysicsService.JoltCharacterCount, "… destroys A's Jolt character");
                t.False(CollisionService.HasCharacter(ha), "… and its managed capsule");
                t.True(FirstHit() == b, "the ray now passes A's old spot and hits B (" + FirstHit()?.Name + ")");

                Move(ha, new Vector3(0, 0, 0));
                t.Equal(2, PhysicsService.JoltCharacterCount, "the next MoveCharacter creates A again");
                t.True(FirstHit() == a, "… and rays hit it again");

                t.Equal(1, PhysicsService.RemoveCharacterOf(model), "a ragdoll of A's model removes A's character");
                PhysicsService.Step(PhysicsService.FixedStep);
                t.True(PhysicsService.JoltCharacterCount == 1 && FirstHit() == b, "… from the Jolt world too");

                Move(ha, new Vector3(0, 0, 0));
                t.Equal(1, PhysicsService.RemoveCharactersIn(a), "destroying / deactivating A removes its character");
                t.Equal(1, PhysicsService.RemoveCharactersIn(b), "… and B's");
                PhysicsService.Step(PhysicsService.FixedStep);
                t.True(PhysicsService.JoltCharacterCount == 0 && FirstHit() == null, "no Jolt character and nothing to hit left");
            }
            finally { ClearWorlds(); }
        }

        /// <summary>#443: the Jolt world builds no bodies for an inactive level part (as before), and a child switched on
        /// while its ancestor is still inactive joins neither world; switching the ancestor on brings it in.</summary>
        [Test]
        public static void NothingJoinsUnderAnInactiveAncestor(TestContext t)
        {
            if (!Jolt()) return;
            var s = new Scene { Name = "Acts" };
            var floor = PhysicsRaycastTests.Add(s, null, "Floor", new Vector3(0, -0.5f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(40f, 1f, 40f) }));
            var act = PhysicsRaycastTests.Add(s, null, "Area_3_Rome", new Vector3(0, 0, 0), null);
            var wall = PhysicsRaycastTests.Add(s, act, "Col_PalazziR_N", new Vector3(0, 10f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(18f, 20f, 20f) }));
            PhysicsRaycastTests.AsLoaded(s);
            act.SetActiveFlagOnly(false);
            try
            {
                BuildWorlds(s);
                t.Equal(1, PhysicsService.BodyCount, "only the floor has a body");
                PhysicsService.AddEntity(wall);
                CollisionService.AddEntityShapes(wall);
                t.Equal(1, PhysicsService.BodyCount, "a child switched on under the inactive act gets no body");
                t.False(CollisionService.CapsuleOverlaps(new Vector3(0, 0.02f, 0), 0.3f, 1.78f), "… and no shape: CanStand inside it");
                act.SetActiveFlagOnly(true);
                CollisionService.AddEntityShapes(act);
                PhysicsService.AddEntity(act);
                t.Equal(2, PhysicsService.BodyCount, "switching the act on adds the wall's body");
                t.True(CollisionService.CapsuleOverlaps(new Vector3(0, 0.02f, 0), 0.3f, 1.78f), "… and its shape");
            }
            finally { ClearWorlds(); }
        }

        /// <summary>#417 with Physics.SetCharacterController("jolt"): the CharacterVirtual passes through the bodies of the
        /// entity its characterId names — a static collider and a kinematic Rigidbody + collider — while a character whose id
        /// names no entity is still shoved out of a collider it stands in.</summary>
        [Test]
        public static void JoltCharacterIgnoresItsOwnColliders(TestContext t)
        {
            if (!Jolt()) return;
            var s = new Scene { Name = "OwnColliders" };
            var floor = PhysicsRaycastTests.Add(s, null, "Floor", new Vector3(0, -0.5f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(40f, 1f, 40f) }));
            GameEntity Guard(string name, float x, bool kinematic) => PhysicsRaycastTests.Add(s, null, name, new Vector3(x, 0, 0), e =>
            {
                e.AddComponentDirect(new CapsuleCollider(e) { Radius = 0.35f, Height = 1.8f, Center = new Vector3(0, 0.9f, 0) });
                if (kinematic) e.AddComponentDirect(new Rigidbody(e) { BodyType = RigidbodyType.Kinematic });
            });
            var still = Guard("StaticGuard", 0f, false);
            var moving = Guard("KinematicGuard", 5f, true);
            var nobody = Guard("Pillar", 10f, false);
            PhysicsRaycastTests.AsLoaded(s);
            const long hs = 31, hm = 32, hAnon = 99;
            try
            {
                BuildWorlds(s);
                t.Equal(4, PhysicsService.BodyCount, "floor + three guard bodies");
                CollisionService.CharacterOwnerResolver = id => id == hs ? still : id == hm ? moving : null;
                PhysicsService.JoltCharacters = true;
                Vector3 Run(Vector3 feet, long id)
                {
                    for (int i = 0; i < 30; i++)
                    {
                        feet = PhysicsService.MoveCharacterJolt(feet, 0.35f, 1.8f, new Vector3(0, -9.81f * PhysicsService.FixedStep, 0), PhysicsService.FixedStep,
                            out _, id, 0.35f, 50f);
                        PhysicsService.Step(PhysicsService.FixedStep);
                    }
                    return feet;
                }
                var ps = Run(new Vector3(0, 0, 0), hs);
                t.True(Math.Abs(ps.Y) < 0.05f && Math.Abs(ps.X) < 0.02f && Math.Abs(ps.Z) < 0.02f, "inside its own static collider the guard stays on the floor (" + ps.X + ", " + ps.Y + ", " + ps.Z + ")");
                var pm = Run(new Vector3(5f, 0, 0), hm);
                t.True(Math.Abs(pm.Y) < 0.05f && Math.Abs(pm.X - 5f) < 0.02f && Math.Abs(pm.Z) < 0.02f, "… and inside its own kinematic body (" + pm.X + ", " + pm.Y + ", " + pm.Z + ")");
                var pa = Run(new Vector3(10f, 0, 0), hAnon);
                t.True(pa.Y > 0.3f || Math.Abs(pa.X - 10f) > 0.3f || Math.Abs(pa.Z) > 0.3f, "a character whose id names no entity is pushed out of the pillar (" + pa.X + ", " + pa.Y + ", " + pa.Z + ")");
            }
            finally { ClearWorlds(); }
        }
    }
}
