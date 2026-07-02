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
	// Release one cached sound / all cached sounds (shutdown implies unload_all).
	void unload_sound(const char* path);
	void unload_all_sounds();

	// Fire-and-forget playback used by tests and editor audition until the voice
	// pool (issue #7) lands. Volume is linear 0..1.
	bool play_one_shot(const char* path, float volume = 1.0f);

	// Short generated sine beep — the audible smoke test that the device works
	// without needing any asset on disk.
	void play_test_beep();

	// Per-tick housekeeping. In silent no-device mode this also pumps the mixer so
	// sound timers keep advancing.
	void update(float dt);
}
