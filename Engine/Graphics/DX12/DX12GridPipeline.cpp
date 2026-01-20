#include "DX12GridPipeline.h"
#include <d3dcompiler.h>
#include <cstring>

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
			return reinterpret_cast<PFN_D3DCompile>(GetProcAddress(compiler, "D3DCompile"));
		}

		PFN_D3D12SerializeRootSignature get_serialize_root_signature()
		{
			return reinterpret_cast<PFN_D3D12SerializeRootSignature>(
				GetProcAddress(LoadLibraryW(L"d3d12.dll"), "D3D12SerializeRootSignature"));
		}

		// Simple line grid vertex shader
		const char* g_grid_vs = R"(
cbuffer CB : register(b0) {
    row_major float4x4 ViewProjection;
    row_major float4x4 InvViewProjection;
    float3 CamPos;
    float Spacing;
    float Extent;
    float Major;
    float2 Pad;
};

struct VS_OUT {
    float4 pos : SV_POSITION;
    float3 worldPos : TEXCOORD0;
    float3 near : TEXCOORD1;
    float3 far : TEXCOORD2;
};

VS_OUT main(uint id : SV_VertexID) {
    VS_OUT o;
    float2 uv = float2((id << 1) & 2, id & 2);
    float2 ndc = uv * 2.0 - 1.0;
    
    o.pos = float4(ndc.x, -ndc.y, 0, 1);
    
    float4 nearPt = mul(float4(ndc.x, -ndc.y, 0, 1), InvViewProjection);
    float4 farPt = mul(float4(ndc.x, -ndc.y, 1, 1), InvViewProjection);
    
    o.near = nearPt.xyz / nearPt.w;
    o.far = farPt.xyz / farPt.w;
    o.worldPos = o.near;
    
    return o;
}
)";

		// Simple grid pixel shader
		const char* g_grid_ps = R"(
cbuffer CB : register(b0) {
    row_major float4x4 ViewProjection;
    row_major float4x4 InvViewProjection;
    float3 CamPos;
    float Spacing;
    float Extent;
    float Major;
    float2 Pad;
};

struct PS_IN {
    float4 pos : SV_POSITION;
    float3 worldPos : TEXCOORD0;
    float3 near : TEXCOORD1;
    float3 far : TEXCOORD2;
};

struct PS_OUT {
    float4 color : SV_TARGET;
    float depth : SV_DEPTH;
};

float Grid(float3 p, float s) {
    float2 c = p.xz / s;
    float2 d = fwidth(c);
    float2 g = abs(frac(c - 0.5) - 0.5) / d;
    return 1.0 - min(min(g.x, g.y), 1.0);
}

PS_OUT main(PS_IN i) {
    PS_OUT o;
    
    float3 dir = i.far - i.near;
    if (abs(dir.y) < 0.0001) discard;
    
    float t = -i.near.y / dir.y;
    if (t < 0) discard;
    
    float3 p = i.near + t * dir;
    float dist = length(p.xz - CamPos.xz);
    
    if (dist > Extent) discard;
    
    float fade = 1.0 - (dist / Extent);
    fade = fade * fade;
    
    float g1 = Grid(p, Spacing) * 0.3;
    float g2 = Grid(p, Spacing * Major) * 0.6;
    float g = g1 + g2;
    
    float3 col = float3(0.5, 0.5, 0.5);
    
    float axisW = Spacing * min(fwidth(p.x / Spacing), 1.0);
    if (abs(p.x) < axisW) col = float3(0.2, 0.4, 1.0);
    if (abs(p.z) < axisW) col = float3(1.0, 0.3, 0.3);
    
    float alpha = g * fade;
    if (alpha < 0.01) discard;
    
    float4 clip = mul(float4(p, 1), ViewProjection);
    o.depth = clip.z / clip.w;
    o.color = float4(col, alpha);
    
    return o;
}
)";
	}

	bool DX12GridPipeline::initialize(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format)
	{
		if (!device) return false;
		
		auto compile = get_d3d_compile();
		if (!compile) {
			OutputDebugStringA("DX12GridPipeline: No D3DCompile\n");
			return false;
		}

		UINT flags = 0;
#ifdef _DEBUG
		flags = D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION;
#endif

		ComPtr<ID3DBlob> err;
		
		if (FAILED(compile(g_grid_vs, strlen(g_grid_vs), "grid_vs", nullptr, nullptr, "main", "vs_5_0", flags, 0, &m_vs_blob, &err))) {
			if (err) OutputDebugStringA((char*)err->GetBufferPointer());
			return false;
		}
		
		if (FAILED(compile(g_grid_ps, strlen(g_grid_ps), "grid_ps", nullptr, nullptr, "main", "ps_5_0", flags, 0, &m_ps_blob, &err))) {
			if (err) OutputDebugStringA((char*)err->GetBufferPointer());
			return false;
		}

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
		pso.DepthStencilState.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ALL;
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
