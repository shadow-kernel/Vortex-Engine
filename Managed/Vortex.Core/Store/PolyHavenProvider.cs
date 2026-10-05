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
    /// Poly Haven (#68): models, PBR texture sets and HDRIs, all CC0, through the public API at api.polyhaven.com.
    /// The asset list per type is fetched once (cached) and searched locally — name, tags and categories — sorted by
    /// popularity. <c>/files/{id}</c> lists the resolutions with direct CDN links and MD5 per file. Their API terms ask
    /// for a unique User-Agent (sent with every request) and for "Poly Haven" credit next to the results.
    /// </summary>
    public sealed class PolyHavenProvider : IAssetProvider
    {
        public const string Api = "https://api.polyhaven.com";
        public string Id => "polyhaven";
        public string Name => "Poly Haven";
        public string Tagline => "CC0 models, PBR textures and HDRIs";
        public string HomeUrl => "https://polyhaven.com";
        public string Attribution => "Assets from Poly Haven (polyhaven.com) — free CC0 assets. Please consider supporting them on Patreon.";
        public IReadOnlyList<StoreKind> Kinds { get; } = new[] { StoreKind.Model, StoreKind.Material, StoreKind.Hdri };
        public ProviderAccess Access => ProviderAccess.Anonymous;
        public string KeyName => null;
        public string KeyHelpUrl => null;
        public bool HasLicenseFilter => false;
        public TimeSpan MinInterval => TimeSpan.FromMilliseconds(100);
        public void Decorate(HttpRequestMessage request) { }

        private static string TypeFor(StoreKind? k) => k == StoreKind.Material ? "textures" : k == StoreKind.Hdri ? "hdris" : "models";
        private static StoreKind KindFor(int type) => type == 0 ? StoreKind.Hdri : type == 1 ? StoreKind.Material : StoreKind.Model;

        public async Task<StorePage> SearchAsync(StoreQuery q, CancellationToken ct)
        {
            var page = new StorePage();
            string type = TypeFor(q.Kind ?? StoreKind.Model);
            string url = Api + "/assets?type=" + type + (string.IsNullOrEmpty(q.Category) ? "" : "&categories=" + StoreHttp.UrlEncode(q.Category));
            using (var doc = await StoreHttp.GetJsonAsync(this, url, TimeSpan.FromHours(6), ct).ConfigureAwait(false))
            {
                var all = new List<(StoreItem item, long downloads, int score)>();
                string text = (q.Text ?? "").Trim().ToLowerInvariant();
                var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var a = p.Value;
                    var item = new StoreItem
                    {
                        ProviderId = Id, Id = p.Name, Name = Str(a, "name") ?? p.Name,
                        Kind = KindFor(Int(a, "type", 2)),
                        ThumbnailUrl = "https://cdn.polyhaven.com/asset_img/thumbs/" + p.Name + ".png?width=256&height=256",
                        PageUrl = "https://polyhaven.com/a/" + p.Name,
                        License = StoreLicense.Get("CC0-1.0"),
                        Author = a.TryGetProperty("authors", out var au) && au.ValueKind == JsonValueKind.Object ? string.Join(", ", au.EnumerateObject().Select(x => x.Name)) : null,
                        Description = Str(a, "description"),
                    };
                    if (a.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array) item.Tags.AddRange(tags.EnumerateArray().Select(x => x.GetString()).Where(x => x != null));
                    if (a.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array) item.Categories.AddRange(cats.EnumerateArray().Select(x => x.GetString()).Where(x => x != null));
                    if (a.TryGetProperty("polycount", out var pc) && pc.ValueKind == JsonValueKind.Number) item.Extra["polycount"] = pc.GetInt64().ToString("N0", CultureInfo.InvariantCulture);
                    if (a.TryGetProperty("dimensions", out var dim) && dim.ValueKind == JsonValueKind.Array)
                        item.Extra["dimensions"] = string.Join(" × ", dim.EnumerateArray().Select(d => (d.GetDouble() / 1000.0).ToString("0.00", CultureInfo.InvariantCulture))) + " m";
                    int score = 1;
                    if (words.Length > 0)
                    {
                        score = 0;
                        foreach (var w in words)
                        {
                            if (item.Name.ToLowerInvariant().Contains(w) || item.Id.ToLowerInvariant().Contains(w)) score += 3;
                            else if (item.Tags.Any(t => t.ToLowerInvariant().Contains(w))) score += 2;
                            else if (item.Categories.Any(c => c.ToLowerInvariant().Contains(w))) score += 1;
                            else { score = 0; break; }
                        }
                    }
                    if (score > 0) all.Add((item, Long(a, "download_count"), score));
                }
                var sorted = all.OrderByDescending(x => x.score).ThenByDescending(x => x.downloads).Select(x => x.item).ToList();
                page.Total = sorted.Count;
                page.Items = sorted.Skip(q.Page * q.PageSize).Take(q.PageSize).ToList();
                page.HasMore = (q.Page + 1) * q.PageSize < sorted.Count;
            }
            return page;
        }

        public async Task<IReadOnlyList<string>> CategoriesAsync(StoreKind kind, CancellationToken ct)
        {
            using (var doc = await StoreHttp.GetJsonAsync(this, Api + "/categories/" + TypeFor(kind), TimeSpan.FromHours(24), ct).ConfigureAwait(false))
                return doc.RootElement.EnumerateObject().Where(p => p.Name != "all" && !p.Name.StartsWith("collection:", StringComparison.Ordinal))
                    .OrderByDescending(p => p.Value.GetInt32()).Select(p => p.Name).ToList();
        }

        public async Task<StoreDetails> DetailsAsync(StoreItem item, CancellationToken ct)
        {
            var d = new StoreDetails { Item = item, Description = item.Description };
            if (item.Extra.TryGetValue("polycount", out var pc)) d.Facts.Add(pc + " triangles");
            if (item.Extra.TryGetValue("dimensions", out var dim)) d.Facts.Add(dim);
            using (var doc = await StoreHttp.GetJsonAsync(this, Api + "/files/" + item.Id, TimeSpan.FromHours(6), ct).ConfigureAwait(false))
            {
                var root = doc.RootElement;
                foreach (var res in Resolutions(root, item.Kind))
                {
                    long size = 0;
                    try { size = Plan(root, item, res).Files.Sum(f => f.Size); } catch { }
                    if (size > 0) d.Variants.Add(new StoreVariant { Id = res, Label = res.ToUpperInvariant(), Size = size });
                }
            }
            d.Variants = d.Variants.OrderBy(v => ResOrder(v.Id)).ToList();
            d.DefaultVariant = d.Variants.Any(v => v.Id == "2k") ? "2k" : d.Variants.FirstOrDefault()?.Id;
            return d;
        }

        private static int ResOrder(string r) => int.TryParse(new string((r ?? "").TakeWhile(char.IsDigit).ToArray()), out int n) ? n : 999;

        private static IEnumerable<string> Resolutions(JsonElement root, StoreKind kind)
        {
            string key = kind == StoreKind.Model ? "gltf" : kind == StoreKind.Hdri ? "hdri" : "Diffuse";
            if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object) return Enumerable.Empty<string>();
            return el.EnumerateObject().Select(p => p.Name).Where(r => ResOrder(r) <= 8).ToList();   // 16k/24k HDRIs are too big for a game
        }

        public async Task<DownloadPlan> ResolveAsync(StoreItem item, StoreVariant variant, CancellationToken ct)
        {
            using (var doc = await StoreHttp.GetJsonAsync(this, Api + "/files/" + item.Id, TimeSpan.FromHours(6), ct).ConfigureAwait(false))
                return Plan(doc.RootElement, item, variant?.Id ?? "2k");
        }

        private static DownloadPlan Plan(JsonElement root, StoreItem item, string res)
        {
            var plan = new DownloadPlan { Item = item, Variant = new StoreVariant { Id = res, Label = res.ToUpperInvariant() } };
            if (item.Kind == StoreKind.Model)
            {
                var g = root.GetProperty("gltf").GetProperty(res).GetProperty("gltf");
                string url = g.GetProperty("url").GetString();
                string main = Path.GetFileName(new Uri(url).AbsolutePath);
                plan.Artifact = StoreArtifact.Model;
                plan.MainFile = main;
                plan.Files.Add(File(g, main));
                if (g.TryGetProperty("include", out var inc))
                    foreach (var p in inc.EnumerateObject()) plan.Files.Add(File(p.Value, p.Name));
            }
            else if (item.Kind == StoreKind.Hdri)
            {
                var h = root.GetProperty("hdri").GetProperty(res).GetProperty("hdr");   // Radiance .hdr (stb_image); no EXR decoder
                string url = h.GetProperty("url").GetString();
                plan.Artifact = StoreArtifact.Single;
                plan.MainFile = Path.GetFileName(new Uri(url).AbsolutePath);
                plan.Files.Add(File(h, plan.MainFile));
            }
            else
            {
                plan.Artifact = StoreArtifact.Material;
                void Map(string key, string role)
                {
                    if (!root.TryGetProperty(key, out var m) || !m.TryGetProperty(res, out var r)) return;
                    if (!r.TryGetProperty("jpg", out var f) && !r.TryGetProperty("png", out f)) return;
                    string url = f.GetProperty("url").GetString();
                    string rel = "textures/" + Path.GetFileName(new Uri(url).AbsolutePath);
                    plan.Files.Add(File(f, rel));
                    plan.MapRoles[rel] = role;
                }
                Map("Diffuse", "Albedo");
                Map("nor_gl", "NormalGL");
                if (root.TryGetProperty("arm", out _)) Map("arm", "ARM");
                else { Map("Rough", "Roughness"); Map("AO", "AO"); Map("Metal", "Metallic"); }
                Map("Displacement", "Height");
                if (plan.Files.Count == 0) throw new InvalidDataException("Poly Haven lists no maps for " + item.Name + " at " + res + ".");
            }
            return plan;
        }

        private static DownloadFile File(JsonElement f, string rel)
            => new DownloadFile
            {
                Url = f.GetProperty("url").GetString(),
                RelPath = rel.Replace('\\', '/'),
                Size = f.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0,
                Md5 = f.TryGetProperty("md5", out var m) ? m.GetString() : null,
            };

        internal static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        internal static int Int(JsonElement e, string name, int fallback) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;
        internal static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long i) ? i : 0;
    }
}
