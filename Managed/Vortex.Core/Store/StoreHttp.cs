using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Assets.Library;

namespace Editor.Core.Assets.Store
{
    /// <summary>
    /// HTTP for the store: one client (redirects followed — ambientCG downloads go through a redirect), a polite
    /// per-provider request spacing, a response cache on disk (search results and pages are reused instead of asking
    /// again; ambientCG asks for that explicitly), a thumbnail cache and resumable downloads (HTTP range).
    /// Every request identifies itself with a unique User-Agent (Poly Haven's API terms require one).
    /// </summary>
    public static class StoreHttp
    {
        private static HttpClient _client;
        private static HttpMessageHandler _handler;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, DateTime> LastRequest = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, SemaphoreSlim> Spacing = new Dictionary<string, SemaphoreSlim>();

        public static string UserAgent => "VortexEngine/" + Editor.Core.EngineInfo.VersionString + " (asset store; +https://github.com/shadow-kernel/Vortex-Engine)";

        /// <summary>Tests swap the transport (a fake handler) — set before the first request.</summary>
        public static HttpMessageHandler Handler
        {
            get => _handler;
            set { lock (Gate) { _handler = value; _client?.Dispose(); _client = null; } }
        }

        /// <summary>Override for the cache folder (tests); default = the asset library's store-cache folder.</summary>
        public static string CacheRootOverride;

        public static string CacheRoot => CacheRootOverride ?? Path.Combine(GlobalAssetDatabase.Instance.Root, "store-cache");

        private static HttpClient Client
        {
            get
            {
                lock (Gate)
                {
                    if (_client != null) return _client;
                    var h = _handler ?? new SocketsHttpHandler
                    {
                        AllowAutoRedirect = true,
                        MaxAutomaticRedirections = 8,
                        AutomaticDecompression = DecompressionMethods.All,
                        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    };
                    _client = new HttpClient(h, disposeHandler: _handler == null) { Timeout = TimeSpan.FromMinutes(30) };
                    return _client;
                }
            }
        }

        /// <summary>Wait until the provider may send its next request (one request in flight per provider at a time
        /// for API calls; CDN downloads are not spaced) and count it against the provider's daily limit.</summary>
        private static async Task PaceAsync(IAssetProvider p, CancellationToken ct)
        {
            CountRequest(p);
            if (p == null || p.MinInterval <= TimeSpan.Zero) return;
            SemaphoreSlim sem;
            lock (Spacing) { if (!Spacing.TryGetValue(p.Id, out sem)) Spacing[p.Id] = sem = new SemaphoreSlim(1, 1); }
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                DateTime last;
                lock (LastRequest) LastRequest.TryGetValue(p.Id, out last);
                var wait = last + p.MinInterval - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                lock (LastRequest) LastRequest[p.Id] = DateTime.UtcNow;
            }
            finally { sem.Release(); }
        }

        // ---- daily request limits (Freesound: 2,000 API requests per day) — persisted, so a restart doesn't reset them
        private sealed class DailyUsage
        {
            public string Day { get; set; }
            public int Count { get; set; }
        }

        private static readonly object UsageGate = new object();
        private static Dictionary<string, DailyUsage> _usage;

        private static string UsageFile => Path.Combine(CacheRoot, "usage.json");

        private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static Dictionary<string, DailyUsage> Usage
        {
            get
            {
                if (_usage != null) return _usage;
                try { _usage = File.Exists(UsageFile) ? JsonSerializer.Deserialize<Dictionary<string, DailyUsage>>(File.ReadAllText(UsageFile)) : null; } catch { _usage = null; }
                return _usage ?? (_usage = new Dictionary<string, DailyUsage>());
            }
        }

        /// <summary>Count one API request; throws a "limit reached" 429 when the provider's budget for today is used up.</summary>
        private static void CountRequest(IAssetProvider p)
        {
            int limit = p?.DailyRequestLimit ?? 0;
            if (limit <= 0) return;
            lock (UsageGate)
            {
                if (!Usage.TryGetValue(p.Id, out var u) || u.Day != Today) Usage[p.Id] = u = new DailyUsage { Day = Today };
                if (u.Count >= limit)
                    throw new StoreHttpException(HttpStatusCode.TooManyRequests, p.Name + "'s daily limit of " + limit.ToString("N0", CultureInfo.InvariantCulture) +
                                                 " requests is used up — cached results still work; the limit resets at midnight UTC.", limitReached: true);
                u.Count++;
                try { Directory.CreateDirectory(CacheRoot); File.WriteAllText(UsageFile, JsonSerializer.Serialize(Usage)); } catch { }
            }
        }

