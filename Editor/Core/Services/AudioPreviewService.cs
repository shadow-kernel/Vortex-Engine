using System;
using System.IO;
using Editor.Core.Data;
using Editor.DllWrapper;
using Editor.ECS.Components.Audio;

namespace Editor.Core.Services
{
    /// <summary>
    /// Edit-mode audio preview (issue #19): plays ONE AudioSource without entering
    /// play mode. Applies the component's live inspector values every editor frame;
    /// optional "listen from camera" mode spatializes against the editor camera.
    /// Fully separate from the play-mode bridge — entering play stops the preview.
    /// </summary>
    public sealed class AudioPreviewService
    {
        public static AudioPreviewService Instance { get; } = new AudioPreviewService();

        private ulong _voice = VortexAudio.InvalidVoice;
        private AudioSource _component;
        private bool _spatial;
        private float _rolledVolume = 1f, _rolledPitch = 1f;

        /// <summary>The component currently previewing (null = idle) — inspectors bind
        /// their playing indicator to this.</summary>
        public AudioSource Current => VortexAudio.IsVoiceValid(_voice) ? _component : null;

        public bool IsPreviewing(AudioSource component)
            => component != null && ReferenceEquals(Current, component);

        /// <summary>Preview a source with its current settings. Only one preview at a
        /// time — starting a new one stops the previous.</summary>
        public void Start(AudioSource component, bool listenFromCamera)
        {
            Stop();
            if (component == null || string.IsNullOrEmpty(component.AudioClipPath)) return;

            try
            {
                var root = ProjectData.Current?.Path;
                if (string.IsNullOrEmpty(root)) return;

                var clip = component.AudioClipPath;
                _rolledVolume = 1f; _rolledPitch = 1f;
                if (Core.Audio.SoundContainerService.IsContainerPath(clip))
                {
                    var containerAbs = Path.IsPathRooted(clip) ? clip : Path.Combine(root, clip);
                    if (!Core.Audio.SoundContainerService.Resolve(containerAbs, out var rolled)) return;
                    clip = rolled.ClipPath;
                    _rolledVolume = rolled.VolumeScale;
                    _rolledPitch = rolled.PitchScale;
                }

                var full = Path.IsPathRooted(clip) ? clip : Path.Combine(root, clip);
                if (!File.Exists(full)) return;

                _component = component;
                _spatial = listenFromCamera && component.SpatialBlend > 0f;
                // Priority 0 (never stolen), streamed (no UI-thread decode), bus honored.
                _voice = VortexAudio.PlayVoice(full,
                    (component.Mute ? 0f : component.Volume) * _rolledVolume,
                    component.Pitch * _rolledPitch,
                    component.StereoPan,
                    component.Loop,
                    priority: 0,
                    stream: true,
                    bus: component.OutputBus);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AudioPreview] start failed: " + ex.Message);
            }
        }

        public void Stop()
        {
            if (_voice != VortexAudio.InvalidVoice)
            {
                VortexAudio.StopVoice(_voice);
                _voice = VortexAudio.InvalidVoice;
            }
            _component = null;
        }

        /// <summary>Per editor frame (viewport render tick): live inspector values, and
        /// in camera mode the source position + editor-camera listener — dragging the
        /// entity or flying the camera changes what you hear immediately.</summary>
        public void Tick(float camX, float camY, float camZ, float camYawDeg, float camPitchDeg)
        {
            if (_voice == VortexAudio.InvalidVoice) return;
            if (!VortexAudio.IsVoiceValid(_voice)) { Stop(); return; }
            var c = _component;
            if (c == null) { Stop(); return; }

            try
            {
                VortexAudio.SetVoiceVolume(_voice, (c.Mute ? 0f : c.Volume) * _rolledVolume);
                VortexAudio.SetVoicePitch(_voice, c.Pitch * _rolledPitch);
                VortexAudio.SetVoicePan(_voice, c.StereoPan);

                if (_spatial)
                {
                    VortexAudio.SetVoiceSpatial(_voice, c.SpatialBlend, c.MinDistance, c.MaxDistance,
                        (int)c.RolloffMode, 0f /* no doppler while hand-dragging */, c.Spread);
                    var pos = c.Entity?.Transform?.LocalPosition ?? new ECS.Vector3(0, 0, 0);
                    VortexAudio.SetVoicePosition(_voice, pos.X, pos.Y, pos.Z);

                    // Editor camera = the ears (same yaw/pitch convention as the viewport).
                    float clampedPitch = camPitchDeg > 89f ? 89f : (camPitchDeg < -89f ? -89f : camPitchDeg);
                    double yaw = camYawDeg * Math.PI / 180.0;
                    double pitch = clampedPitch * Math.PI / 180.0;
                    VortexAudio.SetListener(camX, camY, camZ,
                        (float)(Math.Sin(yaw) * Math.Cos(pitch)),
                        (float)(-Math.Sin(pitch)),
                        (float)(Math.Cos(yaw) * Math.Cos(pitch)),
                        0f, 1f, 0f);
                }
                else
                {
                    VortexAudio.SetVoiceSpatial(_voice, 0f, 1f, 500f, 0, 0f, 0f); // flat 2D preview
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AudioPreview] tick failed: " + ex.Message);
                Stop();
            }
        }
    }
}
