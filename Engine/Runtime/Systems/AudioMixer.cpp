#include "AudioMixer.h"
#include "AudioEngine.h"
#include "AudioInternal.h"
#include "AudioReverb.h"

#include <atomic>
#include <cmath>
#include <cstring>
#include <mutex>
#include <vector>

#pragma warning(push)
#pragma warning(disable: 4244 4245 4456 4457 4701 4267 4100 4189)
#include "../../ThirdParty/miniaudio.h"
#pragma warning(pop)

namespace vortex::runtime::audio {

	namespace {

		// Passthrough metering node: copies input to output while capturing peak and
		// RMS on the AUDIO thread into atomics the game/UI thread reads. One sits
		// between every bus group and its parent, so the mixer window meters show
		// exactly what each bus contributes.
		struct meter_node
		{
			ma_node_base		base;			// must be first (miniaudio node contract)
			std::atomic<f32>	peak{ 0.0f };
			std::atomic<f32>	rms{ 0.0f };
			u32					channels{ 2 };
		};

		void meter_process(ma_node* node, const float** frames_in, ma_uint32* frame_count_in,
			float** frames_out, ma_uint32* frame_count_out)
		{
			meter_node* meter = (meter_node*)node;
			const float* in = frames_in[0];
			float* out = frames_out[0];
			const ma_uint32 frames = *frame_count_out < *frame_count_in ? *frame_count_out : *frame_count_in;
			const u32 samples = frames * meter->channels;

			f32 peak = 0.0f;
			f32 sum_sq = 0.0f;
			for (u32 i = 0; i < samples; ++i)
			{
				const f32 v = in[i];
				out[i] = v;
				const f32 a = fabsf(v);
				if (a > peak) peak = a;
				sum_sq += v * v;
			}
			const f32 rms = samples > 0 ? sqrtf(sum_sq / samples) : 0.0f;

			// Fast attack, slow decay — meters snap up and fall smoothly.
			const f32 old_peak = meter->peak.load(std::memory_order_relaxed);
			meter->peak.store(peak > old_peak ? peak : old_peak * 0.94f + peak * 0.06f, std::memory_order_relaxed);
			const f32 old_rms = meter->rms.load(std::memory_order_relaxed);
			meter->rms.store(rms > old_rms ? rms : old_rms * 0.92f + rms * 0.08f, std::memory_order_relaxed);

			*frame_count_in = frames;
			*frame_count_out = frames;
		}

		ma_node_vtable g_meter_vtable = { meter_process, nullptr, 1, 1, 0 };

		struct bus_state
		{
			ma_sound_group	group{};
			meter_node		meter{};
			f32				user_volume{ 1.0f };
			bool			muted{ false };
			f32				duck_gain{ 1.0f };	// smoothed by the ducking envelope
			bool			alive{ false };
		};

		struct duck_rule
		{
			s32		trigger{ -1 };
			s32		target{ -1 };
			f32		duck_gain{ 1.0f };	// linear gain while fully ducked (from dB)
			f32		attack_per_s{ 0.0f };
			f32		release_per_s{ 0.0f };
			f32		threshold{ 0.05f };	// trigger RMS above this engages the duck
		};

		static bus_state	g_buses[(s32)bus::count];
		static std::vector<duck_rule> g_ducks;
		static std::mutex	g_mixer_mutex;
		static bool			g_mixer_alive{ false };

		// Applies user volume x duck gain x mute to the miniaudio group.
		void apply_bus_volume(bus_state& state)
		{
			const f32 v = state.muted ? 0.0f : state.user_volume * state.duck_gain;
			ma_sound_group_set_volume(&state.group, v);
		}

		bool valid(bus b) { return (s32)b >= 0 && (s32)b < (s32)bus::count; }
	}

