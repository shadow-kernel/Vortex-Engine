using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.ECS;

namespace VortexEditor.Shell.Audio
{
    /// <summary>
    /// #465 in the running editor, through the scripting API: AudioSource.Time is the mixer's position — after a
    /// PlayScheduled start it matches the mixer clock (Audio.DspTime) since that start to within 10 ms, it holds through
    /// Pause, runs at the source's Pitch, seeks a decoded and a streamed clip, and a Time set while stopped is where Play()
    /// begins; Length / NormalizedTime / TimeSamples agree with it; Audio.Music.Time seeks the music channel. Without an
    /// audio device the silent mixer runs on the game clock, so the check holds there too.
    /// </summary>
    internal static class AudioPositionSmoke
    {
        private const string Clip = "Assets/Audio/smoke_position.wav";
        private const double ClipSeconds = 3.0;
        private const int ClipRate = 44100;

        [ModuleInitializer]
        internal static void Register()
            => SmokeRegistry.Add("audio position: Time follows the mixer through pause, pitch and seeks; PlayScheduled starts on its frame", Run);

        private static async Task<bool> Run()
        {
            var log = ConsoleService.Instance;
            string root = ProjectData.Current?.Path;
            var scene = ProjectData.Current?.ActiveScene;
            if (root == null || scene == null) return false;
            string wav = Path.Combine(root, Clip.Replace('/', Path.DirectorySeparatorChar));
            GameEntity entity = null;
            bool started = false;
            var fails = new List<string>();
            void Expect(bool ok, string what) { if (!ok) fails.Add(what); }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(wav));
                WriteTone(wav, 330, ClipSeconds);
                entity = EditorCommands.CreateAudioSource(scene);
                var comp = entity?.GetComponent<Editor.ECS.Components.Audio.AudioSource>();
                if (comp == null) { log.LogError("audio position: no Audio Source was created"); return false; }
                entity.Name = "SmokePosition";
                comp.AudioClipPath = Clip; comp.Loop = true; comp.PlayOnAwake = false; comp.SpatialBlend = 0f; comp.Volume = 0.1f;

                EditorCommands.Play();
                started = PlayModeService.Instance.State == PlayState.Playing;
                if (!started) { log.LogError("audio position: play mode did not start"); return false; }
                await Task.Delay(200);
                var src = new Vortex.AudioSource(comp);

                // PlayScheduled: playing (as in Unity) while it waits; then Time = mixer seconds since the scheduled start
                double start = Vortex.Audio.DspTime + 0.3;
                src.PlayScheduled(start);
                Expect(src.IsPlaying && src.Time == 0f, "a scheduled source reads IsPlaying with Time 0 before its start (IsPlaying=" + src.IsPlaying + ", Time=" + src.Time + ")");
                await Task.Delay(800);
                double err = double.MaxValue;
                for (int i = 0; i < 6; i++)   // Time and DspTime read back to back; a mix may fall between two reads — keep the best pair
                {
                    double dsp = Vortex.Audio.DspTime;
                    err = Math.Min(err, Math.Abs(src.Time - (dsp - start)));
                    await Task.Delay(7);
                }
                Expect(err <= 0.010, "Time matches the mixer clock since the scheduled start within 10 ms (error " + (err * 1000).ToString("0.0") + " ms)");

                // Pause holds, Resume continues
                src.Pause();
                await Task.Delay(100);
                float held0 = src.Time;
                await Task.Delay(300);
                float held1 = src.Time;
                src.Resume();
                await Task.Delay(200);
                Expect(held0 == held1 && src.Time != held1, "Pause holds Time, Resume continues (" + held0 + " / " + held1 + " / " + src.Time + ")");

