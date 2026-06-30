#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <dxgi1_6.h>
#include <DirectXMath.h>

namespace vortex::graphics::dx12
{
	// Everything slEvaluateFeature(DLSS) needs for one frame. States are the CURRENT D3D12 state of each resource
	// (SL manages transitions from there + restores them). Matrices are row-major, NO jitter (engine convention).
	struct DlssEvalDesc
	{
		ID3D12GraphicsCommandList* cmd{};
		int mode{};                       // 1=Quality 2=Balanced 3=Performance 4=UltraPerformance
		u32 outW{}, outH{};               // output (display) resolution
		u32 renderW{}, renderH{};         // render (scaled) resolution
		ID3D12Resource* colorIn{};        // scaled scene color (render res)
		ID3D12Resource* colorOut{};       // DLSS output (display res, UAV)
		ID3D12Resource* depth{};          // scaled depth (render res)
		ID3D12Resource* mvec{};           // RG16F motion vectors (render res)
		u32 colorInState{}, colorOutState{}, depthState{}, mvecState{};
		DirectX::XMFLOAT4X4 proj{};       // cameraViewToClip (view->clip)
		DirectX::XMFLOAT3 camPos{}, camRight{}, camFwd{};
		float fovY{}, nearZ{}, farZ{};
	};

	// Thin, OPTIONAL, dynamically-loaded NVIDIA Streamline (DLSS) wrapper.
	//
	// It LoadLibrary's sl.interposer.dll at runtime (expected next to the exe, alongside sl.common/sl.dlss/... )
	// and resolves the handful of entry points we need via GetProcAddress — exactly like DX12Core already loads
	// d3d12.dll/dxgi.dll. Nothing here is linked, so if the Streamline DLLs are missing (any non-dev machine) or
	// slInit fails, available() simply stays false and the renderer keeps using the existing render-scale upscale.
	// DLSS is therefore purely additive and hardware-gated; it can never break startup or the render path.
	//
	// This first slice wires INIT + SUPPORT-QUERY only (no command-list / evaluate path yet), so it cannot affect
	// any pixels — it just replaces the "NVIDIA + RTX in the name" heuristic with Streamline's real
	// slIsFeatureSupported. The motion-vector + slEvaluateFeature render surgery lands in later, separate steps.
	class DX12Streamline
	{
	public:
		static DX12Streamline& instance();

		// Load sl.interposer.dll + slInit (requests the DLSS plugin). Call ONCE, early, before device creation.
		// Returns true if Streamline is up. Idempotent; harmless to call when the DLLs aren't present.
		bool init();

		// True once slInit has succeeded and the entry points resolved.
		bool available() const { return m_available; }

		// The loaded sl.interposer.dll module (HMODULE as void*), or null when unavailable. DX12Core resolves
		// CreateDXGIFactory2 / D3D12CreateDevice from HERE when present, so the device + command lists become SL
		// proxies — which slEvaluateFeature(DLSS) needs to inject into the command list. Transparent passthrough
		// to the real d3d12/dxgi when DLSS isn't doing anything, so it's safe to always route when available.
		void* interposer_module() const { return m_available ? m_module : nullptr; }

		// Real DLSS support for the chosen adapter (LUID), via slIsFeatureSupported. Returns false if !available().
		bool is_dlss_supported(const LUID& luid);

		// Register the main D3D12 device with Streamline (call right after the device is created). No-op if !available().
		void set_device(ID3D12Device* device);

		// True once the DLSS feature functions resolved (after set_device + the plugin started). If false,
		// evaluate_dlss can't run and callers must use the bilinear upscale fallback.
		bool dlss_ready() const { return m_dlss_ready; }

		// Run DLSS for this frame on desc.cmd. Returns false on ANY failure (caller falls back to the bilinear
		// upscale) — so a DLSS hiccup degrades gracefully and never black-screens.
		bool evaluate_dlss(const DlssEvalDesc& desc);

		// Frame-Generation per-frame inputs: set the common (camera) constants + tag depth + motion vectors with the
		// CURRENT frame token (the one frame_begin made — markers + FG must share it). NO slEvaluateFeature: DLSS-G
		// consumes these in its Present hook to interpolate the back buffer. Only desc.cmd/depth/mvec/renderW/H +
		// the camera fields are used (color in/out are ignored — FG uses the swap-chain back buffer as its color).
		bool tag_fg_frame(const DlssEvalDesc& desc);

		// ---- Frame Generation (DLSS-G) + Reflex ----
		// DLSS-G inserts AI frames at Present; it REQUIRES Reflex active + PCL markers threaded through the frame
		// loop. fg_ready() is true once the DLSS-G/Reflex/PCL feature functions resolved (after set_device).
		bool fg_ready() const { return m_fg_ready; }
		void set_reflex(bool enabled);                         // slReflexSetOptions (low-latency on/off; on = FG-ready)
		void* new_frame_token(unsigned frame_index);           // slGetNewFrameToken -> opaque token (cast internally)
		void reflex_sleep(void* frame_token);                  // slReflexSleep (call once near the top of the frame)
		void pcl_marker(int marker, void* frame_token);        // slPCLSetMarker (sl::PCLMarker value: 0..5)
		// numFramesToGenerate: 0 = OFF, 1 = x2, 2 = x3, 3 = x4. outW/outH = display (back-buffer) size.
		// Also flips Reflex on/off (FG requires Reflex) + arms the per-frame markers below.
		void set_frame_gen(int num_frames_to_generate, unsigned out_w, unsigned out_h);
		int  fg_presented_frames();                            // slDLSSGGetState.numFramesActuallyPresented (readout)
		bool frame_gen_active() const { return m_reflex_active; }

		// Per-frame Reflex/PCL hooks for the GameHost loop. ALL no-ops when FG is off, so the loop is unaffected
		// unless the user enables Frame Generation. frame_begin: token + slReflexSleep + SimStart marker (call at
		// the top of the frame). frame_marker(m): a PCL marker (1=SimEnd,2=RenderSubmitStart,3=RenderSubmitEnd,
		// 4=PresentStart,5=PresentEnd). current_token: the frame token for render_frame's FG constants/tags.
		void frame_begin(unsigned frame_index);
		void frame_marker(int marker);
		void* current_token() const;

		void shutdown();

	private:
		DX12Streamline() = default;
		~DX12Streamline() = default;
		DX12Streamline(const DX12Streamline&) = delete;
		DX12Streamline& operator=(const DX12Streamline&) = delete;

		void* m_module{ nullptr };   // HMODULE of sl.interposer.dll (void* to keep windows.h out of this header)
		bool  m_available{ false };
		bool  m_dlss_ready{ false }; // DLSS feature functions resolved
		bool  m_fg_ready{ false };   // DLSS-G + Reflex + PCL feature functions resolved
		bool  m_reflex_active{ false }; // FG enabled -> Reflex + per-frame markers armed
		void* m_current_token{ nullptr }; // this frame's sl::FrameToken (markers + FG constants/tags)
		u32   m_frame_index{ 0 };    // incrementing index for slGetNewFrameToken
	};
}
