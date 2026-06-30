#include "DX12MotionVectorPipeline.h"
#include <d3dcompiler.h>

namespace vortex::graphics::dx12
{
	namespace
	{
		using PFN_D3DCompile = HRESULT(WINAPI*)(LPCVOID, SIZE_T, LPCSTR, const D3D_SHADER_MACRO*, ID3DInclude*, LPCSTR, LPCSTR, UINT, UINT, ID3DBlob**, ID3DBlob**);
		using PFN_D3D12SerializeRootSignature = HRESULT(WINAPI*)(const D3D12_ROOT_SIGNATURE_DESC*, D3D_ROOT_SIGNATURE_VERSION, ID3DBlob**, ID3DBlob**);

		PFN_D3DCompile get_d3d_compile()
		{
			static HMODULE compiler = LoadLibraryW(L"d3dcompiler_47.dll");
			if (!compiler) compiler = LoadLibraryW(L"d3dcompiler_43.dll");
			if (!compiler) return nullptr;
			static auto fn = reinterpret_cast<PFN_D3DCompile>(GetProcAddress(compiler, "D3DCompile"));
			return fn;
		}
		PFN_D3D12SerializeRootSignature get_serialize_root_signature()
		{
			static auto fn = reinterpret_cast<PFN_D3D12SerializeRootSignature>(
				GetProcAddress(LoadLibraryW(L"d3d12.dll"), "D3D12SerializeRootSignature"));
			return fn;
		}

		// Fullscreen triangle from SV_VertexID (same as the upscale pass).
		const char* g_mvec_vs = R"(
struct VS_OUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VS_OUT main(uint vertexID : SV_VertexID) {
	VS_OUT o;
	float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
	o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
	o.pos.y = -o.pos.y;
	o.uv = uv;
	return o;
}
)";

		// Reproject the current pixel through inverse(curVP) -> world -> prevVP to its previous screen UV, and
		// output the pixel-space velocity (prev - cur). Engine convention: row-major matrices, mul(vec,mat),
		// clip.y = -ndc.y. DLSS gets this with mvecScale = {1/renderW, 1/renderH} (pixel-space -> NDC).
		const char* g_mvec_ps = R"(
Texture2D<float> Depth : register(t0);
SamplerState Smp : register(s0);
cbuffer C : register(b0) {
	row_major float4x4 InvViewProj;
	row_major float4x4 PrevViewProj;
	float2 Dims;
	float2 Pad;
};
struct PS_IN { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
float2 main(PS_IN i) : SV_TARGET {
	float d = Depth.SampleLevel(Smp, i.uv, 0);
	float2 ndc = i.uv * 2.0 - 1.0;
	float4 clip = float4(ndc.x, -ndc.y, d, 1.0);
	float4 world = mul(clip, InvViewProj);
	world /= world.w;
	float4 pc = mul(world, PrevViewProj);
	pc /= pc.w;
	float2 prevUV = float2(pc.x * 0.5 + 0.5, 0.5 - pc.y * 0.5);
	return (prevUV - i.uv) * Dims;   // pixel-space motion vector (current -> previous)
}
)";
	}

	bool DX12MotionVectorPipeline::initialize(ID3D12Device* device, DXGI_FORMAT rtv_format)
	{
		if (!compile_shaders()) return false;
		if (!create_root_signature(device)) return false;
		if (!create_pso(device, rtv_format)) return false;
		OutputDebugStringA("Motion-vector pipeline initialized\n");
		return true;
	}

	void DX12MotionVectorPipeline::shutdown()
	{
		m_pipeline_state.Reset();
		m_root_signature.Reset();
		m_vs_blob.Reset();
		m_ps_blob.Reset();
	}

	bool DX12MotionVectorPipeline::compile_shaders()
	{
		auto d3dCompile = get_d3d_compile();
		if (!d3dCompile) return false;
		ComPtr<ID3DBlob> error;
		if (FAILED(d3dCompile(g_mvec_vs, strlen(g_mvec_vs), nullptr, nullptr, nullptr,
			"main", "vs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &m_vs_blob, &error)))
		{
			if (error) OutputDebugStringA(static_cast<const char*>(error->GetBufferPointer()));
			return false;
		}
		if (FAILED(d3dCompile(g_mvec_ps, strlen(g_mvec_ps), nullptr, nullptr, nullptr,
			"main", "ps_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &m_ps_blob, &error)))
		{
			if (error) OutputDebugStringA(static_cast<const char*>(error->GetBufferPointer()));
			return false;
		}
		return true;
	}

	bool DX12MotionVectorPipeline::create_root_signature(ID3D12Device* device)
	{
		auto serialize = get_serialize_root_signature();
		if (!serialize) return false;

		// param0: SRV table (t0 = depth). param1: 34 root constants (b0 = 2 matrices + dims + pad).
		D3D12_DESCRIPTOR_RANGE srv_range{};
		srv_range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
		srv_range.NumDescriptors = 1;
		srv_range.BaseShaderRegister = 0;
		srv_range.RegisterSpace = 0;
		srv_range.OffsetInDescriptorsFromTableStart = 0;

		D3D12_ROOT_PARAMETER params[2]{};
		params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		params[0].DescriptorTable.NumDescriptorRanges = 1;
		params[0].DescriptorTable.pDescriptorRanges = &srv_range;
		params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
		params[1].Constants.ShaderRegister = 0;
		params[1].Constants.RegisterSpace = 0;
		params[1].Constants.Num32BitValues = sizeof(Constants) / 4; // 34
		params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		// Point + clamp sampler (depth must not be filtered).
		D3D12_STATIC_SAMPLER_DESC sampler{};
		sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;
		sampler.AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		sampler.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_NEVER;
		sampler.BorderColor = D3D12_STATIC_BORDER_COLOR_OPAQUE_BLACK;
		sampler.MaxLOD = D3D12_FLOAT32_MAX;
		sampler.ShaderRegister = 0;
		sampler.RegisterSpace = 0;
		sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		D3D12_ROOT_SIGNATURE_DESC desc{};
		desc.NumParameters = 2;
		desc.pParameters = params;
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

	bool DX12MotionVectorPipeline::create_pso(ID3D12Device* device, DXGI_FORMAT rtv_format)
	{
		D3D12_RASTERIZER_DESC rasterizer{};
		rasterizer.FillMode = D3D12_FILL_MODE_SOLID;
		rasterizer.CullMode = D3D12_CULL_MODE_NONE;
		rasterizer.DepthClipEnable = TRUE;

		D3D12_BLEND_DESC blend{};
		blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

		D3D12_DEPTH_STENCIL_DESC depth_stencil{};
		depth_stencil.DepthEnable = FALSE;
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
		pso_desc.RTVFormats[0] = rtv_format;   // RG16F
		pso_desc.DSVFormat = DXGI_FORMAT_UNKNOWN;
		pso_desc.SampleDesc.Count = 1;

		return SUCCEEDED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_pipeline_state)));
	}
}
