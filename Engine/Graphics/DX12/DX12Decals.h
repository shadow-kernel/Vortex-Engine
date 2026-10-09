#pragma once

// ============================================================================
// DX12Decals — the projected decal pass on DirectX 12 (#120), fed by decals.hlsl. Draws the frame's decal boxes
// (Graphics/Decals/Decals.h) between the opaque and the transparent meshes of the world pass: back faces only, no
// depth test, the scene position reconstructed from a copy of the pass's depth (DX12Particles owns the copy), the
// material's albedo texture + base colour blended lit / multiplied / additive. Instances and constants come from
// the shared per-frame upload ring.
// ============================================================================
#include "../../Common/CommonHeaders.h"
#include "../Decals/Decals.h"
#include "DX12FrameRing.h"
#include "DX12Particles.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <DirectXMath.h>
#include <vector>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	class DX12Decals
	{
	public:
		struct View
		{
			DirectX::XMFLOAT4X4 view_projection;
			DirectX::XMFLOAT4X4 inv_view_projection;
			DirectX::XMFLOAT3 eye;
			float near_clip{ 0.1f }, far_clip{ 1000.0f };
			bool ortho{ false };
		};

		bool initialize(ID3D12Device* device, DX12FrameRing* ring, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format);
		void shutdown();
		bool ready() const { return m_ready; }

		// Draw every decal of `list` into the bound target. `depth_srv` is the scene depth copy (DX12Particles::
		// scene_depth_srv), `lights` the renderer's light constant buffer. Leaves the particle / decal root
		// signature bound — the caller rebinds the scene pass.
		void draw(ID3D12GraphicsCommandList* cmd, D3D12_CPU_DESCRIPTOR_HANDLE rtv, D3D12_CPU_DESCRIPTOR_HANDLE dsv,
			D3D12_GPU_DESCRIPTOR_HANDLE depth_srv, u32 w, u32 h, const View& view, const DX12Particles::Environment& env,
			D3D12_GPU_VIRTUAL_ADDRESS lights, const std::vector<decals::Decal>& list);

	private:
		bool create_root_signature();
		bool create_pipelines(DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format);

		ID3D12Device* m_device{ nullptr };
		DX12FrameRing* m_ring{ nullptr };
		bool m_ready{ false };
		ComPtr<ID3D12RootSignature> m_root_signature;
		ComPtr<ID3DBlob> m_vs, m_ps;
		ComPtr<ID3D12PipelineState> m_pso[3];   // by blend mode
		std::vector<decals::GpuDecal> m_instances;
		std::vector<decals::Batch> m_batches;
	};
}
