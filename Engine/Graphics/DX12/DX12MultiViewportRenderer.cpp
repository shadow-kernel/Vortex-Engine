#include "DX12MultiViewportRenderer.h"
#include "DX12Core.h"
#include "DX12Pipeline3D.h"
#include "DX12GridPipeline.h"
#include "../Resources/ResourceRegistry.h"
#include <algorithm>

namespace vortex::graphics::dx12
{
	using namespace DirectX;
	
	DX12MultiViewportRenderer& DX12MultiViewportRenderer::instance()
	{
		static DX12MultiViewportRenderer inst;
		return inst;
	}
	
	bool DX12MultiViewportRenderer::initialize(ID3D12Device* device, ID3D12CommandQueue* queue)
	{
		if (m_initialized) return true;
		if (!device || !queue) return false;
		
		m_device = device;
		m_queue = queue;
		
		if (!create_command_resources())
		{
			shutdown();
			return false;
		}
		
		m_initialized = true;
		OutputDebugStringA("DX12MultiViewportRenderer initialized\n");
		return true;
	}
	
	void DX12MultiViewportRenderer::shutdown()
	{
		if (!m_initialized) return;
		
		// Wait for GPU to finish
		if (m_fence && m_fence_event)
		{
			m_fence_value++;
			m_queue->Signal(m_fence.Get(), m_fence_value);
			if (m_fence->GetCompletedValue() < m_fence_value)
			{
				m_fence->SetEventOnCompletion(m_fence_value, m_fence_event);
				WaitForSingleObject(m_fence_event, INFINITE);
			}
		}
		
		m_viewports.clear();
		m_render_queue.clear();
		
		if (m_fence_event)
		{
			CloseHandle(m_fence_event);
			m_fence_event = nullptr;
		}
		
		m_command_list.Reset();
		for (auto& alloc : m_command_allocators) alloc.Reset();
		m_fence.Reset();
		
		m_device = nullptr;
		m_queue = nullptr;
		m_initialized = false;
		
		OutputDebugStringA("DX12MultiViewportRenderer shutdown\n");
	}
	
