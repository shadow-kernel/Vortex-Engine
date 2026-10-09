using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Assets;
using Editor.Core.Assets.Library;
using Editor.Core.Assets.Store;
using Editor.Core.Audio.SoundStudio;
using Editor.Core.Serialization;

namespace VortexTests
{
    /// <summary>Claude Sound Studio (milestone v2.10.0): backends, the Claude tool loop, recipes.</summary>
    public static class SoundStudioTests
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

        private static void Key(string id, string value) { Environment.SetEnvironmentVariable("VORTEX_STORE_KEY_" + id.ToUpperInvariant().Replace('-', '_'), value); StoreKeys.Reload(); }

        [Test]
        public static void ProceduralFamilyRouting(TestContext t)
        {
            // whole words, most specific family first (#353)
            t.Equal("reload", ProceduralBackend.Family("pistol magazine reload"), "reload before gunshot");
            t.Equal("glass", ProceduralBackend.Family("glass bottle shatters on stone floor"), "glass");
            t.Equal("rustle", ProceduralBackend.Family("soft paper rustle"), "rustle");
            t.Equal("crowd", ProceduralBackend.Family("crowd murmur in a plaza"), "crowd");
            t.Equal("siren", ProceduralBackend.Family("car horn"), "horn");
            t.Equal("creak", ProceduralBackend.Family("Old heavy wooden door slowly creaking open on rusty hinges, long creak with tension, quiet room"), "door creak preset");
            t.Equal("impact", ProceduralBackend.Family("Slow footsteps on wet basement concrete, leather boots"), "footsteps preset: an impact, not a basement drone");
            t.Equal("drone", ProceduralBackend.Family("Dark basement ambience: low ventilation hum, distant water drips"), "ambience wins over water");
            t.Equal("whoosh", ProceduralBackend.Family("Bullet whizzing past the listener's head, quick supersonic crack and whoosh"), "bullet whiz preset");
            t.Equal("impact", ProceduralBackend.Family("short metallic clank"), "clank (the MCP smoke prompt)");
            t.Equal("glass", ProceduralBackend.Family("window breaks"), "window is not wind");
            t.Equal("impact", ProceduralBackend.Family("human thumping"), "thumping is an impact, human is not a hum");
            t.Equal("gunshot", ProceduralBackend.Family("Rifle gunshot far away"), "gunshot");
            t.Equal("fire", ProceduralBackend.Family("campfire crackling at night"), "fire");
            t.Equal("rain", ProceduralBackend.Family("heavy rain on a tin roof"), "rain");
            t.True(ProceduralBackend.Family("pressurised cabin") == null, "pressurised is not a riser");
            t.True(ProceduralBackend.Family("white architecture") == null, "white is not a hit");
            t.True(ProceduralBackend.Family("quiet liquid") == null, "quiet / liquid are not ui clicks");
            t.True(ProceduralBackend.Family("walking along slowly") == null, "along is not long, slowly is not low");
            t.True(ProceduralBackend.Family("xyzzy frobnicate") == null, "unknown prompt -> null");
            ProceduralBackend.Family("glass shatter", out string kw);
            t.Equal("glass", kw, "the deciding keyword is reported");
            // band-limited recipes: no broadband hiss — the long-term spectrum is far from flat
            t.True(Flatness(WhiteNoise(ProceduralBackend.Rate, 11)) > 0.75, "white noise measures as flat");
            var report = new List<string>(); bool allShaped = true;
            // thresholds leave room for the platform's float differences (CI measured glass 0.52 where this Mac read 0.48)
            foreach (var (prompt, max) in new[] { ("glass bottle shatters", 0.6), ("soft paper rustle", 0.5), ("crowd murmur in a plaza", 0.5), ("heavy rain on a roof", 0.65), ("pistol magazine reload", 0.6), ("campfire crackling", 0.5), ("quick whoosh", 0.6) })
            {
                var s = ProceduralBackend.Synthesize(prompt, 1.5, false, 2);
                double fl = Flatness(s);
                report.Add(prompt + " " + fl.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + (fl < max ? "" : " (>= " + max + ")"));
                if (fl >= max) allShaped = false;
            }
            t.True(allShaped, "spectral flatness per recipe: " + string.Join("; ", report));
            var soft = ProceduralBackend.Synthesize("soft paper rustle", 1.0, false, 3);
            t.True(soft.Max(Math.Abs) < 0.6f, "a soft prompt is not normalised to full scale");
        }

