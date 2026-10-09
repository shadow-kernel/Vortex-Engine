using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Decals;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>Projected decals on the real backend (#120): a green wall 6 m in front of the editor camera gets a red Decal
    /// component projected onto it (box 2 × 2 m, the entity's +Y turned towards the wall) — the centre of the frame must turn
    /// red; the spawned-decal bookkeeping (lifetime, expiry) is checked without the renderer.</summary>
    internal static class DecalSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("decals", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("decals: no scene — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            try
            {
                var wall = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (wall?.Transform == null) { log.LogError("decals: no cube"); return false; }
                made.Add(wall);
                wall.Name = "SmokeDecalWall";
                wall.Transform.LocalPosition = new Vector3(0, 500, 6);
                wall.Transform.LocalScale = new Vector3(8, 8, 1);
                var mr = wall.GetComponent<MeshRenderer>();
                if (mr != null) { mr.ColorR = 0.1f; mr.ColorG = 0.8f; mr.ColorB = 0.1f; }
                cam.SetPositionAndRotation(0, 500, 0, 0, 0);   // looking down +Z at the wall's front face (z = 5.5)
                EditorViewportSession.RequestResubmit();
                var before = await CameraSkySmoke.Sample("decal_off.bmp", 0.4, 0.4);   // inside the decal square, off the selection gizmo at the centre

                var holder = new GameEntity(scene, "SmokeDecal");   // an empty entity: only the decal, no mesh in front of the wall
                scene.AddEntity(holder);
                if (holder.Transform == null) { log.LogError("decals: no holder transform"); return false; }
                made.Add(holder);
                holder.Transform.LocalPosition = new Vector3(0, 500, 5.5f);
                holder.Transform.LocalRotation = new Vector3(-90, 0, 0);   // local +Y -> world -Z: project onto the wall
                holder.Transform.LocalScale = new Vector3(1, 1, 1);
                var decal = new Decal(holder) { Size = new Vector3(2f, 0.6f, 2f), ColorR = 1f, ColorG = 0.05f, ColorB = 0.05f, Opacity = 1f, Blend = 0, AngleFade = 0.5f };
                holder.AddComponent(decal);
                EditorViewportSession.RequestResubmit();
                var after = await CameraSkySmoke.Sample("decal_on.bmp", 0.4, 0.4);

                // spawned decals: pure bookkeeping
                DecalService.Clear();
                long id = DecalService.Spawn("", new System.Numerics.Vector3(0, 500, 5.5f), new System.Numerics.Vector3(0, 0, -1), new System.Numerics.Vector3(1, 0.3f, 1), 0.2f, 0f);
                bool spawnedAlive = DecalService.IsAlive(id) && DecalService.SpawnedCount == 1;
                DecalService.Tick(0.5f);
                bool expired = !DecalService.IsAlive(id) && DecalService.SpawnedCount == 0;

                log.Log("decals: wall sample " + CameraSkySmoke.Rgb(before) + " -> " + CameraSkySmoke.Rgb(after) + " with a red decal; scene decals submitted " + DecalService.SceneDecalCount
                    + ", renderer holds " + Editor.DllWrapper.VortexAPI.SubmittedDecalCount() + ", spawned lifetime " + (spawnedAlive && expired ? "ok" : "BROKEN"));
                bool red = after.r >= 0 && after.r > before.r + 40 && after.g < before.g - 20;
                if (DecalService.SceneDecalCount < 1) log.LogError("decals: the Decal component was not submitted with the scene");
                if (!red) log.LogError("decals: the decal is not visible on the wall (no red at the centre)");
                if (!(spawnedAlive && expired)) log.LogError("decals: spawned decal lifetime bookkeeping is broken");
                return red && DecalService.SceneDecalCount >= 1 && spawnedAlive && expired;
            }
            finally
            {
                DecalService.Clear();
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }
    }
}
