using System;
using System.Collections.Generic;
using SkyOverride = Editor.Core.Services.SceneRenderService.SkyOverride;

namespace Editor.Core.Services
{
    /// <summary>
    /// The sky's cross-fade (#464): the sky as a weighted list of skies — pictures and gradients — while a script fades from
    /// one to the next (Sky.SetTexture / SetGradient / Clear with a fade time) or blends two pictures itself (Sky.SetBlend).
    /// The fade starts from whatever the sky shows at that moment, a blend included, so a fade that starts during another
    /// one does not pop. The renderer's sky pass draws a base sky (a picture or the gradient) and up to two pictures over it;
    /// <see cref="Compose"/> merges the list down to that (identical pictures add up; gradients are linear in their colours,
    /// so all of them become one gradient of the weight-averaged colours) and <see cref="Plan"/> turns the weights into the
    /// pass's blend amounts. The sky light (SH9), the reflection map and the haze follow the same weights.
    /// </summary>
    public static class SkyFade
    {
        /// <summary>One sky of a blend and its weight (the weights of a list sum to 1).</summary>
        public sealed class Layer
        {
            public SkyOverride Sky;
            public float Weight;
            public Layer(SkyOverride sky, float weight) { Sky = sky; Weight = weight; }
        }

        /// <summary>What the sky pass draws: the base sky (a picture or the gradient) and up to two pictures over it, each with
        /// its amount — the nested blend lerp(lerp(base, over[0], amount[0]), over[1], amount[1]) gives every layer its weight.</summary>
        public sealed class PassPlan
        {
            public Layer Base;
            public readonly List<Layer> Over = new List<Layer>();
            public readonly List<float> Amount = new List<float>();
        }

        /// <summary>The pass draws a base sky and two pictures over it: at most three skies at once.</summary>
        public const int MaxLayers = 3;

        private static List<Layer> _from;   // the sky the fade started from (weights sum to 1); null = no fade
        private static float _progress = 1f, _duration;
        private static bool _manual;         // Sky.SetBlend: the script sets the progress

        /// <summary>True while a fade or a script-driven blend is under way.</summary>
        public static bool Active => _from != null;
        /// <summary>0 .. 1 through the running fade (the blend factor of Sky.SetBlend); 1 when none runs.</summary>
        public static float Progress => _from != null ? _progress : 1f;
        /// <summary>True while the script drives the blend (Sky.SetBlend) instead of the clock.</summary>
        public static bool Manual => _from != null && _manual;

        /// <summary>A fade from the sky shown now (<paramref name="current"/>, weights summing to 1) to the script's next sky over
        /// <paramref name="seconds"/>. No current sky or no time: no fade (the next sky shows at once).</summary>
        public static void Start(List<Layer> current, float seconds)
        {
            if (current == null || current.Count == 0 || !(seconds > 0f)) { Cancel(); return; }
            _from = current; _duration = seconds; _progress = 0f; _manual = false;
        }

        /// <summary>A blend the script drives (Sky.SetBlend): <paramref name="from"/> fully at t = 0, the script's sky at t = 1.</summary>
        public static void SetManual(SkyOverride from, float t)
        {
            _from = new List<Layer> { new Layer(from, 1f) };
            _progress = Clamp01(t); _manual = true; _duration = 0f;
        }

        /// <summary>No fade any more: the script's sky (or the component) alone.</summary>
        public static void Cancel() { _from = null; _progress = 1f; _manual = false; _duration = 0f; }

        /// <summary>Advances a running fade by <paramref name="dt"/> seconds of game time; true when it ended in this step (the
        /// caller re-submits the plain sky once).</summary>
        public static bool Tick(float dt)
        {
            if (_from == null || _manual || !(dt > 0f)) return false;
            _progress = _duration > 0f ? _progress + dt / _duration : 1f;
            if (_progress < 1f) return false;
            Cancel();
            return true;
        }

