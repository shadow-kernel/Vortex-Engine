#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <wrl/client.h>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	/// <summary>
	/// Manages depth/stencil buffer resources.
	/// </summary>
	class DX12DepthBuffer
	{
	public:
		bool initialize(ID3D12Device* device, u32 width, u32 height, DXGI_FORMAT format = DXGI_FORMAT_D32_FLOAT);
		void shutdown();
		bool resize(ID3D12Device* device, u32 width, u32 height);

		ID3D12Resource* resource() const { return m_depth_buffer.Get(); }
		D3D12_CPU_DESCRIPTOR_HANDLE dsv() const { return m_dsv; }
		DXGI_FORMAT format() const { return m_format; }

		u32 width() const { return m_width; }
		u32 height() const { return m_height; }

	private:
		bool create_depth_buffer(ID3D12Device* device);
		bool create_dsv_heap(ID3D12Device* device);

		ComPtr<ID3D12Resource> m_depth_buffer;
		ComPtr<ID3D12DescriptorHeap> m_dsv_heap;
		D3D12_CPU_DESCRIPTOR_HANDLE m_dsv{};
		DXGI_FORMAT m_format{ DXGI_FORMAT_D32_FLOAT };
		u32 m_width{ 0 };
		u32 m_height{ 0 };
	};
}
