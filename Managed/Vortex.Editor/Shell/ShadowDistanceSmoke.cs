using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>The sun's shadow distance (#466), on the rendered picture so the DX12 (WARP) smoke covers it too. A 40 m pillar
    /// stands 270 m ahead on a wide floor high above the scene; a low sun behind it throws its shadow towards the camera, over
    /// the floor point 240 m ahead that the view is centred on (the last cascade's bounding sphere reaches well past the
    /// nominal distance, so the point sits far beyond what 80 m can cover). With the Directional Light's ShadowDistance at
    /// 80 m that point is lit like the floor beside it; at 300 m it is in shadow. A script's Lighting.SetDirectional(…, shadowDistance:
    /// 300) does the same over the light's 80 m and lasts through scene re-submits (it used to be reset by the next one);
    /// ClearDirectional hands the sun back.</summary>
    internal static class ShadowDistanceSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("sun shadow distance", Run);

        private static int Lum((int r, int g, int b) c) => (c.r * 2126 + c.g * 7152 + c.b * 722) / 10000;

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("sun shadow distance: no scene — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            // the scene's own directional lights step aside for the check (restored below)
            var others = VortexEditor.Claude.SceneModel.All(scene).Select(e => e.GetComponent<Light>()).Where(l => l != null && l.IsEnabled && l.LightType == LightType.Directional).ToList();
            var prevSun = SceneRenderService.ScriptSun;
            try
            {
                foreach (var l in others) l.IsEnabled = false;
                var floor = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                var pillar = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                var sunEntity = EditorCommands.CreateLight(LightType.Directional);
                if (floor?.Transform == null || pillar?.Transform == null || sunEntity?.Transform == null) { log.LogError("sun shadow distance: could not build the test scene"); return false; }
                made.Add(floor); made.Add(pillar); made.Add(sunEntity);
                floor.Name = "SmokeShadowFloor"; pillar.Name = "SmokeShadowPillar"; sunEntity.Name = "SmokeShadowSun";
                floor.Transform.LocalPosition = new Vector3(0, 500, 250);
                floor.Transform.LocalScale = new Vector3(800, 0.2f, 800);
                pillar.Transform.LocalPosition = new Vector3(0, 520, 270);
                pillar.Transform.LocalScale = new Vector3(10, 40, 4);
                // the light travels towards the camera and 30° down: (0, -0.5, -0.866) — the pillar's shadow runs ~69 m back over z = 240
                sunEntity.Transform.LocalRotation = new Vector3(30, 180, 0);
                var sun = sunEntity.GetComponent<Light>();
                sun.Intensity = 3f; sun.ColorR = sun.ColorG = sun.ColorB = 1f;
                sun.ShadowType = ShadowType.Hard; sun.ShadowStrength = 1f;

                SceneRenderService.HideEditorOverlays = true;
                TransformMath.LookAngles(new Vector3(0, 530, 0), new Vector3(0, 500.1f, 240), out float cy, out float cp);
                cam.SetPositionAndRotation(0, 530, 0, cy, cp);   // centred on the floor point 240 m ahead

                sun.ShadowDistance = 80f;
                EditorViewportSession.RequestResubmit();
                var near80 = await CameraSkySmoke.Sample("shadow_distance_80.bmp", 0.5, 0.5);
                var lit80 = await CameraSkySmoke.Sample("shadow_distance_80.bmp", 0.62, 0.5, capture: false);

                sun.ShadowDistance = 300f;   // what the Inspector / set_component_properties write
                EditorViewportSession.RequestResubmit();
                var near300 = await CameraSkySmoke.Sample("shadow_distance_300.bmp", 0.5, 0.5);
                var lit300 = await CameraSkySmoke.Sample("shadow_distance_300.bmp", 0.62, 0.5, capture: false);

                // a script's sun over the light's 80 m — it must survive the re-submits that used to reset it
                sun.ShadowDistance = 80f;
                Vortex.Lighting.SetDirectional(0f, -0.5f, -0.866f, 1f, 1f, 1f, 3f, castShadows: true, shadowDistance: 300f);
                EditorViewportSession.RequestResubmit();
                await SmokeRegistry.Settle(400);
                EditorViewportSession.RequestResubmit();
                var script = await CameraSkySmoke.Sample("shadow_distance_script.bmp", 0.5, 0.5);
                var scriptLit = await CameraSkySmoke.Sample("shadow_distance_script.bmp", 0.62, 0.5, capture: false);
                Vortex.Lighting.ClearDirectional();
                EditorViewportSession.RequestResubmit();
                var cleared = await CameraSkySmoke.Sample("shadow_distance_cleared.bmp", 0.5, 0.5);
                var clearedLit = await CameraSkySmoke.Sample("shadow_distance_cleared.bmp", 0.62, 0.5, capture: false);

                log.Log("sun shadow distance: floor 240 m ahead / beside it — 80 m " + CameraSkySmoke.Rgb(near80) + " / " + CameraSkySmoke.Rgb(lit80)
                    + ", 300 m " + CameraSkySmoke.Rgb(near300) + " / " + CameraSkySmoke.Rgb(lit300)
                    + ", script 300 m over the light's 80 m " + CameraSkySmoke.Rgb(script) + " / " + CameraSkySmoke.Rgb(scriptLit)
                    + ", cleared " + CameraSkySmoke.Rgb(cleared) + " / " + CameraSkySmoke.Rgb(clearedLit));
                bool litAt80 = Lum(lit80) > 40 && Lum(near80) > Lum(lit80) * 0.8;
                bool shadowAt300 = Lum(lit300) > 40 && Lum(near300) < Lum(lit300) * 0.6;
                bool scriptShadow = Lum(script) < Lum(scriptLit) * 0.6;
                bool backTo80 = Lum(cleared) > Lum(clearedLit) * 0.8;
                if (!litAt80) log.LogError("sun shadow distance: at 80 m the floor 240 m away is shadowed (or the floor is dark) — the distance is not applied");
                if (!shadowAt300) log.LogError("sun shadow distance: with the Light's ShadowDistance at 300 m the pillar casts no shadow 240 m away (#466)");
                if (!scriptShadow) log.LogError("sun shadow distance: Lighting.SetDirectional(…, shadowDistance: 300) did not last through a scene re-submit (#466)");
                if (!backTo80) log.LogError("sun shadow distance: after Lighting.ClearDirectional the scene light's 80 m does not apply again");
                return litAt80 && shadowAt300 && scriptShadow && backTo80;
            }
            finally
            {
                SceneRenderService.ScriptSun = prevSun;
                SceneRenderService.HideEditorOverlays = false;
                foreach (var l in others) l.IsEnabled = true;
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }
    }
}
