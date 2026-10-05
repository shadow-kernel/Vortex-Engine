using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Editor.Core.Assets;
using Editor.Core.Assets.Library;
using Editor.Core.Assets.Store;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.Core.Services.Rendering;
using VortexEditor.Panels;
using VortexEditor.Panels.AssetBrowser;
using VortexEditor.Shell.Material;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Shell.Library
{
    /// <summary>
    /// Editor smoke checks of the Store tab (VORTEX_SMOKE_ONLY=store): Poly Haven search → result tiles with thumbnails →
    /// details → "Download + Add to Project" → the model lands in the library (with its files, source and license) and
    /// in the project; ambientCG → a PBR zip becomes a wired .vmat that renders in the Material Editor preview. Runs
    /// against a scripted HTTP server by default; VORTEX_STORE_LIVE=1 uses the real Poly Haven / ambientCG APIs.
    /// </summary>
    internal static class StoreSmoke
    {
        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("asset store", Run);
            SmokeRegistry.Add("asset store material", RunMaterial);
        }

        private sealed class Fake : HttpMessageHandler
        {
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            {
                string url = r.RequestUri.ToString();
                var hit = Files.Where(kv => url.StartsWith(kv.Key, StringComparison.Ordinal)).OrderByDescending(kv => kv.Key.Length).FirstOrDefault();
                if (hit.Value == null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(hit.Value) });
            }
        }

        private static string Md5(byte[] b) { using (var m = MD5.Create()) return ContentHash.Hex(m.ComputeHash(b)); }

        private static byte[] Png(Color c)
        {
            var rtb = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
            using (var dc = rtb.CreateDrawingContext())
            {
                dc.FillRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x34)), new Rect(0, 0, 64, 64));
                dc.DrawEllipse(new SolidColorBrush(c), null, new Point(32, 34), 22, 22);
            }
            using (var ms = new MemoryStream()) { rtb.Save(ms); return ms.ToArray(); }
        }

        /// <summary>A 64×64 striped map (planks: base colour + darker gaps).</summary>
        private static byte[] Map(Color a, Color b)
        {
            var rtb = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
            using (var dc = rtb.CreateDrawingContext())
            {
                dc.FillRectangle(new SolidColorBrush(a), new Rect(0, 0, 64, 64));
                for (int y = 0; y < 64; y += 16) dc.FillRectangle(new SolidColorBrush(b), new Rect(0, y, 64, 3));
            }
            using (var ms = new MemoryStream()) { rtb.Save(ms); return ms.ToArray(); }
        }

        private static byte[] Zip(params (string name, byte[] data)[] files)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    foreach (var f in files)
                        using (var s = z.CreateEntry(f.name).Open()) s.Write(f.data, 0, f.data.Length);
                return ms.ToArray();
            }
        }

        /// <summary>Average (R - B) / 255 over the central fifth of a render — warm = the orange albedo map shows.</summary>
        private static double Warmth(PreviewImage img)
        {
            double sum = 0; int n = 0;
            for (int y = img.Height * 2 / 5; y < img.Height * 3 / 5; y += 2)
                for (int x = img.Width * 2 / 5; x < img.Width * 3 / 5; x += 2)
                {
                    int o = y * img.Stride + x * 4;
                    sum += (img.Bgra[o + 2] - img.Bgra[o]) / 255.0;
                    n++;
                }
            return n == 0 ? 0 : sum / n;
        }

        /// <summary>ambientCG (#69): search → download a PBR zip → the generated .vmat (albedo, normal, roughness wired)
        /// lands in the project and renders in the Material Editor's sphere preview.</summary>
        public static async Task<bool> RunMaterial()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("store material smoke: " + why); return false; }
            var panel = AssetBrowserPanel.Current;
            if (panel == null || ProjectData.Current == null) return Fail("no asset browser / project");
            bool live = Environment.GetEnvironmentVariable("VORTEX_STORE_LIVE") == "1";
            var oldHandler = StoreHttp.Handler;
            if (!live)
            {
                var fake = new Fake();
                var orange = Color.FromRgb(0xD0, 0x6A, 0x1E);
                var zip = Zip(("SmokePlanks001_1K-PNG_Color.png", Map(orange, Color.FromRgb(0x5A, 0x2E, 0x10))),
                              ("SmokePlanks001_1K-PNG_NormalGL.png", Map(Color.FromRgb(0x80, 0x80, 0xFF), Color.FromRgb(0x80, 0x5C, 0xE8))),
                              ("SmokePlanks001_1K-PNG_Roughness.png", Map(Color.FromRgb(0xA0, 0xA0, 0xA0), Color.FromRgb(0xE8, 0xE8, 0xE8))),
                              ("SmokePlanks001.png", Png(orange)));   // the preview image of the zip — not a map
                fake.Files["https://ambientcg.com/api/v3/assets"] = Encoding.UTF8.GetBytes(
                    "{\"totalResults\":1,\"assets\":[{\"id\":\"SmokePlanks001\",\"title\":\"Smoke Planks 001\",\"tags\":[\"wood\",\"planks\"]," +
                    "\"thumbnails\":{\"256-PNG\":\"https://acg.test/SmokePlanks001.png\"},\"dimensions\":{\"width\":200,\"height\":200}," +
                    "\"downloads\":[{\"attributes\":\"1K-PNG\",\"extension\":\"zip\",\"size\":" + zip.Length + "}]}]}");
                fake.Files["https://ambientcg.com/get?file=SmokePlanks001_1K-PNG.zip"] = zip;
                fake.Files["https://acg.test/SmokePlanks001.png"] = Png(orange);
                StoreHttp.Handler = fake;
                StoreHttp.ClearResponses("ambientcg");
            }
            MaterialEditorWindow editor = null;
            string folder = null;
            try
            {
                panel.SetTab("Store");
                var store = StoreView.Current;
                if (store == null) return Fail("no store view");
                store.SelectProvider("ambientcg");
                panel.SetSearch(live ? "planks" : "smoke planks");
                for (int i = 0; i < 80 && (store.Loading || store.Tiles.Count == 0); i++) await Task.Delay(100);
                if (store.Tiles.Count == 0) return Fail("no results (" + store.LastError + ")");
                store.Select(store.Tiles[0]);
                await SmokeRegistry.Settle(400);
                var job = await store.Download(addToProject: true);
                if (job == null) return Fail("download did not start");
                for (int i = 0; i < 1200 && job.IsActive; i++) await Task.Delay(100);
                if (job.State != StoreJobState.Done) return Fail("download " + job.State + ": " + job.Error);
                string vmat = job.ProjectPaths.FirstOrDefault();
                if (vmat == null || !vmat.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase) || !File.Exists(vmat)) return Fail("no .vmat in the project (" + vmat + ")");
                folder = Path.GetDirectoryName(vmat);   // Add to Project gives an asset with files its own folder
                var mat = VortexMaterial.Load(vmat);
                if (mat?.AlbedoTexture == null || mat.NormalTexture == null || mat.RoughnessTexture == null) return Fail("maps not wired into the .vmat");
                foreach (var t in MaterialBuilder.TexturePaths(mat))
                    if (!File.Exists(Path.Combine(folder, t.Replace('/', Path.DirectorySeparatorChar)))) return Fail("texture missing in the project: " + t);
                if (GlobalAssetDatabase.Instance.Get(job.Entries[0].Id)?.License != "CC0-1.0") return Fail("library entry has no CC0 license");

                MaterialEditorWindow.Open(vmat);
                editor = MaterialEditorWindow.Find(vmat);
                if (editor == null) return Fail("the Material Editor did not open");
                var pane = editor.PreviewPane;
                for (int i = 0; i < 60 && (pane.Viewport.LastImage == null || pane.Builds == 0); i++) await Task.Delay(100);
                await SmokeRegistry.Settle(800);
                var img = pane.Viewport.LastImage;
                if (!MaterialPackageSmoke.HasContent(img)) return Fail("the material preview rendered nothing");
                double warm = Warmth(img);
                if (!live && warm < 0.08) return Fail("the preview does not show the albedo map (warmth " + warm.ToString("0.00") + ")");
                SmokeRegistry.Capture(editor, "store_material.png");
                log.Log("store material smoke: OK (" + (live ? "live ambientCG" : "scripted server") + ", warmth " + warm.ToString("0.00") + ") → " + Ui.ProjectRelative(vmat));
                return true;
            }
            finally
            {
                editor?.Close();
                if (folder != null) try { Directory.Delete(folder, true); } catch { }
                panel.SetSearch("");
                panel.SetTab("Explorer");
                if (!live) { StoreHttp.Handler = oldHandler; StoreHttp.ClearResponses("ambientcg"); }
            }
        }

        public static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            bool Fail(string why) { log.LogError("store smoke: " + why); return false; }
            var panel = AssetBrowserPanel.Current;
            if (panel == null || ProjectData.Current == null) return Fail("no asset browser / project");
            bool live = Environment.GetEnvironmentVariable("VORTEX_STORE_LIVE") == "1";
            var oldHandler = StoreHttp.Handler;
            if (!live)
            {
                var fake = new Fake();
                var gltf = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"Smoke_Chair.bin\",\"byteLength\":4}],\"images\":[{\"uri\":\"textures/smoke_chair_diff_1k.png\"}]}");
                var bin = new byte[] { 1, 2, 3, 4 };
                var tex = Png(Color.FromRgb(0xC0, 0x80, 0x40));
                fake.Files["https://api.polyhaven.com/assets?type=models"] = Encoding.UTF8.GetBytes(
                    "{\"Smoke_Chair\":{\"name\":\"Smoke Chair\",\"type\":2,\"tags\":[\"chair\"],\"categories\":[\"furniture\"],\"authors\":{\"Smoke Tester\":\"All\"},\"download_count\":5}," +
                    "\"Smoke_Table\":{\"name\":\"Smoke Table\",\"type\":2,\"tags\":[\"table\"],\"categories\":[\"furniture\"],\"authors\":{\"Smoke Tester\":\"All\"},\"download_count\":3}}");
                fake.Files["https://api.polyhaven.com/categories/models"] = Encoding.UTF8.GetBytes("{\"all\":2,\"furniture\":2}");
                fake.Files["https://api.polyhaven.com/files/Smoke_Chair"] = Encoding.UTF8.GetBytes("{\"gltf\":{\"1k\":{\"gltf\":{\"url\":\"https://dl.test/Smoke_Chair_1k.gltf\",\"size\":" + gltf.Length + ",\"md5\":\"" + Md5(gltf) +
                    "\",\"include\":{\"Smoke_Chair.bin\":{\"url\":\"https://dl.test/Smoke_Chair.bin\",\"size\":4,\"md5\":\"" + Md5(bin) + "\"},\"textures/smoke_chair_diff_1k.png\":{\"url\":\"https://dl.test/diff.png\",\"size\":" + tex.Length + ",\"md5\":\"" + Md5(tex) + "\"}}}}}}");
                fake.Files["https://dl.test/Smoke_Chair_1k.gltf"] = gltf;
                fake.Files["https://dl.test/Smoke_Chair.bin"] = bin;
                fake.Files["https://dl.test/diff.png"] = tex;
                fake.Files["https://cdn.polyhaven.com/asset_img/thumbs/Smoke_Chair.png"] = Png(Color.FromRgb(0x4E, 0xC9, 0xB0));
                fake.Files["https://cdn.polyhaven.com/asset_img/thumbs/Smoke_Table.png"] = Png(Color.FromRgb(0xBD, 0x63, 0xC5));
                StoreHttp.Handler = fake;
                StoreHttp.ClearResponses("polyhaven");
            }
            try
            {
                panel.SetTab("Store");
                var store = StoreView.Current;
                if (store == null) return Fail("no store view");
                store.SelectProvider("polyhaven");
                panel.SetSearch(live ? "armchair" : "chair");
                for (int i = 0; i < 80 && (store.Loading || store.Tiles.Count == 0); i++) await Task.Delay(100);
                if (store.Tiles.Count == 0) return Fail("no results (" + store.LastError + ")");
                var tile = store.Tiles[0];
                for (int i = 0; i < 60 && !tile.HasThumbnail; i++) await Task.Delay(100);
                if (!tile.HasThumbnail) return Fail("result thumbnail did not load");
                store.Select(tile);
                await SmokeRegistry.Settle(800);
                SmokeRegistry.Capture(EditorCommands.Window, "store_tab.png");
                var job = await store.Download(addToProject: true);
                if (job == null) return Fail("download did not start");
                for (int i = 0; i < 600 && job.IsActive; i++) await Task.Delay(100);
                if (job.State != StoreJobState.Done) return Fail("download " + job.State + ": " + job.Error);
                if (job.ProjectPaths.Count != 1 || !File.Exists(job.ProjectPaths[0])) return Fail("not added to the project");
                var e = GlobalAssetDatabase.Instance.Get(job.Entries[0].Id);
                if (e.Companions.Count == 0 || e.SourceName != "Poly Haven" || e.License != "CC0-1.0") return Fail("library entry incomplete");
                var meta = Editor.Core.Serialization.DataSerializer.LoadFromJson<AssetMetadata>(job.ProjectPaths[0] + AssetDatabase.MetaFileExtension);
                if (meta?.License != "CC0-1.0") return Fail("project .vmeta has no license");
                await SmokeRegistry.Settle(500);
                SmokeRegistry.Capture(EditorCommands.Window, "store_downloaded.png");
                if (!tile.InLibrary) return Fail("tile not marked as in the library");
                log.Log("store smoke: OK (" + (live ? "live Poly Haven" : "scripted server") + ") → " + Ui.ProjectRelative(job.ProjectPaths[0]));
                // leave the project as it was
                try { Directory.Delete(Path.GetDirectoryName(job.ProjectPaths[0]), true); } catch { }
                panel.SetSearch("");
                panel.SetTab("Explorer");
                return true;
            }
            finally { if (!live) { StoreHttp.Handler = oldHandler; StoreHttp.ClearResponses("polyhaven"); } }
        }
    }
}
