using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.ECS;
using Editor.ECS.Components.Rendering;
using VortexEditor.Panels;
using VortexEditor.Shell.Material;

namespace VortexEditor.Shell.Library
{
    /// <summary>
    /// Editor smoke checks for store materials in scenes:
    /// "primitive shading" — the material preview's sphere is lit from the outside (sphere, cylinder and cone triangles used
    /// to face inward, so back-face culling showed the inner far wall: a dark, inside-out sphere in every preview);
    /// "material fit" — a material that knows its real-world size, dropped on a 24 × 92 m floor, becomes a copy tiled
    /// for that size, and Undo restores the old material.
    /// </summary>
    internal static class MaterialFitSmoke
    {
        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("primitive shading", PrimitiveShading);
            SmokeRegistry.Add("material fit", MaterialFit);
        }

        private static double CenterLuma(Editor.Core.Services.Rendering.PreviewImage img)
        {
            if (img == null) return -1;
            double sum = 0; int n = 0;
            for (int y = img.Height * 9 / 20; y < img.Height * 11 / 20; y++)
                for (int x = img.Width * 9 / 20; x < img.Width * 11 / 20; x++)
                {
                    int o = y * img.Stride + x * 4;
                    sum += 0.0722 * img.Bgra[o] + 0.7152 * img.Bgra[o + 1] + 0.2126 * img.Bgra[o + 2];
                    n++;
                }
            return n == 0 ? -1 : sum / n;
        }

        public static async Task<bool> PrimitiveShading()
        {
            var log = ConsoleService.Instance;
            var project = ProjectData.Current;
            if (project == null) { log.LogError("primitive shading: no project"); return false; }
            // a plain white material on the material preview's sphere: the studio key light faces the default camera,
            // so the middle of a correctly wound sphere is bright (~190); an inside-out one shows its unlit inner far
            // wall there (~105). The winding of every primitive is checked headless by VortexGeometryTest.
            string dir = Path.Combine(project.Path, "Assets", "Materials", "_ShadingSmoke");
            Directory.CreateDirectory(dir);
            string vmat = Path.Combine(dir, "White.vmat");
            new VortexMaterial { Name = "White", Roughness = 0.85f }.Save(vmat);
            MaterialEditorWindow w = null;
            try
            {
                MaterialEditorWindow.Open(vmat);
                w = MaterialEditorWindow.Find(vmat);
                if (w == null) { log.LogError("primitive shading: no material editor"); return false; }
                var pane = w.PreviewPane;
                pane.SetShape("Sphere");
                for (int i = 0; i < 60 && (pane.Viewport.LastImage == null || pane.Builds == 0); i++) await Task.Delay(100);
                await SmokeRegistry.Settle(800);
                double luma = CenterLuma(pane.Viewport.LastImage);
                SmokeRegistry.Capture(w, "primitive_sphere_white.png");
                if (luma > 160) { log.Log("primitive shading: white sphere centre luma " + luma.ToString("0")); return true; }
                log.LogError("primitive shading: the preview sphere is lit inside-out (centre luma " + luma.ToString("0") + ", expected ~190)");
                return false;
            }
            finally
            {
                w?.Close();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        public static async Task<bool> MaterialFit()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("material fit: " + why); return false; }
            var project = ProjectData.Current;
            var scene = project?.ActiveScene;
            if (scene == null) return Fail("no scene");
            string dir = Path.Combine(project.Path, "Assets", "Materials", "_FitSmoke");
            Directory.CreateDirectory(dir);
            string vmat = Path.Combine(dir, "Smoke Ground.vmat");
            new VortexMaterial { Name = "Smoke Ground", RealWorldSize = new[] { 15f, 15f } }.Save(vmat);
            GameEntity floor = null;
            try
            {
                floor = EditorCommands.CreatePrimitive(PrimitiveType.Cube);
                if (floor == null) return Fail("could not create a cube");
                floor.Name = "Smoke_Lanes_Ground";
                floor.Transform.LocalScale = new Vector3(24f, 0.024f, 92f);
                var mr = floor.GetComponent<MeshRenderer>();
                string before = mr.MaterialPath;
                if (!ViewportPanel.ApplyMaterial(floor, vmat)) return Fail("the material was not applied");
                string expected = "Assets/Materials/_FitSmoke/Smoke Ground_24x92m.vmat";
                if (mr.MaterialPath != expected) return Fail("expected the tiled copy " + expected + ", got " + mr.MaterialPath);
                var copy = VortexMaterial.Load(Path.Combine(project.Path, expected));
                if (copy?.UVTiling == null || Math.Abs(copy.UVTiling[0] - 1.6f) > 0.001f || Math.Abs(copy.UVTiling[1] - 6.13f) > 0.001f)
                    return Fail("tiling of the copy is " + (copy?.UVTiling == null ? "missing" : copy.UVTiling[0] + " × " + copy.UVTiling[1]));
                var original = VortexMaterial.Load(vmat);
                if (original.UVTiling[0] != 1f || original.UVTiling[1] != 1f) return Fail("the original material was changed");
                // the same object again: nothing new — it already fits
                ViewportPanel.ApplyMaterial(floor, Path.Combine(project.Path, expected));
                if (mr.MaterialPath != expected || Directory.GetFiles(dir, "*.vmat").Length != 2) return Fail("re-applying the copy made another copy");
                UndoRedoManager.Instance.Undo();
                UndoRedoManager.Instance.Undo();
                if (mr.MaterialPath != before) return Fail("Undo did not restore " + before + " (now " + mr.MaterialPath + ")");
                log.Log("material fit: OK → " + Path.GetFileName(expected) + " tiled 1.6 × 6.13");
                return true;
            }
            finally
            {
                if (floor != null) { try { scene.RemoveEntity(floor); } catch { } }
                try { Directory.Delete(dir, true); } catch { }
                try { AssetDatabase.Instance.Refresh(); } catch { }
            }
        }
    }
}
