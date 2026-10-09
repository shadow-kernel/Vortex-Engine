#pragma once

// ============================================================================
// DX12FrameRing — a per-frame linear upload allocator shared by the effect passes (particles, decals): constant
// slots, instance arrays, vertex / index data for one frame are bump-allocated from an upload-heap buffer, and
// the renderer keeps fewer than FRAMES frames in flight (it waits on the frame fence), so a slot is reused three
// frames later without any further synchronisation. Buffers an in-flight frame may still read (a grown ring, a
// resized texture handed to retire()) die through a graveyard FRAMES + 1 frames later.
// render_scene_to_target() runs against an idle GPU and simply shares the current frame's slot.
// ============================================================================
#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <string>
#include <utility>
#include <vector>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	class DX12FrameRing
	{
	public:
		static constexpr u32 FRAMES = 3;
		struct Upload { u8* cpu{ nullptr }; D3D12_GPU_VIRTUAL_ADDRESS gpu{ 0 }; };

		void initialize(ID3D12Device* device) { shutdown(); m_device = device; }
		void shutdown()
		{
			for (auto& r : m_rings) r = Ring{};
			m_graveyard.clear();
			m_device = nullptr;
		}
		u64 frame() const { return m_frame; }

		// Once per renderer frame, right after the frame fence wait.
		void begin_frame()
		{
			++m_frame;
			for (auto it = m_graveyard.begin(); it != m_graveyard.end();)
			{
				if (it->first + FRAMES + 1 <= m_frame) it = m_graveyard.erase(it);
				else ++it;
			}
			m_rings[m_frame % FRAMES].used = 0;
		}

		// Keeps a resource alive until every frame that may read it has retired.
		void retire(const ComPtr<ID3D12Resource>& res) { if (res) m_graveyard.emplace_back(m_frame, res); }

		Upload alloc(u64 bytes, u64 align = 256)
		{
			Upload u{};
			if (bytes == 0 || !m_device) return u;
			Ring& r = m_rings[m_frame % FRAMES];
			u64 off = (r.used + align - 1) / align * align;
			if (!r.buffer || off + bytes > r.cap)
			{
				// Grow: the frame's earlier allocations stay valid in the old buffer until it is retired.
				if (r.buffer) retire(r.buffer);
				r.buffer.Reset(); r.mapped = nullptr; r.used = 0; off = 0;
				u64 cap = (std::max<u64>)(1ull << 20, r.cap * 2);
				while (cap < bytes) cap *= 2;
				D3D12_HEAP_PROPERTIES hp{};
				hp.Type = D3D12_HEAP_TYPE_UPLOAD;
				D3D12_RESOURCE_DESC rd{};
				rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
				rd.Width = cap;
				rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
				rd.SampleDesc.Count = 1;
				rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
				if (FAILED(m_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&r.buffer))))
				{
					r.cap = 0;
					OutputDebugStringA(("[fx] upload ring allocation failed (" + std::to_string(cap) + " bytes)\n").c_str());
					return u;
				}
				D3D12_RANGE none{ 0, 0 };
				void* p = nullptr;
				if (FAILED(r.buffer->Map(0, &none, &p)) || !p) { r.buffer.Reset(); r.cap = 0; return u; }
				r.mapped = static_cast<u8*>(p);
				r.cap = cap;
			}
			u.cpu = r.mapped + off;
			u.gpu = r.buffer->GetGPUVirtualAddress() + off;
			r.used = off + bytes;
			return u;
		}

	private:
		struct Ring { ComPtr<ID3D12Resource> buffer; u8* mapped{ nullptr }; u64 cap{ 0 }, used{ 0 }; };
		ID3D12Device* m_device{ nullptr };
		Ring m_rings[FRAMES];
		std::vector<std::pair<u64, ComPtr<ID3D12Resource>>> m_graveyard;   // (retired in frame, resource)
		u64 m_frame{ 0 };
	};
}
