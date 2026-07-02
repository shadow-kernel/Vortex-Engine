#pragma once

#include "../../Common/CommonHeaders.h"

// Voice management (issue #7): a fixed, preallocated pool of playback voices on
// top of the AudioEngine core, with priority-driven stealing when the pool is
// full. Handles carry a generation counter so a stolen/finished voice's handle
// goes stale instead of controlling whoever reused the slot.
namespace vortex::runtime::audio {

	// (generation << 32) | slot index. 0 is never valid (generations start at 1).
	using voice_handle = u64;
	inline constexpr voice_handle invalid_voice{ 0 };

	struct voice_params
	{
		f32 volume{ 1.0f };
		f32 pitch{ 1.0f };
		f32 pan{ -0.0f };		// -1 left .. +1 right
		bool loop{ false };
		s32 priority{ 128 };	// 0 = most important .. 256 = least (Unity convention)
	};

	// Pool lifetime — driven by audio::initialize/shutdown, not called directly.
	void voices_initialize(u32 max_voices);
	void voices_shutdown();
	// Reaps finished one-shots back into the pool. Called from audio::update.
	void voices_update(float dt);

	// Starts a voice. When the pool is full, steals the least-important voice
	// (highest priority value; ties broken by lowest volume) if the request is at
	// least as important — otherwise the play is rejected and invalid_voice
	// returned. Also returns invalid_voice when the file cannot be decoded.
	voice_handle voice_play(const char* path, const voice_params& params);

	// All handle-based calls are safe no-ops for stale/invalid handles.
	void voice_stop(voice_handle handle);
	void voice_pause(voice_handle handle);
	void voice_resume(voice_handle handle);
	bool voice_is_playing(voice_handle handle);
	bool voice_is_valid(voice_handle handle);
	void voice_set_volume(voice_handle handle, f32 volume);
	void voice_set_pitch(voice_handle handle, f32 pitch);
	void voice_set_pan(voice_handle handle, f32 pan);

	// Stats for diagnostics and the future mixer window meters.
	u32 voices_active_count();
	u32 voices_stolen_count();	// total steals since initialize
	u32 voices_max_count();
}
