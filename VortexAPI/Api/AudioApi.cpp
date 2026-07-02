#include "../ApiCommon.h"
#include "..\..\Engine\Runtime\Systems\AudioEngine.h"
#include "..\..\Engine\Runtime\Systems\AudioVoices.h"

// Voice-level audio API (issue #7). Handles are opaque u64 values with a
// generation counter — stale handles (stolen/finished voices) are safely
// ignored on every call, so C# never has to worry about lifetime races.

// Decode + cache a clip; 1 = playable, 0 = missing/undecodable. Lets the bridge
// distinguish a permanently bad clip (stop retrying) from a full voice pool.
EDITOR_INTERFACE s32 AudioPreloadClip(const char* path)
{
	return runtime::audio::preload(path) ? 1 : 0;
}

// Header-probe only (no full decode) — the streaming counterpart of AudioPreloadClip.
EDITOR_INTERFACE s32 AudioValidateClip(const char* path)
{
	return runtime::audio::validate_clip(path) ? 1 : 0;
}

// Editor asset browser: duration/format facts for tile tooltips.
EDITOR_INTERFACE s32 AudioGetClipInfo(const char* path, f32* duration_seconds, s32* sample_rate, s32* channels)
{
	f32 duration = 0.0f;
	u32 rate = 0, ch = 0;
	const bool ok = runtime::audio::clip_info(path, &duration, &rate, &ch);
	if (duration_seconds) *duration_seconds = duration;
	if (sample_rate) *sample_rate = (s32)rate;
	if (channels) *channels = (s32)ch;
	return ok ? 1 : 0;
}

// Editor asset browser: per-bin peak amplitudes (0..1) for waveform thumbnails.
// Decodes the whole clip once — call from a background thread.
EDITOR_INTERFACE s32 AudioGetWaveform(const char* path, f32* peaks, s32 bin_count)
{
	if (!peaks || bin_count <= 0) return 0;
	return runtime::audio::clip_waveform(path, peaks, (u32)bin_count) ? 1 : 0;
}

EDITOR_INTERFACE u64 AudioPlayVoice(const char* path, f32 volume, f32 pitch, f32 pan, s32 loop, s32 priority, s32 stream)
{
	runtime::audio::voice_params params{};
	params.volume = volume;
	params.pitch = pitch;
	params.pan = pan;
	params.loop = loop != 0;
	params.priority = priority;
	params.stream = stream != 0;
	return runtime::audio::voice_play(path, params);
}

// Hands a .vpak audio entry to the native engine — the name then plays exactly
// like a file path (both decoded and streaming voices). Data is copied natively.
EDITOR_INTERFACE s32 AudioRegisterClipData(const char* name, const void* data, u64 size)
{
	return runtime::audio::register_clip_data(name, data, size) ? 1 : 0;
}

EDITOR_INTERFACE void AudioStopVoice(u64 handle)
{
	runtime::audio::voice_stop(handle);
}

EDITOR_INTERFACE void AudioPauseVoice(u64 handle)
{
	runtime::audio::voice_pause(handle);
}

EDITOR_INTERFACE void AudioResumeVoice(u64 handle)
{
	runtime::audio::voice_resume(handle);
}

EDITOR_INTERFACE s32 AudioIsVoicePlaying(u64 handle)
{
	return runtime::audio::voice_is_playing(handle) ? 1 : 0;
}

EDITOR_INTERFACE s32 AudioIsVoiceValid(u64 handle)
{
	return runtime::audio::voice_is_valid(handle) ? 1 : 0;
}

EDITOR_INTERFACE void AudioSetVoiceVolume(u64 handle, f32 volume)
{
	runtime::audio::voice_set_volume(handle, volume);
}

EDITOR_INTERFACE void AudioSetVoicePitch(u64 handle, f32 pitch)
{
	runtime::audio::voice_set_pitch(handle, pitch);
}

EDITOR_INTERFACE void AudioSetVoicePan(u64 handle, f32 pan)
{
	runtime::audio::voice_set_pan(handle, pan);
}

EDITOR_INTERFACE void AudioSetVoicePosition(u64 handle, f32 x, f32 y, f32 z)
{
	runtime::audio::voice_set_position(handle, x, y, z);
}

EDITOR_INTERFACE void AudioSetVoiceSpatial(u64 handle, f32 spatial_blend, f32 min_distance,
	f32 max_distance, s32 rolloff_mode, f32 doppler_level, f32 spread)
{
	runtime::audio::voice_spatial spatial{};
	spatial.spatial_blend = spatial_blend;
	spatial.min_distance = min_distance;
	spatial.max_distance = max_distance;
	spatial.rolloff_mode = rolloff_mode;
	spatial.doppler_level = doppler_level;
	spatial.spread = spread;
	runtime::audio::voice_set_spatial(handle, spatial);
}

EDITOR_INTERFACE void AudioSetListener(f32 px, f32 py, f32 pz, f32 fx, f32 fy, f32 fz, f32 ux, f32 uy, f32 uz)
{
	runtime::audio::set_listener(px, py, pz, fx, fy, fz, ux, uy, uz);
}

EDITOR_INTERFACE s32 AudioHasDevice()
{
	return runtime::audio::has_device() ? 1 : 0;
}

// active/stolen/max in one call — the mixer window meters poll this per frame.
EDITOR_INTERFACE void AudioGetVoiceStats(s32* active, s32* stolen, s32* max_voices)
{
	if (active) *active = (s32)runtime::audio::voices_active_count();
	if (stolen) *stolen = (s32)runtime::audio::voices_stolen_count();
	if (max_voices) *max_voices = (s32)runtime::audio::voices_max_count();
}
