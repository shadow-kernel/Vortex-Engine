using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Terrain;
using Editor.Core.Services.Water;
using Editor.Core.Terrain;
using Editor.Core.Viewport;
using Editor.ECS;

namespace VortexEditor.Shell
{
    /// <summary>A body of water on the real backend (#200): a terrain with a basin in front of the editor camera gets a Water
    /// entity whose level lies above the basin floor — the surface must draw (the basin's centre changes colour), stop at the
    /// bank (the wet vertex count matches the basin), answer the height queries and the script API, and vanish when the
    /// terrain is raised above the level.</summary>
    internal static class WaterSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("water", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("water: no scene — skipped"); return true; }
            if (!Editor.DllWrapper.VortexAPI.TerrainApiAvailable) { log.LogError("water: the engine library has no terrain exports"); return false; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            try
            {
                // a 64 m terrain at y = 498 with a 4 m deep basin in the middle
                var te = new GameEntity(scene, "SmokeWaterGround");
                scene.AddEntity(te);
                made.Add(te);
                te.Transform.LocalPosition = new Vector3(-32f, 498f, -32f);
                te.AddComponent(new Editor.ECS.Components.Rendering.Terrain(te) { Size = 64f, Resolution = 65, Collision = false });
                TerrainData tdata; float cell;
                if (!TerrainService.TryGetData(te, out tdata, out cell)) { log.LogError("water: no terrain data"); return false; }
                tdata.Raise(32f, 32f, 14f, -4f, 0.4f);
                TerrainService.MarkDirty(te, new SampleRect { X0 = 0, Z0 = 0, X1 = 64, Z1 = 64 }, true, false);

                cam.SetPositionAndRotation(0, 504, -24, 0, 0);
                EditorViewportSession.RequestResubmit();
                await Task.Delay(300);
                EditorViewportSession.RequestResubmit();
                // the basin's centre lies in the lower third of the frame from this camera (the rim ends at ~0.68)
                var before = await CameraSkySmoke.Sample("water_off.bmp", 0.5, 0.82);

                var we = new GameEntity(scene, "SmokeWater");
                scene.AddEntity(we);
                made.Add(we);
                we.Transform.LocalPosition = new Vector3(0f, 496.5f, 0f);   // 1.5 m below the rim: the basin fills, the plain stays dry
                var water = new Editor.ECS.Components.Rendering.Water(we) { Size = 40f, CellSize = 1f, DeepR = 0.02f, DeepG = 0.12f, DeepB = 0.16f, ShallowR = 0.2f, ShallowG = 0.5f, ShallowB = 0.45f, Absorption = 2f, WaveHeight = 0.3f };
                we.AddComponent(water);
                EditorViewportSession.RequestResubmit();
                await Task.Delay(400);
                EditorViewportSession.RequestResubmit();
                var after = await CameraSkySmoke.Sample("water_on.bmp", 0.5, 0.82);
                int submitted = WaterService.LastSubmitted;
                int wet = WaterService.WetVertices(we);

                float h; bool centre = WaterService.TryHeight(0f, 0f, out h);
                float dry; bool rim = WaterService.TryHeight(25f, 25f, out dry);
                bool under = Vortex.Water.IsUnderwater(new Vortex.Vector3(0f, 495.5f, 0f));
                bool above = Vortex.Water.IsUnderwater(new Vortex.Vector3(0f, 497f, 0f));
                float apiH = Vortex.Water.Height(2f, 1f);
                float depthApi = Vortex.Water.Depth(new Vortex.Vector3(0f, 495f, 0f));

                // raise the basin above the level: the surface dries up
                tdata.Raise(32f, 32f, 14f, 6f, 0.4f);
                TerrainService.MarkDirty(te, new SampleRect { X0 = 0, Z0 = 0, X1 = 64, Z1 = 64 }, true, false);
                EditorViewportSession.RequestResubmit();
                await Task.Delay(500);
                EditorViewportSession.RequestResubmit();
                await Task.Delay(150);
                float gone; bool stillWater = WaterService.TryHeight(0f, 0f, out gone);
                int wetAfter = WaterService.WetVertices(we);

                log.Log("water: " + submitted + " surface(s) drawn, sample " + CameraSkySmoke.Rgb(before) + " -> " + CameraSkySmoke.Rgb(after) + ", wet vertices " + wet + " -> " + wetAfter
                    + ", height at the centre " + (centre ? h.ToString("0.00") : "none") + " (api " + apiH.ToString("0.00") + "), rim " + (rim ? "WET" : "dry")
                    + ", under " + under + " / above " + above + ", depth " + depthApi.ToString("0.00") + ", after raising: " + (stillWater ? "still water" : "dry"));

                bool drawn = submitted >= 1;
                bool visible = after.r >= 0 && (Math.Abs(after.r - before.r) + Math.Abs(after.g - before.g) + Math.Abs(after.b - before.b)) > 20;
                bool wetOk = wet > 100 && wet < 1681;   // part of the 41 × 41 grid, not all of it
                bool queries = centre && Math.Abs(h - 496.5f) < 1e-3f && !rim && under && !above && Math.Abs(apiH - 496.5f) < 1e-3f && Math.Abs(depthApi - 1.5f) < 1e-3f;
                bool dried = !stillWater && wetAfter < wet;
                if (!drawn) log.LogError("water: no surface was submitted");
                if (!visible) log.LogError("water: the surface is not visible (the basin did not change colour)");
                if (!wetOk) log.LogError("water: the wet part of the surface does not match the basin (" + wet + ")");
                if (!queries) log.LogError("water: the height / underwater queries are wrong");
                if (!dried) log.LogError("water: raising the ground above the level did not dry the water");
                return drawn && visible && wetOk && queries && dried;
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
