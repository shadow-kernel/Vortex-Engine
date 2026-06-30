#include "DX12Renderer_Internal.h"

namespace vortex::graphics::dx12
{
	u32 DX12Renderer::create_render_target(u32 width, u32 height)
	{
		if (!m_initialized || width == 0 || height == 0) return 0;
		if (m_render_targets.size() >= MAX_RENDER_TARGETS) return 0;
		
		auto target = std::make_unique<DX12RenderTarget>();
		if (!target->initialize(DX12Core::instance().device(), width, height))
		{
			return 0;
		}
		
		u32 id = m_next_render_target_id++;
		m_render_targets[id] = std::move(target);
		
		OutputDebugStringA(("Created render target ID " + std::to_string(id) + 
			" (" + std::to_string(width) + "x" + std::to_string(height) + ")\n").c_str());
		
		return id;
	}
	

	void DX12Renderer::destroy_render_target(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it != m_render_targets.end())
		{
			m_command_queue.flush(); // Ensure GPU is done with this target
			m_render_targets.erase(it);
			OutputDebugStringA(("Destroyed render target ID " + std::to_string(target_id) + "\n").c_str());
		}
	}
	

	bool DX12Renderer::resize_render_target(u32 target_id, u32 width, u32 height)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return false;
		
		m_command_queue.flush(); // Ensure GPU is done
		return it->second->resize(DX12Core::instance().device(), width, height);
	}
	

	bool DX12Renderer::has_render_target(u32 target_id) const
	{
		return m_render_targets.find(target_id) != m_render_targets.end();
	}
	

	void DX12Renderer::render_to_target(u32 target_id, const ViewportCamera& camera, bool render_grid)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return;
		
		render_scene_to_target(it->second.get(), camera, render_grid);
	}
	

	void DX12Renderer::render_scene_to_target(DX12RenderTarget* target, const ViewportCamera& camera, bool render_grid)
	{
		using namespace DirectX;
		
		if (!target || !target->is_initialized()) return;
		
		// Get current back buffer index for command allocator
		u32 idx = m_swapchain.current_back_buffer_index();
		
		// Ensure previous work is complete before rendering to secondary target
		m_command_queue.flush();
		m_command_allocators[idx]->Reset();
		
		auto* pso = m_render_queue.empty() ? m_pipeline.pipeline_state() :
			(m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state());
		m_command_list->Reset(m_command_allocators[idx].Get(), pso);
		
		// Transition render target to render target state
		target->transition_to_render_target(m_command_list.Get());
		
		// Setup viewport and scissor
		D3D12_VIEWPORT vp{};
		vp.Width = (float)target->width();
		vp.Height = (float)target->height();
		vp.MaxDepth = 1.0f;
		
		D3D12_RECT sc{};
		sc.right = target->width();
		sc.bottom = target->height();
		
		m_command_list->RSSetViewports(1, &vp);
		m_command_list->RSSetScissorRects(1, &sc);
		
		// Clear render target and depth - skybox will fill background
		FLOAT clear_color[4] = { 0.0f, 0.0f, 0.0f, 1.0f }; // Black - skybox renders over this
		auto rtv = target->rtv();
		auto dsv = target->dsv();
		m_command_list->ClearRenderTargetView(rtv, clear_color, 0, nullptr);
		m_command_list->ClearDepthStencilView(dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		
		// Build view/projection matrix for this camera
		XMVECTOR eye = XMLoadFloat3(&camera.position);
		XMVECTOR at = XMLoadFloat3(&camera.target);
		XMVECTOR up = XMLoadFloat3(&camera.up);
		XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
		
		float aspect = (float)target->width() / (float)target->height();
		XMMATRIX proj;
		if (camera.orthographic)
		{
			proj = XMMatrixOrthographicLH(camera.ortho_size * aspect, camera.ortho_size, 
				camera.near_clip, camera.far_clip);
		}
		else
		{
			proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(camera.fov_degrees), aspect, 
				camera.near_clip, camera.far_clip);
		}
		
		XMMATRIX vp_matrix = view * proj;
		
		// Update per-frame constants with this camera's view/proj
		PerFrameConstants frame_constants = m_frame_constants;
		XMStoreFloat4x4(&frame_constants.view_projection, vp_matrix);
		frame_constants.camera_position = camera.position;

		// Pull in the CURRENT lighting state. Offscreen previews (AssetPreviewRenderer) set ambient + directional
		// + point lights via the API right before this call, but m_frame_constants is only refreshed by the main
		// loop — so without this the preview rendered with stale (dark) lighting and NO point lights. Copy them
		// here AND upload the point lights to the light CB so they actually illuminate the preview.
		frame_constants.light_direction = m_light_direction;
		frame_constants.directional_intensity = m_directional_intensity;
		frame_constants.light_color = m_light_color;
		frame_constants.ambient_strength = m_ambient_strength;
		frame_constants.point_light_count = static_cast<u32>((std::min)(m_point_lights.size(), static_cast<size_t>(MAX_POINT_LIGHTS)));
		frame_constants.spot_light_count = static_cast<u32>((std::min)(m_spot_lights.size(), static_cast<size_t>(MAX_SPOT_LIGHTS)));
		if (m_light_cb_mapped)
		{
			u8* lptr = static_cast<u8*>(m_light_cb_mapped);
			for (size_t i = 0; i < m_point_lights.size() && i < MAX_POINT_LIGHTS; ++i)
			{
				GPUPointLight gl{};
				gl.position = m_point_lights[i].position; gl.range = m_point_lights[i].range;
				gl.color = m_point_lights[i].color; gl.intensity = m_point_lights[i].intensity;
				memcpy(lptr + i * sizeof(GPUPointLight), &gl, sizeof(GPUPointLight));
			}
		}

		if (m_per_frame_cb_mapped)
			memcpy(m_per_frame_cb_mapped, &frame_constants, sizeof(frame_constants));
		
		// ========== RENDER SKYBOX FIRST (appears behind everything) ==========
		if (m_skybox_pipeline.pipeline_state())
		{
		m_command_list->SetPipelineState(m_skybox_pipeline.pipeline_state());
		m_command_list->SetGraphicsRootSignature(m_skybox_pipeline.root_signature());
			
		// Update skybox constants with THIS camera's inverse VP matrix
		if (m_skybox_cb_mapped)
		{
		auto skybox_ptr = reinterpret_cast<u8*>(m_skybox_cb_mapped);
		memcpy(skybox_ptr, m_skybox_pipeline.get_constants(), m_skybox_pipeline.get_constants_size());
				
		XMMATRIX inv_vp = XMMatrixInverse(nullptr, vp_matrix);
		XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(skybox_ptr), inv_vp);
		}
			
		m_command_list->SetGraphicsRootConstantBufferView(0, m_skybox_cb->GetGPUVirtualAddress());
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		m_command_list->DrawInstanced(3, 1, 0, 0);
			
		// Reset render target state after skybox
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		}
		
		// Render grid if requested
		if (render_grid && m_grid_pipeline.pipeline_state() && m_grid_vertex_count > 0)
		{
			// Update grid constants
			if (m_grid_cb_mapped)
			{
				GridConstants grid_cb{};
				XMStoreFloat4x4(&grid_cb.view_projection, vp_matrix);
				
				XMMATRIX inv_vp = XMMatrixInverse(nullptr, vp_matrix);
				XMStoreFloat4x4(&grid_cb.inverse_view_projection, inv_vp);
				
				grid_cb.camera_position = camera.position;
				grid_cb.grid_spacing = m_grid_spacing;
				grid_cb.grid_extent = m_grid_extent;
				grid_cb.major_line_interval = m_grid_major_interval;
				
				memcpy(m_grid_cb_mapped, &grid_cb, sizeof(grid_cb));
			}
			
			m_command_list->SetPipelineState(m_grid_pipeline.pipeline_state());
			m_command_list->SetGraphicsRootSignature(m_grid_pipeline.root_signature());
			m_command_list->SetGraphicsRootConstantBufferView(0, m_grid_cb->GetGPUVirtualAddress());
			m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_LINELIST);
			m_command_list->IASetVertexBuffers(0, 1, &m_grid_vbv);
			m_command_list->DrawInstanced(m_grid_vertex_count, 1, 0, 0);
		}
		
		// Render 3D objects
		if (!m_render_queue.empty())
		{
			m_command_list->SetPipelineState(m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state());
			m_command_list->SetGraphicsRootSignature(m_pipeline_3d.root_signature());
			m_command_list->SetGraphicsRootConstantBufferView(0, m_per_frame_cb->GetGPUVirtualAddress());
			m_command_list->SetGraphicsRootConstantBufferView(2, m_light_cb->GetGPUVirtualAddress()); // point/spot lights
				// MUST set the SRV heap before any texture descriptor table is bound in the loop below — without
				// it, SetGraphicsRootDescriptorTable removes the device (the crash when a textured model's
				// thumbnail rendered, e.g. opening the Models folder).
				{ auto* _sh = ResourceRegistry::instance().srv_heap(); if (_sh) { ID3D12DescriptorHeap* _hh[] = { _sh }; m_command_list->SetDescriptorHeaps(1, _hh); } }
				m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
			
			auto& reg = ResourceRegistry::instance();
			size_t objectCount = (std::min)(m_render_queue.size(), static_cast<size_t>(MAX_RENDER_OBJECTS));
			
			for (size_t i = 0; i < objectCount; ++i)
			{
				const auto& item = m_render_queue[i];
				auto* mesh = reg.get_mesh(item.mesh_id);
				if (!mesh) continue;
				
				PerObjectConstants obj_cb{};
				obj_cb.world = item.world_matrix;
				obj_cb.base_color = { 0.8f, 0.8f, 0.8f, 1.0f };
				obj_cb.metallic = 0.0f; obj_cb.roughness = 0.5f; obj_cb.ao = 1.0f; obj_cb.normal_strength = 1.0f;

				// Full PBR material + textures (matches render_3d_scene) so previews show the REAL material, not a
				// flat base color.
				auto* mat = reg.get_material(item.material_id);
				if (mat)
				{
					const auto& props = mat->properties();
					obj_cb.base_color = props.base_color;
					obj_cb.metallic = props.metallic; obj_cb.roughness = props.roughness;
					obj_cb.ao = props.ao; obj_cb.normal_strength = props.normal_strength;
					obj_cb.use_directx_normals = props.use_directx_normals;
					auto* tex = mat->albedo_texture();
					if (tex && tex->is_valid() && tex->srv_gpu().ptr != 0) { obj_cb.has_albedo_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(3, tex->srv_gpu()); }
					auto* normal = mat->normal_texture();
					if (normal && normal->is_valid() && normal->srv_gpu().ptr != 0) { obj_cb.has_normal_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(4, normal->srv_gpu()); }
					auto* metallic_tex = mat->metallic_texture();
					if (metallic_tex && metallic_tex->is_valid() && metallic_tex->srv_gpu().ptr != 0) { obj_cb.has_metallic_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(5, metallic_tex->srv_gpu()); }
					auto* roughness_tex = mat->roughness_texture();
					if (roughness_tex && roughness_tex->is_valid() && roughness_tex->srv_gpu().ptr != 0) { obj_cb.has_roughness_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(6, roughness_tex->srv_gpu()); }
					auto* ao_tex = mat->ao_texture();
					if (ao_tex && ao_tex->is_valid() && ao_tex->srv_gpu().ptr != 0) { obj_cb.has_ao_texture = 1; m_command_list->SetGraphicsRootDescriptorTable(7, ao_tex->srv_gpu()); }
				}
				
				void* dest = static_cast<u8*>(m_per_object_cb_mapped) + i * 256;
				memcpy(dest, &obj_cb, sizeof(obj_cb));
				
				D3D12_GPU_VIRTUAL_ADDRESS obj_cb_addr = m_per_object_cb->GetGPUVirtualAddress() + i * 256;
				m_command_list->SetGraphicsRootConstantBufferView(1, obj_cb_addr);
				
				// The shared 3D PSO now REQUIRES per-instance world data on slot 1 (instancing). The editor
				// preview draws one instance per object, so stage this world matrix + bind the instance VB.
				if (m_instance_vb_mapped)
					memcpy(static_cast<u8*>(m_instance_vb_mapped) + (size_t)i * 64, &item.world_matrix, 64);
				D3D12_VERTEX_BUFFER_VIEW vbs2[2];
				vbs2[0] = mesh->vertex_buffer_view();
				vbs2[1].BufferLocation = m_instance_vb->GetGPUVirtualAddress() + (UINT64)i * 64;
				vbs2[1].SizeInBytes = 64;
				vbs2[1].StrideInBytes = 64;
				m_command_list->IASetVertexBuffers(0, 2, vbs2);
				m_command_list->IASetIndexBuffer(&mesh->index_buffer_view());
				m_command_list->DrawIndexedInstanced(mesh->index_count(), 1, 0, 0, 0);
			}
		}
		
		// Execute commands
		m_command_list->Close();
		m_command_queue.execute_command_list(m_command_list.Get());
		m_command_queue.signal_and_wait();
		
		// Restore main camera constants
		if (m_per_frame_cb_mapped)
			memcpy(m_per_frame_cb_mapped, &m_frame_constants, sizeof(m_frame_constants));
	}
	

	bool DX12Renderer::prepare_render_target_readback(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end()) return false;
		
		auto* target = it->second.get();
		
		// Ensure GPU is idle before readback
		m_command_queue.flush();
		
		u32 idx = m_swapchain.current_back_buffer_index();
		m_command_allocators[idx]->Reset();
		m_command_list->Reset(m_command_allocators[idx].Get(), nullptr);
		
		target->copy_to_staging(m_command_list.Get());
		
		m_command_list->Close();
		m_command_queue.execute_command_list(m_command_list.Get());
		m_command_queue.signal_and_wait();
		
		return true;
	}
	

	const void* DX12Renderer::read_render_target_pixels(u32 target_id, u32& out_width, u32& out_height, u32& out_row_pitch)
	{
		auto it = m_render_targets.find(target_id);
		if (it == m_render_targets.end())
		{
			out_width = out_height = out_row_pitch = 0;
			return nullptr;
		}
		
		auto* target = it->second.get();
		out_width = target->width();
		out_height = target->height();
		out_row_pitch = target->staging_row_pitch();
		
		return target->map_staging_buffer();
	}
	

	void DX12Renderer::release_render_target_pixels(u32 target_id)
	{
		auto it = m_render_targets.find(target_id);
		if (it != m_render_targets.end())
		{
			it->second->unmap_staging_buffer();
		}
	}

}
