#include "AudioSystem.h"
#include "AudioEngine.h"

namespace vortex::runtime::systems {

	namespace {
		static bool g_initialized{ false };
		static bool g_startup_beep_played{ false };
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
		g_startup_beep_played = false;
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

		// update_audio only ticks while the game simulation runs, so the first tick
		// marks "runtime started in play mode". The beep is the audible smoke test
		// from issue #6; issue #8 replaces it with real AudioSource playback.
		if (!g_startup_beep_played)
		{
			g_startup_beep_played = true;
			audio::play_test_beep();
		}

		audio::update(dt);
	}
}
