using System;
using System.Collections.Generic;
using Editor.Core.Data;
using Editor.Core.Services.Physics;
using Editor.ECS;
using Editor.ECS.Components.Physics;

namespace VortexTests
{
    /// <summary>The managed character controller (Physics.MoveCharacter) against the collision world: the moving entity's
    /// own colliders (#417), the lifetime of registered characters (#407), inactive level parts (#443).</summary>
    public static class CharacterControllerTests
    {
        private const float Dt = 1f / 60f;
        private const float R = 0.35f, H = 1.8f;

        private static GameEntity Add(Scene s, GameEntity parent, string name, Vector3 pos, Action<GameEntity> setup)
            => PhysicsRaycastTests.Add(s, parent, name, pos, setup);

        private static GameEntity Floor(Scene s)
            => Add(s, null, "Floor", new Vector3(0, -0.5f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(40f, 1f, 40f) }));

        /// <summary>A guard at the origin with a solid capsule collider (what makes it shootable) and a hitbox child.</summary>
        private static GameEntity Guard(Scene s, out GameEntity hitbox)
        {
            var guard = Add(s, null, "Guard", new Vector3(0, 0, 0), e => e.AddComponentDirect(new CapsuleCollider(e) { Radius = R, Height = H, Center = new Vector3(0, 0.9f, 0) }));
            hitbox = Add(s, guard, "Hitbox", new Vector3(0, 1.5f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(0.5f, 0.5f, 0.5f) }));
            return guard;
        }

        private static Vector3 Fall(Vector3 feet, long id, int frames, out bool grounded)
        {
            grounded = false;
            for (int i = 0; i < frames; i++) feet = CollisionService.MoveCharacter(feet, R, H, new Vector3(0, -9.81f * Dt, 0), out grounded, id);
            return feet;
        }

        [Test]
        public static void OwnCollidersAreNoObstacle(TestContext t)
        {
            var s = new Scene { Name = "Guards" };
            Floor(s);
            var guard = Guard(s, out _);
            const long guardId = 21, otherId = 22, unknownId = 99;   // 21 is the guard's handle; 99 names no entity
            try
            {
                CollisionService.Build(s);
                CollisionService.CharacterOwnerResolver = id => id == guardId ? guard : null;

                // the bug: an id that names no entity still collides with the collider it stands in — shoved up out of it
                var anon = Fall(new Vector3(0, 0, 0), unknownId, 30, out _);
                t.True(anon.Y > 0.3f, "without an owner the capsule is pushed out of the collider it starts in (y = " + anon.Y + ")");
                CollisionService.RemoveCharacter(unknownId);

                // the guard's own id: its capsule collider and its hitbox child are no obstacles
                var p = Fall(new Vector3(0, 0, 0), guardId, 30, out bool grounded);
                t.True(Math.Abs(p.Y) < 0.02f && Math.Abs(p.X) < 1e-3f && Math.Abs(p.Z) < 1e-3f && grounded,
                       "the guard stays on the floor inside its own collider (" + p.X + ", " + p.Y + ", " + p.Z + ")");
                p = CollisionService.MoveCharacter(p, R, H, new Vector3(0.1f, -0.01f, 0.05f), out _, guardId);
                t.True(Math.Abs(p.X - 0.1f) < 1e-3f && Math.Abs(p.Z - 0.05f) < 1e-3f, "… and walks out of it freely (" + p.X + ", " + p.Z + ")");
                t.False(CollisionService.CapsuleOverlaps(new Vector3(0, 0.02f, 0), R, H - 0.02f, guardId), "CanStand ignores the guard's own collider");
                t.True(CollisionService.CapsuleOverlaps(new Vector3(0, 0.02f, 0), R, H - 0.02f, otherId), "… but not for anyone else");

                // other characters and rays still collide with the guard's collider (its capsule registration removed,
                // so only the collider can stop them)
                CollisionService.RemoveCharacter(guardId);
                var q = CollisionService.MoveCharacter(new Vector3(2f, 0, 0), R, H, new Vector3(-3f, 0, 0), out _, otherId);
                t.True(q.X > 2 * R - 0.05f, "another character is stopped by the guard's collider (x = " + q.X + ")");
                t.True(CollisionService.Raycast(new Vector3(0, 1f, -3f), new Vector3(0, 0, 1), 10f, ~0, out _, out _, out var hit, out _) && hit == guard,
                       "a ray still hits the guard's collider");
            }
            finally { CollisionService.Clear(); CollisionService.CharacterOwnerResolver = null; }
        }

        [Test]
        public static void NoCollisionEventsAgainstOwnColliders(TestContext t)
        {
            var s = new Scene { Name = "Events" };
            Floor(s);
            var guard = Guard(s, out var hitbox);
            const long guardId = 21, otherId = 22;
            var enter = new List<CollisionService.Contact>();
            var stay = new List<CollisionService.Contact>();
            var exit = new List<CollisionService.Contact>();
            var collide = new List<CollisionService.Contact>();
            try
            {
                CollisionService.Build(s);
                CollisionService.CharacterOwnerResolver = id => id == guardId ? guard : null;
                Fall(new Vector3(0, 0, 0), guardId, 2, out _);
                CollisionService.RegisterCharacter(otherId, new Vector3(0.5f, 0, 0), R, H);   // another character pressing into the guard's collider
                CollisionService.StepEvents(enter, stay, exit, collide);
                foreach (var c in collide)
                    t.False(c.CharacterId == guardId && (c.Other == guard || c.Other == hitbox), "no OnCollisionEnter of the guard against its own " + c.Other.Name);
                t.True(collide.Exists(c => c.CharacterId == guardId && c.Other?.Name == "Floor"), "the guard still touches the floor");
                t.True(collide.Exists(c => c.CharacterId == otherId && c.Other == guard), "another character touching the guard's collider gets OnCollisionEnter");
            }
            finally { CollisionService.Clear(); CollisionService.CharacterOwnerResolver = null; }
        }

        /// <summary>#407: a registered capsule goes with Physics.RemoveCharacter, with its destroyed / deactivated subtree
        /// (what ScriptRuntime calls) and with a ragdoll of the model under the root that moved it — and comes back with the
        /// next MoveCharacter.</summary>
        [Test]
        public static void RemovedCharactersStopBlocking(TestContext t)
        {
            var s = new Scene { Name = "Lifetime" };
            Floor(s);
            var guard = Add(s, null, "Guard", new Vector3(0, 0, 0), null);
            var model = Add(s, guard, "GuardModel", new Vector3(0, 0, 0), null);
            var car = Add(s, null, "Car", new Vector3(10f, 0, 0), null);
            var passenger = Add(s, car, "Passenger", new Vector3(0, 0, 0), null);
            var passengerModel = Add(s, passenger, "PassengerModel", new Vector3(0, 0, 0), null);
            const long hg = 21, hb = 22, hcar = 23, hp = 24, anonymous = 77;
            try
            {
                CollisionService.Build(s);
                CollisionService.CharacterOwnerResolver = id => id == hg ? guard : id == hcar ? car : id == hp ? passenger : null;
                Fall(new Vector3(0, 0, 0), hg, 2, out _);
                var b = CollisionService.MoveCharacter(new Vector3(2f, 0, 0), R, H, new Vector3(-3f, 0, 0), out _, hb);
                t.True(b.X > 2 * R - 0.05f, "the guard's capsule blocks another character (x = " + b.X + ")");

                t.True(PhysicsService.RemoveCharacter(hg), "Physics.RemoveCharacter removes the guard");
                t.False(PhysicsService.RemoveCharacter(hg), "… and reports false when nothing is registered");
                b = CollisionService.MoveCharacter(b, R, H, new Vector3(-3f, 0, 0), out _, hb);
                t.True(b.X < -1f, "the other character walks through the spot (x = " + b.X + ")");
                t.False(CollisionService.CapsuleOverlaps(new Vector3(0, 0.02f, 0), R, H - 0.02f, anonymous), "CanStand at the old spot");

                Fall(new Vector3(0, 0, 0), hg, 1, out _);
                t.True(CollisionService.HasCharacter(hg), "the next MoveCharacter registers the guard again");
                b = CollisionService.MoveCharacter(b, R, H, new Vector3(3f, 0, 0), out _, hb);
                t.True(b.X < -2 * R + 0.05f, "… and it blocks again (x = " + b.X + ")");

                CollisionService.RegisterCharacter(anonymous, new Vector3(-5f, 0, 0), R, H);
                t.Equal(1, PhysicsService.RemoveCharactersIn(guard), "destroying / deactivating the guard removes its character");
                t.False(CollisionService.HasCharacter(hg), "… the guard's capsule is gone");
                t.True(CollisionService.HasCharacter(anonymous), "… a character whose id names no entity stays");

                Fall(new Vector3(0, 0, 0), hg, 1, out _);
                t.Equal(1, PhysicsService.RemoveCharacterOf(model), "a ragdoll of the model removes the capsule its root registered");
                t.False(CollisionService.HasCharacter(hg), "… the guard's capsule is gone");

                CollisionService.RegisterCharacter(hcar, new Vector3(10f, 0, 0), 1f, 2f);
                CollisionService.RegisterCharacter(hp, new Vector3(10f, 0.5f, 0), R, H);
                t.Equal(1, PhysicsService.RemoveCharacterOf(passengerModel), "a passenger's ragdoll removes the nearest character above it");
                t.False(CollisionService.HasCharacter(hp), "… the passenger's");
                t.True(CollisionService.HasCharacter(hcar), "… not the car's");
            }
            finally { CollisionService.Clear(); CollisionService.CharacterOwnerResolver = null; }
        }

        /// <summary>Triangles the character world holds (every box is 12) — counts shapes, so a re-add that duplicated
        /// them would show.</summary>
        private static int WorldTriangles()
            => CollisionService.ExportOcclusionGeometry(out _, out var idx) ? idx.Length / 3 : 0;

        /// <summary>#443: a level part saved inactive (the act roots of a multi-act game) and an entity that is itself
        /// inactive hold no collision: characters fall past them and rays miss them, until the part is switched on —
        /// once, without duplicates — and they leave again when it is switched off.</summary>
        [Test]
        public static void InactiveCollidersTakeNoPart(TestContext t)
        {
            var s = new Scene { Name = "Acts" };
            Floor(s);
            var act = Add(s, null, "Area_6_Desert", new Vector3(0, 0, 0), null);
            var edge = Add(s, act, "EdgeWall", new Vector3(0, 40f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(2f, 80f, 100f) }));
            var sub = Add(s, act, "Props", new Vector3(0, 0, 0), null);
            Add(s, sub, "Crate", new Vector3(3f, 0.5f, 0), e => e.AddComponentDirect(new BoxCollider(e)));
            var oldWall = Add(s, null, "OldMI6_WW", new Vector3(8f, 2.1f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(0.3f, 4.2f, 23f) }));
            act.SetActiveFlagOnly(false);
            oldWall.SetActiveFlagOnly(false);
            try
            {
                CollisionService.Build(s);
                t.Equal(12, WorldTriangles(), "only the floor is in the world");
                var p = new Vector3(0, 90f, 0);
                for (int i = 0; i < 200; i++) p = CollisionService.MoveCharacter(p, R, H, new Vector3(0, -1f, 0), out _, 0);
                t.True(Math.Abs(p.Y) < 0.05f, "the character falls past the inactive act's edge wall onto the floor (y = " + p.Y + ")");
                t.False(CollisionService.Raycast(new Vector3(0, 100f, 0), new Vector3(0, -1, 0), 99f, ~0, out _, out _, out _, out _),
                        "a ray misses the inactive wall (it would hit its top at y 80)");
                var w = CollisionService.MoveCharacter(new Vector3(6f, 0, 0), R, H, new Vector3(5f, 0, 0), out _, 0);
                t.True(w.X > 10f, "the character walks through the inactive entity's wall (x = " + w.X + ")");

                // a child switched on under the still inactive act joins nothing (SetActive on the child alone)
                CollisionService.AddEntityShapes(edge);
                t.Equal(12, WorldTriangles(), "nothing joins while an ancestor is inactive");

                // Scene.SetActive(act, true): the act's colliders join once
                act.SetActiveFlagOnly(true);
                CollisionService.AddEntityShapes(act);
                t.Equal(36, WorldTriangles(), "the act's wall and crate joined");
                p = new Vector3(0, 90f, 0);
                for (int i = 0; i < 200; i++) p = CollisionService.MoveCharacter(p, R, H, new Vector3(0, -1f, 0), out _, 0);
                t.True(Math.Abs(p.Y - 80f) < 0.05f, "now the character lands on the wall (y = " + p.Y + ")");

                // Scene.SetActive(act, false) and on again: no duplicates
                act.SetActiveFlagOnly(false);
                CollisionService.RemoveEntityShapes(act);
                t.Equal(12, WorldTriangles(), "switched off, the act's colliders left");
                act.SetActiveFlagOnly(true);
                CollisionService.AddEntityShapes(act);
                t.Equal(36, WorldTriangles(), "switched on again, they are back once");
            }
            finally { CollisionService.Clear(); }
        }

        /// <summary>A guard with a Rigidbody: its collider is a simulated body published to the character world (the
        /// _dynamic shapes) — the guard's character neither collides with it nor records a push against it.</summary>
        [Test]
        public static void OwnRigidbodyIsNoObstacle(TestContext t)
        {
            var s = new Scene { Name = "Body" };
            Floor(s);
            var guard = Add(s, null, "Guard", new Vector3(0, 0, 0), null);
            const long guardId = 21;
            try
            {
                CollisionService.Build(s);
                CollisionService.CharacterOwnerResolver = id => id == guardId ? guard : null;
                CollisionService.SetDynamicBodies(new List<CollisionService.DynamicBody>
                {
                    new CollisionService.DynamicBody
                    {
                        BodyId = 7, Owner = guard, Center = new Vector3(0, 0.9f, 0), HalfExtents = new Vector3(0.3f, 0.9f, 0.3f),
                        AxisX = new Vector3(1, 0, 0), AxisY = new Vector3(0, 1, 0), AxisZ = new Vector3(0, 0, 1)
                    }
                });
                CollisionService.CharacterContacts.Clear();
                var p = Fall(new Vector3(0, 0, 0), guardId, 10, out _);
                t.True(Math.Abs(p.Y) < 0.02f && Math.Abs(p.X) < 1e-3f && Math.Abs(p.Z) < 1e-3f, "the guard is not pushed by its own body (" + p.X + ", " + p.Y + ", " + p.Z + ")");
                t.Equal(0, CollisionService.CharacterContacts.Count, "no push against its own body is recorded");
            }
            finally { CollisionService.Clear(); CollisionService.CharacterOwnerResolver = null; }
        }
    }
}
