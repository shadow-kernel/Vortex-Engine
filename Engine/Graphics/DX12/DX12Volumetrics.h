#pragma once

// ============================================================================
// DX12Volumetrics — volumetric fog on DirectX 12 (#119), fed by volumetrics.hlsl. VolFogPS ray-marches a half-
// resolution target (R16G16B16A16: scattered light in rgb, transmittance in a) through the scene depth copy, the
// renderer's light buffer and its shadow atlases; VolCompositePS blends it over the scene (ONE / SRC_ALPHA) after
// the world's opaque + transparent meshes and before the particles. See Graphics/Volumetrics/Volumetrics.h.
// ============================================================================
#include "../../Common/CommonHeaders.h"
#include "../Volumetrics/Volumetrics.h"
#include "DX12FrameRing.h"
#include "DX12Particles.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <DirectXMath.h>
#include <chrono>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	class DX12Volumetrics
	{
	public:
		struct View
		{
			DirectX::XMFLOAT4X4 inv_view_projection;
			DirectX::XMFLOAT3 eye;
			float near_clip{ 0.1f }, far_clip{ 1000.0f };
			bool ortho{ false };
		};

		bool initialize(ID3D12Device* device, DX12FrameRing* ring, DXGI_FORMAT scene_rtv_format, DXGI_FORMAT dsv_format);
		void shutdown();
		bool ready() const { return m_ready; }

		void set_params(const volumetrics::Params& p) { m_params = p; }
		const volumetrics::Params& params() const { return m_params; }
		bool active() const { return m_ready && m_params.enabled && m_params.density > 0.0f; }

		// Draw the fog over the bound scene target (rtv / dsv of the current pass; `depth_srv` = the scene depth copy).
		// The shadow atlas handles may be {} (no shadows then). Leaves the fog root signature bound — the caller rebinds.
		void draw(ID3D12GraphicsCommandList* cmd, D3D12_CPU_DESCRIPTOR_HANDLE rtv, D3D12_CPU_DESCRIPTOR_HANDLE dsv,
			D3D12_GPU_DESCRIPTOR_HANDLE depth_srv, u32 w, u32 h, const View& view, const DX12Particles::Environment& env,
			D3D12_GPU_VIRTUAL_ADDRESS lights_cb, D3D12_GPU_DESCRIPTOR_HANDLE spot_atlas, D3D12_GPU_DESCRIPTOR_HANDLE csm_atlas,
			D3D12_GPU_DESCRIPTOR_HANDLE point_atlas, float shadow_map_texel);

	private:
		bool create_root_signature();
		bool create_pipelines(DXGI_FORMAT scene_rtv_format, DXGI_FORMAT dsv_format);
		bool ensure_target(u32 w, u32 h);

		ID3D12Device* m_device{ nullptr };
		DX12FrameRing* m_ring{ nullptr };
		bool m_ready{ false };
		volumetrics::Params m_params{};
		ComPtr<ID3D12RootSignature> m_root_signature;
		ComPtr<ID3DBlob> m_vs, m_ps_march, m_ps_composite;
		ComPtr<ID3D12PipelineState> m_pso_march, m_pso_composite;

		// the half-resolution fog target (RGBA16F), its RTV and its SRV slot in the registry heap
		ComPtr<ID3D12Resource> m_target;
		ComPtr<ID3D12DescriptorHeap> m_rtv_heap;
		D3D12_RESOURCE_STATES m_target_state{ D3D12_RESOURCE_STATE_RENDER_TARGET };
		D3D12_CPU_DESCRIPTOR_HANDLE m_srv_cpu{};
		D3D12_GPU_DESCRIPTOR_HANDLE m_srv_gpu{};
		bool m_srv_slot{ false };
		u32 m_w{ 0 }, m_h{ 0 };
		std::chrono::steady_clock::time_point m_origin{ std::chrono::steady_clock::now() };
	};
}
