#pragma once

#include "../Engine/Runtime/ResourceManager.h"
#include "../Engine/Runtime/Systems/AudioSystem.h"
#include "../Engine/Runtime/Systems/AudioEngine.h"
#include "../Engine/Runtime/Systems/AudioMixer.h"
#include "../Engine/Runtime/Systems/AudioReverb.h"
#include "../Engine/Runtime/Systems/AudioVoices.h"

#include "Test.h"

#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <iostream>
#include <random>
#include <string>
#include <thread>
#include <vector>


using namespace vortex;

// Audio smoke test (issue #6): device init, decode via ResourceManager::load_audio,
// audible one-shot + test beep, unload, clean shutdown. Runs non-interactively.
// Set VORTEX_AUDIO_TEST_DIR to a folder of .wav/.mp3/.flac/.ogg files to also
// verify every decoder against real assets.
class engine_test : public test
{
public:
	bool initialize() override
	{
		std::cout << "Audio Test Initialized\n";
		runtime::resource_manager::initialize();
		runtime::systems::initialize_audio();
		return runtime::systems::audio_initialized();
	}

	void run() override
	{
		check("audio engine object created", runtime::audio::is_initialized());
		std::cout << (runtime::audio::has_device()
			? "[info] real output device opened\n"
			: "[info] no output device - silent mode (expected on headless machines)\n");

		// -- decode a generated wav through the ResourceManager path --------------
		const std::string wav_path = write_test_wav();
		check("test wav written", !wav_path.empty());

		auto handle = runtime::resource_manager::load_audio(wav_path.c_str());
		check("load_audio returns valid handle", handle.is_valid());
		check("wav decoded into sound cache", runtime::audio::is_loaded(wav_path.c_str()));

		// -- bad file must fail decode but not crash ------------------------------
		const std::string bad_path = write_garbage_file();
		auto bad_handle = runtime::resource_manager::load_audio(bad_path.c_str());
		check("garbage file: handle still valid (generic resource)", bad_handle.is_valid());
		check("garbage file: NOT in sound cache", !runtime::audio::is_loaded(bad_path.c_str()));

		// -- optional: real assets for every decoder ------------------------------
		load_optional_test_assets();

		// -- audible playback ------------------------------------------------------
		check("one-shot playback accepted", runtime::audio::play_one_shot(wav_path.c_str(), 0.8f));
		runtime::audio::play_test_beep();
		tick_seconds(1.2f);

		// -- voice pool (issue #7) -------------------------------------------------
		run_voice_tests(wav_path);

		// -- streaming playback (issue #10) -----------------------------------------
		run_streaming_tests(wav_path);

		// -- playback position, seeking, scheduled starts (issue #465) ----------------
		run_position_tests();

		// -- adaptive music: sample-locked stem groups (issue #437) ---------------------
		run_music_tests();

		// -- mixer buses + ducking (issue #13) ---------------------------------------
		run_mixer_tests(wav_path);

		// -- reverb send bus (issue #15) ----------------------------------------------
		run_reverb_tests(wav_path);

		// -- fade envelopes (issue #17) --------------------------------------------------
		run_fade_tests(wav_path);

		// -- optional: file-streamed OGGs know their length (#465 / #437 review) -----------
		run_optional_ogg_tests();

		// -- 3D spatialization measurement demo (issue #9) --------------------------
		// Opt-in: VORTEX_AUDIO_SPATIAL_DEMO=<phase-file>. An external meter samples
		// per-channel device peaks and correlates them against the phase timestamps.
		run_spatial_demo_if_requested(wav_path);

		// -- unload / refcount -----------------------------------------------------
		runtime::resource_manager::unload(handle);
		check("unload releases cached sound", !runtime::audio::is_loaded(wav_path.c_str()));

		std::remove(wav_path.c_str());
		std::remove(bad_path.c_str());

		std::cout << "\nRESULT: " << _passed << "/" << (_passed + _failed)
			<< (_failed == 0 ? " - ALL PASSED\n" : " - FAILURES!\n");
	}

	void shutdown() override
	{
		runtime::systems::shutdown_audio();
		runtime::resource_manager::shutdown();
		std::cout << "Audio Test Shutdown\n";
	}

private:
	// Plays a scripted sequence of spatial configurations, writing "<phase> <ms>"
	// (platform::tick_count_ms) lines so the external per-channel peak meter can prove
	// distance attenuation, spatial-blend mixing and stereo panning.
	void run_spatial_demo_if_requested(const std::string& wav)
	{
		char* phase_file = nullptr;
		size_t len = 0;
		if (_dupenv_s(&phase_file, &len, "VORTEX_AUDIO_SPATIAL_DEMO") != 0 || !phase_file) return;
		std::string out_path(phase_file);
		free(phase_file);

		using namespace runtime::audio;
		FILE* f = nullptr;
		if (fopen_s(&f, out_path.c_str(), "w") != 0 || !f) return;
		auto mark = [&](const char* phase)
		{
			fprintf(f, "%s %llu\n", phase, (unsigned long long)platform::tick_count_ms());
			fflush(f);
			std::cout << "[spatial] " << phase << "\n";
		};

		set_listener(0, 0, 0, 0, 0, 1, 0, 1, 0); // origin, facing +z

		voice_params p{};
		p.loop = true;
		p.volume = 1.0f;
		voice_handle v = voice_play(wav.c_str(), p);

		voice_spatial sp{};
		sp.spatial_blend = 1.0f;
		sp.min_distance = 1.0f;
		sp.max_distance = 30.0f;
		sp.rolloff_mode = 0;   // logarithmic/inverse
		sp.doppler_level = 0.0f;
		voice_set_spatial(v, sp);

		voice_set_position(v, 0, 0, 2);      mark("near");       tick_seconds(1.6f);
		voice_set_position(v, 0, 0, 25);     mark("far");        tick_seconds(1.6f);
		sp.spatial_blend = 0.0f; voice_set_spatial(v, sp);
		                                     mark("farblend0");  tick_seconds(1.6f);
		sp.spatial_blend = 0.5f; voice_set_spatial(v, sp);
		                                     mark("farblend05"); tick_seconds(1.6f);
		sp.spatial_blend = 1.0f; voice_set_spatial(v, sp);
		voice_set_position(v, -8, 0, 0.5f);  mark("xneg");       tick_seconds(1.6f);
		voice_set_position(v, 8, 0, 0.5f);   mark("xpos");       tick_seconds(1.6f);
		// Exact standalone-scene geometry (camera listener + far emitter) — must
		// attenuate to ~0.07, catches environment-specific regressions.
		set_listener(0, 1.7f, 4.6f, 0, -0.15f, -0.99f, 0, 1, 0);
		voice_set_position(v, 14, 1, 0);     mark("sageom");     tick_seconds(1.6f);

		// Same geometry but replaying the component bridge's PER-FRAME push pattern
		// (PushProperties + listener each tick) — catches repeated-setter regressions.
		mark("perframe");
		{
			const float dt = 1.0f / 60.0f;
			for (float t = 0.0f; t < 1.6f; t += dt)
			{
				set_listener(0, 1.7f, 4.6f, 0, -0.15f, -0.99f, 0, 1, 0);
				voice_set_volume(v, 0.9f);
				voice_set_pitch(v, 1.0f);
				voice_set_pan(v, 0.0f);
				voice_set_spatial(v, sp);
				voice_set_position(v, 14, 1, 0);
				runtime::systems::update_audio(dt);
				std::this_thread::sleep_for(std::chrono::milliseconds(16));
			}
		}
		mark("end");

		voice_stop(v);
		fclose(f);
	}

	static size_t working_set()
	{
		return platform::process_resident_bytes();
	}

