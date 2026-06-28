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

		const char* g_vertex_shader_3d = R"(
cbuffer PerFrame : register(b0) {
	row_major float4x4 ViewProjection;
	float3 CameraPosition;
	float Padding0;
	float3 LightDirection;
	float DirectionalIntensity;
	float3 LightColor;
	float AmbientStrength;
	uint PointLightCount;
	uint SpotLightCount;
	uint2 FramePadding;
};

cbuffer PerObject : register(b1) {
	row_major float4x4 World;
	float4 BaseColor;
	float Metallic;
	float Roughness;
	float AO;
	float NormalStrength;
	uint HasAlbedoTexture;
	uint HasNormalTexture;
	uint HasMetallicTexture;
	uint HasRoughnessTexture;
	uint HasAOTexture;
	uint UseDirectXNormals;
	uint IsUnlit;
	float EmissiveStrength;
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
	float3 tangent : TEXCOORD3;
	float3 bitangent : TEXCOORD4;
};

PS_IN main(VS_IN input) {
	PS_IN output;
	float4 worldPos = mul(float4(input.pos, 1), World);
	output.worldPos = worldPos.xyz;
	output.pos = mul(worldPos, ViewProjection);
	output.norm = normalize(mul(input.norm, (float3x3)World));
	output.uv = input.uv;

	float3 N = output.norm;
	float3 T = normalize(cross(N, float3(0, 1, 0)));
	if (length(T) < 0.001) T = normalize(cross(N, float3(1, 0, 0)));
	float3 B = normalize(cross(N, T));
	output.tangent = T;
	output.bitangent = B;

	return output;
}
)";

		const char* g_pixel_shader_3d = R"(
#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8
#define PI 3.14159265359

cbuffer PerFrame : register(b0) {
	row_major float4x4 ViewProjection;
	float3 CameraPosition;
	float Padding0;
	float3 LightDirection;
	float DirectionalIntensity;
	float3 LightColor;
	float AmbientStrength;
	uint PointLightCount;
	uint SpotLightCount;
	uint2 FramePadding;
};

cbuffer PerObject : register(b1) {
	row_major float4x4 World;
	float4 BaseColor;
	float Metallic;
	float Roughness;
	float AO;
	float NormalStrength;
	uint HasAlbedoTexture;
	uint HasNormalTexture;
	uint HasMetallicTexture;
	uint HasRoughnessTexture;
	uint HasAOTexture;
	uint UseDirectXNormals;
	uint IsUnlit;
	float EmissiveStrength;
};

struct PointLight {
	float3 position;
	float range;
	float3 color;
	float intensity;
};

struct SpotLight {
	float3 position;
	float range;
	float3 direction;
	float spotAngle;
	float3 color;
	float intensity;
	float innerSpotAngle;
	float3 spotPadding;
};

cbuffer LightBuffer : register(b2) {
	PointLight PointLights[MAX_POINT_LIGHTS];
	SpotLight SpotLights[MAX_SPOT_LIGHTS];
};

Texture2D AlbedoTexture : register(t0);
Texture2D NormalTexture : register(t1);
Texture2D MetallicTexture : register(t2);
Texture2D RoughnessTexture : register(t3);
Texture2D AOTexture : register(t4);
SamplerState LinearSampler : register(s0);

struct PS_IN {
	float4 pos : SV_POSITION;
	float3 worldPos : TEXCOORD1;
	float3 norm : TEXCOORD2;
	float2 uv : TEXCOORD0;
	float3 tangent : TEXCOORD3;
	float3 bitangent : TEXCOORD4;
};

float D_GGX(float NdotH, float roughness) {
	float a = roughness * roughness;
	float a2 = a * a;
	float d = (NdotH * NdotH) * (a2 - 1.0) + 1.0;
	return a2 / (PI * d * d + 0.0001);
}

float G_SchlickGGX(float NdotV, float roughness) {
	float k = (roughness + 1.0);
	k = (k * k) / 8.0;
	return NdotV / (NdotV * (1.0 - k) + k + 0.0001);
}

float G_Smith(float NdotV, float NdotL, float roughness) {
	return G_SchlickGGX(NdotV, roughness) * G_SchlickGGX(NdotL, roughness);
}

float3 F_Schlick(float VdotH, float3 F0) {
	return F0 + (1.0 - F0) * pow(1.0 - VdotH, 5.0);
}

float3 SRGBToLinear(float3 color) {
	return pow(max(color, 0.0), 2.2);
}

float Attenuation(float distance, float range) {
	float d = distance / range;
	float atten = saturate(1.0 - d * d);
	return atten * atten / (distance * distance + 0.01);
}

