#include "AudioMusic.h"
#include "AudioEngine.h"
#include "AudioInternal.h"

#include <atomic>
#include <chrono>
#include <cstring>
#include <string>
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
		// A streamed stem's next pass is opened this long before the loop wraps (its first pages load on the resource
		// manager's job thread meanwhile).
		inline constexpr d64 k_prepare_seconds{ 4.0 };
		// A stream with nothing left whose position is within this of its known length has ended (the length the caller
		// measured and the stream's own may differ by a resampled frame or two).
		inline constexpr u64 k_end_slack{ 256 };

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

		// decoded: a file decoded into memory (rm[0]); streamed: a resource-manager stream of a file (rm[cur] plays,
		// rm[1 - cur] is the next pass, opened at the loop start ahead of the wrap); pak: a decoder over registered bytes.
		enum class stem_kind : u8 { decoded, streamed, pak };

		// streamed: none -> opening (game thread, async load) -> ready (game thread) -> retired (mixing thread, at the
		// wrap) -> none (game thread, after freeing the pass that ended)
		enum next_state : u32 { next_none = 0, next_opening, next_ready, next_retired };

		struct stem
		{
			stem_kind kind{ stem_kind::decoded };
			std::string path;
			ma_resource_manager_data_source rm[2]{};
			bool rm_alive[2]{};					// game thread
			std::atomic<u32> cur{ 0 };			// streamed: the stream playing; swapped by the mixing thread at the wrap
			std::atomic<u32> next{ next_none };
			ma_decoder decoder{};				// pak
			bool decoder_alive{ false };
			bool exhausted{ false };			// mixing thread: read to its end this pass, silent until the wrap
			u32 channels{ 0 };
			u64 length{ 0 };					// frames at the engine rate, 0 = unknown
			gain_ramp stage[2];

			ma_data_source* source()
			{
				if (kind == stem_kind::pak) return &decoder;
				return &rm[kind == stem_kind::streamed ? cur.load(std::memory_order_relaxed) : 0u];
			}
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
		bool has_streams{ false };
		u64 loop_start{ 0 };
		u64 loop_end{ 0 };			// 0 = unknown: the stems' own ends decide when it wraps
		u64 length{ 0 };			// the longest stem, 0 = unknown

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
		// A file through the resource manager (`flags`: decoded into memory, or streamed), positioned at `seek`.
		// Registered pak names are not opened here — they get their own decoder (internal_open_pak_decoder).
		bool open_rm(ma_resource_manager* rm, const char* path, ma_uint32 flags, u64 seek, ma_resource_manager_data_source* out)
		{
			ma_resource_manager_data_source_config config = ma_resource_manager_data_source_config_init();
			config.flags = flags;
			config.initialSeekPointInPCMFrames = seek;
			const bool registered = is_registered_clip(path);
#if VORTEX_PLATFORM_WINDOWS
			wchar_t wide[1024];
			if (registered) config.pFilePath = path;
			else
			{
				if (!internal_widen_path(path, wide, 1024)) return false;
				config.pFilePathW = wide;
			}
#else
			(void)registered;
			config.pFilePath = path;
#endif
			return ma_resource_manager_data_source_init_ex(rm, &config, out) == MA_SUCCESS;
		}

		// One stem to `frame` — past a known end it waits there, silent until the wrap. Decoded and pak stems move at
		// once (a pak MP3 through its seek table); a stream refills from there on the job thread, and every stem waits
		// for it (the lockstep check in mix).
		void seek_stem(stem& s, u64 frame)
		{
			u64 f = frame;
			if (s.length > 0 && f > s.length) f = s.length;
			s.exhausted = s.length > 0 && f >= s.length;
			ma_data_source_seek_to_pcm_frame(s.source(), f);
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

		// The loop wraps: every stem back to the loop start on the same frame. Streamed stems switch to their next pass,
		// opened there ahead of time — all of them or none: when one is not ready yet, the current streams refill from
		// the loop start instead and every stem waits for them, still together.
		void wrap(music_group* g)
		{
			bool swap = g->has_streams;
			for (u32 i = 0; i < g->stem_count && swap; ++i)
				if (g->stems[i].kind == stem_kind::streamed && g->stems[i].next.load(std::memory_order_acquire) != next_ready) swap = false;
			for (u32 i = 0; i < g->stem_count; ++i)
			{
				stem& s = g->stems[i];
				if (s.kind == stem_kind::streamed && swap)
				{
					s.cur.store(1u - s.cur.load(std::memory_order_relaxed), std::memory_order_relaxed);
					s.next.store(next_retired, std::memory_order_release);
					// a stem that ends before the loop start was opened past its end: silent this pass
					s.exhausted = s.length > 0 && g->loop_start >= s.length;
				}
				else seek_stem(s, g->loop_start);
			}
		}

		bool all_exhausted(music_group* g)
		{
			for (u32 i = 0; i < g->stem_count; ++i)
				if (!g->stems[i].exhausted) return false;
			return g->stem_count > 0;
		}

		// A streamed stem with nothing loaded: still refilling (false — every stem waits), or its file has ended (true:
		// silent until the wrap). A known length decides; without one, the stream's own end flag does.
		bool stream_ended(stem& s, u64 position)
		{
			if (s.length > 0) return position + k_end_slack >= s.length;
			ma_resource_manager_data_source& d = s.rm[s.cur.load(std::memory_order_relaxed)];
			return std::atomic_ref<ma_bool32>(d.backend.stream.isDecoderAtEnd).load(std::memory_order_acquire) != MA_FALSE;
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

		// Reads `n` frames of every stem (each stem exactly n — the shared cursor — or what is left of it) and of the
		// playing stingers into out.
		void mix_block(music_group* g, f32* out, u64 n)
		{
			f32* scratch = g->scratch.data();
			for (u32 i = 0; i < g->stem_count; ++i)
			{
				stem& s = g->stems[i];
				ma_uint64 got = 0;
				if (!s.exhausted)
				{
					ma_data_source_read_pcm_frames(s.source(), scratch, n, &got);
					if (got > n) got = n;
					if (got < n) s.exhausted = true;   // its end: silent for the rest of the pass
				}
				mix_stem(out, scratch, got, s.channels, s.stage[0], s.stage[1]);
				if (got < n) { s.stage[0].skip(n - got); s.stage[1].skip(n - got); }   // past a stem's end its gains move on
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
				// the loop end, or the end of a one-shot music (its longest stem); when neither is known, the end of the
				// last stem still playing decides
				const u64 end = g->loop && g->loop_end > 0 ? g->loop_end : g->length;
				if ((end > 0 && position >= end) || (end == 0 && all_exhausted(g)))
				{
					if (!g->loop) { ended = true; break; }
					wrap(g);
					position = g->loop_start;
					++loops;
					continue;
				}
				if (end > 0 && end - position < n) n = end - position;
				// a stream that has not refilled holds every stem back, so they stay together; one whose file ended is
				// silence, not a wait (#437 review: a shorter streamed stem stalled a one-shot music for good)
				for (u32 i = 0; i < g->stem_count && n > 0; ++i)
				{
					stem& s = g->stems[i];
					if (s.kind != stem_kind::streamed || s.exhausted) continue;
					ma_uint64 avail = 0;
					ma_resource_manager_data_source_get_available_frames(&s.rm[s.cur.load(std::memory_order_relaxed)], &avail);
					if (avail > 0) { if (avail < n) n = avail; continue; }
					if (stream_ended(s, position)) s.exhausted = true;
					else n = 0;
				}
				if (n == 0) { stalled = true; break; }
				const u64 to_event = frames_to_next_event(g, played);
				if (to_event < n) n = to_event;

				mix_block(g, out ? out + done * 2 : nullptr, n);
				played += n;
				position += n;
				done += n;
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
			if (g->loop && g->loop_end > g->loop_start) { if (f >= g->loop_end) f = g->loop_start + (f - g->loop_start) % (g->loop_end - g->loop_start); }
			else if (g->length > 0 && f > g->length) f = g->length;
			for (u32 i = 0; i < g->stem_count; ++i) seek_stem(g->stems[i], f);
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

		// The channel count of a stem's source (a decoded buffer writes every out-parameter: none may be null).
		bool stem_format(stem& s, u32 engine_rate, u32* channels)
		{
			ma_format format = ma_format_unknown;
			ma_uint32 ch = 0, rate = 0;
			if (ma_data_source_get_data_format(s.source(), &format, &ch, &rate, nullptr, 0) != MA_SUCCESS) return false;
			*channels = ch;
			// every stem reaches the mix at the engine rate — that shared rate is what keeps them locked
			return ch > 0 && ch <= k_max_channels && rate == engine_rate;
		}
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

		for (u32 i = 0; i < desc.stem_count; ++i)
		{
			const char* path = desc.stems[i];
			stem& s = g->stems[i];
			bool opened = false;
			if (path && *path)
			{
				s.path = path;
				if (is_registered_clip(path))
				{
					// a shipped game's stem: decoded on demand from the pak bytes, at the engine rate, an MP3 through
					// its seek table — no load, no PCM in memory
					s.kind = stem_kind::pak;
					opened = s.decoder_alive = internal_open_pak_decoder(path, true, &s.decoder);
					if (opened)
					{
						ma_uint64 len = 0;
						ma_decoder_get_length_in_pcm_frames(&s.decoder, &len);   // cheap here (seek table / Vorbis pull mode)
						s.length = (u64)len;
					}
				}
				else
				{
					s.kind = desc.decode ? stem_kind::decoded : stem_kind::streamed;
					const ma_uint32 flags = desc.decode ? MA_RESOURCE_MANAGER_DATA_SOURCE_FLAG_DECODE : MA_RESOURCE_MANAGER_DATA_SOURCE_FLAG_STREAM;
					// a stream fills its first pages from the start frame; a decoded stem is sought below
					opened = s.rm_alive[0] = open_rm(rm, path, flags, desc.decode ? 0 : desc.start_frame, &s.rm[0]);
					if (opened)
					{
						ma_uint64 len = 0;
						ma_data_source_get_length_in_pcm_frames(&s.rm[0], &len);   // a decoded file / a file stream keeps it
						s.length = (u64)len;
					}
				}
			}
			g->stem_count = i + 1;
			if (!opened || !stem_format(s, g->sample_rate, &s.channels))
			{
				internal_log("WARNING: music stem %u could not be opened (or has no usable format): '%s'", i, path ? path : "(null)");
				music_destroy(g);
				return nullptr;
			}
			// a file-streamed OGG cannot tell its length (push mode): without it a one-shot music could not end on time
			if (s.length == 0 && s.kind == stem_kind::streamed) s.length = internal_probe_length(path);
			if (s.kind == stem_kind::streamed) g->has_streams = true;
			if (s.length > g->length) g->length = s.length;
			const f32 state_gain = clamp_gain(desc.state_gains ? desc.state_gains[i] : 1.0f);
			const f32 param_gain = clamp_gain(desc.param_gains ? desc.param_gains[i] : 1.0f);
			s.stage[0].value = s.stage[0].target = state_gain;
			s.stage[1].value = s.stage[1].target = param_gain;
			g->gain_now[i].store(state_gain * param_gain);
		}

		// the loop region (the stems themselves never loop: the group wraps them all on one frame)
		g->loop_end = desc.loop_end > 0 ? desc.loop_end : g->length;   // past a stem's end it is silent until the wrap
		g->loop_start = desc.loop_start < g->loop_end ? desc.loop_start : 0;
		u64 start = desc.start_frame;
		if (g->loop && g->loop_end > g->loop_start && start >= g->loop_end)
			start = g->loop_start + (start - g->loop_start) % (g->loop_end - g->loop_start);
		else if (!g->loop && g->length > 0 && start > g->length)
			start = g->length;
		for (u32 i = 0; i < g->stem_count; ++i)
		{
			stem& s = g->stems[i];
			if (s.kind == stem_kind::streamed) s.exhausted = s.length > 0 && start >= s.length;   // opened at the start frame
			else seek_stem(s, start);
		}
		g->position.store(start);
		publish(g, false);
		return g;
	}

	void music_destroy(music_group* g)
	{
		if (!g) return;
		for (stinger& s : g->stingers)
			if (s.state.load() != stinger_free) ma_resource_manager_data_source_uninit(&s.ds);
		for (u32 i = 0; i < g->stem_count; ++i)
		{
			stem& s = g->stems[i];
			for (u32 k = 0; k < 2; ++k)
				if (s.rm_alive[k]) ma_resource_manager_data_source_uninit(&s.rm[k]);
			if (s.decoder_alive) ma_decoder_uninit(&s.decoder);
		}
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
			if (!open_rm(ma_engine_get_resource_manager(g->engine), path, MA_RESOURCE_MANAGER_DATA_SOURCE_FLAG_DECODE, 0, &s.ds))
			{
				internal_log("WARNING: music stinger could not be opened: '%s'", path);
				return false;
			}
			ma_format format = ma_format_unknown;
			ma_uint32 channels = 0, rate = 0;
			ma_data_source_get_data_format(&s.ds, &format, &channels, &rate, nullptr, 0);
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
		if (!g->has_streams) return;

		// streamed stems: free the pass that ended, mark opened next passes ready, open the next pass ahead of the wrap
		const bool prepare = g->loop && g->loop_end > g->loop_start && [&] {
			const u64 position = g->position.load(std::memory_order_relaxed);
			const u64 ahead = (u64)(k_prepare_seconds * g->sample_rate);
			return position + ahead >= g->loop_end || g->loop_end - g->loop_start <= ahead;
		}();
		ma_resource_manager* rm = ma_engine_get_resource_manager(g->engine);
		for (u32 i = 0; i < g->stem_count; ++i)
		{
			stem& s = g->stems[i];
			if (s.kind != stem_kind::streamed) continue;
			const u32 other = 1u - s.cur.load(std::memory_order_acquire);
			u32 state = s.next.load(std::memory_order_acquire);
			if (state == next_retired)
			{
				// the mixing thread switched to the next pass at the wrap: the stream it left is free
				if (s.rm_alive[other]) ma_resource_manager_data_source_uninit(&s.rm[other]);
				s.rm_alive[other] = false;
				s.next.store(next_none, std::memory_order_release);
				state = next_none;
			}
			if (state == next_opening)
			{
				const ma_result r = ma_resource_manager_data_source_result(&s.rm[other]);
				if (r == MA_SUCCESS) s.next.store(next_ready, std::memory_order_release);
				else if (r != MA_BUSY)
				{
					internal_log("WARNING: music stem '%s' could not reopen at the loop start (%s)", s.path.c_str(), ma_result_description(r));
					ma_resource_manager_data_source_uninit(&s.rm[other]);
					s.rm_alive[other] = false;
					s.next.store(next_none, std::memory_order_release);
				}
			}
			else if (state == next_none && prepare)
			{
				// loads on the job thread, positioned at the loop start; no length scan (an MP3 would read its whole file)
				const ma_uint32 flags = MA_RESOURCE_MANAGER_DATA_SOURCE_FLAG_STREAM | MA_RESOURCE_MANAGER_DATA_SOURCE_FLAG_ASYNC | MA_RESOURCE_MANAGER_DATA_SOURCE_FLAG_UNKNOWN_LENGTH;
				if (open_rm(rm, s.path.c_str(), flags, g->loop_start, &s.rm[other]))
				{
					s.rm_alive[other] = true;
					s.next.store(next_opening, std::memory_order_release);
				}
			}
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
