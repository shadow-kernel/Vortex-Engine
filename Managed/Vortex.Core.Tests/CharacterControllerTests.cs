using System;
using System.Collections.Generic;
using Editor.Core.Data;
using Editor.Core.Services.Physics;
using Editor.ECS;
using Editor.ECS.Components.Physics;

namespace VortexTests
{
    /// <summary>The managed character controller (Physics.MoveCharacter) against the collision world: the moving entity's
    /// own colliders (#417).</summary>
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
