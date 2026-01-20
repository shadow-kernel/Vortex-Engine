#include "DX12Swapchain.h"

namespace vortex::graphics::dx12
{
	bool DX12Swapchain::initialize(IDXGIFactory4* factory, ID3D12CommandQueue* queue,
								   ID3D12Device* device, const SwapchainDesc& desc)
	{
		if (!factory || !queue || !device || !desc.hwnd) return false;
		if (desc.width == 0 || desc.height == 0) return false;

		m_device = device;
		m_width = desc.width;
		m_height = desc.height;
		m_buffer_count = desc.buffer_count;
		m_format = desc.format;

		DXGI_SWAP_CHAIN_DESC1 scDesc{};
		scDesc.BufferCount = m_buffer_count;
		scDesc.Width = m_width;
		scDesc.Height = m_height;
		scDesc.Format = m_format;
		scDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
		scDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
		scDesc.SampleDesc.Count = 1;

		ComPtr<IDXGISwapChain1> swapchain1;
		if (FAILED(factory->CreateSwapChainForHwnd(queue, desc.hwnd, &scDesc, nullptr, nullptr, &swapchain1)))
			return false;

		if (FAILED(swapchain1.As(&m_swapchain)))
			return false;

		factory->MakeWindowAssociation(desc.hwnd, DXGI_MWA_NO_ALT_ENTER);

		if (!create_rtv_heap(device)) return false;
		if (!create_back_buffer_rtvs(device)) return false;

		m_current_index = m_swapchain->GetCurrentBackBufferIndex();
		return true;
	}

	void DX12Swapchain::shutdown()
	{
		release_back_buffers();
		m_rtv_heap.Reset();
		m_swapchain.Reset();
		m_device = nullptr;
	}

	bool DX12Swapchain::resize(u32 width, u32 height)
	{
		if (width == 0 || height == 0) return false;
		if (!m_swapchain || !m_device) return false;

		release_back_buffers();

		if (FAILED(m_swapchain->ResizeBuffers(m_buffer_count, width, height, m_format, 0)))
			return false;

		m_width = width;
		m_height = height;

		if (!create_back_buffer_rtvs(m_device)) return false;

		m_current_index = m_swapchain->GetCurrentBackBufferIndex();
		return true;
	}

	void DX12Swapchain::present(bool vsync)
	{
		m_swapchain->Present(vsync ? 1 : 0, 0);
		m_current_index = m_swapchain->GetCurrentBackBufferIndex();
	}

	ID3D12Resource* DX12Swapchain::current_back_buffer() const
	{
		return m_back_buffers[m_current_index].Get();
	}

	D3D12_CPU_DESCRIPTOR_HANDLE DX12Swapchain::current_rtv() const
	{
		D3D12_CPU_DESCRIPTOR_HANDLE handle = m_rtv_heap->GetCPUDescriptorHandleForHeapStart();
		handle.ptr += m_current_index * m_rtv_descriptor_size;
		return handle;
	}

	bool DX12Swapchain::create_rtv_heap(ID3D12Device* device)
	{
		D3D12_DESCRIPTOR_HEAP_DESC desc{};
		desc.NumDescriptors = m_buffer_count;
		desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
		desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;

		if (FAILED(device->CreateDescriptorHeap(&desc, IID_PPV_ARGS(&m_rtv_heap))))
			return false;

		m_rtv_descriptor_size = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
		return true;
	}

	bool DX12Swapchain::create_back_buffer_rtvs(ID3D12Device* device)
	{
		D3D12_CPU_DESCRIPTOR_HANDLE rtvHandle = m_rtv_heap->GetCPUDescriptorHandleForHeapStart();

		for (u32 i = 0; i < m_buffer_count; ++i)
		{
			if (FAILED(m_swapchain->GetBuffer(i, IID_PPV_ARGS(&m_back_buffers[i]))))
				return false;

			device->CreateRenderTargetView(m_back_buffers[i].Get(), nullptr, rtvHandle);
			rtvHandle.ptr += m_rtv_descriptor_size;
		}
		return true;
	}

	void DX12Swapchain::release_back_buffers()
	{
		for (u32 i = 0; i < MaxBufferCount; ++i)
		{
			m_back_buffers[i].Reset();
		}
	}
}
