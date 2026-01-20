#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <wrl/client.h>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	struct Vertex
	{
		float position[3];
		float color[3];
	};

	class DX12Geometry
	{
	public:
		bool initialize(ID3D12Device* device);
		void shutdown();

		const D3D12_VERTEX_BUFFER_VIEW& vertex_buffer_view() const { return m_vb_view; }
		u32 vertex_count() const { return m_vertex_count; }

	private:
		ComPtr<ID3D12Resource> m_vertex_buffer;
		D3D12_VERTEX_BUFFER_VIEW m_vb_view{};
		u32 m_vertex_count{ 0 };
	};
}
