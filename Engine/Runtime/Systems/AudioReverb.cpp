#include "AudioReverb.h"
#include "AudioEngine.h"
#include "AudioInternal.h"
#include "AudioMixer.h"

#include <atomic>
#include <cmath>
#include <cstring>
#include <vector>

#pragma warning(push)
#pragma warning(disable: 4244 4245 4456 4457 4701 4267 4100 4189)
#include "../../ThirdParty/miniaudio.h"
#pragma warning(pop)

namespace vortex::runtime::audio {

	namespace {

		// ---- Freeverb (Jezar at Dreampoint, public domain constants) -----------------
		// 8 parallel lowpass-feedback combs + 4 serial allpasses per channel, right
		// channel offset by 23 samples for stereo width. Tunings are the classic
		// 44.1 kHz sample counts — at 48 kHz the tail is ~8% shorter, inaudible for
		// game ambience purposes.
		constexpr u32 k_comb_tunings[8] = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
		constexpr u32 k_allpass_tunings[4] = { 556, 441, 341, 225 };
		constexpr u32 k_stereo_spread = 23;
		constexpr u32 k_max_predelay_frames = 48000 / 5; // 200 ms @ 48 kHz

		struct comb_filter
		{
			std::vector<f32> buffer;
			u32 pos{ 0 };
			f32 filter_store{ 0.0f };

			void init(u32 size) { buffer.assign(size, 0.0f); pos = 0; filter_store = 0.0f; }

			f32 process(f32 input, f32 feedback, f32 damp)
			{
				const f32 output = buffer[pos];
				// One-pole lowpass in the feedback path (the "damp" of freeverb).
				filter_store = output * (1.0f - damp) + filter_store * damp;
				buffer[pos] = input + filter_store * feedback;
				if (++pos >= buffer.size()) pos = 0;
				return output;
			}
		};

		struct allpass_filter
		{
			std::vector<f32> buffer;
			u32 pos{ 0 };

			void init(u32 size) { buffer.assign(size, 0.0f); pos = 0; }

			f32 process(f32 input)
			{
				const f32 buffered = buffer[pos];
				buffer[pos] = input + buffered * 0.5f;
				if (++pos >= buffer.size()) pos = 0;
				return buffered - input;
			}
		};

		struct reverb_node_t
		{
			ma_node_base		base;			// must be first
			comb_filter			combs[2][8];
			allpass_filter		allpasses[2][4];
			std::vector<f32>	predelay[2];
			u32					predelay_pos{ 0 };
			std::atomic<u32>	predelay_frames{ 0 };
			std::atomic<f32>	feedback{ 0.84f };	// comb feedback ("room size")
			std::atomic<f32>	damp{ 0.2f };
			std::atomic<f32>	wet{ 0.0f };		// 0 = reverb silent
			u32					channels{ 2 };
		};

		void reverb_process(ma_node* node, const float** frames_in, ma_uint32* frame_count_in,
			float** frames_out, ma_uint32* frame_count_out)
		{
			reverb_node_t* rv = (reverb_node_t*)node;
			const float* in = frames_in[0];
			float* out = frames_out[0];
			const ma_uint32 frames = *frame_count_out < *frame_count_in ? *frame_count_out : *frame_count_in;
			const u32 channels = rv->channels;

			const f32 feedback = rv->feedback.load(std::memory_order_relaxed);
			const f32 damp = rv->damp.load(std::memory_order_relaxed);
			const f32 wet = rv->wet.load(std::memory_order_relaxed);
			const u32 predelay = rv->predelay_frames.load(std::memory_order_relaxed);
			// Freeverb input is heavily attenuated before the combs (fixed gain).
			constexpr f32 k_input_gain = 0.015f;

			for (ma_uint32 f = 0; f < frames; ++f)
			{
				for (u32 c = 0; c < channels && c < 2; ++c)
				{
					f32 input = in[f * channels + c];

					// Pre-delay: a simple circular tap.
					if (predelay > 0)
					{
						f32& slot = rv->predelay[c][rv->predelay_pos % k_max_predelay_frames];
						const f32 delayed = rv->predelay[c][(rv->predelay_pos + k_max_predelay_frames - predelay) % k_max_predelay_frames];
						slot = input;
						input = delayed;
					}

					input *= k_input_gain;
					f32 mixed = 0.0f;
					for (u32 i = 0; i < 8; ++i)
						mixed += rv->combs[c][i].process(input, feedback, damp);
					for (u32 i = 0; i < 4; ++i)
						mixed = rv->allpasses[c][i].process(mixed);

					out[f * channels + c] = mixed * wet;   // WET ONLY — dry stays on the buses
				}
				// Any extra channels (>2) pass silence from the reverb.
				for (u32 c = 2; c < channels; ++c) out[f * channels + c] = 0.0f;
				++rv->predelay_pos;
			}

			*frame_count_in = frames;
			*frame_count_out = frames;
		}

