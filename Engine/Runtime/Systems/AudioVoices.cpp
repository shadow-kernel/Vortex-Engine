#include "AudioVoices.h"
#include "AudioEngine.h"
#include "AudioInternal.h"
#include "AudioMixer.h"
#include "AudioReverb.h"

#include <chrono>
#include <cmath>
#include <mutex>
#include <vector>

#pragma warning(push)
#pragma warning(disable: 4244 4245 4456 4457 4701 4267 4100 4189)
#include "../../ThirdParty/miniaudio.h"
#pragma warning(pop)

namespace vortex::runtime::audio {

	namespace {

		struct voice_slot
		{
			ma_sound	sound{};		// storage reused across plays; slots never move
			u32			generation{ 1 };
			bool		in_use{ false };
			bool		paused{ false };
			s32			priority{ 128 };
			f32			volume{ 1.0f };	// base volume; the blend correction multiplies on top
			f32			correction{ 1.0f };	// current blend correction — EVERY ma volume write must include it
			bool		looping{ false };
			f32			position[3]{};	// world position (steal tiebreak + spatializer)
			voice_spatial spatial{};
			// Set when streaming a registered in-memory blob: the sound reads through
			// this decoder (decode-on-demand); released together with the sound.
			ma_decoder*	stream_decoder{ nullptr };
			// Reverb send: sound -> splitter -> (bus 0: dry to the mixer group,
			// bus 1: wet send into the global reverb node, gain = reverbZoneMix x
			// listener zone weight). Only built while the reverb node exists.
			ma_splitter_node splitter{};
			bool		splitter_alive{ false };
			// FadeOut semantics: release the voice once its fade has completed.
			std::chrono::steady_clock::time_point fade_stop_deadline{};
			bool		fade_stop_pending{ false };
			// Doppler: velocity = position delta / wall-clock between pushes.
			std::chrono::steady_clock::time_point last_push{};
			bool		has_last_push{ false };
		};

		// The pool itself is the only allocation, made once in voices_initialize.
		// (miniaudio still makes small bounded internal allocations per sound init;
		// the decoded PCM is shared through the AudioEngine preload cache.)
		static std::vector<voice_slot>	g_slots;
		static std::mutex				g_voices_mutex;
		static u32						g_stolen_total{ 0 };

		voice_handle make_handle(u32 index, u32 generation)
		{
			return ((voice_handle)generation << 32) | index;
		}

