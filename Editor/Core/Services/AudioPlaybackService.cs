using System;
using System.Collections.Generic;
using System.IO;
using Editor.Core.Data;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Audio;

namespace Editor.Core.Services
{
    /// <summary>
    /// Bridges AudioSource/AudioListener components to the native voice pool while the
    /// game runs (issue #8). Shared by editor play mode and the standalone GameHost:
    /// BeginPlay starts PlayOnAwake voices, Tick pushes transforms + live inspector
    /// values every frame, EndPlay tears everything down (non-destructive play).
    /// </summary>
    public sealed class AudioPlaybackService
    {
        public static AudioPlaybackService Instance { get; } = new AudioPlaybackService();

        private sealed class SourceBinding
        {
            public AudioSource Source;
            public GameEntity Entity;
            public ulong Handle = VortexAudio.InvalidVoice;
            /// <summary>Auto-managed play intent: true for PlayOnAwake sources until their
            /// one-shot finishes naturally. Script-driven Play/Stop (issue #11) flips this.</summary>
            public bool WantsPlay;
            /// <summary>Earliest Environment.TickCount for the next start attempt. Throttles
            /// restart-after-steal and pool-full retries: an oversubscribed pool would
            /// otherwise steal-ping-pong equal-priority looping voices EVERY frame
            /// (audible per-frame restarts). 0 = no throttle.</summary>
            public int NextStartTick;
        }

        private const int RetryCooldownMs = 500;

        private readonly List<SourceBinding> _bindings = new List<SourceBinding>();
        private readonly HashSet<string> _warnedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Clips that natively failed to decode (or have malformed paths) —
        /// permanent for the session, never retried per frame.</summary>
        private readonly HashSet<string> _failedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private GameEntity _listenerEntity;
        private bool _active;
        private bool _paused;
        private bool _tickFaulted;

        public bool IsActive => _active;

        /// <summary>Collect all AudioSources + the AudioListener of the scene and start
        /// every enabled PlayOnAwake source. Call when play begins (or a scene switches).</summary>
        public void BeginPlay(Scene scene)
        {
            EndPlay(); // defensive: a scene switch mid-play re-enters here
            _active = true;
            _paused = false;
            _tickFaulted = false;

            try
            {
                if (scene?.Entities == null) return;
                bool warnedMultipleListeners = false;
                foreach (var e in scene.Entities) CollectRecursive(e, ref warnedMultipleListeners);

                foreach (var b in _bindings)
                {
                    b.WantsPlay = b.Source.PlayOnAwake;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Audio] BeginPlay failed: " + ex);
            }
            Tick(); // frame-0: starts PlayOnAwake voices + positions the listener
        }

