#include "RenderSystem.h"

namespace vortex::runtime::systems {

	namespace {
		static bool g_initialized{ false };
	}

	bool render_initialized()
	{
		return g_initialized;
	}

	void initialize_render()
	{
		g_initialized = true;
	}

	void shutdown_render()
	{
		g_initialized = false;
	}
}