        /// <summary>API requests sent to a provider today (UTC).</summary>
        public static int RequestsToday(string providerId)
        {
            lock (UsageGate) return Usage.TryGetValue(providerId, out var u) && u.Day == Today ? u.Count : 0;
        }

        /// <summary>Forget the loaded usage counts (tests switch <see cref="CacheRootOverride"/>).</summary>
        public static void ReloadUsage() { lock (UsageGate) _usage = null; }

        private static HttpRequestMessage Request(HttpMethod m, string url, IAssetProvider p, bool authenticated)
        {
            var req = new HttpRequestMessage(m, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (authenticated) p?.Decorate(req);
            return req;
        }

        /// <summary>Thrown for HTTP errors with the status the UI can explain (401 = key, 429 = rate limit …).</summary>
        public sealed class StoreHttpException : Exception
        {
            public HttpStatusCode Status { get; }
            /// <summary>The provider's daily request limit is used up — retrying today won't help.</summary>
            public bool LimitReached { get; }
            public StoreHttpException(HttpStatusCode status, string message, bool limitReached = false) : base(message) { Status = status; LimitReached = limitReached; }
        }

        private static string Explain(HttpStatusCode s, string providerName)
        {
            switch ((int)s)
            {
                case 401: case 403: return providerName + " refused the request — check the API key in the Store settings.";
                case 404: return providerName + " no longer has this asset.";
                case 429: return providerName + " is rate limiting — wait a minute and try again.";
                default: return providerName + " answered HTTP " + (int)s + ".";
            }
        }

        private static string CacheFile(string kind, string providerId, string key, string ext)
        {
            using (var sha = SHA1.Create())
            {
                var h = ContentHash.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(key)));
                return Path.Combine(CacheRoot, kind, providerId ?? "_", h + ext);
            }
        }

        /// <summary>GET text (JSON / HTML), served from the disk cache while younger than <paramref name="ttl"/>.</summary>
        public static async Task<string> GetTextAsync(IAssetProvider p, string url, TimeSpan ttl, CancellationToken ct, bool authenticated = true)
        {
            string cache = CacheFile("responses", p?.Id, url, ".txt");
            try
            {
                if (ttl > TimeSpan.Zero && File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < ttl)
                    return await File.ReadAllTextAsync(cache, ct).ConfigureAwait(false);
            }
            catch { }
            await PaceAsync(p, ct).ConfigureAwait(false);
            using (var req = Request(HttpMethod.Get, url, p, authenticated))
            using (var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) throw new StoreHttpException(resp.StatusCode, Explain(resp.StatusCode, p?.Name ?? "The server"));
                string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (ttl > TimeSpan.Zero)
                    try { Directory.CreateDirectory(Path.GetDirectoryName(cache)); await File.WriteAllTextAsync(cache, text, ct).ConfigureAwait(false); } catch { }
                return text;
            }
        }

        public static async Task<JsonDocument> GetJsonAsync(IAssetProvider p, string url, TimeSpan ttl, CancellationToken ct, bool authenticated = true)
            => JsonDocument.Parse(await GetTextAsync(p, url, ttl, ct, authenticated).ConfigureAwait(false));

        /// <summary>Send a request with the shared client (User-Agent added) — the sound backends use it.</summary>
        public static Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, HttpCompletionOption option, CancellationToken ct)
        {
            if (!req.Headers.Contains("User-Agent")) req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            return Client.SendAsync(req, option, ct);
        }

        /// <summary>POST JSON (sound generation backends); never cached.</summary>
        public static async Task<HttpResponseMessage> PostAsync(IAssetProvider p, string url, HttpContent content, CancellationToken ct, Action<HttpRequestMessage> decorate = null)
        {
            await PaceAsync(p, ct).ConfigureAwait(false);
            var req = Request(HttpMethod.Post, url, p, true);
            decorate?.Invoke(req);
            req.Content = content;
            return await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }

        /// <summary>A thumbnail as a local file (downloaded once, kept for a week). Null when it can't be fetched.</summary>
        public static Task<string> GetImageFileAsync(string url, CancellationToken ct) => GetCachedFileAsync(url, ".img", ct);

        /// <summary>Any small file (thumbnail, audio preview) as a cached local file with <paramref name="ext"/>.</summary>
        public static async Task<string> GetCachedFileAsync(string url, string ext, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string cache = CacheFile(ext == ".img" ? "thumbs" : "previews", "_", url, ext);
            try { if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromDays(7)) return cache; } catch { }
            try
            {
                using (var req = Request(HttpMethod.Get, url, null, false))
                using (var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    Directory.CreateDirectory(Path.GetDirectoryName(cache));
                    string tmp = cache + "." + Guid.NewGuid().ToString("N") + ".part";
                    await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                    File.Move(tmp, cache, true);
                    return cache;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        /// <summary>
        /// Download <paramref name="url"/> to <paramref name="target"/>, resuming a previous partial download
        /// (<c>target.part</c>, HTTP Range) when the server supports it. <paramref name="progress"/>(done, total).
        /// </summary>
        public static async Task DownloadAsync(IAssetProvider p, string url, string target, bool authenticated, Action<long, long> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string part = target + ".part";
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            using (var req = Request(HttpMethod.Get, url, p, authenticated))
            {
                if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
                using (var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
                    {
                        // the part is already complete (or the server changed the file): start over
                        File.Delete(part);
                        await DownloadAsync(p, url, target, authenticated, progress, ct).ConfigureAwait(false);
                        return;
                    }
                    if (!resp.IsSuccessStatusCode) throw new StoreHttpException(resp.StatusCode, Explain(resp.StatusCode, p?.Name ?? "The server"));
                    bool resumed = resp.StatusCode == HttpStatusCode.PartialContent && have > 0;
                    if (!resumed) have = 0;
                    long total = (resp.Content.Headers.ContentLength ?? -1) + (resumed ? have : 0);
                    using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    using (var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                    {
                        var buf = new byte[1 << 16];
                        long done = have;
                        var last = DateTime.MinValue;
                        int n;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                            done += n;
                            if ((DateTime.UtcNow - last).TotalMilliseconds > 120) { last = DateTime.UtcNow; progress?.Invoke(done, total); }
                        }
                        progress?.Invoke(done, total);
                    }
                }
            }
            File.Move(part, target, true);
        }

        /// <summary>MD5 of a file (Poly Haven publishes MD5 per file).</summary>
        public static string Md5(string path)
        {
            using (var md5 = MD5.Create())
            using (var fs = File.OpenRead(path))
                return ContentHash.Hex(md5.ComputeHash(fs));
        }

        /// <summary>Drop the cached responses of one provider (the "refresh" button).</summary>
        public static void ClearResponses(string providerId)
        {
            try { var d = Path.Combine(CacheRoot, "responses", providerId); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }

        public static string UrlEncode(string s) => Uri.EscapeDataString(s ?? "");
    }

    /// <summary>
    /// The user's own provider keys (poly.pizza, Freesound, Sketchfab …) in <c>&lt;VortexAppData&gt;/store-keys.json</c>
    /// (owner-only file permissions on macOS/Linux). The editor never ships a shared key.
    /// </summary>
    public static class StoreKeys
    {
        private static Dictionary<string, string> _keys;
        private static readonly object Gate = new object();

        public static string FilePath => Path.Combine(Editor.Core.Services.EditorPaths.VortexAppData, "store-keys.json");

        private static Dictionary<string, string> Keys
        {
            get
            {
                if (_keys != null) return _keys;
                try { _keys = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) : null; } catch { _keys = null; }
                return _keys ?? (_keys = new Dictionary<string, string>());
            }
        }

        public static string Get(string providerId)
        {
            var env = Environment.GetEnvironmentVariable("VORTEX_STORE_KEY_" + providerId.ToUpperInvariant().Replace('-', '_'));
            if (!string.IsNullOrEmpty(env)) return env;
            lock (Gate) return Keys.TryGetValue(providerId, out var k) ? k : null;
        }

        public static bool Has(string providerId) => !string.IsNullOrWhiteSpace(Get(providerId));

        public static void Set(string providerId, string key)
        {
            lock (Gate)
            {
                if (string.IsNullOrWhiteSpace(key)) Keys.Remove(providerId); else Keys[providerId] = key.Trim();
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Keys, new JsonSerializerOptions { WriteIndented = true }));
                if (!OperatingSystem.IsWindows())
                    try { File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            }
        }

        /// <summary>Forget the cached file contents (tests).</summary>
        public static void Reload() { lock (Gate) _keys = null; }
    }
}
