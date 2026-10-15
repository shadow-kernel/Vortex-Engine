#pragma once

// Internal to the audio implementation TUs (AudioEngine.cpp, AudioVoices.cpp).
// Exposes the raw miniaudio engine so sibling audio files can create sounds
// without AudioEngine.h leaking miniaudio types to the rest of the engine.
struct ma_engine;
struct ma_decoder;

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

	// Listener position in GAME space (left-handed, +z forward). miniaudio itself
	// stores the right-handed mirror (z negated at the boundary) — never mix the
	// two spaces in distance math.
	void internal_listener_position(float out[3]);

	// A registered pak clip as a decoder over its bytes — decoded on demand, nothing loaded up front (#465 review). An MP3
	// gets a seek table: without one every seek, and every length query, decodes from the start of the file (measured on
	// 3 minutes: 38 ms per seek to 2:30, 0.02 ms with the table, which costs ~0.4 ms to build). Vorbis opens in pull mode,
	// so its length is known. f32_engine_rate: f32 at the engine's rate (music stems, locked to each other); otherwise the
	// clip's own format. False when the name is not registered or no decoder accepts it.
	bool internal_open_pak_decoder(const char* name, bool f32_engine_rate, ma_decoder* out);

	// The clip is an MP3 (by its extension) — the format whose length and seeks are expensive without a seek table.
	bool internal_is_mp3(const char* path);

	// A clip's length in frames at the engine rate, through a probe decoder in pull mode — for a file stream of an OGG,
	// which cannot tell its own (miniaudio streams Vorbis in push mode: length 0, and its cursor never wraps at the loop).
	// Reads the headers and the last page, under a millisecond. 0 when unknown.
	u64 internal_probe_length(const char* path);
}
