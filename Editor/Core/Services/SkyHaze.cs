using System;

namespace Editor.Core.Services
{
    /// <summary>
    /// The haze (the distance fog's colour) as one state, so "follow the sky" works for every caller — the scene settings,
    /// a script's Atmosphere.SetFog, the editor's weather tool. When following, the colour pushed to the renderer is the sky's
    /// own radiance just above the horizon (measured by the sky light bake, in the units the skybox is drawn with) times the
    /// given colour as a tint: the far dunes fade into exactly the sky they stand against. A hand-picked haze three times
    /// brighter than the sky turned the whole distance white. Without a sky, or with following off, the colour is used as given.
    /// </summary>
    public static class SkyHaze
    {
        public static bool FollowSky { get; private set; }
        public static float TintR { get; private set; } = 1f;
        public static float TintG { get; private set; } = 1f;
        public static float TintB { get; private set; } = 1f;
        public static float Density { get; private set; }
        public static float HeightY { get; private set; }
        public static float Falloff { get; private set; }
        /// <summary>The sky's horizon radiance as last measured (SceneRenderService's sky bake); false without a sky.</summary>
        public static bool HorizonKnown { get; private set; }
        public static float HorizonR { get; private set; }
        public static float HorizonG { get; private set; }
        public static float HorizonB { get; private set; }

        /// <summary>The fog as the scene or a script wants it; pushed to the renderer at once, and again whenever the sky's
        /// horizon colour changes while following.</summary>
        public static void Set(bool followSky, float r, float g, float b, float density, float heightY, float falloff)
        {
            FollowSky = followSky; TintR = r; TintG = g; TintB = b;
            Density = density; HeightY = heightY; Falloff = falloff;
            Push();
        }

        /// <summary>The sky bake reports the sky's radiance just above the horizon (or that there is no sky).</summary>
        public static void SetHorizon(bool known, float r, float g, float b)
        {
            if (known == HorizonKnown && Math.Abs(r - HorizonR) < 1e-4f && Math.Abs(g - HorizonG) < 1e-4f && Math.Abs(b - HorizonB) < 1e-4f) return;
            HorizonKnown = known; HorizonR = r; HorizonG = g; HorizonB = b;
            if (FollowSky) Push();
        }

        /// <summary>The colour the renderer gets: the sky's horizon times the tint while following a known sky, else the tint itself.</summary>
        public static void Effective(out float r, out float g, out float b)
        {
            if (FollowSky && HorizonKnown) { r = HorizonR * TintR; g = HorizonG * TintG; b = HorizonB * TintB; }
            else { r = TintR; g = TintG; b = TintB; }
        }

        private static void Push()
        {
            float r, g, b; Effective(out r, out g, out b);
            try { Editor.DllWrapper.VortexAPI.SetFog(r, g, b, Density, HeightY, Falloff); } catch { }
        }
    }
}
