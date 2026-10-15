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
        internal static void Register()
        {
            SmokeRegistry.Add("sun shadow distance", Run);
            SmokeRegistry.Add("sun shadow distance small caster", RunSmallCaster);
            SmokeRegistry.Add("sun shadow distance tall caster", RunTallCaster);
        }

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

        /// <summary>The review of #466: a shadow distance of 300 m used to lose the shadows it should add. The shaders' depth
        /// bias was a fixed share of a cascade's depth range, and that range grows with the distance: about 7 m of bias in
        /// the last cascade at 300 m (Soft), more than a crate or a person is thick. A 2 m cube stands 128 m from the camera
        /// (the last cascade: the second ends at 65 m); a 30° sun from the side throws its shadow next to it. Soft and Hard,
        /// the floor in that shadow must be dark against the floor beside it.</summary>
        private static Task<bool> RunSmallCaster() => Staged("sun shadow distance small caster", async (log, cam, sun, made) =>
        {
            var cube = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
            if (cube?.Transform == null) { log.LogError("sun shadow distance small caster: could not build the test scene"); return false; }
            made.Add(cube); cube.Name = "SmokeShadowCrate";
            cube.Transform.LocalPosition = new Vector3(0, 501.1f, 128);
            cube.Transform.LocalScale = new Vector3(2, 2, 2);
            sun.Entity.Transform.LocalRotation = new Vector3(30, 270, 0);   // the light travels towards -x, 30° down: the shadow lies 0 .. 3.5 m beside the cube
            sun.ShadowDistance = 300f;
            var shade = new Vector3(-2.7f, 500.1f, 128);   // the middle of the shadow
            var lit = new Vector3(-2.7f, 500.1f, 134);     // the floor 5 m further on, beside it
            bool ok = true;
            var report = new List<string>();
            foreach (var type in new[] { ShadowType.Soft, ShadowType.Hard })
            {
                sun.ShadowType = type;
                string t = type == ShadowType.Soft ? "soft" : "hard";
                // looked at steeply from 128 m away (the view distance picks the cascade: the last one at 300 m)
                Aim(cam, new Vector3(shade.X, shade.Y + 99.9f, shade.Z - 80), shade);
                var inShade = await CameraSkySmoke.Sample("shadow_small_caster_" + t + ".bmp", 0.5, 0.5);
                Aim(cam, new Vector3(lit.X, lit.Y + 99.9f, lit.Z - 80), lit);
                var beside = await CameraSkySmoke.Sample("shadow_small_caster_" + t + "_lit.bmp", 0.5, 0.5);
                report.Add(t + " " + CameraSkySmoke.Rgb(inShade) + " / " + CameraSkySmoke.Rgb(beside));
                if (!(Lum(beside) > 40 && Lum(inShade) < Lum(beside) * 0.6))
                {
                    log.LogError("sun shadow distance small caster: at 300 m the 2 m cube 128 m away casts no shadow (" + t + "): the depth bias swallows it (#466)");
                    ok = false;
                }
            }
            log.Log("sun shadow distance small caster: the floor in the 2 m cube's shadow / beside it, 300 m, last cascade: " + string.Join(", ", report));
            return ok;
        });

        /// <summary>The review of #466: a 60 m tower 127 m ahead and a low sun (20°) behind it; its shadow (165 m long) covers
        /// the floor 8 m in front of the camera, in the first cascade. The cascade boxes reach back towards the light by the
        /// shadow distance, so at 300 m the tower is inside the first cascade's box and the floor is dark (it stayed lit while
        /// that reach was capped at 80 m for the near cascade). The same view with the tower gone is the lit reference; at
        /// 80 m the tower is out of reach, as it always was (logged).</summary>
        private static Task<bool> RunTallCaster() => Staged("sun shadow distance tall caster", async (log, cam, sun, made) =>
        {
            var tower = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
            if (tower?.Transform == null) { log.LogError("sun shadow distance tall caster: could not build the test scene"); return false; }
            made.Add(tower); tower.Name = "SmokeShadowTower";
            tower.Transform.LocalPosition = new Vector3(0, 530.1f, 135);
            tower.Transform.LocalScale = new Vector3(30, 60, 6);
            sun.Entity.Transform.LocalRotation = new Vector3(20, 180, 0);   // the light travels towards the camera (-z), 20° down
            sun.ShadowType = ShadowType.Hard;
            sun.ShadowDistance = 300f;
            Aim(cam, new Vector3(0, 503, 0), new Vector3(0, 500.1f, 8));   // re-submits the scene
            var shade300 = await CameraSkySmoke.Sample("shadow_tall_caster_300.bmp", 0.5, 0.5);
            sun.ShadowDistance = 80f;
            EditorViewportSession.RequestResubmit();
            var at80 = await CameraSkySmoke.Sample("shadow_tall_caster_80.bmp", 0.5, 0.5);
            sun.ShadowDistance = 300f;
            tower.Transform.LocalPosition = new Vector3(0, 530.1f, 2000);   // gone: the lit reference
            EditorViewportSession.RequestResubmit();
            var lit300 = await CameraSkySmoke.Sample("shadow_tall_caster_300_lit.bmp", 0.5, 0.5);
            log.Log("sun shadow distance tall caster: the floor 8 m ahead in the shadow of a tower 127 m away: 300 m " + CameraSkySmoke.Rgb(shade300)
                + " (without the tower " + CameraSkySmoke.Rgb(lit300) + "), 80 m " + CameraSkySmoke.Rgb(at80));
            bool ok = Lum(lit300) > 40 && Lum(shade300) < Lum(lit300) * 0.6;
            if (!ok) log.LogError("sun shadow distance tall caster: at 300 m the 60 m tower 127 m away throws no shadow into the first cascade: its box does not reach back far enough (#466)");
            return ok;
        });

        private static void Aim(EditorCameraController cam, Vector3 from, Vector3 to)
        {
            TransformMath.LookAngles(from, to, out float yaw, out float pitch);
            cam.SetPositionAndRotation(from.X, from.Y, from.Z, yaw, pitch);
            EditorViewportSession.RequestResubmit();
        }

        /// <summary>A wide floor high above the scene and a test sun of its own (the scene's directional lights and a script's
        /// sun step aside); everything is put back afterwards.</summary>
        private static async Task<bool> Staged(string name, Func<ConsoleService, EditorCameraController, Light, List<GameEntity>, Task<bool>> body)
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log(name + ": no scene — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            var others = VortexEditor.Claude.SceneModel.All(scene).Select(e => e.GetComponent<Light>()).Where(l => l != null && l.IsEnabled && l.LightType == LightType.Directional).ToList();
            var prevSun = SceneRenderService.ScriptSun;
            try
            {
                SceneRenderService.ScriptSun = null;
                foreach (var l in others) l.IsEnabled = false;
                var floor = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                var sunEntity = EditorCommands.CreateLight(LightType.Directional);
                if (floor?.Transform == null || sunEntity?.Transform == null) { log.LogError(name + ": could not build the test scene"); return false; }
                made.Add(floor); made.Add(sunEntity);
                floor.Name = "SmokeShadowFloor"; sunEntity.Name = "SmokeShadowSun";
                floor.Transform.LocalPosition = new Vector3(0, 500, 150);
                floor.Transform.LocalScale = new Vector3(800, 0.2f, 800);
                var sun = sunEntity.GetComponent<Light>();
                sun.Intensity = 3f; sun.ColorR = sun.ColorG = sun.ColorB = 1f; sun.ShadowStrength = 1f;
                SceneRenderService.HideEditorOverlays = true;
                return await body(log, cam, sun, made);
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
