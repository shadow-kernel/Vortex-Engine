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
    /// <summary>Per-instance tint (#331): two cubes share ONE white .vmat; the MeshRenderer colour of the right one is red
    /// and only that cube turns red — the left one with the default colour stays neutral. A plain cube (no material)
    /// with a green colour is green: its colour rides with the instance, the shared plain material is white.</summary>
    internal static class InstanceTintSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("instance tint", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("instance tint: no scene / project — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            string matRel = "Assets/Materials/SmokeTintShared.vmat";
            try
            {
                new VortexMaterial { BlendMode = "Opaque", BaseColor = new[] { 1f, 1f, 1f, 1f }, Metallic = 0f, Roughness = 0.8f }.Save(Path.Combine(project, matRel));

                var left = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                var right = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                var plain = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (left?.Transform == null || right?.Transform == null || plain?.Transform == null) { log.LogError("instance tint: no cubes"); return false; }
                made.Add(left); made.Add(right); made.Add(plain);
                left.Name = "SmokeTintLeft"; right.Name = "SmokeTintRight"; plain.Name = "SmokeTintPlain";
                left.Transform.LocalPosition = new Vector3(-5, 500, 20);
                right.Transform.LocalPosition = new Vector3(5, 500, 20);
                plain.Transform.LocalPosition = new Vector3(0, 500, 20);
                left.Transform.LocalScale = new Vector3(7, 8, 1);
                right.Transform.LocalScale = new Vector3(7, 8, 1);
                plain.Transform.LocalScale = new Vector3(2.4f, 8, 1);
                var lmr = left.GetComponent<MeshRenderer>(); var rmr = right.GetComponent<MeshRenderer>(); var pmr = plain.GetComponent<MeshRenderer>();
                if (lmr == null || rmr == null || pmr == null) { log.LogError("instance tint: no MeshRenderer"); return false; }
                lmr.MaterialPath = matRel;                 // default colour: untinted
                rmr.MaterialPath = matRel; rmr.ColorR = 1f; rmr.ColorG = 0f; rmr.ColorB = 0f;   // the SAME material, tinted red
                pmr.ColorR = 0f; pmr.ColorG = 1f; pmr.ColorB = 0f;   // plain: the colour is the tint

                cam.SetPositionAndRotation(0, 500, 0, 0, 0);
                SceneRenderService.HideEditorOverlays = true;
                EditorViewportSession.RequestResubmit();
                // the cubes sit close to the centre so the columns land on them whatever the viewport aspect (WARP runs smaller)
                var l = await CameraSkySmoke.Sample("tint.bmp", 0.33, 0.5);
                var r = await CameraSkySmoke.Sample("tint.bmp", 0.67, 0.5, capture: false);
                var p = await CameraSkySmoke.Sample("tint.bmp", 0.5, 0.5, capture: false);
                log.Log("instance tint: shared material untinted " + CameraSkySmoke.Rgb(l) + ", tinted red " + CameraSkySmoke.Rgb(r) + ", plain green cube " + CameraSkySmoke.Rgb(p));
                bool neutral = Math.Abs(l.r - l.g) < 25 && Math.Abs(l.g - l.b) < 30 && l.r + l.g + l.b > 90;
                bool red = r.r > 2 * r.g + 15 && r.r > 2 * r.b + 15 && r.r > 25;
                bool green = p.g > 2 * p.r + 15 && p.g > 2 * p.b + 15 && p.g > 25;
                bool leftUntouched = l.r < l.g + 25;   // the tint did not leak into the other instance of the material
                if (!neutral) log.LogError("instance tint: the untinted instance of the shared material is not neutral");
                if (!red) log.LogError("instance tint: the MeshRenderer colour does not tint the instance of a .vmat material (#331)");
                if (!green) log.LogError("instance tint: a plain cube lost its colour");
                if (!leftUntouched) log.LogError("instance tint: the tint leaked into the other instance of the shared material");
                return neutral && red && green && leftUntouched;
            }
            finally
            {
                SceneRenderService.HideEditorOverlays = false;
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                try { File.Delete(Path.Combine(project, matRel)); } catch { }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }
    }
}
