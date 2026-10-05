using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Assets.Library;
using Editor.Core.Assets.Store;
using Editor.Core.Serialization;

namespace VortexTests
{
    /// <summary>A scripted HTTP server: routes by URL prefix, supports HTTP ranges and failure injection.</summary>
    internal sealed class FakeHttp : HttpMessageHandler
    {
        public readonly List<(string prefix, Func<HttpRequestMessage, HttpResponseMessage> respond)> Routes = new List<(string, Func<HttpRequestMessage, HttpResponseMessage>)>();
        public readonly List<HttpRequestMessage> Requests = new List<HttpRequestMessage>();

        public void Json(string prefix, string json) => Routes.Add((prefix, r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        public void Text(string prefix, string text) => Routes.Add((prefix, r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) }));
        public void Status(string prefix, HttpStatusCode code) => Routes.Add((prefix, r => new HttpResponseMessage(code) { Content = new StringContent("") }));

        /// <summary>Serve bytes with Range support; <paramref name="failFirstAfter"/> breaks the first response after N bytes.</summary>
        public void Bytes(string prefix, byte[] data, int failFirstAfter = -1)
        {
            int calls = 0;
            Routes.Add((prefix, r =>
            {
                calls++;
                long from = r.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
                var slice = data.Skip((int)from).ToArray();
                Stream body = new MemoryStream(slice);
                if (calls == 1 && failFirstAfter > 0) body = new BrokenStream(slice, failFirstAfter);
                var resp = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(body) };
                resp.Content.Headers.ContentLength = slice.Length;
                return resp;
            }));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            string url = request.RequestUri.ToString();
            var route = Routes.Where(x => url.StartsWith(x.prefix, StringComparison.Ordinal)).OrderByDescending(x => x.prefix.Length).FirstOrDefault();
            if (route.respond == null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no route: " + url) });
            return Task.FromResult(route.respond(request));
        }

