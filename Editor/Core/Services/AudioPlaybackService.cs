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
            /// <summary>Script called Pause() — ResumeAll (editor un-pause) must NOT
            /// override a deliberate script pause.</summary>
            public bool ScriptPaused;
            /// <summary>Container roll of the CURRENT voice — the per-frame property
            /// push must keep multiplying these in or the variation gets stomped.</summary>
            public float RolledVolumeScale = 1f;
            public float RolledPitchScale = 1f;
            // Pre-play values of everything scripts can write through the handle —
            // play must stay non-destructive (mirrors the transform snapshot).
            public float OrigVolume;
            public float OrigPitch;
            public bool OrigLoop;
            public string OrigClip;
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

            _musicVolume = 1f;
            _musicCurrentClip = null;

            // Project mixer state (bus volumes/mutes + duck rules) — identical in
            // editor play mode and the shipped game.
            try { AudioMixerConfig.Load(ProjectData.Current?.Path).Apply(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Audio] mixer config apply failed: " + ex.Message); }

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
            // No frame-0 Tick here: PlayOnAwake voices start on the FIRST game tick,
            // which runs after the scripts' Start() — so a Start() that calls Stop()
            // wins, and audio can't blare during the script-compile hitch.
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
                UpdateReverbZones(_listenerPos);
                TickMusic();

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

                // Prune finished one-shots so the pause/stop lists stay small.
                for (int i = _oneShots.Count - 1; i >= 0; i--)
                    if (!VortexAudio.IsVoiceValid(_oneShots[i])) _oneShots.RemoveAt(i);
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
            VortexAudio.PauseVoice(_musicCurrent);
            VortexAudio.PauseVoice(_musicPrevious);
            foreach (var h in _oneShots) VortexAudio.PauseVoice(h);
        }

        public void ResumeAll()
        {
            if (!_active || !_paused) return;
            _paused = false;
            foreach (var b in _bindings)
            {
                if (!b.ScriptPaused) VortexAudio.ResumeVoice(b.Handle); // a script's Pause() survives editor un-pause
            }
            VortexAudio.ResumeVoice(_musicCurrent);
            VortexAudio.ResumeVoice(_musicPrevious);
            foreach (var h in _oneShots) VortexAudio.ResumeVoice(h);
        }

        /// <summary>Stop every component voice and forget the scene. Play is non-destructive:
        /// nothing of the pre-play state is touched.</summary>
        public void EndPlay()
        {
            foreach (var b in _bindings)
            {
                VortexAudio.StopVoice(b.Handle);
                RestoreBinding(b); // play is non-destructive — undo script writes
            }
            _bindings.Clear();
            _listenerEntity = null;
            _active = false;
            _paused = false;
            VortexAudio.StopVoice(_musicCurrent);
            VortexAudio.StopVoice(_musicPrevious);
            _musicCurrent = VortexAudio.InvalidVoice;
            _musicPrevious = VortexAudio.InvalidVoice;
            _musicCurrentClip = null;
            foreach (var h in _oneShots) VortexAudio.StopVoice(h);
            _oneShots.Clear();
            _zones.Clear();
            _zoneWeight = 0f;
            _lastDecay = _lastWet = _lastPredelay = -1f;
            VortexAudio.SetReverbParams(1f, 0f, 0f); // tail off outside play
        }

        private static void SnapshotBinding(SourceBinding b)
        {
            b.OrigVolume = b.Source.Volume;
            b.OrigPitch = b.Source.Pitch;
            b.OrigLoop = b.Source.Loop;
            b.OrigClip = b.Source.AudioClipPath;
        }

        private static void RestoreBinding(SourceBinding b)
        {
            try
            {
                b.Source.Volume = b.OrigVolume;
                b.Source.Pitch = b.OrigPitch;
                b.Source.Loop = b.OrigLoop;
                b.Source.AudioClipPath = b.OrigClip;
            }
            catch { /* component may be mid-teardown on scene switch */ }
        }

        // ---- script surface (Vortex.Audio, issue #11) --------------------------------

        private SourceBinding FindBinding(AudioSource source)
        {
            foreach (var b in _bindings)
                if (ReferenceEquals(b.Source, source)) return b;
            return null;
        }

        /// <summary>Script Play(): (re)start the source's voice from the beginning,
        /// regardless of PlayOnAwake. Sources ADDED at runtime by scripts get a
        /// binding lazily so they are controllable too.</summary>
        public void ScriptPlay(AudioSource source)
        {
            if (!_active || source == null) return;
            var b = FindBinding(source);
            if (b == null)
            {
                if (source.Entity == null) return;
                b = new SourceBinding { Source = source, Entity = source.Entity };
                SnapshotBinding(b);
                _bindings.Add(b);
            }
            if (b.Handle != VortexAudio.InvalidVoice)
            {
                VortexAudio.StopVoice(b.Handle);
                b.Handle = VortexAudio.InvalidVoice;
            }
            b.WantsPlay = true;
            b.ScriptPaused = false;
            b.NextStartTick = 0;
            StartVoice(b); // immediate — stingers must not wait a frame
        }

        public void ScriptStop(AudioSource source)
        {
            var b = FindBinding(source);
            if (b == null) return;
            b.WantsPlay = false;
            if (b.Handle != VortexAudio.InvalidVoice)
            {
                VortexAudio.StopVoice(b.Handle);
                b.Handle = VortexAudio.InvalidVoice;
            }
        }

        public void ScriptPause(AudioSource source)
        {
            var b = FindBinding(source);
            if (b == null) return;
            b.ScriptPaused = true;
            VortexAudio.PauseVoice(b.Handle);
        }

        public void ScriptResume(AudioSource source)
        {
            var b = FindBinding(source);
            if (b == null) return;
            b.ScriptPaused = false;
            if (!_paused) VortexAudio.ResumeVoice(b.Handle);
        }

        public bool ScriptIsPlaying(AudioSource source)
        {
            var b = FindBinding(source);
            return b != null && VortexAudio.IsVoicePlaying(b.Handle);
        }

        /// <summary>FadeIn: (re)start the source silent and ramp the envelope to 1.</summary>
        public void ScriptFadeIn(AudioSource source, float seconds)
        {
            ScriptPlay(source);
            var b = FindBinding(source);
            if (b == null || b.Handle == VortexAudio.InvalidVoice) return;
            VortexAudio.FadeVoice(b.Handle, 0f, 0f);          // snap envelope to 0
            VortexAudio.FadeVoice(b.Handle, 1f, seconds);     // glide up
        }

        /// <summary>FadeOut: ramp to silence, then stop and free the voice natively.</summary>
        public void ScriptFadeOut(AudioSource source, float seconds)
        {
            var b = FindBinding(source);
            if (b == null || b.Handle == VortexAudio.InvalidVoice) return;
            b.WantsPlay = false; // the bridge must not restart it while it fades away
            VortexAudio.FadeVoice(b.Handle, 0f, seconds, stopWhenDone: true);
            b.Handle = VortexAudio.InvalidVoice; // native reaps after the fade
        }

        /// <summary>FadeTo: glide the envelope to a live target (ducks/swells) —
        /// composed ON TOP of the source volume, retargets smoothly mid-fade.</summary>
        public void ScriptFadeTo(AudioSource source, float target, float seconds)
        {
            var b = FindBinding(source);
            if (b != null) VortexAudio.FadeVoice(b.Handle, target, seconds);
        }

        /// <summary>In-flight one-shot handles: tracked so Pause/Stop/scene switches
        /// silence them with everything else, pruned as they finish. Scripts still
        /// never see a handle.</summary>
        private readonly List<ulong> _oneShots = new List<ulong>();

        /// <summary>Fire-and-forget positional one-shot — no entity needed. The voice
        /// auto-returns to the pool when finished; no handle leaks to scripts.</summary>
        public void PlayOneShot(string clip, float x, float y, float z, float volume, float pitch)
        {
            var h = StartOneShot(clip, volume, pitch);
            if (h != VortexAudio.InvalidVoice)
            {
                VortexAudio.SetVoiceSpatial(h, 1f, 1f, 500f, 0, 1f, 0f);
                VortexAudio.SetVoicePosition(h, x, y, z);
            }
        }

        public void PlayOneShot2D(string clip, float volume, float pitch)
        {
            StartOneShot(clip, volume, pitch);
        }

        private ulong StartOneShot(string clip, float volume, float pitch)
        {
            if (!_active || string.IsNullOrEmpty(clip) || _failedPaths.Contains(clip))
                return VortexAudio.InvalidVoice;

            // Containers roll a fresh clip + variation per shot.
            if (Core.Audio.SoundContainerService.IsContainerPath(clip))
            {
                var containerAbs = ResolveContainerFile(clip);
                if (containerAbs == null || !Core.Audio.SoundContainerService.Resolve(containerAbs, out var rolled))
                    return VortexAudio.InvalidVoice;
                clip = rolled.ClipPath;
                volume *= rolled.VolumeScale;
                pitch *= rolled.PitchScale;
                if (_failedPaths.Contains(clip)) return VortexAudio.InvalidVoice;
            }

            var path = ResolveClipPath(clip);
            if (path == null) return VortexAudio.InvalidVoice;
            // Same permanent-failure discipline as component voices: an undecodable
            // clip in a per-frame footstep script must not re-probe + log every call.
            if (!VortexAudio.PreloadClip(path))
            {
                _failedPaths.Add(clip);
                return VortexAudio.InvalidVoice;
            }
            var h = VortexAudio.PlayVoice(path, volume, pitch, 0f, false, 128);
            if (h != VortexAudio.InvalidVoice) _oneShots.Add(h);
            return h;
        }

        // ---- music channel: streamed looping track at priority 0; fades are the
        // NATIVE sample-accurate envelopes from #17 (no per-frame C# ramps) ----------

        private ulong _musicCurrent = VortexAudio.InvalidVoice;
        private ulong _musicPrevious = VortexAudio.InvalidVoice;
        private string _musicCurrentClip;     // same-clip guard: Play/CrossFade per frame must not restart
        private float _musicVolume = 1f;      // user target volume

        public float MusicVolume
        {
            get => _musicVolume;
            set
            {
                _musicVolume = value < 0f ? 0f : (value > 1f ? 1f : value);
                VortexAudio.SetVoiceVolume(_musicCurrent, _musicVolume); // envelope multiplies on top
            }
        }

        public bool MusicIsPlaying => VortexAudio.IsVoicePlaying(_musicCurrent);

        /// <summary>Start a music track (streamed, looping, priority 0 — never stolen),
        /// fading in over fadeInSeconds. Replaces the current track with a quick fade.
        /// Calling it again with the SAME clip while it plays is a no-op (scripts often
        /// call this per frame).</summary>
        public void MusicPlay(string clip, float fadeInSeconds, bool loop = true)
        {
            StartMusic(clip, fadeInSeconds, 0.25f, loop);
        }

        /// <summary>Fade the current music out while the new clip fades in, overlapping.
        /// Same-clip calls while playing are no-ops.</summary>
        public void MusicCrossFade(string clip, float seconds)
        {
            StartMusic(clip, seconds, seconds, loop: true);
        }

        private void StartMusic(string clip, float fadeInSeconds, float fadeOutSeconds, bool loop)
        {
            if (!_active || string.IsNullOrEmpty(clip)) return;
            if (string.Equals(clip, _musicCurrentClip, StringComparison.OrdinalIgnoreCase) && MusicIsPlaying)
                return; // already the current track — per-frame calls must not restart the stream

            var path = ResolveClipPath(clip);
            if (path == null) return;

            // Start the NEW track first — if it can't start (undecodable file), the
            // currently playing music keeps playing instead of fading into silence.
            var h = VortexAudio.PlayVoice(path, _musicVolume, 1f, 0f, loop, 0, stream: true, bus: VortexAudio.BusMusic);
            if (h == VortexAudio.InvalidVoice)
            {
                _failedPaths.Add(clip);
                System.Diagnostics.Debug.WriteLine("[Audio] music failed to start: '" + clip + "'");
                return;
            }

            SwapMusicToPrevious(fadeOutSeconds);
            _musicCurrent = h;
            _musicCurrentClip = clip;
            if (fadeInSeconds > 0f)
            {
                VortexAudio.FadeVoice(h, 0f, 0f);            // envelope starts silent
                VortexAudio.FadeVoice(h, 1f, fadeInSeconds); // native sample-accurate ramp
            }
        }

        public void MusicStop(float fadeOutSeconds = 0f)
        {
            _musicCurrentClip = null;
            if (fadeOutSeconds <= 0f)
            {
                // Immediate means IMMEDIATE — including an outgoing crossfade track.
                VortexAudio.StopVoice(_musicCurrent);
                VortexAudio.StopVoice(_musicPrevious);
                _musicCurrent = VortexAudio.InvalidVoice;
                _musicPrevious = VortexAudio.InvalidVoice;
                return;
            }
            SwapMusicToPrevious(fadeOutSeconds);
        }

        private void SwapMusicToPrevious(float fadeOutSeconds)
        {
            if (_musicPrevious != VortexAudio.InvalidVoice)
            {
                VortexAudio.StopVoice(_musicPrevious); // only one outgoing track at a time
                _musicPrevious = VortexAudio.InvalidVoice;
            }
            if (_musicCurrent != VortexAudio.InvalidVoice)
            {
                _musicPrevious = _musicCurrent;
                _musicCurrent = VortexAudio.InvalidVoice;
                // Native fade to silence, voice self-releases when done.
                VortexAudio.FadeVoice(_musicPrevious, 0f, fadeOutSeconds <= 0f ? 0.05f : fadeOutSeconds, stopWhenDone: true);
            }
        }

        private void TickMusic()
        {
            // Reap the fading-out handle once the native envelope released it.
            if (_musicPrevious != VortexAudio.InvalidVoice && !VortexAudio.IsVoiceValid(_musicPrevious))
                _musicPrevious = VortexAudio.InvalidVoice;
        }

        private void CollectRecursive(GameEntity e, ref bool warnedMultipleListeners)
        {
            if (e == null) return;

            var source = e.GetComponent<AudioSource>();
            if (source != null)
            {
                var b = new SourceBinding { Source = source, Entity = e };
                SnapshotBinding(b);
                _bindings.Add(b);
            }

            var zone = e.GetComponent<ReverbZone>();
            if (zone != null)
                _zones.Add(new ZoneBinding { Zone = zone, Entity = e });

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

            // Sound containers (.vsndc): every (re)start rolls a fresh clip + pitch/
            // volume variation — footsteps stop sounding like a metronome.
            float volumeScale = 1f, pitchScale = 1f;
            if (Core.Audio.SoundContainerService.IsContainerPath(clip))
            {
                var containerAbs = ResolveContainerFile(clip);
                if (containerAbs == null || !Core.Audio.SoundContainerService.Resolve(containerAbs, out var rolled))
                {
                    if (_warnedPaths.Add(clip))
                        System.Diagnostics.Debug.WriteLine("[Audio] container empty/missing: '" + clip + "'");
                    return;
                }
                clip = rolled.ClipPath;
                volumeScale = rolled.VolumeScale;
                pitchScale = rolled.PitchScale;
                if (_failedPaths.Contains(clip)) return;
            }

            var path = ResolveClipPath(clip);
            if (path == null) return;

            // Preload/validate separates the two failure modes: undecodable clip =
            // permanent (stop retrying, else it re-probes the decoder + logs every
            // frame), pool full/outranked = transient (retry after the cooldown).
            // Streaming clips get the cheap header-probe instead of a full decode.
            bool clipOk = b.Source.Streaming
                ? VortexAudio.ValidateClip(path)
                : VortexAudio.PreloadClip(path);
            if (!clipOk)
            {
                _failedPaths.Add(clip);
                b.WantsPlay = false;
                System.Diagnostics.Debug.WriteLine("[Audio] clip failed to decode, giving up: '" + clip + "'");
                return;
            }

            b.Handle = VortexAudio.PlayVoice(path,
                (b.Source.Mute ? 0f : b.Source.Volume) * volumeScale,
                b.Source.Pitch * pitchScale,
                b.Source.StereoPan,
                b.Source.Loop,
                b.Source.Priority,
                b.Source.Streaming,
                b.Source.OutputBus);
            b.RolledVolumeScale = volumeScale;
            b.RolledPitchScale = pitchScale;

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
            VortexAudio.SetVoiceVolume(b.Handle, (s.Mute ? 0f : s.Volume) * b.RolledVolumeScale);
            VortexAudio.SetVoicePitch(b.Handle, s.Pitch * b.RolledPitchScale);
            VortexAudio.SetVoicePan(b.Handle, s.StereoPan);
            VortexAudio.SetVoiceSpatial(b.Handle, s.SpatialBlend, s.MinDistance, s.MaxDistance,
                (int)s.RolloffMode, s.DopplerLevel, s.Spread);
            // Per-source reverb feed = the source's own mix x how deep the LISTENER
            // sits in reverb zones this frame (issue #15).
            VortexAudio.SetVoiceReverbSend(b.Handle, s.ReverbZoneMix * _zoneWeight);

            var pos = ReadWorldPosition(b.Entity);
            VortexAudio.SetVoicePosition(b.Handle, pos.X, pos.Y, pos.Z);
        }

        // ---- reverb zones (issue #15) --------------------------------------------

        private sealed class ZoneBinding
        {
            public ReverbZone Zone;
            public GameEntity Entity;
        }

        private readonly List<ZoneBinding> _zones = new List<ZoneBinding>();
        private float _zoneWeight;   // blended listener weight 0..1 this frame
        private float _lastDecay = -1f, _lastWet = -1f, _lastPredelay = -1f;

        /// <summary>Computes the listener's blended reverb this frame: inside a zone =
        /// full weight, within the falloff shell = smooth 0..1, overlapping zones
        /// blend by weight. Pushes the blended parameters natively when they change.</summary>
        private void UpdateReverbZones(ECS.Vector3 listenerPos)
        {
            float totalWeight = 0f;
            float decay = 0f, wet = 0f, predelay = 0f;

            foreach (var zb in _zones)
            {
                if (zb.Zone == null || !zb.Zone.IsEnabled || zb.Entity == null || !zb.Entity.IsActive) continue;
                var center = zb.Entity.Transform != null ? zb.Entity.Transform.LocalPosition : new ECS.Vector3(0, 0, 0);
                float w = ZoneWeight(zb.Zone, center, listenerPos);
                if (w <= 0f) continue;
                totalWeight += w;
                decay += zb.Zone.DecayTime * w;
                wet += zb.Zone.WetLevel * w;
                predelay += zb.Zone.PreDelayMs * w;
            }

            if (totalWeight > 0f)
            {
                decay /= totalWeight;
                wet /= totalWeight;
                predelay /= totalWeight;
            }
            _zoneWeight = totalWeight > 1f ? 1f : totalWeight;

            // The effective tail loudness is zone wet x listener depth.
            float effectiveWet = wet * _zoneWeight;
            if (Math.Abs(decay - _lastDecay) > 0.01f || Math.Abs(effectiveWet - _lastWet) > 0.005f
                || Math.Abs(predelay - _lastPredelay) > 0.5f)
            {
                _lastDecay = decay;
                _lastWet = effectiveWet;
                _lastPredelay = predelay;
                VortexAudio.SetReverbParams(decay <= 0f ? 1f : decay, effectiveWet, predelay);
            }
        }

        /// <summary>1 inside the shape, easing to 0 at boundary + falloff.</summary>
        private static float ZoneWeight(ReverbZone zone, ECS.Vector3 center, ECS.Vector3 p)
        {
            float outside;
            if (zone.Shape == (int)ReverbZoneShape.Box)
            {
                var ex = zone.BoxExtents;
                float dx = Math.Abs(p.X - center.X) - Math.Max(0.01f, ex.X);
                float dy = Math.Abs(p.Y - center.Y) - Math.Max(0.01f, ex.Y);
                float dz = Math.Abs(p.Z - center.Z) - Math.Max(0.01f, ex.Z);
                outside = Math.Max(dx, Math.Max(dy, dz)); // <= 0 means inside
            }
            else
            {
                float ddx = p.X - center.X, ddy = p.Y - center.Y, ddz = p.Z - center.Z;
                outside = (float)Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz) - Math.Max(0.01f, zone.Radius);
            }

            if (outside <= 0f) return 1f;
            float falloff = Math.Max(0.01f, zone.Falloff);
            if (outside >= falloff) return 0f;
            float t = 1f - outside / falloff;
            return t * t * (3f - 2f * t); // smoothstep — no click at the door
        }

        private ECS.Vector3 _listenerPos;

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
            _listenerPos = pos;
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

        /// <summary>Clips already handed to the native engine as in-memory blobs.</summary>
        private readonly HashSet<string> _registeredPakClips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Container file (.vsndc) → absolute path: loose file in the project,
        /// or extracted from the mounted pak (text asset, tiny).</summary>
        private string ResolveContainerFile(string containerRel)
        {
            try
            {
                if (Path.IsPathRooted(containerRel) && File.Exists(containerRel)) return containerRel;
                var root = ProjectData.Current?.Path;
                if (!string.IsNullOrEmpty(root))
                {
                    var full = Path.Combine(root, containerRel);
                    if (File.Exists(full)) return full;
                }
                if (AssetVfs.TryGetBytes(containerRel, out var bytes))
                {
                    var tmp = Path.Combine(Path.GetTempPath(), "VortexContainers",
                        (uint)containerRel.ToLowerInvariant().GetHashCode() + "_" + bytes.Length + "_" + Path.GetFileName(containerRel));
                    if (!File.Exists(tmp))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(tmp));
                        File.WriteAllBytes(tmp, bytes);
                    }
                    return tmp;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Audio] container resolve failed: " + ex.Message);
            }
            return null;
        }

        /// <summary>Project-relative clip path → something the native decoder can open.
        /// Editor + loose-file builds resolve to a disk path; pak-only shipped builds
        /// register the entry's bytes with miniaudio's resource manager and return the
        /// clip name itself (works for decoded AND streaming voices — no temp files).
        /// Never throws: net48 Path APIs throw on invalid path chars, and a malformed
        /// serialized clip path must not take down the render tick.</summary>
        private string ResolveClipPath(string clip)
        {
            if (string.IsNullOrEmpty(clip)) return null;

            try
            {
                if (_registeredPakClips.Contains(clip)) return clip;

                if (Path.IsPathRooted(clip) && File.Exists(clip)) return clip;

                var root = ProjectData.Current?.Path;
                if (!string.IsNullOrEmpty(root))
                {
                    var full = Path.Combine(root, clip);
                    if (File.Exists(full)) return full;
                }

                if (AssetVfs.TryGetBytes(clip, out var bytes))
                {
                    if (VortexAudio.RegisterClipData(clip, bytes))
                    {
                        _registeredPakClips.Add(clip);
                        return clip;
                    }
                    _failedPaths.Add(clip);
                    return null;
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
    }
}
