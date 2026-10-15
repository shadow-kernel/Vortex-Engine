#pragma once

#include "../../Common/CommonHeaders.h"

// Adaptive music (#437): the stems of a .vmusic mixed sample-locked inside ONE voice. A custom miniaudio data
// source reads every stem for the same frames in each mix, so the stems share one cursor: they cannot drift apart
// however long the music loops, and a streamed stem that runs dry stalls them all together. Every stem has two
// gain stages — the music state's volume and the parameter mapping's volume, final gain = state x parameter —
// that ramp per sample and can be scheduled to begin on an exact frame of the music: that is how a state switch
// lands on the bar line without a click. Stingers mix into the same voice from an exact frame. (miniaudio's own
// delayed fades cannot do this: a sound plays at full volume while its delayed fade waits.)
// Stems stream by default (#437 review: decoding eight 3-minute stems froze the game for 0.7 s and took 0.5 GB at every
// Play): a file stem through the resource manager's streams, a pak stem through its own decoder over the registered bytes.
// A streamed stem cannot jump back to the loop start without refilling, so its next pass is opened ahead of the wrap at
// the loop start, and at the wrap every stem switches on the same frame — the loop region is kept, the stems stay locked.
namespace vortex::runtime::audio {

	inline constexpr u32 music_max_stems{ 16 };
	inline constexpr u32 music_max_stingers{ 4 };

	struct music_desc
	{
		const char* const* stems{ nullptr };	// clip paths or registered pak names, one per stem
		u32 stem_count{ 0 };
		const f32* state_gains{ nullptr };		// initial state-stage gain per stem (null = 1)
		const f32* param_gains{ nullptr };		// initial parameter-stage gain per stem (null = 1)
		// The loop region [loop_start, loop_end) in frames of the engine rate — loop_end 0 = the longest stem's end;
		// a stem shorter than the region is silent for the rest of it.
		u64 loop_start{ 0 };
		u64 loop_end{ 0 };
		u64 start_frame{ 0 };					// where in the stems playback begins
		bool loop{ true };
		bool decode{ false };					// decode file stems into memory instead of streaming them (short loops)
	};

	enum class music_stage : u32 { state = 0, parameter = 1 };

	struct music_clock
	{
		u64 played{ 0 };			// frames the music has played — monotonic, the clock schedules run on
		u64 position{ 0 };			// where in the stems it is (frames at sample_rate), wrapping in the loop region
		u64 loops{ 0 };				// completed passes through the loop region
		u64 loop_start{ 0 };
		u64 loop_end{ 0 };
		u64 length{ 0 };			// the longest stem (decoded) / the first stem (streamed)
		u32 sample_rate{ 0 };
		d64 seconds_since_mix{ 0.0 };	// wall time since the mixer last read the music — a smooth clock between mixes
		u64 last_state_frame{ ~0ull };	// `played` at which the last state schedule began (~0 = none yet)
		bool stalled{ false };		// the last mix waited for a stream to refill (the music paused, all stems together)
	};

	struct music_group;

	// Opens every stem (synchronously; a decoded stem shares the engine's decoded cache) and builds the group.
	// nullptr when there are no stems, too many, or one cannot be opened.
	music_group* music_create(const music_desc& desc);
	// Only once no voice reads the group any more (the voice layer uninits its sound first).
	void music_destroy(music_group* group);
	// The group as a miniaudio data source (ma_data_source*): f32 stereo at the engine rate.
	void* music_data_source(music_group* group);

	// Ramp one gain stage of every stem to `gains` over `ramp_frames` (never shorter than ~1.3 ms — a jump would
	// click), beginning exactly when the music has played `at_frame` frames; 0, or a frame already played, begins
	// at the next mix. A newer schedule for the same stage replaces one that has not begun.
	void music_schedule(music_group* group, music_stage stage, const f32* gains, u32 count, u64 at_frame, u64 ramp_frames);
	// Mix a clip once into the music from the frame `at_frame` of its clock (a stinger), at `volume`. False when
	// the clip cannot be opened or every stinger slot is busy.
	bool music_play_stinger(music_group* group, const char* path, f32 volume, u64 at_frame);
	// Main thread, per tick: frees the stingers that finished, and opens the next pass of streamed stems ahead of the
	// loop wrap (on the resource manager's job thread) / frees the pass they left.
	void music_update(music_group* group);

	bool music_get_clock(music_group* group, music_clock* out);
	// One stem's gain (state x parameter) at the end of the last mix; -1 out of range.
	f32 music_stem_gain(music_group* group, u32 stem);

	// Read frames straight from the group, interleaved stereo f32 — the native test renders a group no voice
	// plays. Returns the frames produced (fewer only at the end of a one-shot music).
	u64 music_render(music_group* group, f32* out, u64 frames);
}
