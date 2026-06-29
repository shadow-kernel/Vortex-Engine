#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	struct SwapchainDesc
	{
		HWND hwnd{ nullptr };
		u32 width{ 0 };
		u32 height{ 0 };
		u32 buffer_count{ 2 };
		DXGI_FORMAT format{ DXGI_FORMAT_R8G8B8A8_UNORM };
	};

	class DX12Swapchain
	{
	public:
		static constexpr u32 MaxBufferCount = 3;

		bool initialize(IDXGIFactory4* factory, ID3D12CommandQueue* queue,
						ID3D12Device* device, const SwapchainDesc& desc);
		void shutdown();

		bool resize(u32 width, u32 height);
		void present(bool vsync = true);

		ID3D12Resource* current_back_buffer() const;
		D3D12_CPU_DESCRIPTOR_HANDLE current_rtv() const;
		u32 current_back_buffer_index() const { return m_current_index; }
		u32 buffer_count() const { return m_buffer_count; }
		u32 width() const { return m_width; }
		u32 height() const { return m_height; }

	private:
		bool create_rtv_heap(ID3D12Device* device);
		bool create_back_buffer_rtvs(ID3D12Device* device);
		void release_back_buffers();

		ComPtr<IDXGISwapChain3> m_swapchain;
		ComPtr<ID3D12DescriptorHeap> m_rtv_heap;
		ComPtr<ID3D12Resource> m_back_buffers[MaxBufferCount];
		ID3D12Device* m_device{ nullptr };

		u32 m_buffer_count{ 2 };
		u32 m_current_index{ 0 };
		u32 m_rtv_descriptor_size{ 0 };
		bool m_allow_tearing{ false };   // flip-model uncap: DXGI_PRESENT_ALLOW_TEARING when supported + !vsync
		u32 m_width{ 0 };
		u32 m_height{ 0 };
		DXGI_FORMAT m_format{ DXGI_FORMAT_R8G8B8A8_UNORM };
	};
}
