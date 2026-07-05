#include "Mesh.h"
#include "../../Common/VerboseLog.h"
#include <cstring>

namespace vortex::graphics
{
	Mesh::Mesh(Mesh&& other) noexcept
		: m_vertex_buffer(std::move(other.m_vertex_buffer))
		, m_index_buffer(std::move(other.m_index_buffer))
		, m_vb_view(other.m_vb_view)
		, m_ib_view(other.m_ib_view)
		, m_vertex_count(other.m_vertex_count)
		, m_index_count(other.m_index_count)
		, m_skinned(other.m_skinned)
		, m_name(std::move(other.m_name))
	{
		other.m_vb_view = {};
		other.m_ib_view = {};
		other.m_vertex_count = 0;
		other.m_index_count = 0;
		other.m_skinned = false;
	}

	Mesh& Mesh::operator=(Mesh&& other) noexcept
	{
		if (this != &other)
		{
			destroy();
			m_vertex_buffer = std::move(other.m_vertex_buffer);
			m_index_buffer = std::move(other.m_index_buffer);
			m_vb_view = other.m_vb_view;
			m_ib_view = other.m_ib_view;
			m_vertex_count = other.m_vertex_count;
			m_index_count = other.m_index_count;
			m_skinned = other.m_skinned;
			m_name = std::move(other.m_name);

			other.m_vb_view = {};
			other.m_ib_view = {};
			other.m_vertex_count = 0;
			other.m_index_count = 0;
			other.m_skinned = false;
		}
		return *this;
	}

	bool Mesh::create(ID3D12Device* device, const MeshData& data)
	{
		if (data.vertices.empty()) return false;

		VORTEX_VLOG(("Mesh::create - vertices: " + std::to_string(data.vertices.size()) +
			", indices: " + std::to_string(data.indices.size()) + "\n").c_str());

		return create_from_vertices(device,
			data.vertices.data(),
			static_cast<u32>(data.vertices.size()),
			sizeof(VertexPosNormalUV),
			data.indices.empty() ? nullptr : data.indices.data(),
			static_cast<u32>(data.indices.size()));
	}

	bool Mesh::create_from_generator(ID3D12Device* device, const IMeshGenerator& generator)
	{
		MeshData data = generator.generate();
		m_name = generator.type_name();
		return create(device, data);
	}

	bool Mesh::create_from_vertices(ID3D12Device* device, const void* vertices, u32 vertex_count, u32 vertex_stride,
									const u32* indices, u32 index_count)
	{
		if (!device || !vertices || vertex_count == 0) return false;

		destroy();

		const UINT vb_size = vertex_count * vertex_stride;

		D3D12_HEAP_PROPERTIES heap_props{};
		heap_props.Type = D3D12_HEAP_TYPE_UPLOAD;

		D3D12_RESOURCE_DESC res_desc{};
		res_desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		res_desc.Width = vb_size;
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
			std::memcpy(mapped, vertices, vb_size);
			m_vertex_buffer->Unmap(0, nullptr);
		}

		m_vb_view.BufferLocation = m_vertex_buffer->GetGPUVirtualAddress();
		m_vb_view.StrideInBytes = vertex_stride;
		m_vb_view.SizeInBytes = vb_size;
		m_vertex_count = vertex_count;

		if (indices && index_count > 0)
		{
			const UINT ib_size = index_count * sizeof(u32);

			res_desc.Width = ib_size;

			if (FAILED(device->CreateCommittedResource(&heap_props, D3D12_HEAP_FLAG_NONE,
				&res_desc, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_index_buffer))))
			{
				return false;
			}

			if (SUCCEEDED(m_index_buffer->Map(0, &read_range, &mapped)))
			{
				std::memcpy(mapped, indices, ib_size);
				m_index_buffer->Unmap(0, nullptr);
			}

			m_ib_view.BufferLocation = m_index_buffer->GetGPUVirtualAddress();
			m_ib_view.Format = DXGI_FORMAT_R32_UINT;
			m_ib_view.SizeInBytes = ib_size;
			m_index_count = index_count;
		}

		return true;
	}

	void Mesh::destroy()
	{
		m_vertex_buffer.Reset();
		m_index_buffer.Reset();
		m_vb_view = {};
		m_ib_view = {};
		m_vertex_count = 0;
		m_index_count = 0;
		m_skinned = false;
		m_name.clear();
		m_bounds_min[0] = m_bounds_min[1] = m_bounds_min[2] = 0;
		m_bounds_max[0] = m_bounds_max[1] = m_bounds_max[2] = 1;
	}

	void Mesh::set_bounds(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
	{
		m_bounds_min[0] = minX;
		m_bounds_min[1] = minY;
		m_bounds_min[2] = minZ;
		m_bounds_max[0] = maxX;
		m_bounds_max[1] = maxY;
		m_bounds_max[2] = maxZ;
	}

	void Mesh::get_bounds(float& sizeX, float& sizeY, float& sizeZ) const
	{
		sizeX = m_bounds_max[0] - m_bounds_min[0];
		sizeY = m_bounds_max[1] - m_bounds_min[1];
		sizeZ = m_bounds_max[2] - m_bounds_min[2];
		
		// Ensure minimum size of 1 for empty/invalid bounds
		if (sizeX < 0.001f) sizeX = 1.0f;
		if (sizeY < 0.001f) sizeY = 1.0f;
		if (sizeZ < 0.001f) sizeZ = 1.0f;
	}

	void Mesh::get_bounds_center(float& centerX, float& centerY, float& centerZ) const
	{
		centerX = (m_bounds_min[0] + m_bounds_max[0]) * 0.5f;
		centerY = (m_bounds_min[1] + m_bounds_max[1]) * 0.5f;
		centerZ = (m_bounds_min[2] + m_bounds_max[2]) * 0.5f;
	}
}
