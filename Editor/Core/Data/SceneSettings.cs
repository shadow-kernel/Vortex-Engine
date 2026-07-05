using System.Runtime.Serialization;

namespace Editor.Core.Data
{
    /// <summary>
    /// Per-scene environment settings (fog #27 + post-FX #28/#29), serialized into the .vscene and
    /// applied whenever the scene becomes the live one (open in editor, play, shipped game). Scripts
    /// (Vortex.Atmosphere / Vortex.PostFx) can still override at runtime — leaving play mode re-applies
    /// these authored values. Old .vscene files simply have no settings block and get the defaults
    /// (everything off = the untouched render path).
    /// </summary>
    [DataContract(Name = "SceneSettings", Namespace = "")]
    public class SceneSettings
    {
        // ---- Fog (#27) ----
        [DataMember(Name = "fogOn", Order = 0)] public bool FogEnabled { get; set; }
        [DataMember(Name = "fogDensity", Order = 1)] public float FogDensity { get; set; } = 0.08f;
        [DataMember(Name = "fogHeightY", Order = 2)] public float FogHeightY { get; set; }
        [DataMember(Name = "fogHeightFalloff", Order = 3)] public float FogHeightFalloff { get; set; }
        [DataMember(Name = "fogR", Order = 4)] public float FogR { get; set; } = 0.02f;
        [DataMember(Name = "fogG", Order = 5)] public float FogG { get; set; } = 0.025f;
        [DataMember(Name = "fogB", Order = 6)] public float FogB { get; set; } = 0.035f;

        // ---- Vignette (#29) ----
        [DataMember(Name = "vigOn", Order = 10)] public bool VignetteEnabled { get; set; }
        [DataMember(Name = "vigIntensity", Order = 11)] public float VignetteIntensity { get; set; } = 0.8f;
        [DataMember(Name = "vigSmoothness", Order = 12)] public float VignetteSmoothness { get; set; } = 0.5f;
        [DataMember(Name = "vigRoundness", Order = 13)] public float VignetteRoundness { get; set; } = 1.0f;
        [DataMember(Name = "vigR", Order = 14)] public float VignetteR { get; set; }
        [DataMember(Name = "vigG", Order = 15)] public float VignetteG { get; set; }
        [DataMember(Name = "vigB", Order = 16)] public float VignetteB { get; set; }

        // ---- Film grain (#29) ----
        [DataMember(Name = "grainOn", Order = 20)] public bool GrainEnabled { get; set; }
        [DataMember(Name = "grainIntensity", Order = 21)] public float GrainIntensity { get; set; } = 0.35f;
        [DataMember(Name = "grainSize", Order = 22)] public float GrainSize { get; set; } = 1.6f;

        // ---- Chromatic aberration (#29) ----
        [DataMember(Name = "caOn", Order = 30)] public bool CaEnabled { get; set; }
        [DataMember(Name = "caStrength", Order = 31)] public float CaStrength { get; set; } = 0.35f;
        [DataMember(Name = "caFalloff", Order = 32)] public float CaFalloff { get; set; } = 1.2f;

        // ---- Color grading (#31) ----
        [DataMember(Name = "gradeOn", Order = 40)] public bool GradeEnabled { get; set; }
        [DataMember(Name = "exposure", Order = 41)] public float Exposure { get; set; }
        [DataMember(Name = "contrast", Order = 42)] public float Contrast { get; set; } = 1.0f;
        [DataMember(Name = "saturation", Order = 43)] public float Saturation { get; set; } = 1.0f;
        [DataMember(Name = "temperature", Order = 44)] public float Temperature { get; set; }
        [DataMember(Name = "tint", Order = 45)] public float Tint { get; set; }

        // ---- Bloom (#30) ----
        [DataMember(Name = "bloomOn", Order = 50)] public bool BloomEnabled { get; set; }
        [DataMember(Name = "bloomThreshold", Order = 51)] public float BloomThreshold { get; set; } = 0.75f;
        [DataMember(Name = "bloomKnee", Order = 52)] public float BloomKnee { get; set; } = 0.5f;
        [DataMember(Name = "bloomIntensity", Order = 53)] public float BloomIntensity { get; set; } = 0.7f;
        [DataMember(Name = "bloomScatter", Order = 54)] public float BloomScatter { get; set; } = 0.65f;

        /// <summary>DataContractSerializer builds an UNINITIALIZED object (field initializers never
        /// run), so members missing from an older .vscene would deserialize as 0 — a scene saved
        /// before bloom existed must still open with the neutral bloom defaults, not zeros. Restore
        /// every non-zero default here; authored values then overwrite whichever members the file has.</summary>
        [OnDeserializing]
        private void RestoreDefaults(StreamingContext context)
        {
            FogDensity = 0.08f; FogR = 0.02f; FogG = 0.025f; FogB = 0.035f;
            VignetteIntensity = 0.8f; VignetteSmoothness = 0.5f; VignetteRoundness = 1.0f;
            GrainIntensity = 0.35f; GrainSize = 1.6f;
            CaStrength = 0.35f; CaFalloff = 1.2f;
            Contrast = 1.0f; Saturation = 1.0f;
            BloomThreshold = 0.75f; BloomKnee = 0.5f; BloomIntensity = 0.7f; BloomScatter = 0.65f;
        }

        /// <summary>Push every setting into the renderer (persistent frame state, same-frame visible).</summary>
        public void Apply()
        {
            DllWrapper.VortexAPI.SetFog(FogR, FogG, FogB, FogEnabled ? FogDensity : 0f,
                FogHeightY, FogHeightFalloff);
            DllWrapper.VortexAPI.SetPostVignette(VignetteEnabled, VignetteIntensity, VignetteSmoothness,
                VignetteRoundness, VignetteR, VignetteG, VignetteB);
            DllWrapper.VortexAPI.SetPostGrain(GrainEnabled, GrainIntensity, GrainSize);
            DllWrapper.VortexAPI.SetPostChromaticAberration(CaEnabled, CaStrength, CaFalloff);
            DllWrapper.VortexAPI.SetPostColorGrade(GradeEnabled, Exposure, Contrast, Saturation, Temperature, Tint);
            DllWrapper.VortexAPI.SetPostBloom(BloomEnabled, BloomThreshold, BloomKnee, BloomIntensity, BloomScatter);
        }

        /// <summary>Renderer back to a clean image (used when a scene without settings becomes live).</summary>
        public static void ApplyDefaults() => new SceneSettings().Apply();
    }
}
