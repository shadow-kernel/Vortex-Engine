#include "DX12Renderer.h"
#include "../Resources/ResourceRegistry.h"
#include <algorithm>
#include <memory>

namespace vortex::graphics::dx12
{
	DX12Renderer& DX12Renderer::instance()
	{
		static DX12Renderer inst;
		return inst;
	}

	bool DX12Renderer::initialize(const RendererDesc& desc)
	{
		if (m_initialized) return true;
		if (!desc.hwnd || desc.width == 0 || desc.height == 0) return false;

		auto& core = DX12Core::instance();
		if (!core.initialize()) return false;
		if (!m_command_queue.initialize(core.device())) return false;

		SwapchainDesc sc_desc{};
		sc_desc.hwnd = desc.hwnd;
		sc_desc.width = desc.width;
		sc_desc.height = desc.height;
		sc_desc.buffer_count = 2;

		if (!m_swapchain.initialize(core.factory(), m_command_queue.queue(), core.device(), sc_desc))
			return false;

		if (!create_command_allocators()) return false;
		if (!create_command_list()) return false;

		if (!m_depth_buffer.initialize(core.device(), desc.width, desc.height, DXGI_FORMAT_D32_FLOAT))
			return false;

		if (!m_pipeline.initialize(core.device())) return false;

		if (!m_pipeline_3d.initialize(core.device(), DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_D32_FLOAT))
			return false;

		if (m_grid_pipeline.initialize(core.device(), DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_D32_FLOAT))
		{
			OutputDebugStringA("Grid pipeline OK\n");
		}
		else
		{
			OutputDebugStringA("Grid pipeline FAILED\n");
			m_grid_visible = false;
		}

		if (!m_geometry.initialize(core.device())) return false;
		if (!create_constant_buffers()) return false;
		create_grid_resources();

		ResourceRegistry::instance().initialize(core.device());

		m_initialized = true;
		return true;
	}

	void DX12Renderer::shutdown()
	{
		if (!m_initialized) return;
		m_command_queue.flush();
		
		// Destroy all secondary render targets
		m_render_targets.clear();
		
		ResourceRegistry::instance().shutdown();
		{
			std::lock_guard<std::mutex> lock(m_queue_mutex);
			m_render_queue.clear();
			m_submit_queue.clear();
		}

		if (m_per_frame_cb && m_per_frame_cb_mapped) { m_per_frame_cb->Unmap(0, nullptr); m_per_frame_cb_mapped = nullptr; }
		m_per_frame_cb.Reset();
		if (m_per_object_cb && m_per_object_cb_mapped) { m_per_object_cb->Unmap(0, nullptr); m_per_object_cb_mapped = nullptr; }
		m_per_object_cb.Reset();
		if (m_grid_cb && m_grid_cb_mapped) { m_grid_cb->Unmap(0, nullptr); m_grid_cb_mapped = nullptr; }
		m_grid_cb.Reset();
		m_grid_vertex_buffer.Reset();

		m_depth_buffer.shutdown();
		m_geometry.shutdown();
		m_grid_pipeline.shutdown();
		m_pipeline_3d.shutdown();
		m_pipeline.shutdown();
		m_command_list.Reset();
		for (auto& a : m_command_allocators) a.Reset();
		m_swapchain.shutdown();
		m_command_queue.shutdown();
		DX12Core::instance().shutdown();
		m_initialized = false;
	}

	void DX12Renderer::resize(u32 w, u32 h)
	{
		if (!m_initialized || w == 0 || h == 0) return;
		m_command_queue.flush();
		m_swapchain.resize(w, h);
		m_depth_buffer.resize(DX12Core::instance().device(), w, h);
	}

	void DX12Renderer::render_frame()
	{
		if (!m_initialized) return;
		
		// Update FPS counter
		// Swap render queues (thread-safe) and clear submit queue for next frame
		{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_render_queue.swap(m_submit_queue);
		m_submit_queue.clear(); // Clear for next frame's submissions
		}
		
		// Update FPS counter
		m_frame_count++;
		auto now = std::chrono::high_resolution_clock::now();
		auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - m_last_fps_time).count();
		if (elapsed >= 500) // Update every 0.5 seconds
		{
			m_current_fps = static_cast<int>((m_frame_count * 1000) / elapsed);
			m_frame_count = 0;
			m_last_fps_time = now;
		}
		
		// Reset per-frame stats
		m_draw_call_count = 0;
		m_vertex_count = 0;
		
		u32 idx = m_swapchain.current_back_buffer_index();
		
		// Wait for this frame's previous work to complete (proper double buffering)
		// Only wait if GPU hasn't finished with this buffer yet
		m_command_queue.wait_for_fence_value(m_frame_fence_values[idx]);
		
