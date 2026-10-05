using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.Core.Audio.SoundStudio
{
    /// <summary>
    /// The offline backend: a small procedural synthesizer that reads the prompt for a sound family (click, whoosh,
    /// impact, explosion, gunshot, reload, heartbeat, riser, stinger, laser, drone/ambience, wind) and modifiers (short,
    /// long, low, high, metal, wood, distant, reverb), then synthesizes it from noise, oscillators and envelopes —
    /// 44.1 kHz mono WAV. No account, no cost, instant: placeholder SFX, UI sounds, and a test bed for the studio.
    /// Seeds give different takes; the result is your own work (no license attached).
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
        public string Notice => "Synthesized on your machine — the sounds are your own work. Good for placeholders, UI and simple impacts; use a generative backend for realistic sounds.";
        public string CostHint(SoundRequest r) => "free";

        public Task<GeneratedSound> GenerateAsync(SoundRequest r, string outDir, CancellationToken ct)
            => Task.Run(() =>
            {
                var samples = Synthesize(r.Prompt ?? "", r.DurationSeconds, r.Loop, r.Seed);
                string file = Path.Combine(outDir, "procedural_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wav");
                Wav.Write(file, samples, Rate);
                return new GeneratedSound { FilePath = file, BackendId = Id, Request = r.Clone(), Label = r.Label ?? Family(r.Prompt) + " (procedural)", CostNote = "free", Format = "wav" };
            }, ct);

        public static string Family(string prompt)
        {
            string p = (prompt ?? "").ToLowerInvariant();
            bool Has(params string[] w) => w.Any(p.Contains);
            if (Has("heartbeat", "heart beat", "herzschlag")) return "heartbeat";
            if (Has("explosion", "explode", "blast", "boom", "detonat")) return "explosion";
            if (Has("gunshot", "gun shot", "rifle", "pistol", "shot", "schuss")) return "gunshot";
            if (Has("reload", "magazine", "charging handle", "bolt")) return "reload";
            if (Has("laser", "zap", "blaster")) return "laser";
            if (Has("stinger", "jump scare", "jumpscare", "scare")) return "stinger";
            if (Has("riser", "rise", "sweep up", "build up", "build-up")) return "riser";
            if (Has("wind", "howl", "wind")) return "wind";
            if (Has("ambience", "ambient", "drone", "hum", "room tone", "basement", "atmo")) return "drone";
            if (Has("whoosh", "swish", "swoosh", "whiz", "woosh")) return "whoosh";
            if (Has("impact", "hit", "thud", "punch", "slam", "knock", "footstep", "step")) return "impact";
            if (Has("click", "ui", "button", "tick")) return "click";
            if (Has("beep", "blip", "chime", "notification")) return "beep";
            return "whoosh";
        }

        /// <summary>The synthesis (public for tests): mono samples in -1..1.</summary>
        public static float[] Synthesize(string prompt, double? seconds, bool loop, int seed)
        {
            string p = (prompt ?? "").ToLowerInvariant();
            bool Has(params string[] w) => w.Any(p.Contains);
            string fam = Family(p);
            double defLen = fam switch { "click" => 0.12, "beep" => 0.35, "impact" => 0.6, "whoosh" => 0.9, "laser" => 0.5, "gunshot" => 1.6, "reload" => 1.4, "explosion" => 3.5, "heartbeat" => 4, "riser" => 3, "stinger" => 3.5, "wind" => 10, "drone" => 12, _ => 1 };
            double len = Math.Max(0.05, Math.Min(30, seconds ?? defLen * (Has("long", "lang") ? 1.8 : Has("short", "kurz") ? 0.6 : 1)));
            int n = (int)(len * Rate);
            var o = new float[n];
            var rng = new Random(seed * 7919 + fam.GetHashCode());
            double pitch = Has("low", "deep", "tief", "dumpf") ? 0.6 : Has("high", "hoch", "bright") ? 1.6 : 1.0;
            bool metal = Has("metal", "steel", "iron", "metall"), wood = Has("wood", "holz");
            double Noise() => rng.NextDouble() * 2 - 1;
            double T(int i) => (double)i / Rate;

            switch (fam)
            {
                case "click":
                    for (int i = 0; i < n; i++) { double t = T(i); o[i] = (float)(Math.Exp(-t * 900) * Noise() * 0.6 + Math.Sin(2 * Math.PI * 2400 * pitch * t) * Math.Exp(-t * 60) * 0.4); }
                    break;
                case "beep":
                    for (int i = 0; i < n; i++) { double t = T(i); double env = Math.Min(1, t * 200) * Math.Exp(-t * 6); o[i] = (float)(Math.Sin(2 * Math.PI * 880 * pitch * t) * env * 0.5 + Math.Sin(2 * Math.PI * 1320 * pitch * t) * env * 0.15); }
                    break;
                case "impact":
                    {
                        double f0 = (wood ? 120 : metal ? 180 : 70) * pitch;
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            double body = Math.Sin(2 * Math.PI * f0 * t * (1 - 0.3 * Math.Min(1, t * 4))) * Math.Exp(-t * 9);
                            double crack = Noise() * Math.Exp(-t * (wood ? 55 : 35));
                            double ring = metal ? (Math.Sin(2 * Math.PI * 1170 * pitch * t) + 0.6 * Math.Sin(2 * Math.PI * 1873 * pitch * t) + 0.4 * Math.Sin(2 * Math.PI * 2931 * pitch * t)) * Math.Exp(-t * 4) * 0.25 : 0;
                            o[i] = (float)(body * 0.8 + crack * 0.45 + ring);
                        }
                        break;
                    }
                case "whoosh":
                    {
                        double lp = 0, bp = 0;
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i), x = t / len;
                            double env = Math.Sin(Math.PI * Math.Pow(x, 0.7));
                            double cut = 0.02 + 0.25 * env * pitch;
                            lp += cut * (Noise() - lp); bp += cut * (lp - bp);
                            o[i] = (float)((lp - bp) * env * 3.2);
                        }
                        break;
                    }
                case "laser":
                    for (int i = 0; i < n; i++) { double t = T(i); double f = (1800 * pitch) * Math.Exp(-t * 6) + 120; o[i] = (float)(Math.Sign(Math.Sin(2 * Math.PI * f * t)) * 0.25 * Math.Exp(-t * 5) + Math.Sin(2 * Math.PI * f * 0.5 * t) * 0.25 * Math.Exp(-t * 5)); }
                    break;
                case "gunshot":
                    {
                        bool far = Has("distant", "far", "fern", "away");
                        double lp = 0;
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            double crack = Noise() * Math.Exp(-t * (far ? 28 : 60));
                            double thump = Math.Sin(2 * Math.PI * 55 * pitch * t) * Math.Exp(-t * 14);
                            double tail = Noise() * Math.Exp(-t * 2.4) * 0.18;
                            double s = crack * 0.9 + thump * 0.8 + tail;
                            if (far) { lp += 0.08 * (s - lp); s = lp * 2.2; }
                            o[i] = (float)s;
                        }
                        break;
                    }
                case "reload":
                    {
                        double[] at = { 0.05, 0.38, 0.62, 0.95, 1.08 };
                        foreach (var a in at.Where(a => a < len))
                        {
                            int s0 = (int)(a * Rate);
                            double f = 1500 + rng.NextDouble() * 1800;
                            for (int i = s0; i < Math.Min(n, s0 + Rate / 6); i++) { double t = T(i - s0); o[i] += (float)((Noise() * Math.Exp(-t * 140) + Math.Sin(2 * Math.PI * f * pitch * t) * Math.Exp(-t * 45) * 0.6) * 0.7); }
                        }
                        break;
                    }
                case "explosion":
                    {
                        double lp = 0, lp2 = 0;
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            lp += 0.03 * (Noise() - lp); lp2 += 0.005 * (Noise() - lp2);
                            double env = Math.Min(1, t * 60) * Math.Exp(-t * (1.6 / Math.Max(0.5, len / 3.5)));
                            o[i] = (float)((lp * 3 + lp2 * 9 + Math.Sin(2 * Math.PI * 38 * pitch * t) * Math.Exp(-t * 3)) * env * 0.8 + Noise() * Math.Exp(-t * 30) * 0.4);
                        }
                        break;
                    }
                case "heartbeat":
                    {
                        double bpm = Has("fast", "schnell", "panic") ? 120 : 64, period = 60.0 / bpm;
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i) % period, s = 0;
                            foreach (var (off, amp) in new[] { (0.0, 1.0), (0.28, 0.7) })
                                if (t >= off) { double u = t - off; s += amp * Math.Sin(2 * Math.PI * 48 * pitch * u) * Math.Exp(-u * 18) * Math.Min(1, u * 300); }
                            o[i] = (float)(s * 0.9);
                        }
                        break;
                    }
                case "riser":
                    {
                        double phase = 0, lp = 0;
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i), x = t / len;
                            double f = (90 + 1400 * x * x) * pitch;
                            phase += 2 * Math.PI * f / Rate;
                            lp += (0.02 + 0.3 * x) * (Noise() - lp);
                            o[i] = (float)((Math.Sin(phase) * 0.35 + (2 * ((phase / (2 * Math.PI)) % 1) - 1) * 0.15 + lp * 0.8) * x * x);
                        }
                        break;
                    }
                case "stinger":
                    {
                        double[] freqs = { 220, 233.1, 311.1, 466.2, 659.3 };
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i), s = 0;
                            foreach (var f in freqs) s += Math.Sin(2 * Math.PI * f * pitch * t + Math.Sin(2 * Math.PI * 5.5 * t) * 0.4);
                            s = s / freqs.Length * Math.Min(1, t * 400) * Math.Exp(-t * 1.1);
                            s += Math.Sin(2 * Math.PI * 41 * t) * Math.Exp(-t * 3) * 0.7 + Noise() * Math.Exp(-t * 20) * 0.3;
                            o[i] = (float)s;
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
                            o[i] = (float)((lp - bp) * 9 * gust + Math.Sin(2 * Math.PI * (600 + 300 * gust) * pitch * t) * 0.02 * gust);
                        }
                        break;
                    }
                default: // drone / ambience
                    {
                        double lp = 0, lp2 = 0;
                        for (int i = 0; i < n; i++)
                        {
                            double t = T(i);
                            lp += 0.004 * (Noise() - lp); lp2 += 0.04 * (Noise() - lp2);
                            double lfo = 0.6 + 0.4 * Math.Sin(2 * Math.PI * 0.11 * t + seed);
                            double hum = Math.Sin(2 * Math.PI * 50 * pitch * t) * 0.08 + Math.Sin(2 * Math.PI * 100 * pitch * t) * 0.03;
                            o[i] = (float)((lp * 10 + lp2 * 0.25) * lfo * 0.5 + hum);
                            if (Has("drip", "water") && rng.NextDouble() < 1.0 / Rate * 0.6) for (int k = 0; k < 2000 && i + k < n; k++) o[i + k] += (float)(Math.Sin(2 * Math.PI * 1400 * k / (double)Rate) * Math.Exp(-k / 300.0) * 0.25);
                        }
                        break;
                    }
            }

            if (Has("reverb", "echo", "hall", "room", "cave", "basement", "keller")) Reverb(o, Has("cave", "hall", "large", "big") ? 0.82 : 0.68);
            if (loop) o = CrossfadeLoop(o, Math.Min(n / 4, Rate / 2));
            Normalize(o, 0.89f);
            return o;
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
