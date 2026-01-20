#include "DX12Pipeline3D.h"
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
			static auto fn = reinterpret_cast<PFN_D3DCompile>(GetProcAddress(compiler, "D3DCompile"));
			return fn;
		}

		PFN_D3D12SerializeRootSignature get_serialize_root_signature()
		{
			static auto fn = reinterpret_cast<PFN_D3D12SerializeRootSignature>(
				GetProcAddress(LoadLibraryW(L"d3d12.dll"), "D3D12SerializeRootSignature"));
			return fn;
		}

		// Simple 3D Vertex Shader
		// Uses row_major matrices so mul(M,v) works correctly
		const char* g_vertex_shader_3d = R"(
cbuffer PerFrame : register(b0) {
    row_major float4x4 ViewProjection;
    float3 CameraPosition;
    float Padding0;
    float3 LightDirection;
    float Padding1;
    float3 LightColor;
    float AmbientStrength;
};

cbuffer PerObject : register(b1) {
    row_major float4x4 World;
    float4 BaseColor;
};

struct VS_IN {
    float3 pos : POSITION;
    float3 norm : NORMAL;
    float2 uv : TEXCOORD0;
};

struct PS_IN {
    float4 pos : SV_POSITION;
    float3 worldPos : TEXCOORD1;
    float3 norm : TEXCOORD2;
    float2 uv : TEXCOORD0;
};

PS_IN main(VS_IN input) {
    PS_IN output;
    float4 worldPos = mul(float4(input.pos, 1), World);
    output.worldPos = worldPos.xyz;
    output.pos = mul(worldPos, ViewProjection);
    output.norm = mul(input.norm, (float3x3)World);
    output.uv = input.uv;
    return output;
}
)";

		// Simple 3D Pixel Shader
		const char* g_pixel_shader_3d = R"(
cbuffer PerFrame : register(b0) {
    row_major float4x4 ViewProjection;
    float3 CameraPosition;
    float Padding0;
    float3 LightDirection;
    float Padding1;
    float3 LightColor;
    float AmbientStrength;
};

cbuffer PerObject : register(b1) {
    row_major float4x4 World;
    float4 BaseColor;
};

struct PS_IN {
    float4 pos : SV_POSITION;
    float3 worldPos : TEXCOORD1;
    float3 norm : TEXCOORD2;
    float2 uv : TEXCOORD0;
};

float4 main(PS_IN input) : SV_TARGET {
    float3 N = normalize(input.norm);
    float3 L = normalize(-LightDirection);
    float NdotL = max(dot(N, L), 0);
    float3 diffuse = NdotL * LightColor;
    float3 ambient = AmbientStrength * LightColor;
    float3 color = BaseColor.rgb * (ambient + diffuse);
    return float4(color, BaseColor.a);
}
)";
	}

	bool DX12Pipeline3D::initialize(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format)
	{
		if (!device) return false;
		if (!compile_shaders()) return false;
		if (!create_root_signature(device)) return false;
		if (!create_pso(device, rtv_format, dsv_format)) return false;
		return true;
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
		auto compile = get_d3d_compile();
		if (!compile) return false;

		UINT flags = D3DCOMPILE_OPTIMIZATION_LEVEL3;
#ifdef _DEBUG
		flags = D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION;
#endif

		ComPtr<ID3DBlob> error;

		HRESULT hr = compile(g_vertex_shader_3d, std::strlen(g_vertex_shader_3d), nullptr, nullptr, nullptr,
			"main", "vs_5_0", flags, 0, m_vs_blob.GetAddressOf(), error.GetAddressOf());

		if (FAILED(hr))
		{
			if (error) OutputDebugStringA(static_cast<const char*>(error->GetBufferPointer()));
			return false;
		}

		hr = compile(g_pixel_shader_3d, std::strlen(g_pixel_shader_3d), nullptr, nullptr, nullptr,
			"main", "ps_5_0", flags, 0, m_ps_blob.GetAddressOf(), error.GetAddressOf());

		if (FAILED(hr))
		{
			if (error) OutputDebugStringA(static_cast<const char*>(error->GetBufferPointer()));
			return false;
		}

		return true;
	}

	bool DX12Pipeline3D::create_root_signature(ID3D12Device* device)
	{
		auto serialize = get_serialize_root_signature();
		if (!serialize) return false;

		D3D12_ROOT_PARAMETER params[2] = {};
		
		params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[0].Descriptor.ShaderRegister = 0;
		params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

		params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[1].Descriptor.ShaderRegister = 1;
		params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

		D3D12_ROOT_SIGNATURE_DESC desc{};
		desc.NumParameters = 2;
		desc.pParameters = params;
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
			{ "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 24, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 }
		};

		D3D12_RASTERIZER_DESC rasterizer{};
		rasterizer.FillMode = D3D12_FILL_MODE_SOLID;
		rasterizer.CullMode = D3D12_CULL_MODE_BACK;
		rasterizer.FrontCounterClockwise = FALSE;
		rasterizer.DepthClipEnable = TRUE;

		D3D12_BLEND_DESC blend{};
		blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

		D3D12_DEPTH_STENCIL_DESC depth{};
		depth.DepthEnable = TRUE;
		depth.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ALL;
		depth.DepthFunc = D3D12_COMPARISON_FUNC_LESS;

		D3D12_GRAPHICS_PIPELINE_STATE_DESC pso{};
		pso.InputLayout = { input_layout, _countof(input_layout) };
		pso.pRootSignature = m_root_signature.Get();
		pso.VS = { m_vs_blob->GetBufferPointer(), m_vs_blob->GetBufferSize() };
		pso.PS = { m_ps_blob->GetBufferPointer(), m_ps_blob->GetBufferSize() };
		pso.RasterizerState = rasterizer;
		pso.BlendState = blend;
		pso.DepthStencilState = depth;
		pso.SampleMask = UINT_MAX;
		pso.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
		pso.NumRenderTargets = 1;
		pso.RTVFormats[0] = rtv_format;
		pso.DSVFormat = dsv_format;
		pso.SampleDesc.Count = 1;

		if (FAILED(device->CreateGraphicsPipelineState(&pso, IID_PPV_ARGS(&m_pipeline_state))))
			return false;

		rasterizer.FillMode = D3D12_FILL_MODE_WIREFRAME;
		rasterizer.CullMode = D3D12_CULL_MODE_NONE;
		pso.RasterizerState = rasterizer;

		return SUCCEEDED(device->CreateGraphicsPipelineState(&pso, IID_PPV_ARGS(&m_wireframe_pso)));
	}
}
