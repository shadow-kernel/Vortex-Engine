#include "DX12Renderer.h"
#include "../Resources/ResourceRegistry.h"
#include <algorithm>

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
		// Swap render queues (thread-safe)
		{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_render_queue.swap(m_submit_queue);
		m_submit_queue.clear();
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
}
