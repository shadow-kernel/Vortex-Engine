using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.Core.Audio.SoundStudio
{
    /// <summary>
    /// The offline backend: a small procedural synthesizer that reads the prompt for a sound family (click, beep, impact,
    /// whoosh, laser, gunshot, reload, explosion, heartbeat, riser, stinger, siren/alarm/horn, glass, fire, rain, water,
    /// creak, rustle, whisper, crowd, engine, wind, drone/ambience) and modifiers (short, long, low, high, metal, wood,
    /// distant, soft, reverb), then synthesizes it from band-limited noise, oscillators and envelopes — 44.1 kHz mono WAV.
    /// Whole words are matched, the most specific family first, and a prompt outside the recipes is reported as not
    /// understood instead of coming back as a generic noise burst (#353). No account, no cost, instant: placeholder SFX,
    /// UI sounds, and a test bed for the studio. Seeds give different takes; the result is your own work.
    /// </summary>
    public sealed class ProceduralBackend : ISoundBackend
    {
        public const int Rate = 44100;
        public string Id => "procedural";
        public string Name => "Procedural (offline)";
        public string Tagline => "Instant synthesized SFX — no account, no cost";
        public string KeyId => null;
        public string KeyHelpUrl => null;
        public double MaxSeconds => 30;
        public bool SupportsLoop => true;
        public bool SupportsInfluence => false;
        public string LicenseId => null;
        public string Notice => "Synthesized on your machine — the sounds are your own work. Recipes: " + string.Join(", ", KnownFamilies) +
                                ". A prompt outside them is reported as not understood; use a generative backend for realistic sounds.";
        public string CostHint(SoundRequest r) => "free";

        public Task<GeneratedSound> GenerateAsync(SoundRequest r, string outDir, CancellationToken ct)
            => Task.Run(() =>
            {
                var samples = Synthesize(r.Prompt ?? "", r.DurationSeconds, r.Loop, r.Seed);
                string file = Path.Combine(outDir, "procedural_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wav");
                Wav.Write(file, samples, Rate);
                return new GeneratedSound { FilePath = file, BackendId = Id, Request = r.Clone(), Label = r.Label ?? (Family(r.Prompt) ?? "sound") + " (procedural)", CostNote = "free", Format = "wav" };
            }, ct);

        // ------------------------------------------------------------------ prompt → family

        /// <summary>Every recipe the synthesizer knows.</summary>
        public static readonly string[] KnownFamilies =
        {
            "drone", "heartbeat", "reload", "explosion", "gunshot", "laser", "stinger", "riser", "siren", "glass", "fire", "rain",
            "water", "creak", "rustle", "whisper", "crowd", "impact", "click", "beep", "engine", "wind", "whoosh",
        };

        // Matching priority: the first rule whose keyword appears as a whole word (or, for keys of five letters and
        // more, as the start of a word: "creak" → "creaking") wins. Specific intents sit above generic ones, so
        // "pistol magazine reload" is a reload, "car horn" a horn, "footsteps on basement concrete" an impact — and
        // "window" never becomes wind, "quiet" never a UI click, "slowly" never a low pitch.
        private static readonly (string family, string[] keys)[] Rules =
        {
            ("drone",     new[] { "ambience", "ambient", "atmo", "atmosphere", "roomtone", "soundscape" }),
            ("heartbeat", new[] { "heartbeat", "heart", "herzschlag", "pulse" }),
            ("reload",    new[] { "reload", "reloading", "magazine", "mag", "charging", "bolt", "cocking", "chamber", "nachladen" }),
            ("explosion", new[] { "explosion", "explode", "explodes", "exploding", "blast", "boom", "detonate", "detonation", "grenade", "bomb" }),
            ("gunshot",   new[] { "gunshot", "gunshots", "gun", "gunfire", "rifle", "pistol", "shot", "shots", "shoot", "shooting", "shotgun", "firearm", "revolver", "sniper", "schuss" }),
            ("laser",     new[] { "laser", "zap", "blaster", "plasma", "phaser" }),
            ("stinger",   new[] { "stinger", "jumpscare", "scare", "sting" }),
            ("riser",     new[] { "riser", "rising", "sweep", "buildup", "swell", "crescendo" }),
            ("siren",     new[] { "siren", "sirene", "alarm", "buzzer", "horn", "klaxon", "honk", "hupe" }),
            ("glass",     new[] { "glass", "shatter", "shatters", "shattering", "bottle", "window", "smash", "ceramic", "porcelain", "plate", "vase" }),
            ("fire",      new[] { "fire", "flame", "flames", "burning", "crackle", "crackling", "campfire", "torch", "fireplace", "bonfire" }),
            ("rain",      new[] { "rain", "raining", "rainfall", "drizzle", "downpour", "regen" }),
            ("water",     new[] { "water", "splash", "splashes", "splashing", "river", "stream", "brook", "waves", "drip", "drips", "dripping", "puddle", "wasser" }),
            ("creak",     new[] { "creak", "creaking", "creaks", "squeak", "squeaking", "hinge", "hinges", "groan", "groaning", "knarren" }),
            ("rustle",    new[] { "rustle", "rustling", "paper", "leaves", "foliage", "cloth", "fabric", "rascheln" }),
            ("whisper",   new[] { "whisper", "whispering", "whispers", "fluestern" }),
            ("crowd",     new[] { "crowd", "murmur", "babble", "chatter", "voices", "audience", "market", "plaza" }),
            ("impact",    new[] { "impact", "hit", "hits", "thud", "thump", "punch", "slam", "knock", "footstep", "footsteps", "step", "steps", "clank", "clang", "clink", "bang", "drop", "debris", "rubble", "crash", "collision", "collapse" }),
            ("click",     new[] { "click", "clicks", "button", "tick", "switch", "toggle", "ui", "menu" }),
            ("beep",      new[] { "beep", "blip", "chime", "notification", "ping", "ding", "levelup", "scanner", "scan" }),
            ("engine",    new[] { "engine", "motor", "idle", "idling", "revving", "rev", "diesel", "truck", "car", "vehicle" }),
            ("wind",      new[] { "wind", "howl", "howling", "gust", "gusts", "breeze", "storm" }),
            ("whoosh",    new[] { "whoosh", "swish", "swoosh", "whiz", "whizz", "whizzing", "woosh", "whip", "swing" }),
            ("drone",     new[] { "drone", "hum", "humming", "basement", "ventilation", "tone" }),
        };

        /// <summary>The sound family a prompt asks for, or null when the synthesizer has no recipe for it.</summary>
        public static string Family(string prompt) => Family(prompt, out _);

        /// <summary>Like <see cref="Family(string)"/>; <paramref name="keyword"/> receives the word that decided it.</summary>
        public static string Family(string prompt, out string keyword)
        {
            keyword = null;
            var words = Words(prompt);
            foreach (var (family, keys) in Rules)
                foreach (var k in keys)
                    foreach (var w in words)
                        if (Matches(w, k)) { keyword = k; return family; }
            return null;
        }

        /// <summary>The prompt's whole words, lower-case, letters and digits only. "build-up" and "jump scare" are joined
        /// as well ("buildup", "jumpscare") so the spellings with and without a gap both match.</summary>
        public static string[] Words(string prompt)
        {
            string p = (prompt ?? "").ToLowerInvariant();
            var list = new List<string>();
            var cur = new System.Text.StringBuilder();
            foreach (char c in p + " ")
            {
                if (char.IsLetterOrDigit(c)) cur.Append(c);
                else if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); }
            }
            // joined neighbours for the two-word spellings the rules list as one word
            int n = list.Count;
            for (int i = 0; i + 1 < n; i++) list.Add(list[i] + list[i + 1]);
            return list.ToArray();
        }

        private static bool Matches(string word, string key) => word == key || (key.Length >= 5 && word.StartsWith(key, StringComparison.Ordinal));

        // ------------------------------------------------------------------ synthesis

        /// <summary>The synthesis (public for tests): mono samples in -1..1. A prompt without a known family is made as a
        /// whoosh — the caller (generate_sound, the studio) says so.</summary>
        public static float[] Synthesize(string prompt, double? seconds, bool loop, int seed)
        {
            var words = Words(prompt);
            bool Has(params string[] w) => w.Any(k => words.Any(x => Matches(x, k)));
            string fam = Family(prompt) ?? "whoosh";
            double defLen = fam switch
            {
                "click" => 0.12, "beep" => 0.35, "impact" => 0.6, "whoosh" => 0.9, "laser" => 0.5, "gunshot" => 1.6, "reload" => 1.4,
                "explosion" => 3.5, "heartbeat" => 4, "riser" => 3, "stinger" => 3.5, "siren" => 3, "glass" => 1.3, "fire" => 8,
                "rain" => 10, "water" => 6, "creak" => 2.2, "rustle" => 1.6, "whisper" => 3, "crowd" => 12, "engine" => 6,
                "wind" => 10, "drone" => 12, _ => 1,
            };
            double len = Math.Max(0.05, Math.Min(30, seconds ?? defLen * (Has("long", "lang", "sustained") ? 1.8 : Has("short", "kurz", "brief", "quick") ? 0.6 : 1)));
            int n = (int)(len * Rate);
            var o = new float[n];
            var rng = new Random(seed * 7919 + fam.GetHashCode());
            double pitch = Has("low", "deep", "tief", "dumpf", "bass") ? 0.6 : Has("high", "hoch", "bright", "shrill") ? 1.6 : 1.0;
            bool metal = Has("metal", "metallic", "steel", "iron", "metall"), wood = Has("wood", "wooden", "holz");
            double Noise() => rng.NextDouble() * 2 - 1;
            double T(int i) => (double)i / Rate;
            const double TwoPi = 2 * Math.PI;

            switch (fam)
            {
                case "click":
                    for (int i = 0; i < n; i++) { double t = T(i); o[i] = (float)(Math.Exp(-t * 900) * Noise() * 0.6 + Math.Sin(TwoPi * 2400 * pitch * t) * Math.Exp(-t * 60) * 0.4); }
                    break;
                case "beep":
                    if (Has("scanner", "scan"))
                    {
                        // a scanner: fast blips
                        for (int i = 0; i < n; i++) { double t = T(i), u = t % 0.125; double env = u < 0.05 ? Math.Min(1, u * 400) * Math.Exp(-u * 40) : 0; o[i] = (float)(Math.Sin(TwoPi * 1760 * pitch * t) * env * 0.5); }
                    }
                    else if (Has("levelup", "level", "fanfare", "success"))
                    {
                        double[] notes = { 523.25, 659.25, 783.99, 1046.5 };
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i), s = 0;
                            for (int k = 0; k < notes.Length; k++) { double u = t - k * 0.09; if (u >= 0) s += Math.Sin(TwoPi * notes[k] * pitch * u) * Math.Min(1, u * 200) * Math.Exp(-u * 3) * 0.35; }
                            o[i] = (float)s;
                        }
                    }
                    else
                        for (int i = 0; i < n; i++) { double t = T(i); double env = Math.Min(1, t * 200) * Math.Exp(-t * 6); o[i] = (float)(Math.Sin(TwoPi * 880 * pitch * t) * env * 0.5 + Math.Sin(TwoPi * 1320 * pitch * t) * env * 0.15); }
                    break;
                case "impact":
                    {
                        double f0 = (wood ? 120 : metal ? 180 : 70) * pitch;
                        var crackLp = Biquad.LowPass(wood ? 5000 : 7000, 0.7);
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            double body = Math.Sin(TwoPi * f0 * t * (1 - 0.3 * Math.Min(1, t * 4))) * Math.Exp(-t * 9);
                            double crack = crackLp.Run(Noise()) * Math.Exp(-t * (wood ? 55 : 35));
                            double ring = metal ? (Math.Sin(TwoPi * 1170 * pitch * t) + 0.6 * Math.Sin(TwoPi * 1873 * pitch * t) + 0.4 * Math.Sin(TwoPi * 2931 * pitch * t)) * Math.Exp(-t * 4) * 0.25 : 0;
                            o[i] = (float)(body * 0.8 + crack * 0.45 + ring);
                        }
                        if (Has("debris", "rubble", "crash", "collapse"))
                        {
                            // pieces coming down after the hit: staggered thuds and a band-limited rattle
                            var rattle = Biquad.BandPass(1800, 1.2);
                            int pieces = 6 + rng.Next(6);
                            for (int p = 0; p < pieces; p++)
                            {
                                double at = 0.08 + rng.NextDouble() * Math.Min(1.2, len * 0.7), f = (60 + rng.NextDouble() * 160) * pitch, amp = 0.2 + rng.NextDouble() * 0.4;
                                int s0 = (int)(at * Rate);
                                for (int k = s0; k < n && k < s0 + Rate / 3; k++) { double u = T(k - s0); o[k] += (float)(Math.Sin(TwoPi * f * u) * Math.Exp(-u * 14) * amp + rattle.Run(Noise()) * Math.Exp(-u * 25) * amp * 0.6); }
                            }
                        }
                        if (Has("footstep", "footsteps", "steps", "walking"))
                        {
                            // a sequence of steps instead of one hit
                            Array.Clear(o, 0, n);
                            double period = Has("run", "running", "fast") ? 0.3 : 0.55;
                            var stepLp = Biquad.LowPass(metal ? 4000 : 2200, 0.7);
                            for (double at = 0.02; at < len - 0.05; at += period * (0.92 + rng.NextDouble() * 0.16))
                            {
                                int s0 = (int)(at * Rate); double f = (wood ? 140 : 90) * pitch * (0.9 + rng.NextDouble() * 0.2), amp = 0.6 + rng.NextDouble() * 0.4;
                                for (int k = s0; k < n && k < s0 + Rate / 5; k++) { double u = T(k - s0); o[k] += (float)((Math.Sin(TwoPi * f * u) * Math.Exp(-u * 30) * 0.7 + stepLp.Run(Noise()) * Math.Exp(-u * 45) * 0.5) * amp); }
                            }
                        }
                        break;
                    }
                case "whoosh":
                    {
                        // band-passed noise swept up and back with a real attack and decay (not a flat broadband swell)
                        var bp = Biquad.BandPass(500, 1.0); var bp2 = Biquad.BandPass(500, 1.0);
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i), x = t / len;
                            double env = Math.Pow(Math.Sin(Math.PI * Math.Pow(x, 0.6)), 1.3);
                            if ((i & 63) == 0) { var c = Biquad.BandPass((250 + 2200 * Math.Sin(Math.PI * x)) * pitch, 1.0); bp.Retune(c); bp2.Retune(c); }
                            o[i] = (float)(bp2.Run(bp.Run(Noise())) * env * 4.0);
                        }
                        break;
                    }
                case "laser":
                    for (int i = 0; i < n; i++) { double t = T(i); double f = (1800 * pitch) * Math.Exp(-t * 6) + 120; o[i] = (float)(Math.Sign(Math.Sin(TwoPi * f * t)) * 0.25 * Math.Exp(-t * 5) + Math.Sin(TwoPi * f * 0.5 * t) * 0.25 * Math.Exp(-t * 5)); }
                    break;
                case "gunshot":
                    {
                        bool far = Has("distant", "far", "fern", "away");
                        var crackLp = Biquad.LowPass(far ? 2500 : 7000, 0.7);
                        var tailLp = Biquad.LowPass(far ? 900 : 2500, 0.7);
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            double crack = crackLp.Run(Noise()) * Math.Exp(-t * (far ? 28 : 60));
                            double thump = Math.Sin(TwoPi * 55 * pitch * t) * Math.Exp(-t * 14);
                            double tail = tailLp.Run(Noise()) * Math.Exp(-t * 2.4) * 0.3;
                            o[i] = (float)((crack * 0.9 + thump * 0.8 + tail) * (far ? 2.0 : 1.0));
                        }
                        break;
                    }
                case "reload":
                    {
                        // magazine out, in, charging handle: band-limited metallic clicks, not broadband bursts
                        double[] at = { 0.05, 0.38, 0.62, 0.95, 1.08 };
                        var clickBp = Biquad.BandPass(2600, 1.1);
                        foreach (var a in at.Where(a => a < len))
                        {
                            int s0 = (int)(a * Rate);
                            double f = 1500 + rng.NextDouble() * 1800;
                            for (int i = s0; i < Math.Min(n, s0 + Rate / 6); i++) { double t = T(i - s0); o[i] += (float)((clickBp.Run(Noise()) * Math.Exp(-t * 120) * 1.6 + Math.Sin(TwoPi * f * pitch * t) * Math.Exp(-t * 45) * 0.6) * 0.7); }
                        }
                        break;
                    }
                case "explosion":
                    {
                        var crackLp = Biquad.LowPass(5000, 0.7);
                        double lp = 0, lp2 = 0;
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            lp += 0.03 * (Noise() - lp); lp2 += 0.005 * (Noise() - lp2);
                            double env = Math.Min(1, t * 60) * Math.Exp(-t * (1.6 / Math.Max(0.5, len / 3.5)));
                            o[i] = (float)((lp * 3 + lp2 * 9 + Math.Sin(TwoPi * 38 * pitch * t) * Math.Exp(-t * 3)) * env * 0.8 + crackLp.Run(Noise()) * Math.Exp(-t * 30) * 0.4);
                        }
                        break;
                    }
                case "heartbeat":
                    {
                        double bpm = Has("fast", "schnell", "panic", "racing") ? 120 : 64, period = 60.0 / bpm;
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i) % period, s = 0;
                            foreach (var (off, amp) in new[] { (0.0, 1.0), (0.28, 0.7) })
                                if (t >= off) { double u = t - off; s += amp * Math.Sin(TwoPi * 48 * pitch * u) * Math.Exp(-u * 18) * Math.Min(1, u * 300); }
                            o[i] = (float)(s * 0.9);
                        }
                        break;
                    }
                case "riser":
                    {
                        double phase = 0;
                        var bp = Biquad.BandPass(400, 0.8);
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i), x = t / len;
                            double f = (90 + 1400 * x * x) * pitch;
                            phase += TwoPi * f / Rate;
                            if ((i & 63) == 0) bp.Retune(Biquad.BandPass(300 + 4000 * x * x, 0.8));
                            o[i] = (float)((Math.Sin(phase) * 0.35 + (2 * ((phase / TwoPi) % 1) - 1) * 0.15 + bp.Run(Noise()) * 1.2) * x * x);
                        }
                        break;
                    }
                case "stinger":
                    {
                        double[] freqs = { 220, 233.1, 311.1, 466.2, 659.3 };
                        var scrape = Biquad.BandPass(3200, 1.5);
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i), s = 0;
                            foreach (var f in freqs) s += Math.Sin(TwoPi * f * pitch * t + Math.Sin(TwoPi * 5.5 * t) * 0.4);
                            s = s / freqs.Length * Math.Min(1, t * 400) * Math.Exp(-t * 1.1);
                            s += Math.Sin(TwoPi * 41 * t) * Math.Exp(-t * 3) * 0.7 + scrape.Run(Noise()) * Math.Exp(-t * 20) * 0.6;
                            o[i] = (float)s;
                        }
                        break;
                    }
                case "siren":
                    {
                        if (Has("horn", "honk", "klaxon", "hupe"))
                        {
                            double f = 380 * pitch;
                            for (int i = 0; i < n; i++)
                            {
                                double t = T(i), env = Math.Min(1, t * 25) * Math.Min(1, Math.Max(0, (len - t)) * 12);
                                o[i] = (float)((Math.Sin(TwoPi * f * t) + Math.Sin(TwoPi * 3 * f * t) / 3 + Math.Sin(TwoPi * 5 * f * t) / 5 + 0.3 * Math.Sin(TwoPi * 1.5 * f * t)) * env * 0.5);
                            }
                        }
                        else if (Has("alarm", "buzzer"))
                        {
                            double f = 880 * pitch;
                            for (int i = 0; i < n; i++)
                            {
                                double t = T(i), u = t % 0.5, env = u < 0.25 ? Math.Min(1, u * 200) * Math.Min(1, (0.25 - u) * 200) : 0;
                                o[i] = (float)((Math.Sin(TwoPi * f * t) + Math.Sin(TwoPi * 3 * f * t) / 3) * env * 0.5);
                            }
                        }
                        else
                        {
                            double period = Has("police", "fast") ? 0.7 : 1.4, phase = 0;
                            for (int i = 0; i < n; i++)
                            {
                                double t = T(i), f = (650 + 550 * (0.5 + 0.5 * Math.Sin(TwoPi * t / period))) * pitch;
                                phase += TwoPi * f / Rate;
                                o[i] = (float)((Math.Sin(phase) * 0.5 + Math.Sin(2 * phase) * 0.15) * Math.Min(1, t * 10));
                            }
                        }
                        break;
                    }
                case "glass":
                    {
                        bool thick = Has("bottle", "ceramic", "porcelain", "plate", "vase", "jar");
                        var burst = Biquad.BandPass(thick ? 2600 : 4200, 1.8);
                        var burst2 = Biquad.BandPass(thick ? 5200 : 7800, 2.4);
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            double x = Noise();
                            o[i] = (float)((burst.Run(x) * 1.1 + burst2.Run(x) * 0.6) * Math.Exp(-t * 26) + Math.Sin(TwoPi * (thick ? 110 : 160) * t) * Math.Exp(-t * 30) * 0.25);
                        }
                        // the shards: short ringing partials scattered over the first half second
                        int shards = (thick ? 8 : 14) + rng.Next(10);
                        for (int s = 0; s < shards; s++)
                        {
                            double at = 0.01 + rng.NextDouble() * Math.Min(0.6, len * 0.5);
                            double f = ((thick ? 1500 : 2800) + rng.NextDouble() * (thick ? 3500 : 6000)) * pitch;
                            double dec = 35 + rng.NextDouble() * 90, amp = 0.2 + rng.NextDouble() * 0.3;
                            int s0 = (int)(at * Rate);
                            for (int k = s0; k < n && k < s0 + Rate / 4; k++) { double u = T(k - s0); o[k] += (float)(Math.Sin(TwoPi * f * u + 0.3 * Math.Sin(TwoPi * 37 * u)) * Math.Exp(-u * dec) * amp); }
                        }
                        break;
                    }
                case "fire":
                    {
                        var roarLp = Biquad.LowPass(900, 0.6);
                        var popBp = Biquad.BandPass(2600, 2.0);
                        double popEnv = 0;
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            double roar = roarLp.Run(Noise()) * (0.55 + 0.25 * Math.Sin(TwoPi * 0.37 * t + seed) + 0.1 * Math.Sin(TwoPi * 1.9 * t));
                            if (rng.NextDouble() < 18.0 / Rate) popEnv = 0.6 + rng.NextDouble() * 0.6;   // ~18 crackles a second
                            popEnv *= 0.9985;
                            o[i] = (float)(roar * 0.9 + popBp.Run(Noise()) * popEnv * 0.8);
                        }
                        break;
                    }
                case "rain":
                    {
                        bool heavy = Has("heavy", "downpour", "storm", "pouring"), light = Has("light", "drizzle", "soft", "gentle");
                        var bed = Biquad.BandPass(2600, 1.1);
                        var drop = Biquad.BandPass(5200, 2.0);
                        double dropEnv = 0, density = heavy ? 260 : light ? 60 : 140;
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            double b = bed.Run(Noise()) * (0.45 + 0.1 * Math.Sin(TwoPi * 0.23 * t));
                            if (rng.NextDouble() < density / Rate) dropEnv = 0.3 + rng.NextDouble() * 0.5;
                            dropEnv *= 0.994;
                            o[i] = (float)(b + drop.Run(Noise()) * dropEnv * 0.9);
                        }
                        break;
                    }
                case "water":
                    {
                        if (Has("splash", "splashes", "splashing", "plunge"))
                        {
                            var lp = Biquad.LowPass(4000, 0.7);
                            for (int i = 0; i < n; i++)
                            {
                                double t = T(i);
                                if ((i & 63) == 0) lp.Retune(Biquad.LowPass(400 + 3600 * Math.Exp(-t * 4), 0.7));
                                double env = Math.Min(1, t * 400) * Math.Exp(-t * 3.2);
                                o[i] = (float)(lp.Run(Noise()) * env * 1.4 + Math.Sin(TwoPi * 90 * t) * Math.Exp(-t * 18) * 0.3);
                            }
                            for (int d = 0; d < 6; d++)
                            {
                                double at = 0.15 + rng.NextDouble() * Math.Min(1.0, len * 0.6), f = 900 + rng.NextDouble() * 900;
                                int s0 = (int)(at * Rate);
                                for (int k = s0; k < n && k < s0 + Rate / 8; k++) { double u = T(k - s0); o[k] += (float)(Math.Sin(TwoPi * (f + 600 * u) * u) * Math.Exp(-u * 35) * 0.25); }
                            }
                        }
                        else
                        {
                            var bp = Biquad.BandPass(700, 0.4);
                            var bp2 = Biquad.BandPass(1800, 0.6);
                            double bubble = 0, bubbleF = 0, bubbleT = 0;
                            for (int i = 0; i < n; i++)
                            {
                                double t = T(i);
                                if (rng.NextDouble() < 6.0 / Rate) { bubble = 0.2 + rng.NextDouble() * 0.2; bubbleF = 300 + rng.NextDouble() * 400; bubbleT = t; }
                                double u = t - bubbleT;
                                double bub = bubble * Math.Sin(TwoPi * (bubbleF + 900 * u) * u) * Math.Exp(-u * 40);
                                o[i] = (float)(bp.Run(Noise()) * 0.6 * (0.8 + 0.2 * Math.Sin(TwoPi * 0.3 * t)) + bp2.Run(Noise()) * 0.25 + bub);
                            }
                        }
                        break;
                    }
                case "creak":
                    {
                        // stick-slip: a sawtooth-ish tone whose pitch wanders and whose amplitude stutters, plus a
                        // band-limited rasp around its fifth harmonic
                        double f = (160 + rng.NextDouble() * 120) * pitch, phase = 0, slip = 1, nextSlip = 0;
                        var rasp = Biquad.BandPass(f * 5, 3);
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            f = Math.Max(100 * pitch, Math.Min(500 * pitch, f + (rng.NextDouble() - 0.5) * 0.4));
                            if (t >= nextSlip) { slip = 0.5 + rng.NextDouble(); nextSlip = t + 0.008 + rng.NextDouble() * 0.03; }
                            phase += TwoPi * f / Rate;
                            double saw = 2 * ((phase / TwoPi) % 1) - 1;
                            double env = Math.Min(1, t * 12) * Math.Min(1, Math.Max(0, len - t) * 6) * (0.7 + 0.3 * Math.Sin(TwoPi * 0.8 * t));
                            o[i] = (float)((saw * 0.35 + Math.Sin(phase) * 0.25 + rasp.Run(Noise()) * 0.6) * slip * env);
                        }
                        break;
                    }
                case "rustle":
                    {
                        double centre = Has("paper") ? 4200 : Has("cloth", "fabric") ? 1500 : 3200;
                        var bp = Biquad.BandPass(centre, 1.2); var bp2 = Biquad.BandPass(centre * 1.15, 1.2);
                        double burst = 0, burstLen = 0.05, burstStart = -1, nextBurst = 0;
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            if (t >= nextBurst) { burst = 0.4 + rng.NextDouble() * 0.6; burstLen = 0.03 + rng.NextDouble() * 0.12; burstStart = t; nextBurst = t + burstLen + rng.NextDouble() * 0.08; }
                            double u = (t - burstStart) / burstLen;
                            double env = u >= 0 && u < 1 ? Math.Sin(Math.PI * u) : 0;
                            o[i] = (float)(bp2.Run(bp.Run(Noise())) * burst * env * 2.2);
                        }
                        break;
                    }
                case "whisper":
                    {
                        var voice = Biquad.BandPass(1400, 1.2);
                        var breath = Biquad.HighPass(3000, 0.7);
                        double syl = 0, sylLen = 0.12, sylStart = -1, nextSyl = 0;
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            if (t >= nextSyl)
                            {
                                syl = 0.4 + rng.NextDouble() * 0.6; sylLen = 0.08 + rng.NextDouble() * 0.18; sylStart = t;
                                nextSyl = t + sylLen + (rng.NextDouble() < 0.2 ? 0.3 + rng.NextDouble() * 0.5 : 0.02 + rng.NextDouble() * 0.06);
                                voice.Retune(Biquad.BandPass(900 + 1400 * rng.NextDouble(), 1.5));   // a new formant per syllable
                            }
                            double u = (t - sylStart) / sylLen;
                            double env = u >= 0 && u < 1 ? Math.Sin(Math.PI * u) : 0;
                            o[i] = (float)((voice.Run(Noise()) * 0.9 + breath.Run(Noise()) * 0.25) * syl * env);
                        }
                        break;
                    }
                case "crowd":
                    {
                        int nv = Has("small", "few", "couple") ? 6 : 22;
                        var vf = new Biquad[nv]; var vRate = new double[nv]; var vPhase = new double[nv]; var vGate = new double[nv]; var vGateT = new double[nv];
                        for (int v = 0; v < nv; v++)
                        {
                            vf[v] = Biquad.BandPass(300 + rng.NextDouble() * 1500, 2.5);
                            vRate[v] = 3 + rng.NextDouble() * 3; vPhase[v] = rng.NextDouble() * TwoPi;
                            vGate[v] = rng.NextDouble() < 0.6 ? 1 : 0; vGateT[v] = rng.NextDouble() * 2;
                        }
                        var room = Biquad.LowPass(350, 0.7);
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i), s = 0;
                            for (int v = 0; v < nv; v++)
                            {
                                if (t >= vGateT[v]) { vGate[v] = rng.NextDouble() < 0.55 ? 1 : 0; vGateT[v] = t + 0.5 + rng.NextDouble() * 2.5; }
                                if (vGate[v] == 0) continue;
                                double sylEnv = Math.Max(0, Math.Sin(TwoPi * vRate[v] * t + vPhase[v]));
                                s += vf[v].Run(Noise()) * sylEnv;
                            }
                            o[i] = (float)(s * (1.6 / nv) + room.Run(Noise()) * 0.12);
                        }
                        break;
                    }
                case "engine":
                    {
                        bool rev = Has("rev", "revving", "accelerate", "accelerating", "throttle");
                        double f0 = (Has("truck", "diesel", "heavy", "lorry") ? 22 : 32) * pitch, phase = 0;
                        var lp = Biquad.LowPass(500, 0.7);
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i), x = t / len;
                            double f = f0 * (rev ? 1 + 2.5 * Math.Sin(Math.PI * x) : 1 + 0.03 * Math.Sin(TwoPi * 0.7 * t));
                            phase += TwoPi * f / Rate;
                            double pulse = Math.Pow(Math.Max(0, Math.Sin(phase)), 6);
                            double body = Math.Sin(phase) * 0.35 + Math.Sin(2 * phase) * 0.25 + Math.Sin(4 * phase) * 0.12;
                            o[i] = (float)(body * 0.8 + lp.Run(Noise()) * (0.25 + 0.5 * pulse) + (Math.Sin(phase * 0.5) > 0 ? 0.06 : -0.06));
                        }
                        break;
                    }
                case "wind":
                    {
                        double lp = 0, bp = 0, gust = 0.5;
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            if (i % 2205 == 0) gust += (rng.NextDouble() - 0.5) * 0.2;
                            gust = Math.Max(0.15, Math.Min(1, gust));
                            double cut = 0.01 + 0.05 * gust * pitch;
                            lp += cut * (Noise() - lp); bp += cut * (lp - bp);
                            o[i] = (float)((lp - bp) * 9 * gust + Math.Sin(TwoPi * (600 + 300 * gust) * pitch * t) * 0.02 * gust);
                        }
                        break;
                    }
                default: // drone / ambience
                    {
                        double lp = 0, lp2 = 0;
                        bool drips = Has("drip", "drips", "dripping", "water");
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            lp += 0.004 * (Noise() - lp); lp2 += 0.04 * (Noise() - lp2);
                            double lfo = 0.6 + 0.4 * Math.Sin(TwoPi * 0.11 * t + seed);
                            double hum = Math.Sin(TwoPi * 50 * pitch * t) * 0.08 + Math.Sin(TwoPi * 100 * pitch * t) * 0.03;
                            o[i] = (float)((lp * 10 + lp2 * 0.25) * lfo * 0.5 + hum);
                            if (drips && rng.NextDouble() < 1.0 / Rate * 0.6) for (int k = 0; k < 2000 && i + k < n; k++) o[i + k] += (float)(Math.Sin(TwoPi * 1400 * k / (double)Rate) * Math.Exp(-k / 300.0) * 0.25);
                        }
                        break;
                    }
            }

            if (Has("reverb", "echo", "hall", "room", "cave", "basement", "keller", "corridor", "tunnel")) Reverb(o, Has("cave", "hall", "large", "big", "tunnel") ? 0.82 : 0.68);
            if (loop) o = CrossfadeLoop(o, Math.Min(n / 4, Rate / 2));
            // a soft prompt stays soft: peak normalisation alone made every sound full-scale
            Normalize(o, Has("soft", "quiet", "gentle", "subtle", "faint", "leise") ? 0.5f : 0.89f);
            return o;
        }

        /// <summary>Second-order IIR (RBJ cookbook), transposed direct form II. A struct: keep it in a local or an array
        /// element and call <see cref="Run"/> on that — a copy would carry its own state.</summary>
        private struct Biquad
        {
            private double _b0, _b1, _b2, _a1, _a2, _z1, _z2;

            private static Biquad Make(double b0, double b1, double b2, double a0, double a1, double a2)
                => new Biquad { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0, _a1 = a1 / a0, _a2 = a2 / a0 };
            private static double Clamp(double hz) => Math.Max(20, Math.Min(Rate * 0.45, hz));

            public static Biquad LowPass(double hz, double q = 0.707)
            {
                double w = 2 * Math.PI * Clamp(hz) / Rate, c = Math.Cos(w), a = Math.Sin(w) / (2 * q);
                return Make((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + a, -2 * c, 1 - a);
            }
            public static Biquad HighPass(double hz, double q = 0.707)
            {
                double w = 2 * Math.PI * Clamp(hz) / Rate, c = Math.Cos(w), a = Math.Sin(w) / (2 * q);
                return Make((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + a, -2 * c, 1 - a);
            }
            public static Biquad BandPass(double hz, double q)
            {
                double w = 2 * Math.PI * Clamp(hz) / Rate, c = Math.Cos(w), a = Math.Sin(w) / (2 * q);
                return Make(a, 0, -a, 1 + a, -2 * c, 1 - a);
            }
            /// <summary>New coefficients, same state (for sweeps).</summary>
            public void Retune(Biquad coeffs) { _b0 = coeffs._b0; _b1 = coeffs._b1; _b2 = coeffs._b2; _a1 = coeffs._a1; _a2 = coeffs._a2; }
            public double Run(double x)
            {
                double y = _b0 * x + _z1;
                _z1 = _b1 * x - _a1 * y + _z2;
                _z2 = _b2 * x - _a2 * y;
                return y;
            }
        }

        private static void Reverb(float[] x, double feedback)
        {
            int[] combs = { 1557, 1617, 1491, 1422 };
            var y = new float[x.Length];
            foreach (var d in combs)
            {
                var buf = new double[d]; int k = 0;
                for (int i = 0; i < x.Length; i++) { double v = buf[k]; buf[k] = x[i] + v * feedback; k = (k + 1) % d; y[i] += (float)(v * 0.25); }
            }
            for (int i = 0; i < x.Length; i++) x[i] = x[i] * 0.75f + y[i] * 0.5f;
        }

        /// <summary>Seamless loop: the tail is cross-faded into the head and cut off.</summary>
        private static float[] CrossfadeLoop(float[] x, int fade)
        {
            if (fade <= 0 || x.Length < fade * 2) return x;
            int n = x.Length;
            for (int i = 0; i < fade; i++)
            {
                float a = (float)i / fade;
                x[i] = x[i] * a + x[n - fade + i] * (1 - a);
            }
            Array.Resize(ref x, n - fade);
            return x;
        }

        private static void Normalize(float[] x, float peak)
        {
            float m = 0;
            foreach (var v in x) m = Math.Max(m, Math.Abs(v));
            if (m < 1e-6f) return;
            float g = peak / m;
            for (int i = 0; i < x.Length; i++) x[i] *= g;
        }
    }
}