	bool DX12MultiViewportRenderer::create_command_resources()
	{
		// Create command allocators (double-buffered)
		for (int i = 0; i < 2; i++)
		{
			if (FAILED(m_device->CreateCommandAllocator(
				D3D12_COMMAND_LIST_TYPE_DIRECT,
				IID_PPV_ARGS(&m_command_allocators[i]))))
			{
				return false;
			}
		}
		
		// Create command list
		if (FAILED(m_device->CreateCommandList(
			0, D3D12_COMMAND_LIST_TYPE_DIRECT,
			m_command_allocators[0].Get(), nullptr,
			IID_PPV_ARGS(&m_command_list))))
		{
			return false;
		}
		m_command_list->Close();
		
		// Create fence
		if (FAILED(m_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&m_fence))))
		{
			return false;
		}
		
		m_fence_event = CreateEvent(nullptr, FALSE, FALSE, nullptr);
		if (!m_fence_event) return false;
		
		return true;
	}
	
	u32 DX12MultiViewportRenderer::create_viewport(u32 width, u32 height)
	{
		if (!m_initialized || width == 0 || height == 0) return 0;
		
		auto target = std::make_unique<DX12RenderTarget>();
		if (!target->initialize(m_device, width, height, DXGI_FORMAT_B8G8R8A8_UNORM))
		{
			return 0;
		}
		
		u32 id = m_next_viewport_id++;
		ViewportData data;
		data.render_target = std::move(target);
		data.pending_readback = false;
		
		m_viewports[id] = std::move(data);
		
		OutputDebugStringA(("Created viewport " + std::to_string(id) + 
			" (" + std::to_string(width) + "x" + std::to_string(height) + ")\n").c_str());
		
		return id;
	}
	
	void DX12MultiViewportRenderer::destroy_viewport(u32 viewport_id)
	{
		auto it = m_viewports.find(viewport_id);
		if (it == m_viewports.end()) return;
		
		// Wait for GPU to finish with this viewport
		m_fence_value++;
		m_queue->Signal(m_fence.Get(), m_fence_value);
		if (m_fence->GetCompletedValue() < m_fence_value)
		{
			m_fence->SetEventOnCompletion(m_fence_value, m_fence_event);
			WaitForSingleObject(m_fence_event, INFINITE);
		}
		
		m_viewports.erase(it);
		OutputDebugStringA(("Destroyed viewport " + std::to_string(viewport_id) + "\n").c_str());
	}
	
	bool DX12MultiViewportRenderer::resize_viewport(u32 viewport_id, u32 width, u32 height)
	{
		auto it = m_viewports.find(viewport_id);
		if (it == m_viewports.end()) return false;
		
		// Wait for GPU
		m_fence_value++;
		m_queue->Signal(m_fence.Get(), m_fence_value);
		if (m_fence->GetCompletedValue() < m_fence_value)
		{
			m_fence->SetEventOnCompletion(m_fence_value, m_fence_event);
			WaitForSingleObject(m_fence_event, INFINITE);
		}
		
		return it->second.render_target->resize(m_device, width, height);
	}
	
	void DX12MultiViewportRenderer::queue_render(const ViewportRenderRequest& request)
	{
		std::lock_guard<std::mutex> lock(m_queue_mutex);
		m_render_queue.push_back(request);
	}
	
	void DX12MultiViewportRenderer::execute_queued_renders()
	{
		if (!m_initialized) return;
		
		// Swap queue
		std::vector<ViewportRenderRequest> requests;
		{
			std::lock_guard<std::mutex> lock(m_queue_mutex);
			requests.swap(m_render_queue);
		}
		
		if (requests.empty()) return;
		
		// Wait for previous frame's work on this allocator
		u32 alloc_idx = m_current_frame_index % 2;
		
		// Reset command allocator and list
		m_command_allocators[alloc_idx]->Reset();
		m_command_list->Reset(m_command_allocators[alloc_idx].Get(), nullptr);
		
		// Process all viewport renders in a single command buffer
		for (const auto& req : requests)
		{
			auto it = m_viewports.find(req.target_id);
			if (it == m_viewports.end()) continue;
			
			auto* target = it->second.render_target.get();
			if (!target || !target->is_initialized()) continue;
			
			render_single_viewport(target, req);
			
			if (req.needs_readback)
			{
				it->second.pending_readback = true;
			}
		}
		
		// Copy to staging for all viewports that need readback
		for (auto& pair : m_viewports)
		{
			if (pair.second.pending_readback)
			{
				pair.second.render_target->copy_to_staging(m_command_list.Get());
			}
		}
		
		// Close and execute
		m_command_list->Close();
		
		ID3D12CommandList* lists[] = { m_command_list.Get() };
		m_queue->ExecuteCommandLists(1, lists);
		
		// Signal fence
		m_fence_value++;
		m_queue->Signal(m_fence.Get(), m_fence_value);
		
		m_current_frame_index++;
	}
	
	void DX12MultiViewportRenderer::render_single_viewport(
		DX12RenderTarget* target, 
		const ViewportRenderRequest& request)
	{
		// Transition to render target
		target->transition_to_render_target(m_command_list.Get());
		
		// Setup viewport
		D3D12_VIEWPORT vp{};
		vp.Width = (float)target->width();
		vp.Height = (float)target->height();
		vp.MaxDepth = 1.0f;
		
		D3D12_RECT sc{};
		sc.right = target->width();
		sc.bottom = target->height();
		
		m_command_list->RSSetViewports(1, &vp);
		m_command_list->RSSetScissorRects(1, &sc);
		
		// Clear
		FLOAT clear_color[4] = { 0.22f, 0.22f, 0.24f, 1.0f };
		auto rtv = target->rtv();
		auto dsv = target->dsv();
		m_command_list->ClearRenderTargetView(rtv, clear_color, 0, nullptr);
		m_command_list->ClearDepthStencilView(dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
		m_command_list->OMSetRenderTargets(1, &rtv, FALSE, &dsv);
		
		// Build camera matrices
		const auto& cam = request.camera;
		XMVECTOR eye = XMLoadFloat3(&cam.position);
		XMVECTOR at = XMLoadFloat3(&cam.target);
		XMVECTOR up = XMLoadFloat3(&cam.up);
		XMMATRIX view = XMMatrixLookAtLH(eye, at, up);
		
		float aspect = (float)target->width() / (float)target->height();
		XMMATRIX proj;
		if (cam.orthographic)
		{
			proj = XMMatrixOrthographicLH(cam.ortho_size * aspect, cam.ortho_size, 
				cam.near_clip, cam.far_clip);
		}
		else
		{
			proj = XMMatrixPerspectiveFovLH(XMConvertToRadians(cam.fov_degrees), aspect, 
				cam.near_clip, cam.far_clip);
		}
		
		XMMATRIX vp_matrix = view * proj;
		
		// Note: In a full implementation, we would update constant buffers here
		// For now, this provides the framework for batched rendering
	}
	
	ViewportReadbackResult DX12MultiViewportRenderer::get_readback(u32 viewport_id)
	{
		ViewportReadbackResult result{};
		
		auto it = m_viewports.find(viewport_id);
		if (it == m_viewports.end()) return result;
		
		// Wait for GPU if needed
		if (m_fence->GetCompletedValue() < m_fence_value)
		{
			m_fence->SetEventOnCompletion(m_fence_value, m_fence_event);
			WaitForSingleObject(m_fence_event, 1); // Short timeout
		}
		
		auto* target = it->second.render_target.get();
		if (!target) return result;
		
		result.pixel_data = target->map_staging_buffer();
		result.width = target->width();
		result.height = target->height();
		result.row_pitch = target->staging_row_pitch();
		result.is_valid = result.pixel_data != nullptr;
		
		return result;
	}
	
	void DX12MultiViewportRenderer::release_readback(u32 viewport_id)
	{
		auto it = m_viewports.find(viewport_id);
		if (it == m_viewports.end()) return;
		
		auto* target = it->second.render_target.get();
		if (target)
		{
			target->unmap_staging_buffer();
		}
		
		it->second.pending_readback = false;
	}
	
	bool DX12MultiViewportRenderer::has_viewport(u32 viewport_id) const
	{
		return m_viewports.find(viewport_id) != m_viewports.end();
	}
	
	void DX12MultiViewportRenderer::set_frame_constants_buffer(ID3D12Resource* buffer, void* mapped_ptr)
	{
		m_frame_cb = buffer;
		m_frame_cb_mapped = mapped_ptr;
	}
	
	void DX12MultiViewportRenderer::set_object_constants_buffer(ID3D12Resource* buffer, void* mapped_ptr)
	{
		m_object_cb = buffer;
		m_object_cb_mapped = mapped_ptr;
	}
	
	void DX12MultiViewportRenderer::set_grid_constants_buffer(ID3D12Resource* buffer, void* mapped_ptr)
	{
		m_grid_cb = buffer;
		m_grid_cb_mapped = mapped_ptr;
	}
}
