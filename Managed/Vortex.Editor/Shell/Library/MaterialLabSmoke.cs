using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Assets.Store;
using Editor.Core.Data;
using Editor.Core.Services;
using VortexEditor.Shell.Material;

namespace VortexEditor.Shell.Library
{
    /// <summary>
    /// Look-development check for store materials (VORTEX_MATERIAL_LAB=1, live network): downloads a Poly Haven texture
    /// set exactly like the Store does (default variant, Add to Project), opens the generated .vmat in the Material
    /// Editor and captures sphere + plane under several settings — the store defaults, without parallax, tiled — so
    /// the look can be compared with the source's own preview. VORTEX_MATERIAL_LAB_ID picks the asset
    /// (default coast_sand_rocks_02). Does nothing without VORTEX_MATERIAL_LAB.
    /// </summary>
    internal static class MaterialLabSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("material lab (live)", Run);

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            if (Environment.GetEnvironmentVariable("VORTEX_MATERIAL_LAB") != "1") { log.Log("material lab: skipped (set VORTEX_MATERIAL_LAB=1)"); return true; }
            bool Fail(string why) { log.LogError("material lab: " + why); return false; }
            var project = ProjectData.Current;
            if (project == null) return Fail("no project");
            string id = Environment.GetEnvironmentVariable("VORTEX_MATERIAL_LAB_ID") ?? "coast_sand_rocks_02";
            var provider = StoreProviders.Get("polyhaven");
            var page = await provider.SearchAsync(new StoreQuery { Text = id.Replace('_', ' '), Kind = StoreKind.Material, PageSize = 40 }, CancellationToken.None);
            var item = page.Items.FirstOrDefault(i => i.Id == id);
            if (item == null) return Fail("Poly Haven has no texture " + id + " (" + page.Items.Count + " results)");
            var det = await provider.DetailsAsync(item, CancellationToken.None);
            var variant = det.Variants.FirstOrDefault(v => v.Id == det.DefaultVariant) ?? det.Variants.First();
            var job = StoreDownloads.Enqueue(provider, item, variant, true, project.Path, project.Name);
            for (int i = 0; i < 1800 && job.IsActive; i++) await Task.Delay(100);
            if (job.State != StoreJobState.Done) return Fail("download " + job.State + ": " + job.Error);
            string vmat = job.ProjectPaths.FirstOrDefault();
            if (vmat == null) return Fail("no .vmat");
            log.Log("material lab: " + id + " " + variant.Id + " → " + File.ReadAllText(vmat).Replace("\n", " "));

            MaterialEditorWindow.Open(vmat);
            var w = MaterialEditorWindow.Find(vmat);
            if (w == null) return Fail("no material editor");
            w.Width = 1100; w.Height = 820;
            var pane = w.PreviewPane;
            async Task Shot(string name)
            {
                int b0 = pane.Builds;
                for (int i = 0; i < 40 && pane.Builds == b0; i++) await Task.Delay(50);
                await SmokeRegistry.Settle(900);
                SmokeRegistry.Capture(w, "lab_" + name + ".png");
            }
            await SmokeRegistry.Settle(1500);
            string Mean()
            {
                var img = pane.Viewport.LastImage;
                if (img == null) return "no image";
                double r = 0, g = 0, b = 0; int n = 0;
                for (int y = img.Height * 2 / 5; y < img.Height * 3 / 5; y += 2)
                    for (int x = img.Width * 2 / 5; x < img.Width * 3 / 5; x += 2)
                    {
                        int o = y * img.Stride + x * 4;
                        b += img.Bgra[o]; g += img.Bgra[o + 1]; r += img.Bgra[o + 2]; n++;
                    }
                return n == 0 ? "-" : (int)(r / n) + "," + (int)(g / n) + "," + (int)(b / n);
            }
            // the material exactly as the Store made it, then the usual adjustments
            foreach (var shape in new[] { "Sphere", "Cube", "Plane" })
            {
                pane.SetShape(shape);
                await Shot("store_" + shape.ToLowerInvariant());
                log.Log("material lab: " + shape + " center RGB " + Mean());
            }
            w.ApplyEdit("lab", m => m.HeightScale = 0f);
            await Shot("noparallax_plane");
            w.ApplyEdit("lab", m => m.UVTiling = new[] { 3f, 3f });
            await Shot("tiled3_plane");
            log.Log("material lab: captured → lab_*.png");
            return true;
        }
    }
}