        private sealed class BrokenStream : MemoryStream
        {
            private readonly int _failAfter;
            public BrokenStream(byte[] data, int failAfter) : base(data) { _failAfter = failAfter; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Position >= _failAfter) throw new IOException("connection reset (test)");
                return base.Read(buffer, offset, (int)Math.Min(count, _failAfter - Position));
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(Read(buffer, offset, count));
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                var tmp = new byte[buffer.Length];
                int n = Read(tmp, 0, tmp.Length);
                tmp.AsSpan(0, n).CopyTo(buffer.Span);
                return new ValueTask<int>(n);
            }
        }
    }

    /// <summary>Asset Store (milestone v2.10.0): licenses, providers, the download → import pipeline.</summary>
    public static class StoreTests
    {
        private static FakeHttp Setup(TestContext t)
        {
            Environment.SetEnvironmentVariable("VORTEX_ASSETDB_DIR", t.Path("lib"));
            GlobalAssetDatabase.ResetInstance();
            StoreHttp.CacheRootOverride = t.Path("cache");
            var fake = new FakeHttp();
            StoreHttp.Handler = fake;
            return fake;
        }

        private static async Task<StoreJob> Run(StoreJob j)
        {
            for (int i = 0; i < 400 && j.IsActive; i++) await Task.Delay(25);
            return j;
        }

        private static string Md5(byte[] b) { using (var m = MD5.Create()) return ContentHash.Hex(m.ComputeHash(b)); }

        [Test]
        public static void LicenseMapping(TestContext t)
        {
            t.Equal("CC0-1.0", StoreLicense.FromUrl("http://creativecommons.org/publicdomain/zero/1.0/").Id, "cc0 url");
            t.Equal("CC-BY-NC-4.0", StoreLicense.FromUrl("https://creativecommons.org/licenses/by-nc/4.0/").Id, "nc url");
            t.Equal("CC-BY-3.0", StoreLicense.FromUrl("http://creativecommons.org/licenses/by/3.0/").Id, "by 3.0 url");
            t.Equal("CC-BY-4.0", StoreLicense.FromLabel("CC Attribution").Id, "sketchfab label");
            t.Equal("CC-BY-NC-SA-4.0", StoreLicense.FromLabel("CC Attribution-NonCommercial-ShareAlike").Id, "nc-sa label");
            t.Equal("CC0-1.0", StoreLicense.FromLabel("CC0 1.0").Id, "poly.pizza label");
            t.Equal("CC-BY-3.0", StoreLicense.FromLabel("CC-BY 3.0").Id, "poly.pizza by label");
            t.Equal("CC BY-NC-SA", StoreLicense.Get("CC-BY-NC-SA-4.0").Badge, "badge");
            t.True(StoreLicense.Get("CC-BY-4.0").GameSafe, "BY is game-safe");
            t.False(StoreLicense.Get("CC-BY-NC-4.0").GameSafe, "NC is not");
            t.False(StoreLicense.Get("Mixamo").Redistributable, "Mixamo can't be re-shared");
            var q = new StoreQuery();
            t.False(q.Allows(StoreLicense.Get("CC-BY-NC-4.0")), "NC filtered by default");
            t.False(q.Allows(StoreLicense.Get("CC-BY-ND-4.0")), "ND filtered by default (game assets get modified)");
            t.True(q.Allows(StoreLicense.Get("CC-BY-SA-4.0")), "SA shown by default (flagged before builds)");
            t.True(q.Allows(StoreLicense.Get("CC-BY-4.0")) && q.Allows(StoreLicense.Get("CC0-1.0")), "BY / CC0 always shown");
            q.IncludeNonCommercial = true;
            t.True(q.Allows(StoreLicense.Get("CC-BY-NC-4.0")), "NC allowed when asked");
            t.False(q.Allows(StoreLicense.Get("CC-BY-NC-ND-4.0")), "NC-ND still hidden without the ND switch");
            q.IncludeNoDerivatives = true;
            t.True(q.Allows(StoreLicense.Get("CC-BY-NC-ND-4.0")), "NC-ND allowed when both are asked for");
            t.False(StoreLicense.Get("ElevenLabs").Redistributable || StoreLicense.Get("fal-ai").Redistributable || StoreLicense.Get("Stability-AI").Redistributable,
                    "generated audio is not re-shared as files");
        }

        /// <summary>Counts API requests against a daily budget (Freesound: 2,000 per day).</summary>
        private sealed class LimitedProvider : IAssetProvider
        {
            public string Id => "limited";
            public string Name => "Limited";
            public string Tagline => "";
            public string HomeUrl => "https://limited.test";
            public string Attribution => "";
            public IReadOnlyList<StoreKind> Kinds { get; } = new[] { StoreKind.Sound };
            public ProviderAccess Access => ProviderAccess.Anonymous;
            public string KeyName => null;
            public string KeyHelpUrl => null;
            public bool HasLicenseFilter => false;
            public TimeSpan MinInterval => TimeSpan.Zero;
            public int DailyRequestLimit => 2;
            public Task<StorePage> SearchAsync(StoreQuery query, CancellationToken ct) => throw new NotSupportedException();
            public Task<IReadOnlyList<string>> CategoriesAsync(StoreKind kind, CancellationToken ct) => throw new NotSupportedException();
            public Task<StoreDetails> DetailsAsync(StoreItem item, CancellationToken ct) => throw new NotSupportedException();
            public Task<DownloadPlan> ResolveAsync(StoreItem item, StoreVariant variant, CancellationToken ct) => throw new NotSupportedException();
            public void Decorate(HttpRequestMessage request) { }
        }

        [Test]
        public static async Task DailyRequestLimit(TestContext t)
        {
            var fake = Setup(t);
            StoreHttp.ReloadUsage();
            fake.Json("https://limited.test/", "{}");
            var p = new LimitedProvider();
            await StoreHttp.GetTextAsync(p, "https://limited.test/a", TimeSpan.FromHours(1), CancellationToken.None);
            await StoreHttp.GetTextAsync(p, "https://limited.test/a", TimeSpan.FromHours(1), CancellationToken.None);
            t.Equal(1, StoreHttp.RequestsToday("limited"), "a cached response doesn't count");
            await StoreHttp.GetTextAsync(p, "https://limited.test/b", TimeSpan.Zero, CancellationToken.None);
            StoreHttp.StoreHttpException hit = null;
            try { await StoreHttp.GetTextAsync(p, "https://limited.test/c", TimeSpan.Zero, CancellationToken.None); }
            catch (StoreHttp.StoreHttpException ex) { hit = ex; }
            t.True(hit != null && hit.LimitReached && hit.Status == HttpStatusCode.TooManyRequests && hit.Message.Contains("daily limit of 2"), "third request refused: " + hit?.Message);
            t.Equal(2, fake.Requests.Count, "the refused request never left the machine");
            t.True((await StoreHttp.GetTextAsync(p, "https://limited.test/a", TimeSpan.FromHours(1), CancellationToken.None)) == "{}", "cached results still work");
            StoreHttp.ReloadUsage();
            t.Equal(2, StoreHttp.RequestsToday("limited"), "the count survives a restart");
            t.Equal(2000, new FreesoundProvider().DailyRequestLimit, "Freesound's documented limit");
        }

        [Test]
        public static async Task DiskSpaceIsCheckedAndExplained(TestContext t)
        {
            var fake = Setup(t);
            t.True(StoreDownloads.IsDiskFull(new IOException("No space left on device", OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28)), "ENOSPC / ERROR_DISK_FULL");
            t.True(StoreDownloads.IsDiskFull(new InvalidOperationException("wrapped", new StoreDownloads.DiskSpaceException("full"))), "found in inner exceptions");
            t.False(StoreDownloads.IsDiskFull(new IOException("connection reset")), "other IO errors are not");
            t.True(StoreDownloads.FreeBytes(t.Dir) > 0, "free space of the test volume is known");
            // a download bigger than the disk fails before a single byte is fetched
            fake.Json("https://ambientcg.com/api/v3/assets", "{\"totalResults\":1,\"assets\":[{\"id\":\"Huge001\",\"title\":\"Huge 001\"," +
                "\"downloads\":[{\"attributes\":\"1K-JPG\",\"extension\":\"zip\",\"size\":4000000000000000}]}]}");
            var p = new AmbientCgProvider();
            var item = (await p.SearchAsync(new StoreQuery { Text = "huge" }, CancellationToken.None)).Items.Single();
            var det = await p.DetailsAsync(item, CancellationToken.None);
            var job = await Run(StoreDownloads.Enqueue(p, item, det.Variants.Single()));
            t.Equal(StoreJobState.Failed, job.State, "failed");
            t.True(job.Error.StartsWith("Not enough disk space") && job.Error.Contains("Library Settings"), "actionable message: " + job.Error);
            t.Equal(1, job.Attempts, "not retried");
            t.False(fake.Requests.Any(r => r.RequestUri.ToString().Contains("/get?file=")), "nothing downloaded");
        }

        [Test]
        public static void MaterialRolesFromFileNames(TestContext t)
        {
            t.Equal("Albedo", MaterialBuilder.RoleOf("Wood096_1K-JPG_Color.jpg"), "ambientCG color");
            t.Equal("NormalGL", MaterialBuilder.RoleOf("Wood096_1K-JPG_NormalGL.jpg"), "ambientCG GL normal");
            t.Equal("NormalDX", MaterialBuilder.RoleOf("Wood096_1K-JPG_NormalDX.jpg"), "ambientCG DX normal");
            t.Equal("AO", MaterialBuilder.RoleOf("Wood096_1K-JPG_AmbientOcclusion.jpg"), "ambientCG AO");
            t.Equal("Metallic", MaterialBuilder.RoleOf("Metal063_1K-JPG_Metalness.jpg"), "metalness");
            t.Equal("Height", MaterialBuilder.RoleOf("Wood096_1K-JPG_Displacement.jpg"), "displacement");
            t.Equal("Albedo", MaterialBuilder.RoleOf("brick_wall_diff_2k.jpg"), "poly haven diff");
            t.Equal("ARM", MaterialBuilder.RoleOf("brick_wall_arm_2k.jpg"), "poly haven arm");
            t.Equal("NormalGL", MaterialBuilder.RoleOf("brick_wall_nor_gl_2k.jpg"), "poly haven nor_gl");
            t.True(MaterialBuilder.RoleOf("Wood096.png") == null, "preview without role");
            t.True(MaterialBuilder.RoleOf("Wood096_PREVIEW.jpg") == null, "preview skipped");
        }

        private const string PolyHavenAssets = @"{
 ""Old_Chair"": {""name"": ""Old Chair"", ""type"": 2, ""tags"": [""chair"", ""wood""], ""categories"": [""furniture""], ""authors"": {""Jane"": ""All""}, ""download_count"": 50, ""polycount"": 1200, ""dimensions"": [500, 900, 450]},
 ""Rusty_Barrel"": {""name"": ""Rusty Barrel"", ""type"": 2, ""tags"": [""barrel""], ""categories"": [""containers""], ""authors"": {""Joe"": ""All""}, ""download_count"": 99}
}";

        [Test]
        public static async Task PolyHavenModelEndToEnd(TestContext t)
        {
            var fake = Setup(t);
            var gltf = Encoding.UTF8.GetBytes("{\"buffers\":[{\"uri\":\"Old_Chair.bin\"}],\"images\":[{\"uri\":\"textures/chair_diff_1k.jpg\"}]}");
            var bin = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
            var tex = Encoding.UTF8.GetBytes("jpeg-bytes-of-the-chair");
            fake.Json("https://api.polyhaven.com/assets?type=models", PolyHavenAssets);
            fake.Json("https://api.polyhaven.com/files/Old_Chair", "{\"gltf\":{\"1k\":{\"gltf\":{\"url\":\"https://dl.polyhaven.org/Old_Chair_1k.gltf\",\"size\":" + gltf.Length + ",\"md5\":\"" + Md5(gltf) +
                "\",\"include\":{\"Old_Chair.bin\":{\"url\":\"https://dl.polyhaven.org/Old_Chair.bin\",\"size\":" + bin.Length + ",\"md5\":\"" + Md5(bin) + "\"},\"textures/chair_diff_1k.jpg\":{\"url\":\"https://dl.polyhaven.org/chair_diff_1k.jpg\",\"size\":" + tex.Length + ",\"md5\":\"" + Md5(tex) + "\"}}}}}}");
            fake.Bytes("https://dl.polyhaven.org/Old_Chair_1k.gltf", gltf);
            fake.Bytes("https://dl.polyhaven.org/Old_Chair.bin", bin, failFirstAfter: 1200);   // the bin download breaks once → resumed
            fake.Bytes("https://dl.polyhaven.org/chair_diff_1k.jpg", tex);
            fake.Bytes("https://cdn.polyhaven.com/asset_img/thumbs/Old_Chair.png", new byte[] { 1, 2, 3 });
            var p = new PolyHavenProvider();
            var page = await p.SearchAsync(new StoreQuery { Text = "chair", Kind = StoreKind.Model }, CancellationToken.None);
            t.Equal(1, page.Items.Count, "search finds the chair only");
            var item = page.Items[0];
            t.Equal("Jane", item.Author, "author");
            t.Equal("CC0-1.0", item.License.Id, "license");
            var det = await p.DetailsAsync(item, CancellationToken.None);
            t.Equal("1k", det.Variants.Single().Id, "one resolution");
            t.Equal((long)(gltf.Length + bin.Length + tex.Length), det.Variants[0].Size, "variant size = all files");
            string project = t.Path("game");
            Directory.CreateDirectory(Path.Combine(project, "Assets"));
            var job = await Run(StoreDownloads.Enqueue(p, item, det.Variants[0], addToProject: true, projectRoot: project, projectName: "Game"));
            t.Equal(StoreJobState.Done, job.State, "done: " + job.Error);
            var e = GlobalAssetDatabase.Instance.Get(job.Entries.Single().Id);
            t.Equal(AssetType.Mesh, e.Type, "model entry");
            t.Equal(2, e.Companions.Count, "bin + texture travel with it");
            t.Equal("Poly Haven", e.SourceName, "source");
            t.Equal("CC0-1.0", e.License, "license recorded");
            t.True(GlobalAssetDatabase.Instance.HasThumbnail(e.Hash), "store preview became the thumbnail");
            t.True(fake.Requests.Any(r => r.RequestUri.ToString().EndsWith("Old_Chair.bin") && r.Headers.Range != null), "the broken download resumed with a Range request");
            t.True(fake.Requests.All(r => r.Headers.UserAgent.ToString().StartsWith("VortexEngine/")), "unique User-Agent on every request");
            string path = job.ProjectPaths.Single();
            t.True(File.Exists(path), "model in the project");
            t.True(File.Exists(Path.Combine(Path.GetDirectoryName(path), "textures", "chair_diff_1k.jpg")), "texture next to it");
            var meta = DataSerializer.LoadFromJson<AssetMetadata>(path + ".vmeta");
            t.Equal("CC0-1.0", meta.License, "license in the project .vmeta");
            t.Equal("Poly Haven", meta.Source, "source in the project .vmeta");
            // a second download of the same item comes from the library
            int before = fake.Requests.Count;
            var again = await Run(StoreDownloads.Enqueue(p, item, det.Variants[0]));
            t.True(again.AlreadyInLibrary && again.State == StoreJobState.Done, "second download skipped");
            t.Equal(before, fake.Requests.Count, "no new requests");
        }

        [Test]
        public static async Task ChecksumMismatchFails(TestContext t)
        {
            var fake = Setup(t);
            fake.Json("https://api.polyhaven.com/files/Rusty_Barrel", "{\"hdri\":{\"1k\":{\"hdr\":{\"url\":\"https://dl.polyhaven.org/barrel.hdr\",\"size\":5,\"md5\":\"00000000000000000000000000000000\"}}}}");
            fake.Bytes("https://dl.polyhaven.org/barrel.hdr", Encoding.UTF8.GetBytes("hello"));
            var item = new StoreItem { ProviderId = "polyhaven", Id = "Rusty_Barrel", Name = "Rusty Barrel", Kind = StoreKind.Hdri, License = StoreLicense.Get("CC0-1.0") };
            var job = await Run(StoreDownloads.Enqueue(new PolyHavenProvider(), item, new StoreVariant { Id = "1k" }));
            t.Equal(StoreJobState.Failed, job.State, "failed");
            t.True(job.Error.Contains("checksum"), "says why: " + job.Error);
            t.Equal(0, GlobalAssetDatabase.Instance.Count(), "nothing registered");
        }

        private static byte[] Zip(params (string name, byte[] data)[] files)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    foreach (var (name, data) in files) { var e = z.CreateEntry(name); using (var s = e.Open()) s.Write(data, 0, data.Length); }
                return ms.ToArray();
            }
        }

        [Test]
        public static async Task AmbientCgZipBecomesMaterial(TestContext t)
        {
            var fake = Setup(t);
            byte[] B(string s) => Encoding.UTF8.GetBytes(s);
            var zip = Zip(("Bricks010_1K-JPG_Color.jpg", B("c")), ("Bricks010_1K-JPG_NormalGL.jpg", B("ngl")), ("Bricks010_1K-JPG_NormalDX.jpg", B("ndx")),
                          ("Bricks010_1K-JPG_Roughness.jpg", B("r")), ("Bricks010_1K-JPG_AmbientOcclusion.jpg", B("ao")), ("Bricks010_1K-JPG_Displacement.jpg", B("d")),
                          ("Bricks010.png", B("preview")), ("Bricks010_1K-JPG.usdc", B("usd")));
            fake.Json("https://ambientcg.com/api/v3/assets", "{\"totalResults\":1,\"assets\":[{\"id\":\"Bricks010\",\"title\":\"Bricks 010\",\"tags\":[\"brick\",\"010\"],\"thumbnails\":{\"256-PNG\":\"https://acg.test/thumb.png\"}," +
                "\"downloads\":[{\"attributes\":\"1K-JPG\",\"extension\":\"zip\",\"url\":\"https://ambientcg.com/get?file=Bricks010_1K-JPG.zip\",\"size\":" + zip.Length + "}]}]}");
            fake.Bytes("https://ambientcg.com/get?file=Bricks010_1K-JPG.zip", zip);
            var p = new AmbientCgProvider();
            var page = await p.SearchAsync(new StoreQuery { Text = "brick" }, CancellationToken.None);
            var item = page.Items.Single();
            t.Equal("Bricks 010", item.Name, "title");
            t.False(item.Tags.Contains("010"), "number-only tags dropped");
            var det = await p.DetailsAsync(item, CancellationToken.None);
            var job = await Run(StoreDownloads.Enqueue(p, item, det.Variants.Single()));
            t.Equal(StoreJobState.Done, job.State, "done: " + job.Error);
            var e = GlobalAssetDatabase.Instance.Get(job.Entries.Single().Id);
            t.Equal(AssetType.Material, e.Type, ".vmat entry");
            t.Equal(5, e.Companions.Count, "color, GL normal, roughness, AO, height (no DX duplicate, preview or usdc)");
            t.True(e.Companions.All(c => c.RelPath.StartsWith("textures/")), "maps in textures/");
            string vmat = Path.Combine(t.Path("check"), "m.vmat");
            Directory.CreateDirectory(Path.GetDirectoryName(vmat));
            File.Copy(GlobalAssetDatabase.Instance.BlobPath(e.Hash), vmat);
            var m = VortexMaterial.Load(vmat);
            t.Equal("textures/Bricks010_1K-JPG_Color.jpg", m.AlbedoTexture, "albedo wired");
            t.Equal("textures/Bricks010_1K-JPG_NormalGL.jpg", m.NormalTexture, "GL normal preferred");
            t.False(m.UseDirectXNormals, "OpenGL convention");
            t.Equal("textures/Bricks010_1K-JPG_AmbientOcclusion.jpg", m.AOTexture, "AO wired");
            t.True(fake.Requests.Any(r => r.RequestUri.Query.Contains("q=brick")), "query sent");
        }

        [Test]
        public static async Task KenneyPackImportsEveryUsefulFile(TestContext t)
        {
            var fake = Setup(t);
            byte[] B(string s) => Encoding.UTF8.GetBytes(s);
            var zip = Zip(("Models/GLB format/crate.glb", B("glb")), ("Models/FBX format/crate.fbx", B("fbx")), ("Textures/colormap.png", B("png")),
                          ("Audio/click_001.ogg", B("ogg")), ("License.txt", B("cc0")), ("Preview.png", B("pv")));
            fake.Text("https://kenney.nl/assets/survival-kit", "<html><meta property='og:image'\tcontent='https://kenney.nl/media/pages/assets/survival-kit/abc-1/preview.png' />" +
                      "<a href='https://kenney.nl/media/pages/assets/survival-kit/f00-17/kenney_survival-kit.zip'>Download</a></html>");
            fake.Bytes("https://kenney.nl/media/pages/assets/survival-kit/f00-17/kenney_survival-kit.zip", zip);
            var p = new KenneyProvider();
            var page = await p.SearchAsync(new StoreQuery { Text = "survival" }, CancellationToken.None);
            var item = page.Items.Single();
            t.Equal("https://kenney.nl/media/pages/assets/survival-kit/abc-1/preview.png", await ((IAssetProvider)p).ThumbnailUrlAsync(item, CancellationToken.None), "preview from the page");
            var job = await Run(StoreDownloads.Enqueue(p, item, new StoreVariant { Id = "zip" }));
            t.Equal(StoreJobState.Done, job.State, "done: " + job.Error);
            var names = job.Entries.Select(e => e.FileName).OrderBy(x => x).ToList();
            t.Equal("click_001.ogg,colormap.png,crate.glb", string.Join(",", names), "GLB preferred over FBX; license/preview skipped");
            t.True(job.Entries.All(e => e.Tags.Contains("Survival Kit")), "tagged with the pack");
            var again = await Run(StoreDownloads.Enqueue(p, item, new StoreVariant { Id = "zip" }));
            t.True(again.AlreadyInLibrary, "pack not downloaded twice");
            t.Equal(3, again.Entries.Count, "all pack entries found by the store key");
        }

        [Test]
        public static async Task KeyedProvidersNeedTheUsersKey(TestContext t)
        {
            var fake = Setup(t);
            Environment.SetEnvironmentVariable("VORTEX_STORE_KEY_FREESOUND", null);
            StoreKeys.Reload();
            var fs = new FreesoundProvider();
            var none = await fs.SearchAsync(new StoreQuery { Text = "door" }, CancellationToken.None);
            t.True(none.NeedsKey, "no key → asks for one");
            Environment.SetEnvironmentVariable("VORTEX_STORE_KEY_FREESOUND", "test-token");
            try
            {
                fake.Json("https://freesound.org/apiv2/search/text/", "{\"count\":2,\"next\":null,\"results\":[" +
                    "{\"id\":1,\"name\":\"Door creak\",\"username\":\"a\",\"license\":\"http://creativecommons.org/publicdomain/zero/1.0/\",\"previews\":{\"preview-hq-mp3\":\"https://cdn.fs/1.mp3\",\"preview-hq-ogg\":\"https://cdn.fs/1.ogg\"},\"duration\":2.5,\"images\":{\"waveform_m\":\"https://cdn.fs/1.png\"}}," +
                    "{\"id\":2,\"name\":\"NC creak\",\"username\":\"b\",\"license\":\"http://creativecommons.org/licenses/by-nc/3.0/\",\"previews\":{}}]}");
                var page = await fs.SearchAsync(new StoreQuery { Text = "door" }, CancellationToken.None);
                t.Equal(1, page.Items.Count, "NC result dropped by the default filter");
                t.Equal("https://cdn.fs/1.mp3", page.Items[0].PreviewUrl, "audition preview");
                var req = fake.Requests.Last();
                t.Equal("Token test-token", req.Headers.Authorization?.ToString() ?? string.Join(",", req.Headers.GetValues("Authorization")), "token header");
                t.True(Uri.UnescapeDataString(req.RequestUri.Query).Contains("license:(\"Creative Commons 0\" OR \"Attribution\")"), "license filter in the query");
                var plan = await fs.ResolveAsync(page.Items[0], new StoreVariant { Id = "ogg" }, CancellationToken.None);
                t.Equal("https://cdn.fs/1.ogg", plan.Files.Single().Url, "HQ ogg preview downloaded");
            }
            finally { Environment.SetEnvironmentVariable("VORTEX_STORE_KEY_FREESOUND", null); }
        }

        [Test]
        public static async Task PolyPizzaSearchAndDownload(TestContext t)
        {
            var fake = Setup(t);
            Environment.SetEnvironmentVariable("VORTEX_STORE_KEY_POLYPIZZA", "pp-test");
            StoreKeys.Reload();
            try
            {
                fake.Json("https://api.poly.pizza/v1.1/search/", "{\"total\":1,\"results\":[{\"ID\":\"abc\",\"Title\":\"Wooden Crate\",\"Thumbnail\":\"https://static.poly.pizza/abc.webp\"," +
                    "\"Licence\":\"CC0 1.0\",\"Creator\":{\"Username\":\"Quaternius\"},\"Download\":\"https://static.poly.pizza/abc.glb\",\"Tri Count\":1200,\"Animated\":true,\"Category\":\"Props\",\"Tags\":[\"crate\"]}]}");
                fake.Bytes("https://static.poly.pizza/abc.glb", Encoding.UTF8.GetBytes("glTF test model"));
                var p = new PolyPizzaProvider();
                var page = await p.SearchAsync(new StoreQuery { Text = "wooden crate" }, CancellationToken.None);
                var it = page.Items.Single();
                var req = fake.Requests.Last();
                t.Equal("pp-test", req.Headers.GetValues("x-auth-token").Single(), "x-auth-token header");
                t.True(Uri.UnescapeDataString(req.RequestUri.AbsolutePath).EndsWith("/v1.1/search/wooden crate"), "keyword in the path: " + req.RequestUri);
                t.Equal("Quaternius", it.Author, "creator");
                t.Equal("CC0-1.0", it.License.Id, "licence label");
                t.True(it.Tags.Contains("animated") && it.Categories.Contains("Props"), "animated flag + category");
                var det = await p.DetailsAsync(it, CancellationToken.None);
                t.True(det.Facts.Contains("Rigged + animated"), "details say rigged");
                var job = await Run(StoreDownloads.Enqueue(p, it, det.Variants.Single()));
                t.Equal(StoreJobState.Done, job.State, "done: " + job.Error);
                var e = GlobalAssetDatabase.Instance.Get(job.Entries.Single().Id);
                t.True(e.FileName.EndsWith(".glb"), "GLB in the library: " + e.FileName);
                t.True(e.License == "CC0-1.0" && e.Author == "Quaternius" && e.SourceName == "poly.pizza", "license, author, source recorded");
            }
            finally { Environment.SetEnvironmentVariable("VORTEX_STORE_KEY_POLYPIZZA", null); StoreKeys.Reload(); }
        }

        [Test]
        public static async Task SketchfabLicensesAndTokenDownloads(TestContext t)
        {
            var fake = Setup(t);
            Environment.SetEnvironmentVariable("VORTEX_STORE_KEY_SKETCHFAB", null);
            StoreKeys.Reload();
            string M(string uid, string name, string label) => "{\"uid\":\"" + uid + "\",\"name\":\"" + name + "\",\"viewerUrl\":\"https://sketchfab.com/3d-models/" + uid +
                "\",\"license\":{\"label\":\"" + label + "\"},\"user\":{\"displayName\":\"Ann\"},\"archives\":{\"glb\":{\"size\":5}}}";
            fake.Json("https://api.sketchfab.com/v3/search", "{\"next\":null,\"results\":[" + M("u1", "Door", "CC Attribution") + "," +
                M("u2", "ND Door", "CC Attribution-NoDerivs") + "," + M("u3", "NC Door", "CC Attribution-NonCommercial") + "]}");
            var p = new SketchfabProvider();
            var page = await p.SearchAsync(new StoreQuery { Text = "door" }, CancellationToken.None);
            t.Equal(1, page.Items.Count, "NoDerivatives and NonCommercial hidden by default");
            t.Equal("CC-BY-4.0", page.Items[0].License.Id, "per-model license");
            t.Equal("Ann", page.Items[0].Author, "author");
            t.False(fake.Requests.Last().Headers.Contains("Authorization"), "search is anonymous");
            var all = await p.SearchAsync(new StoreQuery { Text = "door", IncludeNonCommercial = true, IncludeNoDerivatives = true }, CancellationToken.None);
            t.Equal(3, all.Items.Count, "all three with NC / ND switched on");
            var door = page.Items[0];
            string err = null;
            try { await p.ResolveAsync(door, null, CancellationToken.None); } catch (StoreHttp.StoreHttpException ex) { err = ex.Message; }
            t.True(err != null && err.Contains("API token"), "downloading needs the user's token: " + err);
            Environment.SetEnvironmentVariable("VORTEX_STORE_KEY_SKETCHFAB", "sf-test");
            StoreKeys.Reload();
            try
            {
                int asked = 0;
                fake.Routes.Add(("https://api.sketchfab.com/v3/models/u1/download", r =>
                {
                    asked++;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"glb\":{\"url\":\"https://media.sketchfab.test/u1.glb?sig=" + asked + "\",\"size\":5,\"expires\":300}}") };
                }));
                await p.ResolveAsync(door, new StoreVariant { Id = "glb" }, CancellationToken.None);
                var plan = await p.ResolveAsync(door, new StoreVariant { Id = "glb" }, CancellationToken.None);
                t.Equal(2, asked, "the expiring archive URL is requested for every download, never cached");
                t.True(plan.Files.Single().Url.EndsWith("sig=2"), "fresh URL");
                var dreq = fake.Requests.Last(r => r.RequestUri.AbsolutePath.EndsWith("/download"));
                t.Equal("Token sf-test", string.Join(",", dreq.Headers.GetValues("Authorization")), "token header");
            }
            finally { Environment.SetEnvironmentVariable("VORTEX_STORE_KEY_SKETCHFAB", null); StoreKeys.Reload(); }
        }

        [Test]
        public static void LicenseAuditAndCredits(TestContext t)
        {
            string proj = t.Path("game");
            void Meta(string rel, string license, string author, string source, string url)
            {
                var f = t.Write("game/" + rel, "x");
                var m = new AssetMetadata(AssetType.Texture, rel, Path.GetFileName(rel)) { License = license, Author = author, Source = source, SourceUrl = url };
                DataSerializer.SaveAsJson(m, f + ".vmeta");
            }
            Meta("Assets/Models/Door/door.glb", "CC-BY-4.0", "Ann", "Sketchfab", "https://sketchfab.com/3d-models/door");
            Meta("Assets/Models/Door/textures/door_diff.png", "CC-BY-4.0", "Ann", "Sketchfab", "https://sketchfab.com/3d-models/door");
            Meta("Assets/Audio/scream.ogg", "CC-BY-NC-3.0", "Bob", "Freesound", "https://freesound.org/s/1/");
            Meta("Assets/Materials/Wood/wood.vmat", "CC0-1.0", "ambientCG", "ambientCG", "https://ambientcg.com/view?id=Wood");
            t.Write("game/Assets/own.png", "mine");   // own work: no .vmeta license
            var r = LicenseAudit.Scan(proj);
            t.Equal(4, r.Assets.Count, "licensed files");
            t.Equal(2, r.Credits.Count, "door (once, not per texture) + NC sound need credit");
            t.Equal(1, r.NonCommercial.Count, "NC flagged");
            t.True(r.HasWarnings, "warnings");
            t.True(LicenseAudit.Warnings(r).Contains("scream.ogg"), "warning lists the asset");
            string credits = LicenseAudit.WriteCredits(proj, t.Path("out"), "My Shooter", out int n);
            string md = File.ReadAllText(credits);
            t.True(md.Contains("[\"door\"](https://sketchfab.com/3d-models/door) by Ann (Sketchfab), licensed under [CC-BY-4.0](https://creativecommons.org/licenses/by/4.0/)"), "credit line: " + md);
            t.True(md.Contains("ambientCG"), "thanks section names CC0 sources");
            t.Equal(2, n, "credited count");
        }

        /// <summary>Against the real Poly Haven + ambientCG APIs (network): VORTEX_STORE_LIVE=1 to run it.</summary>
        [Test]
        public static async Task LiveProviders(TestContext t)
        {
            if (Environment.GetEnvironmentVariable("VORTEX_STORE_LIVE") != "1") { Console.WriteLine("        (skipped — set VORTEX_STORE_LIVE=1)"); return; }
            Setup(t);
            StoreHttp.Handler = null;
            var ph = new PolyHavenProvider();
            var models = await ph.SearchAsync(new StoreQuery { Text = "chair", Kind = StoreKind.Model, PageSize = 5 }, CancellationToken.None);
            t.True(models.Items.Count > 0, "Poly Haven finds chairs");
            var cats = await ph.CategoriesAsync(StoreKind.Model, CancellationToken.None);
            t.True(cats.Count > 5, "Poly Haven categories");
            var item = models.Items[0];
            var det = await ph.DetailsAsync(item, CancellationToken.None);
            var v = det.Variants.First(x => x.Id == "1k");
            var job = await Run(StoreDownloads.Enqueue(ph, item, v));
            for (int i = 0; i < 1200 && job.IsActive; i++) await Task.Delay(100);
            t.Equal(StoreJobState.Done, job.State, "Poly Haven model: " + job.Error);
            t.True(GlobalAssetDatabase.Instance.Get(job.Entries[0].Id).Companions.Count > 0, "model companions");
            var acg = new AmbientCgProvider();
            var mats = await acg.SearchAsync(new StoreQuery { Text = "planks", PageSize = 3 }, CancellationToken.None);
            t.True(mats.Items.Count > 0, "ambientCG finds planks");
            var md = await acg.DetailsAsync(mats.Items[0], CancellationToken.None);
            var mjob = await Run(StoreDownloads.Enqueue(acg, mats.Items[0], md.Variants.First(x => x.Id == "1K-JPG")));
            for (int i = 0; i < 1200 && mjob.IsActive; i++) await Task.Delay(100);
            t.Equal(StoreJobState.Done, mjob.State, "ambientCG material: " + mjob.Error);
            var sk = new SketchfabProvider();
            var models2 = await sk.SearchAsync(new StoreQuery { Text = "door", PageSize = 6 }, CancellationToken.None);
            t.True(models2.Items.Count > 0, "Sketchfab anonymous search");
            t.True(models2.Items.All(x => x.License != null && x.License.Commercial), "NC models filtered out by default");
            var kenney = new KenneyProvider();
            var kp = (await kenney.SearchAsync(new StoreQuery { Text = "ui pack" }, CancellationToken.None)).Items.First(x => x.Id == "ui-pack");
            t.True((await ((IAssetProvider)kenney).ThumbnailUrlAsync(kp, CancellationToken.None))?.StartsWith("https://kenney.nl/media/") == true, "Kenney preview from the page");
            var kplan = await kenney.ResolveAsync(kp, null, CancellationToken.None);
            t.True(kplan.Files[0].Url.EndsWith(".zip"), "Kenney zip link from the page");
            Console.WriteLine("        live: " + item.Name + " (" + job.Entries[0].Size + " B + companions), " + mats.Items[0].Name + " → " + mjob.Entries[0].FileName +
                              ", Sketchfab " + models2.Items.Count + " results (" + models2.Items[0].License.Badge + "), Kenney zip " + Path.GetFileName(new Uri(kplan.Files[0].Url).AbsolutePath));
        }

        [Test]
        public static async Task SonnissFolderIndexing(TestContext t)
        {
            Setup(t);
            var a = t.Write("sonniss/Doors/Wood/door_open.wav", "RIFF-open");
            var b = t.Write("sonniss/Doors/Wood/door_close.wav", "RIFF-close");
            t.Write("sonniss/readme.txt", "x");
            var p = new SonnissProvider();
            var files = SonnissProvider.FindWavs(t.Path("sonniss")).ToList();
            t.Equal(2, files.Count, "two wavs");
            var r = await p.ImportAsync(files, null, CancellationToken.None);
            t.True(r.All(x => x.Success && !x.EntryExisted), "registered");
            var e = GlobalAssetDatabase.Instance.FindByFile(a, out _);
            t.False(e.Redistributable, "never exported in a bundle");
            t.Equal("Sonniss-GDC", e.License, "license");
            t.True(e.Tags.Contains("Wood"), "folder becomes a tag");
            var again = await p.ImportAsync(files, null, CancellationToken.None);
            t.True(again.All(x => x.EntryExisted), "re-scan dedups");
        }
    }
}
