#include "AudioSystem.h"

namespace vortex::runtime::systems {

	namespace {
		static bool g_initialized{ false };
		static float g_sim_time{ 0.0f };
	}

	bool audio_initialized()
	{
		return g_initialized;
	}

	void initialize_audio()
	{
		g_initialized = true;
		g_sim_time = 0.0f;
	}

	void shutdown_audio()
	{
		g_initialized = false;
	}

	// Advances audio playback/voices by dt seconds. Called once per game tick
	// from StepRuntime (play mode / standalone player).
	void update_audio(float dt)
	{
		if (!g_initialized) return;
		g_sim_time += dt;
		// TODO(audio): update 3D voices, listener transform, streaming.
	}
}
