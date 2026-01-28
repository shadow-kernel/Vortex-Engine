#include "DX12SkyboxPipeline.h"
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

		// Skybox vertex shader - generates full-screen triangle
		const char* g_skybox_vs = R"(
cbuffer SkyboxConstants : register(b0) {
	row_major float4x4 InverseViewProjection;
	float3 CameraPosition;
	float Padding0;
	float3 SkyColor;
	float Padding1;
	float3 HorizonColor;
	float Padding2;
	float3 GroundColor;
	float Padding3;
	float3 SunDirection;
	float SunIntensity;
	float3 SunColor;
	float Padding4;
};

struct VS_OUT {
	float4 pos : SV_POSITION;
	float3 worldDir : TEXCOORD0;
};

VS_OUT main(uint vertexID : SV_VertexID) {
	VS_OUT output;
	
	// Generate full-screen triangle
	float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
	output.pos = float4(uv * 2.0 - 1.0, 1.0, 1.0);
	output.pos.y = -output.pos.y;
	
	// Transform to world direction
	float4 clipPos = float4(uv * 2.0 - 1.0, 1.0, 1.0);
	clipPos.y = -clipPos.y;
	float4 worldPos = mul(clipPos, InverseViewProjection);
	output.worldDir = worldPos.xyz / worldPos.w - CameraPosition;
	
	return output;
}
)";

		// Skybox pixel shader - procedural gradient sky with sun
		const char* g_skybox_ps = R"(
#define PI 3.14159265359

cbuffer SkyboxConstants : register(b0) {
	row_major float4x4 InverseViewProjection;
	float3 CameraPosition;
	float Padding0;
	float3 SkyColor;
	float Padding1;
	float3 HorizonColor;
	float Padding2;
	float3 GroundColor;
	float Padding3;
	float3 SunDirection;
	float SunIntensity;
	float3 SunColor;
	float Padding4;
};

struct PS_IN {
	float4 pos : SV_POSITION;
	float3 worldDir : TEXCOORD0;
};

