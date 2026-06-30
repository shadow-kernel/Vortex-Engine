#include "../ApiCommon.h"

EDITOR_INTERFACE bool InitializeRenderViewport(void* hwnd, unsigned int width, unsigned int height)
{
	using namespace runtime::systems::dx12;
	viewport_desc desc{};
	desc.hwnd = reinterpret_cast<HWND>(hwnd);
	desc.width = width;
	desc.height = height;
	return initialize(desc);
}

EDITOR_INTERFACE void ResizeRenderViewport(unsigned int width, unsigned int height)
{
	runtime::systems::dx12::resize(width, height);
}

EDITOR_INTERFACE void RenderFrame()
{
	runtime::systems::dx12::render_frame();
}

// Swap the render queue WITHOUT presenting to the main swapchain. Used by offscreen
// thumbnail/preview rendering so it can pick up its submitted item without flashing the
// editor viewport (calling RenderFrame for this caused the asset-browser white-flash).
EDITOR_INTERFACE void SwapRenderQueue()
{
	graphics::dx12::DX12Renderer::instance().swap_render_queue();
}

// Scene-transition hook: GPU idle + drop overlay cache + clear stale queue BEFORE the managed layer
// frees the old scene's meshes (prevents in-flight use-after-free + stale-overlay carryover).
EDITOR_INTERFACE void OnSceneSwitch()
{
	graphics::dx12::DX12Renderer::instance().on_scene_switch();
}

// Reliable frame verification: write the NEXT presented back buffer to a 32-bit BMP (GDI window capture
// reads a stale FLIP_DISCARD redirection surface and cannot be trusted).
EDITOR_INTERFACE void CaptureFrame(const char* path)
{
	graphics::dx12::DX12Renderer::instance().request_capture(path);
}

// ---- Standalone game window: a SECOND DX12 swapchain on its own HWND (shares device/queue). The
// editor keeps its own swapchain; RenderGameWindow renders the current scene through the current
// camera into the game window. This is the real "exe window" play mode. ----
EDITOR_INTERFACE bool CreateGameWindow(void* hwnd, unsigned int width, unsigned int height)
{
	return graphics::dx12::DX12Renderer::instance().create_game_window((HWND)hwnd, width, height);
}

EDITOR_INTERFACE void RenderGameWindow()
{
	graphics::dx12::DX12Renderer::instance().render_game_window();
}

EDITOR_INTERFACE void ResizeGameWindow(unsigned int width, unsigned int height)
{
	graphics::dx12::DX12Renderer::instance().resize_game_window(width, height);
}

EDITOR_INTERFACE void DestroyGameWindow()
{
	graphics::dx12::DX12Renderer::instance().destroy_game_window();
}

EDITOR_INTERFACE bool IsGameWindowActive()
{
	return graphics::dx12::DX12Renderer::instance().is_game_window_active();
}

// ---- 2D UI overlay (generic; driven by the game's Vortex.UI scripting API) ----
EDITOR_INTERFACE void UIBegin(float w, float h)
{
	graphics::dx12::DX12Renderer::instance().ui_begin(w, h);
}
EDITOR_INTERFACE void UIRect(float x, float y, float w, float h, float r, float g, float b, float a, float radius)
{
	graphics::dx12::DX12Renderer::instance().ui_rect(x, y, w, h, r, g, b, a, radius);
}
EDITOR_INTERFACE void UIText(float x, float y, float w, float h, const wchar_t* text,
	float size, float r, float g, float b, float a, int align, int weight)
{
	graphics::dx12::DX12Renderer::instance().ui_text(x, y, w, h, text, size, r, g, b, a, align, weight);
}
EDITOR_INTERFACE void UILine(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thickness)
{
	graphics::dx12::DX12Renderer::instance().ui_line(x1, y1, x2, y2, r, g, b, a, thickness);
}
// 5th UI primitive: a textured quad (PNG/JPG via WIC), tinted by (r,g,b,a). path = absolute or project file.
EDITOR_INTERFACE void UIImage(float x, float y, float w, float h, const wchar_t* path, float r, float g, float b, float a)
{
	graphics::dx12::DX12Renderer::instance().ui_image(x, y, w, h, path, r, g, b, a);
}
EDITOR_INTERFACE void UIPushClip(float x, float y, float w, float h)
{
	graphics::dx12::DX12Renderer::instance().ui_push_clip(x, y, w, h);
}
EDITOR_INTERFACE void UIPopClip()
{
	graphics::dx12::DX12Renderer::instance().ui_pop_clip();
}

