#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <string>

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
		
		// Double-sided PSO for skybox/unlit materials (no backface culling)
		ID3D12PipelineState* double_sided_pso() const { return m_double_sided_pso.Get(); }

		// Compile a CUSTOM material shader (.hlsl, VSMain/PSMain) into a PSO that reuses this pipeline's root
		// signature + input layout + render state — only the shader stages differ, so it stays binding-compatible
		// with the same PerFrame/PerObject/light/texture setup. Returns nullptr on any compile/create failure (the
		// caller keeps the built-in PSO as a fallback -> a bad custom shader never black-screens). No device state
		// is mutated. hlsl_path is an ABSOLUTE path to the project's shader file.
		ComPtr<ID3D12PipelineState> create_custom_pso(ID3D12Device* device, const std::wstring& hlsl_path);

	private:
		bool compile_shaders();
		bool create_root_signature(ID3D12Device* device);
		bool create_pso(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format);

		DXGI_FORMAT m_rtv_format{ DXGI_FORMAT_R8G8B8A8_UNORM };  // remembered from initialize (for custom PSOs)
		DXGI_FORMAT m_dsv_format{ DXGI_FORMAT_D32_FLOAT };
		ComPtr<ID3D12RootSignature> m_root_signature;
		ComPtr<ID3D12PipelineState> m_pipeline_state;
		ComPtr<ID3D12PipelineState> m_wireframe_pso;
		ComPtr<ID3D12PipelineState> m_double_sided_pso;
		ComPtr<ID3DBlob> m_vs_blob;
		ComPtr<ID3DBlob> m_ps_blob;
	};
}
