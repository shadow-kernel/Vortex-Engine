using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Assets.Store;

namespace Editor.Core.Audio.SoundStudio
{
    /// <summary>A generation failed with a message the studio can show as is (no key, no credits, bad prompt …).</summary>
    public sealed class SoundGenException : Exception
    {
        public HttpStatusCode? Status { get; }
        public SoundGenException(string message, HttpStatusCode? status = null) : base(message) { Status = status; }
    }

    /// <summary>
    /// ElevenLabs Sound Effects (#80): <c>POST /v1/sound-generation</c> with the user's <c>xi-api-key</c>, body
    /// <c>{text, model_id, duration_seconds (0.5–30 | auto), prompt_influence, loop}</c>, raw audio back. MP3 by default
    /// (every plan); "pcm_44100" (Creator/Pro plans) is wrapped into a WAV. Free-tier generations are non-commercial
    /// and need attribution — commercial rights start with the Starter plan.
    /// </summary>
    public sealed class ElevenLabsBackend : ISoundBackend
    {
        public const string Url = "https://api.elevenlabs.io/v1/sound-generation";
        public string Id => "elevenlabs";
        public string Name => "ElevenLabs Sound Effects";
        public string Tagline => "Realistic SFX up to 30 s, loops for ambience";
        public string KeyId => "elevenlabs";
        public string KeyHelpUrl => "https://elevenlabs.io/app/settings/api-keys";
        public double MaxSeconds => 30;
        public bool SupportsLoop => true;
        public bool SupportsInfluence => true;
        public string LicenseId => "ElevenLabs";
        public string Notice => "Uses your ElevenLabs credits (≈200 per auto-length take, 40 per second when the length is set). Free plan: non-commercial with attribution; paid plans: commercial use in games (no resale as sample packs).";
        /// <summary>"mp3_44100_128" (default, all plans) or "pcm_44100" (Creator/Pro).</summary>
        public string OutputFormat = "mp3_44100_128";

        public string CostHint(SoundRequest r) => r.DurationSeconds.HasValue ? "≈ " + Math.Ceiling(r.DurationSeconds.Value * 40).ToString(CultureInfo.InvariantCulture) + " credits" : "≈ 200 credits";

        public async Task<GeneratedSound> GenerateAsync(SoundRequest r, string outDir, CancellationToken ct)
        {
            string key = StoreKeys.Get(KeyId);
            if (string.IsNullOrWhiteSpace(key)) throw new SoundGenException("Add your ElevenLabs API key first (Keys…).");
            var body = new JsonObject
            {
                ["text"] = (r.Prompt ?? "").Trim(),
                ["model_id"] = "eleven_text_to_sound_v2",
                ["prompt_influence"] = Math.Max(0, Math.Min(1, r.PromptInfluence)),
                ["loop"] = r.Loop,
            };
            if (r.DurationSeconds.HasValue) body["duration_seconds"] = Math.Max(0.5, Math.Min(30, r.DurationSeconds.Value));
            using (var req = new HttpRequestMessage(HttpMethod.Post, Url + "?output_format=" + OutputFormat))
            {
                req.Headers.TryAddWithoutValidation("xi-api-key", key);
                req.Headers.TryAddWithoutValidation("Accept", "audio/*");
                req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using (var resp = await StoreHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) throw new SoundGenException(Explain(resp.StatusCode, bytes), resp.StatusCode);
                    bool pcm = OutputFormat.StartsWith("pcm", StringComparison.Ordinal);
                    string file = Path.Combine(outDir, "elevenlabs_" + Guid.NewGuid().ToString("N").Substring(0, 8) + (pcm ? ".wav" : ".mp3"));
                    Directory.CreateDirectory(outDir);
                    if (pcm) Wav.WrapPcm16(file, bytes, 44100); else await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
                    return new GeneratedSound { FilePath = file, BackendId = Id, Request = r.Clone(), Label = r.Label, CostNote = CostHint(r), Format = OutputFormat };
                }
            }
        }

