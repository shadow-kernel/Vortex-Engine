#include "DX12RenderTarget.h"
#include "DX12Core.h"

namespace vortex::graphics::dx12
{
	DX12RenderTarget::~DX12RenderTarget()
	{
		shutdown();
	}

	bool DX12RenderTarget::initialize(ID3D12Device* device, u32 width, u32 height, DXGI_FORMAT format, bool sampleable_depth)
	{
		if (!device || width == 0 || height == 0) return false;
		if (m_initialized) shutdown();

		m_width = width;
		m_height = height;
		m_format = format;
		m_sampleable_depth = sampleable_depth;

		if (!create_descriptor_heaps(device)) return false;
		if (!create_render_target(device)) return false;
		if (!create_depth_buffer(device)) return false;
		if (!create_staging_buffer(device)) return false;

		m_initialized = true;
		return true;
	}

	void DX12RenderTarget::shutdown()
	{
		if (m_staging_mapped)
		{
			unmap_staging_buffer();
		}

		m_staging_buffer.Reset();
		m_depth_buffer.Reset();
		m_render_target.Reset();
		m_srv_heap.Reset();
		m_dsv_heap.Reset();
		m_rtv_heap.Reset();
		
		m_initialized = false;
		m_width = 0;
		m_height = 0;
	}

	bool DX12RenderTarget::resize(ID3D12Device* device, u32 width, u32 height)
	{
		if (!device || width == 0 || height == 0) return false;
		if (width == m_width && height == m_height) return true;

		// Release old resources
		m_staging_buffer.Reset();
		m_depth_buffer.Reset();
		m_render_target.Reset();

		m_width = width;
		m_height = height;

		if (!create_render_target(device)) return false;
		if (!create_depth_buffer(device)) return false;
		if (!create_staging_buffer(device)) return false;

		return true;
	}

	bool DX12RenderTarget::create_descriptor_heaps(ID3D12Device* device)
	{
		// RTV heap
		D3D12_DESCRIPTOR_HEAP_DESC rtv_desc{};
		rtv_desc.NumDescriptors = 1;
		rtv_desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
		rtv_desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;
		if (FAILED(device->CreateDescriptorHeap(&rtv_desc, IID_PPV_ARGS(&m_rtv_heap))))
			return false;

		// DSV heap
		D3D12_DESCRIPTOR_HEAP_DESC dsv_desc{};
		dsv_desc.NumDescriptors = 1;
		dsv_desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_DSV;
		dsv_desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;
		if (FAILED(device->CreateDescriptorHeap(&dsv_desc, IID_PPV_ARGS(&m_dsv_heap))))
			return false;

		// SRV heap (shader visible for texture sampling). Slot 0 = color; slot 1 = depth (when sampleable).
		D3D12_DESCRIPTOR_HEAP_DESC srv_desc{};
		srv_desc.NumDescriptors = m_sampleable_depth ? 2 : 1;
		srv_desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
		srv_desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
		if (FAILED(device->CreateDescriptorHeap(&srv_desc, IID_PPV_ARGS(&m_srv_heap))))
			return false;
		m_srv_increment = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

		return true;
	}

	bool DX12RenderTarget::create_render_target(ID3D12Device* device)
	{
		D3D12_HEAP_PROPERTIES heap_props{};
		heap_props.Type = D3D12_HEAP_TYPE_DEFAULT;

		D3D12_RESOURCE_DESC desc{};
		desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
		desc.Width = m_width;
		desc.Height = m_height;
		desc.DepthOrArraySize = 1;
		desc.MipLevels = 1;
		desc.Format = m_format;
		desc.SampleDesc.Count = 1;
		desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;

		D3D12_CLEAR_VALUE clear_value{};
		clear_value.Format = m_format;
		clear_value.Color[0] = 0.2f;
		clear_value.Color[1] = 0.2f;
		clear_value.Color[2] = 0.2f;
		clear_value.Color[3] = 1.0f;

		m_current_state = D3D12_RESOURCE_STATE_RENDER_TARGET;

		if (FAILED(device->CreateCommittedResource(
			&heap_props, D3D12_HEAP_FLAG_NONE,
			&desc, m_current_state,
			&clear_value, IID_PPV_ARGS(&m_render_target))))
		{
			return false;
		}

		// Create RTV
		D3D12_RENDER_TARGET_VIEW_DESC rtv_desc{};
		rtv_desc.Format = m_format;
		rtv_desc.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2D;
		device->CreateRenderTargetView(m_render_target.Get(), &rtv_desc, 
			m_rtv_heap->GetCPUDescriptorHandleForHeapStart());

		// Create SRV
		D3D12_SHADER_RESOURCE_VIEW_DESC srv_desc{};
		srv_desc.Format = m_format;
		srv_desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
		srv_desc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
		srv_desc.Texture2D.MipLevels = 1;
		device->CreateShaderResourceView(m_render_target.Get(), &srv_desc,
			m_srv_heap->GetCPUDescriptorHandleForHeapStart());

		return true;
	}

