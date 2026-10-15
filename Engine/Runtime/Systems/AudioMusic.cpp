#include "AudioMusic.h"
#include "AudioEngine.h"
#include "AudioInternal.h"

#include <atomic>
#include <chrono>
#include <cstring>
#include <vector>

#ifdef _MSC_VER
#pragma warning(push)
#pragma warning(disable: 4244 4245 4456 4457 4701 4267 4100 4189)
#endif
#include "../../ThirdParty/miniaudio.h"
#ifdef _MSC_VER
#pragma warning(pop)
#endif

namespace vortex::runtime::audio {

	namespace {
		// A jump in gain clicks: every schedule ramps for at least this long (~1.3 ms at 48 kHz).
		inline constexpr u64 k_min_ramp{ 64 };
		// Frames per pass over the stems — bounds the scratch buffer (miniaudio asks for at most 512 per read).
		inline constexpr u64 k_block{ 512 };
		inline constexpr u32 k_max_channels{ 8 };
		inline constexpr u64 k_none{ ~0ull };

		// One gain stage of one stem: a linear ramp from where it was to its target, advanced per frame.
		struct gain_ramp
		{
			f32 value{ 1.0f };
			f32 target{ 1.0f };
			f32 step{ 0.0f };
			u64 left{ 0 };

			void start(f32 to, u64 frames)
			{
				target = to;
				left = frames < k_min_ramp ? k_min_ramp : frames;
				step = (to - value) / (f32)left;
			}
			f32 next()
			{
				if (left == 0) return value;
				value += step;
				if (--left == 0) value = target;   // land exactly, whatever the float steps summed to
				return value;
			}
			void skip(u64 n)
			{
				if (left == 0) return;
				if (n >= left) { value = target; left = 0; return; }
				value += step * (f32)n;
				left -= n;
			}
			bool settled() const { return left == 0; }
		};

		struct schedule
		{
			bool valid{ false };
			u64 at{ 0 };
			u64 ramp{ 0 };
			u32 count{ 0 };
			f32 gains[music_max_stems]{};
		};

		struct stem
		{
			ma_resource_manager_data_source ds{};
			bool alive{ false };
			bool streamed{ false };		// a resource-manager stream (its pages refill on the job thread)
			u32 channels{ 0 };
			u64 length{ 0 };
			gain_ramp stage[2];
		};

		enum stinger_state : u32 { stinger_free = 0, stinger_armed, stinger_playing, stinger_done };

		// free -> armed (game thread, after it opened the clip) -> playing -> done (mixing thread) -> free (game thread)
		struct stinger
		{
			std::atomic<u32> state{ stinger_free };
			ma_resource_manager_data_source ds{};
			u32 channels{ 0 };
			u64 at{ 0 };
			f32 volume{ 1.0f };
		};

		// The game thread holds it for a copy; the mixing thread only try-locks (a miss takes the schedule next mix).
		struct spin_lock
		{
			std::atomic<bool> busy{ false };
			bool try_lock() { return !busy.exchange(true, std::memory_order_acquire); }
			void lock() { while (busy.exchange(true, std::memory_order_acquire)) {} }
			void unlock() { busy.store(false, std::memory_order_release); }
		};

		// miniaudio casts the data source pointer to ma_data_source_base: this standard-layout wrapper keeps the base
		// at offset 0 and points back at its group.
		struct music_source
		{
			ma_data_source_base base;
			music_group* group;
		};

		s64 now_ns()
		{
			return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
		}

		f32 clamp_gain(f32 g) { return g > 0.0f ? (g < 4.0f ? g : 4.0f) : 0.0f; }   // NaN -> 0
	}

	struct music_group
	{
		music_source source{};
		bool source_alive{ false };
		ma_engine* engine{ nullptr };
		u32 sample_rate{ 0 };
		u32 stem_count{ 0 };
		stem stems[music_max_stems];
		bool loop{ true };
		bool stream{ false };		// at least one stem streams: whole files loop, the stems' own looping wraps them
		u64 loop_start{ 0 };
		u64 loop_end{ 0 };
		u64 length{ 0 };