float4 main(PS_IN input) : SV_TARGET {
	float3 albedo = BaseColor.rgb;
	float alpha = BaseColor.a;
	
	if (HasAlbedoTexture != 0) {
		float4 tex = AlbedoTexture.Sample(LinearSampler, input.uv);
		albedo = SRGBToLinear(tex.rgb);
		alpha = tex.a;
	}

	// UNLIT/EMISSIVE PATH - bypass all lighting calculations (for skybox, etc.)
	if (IsUnlit != 0) {
		float3 emissive = albedo * EmissiveStrength;
		// Apply simple tone mapping for HDR
		emissive = emissive / (emissive + 1.0);
		// Gamma correction
		emissive = pow(emissive, 1.0 / 2.2);
		return float4(emissive, alpha);
	}

	float metallic = Metallic;
	if (HasMetallicTexture != 0) {
		metallic = MetallicTexture.Sample(LinearSampler, input.uv).r;
	}

	float roughness = max(Roughness, 0.04);
	if (HasRoughnessTexture != 0) {
		roughness = max(RoughnessTexture.Sample(LinearSampler, input.uv).r, 0.04);
	}

	float ao = AO;
	if (HasAOTexture != 0) {
		ao = AOTexture.Sample(LinearSampler, input.uv).r;
	}

	float3 N = normalize(input.norm);
	if (HasNormalTexture != 0) {
		float3 normalMap = NormalTexture.Sample(LinearSampler, input.uv).rgb;
		normalMap = normalMap * 2.0 - 1.0;
		if (UseDirectXNormals == 0) normalMap.y = -normalMap.y;
		normalMap.xy *= NormalStrength;
		float3x3 TBN = float3x3(normalize(input.tangent), normalize(input.bitangent), N);
		N = normalize(mul(normalMap, TBN));
	}

	float3 V = normalize(CameraPosition - input.worldPos);
	float NdotV = max(dot(N, V), 0.001);

	float3 F0 = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);

	float3 Lo = float3(0, 0, 0);

	// DIRECTIONAL LIGHT
	if (DirectionalIntensity > 0.001) {
		float3 L = normalize(-LightDirection);
		float3 H = normalize(V + L);
		float NdotL = max(dot(N, L), 0.0);
		float NdotH = max(dot(N, H), 0.0);
		float VdotH = max(dot(V, H), 0.0);
		
		float D = D_GGX(NdotH, roughness);
		float G = G_Smith(NdotV, NdotL, roughness);
		float3 F = F_Schlick(VdotH, F0);
		
		float3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
		float3 kD = (1.0 - F) * (1.0 - metallic);
		
		float3 radiance = LightColor * DirectionalIntensity;
		Lo += (kD * albedo / PI + spec) * radiance * NdotL;
	}

	// POINT LIGHTS
	for (uint i = 0; i < PointLightCount && i < MAX_POINT_LIGHTS; ++i) {
		float3 lightVec = PointLights[i].position - input.worldPos;
		float dist = length(lightVec);
		
		if (dist < PointLights[i].range) {
			float3 L = lightVec / dist;
			float3 H = normalize(V + L);
			float NdotL = max(dot(N, L), 0.0);
			float NdotH = max(dot(N, H), 0.0);
			float VdotH = max(dot(V, H), 0.0);
			
			float atten = Attenuation(dist, PointLights[i].range);
			float3 radiance = PointLights[i].color * PointLights[i].intensity * atten;
			
			float D = D_GGX(NdotH, roughness);
			float G = G_Smith(NdotV, NdotL, roughness);
			float3 F = F_Schlick(VdotH, F0);
			
			float3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
			float3 kD = (1.0 - F) * (1.0 - metallic);
			
			Lo += (kD * albedo / PI + spec) * radiance * NdotL;
		}
	}

	// SPOT LIGHTS
	for (uint j = 0; j < SpotLightCount && j < MAX_SPOT_LIGHTS; ++j) {
		float3 lightVec = SpotLights[j].position - input.worldPos;
		float dist = length(lightVec);
		
		if (dist < SpotLights[j].range) {
			float3 L = lightVec / dist;
			float3 spotDir = normalize(SpotLights[j].direction);
			
			float theta = dot(-L, spotDir);
			float outerCos = cos(radians(SpotLights[j].spotAngle * 0.5));
			float innerCos = cos(radians(SpotLights[j].innerSpotAngle * 0.5));
			float spotFade = saturate((theta - outerCos) / (innerCos - outerCos + 0.001));
			
			if (theta > outerCos) {
				float3 H = normalize(V + L);
				float NdotL = max(dot(N, L), 0.0);
				float NdotH = max(dot(N, H), 0.0);
				float VdotH = max(dot(V, H), 0.0);
				
				float atten = Attenuation(dist, SpotLights[j].range) * spotFade;
				float3 radiance = SpotLights[j].color * SpotLights[j].intensity * atten;
				
				float D = D_GGX(NdotH, roughness);
				float G = G_Smith(NdotV, NdotL, roughness);
				float3 F = F_Schlick(VdotH, F0);
				
				float3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
				float3 kD = (1.0 - F) * (1.0 - metallic);
				
				Lo += (kD * albedo / PI + spec) * radiance * NdotL;
			}
		}
	}

	// AMBIENT - Reduced hemisphere lighting for realistic PBR
	float3 skyColor = float3(0.5, 0.55, 0.7);
	float3 groundColor = float3(0.15, 0.15, 0.18);
	float skyAmount = dot(N, float3(0, 1, 0)) * 0.5 + 0.5;
	float3 hemisphereLight = lerp(groundColor, skyColor, skyAmount);
	
	float3 ambient = hemisphereLight * AmbientStrength * albedo * ao * (1.0 - metallic);
	
	// Subtle rim for metals only
	float rimFresnel = pow(saturate(1.0 - NdotV), 5.0);
	float3 rimLight = rimFresnel * F0 * 0.1 * ao * metallic;
	
	// Environment reflection for metals
	float3 R = reflect(-V, N);
	float upFactor = R.y * 0.5 + 0.5;
	float3 envColor = lerp(float3(0.01, 0.01, 0.02), float3(0.08, 0.10, 0.15), upFactor);
	float envRoughness = roughness * roughness;
	envColor = lerp(envColor, envColor * 0.2, envRoughness);
	
	float3 envFresnel = F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0);
	float3 specularAmbient = envColor * envFresnel * ao;
	
	ambient += specularAmbient + rimLight;

	float3 color = ambient + Lo;

	// ACES Filmic Tone Mapping (RRT+ODT fit)
	float3 x = color * 0.5;
	float3 a = x * (x + 0.0245786) - 0.000090537;
	float3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
	color = saturate(a / b);

	// Gamma Correction (sRGB)
	color = pow(max(color, 0.0), 1.0 / 2.2);

	return float4(color, alpha);
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
			{ "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 24, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 }
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

		return true;
	}
}
