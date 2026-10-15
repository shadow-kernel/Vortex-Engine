using System;
using System.Collections.Generic;
using Editor.Core.Services;
using SkyOverride = Editor.Core.Services.SceneRenderService.SkyOverride;

namespace VortexTests
{
    /// <summary>The sky's cross-fade (#464), headless: the weighted list of skies a fade shows, merged to what the sky pass
    /// draws (identical pictures add up, gradients average into one, at most three layers), the pass's blend amounts, the
    /// fade's progress on game time, a fade that starts during another one (no pop), Sky.SetBlend, and the reflection maps'
    /// weighted blend with the skies' yaws. The picture itself is checked by the "sky crossfade" smoke.</summary>
    public static class SkyFadeTests
    {
        private static SkyOverride Pic(string p, float rot = 0f) => new SkyOverride { TexturePath = p, Exposure = 1f, RotationDeg = rot };
        private static SkyOverride Grad(float r, float g, float b) => new SkyOverride { Exposure = 1f, Gradient = new[] { r, g, b, r, g, b, r, g, b } };
        private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-4f;

        private static float WeightOf(List<SkyFade.Layer> layers, string path)
        {
            foreach (var l in layers) if (l.Sky.TexturePath == path) return l.Weight;
            return 0f;
        }

        [Test]
        public static void ComposeMergesAndCaps(TestContext t)
        {
            var list = SkyFade.Compose(new List<SkyFade.Layer>
            {
                new SkyFade.Layer(Pic("a.hdr"), 0.2f), new SkyFade.Layer(Pic("A.HDR"), 0.2f),   // the same file: one layer
                new SkyFade.Layer(Grad(1, 0, 0), 0.1f), new SkyFade.Layer(Grad(0, 0, 1), 0.3f),  // two gradients: one, averaged
                new SkyFade.Layer(Pic("b.hdr"), 0.2f), new SkyFade.Layer(Pic("b.hdr", 90f), 0f), // weight 0 drops out
            });
            t.Equal(3, list.Count, "a gradient and two pictures");
            t.True(list[0].Sky.Gradient != null && Near(list[0].Weight, 0.4f), "the gradients became one of weight 0.4");
            t.True(Near(list[0].Sky.Gradient[0], 0.25f) && Near(list[0].Sky.Gradient[2], 0.75f), "its colour is the weighted average (red .25, blue .75)");
            t.True(Near(WeightOf(list, "a.hdr"), 0.4f), "the two a.hdr layers add up");
            t.True(Near(WeightOf(list, "b.hdr"), 0.2f), "b.hdr");

            var four = SkyFade.Compose(new List<SkyFade.Layer>
                { new SkyFade.Layer(Pic("a"), 0.4f), new SkyFade.Layer(Pic("b"), 0.3f), new SkyFade.Layer(Pic("c"), 0.2f), new SkyFade.Layer(Pic("d"), 0.1f) });
            t.Equal(3, four.Count, "the sky pass draws three skies at most");
            t.True(WeightOf(four, "d") == 0f && Near(WeightOf(four, "a") + WeightOf(four, "b") + WeightOf(four, "c"), 1f), "the lightest went, the rest renormalised");
        }

        [Test]
        public static void ThePlanGivesEveryLayerItsWeight(TestContext t)
        {
            var layers = SkyFade.Compose(new List<SkyFade.Layer>
                { new SkyFade.Layer(Pic("a"), 0.5f), new SkyFade.Layer(Grad(0, 1, 0), 0.2f), new SkyFade.Layer(Pic("b"), 0.3f) });
            var plan = SkyFade.Plan(layers);
            t.True(plan.Base.Sky.Gradient != null, "the gradient is the base (the pass blends pictures over it)");
            t.Equal(2, plan.Over.Count, "two pictures over it");
            // the nested lerp: base * (1-a0)(1-a1), over0 * a0 (1-a1), over1 * a1
            float a0 = plan.Amount[0], a1 = plan.Amount[1];
            float wb = (1 - a0) * (1 - a1), w0 = a0 * (1 - a1), w1 = a1;
            t.True(Near(wb, plan.Base.Weight) && Near(w0, plan.Over[0].Weight) && Near(w1, plan.Over[1].Weight), "the amounts reproduce the weights");
            var single = SkyFade.Plan(SkyFade.Compose(new List<SkyFade.Layer> { new SkyFade.Layer(Pic("a"), 1f) }));
            t.True(single.Base != null && single.Over.Count == 0, "one sky: no blend at all");
        }

        [Test]
        public static void AFadeRunsOnGameTime(TestContext t)
        {
            try
            {
                SkyFade.Start(new List<SkyFade.Layer> { new SkyFade.Layer(Pic("a"), 1f) }, 2f);
                t.True(SkyFade.Active && SkyFade.Progress == 0f, "started at 0");
                t.False(SkyFade.Tick(1f), "half way");
                t.True(Near(SkyFade.Progress, 0.5f), "progress 0.5 after 1 of 2 s");
                var mid = SkyFade.Current(Pic("b"));
                t.True(Near(WeightOf(mid, "a"), 0.5f) && Near(WeightOf(mid, "b"), 0.5f), "half the old sky, half the new");
                t.True(SkyFade.Tick(1.5f), "the fade ends in this step");
                t.True(!SkyFade.Active && SkyFade.Progress == 1f, "done: progress 1");
                var after = SkyFade.Current(Pic("b"));
                t.True(after.Count == 1 && Near(WeightOf(after, "b"), 1f), "the new sky alone");
                SkyFade.Start(new List<SkyFade.Layer>(), 2f);
                t.False(SkyFade.Active, "no sky to fade from: no fade");
                SkyFade.Start(new List<SkyFade.Layer> { new SkyFade.Layer(Pic("a"), 1f) }, 0f);
                t.False(SkyFade.Active, "no time: no fade");
            }
            finally { SkyFade.Cancel(); }
        }

