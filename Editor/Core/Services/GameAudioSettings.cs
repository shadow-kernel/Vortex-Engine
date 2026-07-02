using System;
using System.IO;
using System.Runtime.Serialization;
using Editor.Core.Data;
using Editor.DllWrapper;

namespace Editor.Core.Services
{
    /// <summary>
    /// Per-PLAYER audio settings for a SHIPPED game (#20): the runtime bus-volume / mute choices a player makes in an
    /// in-game options screen, persisted to a writable location (%LocalAppData%\Vortex\&lt;Game&gt;\audio-settings.json)
    /// and re-applied on the next launch — ON TOP of the designer-tuned mixer defaults that ship packed in the .vpak
    /// (<see cref="AudioMixerConfig"/>). Only active in a shipped/release build; in the editor the Audio Mixer window
    /// owns the project's ProjectSettings config, so player-override persistence there would fight it.
    /// </summary>
    public sealed class GameAudioSettings
    {
        public static GameAudioSettings Instance { get; } = new GameAudioSettings();
        private GameAudioSettings() { }

        [DataContract(Name = "GameAudioSettings", Namespace = "")]
        private class State
        {
            [DataMember(Name = "busVolumes", Order = 0)] public float[] BusVolumes { get; set; }
            [DataMember(Name = "busMutes", Order = 1)] public bool[] BusMutes { get; set; }
        }

        private string _lastSig;   // dedup: skip a disk write when nothing actually changed

        // Player-override persistence is a SHIPPED-game concept. In the editor, changes are the designer's and belong
        // in the project's ProjectSettings (owned by the Audio Mixer window) — persisting to LocalAppData would shadow it.
        private static bool ShouldPersist => PlayModeService.Instance.IsReleaseMode;

        private static string SettingsFile()
        {
            var name = ProjectData.Current?.Name;
            if (string.IsNullOrWhiteSpace(name)) name = "VortexGame";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vortex", name);
            return Path.Combine(dir, "audio-settings.json");
        }

        /// <summary>Shipped game only: load the player's saved bus volumes/mutes and push them over the mixer defaults.
        /// Call right AFTER <c>AudioMixerConfig.Load(...).Apply()</c> at play start, so the player's choices win.</summary>
        public void LoadAndApply()
        {
            if (!ShouldPersist) return;
            try
            {
                var file = SettingsFile();
                if (!File.Exists(file)) return;
                var s = Serialization.DataSerializer.FromJson<State>(File.ReadAllText(file));
                if (s == null) return;
                for (int i = 0; i < VortexAudio.BusCount; i++)
                {
                    if (s.BusVolumes != null && i < s.BusVolumes.Length) VortexAudio.SetBusVolume(i, s.BusVolumes[i]);
                    if (s.BusMutes != null && i < s.BusMutes.Length) VortexAudio.SetBusMute(i, s.BusMutes[i]);
                }
                _lastSig = Signature();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[GameAudioSettings] load failed: " + ex.Message); }
        }

        /// <summary>Shipped game only: snapshot the CURRENT native bus volumes/mutes and persist them, so a runtime
        /// change (options slider, script) survives a restart. Dedup'd — a value that didn't move writes nothing, so
        /// it's safe to call on every slider tick.</summary>
        public void Persist()
        {
            if (!ShouldPersist) return;
            try
            {
                var sig = Signature();
                if (sig == _lastSig) return;   // no real change since the last write

                var s = new State { BusVolumes = new float[VortexAudio.BusCount], BusMutes = new bool[VortexAudio.BusCount] };
                for (int i = 0; i < VortexAudio.BusCount; i++)
                {
                    s.BusVolumes[i] = VortexAudio.GetBusVolume(i);
                    s.BusMutes[i] = VortexAudio.GetBusMute(i);
                }
                var file = SettingsFile();
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, Serialization.DataSerializer.ToJson(s));
                _lastSig = sig;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[GameAudioSettings] save failed: " + ex.Message); }
        }

        private static string Signature()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < VortexAudio.BusCount; i++)
                sb.Append(VortexAudio.GetBusVolume(i).ToString("0.###")).Append(VortexAudio.GetBusMute(i) ? '1' : '0').Append('|');
            return sb.ToString();
        }
    }
}