	void mixer_initialize()
	{
		ma_engine* engine = internal_engine();
		if (!engine || g_mixer_alive) return;

		std::lock_guard<std::mutex> lock(g_mixer_mutex);
		const u32 channels = ma_engine_get_channels(engine);

		// Build bottom-up: master first (meters to the endpoint), children after
		// (meters into master's group).
		for (s32 i = 0; i < (s32)bus::count; ++i)
		{
			bus_state& state = g_buses[i];
			state.user_volume = 1.0f;
			state.muted = false;
			state.duck_gain = 1.0f;

			ma_sound_group* parent = (i == (s32)bus::master) ? nullptr : &g_buses[(s32)bus::master].group;
			if (ma_sound_group_init(engine, 0, parent, &state.group) != MA_SUCCESS)
			{
				internal_log("WARNING: mixer bus %d failed to init", i);
				continue;
			}

			// Metering node between the group and whatever it was attached to.
			state.meter.channels = channels;
			state.meter.peak.store(0.0f);
			state.meter.rms.store(0.0f);
			ma_node_config node_config = ma_node_config_init();
			node_config.vtable = &g_meter_vtable;
			node_config.pInputChannels = &channels;
			node_config.pOutputChannels = &channels;
			if (ma_node_init(ma_engine_get_node_graph(engine), &node_config, nullptr, &state.meter.base) == MA_SUCCESS)
			{
				ma_node* downstream = (i == (s32)bus::master)
					? ma_engine_get_endpoint(engine)
					: (ma_node*)&g_buses[(s32)bus::master].group;
				ma_node_attach_output_bus(&state.group, 0, &state.meter.base, 0);
				ma_node_attach_output_bus(&state.meter.base, 0, downstream, 0);
			}
			state.alive = true;
		}

		g_ducks.clear();
		g_mixer_alive = true;
		internal_log("mixer up: Master + Music/SFX/Ambience/UI");

		// The global reverb hangs off the master bus — needs the tree first.
		reverb_initialize();
	}

	void mixer_shutdown()
	{
		if (!g_mixer_alive) return;
		reverb_shutdown(); // voice splitters feed it; voices are already gone
		std::lock_guard<std::mutex> lock(g_mixer_mutex);
		// Children before master (their meters feed master's group).
		for (s32 i = (s32)bus::count - 1; i >= 0; --i)
		{
			bus_state& state = g_buses[i];
			if (!state.alive) continue;
			ma_sound_group_uninit(&state.group);
			ma_node_uninit(&state.meter.base, nullptr);
			state.alive = false;
		}
		g_ducks.clear();
		g_mixer_alive = false;
	}

	void mixer_update(float dt)
	{
		if (!g_mixer_alive || dt <= 0.0f) return;
		std::lock_guard<std::mutex> lock(g_mixer_mutex);

		// Ducking: an envelope follower per rule — while the trigger bus is loud the
		// target's duck gain glides to duck_gain (attack), else back to 1 (release).
		for (const duck_rule& rule : g_ducks)
		{
			if (rule.trigger < 0 || rule.target < 0) continue;
			bus_state& trigger = g_buses[rule.trigger];
			bus_state& target = g_buses[rule.target];
			if (!trigger.alive || !target.alive) continue;

			const bool engaged = trigger.meter.rms.load(std::memory_order_relaxed) > rule.threshold;
			const f32 goal = engaged ? rule.duck_gain : 1.0f;
			const f32 speed = engaged ? rule.attack_per_s : rule.release_per_s;
			f32 gain = target.duck_gain;
			if (speed <= 0.0f) gain = goal;
			else if (gain < goal) { gain += speed * dt; if (gain > goal) gain = goal; }
			else if (gain > goal) { gain -= speed * dt; if (gain < goal) gain = goal; }
			if (gain != target.duck_gain)
			{
				target.duck_gain = gain;
				apply_bus_volume(target);
			}
		}
	}

	::ma_sound* mixer_group(bus b)
	{
		if (!g_mixer_alive || !valid(b)) return nullptr;
		bus_state& state = g_buses[(s32)b];
		return state.alive ? &state.group : nullptr;
	}

