#pragma once

// ============================================================================
// The scene render queue, shared by both render backends (DX12 and SDL GPU).
//
// CpuStats: what the queue cost the CPU in the last frame (#361) — the swap of the submitted items into the render
// queue, the sort + draw-run build, the cull + instance pack of every view and the shadow caster gather. engine_stats
// and the player's FPS line show them, so a change to the submit path can be measured instead of guessed.
// ============================================================================
#include "../../Common/CommonHeaders.h"
#include <chrono>

namespace vortex::graphics::instancing
{
	struct CpuStats
	{
		float swap_ms{ 0.0f };     // submitted items -> render queue (merge, retained-instance sync)
		float sort_ms{ 0.0f };     // the sort and the draw-run build (0 on a frame that kept the layout)
		float cull_ms{ 0.0f };     // frustum / distance cull + instance pack, summed over every view of the frame
		float shadow_ms{ 0.0f };   // the shadow casters gathered and packed per tile
		u32 sorted{ 0 };           // items the sort touched this frame
		u32 queue{ 0 };            // items in the render queue
		u32 frame{ 0 };            // which frame these are (counts up), so a sampler sees each frame once

		// render_frame: publish the frame that just finished into `last` and start the next one
		void next_frame(CpuStats& last)
		{
			last = *this;
			const u32 f = frame + 1;
			*this = {};
			frame = f;
		}
	};

	using Clock = std::chrono::steady_clock;

	inline float ms_since(Clock::time_point t0)
	{
		return std::chrono::duration<float, std::milli>(Clock::now() - t0).count();
	}

	// Adds the milliseconds of its scope to a CpuStats field.
	struct ScopedMs
	{
		float& acc;
		Clock::time_point t0{ Clock::now() };
		explicit ScopedMs(float& accumulator) : acc(accumulator) {}
		~ScopedMs() { acc += ms_since(t0); }
		ScopedMs(const ScopedMs&) = delete;
		ScopedMs& operator=(const ScopedMs&) = delete;
	};
}
