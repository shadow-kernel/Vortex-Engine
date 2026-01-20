#include "DX12Geometry.h"
#include <cstring>

namespace vortex::graphics::dx12
{
	bool DX12Geometry::initialize(ID3D12Device* device)
	{
		if (!device) return false;

		Vertex vertices[] = {
			{ {  0.0f,   0.5f, 0.0f }, { 1.0f, 0.0f, 0.0f } },
			{ {  0.5f,  -0.5f, 0.0f }, { 0.0f, 1.0f, 0.0f } },
			{ { -0.5f,  -0.5f, 0.0f }, { 0.0f, 0.0f, 1.0f } }
		};

		m_vertex_count = _countof(vertices);
		const UINT buffer_size = sizeof(vertices);

		D3D12_HEAP_PROPERTIES heap_props{};
		heap_props.Type = D3D12_HEAP_TYPE_UPLOAD;

		D3D12_RESOURCE_DESC res_desc{};
		res_desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		res_desc.Width = buffer_size;
		res_desc.Height = 1;
		res_desc.DepthOrArraySize = 1;
		res_desc.MipLevels = 1;
		res_desc.Format = DXGI_FORMAT_UNKNOWN;
		res_desc.SampleDesc.Count = 1;
		res_desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

		if (FAILED(device->CreateCommittedResource(&heap_props, D3D12_HEAP_FLAG_NONE,
			&res_desc, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_vertex_buffer))))
		{
			return false;
		}

		void* mapped = nullptr;
		D3D12_RANGE read_range{ 0, 0 };
		if (SUCCEEDED(m_vertex_buffer->Map(0, &read_range, &mapped)))
		{
			std::memcpy(mapped, vertices, buffer_size);
			m_vertex_buffer->Unmap(0, nullptr);
		}

		m_vb_view.BufferLocation = m_vertex_buffer->GetGPUVirtualAddress();
		m_vb_view.StrideInBytes = sizeof(Vertex);
		m_vb_view.SizeInBytes = buffer_size;

		return true;
	}

	void DX12Geometry::shutdown()
	{
		m_vertex_buffer.Reset();
		m_vb_view = {};
		m_vertex_count = 0;
	}
}
