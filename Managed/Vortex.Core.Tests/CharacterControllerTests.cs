using System;
using System.Collections.Generic;
using Editor.Core.Data;
using Editor.Core.Services.Physics;
using Editor.ECS;
using Editor.ECS.Components.Physics;

namespace VortexTests
{
    /// <summary>The managed character controller (Physics.MoveCharacter) against the collision world: the moving entity's
    /// own colliders (#417), the lifetime of registered characters (#407), inactive level parts (#443), rotated colliders
    /// (#498).</summary>
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

        private static Vector3 Fall(Vector3 feet, long id, int frames, out bool grounded, GameEntity owner = null)
        {
            grounded = false;
            for (int i = 0; i < frames; i++) feet = CollisionService.MoveCharacter(feet, R, H, new Vector3(0, -9.81f * Dt, 0), out grounded, id, owner: owner);
            return feet;
        }

        [Test]
        public static void OwnCollidersAreNoObstacle(TestContext t)
        {
            var s = new Scene { Name = "Guards" };
            Floor(s);
            var guard = Guard(s, out _);
            const long guardId = 21, otherId = 22, unknownId = 99;   // 21 is the guard's handle (its script passes it); 99 is custom
            try
            {
                CollisionService.Build(s);

                // the bug: a character without an owner collides with the collider it stands in — shoved up out of it
                var anon = Fall(new Vector3(0, 0, 0), unknownId, 30, out _);
                t.True(anon.Y > 0.3f, "without an owner the capsule is pushed out of the collider it starts in (y = " + anon.Y + ")");
                CollisionService.RemoveCharacter(unknownId);

                // the guard's own id: its capsule collider and its hitbox child are no obstacles
                var p = Fall(new Vector3(0, 0, 0), guardId, 30, out bool grounded, guard);
                t.True(Math.Abs(p.Y) < 0.02f && Math.Abs(p.X) < 1e-3f && Math.Abs(p.Z) < 1e-3f && grounded,
                       "the guard stays on the floor inside its own collider (" + p.X + ", " + p.Y + ", " + p.Z + ")");
                p = CollisionService.MoveCharacter(p, R, H, new Vector3(0.1f, -0.01f, 0.05f), out _, guardId, owner: guard);
                t.True(Math.Abs(p.X - 0.1f) < 1e-3f && Math.Abs(p.Z - 0.05f) < 1e-3f, "… and walks out of it freely (" + p.X + ", " + p.Z + ")");
                t.False(CollisionService.CapsuleOverlaps(new Vector3(0, 0.02f, 0), R, H - 0.02f, guardId), "CanStand ignores the guard's own collider (its registered owner)");
                t.True(CollisionService.CapsuleOverlaps(new Vector3(0, 0.02f, 0), R, H - 0.02f, otherId), "… but not for anyone else");

                // other characters and rays still collide with the guard's collider (its capsule registration removed,
                // so only the collider can stop them)
                CollisionService.RemoveCharacter(guardId);
                t.False(CollisionService.CapsuleOverlaps(new Vector3(0, 0.02f, 0), R, H - 0.02f, 0, owner: guard), "an explicit owner skips its colliders without a registration");
                var q = CollisionService.MoveCharacter(new Vector3(2f, 0, 0), R, H, new Vector3(-3f, 0, 0), out _, otherId);
                t.True(q.X > 2 * R - 0.05f, "another character is stopped by the guard's collider (x = " + q.X + ")");
                t.True(CollisionService.Raycast(new Vector3(0, 1f, -3f), new Vector3(0, 0, 1), 10f, ~0, out _, out _, out var hit, out _) && hit == guard,
                       "a ray still hits the guard's collider");
            }
            finally { CollisionService.Clear(); }
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
                Fall(new Vector3(0, 0, 0), guardId, 2, out _, guard);
                CollisionService.RegisterCharacter(otherId, new Vector3(0.5f, 0, 0), R, H);   // another character pressing into the guard's collider
                CollisionService.StepEvents(enter, stay, exit, collide);
                foreach (var c in collide)
                    t.False(c.CharacterId == guardId && (c.Other == guard || c.Other == hitbox), "no OnCollisionEnter of the guard against its own " + c.Other.Name);
                t.True(collide.Exists(c => c.CharacterId == guardId && c.Other?.Name == "Floor"), "the guard still touches the floor");
                t.True(collide.Exists(c => c.CharacterId == otherId && c.Other == guard), "another character touching the guard's collider gets OnCollisionEnter");
            }
            finally { CollisionService.Clear(); }
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
                Fall(new Vector3(0, 0, 0), hg, 2, out _, guard);
                var b = CollisionService.MoveCharacter(new Vector3(2f, 0, 0), R, H, new Vector3(-3f, 0, 0), out _, hb);
                t.True(b.X > 2 * R - 0.05f, "the guard's capsule blocks another character (x = " + b.X + ")");

