#pragma once

#include "..\Engine\Runtime\ResourceManager.h"
#include "..\Engine\Runtime\Systems\AudioSystem.h"
#include "..\Engine\Runtime\Systems\AudioEngine.h"
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
		const std::string path = temp_path("vortex_audio_test.wav");
		const uint32_t sample_rate = 44100;
		const uint32_t frames = sample_rate / 2;
		std::vector<int16_t> pcm(frames);
		for (uint32_t i = 0; i < frames; ++i)
		{
			pcm[i] = (int16_t)(0.35f * 32767.0f * sinf(i * 660.0f * 2.0f * 3.14159265f / sample_rate));
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
