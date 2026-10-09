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
    /// <summary>Material pipeline checks from the "My Spectre" report, on the real backend (Metal here, DX12 / WARP on
    /// the Windows runner): a .vmat with BlendMode AlphaTest drops its transparent texels (#329), and a mirrored entity
    /// (negative scale) is not drawn inside-out — its back faces are still culled (#334).</summary>
    internal static class MaterialPipelineSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("material pipelines", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            var scene = ProjectData.Current?.ActiveScene;
            string project = ProjectData.Current?.Path;
            if (scene == null || string.IsNullOrEmpty(project)) { log.Log("material pipelines: no scene / project — skipped"); return true; }
            var cam = EditorCameraController.Instance;
            float px = cam.PositionX, py = cam.PositionY, pz = cam.PositionZ, yaw = cam.Yaw, pitch = cam.Pitch;
            var made = new List<GameEntity>();
            string texRel = "Assets/Textures/SmokeCutout.png";
            string opaqueRel = "Assets/Materials/SmokeCutoutOpaque.vmat", cutRel = "Assets/Materials/SmokeCutout.vmat", twoSidedRel = "Assets/Materials/SmokeTwoSided.vmat";
            try
            {
                // a 64x64 map: the left half fully transparent, the right half opaque green
                string tex = Path.Combine(project, texRel);
                CameraSkySmoke.WritePng(tex, 64, 64, (x, y) => x < 32 ? ((byte)0, (byte)255, (byte)0, (byte)0) : ((byte)0, (byte)255, (byte)0, (byte)255));
                void Vmat(string rel, string blend, bool twoSided, bool textured, bool unlitRed = false)
                {
                    var m = new VortexMaterial { BlendMode = blend, AlphaCutoff = 0.5f, TwoSided = twoSided, BaseColor = new[] { 1f, 1f, 1f, 1f } };
                    if (textured) m.AlbedoTexture = tex;
                    if (unlitRed) { m.ShaderType = "Unlit"; m.BaseColor = new[] { 1f, 0f, 0f, 1f }; }
                    m.Save(Path.Combine(project, rel));
                }
                Vmat(opaqueRel, "Opaque", false, true);
                Vmat(cutRel, "AlphaTest", true, true);
                Vmat(twoSidedRel, "Opaque", true, false, unlitRed: true);

                // the entities first (creating one re-frames the editor camera), then the camera, then the captures
                var quad = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                var box = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (quad?.Transform == null || box?.Transform == null) { log.LogError("material pipelines: no cubes"); return false; }
                made.Add(quad); made.Add(box);
                quad.Name = "SmokeCutout";
                quad.Transform.LocalPosition = new Vector3(0, 500, 20);
                quad.Transform.LocalScale = new Vector3(20, 20, 0.2f);
                quad.IsActive = false;
                box.Name = "SmokeMirrored";
                box.Transform.LocalPosition = new Vector3(0, 500, 0);
                box.Transform.LocalScale = new Vector3(-10, 10, 10);
                box.IsActive = false;
                var mr = quad.GetComponent<MeshRenderer>();
                var bmr = box.GetComponent<MeshRenderer>();
                if (mr == null || bmr == null) { log.LogError("material pipelines: no MeshRenderer"); return false; }

                cam.SetPositionAndRotation(0, 500, 0, 0, 0);   // high above the scene, looking +Z
                EditorViewportSession.RequestResubmit();
                var background = await CameraSkySmoke.Centre("mat_background.bmp");

                // ---- #329: a thin cube 20 m ahead wearing the cut-out ----
                quad.IsActive = true;
                mr.MaterialPath = opaqueRel;
                EditorViewportSession.RequestResubmit();
                var oL = await CameraSkySmoke.Sample("mat_opaque.bmp", 0.36, 0.5);
                var oR = await CameraSkySmoke.Sample("mat_opaque.bmp", 0.64, 0.5, capture: false);
                mr.MaterialPath = cutRel;
                EditorViewportSession.RequestResubmit();
                var cL = await CameraSkySmoke.Sample("mat_cutout.bmp", 0.36, 0.5);
                var cR = await CameraSkySmoke.Sample("mat_cutout.bmp", 0.64, 0.5, capture: false);
                bool opaqueBoth = CameraSkySmoke.Green(oL) && CameraSkySmoke.Green(oR);
                bool cutOne = CameraSkySmoke.Green(cL) != CameraSkySmoke.Green(cR);   // whichever side the U axis puts the hole on
                log.Log("material pipelines: opaque " + CameraSkySmoke.Rgb(oL) + " / " + CameraSkySmoke.Rgb(oR) + ", alpha test " + CameraSkySmoke.Rgb(cL) + " / " + CameraSkySmoke.Rgb(cR) + " (background " + CameraSkySmoke.Rgb(background) + ")");
                if (!opaqueBoth) log.LogError("material pipelines: the textured cube is not green on both halves with BlendMode Opaque");
                if (!cutOne) log.LogError("material pipelines: AlphaTest did not cut the transparent half away");
                quad.IsActive = false;

                // ---- #334: a cube around the camera, mirrored on X — its inner faces must stay culled ----
                SelectionService.Instance.Select((GameEntity)null);   // a selected entity is drawn with its outline
                box.IsActive = true;
                box.Transform.LocalScale = new Vector3(10, 10, 10);
                EditorViewportSession.RequestResubmit();
                var normal = await CameraSkySmoke.Centre("mat_normal.bmp");
                box.Transform.LocalScale = new Vector3(-10, 10, 10);
                EditorViewportSession.RequestResubmit();
                var mirrored = await CameraSkySmoke.Centre("mat_mirrored.bmp");
                bmr.MaterialPath = twoSidedRel;   // sanity: a two-sided unlit red material shows the inside
                EditorViewportSession.RequestResubmit();
                var inside = await CameraSkySmoke.Centre("mat_mirrored_twosided.bmp");
                // culled inner faces show whatever is behind the cube — the same for the normal and the mirrored one — and
                // never the red the two-sided material paints on them
                bool culled = Near(mirrored, normal) && !Red(mirrored) && !Red(normal), shown = Red(inside);
                log.Log("material pipelines: cube around the camera " + CameraSkySmoke.Rgb(normal) + ", mirrored " + CameraSkySmoke.Rgb(mirrored) + ", two-sided red " + CameraSkySmoke.Rgb(inside) + " (background " + CameraSkySmoke.Rgb(background) + ")");
                if (!culled) log.LogError("material pipelines: the mirrored cube is drawn inside-out (its inner faces are visible)");
                if (!shown) log.LogError("material pipelines: the two-sided material does not show the cube's inside (the check cannot tell)");
                return opaqueBoth && cutOne && culled && shown;
            }
            finally
            {
                if (made.Count > 0) EditorCommands.DeleteEntities(made);
                foreach (var rel in new[] { texRel, opaqueRel, cutRel, twoSidedRel }) { try { File.Delete(Path.Combine(project, rel)); } catch { } }
                cam.SetPositionAndRotation(px, py, pz, yaw, pitch);
                EditorViewportSession.RequestResubmit();
            }
        }

        private static bool Near((int r, int g, int b) a, (int r, int g, int b) b) =>
            Math.Abs(a.r - b.r) <= 24 && Math.Abs(a.g - b.g) <= 24 && Math.Abs(a.b - b.b) <= 24;
        private static bool Red((int r, int g, int b) c) => c.r > 150 && c.g < 80 && c.b < 80;
    }
}
