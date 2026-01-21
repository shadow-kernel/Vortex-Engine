#pragma once

#include "../../Common/CommonHeaders.h"
#include "DX12RenderTarget.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <DirectXMath.h>
#include <vector>
#include <unordered_map>
#include <mutex>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;
	
	/// <summary>
	/// Camera parameters for viewport rendering.
	/// </summary>
	struct ViewportCameraParams
	{
		DirectX::XMFLOAT3 position{ 0.0f, 10.0f, 0.0f };
		DirectX::XMFLOAT3 target{ 0.0f, 0.0f, 0.0f };
		DirectX::XMFLOAT3 up{ 0.0f, 1.0f, 0.0f };
		float fov_degrees{ 60.0f };
		float near_clip{ 0.1f };
		float far_clip{ 1000.0f };
		bool orthographic{ false };
		float ortho_size{ 20.0f };
	};
	
	/// <summary>
	/// Pending render request for a viewport.
	/// </summary>
	struct ViewportRenderRequest
	{
		u32 target_id{ 0 };
		ViewportCameraParams camera;
		bool render_grid{ false };
		bool needs_readback{ true };
	};
	
	/// <summary>
	/// Result of a viewport render that can be read by the CPU.
	/// </summary>
	struct ViewportReadbackResult
	{
		const void* pixel_data{ nullptr };
		u32 width{ 0 };
		u32 height{ 0 };
		u32 row_pitch{ 0 };
		bool is_valid{ false };
	};

	/// <summary>
	/// High-performance multi-viewport render system.
	/// 
	/// Design principles:
	/// - Batched rendering: All viewports rendered in a single command buffer
	/// - Asynchronous readback: GPU copy happens in parallel with CPU work
	/// - Minimal synchronization: Only one fence wait per batch
	/// 
	/// Separation of Concerns:
	/// - This class ONLY handles secondary viewport rendering
	/// - Main viewport rendering is handled by DX12Renderer
	/// - Resource creation/destruction is handled by DX12RenderTarget
	/// </summary>
	class DX12MultiViewportRenderer
	{
	public:
		static DX12MultiViewportRenderer& instance();
		
		/// <summary>
		/// Initialize the multi-viewport system.
		/// Must be called after DX12Renderer is initialized.
		/// </summary>
		bool initialize(ID3D12Device* device, ID3D12CommandQueue* queue);
		
		/// <summary>
		/// Shutdown and release all resources.
		/// </summary>
		void shutdown();
		
		/// <summary>
		/// Create a viewport with specified dimensions.
		/// Returns a unique viewport ID.
		/// </summary>
		u32 create_viewport(u32 width, u32 height);
		
		/// <summary>
		/// Destroy a viewport by ID.
		/// </summary>
		void destroy_viewport(u32 viewport_id);
		
		/// <summary>
		/// Resize a viewport.
		/// </summary>
		bool resize_viewport(u32 viewport_id, u32 width, u32 height);
		
		/// <summary>
		/// Queue a viewport for rendering this frame.
		/// </summary>
		void queue_render(const ViewportRenderRequest& request);
		
		/// <summary>
		/// Execute all queued viewport renders in a single batch.
		/// This should be called once per frame after the main render.
		/// </summary>
		void execute_queued_renders();
		
		/// <summary>
		/// Get the readback result for a viewport.
		/// </summary>
		ViewportReadbackResult get_readback(u32 viewport_id);
		
		/// <summary>
		/// Release a readback result (must be called after get_readback).
		/// </summary>
		void release_readback(u32 viewport_id);
		
		/// <summary>
		/// Check if a viewport exists.
		/// </summary>
		bool has_viewport(u32 viewport_id) const;
		
		/// <summary>
		/// Set the per-frame constant buffer (shared with main renderer).
		/// </summary>
		void set_frame_constants_buffer(ID3D12Resource* buffer, void* mapped_ptr);
		
		/// <summary>
		/// Set the per-object constant buffer (shared with main renderer).
		/// </summary>
		void set_object_constants_buffer(ID3D12Resource* buffer, void* mapped_ptr);
		
		/// <summary>
		/// Set the grid constant buffer (shared with main renderer).
		/// </summary>
		void set_grid_constants_buffer(ID3D12Resource* buffer, void* mapped_ptr);
		
	private:
		DX12MultiViewportRenderer() = default;
		~DX12MultiViewportRenderer() = default;
		DX12MultiViewportRenderer(const DX12MultiViewportRenderer&) = delete;
		DX12MultiViewportRenderer& operator=(const DX12MultiViewportRenderer&) = delete;
		
		bool create_command_resources();
		void render_single_viewport(DX12RenderTarget* target, const ViewportRenderRequest& request);
		
		// Core D3D12 resources
		ID3D12Device* m_device{ nullptr };
		ID3D12CommandQueue* m_queue{ nullptr };
		ComPtr<ID3D12CommandAllocator> m_command_allocators[2];
		ComPtr<ID3D12GraphicsCommandList> m_command_list;
		ComPtr<ID3D12Fence> m_fence;
		UINT64 m_fence_value{ 0 };
		HANDLE m_fence_event{ nullptr };
		
		// Viewports
		struct ViewportData
		{
			std::unique_ptr<DX12RenderTarget> render_target;
			bool pending_readback{ false };
		};
		std::unordered_map<u32, ViewportData> m_viewports;
		u32 m_next_viewport_id{ 1 };
		
		// Render queue
		std::vector<ViewportRenderRequest> m_render_queue;
		std::mutex m_queue_mutex;
		
		// Shared constant buffers (owned by DX12Renderer)
		ID3D12Resource* m_frame_cb{ nullptr };
		void* m_frame_cb_mapped{ nullptr };
		ID3D12Resource* m_object_cb{ nullptr };
		void* m_object_cb_mapped{ nullptr };
		ID3D12Resource* m_grid_cb{ nullptr };
		void* m_grid_cb_mapped{ nullptr };
		
		// Frame tracking
		u32 m_current_frame_index{ 0 };
		bool m_initialized{ false };
	};
}