		// Must be called with g_voices_mutex held.
		voice_slot* resolve(voice_handle handle)
		{
			const u32 index = (u32)(handle & 0xFFFF'FFFF);
			const u32 generation = (u32)(handle >> 32);
			if (handle == invalid_voice || index >= g_slots.size()) return nullptr;
			voice_slot& slot = g_slots[index];
			if (!slot.in_use || slot.generation != generation) return nullptr;
			return &slot;
		}

		// Upper bound keeps (fileRate/engineRate) * pitch inside miniaudio's
		// uint32 resampler-ratio conversion — huge script values are formal UB there.
		f32 clamp_pitch(f32 pitch)
		{
			return pitch < 0.01f ? 0.01f : (pitch > 100.0f ? 100.0f : pitch);
		}

		// Rolloff mapping (documented so Steam Audio v2 can swap the panner without
		// changing attenuation semantics):
		//   AudioRolloffMode.Logarithmic (0) -> ma_attenuation_model_inverse (1/d)
		//   AudioRolloffMode.Linear      (1) -> ma_attenuation_model_linear
		//   AudioRolloffMode.Custom      (2) -> inverse (custom curves are post-v1)
		ma_attenuation_model to_attenuation_model(s32 rolloff_mode)
		{
			return rolloff_mode == 1 ? ma_attenuation_model_linear : ma_attenuation_model_inverse;
		}

		// Same math miniaudio's spatializer uses (rolloff factor 1) — needed on the
		// CPU side for the spatial-blend volume compensation below.
		f32 model_attenuation(const voice_spatial& sp, f32 distance)
		{
			const f32 min_d = sp.min_distance > 0.01f ? sp.min_distance : 0.01f;
			const f32 max_d = sp.max_distance > min_d ? sp.max_distance : min_d + 0.01f;
			const f32 d = distance < min_d ? min_d : (distance > max_d ? max_d : distance);
			if (sp.rolloff_mode == 1)
			{
				const f32 a = 1.0f - (d - min_d) / (max_d - min_d);
				return a < 0.0f ? 0.0f : a;
			}
			return min_d / d; // inverse
		}

		// Apply the stored spatial properties to the miniaudio sound. Must be called
		// with g_voices_mutex held.
		// Spatial blend: 0 disables the spatializer entirely (flat 2D + stereo pan);
		// > 0 enables it. Values in between keep full 3D panning but scale doppler by
		// the blend and compensate the distance attenuation toward 2D in
		// voices_update (effective gain = (1-b) + b*attenuation).
		void apply_spatial(voice_slot& slot)
		{
			ma_sound* s = &slot.sound;
			const voice_spatial& sp = slot.spatial;

			if (sp.spatial_blend <= 0.0f)
			{
				ma_sound_set_spatialization_enabled(s, MA_FALSE);
				// Disabling skips the spatializer's process step, which is the only
				// place dopplerPitch gets recomputed — a voice that was moving keeps
				// its stale pitch forever otherwise. Reset it explicitly.
				s->engineNode.spatializer.dopplerPitch = 1.0f;
				return;
			}

			const f32 blend = sp.spatial_blend > 1.0f ? 1.0f : sp.spatial_blend;
			const f32 min_d = sp.min_distance > 0.01f ? sp.min_distance : 0.01f;

			ma_sound_set_spatialization_enabled(s, MA_TRUE);
			ma_sound_set_positioning(s, ma_positioning_absolute);
			ma_sound_set_attenuation_model(s, to_attenuation_model(sp.rolloff_mode));
			ma_sound_set_min_distance(s, min_d);
			ma_sound_set_max_distance(s, sp.max_distance > min_d ? sp.max_distance : min_d + 0.01f);
			// Doppler factor capped at 2: with the 150 u/s velocity cap this keeps
			// miniaudio's doppler denominator (343.3 - factor*speed) strictly positive —
			// unclamped inspector values could drive the pitch ratio to +inf and into
			// the resampler's float->uint32 UB (same class clamp_pitch guards).
			const f32 doppler = sp.doppler_level < 0.0f ? 0.0f : (sp.doppler_level > 2.0f ? 2.0f : sp.doppler_level);
			ma_sound_set_doppler_factor(s, doppler * blend);
			// Partial blend computes its volume correction against a 0.001-floored
			// attenuation replica; mirror the floor into miniaudio or linear rolloff
			// hits exact 0 past max_distance and the product loses the (1-b) 2D share.
			ma_sound_set_min_gain(s, blend < 1.0f ? 0.001f : 0.0f);

			// Spread approximation: miniaudio has no stereo-spread parameter, but its
			// directional attenuation factor de-focuses the source (0 = fully
			// omnidirectional/wide, 1 = point source) — a serviceable v1 mapping.
			const f32 spread = sp.spread < 0.0f ? 0.0f : (sp.spread > 360.0f ? 360.0f : sp.spread);
			ma_sound_set_directional_attenuation_factor(s, 1.0f - spread / 360.0f);
		}

		// Must be called with g_voices_mutex held. Bumping the generation is what
		// invalidates every handle that still points at this slot.
		void release_slot(voice_slot& slot)
		{
			ma_sound_uninit(&slot.sound);
			if (slot.splitter_alive)
			{
				ma_splitter_node_uninit(&slot.splitter, nullptr);
				slot.splitter_alive = false;
			}
			if (slot.stream_decoder)
			{
				ma_decoder_uninit(slot.stream_decoder);
				delete slot.stream_decoder;
				slot.stream_decoder = nullptr;
			}
			slot.in_use = false;
			slot.paused = false;
			slot.fade_stop_pending = false;
			++slot.generation;
		}
	}

	void voices_initialize(u32 max_voices)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		if (!g_slots.empty()) return;
		if (max_voices == 0) max_voices = 1;
		g_slots.resize(max_voices);	// sized exactly once — ma_sound storage must never move
		g_stolen_total = 0;
	}