	// Streaming (issue #10): a long clip must NOT hold its full decoded PCM in
	// memory, loops must survive the buffer boundary, and pak-style registered
	// memory blobs must stream like files.
	void run_streaming_tests(const std::string& short_wav)
	{
		using namespace runtime::audio;

		// 5 minutes mono 16-bit — ~26 MB encoded, ~50-100 MB as decoded PCM.
		const std::string long_wav = write_wav("vortex_audio_long.wav", 300, 110.0f);
		check("long test wav written", !long_wav.empty());

		voice_params quiet{};
		quiet.volume = 0.0f; // memory measurement, not an audibility test

		// Stream FIRST (heap is cold), decode second — the other way round the
		// allocator reuses the freed decode pages and hides the stream footprint.
		const size_t mem0 = working_set();
		voice_params stream_params = quiet;
		stream_params.stream = true;
		stream_params.loop = true;
		voice_handle streamed = voice_play(long_wav.c_str(), stream_params);
		check("streamed long voice starts", streamed != invalid_voice);
		tick_seconds(0.5f);
		check("streamed voice is playing", voice_is_playing(streamed));
		const size_t mem_stream = working_set();
		voice_stop(streamed);

		voice_handle decoded = voice_play(long_wav.c_str(), quiet);
		check("decoded long voice starts", decoded != invalid_voice);
		const size_t mem_decode = working_set();
		voice_stop(decoded);

		const long long stream_delta = (long long)mem_stream - (long long)mem0;
		const long long decode_delta = (long long)mem_decode - (long long)mem0;
		std::cout << "[info] memory delta: streamed " << (stream_delta / (1024 * 1024))
			<< " MB vs decoded " << (decode_delta / (1024 * 1024)) << " MB\n";
		check("streaming uses a fraction of full-decode memory",
			decode_delta > 30LL * 1024 * 1024 && stream_delta * 4 < decode_delta);
		unload_sound(long_wav.c_str());

		// Seamless loop: a 0.5 s clip streamed with loop must still be playing
		// well past several wrap-arounds.
		voice_params loop_stream{};
		loop_stream.volume = 0.05f;
		loop_stream.loop = true;
		loop_stream.stream = true;
		voice_handle looped = voice_play(short_wav.c_str(), loop_stream);
		check("streamed loop starts", looped != invalid_voice);
		tick_seconds(1.6f); // >3 wrap-arounds
		check("streamed loop survives wrap-around", voice_is_playing(looped));
		voice_stop(looped);

		// Pak-style path: encoded bytes registered in memory stream like a file
		// (this is what shipped .vpak builds do — no loose files, no temp extract).
		std::vector<uint8_t> bytes;
		{
			FILE* f = nullptr;
			if (fopen_s(&f, long_wav.c_str(), "rb") == 0 && f)
			{
				fseek(f, 0, SEEK_END);
				bytes.resize((size_t)_ftelli64(f));
				fseek(f, 0, SEEK_SET);
				fread(bytes.data(), 1, bytes.size(), f);
				fclose(f);
			}
		}
		check("registered pak blob accepted",
			register_clip_data("vpak://longtest.wav", bytes.data(), bytes.size()));
		voice_handle pak_stream = voice_play("vpak://longtest.wav", stream_params);
		check("registered blob streams", pak_stream != invalid_voice);
		tick_seconds(0.4f);
		check("registered stream is playing", voice_is_playing(pak_stream));

		// Registered names must also work for the normal decode path.
		std::vector<uint8_t> short_bytes;
		{
			FILE* f = nullptr;
			if (fopen_s(&f, short_wav.c_str(), "rb") == 0 && f)
			{
				fseek(f, 0, SEEK_END);
				short_bytes.resize((size_t)_ftelli64(f));
				fseek(f, 0, SEEK_SET);
				fread(short_bytes.data(), 1, short_bytes.size(), f);
				fclose(f);
			}
		}
		register_clip_data("vpak://shorttest.wav", short_bytes.data(), short_bytes.size());
		voice_params decoded_pak{};
		decoded_pak.volume = 0.05f;
		voice_handle pak_decoded = voice_play("vpak://shorttest.wav", decoded_pak);
		check("registered blob decodes (non-stream)", pak_decoded != invalid_voice);

		// Music + two ambience beds simultaneously.
		voice_handle s2 = voice_play(long_wav.c_str(), stream_params);
		voice_handle s3 = voice_play(short_wav.c_str(), loop_stream);
		tick_seconds(0.4f);
		check("three simultaneous streams play",
			voice_is_playing(pak_stream) && voice_is_playing(s2) && voice_is_playing(s3));

		// An undecodable streaming clip must be rejected BEFORE slot selection —
		// otherwise it would steal (and kill) a live voice on every retry.
		const std::string garbage = write_garbage_file();
		voice_params bad_stream{};
		bad_stream.stream = true;
		const u32 active_before = voices_active_count();
		check("corrupt streaming clip rejected without stealing",
			voice_play(garbage.c_str(), bad_stream) == invalid_voice
			&& voices_active_count() == active_before);
		std::remove(garbage.c_str());

		voice_stop(pak_stream);
		voice_stop(pak_decoded);
		voice_stop(s2);
		voice_stop(s3);

		std::remove(long_wav.c_str());
	}

	// A (mixer clock, voice cursor) pair taken while the mixer is idle: the cursor advances DURING a mix and the clock
	// at its end, so a pair read across a mix is skewed by a whole block. Two reads 2 ms apart that agree saw no mix
	// in between (a mix takes microseconds, a device period ~10 ms). False if no quiet window was found.
	bool quiet_position(runtime::audio::voice_handle v, u64* clock, u64* cursor)
	{
		using namespace runtime::audio;
		for (int attempt = 0; attempt < 400; ++attempt)
		{
			u64 c1 = 0, c2 = 0;
			const u64 t1 = engine_time_frames();
			voice_get_position(v, &c1, nullptr, nullptr);
			std::this_thread::sleep_for(std::chrono::milliseconds(2));   // (silent mode mixes only inside update_audio)
			const u64 t2 = engine_time_frames();
			voice_get_position(v, &c2, nullptr, nullptr);
			if (t1 == t2 && c1 == c2) { *clock = t1; *cursor = c1; return true; }
		}
		return false;
	}

