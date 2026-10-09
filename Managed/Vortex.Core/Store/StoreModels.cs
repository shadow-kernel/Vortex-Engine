using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.Core.Assets.Store
{
    /// <summary>What a store result is (decides the import path).</summary>
    public enum StoreKind { Model, Material, Hdri, Texture, Sound, Animation, Pack, Other }

    /// <summary>How a provider is reached.</summary>
    public enum ProviderAccess
    {
        /// <summary>Public API, no account (Poly Haven, ambientCG, Kenney).</summary>
        Anonymous,
        /// <summary>The user's own key/token from the provider's site (poly.pizza, Freesound, Sketchfab downloads).</summary>
        ApiKey,
        /// <summary>No API: the user downloads in their browser and Vortex picks the files up (Mixamo, Sonniss).</summary>
        Guided,
    }

    /// <summary>
    /// A license, mapped from whatever the provider reports (never guessed). Ids are SPDX where one exists.
    /// <see cref="Redistributable"/> = the files may be re-shared outside a built game (library bundles); a game build
    /// may always contain them unless <see cref="Commercial"/> is false and the game is sold.
    /// </summary>
    public sealed class StoreLicense
    {
        public string Id;
        public string Name;
        public string Url;
        public bool Attribution;
        public bool Commercial = true;
        public bool ShareAlike;
        public bool NoDerivatives;
        public bool Redistributable = true;
        /// <summary>Extra terms shown with the license ("no AI training", "re-hosting prohibited" …).</summary>
        public string Notes;

        /// <summary>Short badge text: CC0, CC BY, CC BY-NC-SA, Mixamo …</summary>
        public string Badge
        {
            get
            {
                if (Id == null) return "?";
                if (Id.StartsWith("CC0", StringComparison.OrdinalIgnoreCase)) return "CC0";
                if (Id.StartsWith("CC-", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = Id.Split('-').Skip(1).TakeWhile(p => !char.IsDigit(p[0]));
                    return "CC " + string.Join("-", parts);
                }
                return Name ?? Id;
            }
        }

        /// <summary>Safe to ship in a commercial game without further thought (credit may still be required).</summary>
        public bool GameSafe => Commercial && !NoDerivatives && !ShareAlike;

        public override string ToString() => Badge;

        private static readonly Dictionary<string, StoreLicense> Known = new Dictionary<string, StoreLicense>(StringComparer.OrdinalIgnoreCase);

        private static void Add(StoreLicense l) => Known[l.Id] = l;

        static StoreLicense()
        {
            Add(new StoreLicense { Id = "CC0-1.0", Name = "Creative Commons Zero (public domain)", Url = "https://creativecommons.org/publicdomain/zero/1.0/" });
            foreach (var v in new[] { "3.0", "4.0" })
            {
                Add(new StoreLicense { Id = "CC-BY-" + v, Name = "Creative Commons Attribution " + v, Url = "https://creativecommons.org/licenses/by/" + v + "/", Attribution = true });
                Add(new StoreLicense { Id = "CC-BY-SA-" + v, Name = "Creative Commons Attribution-ShareAlike " + v, Url = "https://creativecommons.org/licenses/by-sa/" + v + "/", Attribution = true, ShareAlike = true });
                Add(new StoreLicense { Id = "CC-BY-ND-" + v, Name = "Creative Commons Attribution-NoDerivatives " + v, Url = "https://creativecommons.org/licenses/by-nd/" + v + "/", Attribution = true, NoDerivatives = true });
                Add(new StoreLicense { Id = "CC-BY-NC-" + v, Name = "Creative Commons Attribution-NonCommercial " + v, Url = "https://creativecommons.org/licenses/by-nc/" + v + "/", Attribution = true, Commercial = false });
                Add(new StoreLicense { Id = "CC-BY-NC-SA-" + v, Name = "Creative Commons Attribution-NonCommercial-ShareAlike " + v, Url = "https://creativecommons.org/licenses/by-nc-sa/" + v + "/", Attribution = true, Commercial = false, ShareAlike = true });
                Add(new StoreLicense { Id = "CC-BY-NC-ND-" + v, Name = "Creative Commons Attribution-NonCommercial-NoDerivatives " + v, Url = "https://creativecommons.org/licenses/by-nc-nd/" + v + "/", Attribution = true, Commercial = false, NoDerivatives = true });
            }
            Add(new StoreLicense { Id = "Sampling-Plus-1.0", Name = "Creative Commons Sampling+ 1.0", Url = "https://creativecommons.org/licenses/sampling+/1.0/", Attribution = true });
            Add(new StoreLicense { Id = "Mixamo", Name = "Mixamo (Adobe) — royalty-free in games, no re-hosting", Url = "https://helpx.adobe.com/creative-cloud/faq/mixamo-faq.html", Redistributable = false, Notes = "Use in games and films is royalty-free; the files themselves may not be shared or re-hosted." });
            Add(new StoreLicense { Id = "Sonniss-GDC", Name = "Sonniss GDC bundle — royalty-free, no attribution", Url = "https://sonniss.com/gameaudiogdc", Redistributable = false, Notes = "Unlimited commercial use in games; no redistribution as files or as a sound library; no AI/ML training." });
            Add(new StoreLicense { Id = "Sketchfab-Standard", Name = "Sketchfab Standard / Free Standard license", Url = "https://sketchfab.com/licenses", Redistributable = false, Notes = "Use in a game is allowed; the files may not be re-shared." });
            Add(new StoreLicense { Id = "ElevenLabs", Name = "ElevenLabs generated audio", Url = "https://elevenlabs.io/terms-of-use", Redistributable = false, Notes = "Paid plans: commercial use in games (no resale as sample packs). Free plan: non-commercial, with attribution." });
            Add(new StoreLicense { Id = "Stability-AI", Name = "Generated with the Stability AI API", Url = "https://stability.ai/terms-of-use", Redistributable = false, Notes = "Output use follows Stability AI's API terms — check them before shipping." });
            Add(new StoreLicense { Id = "fal-ai", Name = "Generated with a fal.ai model", Url = "https://fal.ai/terms", Redistributable = false, Notes = "Output rights follow the model's terms — check them before shipping." });
            Add(new StoreLicense { Id = "Unknown", Name = "Unknown license — check the source before shipping", Commercial = false, Redistributable = false });
        }

        public static StoreLicense Get(string id) => id != null && Known.TryGetValue(id, out var l) ? l : Known["Unknown"];
        public static bool IsKnown(string id) => id != null && Known.ContainsKey(id);
        public static IEnumerable<StoreLicense> All => Known.Values;

        /// <summary>A creativecommons.org URL (Freesound reports licenses that way).</summary>
        public static StoreLicense FromUrl(string url)
        {
            var u = (url ?? "").ToLowerInvariant();
            if (u.Contains("publicdomain/zero")) return Get("CC0-1.0");
            if (u.Contains("sampling+")) return Get("Sampling-Plus-1.0");
            string v = u.Contains("/4.0") ? "4.0" : "3.0";
            if (u.Contains("/by-nc-nd/")) return Get("CC-BY-NC-ND-" + v);
            if (u.Contains("/by-nc-sa/")) return Get("CC-BY-NC-SA-" + v);
            if (u.Contains("/by-nc/")) return Get("CC-BY-NC-" + v);
            if (u.Contains("/by-nd/")) return Get("CC-BY-ND-" + v);
            if (u.Contains("/by-sa/")) return Get("CC-BY-SA-" + v);
            if (u.Contains("/by/")) return Get("CC-BY-" + v);
            return Get("Unknown");
        }

        /// <summary>Freesound / Sketchfab / poly.pizza style labels ("CC Attribution-NonCommercial", "CC0 1.0", "CC-BY 3.0").</summary>
        public static StoreLicense FromLabel(string label)
        {
            var l = (label ?? "").ToLowerInvariant().Replace("creative commons", "cc").Replace("attribution", "by").Trim();
            if (l.StartsWith("cc0") || l.Contains("public domain") || l == "cc 0") return Get("CC0-1.0");
            if (l.Contains("free standard") || l.Contains("standard")) return Get("Sketchfab-Standard");
            bool nc = l.Contains("noncommercial") || l.Contains("non-commercial") || l.Contains("-nc") || l.Contains(" nc");
            bool nd = l.Contains("noderiv") || l.Contains("-nd") || l.Contains(" nd");
            bool sa = l.Contains("sharealike") || l.Contains("share-alike") || l.Contains("-sa") || l.Contains(" sa");
            string v = l.Contains("3.0") ? "3.0" : "4.0";
            if (!l.Contains("by")) return Get("Unknown");
            string id = "CC-BY" + (nc ? "-NC" : "") + (nd ? "-ND" : sa ? "-SA" : "") + "-" + v;
            return Get(id);
        }
    }

    /// <summary>One search result.</summary>
    public sealed class StoreItem
    {
        public string ProviderId;
        public string Id;
        public string Name;
        public StoreKind Kind;
        public string ThumbnailUrl;
        public string Author;
        public StoreLicense License;
        public string PageUrl;
        /// <summary>Audio preview to audition before downloading (Freesound).</summary>
        public string PreviewUrl;
        public List<string> Tags = new List<string>();
        public List<string> Categories = new List<string>();
        public double? Duration;
        public long? Size;
        public string Description;
        /// <summary>Provider-private data needed later (download urls in the search payload, …).</summary>
        public Dictionary<string, string> Extra = new Dictionary<string, string>();

        /// <summary>Stable id across sessions: "provider:id".</summary>
        public string Key => ProviderId + ":" + Id;
        public override string ToString() => Name;
    }

    public sealed class StoreQuery
    {
        public string Text;
        public StoreKind? Kind;
        public string Category;
        public int Page;
        public int PageSize = 40;
        /// <summary>Include licenses that forbid commercial use (off by default — they can't ship in a sold game).</summary>
        public bool IncludeNonCommercial;
        /// <summary>Include no-derivatives licenses (off by default — an ND asset may not be modified: no re-texturing,
        /// cutting or re-rigging).</summary>
        public bool IncludeNoDerivatives;
        /// <summary>Include share-alike licenses (on by default — usable, but flagged by the license check before builds).</summary>
        public bool IncludeShareAlike = true;

        public bool Allows(StoreLicense l)
        {
            if (l == null) return true;
            if (!l.Commercial && !IncludeNonCommercial) return false;
            if (l.NoDerivatives && !IncludeNoDerivatives) return false;
            if (l.ShareAlike && !IncludeShareAlike) return false;
            return true;
        }
    }

    public sealed class StorePage
    {
        public List<StoreItem> Items = new List<StoreItem>();
        public int Total = -1;
        public bool HasMore;
        public string Error;
        /// <summary>The provider needs the user's key before it can answer.</summary>
        public bool NeedsKey;
    }

    /// <summary>A download choice: resolution / format / quality.</summary>
    public sealed class StoreVariant
    {
        public string Id;
        public string Label;
        public long Size;
        public override string ToString() => Label + (Size > 0 ? " · " + Library.GlobalAssetDatabase.FormatBytes(Size) : "");
    }

    public sealed class StoreDetails
    {
        public StoreItem Item;
        public List<StoreVariant> Variants = new List<StoreVariant>();
        public string DefaultVariant;
        public string Description;
        /// <summary>Facts for the detail panel ("Polycount 5,626", "44.1 kHz stereo" …).</summary>
        public List<string> Facts = new List<string>();
    }

    /// <summary>One file of a download.</summary>
    public sealed class DownloadFile
    {
        public string Url;
        /// <summary>Where the file goes inside the package folder ('/' separators).</summary>
        public string RelPath;
        public long Size;
        public string Md5;
        public string Sha256;
        /// <summary>A zip that is unpacked into the package folder after download.</summary>
        public bool Unzip;
        /// <summary>Send the provider's auth headers with this request (API downloads); plain CDN files go without.</summary>
        public bool Authenticated;
    }

    /// <summary>What to do with downloaded files.</summary>
    public enum StoreArtifact
    {
        /// <summary>A model and the files next to it (glTF + bin + textures).</summary>
        Model,
        /// <summary>PBR maps (a zip or separate files) → a generated .vmat with its textures.</summary>
        Material,
        /// <summary>One file (HDRI, sound, texture).</summary>
        Single,
        /// <summary>A pack zip: every supported file becomes its own library entry.</summary>
        Pack,
    }

    public sealed class DownloadPlan
    {
        public StoreItem Item;
        public StoreVariant Variant;
        public StoreArtifact Artifact;
        public List<DownloadFile> Files = new List<DownloadFile>();
        /// <summary>The main file inside the package (model / sound / hdri); null = detect after download.</summary>
        public string MainFile;
        /// <summary>Material maps whose role the provider knows (rel path → role: Albedo, NormalGL, NormalDX, Roughness,
        /// Metallic, AO, Height, Opacity, Emissive, ARM).</summary>
        public Dictionary<string, string> MapRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A free-asset source of the Store tab (#67). Providers only describe and resolve; the shared download pipeline
    /// (<see cref="StoreDownloads"/>) downloads, verifies, imports into the library and optionally the project.
    /// </summary>
    public interface IAssetProvider
    {
        string Id { get; }
        string Name { get; }
        string Tagline { get; }
        string HomeUrl { get; }
        /// <summary>Text the UI must show next to this provider's results (Poly Haven's ToS asks for it).</summary>
        string Attribution { get; }
        IReadOnlyList<StoreKind> Kinds { get; }
        ProviderAccess Access { get; }
        /// <summary>Label of the user's key ("API key", "API token"); null for anonymous providers.</summary>
        string KeyName { get; }
        /// <summary>Where the user gets a key.</summary>
        string KeyHelpUrl { get; }
        /// <summary>Results carry per-item licenses that the license filter applies to.</summary>
        bool HasLicenseFilter { get; }
        /// <summary>Minimum time between two requests (be polite — ambientCG is run by one person).</summary>
        TimeSpan MinInterval { get; }
        /// <summary>API requests the provider allows per day (UTC); 0 = no daily limit. Cached responses don't count.</summary>
        int DailyRequestLimit => 0;

        Task<StorePage> SearchAsync(StoreQuery query, CancellationToken ct);
        Task<IReadOnlyList<string>> CategoriesAsync(StoreKind kind, CancellationToken ct);
        Task<StoreDetails> DetailsAsync(StoreItem item, CancellationToken ct);
        Task<DownloadPlan> ResolveAsync(StoreItem item, StoreVariant variant, CancellationToken ct);
        /// <summary>Add the provider's headers (User-Agent is added for everyone; keys go here).</summary>
        void Decorate(HttpRequestMessage request);

        /// <summary>The thumbnail URL of a result — providers whose search results carry none (Kenney) look it up here.</summary>
        Task<string> ThumbnailUrlAsync(StoreItem item, CancellationToken ct) => Task.FromResult(item?.ThumbnailUrl);

        /// <summary>One item by its id, WITHOUT the licence filter (the caller applies it and can say why an item is
        /// excluded) — null when the provider cannot look items up directly or the id does not exist (#356).</summary>
        Task<StoreItem> GetItemAsync(string id, CancellationToken ct) => Task.FromResult<StoreItem>(null);
    }

    /// <summary>A store source without an API (Mixamo, Sonniss): instructions, a link to open, and how the files get in.</summary>
    public interface IGuidedProvider
    {
        string Id { get; }
        string Name { get; }
        string Tagline { get; }
        string HomeUrl { get; }
        StoreLicense License { get; }
        /// <summary>Step-by-step text for the Store panel.</summary>
        IReadOnlyList<string> Steps { get; }
        /// <summary>Import downloaded files (dropped, picked or found in a watched folder) into the library.</summary>
        Task<List<Library.RegisterResult>> ImportAsync(IEnumerable<string> files, IProgress<Library.LibraryProgress> progress, CancellationToken ct);
        /// <summary>Which files this provider takes ("*.fbx", "*.wav").</summary>
        IReadOnlyList<string> FilePatterns { get; }
    }
}
