using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Assets.Library;

namespace Editor.Core.Assets.Store
{
    /// <summary>
    /// Kenney (#74): curated CC0 packs (3D kits, UI, audio, VFX). Kenney has no API, so the editor ships a curated
    /// manifest; the pack's real zip link (with an unguessable hash segment) and its preview image are read from the
    /// asset page only when the user looks at / downloads a pack. Every supported file of the pack becomes its own
    /// library entry tagged with the pack name. If the page layout changes, the provider fails gracefully with a link.
    /// </summary>
    public sealed class KenneyProvider : IAssetProvider
    {
        /// <summary>(slug, category) — refreshed per engine release.</summary>
        public static readonly (string slug, string category)[] Manifest =
        {
            ("blaster-kit", "3D kits"), ("building-kit", "3D kits"), ("castle-kit", "3D kits"), ("city-kit-commercial", "3D kits"),
            ("city-kit-industrial", "3D kits"), ("city-kit-roads", "3D kits"), ("city-kit-suburban", "3D kits"), ("factory-kit", "3D kits"),
            ("furniture-kit", "3D kits"), ("graveyard-kit", "3D kits"), ("modular-buildings", "3D kits"), ("modular-cave-kit", "3D kits"),
            ("modular-dungeon-kit", "3D kits"), ("modular-space-kit", "3D kits"), ("nature-kit", "3D kits"), ("survival-kit", "3D kits"),
            ("space-station-kit", "3D kits"), ("car-kit", "3D kits"), ("prototype-kit", "3D kits"), ("retro-urban-kit", "3D kits"),
            ("mini-dungeon", "3D kits"), ("toy-car-kit", "3D kits"), ("train-kit", "3D kits"), ("watercraft-kit", "3D kits"),
            ("tower-defense-kit", "3D kits"), ("food-kit", "3D kits"), ("fantasy-town-kit", "3D kits"), ("pirate-kit", "3D kits"),
            ("platformer-kit", "3D kits"), ("racing-kit", "3D kits"), ("space-kit", "3D kits"),
            ("mini-characters", "Characters"), ("animated-characters-survivors", "Characters"), ("animated-characters-protagonists", "Characters"), ("blocky-characters", "Characters"),
            ("prototype-textures", "Textures"), ("retro-textures-fantasy", "Textures"), ("road-textures", "Textures"), ("skyboxes", "Textures"), ("skyboxes-space", "Textures"),
            ("ui-pack", "UI"), ("ui-pack-sci-fi", "UI"), ("ui-pack-adventure", "UI"), ("game-icons", "UI"), ("game-icons-expansion", "UI"), ("input-prompts", "UI"),
            ("crosshair-pack", "UI"), ("cursor-pack", "UI"), ("minimap-pack", "UI"), ("mobile-controls", "UI"), ("kenney-fonts", "UI"), ("emotes-pack", "UI"),
            ("impact-sounds", "Audio"), ("interface-sounds", "Audio"), ("sci-fi-sounds", "Audio"), ("casino-audio", "Audio"), ("voiceover-pack", "Audio"), ("voiceover-pack-fighter", "Audio"),
            ("particle-pack", "VFX"), ("smoke-particles", "VFX"), ("light-masks", "VFX"), ("splat-pack", "VFX"),
        };

        public string Id => "kenney";
        public string Name => "Kenney";
        public string Tagline => "Curated CC0 packs — 3D kits, UI, audio, VFX";
        public string HomeUrl => "https://kenney.nl/assets";
        public string Attribution => "Packs by Kenney (kenney.nl) — CC0, credit appreciated.";
        public IReadOnlyList<StoreKind> Kinds { get; } = new[] { StoreKind.Pack };
        public ProviderAccess Access => ProviderAccess.Anonymous;
        public string KeyName => null;
        public string KeyHelpUrl => null;
        public bool HasLicenseFilter => false;
        public TimeSpan MinInterval => TimeSpan.FromMilliseconds(500);
        public void Decorate(HttpRequestMessage request) { }

        public static string Title(string slug)
            => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slug.Replace('-', ' ')).Replace("Ui ", "UI ").Replace("Sci Fi", "Sci-Fi");

