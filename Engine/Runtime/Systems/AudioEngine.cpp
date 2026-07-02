#include "AudioEngine.h"

#include <cstdarg>
#include <cstdio>
#include <cmath>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

// This is the single translation unit that compiles miniaudio and stb_vorbis.
// Order matters (documented miniaudio pattern): stb_vorbis header first so the
// miniaudio implementation picks up OGG support, stb_vorbis implementation after.
#pragma warning(push)
#pragma warning(disable: 4244 4245 4456 4457 4701 4267 4100 4189)
#define STB_VORBIS_HEADER_ONLY
#include "../../ThirdParty/stb_vorbis.c"

#define MA_IMPLEMENTATION
#include "../../ThirdParty/miniaudio.h"

#undef STB_VORBIS_HEADER_ONLY
#include "../../ThirdParty/stb_vorbis.c"
#pragma warning(pop)

namespace vortex::runtime::audio {

	namespace {

		enum class engine_state { uninitialized, device, silent };

		static engine_state		g_state{ engine_state::uninitialized };
		static ma_engine		g_engine{};

		// Decoded-sound cache: one non-playing "template" ma_sound per path. It pins
		// the decoded data in miniaudio's resource manager (ref-counted by path), so
		// later plays of the same file share the buffer instead of re-decoding.
		static std::unordered_map<std::string, ma_sound*> g_sounds;
		static std::mutex		g_sounds_mutex;

		// Live fire-and-forget instances; reaped in update() once they finish.
		// The proper voice pool with priorities replaces this in issue #7.
		static std::vector<ma_sound*> g_one_shots;
		static std::mutex		g_one_shots_mutex;

		// Generated test beep (no asset needed): finite PCM buffer + one reusable sound.
		static float*			g_beep_pcm{ nullptr };
		static ma_audio_buffer	g_beep_buffer{};
		static ma_sound			g_beep_sound{};
		static bool				g_beep_ready{ false };

		// Silent-mode pump scratch (keeps the node graph clock advancing off-device).
		static float			g_silent_pump_accumulator{ 0.0f };

		void log(const char* fmt, ...)
		{
			char buffer[1024];
			va_list args;
			va_start(args, fmt);
			vsnprintf(buffer, sizeof(buffer), fmt, args);
			va_end(args);
			OutputDebugStringA("[VortexAudio] ");
			OutputDebugStringA(buffer);
			OutputDebugStringA("\n");
		}

		bool init_beep()
		{
			if (g_beep_ready) return true;
			if (g_state == engine_state::uninitialized) return false;

			// 440 Hz sine, 0.35 s, mono, with a short fade-out so it doesn't click.
			const ma_uint32 sample_rate = ma_engine_get_sample_rate(&g_engine);
			const ma_uint64 frame_count = (ma_uint64)(sample_rate * 0.35);
			g_beep_pcm = (float*)ma_malloc((size_t)(frame_count * sizeof(float)), nullptr);
			if (!g_beep_pcm) return false;

			for (ma_uint64 i = 0; i < frame_count; ++i)
			{
				const float t = (float)i / (float)sample_rate;
				const float envelope = 1.0f - (float)i / (float)frame_count;
				g_beep_pcm[i] = 0.25f * envelope * sinf(t * 440.0f * 2.0f * 3.14159265f);
			}

			ma_audio_buffer_config config = ma_audio_buffer_config_init(
				ma_format_f32, 1, frame_count, g_beep_pcm, nullptr);
			config.sampleRate = sample_rate;
			if (ma_audio_buffer_init(&config, &g_beep_buffer) != MA_SUCCESS)
			{
				ma_free(g_beep_pcm, nullptr);
				g_beep_pcm = nullptr;
				return false;
			}

			if (ma_sound_init_from_data_source(&g_engine, &g_beep_buffer,
				MA_SOUND_FLAG_NO_SPATIALIZATION, nullptr, &g_beep_sound) != MA_SUCCESS)
			{
				ma_audio_buffer_uninit(&g_beep_buffer);
				ma_free(g_beep_pcm, nullptr);
				g_beep_pcm = nullptr;
				return false;
			}

			g_beep_ready = true;
			return true;
		}

		void shutdown_beep()
		{
			if (!g_beep_ready) return;
			ma_sound_uninit(&g_beep_sound);
			ma_audio_buffer_uninit(&g_beep_buffer);
			ma_free(g_beep_pcm, nullptr);
			g_beep_pcm = nullptr;
			g_beep_ready = false;
		}
	}

	bool initialize()
	{
		if (g_state != engine_state::uninitialized) return g_state == engine_state::device;

		// Normal path: default output device (WASAPI on Windows).
		if (ma_engine_init(nullptr, &g_engine) == MA_SUCCESS)
		{
			g_state = engine_state::device;
			log("initialized: device '%s', %u Hz, %u channels",
				g_engine.pDevice ? g_engine.pDevice->playback.name : "?",
				ma_engine_get_sample_rate(&g_engine),
				ma_engine_get_channels(&g_engine));
			return true;
		}

		// No/broken audio device: run the full engine without one so the game keeps
		// working (loads succeed, play calls are valid, nothing is audible).
		ma_engine_config config = ma_engine_config_init();
		config.noDevice = MA_TRUE;
		config.channels = 2;
		config.sampleRate = 48000;
		if (ma_engine_init(&config, &g_engine) == MA_SUCCESS)
		{
			g_state = engine_state::silent;
			log("WARNING: no audio output device available — running silent");
			return false;
		}

		log("ERROR: audio engine failed to initialize");
		return false;
	}

