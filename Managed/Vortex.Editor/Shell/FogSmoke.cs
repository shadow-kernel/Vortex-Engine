using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>Height fog (#328) on the real backend: a green wall 120 m ahead is fogged red when camera and wall sit
    /// inside the fog layer, LESS when the camera is 10 m above its ceiling — but still fogged, because the ray is
    /// integrated through the thinning layer (the old band formula gave exactly nothing above the ceiling).</summary>
    internal static class FogSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("height fog", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("height fog: no scene — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            try
            {
                var wall = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (wall?.Transform == null) { log.LogError("height fog: no cube"); return false; }
                made.Add(wall);
                wall.Name = "SmokeFogWall";
                wall.Transform.LocalPosition = new Vector3(0, 500, 120);
                wall.Transform.LocalScale = new Vector3(80, 80, 1);
                var mr = wall.GetComponent<MeshRenderer>();
                if (mr != null) { mr.ColorR = 0f; mr.ColorG = 1f; mr.ColorB = 0f; }
                cam.SetPositionAndRotation(0, 500, 0, 0, 0);
                EditorViewportSession.RequestResubmit();

                VortexAPI.SetFog(1f, 0f, 0f, 0f, 0f, 0f);                       // off
                var clear = await CameraSkySmoke.Centre("fog_off.bmp");
                VortexAPI.SetFog(1f, 0f, 0f, 0.006f, 510f, 0.1f);                // red fog, ceiling 10 m above the camera
                var inside = await CameraSkySmoke.Centre("fog_inside.bmp");
                VortexAPI.SetFog(1f, 0f, 0f, 0.006f, 490f, 0.1f);                // ceiling 10 m below the camera
                var above = await CameraSkySmoke.Centre("fog_above.bmp");

                log.Log("height fog: wall at 120 m — no fog " + CameraSkySmoke.Rgb(clear) + ", inside the layer " + CameraSkySmoke.Rgb(inside) + ", 10 m above its ceiling " + CameraSkySmoke.Rgb(above));
                bool fogsInside = inside.r > clear.r + 20 && inside.g < clear.g - 10;
                bool thinnerAbove = above.r < inside.r - 5;
                bool stillSome = above.r > clear.r + 4;
                if (!fogsInside) log.LogError("height fog: no fog inside the layer");
                if (!thinnerAbove) log.LogError("height fog: the fog is not thinner with the camera above the ceiling");
                if (!stillSome) log.LogError("height fog: no fog at all above the ceiling (the band formula is back)");
                return fogsInside && thinnerAbove && stillSome;
            }
            finally
            {
                VortexAPI.SetFog(0f, 0f, 0f, 0f, 0f, 0f);
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }
    }
}