        private static float[] WhiteNoise(int n, int seed) { var r = new Random(seed); var x = new float[n]; for (int i = 0; i < n; i++) x[i] = (float)(r.NextDouble() * 2 - 1); return x; }

        /// <summary>Spectral flatness (geometric / arithmetic mean of the magnitude spectrum) averaged over 16 frames of
        /// 1024 samples — ~0.85 for white noise, well below 0.5 for anything with a shape.</summary>
        private static double Flatness(float[] x)
        {
            const int N = 1024; int frames = 16;
            var acc = new double[N / 2];
            for (int f = 0; f < frames; f++)
            {
                int start = (int)((long)(x.Length - N) * f / Math.Max(1, frames - 1));
                if (start < 0) start = 0;
                for (int k = 1; k < N / 2; k++)
                {
                    double re = 0, im = 0;
                    for (int i = 0; i < N && start + i < x.Length; i++) { double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N); double a = 2 * Math.PI * k * i / N; re += x[start + i] * w * Math.Cos(a); im -= x[start + i] * w * Math.Sin(a); }
                    acc[k] += Math.Sqrt(re * re + im * im);
                }
            }
            double logSum = 0, sum = 0; int cnt = 0;
            for (int k = 1; k < N / 2; k++) { double m = acc[k] / frames + 1e-9; logSum += Math.Log(m); sum += m; cnt++; }
            return Math.Exp(logSum / cnt) / (sum / cnt);
        }

        [Test]
        public static async Task ProceduralSynthMakesWavs(TestContext t)
        {
            t.Equal("heartbeat", ProceduralBackend.Family("slow tense heartbeat"), "heartbeat");
            t.Equal("gunshot", ProceduralBackend.Family("Rifle gunshot far away"), "gunshot");
            t.Equal("drone", ProceduralBackend.Family("dark basement ambience"), "ambience → drone");
            t.Equal("impact", ProceduralBackend.Family("heavy door slam"), "slam → impact");
            var s = ProceduralBackend.Synthesize("short metal impact with reverb", 1.0, false, 3);
            t.Equal(ProceduralBackend.Rate, s.Length, "1 s of samples");
            t.True(s.Max(Math.Abs) > 0.85f && s.Max(Math.Abs) <= 0.9f, "normalized");
            var loop = ProceduralBackend.Synthesize("wind", 4, true, 1);
            t.True(loop.Length < 4 * ProceduralBackend.Rate, "loop cut by the cross-fade");
            var b = new ProceduralBackend();
            var g = await b.GenerateAsync(new SoundRequest { Prompt = "ui click", DurationSeconds = 0.2 }, t.Path("out"), CancellationToken.None);
            var bytes = File.ReadAllBytes(g.FilePath);
            t.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4), "wav header");
            t.Equal(44 + (int)(0.2 * ProceduralBackend.Rate) * 2, bytes.Length, "16-bit mono payload");
            var other = ProceduralBackend.Synthesize("whoosh", 1, false, 4);
            t.False(other.SequenceEqual(ProceduralBackend.Synthesize("whoosh", 1, false, 5)), "seeds make different takes");
        }

        [Test]
        public static async Task ElevenLabsRequestAndErrors(TestContext t)
        {
            var fake = Setup(t);
            Key("elevenlabs", "xi-test");
            try
            {
                string body = null; HttpRequestMessage seen = null;
                fake.Routes.Add(("https://api.elevenlabs.io/v1/sound-generation", r =>
                {
                    seen = r; body = r.Content.ReadAsStringAsync().Result;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0xFF, 0xFB, 1, 2, 3 }) };
                }));
                var b = new ElevenLabsBackend();
                var g = await b.GenerateAsync(new SoundRequest { Prompt = " door creak ", DurationSeconds = 3, Loop = false, PromptInfluence = 0.5 }, t.Path("out"), CancellationToken.None);
                t.True(seen.RequestUri.Query.Contains("output_format=mp3_44100_128"), "mp3 by default");
                t.Equal("xi-test", seen.Headers.GetValues("xi-api-key").Single(), "key header");
                var j = JsonNode.Parse(body);
                t.Equal("door creak", (string)j["text"], "trimmed text");
                t.Equal("eleven_text_to_sound_v2", (string)j["model_id"], "model");
                t.Equal(3.0, (double)j["duration_seconds"], "duration");
                t.Equal(0.5, (double)j["prompt_influence"], "influence");
                t.True(g.FilePath.EndsWith(".mp3") && File.ReadAllBytes(g.FilePath).Length == 5, "audio written");
                t.Equal("≈ 120 credits", g.CostNote, "credit estimate: 40 per second");
                var auto = await b.GenerateAsync(new SoundRequest { Prompt = "x" }, t.Path("out"), CancellationToken.None);
                t.True(JsonNode.Parse(body)["duration_seconds"] == null, "auto length omits the field");
                fake.Routes.Clear();
                fake.Routes.Add(("https://api.elevenlabs.io/v1/sound-generation", r => new HttpResponseMessage((HttpStatusCode)401) { Content = new StringContent("{\"detail\":{\"status\":\"invalid_api_key\",\"message\":\"Invalid API key\"}}") }));
                string err = null;
                try { await b.GenerateAsync(new SoundRequest { Prompt = "x" }, t.Path("out"), CancellationToken.None); } catch (SoundGenException ex) { err = ex.Message; }
                t.True(err != null && err.Contains("rejected the API key") && err.Contains("Invalid API key"), "readable 401: " + err);
                fake.Routes.Clear();
                fake.Routes.Add(("https://api.elevenlabs.io/v1/sound-generation", r => new HttpResponseMessage((HttpStatusCode)422) { Content = new StringContent("{\"detail\":[{\"msg\":\"duration_seconds must be <= 30\"}]}") }));
                try { await b.GenerateAsync(new SoundRequest { Prompt = "x" }, t.Path("out"), CancellationToken.None); } catch (SoundGenException ex) { err = ex.Message; }
                t.True(err.Contains("duration_seconds must be <= 30"), "readable 422: " + err);
            }
            finally { Key("elevenlabs", null); }
        }

        [Test]
        public static async Task FalQueueFlow(TestContext t)
        {
            var fake = Setup(t);
            Key("fal", "fal-test");
            try
            {
                int polls = 0;
                fake.Json("https://queue.fal.run/fal-ai/stable-audio", "{\"request_id\":\"r1\",\"status_url\":\"https://queue.fal.run/fal-ai/stable-audio/requests/r1/status\",\"response_url\":\"https://queue.fal.run/fal-ai/stable-audio/requests/r1\"}");
                fake.Routes.Add(("https://queue.fal.run/fal-ai/stable-audio/requests/r1/status", r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(++polls < 2 ? "{\"status\":\"IN_PROGRESS\"}" : "{\"status\":\"COMPLETED\"}") }));
                fake.Json("https://queue.fal.run/fal-ai/stable-audio/requests/r1", "{\"audio_file\":{\"url\":\"https://v3.fal.media/files/abc/ambience.wav\",\"content_type\":\"audio/wav\"}}");
                fake.Bytes("https://v3.fal.media/files/abc/ambience.wav", Encoding.ASCII.GetBytes("RIFFfake"));
                var b = FalBackend.StableAudio();
                b.PollInterval = TimeSpan.Zero;
                var g = await b.GenerateAsync(new SoundRequest { Prompt = "dark ambience", DurationSeconds = 60 }, t.Path("out"), CancellationToken.None);
                t.True(g.FilePath.EndsWith(".wav"), "wav result");
                t.Equal(47.0, g.Request.DurationSeconds ?? 0, "clamped to the model's 47 s");
                var post = fake.Requests.First(r => r.Method == HttpMethod.Post);
                t.Equal("Key fal-test", post.Headers.Authorization.ToString(), "fal key header");
                t.True(polls >= 2, "polled until completed");
                t.False(b.SupportsLoop, "Stable Audio has no loop flag");
            }
            finally { Key("fal", null); }
        }

        [Test]
        public static async Task StabilityStableAudio(TestContext t)
        {
            var fake = Setup(t);
            Key("stability", "sk-stab-test");
            try
            {
                // Stable Audio 2.5: synchronous multipart → raw audio bytes
                string form = null; HttpRequestMessage seen = null;
                fake.Routes.Add(("https://api.stability.ai/v2beta/audio/stable-audio-2/text-to-audio", r =>
                {
                    seen = r; form = r.Content.ReadAsStringAsync().Result;
                    var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("RIFFwav")) };
                    ok.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
                    return ok;
                }));
                var b = StabilityAudioBackend.Audio25();
                b.OutputFormat = "wav";
                var g = await b.GenerateAsync(new SoundRequest { Prompt = " slow dark drone ", DurationSeconds = 400 }, t.Path("out"), CancellationToken.None);
                t.Equal("Bearer sk-stab-test", seen.Headers.Authorization.ToString(), "bearer key");
                t.Equal("audio/*", string.Join(",", seen.Headers.GetValues("Accept")), "raw audio requested");
                t.True(seen.Content.Headers.ContentType.MediaType == "multipart/form-data", "multipart body");
                foreach (var (field, value) in new[] { ("prompt", "slow dark drone"), ("duration", "190"), ("model", "stable-audio-2.5"), ("output_format", "wav") })
                    t.True(form.Contains("name=" + field) && form.Contains(value), field + " = " + value);
                t.True(g.FilePath.EndsWith(".wav") && g.Format == "wav", "wav take");
                t.Equal(190.0, b.MaxSeconds, "2.5 goes to 3:10 min");
                t.False(b.SupportsLoop, "no loop flag");

                // Stable Audio 3: 202 + id, then poll the result
                int polls = 0;
                fake.Routes.Add(("https://api.stability.ai/v2beta/audio/stable-audio/text-to-audio", r => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{\"id\":\"gen42\"}") }));
                fake.Routes.Add(("https://api.stability.ai/v2beta/audio/results/gen42", r =>
                {
                    if (++polls < 3) return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{\"id\":\"gen42\",\"status\":\"in-progress\"}") };
                    var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0xFF, 0xFB, 9 }) };
                    ok.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
                    return ok;
                }));
                var b3 = StabilityAudioBackend.Audio3();
                b3.PollInterval = TimeSpan.Zero;
                var g3 = await b3.GenerateAsync(new SoundRequest { Prompt = "menu music" }, t.Path("out"), CancellationToken.None);
                t.Equal(3, polls, "polled until the audio was ready");
                t.True(g3.FilePath.EndsWith(".mp3") && File.ReadAllBytes(g3.FilePath).Length == 3, "mp3 take");
                t.Equal(380.0, b3.MaxSeconds, "3 goes to 6:20 min");

                // errors: {"name", "errors": [...]}
                fake.Routes.Clear();
                fake.Routes.Add(("https://api.stability.ai/v2beta/audio/stable-audio-2/text-to-audio", r => new HttpResponseMessage((HttpStatusCode)403) { Content = new StringContent("{\"id\":\"e1\",\"name\":\"content_moderation\",\"errors\":[\"Your request was flagged\"]}") }));
                string err = null;
                try { await b.GenerateAsync(new SoundRequest { Prompt = "x" }, t.Path("out"), CancellationToken.None); } catch (SoundGenException ex) { err = ex.Message; }
                t.True(err != null && err.Contains("content moderation") && err.Contains("Your request was flagged"), "readable 403: " + err);
                t.True(SoundBackends.All.Any(x => x.Id == "stability-audio-2.5") && SoundBackends.All.Any(x => x.Id == "stability-audio-3"), "both in the backend list");
            }
            finally { Key("stability", null); }
        }

        private static string Sse(params string[] events) => string.Join("", events.Select(e => "event: x\ndata: " + e + "\n\n"));

        [Test]
        public static async Task ClaudeDesignsTakesThroughTheToolLoop(TestContext t)
        {
            var fake = Setup(t);
            Key("anthropic", "sk-ant-test");
            try
            {
                var bodies = new List<string>();
                int call = 0;
                fake.Routes.Add(("https://api.anthropic.com/v1/messages", r =>
                {
                    bodies.Add(r.Content.ReadAsStringAsync().Result);
                    call++;
                    string sse;
                    if (call == 1)
                        sse = Sse("{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":900}}}",
                                  "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
                                  "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Zwei Varianten: \"}}",
                                  "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"eine nah, eine fern.\"}}",
                                  "{\"type\":\"content_block_stop\",\"index\":0}",
                                  "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tu_1\",\"name\":\"generate_sound\",\"input\":{}}}",
                                  "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"prompt\\\": \\\"Heavy wooden door creaking open\"}}",
                                  "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\" slowly, close\\\", \\\"label\\\": \\\"Nah, langsam\\\", \\\"duration_seconds\\\": 3}\"}}",
                                  "{\"type\":\"content_block_stop\",\"index\":1}",
                                  "{\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tu_2\",\"name\":\"generate_sound\",\"input\":{}}}",
                                  "{\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"prompt\\\":\\\"Distant door creak in a large hall\\\",\\\"label\\\":\\\"Fern, Hall\\\"}\"}}",
                                  "{\"type\":\"content_block_stop\",\"index\":2}",
                                  "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":120}}",
                                  "{\"type\":\"message_stop\"}");
                    else
                        sse = Sse("{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":1100}}}",
                                  "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
                                  "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Fertig.\"}}",
                                  "{\"type\":\"content_block_stop\",\"index\":0}",
                                  "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":8}}");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
                }));
                var d = new SoundDesigner { Backend = new ProceduralBackend(), OutDir = t.Path("takes"), Model = "claude-opus-5-5" };
                var text = new StringBuilder();
                d.TextDelta += s => text.Append(s);
                var takes = await d.SendAsync("unheimliche Tür", 2, CancellationToken.None);
                t.Equal(2, takes.Count, "two takes");
                t.Equal("Nah, langsam", takes[0].Label, "Claude's label");
                t.Equal(3.0, takes[0].Request.DurationSeconds ?? 0, "Claude's duration");
                t.True(takes.All(x => File.Exists(x.FilePath)), "files generated");
                t.Equal("Zwei Varianten: eine nah, eine fern.Fertig.", text.ToString(), "streamed text");
                t.Equal(2000, d.InputTokens, "input tokens counted");
                t.Equal(128, d.OutputTokens, "output tokens counted");
                var first = JsonNode.Parse(bodies[0]);
                t.Equal("claude-opus-5-5", (string)first["model"], "model");
                t.True((bool)first["stream"], "streamed");
                t.Equal("generate_sound", (string)first["tools"][0]["name"], "tool offered");
                var second = JsonNode.Parse(bodies[1]);
                var results = second["messages"].AsArray().Last()["content"].AsArray();
                t.Equal("tu_1", (string)results[0]["tool_use_id"], "tool result answers the right call");
                t.Equal(2, results.Count, "both tool results returned");
                t.True(d.Notes.Any(n => n.Contains("Nah, langsam")), "notes keep the takes");
                t.Equal("unheimliche Tür", d.FirstInput, "first input kept for the recipe");
            }
            finally { Key("anthropic", null); }
        }

        [Test]
        public static async Task SavedTakesKeepTheirRecipe(TestContext t)
        {
            Setup(t);
            var g = await new ProceduralBackend().GenerateAsync(new SoundRequest { Prompt = "metal impact with reverb", DurationSeconds = 1, Label = "Metall, Hall" }, t.Path("takes"), CancellationToken.None);
            var recipe = SoundRecipe.From(g, "metallischer Schlag", "claude-opus-5-5", new[] { "user: mehr Hall" }, null);
            string project = t.Path("game");
            Directory.CreateDirectory(Path.Combine(project, "Assets"));
            var r = SoundStudioLibrary.Save(g, recipe, true, project, "Game");
            t.True(r.Success, "saved: " + r.Error);
            t.Equal(LibrarySource.Generated, r.Entry.SourceKind, "generated source");
            t.Equal(AssetType.Audio, r.Entry.Type, "audio entry");
            t.True(r.Entry.Tags.Contains("Generated") && r.Entry.Tags.Contains("SFX"), "tags");
            var back = SoundStudioLibrary.RecipeOf(GlobalAssetDatabase.Instance.Get(r.Entry.Id));
            t.Equal("metal impact with reverb", back.Prompt, "recipe prompt in the catalog");
            t.Equal("metallischer Schlag", back.Input, "original idea");
            t.Equal("procedural", back.Backend, "backend");
            t.Equal("wav", back.Format, "output format");
            t.Equal("claude-opus-5-5", back.Designer, "designer model");
            t.True(back.Notes.Contains("user: mehr Hall"), "Claude iteration notes");
            t.True(DateTime.TryParse(back.Created, null, System.Globalization.DateTimeStyles.RoundtripKind, out _), "generation time");
            t.True(r.ProjectPath.Contains(Path.Combine("Assets", "Audio")), "added to Assets/Audio: " + r.ProjectPath);
            var meta = DataSerializer.LoadFromJson<AssetMetadata>(r.ProjectPath + ".vmeta");
            t.Equal("metal impact with reverb", SoundRecipe.FromJson(meta.Recipe).Prompt, "recipe in the project .vmeta");
            var sibling = back.ToRequest();
            t.Equal(back.Seed + 1, sibling.Seed, "regenerating makes a sibling (new seed)");
        }
    }
}