	void shutdown()
	{
		if (g_state == engine_state::uninitialized) return;
		{
			std::lock_guard<std::mutex> lock(g_one_shots_mutex);
			for (ma_sound* sound : g_one_shots)
			{
				ma_sound_uninit(sound);
				delete sound;
			}
			g_one_shots.clear();
		}
		unload_all_sounds();
		shutdown_beep();
		ma_engine_uninit(&g_engine);
		g_state = engine_state::uninitialized;
		g_silent_pump_accumulator = 0.0f;
		log("shut down");
	}

	bool is_initialized()
	{
		return g_state != engine_state::uninitialized;
	}

	bool has_device()
	{
		return g_state == engine_state::device;
	}

	bool preload(const char* path)
	{
		if (g_state == engine_state::uninitialized || !path || !*path) return false;

		{
			std::lock_guard<std::mutex> lock(g_sounds_mutex);
			if (g_sounds.count(path)) return true;
		}

		// Decode fully up front (MA_SOUND_FLAG_DECODE) — load-time cost instead of a
		// first-play hitch, and the decoded buffer is shared by all future instances.
		ma_sound* sound = new ma_sound{};
		const ma_result result = ma_sound_init_from_file(&g_engine, path,
			MA_SOUND_FLAG_DECODE | MA_SOUND_FLAG_NO_SPATIALIZATION, nullptr, nullptr, sound);
		if (result != MA_SUCCESS)
		{
			delete sound;
			log("WARNING: failed to decode '%s' (%s)", path, ma_result_description(result));
			return false;
		}

		std::lock_guard<std::mutex> lock(g_sounds_mutex);
		const bool inserted = g_sounds.emplace(path, sound).second;
		if (!inserted)
		{
			// Lost a load race — another thread cached it first.
			ma_sound_uninit(sound);
			delete sound;
		}
		return true;
	}

	bool is_loaded(const char* path)
	{
		if (!path) return false;
		std::lock_guard<std::mutex> lock(g_sounds_mutex);
		return g_sounds.count(path) != 0;
	}

	void unload_sound(const char* path)
	{
		if (!path) return;
		std::lock_guard<std::mutex> lock(g_sounds_mutex);
		auto it = g_sounds.find(path);
		if (it == g_sounds.end()) return;
		ma_sound_uninit(it->second);
		delete it->second;
		g_sounds.erase(it);
	}

	void unload_all_sounds()
	{
		std::lock_guard<std::mutex> lock(g_sounds_mutex);
		for (auto& [path, sound] : g_sounds)
		{
			ma_sound_uninit(sound);
			delete sound;
		}
		g_sounds.clear();
	}

	bool play_one_shot(const char* path, float volume)
	{
		if (g_state == engine_state::uninitialized || !path || !*path) return false;

		// Warm the cache first; the instance below then shares the decoded buffer
		// (miniaudio's resource manager ref-counts by path + DECODE flag).
		if (!preload(path)) return false;

		ma_sound* sound = new ma_sound{};
		const ma_result result = ma_sound_init_from_file(&g_engine, path,
			MA_SOUND_FLAG_DECODE | MA_SOUND_FLAG_NO_SPATIALIZATION, nullptr, nullptr, sound);
		if (result != MA_SUCCESS)
		{
			delete sound;
			log("WARNING: one-shot init failed for '%s' (%s)", path, ma_result_description(result));
			return false;
		}

		ma_sound_set_volume(sound, volume < 0.0f ? 0.0f : volume);
		ma_sound_start(sound);

		std::lock_guard<std::mutex> lock(g_one_shots_mutex);
		g_one_shots.push_back(sound);
		return true;
	}

	void play_test_beep()
	{
		if (!init_beep()) return;
		ma_sound_seek_to_pcm_frame(&g_beep_sound, 0);
		ma_sound_start(&g_beep_sound);
		log("test beep (%s)", has_device() ? "audible" : "silent mode");
	}

	void update(float dt)
	{
		if (g_state == engine_state::uninitialized) return;

		// Reap finished one-shots (looping never applies to them, so at_end is final).
		{
			std::lock_guard<std::mutex> lock(g_one_shots_mutex);
			for (size_t i = g_one_shots.size(); i > 0; --i)
			{
				ma_sound* sound = g_one_shots[i - 1];
				if (ma_sound_at_end(sound))
				{
					ma_sound_uninit(sound);
					delete sound;
					g_one_shots.erase(g_one_shots.begin() + (i - 1));
				}
			}
		}

		if (g_state == engine_state::silent)
		{
			// No device thread exists, so pull the mixer manually to keep sound
			// clocks (is_playing, at_end, fades) advancing in real time.
			g_silent_pump_accumulator += dt;
			// One check handles NaN and negatives (comparison with NaN is false).
			if (!(g_silent_pump_accumulator > 0.0f)) { g_silent_pump_accumulator = 0.0f; return; }
			const ma_uint32 sample_rate = ma_engine_get_sample_rate(&g_engine);
			const ma_uint32 channels = ma_engine_get_channels(&g_engine);
			ma_uint64 frames_to_read = (ma_uint64)(g_silent_pump_accumulator * sample_rate);
			if (frames_to_read == 0) return;
			g_silent_pump_accumulator -= (float)frames_to_read / (float)sample_rate;

			float scratch[4096];
			const ma_uint64 frames_per_chunk = (sizeof(scratch) / sizeof(float)) / channels;
			while (frames_to_read > 0)
			{
				const ma_uint64 chunk = frames_to_read < frames_per_chunk ? frames_to_read : frames_per_chunk;
				ma_uint64 frames_read = 0;
				ma_engine_read_pcm_frames(&g_engine, scratch, chunk, &frames_read);
				if (frames_read == 0) break;
				frames_to_read -= frames_read;
			}
		}

		// Device mode: miniaudio's own audio thread drives everything; per-tick
		// voice/streaming housekeeping arrives with issues #7 and #10.
	}
}
