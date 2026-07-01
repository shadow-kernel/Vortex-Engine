#include "DX12Pipeline3D.h"
#include "DX12ShaderCompiler.h"   // shaders now live in Engine/Shaders/standard.hlsl

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

	bool DX12Pipeline3D::initialize(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format)
	{
		if (!device) return false;
		m_rtv_format = rtv_format; m_dsv_format = dsv_format;   // remembered for custom material PSOs
		if (!compile_shaders()) return false;
		if (!create_root_signature(device)) return false;
		if (!create_pso(device, rtv_format, dsv_format)) return false;
		return true;
	}

	ComPtr<ID3D12PipelineState> DX12Pipeline3D::create_custom_pso(ID3D12Device* device, const std::wstring& hlsl_path)
	{
		if (!device || !m_root_signature || hlsl_path.empty()) return nullptr;

		// Compile VSMain/PSMain from the project's .hlsl. nullptr on failure -> caller keeps the built-in PSO.
		ComPtr<ID3DBlob> vs = DX12ShaderCompiler::compile_from_file(hlsl_path, "VSMain", "vs_5_0");
		ComPtr<ID3DBlob> ps = DX12ShaderCompiler::compile_from_file(hlsl_path, "PSMain", "ps_5_0");
		if (!vs || !ps) return nullptr;

		// Same input layout + render state as the built-in PBR PSO — only the shader stages differ (binding-compatible).
		D3D12_INPUT_ELEMENT_DESC input_layout[] = {
			{ "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "NORMAL", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 24, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "INSTANCEWORLD", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 0,  D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 1, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 16, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 2, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 32, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 3, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 48, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 }
		};

		D3D12_RASTERIZER_DESC rasterizer{};
		rasterizer.FillMode = D3D12_FILL_MODE_SOLID;
		rasterizer.CullMode = D3D12_CULL_MODE_BACK;
		rasterizer.DepthClipEnable = TRUE;

		D3D12_BLEND_DESC blend{};
		blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

		D3D12_DEPTH_STENCIL_DESC depth_stencil{};
		depth_stencil.DepthEnable = TRUE;
		depth_stencil.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ALL;
		depth_stencil.DepthFunc = D3D12_COMPARISON_FUNC_LESS;

		D3D12_GRAPHICS_PIPELINE_STATE_DESC pso_desc{};
		pso_desc.pRootSignature = m_root_signature.Get();
		pso_desc.VS = { vs->GetBufferPointer(), vs->GetBufferSize() };
		pso_desc.PS = { ps->GetBufferPointer(), ps->GetBufferSize() };
		pso_desc.BlendState = blend;
		pso_desc.SampleMask = UINT_MAX;
		pso_desc.RasterizerState = rasterizer;
		pso_desc.DepthStencilState = depth_stencil;
		pso_desc.InputLayout = { input_layout, _countof(input_layout) };
		pso_desc.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
		pso_desc.NumRenderTargets = 1;
		pso_desc.RTVFormats[0] = m_rtv_format;
		pso_desc.DSVFormat = m_dsv_format;
		pso_desc.SampleDesc.Count = 1;

		ComPtr<ID3D12PipelineState> pso;
		if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&pso)))) return nullptr;
		return pso;
	}

	void DX12Pipeline3D::shutdown()
	{
		m_wireframe_pso.Reset();
		m_pipeline_state.Reset();
		m_root_signature.Reset();
		m_vs_blob.Reset();
		m_ps_blob.Reset();
	}

	bool DX12Pipeline3D::compile_shaders()
	{
		// Engine/Shaders/standard.hlsl (VSMain/PSMain), via the shared compiler. This is the FATAL pass — a load
		// failure aborts renderer init (DX12Renderer.cpp) rather than degrading, so this must always resolve.
		m_vs_blob = DX12ShaderCompiler::load_shader("standard", "vs", "VSMain", "vs_5_0");
		m_ps_blob = DX12ShaderCompiler::load_shader("standard", "ps", "PSMain", "ps_5_0");
		return m_vs_blob != nullptr && m_ps_blob != nullptr;
	}

	bool DX12Pipeline3D::create_root_signature(ID3D12Device* device)
	{
		auto serialize = get_serialize_root_signature();
		if (!serialize) return false;

		D3D12_ROOT_PARAMETER params[8] = {};
		
		params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[0].Descriptor.ShaderRegister = 0;
		params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

		params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[1].Descriptor.ShaderRegister = 1;
		params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

		params[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[2].Descriptor.ShaderRegister = 2;
		params[2].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		static D3D12_DESCRIPTOR_RANGE srv_ranges[5] = {};
		for (int i = 0; i < 5; i++)
		{
			srv_ranges[i].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
			srv_ranges[i].NumDescriptors = 1;
			srv_ranges[i].BaseShaderRegister = i;
			srv_ranges[i].RegisterSpace = 0;
			srv_ranges[i].OffsetInDescriptorsFromTableStart = 0;
			
			params[3 + i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
			params[3 + i].DescriptorTable.NumDescriptorRanges = 1;
			params[3 + i].DescriptorTable.pDescriptorRanges = &srv_ranges[i];
			params[3 + i].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		}

		D3D12_STATIC_SAMPLER_DESC sampler{};
		sampler.Filter = D3D12_FILTER_ANISOTROPIC;
		sampler.AddressU = D3D12_TEXTURE_ADDRESS_MODE_WRAP;
		sampler.AddressV = D3D12_TEXTURE_ADDRESS_MODE_WRAP;
		sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_WRAP;
		sampler.MipLODBias = 0.0f;
		sampler.MaxAnisotropy = 16;
		sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_NEVER;
		sampler.BorderColor = D3D12_STATIC_BORDER_COLOR_OPAQUE_WHITE;
		sampler.MinLOD = 0.0f;
		sampler.MaxLOD = D3D12_FLOAT32_MAX;
		sampler.ShaderRegister = 0;
		sampler.RegisterSpace = 0;
		sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		D3D12_ROOT_SIGNATURE_DESC desc{};
		desc.NumParameters = 8;
		desc.pParameters = params;
		desc.NumStaticSamplers = 1;
		desc.pStaticSamplers = &sampler;
		desc.Flags = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;

		ComPtr<ID3DBlob> signature;
		ComPtr<ID3DBlob> error;

		if (FAILED(serialize(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error)))
		{
			if (error) OutputDebugStringA(static_cast<const char*>(error->GetBufferPointer()));
			return false;
		}

		return SUCCEEDED(device->CreateRootSignature(0, signature->GetBufferPointer(),
			signature->GetBufferSize(), IID_PPV_ARGS(&m_root_signature)));
	}

	bool DX12Pipeline3D::create_pso(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format)
	{
		D3D12_INPUT_ELEMENT_DESC input_layout[] = {
			{ "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "NORMAL", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 24, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			// Per-instance world matrix (slot 1, advances once per instance) — GPU instancing.
			{ "INSTANCEWORLD", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 0,  D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 1, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 16, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 2, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 32, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
			{ "INSTANCEWORLD", 3, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 48, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 }
		};

		D3D12_RASTERIZER_DESC rasterizer{};
		rasterizer.FillMode = D3D12_FILL_MODE_SOLID;
		rasterizer.CullMode = D3D12_CULL_MODE_BACK;
		rasterizer.FrontCounterClockwise = FALSE;
		rasterizer.DepthBias = 0;
		rasterizer.DepthBiasClamp = 0.0f;
		rasterizer.SlopeScaledDepthBias = 0.0f;
		rasterizer.DepthClipEnable = TRUE;
		rasterizer.MultisampleEnable = FALSE;
		rasterizer.AntialiasedLineEnable = FALSE;
		rasterizer.ForcedSampleCount = 0;
		rasterizer.ConservativeRaster = D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF;

		D3D12_BLEND_DESC blend{};
		blend.AlphaToCoverageEnable = FALSE;
		blend.IndependentBlendEnable = FALSE;
		blend.RenderTarget[0].BlendEnable = FALSE;
		blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

		D3D12_DEPTH_STENCIL_DESC depth_stencil{};
		depth_stencil.DepthEnable = TRUE;
		depth_stencil.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ALL;
		depth_stencil.DepthFunc = D3D12_COMPARISON_FUNC_LESS;
		depth_stencil.StencilEnable = FALSE;

		D3D12_GRAPHICS_PIPELINE_STATE_DESC pso_desc{};
		pso_desc.pRootSignature = m_root_signature.Get();
		pso_desc.VS = { m_vs_blob->GetBufferPointer(), m_vs_blob->GetBufferSize() };
		pso_desc.PS = { m_ps_blob->GetBufferPointer(), m_ps_blob->GetBufferSize() };
		pso_desc.BlendState = blend;
		pso_desc.SampleMask = UINT_MAX;
		pso_desc.RasterizerState = rasterizer;
		pso_desc.DepthStencilState = depth_stencil;
		pso_desc.InputLayout = { input_layout, _countof(input_layout) };
		pso_desc.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
		pso_desc.NumRenderTargets = 1;
		pso_desc.RTVFormats[0] = rtv_format;
		pso_desc.DSVFormat = dsv_format;
		pso_desc.SampleDesc.Count = 1;

		if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_pipeline_state))))
		{
			return false;
		}

		rasterizer.FillMode = D3D12_FILL_MODE_WIREFRAME;
		rasterizer.CullMode = D3D12_CULL_MODE_NONE;
		pso_desc.RasterizerState = rasterizer;
		if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_wireframe_pso))))
		{
			return false;
		}

		// Double-sided PSO (no backface culling, for skybox/unlit materials)
		rasterizer.FillMode = D3D12_FILL_MODE_SOLID;
		rasterizer.CullMode = D3D12_CULL_MODE_NONE;
		pso_desc.RasterizerState = rasterizer;
		if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_double_sided_pso))))
		{
			return false;
		}

		// Gizmo PSO: no backface culling (already set above) + depth test/write DISABLED so editor transform gizmos
		// always render ON TOP of scene geometry and are never occluded. Same shaders/root sig/input layout, so it
		// stays binding-compatible with the standard PerFrame/PerObject/instance-VB setup.
		{
			D3D12_DEPTH_STENCIL_DESC gizmo_ds{};
			gizmo_ds.DepthEnable = FALSE;
			gizmo_ds.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO;
			gizmo_ds.DepthFunc = D3D12_COMPARISON_FUNC_ALWAYS;
			gizmo_ds.StencilEnable = FALSE;
			pso_desc.DepthStencilState = gizmo_ds;   // rasterizer already SOLID + CULL_NONE from double-sided above
			if (FAILED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_gizmo_pso))))
			{
				return false;
			}
		}

		return true;
	}
}