float4 main(PS_IN input) : SV_TARGET {
	float3 dir = normalize(input.worldDir);
	
	// Vertical gradient
	float y = dir.y;
	
	// Sky to horizon to ground gradient
	float3 color;
	if (y > 0) {
		// Above horizon: sky to horizon
		float t = pow(y, 0.4); // Smooth falloff
		color = lerp(HorizonColor, SkyColor, t);
	} else {
		// Below horizon: horizon to ground
		float t = pow(-y, 0.7);
		color = lerp(HorizonColor, GroundColor, t);
	}
	
	// Add sun disc
	if (SunIntensity > 0.001) {
		float3 sunDir = normalize(-SunDirection);
		float sunDot = dot(dir, sunDir);
		
		// Sun disc
		float sunDisc = smoothstep(0.9995, 0.9999, sunDot);
		color += SunColor * sunDisc * SunIntensity * 10.0;
		
		// Sun glow
		float sunGlow = pow(max(sunDot, 0.0), 256.0);
		color += SunColor * sunGlow * SunIntensity * 0.5;
		
		// Atmospheric scattering near horizon when sun is visible
		float horizonGlow = pow(1.0 - abs(y), 4.0) * pow(max(sunDot, 0.0), 2.0);
		color += SunColor * horizonGlow * SunIntensity * 0.3;
	}
	
	// Subtle noise for texture
	float noise = frac(sin(dot(dir.xz, float2(12.9898, 78.233))) * 43758.5453);
	color += (noise - 0.5) * 0.01;
	
	return float4(color, 1.0);
}
)";
	}

	bool DX12SkyboxPipeline::initialize(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format)
	{
		if (!compile_shaders()) return false;
		if (!create_root_signature(device)) return false;
		if (!create_pso(device, rtv_format, dsv_format)) return false;

		// Set default colors (pleasant blue sky)
		m_constants.sky_color = { 0.4f, 0.6f, 0.9f };
		m_constants.horizon_color = { 0.7f, 0.8f, 0.95f };
		m_constants.ground_color = { 0.3f, 0.25f, 0.2f };
		m_constants.sun_direction = { -0.5f, -0.7f, 0.5f };
		m_constants.sun_color = { 1.0f, 0.95f, 0.8f };
		m_constants.sun_intensity = 1.0f;

		OutputDebugStringA("Skybox pipeline initialized\n");
		return true;
	}

	void DX12SkyboxPipeline::shutdown()
	{
		m_pipeline_state.Reset();
		m_root_signature.Reset();
		m_vs_blob.Reset();
		m_ps_blob.Reset();
	}

	void DX12SkyboxPipeline::set_colors(
		const DirectX::XMFLOAT3& sky_color,
		const DirectX::XMFLOAT3& horizon_color,
		const DirectX::XMFLOAT3& ground_color)
	{
		m_constants.sky_color = sky_color;
		m_constants.horizon_color = horizon_color;
		m_constants.ground_color = ground_color;
	}

	void DX12SkyboxPipeline::set_sun(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity)
	{
		m_constants.sun_direction = direction;
		m_constants.sun_color = color;
		m_constants.sun_intensity = intensity;
	}

	bool DX12SkyboxPipeline::compile_shaders()
	{
		auto d3dCompile = get_d3d_compile();
		if (!d3dCompile) return false;

		ComPtr<ID3DBlob> error;

		// Compile vertex shader
		if (FAILED(d3dCompile(g_skybox_vs, strlen(g_skybox_vs), nullptr, nullptr, nullptr,
			"main", "vs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &m_vs_blob, &error)))
		{
			if (error) OutputDebugStringA(static_cast<const char*>(error->GetBufferPointer()));
			return false;
		}

		// Compile pixel shader
		if (FAILED(d3dCompile(g_skybox_ps, strlen(g_skybox_ps), nullptr, nullptr, nullptr,
			"main", "ps_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &m_ps_blob, &error)))
		{
			if (error) OutputDebugStringA(static_cast<const char*>(error->GetBufferPointer()));
			return false;
		}

		return true;
	}

	bool DX12SkyboxPipeline::create_root_signature(ID3D12Device* device)
	{
		auto serialize = get_serialize_root_signature();
		if (!serialize) return false;

		// One CBV for skybox constants
		D3D12_ROOT_PARAMETER param{};
		param.ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		param.Descriptor.ShaderRegister = 0;
		param.Descriptor.RegisterSpace = 0;
		param.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

		D3D12_ROOT_SIGNATURE_DESC desc{};
		desc.NumParameters = 1;
		desc.pParameters = &param;
		desc.Flags = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;

		ComPtr<ID3DBlob> signature, error;
		if (FAILED(serialize(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error)))
		{
			if (error) OutputDebugStringA(static_cast<const char*>(error->GetBufferPointer()));
			return false;
		}

		return SUCCEEDED(device->CreateRootSignature(0, signature->GetBufferPointer(),
			signature->GetBufferSize(), IID_PPV_ARGS(&m_root_signature)));
	}

	bool DX12SkyboxPipeline::create_pso(ID3D12Device* device, DXGI_FORMAT rtv_format, DXGI_FORMAT dsv_format)
	{
		D3D12_RASTERIZER_DESC rasterizer{};
		rasterizer.FillMode = D3D12_FILL_MODE_SOLID;
		rasterizer.CullMode = D3D12_CULL_MODE_NONE;
		rasterizer.FrontCounterClockwise = FALSE;
		rasterizer.DepthClipEnable = TRUE;

		D3D12_BLEND_DESC blend{};
		blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

		D3D12_DEPTH_STENCIL_DESC depth_stencil{};
		depth_stencil.DepthEnable = TRUE;
		depth_stencil.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO; // Don't write depth
		depth_stencil.DepthFunc = D3D12_COMPARISON_FUNC_LESS_EQUAL; // Draw at far plane

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
		pso_desc.DSVFormat = dsv_format;
		pso_desc.SampleDesc.Count = 1;

		return SUCCEEDED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_pipeline_state)));
	}
}
