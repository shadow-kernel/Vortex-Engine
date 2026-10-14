using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Viewport;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace VortexEditor.Shell
{
    /// <summary>A two-sided material's back face is lit on its own side. A horizontal TwoSided plane under the scene's
    /// sun: seen from above, its top is in the sunlight; seen from below, its underside faces away from the sun and the sky
    /// and must be clearly darker. The standard shader used to light a back face with the front face's normal, so the
    /// underside looked as sunlit as the top (glTF doubleSided cards, fences and cloth turned inside out).</summary>
    internal static class TwoSidedSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("two-sided back faces", Run);

        private static int Luma((int r, int g, int b) c) => (c.r * 299 + c.g * 587 + c.b * 114) / 1000;

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("two-sided back faces: no scene / project — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            GameEntity plane = null;
            string matRel = "Assets/Materials/SmokeTwoSided.vmat";
            try
            {
                new VortexMaterial { ShaderType = "StandardPBR", BaseColor = new[] { 0.8f, 0.8f, 0.8f, 1f }, Metallic = 0f, Roughness = 0.9f, TwoSided = true }
                    .Save(Path.Combine(project, matRel));
                plane = EditorCommands.CreatePrimitive(PrimitiveType.Plane);
                if (plane?.Transform == null) { log.LogError("two-sided back faces: no plane"); return false; }
                plane.Name = "SmokeTwoSidedPlane";
                plane.Transform.LocalPosition = new Vector3(0, 500, 20);
                plane.Transform.LocalScale = new Vector3(40, 1, 40);
                var mr = plane.GetComponent<MeshRenderer>();
                if (mr != null) mr.MaterialPath = matRel;

                SceneRenderService.HideEditorOverlays = true;
                cam.SetPositionAndRotation(0, 503, 20, 0, 89);    // above, looking down at the top (the front face)
                EditorViewportSession.RequestResubmit();
                var top = await CameraSkySmoke.Sample("two_sided_top.bmp", 0.5, 0.5);
                cam.SetPositionAndRotation(0, 497, 20, 0, -89);   // below, looking up at the underside (the back face)
                EditorViewportSession.RequestResubmit();
                var under = await CameraSkySmoke.Sample("two_sided_under.bmp", 0.5, 0.5);
                log.Log("two-sided back faces: top " + CameraSkySmoke.Rgb(top) + " (luma " + Luma(top) + "), underside " + CameraSkySmoke.Rgb(under) + " (luma " + Luma(under) + ")");
                bool drawn = Math.Abs(under.r - under.g) < 30 && Math.Abs(under.g - under.b) < 35;   // grey plane, not the blue sky behind it
                bool darker = Luma(under) < Luma(top) * 0.7;
                if (!drawn) log.LogError("two-sided back faces: the underside of a TwoSided plane is not drawn (the sky shows through)");
                if (!darker) log.LogError("two-sided back faces: the underside is lit like the sunlit top — the back face uses the front face's normal");
                return drawn && darker;
            }
            finally
            {
                SceneRenderService.HideEditorOverlays = false;
                if (plane != null) EditorCommands.DeleteEntities(new List<GameEntity> { plane });
                try { File.Delete(Path.Combine(project, matRel)); } catch { }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }
    }
}