        /// <summary>Turn ElevenLabs' error bodies ({"detail":{"status","message"}} or 422 lists) into one sentence.</summary>
        internal static string Explain(HttpStatusCode code, byte[] body)
        {
            string detail = null;
            try
            {
                var n = JsonNode.Parse(Encoding.UTF8.GetString(body ?? new byte[0]));
                var d = n?["detail"];
                if (d is JsonObject o) detail = (o["message"] ?? o["status"])?.ToString();
                else if (d is JsonArray a && a.Count > 0) detail = a[0]?["msg"]?.ToString();
                else if (d != null) detail = d.ToString();
            }
            catch { }
            switch ((int)code)
            {
                case 401: return "ElevenLabs rejected the API key" + (detail != null ? " (" + detail + ")" : "") + ".";
                case 402: case 429: return "ElevenLabs: out of credits or rate limited" + (detail != null ? " — " + detail : "") + ".";
                case 422: return "ElevenLabs could not use this request" + (detail != null ? ": " + detail : "") + ".";
                default: return "ElevenLabs answered HTTP " + (int)code + (detail != null ? ": " + detail : "") + ".";
            }
        }
    }

    /// <summary>
    /// fal.ai (#82): one key in front of several audio models, through fal's queue API — submit
    /// (<c>POST https://queue.fal.run/{model}</c>, <c>Authorization: Key …</c>), poll the status URL, read the result and
    /// download the signed audio URL. Two presets: Stable Audio Open (music beds and ambience, up to 47 s) and
    /// CassetteAI sound effects. Stable Audio has no loop flag (the toggle is hidden).
    /// </summary>
    public sealed class FalBackend : ISoundBackend
    {
        private readonly string _model;
        private readonly bool _music;

        public static FalBackend StableAudio() => new FalBackend("fal-ai/stable-audio", true);
        public static FalBackend CassetteSfx() => new FalBackend("cassetteai/sound-effects-generator", false);

        private FalBackend(string model, bool music) { _model = model; _music = music; }

        public string Id => _music ? "fal-stable-audio" : "fal-cassette-sfx";
        public string Name => _music ? "Stable Audio Open (fal.ai)" : "CassetteAI SFX (fal.ai)";
        public string Tagline => _music ? "Music beds and ambience (up to 47 s)" : "Sound effects through fal.ai";
        public string KeyId => "fal";
        public string KeyHelpUrl => "https://fal.ai/dashboard/keys";
        public double MaxSeconds => _music ? 47 : 30;
        public bool SupportsLoop => false;
        public bool SupportsInfluence => false;
        public string LicenseId => "fal-ai";
        public string Notice => "Billed per run on your fal.ai account. Output rights follow the model's terms (" + _model + ") — check them before shipping.";
        public string CostHint(SoundRequest r) => _music ? "≈ $0.02–0.20 per run" : "≈ $0.01 per run";
        public TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

        public async Task<GeneratedSound> GenerateAsync(SoundRequest r, string outDir, CancellationToken ct)
        {
            string key = StoreKeys.Get(KeyId);
            if (string.IsNullOrWhiteSpace(key)) throw new SoundGenException("Add your fal.ai key first (Keys…).");
            double secs = Math.Max(1, Math.Min(MaxSeconds, r.DurationSeconds ?? (_music ? 30 : 5)));
            var input = new JsonObject { ["prompt"] = (r.Prompt ?? "").Trim() };
            if (_music) { input["seconds_total"] = (int)Math.Round(secs); input["steps"] = 100; }
            else input["duration"] = (int)Math.Round(secs);

            JsonNode submitted = await Call(HttpMethod.Post, "https://queue.fal.run/" + _model, key, input, ct).ConfigureAwait(false);
            string statusUrl = submitted?["status_url"]?.ToString(), responseUrl = submitted?["response_url"]?.ToString();
            if (statusUrl == null || responseUrl == null) throw new SoundGenException("fal.ai did not accept the job.");
            for (int i = 0; i < 400; i++)
            {
                var st = await Call(HttpMethod.Get, statusUrl, key, null, ct).ConfigureAwait(false);
                string status = st?["status"]?.ToString();
                if (status == "COMPLETED") break;
                if (status == "FAILED" || status == "ERROR") throw new SoundGenException("fal.ai: the job failed" + (st?["error"] != null ? " — " + st["error"] : "") + ".");
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            var result = await Call(HttpMethod.Get, responseUrl, key, null, ct).ConfigureAwait(false);
            string audioUrl = FindAudioUrl(result);
            if (audioUrl == null) throw new SoundGenException("fal.ai returned no audio file.");
            string ext = Path.GetExtension(new Uri(audioUrl).AbsolutePath);
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".wav";
            string file = Path.Combine(outDir, Id + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ext);
            Directory.CreateDirectory(outDir);
            using (var req = new HttpRequestMessage(HttpMethod.Get, audioUrl))
            using (var resp = await StoreHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) throw new SoundGenException("Downloading the generated audio failed (HTTP " + (int)resp.StatusCode + ").", resp.StatusCode);
                await File.WriteAllBytesAsync(file, await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            }
            var rr = r.Clone(); rr.DurationSeconds = secs;
            return new GeneratedSound { FilePath = file, BackendId = Id, Request = rr, Label = r.Label, CostNote = CostHint(r), Format = ext.TrimStart('.') };
        }

        private static async Task<JsonNode> Call(HttpMethod m, string url, string key, JsonObject body, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(m, url))
            {
                req.Headers.TryAddWithoutValidation("Authorization", "Key " + key);
                if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using (var resp = await StoreHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new SoundGenException(((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403 ? "fal.ai rejected the key" : "fal.ai answered HTTP " + (int)resp.StatusCode) + (text.Length > 0 && text.Length < 300 ? ": " + text : "") + ".", resp.StatusCode);
                    try { return JsonNode.Parse(text); } catch { return null; }
                }
            }
        }

        /// <summary>The first URL in the result that points at an audio file (models name the field differently).</summary>
        internal static string FindAudioUrl(JsonNode n)
        {
            if (n == null) return null;
            if (n is JsonValue v && v.TryGetValue(out string s) && s.StartsWith("http", StringComparison.Ordinal))
            {
                var path = s.Split('?')[0].ToLowerInvariant();
                if (path.EndsWith(".wav") || path.EndsWith(".mp3") || path.EndsWith(".ogg") || path.EndsWith(".flac")) return s;
                return null;
            }
            if (n is JsonObject o)
            {
                foreach (var key in new[] { "audio_file", "audio", "audio_url", "file" })
                    if (o[key] is JsonObject f && f["url"] != null) return f["url"].ToString();
                foreach (var kv in o) { var u = FindAudioUrl(kv.Value); if (u != null) return u; }
            }
            if (n is JsonArray a) foreach (var x in a) { var u = FindAudioUrl(x); if (u != null) return u; }
            return null;
        }
    }

    /// <summary>
    /// Stability AI's Stable Audio, direct (#82): Stable Audio 2.5 — <c>POST /v2beta/audio/stable-audio-2/text-to-audio</c>,
    /// synchronous, up to 190 s — and Stable Audio 3 — <c>POST /v2beta/audio/stable-audio/text-to-audio</c> answers 202
    /// with an id, then <c>GET /v2beta/audio/results/{id}</c> until it returns the audio, up to 380 s. The request is
    /// multipart/form-data (prompt, duration, model, output_format) with <c>Authorization: Bearer</c> + the user's key and
    /// <c>Accept: audio/*</c> for raw audio bytes. Field names checked against Stability's live OpenAPI spec
    /// (api.stability.ai/v2alpha/openapi, October 2026). There is no loop flag, so the studio hides the toggle.
    /// </summary>
    public sealed class StabilityAudioBackend : ISoundBackend
    {
        public const string Api = "https://api.stability.ai/v2beta/audio";
        private readonly bool _v3;

        public static StabilityAudioBackend Audio25() => new StabilityAudioBackend(false);
        public static StabilityAudioBackend Audio3() => new StabilityAudioBackend(true);

        private StabilityAudioBackend(bool v3) { _v3 = v3; }

        public string Id => _v3 ? "stability-audio-3" : "stability-audio-2.5";
        public string Name => _v3 ? "Stable Audio 3 (Stability AI)" : "Stable Audio 2.5 (Stability AI)";
        public string Tagline => _v3 ? "Music and long ambience up to 6:20 min" : "Music beds and long ambience up to 3:10 min";
        public string KeyId => "stability";
        public string KeyHelpUrl => "https://platform.stability.ai/account/keys";
        public double MaxSeconds => _v3 ? 380 : 190;
        public bool SupportsLoop => false;
        public bool SupportsInfluence => false;
        public string LicenseId => "Stability-AI";
        public string Notice => "Uses your Stability AI credits — " + CostHint(null) + " per take, the same for any length; failed generations are free. Output use follows Stability AI's API terms.";
        /// <summary>"mp3" (default) or "wav".</summary>
        public string OutputFormat = "mp3";
        public TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        public string CostHint(SoundRequest r) => _v3 ? "26 credits (≈ $0.26)" : "20 credits (≈ $0.20)";

        public async Task<GeneratedSound> GenerateAsync(SoundRequest r, string outDir, CancellationToken ct)
        {
            string key = StoreKeys.Get(KeyId);
            if (string.IsNullOrWhiteSpace(key)) throw new SoundGenException("Add your Stability AI API key first (Keys…).");
            var form = new MultipartFormDataContent();
            form.Add(new StringContent((r.Prompt ?? "").Trim()), "prompt");
            // auto = the API's default length (190 s); the price is per take, not per second
            if (r.DurationSeconds.HasValue) form.Add(new StringContent(Math.Max(1, Math.Min(MaxSeconds, r.DurationSeconds.Value)).ToString("0.#", CultureInfo.InvariantCulture)), "duration");
            form.Add(new StringContent(_v3 ? "stable-audio-3" : "stable-audio-2.5"), "model");
            form.Add(new StringContent(OutputFormat), "output_format");

            byte[] audio;
            string mediaType;
            using (var req = Request(HttpMethod.Post, Api + (_v3 ? "/stable-audio/text-to-audio" : "/stable-audio-2/text-to-audio"), key))
            {
                req.Content = form;
                using (var resp = await StoreHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) throw new SoundGenException(Explain(resp.StatusCode, bytes), resp.StatusCode);
                    if (resp.StatusCode == HttpStatusCode.Accepted)
                    {
                        string id = null;
                        try { id = JsonNode.Parse(Encoding.UTF8.GetString(bytes))?["id"]?.ToString(); } catch { }
                        if (string.IsNullOrEmpty(id)) throw new SoundGenException("Stability AI did not return a generation id.");
                        (audio, mediaType) = await PollAsync(id, key, ct).ConfigureAwait(false);
                    }
                    else { audio = bytes; mediaType = resp.Content.Headers.ContentType?.MediaType; }
                }
            }
            if (audio == null || audio.Length == 0) throw new SoundGenException("Stability AI returned no audio.");
            bool wav = mediaType != null ? mediaType.IndexOf("wav", StringComparison.OrdinalIgnoreCase) >= 0 : OutputFormat == "wav";
            string file = Path.Combine(outDir, Id.Replace('.', '_') + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + (wav ? ".wav" : ".mp3"));
            Directory.CreateDirectory(outDir);
            await File.WriteAllBytesAsync(file, audio, ct).ConfigureAwait(false);
            return new GeneratedSound { FilePath = file, BackendId = Id, Request = r.Clone(), Label = r.Label, CostNote = CostHint(r), Format = wav ? "wav" : "mp3" };
        }

        /// <summary>Stable Audio 3 is asynchronous: 202 while it runs, 200 with the audio when done.</summary>
        private async Task<(byte[] audio, string mediaType)> PollAsync(string id, string key, CancellationToken ct)
        {
            for (int i = 0; i < 450; i++)
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                using (var req = Request(HttpMethod.Get, Api + "/results/" + id, key))
                using (var resp = await StoreHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    if (resp.StatusCode == HttpStatusCode.Accepted) continue;
                    if (!resp.IsSuccessStatusCode) throw new SoundGenException(Explain(resp.StatusCode, bytes), resp.StatusCode);
                    return (bytes, resp.Content.Headers.ContentType?.MediaType);
                }
            }
            throw new SoundGenException("Stability AI is still working on the take after 15 minutes — try again later.");
        }

        private static HttpRequestMessage Request(HttpMethod m, string url, string key)
        {
            var req = new HttpRequestMessage(m, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            req.Headers.TryAddWithoutValidation("Accept", "audio/*");
            req.Headers.TryAddWithoutValidation("stability-client-id", "VortexEngine");
            req.Headers.TryAddWithoutValidation("stability-client-version", Editor.Core.EngineInfo.VersionString);
            return req;
        }

        /// <summary>Stability's error bodies are {"id", "name", "errors": [...]}: one sentence for the studio.</summary>
        internal static string Explain(HttpStatusCode code, byte[] body)
        {
            string detail = null;
            try
            {
                var n = JsonNode.Parse(Encoding.UTF8.GetString(body ?? new byte[0]));
                if (n?["errors"] is JsonArray a && a.Count > 0) detail = string.Join("; ", a.Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)));
                else detail = n?["message"]?.ToString() ?? n?["name"]?.ToString();
            }
            catch { }
            string d = string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")";
            switch ((int)code)
            {
                case 401: return "Stability AI rejected the API key" + d + ".";
                case 402: return "Stability AI: not enough credits on your account" + d + ".";
                case 403: return "Stability AI's content moderation flagged this prompt" + d + ".";
                case 400: case 422: return "Stability AI could not use this request" + d + ".";
                case 429: return "Stability AI is rate limiting — wait a moment and try again.";
                default: return "Stability AI answered HTTP " + (int)code + d + ".";
            }
        }
    }

    /// <summary>All sound backends, in display order.</summary>
    public static class SoundBackends
    {
        public static readonly IReadOnlyList<ISoundBackend> All = new ISoundBackend[]
        {
            new ProceduralBackend(), new ElevenLabsBackend(), FalBackend.CassetteSfx(), FalBackend.StableAudio(),
            StabilityAudioBackend.Audio25(), StabilityAudioBackend.Audio3(),
        };
        public static ISoundBackend Get(string id) => All.FirstOrDefault(b => b.Id == id) ?? All[0];
    }
}
