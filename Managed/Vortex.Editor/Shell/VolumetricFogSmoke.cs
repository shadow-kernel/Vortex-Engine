using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Lighting;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>Volumetric fog (#119) on the real backend: a spot light shining towards the camera through fog in front of
    /// a dark wall. With the volumetric pass off the frame shows the dark wall; with it on, the light scattered in the
    /// cone brightens the frame — sampled above the light itself (its editor icon sits at the centre).</summary>
    internal static class VolumetricFogSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("volumetric fog", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("volumetric fog: no scene — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            try
            {
                var wall = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (wall?.Transform == null) { log.LogError("volumetric fog: no cube"); return false; }
                made.Add(wall);
                wall.Name = "SmokeFogWall";
                wall.Transform.LocalPosition = new Vector3(0, 500, 20);
                wall.Transform.LocalScale = new Vector3(40, 40, 1);
                var mr = wall.GetComponent<MeshRenderer>();
                if (mr != null) { mr.ColorR = 0.04f; mr.ColorG = 0.04f; mr.ColorB = 0.05f; }
                var lamp = EditorCommands.CreateLight(LightType.Spot);
                if (lamp?.Transform == null) { log.LogError("volumetric fog: no light"); return false; }
                made.Add(lamp);
                lamp.Name = "SmokeFogLamp";
                lamp.Transform.LocalPosition = new Vector3(0, 500, 8);
                lamp.Transform.LocalRotation = new Vector3(0, 180, 0);   // shines down -Z, towards the camera
                var light = lamp.GetComponent<Light>();
                if (light != null) { light.LightType = LightType.Spot; light.Range = 14f; light.Intensity = 80f; light.SpotAngle = 50f; light.InnerSpotAngle = 30f; light.ColorR = 1f; light.ColorG = 1f; light.ColorB = 1f; }
                cam.SetPositionAndRotation(0, 500, 0, 0, 0);
                EditorViewportSession.RequestResubmit();
                VortexAPI.SetFog(0.5f, 0.5f, 0.55f, 0f, 0f, 0f);                                        // analytic fog off, a grey fog colour
                VortexAPI.SetVolumetricFog(false, 0f, 0.55f, 60f, 0f, 6f, 0f, 1f, 1f, 0.35f, 24, true);
                var off = await CameraSkySmoke.Sample("volfog_off.bmp", 0.5, 0.35);
                VortexAPI.SetVolumetricFog(true, 0.9f, 0.6f, 40f, 0f, 6f, 0f, 0f, 1f, 0.3f, 24, false);
                EditorViewportSession.RequestResubmit();
                var on = await CameraSkySmoke.Sample("volfog_on.bmp", 0.5, 0.35);
                log.Log("volumetric fog: spot cone towards the camera over a dark wall — off " + CameraSkySmoke.Rgb(off) + ", on " + CameraSkySmoke.Rgb(on));
                bool brighter = on.r >= 0 && (on.r + on.g + on.b) > (off.r + off.g + off.b) + 24;
                if (!brighter) log.LogError("volumetric fog: the pass did not brighten the lit fog (no in-scattering visible)");
                return brighter;
            }
            finally
            {
                VortexAPI.SetVolumetricFog(false, 0f, 0.55f, 60f, 0f, 6f, 0f, 1f, 1f, 0.35f, 24, true);
                VortexAPI.SetFog(0f, 0f, 0f, 0f, 0f, 0f);
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }
    }
}
