#include "AudioSystem.h"
#include "AudioEngine.h"

namespace vortex::runtime::systems {

	namespace {
		static bool g_initialized{ false };
	}

	bool audio_initialized()
	{
		return g_initialized;
	}

	void initialize_audio()
	{
		if (g_initialized) return;
		audio::initialize();
		g_initialized = true;
	}

	void shutdown_audio()
	{
		if (!g_initialized) return;
		audio::shutdown();
		g_initialized = false;
	}

	// Advances audio playback/voices by dt seconds. Called once per game tick
	// from StepRuntime (play mode / standalone player).
	void update_audio(float dt)
	{
		if (!g_initialized) return;
		audio::update(dt);
		// (The issue-#6 startup test beep is gone: AudioSource playback is the real
		// audible path now; play_test_beep() stays available for diagnostics.)
	}
}
