#include "../ApiCommon.h"

EDITOR_INTERFACE void InitializeRuntime()
{
	runtime::resource_manager::initialize();
	runtime::prefab_service::initialize();
	runtime::systems::initialize_render();
	runtime::systems::initialize_physics();
	physics::init();   // Physics v2 (Jolt): idempotent, the managed PhysicsService may call PhysicsInit() again
	runtime::systems::initialize_audio();
}

EDITOR_INTERFACE void ShutdownRuntime()
{
	runtime::systems::shutdown_audio();
	physics::shutdown();
	runtime::systems::shutdown_physics();
	runtime::systems::shutdown_render();
	runtime::prefab_service::shutdown();
	runtime::resource_manager::shutdown();
}

// Advances the whole game simulation by dt seconds (one game tick).
// Call this once per frame while in play mode / from the standalone player,
// before submitting render items. The editor's idle viewport does NOT call it,
// which is exactly why entering play mode "comes alive" and exiting it freezes.
// NOTE: the Jolt world (Physics v2) is NOT stepped here — the managed side drives PhysicsStep with its
// own fixed-step accumulator so script callbacks and contact events line up with the game loop.
namespace
{
	// Fixed-timestep game clock: the simulation always advances in stable 1/60 s steps regardless of
	// render frame rate (deterministic physics), with an accumulator for leftover time. g_game_time is
	// the elapsed in-game seconds since the last ResetGameTime (Play start).
	constexpr float k_fixed_dt = 1.0f / 60.0f;
	float g_time_accumulator = 0.0f;
	float g_game_time = 0.0f;
}

namespace
{
	// game_dt seconds of game time into fixed steps of the fallback physics and the game clock, at most max_steps
	void step_simulation(float game_dt, int max_steps)
	{
		g_time_accumulator += game_dt;
		int steps = 0;
		while (g_time_accumulator >= k_fixed_dt && steps < max_steps) // cap steps to avoid a spiral of death
		{
			runtime::systems::update_physics(k_fixed_dt);
			g_game_time += k_fixed_dt;
			g_time_accumulator -= k_fixed_dt;
			++steps;
		}
	}
}

EDITOR_INTERFACE void StepRuntime(float dt)
{
	if (dt < 0.0f) dt = 0.0f;
	if (dt > 0.25f) dt = 0.25f; // clamp huge spikes (e.g. after a breakpoint)
	step_simulation(dt, 8);
	runtime::systems::update_audio(dt);
}

// One game tick with the game's time scale (#425): dt real seconds; the fallback physics and the game clock advance by
// dt x time_scale (0 = paused, 0..100), the audio by the real seconds. Fast-forward may take more steps per tick.
EDITOR_INTERFACE void StepRuntimeScaled(float dt, float time_scale)
{
	if (!(dt >= 0.0f)) dt = 0.0f;
	if (dt > 0.25f) dt = 0.25f; // clamp huge spikes (e.g. after a breakpoint)
	if (!(time_scale >= 0.0f)) time_scale = 1.0f;   // NaN or negative: normal speed
	if (time_scale > 100.0f) time_scale = 100.0f;
	const int max_steps = time_scale > 1.0f ? (time_scale >= 4.0f ? 32 : static_cast<int>(8.0f * time_scale + 0.999f)) : 8;
	step_simulation(dt * time_scale, max_steps);
	runtime::systems::update_audio(dt);
}

// Elapsed in-game seconds since the last ResetGameTime (i.e. since Play started).
EDITOR_INTERFACE float GetGameTime()
{
	return g_game_time;
}

// Reset the game clock + fixed-step accumulator (call when Play starts).
EDITOR_INTERFACE void ResetGameTime()
{
	g_game_time = 0.0f;
	g_time_accumulator = 0.0f;
}

// DX12 viewport control
