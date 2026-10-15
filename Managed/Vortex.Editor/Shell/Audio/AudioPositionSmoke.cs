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
        /// <summary>A device mixes ahead of what it plays (a period or a few), so a position read after a measured wall time
        /// may lead that time by this much — the slack EngineTest's position bounds use too.</summary>
        private const double ReadAhead = 0.25;

        /// <summary>A position on the looping clip, unwrapped past the clip's end when it lies before <paramref name="target"/>.</summary>
        private static double Unwrapped(double value, double target) => value < target - 1e-3 ? value + ClipSeconds : value;

        /// <summary>A position read <paramref name="since"/> seconds of wall time after it was put at <paramref name="target"/>:
        /// from the target on, and at most that wall time plus the read-ahead further — measured, never an assumed delay
        /// (on the WARP runner a 300 ms delay lasted ~450 ms). The looping clip may have wrapped once.</summary>
        private static bool After(double value, double target, double since)
        {
            double v = Unwrapped(value, target);
            return v >= target - 1e-3 && v <= target + since + ReadAhead;
        }

        /// <summary>A value read between two reads of Time: up to five tries for a pair no mix fell between (both Time reads
        /// equal, so the value comes from that very cursor), else the last bracket — a mix moves the cursor by a block.</summary>
        private static (double Before, T Value, double After) Bracketed<T>(Vortex.AudioSource src, Func<T> read)
        {
            (double, T, double) last = default;
            for (int i = 0; i < 5; i++)
            {
                double a = src.Time;
                T v = read();
                double b = src.Time;
                last = (a, v, b);
                if (a == b) break;
            }
            return last;
        }

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
            bool audioExisted = Directory.Exists(Path.GetDirectoryName(wav));
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

                // Seek a decoded clip: the target reads back at once, playback continues from there — by at most the wall time
                // since the seek plus the device's read-ahead (measured: a 200 ms delay lasts longer on a slow runner)
                src.Time = 2.0f;
                var since = System.Diagnostics.Stopwatch.StartNew();
                float sought = src.Time;
                await Task.Delay(200);
                float after = src.Time;
                double afterSince = since.Elapsed.TotalSeconds;
                Expect(Math.Abs(sought - 2.0f) < 0.001f && Unwrapped(after, 2.0) > 2.0 && After(after, 2.0, afterSince),
                    "seeking a decoded clip (" + sought + " then " + after + ", " + afterSince.ToString("0.000") + " s later)");
                double length = src.Length;
                Expect(Math.Abs(length - ClipSeconds) < 0.01, "Length " + length);
                // NormalizedTime and TimeSamples come from the same cursor as Time: each is read between two reads of Time, and
                // the pair with no mix in between (both Time reads equal) must agree to the sample
                var (ta, nt, tb) = Bracketed(src, () => src.NormalizedTime);
                Expect(nt >= ta / length - 1e-5 && nt <= tb / length + 1e-5, "NormalizedTime " + nt + " = Time / Length (Time " + ta + " … " + tb + ")");
                var (sa, samples, sb) = Bracketed(src, () => src.TimeSamples);
                Expect(samples >= sa * ClipRate - 1 && samples <= sb * ClipRate + 1, "TimeSamples " + samples + " counts the clip's 44.1 kHz samples (Time " + sa + " … " + sb + ")");

                // Stop rewinds; a Time set while stopped is where Play() begins
                src.Stop();
                Expect(src.Time == 0f, "Stop rewinds Time to 0 (" + src.Time + ")");
                src.Time = 1.5f;
                Expect(Math.Abs(src.Time - 1.5f) < 1e-4f, "a stopped source reads the Time it will start from (" + src.Time + ")");
                since.Restart();
                src.Play();
                float resumed = src.Time;
                Expect(After(resumed, 1.5, since.Elapsed.TotalSeconds), "Play() begins at the Time set while stopped (" + resumed + ")");

                // Seek a streamed clip: the target reads back at once, then the stream refills from there and plays on
                src.Stop();
                comp.Streaming = true;
                src.Play();
                await Task.Delay(150);
                src.Time = 2.5f;
                since.Restart();
                float streamSought = src.Time;
                await Task.Delay(300);
                float streamed = src.Time;
                double streamedSince = since.Elapsed.TotalSeconds;
                Expect(Math.Abs(streamSought - 2.5f) < 0.001f && After(streamed, 2.5, streamedSince),
                    "seeking a streamed clip (" + streamSought + " then " + streamed + ", " + streamedSince.ToString("0.000") + " s later)");
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
                SmokeRegistry.RemoveIfCreated(Path.GetDirectoryName(wav), audioExisted);
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
