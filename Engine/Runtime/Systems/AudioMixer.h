#pragma once

#include "../../Common/CommonHeaders.h"

// miniaudio's ma_sound_group is `typedef ma_sound ma_sound_group` — forward the
// underlying struct tag so this header stays miniaudio-free for callers.
struct ma_sound;

// Mixer buses (issue #13): a small fixed hierarchy on miniaudio's node graph —
// Master with Music/SFX/Ambience/UI children. Every voice routes to exactly one
// bus; bus volume/mute scales all its voices; a metering node per bus captures
// RMS/peak for the editor mixer window; ducking lets one bus dip another.
// Bus IDs are STABLE indices so .vui settings screens and the mixer window bind
// by name without depending on the tree order.
namespace vortex::runtime::audio {

	enum class bus : s32
	{
		master = 0,
		music,
		sfx,
		ambience,
		ui,
		count
	};

	// Lifetime driven by the AudioEngine (initialize/shutdown build/tear the tree).
	void mixer_initialize();
	void mixer_shutdown();
	// Per-tick: advances the ducking envelopes. Called from audio::update.
	void mixer_update(float dt);

	// The native group a bus routes through — voice_play passes this as the sound's
	// output group (ma_sound_group == ma_sound). nullptr when the mixer isn't up
	// (the voice then attaches straight to the engine output).
	::ma_sound* mixer_group(bus b);
	// Map a serialized bus index (AudioSource.OutputBus) to a bus, clamped.
	bus mixer_bus_from_index(s32 index);

	// Bus controls — real-time safe, apply to every routed voice immediately.
	void mixer_set_bus_volume(bus b, f32 volume);
	f32  mixer_get_bus_volume(bus b);
	void mixer_set_bus_mute(bus b, bool mute);
	bool mixer_get_bus_mute(bus b);

	// Metering for the mixer window (linear 0..1). Peak is instantaneous, RMS is
	// windowed. Both decay so an idle bus reads ~0.
	void mixer_get_bus_levels(bus b, f32* out_peak, f32* out_rms);

	// Ducking rule: while the TRIGGER bus is loud, the TARGET bus is attenuated by
	// duck_db, with attack/release smoothing. amount 0 dB / duration 0 disables.
	void mixer_set_duck(bus trigger, bus target, f32 duck_db, f32 attack_ms, f32 release_ms, f32 threshold);
	void mixer_clear_ducks();
}
