#include "DX12Renderer_Internal.h"
#include <algorithm>
#include <unordered_map>

// Spot-light shadow ATLAS (#23 — "the flashlight", and every other authored spot).
//
// Up to MAX_SHADOW_SPOTS (4) shadow-casting spots per frame share ONE depth atlas (2x2 tiles of
// SHADOW_TILE_SIZE²). Each shadow spot renders a depth-only sub-pass into its tile viewport; the
// pixel shader picks the tile via the per-spot shadow_slot packed into GPUSpotLight and the per-tile
// view-projections in the light buffer's ShadowVP[4] tail (b2 @1024 — the buffer was already 1280
// bytes wide, the tail fits exactly).
//
//  prepare_shadow_pass()  CPU side, inside update_per_frame_constants BEFORE the CB uploads:
//                         collects the first 4 submitted spots with cast_shadows (submit order =
//                         scene order — deterministic and user-controllable), builds their VPs and
//                         primes the per-tile b0 clones for the depth passes.
//  render_shadow_pass()   GPU side, right after the command-list reset: one atlas clear, then per
//                         shadow spot a cone-culled caster pack + instanced depth-only draws into
//                         its tile. The atlas then transitions to PSR for every scene pass (t7).
//
// State machine: the atlas has its own DEPTH_WRITE <-> PIXEL_SHADER_RESOURCE round-trip per frame,
// deliberately separate from m_scaled_rt's depth (which Frame Generation leaves in PSR across frames).

namespace vortex::graphics::dx12
{
	namespace
	{
		// Local copies of the frustum helpers — the originals live in DX12Renderer_3DScene.cpp's
		// anonymous namespace (same Gribb/Hartmann extraction for the row-major, mul(vec,mat) VP).
		struct ShadowFrustum { float p[6][4]; };

		ShadowFrustum extract_shadow_frustum(const DirectX::XMFLOAT4X4& m)
		{
			ShadowFrustum f = {
				{
					{ m._14 + m._11, m._24 + m._21, m._34 + m._31, m._44 + m._41 }, // left
					{ m._14 - m._11, m._24 - m._21, m._34 - m._31, m._44 - m._41 }, // right
					{ m._14 + m._12, m._24 + m._22, m._34 + m._32, m._44 + m._42 }, // bottom
					{ m._14 - m._12, m._24 - m._22, m._34 - m._32, m._44 - m._42 }, // top
					{ m._13,         m._23,         m._33,         m._43         }, // near
					{ m._14 - m._13, m._24 - m._23, m._34 - m._33, m._44 - m._43 }, // far
				}
			};
			for (int i = 0; i < 6; ++i)
			{
				float a = f.p[i][0], b = f.p[i][1], c = f.p[i][2];
				float len = sqrtf(a * a + b * b + c * c);
				if (len > 1e-6f) { f.p[i][0] /= len; f.p[i][1] /= len; f.p[i][2] /= len; f.p[i][3] /= len; }
			}
			return f;
		}

		bool sphere_in_shadow_frustum(const ShadowFrustum& f, float cx, float cy, float cz, float r)
		{
			for (int i = 0; i < 6; ++i)
			{
				float dist = f.p[i][0] * cx + f.p[i][1] * cy + f.p[i][2] * cz + f.p[i][3];
				if (dist < -r) return false;
			}
			return true;
		}

		// Spot light view-projection: FOV = the FULL outer cone angle (SpotAngle travels end-to-end in
		// degrees; the shader gates at cos(angle/2), so the full angle IS the perspective FOV). Far
		// plane = range (attenuation + the dist<range gate zero the light beyond it anyway).
		bool build_spot_vp(const DX12Renderer::SpotLightData& s, DirectX::XMFLOAT4X4& out)
		{
			using namespace DirectX;
			XMVECTOR dir = XMVector3Normalize(XMLoadFloat3(&s.direction));
			if (XMVectorGetX(XMVector3LengthSq(dir)) < 0.5f) return false;   // zero direction
			XMVECTOR pos = XMLoadFloat3(&s.position);
			XMVECTOR up = fabsf(XMVectorGetY(dir)) > 0.99f ? XMVectorSet(0, 0, 1, 0) : XMVectorSet(0, 1, 0, 0);
			float fovY = XMConvertToRadians(s.spot_angle < 1.0f ? 1.0f : (s.spot_angle > 175.0f ? 175.0f : s.spot_angle));
			float farZ = s.range > 0.1f ? s.range : 0.1f;
			XMMATRIX view = XMMatrixLookToLH(pos, dir, up);
			XMMATRIX proj = XMMatrixPerspectiveFovLH(fovY, 1.0f, 0.05f, farZ);
			XMStoreFloat4x4(&out, view * proj);   // same row-major/no-transpose treatment as view_projection
			return true;
		}
	}


