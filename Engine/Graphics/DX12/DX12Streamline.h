#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <dxgi1_6.h>

namespace vortex::graphics::dx12
{
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

		void shutdown();

	private:
		DX12Streamline() = default;
		~DX12Streamline() = default;
		DX12Streamline(const DX12Streamline&) = delete;
		DX12Streamline& operator=(const DX12Streamline&) = delete;

		void* m_module{ nullptr };   // HMODULE of sl.interposer.dll (void* to keep windows.h out of this header)
		bool  m_available{ false };
	};
}
