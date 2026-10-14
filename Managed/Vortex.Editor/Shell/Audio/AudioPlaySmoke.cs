using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Audio;

namespace VortexEditor.Shell.Audio
{
    /// <summary>
    /// Sounds of entities that join or leave the running game: a prefab spawned with Scene.Instantiate plays its Play On
    /// Awake source, Scene.Destroy stops it (both were silent / kept playing before). Plays a generated tone — with no
    /// audio device (CI) the check stops at "managed and meant to play".
    /// </summary>
    internal static class AudioPlaySmoke
    {
        private const string Clip = "Assets/Audio/smoke_hum.wav";
        private const string Prefab = "Assets/Prefabs/SmokeHum.ventity";

        [ModuleInitializer]
        internal static void Register()
        {
            SmokeRegistry.Add("audio play: a spawned prefab's sound starts, Destroy stops it", Run);
            SmokeRegistry.Add("audio play: one-shots get the reverb send inside a Reverb Zone", OneShotReverb);
        }

        /// <summary>#496: inside a wet Reverb Zone a 3D one-shot gets the send of an Audio Source with Reverb Zone Mix 1 from
        /// its first sample, a 2D one-shot stays dry unless given a mix, and the sends follow the zone while the shots play
        /// (zone switched off → 0). Without an audio engine (CI) no voice starts — then only the zone weight is checked.</summary>
        private static async Task<bool> OneShotReverb()
        {
            var log = ConsoleService.Instance;
            string root = ProjectData.Current?.Path;
            var scene = ProjectData.Current?.ActiveScene;
            if (root == null || scene == null) return false;
            string wav = Path.Combine(root, Clip.Replace('/', Path.DirectorySeparatorChar));
            GameEntity zoneEntity = null;
            bool started = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(wav));
                WriteTone(wav, 330, 2.0);
                zoneEntity = EditorCommands.CreateReverbZone(scene);
                var zone = zoneEntity?.GetComponent<ReverbZone>();
                if (zone == null) { log.LogError("one-shot reverb: no Reverb Zone was created"); return false; }
                zone.Shape = (int)ReverbZoneShape.Sphere; zone.Radius = 5000f; zone.Falloff = 1f; zone.WetLevel = 1f; zone.DecayTime = 3f;

                EditorCommands.Play();
                started = PlayModeService.Instance.State == PlayState.Playing;
                if (!started) { log.LogError("one-shot reverb: play mode did not start"); return false; }
                await Task.Delay(300);
                var svc = AudioPlaybackService.Instance;
                float weight = svc.ListenerZoneWeight;
                int before = svc.OneShotsForDiagnostics().Count;
                Vortex.Audio.PlayOneShot(Clip, new Vortex.Vector3(0f, 1f, 0f));
                Vortex.Audio.PlayOneShot2D(Clip);
                Vortex.Audio.PlayOneShot2D(Clip, 1f, 1f, 0.5f);
                var shots = svc.OneShotsForDiagnostics();
                if (shots.Count < before + 3)
                {
                    log.Log("one-shot reverb: listener zone weight " + weight + ", no voices started (device=" + VortexAudio.HasDevice() + ")");
                    return weight > 0.99f && !VortexAudio.HasDevice();
                }
                var mine = shots.GetRange(shots.Count - 3, 3);
                float s3d = VortexAudio.GetVoiceReverbSend(mine[0].Handle), s2d = VortexAudio.GetVoiceReverbSend(mine[1].Handle), s2dMix = VortexAudio.GetVoiceReverbSend(mine[2].Handle);
                zone.IsEnabled = false;   // the listener leaves the room: the sends of the shots still playing follow
                await Task.Delay(250);
                float a3d = VortexAudio.GetVoiceReverbSend(mine[0].Handle), a2dMix = VortexAudio.GetVoiceReverbSend(mine[2].Handle);
                log.Log("one-shot reverb: zone weight " + weight + "; sends at start 3D=" + s3d + " 2D=" + s2d + " 2D(mix 0.5)=" + s2dMix +
                        "; after the zone went off 3D=" + a3d + " 2D(mix 0.5)=" + a2dMix);
                return weight > 0.99f && Math.Abs(s3d - 1f) < 0.01f && Math.Abs(s2d) < 0.001f && Math.Abs(s2dMix - 0.5f) < 0.01f
                       && Math.Abs(a3d) < 0.001f && Math.Abs(a2dMix) < 0.001f;
            }
            catch (Exception ex) { log.LogError("one-shot reverb: " + ex.Message); return false; }
            finally
            {
                if (started) EditorCommands.Stop();
                if (zoneEntity != null) EditorCommands.DeleteEntities(new System.Collections.Generic.List<GameEntity> { zoneEntity });
                foreach (var f in new[] { wav, wav + ".vmeta" })
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            string root = ProjectData.Current?.Path;
            var scene = ProjectData.Current?.ActiveScene;
            if (root == null || scene == null) return false;
            string wav = Path.Combine(root, Clip.Replace('/', Path.DirectorySeparatorChar));
            string prefabFile = null;
            bool started = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(wav));
                WriteTone(wav, 220, 1.0);
                var template = new GameEntity("SmokeHum");
                template.AddComponent(new AudioSource { AudioClipPath = Clip, Loop = true, PlayOnAwake = true, SpatialBlend = 1f, MaxDistance = 30f });
                prefabFile = PrefabService.Instance.SaveAsPrefab(template, "SmokeHum");
                if (prefabFile == null) { log.LogError("audio play: the prefab was not written"); return false; }