        private StoreItem ItemFor((string slug, string category) m)
        {
            var item = new StoreItem
            {
                ProviderId = Id, Id = m.slug, Name = Title(m.slug), Kind = StoreKind.Pack, Author = "Kenney",
                License = StoreLicense.Get("CC0-1.0"), PageUrl = "https://kenney.nl/assets/" + m.slug,
            };
            item.Categories.Add(m.category);
            item.Tags.Add("kenney");
            return item;
        }

        public Task<StorePage> SearchAsync(StoreQuery q, CancellationToken ct)
        {
            var words = (q.Text ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var all = Manifest.Where(m => (string.IsNullOrEmpty(q.Category) || m.category == q.Category)
                                          && words.All(w => m.slug.Contains(w) || m.category.ToLowerInvariant().Contains(w)))
                              .Select(ItemFor).ToList();
            var page = new StorePage { Total = all.Count, Items = all.Skip(q.Page * q.PageSize).Take(q.PageSize).ToList() };
            page.HasMore = (q.Page + 1) * q.PageSize < all.Count;
            return Task.FromResult(page);
        }

        public Task<IReadOnlyList<string>> CategoriesAsync(StoreKind kind, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Manifest.Select(m => m.category).Distinct().ToList());

        private Task<string> PageAsync(string slug, CancellationToken ct)
            => StoreHttp.GetTextAsync(this, "https://kenney.nl/assets/" + slug, TimeSpan.FromDays(1), ct);

        public async Task<string> ThumbnailUrlAsync(StoreItem item, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(item.ThumbnailUrl)) return item.ThumbnailUrl;
            try
            {
                var html = await PageAsync(item.Id, ct).ConfigureAwait(false);
                var m = Regex.Match(html, "og:image['\"]\\s+content=['\"]([^'\"]+)['\"]");
                if (!m.Success) m = Regex.Match(html, "https://kenney\\.nl/media/pages/assets/" + Regex.Escape(item.Id) + "/[^'\"]+/preview\\.png");
                item.ThumbnailUrl = m.Success ? (m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value) : null;
            }
            catch (OperationCanceledException) { throw; }
            catch { item.ThumbnailUrl = null; }
            return item.ThumbnailUrl;
        }

        public Task<StoreDetails> DetailsAsync(StoreItem item, CancellationToken ct)
        {
            var d = new StoreDetails { Item = item };
            d.Variants.Add(new StoreVariant { Id = "zip", Label = "Full pack (zip)" });
            d.DefaultVariant = "zip";
            d.Facts.Add(item.Categories.FirstOrDefault() + " · every file of the pack becomes a library asset tagged “" + item.Name + "”");
            return Task.FromResult(d);
        }

