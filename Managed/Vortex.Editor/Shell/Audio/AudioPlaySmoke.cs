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
        internal static void Register() => SmokeRegistry.Add("audio play: a spawned prefab's sound starts, Destroy stops it", Run);

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
