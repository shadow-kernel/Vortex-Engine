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
		// Stream from disk/pak in chunks instead of pre-decoding the whole clip —
		// for music and long ambience beds (a 5-minute OGG fully decoded is tens of
		// MB of PCM; streamed it stays at its encoded size + a small decode window).
		bool stream{ false };
		s32 priority{ 128 };	// 0 = most important .. 256 = least (Unity convention)
		s32 bus{ 2 };			// mixer bus index (audio::bus; default sfx)
	};

	// 3D properties mirrored from the AudioSource component. Stored per voice by
	// issue #8's component bridge; issue #9's spatializer turns them into DSP.
	struct voice_spatial
	{
		f32 spatial_blend{ 0.0f };	// 0 = flat 2D, 1 = fully positional
		f32 min_distance{ 1.0f };
		f32 max_distance{ 500.0f };
		s32 rolloff_mode{ 0 };		// 0 = logarithmic, 1 = linear, 2 = custom
		f32 doppler_level{ 1.0f };
		f32 spread{ 0.0f };			// degrees
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
	// World position, pushed per frame by the component bridge for 3D voices.
	void voice_set_position(voice_handle handle, f32 x, f32 y, f32 z);
	void voice_set_spatial(voice_handle handle, const voice_spatial& spatial);
	// Reverb send gain 0..1 (reverbZoneMix x listener zone weight) — no-op while
	// the reverb node is down.
	void voice_set_reverb_send(voice_handle handle, f32 send);

	// Fade envelope (issue #17): a sample-accurate multiplier ON TOP of the voice
	// volume (final gain = volume x bus x envelope — no write conflicts, no zipper).
	// target 0..1; starting a new fade retargets smoothly from the CURRENT value.
	// stop_when_done releases the voice once the fade completes (FadeOut semantics).
	void voice_fade(voice_handle handle, f32 target, f32 seconds, bool stop_when_done);

	// Stats for diagnostics and the future mixer window meters.
	u32 voices_active_count();
	u32 voices_stolen_count();	// total steals since initialize
	u32 voices_max_count();
}
