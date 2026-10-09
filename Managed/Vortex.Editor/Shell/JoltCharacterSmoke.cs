using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Physics;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Physics;

namespace VortexEditor.Shell
{
    /// <summary>The character controller on Jolt's CharacterVirtual (#187): a capsule dropped onto a floor cube lands
    /// grounded, walks along it, stops at a wall cube and climbs a step no taller than the step height — all against
    /// the static bodies the physics build made from plain BoxColliders. Skipped on an engine build without Jolt.</summary>
    internal static class JoltCharacterSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("jolt character", Run);

        private const float Radius = 0.35f, Height = 1.85f, Dt = 1f / 60f;
        private const float Base = 500f;   // the rig floats far above the scene's own geometry — the capsule must only ever meet its cubes

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("jolt character: no scene — skipped"); return true; }
            if (!PhysicsService.Available) { log.Log("jolt character: no Jolt world in this engine build (" + Editor.DllWrapper.PhysicsNative.Reason + ") — skipped"); return true; }

            var made = new List<GameEntity>();
            bool ok = true;
            bool playing = false;
            try
            {
                // floor 20 × 1 × 20 with its top at y = Base, a wall at x = 3, a 0.25 m step at z = 3 (all plain BoxColliders)
                made.Add(Box("JoltFloor", new Vector3(0f, Base - 0.5f, 0f), new Vector3(20f, 1f, 20f)));
                made.Add(Box("JoltWall", new Vector3(3f, Base + 1.5f, 0f), new Vector3(1f, 3f, 4f)));
                made.Add(Box("JoltStep", new Vector3(0f, Base + 0.125f, 3f), new Vector3(4f, 0.25f, 1f)));
                foreach (var e in made) if (e == null) { log.LogError("jolt character: could not create the test cubes"); return false; }
                EditorViewportSession.RequestResubmit();
                await SmokeRegistry.Settle(300);

                EditorCommands.Play();
                playing = true;
                await SmokeRegistry.Settle(600);
                if (!PhysicsService.IsBuilt) { log.LogError("jolt character: the physics world was not built for play mode"); return false; }
                PhysicsService.JoltCharacters = true;

                // 1) drop: 1 m above the floor, gravity-style displacement each tick → grounded at y ≈ 0
                var feet = new Vector3(0f, Base + 1f, 0f);
                bool grounded = false;
                for (int i = 0; i < 90 && !grounded; i++)
                    feet = Move(feet, new Vector3(0f, -0.1f, 0f), out grounded);
                log.Log("jolt character: dropped to " + F(feet) + (grounded ? " grounded" : " NOT grounded"));
                if (!grounded || Math.Abs(feet.Y - Base) > 0.08f) { log.LogError("jolt character: the capsule did not land on the floor cube (y=" + feet.Y.ToString("0.000") + ")"); ok = false; }

                // 2) walk +X into the wall: the capsule stops at the wall face minus its radius
                for (int i = 0; i < 60; i++) feet = Move(feet, new Vector3(0.1f, -0.05f, 0f), out grounded);
                float wallX = 3f - 0.5f - Radius;
                log.Log("jolt character: walked into the wall, stopped at " + F(feet) + " (face at x=" + wallX.ToString("0.00") + ")" + (grounded ? " grounded" : " NOT grounded"));
                if (feet.X > wallX + 0.06f || feet.X < wallX - 0.3f) { log.LogError("jolt character: the wall did not stop the capsule (x=" + feet.X.ToString("0.000") + ")"); ok = false; }
                if (!grounded || Math.Abs(feet.Y - Base) > 0.08f) { log.LogError("jolt character: lost the ground while walking (y=" + feet.Y.ToString("0.000") + ")"); ok = false; }

                // 3) walk +Z onto the 0.25 m step (step height 0.35, the step is 1 m deep at z = 2.5..3.5): 30 ticks of
                //    0.1 m end with the capsule standing on top of it at z ≈ 3
                feet = new Vector3(0f, Base, 0f);   // teleport back to the middle
                for (int i = 0; i < 30; i++) feet = Move(feet, new Vector3(0f, -0.05f, 0.1f), out grounded);
                log.Log("jolt character: walked onto the step, now at " + F(feet) + (grounded ? " grounded" : " NOT grounded"));
                if (feet.Z < 2.7f || feet.Z > 3.3f || Math.Abs(feet.Y - Base - 0.25f) > 0.08f || !grounded) { log.LogError("jolt character: the capsule did not climb the 0.25 m step (z=" + feet.Z.ToString("0.00") + ", y=" + feet.Y.ToString("0.000") + ")"); ok = false; }
                if (PhysicsService.JoltCharacterCount != 1) { log.LogError("jolt character: expected one live Jolt character, found " + PhysicsService.JoltCharacterCount); ok = false; }
            }
            finally
            {
                if (playing) { EditorCommands.Stop(); await SmokeRegistry.Settle(300); }
                if (PhysicsService.JoltCharacterCount != 0) { log.LogError("jolt character: Stop left " + PhysicsService.JoltCharacterCount + " Jolt characters alive"); ok = false; }
                var alive = new List<GameEntity>();
                foreach (var e in made) if (e != null) alive.Add(e);
                if (alive.Count > 0) EditorCommands.DeleteEntities(alive);
                EditorViewportSession.RequestResubmit();
            }
            return ok;
        }

        private static Vector3 Move(Vector3 feet, Vector3 disp, out bool grounded)
            => PhysicsService.MoveCharacterJolt(feet, Radius, Height, disp, Dt, out grounded, 4242, CollisionService.CharacterStepHeight, CollisionService.CharacterMaxSlopeDeg);

        private static GameEntity Box(string name, Vector3 pos, Vector3 scale)
        {
            var e = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
            if (e?.Transform == null) return null;
            e.Name = name;
            e.Transform.LocalPosition = pos;
            e.Transform.LocalScale = scale;
            e.AddComponent(new BoxCollider(e));
            return e;
        }

        private static string F(Vector3 v) => "(" + v.X.ToString("0.00") + ", " + v.Y.ToString("0.00") + ", " + v.Z.ToString("0.00") + ")";
    }
}