        public async Task<DownloadPlan> ResolveAsync(StoreItem item, StoreVariant variant, CancellationToken ct)
        {
            var html = await PageAsync(item.Id, ct).ConfigureAwait(false);
            var m = Regex.Match(html, "https://kenney\\.nl/media/pages/assets/" + Regex.Escape(item.Id) + "/[^'\"\\s]+\\.zip");
            if (!m.Success) throw new InvalidDataException("Couldn't find the download link on kenney.nl — open " + item.PageUrl + " and download it there (then drop the files on the Library tab).");
            var plan = new DownloadPlan { Item = item, Variant = variant, Artifact = StoreArtifact.Pack };
            plan.Files.Add(new DownloadFile { Url = m.Value, RelPath = item.Id + ".zip", Unzip = true });
            return plan;
        }
    }

    /// <summary>
    /// Mixamo (#73): there is no public API (and the community endpoints break whenever Adobe changes the site), so
    /// the honest flow is guided — open mixamo.com, download FBX files, and Vortex picks them up (dropped onto the
    /// Store panel, chosen in a file dialog, or noticed in the Downloads folder while watching is on). Mixamo files
    /// are royalty-free in games but must not be re-hosted: they are marked non-redistributable.
    /// </summary>
    public sealed class MixamoProvider : IGuidedProvider
    {
        public string Id => "mixamo";
        public string Name => "Mixamo";
        public string Tagline => "Rigged characters + thousands of animations (free Adobe account)";
        public string HomeUrl => "https://www.mixamo.com";
        public StoreLicense License => StoreLicense.Get("Mixamo");
        public IReadOnlyList<string> FilePatterns { get; } = new[] { "*.fbx" };
        public IReadOnlyList<string> Steps { get; } = new[]
        {
            "Open mixamo.com and sign in with your free Adobe account.",
            "Characters: pick one → Download → Format FBX Binary, Pose T-pose. Animations: pick a character first, then an animation → Download → FBX Binary, \"Without Skin\" for clips that share one character, 30 fps.",
            "Drop the downloaded FBX files on this panel — or switch on \"Watch my Downloads folder\" and Vortex adds every new Mixamo FBX by itself.",
            "Add them to a project from the Library: the import extracts the skeleton and the animation clips (.vanim).",
        };

        public Task<List<RegisterResult>> ImportAsync(IEnumerable<string> files, IProgress<LibraryProgress> progress, CancellationToken ct)
            => GuidedImport.Register(this, files.Where(f => f.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)), "Mixamo", progress, ct, new[] { "mixamo", "character" });
    }

    /// <summary>
    /// Sonniss GDC audio (#75): tens of GB of professional WAVs, royalty-free with no attribution, but no API (the
    /// download site blocks HTTP clients) and no redistribution. Guided: open the bundle page, download + extract it
    /// yourself, then let Vortex index the folder — every WAV becomes a searchable, playable library entry marked
    /// non-redistributable (never exported in a library bundle). Re-scans add only new files (content hash).
    /// </summary>
    public sealed class SonnissProvider : IGuidedProvider
    {
        public string Id => "sonniss";
        public string Name => "Sonniss GDC Audio";
        public string Tagline => "Professional sound libraries — royalty-free, guided download";
        public string HomeUrl => "https://sonniss.com/gameaudiogdc";
        public StoreLicense License => StoreLicense.Get("Sonniss-GDC");
        public IReadOnlyList<string> FilePatterns { get; } = new[] { "*.wav" };
        public IReadOnlyList<string> Steps { get; } = new[]
        {
            "Open the Sonniss GDC page and download a bundle (direct download or torrent) in your browser.",
            "Extract the archives into a folder of your choice.",
            "Index the folder: every WAV is added to the library with its duration and sample rate, tagged by its sub-folders.",
            "License: royalty-free for unlimited commercial use in games, no attribution — but no redistribution of the files and no AI/ML training.",
        };

        public Task<List<RegisterResult>> ImportAsync(IEnumerable<string> files, IProgress<LibraryProgress> progress, CancellationToken ct)
            => GuidedImport.Register(this, files.Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)), "Sonniss GDC", progress, ct, new[] { "sonniss" });

        /// <summary>All WAVs below <paramref name="folder"/>.</summary>
        public static IEnumerable<string> FindWavs(string folder)
        {
            try { return Directory.EnumerateFiles(folder, "*.wav", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith(".", StringComparison.Ordinal)).ToList(); }
            catch { return Enumerable.Empty<string>(); }
        }
    }

    internal static class GuidedImport
    {
        public static Task<List<RegisterResult>> Register(IGuidedProvider p, IEnumerable<string> files, string sourceName, IProgress<LibraryProgress> progress, CancellationToken ct, string[] extraTags)
            => Task.Run(() =>
            {
                var list = files.ToList();
                var results = new List<RegisterResult>();
                var lib = GlobalAssetDatabase.Instance;
                for (int i = 0; i < list.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new LibraryProgress("Adding " + p.Name + " files", i, list.Count, Path.GetFileName(list[i])));
                    var tags = new List<string>(extraTags);
                    // a library's sub-folders are its categories ("Sonniss/Doors/Wood/…")
                    var parent = Path.GetFileName(Path.GetDirectoryName(list[i]) ?? "");
                    if (!string.IsNullOrEmpty(parent) && parent.Length < 40) tags.Add(parent);
                    results.Add(lib.Register(list[i], new RegisterOptions
                    {
                        Explicit = true, SourceKind = LibrarySource.Store, SourceName = sourceName, SourceUrl = p.HomeUrl,
                        License = p.License.Id, Redistributable = p.License.Redistributable, Tags = tags,
                        Companions = new Dictionary<string, string>(),
                    }, ct));
                }
                progress?.Report(new LibraryProgress("Adding " + p.Name + " files", list.Count, list.Count, null));
                return results;
            }, ct);
    }
}
