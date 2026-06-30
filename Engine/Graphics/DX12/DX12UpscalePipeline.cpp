#include "DX12UpscalePipeline.h"
#include "DX12ShaderCompiler.h"   // shaders now live in Engine/Shaders/upscale.hlsl, loaded via this

namespace vortex::graphics::dx12
{
	namespace
	{
		using PFN_D3D12SerializeRootSignature = HRESULT(WINAPI*)(const D3D12_ROOT_SIGNATURE_DESC*, D3D_ROOT_SIGNATURE_VERSION, ID3DBlob**, ID3DBlob**);

		PFN_D3D12SerializeRootSignature get_serialize_root_signature()
		{
			static auto fn = reinterpret_cast<PFN_D3D12SerializeRootSignature>(
				GetProcAddress(LoadLibraryW(L"d3d12.dll"), "D3D12SerializeRootSignature"));
			return fn;
		}
	}

	bool DX12UpscalePipeline::initialize(ID3D12Device* device, DXGI_FORMAT rtv_format)
	{
		if (!compile_shaders()) return false;
		if (!create_root_signature(device)) return false;
		if (!create_pso(device, rtv_format)) return false;
		OutputDebugStringA("Upscale pipeline initialized\n");
		return true;
	}

	void DX12UpscalePipeline::shutdown()
	{
		m_pipeline_state.Reset();
		m_root_signature.Reset();
		m_vs_blob.Reset();
		m_ps_blob.Reset();
	}

	bool DX12UpscalePipeline::compile_shaders()
	{
		// Engine/Shaders/upscale.hlsl (VSMain/PSMain), via the shared compiler: precompiled .cso if present, else
		// compiled from disk. This pass is optional/non-fatal, so a missing-shader failure degrades gracefully.
		m_vs_blob = DX12ShaderCompiler::load_shader("upscale", "vs", "VSMain", "vs_5_0");
		m_ps_blob = DX12ShaderCompiler::load_shader("upscale", "ps", "PSMain", "ps_5_0");
		return m_vs_blob != nullptr && m_ps_blob != nullptr;
	}

	bool DX12UpscalePipeline::create_root_signature(ID3D12Device* device)
	{
		auto serialize = get_serialize_root_signature();
		if (!serialize) return false;

		// One SRV descriptor table (t0) for the source texture.
		D3D12_DESCRIPTOR_RANGE srv_range{};
		srv_range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
		srv_range.NumDescriptors = 1;
		srv_range.BaseShaderRegister = 0;
		srv_range.RegisterSpace = 0;
		srv_range.OffsetInDescriptorsFromTableStart = 0;

		D3D12_ROOT_PARAMETER param{};
		param.ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		param.DescriptorTable.NumDescriptorRanges = 1;
		param.DescriptorTable.pDescriptorRanges = &srv_range;
		param.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		// Static LINEAR + CLAMP sampler (s0) — true bilinear upscale, no edge wrap-bleed.
		D3D12_STATIC_SAMPLER_DESC sampler{};
		sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
		sampler.AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		sampler.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		sampler.MipLODBias = 0.0f;
		sampler.MaxAnisotropy = 1;
		sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_NEVER;
		sampler.BorderColor = D3D12_STATIC_BORDER_COLOR_OPAQUE_BLACK;
		sampler.MinLOD = 0.0f;
		sampler.MaxLOD = D3D12_FLOAT32_MAX;
		sampler.ShaderRegister = 0;
		sampler.RegisterSpace = 0;
		sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		D3D12_ROOT_SIGNATURE_DESC desc{};
		desc.NumParameters = 1;
		desc.pParameters = &param;
		desc.NumStaticSamplers = 1;
		desc.pStaticSamplers = &sampler;
		desc.Flags = D3D12_ROOT_SIGNATURE_FLAG_NONE;

		ComPtr<ID3DBlob> signature, error;
		if (FAILED(serialize(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error)))
		{
			if (error) OutputDebugStringA(static_cast<const char*>(error->GetBufferPointer()));
			return false;
		}
		return SUCCEEDED(device->CreateRootSignature(0, signature->GetBufferPointer(),
			signature->GetBufferSize(), IID_PPV_ARGS(&m_root_signature)));
	}

	bool DX12UpscalePipeline::create_pso(ID3D12Device* device, DXGI_FORMAT rtv_format)
	{
		D3D12_RASTERIZER_DESC rasterizer{};
		rasterizer.FillMode = D3D12_FILL_MODE_SOLID;
		rasterizer.CullMode = D3D12_CULL_MODE_NONE;
		rasterizer.DepthClipEnable = TRUE;

		D3D12_BLEND_DESC blend{};
		blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

		D3D12_DEPTH_STENCIL_DESC depth_stencil{};
		depth_stencil.DepthEnable = FALSE;   // fullscreen composite — no depth
		depth_stencil.StencilEnable = FALSE;

		D3D12_GRAPHICS_PIPELINE_STATE_DESC pso_desc{};
		pso_desc.pRootSignature = m_root_signature.Get();
		pso_desc.VS = { m_vs_blob->GetBufferPointer(), m_vs_blob->GetBufferSize() };
		pso_desc.PS = { m_ps_blob->GetBufferPointer(), m_ps_blob->GetBufferSize() };
		pso_desc.BlendState = blend;
		pso_desc.SampleMask = UINT_MAX;
		pso_desc.RasterizerState = rasterizer;
		pso_desc.DepthStencilState = depth_stencil;
		pso_desc.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
		pso_desc.NumRenderTargets = 1;
		pso_desc.RTVFormats[0] = rtv_format;
		pso_desc.DSVFormat = DXGI_FORMAT_UNKNOWN;
		pso_desc.SampleDesc.Count = 1;

		return SUCCEEDED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_pipeline_state)));
	}
}
