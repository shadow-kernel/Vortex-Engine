#include "DX12Volumetrics.h"
#include "DX12ShaderCompiler.h"
#include "../Resources/ResourceRegistry.h"
#include <algorithm>
#include <cstring>
#include <string>

namespace vortex::graphics::dx12
{
	namespace
	{
		void log(const std::string& s) { OutputDebugStringA(("[volumetrics] " + s + "\n").c_str()); }

		// byte-matched to VFrame in volumetrics.hlsl (and volumetrics.metal / volumetrics_common.glsl)
		struct VFrame
		{
			DirectX::XMFLOAT4X4 inv_view_projection;
			float eye[4];
			float screen[4];
			float depth_params[4];
			float fog[4], fog2[4];
			float vol[4];
			float sun_dir[4], sun_color[4];
			float params2[4];
			u32 counts[4];
			float params3[4];
			float pad[4];
		};
		static_assert(sizeof(VFrame) == 256, "VFrame must byte-match volumetrics.hlsl");

		enum : UINT { RP_FRAME = 0, RP_LIGHTS, RP_FOG, RP_DEPTH, RP_SPOT, RP_CSM, RP_POINT, RP_COUNT };

		using PFN_D3D12SerializeRootSignature = HRESULT(WINAPI*)(const D3D12_ROOT_SIGNATURE_DESC*, D3D_ROOT_SIGNATURE_VERSION, ID3DBlob**, ID3DBlob**);
		PFN_D3D12SerializeRootSignature get_serialize_root_signature()
		{
			static auto fn = reinterpret_cast<PFN_D3D12SerializeRootSignature>(
				GetProcAddress(LoadLibraryW(L"d3d12.dll"), "D3D12SerializeRootSignature"));
			return fn;
		}

		void barrier(ID3D12GraphicsCommandList* cmd, ID3D12Resource* res, D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to)
		{
			if (!res || from == to) return;
			D3D12_RESOURCE_BARRIER b{};
			b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
			b.Transition.pResource = res;
			b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
			b.Transition.StateBefore = from;
			b.Transition.StateAfter = to;
			cmd->ResourceBarrier(1, &b);
		}
	}

	bool DX12Volumetrics::initialize(ID3D12Device* device, DX12FrameRing* ring, DXGI_FORMAT scene_rtv_format, DXGI_FORMAT dsv_format)
	{
		shutdown();
		m_device = device;
		m_ring = ring;
		if (!device || !ring) return false;
		m_vs = DX12ShaderCompiler::load_shader("volumetrics", "vs", "VolFogVS", "vs_5_0");
		m_ps_march = DX12ShaderCompiler::load_shader("volumetrics", "ps", "VolFogPS", "ps_5_0");
		m_ps_composite = DX12ShaderCompiler::load_shader("volumetrics", "ps_composite", "VolCompositePS", "ps_5_0");
		if (!m_vs || !m_ps_march || !m_ps_composite) { log("volumetrics.hlsl did not compile — no volumetric fog on this backend"); shutdown(); return false; }
		if (!create_root_signature() || !create_pipelines(scene_rtv_format, dsv_format)) { shutdown(); return false; }
		m_ready = true;
		log("ready (half-res ray march, sun / point / spot in-scattering, shadowed)");
		return true;
	}

	void DX12Volumetrics::shutdown()
	{
		m_pso_march.Reset(); m_pso_composite.Reset();
		m_root_signature.Reset();
		m_vs.Reset(); m_ps_march.Reset(); m_ps_composite.Reset();
		m_target.Reset(); m_rtv_heap.Reset();
		m_w = m_h = 0;
		m_ready = false;
		m_device = nullptr;
		m_ring = nullptr;
	}

