using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;
using VortexEditor.Shell.Material;

namespace VortexEditor.Shell.Audio
{
    /// <summary>Smoke checks for the sound container editor and the audio mixer (no sound is played).</summary>
    internal static class AudioPackageSmoke
    {
        private static void Log(string s) => ConsoleService.Instance.Log("  " + s);

        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("sound container editor: opens a .vsndc with its entries and ranges", SoundContainer);
            SmokeRegistry.Add("audio mixer: bus strips, live solo, meters, single window", Mixer);
        }

        private static async Task<bool> SoundContainer()
        {
            string root = ProjectData.Current?.Path;
            if (root == null) return false;
            var files = Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.vsndc", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            string vsndc = files.FirstOrDefault(f => Path.GetFileName(f).Equals("gun_rifle.vsndc", StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault();
            if (vsndc == null) { Log("no .vsndc in the project"); return false; }
            byte[] before = File.ReadAllBytes(vsndc);
            SoundContainerEditorWindow.Open(vsndc);
            SoundContainerEditorWindow.Open(vsndc);   // second open brings the same window to front
            await SmokeRegistry.Settle(600);
            var windows = EditorKit.OpenWindows<SoundContainerEditorWindow>().ToList();
            var w = windows.FirstOrDefault();
            if (w == null) return false;
            int entries = w.Container.Entries.Count;
            bool rows = entries > 0 && w.EntryRows == entries;
            bool ranges = w.Container.PitchMin <= w.Container.PitchMax && w.Container.VolumeMin <= w.Container.VolumeMax;
            SmokeRegistry.Capture(w, "sound_container.png");
            w.Close();
            await SmokeRegistry.Settle(200);
            bool untouched = File.ReadAllBytes(vsndc).SequenceEqual(before);
            Log($"sound container: {Path.GetFileName(vsndc)} entries={entries} rows={w.EntryRows} windows={windows.Count} untouched={untouched}");
            return rows && ranges && windows.Count == 1 && untouched;
        }

        private static async Task<bool> Mixer()
        {
            string root = ProjectData.Current?.Path;
            if (root == null) return false;
            string cfgFile = Path.Combine(root, AudioMixerConfig.RelativePath);
            bool hadCfg = File.Exists(cfgFile);
            byte[] cfgBefore = hadCfg ? File.ReadAllBytes(cfgFile) : null;
            AudioMixerWindow.Open();
            AudioMixerWindow.Open();
            await SmokeRegistry.Settle(600);
            var all = EditorKit.OpenWindows<AudioMixerWindow>().ToList();
            var w = all.FirstOrDefault();
            if (w == null) return false;
            bool device = VortexAudio.HasDevice();
            bool anyUserMute = w.Config.BusMutes.Any(m => m);
            bool soloOk, soloNotSaved, duckSaved, duckRemoved, released;
            try
            {
                w.SetSolo(VortexAudio.BusMusic, true);
                await SmokeRegistry.Settle(700);   // longer than the deferred save: a solo must never be written
                soloOk = !device || anyUserMute || (VortexAudio.GetBusMute(VortexAudio.BusSfx) && VortexAudio.GetBusMute(VortexAudio.BusUi) && !VortexAudio.GetBusMute(VortexAudio.BusMusic) && !VortexAudio.GetBusMute(VortexAudio.BusMaster));
                soloNotSaved = hadCfg ? File.ReadAllBytes(cfgFile).SequenceEqual(cfgBefore) : !File.Exists(cfgFile);
                SmokeRegistry.Capture(w, "audio_mixer.png");

                int rules = w.Config.Ducks.Count;
                w.AddDuckRule();
                await SmokeRegistry.Settle(250);
                var saved = AudioMixerConfig.Load(root);
                duckSaved = w.Config.Ducks.Count == rules + 1 && saved.Ducks.Count == rules + 1;
                SmokeRegistry.Capture(w, "audio_mixer_ducking.png");
                w.RemoveDuckRule(w.Config.Ducks.Count - 1);
                duckRemoved = w.Config.Ducks.Count == rules && AudioMixerConfig.Load(root).Ducks.Count == rules;
                w.Close();   // closing releases the (never saved) solo
                await SmokeRegistry.Settle(200);
                released = !device || anyUserMute || !VortexAudio.GetBusMute(VortexAudio.BusSfx);
            }
            finally
            {
                // leave the project's mixer exactly as it was
                if (hadCfg) File.WriteAllBytes(cfgFile, cfgBefore); else if (File.Exists(cfgFile)) File.Delete(cfgFile);
                try { AudioMixerConfig.Load(root).Apply(); } catch { }
                foreach (var open in EditorKit.OpenWindows<AudioMixerWindow>()) open.Close();
            }
            Log($"mixer: windows={all.Count} device={device} solo={soloOk} soloNotSaved={soloNotSaved} duckSaved={duckSaved} duckRemoved={duckRemoved} released={released}");
            return all.Count == 1 && soloOk && soloNotSaved && duckSaved && duckRemoved && released;
        }
    }
}