	// Playback position (issue #465): the cursor follows the mixer at the voice's pitch, holds while paused, seeks
	// decoded and streamed clips, starts from a given position, and a scheduled start lands on its exact mixer frame
	// (miniaudio slipped it to the next device period before the VORTEX PATCH in ma_node_read_pcm_frames).
	void run_position_tests()
	{
		using namespace runtime::audio;
		const std::string clip = write_wav("vortex_audio_position.wav", 3.0f, 330.0f);   // 3 s, 44.1 kHz mono
		const u32 rate = engine_sample_rate();
		check("mixer clock has a sample rate", rate > 0);

		voice_params p{};
		p.loop = true;
		p.volume = 0.05f;
		voice_handle v = voice_play(clip.c_str(), p);
		u64 cursor = 0, length = 0;
		u32 voice_rate = 0;
		check("position of a playing voice", voice_get_position(v, &cursor, &length, &voice_rate));
		// decoded clips are converted to the engine rate on load: 3 s of 44.1 kHz = 3 s of engine frames
		check("clip length in the voice's frames", voice_rate == rate && length > 0
			&& std::fabs((double)length / voice_rate - 3.0) < 0.002);

		// pitch 2: the cursor runs twice as fast as the mixer clock (quiet pairs: exact to a block or two)
		voice_set_pitch(v, 2.0f);
		tick_seconds(0.1f);
		u64 t0 = 0, c0 = 0, t1 = 0, c1 = 0;
		const bool q0 = quiet_position(v, &t0, &c0);
		tick_seconds(0.4f);
		const bool q1 = quiet_position(v, &t1, &c1);
		const double ratio = (t1 > t0) ? (double)((c1 + length - c0) % length) / (double)(t1 - t0) : 0.0;
		std::printf("[info] position at pitch 2: %.4f cursor frames per mixer frame\n", ratio);
		check("cursor advances at the voice's pitch", q0 && q1 && std::fabs(ratio - 2.0) < 0.01);
		voice_set_pitch(v, 1.0f);

		// pause holds the position, resume continues from it
		voice_pause(v);
		tick_seconds(0.05f);
		u64 paused0 = 0, paused1 = 0;
		voice_get_position(v, &paused0, nullptr, nullptr);
		tick_seconds(0.3f);
		voice_get_position(v, &paused1, nullptr, nullptr);
		voice_resume(v);
		tick_seconds(0.3f);
		u64 resumed = 0;
		voice_get_position(v, &resumed, nullptr, nullptr);
		check("pause holds the position, resume continues", paused0 == paused1 && resumed != paused1);

		// seek: the target reads back at once (before the mixer applied it) and playback continues from there
		voice_seek(v, 1.25);
		u64 sought = 0;
		voice_get_position(v, &sought, nullptr, nullptr);
		tick_seconds(0.2f);
		u64 after = 0;
		voice_get_position(v, &after, nullptr, nullptr);
		check("seek reads back its target", sought == (u64)(1.25 * rate + 0.5));
		check("playback continues from the seek target", after > sought && after < sought + rate / 2);
		voice_seek(v, 4.0);   // looping: wraps to ~1 s
		voice_get_position(v, &sought, nullptr, nullptr);
		check("seeking a looping voice past its end wraps", sought == (u64)(4.0 * rate + 0.5) % length);
		voice_stop(v);
		check("a stale handle has no position", !voice_get_position(v, &cursor, nullptr, nullptr));

		// a start position (AudioSource.Time set before Play) on a decoded and a streamed voice
		voice_params from{};
		from.volume = 0.05f;
		from.start_position = 2.0;
		voice_handle d = voice_play(clip.c_str(), from);
		voice_get_position(d, &cursor, nullptr, nullptr);
		check("a decoded voice starts at its start position", cursor >= (u64)(2.0 * rate) && cursor < (u64)(2.2 * rate));
		voice_stop(d);
		from.stream = true;
		voice_handle s = voice_play(clip.c_str(), from);
		tick_seconds(0.1f);
		voice_get_position(s, &cursor, &length, &voice_rate);
		check("a streamed voice starts at its start position",
			voice_rate > 0 && cursor >= (u64)(2.0 * voice_rate) && cursor < (u64)(2.4 * voice_rate));
		// seeking a stream: the stream refills from the new spot
		from.start_position = 0.0;
		from.loop = true;
		voice_handle s2 = voice_play(clip.c_str(), from);
		tick_seconds(0.1f);
		voice_seek(s2, 2.5);
		tick_seconds(0.25f);
		voice_get_position(s2, &cursor, nullptr, &voice_rate);
		check("seeking a streamed voice", voice_rate > 0 && cursor >= (u64)(2.5 * voice_rate) && cursor < (u64)(2.9 * voice_rate));
		voice_stop(s);
		voice_stop(s2);

		// scheduled start: silent until its mixer frame, then the cursor counts exactly the frames since that frame
		voice_params later{};
		later.volume = 0.05f;
		later.loop = true;
		const u64 start = engine_time_frames() + rate / 4;   // 250 ms ahead
		later.start_time = start;
		voice_handle sch = voice_play(clip.c_str(), later);
		check("a scheduled voice waits for its start", sch != invalid_voice && !voice_is_playing(sch));
		tick_seconds(0.5f);
		u64 clock = 0;
		const bool quiet = quiet_position(sch, &clock, &cursor);
		const long long error = (long long)cursor - (long long)(clock - start);
		std::printf("[info] scheduled start: cursor %llu, mixer frames since the scheduled frame %llu (error %lld frames)\n",
			(unsigned long long)cursor, (unsigned long long)(clock - start), error);
		check("a scheduled voice plays after its start", voice_is_playing(sch));
		check("a scheduled start lands on its exact mixer frame (+-2)", quiet && clock > start && error >= -2 && error <= 2);
		voice_stop(sch);
		std::remove(clip.c_str());

		// A shipped game's MP3 (pak bytes, streamed and not): reading the position every frame must leave the decoder the
		// mixer reads alone (#465 review: each read queried the length — a whole-file scan, 27-43 ms at 2:30 of a 3-minute
		// file — and moved the decoder under the mixer), and a seek goes through the MP3 seek table.
		const std::vector<uint8_t> mp3 = silent_mp3(180.0);
		check("pak MP3 registered", register_clip_data("vpak://music.mp3", mp3.data(), mp3.size()));
		for (int stream = 1; stream >= 0; --stream)
		{
			voice_params mp{};
			mp.volume = 0.05f;
			mp.loop = true;
			mp.priority = 0;
			mp.stream = stream != 0;
			voice_handle m = voice_play("vpak://music.mp3", mp);
			tick_seconds(0.3f);
			voice_seek(m, 150.0);
			tick_seconds(0.3f);
			double worst_ms = 0.0;
			u64 prev = 0, len = 0;
			u32 r = 0, backwards = 0, jumps = 0;
			for (int i = 0; i < 60; ++i)
			{
				u64 cur = 0;
				const auto t0 = std::chrono::steady_clock::now();
				voice_get_position(m, &cur, &len, &r);
				const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
				if (ms > worst_ms) worst_ms = ms;
				if (i > 0 && cur < prev) ++backwards;
				if (i > 0 && r && cur > prev + r / 5) ++jumps;   // more than 200 ms ahead between two reads ~16 ms apart
				prev = cur;
				tick_seconds(0.016f);
			}
			const double at = r ? (double)prev / r : -1.0, length_s = r ? (double)len / r : -1.0;
			std::printf("[info] pak MP3 (%s): worst position read %.3f ms, cursor %.2f s after a seek to 150 s, length %.2f s, backwards %u, jumps %u\n",
				stream ? "streamed" : "not streamed", worst_ms, at, length_s, backwards, jumps);
			check(stream ? "a streamed pak MP3: position reads are free and leave playback alone; the seek lands"
			             : "a pak MP3 played decoded: position reads are free and leave playback alone; the seek lands",
				m != invalid_voice && worst_ms < 0.5 && backwards == 0 && jumps == 0 && std::fabs(length_s - 180.0) < 0.1 && at > 150.2 && at < 153.0);
			voice_stop(m);
		}
	}

	// Renders `frames` of a music group in mixer-sized blocks; returns the largest |sample| from frame `skip` on.
	static f32 render_peak(runtime::audio::music_group* g, u64 frames, u64 skip = 0, std::vector<f32>* keep = nullptr)
	{
		std::vector<f32> block(480 * 2);
		f32 peak = 0.0f;
		for (u64 done = 0; done < frames;)
		{
			const u64 n = frames - done < 480 ? frames - done : 480;
			runtime::audio::music_render(g, block.data(), n);
			for (u64 f = 0; f < n; ++f)
			{
				if (keep) { keep->push_back(block[f * 2]); }
				if (done + f < skip) continue;
				const f32 v = std::fabs(block[f * 2]) > std::fabs(block[f * 2 + 1]) ? std::fabs(block[f * 2]) : std::fabs(block[f * 2 + 1]);
				if (v > peak) peak = v;
			}
			done += n;
		}
		return peak;
	}

