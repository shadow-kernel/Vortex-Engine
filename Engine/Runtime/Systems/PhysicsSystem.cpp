#include "PhysicsSystem.h"

namespace vortex::runtime::systems {

	namespace {
		static bool g_initialized{ false };
		static float g_sim_time{ 0.0f };
	}

	bool physics_initialized()
	{
		return g_initialized;
	}

	void initialize_physics()
	{
		g_initialized = true;
		g_sim_time = 0.0f;
	}

	void shutdown_physics()
	{
		g_initialized = false;
	}

	// Advances the physics simulation by dt seconds. Called once per game tick
	// from StepRuntime (play mode / standalone player), never from the editor's
	// idle viewport render.
	void update_physics(float dt)
	{
		if (!g_initialized) return;
		g_sim_time += dt;
		// TODO(physics): integrate rigidbodies, apply gravity, resolve collisions.
	}
}
