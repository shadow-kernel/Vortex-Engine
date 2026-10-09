using System.Runtime.InteropServices;

namespace Editor.DllWrapper
{
    /// <summary>Volumetric fog (#119) — mirrors VortexAPI/Api/LightingApi.cpp SetVolumetricFogParams. Persistent renderer
    /// state like the analytic fog: the scene settings apply it, scripts (Atmosphere.SetVolumetricFog) override it.</summary>
    public static partial class VortexAPI
    {
        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetVolumetricFogParams(int enabled, float density, float anisotropy, float maxDistance,
            float noiseStrength, float noiseScale, float noiseSpeed, float sun, float lights, float ambient, int steps, int shadows);

        /// <summary>Ray-marched fog: light scattered towards the camera from the fog colour, the sun (through the cascades)
        /// and the point / spot lights (through their shadow maps). <paramref name="density"/> scales the scattering (the
        /// height profile comes from <see cref="SetFog"/>), <paramref name="anisotropy"/> is the Henyey-Greenstein g
        /// (0 = even, towards 1 = bright when looking into lights), <paramref name="maxDistance"/> the metres marched,
        /// <paramref name="noise"/> / <paramref name="noiseScale"/> / <paramref name="noiseSpeed"/> the wind-blown patchiness,
        /// <paramref name="sun"/> / <paramref name="lights"/> / <paramref name="ambient"/> the contributions, <paramref name="steps"/>
        /// the march quality (8..48), <paramref name="shadows"/> whether light shafts stop at walls.</summary>
        public static void SetVolumetricFog(bool enabled, float density, float anisotropy, float maxDistance, float noise, float noiseScale,
            float noiseSpeed, float sun, float lights, float ambient, int steps, bool shadows)
        {
            try { SetVolumetricFogParams(enabled ? 1 : 0, density, anisotropy, maxDistance, noise, noiseScale, noiseSpeed, sun, lights, ambient, steps, shadows ? 1 : 0); } catch { }
        }
    }
}
