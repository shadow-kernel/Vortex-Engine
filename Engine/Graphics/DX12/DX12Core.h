#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

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

	private:
		DX12Core() = default;
		~DX12Core() = default;
		DX12Core(const DX12Core&) = delete;
		DX12Core& operator=(const DX12Core&) = delete;

		bool create_factory();
		bool create_device();

		ComPtr<IDXGIFactory4> m_factory;
		ComPtr<ID3D12Device> m_device;
		bool m_initialized{ false };
	};
}