		// The music's clock — written by the mixing thread; atomics because Music.Time reads the position (through
		// the voice's cursor) from the game thread.
		std::atomic<u64> position{ 0 };
		std::atomic<u64> played{ 0 };
		std::atomic<u64> loops{ 0 };

		schedule active[2];			// mixing thread: taken over from `pending`, waiting for their frame
		std::vector<f32> scratch;

		spin_lock schedule_lock;
		schedule pending[2];		// game thread -> mixing thread

		// the clock at the end of each mix, for the game thread (seqlock: odd = being written)
		std::atomic<u32> clock_seq{ 0 };
		std::atomic<u64> snap_played{ 0 };
		std::atomic<u64> snap_position{ 0 };
		std::atomic<u64> snap_loops{ 0 };
		std::atomic<s64> snap_wall_ns{ 0 };
		std::atomic<bool> snap_stalled{ false };
		std::atomic<u64> last_state_frame{ k_none };
		std::atomic<f32> gain_now[music_max_stems];

		stinger stingers[music_max_stingers];
	};

	namespace {
		// A clip through the resource manager: decoded (shares the engine's decoded cache) or streamed from its file.
		// Registered pak names decode on demand from memory whatever is asked — miniaudio streams only files.
		bool open_clip(ma_resource_manager* rm, const char* path, bool stream, u64 seek, ma_resource_manager_data_source* out, bool* streamed)
		{
			const bool registered = is_registered_clip(path);
			ma_resource_manager_data_source_config config = ma_resource_manager_data_source_config_init();
			config.flags = stream && !registered ? MA_RESOURCE_MANAGER_DATA_SOURCE_FLAG_STREAM : MA_RESOURCE_MANAGER_DATA_SOURCE_FLAG_DECODE;
			config.initialSeekPointInPCMFrames = seek;
#if VORTEX_PLATFORM_WINDOWS
			wchar_t wide[1024];
			if (registered) config.pFilePath = path;
			else
			{
				if (!internal_widen_path(path, wide, 1024)) return false;
				config.pFilePathW = wide;
			}
#else
			config.pFilePath = path;
#endif
			if (streamed) *streamed = stream && !registered;
			return ma_resource_manager_data_source_init_ex(rm, &config, out) == MA_SUCCESS;
		}

		// Every stem to the same frame — a stem shorter than that waits at its end (silent until the next wrap); a
		// streamed one wraps within its file.
		void seek_stems(music_group* g, u64 frame)
		{
			for (u32 i = 0; i < g->stem_count; ++i)
			{
				stem& s = g->stems[i];
				u64 f = frame;
				if (g->stream) { if (s.length > 0) f %= s.length; }
				else if (s.length > 0 && f > s.length) f = s.length;
				ma_resource_manager_data_source_seek_to_pcm_frame(&s.ds, f);
			}
		}

		void take_schedules(music_group* g)
		{
			if (!g->schedule_lock.try_lock()) return;
			for (u32 st = 0; st < 2; ++st)
			{
				if (!g->pending[st].valid) continue;
				g->active[st] = g->pending[st];		// replaces one that has not begun
				g->pending[st].valid = false;
			}
			g->schedule_lock.unlock();
		}

		// Starts the schedules and stingers whose frame has come.
		void begin_due(music_group* g, u64 played)
		{
			for (u32 st = 0; st < 2; ++st)
			{
				schedule& sc = g->active[st];
				if (!sc.valid || sc.at > played) continue;
				for (u32 i = 0; i < g->stem_count; ++i)
					g->stems[i].stage[st].start(i < sc.count ? sc.gains[i] : 1.0f, sc.ramp);
				sc.valid = false;
				if (st == (u32)music_stage::state) g->last_state_frame.store(played, std::memory_order_relaxed);
			}
			for (stinger& s : g->stingers)
				if (s.state.load(std::memory_order_acquire) == stinger_armed && s.at <= played)
					s.state.store(stinger_playing, std::memory_order_relaxed);
		}