EDITOR_INTERFACE void ShutdownRenderViewport()
{
	runtime::systems::dx12::shutdown();
}

// Primitive mesh creation
EDITOR_INTERFACE id::id_type CreatePrimitiveCube(float size)
{
	return graphics::ResourceRegistry::instance().create_primitive_cube(size);
}

EDITOR_INTERFACE id::id_type CreatePrimitiveSphere(float radius)
{
	return graphics::ResourceRegistry::instance().create_primitive_sphere(radius);
}

EDITOR_INTERFACE id::id_type CreateInvertedSphere(float radius)
{
	return graphics::ResourceRegistry::instance().create_inverted_sphere(radius);
}

EDITOR_INTERFACE id::id_type CreatePrimitivePlane(float width, float height)
{
	return graphics::ResourceRegistry::instance().create_primitive_plane(width, height);
}

EDITOR_INTERFACE id::id_type CreatePrimitiveCylinder(float radius, float height)
{
	return graphics::ResourceRegistry::instance().create_primitive_cylinder(radius, height);
}

EDITOR_INTERFACE id::id_type CreatePrimitiveCone(float radius, float height)
{
	return graphics::ResourceRegistry::instance().create_primitive_cone(radius, height);
}

EDITOR_INTERFACE void DestroyMesh(id::id_type mesh_id)
{
	graphics::ResourceRegistry::instance().destroy_mesh(mesh_id);
}

// Mesh bounds query
EDITOR_INTERFACE bool QueryMeshBounds(id::id_type mesh_id, float* sizeX, float* sizeY, float* sizeZ)
{
	auto* mesh = graphics::ResourceRegistry::instance().get_mesh(mesh_id);
	if (!mesh || !sizeX || !sizeY || !sizeZ)
	{
		if (sizeX) *sizeX = 1.0f;
		if (sizeY) *sizeY = 1.0f;
		if (sizeZ) *sizeZ = 1.0f;
		return false;
	}
	
	mesh->get_bounds(*sizeX, *sizeY, *sizeZ);
	return true;
}

EDITOR_INTERFACE bool QueryMeshBoundsCenter(id::id_type mesh_id, float* centerX, float* centerY, float* centerZ)
{
	auto* mesh = graphics::ResourceRegistry::instance().get_mesh(mesh_id);
	if (!mesh || !centerX || !centerY || !centerZ)
	{
		if (centerX) *centerX = 0.0f;
		if (centerY) *centerY = 0.0f;
		if (centerZ) *centerZ = 0.0f;
		return false;
	}
	
	mesh->get_bounds_center(*centerX, *centerY, *centerZ);
	return true;
}

// Material creation
EDITOR_INTERFACE id::id_type CreateMaterial()
{
	return graphics::ResourceRegistry::instance().create_material();
}

EDITOR_INTERFACE void SetMaterialColor(id::id_type material_id, float r, float g, float b, float a)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_base_color({ r, g, b, a });
}

EDITOR_INTERFACE void SetMaterialTexture(id::id_type material_id, id::id_type texture_id)
{
	OutputDebugStringA(("SetMaterialTexture called: material=" + std::to_string(material_id) + ", texture=" + std::to_string(texture_id) + "\n").c_str());
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	auto* tex = graphics::ResourceRegistry::instance().get_texture(texture_id);
	
	OutputDebugStringA(("  mat_ptr=" + std::to_string((size_t)mat) + ", tex_ptr=" + std::to_string((size_t)tex) + "\n").c_str());
	
	if (mat && tex) 
	{
		mat->set_albedo_texture(tex);
		// Verify it was set
		auto* verify = mat->albedo_texture();
		OutputDebugStringA(("  After set: albedo_texture=" + std::to_string((size_t)verify) + 
			", tex_valid=" + std::string(tex->is_valid() ? "YES" : "NO") +
			", srv_ptr=" + std::to_string(tex->srv_gpu().ptr) + "\n").c_str());
	}
	else
	{
		OutputDebugStringA("  ERROR: mat or tex is null!\n");
	}
}

EDITOR_INTERFACE bool MaterialHasTexture(id::id_type material_id)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	return mat && mat->albedo_texture() != nullptr;
}

EDITOR_INTERFACE void DestroyMaterial(id::id_type material_id)
{
	graphics::ResourceRegistry::instance().destroy_material(material_id);
}

// PBR Material texture setters
EDITOR_INTERFACE void SetMaterialNormalTexture(id::id_type material_id, id::id_type texture_id)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	auto* tex = graphics::ResourceRegistry::instance().get_texture(texture_id);
	if (mat && tex) mat->set_normal_texture(tex);
}

