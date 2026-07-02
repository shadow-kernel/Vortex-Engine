using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using Editor.DllWrapper;

namespace Editor.Core.Services
{
    /// <summary>
    /// The project's mixer state (issue #13): per-bus default volumes/mutes and the
    /// ducking rules, serialized as ProjectSettings/AudioMixer.json. Loaded and
    /// applied identically in editor play mode and the shipped game; the Audio Mixer
    /// window (issue #14) edits and saves it.
    /// </summary>
    [DataContract(Name = "AudioMixerConfig", Namespace = "")]
    public class AudioMixerConfig
    {
        [DataContract(Name = "DuckRule", Namespace = "")]
        public class DuckRule
        {
            [DataMember(Name = "triggerBus", Order = 0)] public int TriggerBus { get; set; }
            [DataMember(Name = "targetBus", Order = 1)] public int TargetBus { get; set; }
            /// <summary>Attenuation while ducked, in dB (negative, e.g. -12).</summary>
            [DataMember(Name = "duckDb", Order = 2)] public float DuckDb { get; set; } = -12f;
            [DataMember(Name = "attackMs", Order = 3)] public float AttackMs { get; set; } = 80f;
            [DataMember(Name = "releaseMs", Order = 4)] public float ReleaseMs { get; set; } = 400f;
            /// <summary>Trigger-bus RMS above this engages the duck (linear 0..1).</summary>
            [DataMember(Name = "threshold", Order = 5)] public float Threshold { get; set; } = 0.05f;
        }

        [DataMember(Name = "busVolumes", Order = 0)]
        public float[] BusVolumes { get; set; } = { 1f, 1f, 1f, 1f, 1f };

        [DataMember(Name = "busMutes", Order = 1)]
        public bool[] BusMutes { get; set; } = new bool[VortexAudio.BusCount];

        [DataMember(Name = "ducks", Order = 2)]
        public List<DuckRule> Ducks { get; set; } = new List<DuckRule>();

        public const string RelativePath = "ProjectSettings/AudioMixer.json";

        /// <summary>Loads the project's mixer config, or defaults when none exists yet.</summary>
        public static AudioMixerConfig Load(string projectPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(projectPath))
                {
                    var file = Path.Combine(projectPath, RelativePath);
                    if (File.Exists(file))
                    {
                        var loaded = Serialization.DataSerializer.FromJson<AudioMixerConfig>(File.ReadAllText(file));
                        if (loaded != null) { loaded.Normalize(); return loaded; }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AudioMixer] config load failed: " + ex.Message);
            }
            return new AudioMixerConfig();
        }

        public void Save(string projectPath)
        {
            try
            {
                if (string.IsNullOrEmpty(projectPath)) return;
                var file = Path.Combine(projectPath, RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, Serialization.DataSerializer.ToJson(this));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AudioMixer] config save failed: " + ex.Message);
            }
        }

        /// <summary>Pushes the whole config into the native mixer.</summary>
        public void Apply()
        {
            Normalize();
            for (int i = 0; i < VortexAudio.BusCount; i++)
            {
                VortexAudio.SetBusVolume(i, BusVolumes[i]);
                VortexAudio.SetBusMute(i, BusMutes[i]);
            }
            VortexAudio.ClearDucks();
            foreach (var d in Ducks)
            {
                if (d != null && d.DuckDb < 0f)
                    VortexAudio.SetDuck(d.TriggerBus, d.TargetBus, d.DuckDb, d.AttackMs, d.ReleaseMs, d.Threshold);
            }
        }

        private void Normalize()
        {
            if (BusVolumes == null || BusVolumes.Length != VortexAudio.BusCount)
            {
                var v = new float[VortexAudio.BusCount];
                for (int i = 0; i < v.Length; i++) v[i] = (BusVolumes != null && i < BusVolumes.Length) ? BusVolumes[i] : 1f;
                BusVolumes = v;
            }
            if (BusMutes == null || BusMutes.Length != VortexAudio.BusCount)
            {
                var m = new bool[VortexAudio.BusCount];
                if (BusMutes != null) Array.Copy(BusMutes, m, Math.Min(BusMutes.Length, m.Length));
                BusMutes = m;
            }
            if (Ducks == null) Ducks = new List<DuckRule>();
        }
    }
}
