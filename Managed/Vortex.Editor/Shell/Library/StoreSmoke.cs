using System;
using System.Collections.Generic;
using System.IO;
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
using VortexEditor.Panels;
using VortexEditor.Panels.AssetBrowser;
using VortexEditor.Shell.ModelTools;

namespace VortexEditor.Shell.Library
{
    /// <summary>
    /// Editor smoke check of the Store tab (VORTEX_SMOKE_ONLY=store): Poly Haven search → result tiles with thumbnails →
    /// details → "Download + Add to Project" → the model lands in the library (with its files, source and license) and
    /// in the project. Runs against a scripted HTTP server by default; VORTEX_STORE_LIVE=1 uses the real Poly Haven API.
    /// </summary>
    internal static class StoreSmoke
    {
        [ModuleInitializer]
        internal static void Register() => SmokeRegistry.Add("asset store", Run);

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
