using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.Core.Assets.Store
{
    /// <summary>
    /// ambientCG (#69): 2,800+ CC0 PBR materials through API v3 (no account, no attribution needed). Downloads go
    /// through the <c>ambientcg.com/get?file=…</c> redirect; the zip is unpacked and turned into a .vmat. The site is
    /// run by one person, so responses are cached for half a day and requests are spaced.
    /// </summary>
    public sealed class AmbientCgProvider : IAssetProvider
    {
        public const string Api = "https://ambientcg.com/api/v3/assets";
        public string Id => "ambientcg";
        public string Name => "ambientCG";
        public string Tagline => "CC0 PBR materials → ready .vmat";
        public string HomeUrl => "https://ambientcg.com";
        public string Attribution => "Materials from ambientCG.com — CC0, created by Lennart Demes.";
        public IReadOnlyList<StoreKind> Kinds { get; } = new[] { StoreKind.Material };
        public ProviderAccess Access => ProviderAccess.Anonymous;
        public string KeyName => null;
        public string KeyHelpUrl => null;
        public bool HasLicenseFilter => false;
        public TimeSpan MinInterval => TimeSpan.FromMilliseconds(400);
        public void Decorate(HttpRequestMessage request) { }

        public async Task<StorePage> SearchAsync(StoreQuery q, CancellationToken ct)
        {
            var page = new StorePage();
            string url = Api + "?type=Material&limit=" + q.PageSize + "&offset=" + (q.Page * q.PageSize) + "&sort=popular" +
                         "&include=title,tags,thumbnails,downloads,dimensions";
            string text = ((q.Text ?? "") + " " + (q.Category ?? "")).Trim();
            if (text.Length > 0) url += "&q=" + StoreHttp.UrlEncode(text);
            using (var doc = await StoreHttp.GetJsonAsync(this, url, TimeSpan.FromHours(12), ct).ConfigureAwait(false))
            {
                var root = doc.RootElement;
                page.Total = root.TryGetProperty("totalResults", out var t) && t.TryGetInt32(out int total) ? total : -1;
                foreach (var a in root.GetProperty("assets").EnumerateArray())
                {
                    string id = PolyHavenProvider.Str(a, "id");
                    var item = new StoreItem
                    {
                        ProviderId = Id, Id = id, Name = PolyHavenProvider.Str(a, "title") ?? id, Kind = StoreKind.Material,
                        PageUrl = "https://ambientcg.com/view?id=" + id, Author = "ambientCG", License = StoreLicense.Get("CC0-1.0"),
                    };
                    if (a.TryGetProperty("thumbnails", out var th) && th.ValueKind == JsonValueKind.Object)
                        item.ThumbnailUrl = PolyHavenProvider.Str(th, "256-PNG") ?? PolyHavenProvider.Str(th, "128-PNG");
                    if (a.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array) item.Tags.AddRange(tags.EnumerateArray().Select(x => x.GetString()).Where(x => x != null && !x.All(char.IsDigit)));
                    if (a.TryGetProperty("downloads", out var dl)) item.Extra["downloads"] = dl.GetRawText();
                    if (a.TryGetProperty("dimensions", out var dim) && dim.ValueKind == JsonValueKind.Object)
                    {
                        int w = PolyHavenProvider.Int(dim, "width", 0), h = PolyHavenProvider.Int(dim, "height", 0);
                        if (w > 0 && h > 0) item.Extra["dimensions"] = w + " × " + h + " cm";
                    }
                    page.Items.Add(item);
                }
                page.HasMore = page.Total > (q.Page + 1) * q.PageSize;
            }
            return page;
        }

        public Task<IReadOnlyList<string>> CategoriesAsync(StoreKind kind, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "Wood", "Metal", "Concrete", "Bricks", "Tiles", "Planks", "Rock", "Ground", "Gravel", "Grass", "Fabric", "Leather", "Plaster", "Paint", "Rust", "Asphalt", "Paving", "Marble", "Roofing", "Snow" });

        private static List<StoreVariant> Variants(StoreItem item)
        {
            var list = new List<StoreVariant>();
            if (!item.Extra.TryGetValue("downloads", out var raw)) return list;
            using (var doc = JsonDocument.Parse(raw))
                foreach (var d in doc.RootElement.EnumerateArray())
                {
                    string attr = PolyHavenProvider.Str(d, "attributes");
                    if (attr == null || attr.StartsWith("8K", StringComparison.OrdinalIgnoreCase) || !string.Equals(PolyHavenProvider.Str(d, "extension"), "zip", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new StoreVariant { Id = attr, Label = attr, Size = PolyHavenProvider.Long(d, "size") });
                }
            return list;
        }

        public Task<StoreDetails> DetailsAsync(StoreItem item, CancellationToken ct)
        {
            var d = new StoreDetails { Item = item, Variants = Variants(item) };
            d.DefaultVariant = d.Variants.Any(v => v.Id == "2K-JPG") ? "2K-JPG" : d.Variants.FirstOrDefault()?.Id;
            if (item.Extra.TryGetValue("dimensions", out var dim)) d.Facts.Add("Covers " + dim);
            if (item.Tags.Count > 0) d.Facts.Add(string.Join(", ", item.Tags.Take(8)));
            return Task.FromResult(d);
        }

        public Task<DownloadPlan> ResolveAsync(StoreItem item, StoreVariant variant, CancellationToken ct)
        {
            var v = Variants(item).FirstOrDefault(x => x.Id == (variant?.Id ?? "2K-JPG")) ?? Variants(item).FirstOrDefault();
            if (v == null) throw new InvalidDataException("ambientCG lists no download for " + item.Name + ".");
            var plan = new DownloadPlan { Item = item, Variant = v, Artifact = StoreArtifact.Material };
            plan.Files.Add(new DownloadFile { Url = "https://ambientcg.com/get?file=" + StoreHttp.UrlEncode(item.Id + "_" + v.Id + ".zip"), RelPath = item.Id + "_" + v.Id + ".zip", Size = v.Size, Unzip = true });
            return Task.FromResult(plan);
        }
    }

    /// <summary>
    /// poly.pizza (#70): Quaternius, the Google Poly archive and other CC0/CC-BY low-poly creators behind one API
    /// (v1.1). Needs the user's free key (sent as <c>x-auth-token</c>); downloads are GLB files.
    /// </summary>
    public sealed class PolyPizzaProvider : IAssetProvider
    {
        public const string Api = "https://api.poly.pizza/v1.1";
        public string Id => "polypizza";
        public string Name => "poly.pizza";
        public string Tagline => "Low-poly CC0 models (Quaternius & more), often rigged";
        public string HomeUrl => "https://poly.pizza";
        public string Attribution => "Models from poly.pizza — credit the creators of CC-BY models.";
        public IReadOnlyList<StoreKind> Kinds { get; } = new[] { StoreKind.Model };
        public ProviderAccess Access => ProviderAccess.ApiKey;
        public string KeyName => "API key";
        public string KeyHelpUrl => "https://poly.pizza/settings/api";
        public bool HasLicenseFilter => true;
        public TimeSpan MinInterval => TimeSpan.FromMilliseconds(250);
        public void Decorate(HttpRequestMessage request) { var k = StoreKeys.Get(Id); if (!string.IsNullOrEmpty(k)) request.Headers.TryAddWithoutValidation("x-auth-token", k); }

        public async Task<StorePage> SearchAsync(StoreQuery q, CancellationToken ct)
        {
            if (!StoreKeys.Has(Id)) return new StorePage { NeedsKey = true };
            string text = string.IsNullOrWhiteSpace(q.Text) ? (q.Category ?? "chair") : q.Text.Trim();
            string url = Api + "/search/" + StoreHttp.UrlEncode(text) + "?Limit=" + q.PageSize + "&Page=" + q.Page;
            var page = new StorePage();
            using (var doc = await StoreHttp.GetJsonAsync(this, url, TimeSpan.FromHours(6), ct).ConfigureAwait(false))
            {
                var root = doc.RootElement;
                page.Total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out int total) ? total : -1;
                var results = root.TryGetProperty("results", out var r) ? r : root;
                if (results.ValueKind == JsonValueKind.Array)
                    foreach (var m in results.EnumerateArray())
                    {
                        var lic = StoreLicense.FromLabel(Pick(m, "Licence", "License", "licence"));
                        if (!q.Allows(lic)) continue;
                        var item = new StoreItem
                        {
                            ProviderId = Id, Id = Pick(m, "ID", "Id", "id"), Name = Pick(m, "Title", "title", "Name") ?? "Model",
                            Kind = StoreKind.Model, ThumbnailUrl = Pick(m, "Thumbnail", "thumbnail"), License = lic,
                            Author = m.TryGetProperty("Creator", out var c) ? Pick(c, "Username", "username") : Pick(m, "Attribution"),
                        };
                        item.PageUrl = "https://poly.pizza/m/" + item.Id;
                        var dl = Pick(m, "Download", "download");
                        if (dl != null) item.Extra["download"] = dl;
                        if (m.TryGetProperty("Tri Count", out var tc) && tc.ValueKind == JsonValueKind.Number) item.Extra["polycount"] = tc.GetInt64().ToString("N0", CultureInfo.InvariantCulture);
                        if (m.TryGetProperty("Animated", out var an) && an.ValueKind == JsonValueKind.True) item.Tags.Add("animated");
                        if (m.TryGetProperty("Tags", out var tags) && tags.ValueKind == JsonValueKind.Array) item.Tags.AddRange(tags.EnumerateArray().Select(x => x.GetString()).Where(x => x != null));
                        var cat = Pick(m, "Category"); if (cat != null) item.Categories.Add(cat);
                        page.Items.Add(item);
                    }
                page.HasMore = page.Total > (q.Page + 1) * q.PageSize || page.Items.Count >= q.PageSize;
            }
            return page;
        }

        internal static string Pick(JsonElement e, params string[] names)
        {
            foreach (var n in names) if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
            return null;
        }

        public Task<IReadOnlyList<string>> CategoriesAsync(StoreKind kind, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "Furniture", "Weapons", "Vehicles", "Buildings", "Nature", "People", "Animals", "Food", "Props" });

        public Task<StoreDetails> DetailsAsync(StoreItem item, CancellationToken ct)
        {
            var d = new StoreDetails { Item = item };
            d.Variants.Add(new StoreVariant { Id = "glb", Label = "GLB" });
            d.DefaultVariant = "glb";
            if (item.Extra.TryGetValue("polycount", out var pc)) d.Facts.Add(pc + " triangles");
            if (item.Tags.Contains("animated")) d.Facts.Add("Rigged + animated");
            return Task.FromResult(d);
        }

        public Task<DownloadPlan> ResolveAsync(StoreItem item, StoreVariant variant, CancellationToken ct)
        {
            if (!item.Extra.TryGetValue("download", out var url)) throw new InvalidDataException("poly.pizza gave no download link for " + item.Name + ".");
            var plan = new DownloadPlan { Item = item, Variant = variant, Artifact = StoreArtifact.Model, MainFile = LibraryName(item.Name) + ".glb" };
            plan.Files.Add(new DownloadFile { Url = url, RelPath = plan.MainFile });
            return Task.FromResult(plan);
        }

        internal static string LibraryName(string name) => Library.LibraryProjects.SafeName(name ?? "asset").Replace(' ', '_');
    }

    /// <summary>
    /// Freesound (#71): CC sound effects through API v2 with the user's token. The license filter defaults to CC0 +
    /// CC-BY (NonCommercial sounds can't ship in a sold game). Every result carries HQ preview links for auditioning
    /// before download; token-only accounts download the HQ preview (OGG) — originals need OAuth2 (follow-up).
    /// Rate limits (60/min, 2,000/day) are respected by request spacing + caching.
    /// </summary>
    public sealed class FreesoundProvider : IAssetProvider
    {
        public const string Api = "https://freesound.org/apiv2";
        public string Id => "freesound";
        public string Name => "Freesound";
        public string Tagline => "600,000+ CC sound effects — audition before download";
        public string HomeUrl => "https://freesound.org";
        public string Attribution => "Sounds from Freesound.org — CC-BY sounds need credit (added to CREDITS.md on export).";
        public IReadOnlyList<StoreKind> Kinds { get; } = new[] { StoreKind.Sound };
        public ProviderAccess Access => ProviderAccess.ApiKey;
        public string KeyName => "API key (token)";
        public string KeyHelpUrl => "https://freesound.org/apiv2/apply";
        public bool HasLicenseFilter => true;
        public TimeSpan MinInterval => TimeSpan.FromMilliseconds(1100);
        public int DailyRequestLimit => 2000;
        public void Decorate(HttpRequestMessage request) { var k = StoreKeys.Get(Id); if (!string.IsNullOrEmpty(k)) request.Headers.TryAddWithoutValidation("Authorization", "Token " + k); }

        private static string LicenseFilter(StoreQuery q)
        {
            var parts = new List<string> { "\"Creative Commons 0\"", "\"Attribution\"" };
            if (q.IncludeNonCommercial) parts.Add("\"Attribution NonCommercial\"");
            return "license:(" + string.Join(" OR ", parts) + ")";
        }

        public async Task<StorePage> SearchAsync(StoreQuery q, CancellationToken ct)
        {
            if (!StoreKeys.Has(Id)) return new StorePage { NeedsKey = true };
            string text = (q.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(q.Category)) text = (text + " " + q.Category).Trim();
            string url = Api + "/search/text/?query=" + StoreHttp.UrlEncode(text) + "&page=" + (q.Page + 1) + "&page_size=" + Math.Min(q.PageSize, 150) +
                         "&fields=id,name,username,license,previews,duration,samplerate,channels,tags,images,url,filesize,type" +
                         "&filter=" + StoreHttp.UrlEncode(LicenseFilter(q));
            var page = new StorePage();
            using (var doc = await StoreHttp.GetJsonAsync(this, url, TimeSpan.FromHours(6), ct).ConfigureAwait(false))
            {
                var root = doc.RootElement;
                page.Total = root.TryGetProperty("count", out var c) && c.TryGetInt32(out int n) ? n : -1;
                page.HasMore = root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String;
                foreach (var s in root.GetProperty("results").EnumerateArray())
                {
                    var lic = StoreLicense.FromUrl(PolyHavenProvider.Str(s, "license"));
                    if (!q.Allows(lic)) continue;
                    var item = new StoreItem
                    {
                        ProviderId = Id, Id = s.GetProperty("id").GetRawText(), Name = PolyHavenProvider.Str(s, "name"), Kind = StoreKind.Sound,
                        Author = PolyHavenProvider.Str(s, "username"), License = lic, PageUrl = PolyHavenProvider.Str(s, "url"),
                        Duration = s.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetDouble() : (double?)null,
                    };
                    if (s.TryGetProperty("images", out var im)) item.ThumbnailUrl = PolyHavenProvider.Str(im, "waveform_m") ?? PolyHavenProvider.Str(im, "waveform_l");
                    if (s.TryGetProperty("previews", out var pv))
                    {
                        item.PreviewUrl = PolyHavenProvider.Str(pv, "preview-hq-mp3") ?? PolyHavenProvider.Str(pv, "preview-lq-mp3");
                        var ogg = PolyHavenProvider.Str(pv, "preview-hq-ogg"); if (ogg != null) item.Extra["ogg"] = ogg;
                        if (item.PreviewUrl != null) item.Extra["mp3"] = item.PreviewUrl;
                    }
                    if (s.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array) item.Tags.AddRange(tags.EnumerateArray().Select(x => x.GetString()).Where(x => x != null).Take(12));
                    if (s.TryGetProperty("samplerate", out var sr) && sr.ValueKind == JsonValueKind.Number) item.Extra["samplerate"] = sr.GetDouble().ToString("0", CultureInfo.InvariantCulture);
                    if (s.TryGetProperty("channels", out var ch) && ch.ValueKind == JsonValueKind.Number) item.Extra["channels"] = ch.GetInt32().ToString(CultureInfo.InvariantCulture);
                    item.Extra["type"] = PolyHavenProvider.Str(s, "type") ?? "";
                    page.Items.Add(item);
                }
            }
            return page;
        }

        public Task<IReadOnlyList<string>> CategoriesAsync(StoreKind kind, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "door", "footsteps", "gunshot", "reload", "explosion", "impact", "ambience", "wind", "rain", "scream", "whisper", "creak", "heartbeat", "ui click", "metal" });

        public Task<StoreDetails> DetailsAsync(StoreItem item, CancellationToken ct)
        {
            var d = new StoreDetails { Item = item };
            if (item.Extra.ContainsKey("ogg")) d.Variants.Add(new StoreVariant { Id = "ogg", Label = "HQ preview (OGG)" });
            if (item.Extra.ContainsKey("mp3")) d.Variants.Add(new StoreVariant { Id = "mp3", Label = "HQ preview (MP3)" });
            d.DefaultVariant = d.Variants.FirstOrDefault()?.Id;
            if (item.Duration.HasValue) d.Facts.Add(item.Duration.Value.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            if (item.Extra.TryGetValue("samplerate", out var sr)) d.Facts.Add(sr + " Hz" + (item.Extra.TryGetValue("channels", out var ch) ? (ch == "1" ? " mono" : ch == "2" ? " stereo" : " · " + ch + " ch") : ""));
            d.Facts.Add("Original-quality WAV downloads need a Freesound OAuth login (coming later) — the HQ preview is used.");
            return Task.FromResult(d);
        }

        public Task<DownloadPlan> ResolveAsync(StoreItem item, StoreVariant variant, CancellationToken ct)
        {
            string id = variant?.Id ?? "ogg";
            if (!item.Extra.TryGetValue(id, out var url) && !item.Extra.TryGetValue(id = "mp3", out url)) throw new InvalidDataException("Freesound gave no preview for " + item.Name + ".");
            var plan = new DownloadPlan { Item = item, Variant = variant, Artifact = StoreArtifact.Single, MainFile = PolyPizzaProvider.LibraryName(item.Name) + "." + id };
            plan.Files.Add(new DownloadFile { Url = url, RelPath = plan.MainFile });
            return Task.FromResult(plan);
        }
    }

    /// <summary>
    /// Sketchfab (#72): 1M+ downloadable models under per-model CC licenses. Search is anonymous; downloading needs the
    /// user's API token (Settings → Password &amp; API on sketchfab.com). NC/ND licenses are filtered out by default.
    /// Built to be swapped for a future Fab API without UI changes.
    /// </summary>
    public sealed class SketchfabProvider : IAssetProvider
    {
        public const string Api = "https://api.sketchfab.com/v3";
        private readonly Dictionary<string, string> _cursors = new Dictionary<string, string>();
        public string Id => "sketchfab";
        public string Name => "Sketchfab";
        public string Tagline => "1M+ CC models — license shown per model";
        public string HomeUrl => "https://sketchfab.com";
        public string Attribution => "Models from Sketchfab — each model has its own license; CC-BY models are credited in CREDITS.md on export.";
        public IReadOnlyList<StoreKind> Kinds { get; } = new[] { StoreKind.Model };
        public ProviderAccess Access => ProviderAccess.ApiKey;
        public string KeyName => "API token";
        public string KeyHelpUrl => "https://sketchfab.com/settings/password";
        public bool HasLicenseFilter => true;
        public TimeSpan MinInterval => TimeSpan.FromMilliseconds(300);
        public void Decorate(HttpRequestMessage request) { var k = StoreKeys.Get(Id); if (!string.IsNullOrEmpty(k)) request.Headers.TryAddWithoutValidation("Authorization", "Token " + k); }

        public async Task<StorePage> SearchAsync(StoreQuery q, CancellationToken ct)
        {
            string text = string.IsNullOrWhiteSpace(q.Text) ? (q.Category ?? "") : q.Text.Trim();
            string key = text + "|" + q.Category + "|" + q.IncludeNonCommercial + "|" + q.IncludeNoDerivatives + "|" + q.IncludeShareAlike;
            string url;
            if (q.Page > 0 && _cursors.TryGetValue(key + "|" + q.Page, out var next)) url = next;
            else url = Api + "/search?type=models&downloadable=true&count=" + Math.Min(q.PageSize, 24) + (text.Length > 0 ? "&q=" + StoreHttp.UrlEncode(text) : "") + "&sort_by=-likeCount";
            var page = new StorePage();
            using (var doc = await StoreHttp.GetJsonAsync(this, url, TimeSpan.FromHours(3), ct, authenticated: false).ConfigureAwait(false))
            {
                var root = doc.RootElement;
                var nx = PolyHavenProvider.Str(root, "next");
                if (nx != null) { _cursors[key + "|" + (q.Page + 1)] = nx; page.HasMore = true; }
                foreach (var m in root.GetProperty("results").EnumerateArray())
                {
                    var lic = m.TryGetProperty("license", out var l) && l.ValueKind == JsonValueKind.Object ? StoreLicense.FromLabel(PolyHavenProvider.Str(l, "label")) : StoreLicense.Get("Unknown");
                    if (!q.Allows(lic)) continue;
                    var item = new StoreItem
                    {
                        ProviderId = Id, Id = PolyHavenProvider.Str(m, "uid"), Name = PolyHavenProvider.Str(m, "name"), Kind = StoreKind.Model, License = lic,
                        PageUrl = PolyHavenProvider.Str(m, "viewerUrl"),
                        Author = m.TryGetProperty("user", out var u) ? (PolyHavenProvider.Str(u, "displayName") ?? PolyHavenProvider.Str(u, "username")) : null,
                        Description = PolyHavenProvider.Str(m, "description"),
                    };
                    if (m.TryGetProperty("thumbnails", out var th) && th.TryGetProperty("images", out var imgs))
                        item.ThumbnailUrl = imgs.EnumerateArray().OrderBy(i => Math.Abs(PolyHavenProvider.Int(i, "width", 0) - 256)).Select(i => PolyHavenProvider.Str(i, "url")).FirstOrDefault();
                    if (m.TryGetProperty("faceCount", out var fc) && fc.ValueKind == JsonValueKind.Number) item.Extra["polycount"] = fc.GetInt64().ToString("N0", CultureInfo.InvariantCulture);
                    if (m.TryGetProperty("animationCount", out var ac) && ac.ValueKind == JsonValueKind.Number && ac.GetInt32() > 0) item.Tags.Add("animated");
                    if (m.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array) item.Tags.AddRange(tags.EnumerateArray().Select(x => PolyHavenProvider.Str(x, "name")).Where(x => x != null).Take(10));
                    if (m.TryGetProperty("archives", out var ar) && ar.ValueKind == JsonValueKind.Object)
                        foreach (var fmt in new[] { "glb", "gltf" })
                            if (ar.TryGetProperty(fmt, out var a) && a.ValueKind == JsonValueKind.Object) item.Extra["size_" + fmt] = PolyHavenProvider.Long(a, "size").ToString(CultureInfo.InvariantCulture);
                    page.Items.Add(item);
                }
            }
            return page;
        }

        public Task<IReadOnlyList<string>> CategoriesAsync(StoreKind kind, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "weapon", "rifle", "pistol", "soldier", "military", "vehicle", "building", "ruins", "furniture", "horror", "zombie", "lowpoly" });

        public Task<StoreDetails> DetailsAsync(StoreItem item, CancellationToken ct)
        {
            var d = new StoreDetails { Item = item, Description = item.Description };
            foreach (var fmt in new[] { "glb", "gltf" })
                if (item.Extra.TryGetValue("size_" + fmt, out var s) && long.TryParse(s, out long size) && size > 0)
                    d.Variants.Add(new StoreVariant { Id = fmt, Label = fmt == "glb" ? "GLB" : "glTF (zip)", Size = size });
            if (d.Variants.Count == 0) d.Variants.Add(new StoreVariant { Id = "glb", Label = "GLB" });
            d.DefaultVariant = d.Variants[0].Id;
            if (item.Extra.TryGetValue("polycount", out var pc)) d.Facts.Add(pc + " faces");
            if (!StoreKeys.Has(Id)) d.Facts.Add("Downloading needs your Sketchfab API token (Store settings).");
            return Task.FromResult(d);
        }

        public async Task<DownloadPlan> ResolveAsync(StoreItem item, StoreVariant variant, CancellationToken ct)
        {
            if (!StoreKeys.Has(Id)) throw new StoreHttp.StoreHttpException(System.Net.HttpStatusCode.Unauthorized, "Sketchfab downloads need your API token — add it in the Store settings.");
            // archive URLs expire: ask right before downloading, never cache
            using (var doc = await StoreHttp.GetJsonAsync(this, Api + "/models/" + item.Id + "/download", TimeSpan.Zero, ct).ConfigureAwait(false))
            {
                var root = doc.RootElement;
                string fmt = variant?.Id ?? "glb";
                if (!root.TryGetProperty(fmt, out var a)) { fmt = root.TryGetProperty("glb", out a) ? "glb" : "gltf"; a = root.GetProperty(fmt); }
                string name = PolyPizzaProvider.LibraryName(item.Name);
                var plan = new DownloadPlan { Item = item, Variant = variant, Artifact = StoreArtifact.Model };
                if (fmt == "glb")
                {
                    plan.MainFile = name + ".glb";
                    plan.Files.Add(new DownloadFile { Url = PolyHavenProvider.Str(a, "url"), RelPath = plan.MainFile, Size = PolyHavenProvider.Long(a, "size") });
                }
                else plan.Files.Add(new DownloadFile { Url = PolyHavenProvider.Str(a, "url"), RelPath = name + ".zip", Size = PolyHavenProvider.Long(a, "size"), Unzip = true });
                return plan;
            }
        }
    }
}
