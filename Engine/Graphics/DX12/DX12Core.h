#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <string>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	class DX12Core
	{
	public:
		static DX12Core& instance();

		bool initialize();
		void shutdown();

		ID3D12Device* device() const { return m_device.Get(); }
		IDXGIFactory4* factory() const { return m_factory.Get(); }

		bool is_initialized() const { return m_initialized; }

		// Selected adapter info (captured at device creation) — drives the DLSS hardware gate.
		u32 adapter_vendor_id() const { return m_adapter_vendor_id; }       // 0x10DE NVIDIA, 0x1002 AMD, 0x8086 Intel
		const std::wstring& adapter_name() const { return m_adapter_name; }
		// DLSS needs an NVIDIA RTX GPU (Turing+). Heuristic now (NVIDIA + "RTX" in the name); replaced by
		// Streamline's sl::isFeatureSupported once DLSS is wired. False on every non-RTX machine -> falls back to render-scale.
		bool dlss_capable() const { return m_dlss_capable; }

	private:
		DX12Core() = default;
		~DX12Core() = default;
		DX12Core(const DX12Core&) = delete;
		DX12Core& operator=(const DX12Core&) = delete;

		bool create_factory();
		bool create_device();

		ComPtr<IDXGIFactory4> m_factory;
		ComPtr<ID3D12Device> m_device;
		u32 m_adapter_vendor_id{ 0 };
		std::wstring m_adapter_name;
		bool m_dlss_capable{ false };
		bool m_initialized{ false };
	};
}
