using System;
using System.Runtime.InteropServices;

namespace Editor.DllWrapper
{
    /// <summary>
    /// P/Invoke bridge to the native voice-level audio API (VortexAPI AudioApi.cpp).
    /// Voice handles are opaque ulongs with a generation counter — stale handles
    /// (stolen or finished voices) are safe no-ops natively, so callers never need
    /// to guard against lifetime races. 0 is never a valid handle.
    /// </summary>
    public static class VortexAudio
    {
        public const ulong InvalidVoice = 0;

        /// <summary>Stable mixer bus indices — match the native audio::bus enum.</summary>
        public const int BusMaster = 0;
        public const int BusMusic = 1;
        public const int BusSfx = 2;
        public const int BusAmbience = 3;
        public const int BusUi = 4;
        public const int BusCount = 5;

        public static readonly string[] BusNames = { "Master", "Music", "SFX", "Ambience", "UI" };

        /// <summary>Bus name ("Music", case-insensitive) → index, -1 when unknown.</summary>
        public static int BusIndexFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            for (int i = 0; i < BusNames.Length; i++)
                if (string.Equals(BusNames[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private const string _dllName = "VortexAPI";        // .NET probes libVortexAPI.dylib / .so / VortexAPI.dll
        private const CallingConvention _cc = CallingConvention.Cdecl;

        // Paths marshal as UTF-8 (native side widens UTF-8-first with ANSI fallback),
        // so unicode project folders and non-ASCII %TEMP% usernames survive the trip —
        // plain LPStr would best-fit them to '?' and silently kill all audio.
        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int AudioPreloadClip([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int AudioValidateClip([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int AudioGetClipInfo([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            out float durationSeconds, out int sampleRate, out int channels);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int AudioGetWaveform([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            [In, Out] float[] peaks, int binCount);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern ulong AudioPlayVoice([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            float volume, float pitch, float pan, int loop, int priority, int stream, int outBus, int hrtf, int occlusion, float reverbSend,
            ulong startTime, double startPosition);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern ulong AudioGetDspTime(out int sampleRate);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int AudioGetVoicePosition(ulong handle, out ulong cursor, out ulong length, out int sampleRate);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioSeekVoice(ulong handle, double seconds);

        // stems: UTF-8 path pointers (the classic marshaller has no UTF-8 string arrays — PlayMusic builds them)
        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern ulong AudioPlayMusic(IntPtr[] stems, int count,
            float[] stateGains, float[] paramGains, ulong loopStart, ulong loopEnd, ulong startFrame, int loop, int stream, int outBus, float volume, ulong startTime);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioMusicSchedule(ulong handle, int stage, float[] gains, int count, ulong atFrame, ulong rampFrames);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int AudioMusicPlayStinger(ulong handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, float volume, ulong atFrame);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int AudioMusicGetClock(ulong handle, out MusicClock clock);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern float AudioMusicGetStemGain(ulong handle, int stem);

        /// <summary>The clock of a music voice (#437) — mirrors the native AudioMusicClockData field for field.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct MusicClock
        {
            /// <summary>Frames the music has played: monotonic, the clock schedules run on.</summary>
            public ulong Played;
            /// <summary>Where in the stems it is (frames at SampleRate), wrapping in the loop region.</summary>
            public ulong Position;
            /// <summary>Completed passes through the loop region.</summary>
            public ulong Loops;
            public ulong LoopStart, LoopEnd, Length;
            /// <summary>Played at which the last state schedule began; ulong.MaxValue = none yet.</summary>
            public ulong LastStateFrame;
            /// <summary>Wall time since the mixer last read the music.</summary>
            public double SecondsSinceMix;
            public int SampleRate;
            /// <summary>1 = the last mix waited for a stream to refill (every stem waits together).</summary>
            public int Stalled;
        }

        /// <summary>Gain stages of a music voice's stems (#437): the state's volumes and the parameter mapping's.</summary>
        public const int MusicStageState = 0, MusicStageParameter = 1;

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioSteamSetEnabled(int enabled);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioSteamSetGeometry(float[] verts, int vertexCount, int[] indices, int indexCount);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioSetBusVolume(int bus, float volume);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern float AudioGetBusVolume(int bus);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioSetBusMute(int bus, int mute);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int AudioGetBusMute(int bus);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioGetBusLevels(int bus, out float peak, out float rms);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioSetDuck(int triggerBus, int targetBus, float duckDb, float attackMs, float releaseMs, float threshold);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioClearDucks();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioSetReverbParams(float decaySeconds, float wetLevel, float predelayMs);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioSetVoiceReverbSend(ulong handle, float send);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern float AudioGetVoiceReverbSend(ulong handle);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioFadeVoice(ulong handle, float target, float seconds, int stopWhenDone);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int AudioRegisterClipData([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            byte[] data, ulong size);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioStopVoice(ulong handle);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioPauseVoice(ulong handle);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioResumeVoice(ulong handle);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int AudioIsVoicePlaying(ulong handle);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int AudioIsVoiceValid(ulong handle);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioSetVoiceVolume(ulong handle, float volume);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioSetVoicePitch(ulong handle, float pitch);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioSetVoicePan(ulong handle, float pan);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioSetVoicePosition(ulong handle, float x, float y, float z);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioSetVoiceSpatial(ulong handle, float spatialBlend,
            float minDistance, float maxDistance, int rolloffMode, float dopplerLevel, float spread);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioSetListener(float px, float py, float pz,
            float fx, float fy, float fz, float ux, float uy, float uz);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int AudioHasDevice();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AudioGetVoiceStats(out int active, out int stolen, out int maxVoices);

        /// <summary>Decode + cache a clip up front. False means the file is missing or no
        /// decoder accepts it — a permanent failure the caller should stop retrying.</summary>
        public static bool PreloadClip(string path)
            => !string.IsNullOrEmpty(path) && AudioPreloadClip(path) != 0;

        /// <summary>Header-probe without decoding — the cheap streaming counterpart of
        /// PreloadClip, same permanent-failure semantics.</summary>
        public static bool ValidateClip(string path)
            => !string.IsNullOrEmpty(path) && AudioValidateClip(path) != 0;

        /// <summary>Duration/format facts for browser tooltips. False = unreadable clip.</summary>
        public static bool GetClipInfo(string path, out float durationSeconds, out int sampleRate, out int channels)
        {
            durationSeconds = 0; sampleRate = 0; channels = 0;
            return !string.IsNullOrEmpty(path) && AudioGetClipInfo(path, out durationSeconds, out sampleRate, out channels) != 0;
        }

        /// <summary>Per-bin peak amplitudes (0..1) for waveform thumbnails. Decodes the
        /// whole clip once — call from a background thread.</summary>
        public static float[] GetWaveform(string path, int binCount)
        {
            if (string.IsNullOrEmpty(path) || binCount <= 0) return null;
            var peaks = new float[binCount];
            return AudioGetWaveform(path, peaks, binCount) != 0 ? peaks : null;
        }

        /// <summary>Starts a voice; returns InvalidVoice when the clip can't be decoded
        /// or every pooled voice outranks this request (priority 0 = most important).
        /// stream = decode on demand (music/long ambience) instead of full pre-decode.
        /// bus routes the voice through a mixer bus (default SFX). reverbSend = the reverb send (0..1) from the
        /// first sample on, before the per-frame SetVoiceReverbSend takes over (#496). startTime = the mixer frame
        /// (<see cref="GetDspTime"/>) the voice starts on, sample-accurate, 0 = now; startPosition = seconds into the
        /// clip playback begins at (#465).</summary>
        public static ulong PlayVoice(string path, float volume, float pitch, float pan, bool loop, int priority, bool stream = false, int bus = BusSfx,
            bool hrtf = false, bool occlusion = false, float reverbSend = 0f, ulong startTime = 0, double startPosition = 0)
        {
            if (string.IsNullOrEmpty(path)) return InvalidVoice;
            return AudioPlayVoice(path, volume, pitch, pan, loop ? 1 : 0, priority, stream ? 1 : 0, bus, hrtf ? 1 : 0, occlusion ? 1 : 0, reverbSend,
                startTime, startPosition > 0 ? startPosition : 0);
        }

        /// <summary>The mixer clock (#465): frames mixed since the audio engine started; its rate goes to
        /// <paramref name="sampleRate"/> (0 without an audio engine). Advances in mixer blocks (~10 ms).</summary>
        public static ulong GetDspTime(out int sampleRate) => AudioGetDspTime(out sampleRate);

        /// <summary>A voice's mixer position and its clip's length (0 = unknown) in the voice's own frames at
        /// <paramref name="sampleRate"/> (#465). False for a stale handle.</summary>
        public static bool GetVoicePosition(ulong handle, out ulong cursor, out ulong length, out int sampleRate)
        {
            cursor = 0; length = 0; sampleRate = 0;
            return handle != InvalidVoice && AudioGetVoicePosition(handle, out cursor, out length, out sampleRate) != 0;
        }

        /// <summary>Seek a voice to <paramref name="seconds"/> into its clip, decoded or streamed (#465) — the mixer
        /// applies it before its next read; clamped to the clip, wrapped for a looping voice.</summary>
        public static void SeekVoice(ulong handle, double seconds)
        {
            if (handle != InvalidVoice) AudioSeekVoice(handle, double.IsNaN(seconds) || seconds < 0 ? 0 : seconds);
        }

        /// <summary>Adaptive music (#437): plays stems (resolved clip paths) mixed sample-locked as ONE voice at priority 0,
        /// never pitched. Frames are at the engine rate (<see cref="GetDspTime"/>): the loop region (end 0 = the longest
        /// stem), the start frame, and the mixer frame the music begins on (0 = now). InvalidVoice when a stem cannot be
        /// opened.</summary>
        public static ulong PlayMusic(string[] stems, float[] stateGains, float[] paramGains, ulong loopStart, ulong loopEnd, ulong startFrame,
            bool loop, bool stream, int bus, float volume, ulong startTime = 0)
        {
            if (stems == null || stems.Length == 0 || Array.Exists(stems, string.IsNullOrEmpty)) return InvalidVoice;
            var utf8 = new IntPtr[stems.Length];
            try
            {
                for (int i = 0; i < stems.Length; i++) utf8[i] = Marshal.StringToCoTaskMemUTF8(stems[i]);
                return AudioPlayMusic(utf8, stems.Length, stateGains, paramGains, loopStart, loopEnd, startFrame, loop ? 1 : 0, stream ? 1 : 0, bus, volume, startTime);
            }
            finally
            {
                foreach (var p in utf8) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            }
        }

        /// <summary>Ramp a gain stage (<see cref="MusicStageState"/> / <see cref="MusicStageParameter"/>) of every stem of a
        /// music voice to <paramref name="gains"/> over <paramref name="rampFrames"/>, beginning exactly when the music has
        /// played <paramref name="atFrame"/> frames (0 = its next mix). Replaces a schedule of that stage not begun yet.</summary>
        public static void MusicSchedule(ulong handle, int stage, float[] gains, ulong atFrame, ulong rampFrames)
        {
            if (handle != InvalidVoice && gains != null && gains.Length > 0) AudioMusicSchedule(handle, stage, gains, gains.Length, atFrame, rampFrames);
        }

        /// <summary>Mix a clip once into a music voice from its frame <paramref name="atFrame"/> (a stinger). False when it
        /// cannot be opened or four stingers already play.</summary>
        public static bool MusicPlayStinger(ulong handle, string path, float volume, ulong atFrame)
            => handle != InvalidVoice && !string.IsNullOrEmpty(path) && AudioMusicPlayStinger(handle, path, volume, atFrame) != 0;

        public static bool GetMusicClock(ulong handle, out MusicClock clock)
        {
            clock = default;
            return handle != InvalidVoice && AudioMusicGetClock(handle, out clock) != 0;
        }

        /// <summary>One stem's gain (state × parameter) after the last mix; -1 for no such stem / not a music voice.</summary>
        public static float GetMusicStemGain(ulong handle, int stem)
            => handle != InvalidVoice ? AudioMusicGetStemGain(handle, stem) : -1f;

        /// <summary>Steam Audio v2 (#21) project master switch. Off by default; turning it on lazily initializes
        /// HRTF + occlusion if phonon.dll is present next to the app. Safe no-op when the DLL is missing.</summary>
        public static void SteamSetEnabled(bool enabled) => AudioSteamSetEnabled(enabled ? 1 : 0);

        /// <summary>Publish the scene's occlusion geometry to Steam Audio: flat vertex xyz array + triangle vertex
        /// indices. Rebuilds the acoustic scene. No-op unless Steam Audio is enabled + available.</summary>
        public static void SteamSetGeometry(float[] verts, int[] indices)
        {
            if (verts == null || indices == null || verts.Length < 9 || indices.Length < 3) return;
            AudioSteamSetGeometry(verts, verts.Length / 3, indices, indices.Length);
        }

        public static void SetBusVolume(int bus, float volume) => AudioSetBusVolume(bus, volume);
        public static float GetBusVolume(int bus) => AudioGetBusVolume(bus);
        public static void SetBusMute(int bus, bool mute) => AudioSetBusMute(bus, mute ? 1 : 0);
        public static bool GetBusMute(int bus) => AudioGetBusMute(bus) != 0;
        public static void GetBusLevels(int bus, out float peak, out float rms) => AudioGetBusLevels(bus, out peak, out rms);
        /// <summary>duckDb &lt; 0 installs/replaces the rule (e.g. -12 dB); &gt;= 0 removes it.</summary>
        public static void SetDuck(int triggerBus, int targetBus, float duckDb, float attackMs, float releaseMs, float threshold = 0.05f)
            => AudioSetDuck(triggerBus, targetBus, duckDb, attackMs, releaseMs, threshold);
        public static void ClearDucks() => AudioClearDucks();

        /// <summary>Blended reverb-zone parameters (global freeverb send bus).</summary>
        public static void SetReverbParams(float decaySeconds, float wetLevel, float predelayMs)
            => AudioSetReverbParams(decaySeconds, wetLevel, predelayMs);

        /// <summary>Per-voice reverb send (reverbZoneMix x listener zone weight, 0..1).</summary>
        public static void SetVoiceReverbSend(ulong handle, float send)
        {
            if (handle != InvalidVoice) AudioSetVoiceReverbSend(handle, send);
        }

        /// <summary>The voice's current reverb send (diagnostics, smoke checks); -1 for a stale handle or while the
        /// reverb node is down.</summary>
        public static float GetVoiceReverbSend(ulong handle)
            => handle != InvalidVoice ? AudioGetVoiceReverbSend(handle) : -1f;

        /// <summary>Sample-accurate fade envelope on top of the voice volume. Retargets
        /// smoothly mid-fade; stopWhenDone frees the voice after the fade (FadeOut).</summary>
        public static void FadeVoice(ulong handle, float target, float seconds, bool stopWhenDone = false)
        {
            if (handle != InvalidVoice) AudioFadeVoice(handle, target, seconds, stopWhenDone ? 1 : 0);
        }

        /// <summary>Hands an encoded audio blob (e.g. a .vpak entry) to the native engine;
        /// the name then resolves like a file path for both decoded and streaming voices.</summary>
        public static bool RegisterClipData(string name, byte[] data)
        {
            if (string.IsNullOrEmpty(name) || data == null || data.Length == 0) return false;
            return AudioRegisterClipData(name, data, (ulong)data.Length) != 0;
        }

        public static void StopVoice(ulong handle) { if (handle != InvalidVoice) AudioStopVoice(handle); }
        public static void PauseVoice(ulong handle) { if (handle != InvalidVoice) AudioPauseVoice(handle); }
        public static void ResumeVoice(ulong handle) { if (handle != InvalidVoice) AudioResumeVoice(handle); }
        public static bool IsVoicePlaying(ulong handle) => handle != InvalidVoice && AudioIsVoicePlaying(handle) != 0;
        public static bool IsVoiceValid(ulong handle) => handle != InvalidVoice && AudioIsVoiceValid(handle) != 0;
        public static void SetVoiceVolume(ulong handle, float volume) { if (handle != InvalidVoice) AudioSetVoiceVolume(handle, volume); }
        public static void SetVoicePitch(ulong handle, float pitch) { if (handle != InvalidVoice) AudioSetVoicePitch(handle, pitch); }
        public static void SetVoicePan(ulong handle, float pan) { if (handle != InvalidVoice) AudioSetVoicePan(handle, pan); }
        public static void SetVoicePosition(ulong handle, float x, float y, float z) { if (handle != InvalidVoice) AudioSetVoicePosition(handle, x, y, z); }

        public static void SetVoiceSpatial(ulong handle, float spatialBlend, float minDistance,
            float maxDistance, int rolloffMode, float dopplerLevel, float spread)
        {
            if (handle != InvalidVoice)
                AudioSetVoiceSpatial(handle, spatialBlend, minDistance, maxDistance, rolloffMode, dopplerLevel, spread);
        }

        public static void SetListener(float px, float py, float pz, float fx, float fy, float fz, float ux, float uy, float uz)
            => AudioSetListener(px, py, pz, fx, fy, fz, ux, uy, uz);

        public static bool HasDevice() => AudioHasDevice() != 0;

        public static void GetVoiceStats(out int active, out int stolen, out int maxVoices)
            => AudioGetVoiceStats(out active, out stolen, out maxVoices);
    }
}