	// Adaptive music (issue #437): the stems of a group share one cursor — B = -A cancels to digital silence for
	// ten minutes of looping, so a drift of a single sample would show — a state schedule begins on its exact frame,
	// a stinger on its exact frame, streamed stems stay together through page refills, and the group plays as one
	// voice on the music bus. Stems are written at the engine rate, so the resource manager passes them unconverted.
	void run_music_tests()
	{
		using namespace runtime::audio;
		const u32 rate = engine_sample_rate();
		const u64 loop = (u64)rate * 4;   // a 4 s loop (two bars of 4/4 at 120 BPM)
		std::vector<float> a((size_t)loop), b((size_t)loop + rate / 2), shot(2000, 0.25f);
		for (size_t i = 0; i < a.size(); ++i)
			a[i] = 0.3f * sinf((float)i * 220.0f * 2.0f * 3.14159265f / (float)rate) + ((i % (rate / 2)) < 64 ? 0.4f : 0.0f);   // tone + clicks
		for (size_t i = 0; i < b.size(); ++i)
			b[i] = i < a.size() ? -a[i] : 0.2f * sinf((float)i * 330.0f * 2.0f * 3.14159265f / (float)rate);   // -A, then a tail past the loop
		const std::string pa = write_wav_samples("vortex_music_a.wav", rate, a);
		const std::string pb = write_wav_samples("vortex_music_b.wav", rate, b);
		const std::string pshot = write_wav_samples("vortex_music_stinger.wav", rate, shot);
		const char* stems[] = { pa.c_str(), pb.c_str() };
		const f32 solo[] = { 1.0f, 0.0f }, both[] = { 1.0f, 1.0f }, none[] = { 0.0f, 0.0f };

		music_desc d{};
		d.stems = stems;
		d.stem_count = 2;
		d.state_gains = solo;
		d.loop_end = loop;
		d.decode = true;   // rendered faster than real time below: decoded stems never wait for a refill
		music_group* g = music_create(d);
		music_group* ref = music_create(d);   // the same music with B muted for good: the switch is measured against it
		check("a music group opens its stems", g != nullptr && ref != nullptr);
		if (!g || !ref) { music_destroy(g); music_destroy(ref); return; }
		check("a stem plays alone while the other is muted", render_peak(g, rate / 2) > 0.25f && render_peak(ref, rate / 2) > 0.25f);

		// a state switch begins on its exact frame: B ramps in from frame F
		music_clock c{};
		music_get_clock(g, &c);
		const u64 start = c.played;
		music_schedule(g, music_stage::state, both, 2, start + 777, 64);
		std::vector<f32> mixed, alone;
		render_peak(g, 2048, 0, &mixed);
		render_peak(ref, 2048, 0, &alone);
		long long first_diff = -1;
		for (size_t i = 0; i < mixed.size() && first_diff < 0; ++i) if (mixed[i] != alone[i]) first_diff = (long long)i;
		music_get_clock(g, &c);
		std::printf("[info] state switch scheduled for frame +777 began at +%lld (clock: %llu)\n", first_diff, (unsigned long long)(c.last_state_frame - start));
		check("a scheduled state switch begins on its exact frame", first_diff == 777 && c.last_state_frame == start + 777);
		f32 step = 0.0f;
		for (size_t i = 778; i < 777 + 64 && i < mixed.size(); ++i)
		{
			const f32 dv = std::fabs((mixed[i] - alone[i]) - (mixed[i - 1] - alone[i - 1]));
			if (dv > step) step = dv;
		}
		check("the switch ramps (no click: B's level moves in small steps)", step < 0.05f);

		// sample lock: A + (-A) = silence for ten minutes of looping (150 passes; B is 0.5 s longer than the loop)
		const u64 ten_minutes = (u64)rate * 600;
		music_get_clock(g, &c);
		const auto mix_t0 = std::chrono::steady_clock::now();
		const f32 residue = render_peak(g, ten_minutes - (c.played - start));
		const double mix_ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - mix_t0).count();
		music_get_clock(g, &c);
		std::printf("[info] 10 min of looping: %llu passes, largest residue of A + (-A) %.9f; mixing cost %.1f us per 10 ms of two-stem music (%.0fx real time, this build)\n",
			(unsigned long long)c.loops, residue, mix_ms * 1000.0 / 60000.0, 600000.0 / (mix_ms > 0 ? mix_ms : 1));
		check("two stems stay sample-aligned for 10 minutes of looping", residue == 0.0f && c.loops >= 149);

		// a stinger begins on its exact frame
		music_schedule(g, music_stage::state, none, 2, 0, 64);
		render_peak(g, 1024);
		music_get_clock(g, &c);
		check("a stinger is accepted", music_play_stinger(g, pshot.c_str(), 1.0f, c.played + 333));
		std::vector<f32> hit;
		render_peak(g, 1024, 0, &hit);
		long long first_hit = -1;
		for (size_t i = 0; i < hit.size() && first_hit < 0; ++i) if (hit[i] != 0.0f) first_hit = (long long)i;
		check("a stinger begins on its exact frame", first_hit == 333 && std::fabs(hit[400] - 0.25f) < 1e-4f);
		render_peak(g, 4096);
		music_update(g);
		bool all_free = true;
		for (u32 i = 0; i < music_max_stingers; ++i) all_free = all_free && music_play_stinger(g, pshot.c_str(), 0.0f, 0);
		check("finished stingers free their slots", all_free);
		music_destroy(g);
		music_destroy(ref);

		// pak stems (a shipped game): their own decoders over the registered bytes, looping a region inside the files
		// (a 0.5 s pickup, then 3 s): A + (-A) still cancels to digital silence through every wrap
		{
			auto bytes_of = [](const std::string& p) {
				std::vector<uint8_t> v;
				FILE* f = nullptr;
				if (fopen_s(&f, p.c_str(), "rb") == 0 && f)
				{
					fseek(f, 0, SEEK_END); v.resize((size_t)_ftelli64(f)); fseek(f, 0, SEEK_SET);
					fread(v.data(), 1, v.size(), f); fclose(f);
				}
				return v;
			};
			const auto ba = bytes_of(pa), bb = bytes_of(pb);
			register_clip_data("vpak://music_a.wav", ba.data(), ba.size());
			register_clip_data("vpak://music_b.wav", bb.data(), bb.size());
			const char* pak[] = { "vpak://music_a.wav", "vpak://music_b.wav" };
			music_desc dp{};
			dp.stems = pak;
			dp.stem_count = 2;
			dp.state_gains = both;
			dp.loop_start = rate / 2;
			dp.loop_end = rate / 2 + (u64)rate * 3;
			music_group* p = music_create(dp);
			check("a music group opens pak stems", p != nullptr);
			if (p)
			{
				const f32 pak_residue = render_peak(p, (u64)rate * 30);
				music_get_clock(p, &c);
				check("pak stems loop a region sample-aligned (A + -A = 0 over 30 s)", pak_residue == 0.0f && c.loops >= 9 && c.length == b.size());
				music_destroy(p);
			}
		}

