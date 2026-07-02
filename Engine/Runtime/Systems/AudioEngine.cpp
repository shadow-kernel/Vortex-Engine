#include "AudioEngine.h"
#include "AudioInternal.h"
#include "AudioMixer.h"
#include "AudioVoices.h"
#include "SteamAudio.h"

#include <chrono>
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

		// Encoded blobs registered with miniaudio's resource manager (pak entries).
		// miniaudio does NOT copy registered data, so the engine owns these buffers;
		// they are unregistered at shutdown and freed after the engine dies.
		static std::unordered_map<std::string, std::vector<u8>> g_registered_clips;
		static std::mutex		g_registered_mutex;

		// Fixed pool size for the voice layer (AudioVoices.cpp); make this a
		// project setting once audio project settings exist.
		inline constexpr u32	k_max_voices{ 32 };

		// Generated test beep (no asset needed): finite PCM buffer + one reusable sound.
		static float*			g_beep_pcm{ nullptr };
		static ma_audio_buffer	g_beep_buffer{};
		static ma_sound			g_beep_sound{};
		static bool				g_beep_ready{ false };

		// Silent-mode pump scratch (keeps the node graph clock advancing off-device).
		static float			g_silent_pump_accumulator{ 0.0f };

		// Game-space listener position (miniaudio holds the z-mirrored copy) plus the
		// velocity-tracking state — reset on initialize so a stale pre-shutdown pose
		// can't fake a velocity into the first frame of the next session.
		static float			g_listener_pos[3]{};
		static float			g_listener_last[3]{};
		static std::chrono::steady_clock::time_point g_listener_last_push{};
		static bool				g_listener_has_last{ false };

		void log_v(const char* fmt, va_list args)
		{
			char buffer[1024];
			vsnprintf(buffer, sizeof(buffer), fmt, args);
			OutputDebugStringA("[VortexAudio] ");
			OutputDebugStringA(buffer);
			OutputDebugStringA("\n");

			// Opt-in file log (same pattern as the Streamline diagnostics): set
			// VORTEX_AUDIO_LOG to a file path to capture audio events from outside
			// a debugger — used by automated play-mode verification.
			static char log_path[512];
			static bool checked_env{ false };
			if (!checked_env)
			{
				checked_env = true;
				size_t len{ 0 };
				getenv_s(&len, log_path, sizeof(log_path), "VORTEX_AUDIO_LOG");
				if (len == 0) log_path[0] = '\0';
			}
			if (log_path[0])
			{
				FILE* f{ nullptr };
				if (fopen_s(&f, log_path, "a") == 0 && f)
				{
					fputs(buffer, f);
					fputc('\n', f);
					fclose(f);
				}
			}
		}

		void log(const char* fmt, ...)
		{
			va_list args;
			va_start(args, fmt);
			log_v(fmt, args);
			va_end(args);
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

	ma_engine* internal_engine()
	{
		return g_state != engine_state::uninitialized ? &g_engine : nullptr;
	}

	void internal_log(const char* fmt, ...)
	{
		va_list args;
		va_start(args, fmt);
		log_v(fmt, args);
		va_end(args);
	}

	bool internal_widen_path(const char* narrow, wchar_t* out, size_t out_chars)
	{
		if (!narrow || !out || out_chars == 0) return false;
		// Strict UTF-8 first: ANSI umlaut bytes are invalid UTF-8, so this reliably
		// separates the UTF-8 (C# bridge) callers from legacy ACP (engine) callers.
		int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, narrow, -1, out, (int)out_chars);
		if (n == 0)
		{
			n = MultiByteToWideChar(CP_ACP, 0, narrow, -1, out, (int)out_chars);
		}
		return n != 0;
	}

	bool initialize()
	{
		if (g_state != engine_state::uninitialized) return g_state == engine_state::device;

		// Normal path: default output device (WASAPI on Windows).
		if (ma_engine_init(nullptr, &g_engine) == MA_SUCCESS)
		{
			g_state = engine_state::device;
			mixer_initialize();
			voices_initialize(k_max_voices);
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
			mixer_initialize();
			voices_initialize(k_max_voices);
			log("WARNING: no audio output device available — running silent");
			return false;
		}

		log("ERROR: audio engine failed to initialize");
		return false;
	}

	void shutdown()
	{
		if (g_state == engine_state::uninitialized) return;
		voices_shutdown();
		steam::shutdown();  // after voices — they destroy their Steam Audio sources (and thus the ma_nodes) first
		mixer_shutdown();   // after voices (they attach into the bus groups)
		unload_all_sounds();
		shutdown_beep();
		{
			// Unregister BEFORE the engine dies, free the buffers after.
			std::lock_guard<std::mutex> lock(g_registered_mutex);
			for (auto& [name, bytes] : g_registered_clips)
			{
				ma_resource_manager_unregister_data(ma_engine_get_resource_manager(&g_engine), name.c_str());
			}
		}
		ma_engine_uninit(&g_engine);
		{
			std::lock_guard<std::mutex> lock(g_registered_mutex);
			g_registered_clips.clear();
		}
		g_state = engine_state::uninitialized;
		g_silent_pump_accumulator = 0.0f;
		g_listener_has_last = false;
		g_listener_pos[0] = g_listener_pos[1] = g_listener_pos[2] = 0.0f;
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

	bool register_clip_data(const char* name, const void* data, u64 size)
	{
		if (g_state == engine_state::uninitialized || !name || !*name || !data || size == 0) return false;

		std::lock_guard<std::mutex> lock(g_registered_mutex);
		if (g_registered_clips.count(name)) return true;

		auto& bytes = g_registered_clips[name];
		bytes.assign((const u8*)data, (const u8*)data + size);
		const ma_result result = ma_resource_manager_register_encoded_data(
			ma_engine_get_resource_manager(&g_engine), name, bytes.data(), (size_t)bytes.size());
		if (result != MA_SUCCESS)
		{
			g_registered_clips.erase(name);
			log("WARNING: register_clip_data failed for '%s' (%s)", name, ma_result_description(result));
			return false;
		}
		log("registered clip data '%s' (%llu bytes)", name, (unsigned long long)size);
		return true;
	}

	bool is_registered_clip(const char* name)
	{
		if (!name) return false;
		std::lock_guard<std::mutex> lock(g_registered_mutex);
		return g_registered_clips.count(name) != 0;
	}

	bool validate_clip(const char* path)
	{
		if (g_state == engine_state::uninitialized || !path || !*path) return false;

		ma_decoder probe{};
		ma_result result;
		u64 size = 0;
		const void* bytes = registered_clip_bytes(path, &size);
		if (bytes)
		{
			result = ma_decoder_init_memory(bytes, (size_t)size, nullptr, &probe);
		}
		else
		{
			wchar_t wide[1024];
			if (!internal_widen_path(path, wide, 1024)) return false;
			result = ma_decoder_init_file_w(wide, nullptr, &probe);
		}
		if (result != MA_SUCCESS)
		{
			log("WARNING: clip failed validation: '%s' (%s)", path, ma_result_description(result));
			return false;
		}
		ma_decoder_uninit(&probe);
		return true;
	}

	namespace {
		// Shared decoder-open for the editor helpers: registered blob or file path.
		// Forces f32 output (the default keeps the file's native format — reading
		// s16 data into float buffers would be garbage); channels/rate stay native.
		bool open_probe_decoder(const char* path, ma_decoder* out)
		{
			ma_decoder_config config = ma_decoder_config_init(ma_format_f32, 0, 0);
			u64 size = 0;
			const void* bytes = registered_clip_bytes(path, &size);
			if (bytes)
			{
				return ma_decoder_init_memory(bytes, (size_t)size, &config, out) == MA_SUCCESS;
			}
			wchar_t wide[1024];
			if (!internal_widen_path(path, wide, 1024)) return false;
			return ma_decoder_init_file_w(wide, &config, out) == MA_SUCCESS;
		}
	}

	bool clip_info(const char* path, f32* out_duration_seconds, u32* out_sample_rate, u32* out_channels)
	{
		if (out_duration_seconds) *out_duration_seconds = 0.0f;
		if (out_sample_rate) *out_sample_rate = 0;
		if (out_channels) *out_channels = 0;
		if (g_state == engine_state::uninitialized || !path || !*path) return false;

		ma_decoder decoder{};
		if (!open_probe_decoder(path, &decoder)) return false;

		ma_uint64 frames = 0;
		ma_decoder_get_length_in_pcm_frames(&decoder, &frames);
		if (frames == 0)
		{
			// Unknown length (stb_vorbis OGG) — count by decoding, still header-cheap
			// relative to the waveform pass that usually follows.
			const u32 ch = decoder.outputChannels > 0 ? decoder.outputChannels : 1;
			std::vector<f32> scratch(4096 * ch);
			for (;;)
			{
				ma_uint64 read = 0;
				if (ma_decoder_read_pcm_frames(&decoder, scratch.data(), 4096, &read) != MA_SUCCESS || read == 0)
					break;
				frames += read;
			}
		}
		if (out_sample_rate) *out_sample_rate = decoder.outputSampleRate;
		if (out_channels) *out_channels = decoder.outputChannels;
		if (out_duration_seconds && decoder.outputSampleRate > 0)
			*out_duration_seconds = (f32)((double)frames / decoder.outputSampleRate);
		ma_decoder_uninit(&decoder);
		return true;
	}

	bool clip_waveform(const char* path, f32* out_peaks, u32 bin_count)
	{
		if (!out_peaks || bin_count == 0) return false;
		for (u32 i = 0; i < bin_count; ++i) out_peaks[i] = 0.0f;
		if (g_state == engine_state::uninitialized || !path || !*path) return false;

		ma_decoder decoder{};
		if (!open_probe_decoder(path, &decoder)) return false;

		// Some decoders (notably stb_vorbis-backed OGG) report an unknown length —
		// collect coarse per-1024-frame peaks first, then re-bin afterwards, which
		// works for both known and unknown lengths with bounded memory.
		const u32 channels = decoder.outputChannels > 0 ? decoder.outputChannels : 1;
		constexpr ma_uint64 k_group = 1024;
		std::vector<f32> coarse;
		coarse.reserve(4096);

		std::vector<f32> chunk(4096 * channels);
		f32 group_peak = 0.0f;
		ma_uint64 in_group = 0;
		for (;;)
		{
			ma_uint64 frames_read = 0;
			if (ma_decoder_read_pcm_frames(&decoder, chunk.data(), 4096, &frames_read) != MA_SUCCESS || frames_read == 0)
				break;
			for (ma_uint64 f = 0; f < frames_read; ++f)
			{
				f32 peak = 0.0f;
				for (u32 c = 0; c < channels; ++c)
				{
					const f32 v = fabsf(chunk[f * channels + c]);
					if (v > peak) peak = v;
				}
				if (peak > group_peak) group_peak = peak;
				if (++in_group >= k_group)
				{
					coarse.push_back(group_peak);
					group_peak = 0.0f;
					in_group = 0;
				}
			}
		}
		if (in_group > 0) coarse.push_back(group_peak);
		ma_decoder_uninit(&decoder);
		if (coarse.empty()) return false;

		// Re-bin the coarse peaks into the requested resolution.
		for (u32 b = 0; b < bin_count; ++b)
		{
			const size_t start = (size_t)((u64)b * coarse.size() / bin_count);
			size_t end = (size_t)((u64)(b + 1) * coarse.size() / bin_count);
			if (end <= start) end = start + 1;
			f32 peak = 0.0f;
			for (size_t i = start; i < end && i < coarse.size(); ++i)
				if (coarse[i] > peak) peak = coarse[i];
			out_peaks[b] = peak;
		}
		return true;
	}

	const void* registered_clip_bytes(const char* name, u64* out_size)
	{
		if (out_size) *out_size = 0;
		if (!name) return nullptr;
		std::lock_guard<std::mutex> lock(g_registered_mutex);
		auto it = g_registered_clips.find(name);
		if (it == g_registered_clips.end()) return nullptr;
		if (out_size) *out_size = (u64)it->second.size();
		// Stable until shutdown: entries are never erased mid-session and the
		// vector is filled once at registration.
		return it->second.data();
	}

	bool preload(const char* path)
	{
		if (g_state == engine_state::uninitialized || !path || !*path) return false;

		{
			std::lock_guard<std::mutex> lock(g_sounds_mutex);
			if (g_sounds.count(path)) return true;
		}

		// File paths: decode fully up front (MA_SOUND_FLAG_DECODE) — load-time cost
		// instead of a first-play hitch, decoded PCM shared by all instances.
		// Registered names: the resource manager keeps the node ENCODED (the DECODE
		// flag is ignored for pre-registered data), so each instance decodes on
		// demand from the shared encoded bytes — fine for wav, revisit for heavy
		// compressed pak SFX in #20 (register_decoded_data would trade memory back).
		// Real paths go through the wide-char open (narrow fopen mangles paths
		// outside the ANSI code page).
		ma_sound* sound = new ma_sound{};
		ma_result result;
		if (is_registered_clip(path))
		{
			result = ma_sound_init_from_file(&g_engine, path,
				MA_SOUND_FLAG_DECODE | MA_SOUND_FLAG_NO_SPATIALIZATION, nullptr, nullptr, sound);
		}
		else
		{
			wchar_t wide[1024];
			if (!internal_widen_path(path, wide, 1024))
			{
				delete sound;
				log("WARNING: unconvertible path '%s'", path);
				return false;
			}
			result = ma_sound_init_from_file_w(&g_engine, wide,
				MA_SOUND_FLAG_DECODE | MA_SOUND_FLAG_NO_SPATIALIZATION, nullptr, nullptr, sound);
		}
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
		voice_params params{};
		params.volume = volume;
		return voice_play(path, params) != invalid_voice;
	}

	void set_listener(f32 px, f32 py, f32 pz, f32 fx, f32 fy, f32 fz, f32 ux, f32 uy, f32 uz)
	{
		if (g_state == engine_state::uninitialized) return;

		// Listener velocity for doppler, same push-delta scheme as voice positions
		// (teleport + stale-dt guarded, speed-capped).
		const auto now = std::chrono::steady_clock::now();
		float vx = 0.0f, vy = 0.0f, vz = 0.0f;
		if (g_listener_has_last)
		{
			const float dt = std::chrono::duration<float>(now - g_listener_last_push).count();
			const float dx = px - g_listener_last[0], dy = py - g_listener_last[1], dz = pz - g_listener_last[2];
			const float jump = sqrtf(dx * dx + dy * dy + dz * dz);
			if (dt > 0.0001f && dt < 0.5f && jump < 25.0f)
			{
				constexpr float k_max_speed = 150.0f;
				vx = dx / dt; vy = dy / dt; vz = dz / dt;
				const float speed = sqrtf(vx * vx + vy * vy + vz * vz);
				if (speed > k_max_speed)
				{
					const float scale = k_max_speed / speed;
					vx *= scale; vy *= scale; vz *= scale;
				}
			}
		}
		g_listener_last[0] = px; g_listener_last[1] = py; g_listener_last[2] = pz;
		g_listener_last_push = now;
		g_listener_has_last = true;
		g_listener_pos[0] = px; g_listener_pos[1] = py; g_listener_pos[2] = pz;

		// Handedness boundary: the engine is left-handed (DirectX, +z forward),
		// miniaudio's spatializer math is right-handed — without the z mirror a
		// source on the player's left pans into the RIGHT ear (measured).
		ma_engine_listener_set_position(&g_engine, 0, px, py, -pz);
		ma_engine_listener_set_direction(&g_engine, 0, fx, fy, -fz);
		ma_engine_listener_set_world_up(&g_engine, 0, ux, uy, -uz);
		ma_engine_listener_set_velocity(&g_engine, 0, vx, vy, -vz);

		// Steam Audio gets GAME-space values (left-handed) — it applies the same z-mirror internally.
		steam::set_listener(px, py, pz, fx, fy, fz, ux, uy, uz);
	}

	void steam_set_enabled(bool enabled)
	{
		steam::set_enabled(enabled);
		if (enabled)
		{
			if (g_state != engine_state::uninitialized && !steam::is_available())
				steam::initialize(ma_engine_get_sample_rate(&g_engine), 1024);
		}
		else
		{
			steam::shutdown();
		}
	}

	void steam_set_geometry(const float* verts, u32 vertex_count, const s32* indices, u32 index_count)
	{
		steam::set_geometry(verts, vertex_count, indices, index_count);
	}

	void internal_listener_position(float out[3])
	{
		out[0] = g_listener_pos[0];
		out[1] = g_listener_pos[1];
		out[2] = g_listener_pos[2];
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

		voices_update(dt);
		mixer_update(dt);

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