                t.True(PhysicsService.RemoveCharacter(hg), "Physics.RemoveCharacter removes the guard");
                t.False(PhysicsService.RemoveCharacter(hg), "… and reports false when nothing is registered");
                b = CollisionService.MoveCharacter(b, R, H, new Vector3(-3f, 0, 0), out _, hb);
                t.True(b.X < -1f, "the other character walks through the spot (x = " + b.X + ")");
                t.False(CollisionService.CapsuleOverlaps(new Vector3(0, 0.02f, 0), R, H - 0.02f, anonymous), "CanStand at the old spot");

                Fall(new Vector3(0, 0, 0), hg, 1, out _, guard);
                t.True(CollisionService.HasCharacter(hg), "the next MoveCharacter registers the guard again");
                b = CollisionService.MoveCharacter(b, R, H, new Vector3(3f, 0, 0), out _, hb);
                t.True(b.X < -2 * R + 0.05f, "… and it blocks again (x = " + b.X + ")");

                CollisionService.RegisterCharacter(anonymous, new Vector3(-5f, 0, 0), R, H);
                t.Equal(1, PhysicsService.RemoveCharactersIn(guard), "destroying / deactivating the guard removes its character");
                t.False(CollisionService.HasCharacter(hg), "… the guard's capsule is gone");
                t.True(CollisionService.HasCharacter(anonymous), "… a character without an owner stays");

                Fall(new Vector3(0, 0, 0), hg, 1, out _, guard);
                t.Equal(1, PhysicsService.RemoveCharacterOf(model), "a ragdoll of the model removes the capsule its root registered");
                t.False(CollisionService.HasCharacter(hg), "… the guard's capsule is gone");

                CollisionService.RegisterCharacter(hcar, new Vector3(10f, 0, 0), 1f, 2f, car);
                CollisionService.RegisterCharacter(hp, new Vector3(10f, 0.5f, 0), R, H, passenger);
                t.Equal(1, PhysicsService.RemoveCharacterOf(passengerModel), "a passenger's ragdoll removes the nearest character above it");
                t.False(CollisionService.HasCharacter(hp), "… the passenger's");
                t.True(CollisionService.HasCharacter(hcar), "… not the car's");
            }
            finally { CollisionService.Clear(); }
        }

        /// <summary>#407: a behaviour keeps running (and calling MoveCharacter) while its entity is switched off, and the
        /// root of a ragdolled character keeps moving its capsule — neither may bring back the capsule that SetActive(false)
        /// / Ragdoll.Activate removed. The move itself is still resolved; characters without an owner are unaffected.</summary>
        [Test]
        public static void InactiveOrLimpOwnersRegisterNoCharacter(TestContext t)
        {
            var s = new Scene { Name = "Limp" };
            Floor(s);
            var squad = Add(s, null, "Squad", new Vector3(0, 0, 0), null);
            var guard = Add(s, squad, "Guard", new Vector3(0, 0, 0), null);
            var model = Add(s, guard, "GuardModel", new Vector3(0, 0, 0), null);
            const long gid = 21, custom = 7;
            try
            {
                CollisionService.Build(s);
                guard.SetActiveFlagOnly(false);
                var p = Fall(new Vector3(0, 0.5f, 0), gid, 30, out bool grounded, guard);
                t.False(CollisionService.HasCharacter(gid), "a switched-off owner registers no capsule");
                t.True(Math.Abs(p.Y) < 0.02f && grounded, "… while its move is still resolved (y = " + p.Y + ")");
                CollisionService.RegisterCharacter(gid, p, R, H, guard);
                t.False(CollisionService.HasCharacter(gid), "… nor through RegisterCharacter (the Jolt route)");
                guard.SetActiveFlagOnly(true);
                squad.SetActiveFlagOnly(false);
                Fall(p, gid, 1, out _, guard);
                t.False(CollisionService.HasCharacter(gid), "an owner under a switched-off ancestor registers none");
                squad.SetActiveFlagOnly(true);

                // Ragdoll.Activate on the model removed the capsule; the root's script goes on calling MoveCharacter
                RagdollService.TestMarkActive(model, true);
                Fall(p, gid, 3, out _, guard);
                t.False(CollisionService.HasCharacter(gid), "an owner with an active ragdoll under it registers none");
                t.True(CollisionService.MayRegister(null), "a character without an owner always may");
                Fall(p, custom, 1, out _);
                t.True(CollisionService.HasCharacter(custom), "… and registers as before");
                RagdollService.TestMarkActive(model, false);
                Fall(p, gid, 1, out _, guard);
                t.True(CollisionService.HasCharacter(gid), "once the ragdoll is over the owner's next move registers it again");
            }
            finally { RagdollService.TestMarkActive(model, false); CollisionService.Clear(); }
        }

        // Scripts for the owner rules through the real runtime: a custom characterId, a script's own EntityId (from Update,
        // from Physics.CanStand, from an OnMessage of a second script on the same entity).
        private const string OwnerScripts = @"