		ma_node_vtable g_reverb_vtable = { reverb_process, nullptr, 1, 1, MA_NODE_FLAG_CONTINUOUS_PROCESSING };

		static reverb_node_t* g_reverb{ nullptr };
	}

	void reverb_initialize()
	{
		ma_engine* engine = internal_engine();
		if (!engine || g_reverb) return;

		g_reverb = new reverb_node_t{};
		const u32 channels = ma_engine_get_channels(engine);
		g_reverb->channels = channels;
		for (u32 c = 0; c < 2; ++c)
		{
			for (u32 i = 0; i < 8; ++i)
				g_reverb->combs[c][i].init(k_comb_tunings[i] + (c == 1 ? k_stereo_spread : 0));
			for (u32 i = 0; i < 4; ++i)
				g_reverb->allpasses[c][i].init(k_allpass_tunings[i] + (c == 1 ? k_stereo_spread : 0));
			g_reverb->predelay[c].assign(k_max_predelay_frames, 0.0f);
		}

		ma_node_config config = ma_node_config_init();
		config.vtable = &g_reverb_vtable;
		config.pInputChannels = &channels;
		config.pOutputChannels = &channels;
		if (ma_node_init(ma_engine_get_node_graph(engine), &config, nullptr, &g_reverb->base) != MA_SUCCESS)
		{
			delete g_reverb;
			g_reverb = nullptr;
			internal_log("WARNING: reverb node failed to init");
			return;
		}

		// The wet tail lands on the master bus (behind its fader + meter).
		ma_node* master = (ma_node*)mixer_group(bus::master);
		ma_node_attach_output_bus(&g_reverb->base, 0, master ? master : ma_engine_get_endpoint(engine), 0);
		internal_log("reverb node up (freeverb)");
	}

	void reverb_shutdown()
	{
		if (!g_reverb) return;
		ma_node_uninit(&g_reverb->base, nullptr);
		delete g_reverb;
		g_reverb = nullptr;
	}

	void reverb_set_params(f32 decay_seconds, f32 wet_level, f32 predelay_ms)
	{
		if (!g_reverb) return;

		// Decay -> comb feedback: freeverb's usable range is ~0.5 (short slap) to
		// 0.98 (huge hall). Simple asymptotic map, clamped.
		f32 decay = decay_seconds < 0.1f ? 0.1f : (decay_seconds > 20.0f ? 20.0f : decay_seconds);
		f32 feedback = 1.0f - 0.35f / decay;
		if (feedback < 0.5f) feedback = 0.5f;
		if (feedback > 0.98f) feedback = 0.98f;
		g_reverb->feedback.store(feedback, std::memory_order_relaxed);

		f32 wet = wet_level < 0.0f ? 0.0f : (wet_level > 1.0f ? 1.0f : wet_level);
		g_reverb->wet.store(wet, std::memory_order_relaxed);

		ma_engine* engine = internal_engine();
		const u32 rate = engine ? ma_engine_get_sample_rate(engine) : 48000;
		f32 pd = predelay_ms < 0.0f ? 0.0f : (predelay_ms > 200.0f ? 200.0f : predelay_ms);
		u32 pd_frames = (u32)(pd * rate / 1000.0f);
		if (pd_frames >= k_max_predelay_frames) pd_frames = k_max_predelay_frames - 1;
		g_reverb->predelay_frames.store(pd_frames, std::memory_order_relaxed);
	}

	void* reverb_node()
	{
		return g_reverb ? &g_reverb->base : nullptr;
	}
}