		// streamed stems (the default) loop a region too: each stem's next pass opens ahead of the wrap at the loop start
		// and every stem switches to it on the wrap frame — rendered at ~4x real time, with music_update standing in for
		// the game tick. Solo A must continue exactly at the loop start after every wrap; A + (-A) must stay silent.
		{
			const u64 l0 = rate / 2, l1 = rate / 2 + (u64)rate * 3;
			auto paced = [&](music_desc& m, u64 seconds, std::vector<f32>* keep, u32* stalls, bool update = true) {
				music_group* sg = music_create(m);
				if (!sg) return (music_group*)nullptr;
				std::vector<f32> block(1024 * 2);
				music_clock k{};
				for (u64 played = 0; played < (u64)rate * seconds;)
				{
					music_render(sg, block.data(), 1024);
					if (keep) for (u32 f = 0; f < 1024; ++f) keep->push_back(block[f * 2]);
					music_get_clock(sg, &k);
					if (k.stalled && stalls) ++*stalls;
					played = k.played;
					if (update) music_update(sg);
					std::this_thread::sleep_for(std::chrono::microseconds(5300));
				}
				return sg;
			};
			const char* streamed[] = { pa.c_str(), pb.c_str() };
			music_desc ds{};
			ds.stems = streamed;
			ds.stem_count = 2;
			ds.state_gains = solo;
			ds.loop_start = l0;
			ds.loop_end = l1;
			std::vector<f32> out;
			u32 stalls_solo = 0;
			music_group* s = paced(ds, 10, &out, &stalls_solo);
			check("a streamed music group opens", s != nullptr);
			if (s)
			{
				music_get_clock(s, &c);
				// output frame t plays a[t] until the loop end, then a[l0 + (t - l1) % (l1 - l0)] — as the 16-bit WAV holds it
				u64 mismatches = 0;
				for (u64 t = 0; t < out.size() && t < c.played; ++t)
				{
					const u64 src = t < l1 ? t : l0 + (t - l1) % (l1 - l0);
					const f32 expected = (f32)std::lround(a[(size_t)src] * 32767.0f) / 32768.0f;
					if (std::fabs(out[(size_t)t] - expected) > 1e-6f) ++mismatches;
				}
				std::printf("[info] streamed region loop: %llu passes, %u stalled mixes, %llu frames off the expected samples\n",
					(unsigned long long)c.loops, stalls_solo, (unsigned long long)mismatches);
				check("streamed stems wrap a loop region on the exact frame without waiting", c.loops >= 2 && stalls_solo == 0 && mismatches == 0);
				music_destroy(s);
			}
			ds.state_gains = both;
			u32 stalls_null = 0;
			std::vector<f32> null_out;
			music_group* n = paced(ds, 10, &null_out, &stalls_null);
			f32 peak = 0.0f;
			for (f32 v : null_out) peak = std::fabs(v) > peak ? std::fabs(v) : peak;
			std::printf("[info] streamed stems A + (-A): largest residue %.9f over 10 s of region loops\n", peak);
			check("streamed stems stay sample-aligned through refills and wraps", n != nullptr && peak == 0.0f);
			music_destroy(n);

			// without music_update no next pass is ready at the wrap: every stream seeks back and the stems wait for the
			// refill together — a pause, never a drift
			u32 stalls_seek = 0;
			std::vector<f32> seek_out;
			music_group* w = paced(ds, 10, &seek_out, &stalls_seek, false);
			f32 seek_peak = 0.0f;
			for (f32 v : seek_out) seek_peak = std::fabs(v) > seek_peak ? std::fabs(v) : seek_peak;
			if (w) music_get_clock(w, &c);
			std::printf("[info] streamed wrap without a prepared pass: %llu passes, %u stalled mixes, largest residue %.9f\n",
				w ? (unsigned long long)c.loops : 0ull, stalls_seek, seek_peak);
			check("a wrap without a prepared pass seeks every stream and they wait together", w != nullptr && c.loops >= 2 && seek_peak == 0.0f);
			music_destroy(w);

			// a streamed ONE-SHOT whose second stem is shorter: the short one falls silent, the music ends with the long one
			std::vector<float> half(a.begin(), a.begin() + (std::ptrdiff_t)(loop / 2));
			const std::string ph = write_wav_samples("vortex_music_half.wav", rate, half);
			const char* uneven[] = { pa.c_str(), ph.c_str() };
			music_desc du{};
			du.stems = uneven;
			du.stem_count = 2;
			du.loop = false;
			music_group* u = music_create(du);
			u64 produced = 0;
			bool ended = false;
			std::vector<f32> block(1024 * 2);
			for (int i = 0; u && i < 2000 && !ended; ++i)
			{
				const u64 got = music_render(u, block.data(), 1024);
				produced += got;
				ended = got < 1024;
				std::this_thread::sleep_for(std::chrono::microseconds(5300));
			}
			if (u) music_get_clock(u, &c);
			std::printf("[info] streamed one-shot with a stem half as long: ended=%d after %.3f s of music (the long stem is 4 s)\n", ended ? 1 : 0,
				u ? (double)c.played / rate : -1.0);
			check("a streamed one-shot ends with its longest stem, not stalled by a shorter one", u && ended && c.played == loop && produced == loop);
			music_destroy(u);
			std::remove(ph.c_str());
		}

		// the group as one voice on the music bus (streamed stems, the default; voices_update runs music_update): A alone
		// is loud, A + (-A) is silent; the voice reports its position
		voice_params vp{};
		vp.bus = (s32)bus::music;
		vp.priority = 0;
		d.state_gains = solo;
		d.decode = false;
		voice_handle v = voice_play_music(d, vp);
		tick_seconds(0.5f);
		f32 rms_solo = 0;
		mixer_get_bus_levels(bus::music, nullptr, &rms_solo);
		voice_music_schedule(v, music_stage::state, both, 2, 0, 64);
		tick_seconds(0.8f);
		f32 rms_null = 0;
		mixer_get_bus_levels(bus::music, nullptr, &rms_null);
		u64 cursor = 0, length = 0;
		u32 voice_rate = 0;
		const bool pos = voice_get_position(v, &cursor, &length, &voice_rate);
		const bool clock = voice_music_clock(v, &c);
		std::printf("[info] music voice: bus rms %.4f with one stem, %.6f with A + (-A); position %llu of %llu\n", rms_solo, rms_null,
			(unsigned long long)cursor, (unsigned long long)length);
		check("a music voice plays on the music bus", v != invalid_voice && rms_solo > 0.05f);
		check("the music voice's stems cancel on the bus (sample-locked through the mixer)", rms_null < 0.002f);
		check("a music voice reports its clock and position", pos && clock && c.played > rate / 2 && voice_rate == rate && cursor < loop && length == b.size());
		voice_stop(v);
		check("a stale music handle is a safe no-op", !voice_music_clock(v, &c) && voice_music_stem_gain(v, 0) < 0.0f);