		// Frames until the next scheduled schedule or stinger (k_none: nothing waits).
		u64 frames_to_next_event(music_group* g, u64 played)
		{
			u64 n = k_none;
			for (const schedule& sc : g->active)
				if (sc.valid && sc.at > played && sc.at - played < n) n = sc.at - played;
			for (stinger& s : g->stingers)
				if (s.state.load(std::memory_order_acquire) == stinger_armed && s.at > played && s.at - played < n) n = s.at - played;
			return n;
		}

		// out += src x (a x b), the two stages advancing per frame; src has `channels` interleaved (mono feeds both
		// sides, beyond stereo the first two channels). out == nullptr advances the stages only.
		void mix_stem(f32* out, const f32* src, u64 n, u32 channels, gain_ramp& a, gain_ramp& b)
		{
			if (a.settled() && b.settled())
			{
				const f32 gain = a.value * b.value;
				if (!out || gain == 0.0f) return;
				for (u64 f = 0; f < n; ++f)
				{
					const f32* x = src + f * channels;
					out[f * 2] += x[0] * gain;
					out[f * 2 + 1] += (channels > 1 ? x[1] : x[0]) * gain;
				}
				return;
			}
			for (u64 f = 0; f < n; ++f)
			{
				const f32 gain = a.next() * b.next();
				if (!out) continue;
				const f32* x = src + f * channels;
				out[f * 2] += x[0] * gain;
				out[f * 2 + 1] += (channels > 1 ? x[1] : x[0]) * gain;
			}
		}

		// Reads `n` frames of every stem (each stem exactly n — the shared cursor) and of the playing stingers into out.
		void mix_block(music_group* g, f32* out, u64 n)
		{
			f32* scratch = g->scratch.data();
			for (u32 i = 0; i < g->stem_count; ++i)
			{
				stem& s = g->stems[i];
				ma_uint64 got = 0;
				ma_data_source_read_pcm_frames(&s.ds, scratch, n, &got);
				if (got > n) got = n;
				mix_stem(out, scratch, got, s.channels, s.stage[0], s.stage[1]);
				if (got < n) { s.stage[0].skip(n - got); s.stage[1].skip(n - got); }   // past a short stem's end
				g->gain_now[i].store(s.stage[0].value * s.stage[1].value, std::memory_order_relaxed);
			}
			for (stinger& s : g->stingers)
			{
				if (s.state.load(std::memory_order_acquire) != stinger_playing) continue;
				ma_uint64 got = 0;
				ma_data_source_read_pcm_frames(&s.ds, scratch, n, &got);
				if (got > n) got = n;
				if (out)
				{
					for (u64 f = 0; f < got; ++f)
					{
						const f32* x = scratch + f * s.channels;
						out[f * 2] += x[0] * s.volume;
						out[f * 2 + 1] += (s.channels > 1 ? x[1] : x[0]) * s.volume;
					}
				}
				if (got < n) s.state.store(stinger_done, std::memory_order_release);
			}
		}

		void publish(music_group* g, bool stalled)
		{
			g->clock_seq.fetch_add(1, std::memory_order_acq_rel);
			g->snap_played.store(g->played.load(std::memory_order_relaxed), std::memory_order_relaxed);
			g->snap_position.store(g->position.load(std::memory_order_relaxed), std::memory_order_relaxed);
			g->snap_loops.store(g->loops.load(std::memory_order_relaxed), std::memory_order_relaxed);
			g->snap_wall_ns.store(now_ns(), std::memory_order_relaxed);
			g->snap_stalled.store(stalled, std::memory_order_relaxed);
			g->clock_seq.fetch_add(1, std::memory_order_release);
		}

