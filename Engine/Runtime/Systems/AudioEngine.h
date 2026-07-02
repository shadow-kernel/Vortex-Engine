#pragma once

#include "../../Common/CommonHeaders.h"

// Native audio engine core (miniaudio backend). Owns the playback device and the
// decoded-sound cache. AudioSystem drives its lifetime; ResourceManager::load_audio
// feeds the cache. Keep miniaudio types OUT of this header — the implementation TU
// (AudioEngine.cpp) is the only place that includes miniaudio.h.
namespace vortex::runtime::audio {

	// Opens the default output device (WASAPI). If no device is available the engine
	// falls back to a silent no-device mode: every call below stays safe and sound
	// clocks still advance, there is just nothing to hear. Returns true when a real
	// device opened, false for silent mode or failure (check is_initialized()).
	bool initialize();
	void shutdown();

	// Engine object exists (true even in silent no-device mode).
	bool is_initialized();
	// A real output device is producing audio.
	bool has_device();

	// Decode a sound file into the cache (wav/flac/mp3 via miniaudio, ogg via
	// stb_vorbis). Safe to call again for a cached path. Returns false when the file
	// is missing or no decoder accepts it.
	bool preload(const char* path);
	bool is_loaded(const char* path);

	// Registers an in-memory ENCODED audio blob (e.g. a .vpak entry) under a name
	// that preload/voice_play open exactly like a file path. The data is copied and
	// owned by the audio engine until shutdown. Re-registering a name is a no-op.
	bool register_clip_data(const char* name, const void* data, u64 size);
	bool is_registered_clip(const char* name);
	// Cheap decodability probe (header parse only, no full decode) — used to gate
	// STREAMING plays so an undecodable clip can never steal a live voice, and to
	// let the bridge blacklist bad clips instead of retrying forever.
	bool validate_clip(const char* path);

	// Editor support: header-level clip facts for browser tooltips.
	bool clip_info(const char* path, f32* out_duration_seconds, u32* out_sample_rate, u32* out_channels);
	// Editor support: decode the clip once and reduce it to bin_count peak
	// amplitudes (0..1, mono-folded) for waveform thumbnails. Heavy for long
	// files — call from a background thread.
	bool clip_waveform(const char* path, f32* out_peaks, u32 bin_count);
	// Raw bytes of a registered clip (valid until shutdown), or nullptr. Used by
	// the voice layer to stream from memory via a ma_decoder (miniaudio's STREAM
	// flag only streams from files, not from registered encoded data).
	const void* registered_clip_bytes(const char* name, u64* out_size);
	// Release one cached sound / all cached sounds (shutdown implies unload_all).
	void unload_sound(const char* path);
	void unload_all_sounds();

	// Fire-and-forget playback used by tests and editor audition until the voice
	// pool (issue #7) lands. Volume is linear 0..1.
	bool play_one_shot(const char* path, float volume = 1.0f);

	// Drives miniaudio's listener 0 — pushed per frame from the AudioListener
	// entity (editor play mode and standalone player alike).
	void set_listener(f32 px, f32 py, f32 pz, f32 fx, f32 fy, f32 fz, f32 ux, f32 uy, f32 uz);

	// Steam Audio v2 (issue #21) master switch + occlusion geometry, forwarded to the SteamAudio module.
	// Off by default; turning it on lazily initializes Steam Audio (HRTF + occlusion) if phonon.dll is present.
	void steam_set_enabled(bool enabled);
	void steam_set_geometry(const float* verts, u32 vertex_count, const s32* indices, u32 index_count);

	// Short generated sine beep — the audible smoke test that the device works
	// without needing any asset on disk.
	void play_test_beep();

	// Per-tick housekeeping. In silent no-device mode this also pumps the mixer so
	// sound timers keep advancing.
	void update(float dt);
}