	bool DX12Volumetrics::create_root_signature()
	{
		auto serialize = get_serialize_root_signature();
		if (!serialize) return false;
		D3D12_DESCRIPTOR_RANGE ranges[5]{};
		const UINT regs[5] = { 0, 1, 7, 8, 9 };   // t0 fog, t1 depth, t7 spot atlas, t8 cascades, t9 point faces
		for (int i = 0; i < 5; ++i)
		{
			ranges[i].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
			ranges[i].NumDescriptors = 1;
			ranges[i].BaseShaderRegister = regs[i];
		}
		D3D12_ROOT_PARAMETER params[RP_COUNT]{};
		params[RP_FRAME].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[RP_FRAME].Descriptor.ShaderRegister = 0;
		params[RP_FRAME].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
		params[RP_LIGHTS].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[RP_LIGHTS].Descriptor.ShaderRegister = 2;
		params[RP_LIGHTS].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		for (int i = 0; i < 5; ++i)
		{
			params[RP_FOG + i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
			params[RP_FOG + i].DescriptorTable.NumDescriptorRanges = 1;
			params[RP_FOG + i].DescriptorTable.pDescriptorRanges = &ranges[i];
			params[RP_FOG + i].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		}
		D3D12_STATIC_SAMPLER_DESC samplers[2]{};
		samplers[0].Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;   // s0: the fog target (composite upsample)
		samplers[0].AddressU = samplers[0].AddressV = samplers[0].AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		samplers[0].MaxLOD = D3D12_FLOAT32_MAX;
		samplers[0].ShaderRegister = 0;
		samplers[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		samplers[1].Filter = D3D12_FILTER_COMPARISON_MIN_MAG_LINEAR_MIP_POINT;   // s1: the shadow atlases (as standard.hlsl)
		samplers[1].AddressU = samplers[1].AddressV = samplers[1].AddressW = D3D12_TEXTURE_ADDRESS_MODE_BORDER;
		samplers[1].ComparisonFunc = D3D12_COMPARISON_FUNC_LESS_EQUAL;
		samplers[1].BorderColor = D3D12_STATIC_BORDER_COLOR_OPAQUE_WHITE;
		samplers[1].MaxLOD = D3D12_FLOAT32_MAX;
		samplers[1].ShaderRegister = 1;
		samplers[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
		D3D12_ROOT_SIGNATURE_DESC desc{};
		desc.NumParameters = RP_COUNT;
		desc.pParameters = params;
		desc.NumStaticSamplers = 2;
		desc.pStaticSamplers = samplers;
		desc.Flags = D3D12_ROOT_SIGNATURE_FLAG_NONE;
		ComPtr<ID3DBlob> signature, error;
		if (FAILED(serialize(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error)))
		{
			if (error) log(std::string("root signature: ") + static_cast<const char*>(error->GetBufferPointer()));
			return false;
		}
		return SUCCEEDED(m_device->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(), IID_PPV_ARGS(&m_root_signature)));
	}

	bool DX12Volumetrics::create_pipelines(DXGI_FORMAT scene_rtv_format, DXGI_FORMAT dsv_format)
	{
		D3D12_GRAPHICS_PIPELINE_STATE_DESC d{};
		d.pRootSignature = m_root_signature.Get();
		d.VS = { m_vs->GetBufferPointer(), m_vs->GetBufferSize() };
		d.PS = { m_ps_march->GetBufferPointer(), m_ps_march->GetBufferSize() };
		d.SampleMask = UINT_MAX;
		d.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
		d.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
		d.RasterizerState.DepthClipEnable = TRUE;
		d.BlendState.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
		d.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
		d.NumRenderTargets = 1;
		d.RTVFormats[0] = DXGI_FORMAT_R16G16B16A16_FLOAT;
		d.SampleDesc.Count = 1;
		if (FAILED(m_device->CreateGraphicsPipelineState(&d, IID_PPV_ARGS(&m_pso_march)))) { log("march PSO failed"); return false; }

		d.PS = { m_ps_composite->GetBufferPointer(), m_ps_composite->GetBufferSize() };
		auto& rt = d.BlendState.RenderTarget[0];
		rt.BlendEnable = TRUE;                          // scene * T + S
		rt.SrcBlend = D3D12_BLEND_ONE;
		rt.DestBlend = D3D12_BLEND_SRC_ALPHA;
		rt.BlendOp = D3D12_BLEND_OP_ADD;
		rt.SrcBlendAlpha = D3D12_BLEND_ZERO;
		rt.DestBlendAlpha = D3D12_BLEND_ONE;
		rt.BlendOpAlpha = D3D12_BLEND_OP_ADD;
		rt.RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_RED | D3D12_COLOR_WRITE_ENABLE_GREEN | D3D12_COLOR_WRITE_ENABLE_BLUE;
		d.RTVFormats[0] = scene_rtv_format;
		d.DSVFormat = dsv_format;   // the scene's depth stays bound (untouched)
		if (FAILED(m_device->CreateGraphicsPipelineState(&d, IID_PPV_ARGS(&m_pso_composite)))) { log("composite PSO failed"); return false; }
		return true;
	}

	bool DX12Volumetrics::ensure_target(u32 w, u32 h)
	{
		if (m_target && m_w == w && m_h == h) return true;
		if (m_target) { m_ring->retire(m_target); m_target.Reset(); }
		if (!m_rtv_heap)
		{
			D3D12_DESCRIPTOR_HEAP_DESC hd{};
			hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
			hd.NumDescriptors = 1;
			if (FAILED(m_device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&m_rtv_heap)))) return false;
		}
		if (!m_srv_slot)
		{
			if (!ResourceRegistry::instance().reserve_srv_slot(m_srv_cpu, m_srv_gpu)) { log("no SRV slot for the fog target"); return false; }
			m_srv_slot = true;
		}
		D3D12_HEAP_PROPERTIES hp{};
		hp.Type = D3D12_HEAP_TYPE_DEFAULT;
		D3D12_RESOURCE_DESC rd{};
		rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
		rd.Width = w; rd.Height = h;
		rd.DepthOrArraySize = 1; rd.MipLevels = 1;
		rd.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
		rd.SampleDesc.Count = 1;
		rd.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
		rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
		D3D12_CLEAR_VALUE cv{};
		cv.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
		cv.Color[3] = 1.0f;
		if (FAILED(m_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_RENDER_TARGET, &cv, IID_PPV_ARGS(&m_target))))
		{
			log("fog target allocation failed");
			m_w = m_h = 0;
			return false;
		}
		m_target_state = D3D12_RESOURCE_STATE_RENDER_TARGET;
		D3D12_RENDER_TARGET_VIEW_DESC rv{};
		rv.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
		rv.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2D;
		m_device->CreateRenderTargetView(m_target.Get(), &rv, m_rtv_heap->GetCPUDescriptorHandleForHeapStart());
		D3D12_SHADER_RESOURCE_VIEW_DESC sd{};
		sd.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
		sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
		sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
		sd.Texture2D.MipLevels = 1;
		m_device->CreateShaderResourceView(m_target.Get(), &sd, m_srv_cpu);
		m_w = w; m_h = h;
		return true;
	}

	void DX12Volumetrics::draw(ID3D12GraphicsCommandList* cmd, D3D12_CPU_DESCRIPTOR_HANDLE rtv, D3D12_CPU_DESCRIPTOR_HANDLE dsv,
		D3D12_GPU_DESCRIPTOR_HANDLE depth_srv, u32 w, u32 h, const View& view, const DX12Particles::Environment& env,
		D3D12_GPU_VIRTUAL_ADDRESS lights_cb, D3D12_GPU_DESCRIPTOR_HANDLE spot_atlas, D3D12_GPU_DESCRIPTOR_HANDLE csm_atlas,
		D3D12_GPU_DESCRIPTOR_HANDLE point_atlas, float shadow_map_texel)
	{
		if (!active() || !cmd || !m_ring || depth_srv.ptr == 0 || w < 2 || h < 2 || lights_cb == 0) return;
		const u32 fw = (std::max)(1u, w / 2), fh = (std::max)(1u, h / 2);
		if (!ensure_target(fw, fh)) return;
		const bool shadows = m_params.shadows && spot_atlas.ptr != 0 && csm_atlas.ptr != 0 && point_atlas.ptr != 0;

		VFrame f{};
		f.inv_view_projection = view.inv_view_projection;
		f.eye[0] = view.eye.x; f.eye[1] = view.eye.y; f.eye[2] = view.eye.z;
		f.eye[3] = std::chrono::duration<float>(std::chrono::steady_clock::now() - m_origin).count();
		f.screen[0] = (float)fw; f.screen[1] = (float)fh; f.screen[2] = 1.0f / (float)fw; f.screen[3] = 1.0f / (float)fh;
		f.depth_params[0] = view.near_clip; f.depth_params[1] = view.far_clip; f.depth_params[2] = view.ortho ? 1.0f : 0.0f;
		f.fog[0] = env.fog_color.x; f.fog[1] = env.fog_color.y; f.fog[2] = env.fog_color.z; f.fog[3] = m_params.density;
		f.fog2[0] = env.fog_height_y; f.fog2[1] = env.fog_height_falloff; f.fog2[2] = m_params.noise_strength; f.fog2[3] = m_params.noise_scale;
		f.vol[0] = m_params.anisotropy; f.vol[1] = m_params.max_distance; f.vol[2] = (float)m_params.steps; f.vol[3] = m_params.noise_speed;
		f.sun_dir[0] = env.sun_direction.x; f.sun_dir[1] = env.sun_direction.y; f.sun_dir[2] = env.sun_direction.z; f.sun_dir[3] = env.sun_intensity;
		f.sun_color[0] = env.sun_color.x; f.sun_color[1] = env.sun_color.y; f.sun_color[2] = env.sun_color.z; f.sun_color[3] = env.ambient;
		f.params2[0] = m_params.lights; f.params2[1] = m_params.sun; f.params2[2] = shadows ? 1.0f : 0.0f; f.params2[3] = shadow_map_texel;
		f.counts[0] = env.point_lights; f.counts[1] = env.spot_lights;
		f.params3[0] = m_params.ambient;
		DX12FrameRing::Upload fcb = m_ring->alloc(sizeof(VFrame));
		if (!fcb.cpu) return;
		std::memcpy(fcb.cpu, &f, sizeof(f));

		auto& reg = ResourceRegistry::instance();
		cmd->SetGraphicsRootSignature(m_root_signature.Get());
		if (auto* heap = reg.srv_heap()) { ID3D12DescriptorHeap* heaps[] = { heap }; cmd->SetDescriptorHeaps(1, heaps); }
		cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		cmd->SetGraphicsRootConstantBufferView(RP_FRAME, fcb.gpu);
		cmd->SetGraphicsRootConstantBufferView(RP_LIGHTS, lights_cb);
		cmd->SetGraphicsRootDescriptorTable(RP_DEPTH, depth_srv);
		cmd->SetGraphicsRootDescriptorTable(RP_SPOT, shadows ? spot_atlas : depth_srv);
		cmd->SetGraphicsRootDescriptorTable(RP_CSM, shadows ? csm_atlas : depth_srv);
		cmd->SetGraphicsRootDescriptorTable(RP_POINT, shadows ? point_atlas : depth_srv);
		cmd->SetGraphicsRootDescriptorTable(RP_FOG, depth_srv);   // the march never reads the fog target itself

		// 1. ray march into the half-resolution target
		barrier(cmd, m_target.Get(), m_target_state, D3D12_RESOURCE_STATE_RENDER_TARGET);
		m_target_state = D3D12_RESOURCE_STATE_RENDER_TARGET;
		const D3D12_CPU_DESCRIPTOR_HANDLE frtv = m_rtv_heap->GetCPUDescriptorHandleForHeapStart();
		cmd->OMSetRenderTargets(1, &frtv, FALSE, nullptr);
		D3D12_VIEWPORT fvp{ 0.0f, 0.0f, (float)fw, (float)fh, 0.0f, 1.0f };
		D3D12_RECT fsc{ 0, 0, (LONG)fw, (LONG)fh };
		cmd->RSSetViewports(1, &fvp);
		cmd->RSSetScissorRects(1, &fsc);
		cmd->SetPipelineState(m_pso_march.Get());
		cmd->DrawInstanced(3, 1, 0, 0);

		// 2. composite over the scene: scene * T + S
		barrier(cmd, m_target.Get(), D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
		m_target_state = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
		cmd->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		D3D12_VIEWPORT vp{ 0.0f, 0.0f, (float)w, (float)h, 0.0f, 1.0f };
		D3D12_RECT sc{ 0, 0, (LONG)w, (LONG)h };
		cmd->RSSetViewports(1, &vp);
		cmd->RSSetScissorRects(1, &sc);
		cmd->SetGraphicsRootDescriptorTable(RP_FOG, m_srv_gpu);
		cmd->SetPipelineState(m_pso_composite.Get());
		cmd->DrawInstanced(3, 1, 0, 0);
	}
}
