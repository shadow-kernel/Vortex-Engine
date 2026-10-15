using System;
using Editor.Core.AI;
using Editor.Core.Data;
using Editor.Core.Services.AI;
using Editor.Core.Services.Physics;
using Editor.ECS;
using Editor.ECS.Components.AI;
using Editor.ECS.Components.Physics;

namespace VortexTests
{
    /// <summary>The AI looks at — and walks to — the body of a target that moves a character, not at its entity: a
    /// first-person player's entity is its camera, 1.6 m above the floor. Perception aimed its sight rays 25 / 60 / 95 % of
    /// 1.7 m above that point (2.0 – 3.2 m) and saw the player over walls up to ~2.6 m; MoveTo / IsNear measured to the
    /// camera. Targets without a character keep the old rule. The collision world answers the sight rays (no Jolt).</summary>
    public static class PerceptionTargetTests
    {
        private const float Dt = 0.1f;
        private const long PlayerCharacter = 77;

        /// <summary>A guard at the origin looking along +z (eye 1.6 m), the player 8 m ahead — its entity at its camera
        /// (<paramref name="entityHeight"/> above the floor), its script's 1.85 m capsule on the floor — and optionally a wall
        /// <paramref name="wallHeight"/> high, 6 m wide, at <paramref name="wallZ"/>.</summary>
        private static Scene Yard(out GameEntity guard, out GameEntity player, float wallHeight, float wallZ = 4f, float entityHeight = 1.6f)
        {
            var s = new Scene { Name = "Yard", IsActive = true };   // the playing scene: agents of an inactive one do not look
            PhysicsRaycastTests.Add(s, null, "Floor", new Vector3(0, -0.5f, 0), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(40f, 1f, 40f) }));
            guard = PhysicsRaycastTests.Add(s, null, "Guard", new Vector3(0, 0, 0), e =>
                e.AddComponentDirect(new AIPerception(e) { ViewDistance = 30f, UpdateInterval = 0f, SightConfirmTime = 0f }));
            player = PhysicsRaycastTests.Add(s, null, "Player", new Vector3(0, entityHeight, 8f), e => e.Tag = "Player");
            if (wallHeight > 0f)
                PhysicsRaycastTests.Add(s, null, "Wall", new Vector3(0, wallHeight * 0.5f, wallZ), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(6f, wallHeight, 0.3f) }));
            PhysicsRaycastTests.AsLoaded(s);
            return s;
        }

        /// <summary>Build the collision world, register the player's capsule (when <paramref name="capsule"/>), run perception
        /// a few checks and return whether the guard sees the player.</summary>
        private static bool Sees(Scene s, GameEntity guard, GameEntity player, bool capsule, float? targetHeight = null)
        {
            CollisionService.Build(s);
            if (capsule) CollisionService.RegisterCharacter(PlayerCharacter, new Vector3(0, 0, 8f), 0.35f, 1.85f, player);
            PerceptionService.Begin(s);
            if (targetHeight.HasValue) PerceptionService.SetTargetHeight(player, targetHeight.Value);
            for (int i = 0; i < 4; i++) PerceptionService.Tick(Dt);
            return PerceptionService.CanSee(guard, player);
        }

        private static void Done() { PerceptionService.End(); CollisionService.Clear(); }

        [Test]
        public static void SightAimsAtTheCapsuleNotTheCamera(TestContext t)
        {
            foreach (var (wall, expect, what) in new[]
            {
                (2.4f, false, "a 2.4 m wall hides the 1.85 m player (the rays used to pass over it at 2.0 - 3.2 m)"),
                (2.0f, false, "so does a 2 m wall"),
                (1.6f, true, "over a 1.6 m wall the player's head shows"),
                (0f, true, "in the open the guard sees the player"),
            })
            {
                var s = Yard(out var guard, out var player, wall);
                try { t.Equal(expect, Sees(s, guard, player, true), what); }
                finally { Done(); }
            }
            var open = Yard(out var g2, out var p2, 0f);
            try
            {
                t.True(Sees(open, g2, p2, true), "(seen)");
                t.True(PerceptionService.TryGetLastSeen(g2, -1f, out var at, out _, out var who) && who == p2 && Math.Abs(at.Y) < 0.01f && Math.Abs(at.Z - 8f) < 0.01f,
                       "the last seen position is where the player stands, not its camera (" + at + ")");
            }
            finally { Done(); }
        }

        /// <summary>SetTargetHeight is the target's height above its FEET — the capsule's bottom for a character. A prone
        /// player (0.5 m) behind a 0.8 m wall right in front of it is hidden; standing (the capsule's 1.85 m) its head shows.</summary>
        [Test]
        public static void TargetHeightIsMeasuredFromTheFeet(TestContext t)
        {
            var s = Yard(out var guard, out var player, 0.8f, 7f);
            try { t.True(Sees(s, guard, player, true), "standing, the head shows over the 0.8 m wall"); }
            finally { Done(); }
            s = Yard(out guard, out player, 0.8f, 7f);
            try { t.False(Sees(s, guard, player, true, 0.5f), "SetTargetHeight(player, 0.5) — prone — hides it"); }
            finally { Done(); }
        }

        /// <summary>An entity that moves no character keeps the old rule: its position + 25 / 60 / 95 % of its height.</summary>
        [Test]
        public static void TargetsWithoutACharacterKeepTheirPosition(TestContext t)
        {
            var s = Yard(out var guard, out var target, 2.4f, 4f, 0f);   // an NPC standing on the floor, no capsule
            try { t.False(Sees(s, guard, target, false), "a 2.4 m wall hides a target standing on the floor"); }
            finally { Done(); }
            s = Yard(out guard, out target, 0f, 4f, 0f);
            try
            {
                t.True(Sees(s, guard, target, false), "in the open it is seen");
                t.True(PerceptionService.TryGetLastSeen(guard, -1f, out var at, out _, out _) && Math.Abs(at.Y) < 0.01f, "its last seen position is its own (" + at + ")");
            }
            finally { Done(); }
            s = Yard(out guard, out target, 2.2f);   // at 1.6 m and no capsule: aimed from there, as before
            try { t.True(Sees(s, guard, target, false), "an entity floating at 1.6 m without a capsule is still aimed at from its position"); }
            finally { Done(); }
        }

        /// <summary>Physics.CharacterCapsule hands scripts the capsule (the bot's aim), and the behaviour-tree targets
        /// (MoveTo, IsNear, RandomPoint's centre) resolve an entity to the feet of its character: a guard 1 m from the player
        /// is near it, the 1.6 m to the camera no longer count.</summary>
        [Test]
        public static void TasksAndScriptsFindTheCharactersFeet(TestContext t)
        {
            var s = Yard(out var guard, out var player, 0f);
            var rt = Editor.Scripting.ScriptRuntime.Instance;
            try
            {
                rt.Begin(s);   // (the Scene API answers for the running game; Begin builds the collision world)
                long ph = rt.HandleForEntity(player), gh = rt.HandleForEntity(guard);
                t.False(Vortex.Physics.CharacterCapsule(ph, out var feet, out var r, out var h), "no capsule yet");
                t.True(Math.Abs(feet.Y - 1.6f) < 1e-4f && r == 0f && h == 0f, "… the entity's position instead (" + feet.Y + ")");
                CollisionService.RegisterCharacter(PlayerCharacter, new Vector3(0, 0, 8f), 0.35f, 1.85f, player);
                t.True(Vortex.Physics.CharacterCapsule(ph, out feet, out r, out h) && Math.Abs(feet.Y) < 1e-4f && Math.Abs(feet.Z - 8f) < 1e-4f
                       && Math.Abs(r - 0.35f) < 1e-4f && Math.Abs(h - 1.85f) < 1e-4f, "with one: its feet, radius and height");
                t.True(Vortex.Tasks.Targets.Feet(ph).Y < 1e-4f, "a task target resolves the player to its feet");

                var near = new BtNodeData { Kind = BtNodeKind.Condition, Task = "IsNear" };
                near.Params["target"] = "Target"; near.Params["distance"] = "1.2";
                var runner = BehaviorTreeRunner.Create(new BehaviorTreeAsset { Name = "near", Root = near }, "near.vbt", gh, BehaviorTreeService.ResolveTask, x => { });
                runner.Blackboard.Set("Target", ph);
                guard.Transform.LocalPosition = new Vector3(0, 0, 7f);   // 1 m from the player's feet, 1.89 m from its camera
                runner.Tick(Dt);
                t.Equal(Vortex.BtStatus.Success, runner.LastStatus, "IsNear 1.2 m: the guard 1 m from the player's feet is near it");
                guard.Transform.LocalPosition = new Vector3(0, 0, 6.5f);
                runner.Tick(Dt);
                t.Equal(Vortex.BtStatus.Failure, runner.LastStatus, "1.5 m away it is not");
            }
            finally { rt.End(); CollisionService.Clear(); }
        }
    
        /// <summary>#435 review: a capsule is its owner's body only while the owner moves it — a player who stopped calling
        /// MoveCharacter (got into a car, a cutscene) or was teleported by its Position is where its entity is: the guard does
        /// not keep seeing it at the old capsule behind the wall.</summary>
        [Test]
        public static void AStaleCapsuleIsNoBody(TestContext t)
        {
            var s = Yard(out var guard, out var player, 0f);
            PhysicsRaycastTests.Add(s, null, "Wall", new Vector3(10f, 2f, 14f), e => e.AddComponentDirect(new BoxCollider(e) { Size = new Vector3(6f, 4f, 0.3f) }));
            PhysicsRaycastTests.AsLoaded(s);
            try
            {
                t.True(Sees(s, guard, player, true), "standing 8 m ahead (its capsule moved just now) it is seen");
                t.True(CollisionService.TryGetCharacterOf(player, out var f0, out _, out _) && Math.Abs(f0.Z - 8f) < 1e-4f, "its body is the capsule");
                // it drives off behind the wall: its script no longer moves the capsule, the entity moves
                player.Transform.LocalPosition = new Vector3(10f, 1.6f, 18f);
                t.False(CollisionService.TryGetCharacterOf(player, out _, out _, out _), "away from its entity the old capsule is no body");
                for (int i = 0; i < 4; i++) PerceptionService.Tick(Dt);
                PerceptionService.TargetBody(player, out var body, out _);
                t.True(Math.Abs(body.X - 10f) < 1e-3f && Math.Abs(body.Z - 18f) < 1e-3f, "perception looks where the entity is (" + body + ")");
                t.False(PerceptionService.CanSee(guard, player), "… behind the wall: not seen");

                // a capsule not moved for a quarter of a second is no body either (its owner stands where the capsule is)
                player.Transform.LocalPosition = new Vector3(0, 1.6f, 8f);
                CollisionService.RegisterCharacter(PlayerCharacter, new Vector3(0, 0, 8f), 0.35f, 1.85f, player);
                t.True(CollisionService.TryGetCharacterOf(player, out _, out _, out _), "moved just now: its body");
                System.Threading.Thread.Sleep((int)(CollisionService.CharacterFreshSeconds * 1000f) + 60);
                t.False(CollisionService.TryGetCharacterOf(player, out _, out _, out _), "not moved for " + CollisionService.CharacterFreshSeconds + " s: no body");
            }
            finally { Done(); }
        }
    }
}
