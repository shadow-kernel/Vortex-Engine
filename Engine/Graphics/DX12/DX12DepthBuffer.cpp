#include "DX12DepthBuffer.h"

namespace vortex::graphics::dx12
{
	bool DX12DepthBuffer::initialize(ID3D12Device* device, u32 width, u32 height, DXGI_FORMAT format)
	{
		if (!device || width == 0 || height == 0) return false;

		m_width = width;
		m_height = height;
		m_format = format;

		if (!create_dsv_heap(device)) return false;
		if (!create_depth_buffer(device)) return false;

		return true;
	}

	void DX12DepthBuffer::shutdown()
	{
		m_depth_buffer.Reset();
		m_dsv_heap.Reset();
		m_dsv = {};
	}

	bool DX12DepthBuffer::resize(ID3D12Device* device, u32 width, u32 height)
	{
		if (width == m_width && height == m_height) return true;
		if (width == 0 || height == 0) return false;

		m_width = width;
		m_height = height;

		m_depth_buffer.Reset();
		return create_depth_buffer(device);
	}

	bool DX12DepthBuffer::create_depth_buffer(ID3D12Device* device)
	{
		D3D12_HEAP_PROPERTIES heap_props{};
		heap_props.Type = D3D12_HEAP_TYPE_DEFAULT;

		D3D12_RESOURCE_DESC res_desc{};
		res_desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
		res_desc.Width = m_width;
		res_desc.Height = m_height;
		res_desc.DepthOrArraySize = 1;
		res_desc.MipLevels = 1;
		res_desc.Format = m_format;
		res_desc.SampleDesc.Count = 1;
		res_desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;

		D3D12_CLEAR_VALUE clear_value{};
		clear_value.Format = m_format;
		clear_value.DepthStencil.Depth = 1.0f;
		clear_value.DepthStencil.Stencil = 0;

		if (FAILED(device->CreateCommittedResource(&heap_props, D3D12_HEAP_FLAG_NONE,
			&res_desc, D3D12_RESOURCE_STATE_DEPTH_WRITE, &clear_value, IID_PPV_ARGS(&m_depth_buffer))))
		{
			return false;
		}

		// Create DSV
		D3D12_DEPTH_STENCIL_VIEW_DESC dsv_desc{};
		dsv_desc.Format = m_format;
		dsv_desc.ViewDimension = D3D12_DSV_DIMENSION_TEXTURE2D;
		dsv_desc.Texture2D.MipSlice = 0;

		device->CreateDepthStencilView(m_depth_buffer.Get(), &dsv_desc, m_dsv);

		return true;
	}

	bool DX12DepthBuffer::create_dsv_heap(ID3D12Device* device)
	{
		D3D12_DESCRIPTOR_HEAP_DESC heap_desc{};
		heap_desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_DSV;
		heap_desc.NumDescriptors = 1;

		if (FAILED(device->CreateDescriptorHeap(&heap_desc, IID_PPV_ARGS(&m_dsv_heap))))
			return false;

		m_dsv = m_dsv_heap->GetCPUDescriptorHandleForHeapStart();
		return true;
	}
}
