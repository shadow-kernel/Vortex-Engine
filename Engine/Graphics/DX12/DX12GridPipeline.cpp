#include "DX12GridPipeline.h"
#include "DX12ShaderCompiler.h"   // shaders now live in Engine/Shaders/grid.hlsl

namespace vortex::graphics::dx12
{
	namespace
	{
		using PFN_D3D12SerializeRootSignature = HRESULT(WINAPI*)(const D3D12_ROOT_SIGNATURE_DESC*, D3D_ROOT_SIGNATURE_VERSION, ID3DBlob**, ID3DBlob**);

		PFN_D3D12SerializeRootSignature get_serialize_root_signature()
		{
			return reinterpret_cast<PFN_D3D12SerializeRootSignature>(
				GetProcAddress(LoadLibraryW(L"d3d12.dll"), "D3D12SerializeRootSignature"));
		}
	}

	bool DX12GridPipeline::initialize(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format)
	{
		if (!device) return false;

		// Engine/Shaders/grid.hlsl (GridVS/GridPS), via the shared compiler.
		m_vs_blob = DX12ShaderCompiler::load_shader("grid", "vs", "GridVS", "vs_5_0");
		m_ps_blob = DX12ShaderCompiler::load_shader("grid", "ps", "GridPS", "ps_5_0");
		if (!m_vs_blob || !m_ps_blob) {
			OutputDebugStringA("DX12GridPipeline: shader load failed\n");
			return false;
		}

		ComPtr<ID3DBlob> err;
		auto serialize = get_serialize_root_signature();
		if (!serialize) return false;

		D3D12_ROOT_PARAMETER param{};
		param.ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		param.Descriptor.ShaderRegister = 0;
		param.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

		D3D12_ROOT_SIGNATURE_DESC rsDesc{};
		rsDesc.NumParameters = 1;
		rsDesc.pParameters = &param;

		ComPtr<ID3DBlob> sig;
		if (FAILED(serialize(&rsDesc, D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err))) {
			if (err) OutputDebugStringA((char*)err->GetBufferPointer());
			return false;
		}

		if (FAILED(device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), IID_PPV_ARGS(&m_root_signature))))
			return false;

		D3D12_GRAPHICS_PIPELINE_STATE_DESC pso{};
		pso.pRootSignature = m_root_signature.Get();
		pso.VS = { m_vs_blob->GetBufferPointer(), m_vs_blob->GetBufferSize() };
		pso.PS = { m_ps_blob->GetBufferPointer(), m_ps_blob->GetBufferSize() };
		
		pso.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
		pso.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
		pso.RasterizerState.DepthClipEnable = FALSE;
		
		pso.BlendState.RenderTarget[0].BlendEnable = TRUE;
		pso.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND_SRC_ALPHA;
		pso.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND_INV_SRC_ALPHA;
		pso.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP_ADD;
		pso.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND_ONE;
		pso.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND_ZERO;
		pso.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP_ADD;
		pso.BlendState.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
		
		pso.DepthStencilState.DepthEnable = TRUE;
		// Grid is the LOWEST-priority layer: it still depth-TESTS (so closer geometry hides it) but never
		// WRITES depth. The scene (incl. a ground plane at y=0) is drawn AFTER the grid, so it always paints
		// over it — no z-fighting/flicker when a floor sits exactly on the grid plane. It still draws over the
		// skybox because it passes the test against the far-plane background.
		pso.DepthStencilState.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO;
		pso.DepthStencilState.DepthFunc = D3D12_COMPARISON_FUNC_LESS_EQUAL;
		
		pso.SampleMask = UINT_MAX;
		pso.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
		pso.NumRenderTargets = 1;
		pso.RTVFormats[0] = rtv_format;
		pso.DSVFormat = dsv_format;
		pso.SampleDesc.Count = 1;

		if (FAILED(device->CreateGraphicsPipelineState(&pso, IID_PPV_ARGS(&m_pipeline_state)))) {
			OutputDebugStringA("DX12GridPipeline: PSO creation failed\n");
			return false;
		}

		OutputDebugStringA("DX12GridPipeline: Initialized OK\n");
		return true;
	}

	void DX12GridPipeline::shutdown()
	{
		m_pipeline_state.Reset();
		m_root_signature.Reset();
		m_vs_blob.Reset();
		m_ps_blob.Reset();
	}
}