		update_per_frame_constants();

		m_command_allocators[idx]->Reset();

		auto* pso = m_render_queue.empty() ? m_pipeline.pipeline_state() :
			(m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state());
		m_command_list->Reset(m_command_allocators[idx].Get(), pso);

		D3D12_RESOURCE_BARRIER barrier{};
		barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
		barrier.Transition.pResource = m_swapchain.current_back_buffer();
		barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
		barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
		barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
		m_command_list->ResourceBarrier(1, &barrier);

		auto rtv = m_swapchain.current_rtv();
		auto dsv = m_depth_buffer.dsv();

		D3D12_VIEWPORT vp{}; vp.Width = (float)m_swapchain.width(); vp.Height = (float)m_swapchain.height(); vp.MaxDepth = 1.0f;
		D3D12_RECT sc{}; sc.right = m_swapchain.width(); sc.bottom = m_swapchain.height();
		m_command_list->RSSetViewports(1, &vp);
		m_command_list->RSSetScissorRects(1, &sc);
		m_command_list->ClearRenderTargetView(rtv, m_clear_color, 0, nullptr);
		m_command_list->ClearDepthStencilView(dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);

		// Render grid first (background)
		if (m_grid_visible) render_grid();

		// Render 3D objects
		if (!m_render_queue.empty()) render_3d_scene();

		barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
		barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
		m_command_list->ResourceBarrier(1, &barrier);
		m_command_list->Close();

		m_command_queue.execute_command_list(m_command_list.Get());
		
		// Signal after this frame's commands are queued (non-blocking)
		m_frame_fence_values[idx] = m_command_queue.signal();
		