EDITOR_INTERFACE void SetMaterialMetallicTexture(id::id_type material_id, id::id_type texture_id)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	auto* tex = graphics::ResourceRegistry::instance().get_texture(texture_id);
	if (mat && tex) mat->set_metallic_texture(tex);
}

EDITOR_INTERFACE void SetMaterialRoughnessTexture(id::id_type material_id, id::id_type texture_id)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	auto* tex = graphics::ResourceRegistry::instance().get_texture(texture_id);
	if (mat && tex) mat->set_roughness_texture(tex);
}

EDITOR_INTERFACE void SetMaterialAOTexture(id::id_type material_id, id::id_type texture_id)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	auto* tex = graphics::ResourceRegistry::instance().get_texture(texture_id);
	if (mat && tex) mat->set_ao_texture(tex);
}

EDITOR_INTERFACE void SetMaterialMetallic(id::id_type material_id, float value)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_metallic(value);
}

EDITOR_INTERFACE void SetMaterialRoughness(id::id_type material_id, float value)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_roughness(value);
}

EDITOR_INTERFACE void SetMaterialNormalStrength(id::id_type material_id, float value)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_normal_strength(value);
}

EDITOR_INTERFACE void SetMaterialAO(id::id_type material_id, float value)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_ao(value);
}

EDITOR_INTERFACE void SetMaterialUseDirectXNormals(id::id_type material_id, bool use_directx)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_use_directx_normals(use_directx);
}

EDITOR_INTERFACE void SetMaterialUnlit(id::id_type material_id, bool is_unlit)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_unlit(is_unlit);
}

EDITOR_INTERFACE void SetMaterialEmissiveStrength(id::id_type material_id, float strength)
{
	auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
	if (mat) mat->set_emissive_strength(strength);
}

// Render item submission
EDITOR_INTERFACE void SubmitRenderItem(id::id_type mesh_id, id::id_type material_id, float* world_matrix)
{
	// Debug first submission
	static bool first_submit = true;
	if (first_submit)
	{
		auto* mat = graphics::ResourceRegistry::instance().get_material(material_id);
		auto* tex = mat ? mat->albedo_texture() : nullptr;
		OutputDebugStringA(("SUBMIT_FIRST: mesh=" + std::to_string(mesh_id) + 
			", material=" + std::to_string(material_id) +
			", mat_ptr=" + std::to_string((size_t)mat) +
			", has_texture=" + (tex ? "YES" : "NO") + "\n").c_str());
		first_submit = false;
	}
	
	graphics::dx12::RenderItem item{};
	item.mesh_id = mesh_id;
	item.material_id = material_id;
	if (world_matrix)
	{
		memcpy(&item.world_matrix, world_matrix, sizeof(DirectX::XMFLOAT4X4));
	}
	else
	{
		DirectX::XMStoreFloat4x4(&item.world_matrix, DirectX::XMMatrixIdentity());
	}
	graphics::dx12::DX12Renderer::instance().submit_render_item(item);
}

// Submit `count` instances of the SAME mesh+material in ONE call (world_matrices = count*16 floats, row-major
// 4x4 each). The renderer groups them into a single DrawIndexedInstanced — the path for spawning large crowds
// without one P/Invoke per instance.
EDITOR_INTERFACE void SubmitMeshInstances(id::id_type mesh_id, id::id_type material_id, const float* world_matrices, int count)
{
	if (!world_matrices || count <= 0) return;
	graphics::dx12::DX12Renderer::instance().submit_mesh_instances(mesh_id, material_id, world_matrices, static_cast<u32>(count));
}

// Camera control
EDITOR_INTERFACE void SetCamera(float pos_x, float pos_y, float pos_z,
								float target_x, float target_y, float target_z,
								float up_x, float up_y, float up_z)
{
	graphics::dx12::DX12Renderer::instance().set_camera(
		{ pos_x, pos_y, pos_z },
		{ target_x, target_y, target_z },
		{ up_x, up_y, up_z });
}

// Vertical FOV (degrees) of the live view camera — driven by the game's FOV setting.
EDITOR_INTERFACE void SetViewFieldOfView(float fov_degrees)
{
	graphics::dx12::DX12Renderer::instance().set_field_of_view(fov_degrees);
}

EDITOR_INTERFACE float GetViewFieldOfView()
{
	return graphics::dx12::DX12Renderer::instance().field_of_view();
}

// Grid and Gizmo control
EDITOR_INTERFACE void SetGridVisible(bool visible)
{
	graphics::dx12::DX12Renderer::instance().set_grid_visible(visible);
}

