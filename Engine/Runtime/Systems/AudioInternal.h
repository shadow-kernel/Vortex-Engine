#pragma once

// Internal to the audio implementation TUs (AudioEngine.cpp, AudioVoices.cpp).
// Exposes the raw miniaudio engine so sibling audio files can create sounds
// without AudioEngine.h leaking miniaudio types to the rest of the engine.
struct ma_engine;

namespace vortex::runtime::audio {

	// The live miniaudio engine, or nullptr while uninitialized. Valid in both
	// device and silent no-device mode.
	ma_engine* internal_engine();

	// Shared audio log: debugger output always, plus the VORTEX_AUDIO_LOG file
	// when that env var is set (automated verification hook).
	void internal_log(const char* fmt, ...);

	// Narrow path -> wide for miniaudio's *_w file APIs. Tries strict UTF-8 first
	// (the C# bridge marshals UTF-8), falls back to the ANSI code page (legacy
	// engine-internal callers). Returns false when conversion fails or overflows.
	bool internal_widen_path(const char* narrow, wchar_t* out, size_t out_chars);
}
