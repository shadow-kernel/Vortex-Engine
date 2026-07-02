#include "AudioVoices.h"
#include "AudioEngine.h"
#include "AudioInternal.h"

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
			f32			volume{ 1.0f };
			bool		looping{ false };
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

		// Must be called with g_voices_mutex held. Bumping the generation is what
		// invalidates every handle that still points at this slot.
		void release_slot(voice_slot& slot)
		{
			ma_sound_uninit(&slot.sound);
			slot.in_use = false;
			slot.paused = false;
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
		for (voice_slot& slot : g_slots)
		{
			// Looping voices never hit at_end; paused voices hold their position.
			if (slot.in_use && !slot.looping && ma_sound_at_end(&slot.sound))
			{
				release_slot(slot);
			}
		}
	}

	voice_handle voice_play(const char* path, const voice_params& params)
	{
		ma_engine* engine = internal_engine();
		if (!engine || !path || !*path) return invalid_voice;

		// Decode/cache up front (shared PCM); a file no decoder accepts never
		// occupies — or worse, steals — a voice.
		if (!preload(path)) return invalid_voice;

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

		const ma_result result = ma_sound_init_from_file(engine, path,
			MA_SOUND_FLAG_DECODE | MA_SOUND_FLAG_NO_SPATIALIZATION, nullptr, nullptr, &target->sound);
		if (result != MA_SUCCESS) return invalid_voice;

		target->in_use = true;
		target->paused = false;
		target->priority = params.priority;
		target->volume = params.volume < 0.0f ? 0.0f : params.volume;
		target->looping = params.loop;

		ma_sound_set_volume(&target->sound, target->volume);
		ma_sound_set_pitch(&target->sound, clamp_pitch(params.pitch));
		ma_sound_set_pan(&target->sound, params.pan < -1.0f ? -1.0f : (params.pan > 1.0f ? 1.0f : params.pan));
		ma_sound_set_looping(&target->sound, params.loop ? MA_TRUE : MA_FALSE);
		ma_sound_start(&target->sound);

		const u32 index = (u32)(target - g_slots.data());
		return make_handle(index, target->generation);
	}

	void voice_stop(voice_handle handle)
	{
		std::lock_guard<std::mutex> lock(g_voices_mutex);
		if (voice_slot* slot = resolve(handle))
		{
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
			ma_sound_set_volume(&slot->sound, slot->volume);
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