	bool DX12Renderer::ensure_shadow_map(u32 size)
	{
		if (size < 512) size = 512; else if (size > 16384) size = 16384;
		if (m_shadow_map && m_shadow_map_size == size) return true;

		auto* dev = DX12Core::instance().device();
		if (!dev) return false;

		// The map may be referenced by an in-flight frame — idle the GPU before recreating.
		if (m_shadow_map) m_command_queue.flush();

		// One-time: reserve the SRV slot in ResourceRegistry's SHARED shader-visible heap. The scene pass
		// binds that heap, and only one CBV_SRV_UAV heap can be bound — a private per-resource heap could
		// never be sampled mid-scene-pass. The slot survives recreation (view recreated in place below).
		if (m_shadow_srv_cpu.ptr == 0)
		{
			if (!ResourceRegistry::instance().reserve_srv_slot(m_shadow_srv_cpu, m_shadow_srv_gpu))
				return false;
		}

		// R32_TYPELESS so the same resource carries a D32_FLOAT DSV (render) and an R32_FLOAT SRV (sample)
		// — the same sampleable-depth recipe DX12RenderTarget uses for the DLSS depth input.
		D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_DEFAULT;
		D3D12_RESOURCE_DESC rd{};
		rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
		rd.Width = size; rd.Height = size;
		rd.DepthOrArraySize = 1; rd.MipLevels = 1;
		rd.Format = DXGI_FORMAT_R32_TYPELESS;
		rd.SampleDesc.Count = 1;
		rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;

		D3D12_CLEAR_VALUE cv{};
		cv.Format = DXGI_FORMAT_D32_FLOAT;
		cv.DepthStencil.Depth = 1.0f;

		m_shadow_map.Reset();
		if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd,
			D3D12_RESOURCE_STATE_DEPTH_WRITE, &cv, IID_PPV_ARGS(&m_shadow_map))))
		{
			OutputDebugStringA("[shadows] shadow atlas creation failed\n");
			return false;
		}
		m_shadow_map_state = D3D12_RESOURCE_STATE_DEPTH_WRITE;
		m_shadow_map_size = size;

		if (!m_shadow_dsv_heap)
		{
			D3D12_DESCRIPTOR_HEAP_DESC dh{};
			dh.Type = D3D12_DESCRIPTOR_HEAP_TYPE_DSV;
			dh.NumDescriptors = 1;
			if (FAILED(dev->CreateDescriptorHeap(&dh, IID_PPV_ARGS(&m_shadow_dsv_heap))))
				return false;
		}
		D3D12_DEPTH_STENCIL_VIEW_DESC dsv{};
		dsv.Format = DXGI_FORMAT_D32_FLOAT;   // typed view over the typeless resource
		dsv.ViewDimension = D3D12_DSV_DIMENSION_TEXTURE2D;
		dev->CreateDepthStencilView(m_shadow_map.Get(), &dsv, m_shadow_dsv_heap->GetCPUDescriptorHandleForHeapStart());

		D3D12_SHADER_RESOURCE_VIEW_DESC srv{};
		srv.Format = DXGI_FORMAT_R32_FLOAT;
		srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
		srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
		srv.Texture2D.MipLevels = 1;
		dev->CreateShaderResourceView(m_shadow_map.Get(), &srv, m_shadow_srv_cpu);

		// Lazily created support buffers (once): the per-tile light-VP b0 clones + the caster instance VB.
		if (!m_shadow_pass_cb)
		{
			D3D12_HEAP_PROPERTIES up{}; up.Type = D3D12_HEAP_TYPE_UPLOAD;
			D3D12_RESOURCE_DESC bd{}; bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
			bd.Height = 1; bd.DepthOrArraySize = 1; bd.MipLevels = 1; bd.SampleDesc.Count = 1;
			bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
			D3D12_RANGE none{ 0, 0 };

			bd.Width = (UINT64)256 * MAX_SHADOW_SPOTS;
			if (FAILED(dev->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &bd,
				D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_shadow_pass_cb)))) return false;
			if (FAILED(m_shadow_pass_cb->Map(0, &none, &m_shadow_pass_cb_mapped))) return false;

			bd.Width = (UINT64)64 * MAX_SHADOW_INSTANCES;
			if (FAILED(dev->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &bd,
				D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_shadow_instance_vb)))) return false;
			if (FAILED(m_shadow_instance_vb->Map(0, &none, &m_shadow_instance_vb_mapped))) return false;
		}

		OutputDebugStringA("[shadows] shadow atlas ready\n");
		return true;
	}


	void DX12Renderer::prepare_shadow_pass()
	{
		using namespace DirectX;

		m_shadow_spot_count = 0;
		m_frame_constants.shadow_map_texel = 1.0f / (float)SHADOW_TILE_SIZE;

		if (!m_pipeline_3d.shadow_pso() || !m_shadow_map) return;

		// Debug/support kill switch: VORTEX_NO_SPOT_SHADOWS=1 disables all spot shadows at runtime —
		// used for A/B verification captures and as an escape hatch if a scene misbehaves.
		{
			char buf[8];
			DWORD n = GetEnvironmentVariableA("VORTEX_NO_SPOT_SHADOWS", buf, sizeof(buf));
			if (n > 0 && n < sizeof(buf) && buf[0] == '1') return;
		}

		// Collect up to MAX_SHADOW_SPOTS shadow-requesting spots in SUBMIT (= scene) order. Both the
		// flashlight AND authored scene spots get a tile; extras beyond 4 simply render unshadowed.
		for (size_t i = 0; i < m_spot_lights.size() && i < MAX_SPOT_LIGHTS && m_shadow_spot_count < MAX_SHADOW_SPOTS; ++i)
		{
			const SpotLightData& s = m_spot_lights[i];
			if (!s.cast_shadows || s.shadow_strength <= 0.0f) continue;

			XMFLOAT4X4 vp;
			if (!build_spot_vp(s, vp)) continue;

			ShadowSpot& slot = m_shadow_spots[m_shadow_spot_count];
			slot.spot_index = (int)i;
			slot.vp = vp;

			// The depth pass's b0 for this tile: this frame's constants with the LIGHT's VP swapped in.
			// The standard VS only reads view_projection from b0, the rest is along for the ride.
			if (m_shadow_pass_cb_mapped)
			{
				PerFrameConstants sc = m_frame_constants;
				sc.view_projection = vp;
				memcpy((u8*)m_shadow_pass_cb_mapped + (size_t)m_shadow_spot_count * 256, &sc, sizeof(sc));
			}
			m_shadow_spot_count++;
		}
	}


	void DX12Renderer::render_shadow_pass()
	{
		using namespace DirectX;
		if (m_shadow_spot_count == 0 || !m_shadow_map || !m_shadow_instance_vb_mapped) return;

		auto& reg = ResourceRegistry::instance();

		// ---- record the atlas passes ----
		if (m_shadow_map_state != D3D12_RESOURCE_STATE_DEPTH_WRITE)
		{
			D3D12_RESOURCE_BARRIER b{};
			b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
			b.Transition.pResource = m_shadow_map.Get();
			b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
			b.Transition.StateBefore = m_shadow_map_state;
			b.Transition.StateAfter = D3D12_RESOURCE_STATE_DEPTH_WRITE;
			m_command_list->ResourceBarrier(1, &b);
			m_shadow_map_state = D3D12_RESOURCE_STATE_DEPTH_WRITE;
		}

		auto dsv = m_shadow_dsv_heap->GetCPUDescriptorHandleForHeapStart();
		m_command_list->ClearDepthStencilView(dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);   // whole atlas once
		m_command_list->OMSetRenderTargets(0, nullptr, FALSE, &dsv);   // depth-only: no RTV

		m_command_list->SetPipelineState(m_pipeline_3d.shadow_pso());
		m_command_list->SetGraphicsRootSignature(m_pipeline_3d.root_signature());
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

		struct Caster { id::id_type mesh; const DirectX::XMFLOAT4X4* world; };
		std::vector<Caster> casters;
		std::unordered_map<id::id_type, XMFLOAT4> bounds;   // mesh -> (local center, radius) — shared across tiles
		bounds.reserve(64);

		u32 vb_used = 0;   // running offset into the shared shadow instance VB (across all tiles)

		for (u32 t = 0; t < m_shadow_spot_count; ++t)
		{
			// ---- cone-cull + pack this light's casters (CPU) ----
			// Own pack per light: the scene's instance packing is keyed to the MAIN camera frustum and
			// runs later — the wall BEHIND the player must still cast into the light frustum.
			ShadowFrustum fr = extract_shadow_frustum(m_shadow_spots[t].vp);
			casters.clear();

			for (const auto& item : m_render_queue)
			{
				if (item.bone_offset != NO_BONES) continue;   // v1: skinned meshes receive but don't cast
				// Transparent materials (#33) don't cast: an alpha-blended ghost/glass pane throwing a
				// fully SOLID shadow reads as a bug (real tinted-glass shadows need translucent maps — v2).
				{
					auto* cmat = reg.get_material(item.material_id);
					if (cmat && cmat->blend_mode() != 0) continue;
				}
				XMFLOAT4 bd;
				auto bit = bounds.find(item.mesh_id);
				if (bit == bounds.end())
				{
					Mesh* mp = reg.get_mesh(item.mesh_id);
					float mnx = 0, mny = 0, mnz = 0, mxx = 1, mxy = 1, mxz = 1;
					if (mp && mp->is_valid()) { mp->get_min(mnx, mny, mnz); mp->get_max(mxx, mxy, mxz); }
					float dx = mxx - mnx, dy = mxy - mny, dz = mxz - mnz;
					bd = XMFLOAT4((mnx + mxx) * 0.5f, (mny + mxy) * 0.5f, (mnz + mxz) * 0.5f,
						0.5f * sqrtf(dx * dx + dy * dy + dz * dz));
					bounds.emplace(item.mesh_id, bd);
				}
				else bd = bit->second;

				const XMFLOAT4X4& W = item.world_matrix;
				XMVECTOR wc = XMVector3TransformCoord(XMVectorSet(bd.x, bd.y, bd.z, 1.f), XMLoadFloat4x4(&W));
				float sx = sqrtf(W._11 * W._11 + W._12 * W._12 + W._13 * W._13);
				float sy = sqrtf(W._21 * W._21 + W._22 * W._22 + W._23 * W._23);
				float sz = sqrtf(W._31 * W._31 + W._32 * W._32 + W._33 * W._33);
				float ms = sx > sy ? (sx > sz ? sx : sz) : (sy > sz ? sy : sz);
				if (sphere_in_shadow_frustum(fr, XMVectorGetX(wc), XMVectorGetY(wc), XMVectorGetZ(wc), bd.w * ms + 0.05f))
				{
					casters.push_back({ item.mesh_id, &item.world_matrix });
					if (vb_used + casters.size() >= MAX_SHADOW_INSTANCES) break;   // shared hard cap
				}
			}
			if (casters.empty()) continue;   // empty tile stays cleared -> fully lit, correct

			// Group by mesh for instanced draws; pack into the shared VB at the running offset.
			std::sort(casters.begin(), casters.end(),
				[](const Caster& a, const Caster& b) { return a.mesh < b.mesh; });
			u8* vb = (u8*)m_shadow_instance_vb_mapped;
			for (size_t i = 0; i < casters.size(); ++i)
				memcpy(vb + (size_t)(vb_used + i) * 64, casters[i].world, 64);

			// ---- this tile's viewport + b0 (the light's VP clone) ----
			const float tile = (float)SHADOW_TILE_SIZE;
			const LONG tx = (LONG)((t & 1) * SHADOW_TILE_SIZE);
			const LONG ty = (LONG)(((t >> 1) & 1) * SHADOW_TILE_SIZE);
			D3D12_VIEWPORT vp{}; vp.TopLeftX = (float)tx; vp.TopLeftY = (float)ty; vp.Width = tile; vp.Height = tile; vp.MaxDepth = 1.0f;
			D3D12_RECT sc{}; sc.left = tx; sc.top = ty; sc.right = tx + (LONG)SHADOW_TILE_SIZE; sc.bottom = ty + (LONG)SHADOW_TILE_SIZE;
			m_command_list->RSSetViewports(1, &vp);
			m_command_list->RSSetScissorRects(1, &sc);
			m_command_list->SetGraphicsRootConstantBufferView(0, m_shadow_pass_cb->GetGPUVirtualAddress() + (UINT64)t * 256);

			size_t i = 0;
			while (i < casters.size())
			{
				const id::id_type meshId = casters[i].mesh;
				size_t j = i + 1;
				while (j < casters.size() && casters[j].mesh == meshId) ++j;
				const u32 count = (u32)(j - i);

				Mesh* mesh = reg.get_mesh(meshId);
				if (mesh && mesh->is_valid())
				{
					D3D12_VERTEX_BUFFER_VIEW vbs[2];
					vbs[0] = mesh->vertex_buffer_view();
					vbs[1].BufferLocation = m_shadow_instance_vb->GetGPUVirtualAddress() + (UINT64)(vb_used + i) * 64;
					vbs[1].SizeInBytes = count * 64;
					vbs[1].StrideInBytes = 64;
					m_command_list->IASetVertexBuffers(0, 2, vbs);
					if (mesh->has_indices())
					{
						m_command_list->IASetIndexBuffer(&mesh->index_buffer_view());
						m_command_list->DrawIndexedInstanced(mesh->index_count(), count, 0, 0, 0);
					}
					else
					{
						m_command_list->DrawInstanced(mesh->vertex_count(), count, 0, 0);
					}
					++m_draw_call_count;
				}
				i = j;
			}
			vb_used += (u32)casters.size();
		}

		// Hand the atlas to the pixel shaders (t7) for every scene pass this frame.
		{
			D3D12_RESOURCE_BARRIER b{};
			b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
			b.Transition.pResource = m_shadow_map.Get();
			b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
			b.Transition.StateBefore = D3D12_RESOURCE_STATE_DEPTH_WRITE;
			b.Transition.StateAfter = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
			m_command_list->ResourceBarrier(1, &b);
			m_shadow_map_state = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
		}
	}
}
