#include "DX12PostFxChain.h"
#include "DX12ShaderCompiler.h"

namespace vortex::graphics::dx12
{
	namespace
	{
		// d3d12.dll is loaded dynamically engine-wide (DX12Core) — resolve the serializer the same way
		// every other pipeline does instead of linking d3d12.lib.
		using PFN_D3D12SerializeRootSignature = HRESULT(WINAPI*)(const D3D12_ROOT_SIGNATURE_DESC*, D3D_ROOT_SIGNATURE_VERSION, ID3DBlob**, ID3DBlob**);
		PFN_D3D12SerializeRootSignature get_serialize_root_signature()
		{
			static auto fn = reinterpret_cast<PFN_D3D12SerializeRootSignature>(
				GetProcAddress(LoadLibraryW(L"d3d12.dll"), "D3D12SerializeRootSignature"));
			return fn;
		}
	}

	bool DX12PostFxChain::initialize(ID3D12Device* device, DXGI_FORMAT rtv_format, DX12CommandQueue* queue)
	{
		if (!device) return false;
		m_format = rtv_format;
		m_queue = queue;
		m_device = device;

		auto vs = DX12ShaderCompiler::load_shader("postfx", "vs", "VSMain", "vs_5_0");
		auto ps = DX12ShaderCompiler::load_shader("postfx", "ps", "PSMain", "ps_5_0");
		if (!vs || !ps) return false;

		// Root signature: t0 = source color (SRV table), b0 = the pass's parameter CB (root CBV),
		// s0 = static linear-clamp sampler — the upscale pass's layout plus one CBV.
		D3D12_DESCRIPTOR_RANGE srv_range{};
		srv_range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
		srv_range.NumDescriptors = 1;
		srv_range.BaseShaderRegister = 0;

		D3D12_ROOT_PARAMETER params[2]{};
		params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		params[0].DescriptorTable.NumDescriptorRanges = 1;
		params[0].DescriptorTable.pDescriptorRanges = &srv_range;
		params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[1].Descriptor.ShaderRegister = 0;
		params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		D3D12_STATIC_SAMPLER_DESC sampler{};
		sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
		sampler.AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		sampler.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

		D3D12_ROOT_SIGNATURE_DESC rs{};
		rs.NumParameters = 2;
		rs.pParameters = params;
		rs.NumStaticSamplers = 1;
		rs.pStaticSamplers = &sampler;

		auto serialize = get_serialize_root_signature();
		if (!serialize) return false;
		ComPtr<ID3DBlob> blob, err;
		if (FAILED(serialize(&rs, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &err))) return false;
		if (FAILED(device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(),
			IID_PPV_ARGS(&m_root_signature)))) return false;

		D3D12_RASTERIZER_DESC rast{};
		rast.FillMode = D3D12_FILL_MODE_SOLID;
		rast.CullMode = D3D12_CULL_MODE_NONE;
		rast.DepthClipEnable = TRUE;

		D3D12_BLEND_DESC blend{};
		blend.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

		D3D12_GRAPHICS_PIPELINE_STATE_DESC pso{};
		pso.pRootSignature = m_root_signature.Get();
		pso.VS = { vs->GetBufferPointer(), vs->GetBufferSize() };
		pso.PS = { ps->GetBufferPointer(), ps->GetBufferSize() };
		pso.BlendState = blend;
		pso.SampleMask = UINT_MAX;
		pso.RasterizerState = rast;
		pso.DepthStencilState.DepthEnable = FALSE;      // fullscreen pass — no depth
		pso.DepthStencilState.StencilEnable = FALSE;
		pso.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
		pso.NumRenderTargets = 1;
		pso.RTVFormats[0] = rtv_format;
		pso.DSVFormat = DXGI_FORMAT_UNKNOWN;
		pso.SampleDesc.Count = 1;
		if (FAILED(device->CreateGraphicsPipelineState(&pso, IID_PPV_ARGS(&m_pso)))) return false;

