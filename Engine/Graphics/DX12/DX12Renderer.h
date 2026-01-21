#pragma once

#include "../../Common/CommonHeaders.h"
#include "../../Common/Id.h"
#include "DX12Core.h"
#include "DX12CommandQueue.h"
#include "DX12Swapchain.h"
#include "DX12Pipeline.h"
#include "DX12Pipeline3D.h"
#include "DX12GridPipeline.h"
#include "DX12DepthBuffer.h"
#include "DX12RenderTarget.h"
#include "DX12Geometry.h"
#include "../Resources/Mesh.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <vector>
#include <chrono>
#include <mutex>
#include <unordered_map>


namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	// Maximum number of objects that can be rendered per frame
	constexpr u32 MAX_RENDER_OBJECTS = 16384;
	
	// Maximum number of secondary render targets
	constexpr u32 MAX_RENDER_TARGETS = 8;

	struct RendererDesc
	{
		HWND hwnd{ nullptr };
		u32 width{ 0 };
		u32 height{ 0 };
	};

	struct RenderItem
	{
		id::id_type mesh_id{ id::invalid_id };
		id::id_type material_id{ id::invalid_id };
		DirectX::XMFLOAT4X4 world_matrix;
	};
	
	/// <summary>
	/// Camera parameters for secondary viewports.
	/// </summary>
	struct ViewportCamera
	{
		DirectX::XMFLOAT3 position{ 0.0f, 10.0f, 0.0f };
		DirectX::XMFLOAT3 target{ 0.0f, 0.0f, 0.0f };
		DirectX::XMFLOAT3 up{ 0.0f, 0.0f, -1.0f };
		float fov_degrees{ 60.0f };
		float near_clip{ 0.1f };
		float far_clip{ 1000.0f };
		bool orthographic{ true };
		float ortho_size{ 20.0f };
	};

	class DX12Renderer
	{
	public:
		static DX12Renderer& instance();

		bool initialize(const RendererDesc& desc);
		void shutdown();
		void resize(u32 width, u32 height);
		void render_frame();

		bool is_initialized() const { return m_initialized; }

		// Render queue
		void submit_render_item(const RenderItem& item);
		void clear_render_queue();

		// Camera
		void set_camera(const DirectX::XMFLOAT3& position, const DirectX::XMFLOAT3& target, const DirectX::XMFLOAT3& up);


		// Light settings
		void set_directional_light(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color);
		void set_ambient_strength(float strength);

		// Rendering mode
		void set_wireframe_mode(bool enabled) { m_wireframe_mode = enabled; }
		bool is_wireframe_mode() const { return m_wireframe_mode; }

		// VSync control
		void set_vsync(bool enabled) { m_vsync_enabled = enabled; }
		bool is_vsync_enabled() const { return m_vsync_enabled; }

		// Grid rendering
		void set_grid_visible(bool visible) { m_grid_visible = visible; }
		bool is_grid_visible() const { return m_grid_visible; }
		void set_grid_settings(float spacing, float major_interval, float extent);

		// Gizmos
		void set_gizmos_visible(bool visible) { m_gizmos_visible = visible; }
		bool are_gizmos_visible() const { return m_gizmos_visible; }

		// Camera projection settings
		void set_projection(float fov_degrees, float aspect, float near_clip, float far_clip);

		// Camera gizmo rendering
		void render_camera_gizmo(
			const DirectX::XMFLOAT3& position,
			const DirectX::XMFLOAT3& forward,
			const DirectX::XMFLOAT3& right,
			const DirectX::XMFLOAT3& up,
			float near_width, float near_height,
			float far_width, float far_height,
			float near_dist, float far_dist,
			const DirectX::XMFLOAT4& color);

		// Performance statistics
		int get_current_fps() const { return m_current_fps; }
		int get_draw_call_count() const { return m_draw_call_count; }
		int get_vertex_count() const { return m_vertex_count; }

		// ============== Multi-Viewport Rendering ==============
		
		/// <summary>
		/// Create a secondary render target for offscreen rendering.
		/// Returns a unique ID for the render target.
		/// </summary>
		u32 create_render_target(u32 width, u32 height);
		
		/// <summary>
		/// Destroy a render target by ID.
		/// </summary>
		void destroy_render_target(u32 target_id);
		
		/// <summary>
		/// Resize a render target.
		/// </summary>
		bool resize_render_target(u32 target_id, u32 width, u32 height);
		
		/// <summary>
		/// Render the scene to a secondary render target with a specific camera.
		/// </summary>
		void render_to_target(u32 target_id, const ViewportCamera& camera, bool render_grid = false);
		
		/// <summary>
		/// Copy render target to staging buffer for CPU readback.
		/// Must be called after render_to_target and before read_render_target_pixels.
		/// </summary>
		bool prepare_render_target_readback(u32 target_id);
		
		/// <summary>
		/// Read pixel data from a render target (after prepare_render_target_readback).
		/// Returns pointer to RGBA8 pixel data, or nullptr if not available.
		/// </summary>
		const void* read_render_target_pixels(u32 target_id, u32& out_width, u32& out_height, u32& out_row_pitch);
		
		/// <summary>
		/// Release the mapped pixel data from read_render_target_pixels.
		/// </summary>
		void release_render_target_pixels(u32 target_id);
		
		/// <summary>
		/// Check if a render target exists.
		/// </summary>
		bool has_render_target(u32 target_id) const;

	private:
		DX12Renderer() = default;
		~DX12Renderer() = default;
		DX12Renderer(const DX12Renderer&) = delete;
		DX12Renderer& operator=(const DX12Renderer&) = delete;

		bool create_command_allocators();
		bool create_command_list();
		bool create_constant_buffers();
		bool create_grid_resources();
		void update_per_frame_constants();
		void wait_for_previous_frame();

		void render_3d_scene();
		void render_fallback_triangle();
		void render_grid();

		DX12CommandQueue m_command_queue;
		DX12Swapchain m_swapchain;
		DX12Pipeline m_pipeline;           // Simple 2D pipeline (fallback)
		DX12Pipeline3D m_pipeline_3d;      // Full 3D pipeline
		DX12GridPipeline m_grid_pipeline;  // Grid rendering pipeline
		DX12DepthBuffer m_depth_buffer;
		DX12Geometry m_geometry;           // Fallback triangle

		// Grid mesh
		ComPtr<ID3D12Resource> m_grid_vertex_buffer;
		D3D12_VERTEX_BUFFER_VIEW m_grid_vbv{};
		u32 m_grid_vertex_count{ 0 };

		// Grid constants
		ComPtr<ID3D12Resource> m_grid_cb;
		void* m_grid_cb_mapped{ nullptr };

		struct alignas(256) GridConstants
		{
			DirectX::XMFLOAT4X4 view_projection;
			DirectX::XMFLOAT4X4 inverse_view_projection;
			DirectX::XMFLOAT3 camera_position;
			float grid_spacing;
			float grid_extent;
			float major_line_interval;
			float padding[2];
		};

		ComPtr<ID3D12CommandAllocator> m_command_allocators[DX12Swapchain::MaxBufferCount];
		ComPtr<ID3D12GraphicsCommandList> m_command_list;
		UINT64 m_frame_fence_values[DX12Swapchain::MaxBufferCount]{};

		// Per-frame constants (camera, lighting)
		ComPtr<ID3D12Resource> m_per_frame_cb;
		void* m_per_frame_cb_mapped{ nullptr };

		struct alignas(256) PerFrameConstants
		{
			DirectX::XMFLOAT4X4 view_projection;
			DirectX::XMFLOAT3 camera_position;
			float padding0;
			DirectX::XMFLOAT3 light_direction;
			float padding1;
			DirectX::XMFLOAT3 light_color;
			float ambient_strength;
		};
		PerFrameConstants m_frame_constants;

		// Per-object constants
		ComPtr<ID3D12Resource> m_per_object_cb;
		void* m_per_object_cb_mapped{ nullptr };

		struct alignas(256) PerObjectConstants
		{
			DirectX::XMFLOAT4X4 world;
			DirectX::XMFLOAT4 base_color;
		};

		// Render queue - double buffered for thread safety
		std::vector<RenderItem> m_render_queue;
		std::vector<RenderItem> m_submit_queue;
		std::mutex m_queue_mutex;
		
		// Batching structures for instanced rendering
		struct RenderBatch
		{
			id::id_type mesh_id;
			id::id_type material_id;
			u32 start_index;
			u32 instance_count;
		};
		std::vector<RenderBatch> m_batches;

		// Camera
		DirectX::XMFLOAT3 m_camera_position{ 0.0f, 3.0f, -8.0f };
		DirectX::XMFLOAT3 m_camera_target{ 0.0f, 0.0f, 0.0f };
		DirectX::XMFLOAT3 m_camera_up{ 0.0f, 1.0f, 0.0f };
		
		// Projection parameters
		float m_fov_degrees{ 60.0f };
		float m_aspect_ratio{ 16.0f / 9.0f };
		float m_near_clip{ 0.1f };
		float m_far_clip{ 1000.0f };

		// Lighting - brighter for better visibility
		DirectX::XMFLOAT3 m_light_direction{ -0.3f, -0.8f, 0.5f };
		DirectX::XMFLOAT3 m_light_color{ 1.0f, 0.98f, 0.95f };
		float m_ambient_strength{ 0.5f };

		// Rendering settings - Unity-style gray-brown background (like screenshot)
		FLOAT m_clear_color[4]{ 0.345f, 0.345f, 0.369f, 1.0f };
		bool m_wireframe_mode{ false };
		bool m_grid_visible{ true };
		bool m_gizmos_visible{ true };
		float m_grid_spacing{ 1.0f };
		float m_grid_major_interval{ 10.0f };
		float m_grid_extent{ 200.0f };
		bool m_vsync_enabled{ false };
		bool m_initialized{ false };

		// Performance statistics
		int m_current_fps{ 0 };
		int m_draw_call_count{ 0 };
		int m_vertex_count{ 0 };
		int m_frame_count{ 0 };
		std::chrono::high_resolution_clock::time_point m_last_fps_time;
		
		// Secondary render targets for multi-viewport rendering
		std::unordered_map<u32, std::unique_ptr<DX12RenderTarget>> m_render_targets;
		u32 m_next_render_target_id{ 1 };
		
		// Helper for rendering to a specific target
		void render_scene_to_target(DX12RenderTarget* target, const ViewportCamera& camera, bool render_grid);
	};
}