                // Pitch 2: Time runs twice as fast as the mixer clock
                src.Pitch = 2f;
                await Task.Delay(150);
                double t0 = src.Time, d0 = Vortex.Audio.DspTime;
                await Task.Delay(500);
                double t1 = src.Time, d1 = Vortex.Audio.DspTime;
                double ratio = (((t1 - t0) % ClipSeconds) + ClipSeconds) % ClipSeconds / Math.Max(1e-6, d1 - d0);
                Expect(Math.Abs(ratio - 2.0) < 0.1, "Time runs at the Pitch (ratio " + ratio.ToString("0.000") + ")");
                src.Pitch = 1f;

                // Seek a decoded clip: the target reads back at once, playback continues from there
                src.Time = 2.0f;
                float sought = src.Time;
                await Task.Delay(200);
                float after = src.Time;
                Expect(Math.Abs(sought - 2.0f) < 0.001f && after > 2.0f && after < 2.5f, "seeking a decoded clip (" + sought + " then " + after + ")");
                Expect(Math.Abs(src.Length - ClipSeconds) < 0.01, "Length " + src.Length);
                float nt = src.NormalizedTime, tm = src.Time;
                Expect(Math.Abs(nt - tm / ClipSeconds) < 0.01, "NormalizedTime " + nt + " = Time / Length");
                int samples = src.TimeSamples; tm = src.Time;
                Expect(Math.Abs(samples - tm * ClipRate) < 0.01 * ClipRate, "TimeSamples " + samples + " counts the clip's 44.1 kHz samples (Time " + tm + ")");

                // Stop rewinds; a Time set while stopped is where Play() begins
                src.Stop();
                Expect(src.Time == 0f, "Stop rewinds Time to 0 (" + src.Time + ")");
                src.Time = 1.5f;
                Expect(Math.Abs(src.Time - 1.5f) < 1e-4f, "a stopped source reads the Time it will start from (" + src.Time + ")");
                src.Play();
                float resumed = src.Time;
                Expect(resumed >= 1.5f - 1e-4f && resumed < 1.7f, "Play() begins at the Time set while stopped (" + resumed + ")");

                // Seek a streamed clip
                src.Stop();
                comp.Streaming = true;
                src.Play();
                await Task.Delay(150);
                src.Time = 2.5f;
                await Task.Delay(300);
                float streamed = src.Time;
                Expect(streamed >= 2.5f && streamed < 2.95f, "seeking a streamed clip (" + streamed + ")");
                src.Stop();

                // the music channel
                Vortex.Audio.Music.Play(Clip);
                await Task.Delay(100);
                Vortex.Audio.Music.Time = 1.0f;
                float music = Vortex.Audio.Music.Time;
                Expect(Math.Abs(music - 1.0f) < 0.001f && Math.Abs(Vortex.Audio.Music.Length - ClipSeconds) < 0.01, "Audio.Music.Time seeks the track (" + music + ", length " + Vortex.Audio.Music.Length + ")");
                Vortex.Audio.Music.Stop();

                log.Log("audio position: device=" + VortexAudio.HasDevice() + ", scheduled-start error " + (err * 1000).ToString("0.00") + " ms, pitch-2 ratio " +
                        ratio.ToString("0.000") + (fails.Count == 0 ? ", all as expected" : ", FAILED: " + string.Join("; ", fails)));
                return fails.Count == 0;
            }
            catch (Exception ex) { log.LogError("audio position: " + ex.Message); return false; }
            finally
            {
                if (started) EditorCommands.Stop();
                if (entity != null) EditorCommands.DeleteEntities(new List<GameEntity> { entity });
                foreach (var f in new[] { wav, wav + ".vmeta" })
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }

        /// <summary>A mono 16-bit PCM sine tone at 44.1 kHz.</summary>
        private static void WriteTone(string path, double hz, double seconds)
        {
            int n = (int)(ClipRate * seconds);
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + n * 2); w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(ClipRate); w.Write(ClipRate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(n * 2);
                for (int i = 0; i < n; i++) w.Write((short)(Math.Sin(2 * Math.PI * hz * i / ClipRate) * 6000));
            }
        }
    }
}