		// The mix: `frames` frames of the music into stereo `out` (nullptr: advance without output). Returns the frames
		// produced — all of them while the music plays (a stream that has not refilled outputs silence and the music,
		// every stem together, waits), fewer once a one-shot music has ended.
		u64 mix(music_group* g, f32* out, u64 frames)
		{
			if (out) std::memset(out, 0, (size_t)(frames * 2 * sizeof(f32)));
			take_schedules(g);

			u64 played = g->played.load(std::memory_order_relaxed);
			u64 position = g->position.load(std::memory_order_relaxed);
			u64 loops = g->loops.load(std::memory_order_relaxed);
			bool stalled = false, ended = false;
			u64 done = 0;
			while (done < frames)
			{
				begin_due(g, played);

				u64 n = frames - done;
				if (n > k_block) n = k_block;
				if (!g->stream || !g->loop)
				{
					// decoded stems loop the region here, all on the same frame; a one-shot music ends with its longest stem
					const u64 end = g->loop ? g->loop_end : g->length;
					if (position >= end)
					{
						if (!g->loop) { ended = true; break; }
						position = g->loop_start;
						seek_stems(g, position);
						++loops;
						continue;
					}
					if (end - position < n) n = end - position;
				}
				if (g->stream)
				{
					// a stream that has not refilled holds every stem back, so they stay together
					for (u32 i = 0; i < g->stem_count && n > 0; ++i)
					{
						if (!g->stems[i].streamed) continue;
						ma_uint64 avail = 0;
						ma_resource_manager_data_source_get_available_frames(&g->stems[i].ds, &avail);
						if (avail < n) n = avail;
					}
					if (n == 0) { stalled = true; break; }
				}
				const u64 to_event = frames_to_next_event(g, played);
				if (to_event < n) n = to_event;

				mix_block(g, out ? out + done * 2 : nullptr, n);
				played += n;
				position += n;
				done += n;
				if (g->stream && g->loop && g->length > 0 && position >= g->length) { position -= g->length; ++loops; }
			}

			g->played.store(played, std::memory_order_relaxed);
			g->position.store(position, std::memory_order_relaxed);
			g->loops.store(loops, std::memory_order_relaxed);
			publish(g, stalled);
			return ended ? done : frames;
		}

		ma_result on_read(ma_data_source* ds, void* out, ma_uint64 frame_count, ma_uint64* frames_read)
		{
			music_group* g = ((music_source*)ds)->group;
			const u64 got = mix(g, (f32*)out, (u64)frame_count);
			if (frames_read) *frames_read = got;
			return got == 0 && frame_count > 0 ? MA_AT_END : MA_SUCCESS;
		}

		// The mixing thread applies a voice seek (Music.Time = …) here, between two mixes.
		ma_result on_seek(ma_data_source* ds, ma_uint64 frame)
		{
			music_group* g = ((music_source*)ds)->group;
			u64 f = (u64)frame;
			if (g->stream) { if (g->length > 0) f %= g->length; }
			else if (g->loop) { if (f >= g->loop_end) f = g->loop_start + (f - g->loop_start) % (g->loop_end - g->loop_start); }
			else if (f > g->length) f = g->length;
			seek_stems(g, f);
			g->position.store(f, std::memory_order_relaxed);
			return MA_SUCCESS;
		}

		ma_result on_get_format(ma_data_source* ds, ma_format* format, ma_uint32* channels, ma_uint32* rate, ma_channel* map, size_t map_cap)
		{
			music_group* g = ((music_source*)ds)->group;
			if (format) *format = ma_format_f32;
			if (channels) *channels = 2;
			if (rate) *rate = g->sample_rate;
			if (map) ma_channel_map_init_standard(ma_standard_channel_map_default, map, map_cap, 2);
			return MA_SUCCESS;
		}

		ma_result on_get_cursor(ma_data_source* ds, ma_uint64* cursor)
		{
			*cursor = ((music_source*)ds)->group->position.load(std::memory_order_relaxed);
			return MA_SUCCESS;
		}

		ma_result on_get_length(ma_data_source* ds, ma_uint64* length)
		{
			*length = ((music_source*)ds)->group->length;
			return MA_SUCCESS;
		}

		// No onSetLooping: the group loops itself and miniaudio's own looping stays off for it.
		ma_data_source_vtable g_music_vtable = { on_read, on_seek, on_get_format, on_get_cursor, on_get_length, nullptr, 0 };
	}

