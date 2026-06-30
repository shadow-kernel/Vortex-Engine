#include "DX12Renderer_Internal.h"

namespace vortex::graphics::dx12
{
		bool DX12Renderer::create_game_window(HWND hwnd, u32 width, u32 height)
	{
		if (!m_initialized || !hwnd || width == 0 || height == 0) return false;
		if (m_game_window_active) destroy_game_window();

		auto& core = DX12Core::instance();
		SwapchainDesc sc_desc{};
		sc_desc.hwnd = hwnd;
		sc_desc.width = width;
		sc_desc.height = height;
		sc_desc.buffer_count = 2;
		if (!m_game_swapchain.initialize(core.factory(), m_command_queue.queue(), core.device(), sc_desc))
			return false;
		if (!m_game_depth.initialize(core.device(), width, height, DXGI_FORMAT_D32_FLOAT))
		{
			m_game_swapchain.shutdown();
			return false;
		}
		if (FAILED(core.device()->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
				IID_PPV_ARGS(&m_game_cmd_allocator))))
		{
			m_game_depth.shutdown();
			m_game_swapchain.shutdown();
			return false;
		}
		m_game_window_active = true;
		return true;
	}


	void DX12Renderer::render_game_window()
	{
		if (!m_initialized || !m_game_window_active) return;

		// The command list + queue are shared with the editor frame; finish all prior GPU work before
		// reusing them. Simple full sync — perf is not the priority for the play window.
		m_command_queue.flush();

		// Target the game window's own swapchain + depth at its own size, using the CURRENT camera
		// (the caller sets the game's main camera before calling this).
		m_active_rtv = m_game_swapchain.current_rtv();
		m_active_dsv = m_game_depth.dsv();
		m_active_width = m_game_swapchain.width();
		m_active_height = m_game_swapchain.height();

		update_per_frame_constants();

		m_game_cmd_allocator->Reset();
		auto* pso = m_render_queue.empty() ? m_pipeline.pipeline_state() :
			(m_wireframe_mode ? m_pipeline_3d.wireframe_pso() : m_pipeline_3d.pipeline_state());
		m_command_list->Reset(m_game_cmd_allocator.Get(), pso);

		D3D12_RESOURCE_BARRIER barrier{};
		barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
		barrier.Transition.pResource = m_game_swapchain.current_back_buffer();
		barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
		barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
		barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
		m_command_list->ResourceBarrier(1, &barrier);

		D3D12_VIEWPORT vp{}; vp.Width = (float)m_active_width; vp.Height = (float)m_active_height; vp.MaxDepth = 1.0f;
		D3D12_RECT sc{}; sc.right = (LONG)m_active_width; sc.bottom = (LONG)m_active_height;
		m_command_list->RSSetViewports(1, &vp);
		m_command_list->RSSetScissorRects(1, &sc);
		m_command_list->ClearRenderTargetView(m_active_rtv, m_clear_color, 0, nullptr);
		m_command_list->ClearDepthStencilView(m_active_dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
		m_command_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		m_command_list->OMSetRenderTargets(1, &m_active_rtv, FALSE, &m_active_dsv);

		if (m_skybox_enabled) render_skybox();
		if (m_grid_visible) render_grid();
		if (!m_render_queue.empty()) render_3d_scene();

		barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
		barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
		m_command_list->ResourceBarrier(1, &barrier);
		m_command_list->Close();

		m_command_queue.execute_command_list(m_command_list.Get());
		m_command_queue.flush(); // wait before present (safe)

		// 2D UI overlay (Direct2D over the same back buffer) — drawn after the 3D, before present.
		m_ui_overlay.render(m_game_swapchain.current_back_buffer());

		m_game_swapchain.present(false);
	}


	void DX12Renderer::resize_game_window(u32 width, u32 height)
	{
		if (!m_game_window_active || width == 0 || height == 0) return;
		m_command_queue.flush();
		m_ui_overlay.invalidate_targets();   // drop cached bitmaps aliasing the old game back buffers
		m_game_swapchain.resize(width, height);
		m_game_depth.resize(DX12Core::instance().device(), width, height);
	}


	void DX12Renderer::destroy_game_window()
	{
		if (!m_game_window_active) return;
		m_command_queue.flush();
		m_ui_overlay.invalidate_targets();   // wrapped game back buffers are about to be freed
		m_game_window_active = false;
		m_game_depth.shutdown();
		m_game_swapchain.shutdown();
		m_game_cmd_allocator.Reset();
	}

	// --- Frustum culling helpers (don't render what the camera can't see) ---

	void DX12Renderer::submit_render_item(const RenderItem& item)
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_submit_queue.push_back(item);
	}


	void DX12Renderer::submit_mesh_instances(id::id_type mesh, id::id_type material, const float* world_matrices, u32 count)
	{
		if (!world_matrices || count == 0) return;
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_submit_queue.reserve(m_submit_queue.size() + count);
		for (u32 i = 0; i < count; ++i)
		{
			RenderItem item;
			item.mesh_id = mesh;
			item.material_id = material;
			memcpy(&item.world_matrix, world_matrices + (size_t)i * 16, sizeof(DirectX::XMFLOAT4X4));
			m_submit_queue.push_back(item);
		}
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


}
