#include "DX12Renderer_Internal.h"

namespace vortex::graphics::dx12
{
	bool DX12Renderer::create_command_allocators()
	{
		auto dev = DX12Core::instance().device();
		for (u32 i = 0; i < DX12Swapchain::MaxBufferCount; ++i)
			if (FAILED(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&m_command_allocators[i]))))
				return false;
		return true;
	}


	bool DX12Renderer::create_command_list()
	{
		auto dev = DX12Core::instance().device();
		return SUCCEEDED(dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
			m_command_allocators[0].Get(), nullptr, IID_PPV_ARGS(&m_command_list))) && SUCCEEDED(m_command_list->Close());
	}


	bool DX12Renderer::create_constant_buffers()
	{
	auto dev = DX12Core::instance().device();
	D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_UPLOAD;
	D3D12_RESOURCE_DESC rd{}; rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
	rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1; rd.SampleDesc.Count = 1;
	rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

	rd.Width = 256;
	if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_per_frame_cb))))
	return false;
	D3D12_RANGE r{0,0};
	if (FAILED(m_per_frame_cb->Map(0, &r, &m_per_frame_cb_mapped))) return false;

	// Per-object constant buffer: ONE 256-byte slot per DRAW RUN (mesh+material), not per instance.
	rd.Width = (UINT64)256 * MAX_DRAW_RUNS;
	if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_per_object_cb))))
	return false;
	if (FAILED(m_per_object_cb->Map(0, &r, &m_per_object_cb_mapped))) return false;

	// Per-instance world matrices for GPU instancing: 64 bytes (4x float4 rows) per instance.
	rd.Width = (UINT64)64 * MAX_RENDER_OBJECTS;
	if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_instance_vb))))
	return false;
	if (FAILED(m_instance_vb->Map(0, &r, &m_instance_vb_mapped))) return false;

	// Bone palettes for GPU skinning: 64 bytes per bone matrix, bound as a root SRV (StructuredBuffer<float4>)
	// at palette offsets. Uploaded once per queue swap from the staged CPU palettes.
	rd.Width = (UINT64)64 * MAX_BONE_MATRICES;
	if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_bone_vb))))
	return false;
	if (FAILED(m_bone_vb->Map(0, &r, &m_bone_vb_mapped))) return false;

	// Light buffer: Point lights (16 * 32 bytes) + Spot lights (8 * 64 bytes) = 1024 bytes, aligned to 256
	rd.Width = 1280; // 256-byte aligned
	if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_light_cb))))
	return false;
	if (FAILED(m_light_cb->Map(0, &r, &m_light_cb_mapped))) return false;

	return true;
	}


	bool DX12Renderer::create_srv_heap()
	{
		auto dev = DX12Core::instance().device();
		if (!dev) return false;

		D3D12_DESCRIPTOR_HEAP_DESC heap_desc{};
		heap_desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
		heap_desc.NumDescriptors = MAX_SRV_DESCRIPTORS;
		heap_desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

		if (FAILED(dev->CreateDescriptorHeap(&heap_desc, IID_PPV_ARGS(&m_srv_heap))))
		{
			OutputDebugStringA("Failed to create SRV descriptor heap\n");
			return false;
		}

		m_srv_descriptor_size = dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
		
		OutputDebugStringA("SRV descriptor heap created successfully\n");
		return true;
	}


}
