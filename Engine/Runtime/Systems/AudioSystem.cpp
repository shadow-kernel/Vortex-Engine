#include "AudioSystem.h"

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
		g_initialized = true;
	}

	void shutdown_audio()
	{
		g_initialized = false;
	}
}
