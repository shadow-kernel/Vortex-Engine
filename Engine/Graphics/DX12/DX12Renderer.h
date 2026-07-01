#pragma once

#include "../../Common/CommonHeaders.h"
#include "../../Common/Id.h"
#include "DX12Core.h"
#include "DX12CommandQueue.h"
#include "DX12Swapchain.h"
#include "DX12Pipeline.h"
#include "DX12Pipeline3D.h"
#include "DX12GridPipeline.h"
#include "DX12SkyboxPipeline.h"
#include "DX12UpscalePipeline.h"
#include "DX12MotionVectorPipeline.h"
#include "DX12DepthBuffer.h"
#include "DX12RenderTarget.h"
#include "DX12Streamline.h"   // DLSS SR + Frame Generation (inline set_fg_mode/fg_presented_fps below)
#include "DX12Geometry.h"
#include "UIOverlay.h"
#include "../Resources/Mesh.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <vector>
#include <chrono>
#include <mutex>
#include <string>
#include <unordered_map>


namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	// Maximum number of object INSTANCES that can be rendered per frame (render-queue + instance-buffer cap).
	// High so GPU instancing can draw large crowds — e.g. thousands of copies of a multi-submesh model.
	constexpr u32 MAX_RENDER_OBJECTS = 262144;

	// Maximum distinct (mesh, material) DRAW RUNS per frame. This sizes the per-object constant buffer, which
	// holds ONE slot per run (not per instance), so it stays tiny even with a huge instance cap.
	constexpr u32 MAX_DRAW_RUNS = 8192;

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
		// Swap the submit/render queues (thread-safe) WITHOUT presenting. Lets offscreen
		// thumbnail/preview renders pick up a freshly-submitted item without flashing the main
		// swapchain (render_frame both swaps AND presents, which caused the asset-browser white-flash).
		void swap_render_queue();

		// Scene-transition hook: make the GPU idle BEFORE the managed layer frees the old scene's meshes
		// (DeleteMesh has no flush -> in-flight use-after-free) and drop the UI overlay's cached wrapped
		// back-buffer bitmaps + the stale render queue so nothing re-renders freed lobby geometry.
		void on_scene_switch();

		// Capture the NEXT presented frame to a 32-bit BMP at 'path'. Reliable verification: GDI window
		// capture (BitBlt/PrintWindow) reads a FLIP_DISCARD swapchain's stale redirection surface and
		// cannot be trusted — this copies the actual back buffer the GPU produced.
		void request_capture(const char* path);

		// Standalone game window: a SECOND swapchain on its own HWND that shares this renderer's device
		// and command queue (the editor viewport keeps its own swapchain). render_game_window() renders
		// the current scene through the current camera into the game window and presents it. This is the
		// "real exe window" play mode — NOT the offscreen render-target/readback path.
		bool create_game_window(HWND hwnd, u32 width, u32 height);
		void render_game_window();
		void resize_game_window(u32 width, u32 height);
		void destroy_game_window();
		bool is_game_window_active() const { return m_game_window_active; }

		bool is_initialized() const { return m_initialized; }

		// Render queue
		void submit_render_item(const RenderItem& item);
		// Submit `count` instances of the SAME mesh+material in ONE call (world_matrices = count * 16 floats,
		// row-major 4x4 each). Avoids one P/Invoke per instance — the path for spawning large crowds.
		void submit_mesh_instances(id::id_type mesh, id::id_type material, const float* world_matrices, u32 count);
		void clear_render_queue();

		// Camera
		void set_camera(const DirectX::XMFLOAT3& position, const DirectX::XMFLOAT3& target, const DirectX::XMFLOAT3& up);


	// Light settings
		void set_directional_light(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color);
		void set_ambient_strength(float strength);
		
		// Multi-light system
		static constexpr u32 MAX_POINT_LIGHTS = 16;
		static constexpr u32 MAX_SPOT_LIGHTS = 8;
		
		struct PointLightData
		{
			DirectX::XMFLOAT3 position;
			float range;
			DirectX::XMFLOAT3 color;
			float intensity;
		};
		
		struct SpotLightData
		{
			DirectX::XMFLOAT3 position;
			float range;
			DirectX::XMFLOAT3 direction;
			float spot_angle;
			DirectX::XMFLOAT3 color;
			float intensity;
			float inner_spot_angle;
			float padding[3];
		};
		
		void clear_lights();
		void set_directional_light_full(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity);
		void add_point_light(const PointLightData& light);
		void add_spot_light(const SpotLightData& light);

		// Rendering mode
		void set_wireframe_mode(bool enabled) { m_wireframe_mode = enabled; }
		bool is_wireframe_mode() const { return m_wireframe_mode; }

		// VSync control
		void set_vsync(bool enabled) { m_vsync_enabled = enabled; }
		bool is_vsync_enabled() const { return m_vsync_enabled; }

		// Render-scale (3D rendered into a scaled offscreen RT, then upscaled to the back buffer). 1.0 = native.
		// Stored here now (settings UI wires to it); the scaled-RT + upscale pass reads m_render_scale.
		void set_render_scale(float s) { m_render_scale = s < 0.25f ? 0.25f : (s > 2.0f ? 2.0f : s); }
		float render_scale() const { return m_render_scale; }

		// DLSS mode: 0=off, 1=Quality, 2=Balanced, 3=Performance, 4=UltraPerformance. A non-off mode drives the
		// render-scale (the 3D renders at the mode's fraction; DLSS upscales to native in the upscale slot). Off
		// resets render-scale to native. Only has visible effect on DLSS-capable GPUs (else bilinear upscale).
		void set_dlss_mode(int mode)
		{
			m_dlss_mode = (mode < 0 || mode > 4) ? 0 : mode;
			switch (m_dlss_mode)
			{
				case 1: m_render_scale = 0.667f; break; // Quality
				case 2: m_render_scale = 0.580f; break; // Balanced
				case 3: m_render_scale = 0.500f; break; // Performance
				case 4: m_render_scale = 0.333f; break; // Ultra Performance
				default: m_render_scale = 1.0f;  break; // Off -> native
			}
		}
		int dlss_mode() const { return m_dlss_mode; }

		// DLSS Frame Generation (separate from SR above). mode: 0=off, 1=x2, 2=x3, 3=x4 (= N AI frames inserted at
		// Present per real frame). Forces the scaled-RT path on (even at scale 1.0) so depth + motion vectors exist
		// for DLSS-G. Reflex is enabled/disabled with it inside set_frame_gen. Only effective on FG-capable GPUs.
		void set_fg_mode(int mode)
		{
			m_fg_mode = (mode < 0 || mode > 3) ? 0 : mode;
			DX12Streamline::instance().set_frame_gen(m_fg_mode, m_swapchain.width(), m_swapchain.height());
		}
		int fg_mode() const { return m_fg_mode; }
		// Smoothed presented-FPS RATE (real + AI-generated frames/sec), accumulated once per frame in render_frame.
		// 0 when FG is off. This is the "Shown FPS" — the engine's own get_current_fps counts only REAL frames.
		int fg_presented_fps() const { return m_presented_fps; }

		// Per-material custom shaders: bind a .hlsl (VSMain/PSMain) to a material -> the 3D pass uses a per-material
		// PSO instead of the built-in PBR one. hlsl_path empty = clear (revert to built-in). A compile failure keeps
		// the material on the built-in PSO, so a bad custom shader never black-screens.
		void set_material_shader(u32 material_id, const std::wstring& hlsl_path);
		// Hot-reload: recompile any custom material shader whose .hlsl changed on disk (GPU-idle first; keep the last
		// good PSO on failure). Cheap when nothing changed. Call on window focus / before a material-preview render.
		// Returns how many shaders were recompiled this call (0 = nothing changed).
		int reload_dirty_shaders();
		// Cheap (no-compile) check: does any assigned .hlsl differ on disk from what we last compiled? Used so the
		// hot-reload overlay only appears on a REAL change (never a spurious "already up to date").
		bool any_material_shader_dirty() const;

		// Grid rendering
		void set_grid_visible(bool visible) { m_grid_visible = visible; }
		bool is_grid_visible() const { return m_grid_visible; }
		void set_grid_settings(float spacing, float major_interval, float extent);

		// Gizmos
		void set_gizmos_visible(bool visible) { m_gizmos_visible = visible; }
		bool are_gizmos_visible() const { return m_gizmos_visible; }

		// Skybox modes
		enum class SkyboxMode : u32
		{
			SolidColor = 0,
			Gradient = 1,
			Texture = 2
		};

		// Skybox
		void set_skybox_enabled(bool enabled) { m_skybox_enabled = enabled; }
		bool is_skybox_enabled() const { return m_skybox_enabled; }
		void set_skybox_mode(SkyboxMode mode);
		SkyboxMode get_skybox_mode() const { return m_skybox_mode; }
		void set_skybox_colors(
			const DirectX::XMFLOAT3& sky_color,
			const DirectX::XMFLOAT3& horizon_color,
			const DirectX::XMFLOAT3& ground_color);
		void set_skybox_solid_color(const DirectX::XMFLOAT3& color);
		void set_skybox_sun(const DirectX::XMFLOAT3& direction, const DirectX::XMFLOAT3& color, float intensity);

		// Camera projection settings
		void set_projection(float fov_degrees, float aspect, float near_clip, float far_clip);
		// Vertical FOV (degrees) used by the live view camera (editor play + standalone). Settable from the game.
		void set_field_of_view(float fov_degrees) { if (fov_degrees >= 30.0f && fov_degrees <= 120.0f) m_fov_degrees = fov_degrees; }
		float field_of_view() const { return m_fov_degrees; }
		// Generic render-distance cull (world units; 0 = disabled). Set from the game's graphics settings.
		void set_render_distance(float d) { m_render_distance = d >= 0.0f ? d : 0.0f; }
		float render_distance() const { return m_render_distance; }

		// Density LOD: thin out DISTANT instances (keep 1/2 beyond mid, 1/4 beyond far) — the standard crowd/
		// foliage LOD. Deterministic per instance index so there is no per-frame flicker. mid/far are distances.
		void set_lod(bool enabled, float mid, float farD)
		{
			m_lod_enabled = enabled;
			m_lod_mid = mid > 0.0f ? mid : 0.0f;
			m_lod_far = farD > m_lod_mid ? farD : m_lod_mid;
		}

		// GEOMETRIC LOD: distant instances draw a decimated low-poly mesh (the whole crowd stays visible, no
		// thinning/holes). Shares the mid/far distance thresholds. Use INSTEAD of density LOD.
		void set_geometric_lod(bool enabled, float mid, float farD)
		{
			m_geo_lod_enabled = enabled;
			m_lod_mid = mid > 0.0f ? mid : 0.0f;
			m_lod_far = farD > m_lod_mid ? farD : m_lod_mid;
		}

		// Multithreaded culling+packing: parallelize the per-instance frustum/distance test + instance-buffer
		// pack across worker threads (the CPU bottleneck when one mesh is rendered thousands of times). The
		// draw recording stays single-threaded. Auto-gates on instance count; force ignores the threshold.
		void set_multithreading(bool enabled) { m_mt_enabled = enabled; }
		bool is_multithreading() const { return m_mt_enabled; }
		void set_multithreading_force(bool f) { m_mt_force = f; }
		bool mt_active() const { return m_mt_active; }


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

		// 2D UI overlay (generic Direct2D/DirectWrite layer drawn over the 3D; driven by the Vortex.UI
		// script API — the game records panels/text/lines each frame, the engine draws them before present).
		void ui_begin(float w, float h) { m_ui_overlay.begin(w, h); }
		void ui_rect(float x, float y, float w, float h, float r, float g, float b, float a, float radius) { m_ui_overlay.add_rect(x, y, w, h, r, g, b, a, radius); }
		void ui_text(float x, float y, float w, float h, const wchar_t* s, float size, float r, float g, float b, float a, int align, int weight) { m_ui_overlay.add_text(x, y, w, h, s, size, r, g, b, a, align, weight); }
		void ui_line(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thick) { m_ui_overlay.add_line(x1, y1, x2, y2, r, g, b, a, thick); }
		void ui_image(float x, float y, float w, float h, const wchar_t* path, float r, float g, float b, float a) { m_ui_overlay.add_image(x, y, w, h, path, r, g, b, a); }
		void ui_push_clip(float x, float y, float w, float h) { m_ui_overlay.push_clip(x, y, w, h); }
		void ui_pop_clip() { m_ui_overlay.pop_clip(); }

		// Performance statistics
		int get_current_fps() const { return m_current_fps; }
		int get_draw_call_count() const { return m_draw_call_count; }
		int get_vertex_count() const { return m_vertex_count; }
		// Cull stats (per frame): instances examined vs actually drawn — proves cull effectiveness on big maps.
		int get_instances_tested() const { return m_instances_tested; }
		int get_instances_drawn() const { return m_instances_drawn; }

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
		bool create_skybox_resources();
		void update_per_frame_constants();
		void wait_for_previous_frame();

		void render_3d_scene();
		bool capture_backbuffer_to_bmp(const char* path);
		void render_fallback_triangle();
		void render_grid();
		void render_skybox();

		DX12CommandQueue m_command_queue;
		DX12Swapchain m_swapchain;

		// Active render target for the current pass — set to the main swapchain for the editor frame, or
		// to the game window's swapchain in render_game_window(). The render_* helpers target these so the
		// same recording path serves both windows.
		D3D12_CPU_DESCRIPTOR_HANDLE m_active_rtv{};
		D3D12_CPU_DESCRIPTOR_HANDLE m_active_dsv{};
		u32 m_active_width{ 0 };
		u32 m_active_height{ 0 };

		// Standalone game window (second swapchain + depth + allocator; shares device/queue)
		DX12Swapchain m_game_swapchain;
		DX12DepthBuffer m_game_depth;
		ComPtr<ID3D12CommandAllocator> m_game_cmd_allocator;
		bool m_game_window_active{ false };
		DX12Pipeline m_pipeline;           // Simple 2D pipeline (fallback)
		DX12Pipeline3D m_pipeline_3d;      // Full 3D pipeline
		// Custom per-material shaders: material_id -> its compiled PSO + source .hlsl + last-seen mtime (hot-reload).
		struct CustomShader { ComPtr<ID3D12PipelineState> pso; std::wstring path; unsigned long long mtime{ 0 }; };
		std::unordered_map<u32, CustomShader> m_custom_shaders;
		// Shared PSO cache keyed by .hlsl PATH (not material id): many materials + repeated preview rebuilds reuse ONE
		// compiled PSO, so an assigned shader recompiles only when its file's mtime changes (not every orbit frame).
		// Also drives the hot-reload dirty check so the overlay only appears on a REAL change.
		struct CachedPso { ComPtr<ID3D12PipelineState> pso; unsigned long long mtime{ 0 }; };
		std::unordered_map<std::wstring, CachedPso> m_pso_cache;
		ComPtr<ID3D12PipelineState> get_or_compile_pso(const std::wstring& hlsl_path);
		DX12GridPipeline m_grid_pipeline;  // Grid rendering pipeline
		DX12SkyboxPipeline m_skybox_pipeline; // Skybox rendering pipeline
		DX12UpscalePipeline m_upscale;        // Fullscreen upscale (render-scale composite + the DLSS slot)
		DX12RenderTarget m_scaled_rt;         // Offscreen color+depth the 3D renders into when render-scale < 1
		DX12MotionVectorPipeline m_mvec_pipeline; // RG16F velocity pass (DLSS input)
		DX12RenderTarget m_mvec_rt;           // RG16F motion vectors (render res) — DLSS input
		DX12RenderTarget m_dlss_output;       // Full-res DLSS upscaled output (UAV); blitted to the back buffer
		DirectX::XMFLOAT4X4 m_prev_view_projection{}; // previous frame VP (motion vectors + DLSS clipToPrevClip)
		int m_dlss_mode{ 0 };                 // 0=off, 1..4 quality modes
		int m_fg_mode{ 0 };                   // DLSS Frame Generation: 0=off, 1=x2, 2=x3, 3=x4
		DX12DepthBuffer m_depth_buffer;
		DX12Geometry m_geometry;           // Fallback triangle
		UIOverlay m_ui_overlay;            // Direct2D/DirectWrite 2D UI over the 3D


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
			float directional_intensity;
			DirectX::XMFLOAT3 light_color;
			float ambient_strength;
			// Point lights (16 max)
			u32 point_light_count;
			u32 spot_light_count;
			u32 padding1[2];
		};
		
		// Separate light buffer for GPU
		struct alignas(16) GPUPointLight
		{
			DirectX::XMFLOAT3 position;
			float range;
			DirectX::XMFLOAT3 color;
			float intensity;
		};
		
		struct alignas(16) GPUSpotLight
		{
			DirectX::XMFLOAT3 position;
			float range;
			DirectX::XMFLOAT3 direction;
			float spot_angle;
			DirectX::XMFLOAT3 color;
			float intensity;
			float inner_spot_angle;
			float padding[3];
		};
		PerFrameConstants m_frame_constants;

		// Per-object constants - matches shader cbuffer PerObject
		ComPtr<ID3D12Resource> m_per_object_cb;
		void* m_per_object_cb_mapped{ nullptr };
		ComPtr<ID3D12Resource> m_instance_vb;          // per-instance world matrices (GPU instancing)
		void* m_instance_vb_mapped{ nullptr };

		struct alignas(256) PerObjectConstants
		{
		DirectX::XMFLOAT4X4 world;           // 64 bytes
		DirectX::XMFLOAT4 base_color;        // 16 bytes
		float metallic;                       // 4 bytes
		float roughness;                      // 4 bytes
		float ao;                             // 4 bytes
		float normal_strength;                // 4 bytes
		UINT has_albedo_texture;              // 4 bytes
		UINT has_normal_texture;              // 4 bytes
		UINT has_metallic_texture;            // 4 bytes
		UINT has_roughness_texture;           // 4 bytes
	UINT has_ao_texture;                  // 4 bytes
		UINT use_directx_normals;             // 4 bytes
		UINT padding[2];                      // 8 bytes
		};  // Total: 128 bytes, aligned to 256
		
		// Light constant buffer
		ComPtr<ID3D12Resource> m_light_cb;
		void* m_light_cb_mapped{ nullptr };

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

		// One contiguous (mesh,material) run in the sorted render queue. Bounds are precomputed once
		// (single-threaded) so the per-instance cull+pack can run on any worker thread. vbBase reserves a
		// worst-case slab (= item count) in the instance VB so visible instances pack into a disjoint region.
		struct DrawRun
		{
			size_t start; u32 count;
			id::id_type mesh; id::id_type mat;
			Mesh* meshp;
			bool defaultBounds;
			float lcx, lcy, lcz, localR;
			u32 vbBase;
			u32 visible;
			// Geometric LOD (filled at run-build from the mesh's LodChain; lodLevels==1 = no geo-LOD).
			u32 lodLevels{ 1 };
			id::id_type lodMesh[4]{ id::invalid_id, id::invalid_id, id::invalid_id, id::invalid_id };
			float lodT1sq{ 0 }, lodT2sq{ 0 }, lodT3sq{ 0 }; // squared distance thresholds -> LOD1/2/3
			u32 lodCount[4]{ 0, 0, 0, 0 };                  // visible instances per LOD (single-threaded pack)
		};
		std::vector<DrawRun> m_draw_runs;     // per-frame scratch (reused)
		std::vector<u32> m_item_run;          // run index per render-queue item (for flat parallel cull)
		std::vector<unsigned char> m_item_lod; // geometric-LOD level per item (0xFF = culled), for the 2-pass MT cull
		bool m_queue_dirty{ true };           // submit queue changed -> re-sort + rebuild runs; else reuse the cached layout
		bool m_mt_enabled{ true };            // master enable for multithreaded cull+pack
		bool m_mt_force{ false };             // ignore the instance-count threshold (testing/forced)
		bool m_mt_active{ false };            // whether MT was used on the last frame (telemetry)
		int  m_mt_threshold{ 2048 };          // min instances before parallelizing
		u32  m_worker_count{ 0 };             // lazily computed = clamp(hw_concurrency-1, 1, 8)

		// Camera
		DirectX::XMFLOAT3 m_camera_position{ 0.0f, 3.0f, -8.0f };
		DirectX::XMFLOAT3 m_camera_target{ 0.0f, 0.0f, 0.0f };
		DirectX::XMFLOAT3 m_camera_up{ 0.0f, 1.0f, 0.0f };
		
		// Projection parameters
		float m_fov_degrees{ 60.0f };
		float m_aspect_ratio{ 16.0f / 9.0f };
		float m_near_clip{ 0.1f };
		float m_far_clip{ 1000.0f };
		float m_render_distance{ 0.0f };   // generic distance cull (0 = disabled)
		bool  m_lod_enabled{ false };      // density LOD: thin distant instances
		bool  m_geo_lod_enabled{ false };  // geometric LOD: distant instances use a decimated mesh
		float m_lod_mid{ 0.0f };           // density: keep 1/2 beyond this | geometric: -> LOD1 beyond this
		float m_lod_far{ 0.0f };           // density: keep 1/4 beyond this | geometric: -> LOD2 beyond this

	// Lighting - balanced for PBR rendering
		DirectX::XMFLOAT3 m_light_direction{ -0.5f, -0.7f, 0.5f };
		DirectX::XMFLOAT3 m_light_color{ 1.0f, 0.98f, 0.95f };
		float m_ambient_strength{ 0.4f };
		float m_directional_intensity{ 3.0f };
		
		// Multi-light storage
		std::vector<PointLightData> m_point_lights;
		std::vector<SpotLightData> m_spot_lights;

		// Rendering settings - Unity-style gray-brown background (like screenshot)
		FLOAT m_clear_color[4]{ 0.18f, 0.18f, 0.20f, 1.0f };
		bool m_wireframe_mode{ false };
		bool m_grid_visible{ true };
		bool m_gizmos_visible{ true };
		bool m_skybox_enabled{ false };
		SkyboxMode m_skybox_mode{ SkyboxMode::Gradient };
		float m_grid_spacing{ 1.0f };
		float m_grid_major_interval{ 10.0f };
		float m_grid_extent{ 200.0f };
		bool m_vsync_enabled{ false };
		float m_render_scale{ 1.0f };   // 1.0 = render at native res; <1 = scaled offscreen RT + upscale (perf)
		bool m_initialized{ false };

		// Deferred back-buffer capture (set by request_capture, serviced in render_frame before present)
		bool m_capture_requested{ false };
		std::string m_capture_path;

		// Skybox constant buffer
		ComPtr<ID3D12Resource> m_skybox_cb;
		void* m_skybox_cb_mapped{ nullptr };

		// Performance statistics
		int m_current_fps{ 0 };
		int m_draw_call_count{ 0 };
		int m_vertex_count{ 0 };
		int m_instances_tested{ 0 };
		int m_instances_drawn{ 0 };
		int m_frame_count{ 0 };
		int m_presented_accum{ 0 };  // DLSS-G presented frames accumulated since the last rate update
		int m_presented_fps{ 0 };    // smoothed presented-FPS rate (real + AI frames); 0 when FG off
		std::chrono::high_resolution_clock::time_point m_last_fps_time;
		
		// Secondary render targets for multi-viewport rendering
		std::unordered_map<u32, std::unique_ptr<DX12RenderTarget>> m_render_targets;
		u32 m_next_render_target_id{ 1 };
		
		// Helper for rendering to a specific target
		void render_scene_to_target(DX12RenderTarget* target, const ViewportCamera& camera, bool render_grid);

		// Render-scale: (re)create m_scaled_rt at w x h (R8G8B8A8) when the scale/window size changes; idles the
		// GPU first since the RT may be in flight. Returns false if creation failed (caller falls back to direct).
		bool ensure_scaled_rt(u32 width, u32 height);

		// DLSS targets (lazily (re)created): m_mvec_rt at render res (RG16F), m_dlss_output at display res (R8G8B8A8 + UAV).
		bool ensure_mvec_rt(u32 width, u32 height);
		bool ensure_dlss_output(u32 width, u32 height);

		// Create SRV descriptor heap for textures
		bool create_srv_heap();
		
		// SRV Descriptor Heap for textures
		ComPtr<ID3D12DescriptorHeap> m_srv_heap;
		UINT m_srv_descriptor_size{ 0 };
		static constexpr UINT MAX_SRV_DESCRIPTORS = 1024;
	};
}
