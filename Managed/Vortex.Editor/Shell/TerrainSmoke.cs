using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Terrain;
using Editor.Core.Terrain;
using Editor.Core.Viewport;
using Editor.ECS;

namespace VortexEditor.Shell
{
    /// <summary>The heightfield terrain on the real backend (#124): a 64 m terrain with a 6 m hill in front of the editor camera
    /// must draw its LOD chunks (the lower frame turns from sky to ground), the height / raycast queries must find the hill,
    /// a runtime crater must lower it, and the script API must agree.</summary>
    internal static class TerrainSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("terrain", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("terrain: no scene — skipped"); return true; }
            if (!Editor.DllWrapper.VortexAPI.TerrainApiAvailable) { log.LogError("terrain: the engine library has no terrain exports"); return false; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            try
            {
                // the camera looks down +Z from (0, 504, -20): sky before the terrain exists
                cam.SetPositionAndRotation(0, 504, -20, 0, 0);
                EditorViewportSession.RequestResubmit();
                var before = await CameraSkySmoke.Sample("terrain_off.bmp", 0.5, 0.7);

                var e = new GameEntity(scene, "SmokeTerrain");
                scene.AddEntity(e);
                if (e.Transform == null) { log.LogError("terrain: no transform"); return false; }
                made.Add(e);
                e.Transform.LocalPosition = new Vector3(-32f, 498f, -32f);   // spans x, z in [-32, 32], base at y = 498
                var terrain = new Editor.ECS.Components.Rendering.Terrain(e) { Size = 64f, Resolution = 65, Collision = true, LodDistance = 30f };
                e.AddComponent(terrain);

                // a 6 m hill at the centre, layer 2 painted on its top
                TerrainData data; float cell;
                if (!TerrainService.TryGetData(e, out data, out cell)) { log.LogError("terrain: no data"); return false; }
                var rect = data.Raise(32f, 32f, 14f, 6f, 0.3f);
                rect = rect.Union(data.Paint(32f, 32f, 8f, 1, 1f));
                TerrainService.MarkDirty(e, rect, true, true);
                EditorViewportSession.RequestResubmit();
                await Task.Delay(400);
                EditorViewportSession.RequestResubmit();
                var after = await CameraSkySmoke.Sample("terrain_on.bmp", 0.5, 0.7);
                int chunks = TerrainService.LastChunkSubmits;
                int meshes0, materials0, textures0; Editor.DllWrapper.VortexAPI.TryGetResourceCounts(out meshes0, out materials0, out textures0);

                // the splat map drives the layers: paint everything with layer 1 (the brown default) — the ground must change colour
                var whole = new SampleRect { X0 = 0, Z0 = 0, X1 = data.Resolution - 1, Z1 = data.Resolution - 1 };
                for (int i = 0; i < data.Splat.Length; i += 4) { data.Splat[i] = 0; data.Splat[i + 1] = 255; data.Splat[i + 2] = 0; data.Splat[i + 3] = 0; }
                TerrainService.MarkDirty(e, whole, false, true);
                EditorViewportSession.RequestResubmit();
                await Task.Delay(250);
                EditorViewportSession.RequestResubmit();
                var painted = await CameraSkySmoke.Sample("terrain_painted.bmp", 0.5, 0.7);

                float h; bool hOk = TerrainService.TryHeight(e, 0f, 0f, out h);
                System.Numerics.Vector3 hit; GameEntity hitTerrain;
                bool rayOk = TerrainService.Raycast(new System.Numerics.Vector3(0f, 520f, 0f), new System.Numerics.Vector3(0f, -1f, 0f), 100f, out hit, out hitTerrain);
                float apiH = Vortex.Terrain.Height(0f, 0f);
                int layer = Vortex.Terrain.LayerAt(0f, 0f);
                bool crater = Vortex.Terrain.Deform(new Vortex.Vector3(0f, 504f, 0f), 3f, 2f);
                float afterCrater = Vortex.Terrain.Height(0f, 0f);
                float off = Vortex.Terrain.Height(500f, 500f);

                log.Log("terrain: " + chunks + " chunks drawn, sample " + CameraSkySmoke.Rgb(before) + " -> " + CameraSkySmoke.Rgb(after) + " -> painted " + CameraSkySmoke.Rgb(painted) + " (textures " + textures0 + ")"
                    + ", height(0,0) = " + (hOk ? h.ToString("0.00") : "none") + " (api " + apiH.ToString("0.00") + ", layer " + layer + ")"
                    + ", ray from 520 hits " + (rayOk ? hit.Y.ToString("0.00") : "nothing") + ", crater -> " + afterCrater.ToString("0.00")
                    + ", off-terrain " + (float.IsNaN(off) ? "NaN" : off.ToString()) + ", atlas " + TerrainService.LastAtlasBuildMs.ToString("0") + " ms");

                bool drawn = chunks >= 4;
                bool splat = painted.r >= 0 && (Math.Abs(painted.r - after.r) + Math.Abs(painted.g - after.g) + Math.Abs(painted.b - after.b)) > 30 && painted.r > painted.g;
                if (!splat) log.LogError("terrain: the splat map does not drive the layers (painting layer 1 everywhere changed nothing)");
                bool ground = after.r >= 0 && (Math.Abs(after.r - before.r) + Math.Abs(after.g - before.g) + Math.Abs(after.b - before.b)) > 40;
                bool heightOk = hOk && Math.Abs(h - 504f) < 0.05f && Math.Abs(apiH - 504f) < 0.05f;
                bool rayHit = rayOk && Math.Abs(hit.Y - 504f) < 0.1f && ReferenceEquals(hitTerrain, e);
                bool craterOk = crater && afterCrater < 502.5f && afterCrater > 501.5f;
                bool layerOk = layer == 1;
                bool offOk = float.IsNaN(off);
                if (!drawn) log.LogError("terrain: no chunks were submitted");
                if (!ground) log.LogError("terrain: the terrain is not visible (the frame did not change)");
                if (!heightOk) log.LogError("terrain: the height query missed the hill");
                if (!rayHit) log.LogError("terrain: the raycast missed the hill");
                if (!craterOk) log.LogError("terrain: Terrain.Deform did not dig a crater");
                if (!layerOk) log.LogError("terrain: the painted layer is not the dominant one");
                if (!offOk) log.LogError("terrain: a point off every terrain must give NaN");
                return drawn && ground && splat && heightOk && rayHit && craterOk && layerOk && offOk;
            }
            finally
            {
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }
    }
}