		// One 256B CB slot per pass, persistently mapped — written once per record() like the light CB.
		D3D12_HEAP_PROPERTIES up{}; up.Type = D3D12_HEAP_TYPE_UPLOAD;
		D3D12_RESOURCE_DESC bd{};
		bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		bd.Width = (UINT64)256 * MAX_PASSES;
		bd.Height = 1; bd.DepthOrArraySize = 1; bd.MipLevels = 1;
		bd.SampleDesc.Count = 1;
		bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
		if (FAILED(device->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &bd,
			D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_cb)))) return false;
		D3D12_RANGE none{ 0, 0 };
		if (FAILED(m_cb->Map(0, &none, &m_cb_mapped))) return false;

		// VORTEX_POSTFX_TEST=1: boot with vignette AND the invert pass on — the headless verification
		// switch for the chain's multi-pass ping-pong (#28 AC). Two passes force input -> ping B ->
		// back buffer; a frame that is inverted AND bright-edged proves both ran in order.
		char test[8];
		DWORD tn = GetEnvironmentVariableA("VORTEX_POSTFX_TEST", test, sizeof(test));
		if (tn > 0 && tn < sizeof(test) && test[0] == '1')
		{
			m_params.vignette = true;
			m_params.debug_invert = true;
		}
		// VORTEX_GRADE_TEST=1: force a strong cold, desaturated grade (engine-path isolation for #31).
		DWORD gn = GetEnvironmentVariableA("VORTEX_GRADE_TEST", test, sizeof(test));
		if (gn > 0 && gn < sizeof(test) && test[0] == '1')
		{
			m_main_view = true;
			m_params.grade = true;
			m_params.exposure = -0.3f; m_params.contrast = 1.35f;
			m_params.saturation = 0.15f; m_params.temperature = -0.85f; m_params.tint = 0.0f;
		}

		return true;
	}


	void DX12PostFxChain::shutdown()
	{
		for (int s = 0; s < 2; ++s)
			for (int p = 0; p < 2; ++p)
				m_rt[s][p].shutdown();
		if (m_cb && m_cb_mapped) { m_cb->Unmap(0, nullptr); m_cb_mapped = nullptr; }
		m_cb.Reset();
		m_pso.Reset();
		m_root_signature.Reset();
		m_queue = nullptr;
		m_device = nullptr;
	}


	bool DX12PostFxChain::ensure_rt(ID3D12Device* device, int slot, int ping, u32 w, u32 h)
	{
		DX12RenderTarget& rt = m_rt[slot][ping];
		if (rt.is_initialized() && rt.width() == w && rt.height() == h) return true;
		// Size change / first use: the old RT may be referenced by an in-flight frame — idle the GPU
		// first (rare: window/scale changes only, same one-off stall as ensure_scaled_rt).
		if (rt.is_initialized() && m_queue) m_queue->flush();
		if (!rt.is_initialized())
			return rt.initialize(device, w, h, m_format);   // color-only default depth is unused
		return rt.resize(device, w, h);
	}


	DX12RenderTarget* DX12PostFxChain::acquire_input(ID3D12Device* device, u32 w, u32 h, int slot)
	{
		if (!m_pso || !device || w == 0 || h == 0 || slot < 0 || slot > 1) return nullptr;
		if (!ensure_rt(device, slot, 0, w, h)) return nullptr;
		return &m_rt[slot][0];
	}


	void DX12PostFxChain::record(ID3D12GraphicsCommandList* cmd, int slot, D3D12_CPU_DESCRIPTOR_HANDLE out_rtv,
		u32 w, u32 h, float time_seconds)
	{
		if (!m_pso || !cmd || slot < 0 || slot > 1 || !m_rt[slot][0].is_initialized()) return;

		// The enabled pass list, in chain order. Each entry is a CB slot; all share the uber-PSO.
		u32 passes[MAX_PASSES]; u32 n = 0;
		if (m_params.vignette || m_params.grain || m_params.ca || m_params.grade) passes[n++] = 0;
		if (m_params.debug_invert) passes[n++] = 1;
		if (n == 0) return;   // renderer gates on active(), but stay safe against a mid-frame toggle

		if (m_cb_mapped)
		{
			PassCB cb{};
			cb.texel[0] = 1.0f / (float)w;
			cb.texel[1] = 1.0f / (float)h;
			cb.time = time_seconds;
			cb.flags = (m_params.vignette ? 1u : 0u) | (m_params.grain ? 2u : 0u) | (m_params.ca ? 4u : 0u)
				| (m_params.grade ? 16u : 0u);
			cb.vignette[0] = m_params.vig_intensity;
			cb.vignette[1] = m_params.vig_smoothness;
			cb.vignette[2] = m_params.vig_roundness;
			cb.vignette_color[0] = m_params.vig_r;
			cb.vignette_color[1] = m_params.vig_g;
			cb.vignette_color[2] = m_params.vig_b;
			cb.grain_ca[0] = m_params.grain_intensity;
			cb.grain_ca[1] = m_params.grain_size;
			cb.grain_ca[2] = m_params.ca_strength;
			cb.grain_ca[3] = m_params.ca_falloff;
			cb.grade1[0] = m_params.exposure;
			cb.grade1[1] = m_params.contrast;
			cb.grade1[2] = m_params.saturation;
			cb.grade1[3] = m_params.temperature;
			cb.grade2[0] = m_params.tint;
			memcpy(m_cb_mapped, &cb, sizeof(cb));

			PassCB tb{};
			tb.texel[0] = cb.texel[0]; tb.texel[1] = cb.texel[1];
			tb.time = time_seconds;
			tb.flags = 8u;   // invert — the chain-verification pass
			memcpy((u8*)m_cb_mapped + 256, &tb, sizeof(tb));
		}

		cmd->SetPipelineState(m_pso.Get());
		cmd->SetGraphicsRootSignature(m_root_signature.Get());
		cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

		D3D12_VIEWPORT vp{}; vp.Width = (float)w; vp.Height = (float)h; vp.MaxDepth = 1.0f;
		D3D12_RECT sc{}; sc.right = (LONG)w; sc.bottom = (LONG)h;
		cmd->RSSetViewports(1, &vp);
		cmd->RSSetScissorRects(1, &sc);

		DX12RenderTarget* src = &m_rt[slot][0];
		for (u32 k = 0; k < n; ++k)
		{
			bool last = (k == n - 1);
			DX12RenderTarget* dst = nullptr;
			D3D12_CPU_DESCRIPTOR_HANDLE dst_rtv = out_rtv;
			if (!last)
			{
				// Intermediate pass -> the other ping RT (created on first 2-pass frame).
				if (!m_device || !ensure_rt(m_device, slot, 1, w, h)) last = true;   // fall back: write out now
				else
				{
					dst = &m_rt[slot][1];
					dst->transition_to_render_target(cmd);
					dst_rtv = dst->rtv();
				}
			}

			src->transition_to_shader_resource(cmd);
			cmd->OMSetRenderTargets(1, &dst_rtv, FALSE, nullptr);
			ID3D12DescriptorHeap* heaps[] = { src->srv_heap() };
			cmd->SetDescriptorHeaps(1, heaps);
			cmd->SetGraphicsRootDescriptorTable(0, src->srv_gpu());
			cmd->SetGraphicsRootConstantBufferView(1, m_cb->GetGPUVirtualAddress() + (UINT64)passes[k] * 256);
			cmd->DrawInstanced(3, 1, 0, 0);
			src->transition_to_render_target(cmd);   // leave every chain RT ready for the next frame

			if (last) break;
			src = dst;
		}
	}
}