        [Test]
        public static void AFadeDuringAFadeStartsFromTheBlend(TestContext t)
        {
            try
            {
                SkyFade.Start(new List<SkyFade.Layer> { new SkyFade.Layer(Pic("a"), 1f) }, 4f);
                SkyFade.Tick(2f);                                    // a -> b half way
                var shown = SkyFade.Current(Pic("b"));
                SkyFade.Start(shown, 2f);                            // now to c
                var first = SkyFade.Current(Pic("c"));
                t.True(Near(WeightOf(first, "a"), 0.5f) && Near(WeightOf(first, "b"), 0.5f) && WeightOf(first, "c") == 0f, "the new fade starts exactly where the old one stood");
                SkyFade.Tick(1f);
                var half = SkyFade.Current(Pic("c"));
                t.True(Near(WeightOf(half, "a"), 0.25f) && Near(WeightOf(half, "b"), 0.25f) && Near(WeightOf(half, "c"), 0.5f), "and blends the old blend out");
                // a gradient to a picture: the gradient is the base, the picture goes over it by the progress
                SkyFade.Start(new List<SkyFade.Layer> { new SkyFade.Layer(Grad(0.2f, 0.4f, 0.8f), 1f) }, 4f);
                SkyFade.Tick(1f);
                var plan = SkyFade.Plan(SkyFade.Current(Pic("night.hdr")));
                t.True(plan.Base.Sky.Gradient != null && plan.Over.Count == 1 && Near(plan.Amount[0], 0.25f), "gradient -> picture: the picture over the gradient at 0.25");
            }
            finally { SkyFade.Cancel(); }
        }

        [Test]
        public static void SetBlendIsDrivenByTheScript(TestContext t)
        {
            var sky = SceneRenderService.ScriptSky;
            try
            {
                Vortex.Sky.SetBlend("Assets/Skies/clear.hdr", "Assets/Skies/storm.hdr", 0.3f);
                t.True(SkyFade.Manual && Near(Vortex.Sky.FadeProgress, 0.3f), "FadeProgress is the blend factor");
                var shown = SceneRenderService.CurrentSkyLayers();
                t.True(Near(WeightOf(shown, "Assets/Skies/clear.hdr"), 0.7f) && Near(WeightOf(shown, "Assets/Skies/storm.hdr"), 0.3f), "70 % clear, 30 % storm");
                SceneRenderService.TickSkyFade(5f);
                t.True(Near(Vortex.Sky.FadeProgress, 0.3f), "the clock does not move a script-driven blend");
                Vortex.Sky.SetTexture("Assets/Skies/night.hdr", 1f, 0f, 10f);
                t.True(SkyFade.Active && !SkyFade.Manual && Vortex.Sky.FadeProgress == 0f, "a fade from the blend as it stands");
                var start = SceneRenderService.CurrentSkyLayers();
                t.True(Near(WeightOf(start, "Assets/Skies/clear.hdr"), 0.7f) && WeightOf(start, "Assets/Skies/night.hdr") == 0f, "no pop when the fade starts");
                Vortex.Sky.SetTexture("Assets/Skies/day.hdr");
                t.True(!SkyFade.Active && Vortex.Sky.FadeProgress == 1f, "without a fade time the sky switches at once (the old 3-argument call)");
            }
            finally { SkyFade.Cancel(); SceneRenderService.ScriptSky = sky; SceneRenderService.RuntimeDirty = false; }
        }

        [Test]
        public static void ReflectionMapsBlendWithTheirYaws(TestContext t)
        {
            // two 8 x 4 maps: one red, one with a single bright column at u = 0
            var red = new SkyLight.ReflectionMap { Width = 8, Height = 4, Rgba = new byte[8 * 4 * 4] };
            var col = new SkyLight.ReflectionMap { Width = 8, Height = 4, Rgba = new byte[8 * 4 * 4] };
            for (int i = 0; i < 32; i++) { red.Rgba[i * 4] = 200; red.Rgba[i * 4 + 3] = 255; }
            for (int y = 0; y < 4; y++) { int o = (y * 8) * 4; col.Rgba[o] = col.Rgba[o + 1] = col.Rgba[o + 2] = 240; }
            var m = SkyLight.BlendReflections(new List<(SkyLight.ReflectionMap, float, float)> { (red, 0.25f, 0f), (col, 0.75f, 90f) }, 8);
            t.Equal(8, m.Width, "the blend's width");
            t.Equal(4, m.Height, "width / 2");
            t.Equal((byte)50, m.Rgba[(1 * 8 + 3) * 4], "a texel: 0.25 of the red (50) where the other map is dark");
            // turned by 90° (+ a quarter of u), the bright column at u = 0 is read at x = -2, i.e. it shows at x = 6
            t.Equal((byte)(50 + 180), m.Rgba[(1 * 8 + 6) * 4], "the turned map's column shows a quarter turn away");
            t.Equal((byte)180, m.Rgba[(1 * 8 + 6) * 4 + 1], "with its green");
            t.Equal((byte)255, m.Rgba[3], "opaque");
        }
    }
}
