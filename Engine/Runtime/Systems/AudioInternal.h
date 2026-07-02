#pragma once

// Internal to the audio implementation TUs (AudioEngine.cpp, AudioVoices.cpp).
// Exposes the raw miniaudio engine so sibling audio files can create sounds
// without AudioEngine.h leaking miniaudio types to the rest of the engine.
struct ma_engine;

namespace vortex::runtime::audio {

	// The live miniaudio engine, or nullptr while uninitialized. Valid in both
	// device and silent no-device mode.
	ma_engine* internal_engine();
}