	void voices_shutdown()
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		for (voice_slot& slot : g_slots)
		{
			if (slot.in_use) release_slot(slot);
		}
		g_slots.clear();
		g_slots.shrink_to_fit();
	}

	void voices_update(float /*dt*/)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);

		float listener[3]{};
		internal_listener_position(listener); // game space, same as slot.position

		const auto now = std::chrono::steady_clock::now();
		for (voice_slot& slot : g_slots)
		{
			if (!slot.in_use) continue;

			// Looping voices never hit at_end; paused voices hold their position.
			if (!slot.looping && ma_sound_at_end(&slot.sound))
			{
				release_slot(slot);
				continue;
			}

			// A completed FadeOut releases the voice back to the pool.
			if (slot.fade_stop_pending && now >= slot.fade_stop_deadline)
			{
				release_slot(slot);
				continue;
			}

			// Spatial blend 0<b<1: the spatializer runs at full strength, so pull the
			// effective attenuation back toward 2D with a volume correction —
			// effective gain becomes (1-b) + b*attenuation instead of attenuation.
			f32 correction = 1.0f;
			const f32 blend = slot.spatial.spatial_blend;
			if (blend > 0.0f && blend < 1.0f)
			{
				const f32 dx = slot.position[0] - listener[0];
				const f32 dy = slot.position[1] - listener[1];
				const f32 dz = slot.position[2] - listener[2];
				const f32 distance = sqrtf(dx * dx + dy * dy + dz * dz);
				// Floored on BOTH sides: apply_spatial sets ma min_gain to the same
				// 0.001, so correction * ma_gain == (1-b) + b*attenuation exactly.
				const f32 attenuation = model_attenuation(slot.spatial, distance);
				const f32 floor = attenuation > 0.001f ? attenuation : 0.001f;
				correction = ((1.0f - blend) + blend * floor) / floor;
			}
			slot.correction = correction;
			ma_sound_set_volume(&slot.sound, slot.volume * correction);
		}
	}

	voice_handle voice_play(const char* path, const voice_params& params)
	{
		ma_engine* engine = internal_engine();
		if (!engine || !path || !*path) return invalid_voice;

		// Gate BEFORE slot selection: a file no decoder accepts must never occupy —
		// or worse, steal — a live voice. Non-streaming plays decode/cache up front;
		// streaming plays get a cheap header-probe instead (preloading would fully
		// decode exactly what streaming avoids).
		if (params.stream ? !validate_clip(path) : !preload(path)) return invalid_voice;

		std::lock_guard<std::mutex> lock(g_voices_mutex);
		if (g_slots.empty()) return invalid_voice;

		// Free slot first; otherwise pick the steal victim: least important voice
		// (max priority value), ties broken by quietest. Only steal when the victim
		// is at most as important as the request (victim.priority >= request).
		voice_slot* target = nullptr;
		for (voice_slot& slot : g_slots)
		{
			if (!slot.in_use) { target = &slot; break; }
		}

		if (!target)
		{
			voice_slot* victim = nullptr;
			for (voice_slot& slot : g_slots)
			{
				if (slot.priority < params.priority) continue;	// more important than request
				if (!victim
					|| slot.priority > victim->priority
					|| (slot.priority == victim->priority && slot.volume < victim->volume))
				{
					victim = &slot;
				}
			}
			if (!victim) return invalid_voice;	// everything playing outranks the request
			release_slot(*victim);
			++g_stolen_total;
			target = victim;
		}

		// No MA_SOUND_FLAG_NO_SPATIALIZATION: the spatializer is toggled at runtime
		// per the AudioSource's spatial blend (flag would bake 2D at init).
		// STREAM decodes on demand through the resource manager's job thread;
		// registered pak names resolve via the narrow lookup, real paths via wide.
		const ma_uint32 flags = params.stream ? MA_SOUND_FLAG_STREAM : MA_SOUND_FLAG_DECODE;
		// Every voice attaches to its mixer bus group (nullptr before the mixer is
		// up — then it falls back to the engine output as before).
		ma_sound_group* group = (ma_sound_group*)mixer_group(mixer_bus_from_index(params.bus));
		ma_result result;
		ma_decoder* stream_decoder = nullptr;
		if (is_registered_clip(path))
		{
			if (params.stream)
			{
				// miniaudio's STREAM flag only streams from files — for registered
				// in-memory blobs (pak entries), stream through a ma_decoder over the
				// bytes instead: decode-on-demand, no full PCM in memory.
				u64 size = 0;
				const void* bytes = registered_clip_bytes(path, &size);
				if (!bytes) return invalid_voice;
				stream_decoder = new ma_decoder{};
				result = ma_decoder_init_memory(bytes, (size_t)size, nullptr, stream_decoder);
				if (result == MA_SUCCESS)
				{
					result = ma_sound_init_from_data_source(engine, stream_decoder, 0, group, &target->sound);
					if (result != MA_SUCCESS) ma_decoder_uninit(stream_decoder);
				}
				if (result != MA_SUCCESS)
				{
					delete stream_decoder;
					stream_decoder = nullptr;
				}
			}
			else
			{
				result = ma_sound_init_from_file(engine, path, flags, group, nullptr, &target->sound);
			}
		}
		else
		{
			wchar_t wide[1024];
			if (!internal_widen_path(path, wide, 1024)) return invalid_voice;
			result = ma_sound_init_from_file_w(engine, wide, flags, group, nullptr, &target->sound);
		}
		if (result != MA_SUCCESS)
		{
			internal_log("WARNING: voice init failed for '%s' (%s%s)", path,
				ma_result_description(result), params.stream ? ", streaming" : "");
			return invalid_voice;
		}
		target->stream_decoder = stream_decoder;

		// Reverb send plumbing: re-route sound -> splitter, splitter bus 0 (dry) to
		// where the sound was headed, bus 1 (wet, gain 0 until the zone service says
		// otherwise) into the global reverb. Send is PRE-fader (per-voice).
		target->splitter_alive = false;
		ma_node* reverb = (ma_node*)reverb_node();
		if (reverb)
		{
			const u32 channels = ma_engine_get_channels(engine);
			ma_splitter_node_config split_config = ma_splitter_node_config_init(channels);
			if (ma_splitter_node_init(ma_engine_get_node_graph(engine), &split_config, nullptr, &target->splitter) == MA_SUCCESS)
			{
				ma_node* dry_target = group ? (ma_node*)group : ma_engine_get_endpoint(engine);
				ma_node_attach_output_bus(&target->sound, 0, &target->splitter, 0);
				ma_node_attach_output_bus(&target->splitter, 0, dry_target, 0);
				ma_node_attach_output_bus(&target->splitter, 1, reverb, 0);
				ma_node_set_output_bus_volume(&target->splitter, 1, 0.0f);
				target->splitter_alive = true;
			}
		}

		target->in_use = true;
		target->paused = false;
		target->priority = params.priority;
		target->volume = params.volume < 0.0f ? 0.0f : params.volume;
		target->looping = params.loop;
		target->position[0] = target->position[1] = target->position[2] = 0.0f;
		target->spatial = voice_spatial{};
		target->correction = 1.0f;
		target->has_last_push = false;
		target->fade_stop_pending = false;
		apply_spatial(*target); // default blend 0 -> plain 2D until the bridge pushes spatial data

		ma_sound_set_volume(&target->sound, target->volume);
		ma_sound_set_pitch(&target->sound, clamp_pitch(params.pitch));
		ma_sound_set_pan(&target->sound, params.pan < -1.0f ? -1.0f : (params.pan > 1.0f ? 1.0f : params.pan));
		ma_sound_set_looping(&target->sound, params.loop ? MA_TRUE : MA_FALSE);
		ma_sound_start(&target->sound);

		const u32 index = (u32)(target - g_slots.data());
		internal_log("voice %u started: '%s' (vol %.2f, pitch %.2f, loop %d, prio %d)",
			index, path, target->volume, params.pitch, params.loop ? 1 : 0, params.priority);
		return make_handle(index, target->generation);
	}

	void voice_stop(voice_handle handle)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		if (voice_slot* slot = resolve(handle))
		{
			internal_log("voice %u stopped", (u32)(handle & 0xFFFF'FFFF));
			release_slot(*slot);
		}
	}

	void voice_pause(voice_handle handle)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		if (voice_slot* slot = resolve(handle))
		{
			ma_sound_stop(&slot->sound);	// stop = pause in miniaudio; position is kept
			slot->paused = true;
		}
	}

	void voice_resume(voice_handle handle)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		if (voice_slot* slot = resolve(handle))
		{
			if (slot->paused)
			{
				ma_sound_start(&slot->sound);
				slot->paused = false;
			}
		}
	}

	bool voice_is_playing(voice_handle handle)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		voice_slot* slot = resolve(handle);
		return slot && !slot->paused && ma_sound_is_playing(&slot->sound);
	}

	bool voice_is_valid(voice_handle handle)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		return resolve(handle) != nullptr;
	}

	void voice_set_volume(voice_handle handle, f32 volume)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		if (voice_slot* slot = resolve(handle))
		{
			slot->volume = volume < 0.0f ? 0.0f : volume;
			// Include the current blend correction: the bridge pushes volume AFTER
			// voices_update each frame, so a raw write here would stomp the corrected
			// gain for the whole inter-frame interval (partial blend collapsed to
			// full-3D with frame-rate gain flutter — caught in review).
			ma_sound_set_volume(&slot->sound, slot->volume * slot->correction);
		}
	}

	void voice_set_pitch(voice_handle handle, f32 pitch)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		if (voice_slot* slot = resolve(handle))
		{
			ma_sound_set_pitch(&slot->sound, clamp_pitch(pitch));
		}
	}

	void voice_set_pan(voice_handle handle, f32 pan)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		if (voice_slot* slot = resolve(handle))
		{
			ma_sound_set_pan(&slot->sound, pan < -1.0f ? -1.0f : (pan > 1.0f ? 1.0f : pan));
		}
	}

	void voice_set_position(voice_handle handle, f32 x, f32 y, f32 z)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		voice_slot* slot = resolve(handle);
		if (!slot) return;

		// Doppler feed: velocity from the push delta over wall-clock time. Guarded
		// against teleports (huge jumps must not scream a pitch spike) and stale
		// pushes (dt outside a sane frame range -> treat as fresh placement).
		const auto now = std::chrono::steady_clock::now();
		f32 vx = 0.0f, vy = 0.0f, vz = 0.0f;
		if (slot->has_last_push)
		{
			const f32 dt = std::chrono::duration<f32>(now - slot->last_push).count();
			const f32 dx = x - slot->position[0];
			const f32 dy = y - slot->position[1];
			const f32 dz = z - slot->position[2];
			const f32 jump = sqrtf(dx * dx + dy * dy + dz * dz);
			if (dt > 0.0001f && dt < 0.5f && jump < 25.0f)
			{
				constexpr f32 k_max_speed = 150.0f; // beyond this it's a glitch, not motion
				vx = dx / dt; vy = dy / dt; vz = dz / dt;
				const f32 speed = sqrtf(vx * vx + vy * vy + vz * vz);
				if (speed > k_max_speed)
				{
					const f32 scale = k_max_speed / speed;
					vx *= scale; vy *= scale; vz *= scale;
				}
			}
		}
		if (!slot->has_last_push)
		{
			internal_log("voice %u first pos: (%.1f, %.1f, %.1f)", (u32)(handle & 0xFFFF'FFFF), x, y, z);
		}
		slot->position[0] = x;
		slot->position[1] = y;
		slot->position[2] = z;
		slot->last_push = now;
		slot->has_last_push = true;

		// z mirrored at the miniaudio boundary (LH game space -> RH spatializer),
		// matching set_listener — slot.position stays game-space.
		ma_sound_set_position(&slot->sound, x, y, -z);
		ma_sound_set_velocity(&slot->sound, vx, vy, -vz);
	}

	void voice_fade(voice_handle handle, f32 target, f32 seconds, bool stop_when_done)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		voice_slot* slot = resolve(handle);
		if (!slot) return;

		const f32 goal = target < 0.0f ? 0.0f : (target > 1.0f ? 1.0f : target);
		const u64 ms = seconds <= 0.0f ? 0 : (u64)(seconds * 1000.0f);
		// -1 start = fade from the CURRENT envelope value — retargeting an in-flight
		// fade glides instead of snapping. miniaudio renders this sample-accurately
		// on the audio thread, independent of the volume property.
		ma_sound_set_fade_in_milliseconds(&slot->sound, -1.0f, goal, ms);

		if (stop_when_done)
		{
			slot->fade_stop_pending = true;
			slot->fade_stop_deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(ms);
		}
		else
		{
			slot->fade_stop_pending = false;
		}
	}

	void voice_set_reverb_send(voice_handle handle, f32 send)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		if (voice_slot* slot = resolve(handle))
		{
			if (slot->splitter_alive)
			{
				const f32 s = send < 0.0f ? 0.0f : (send > 1.0f ? 1.0f : send);
				ma_node_set_output_bus_volume(&slot->splitter, 1, s);
			}
		}
	}

	void voice_set_spatial(voice_handle handle, const voice_spatial& spatial)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		if (voice_slot* slot = resolve(handle))
		{
			if (slot->spatial.spatial_blend != spatial.spatial_blend)
			{
				internal_log("voice %u spatial: blend %.2f min %.1f max %.1f rolloff %d",
					(u32)(handle & 0xFFFF'FFFF), spatial.spatial_blend,
					spatial.min_distance, spatial.max_distance, spatial.rolloff_mode);
			}
			slot->spatial = spatial;
			apply_spatial(*slot);
		}
	}

	u32 voices_active_count()
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		u32 count = 0;
		for (const voice_slot& slot : g_slots)
		{
			if (slot.in_use) ++count;
		}
		return count;
	}

	u32 voices_stolen_count()
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		return g_stolen_total;
	}

	u32 voices_max_count()
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		return (u32)g_slots.size();
	}
}
