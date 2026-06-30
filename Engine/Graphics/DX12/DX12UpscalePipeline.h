#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <wrl/client.h>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	/// <summary>
	/// Fullscreen upscale pass: samples a (scaled) offscreen color RT and writes it to the full-res back buffer.
	/// This is the render-scale composite step (3D rendered low-res -> upscaled here -> UI overlay on top) and
	/// the exact slot DLSS-SR plugs into. A fullscreen triangle from SV_VertexID (no vertex buffer / no input
	/// layout); root sig = one SRV table (t0) + one static LINEAR/CLAMP sampler (s0); RTV format = swapchain.
	/// </summary>
	class DX12UpscalePipeline
	{
	public:
		bool initialize(ID3D12Device* device, DXGI_FORMAT rtv_format);
		void shutdown();

		ID3D12PipelineState* pipeline_state() const { return m_pipeline_state.Get(); }
		ID3D12RootSignature* root_signature() const { return m_root_signature.Get(); }
		bool is_initialized() const { return m_pipeline_state != nullptr; }

	private:
		bool compile_shaders();
		bool create_root_signature(ID3D12Device* device);
		bool create_pso(ID3D12Device* device, DXGI_FORMAT rtv_format);

		ComPtr<ID3D12PipelineState> m_pipeline_state;
		ComPtr<ID3D12RootSignature> m_root_signature;
		ComPtr<ID3DBlob> m_vs_blob;
		ComPtr<ID3DBlob> m_ps_blob;
	};
}
