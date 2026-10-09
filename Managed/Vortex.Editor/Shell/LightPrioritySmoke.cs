using System;
using System.Collections.Generic;
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
    /// <summary>Lights beyond the renderer's 16 point / 8 spot slots are chosen by relevance (#335), not by hierarchy
    /// order: seventeen dim lights far away come first in the scene, a bright one next to a wall comes last — the wall
    /// must still light up. Runs on the real backend (Metal here, DX12 / WARP on the Windows runner).</summary>
    internal static class LightPrioritySmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("light priority", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            if (scene == null) { log.Log("light priority: no scene — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            try
            {
                var wall = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (wall?.Transform == null) { log.LogError("light priority: no cube"); return false; }
                made.Add(wall);
                wall.Name = "SmokeLitWall";
                wall.Transform.LocalPosition = new Vector3(0, 500, 20);
                wall.Transform.LocalScale = new Vector3(40, 40, 1);
                var mr = wall.GetComponent<MeshRenderer>();
                if (mr != null) { mr.ColorR = 0.5f; mr.ColorG = 0.5f; mr.ColorB = 0.5f; }

                // seventeen dim lights far from everything, first in hierarchy order…
                for (int i = 0; i < 17; i++)
                {
                    var l = MakeLight(made, "SmokeFarLight" + i, new Vector3(-200 + i * 25, 480, 400), 0.2f, 4f);
                    if (l == null) { log.LogError("light priority: could not create a light"); return false; }
                }
                // …and one bright light right in front of the wall, last
                var near = MakeLight(made, "SmokeNearLight", new Vector3(0, 500, 12), 80f, 40f);
                if (near == null) { log.LogError("light priority: could not create the near light"); return false; }

                cam.SetPositionAndRotation(0, 500, 0, 0, 0);
                // the near light sits dead centre in front of the camera, so its gizmo (range circle, axes) would be
                // what the centre pixel samples: hide the editor overlays and read the wall above the light
                SceneRenderService.HideEditorOverlays = true;
                near.IsEnabled = false;
                EditorViewportSession.RequestResubmit();
                var dark = await CameraSkySmoke.Sample("light_without.bmp", 0.5, 0.15);
                near.IsEnabled = true;
                EditorViewportSession.RequestResubmit();
                var lit = await CameraSkySmoke.Sample("light_with.bmp", 0.5, 0.15);

                int gain = (lit.r + lit.g + lit.b) - (dark.r + dark.g + dark.b);
                log.Log("light priority: wall without the 18th light " + CameraSkySmoke.Rgb(dark) + ", with it " + CameraSkySmoke.Rgb(lit) + " (gain " + gain + ")");
                if (gain < 20) log.LogError("light priority: the bright light next to the wall was dropped in favour of the seventeen dim ones");
                return gain >= 20;
            }
            finally
            {
                SceneRenderService.HideEditorOverlays = false;
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }

        private static Light MakeLight(List<GameEntity> made, string name, Vector3 pos, float intensity, float range)
        {
            var e = EditorCommands.CreateLight(LightType.Point);
            var l = e?.GetComponent<Light>();
            if (e == null || l == null || e.Transform == null) return null;
            made.Add(e);
            e.Name = name;
            e.Transform.LocalPosition = pos;
            l.Intensity = intensity; l.Range = range;
            l.ColorR = 1f; l.ColorG = 1f; l.ColorB = 1f;
            return l;
        }
    }
}