	bool DX12RenderTarget::create_depth_buffer(ID3D12Device* device)
	{
		D3D12_HEAP_PROPERTIES heap_props{};
		heap_props.Type = D3D12_HEAP_TYPE_DEFAULT;

		// Sampleable depth (DLSS input): the resource must be TYPELESS so it can carry both a D32_FLOAT DSV and
		// an R32_FLOAT SRV. Functionally identical for depth render/test — the DSV still views it as D32_FLOAT.
		D3D12_RESOURCE_DESC desc{};
		desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
		desc.Width = m_width;
		desc.Height = m_height;
		desc.DepthOrArraySize = 1;
		desc.MipLevels = 1;
		desc.Format = m_sampleable_depth ? DXGI_FORMAT_R32_TYPELESS : DXGI_FORMAT_D32_FLOAT;
		desc.SampleDesc.Count = 1;
		desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;

		D3D12_CLEAR_VALUE clear_value{};
		clear_value.Format = DXGI_FORMAT_D32_FLOAT;   // clear value uses the DSV (typed) format
		clear_value.DepthStencil.Depth = 1.0f;

		if (FAILED(device->CreateCommittedResource(
			&heap_props, D3D12_HEAP_FLAG_NONE,
			&desc, D3D12_RESOURCE_STATE_DEPTH_WRITE,
			&clear_value, IID_PPV_ARGS(&m_depth_buffer))))
		{
			return false;
		}

		// Create DSV (explicit D32_FLOAT — required when the resource is typeless)
		D3D12_DEPTH_STENCIL_VIEW_DESC dsv_desc{};
		dsv_desc.Format = DXGI_FORMAT_D32_FLOAT;
		dsv_desc.ViewDimension = D3D12_DSV_DIMENSION_TEXTURE2D;
		device->CreateDepthStencilView(m_depth_buffer.Get(), &dsv_desc,
			m_dsv_heap->GetCPUDescriptorHandleForHeapStart());

		// Depth SRV at SRV-heap slot 1 (R32_FLOAT view of the typeless depth) — DLSS samples this.
		if (m_sampleable_depth)
		{
			D3D12_SHADER_RESOURCE_VIEW_DESC sd{};
			sd.Format = DXGI_FORMAT_R32_FLOAT;
			sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
			sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
			sd.Texture2D.MipLevels = 1;
			D3D12_CPU_DESCRIPTOR_HANDLE h = m_srv_heap->GetCPUDescriptorHandleForHeapStart();
			h.ptr += (SIZE_T)m_srv_increment; // slot 1
			device->CreateShaderResourceView(m_depth_buffer.Get(), &sd, h);
		}

		return true;
	}

	bool DX12RenderTarget::create_staging_buffer(ID3D12Device* device)
	{
		// Calculate aligned row pitch
		u32 bytes_per_pixel = 4; // RGBA8
		m_staging_row_pitch = (m_width * bytes_per_pixel + 255) & ~255; // 256-byte aligned

		D3D12_HEAP_PROPERTIES heap_props{};
		heap_props.Type = D3D12_HEAP_TYPE_READBACK;

		D3D12_RESOURCE_DESC desc{};
		desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		desc.Width = static_cast<UINT64>(m_staging_row_pitch) * m_height;
		desc.Height = 1;
		desc.DepthOrArraySize = 1;
		desc.MipLevels = 1;
		desc.Format = DXGI_FORMAT_UNKNOWN;
		desc.SampleDesc.Count = 1;
		desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

		if (FAILED(device->CreateCommittedResource(
			&heap_props, D3D12_HEAP_FLAG_NONE,
			&desc, D3D12_RESOURCE_STATE_COPY_DEST,
			nullptr, IID_PPV_ARGS(&m_staging_buffer))))
		{
			return false;
		}

		return true;
	}

	D3D12_CPU_DESCRIPTOR_HANDLE DX12RenderTarget::rtv() const
	{
		return m_rtv_heap ? m_rtv_heap->GetCPUDescriptorHandleForHeapStart() 
						  : D3D12_CPU_DESCRIPTOR_HANDLE{};
	}

	D3D12_CPU_DESCRIPTOR_HANDLE DX12RenderTarget::dsv() const
	{
		return m_dsv_heap ? m_dsv_heap->GetCPUDescriptorHandleForHeapStart()
						  : D3D12_CPU_DESCRIPTOR_HANDLE{};
	}