		m_swapchain.present(m_vsync_enabled);
		// Note: m_render_queue is NOT cleared - we keep last frame's data
		// for re-rendering if no new data is submitted (prevents flickering)
		}

		void DX12Renderer::render_3d_scene()
	{
		auto rtv = m_swapchain.current_rtv();
		auto dsv = m_depth_buffer.dsv();
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);

		auto* pso = m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state();
		m_command_list->SetPipelineState(pso);
		m_command_list->SetGraphicsRootSignature(m_pipeline_3d.root_signature());
		m_command_list->SetGraphicsRootConstantBufferView(0, m_per_frame_cb->GetGPUVirtualAddress());

		auto& reg = ResourceRegistry::instance();
		
		// Limit to MAX_RENDER_OBJECTS to prevent buffer overflow
		size_t objectCount = (std::min)(m_render_queue.size(), static_cast<size_t>(MAX_RENDER_OBJECTS));
		if (objectCount == 0) return;

		// Render all objects (no sorting - keeps original order stable for re-rendering)
		for (size_t i = 0; i < objectCount; ++i)
		{
		const auto& item = m_render_queue[i];
		Mesh* mesh = reg.get_mesh(item.mesh_id);
		if (!mesh || !mesh->is_valid()) continue;

		PerObjectConstants obj;
		obj.world = item.world_matrix;
		obj.base_color = { 0.95f, 0.95f, 0.95f, 1.0f };

		auto* mat = reg.get_material(item.material_id);
		if (mat) obj.base_color = mat->properties().base_color;

		if (m_per_object_cb_mapped)
		memcpy((u8*)m_per_object_cb_mapped + i * 256, &obj, sizeof(obj));

		m_command_list->SetGraphicsRootConstantBufferView(1, 
		m_per_object_cb->GetGPUVirtualAddress() + i * 256);
		m_command_list->IASetVertexBuffers(0, 1, &mesh->vertex_buffer_view());

		if (mesh->has_indices())
		{
		m_command_list->IASetIndexBuffer(&mesh->index_buffer_view());
		m_command_list->DrawIndexedInstanced(mesh->index_count(), 1, 0, 0, 0);
		m_vertex_count += mesh->index_count();
		}
		else
		{
		m_command_list->DrawInstanced(mesh->vertex_count(), 1, 0, 0);
		m_vertex_count += mesh->vertex_count();
		}
		m_draw_call_count++;
		}
		}

		void DX12Renderer::render_fallback_triangle()
	{
		if (m_grid_visible) return;
		auto rtv = m_swapchain.current_rtv();
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
		m_command_list->SetPipelineState(m_pipeline.pipeline_state());
		m_command_list->SetGraphicsRootSignature(m_pipeline.root_signature());
		m_command_list->IASetVertexBuffers(0, 1, &m_geometry.vertex_buffer_view());
		m_command_list->DrawInstanced(m_geometry.vertex_count(), 1, 0, 0);
	}

	void DX12Renderer::render_grid()
	{
		if (!m_grid_pipeline.pipeline_state()) return;

		auto rtv = m_swapchain.current_rtv();
		auto dsv = m_depth_buffer.dsv();
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		
		m_command_list->SetPipelineState(m_grid_pipeline.pipeline_state());
		m_command_list->SetGraphicsRootSignature(m_grid_pipeline.root_signature());

		using namespace DirectX;
		XMVECTOR eye = XMLoadFloat3(&m_camera_position);
		XMVECTOR at = XMLoadFloat3(&m_camera_target);
		XMVECTOR up = XMLoadFloat3(&m_camera_up);
		XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
		float aspect = (float)m_swapchain.width() / (float)m_swapchain.height();
		XMMATRIX proj = XMMatrixPerspectiveFovLH(XM_PIDIV4, aspect, 0.1f, 1000.0f);
		XMMATRIX vp = view * proj;

		GridConstants gc{};
		XMStoreFloat4x4(&gc.view_projection, vp);
		XMStoreFloat4x4(&gc.inverse_view_projection, XMMatrixInverse(nullptr, vp));
		gc.camera_position = m_camera_position;
		gc.grid_spacing = m_grid_spacing;
		gc.grid_extent = m_grid_extent;
		gc.major_line_interval = m_grid_major_interval;

		if (m_grid_cb_mapped)
			memcpy(m_grid_cb_mapped, &gc, sizeof(gc));

		m_command_list->SetGraphicsRootConstantBufferView(0, m_grid_cb->GetGPUVirtualAddress());
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		m_command_list->DrawInstanced(3, 1, 0, 0);
	}

	void DX12Renderer::set_grid_settings(float s, float m, float e)
	{
		m_grid_spacing = s; m_grid_major_interval = m; m_grid_extent = e;
	}

	void DX12Renderer::submit_render_item(const RenderItem& item) 
	{ 
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_submit_queue.push_back(item); 
	}
	
	void DX12Renderer::clear_render_queue() 
	{ 
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_submit_queue.clear(); 
	}

	void DX12Renderer::set_camera(const DirectX::XMFLOAT3& pos, const DirectX::XMFLOAT3& target, const DirectX::XMFLOAT3& up)
	{
		m_camera_position = pos; m_camera_target = target; m_camera_up = up;
	}

	void DX12Renderer::set_projection(float fov_degrees, float aspect, float near_clip, float far_clip)
	{
		m_fov_degrees = fov_degrees;
		m_aspect_ratio = aspect;
		m_near_clip = near_clip;
		m_far_clip = far_clip;
	}

	void DX12Renderer::render_camera_gizmo(
		const DirectX::XMFLOAT3& position,
		const DirectX::XMFLOAT3& forward,
		const DirectX::XMFLOAT3& right,
		const DirectX::XMFLOAT3& up,
		float near_width, float near_height,
		float far_width, float far_height,
		float near_dist, float far_dist,
		const DirectX::XMFLOAT4& color)
	{
		// This renders a wireframe camera frustum gizmo
		// For now, we'll add the gizmo as lines to a line buffer that gets rendered
		// This is a placeholder - full implementation would use a line rendering system
		
		using namespace DirectX;
		
		// Calculate frustum corners in world space
		XMVECTOR pos = XMLoadFloat3(&position);
		XMVECTOR fwd = XMLoadFloat3(&forward);
		XMVECTOR rgt = XMLoadFloat3(&right);
		XMVECTOR upv = XMLoadFloat3(&up);
		
		// Near plane corners
		XMVECTOR near_center = pos + fwd * near_dist;
		XMVECTOR near_tl = near_center - rgt * near_width + upv * near_height;
		XMVECTOR near_tr = near_center + rgt * near_width + upv * near_height;
		XMVECTOR near_bl = near_center - rgt * near_width - upv * near_height;
		XMVECTOR near_br = near_center + rgt * near_width - upv * near_height;
		
		// Far plane corners
		XMVECTOR far_center = pos + fwd * far_dist;
		XMVECTOR far_tl = far_center - rgt * far_width + upv * far_height;
		XMVECTOR far_tr = far_center + rgt * far_width + upv * far_height;
		XMVECTOR far_bl = far_center - rgt * far_width - upv * far_height;
		XMVECTOR far_br = far_center + rgt * far_width - upv * far_height;
		
		// TODO: Add these lines to a line buffer for rendering
		// For now, this is a stub that would be connected to a line rendering system
		// The lines would be:
		// - Near plane rectangle (4 lines)
		// - Far plane rectangle (4 lines)
		// - Connecting lines from camera to far corners (4 lines)
		// - Camera body (small box at position)
	}

	void DX12Renderer::set_directional_light(const DirectX::XMFLOAT3& dir, const DirectX::XMFLOAT3& col)
	{
		m_light_direction = dir; m_light_color = col;
	}

	void DX12Renderer::set_ambient_strength(float s) { m_ambient_strength = s; }

	bool DX12Renderer::create_command_allocators()
	{
		auto dev = DX12Core::instance().device();
		for (u32 i = 0; i < 2; ++i)
			if (FAILED(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&m_command_allocators[i]))))
				return false;
		return true;
	}

	bool DX12Renderer::create_command_list()
	{
		auto dev = DX12Core::instance().device();
		return SUCCEEDED(dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
			m_command_allocators[0].Get(), nullptr, IID_PPV_ARGS(&m_command_list))) && SUCCEEDED(m_command_list->Close());
	}

	bool DX12Renderer::create_constant_buffers()
	{
		auto dev = DX12Core::instance().device();
		D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_UPLOAD;
		D3D12_RESOURCE_DESC rd{}; rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1; rd.SampleDesc.Count = 1;
		rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

		rd.Width = 256;
		if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_per_frame_cb))))
			return false;
		D3D12_RANGE r{0,0};
		if (FAILED(m_per_frame_cb->Map(0, &r, &m_per_frame_cb_mapped))) return false;

		// Support up to MAX_RENDER_OBJECTS objects (16384 = 4MB buffer)
		rd.Width = 256 * MAX_RENDER_OBJECTS;
		if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_per_object_cb))))
			return false;
		if (FAILED(m_per_object_cb->Map(0, &r, &m_per_object_cb_mapped))) return false;

		return true;
	}

	bool DX12Renderer::create_grid_resources()
	{
		auto dev = DX12Core::instance().device();

		D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_UPLOAD;
		D3D12_RESOURCE_DESC rd{}; rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		rd.Width = 256; rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
		rd.SampleDesc.Count = 1; rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

		if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_grid_cb))))
			return false;
		
		D3D12_RANGE r{0,0};
		if (FAILED(m_grid_cb->Map(0, &r, &m_grid_cb_mapped))) return false;

		return true;
	}

	void DX12Renderer::update_per_frame_constants()
	{
		using namespace DirectX;
		XMVECTOR eye = XMLoadFloat3(&m_camera_position);
		XMVECTOR at = XMLoadFloat3(&m_camera_target);
		XMVECTOR up = XMLoadFloat3(&m_camera_up);

		XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
		float aspect = (float)m_swapchain.width() / (float)m_swapchain.height();
		XMMATRIX proj = XMMatrixPerspectiveFovLH(XM_PIDIV4, aspect, 0.1f, 1000.0f);
		XMMATRIX vp = view * proj;

		XMStoreFloat4x4(&m_frame_constants.view_projection, vp);
		m_frame_constants.camera_position = m_camera_position;
		m_frame_constants.light_direction = m_light_direction;
		m_frame_constants.light_color = m_light_color;
		m_frame_constants.ambient_strength = m_ambient_strength;

		if (m_per_frame_cb_mapped)
			memcpy(m_per_frame_cb_mapped, &m_frame_constants, sizeof(m_frame_constants));
	}

	void DX12Renderer::wait_for_previous_frame() { m_command_queue.signal_and_wait(); }
	
	// ============== Multi-Viewport Rendering ==============
	
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
		
		// Clear render target and depth - use a distinct color to verify rendering works
		FLOAT clear_color[4] = { 0.2f, 0.3f, 0.5f, 1.0f }; // Blue-ish to distinguish from main viewport
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
		
		if (m_per_frame_cb_mapped)
			memcpy(m_per_frame_cb_mapped, &frame_constants, sizeof(frame_constants));
		
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
				
				auto* mat = reg.get_material(item.material_id);
				if (mat)
				{
				obj_cb.base_color = mat->properties().base_color;
				}
				else
				{
					obj_cb.base_color = { 0.8f, 0.8f, 0.8f, 1.0f };
				}
				
				void* dest = static_cast<u8*>(m_per_object_cb_mapped) + i * 256;
				memcpy(dest, &obj_cb, sizeof(obj_cb));
				
				D3D12_GPU_VIRTUAL_ADDRESS obj_cb_addr = m_per_object_cb->GetGPUVirtualAddress() + i * 256;
				m_command_list->SetGraphicsRootConstantBufferView(1, obj_cb_addr);
				
				m_command_list->IASetVertexBuffers(0, 1, &mesh->vertex_buffer_view());
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