        /// <summary>Per game frame while playing: listener transform, voice start/stop on
        /// enable/disable, live property + position push. Cheap for typical scene sizes.
        /// Never throws — it runs inside render-tick handlers that have no catch of
        /// their own (a malformed clip path must not crash the editor on Play).</summary>
        public void Tick()
        {
            if (!_active || _paused) return;

            try
            {
                UpdateListener();

                foreach (var b in _bindings)
                {
                    // A handle that went stale was either stolen (looping → retry below,
                    // throttled) or finished naturally (one-shot → done).
                    if (b.Handle != VortexAudio.InvalidVoice && !VortexAudio.IsVoiceValid(b.Handle))
                    {
                        b.Handle = VortexAudio.InvalidVoice;
                        if (!b.Source.Loop) b.WantsPlay = false;
                        else b.NextStartTick = Environment.TickCount + RetryCooldownMs;
                    }

                    bool shouldPlay = b.WantsPlay
                        && b.Entity.IsActive
                        && b.Source.IsEnabled
                        && !string.IsNullOrEmpty(b.Source.AudioClipPath);

                    if (shouldPlay && b.Handle == VortexAudio.InvalidVoice)
                    {
                        if (b.NextStartTick == 0 || Environment.TickCount - b.NextStartTick >= 0)
                            StartVoice(b);
                    }
                    else if (!shouldPlay && b.Handle != VortexAudio.InvalidVoice)
                    {
                        VortexAudio.StopVoice(b.Handle);
                        b.Handle = VortexAudio.InvalidVoice;
                        b.NextStartTick = 0;
                    }

                    if (b.Handle != VortexAudio.InvalidVoice)
                    {
                        PushProperties(b);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_tickFaulted)
                {
                    _tickFaulted = true;
                    System.Diagnostics.Debug.WriteLine("[Audio] Tick failed: " + ex);
                }
            }
        }

        /// <summary>Editor play-mode pause: halt all voices in place (mixer thread keeps
        /// running, so without this audio would play on while the game is frozen).</summary>
        public void PauseAll()
        {
            if (!_active || _paused) return;
            _paused = true;
            foreach (var b in _bindings) VortexAudio.PauseVoice(b.Handle);
        }

        public void ResumeAll()
        {
            if (!_active || !_paused) return;
            _paused = false;
            foreach (var b in _bindings) VortexAudio.ResumeVoice(b.Handle);
        }

        /// <summary>Stop every component voice and forget the scene. Play is non-destructive:
        /// nothing of the pre-play state is touched.</summary>
        public void EndPlay()
        {
            foreach (var b in _bindings) VortexAudio.StopVoice(b.Handle);
            _bindings.Clear();
            _listenerEntity = null;
            _active = false;
            _paused = false;
        }

        private void CollectRecursive(GameEntity e, ref bool warnedMultipleListeners)
        {
            if (e == null) return;

            var source = e.GetComponent<AudioSource>();
            if (source != null)
                _bindings.Add(new SourceBinding { Source = source, Entity = e });

            var listener = e.GetComponent<AudioListener>();
            if (listener != null && listener.IsEnabled)
            {
                if (_listenerEntity == null)
                {
                    _listenerEntity = e;
                }
                else if (!warnedMultipleListeners)
                {
                    warnedMultipleListeners = true;
                    System.Diagnostics.Debug.WriteLine(
                        "[Audio] Multiple active AudioListeners — using '" + _listenerEntity.Name + "', ignoring '" + e.Name + "'.");
                }
            }

            if (e.Children != null)
                foreach (var c in e.Children) CollectRecursive(c, ref warnedMultipleListeners);
        }

        private void StartVoice(SourceBinding b)
        {
            var clip = b.Source.AudioClipPath;
            if (_failedPaths.Contains(clip)) { b.WantsPlay = false; return; }

            var path = ResolveClipPath(clip);
            if (path == null) return;

            // Preload separates the two failure modes: undecodable clip = permanent
            // (stop retrying, else it re-probes the decoder + logs every frame),
            // pool full/outranked = transient (retry after the cooldown).
            if (!VortexAudio.PreloadClip(path))
            {
                _failedPaths.Add(clip);
                b.WantsPlay = false;
                System.Diagnostics.Debug.WriteLine("[Audio] clip failed to decode, giving up: '" + clip + "'");
                return;
            }

            b.Handle = VortexAudio.PlayVoice(path,
                b.Source.Mute ? 0f : b.Source.Volume,
                b.Source.Pitch,
                b.Source.StereoPan,
                b.Source.Loop,
                b.Source.Priority);

            if (b.Handle != VortexAudio.InvalidVoice)
            {
                b.NextStartTick = 0;
                PushProperties(b);
            }
            else
            {
                b.NextStartTick = Environment.TickCount + RetryCooldownMs; // pool full — back off
            }
        }

        private void PushProperties(SourceBinding b)
        {
            var s = b.Source;
            VortexAudio.SetVoiceVolume(b.Handle, s.Mute ? 0f : s.Volume);
            VortexAudio.SetVoicePitch(b.Handle, s.Pitch);
            VortexAudio.SetVoicePan(b.Handle, s.StereoPan);
            VortexAudio.SetVoiceSpatial(b.Handle, s.SpatialBlend, s.MinDistance, s.MaxDistance,
                (int)s.RolloffMode, s.DopplerLevel, s.Spread);

            var pos = ReadWorldPosition(b.Entity);
            VortexAudio.SetVoicePosition(b.Handle, pos.X, pos.Y, pos.Z);
        }

        private void UpdateListener()
        {
            // The AudioListener entity drives the ears; without one, fall back to the main
            // camera so 3D audio still behaves sensibly in camera-only scenes.
            Editor.ECS.Components.Transform t = null;
            if (_listenerEntity?.Transform != null && _listenerEntity.IsActive)
                t = _listenerEntity.Transform;
            else
                t = PlayCameraHelper.FindMainCamera(ProjectData.Current?.ActiveScene);
            if (t == null) return;

            var pos = t.LocalPosition;
            // Same yaw/pitch convention as PlayCameraHelper.ApplyPose — ears follow the eyes.
            float pitchDeg = t.LocalRotation.X;
            if (pitchDeg > 89f) pitchDeg = 89f; else if (pitchDeg < -89f) pitchDeg = -89f;
            double yaw = t.LocalRotation.Y * Math.PI / 180.0;
            double pitch = pitchDeg * Math.PI / 180.0;
            float fx = (float)(Math.Sin(yaw) * Math.Cos(pitch));
            float fy = (float)(-Math.Sin(pitch));
            float fz = (float)(Math.Cos(yaw) * Math.Cos(pitch));

            VortexAudio.SetListener(pos.X, pos.Y, pos.Z, fx, fy, fz, 0f, 1f, 0f);
        }

        private ECS.Vector3 ReadWorldPosition(GameEntity e)
        {
            // The C# transform is the reliable source: scripts write it (and sync to
            // engine), and editor play mirrors physics results back into it per frame.
            // Do NOT read back via EntityId here — deserialized entities that never got
            // an engine entity keep the default id 0, and ReadEntityPosition(0) returns
            // entity #0 (typically the camera), which pinned every sound to the
            // listener (measured: zero attenuation). Rigidbody-driven sources in the
            // standalone player can revisit this once physics mirroring exists there.
            return e.Transform != null ? e.Transform.LocalPosition : new ECS.Vector3(0, 0, 0);
        }

        /// <summary>Project-relative clip path → absolute file the native decoder can open.
        /// Editor + loose-file builds read from disk; pak-only shipped builds extract the
        /// entry to a temp cache (until #10/#20 give miniaudio a real VFS data source).
        /// Never throws: net48 Path APIs throw on invalid path chars, and a malformed
        /// serialized clip path must not take down the render tick.</summary>
        private string ResolveClipPath(string clip)
        {
            if (string.IsNullOrEmpty(clip)) return null;

            try
            {
                if (Path.IsPathRooted(clip) && File.Exists(clip)) return clip;

                var root = ProjectData.Current?.Path;
                if (!string.IsNullOrEmpty(root))
                {
                    var full = Path.Combine(root, clip);
                    if (File.Exists(full)) return full;
                }

                if (AssetVfs.TryGetBytes(clip, out var bytes))
                {
                    return ExtractToCache(clip, bytes);
                }
            }
            catch (Exception ex)
            {
                // Malformed path (invalid chars etc.) — permanent, stop probing it.
                _failedPaths.Add(clip);
                System.Diagnostics.Debug.WriteLine("[Audio] bad clip path '" + clip + "': " + ex.Message);
                return null;
            }

            if (_warnedPaths.Add(clip))
                System.Diagnostics.Debug.WriteLine("[Audio] clip not found: '" + clip + "'");
            return null;
        }

        /// <summary>Write pak bytes to the temp cache, keyed by CONTENT (hash + length),
        /// not by path: a path-only key would keep serving v1 audio after a game update
        /// replaced the clip, and would collide between two games that use the same
        /// conventional relative path. Same content dedupes naturally.</summary>
        private static string ExtractToCache(string clip, byte[] bytes)
        {
            var cacheDir = Path.Combine(Path.GetTempPath(), "VortexAudioCache");
            Directory.CreateDirectory(cacheDir);

            string contentKey;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var hash = md5.ComputeHash(bytes);
                contentKey = BitConverter.ToString(hash, 0, 8).Replace("-", "") + "_" + bytes.Length;
            }

            var cached = Path.Combine(cacheDir, contentKey + "_" + Path.GetFileName(clip));
            var existing = new FileInfo(cached);
            if (existing.Exists && existing.Length == bytes.Length) return cached;

            // Write-then-move so a crash mid-write can't leave a truncated file that
            // File.Exists would trust forever.
            var tmp = cached + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(cached)) File.Delete(cached);
            File.Move(tmp, cached);
            return cached;
        }
    }
}