	music_group* music_create(const music_desc& desc)
	{
		ma_engine* engine = internal_engine();
		if (!engine || !desc.stems || desc.stem_count == 0 || desc.stem_count > music_max_stems) return nullptr;
		ma_resource_manager* rm = ma_engine_get_resource_manager(engine);

		music_group* g = new music_group{};
		g->engine = engine;
		g->sample_rate = ma_engine_get_sample_rate(engine);
		g->loop = desc.loop;
		g->source.group = g;
		g->scratch.resize((size_t)(k_block * k_max_channels));
		for (u32 i = 0; i < music_max_stems; ++i) g->gain_now[i].store(0.0f);
		ma_data_source_config source_config = ma_data_source_config_init();
		source_config.vtable = &g_music_vtable;
		if (ma_data_source_init(&source_config, &g->source.base) != MA_SUCCESS) { delete g; return nullptr; }
		g->source_alive = true;

		u64 longest = 0;
		for (u32 i = 0; i < desc.stem_count; ++i)
		{
			const char* path = desc.stems[i];
			stem& s = g->stems[i];
			bool streamed = false;
			// a streamed stem fills its first pages from the start frame; a decoded one is sought below
			if (!path || !*path || !open_clip(rm, path, desc.stream, desc.stream ? desc.start_frame : 0, &s.ds, &streamed))
			{
				internal_log("WARNING: music stem %u could not be opened: '%s'", i, path ? path : "(null)");
				music_destroy(g);
				return nullptr;
			}
			s.alive = true;
			s.streamed = streamed;
			g->stem_count = i + 1;
			ma_format format = ma_format_unknown;	// (a decoded buffer writes every out-parameter: none may be null)
			ma_uint32 channels = 0, rate = 0;
			ma_uint64 length = 0;
			ma_resource_manager_data_source_get_data_format(&s.ds, &format, &channels, &rate, nullptr, 0);
			ma_resource_manager_data_source_get_length_in_pcm_frames(&s.ds, &length);
			// the resource manager converts every clip to the engine rate — that shared rate is what keeps the stems locked
			if (channels == 0 || channels > k_max_channels || rate != g->sample_rate)
			{
				internal_log("WARNING: music stem %u '%s' has an unusable format (%u channels, %u Hz)", i, path, channels, rate);
				music_destroy(g);
				return nullptr;
			}
			s.channels = channels;
			s.length = length;
			if (length > longest) longest = length;
			if (streamed) g->stream = true;
			const f32 state_gain = clamp_gain(desc.state_gains ? desc.state_gains[i] : 1.0f);
			const f32 param_gain = clamp_gain(desc.param_gains ? desc.param_gains[i] : 1.0f);
			s.stage[0].value = s.stage[0].target = state_gain;
			s.stage[1].value = s.stage[1].target = param_gain;
			g->gain_now[i].store(state_gain * param_gain);
		}

		if (g->stream)
		{
			// streams loop whole files through their own seamless looping; the first stem's length is the music's
			g->length = g->stems[0].length;
			for (u32 i = 0; i < g->stem_count; ++i)
			{
				stem& s = g->stems[i];
				if (desc.loop) ma_resource_manager_data_source_set_looping(&s.ds, MA_TRUE);
				if (s.length != g->length)
					internal_log("WARNING: streamed music stem %u is %lld frames longer than stem 0 — streamed stems loop whole files and drift that much per loop; trim them to one length or decode them",
						i, (long long)s.length - (long long)g->length);
			}
			g->loop_start = 0;
			g->loop_end = g->length;
			const u64 start = g->length > 0 ? desc.start_frame % g->length : desc.start_frame;
			g->position.store(start);
		}
		else
		{
			g->length = longest;
			if (g->length == 0)
			{
				internal_log("WARNING: music stems are empty");
				music_destroy(g);
				return nullptr;
			}
			g->loop_end = desc.loop_end > 0 ? desc.loop_end : g->length;   // past a stem's end it is silent until the wrap
			g->loop_start = desc.loop_start < g->loop_end ? desc.loop_start : 0;
			const u64 end = desc.loop ? g->loop_end : g->length;
			u64 start = desc.start_frame;
			if (start >= end) start = desc.loop ? g->loop_start + (start - g->loop_start) % (g->loop_end - g->loop_start) : end;
			seek_stems(g, start);
			g->position.store(start);
		}
		publish(g, false);
		return g;
	}

