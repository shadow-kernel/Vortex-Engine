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

        private const string _dllName = "VortexAPI.dll";
        private const CallingConvention _cc = CallingConvention.Cdecl;

        // Paths marshal as UTF-8 (native side widens UTF-8-first with ANSI fallback),
        // so unicode project folders and non-ASCII %TEMP% usernames survive the trip —
        // plain LPStr would best-fit them to '?' and silently kill all audio.
        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int AudioPreloadClip([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int AudioValidateClip([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern ulong AudioPlayVoice([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            float volume, float pitch, float pan, int loop, int priority, int stream);

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

        /// <summary>Starts a voice; returns InvalidVoice when the clip can't be decoded
        /// or every pooled voice outranks this request (priority 0 = most important).
        /// stream = decode on demand (music/long ambience) instead of full pre-decode.</summary>
        public static ulong PlayVoice(string path, float volume, float pitch, float pan, bool loop, int priority, bool stream = false)
        {
            if (string.IsNullOrEmpty(path)) return InvalidVoice;
            return AudioPlayVoice(path, volume, pitch, pan, loop ? 1 : 0, priority, stream ? 1 : 0);
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
