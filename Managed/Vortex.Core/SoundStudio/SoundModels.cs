using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.Core.Audio.SoundStudio
{
    /// <summary>What to generate (maps 1:1 to the ElevenLabs sound-generation fields).</summary>
    public sealed class SoundRequest
    {
        public string Prompt;
        /// <summary>Length in seconds; null = the backend picks ("auto").</summary>
        public double? DurationSeconds;
        public bool Loop;
        /// <summary>0..1 — how literally the backend follows the prompt (ElevenLabs default 0.3).</summary>
        public double PromptInfluence = 0.3;
        /// <summary>Variation seed (procedural backend; others are not deterministic).</summary>
        public int Seed;
        /// <summary>One-line description of the take (Claude writes it).</summary>
        public string Label;

        public SoundRequest Clone() => (SoundRequest)MemberwiseClone();
    }

    /// <summary>One generated take, as a file on disk.</summary>
    public sealed class GeneratedSound
    {
        public string FilePath;
        public string BackendId;
        public SoundRequest Request;
        public string Label;
        public string CostNote;
        /// <summary>What the backend delivered ("mp3_44100_128", "pcm_44100", "wav", "mp3" …) — kept in the recipe.</summary>
        public string Format;
        public DateTime Created = DateTime.UtcNow;
        public override string ToString() => Label ?? Request?.Prompt;
    }

    /// <summary>A text-to-audio backend of the Sound Studio.</summary>
    public interface ISoundBackend
    {
        string Id { get; }
        string Name { get; }
        string Tagline { get; }
        /// <summary>Key id in <see cref="Editor.Core.Assets.Store.StoreKeys"/>; null = works without a key.</summary>
        string KeyId { get; }
        string KeyHelpUrl { get; }
        double MaxSeconds { get; }
        bool SupportsLoop { get; }
        bool SupportsInfluence { get; }
        /// <summary>License id written to generated files ("ElevenLabs", "fal-ai", null = your own work).</summary>
        string LicenseId { get; }
        /// <summary>Short honest note about rights / costs shown in the studio.</summary>
        string Notice { get; }
        /// <summary>Estimated cost of one generation ("≈ 200 credits").</summary>
        string CostHint(SoundRequest r);
        /// <summary>Generate one take into <paramref name="outDir"/>.</summary>
        Task<GeneratedSound> GenerateAsync(SoundRequest r, string outDir, CancellationToken ct);
    }

    /// <summary>
    /// How a sound was made (#83) — stored with the library entry and in the project .vmeta, so the Sound Studio can
    /// reopen it and generate a sibling ("same creak, but longer"). Text-to-audio is not deterministic: regenerating
    /// makes a NEW asset linked to its parent, never a byte-identical copy.
    /// </summary>
    public sealed class SoundRecipe
    {
        [JsonPropertyName("input")] public string Input { get; set; }
        [JsonPropertyName("prompt")] public string Prompt { get; set; }
        [JsonPropertyName("backend")] public string Backend { get; set; }
        [JsonPropertyName("designer")] public string Designer { get; set; }
        [JsonPropertyName("duration_seconds")] public double? DurationSeconds { get; set; }
        [JsonPropertyName("loop")] public bool Loop { get; set; }
        [JsonPropertyName("prompt_influence")] public double PromptInfluence { get; set; }
        [JsonPropertyName("format")] public string Format { get; set; }
        [JsonPropertyName("seed")] public int Seed { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; }
        [JsonPropertyName("created")] public string Created { get; set; }
        [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new List<string>();
        [JsonPropertyName("parent")] public string Parent { get; set; }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        public static SoundRecipe FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<SoundRecipe>(json, Json); } catch { return null; }
        }

        public static SoundRecipe From(GeneratedSound g, string input, string designer, IEnumerable<string> notes, string parent)
        {
            var r = new SoundRecipe
            {
                Input = input, Prompt = g.Request.Prompt, Backend = g.BackendId, Designer = designer, DurationSeconds = g.Request.DurationSeconds,
                Loop = g.Request.Loop, PromptInfluence = g.Request.PromptInfluence, Format = g.Format, Seed = g.Request.Seed, Label = g.Label ?? g.Request.Label,
                Created = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Parent = parent,
            };
            if (notes != null) r.Notes.AddRange(notes);
            return r;
        }

        public SoundRequest ToRequest() => new SoundRequest { Prompt = Prompt, DurationSeconds = DurationSeconds, Loop = Loop, PromptInfluence = PromptInfluence, Seed = Seed + 1, Label = Label };
    }

    /// <summary>One-click starting points (horror + shooter).</summary>
    public static class SoundPresets
    {
        public static readonly (string name, string prompt, double? seconds, bool loop)[] All =
        {
            ("Horror Stinger", "Sudden horror stinger: dissonant string cluster hit with a metallic scrape, sub boom underneath, fast attack, long ringing decay", 4, false),
            ("Footsteps Stone", "Slow footsteps on wet basement concrete, leather boots, close perspective, slight echo in a small stone room", 5, false),
            ("Door Creak", "Old heavy wooden door slowly creaking open on rusty hinges, long creak with tension, quiet room", 4, false),
            ("Ambience Basement", "Dark basement ambience: low ventilation hum, distant water drips, faint pipe groans, quiet and oppressive", 20, true),
            ("Whisper", "Unintelligible breathy whisper close to the ear, eerie, slightly reverberant, horror", 3, false),
            ("Heartbeat", "Slow tense heartbeat, deep and muffled, heard from inside the body", 6, true),
            ("Gunshot Distant", "Single rifle gunshot far away across an open valley, sharp crack then rolling echo off hills", 3, false),
            ("Reload", "Assault rifle reload: magazine out, magazine in, charging handle pulled and released, crisp metallic clicks", 2.5, false),
            ("Bullet Whiz", "Bullet whizzing past the listener's head, quick supersonic crack and whoosh, left to right", 1, false),
            ("Explosion Far", "Distant explosion: deep boom with debris and long rumbling tail across a city", 5, false),
            ("UI Click", "Short clean futuristic UI click, soft and satisfying, tactical menu", 0.5, false),
            ("Wind Ruins", "Cold wind howling through a ruined concrete building, gusts and whistles", 15, true),
        };
    }

    internal static class Wav
    {
        /// <summary>16-bit PCM WAV from float samples (-1..1).</summary>
        public static void Write(string path, float[] samples, int rate, int channels = 1)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var w = new BinaryWriter(File.Create(path)))
            {
                int bytes = samples.Length * 2;
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); w.Write(36 + bytes);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); w.Write(bytes);
                foreach (var s in samples) w.Write((short)Math.Round(Math.Max(-1f, Math.Min(1f, s)) * 32760));
            }
        }

        /// <summary>Raw little-endian 16-bit PCM (ElevenLabs pcm_44100) → WAV.</summary>
        public static void WrapPcm16(string path, byte[] pcm, int rate, int channels = 1)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); w.Write(36 + pcm.Length);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); w.Write(pcm.Length);
                w.Write(pcm);
            }
        }
    }
}
