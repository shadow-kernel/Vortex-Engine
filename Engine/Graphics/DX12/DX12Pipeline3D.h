#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <wrl/client.h>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	/// <summary>
	/// 3D Pipeline with MVP transformation, lighting, and depth testing.
	/// </summary>
	class DX12Pipeline3D
	{
	public:
		bool initialize(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format);
		void shutdown();

		ID3D12RootSignature* root_signature() const { return m_root_signature.Get(); }
		ID3D12PipelineState* pipeline_state() const { return m_pipeline_state.Get(); }

		// Wireframe mode for debugging
		ID3D12PipelineState* wireframe_pso() const { return m_wireframe_pso.Get(); }

	private:
		bool compile_shaders();
		bool create_root_signature(ID3D12Device* device);
		bool create_pso(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format);

		ComPtr<ID3D12RootSignature> m_root_signature;
		ComPtr<ID3D12PipelineState> m_pipeline_state;
		ComPtr<ID3D12PipelineState> m_wireframe_pso;
		ComPtr<ID3DBlob> m_vs_blob;
		ComPtr<ID3DBlob> m_ps_blob;
	};
}