        /// <summary>The sky shown now: the start of the fade weighted 1 − progress, <paramref name="target"/> (the script's sky or
        /// the scene's own) weighted progress, merged to at most <see cref="MaxLayers"/>. Without a fade: the target alone.</summary>
        public static List<Layer> Current(SkyOverride target)
        {
            var list = new List<Layer>();
            if (_from != null)
            {
                float p = Clamp01(_progress);
                foreach (var l in _from) list.Add(new Layer(l.Sky, l.Weight * (1f - p)));
                if (target != null) list.Add(new Layer(target, p));
            }
            else if (target != null) list.Add(new Layer(target, 1f));
            return Compose(list);
        }

        /// <summary>Merges a weighted list of skies: identical pictures add up, every gradient goes into one (the weight-averaged
        /// colours), weights under 1e-4 drop out, and beyond <see cref="MaxLayers"/> the lightest layers go; the weights are
        /// renormalised to 1.</summary>
        public static List<Layer> Compose(List<Layer> layers)
        {
            var pictures = new List<Layer>();
            float gw = 0f; var gc = new float[9];
            foreach (var l in layers)
            {
                if (l == null || l.Sky == null || !(l.Weight > 1e-6f)) continue;
                if (!string.IsNullOrEmpty(l.Sky.TexturePath))
                {
                    var same = pictures.Find(p => SamePicture(p.Sky, l.Sky));
                    if (same != null) same.Weight += l.Weight;
                    else pictures.Add(new Layer(l.Sky, l.Weight));
                }
                else if (l.Sky.Gradient != null && l.Sky.Gradient.Length >= 9)
                {
                    // the gradient's colours as the pass draws them (times its exposure), weighted
                    for (int i = 0; i < 9; i++) gc[i] += l.Sky.Gradient[i] * l.Sky.Exposure * l.Weight;
                    gw += l.Weight;
                }
            }
            var all = new List<Layer>();
            if (gw > 1e-6f)
            {
                var g = new float[9];
                for (int i = 0; i < 9; i++) g[i] = gc[i] / gw;
                all.Add(new Layer(new SkyOverride { Gradient = g, Exposure = 1f }, gw));
            }
            all.AddRange(pictures);
            all.RemoveAll(l => l.Weight < 1e-4f);
            while (all.Count > MaxLayers)
            {
                int lightest = 0;
                for (int i = 1; i < all.Count; i++) if (all[i].Weight < all[lightest].Weight) lightest = i;
                all.RemoveAt(lightest);
            }
            float sum = 0f;
            foreach (var l in all) sum += l.Weight;
            if (sum > 1e-6f) foreach (var l in all) l.Weight /= sum;
            return all;
        }

        /// <summary>The sky pass's draw for a merged list: the gradient (or the first picture) as the base, the other pictures
        /// over it with the amounts that give every layer its weight.</summary>
        public static PassPlan Plan(List<Layer> layers)
        {
            var plan = new PassPlan();
            if (layers == null || layers.Count == 0) return plan;
            int b = layers.FindIndex(l => string.IsNullOrEmpty(l.Sky.TexturePath));
            if (b < 0) b = 0;
            plan.Base = layers[b];
            float acc = plan.Base.Weight;
            for (int i = 0; i < layers.Count && plan.Over.Count < MaxLayers - 1; i++)
            {
                if (i == b) continue;
                var l = layers[i];
                acc += l.Weight;
                plan.Over.Add(l);
                plan.Amount.Add(acc > 1e-6f ? Clamp01(l.Weight / acc) : 0f);
            }
            return plan;
        }

        /// <summary>Two picture skies that draw the same: the same file, exposure and yaw.</summary>
        public static bool SamePicture(SkyOverride a, SkyOverride b)
        {
            if (a == null || b == null || string.IsNullOrEmpty(a.TexturePath) || string.IsNullOrEmpty(b.TexturePath)) return false;
            return string.Equals(a.TexturePath.Replace('\\', '/'), b.TexturePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                && Math.Abs(a.Exposure - b.Exposure) < 1e-4f && Math.Abs(a.RotationDeg - b.RotationDeg) < 1e-3f;
        }

        private static float Clamp01(float v) => float.IsNaN(v) ? 0f : (v < 0f ? 0f : (v > 1f ? 1f : v));
    }
}