	D3D12_CPU_DESCRIPTOR_HANDLE DX12RenderTarget::srv() const
	{
		return m_srv_heap ? m_srv_heap->GetCPUDescriptorHandleForHeapStart()
						  : D3D12_CPU_DESCRIPTOR_HANDLE{};
	}

	D3D12_GPU_DESCRIPTOR_HANDLE DX12RenderTarget::srv_gpu() const
	{
		return m_srv_heap ? m_srv_heap->GetGPUDescriptorHandleForHeapStart()
						  : D3D12_GPU_DESCRIPTOR_HANDLE{};
	}

	D3D12_GPU_DESCRIPTOR_HANDLE DX12RenderTarget::depth_srv_gpu() const
	{
		if (!m_srv_heap || !m_sampleable_depth) return D3D12_GPU_DESCRIPTOR_HANDLE{};
		D3D12_GPU_DESCRIPTOR_HANDLE h = m_srv_heap->GetGPUDescriptorHandleForHeapStart();
		h.ptr += (UINT64)m_srv_increment; // slot 1 = depth SRV
		return h;
	}

	void DX12RenderTarget::transition_to_render_target(ID3D12GraphicsCommandList* cmd)
	{
		if (!cmd || m_current_state == D3D12_RESOURCE_STATE_RENDER_TARGET) return;

		D3D12_RESOURCE_BARRIER barrier{};
		barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
		barrier.Transition.pResource = m_render_target.Get();
		barrier.Transition.StateBefore = m_current_state;
		barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
		barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
		cmd->ResourceBarrier(1, &barrier);

		m_current_state = D3D12_RESOURCE_STATE_RENDER_TARGET;
	}

	void DX12RenderTarget::transition_to_shader_resource(ID3D12GraphicsCommandList* cmd)
	{
		if (!cmd || m_current_state == D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE) return;

		D3D12_RESOURCE_BARRIER barrier{};
		barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
		barrier.Transition.pResource = m_render_target.Get();
		barrier.Transition.StateBefore = m_current_state;
		barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
		barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
		cmd->ResourceBarrier(1, &barrier);

		m_current_state = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
	}

	void DX12RenderTarget::transition_to_copy_source(ID3D12GraphicsCommandList* cmd)
	{
		if (!cmd || m_current_state == D3D12_RESOURCE_STATE_COPY_SOURCE) return;

		D3D12_RESOURCE_BARRIER barrier{};
		barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
		barrier.Transition.pResource = m_render_target.Get();
		barrier.Transition.StateBefore = m_current_state;
		barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
		barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
		cmd->ResourceBarrier(1, &barrier);

		m_current_state = D3D12_RESOURCE_STATE_COPY_SOURCE;
	}

	bool DX12RenderTarget::copy_to_staging(ID3D12GraphicsCommandList* cmd)
	{
		if (!cmd || !m_staging_buffer || !m_render_target) return false;

		// Ensure render target is in copy source state
		transition_to_copy_source(cmd);

		// Setup copy destination
		D3D12_TEXTURE_COPY_LOCATION dst{};
		dst.pResource = m_staging_buffer.Get();
		dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
		dst.PlacedFootprint.Offset = 0;
		dst.PlacedFootprint.Footprint.Format = m_format;
		dst.PlacedFootprint.Footprint.Width = m_width;
		dst.PlacedFootprint.Footprint.Height = m_height;
		dst.PlacedFootprint.Footprint.Depth = 1;
		dst.PlacedFootprint.Footprint.RowPitch = m_staging_row_pitch;

		// Setup copy source
		D3D12_TEXTURE_COPY_LOCATION src{};
		src.pResource = m_render_target.Get();
		src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
		src.SubresourceIndex = 0;

		cmd->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);

		return true;
	}

	const void* DX12RenderTarget::map_staging_buffer()
	{
		if (!m_staging_buffer || m_staging_mapped) return nullptr;

		void* data = nullptr;
		D3D12_RANGE read_range{ 0, static_cast<SIZE_T>(m_staging_row_pitch) * m_height };
		
		if (SUCCEEDED(m_staging_buffer->Map(0, &read_range, &data)))
		{
			m_staging_mapped = true;
			return data;
		}

		return nullptr;
	}

	void DX12RenderTarget::unmap_staging_buffer()
	{
		if (m_staging_buffer && m_staging_mapped)
		{
			D3D12_RANGE write_range{ 0, 0 }; // We didn't write anything
			m_staging_buffer->Unmap(0, &write_range);
			m_staging_mapped = false;
		}
	}
}
