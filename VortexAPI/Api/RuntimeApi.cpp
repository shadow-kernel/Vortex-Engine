#include "../ApiCommon.h"

EDITOR_INTERFACE void InitializeRuntime()
{
	runtime::resource_manager::initialize();
	runtime::prefab_service::initialize();
	runtime::systems::initialize_render();
	runtime::systems::initialize_physics();
	runtime::systems::initialize_audio();
}

EDITOR_INTERFACE void ShutdownRuntime()
{
	runtime::systems::shutdown_audio();
	runtime::systems::shutdown_physics();
	runtime::systems::shutdown_render();
	runtime::prefab_service::shutdown();
	runtime::resource_manager::shutdown();
}

// Advances the whole game simulation by dt seconds (one game tick).
// Call this once per frame while in play mode / from the standalone player,
// before submitting render items. The editor's idle viewport does NOT call it,
// which is exactly why entering play mode "comes alive" and exiting it freezes.
namespace
{
	// Fixed-timestep game clock: the simulation always advances in stable 1/60 s steps regardless of
	// render frame rate (deterministic physics), with an accumulator for leftover time. g_game_time is
	// the elapsed in-game seconds since the last ResetGameTime (Play start).
	constexpr float k_fixed_dt = 1.0f / 60.0f;
	float g_time_accumulator = 0.0f;
	float g_game_time = 0.0f;
}

EDITOR_INTERFACE void StepRuntime(float dt)
{
	if (dt < 0.0f) dt = 0.0f;
	if (dt > 0.25f) dt = 0.25f; // clamp huge spikes (e.g. after a breakpoint)

	g_time_accumulator += dt;
	int steps = 0;
	while (g_time_accumulator >= k_fixed_dt && steps < 8) // cap steps to avoid a spiral of death
	{
		runtime::systems::update_physics(k_fixed_dt);
		g_game_time += k_fixed_dt;
		g_time_accumulator -= k_fixed_dt;
		++steps;
	}

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
