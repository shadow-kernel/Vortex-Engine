#pragma once

#include "..\Engine\Runtime\ResourceManager.h"
#include "..\Engine\Runtime\Systems\AudioSystem.h"
#include "..\Engine\Runtime\Systems\AudioEngine.h"
#include "..\Engine\Runtime\Systems\AudioMixer.h"
#include "..\Engine\Runtime\Systems\AudioVoices.h"

#include "Test.h"

#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <iostream>
#include <string>
#include <thread>
#include <vector>

#include <psapi.h>
#pragma comment(lib, "psapi.lib")

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

		// -- mixer buses + ducking (issue #13) ---------------------------------------
		run_mixer_tests(wav_path);

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
	// (GetTickCount64) lines so the external per-channel peak meter can prove
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
			fprintf(f, "%s %llu\n", phase, (unsigned long long)GetTickCount64());
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
		PROCESS_MEMORY_COUNTERS pmc{};
		GetProcessMemoryInfo(GetCurrentProcess(), &pmc, sizeof(pmc));
		return pmc.WorkingSetSize;
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
	void tick_seconds(float seconds)
	{
		const float dt = 1.0f / 60.0f;
		for (float t = 0.0f; t < seconds; t += dt)
		{
			runtime::systems::update_audio(dt);
			std::this_thread::sleep_for(std::chrono::milliseconds(16));
		}
	}

	std::string temp_path(const char* name)
	{
		std::error_code ec;
		auto dir = std::filesystem::temp_directory_path(ec);
		if (ec) return name;
		return (dir / name).string();
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

	int _passed{ 0 };
	int _failed{ 0 };
};