	void music_destroy(music_group* g)
	{
		if (!g) return;
		for (stinger& s : g->stingers)
			if (s.state.load() != stinger_free) ma_resource_manager_data_source_uninit(&s.ds);
		for (u32 i = 0; i < g->stem_count; ++i)
			if (g->stems[i].alive) ma_resource_manager_data_source_uninit(&g->stems[i].ds);
		if (g->source_alive) ma_data_source_uninit(&g->source.base);
		delete g;
	}

	void* music_data_source(music_group* g)
	{
		return g ? &g->source : nullptr;
	}

	void music_schedule(music_group* g, music_stage stage, const f32* gains, u32 count, u64 at_frame, u64 ramp_frames)
	{
		if (!g || !gains) return;
		const u32 st = stage == music_stage::state ? 0u : 1u;
		schedule sc{};
		sc.valid = true;
		sc.at = at_frame;
		sc.ramp = ramp_frames;
		sc.count = count < music_max_stems ? count : music_max_stems;
		for (u32 i = 0; i < sc.count; ++i) sc.gains[i] = clamp_gain(gains[i]);
		g->schedule_lock.lock();
		g->pending[st] = sc;
		g->schedule_lock.unlock();
	}

	bool music_play_stinger(music_group* g, const char* path, f32 volume, u64 at_frame)
	{
		if (!g || !path || !*path) return false;
		for (stinger& s : g->stingers)
		{
			if (s.state.load(std::memory_order_acquire) != stinger_free) continue;
			if (!open_clip(ma_engine_get_resource_manager(g->engine), path, false, 0, &s.ds, nullptr))
			{
				internal_log("WARNING: music stinger could not be opened: '%s'", path);
				return false;
			}
			ma_format format = ma_format_unknown;
			ma_uint32 channels = 0, rate = 0;
			ma_resource_manager_data_source_get_data_format(&s.ds, &format, &channels, &rate, nullptr, 0);
			if (channels == 0 || channels > k_max_channels)
			{
				ma_resource_manager_data_source_uninit(&s.ds);
				return false;
			}
			s.channels = channels;
			s.at = at_frame;
			s.volume = clamp_gain(volume);
			s.state.store(stinger_armed, std::memory_order_release);
			return true;
		}
		return false;
	}

	void music_update(music_group* g)
	{
		if (!g) return;
		for (stinger& s : g->stingers)
		{
			if (s.state.load(std::memory_order_acquire) != stinger_done) continue;
			ma_resource_manager_data_source_uninit(&s.ds);
			s.state.store(stinger_free, std::memory_order_release);
		}
	}

	bool music_get_clock(music_group* g, music_clock* out)
	{
		if (!g || !out) return false;
		u64 played, position, loops;
		s64 wall;
		bool stalled;
		for (;;)
		{
			const u32 s1 = g->clock_seq.load(std::memory_order_acquire);
			if (s1 & 1u) continue;   // being written by the mixing thread right now
			played = g->snap_played.load(std::memory_order_relaxed);
			position = g->snap_position.load(std::memory_order_relaxed);
			loops = g->snap_loops.load(std::memory_order_relaxed);
			wall = g->snap_wall_ns.load(std::memory_order_relaxed);
			stalled = g->snap_stalled.load(std::memory_order_relaxed);
			std::atomic_thread_fence(std::memory_order_acquire);
			if (g->clock_seq.load(std::memory_order_relaxed) == s1) break;
		}
		out->played = played;
		out->position = position;
		out->loops = loops;
		out->loop_start = g->loop_start;
		out->loop_end = g->loop_end;
		out->length = g->length;
		out->sample_rate = g->sample_rate;
		out->seconds_since_mix = (d64)(now_ns() - wall) * 1e-9;
		out->last_state_frame = g->last_state_frame.load(std::memory_order_relaxed);
		out->stalled = stalled;
		return true;
	}

	f32 music_stem_gain(music_group* g, u32 stem_index)
	{
		if (!g || stem_index >= g->stem_count) return -1.0f;
		return g->gain_now[stem_index].load(std::memory_order_relaxed);
	}

	u64 music_render(music_group* g, f32* out, u64 frames)
	{
		return g ? mix(g, out, frames) : 0;
	}
}