EDITOR_INTERFACE void SetGridSettings(float spacing, float major_line_interval, float extent)
{
	graphics::dx12::DX12Renderer::instance().set_grid_settings(spacing, major_line_interval, extent);
}

EDITOR_INTERFACE void SetGizmosVisible(bool visible)
{
	graphics::dx12::DX12Renderer::instance().set_gizmos_visible(visible);
}

EDITOR_INTERFACE bool IsGridVisible()
{
	return graphics::dx12::DX12Renderer::instance().is_grid_visible();
}

EDITOR_INTERFACE bool AreGizmosVisible()
{
	return graphics::dx12::DX12Renderer::instance().are_gizmos_visible();
}

// Rendering mode
EDITOR_INTERFACE void SetWireframeMode(bool enabled)
{
	graphics::dx12::DX12Renderer::instance().set_wireframe_mode(enabled);
}

EDITOR_INTERFACE bool IsWireframeMode()
{
	return graphics::dx12::DX12Renderer::instance().is_wireframe_mode();
}

// VSync control
EDITOR_INTERFACE void SetVSync(bool enabled)
{
	graphics::dx12::DX12Renderer::instance().set_vsync(enabled);
}

EDITOR_INTERFACE bool IsVSyncEnabled()
{
	return graphics::dx12::DX12Renderer::instance().is_vsync_enabled();
}

// Gizmo mesh creation
EDITOR_INTERFACE id::id_type CreateGizmoArrow(float length, float radius)
{
	// Create arrow mesh (cylinder + cone)
	auto& reg = graphics::ResourceRegistry::instance();
	return reg.create_primitive_cone(radius * 2, length * 0.3f);
}

EDITOR_INTERFACE id::id_type CreateGizmoCylinder(float length, float radius)
{
	auto& reg = graphics::ResourceRegistry::instance();
	return reg.create_primitive_cylinder(radius, length);
}

// Performance statistics
EDITOR_INTERFACE int GetCurrentFPS()
{
	// Prefer RenderLoop FPS if running, otherwise use Renderer FPS
	auto& loop = runtime::RenderLoop::instance();
	if (loop.is_running())
		return loop.get_current_fps();
	return graphics::dx12::DX12Renderer::instance().get_current_fps();
}

EDITOR_INTERFACE int GetDrawCallCount()
{
	return graphics::dx12::DX12Renderer::instance().get_draw_call_count();
}

EDITOR_INTERFACE int GetVertexCount()
{
	return graphics::dx12::DX12Renderer::instance().get_vertex_count();
}

EDITOR_INTERFACE int GetInstancesTested()
{
	return graphics::dx12::DX12Renderer::instance().get_instances_tested();
}

EDITOR_INTERFACE int GetInstancesDrawn()
{
	return graphics::dx12::DX12Renderer::instance().get_instances_drawn();
}

// Generic render-distance cull (world units; 0 = disabled). Driven by the game's graphics settings.
EDITOR_INTERFACE void SetRenderDistance(float distance)
{
	graphics::dx12::DX12Renderer::instance().set_render_distance(distance);
}

// Density LOD: thin distant instances (1/2 beyond mid, 1/4 beyond far world units). enabled=false disables.
EDITOR_INTERFACE void SetLOD(bool enabled, float mid, float farD)
{
	graphics::dx12::DX12Renderer::instance().set_lod(enabled, mid, farD);
}

// Geometric LOD: distant instances draw a decimated low-poly mesh (whole crowd visible, no holes). mid/far = the
// distances at which LOD1/LOD2 kick in.
EDITOR_INTERFACE void SetGeometricLOD(bool enabled, float mid, float farD)
{
	graphics::dx12::DX12Renderer::instance().set_geometric_lod(enabled, mid, farD);
}

// Multithreaded per-instance cull+pack (auto-gates on instance count; the draw recording stays single-threaded).
EDITOR_INTERFACE void SetMultithreading(bool enabled)
{
	graphics::dx12::DX12Renderer::instance().set_multithreading(enabled);
}
EDITOR_INTERFACE void SetMultithreadingForce(bool force)
{
	graphics::dx12::DX12Renderer::instance().set_multithreading_force(force);
}
EDITOR_INTERFACE bool IsMultithreadingActive()
{
	return graphics::dx12::DX12Renderer::instance().mt_active();
}

// ============== RENDER LOOP API ==============

// ---- Native GameHost: the standalone game runs in its OWN native Win32 window + DX12 swapchain + loop,
// all on one thread (no WPF HwndHost). Fixes the cross-thread Present-freeze and uncaps FPS. ----
