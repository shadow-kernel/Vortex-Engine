#include "DX12Pipeline.h"
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

		const char* g_vertex_shader = R"(
struct VSInput {
	float3 position : POSITION;
	float3 color : COLOR;
};
struct PSInput {
	float4 position : SV_POSITION;
	float3 color : COLOR;
};
PSInput main(VSInput input) {
	PSInput output;
	output.position = float4(input.position, 1.0f);
	output.color = input.color;
	return output;
})";

		const char* g_pixel_shader = R"(
struct PSInput {
	float4 position : SV_POSITION;
	float3 color : COLOR;
};
float4 main(PSInput input) : SV_TARGET {
	return float4(input.color, 1.0f);
})";
	}

	bool DX12Pipeline::initialize(ID3D12Device* device)
	{
		if (!device) return false;
		if (!compile_shaders()) return false;
		if (!create_root_signature(device)) return false;
		if (!create_pso(device)) return false;
		return true;
	}

	void DX12Pipeline::shutdown()
	{
		m_pipeline_state.Reset();
		m_root_signature.Reset();
		m_vs_blob.Reset();
		m_ps_blob.Reset();
	}

	bool DX12Pipeline::compile_shaders()
	{
		auto compile = get_d3d_compile();
		if (!compile) return false;

		UINT flags = D3DCOMPILE_OPTIMIZATION_LEVEL3;
#ifdef _DEBUG
		flags = D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION;
#endif

		ComPtr<ID3DBlob> error;
		if (FAILED(compile(g_vertex_shader, std::strlen(g_vertex_shader), nullptr, nullptr, nullptr,
						   "main", "vs_5_0", flags, 0, m_vs_blob.GetAddressOf(), error.GetAddressOf())))
		{
			return false;
		}

		if (FAILED(compile(g_pixel_shader, std::strlen(g_pixel_shader), nullptr, nullptr, nullptr,
						   "main", "ps_5_0", flags, 0, m_ps_blob.GetAddressOf(), error.GetAddressOf())))
		{
			return false;
		}

		return true;
	}

	bool DX12Pipeline::create_root_signature(ID3D12Device* device)
	{
		auto serialize = get_serialize_root_signature();
		if (!serialize) return false;

		D3D12_ROOT_SIGNATURE_DESC desc{};
		desc.Flags = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;

		ComPtr<ID3DBlob> signature;
		ComPtr<ID3DBlob> error;

		if (FAILED(serialize(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error)))
			return false;

		return SUCCEEDED(device->CreateRootSignature(0, signature->GetBufferPointer(),
			signature->GetBufferSize(), IID_PPV_ARGS(&m_root_signature)));
	}

	bool DX12Pipeline::create_pso(ID3D12Device* device)
	{
		D3D12_INPUT_ELEMENT_DESC input_layout[] = {
			{ "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
			{ "COLOR", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 }
		};

		D3D12_RASTERIZER_DESC rasterizer{};
		rasterizer.FillMode = D3D12_FILL_MODE_SOLID;
		rasterizer.CullMode = D3D12_CULL_MODE_NONE;
		rasterizer.FrontCounterClockwise = FALSE;
		rasterizer.DepthClipEnable = TRUE;

		D3D12_BLEND_DESC blend{};
		blend.RenderTarget[0].BlendEnable = FALSE;
		blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

		D3D12_GRAPHICS_PIPELINE_STATE_DESC pso_desc{};
		pso_desc.InputLayout = { input_layout, _countof(input_layout) };
		pso_desc.pRootSignature = m_root_signature.Get();
		pso_desc.VS = { m_vs_blob->GetBufferPointer(), m_vs_blob->GetBufferSize() };
		pso_desc.PS = { m_ps_blob->GetBufferPointer(), m_ps_blob->GetBufferSize() };
		pso_desc.RasterizerState = rasterizer;
		pso_desc.BlendState = blend;
		pso_desc.DepthStencilState.DepthEnable = FALSE;
		pso_desc.DepthStencilState.StencilEnable = FALSE;
		pso_desc.SampleMask = UINT_MAX;
		pso_desc.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
		pso_desc.NumRenderTargets = 1;
		pso_desc.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM;
		pso_desc.SampleDesc.Count = 1;

		return SUCCEEDED(device->CreateGraphicsPipelineState(&pso_desc, IID_PPV_ARGS(&m_pipeline_state)));
	}
}
