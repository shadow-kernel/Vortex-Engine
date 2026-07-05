#pragma once

#include "../../Common/CommonHeaders.h"
#include "DX12RenderTarget.h"
#include "DX12CommandQueue.h"
#include <d3d12.h>
#include <wrl/client.h>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	// Post-processing pass chain (#28) + horror pack 1 (#29).
	//
	// An ordered set of fullscreen passes that runs at OUTPUT resolution, after the upscale/DLSS
	// composite (editor viewport) or the 3D scene (game window), and before the UI overlay. When the
	// chain is active the renderer redirects what used to hit the back buffer into acquire_input()'s
	// offscreen RT; record() then ping-pongs through the enabled passes and the LAST pass writes the
	// back buffer. Zero enabled passes = the renderer takes its untouched original path (no extra
	// copies, byte-identical output — the #28 regression guarantee).
	//
	// v1 registers two passes sharing one uber-PSO (postfx.hlsl, feature bits per pass in its CB):
	//   pass 0  "composite"  — vignette + film grain + chromatic aberration in one pass (#29)
	//   pass 1  "invert"     — trivial debug pass proving the multi-pass ping-pong (#28 AC), off in production
	//
	// Traps honored (render-scale work): RTs are EXPLICIT R8G8B8A8_UNORM (class default is BGRA and
	// would mismatch the back buffer PSO format), every pass sets its own full-output viewport, and
	// all RT state round-trips happen through DX12RenderTarget's tracked transitions.
	class DX12PostFxChain
	{
	public:
		// Per-effect parameters, CPU-side. Written by the editor/scripts through the VortexAPI
		// setters (UI thread), read by record() (render thread) — same benign float-race contract as
		// the fog/light setters (a torn frame of a slider drag is invisible).
		struct Params
		{
			bool vignette{ false };
			float vig_intensity{ 0.8f };     // 0..~1.5: how far the darkening reaches in
			float vig_smoothness{ 0.5f };    // 0.01..1: falloff curve hardness
			float vig_roundness{ 1.0f };     // 1 = circular on any aspect, 0 = follows screen shape
			float vig_r{ 0.0f }, vig_g{ 0.0f }, vig_b{ 0.0f };
			bool grain{ false };
			float grain_intensity{ 0.35f };  // 0..1
			float grain_size{ 1.6f };        // cell size in output pixels
			bool ca{ false };
			float ca_strength{ 0.35f };      // percent of half-screen at the edge
			float ca_falloff{ 1.2f };        // radial power (higher = clean center, smeared edges)
			bool debug_invert{ false };      // #28 chain-test pass — never shipped on
		};

		bool initialize(ID3D12Device* device, DXGI_FORMAT rtv_format, DX12CommandQueue* queue);
		void shutdown();
		bool is_initialized() const { return m_pso != nullptr; }

		Params& params() { return m_params; }

		// True when at least one pass would run this frame — the renderer's redirect gate.
		bool active() const
		{
			return m_pso && (m_params.vignette || m_params.grain || m_params.ca || m_params.debug_invert);
		}

		// The RT the scene/composite renders into while the chain is active, ensured to w x h and left
		// in RENDER_TARGET state. `slot` 0 = editor viewport, 1 = game window (independent sizes, so
		// each gets its own ping-pong pair — sharing one would re-create it every frame while both
		// views render). Returns nullptr on creation failure (caller falls back to the direct path).
		DX12RenderTarget* acquire_input(ID3D12Device* device, u32 w, u32 h, int slot);

		// Record the enabled passes: input(slot) -> [ping] -> out_rtv at w x h. Sets its own
		// viewport/scissor per pass and leaves every chain RT back in RENDER_TARGET state.
		void record(ID3D12GraphicsCommandList* cmd, int slot, D3D12_CPU_DESCRIPTOR_HANDLE out_rtv,
			u32 w, u32 h, float time_seconds);

	private:
		// Byte-matched to postfx.hlsl's PostFx cbuffer — keep both in sync.
		struct PassCB
		{
			float texel[2]; float time; u32 flags;
			float vignette[4];         // intensity, smoothness, roundness, unused
			float vignette_color[4];   // rgb + unused
			float grain_ca[4];         // grain intensity, grain size, ca strength, ca falloff
		};
		static_assert(sizeof(PassCB) == 64, "PassCB must byte-match postfx.hlsl");

		static constexpr u32 MAX_PASSES = 2;

		bool ensure_rt(ID3D12Device* device, int slot, int ping, u32 w, u32 h);

		ComPtr<ID3D12RootSignature> m_root_signature;
		ComPtr<ID3D12PipelineState> m_pso;
		ComPtr<ID3D12Resource> m_cb;          // MAX_PASSES x 256B upload heap, persistently mapped
		void* m_cb_mapped{ nullptr };
		DX12RenderTarget m_rt[2][2];          // [slot][ping] — ping B only exists once 2+ passes ran
		DX12CommandQueue* m_queue{ nullptr }; // for the GPU-idle before re-creating an in-flight RT
		ID3D12Device* m_device{ nullptr };    // borrowed (DX12Core owns it) — for lazy ping-B creation
		DXGI_FORMAT m_format{ DXGI_FORMAT_R8G8B8A8_UNORM };
		Params m_params;
	};
}