	bus mixer_bus_from_index(s32 index)
	{
		if (index < 0 || index >= (s32)bus::count) return bus::sfx;
		return (bus)index;
	}

	void mixer_set_bus_volume(bus b, f32 volume)
	{
		if (!g_mixer_alive || !valid(b)) return;
		std::lock_guard<std::mutex> lock(g_mixer_mutex);
		bus_state& state = g_buses[(s32)b];
		state.user_volume = volume < 0.0f ? 0.0f : (volume > 1.0f ? 1.0f : volume);
		apply_bus_volume(state);
	}

	f32 mixer_get_bus_volume(bus b)
	{
		if (!g_mixer_alive || !valid(b)) return 1.0f;
		std::lock_guard<std::mutex> lock(g_mixer_mutex);
		return g_buses[(s32)b].user_volume;
	}

	void mixer_set_bus_mute(bus b, bool mute)
	{
		if (!g_mixer_alive || !valid(b)) return;
		std::lock_guard<std::mutex> lock(g_mixer_mutex);
		bus_state& state = g_buses[(s32)b];
		state.muted = mute;
		apply_bus_volume(state);
	}

	bool mixer_get_bus_mute(bus b)
	{
		if (!g_mixer_alive || !valid(b)) return false;
		std::lock_guard<std::mutex> lock(g_mixer_mutex);
		return g_buses[(s32)b].muted;
	}

	void mixer_get_bus_levels(bus b, f32* out_peak, f32* out_rms)
	{
		if (out_peak) *out_peak = 0.0f;
		if (out_rms) *out_rms = 0.0f;
		if (!g_mixer_alive || !valid(b)) return;
		bus_state& state = g_buses[(s32)b];
		if (!state.alive) return;
		if (out_peak) *out_peak = state.meter.peak.load(std::memory_order_relaxed);
		if (out_rms) *out_rms = state.meter.rms.load(std::memory_order_relaxed);
	}

	void mixer_set_duck(bus trigger, bus target, f32 duck_db, f32 attack_ms, f32 release_ms, f32 threshold)
	{
		if (!g_mixer_alive || !valid(trigger) || !valid(target) || trigger == target) return;
		std::lock_guard<std::mutex> lock(g_mixer_mutex);

		// Replace an existing rule for the same pair.
		for (size_t i = 0; i < g_ducks.size(); ++i)
		{
			if (g_ducks[i].trigger == (s32)trigger && g_ducks[i].target == (s32)target)
			{
				g_ducks.erase(g_ducks.begin() + i);
				break;
			}
		}
		if (duck_db >= 0.0f) // 0 dB or positive = rule removal
		{
			bus_state& target_state = g_buses[(s32)target];
			target_state.duck_gain = 1.0f;
			apply_bus_volume(target_state);
			return;
		}

		duck_rule rule{};
		rule.trigger = (s32)trigger;
		rule.target = (s32)target;
		rule.duck_gain = powf(10.0f, duck_db / 20.0f); // dB -> linear
		rule.attack_per_s = attack_ms > 0.0f ? (1.0f - rule.duck_gain) / (attack_ms / 1000.0f) : 0.0f;
		rule.release_per_s = release_ms > 0.0f ? (1.0f - rule.duck_gain) / (release_ms / 1000.0f) : 0.0f;
		rule.threshold = threshold > 0.0f ? threshold : 0.05f;
		g_ducks.push_back(rule);
	}

	void mixer_clear_ducks()
	{
		if (!g_mixer_alive) return;
		std::lock_guard<std::mutex> lock(g_mixer_mutex);
		g_ducks.clear();
		for (s32 i = 0; i < (s32)bus::count; ++i)
		{
			if (!g_buses[i].alive) continue;
			g_buses[i].duck_gain = 1.0f;
			apply_bus_volume(g_buses[i]);
		}
	}
}