using Vortex;

public class LevelScript : VortexBehaviour { }

public class CustomIdWalker : VortexBehaviour
{
    public Vector3 Feet = new Vector3(-5f, 0.3f, 0f);
    public bool Grounded;
    public override void Update(float dt)
    {
        Feet = Physics.MoveCharacter(Feet, 0.35f, 1.8f, new Vector3(0f, -0.1f, 0f), 1);   // a custom id
        Grounded = Physics.Grounded;
    }
}

public class OwnIdWalker : VortexBehaviour
{
    public Vector3 Feet;
    public bool Room;
    public override void Start() { Feet = Position; }
    public override void Update(float dt)
    {
        Feet = Physics.MoveCharacter(Feet, 0.35f, 1.8f, new Vector3(0f, -0.1f, 0f), EntityId);
        Room = Physics.CanStand(Feet, 0.35f, 1.8f, EntityId);
    }
}

public class Sidekick : VortexBehaviour
{
    public Vector3 Feet;
    public override void Start() { Feet = Position; }
    public override void OnMessage(string message, object arg)
    {
        if (message == ""step"") Feet = Physics.MoveCharacter(Feet, 0.35f, 1.8f, new Vector3(0f, -0.1f, 0f), EntityId);
    }
}
";

        private static object Field(object o, string name) => o.GetType().GetField(name).GetValue(o);

        private static GameEntity Scripted(Scene s, GameEntity parent, string name, Vector3 pos, bool capsule, params string[] classes)
            => Add(s, parent, name, pos, e =>
            {
                if (capsule) e.AddComponentDirect(new CapsuleCollider(e) { Radius = R, Height = H, Center = new Vector3(0, 0.9f, 0) });
                foreach (var cls in classes) e.AddComponentDirect(new Editor.ECS.Components.Scripting.Script(e, "Assets/Scripts/" + cls + ".cs"));
            });

        /// <summary>#406 review: a characterId names an entity only when it is the EntityId of the script that is running
        /// (and from then on the capsule keeps it) — a custom id equal to some other entity's handle (1 = the scripted
        /// "Level" root here) used to make that entity the owner, its floor child no obstacle, and the capsule fell through
        /// the floor.</summary>
        [Test]
        public static void CustomCharacterIdsBelongToNoEntity(TestContext t)
        {
            var file = t.Write("Scripts/Owners.cs", OwnerScripts);
            var asm = Editor.Scripting.RoslynScriptCompiler.Compile(new[] { file }, out var log);
            if (asm == null) throw new TestFailure("the test scripts do not compile: " + log);
            var s = new Scene { Name = "Owners" };
            var level = Scripted(s, null, "Level", new Vector3(0, 0, 0), false, "LevelScript");
            Add(s, level, "Floor", new Vector3(0, -0.5f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(40f, 1f, 40f) }));
            var player = Scripted(s, null, "Player", new Vector3(-5f, 0, 0), false, "CustomIdWalker");
            var guard = Scripted(s, null, "Guard", new Vector3(5f, 0, 0), true, "OwnIdWalker");
            var twin = Scripted(s, null, "Twin", new Vector3(10f, 0, 0), true, "LevelScript", "Sidekick");
            var statue = Scripted(s, null, "Statue", new Vector3(15f, 0, 0), true, "LevelScript");   // scripted, never moves a capsule
            var rt = Editor.Scripting.ScriptRuntime.Instance;
            try
            {
                rt.Begin(s, asm);
                t.Equal(1L, rt.HandleForEntity(level), "the Level root holds handle 1 — the walker's custom id");
                for (int i = 0; i < 30; i++) rt.Update(Dt);

                var walker = rt.BehavioursOf(player)[0];
                var feet = (Vortex.Vector3)Field(walker, "Feet");
                t.True(Math.Abs(feet.Y) < 0.02f && (bool)Field(walker, "Grounded"), "a custom id 1 stands on the Level's floor (y = " + feet.Y + ")");
                var owned = new List<long>();
                CollisionService.CharacterIdsOwnedBy(o => o == level, owned);
                t.Equal(0, owned.Count, "… and its capsule does not belong to the Level");

                var own = rt.BehavioursOf(guard)[0];
                feet = (Vortex.Vector3)Field(own, "Feet");
                t.True(Math.Abs(feet.Y) < 0.02f && Math.Abs(feet.X - 5f) < 1e-3f && Math.Abs(feet.Z) < 1e-3f,
                       "a script's own EntityId: the guard stays inside its own collider (" + feet.X + ", " + feet.Y + ", " + feet.Z + ")");
                t.True((bool)Field(own, "Room"), "… Physics.CanStand with its EntityId ignores its own collider");
                owned.Clear(); CollisionService.CharacterIdsOwnedBy(o => o == guard, owned);
                t.True(owned.Count == 1 && owned[0] == own.EntityId, "… and the capsule belongs to the guard");

                // the second script of an entity, from OnMessage: the shared EntityId names the entity too
                long twinId = rt.BehavioursOf(twin)[0].EntityId;
                for (int i = 0; i < 10; i++) rt.SendEntityMessage(twinId, "step", null);
                feet = (Vortex.Vector3)Field(rt.BehavioursOf(twin)[1], "Feet");
                t.True(Math.Abs(feet.Y) < 0.02f && Math.Abs(feet.X - 10f) < 1e-3f, "a second script of an entity, from OnMessage, owns the capsule too (" + feet.X + ", " + feet.Y + ")");

                // outside any script callback an entity's id names nothing by itself: the statue's capsule (never moved by the
                // statue's own code) is shoved out of the statue's collider ...
                long statueId = rt.BehavioursOf(statue)[0].EntityId;
                var outside = new Vortex.Vector3(15f, 0f, 0f);
                for (int i = 0; i < 10; i++) outside = Vortex.Physics.MoveCharacter(outside, R, H, new Vortex.Vector3(0f, -0.1f, 0f), statueId);
                t.True(outside.Y > 0.3f || Math.Abs(outside.X - 15f) > 0.3f || Math.Abs(outside.Z) > 0.3f,
                       "called from outside a script, the statue's id names no owner (" + outside.X + ", " + outside.Y + ", " + outside.Z + ")");
                // ... while the guard's capsule, which already belongs to the guard, keeps it (#406 review, round two)
                var kept = new Vortex.Vector3(5f, 0f, 0f);
                for (int i = 0; i < 10; i++) kept = Vortex.Physics.MoveCharacter(kept, R, H, new Vortex.Vector3(0f, -0.1f, 0f), own.EntityId);
                t.True(Math.Abs(kept.Y) < 0.02f && Math.Abs(kept.X - 5f) < 1e-3f && Math.Abs(kept.Z) < 1e-3f,
                       "moved from outside a script, the guard's capsule keeps the guard (" + kept.X + ", " + kept.Y + ", " + kept.Z + ")");

                // #418 without a Jolt world: Physics.OverlapPoint answers from the collision world
                var inGuard = Vortex.Physics.OverlapPoint(new Vortex.Vector3(5f, 1f, 0.1f));
                t.True(inGuard.Length == 1 && inGuard[0] == own.EntityId, "Physics.OverlapPoint without Jolt finds the guard's collider (" + inGuard.Length + ")");
            }
            finally { rt.End(); }
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

        /// <summary>Paddi's ramp (#498): a 3 x 0.5 x 12 m BoxCollider pitched by <paramref name="deg"/> about X, rising toward
        /// -z, the foot of its top face on a floor (top y 500) at z 210.</summary>
        private static Scene RampScene(float deg, out GameEntity ramp)
        {
            var s = new Scene { Name = "Ramp" + deg };
            Add(s, null, "Floor", new Vector3(1300f, 499.8f, 200f), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(100f, 0.4f, 100f) }));
            double r = deg * Math.PI / 180.0; float sin = (float)Math.Sin(r), cos = (float)Math.Cos(r);
            ramp = Add(s, null, "Ramp", new Vector3(1270f, 500f + 6f * sin - 0.25f * cos, 210f - 6f * cos - 0.25f * sin), e =>
            {
                e.Transform.LocalRotation = new Vector3(deg, 0, 0);
                e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(3f, 0.5f, 12f) });
            });
            return s;
        }

        /// <summary>Paddi's repro: 200 moves of (0, -0.05, -0.06) toward the ramp.</summary>
        private static Vector3 WalkUp(Vector3 f)
        {
            for (int k = 0; k < 200; k++) f = CollisionService.MoveCharacter(f, 0.3f, 1.8f, new Vector3(0f, -0.05f, -0.06f), out _, 0);
            return f;
        }

        /// <summary>#498: a pitched BoxCollider is an oriented box for the character too — the 10–30° ramps of the report
        /// (walls or walk-through ghosts while only the yaw was kept) are walked up and stood on, a 60° one blocks.</summary>
        [Test]
        public static void PitchedBoxRampsAreWalkedUp(TestContext t)
        {
            foreach (float deg in new[] { 10f, 16f, 20f, 24f, 26f, 30f })
            {
                var s = RampScene(deg, out _);
                try
                {
                    CollisionService.Build(s);
                    var f = WalkUp(new Vector3(1270f, 500f, 212.5f));
                    float surface = 500f + (210f - f.Z) * (float)Math.Tan(deg * Math.PI / 180.0);   // the top face under the capsule
                    t.True(f.Z < 201f, deg + "°: the capsule walked up the ramp (z = " + f.Z + ")");
                    t.True(f.Y > surface - 0.02f && f.Y < surface + 0.1f, deg + "°: it stands on the slope, not in it (y = " + f.Y + ", surface " + surface + ")");
                }
                finally { CollisionService.Clear(); }
            }
            var steep = RampScene(60f, out _);
            try
            {
                CollisionService.Build(steep);
                var f = WalkUp(new Vector3(1270f, 500f, 212.5f));
                t.True(f.Z > 209.5f && f.Y < 500.4f, "60° (steeper than the 50° limit) blocks the capsule at the foot (" + f.Y + ", " + f.Z + ")");
            }
            finally { CollisionService.Clear(); }
        }

        /// <summary>#498: under a 30° ramp's high end there is head room; walking toward the foot the capsule is stopped by
        /// the ramp's underside (the yaw-only slab floated above a 1.8 m capsule there).</summary>
        [Test]
        public static void PitchedRampUndersideBlocks(TestContext t)
        {
            var s = RampScene(30f, out _);
            try
            {
                CollisionService.Build(s);
                var f = new Vector3(1270f, 500f, 202f);
                for (int k = 0; k < 100; k++) f = CollisionService.MoveCharacter(f, 0.3f, 1.8f, new Vector3(0f, -0.05f, 0.06f), out _, 0);
                t.True(f.Z < 206.5f && Math.Abs(f.Y - 500f) < 0.05f, "the ramp's underside stops the capsule (z = " + f.Z + ", y = " + f.Y + ")");
            }
            finally { CollisionService.Clear(); }
        }

        /// <summary>#498: roll and yaw · pitch compose like the renderer (Rz · Rx · Ry), and a rotated parent places and turns
        /// its children: what the character world holds is where the entities are drawn.</summary>
        [Test]
        public static void RotatedCollidersMatchTheirWorldPose(TestContext t)
        {
            // a 20° roll about Z: rises toward +x
            var s = new Scene { Name = "Roll" };
            Add(s, null, "Floor", new Vector3(0, -0.2f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(60f, 0.4f, 60f) }));
            double r20 = 20.0 * Math.PI / 180.0;
            Add(s, null, "RollRamp", new Vector3(6f * (float)Math.Cos(r20) + 0.25f * (float)Math.Sin(r20), 6f * (float)Math.Sin(r20) - 0.25f * (float)Math.Cos(r20), 0), e =>
            {
                e.Transform.LocalRotation = new Vector3(0, 0, 20f);
                e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(12f, 0.5f, 3f) });
            });
            // pitch 20° then yaw 90°: rises toward -x, its foot at x -10
            Add(s, null, "YawPitchRamp", new Vector3(-10f - 6f * (float)Math.Cos(r20) - 0.25f * (float)Math.Sin(r20), 6f * (float)Math.Sin(r20) - 0.25f * (float)Math.Cos(r20), 20f), e =>
            {
                e.Transform.LocalRotation = new Vector3(20f, 90f, 0);
                e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(3f, 0.5f, 12f) });
            });
            // a parent rolled 30° carries a small box 2 m along its local x
            var parent = Add(s, null, "Tilted", new Vector3(0, 0, -20f), e => e.Transform.LocalRotation = new Vector3(0, 0, 30f));
            Add(s, parent, "Knob", new Vector3(2f, 0, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(0.2f, 0.2f, 0.2f) }));
            try
            {
                CollisionService.Build(s);
                var f = new Vector3(-2f, 0, 0);
                for (int k = 0; k < 150; k++) f = CollisionService.MoveCharacter(f, 0.3f, 1.8f, new Vector3(0.06f, -0.05f, 0f), out _, 0);
                float surface = f.X * (float)Math.Tan(r20);
                t.True(f.X > 6f && f.Y > surface - 0.02f && f.Y < surface + 0.1f, "up the rolled ramp along +x (" + f.X + ", " + f.Y + ", surface " + surface + ")");

                f = new Vector3(-8f, 0, 20f);
                for (int k = 0; k < 150; k++) f = CollisionService.MoveCharacter(f, 0.3f, 1.8f, new Vector3(-0.06f, -0.05f, 0f), out _, 0);
                surface = (-10f - f.X) * (float)Math.Tan(r20);
                t.True(f.X < -14f && f.Y > surface - 0.02f && f.Y < surface + 0.1f, "up the yawed + pitched ramp along -x (" + f.X + ", " + f.Y + ", surface " + surface + ")");

                // the knob sits at (2 cos 30°, 2 sin 30°) = (1.73, 1.0), turned with its parent
                t.True(CollisionService.RaycastDown(new Vector3(1.732f, 5f, -20f), 10f, out var hit, out _) && Math.Abs(hit.Y - (1f + 0.1f / (float)Math.Cos(Math.PI / 6))) < 0.02f,
                       "a rotated parent places its child's collider (hit y = " + hit.Y + ")");
                t.False(CollisionService.RaycastDown(new Vector3(2f, 5f, -20f), 4.5f, out _, out _), "… not where a yaw-only placement put it");
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
                CollisionService.SetDynamicBodies(new List<CollisionService.DynamicBody>
                {
                    new CollisionService.DynamicBody
                    {
                        BodyId = 7, Owner = guard, Center = new Vector3(0, 0.9f, 0), HalfExtents = new Vector3(0.3f, 0.9f, 0.3f),
                        AxisX = new Vector3(1, 0, 0), AxisY = new Vector3(0, 1, 0), AxisZ = new Vector3(0, 0, 1)
                    }
                });
                CollisionService.CharacterContacts.Clear();
                var p = Fall(new Vector3(0, 0, 0), guardId, 10, out _, guard);
                t.True(Math.Abs(p.Y) < 0.02f && Math.Abs(p.X) < 1e-3f && Math.Abs(p.Z) < 1e-3f, "the guard is not pushed by its own body (" + p.X + ", " + p.Y + ", " + p.Z + ")");
                t.Equal(0, CollisionService.CharacterContacts.Count, "no push against its own body is recorded");
            }
            finally { CollisionService.Clear(); }
        }
    }
}