                EditorCommands.Play();
                started = PlayModeService.Instance.State == PlayState.Playing;
                if (!started) { log.LogError("audio play: play mode did not start"); return false; }
                await Task.Delay(300);

                long handle = Vortex.Scene.Instantiate(Prefab, new Vortex.Vector3(3f, 1f, 0f), 0f);
                var spawned = FindSource(scene);
                await Task.Delay(400);   // Play On Awake starts on the next ticks
                bool tracked = spawned != null && AudioPlaybackService.Instance.IsTracked(spawned, out bool wants) && wants;
                bool device = VortexAudio.HasDevice();
                bool playing = spawned != null && AudioPlaybackService.Instance.ScriptIsPlaying(spawned);

                Vortex.Scene.Destroy(handle);
                await Task.Delay(200);
                bool released = spawned != null && !AudioPlaybackService.Instance.IsTracked(spawned, out _) && !AudioPlaybackService.Instance.ScriptIsPlaying(spawned);

                log.Log("audio play: handle=" + handle + " tracked=" + tracked + " device=" + device + " playing=" + playing + " released after Destroy=" + released);
                return handle != 0 && tracked && (playing || !device) && released;
            }
            catch (Exception ex) { log.LogError("audio play: " + ex.Message); return false; }
            finally
            {
                if (started) EditorCommands.Stop();
                foreach (var f in new[] { wav, wav + ".vmeta", prefabFile, prefabFile + ".vmeta" })
                    try { if (f != null && File.Exists(f)) File.Delete(f); } catch { }
            }
        }

        private static AudioSource FindSource(Scene scene)
        {
            AudioSource found = null;
            void Walk(GameEntity e)
            {
                if (e == null || found != null) return;
                var s = e.GetComponent<AudioSource>();
                if (s != null && s.AudioClipPath == Clip) { found = s; return; }
                if (e.Children != null) foreach (var c in e.Children) Walk(c);
            }
            foreach (var e in scene.Entities.ToList()) Walk(e);
            return found;
        }

        /// <summary>A mono 16-bit PCM sine tone.</summary>
        private static void WriteTone(string path, double hz, double seconds)
        {
            const int rate = 44100;
            int n = (int)(rate * seconds);
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + n * 2); w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(n * 2);
                for (int i = 0; i < n; i++) w.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * 6000));
            }
        }
    }
}
