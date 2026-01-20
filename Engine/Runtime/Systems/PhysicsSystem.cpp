#include "PhysicsSystem.h"

namespace vortex::runtime::systems {

	namespace {
		static bool g_initialized{ false };
	}

	bool physics_initialized()
	{
		return g_initialized;
	}

	void initialize_physics()
	{
		g_initialized = true;
	}

	void shutdown_physics()
	{
		g_initialized = false;
	}
}