		std::remove(pa.c_str());
		std::remove(pb.c_str());
		std::remove(pshot.c_str());
	}

	// Fades (issue #17): the envelope must actually change the mixed level (bus
	// meters), FadeOut must release the voice back to the pool.
	void run_fade_tests(const std::string& wav)
	{
		using namespace runtime::audio;

		// A fade-in from silence (FadeIn, Music.Play with a fade) begins silent (#465 review): miniaudio keeps ONE pending
		// fade per sound, so the old "snap to 0, then fade from the current level" pair issued in one frame collapsed into
		// a fade from full volume. Measured 0.15 s into a 2 s fade-in, against the same voice at full level.
		{
			voice_params f{};
			f.loop = true;
			f.volume = 0.8f;
			auto level_after = [&](int variant) {
				voice_handle v = voice_play(wav.c_str(), f);
				if (variant == 1) { voice_fade(v, 0.0f, 0.0f, false); voice_fade(v, 1.0f, 2.0f, false); }   // the old pair
				if (variant == 2) voice_fade_from(v, 0.0f, 1.0f, 2.0f, false);
				tick_seconds(0.15f);
				f32 rms = 0;
				mixer_get_bus_levels(bus::sfx, nullptr, &rms);
				voice_stop(v);
				tick_seconds(0.7f);   // the meter's history drains
				return rms;
			};
			const f32 full = level_after(0), pair = level_after(1), from = level_after(2);
			std::printf("[info] 0.15 s into a 2 s fade-in: full level rms %.4f, snap + fade-from-current %.4f, fade from 0 %.4f\n", full, pair, from);
			check("a fade-in from silence starts silent (explicit start level)", full > 0.05f && from < full * 0.25f);
		}

		// FadeTo 0 drops the metered level while the voice keeps playing.
		voice_params p{};
		p.loop = true;
		p.volume = 0.7f;
		voice_handle v = voice_play(wav.c_str(), p);
		tick_seconds(0.4f);
		f32 rms_full = 0;
		mixer_get_bus_levels(bus::master, nullptr, &rms_full);

		voice_fade(v, 0.0f, 0.15f, false);
		tick_seconds(0.6f);
		f32 rms_faded = 0;
		mixer_get_bus_levels(bus::master, nullptr, &rms_faded);
		check("FadeTo 0 silences the mix while the voice lives",
			rms_full > 0.02f && rms_faded < rms_full * 0.25f && voice_is_valid(v));

		// Retarget mid-fade back up — no snap, ends audible again.
		voice_fade(v, 1.0f, 0.15f, false);
		tick_seconds(0.5f);
		f32 rms_back = 0;
		mixer_get_bus_levels(bus::master, nullptr, &rms_back);
		check("retargeted fade comes back up", rms_back > rms_faded * 2.0f);

		// FadeOut releases the voice once silent.
		const u32 active_before = voices_active_count();
		voice_fade(v, 0.0f, 0.2f, true);
		tick_seconds(0.6f);
		check("FadeOut frees the voice after the fade",
			!voice_is_valid(v) && voices_active_count() == active_before - 1);
	}

	// Reverb (issue #15): the freeverb node must produce a TAIL — audio on the
	// master bus after the dry voice has stopped — and stay silent at send 0.
	void run_reverb_tests(const std::string& wav)
	{
		using namespace runtime::audio;
		check("reverb node exists", reverb_node() != nullptr);

		// Reference: send 0, voice stops -> master falls silent quickly.
		reverb_set_params(3.0f, 0.9f, 0.0f);
		voice_params p{};
		p.volume = 0.8f;
		voice_handle v = voice_play(wav.c_str(), p);
		tick_seconds(0.3f);
		voice_stop(v);
		// measure once the master METER's own ~0.4 s history has drained: the dry case is then silent, while a
		// reverb tail still rings (measuring earlier compared the tail with the meter's afterglow — fragile on CI).
		// A busy CI machine runs the audio callback late, so wait for the drain (at least 0.6 s, at most 2 s) instead
		// of a fixed time — one run measured 0.0019 here after 0.6 s and missed the 3x ratio by a hair.
		f32 dry_after = 0;
		for (int i = 0; i < 10; ++i)
		{
			tick_seconds(0.2f);
			mixer_get_bus_levels(bus::master, nullptr, &dry_after);
			if (i >= 2 && dry_after < 0.0005f) break;
		}

		// With full send: the tail must ring on AFTER the voice stopped.
		v = voice_play(wav.c_str(), p);
		voice_set_reverb_send(v, 1.0f);
		tick_seconds(0.4f);
		voice_stop(v);
		tick_seconds(0.6f);
		f32 wet_after = 0;
		mixer_get_bus_levels(bus::master, nullptr, &wet_after);
		std::printf("[info] reverb tail: master rms %.4f after a dry stop, %.4f after a full-send stop (0.6 s later)\n", dry_after, wet_after);
		check("reverb tail rings after the voice stops", wet_after > dry_after * 3.0f && wet_after > 0.002f);

		// #496: a send handed to voice_play applies from the first sample — a one-shot that is over before anyone
		// called voice_set_reverb_send still rings in the room.
		voice_params shot = p;
		shot.reverb_send = 1.0f;
		v = voice_play(wav.c_str(), shot);
		const f32 initial_send = voice_get_reverb_send(v);
		tick_seconds(0.7f);   // the 0.5 s clip plays out and frees its voice
		const bool shot_ended = !voice_is_valid(v);
		tick_seconds(0.4f);
		f32 shot_after = 0;
		mixer_get_bus_levels(bus::master, nullptr, &shot_after);
		std::printf("[info] one-shot started with send 1: send %.2f, master rms %.4f 0.6 s after it ended\n", initial_send, shot_after);
		check("voice_play's reverb_send is the voice's send from the start", std::fabs(initial_send - 1.0f) < 1e-4f);
		check("a one-shot started with a send rings in the reverb after it ended", shot_ended && shot_after > dry_after * 3.0f && shot_after > 0.002f);
		v = voice_play(wav.c_str(), p);
		check("a voice without a send starts dry; a stale handle reads -1", voice_get_reverb_send(v) == 0.0f && voice_get_reverb_send(invalid_voice) < 0.0f);
		voice_stop(v);
		tick_seconds(0.3f);

		// Wet 0 shuts the reverb up even at full send (allow the master METER's own
		// display decay to drain — it holds ~0.4 s of history after the dry stop).
		reverb_set_params(3.0f, 0.0f, 0.0f);
		v = voice_play(wav.c_str(), p);
		voice_set_reverb_send(v, 1.0f);
		tick_seconds(0.4f);
		voice_stop(v);
		tick_seconds(0.7f);
		f32 muted_after = 0;
		mixer_get_bus_levels(bus::master, nullptr, &muted_after);
		check("wet level 0 silences the reverb", muted_after < 0.006f);

		reverb_set_params(1.0f, 0.0f, 0.0f);
		tick_seconds(0.3f); // let any remainder die before the next suite
	}

	// Mixer (issue #13): assertions run against the REAL DSP — the per-bus metering
	// nodes report what actually flows through each bus.
	void run_mixer_tests(const std::string& wav)
	{
		using namespace runtime::audio;

		check("mixer groups exist", mixer_group(bus::master) != nullptr
			&& mixer_group(bus::music) != nullptr && mixer_group(bus::sfx) != nullptr
			&& mixer_group(bus::ambience) != nullptr && mixer_group(bus::ui) != nullptr);

		// Routing: a voice on SFX must register on the SFX meter AND on master.
		voice_params sfx_params{};
		sfx_params.loop = true;
		sfx_params.volume = 0.6f;
		sfx_params.bus = (s32)bus::sfx;
		voice_handle v = voice_play(wav.c_str(), sfx_params);
		tick_seconds(0.4f);
		f32 sfx_peak = 0, sfx_rms = 0, master_peak = 0, master_rms = 0, music_rms = 0;
		mixer_get_bus_levels(bus::sfx, &sfx_peak, &sfx_rms);
		mixer_get_bus_levels(bus::master, &master_peak, &master_rms);
		mixer_get_bus_levels(bus::music, nullptr, &music_rms);
		check("SFX voice meters on the SFX bus", sfx_rms > 0.01f);
		check("SFX voice reaches the master bus", master_rms > 0.01f);
		check("music bus stays silent", music_rms < 0.005f);

		// Bus volume: dropping SFX to 0 silences the routed voice at the master.
		mixer_set_bus_volume(bus::sfx, 0.0f);
		tick_seconds(0.5f);
		mixer_get_bus_levels(bus::master, &master_peak, &master_rms);
		check("bus volume 0 silences routed voices", master_rms < 0.01f);
		mixer_set_bus_volume(bus::sfx, 1.0f);

		// Mute/unmute round-trip.
		mixer_set_bus_mute(bus::sfx, true);
		tick_seconds(0.4f);
		mixer_get_bus_levels(bus::master, &master_peak, &master_rms);
		const bool muted_silent = master_rms < 0.01f;
		mixer_set_bus_mute(bus::sfx, false);
		tick_seconds(0.4f);
		mixer_get_bus_levels(bus::master, &master_peak, &master_rms);
		check("bus mute silences, unmute restores", muted_silent && master_rms > 0.01f);

		// Ducking: a loud MUSIC trigger must dip the AMBIENCE bus by roughly the
		// configured amount (-12 dB = x0.25), then release when the trigger stops.
		voice_params amb_params{};
		amb_params.loop = true;
		amb_params.volume = 0.6f;
		amb_params.bus = (s32)bus::ambience;
		voice_handle amb = voice_play(wav.c_str(), amb_params);
		mixer_set_duck(bus::music, bus::ambience, -12.0f, 40.0f, 150.0f, 0.02f);
		tick_seconds(0.5f);
		f32 amb_rms_free = 0;
		mixer_get_bus_levels(bus::ambience, nullptr, &amb_rms_free);

		voice_params music_params{};
		music_params.loop = true;
		music_params.volume = 0.8f;
		music_params.bus = (s32)bus::music;
		voice_handle mus = voice_play(wav.c_str(), music_params);
		tick_seconds(0.8f); // attack + meter settle
		f32 amb_rms_ducked = 0;
		mixer_get_bus_levels(bus::ambience, nullptr, &amb_rms_ducked);
		check("ducking dips the target bus", amb_rms_free > 0.01f && amb_rms_ducked < amb_rms_free * 0.55f);

		voice_stop(mus);
		tick_seconds(1.0f); // release + meter settle
		f32 amb_rms_released = 0;
		mixer_get_bus_levels(bus::ambience, nullptr, &amb_rms_released);
		check("duck releases when the trigger stops", amb_rms_released > amb_rms_ducked * 1.5f);

		mixer_clear_ducks();
		voice_stop(amb);
		voice_stop(v);
	}

	void run_voice_tests(const std::string& wav)
	{
		using namespace runtime::audio;
		const char* path = wav.c_str();
		const u32 max_voices = voices_max_count();
		check("voice pool sized", max_voices > 0);

		// Handle lifecycle: play → playing, pause → not playing, resume, stop → stale.
		voice_params p{};
		p.loop = true;
		voice_handle v = voice_play(path, p);
		check("voice_play returns valid handle", v != invalid_voice && voice_is_valid(v));
		check("voice reports playing", voice_is_playing(v));
		voice_pause(v);
		check("paused voice not playing but still valid", !voice_is_playing(v) && voice_is_valid(v));
		voice_resume(v);
		check("resumed voice playing", voice_is_playing(v));
		voice_set_volume(v, 0.5f);
		voice_set_pitch(v, 1.2f);
		voice_set_pan(v, -0.5f);
		voice_stop(v);
		check("stopped voice handle is stale", !voice_is_valid(v) && !voice_is_playing(v));
		voice_set_volume(v, 1.0f);	// must be safe no-ops on the stale handle
		voice_stop(v);

		// Priority stealing: fill the pool with unimportant looping voices, then a
		// more important request must steal (not fail), invalidating one victim.
		std::vector<voice_handle> fillers;
		voice_params low{};
		low.loop = true;
		low.priority = 200;
		low.volume = 0.1f;
		for (u32 i = 0; i < max_voices; ++i) fillers.push_back(voice_play(path, low));
		check("pool filled to max", voices_active_count() == max_voices);

		const u32 stolen_before = voices_stolen_count();
		voice_params high{};
		high.loop = true;
		high.priority = 10;
		voice_handle important = voice_play(path, high);
		check("high-priority play steals when pool is full", important != invalid_voice);
		check("steal counted", voices_stolen_count() == stolen_before + 1);
		check("pool did not grow past max", voices_active_count() == max_voices);
		int stale = 0;
		for (voice_handle f : fillers) if (!voice_is_valid(f)) ++stale;
		check("exactly one filler voice was stolen", stale == 1);

		// The reverse must fail: everything playing outranks the request.
		voice_params unimportant{};
		unimportant.loop = true;
		unimportant.priority = 250;
		// Pool is full of priority-200 (and one priority-10) voices; 250 is less
		// important than all of them.
		check("low-priority play rejected on full pool",
			voice_play(path, unimportant) == invalid_voice);

		for (voice_handle f : fillers) voice_stop(f);
		voice_stop(important);
		check("pool empty after stopping all", voices_active_count() == 0);

		// Stress: 220 one-shots (0.5 s clip) — pool never exceeds max, everything
		// reaps back automatically once finished.
		voice_params one_shot{};
		one_shot.volume = 0.05f;
		u32 accepted = 0;
		u32 peak_active = 0;
		for (int i = 0; i < 220; ++i)
		{
			if (voice_play(path, one_shot) != invalid_voice) ++accepted;
			const u32 active = voices_active_count();
			if (active > peak_active) peak_active = active;
			if ((i % 40) == 39) tick_seconds(0.05f);
		}
		check("stress: all 220 plays accepted", accepted == 220);
		check("stress: active voices never exceeded max", peak_active <= max_voices);
		tick_seconds(0.8f);
		check("stress: finished one-shots auto-returned to pool", voices_active_count() == 0);
		std::cout << "[info] stress: peak active " << peak_active
			<< ", total stolen " << voices_stolen_count() << "\n";
	}

	void check(const char* what, bool ok)
	{
		std::cout << (ok ? "[PASS] " : "[FAIL] ") << what << "\n";
		ok ? ++_passed : ++_failed;
	}

	// Simulates the game loop: update_audio + realtime sleep so the device thread
	// (or the silent-mode pump) actually advances playback.
	// Advance by WALL-CLOCK time: a loaded CI machine oversleeps the 16 ms naps, and a fixed tick count would then
	// measure meters and tails at a later moment than the test intends.
	void tick_seconds(float seconds)
	{
		using clock = std::chrono::steady_clock;
		const auto start = clock::now();
		auto last = start;
		for (;;)
		{
			std::this_thread::sleep_for(std::chrono::milliseconds(16));
			const auto now = clock::now();
			const float dt = std::chrono::duration<float>(now - last).count();
			last = now;
			runtime::systems::update_audio(dt);
			if (std::chrono::duration<float>(now - start).count() >= seconds) break;
		}
	}

	// Unique per process: two runs at once (a second ctest on the same machine) shared these names and deleted or
	// rewrote each other's clips mid-test — the "registered pak blob" / "three simultaneous streams play" failures.
	std::string temp_path(const char* name)
	{
		static const std::string prefix = [] {
			std::random_device rd;
			return "vortex-" + std::to_string(rd()) + "-";
		}();
		std::error_code ec;
		auto dir = std::filesystem::temp_directory_path(ec);
		if (ec) return prefix + name;
		return (dir / (prefix + name)).string();
	}

	// Minimal 16-bit PCM mono RIFF wav: 0.5 s, 660 Hz sine.
	std::string write_test_wav()
	{
		return write_wav("vortex_audio_test.wav", 0.5f, 660.0f);
	}

	std::string write_wav(const char* name, float seconds, float frequency)
	{
		const std::string path = temp_path(name);
		const uint32_t sample_rate = 44100;
		const uint32_t frames = (uint32_t)(sample_rate * seconds);
		std::vector<int16_t> pcm(frames);
		for (uint32_t i = 0; i < frames; ++i)
		{
			pcm[i] = (int16_t)(0.35f * 32767.0f * sinf(i * frequency * 2.0f * 3.14159265f / sample_rate));
		}

		const uint32_t data_size = frames * sizeof(int16_t);
		const uint32_t riff_size = 36 + data_size;
		const uint16_t channels = 1, bits = 16, block_align = 2;
		const uint32_t byte_rate = sample_rate * block_align;
		const uint16_t format_pcm = 1;
		const uint32_t fmt_size = 16;

		FILE* f = nullptr;
		if (fopen_s(&f, path.c_str(), "wb") != 0 || !f) return {};
		fwrite("RIFF", 1, 4, f); fwrite(&riff_size, 4, 1, f); fwrite("WAVE", 1, 4, f);
		fwrite("fmt ", 1, 4, f); fwrite(&fmt_size, 4, 1, f);
		fwrite(&format_pcm, 2, 1, f); fwrite(&channels, 2, 1, f);
		fwrite(&sample_rate, 4, 1, f); fwrite(&byte_rate, 4, 1, f);
		fwrite(&block_align, 2, 1, f); fwrite(&bits, 2, 1, f);
		fwrite("data", 1, 4, f); fwrite(&data_size, 4, 1, f);
		fwrite(pcm.data(), 1, data_size, f);
		fclose(f);
		return path;
	}

	// A valid MPEG-1 Layer III stream of digital silence: 128 kbps, 44.1 kHz, mono, no CRC, side info and main data zero.
	static std::vector<uint8_t> silent_mp3(double seconds)
	{
		const int frames = (int)(seconds * 44100 / 1152) + 1;
		const int size = 144 * 128000 / 44100;   // 417 bytes
		std::vector<uint8_t> out;
		out.reserve((size_t)frames * size);
		for (int i = 0; i < frames; ++i)
		{
			const uint8_t header[4] = { 0xFF, 0xFB, 0x90, 0xC0 };
			out.insert(out.end(), header, header + 4);
			out.insert(out.end(), (size_t)(size - 4), (uint8_t)0);
		}
		return out;
	}

	// 16-bit mono RIFF wav of the given samples (-1..1, kept inside +-32767 so a negated stem is the exact negation).
	std::string write_wav_samples(const char* name, uint32_t sample_rate, const std::vector<float>& samples)
	{
		const std::string path = temp_path(name);
		const uint32_t frames = (uint32_t)samples.size();
		std::vector<int16_t> pcm(frames);
		for (uint32_t i = 0; i < frames; ++i)
		{
			float s = samples[i] > 1.0f ? 1.0f : (samples[i] < -1.0f ? -1.0f : samples[i]);
			pcm[i] = (int16_t)std::lround(s * 32767.0f);   // round half away from zero: round(-x) == -round(x)
		}
		const uint32_t data_size = frames * sizeof(int16_t);
		const uint32_t riff_size = 36 + data_size;
		const uint16_t channels = 1, bits = 16, block_align = 2, format_pcm = 1;
		const uint32_t byte_rate = sample_rate * block_align, fmt_size = 16;
		FILE* f = nullptr;
		if (fopen_s(&f, path.c_str(), "wb") != 0 || !f) return {};
		fwrite("RIFF", 1, 4, f); fwrite(&riff_size, 4, 1, f); fwrite("WAVE", 1, 4, f);
		fwrite("fmt ", 1, 4, f); fwrite(&fmt_size, 4, 1, f);
		fwrite(&format_pcm, 2, 1, f); fwrite(&channels, 2, 1, f);
		fwrite(&sample_rate, 4, 1, f); fwrite(&byte_rate, 4, 1, f);
		fwrite(&block_align, 2, 1, f); fwrite(&bits, 2, 1, f);
		fwrite("data", 1, 4, f); fwrite(&data_size, 4, 1, f);
		fwrite(pcm.data(), 1, data_size, f);
		fclose(f);
		return path;
	}

	std::string write_garbage_file()
	{
		const std::string path = temp_path("vortex_audio_garbage.wav");
		FILE* f = nullptr;
		if (fopen_s(&f, path.c_str(), "wb") != 0 || !f) return {};
		fwrite("this is definitely not audio data", 1, 33, f);
		fclose(f);
		return path;
	}

	void load_optional_test_assets()
	{
		char* dir = nullptr;
		size_t len = 0;
		if (_dupenv_s(&dir, &len, "VORTEX_AUDIO_TEST_DIR") != 0 || !dir) return;
		std::string test_dir(dir);
		free(dir);

		std::error_code ec;
		for (const auto& entry : std::filesystem::directory_iterator(test_dir, ec))
		{
			if (!entry.is_regular_file()) continue;
			std::string ext = entry.path().extension().string();
			for (char& c : ext) c = (char)tolower((unsigned char)c);
			if (ext != ".wav" && ext != ".mp3" && ext != ".flac" && ext != ".ogg") continue;

			const std::string file = entry.path().string();
			auto handle = runtime::resource_manager::load_audio(file.c_str());
			check(("decode " + entry.path().filename().string()).c_str(),
				handle.is_valid() && runtime::audio::is_loaded(file.c_str()));
		}
	}

	// miniaudio streams Vorbis files in push mode: the stream's length is 0 and its cursor never wraps at the loop. The
	// voice probes the length (Time wraps, Length is known), and a one-shot music of OGG stems ends with them — checked
	// on the short .ogg files in VORTEX_AUDIO_TEST_DIR (no OGG encoder here to generate one).
	void run_optional_ogg_tests()
	{
		using namespace runtime::audio;
		char* dir = nullptr;
		size_t len = 0;
		if (_dupenv_s(&dir, &len, "VORTEX_AUDIO_TEST_DIR") != 0 || !dir) return;
		std::string test_dir(dir);
		free(dir);
		std::error_code ec;
		for (const auto& entry : std::filesystem::directory_iterator(test_dir, ec))
		{
			std::string ext = entry.path().extension().string();
			for (char& c : ext) c = (char)tolower((unsigned char)c);
			if (!entry.is_regular_file() || ext != ".ogg") continue;
			const std::string file = entry.path().string(), name = entry.path().filename().string();
			f32 seconds = 0.0f;
			u32 clip_rate = 0, channels = 0;
			if (!clip_info(file.c_str(), &seconds, &clip_rate, &channels) || seconds <= 0.2f || seconds > 8.0f) continue;

			voice_params p{};
			p.volume = 0.05f;
			p.loop = true;
			p.stream = true;
			voice_handle v = voice_play(file.c_str(), p);
			tick_seconds(seconds * 1.5f);
			u64 cursor = 0, length = 0;
			u32 rate = 0;
			voice_get_position(v, &cursor, &length, &rate);
			const double length_s = rate ? (double)length / rate : 0.0;
			std::printf("[info] streamed %s: length %.3f s (header %.3f s), cursor %.3f s after %.3f s of looping\n", name.c_str(),
				length_s, seconds, rate ? (double)cursor / rate : -1.0, seconds * 1.5);
			check(("a file-streamed OGG knows its length and its position wraps at the loop: " + name).c_str(),
				v != invalid_voice && std::fabs(length_s - seconds) < 0.01 && cursor < length);
			voice_stop(v);

			const char* stems[] = { file.c_str() };
			music_desc d{};
			d.stems = stems;
			d.stem_count = 1;
			d.loop = false;
			music_group* g = music_create(d);
			bool ended = false;
			std::vector<f32> block(1024 * 2);
			for (int i = 0; g && i < 4000 && !ended; ++i)
			{
				ended = music_render(g, block.data(), 1024) < 1024;
				std::this_thread::sleep_for(std::chrono::microseconds(5300));
			}
			music_clock c{};
			if (g) music_get_clock(g, &c);
			std::printf("[info] one-shot music of %s: ended=%d after %.3f s, length %.3f s\n", name.c_str(), ended ? 1 : 0,
				c.sample_rate ? (double)c.played / c.sample_rate : -1.0, c.sample_rate ? (double)c.length / c.sample_rate : -1.0);
			check(("a one-shot music of a streamed OGG ends with it: " + name).c_str(),
				g && ended && c.length > 0 && c.played + 256 >= c.length && c.played <= c.length);
			music_destroy(g);
		}
	}

	int _passed{ 0 };
	int _failed{ 0 };
};
