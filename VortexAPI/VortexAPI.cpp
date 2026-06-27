#ifndef EDITOR_INTERFACE
#define EDITOR_INTERFACE extern "C" __declspec(dllexport)
#endif // !EDITOR_INTERFACE

#include "CommonHeaders.h"
#include "Id.h"
#include "..\Engine\Components\Entity.h"
#include "..\Engine\Components\Transform.h"
#include "..\Engine\Components\MeshRenderer.h"
#include "..\Engine\Components\Skybox.h"
#include "..\Engine\Runtime\SceneManager.h"
#include "..\Engine\Runtime\ResourceManager.h"
#include "..\Engine\Runtime\AssetDatabase.h"
#include "..\Engine\Runtime\PrefabService.h"
#include "..\Engine\Runtime\RenderLoop.h"
#include "..\Engine\Runtime\Systems\RenderSystem.h"
#include "..\Engine\Runtime\Systems\RenderSystemDX12.h"
#include "..\Engine\Runtime\Systems\PhysicsSystem.h"
#include "..\Engine\Runtime\Systems\AudioSystem.h"
#include "..\Engine\Graphics\Resources\ResourceRegistry.h"
#include "..\Engine\Graphics\Importers\ModelImporter.h"
#include "..\Engine\Graphics\DX12\DX12Renderer.h"
#include "..\Engine\Input\InputSystem.h"
#include "..\Engine\Components\Camera.h"

using namespace vortex;

namespace {

	struct transform_component
	{
		f32 position[3];
		f32 rotation[3];
		f32 scale[3];

		transform::init_info to_init_info()
		{
			using namespace DirectX;
			transform::init_info info{};

			memcpy(&info.position[0], &position[0], sizeof(f32) * _countof(position));
			memcpy(&info.scale[0], &scale[0], sizeof(f32) * _countof(scale));

			XMFLOAT3A rot{ &rotation[0] };
			XMVECTOR quat{ XMQuaternionRotationRollPitchYawFromVector(XMLoadFloat3A(&rot)) };
			XMFLOAT4A rotation_quat{};
			XMStoreFloat4A(&rotation_quat, quat);

			memcpy(&info.rotation[0], &rotation_quat.x, sizeof(f32) * _countof(info.rotation));

			return info;
		}
	};

	struct game_entity_descriptor
	{
		transform_component transform;
	};

struct prefab_descriptor
{
	const char* path;
};

struct resource_descriptor
{
	const char* path;
};

	game_entity::entity entity_from_id(id::id_type id)
	{
		return game_entity::entity{ game_entity::entity_id{ id } };
	}
}

EDITOR_INTERFACE id::id_type CreateGameEntity(game_entity_descriptor* descriptor)
{
	assert(descriptor);
	game_entity_descriptor& desc{ *descriptor };
	transform::init_info transform_info{ desc.transform.to_init_info() };
	game_entity::entity_info entity_info
	{
		&transform_info
	};

	return game_entity::create_game_entity(entity_info).get_id();
}

EDITOR_INTERFACE void RemoveGameEntity(id::id_type id)
{
	assert(id::is_valid(id));
	game_entity::remove_game_entity(entity_from_id(id));
}

// Push an updated transform onto an existing engine entity so the engine-side transform stays
// authoritative/live. Uses the SAME descriptor->to_init_info() conversion as CreateGameEntity, so
// create and update agree exactly.
EDITOR_INTERFACE void SetGameEntityTransform(id::id_type entity_id, game_entity_descriptor* descriptor)
{
	if (!id::is_valid(entity_id) || !descriptor) return;
	const game_entity::entity entity{ entity_from_id(entity_id) };
	if (!game_entity::is_alive(entity)) return;

	transform::init_info transform_info{ descriptor->transform.to_init_info() };
	transform::set_transform(entity, transform_info);
}

// Register an entity as a gravity-affected dynamic body (with an AABB half-extent) for the play tick.
EDITOR_INTERFACE void SetEntityRigidbody(id::id_type entity_id, bool use_gravity, float hx, float hy, float hz)
{
	if (!id::is_valid(entity_id)) return;
	runtime::systems::set_rigidbody(entity_id, use_gravity, hx, hy, hz);
}

EDITOR_INTERFACE void ClearRigidbodies()
{
	runtime::systems::clear_rigidbodies();
}

// --- Collision world ---
EDITOR_INTERFACE void RegisterStaticBox(float cx, float cy, float cz, float hx, float hy, float hz)
{
	runtime::systems::register_static_box(cx, cy, cz, hx, hy, hz);
}

EDITOR_INTERFACE void ClearColliders()
{
	runtime::systems::clear_colliders();
}

// --- Player character (the play-mode camera body) ---
EDITOR_INTERFACE void CharacterInit(float x, float y, float z, float hx, float hy, float hz)
{
	runtime::systems::character_init(x, y, z, hx, hy, hz);
}

EDITOR_INTERFACE void CharacterMove(float wish_x, float wish_z, bool jump, float dt)
{
	runtime::systems::character_move(wish_x, wish_z, jump, dt);
}

EDITOR_INTERFACE void CharacterGetPosition(float* out_xyz)
{
	runtime::systems::character_get_position(out_xyz);
}

EDITOR_INTERFACE bool CharacterGrounded()
{
	return runtime::systems::character_grounded();
}

// Read an entity's current world-ish position (the runtime authority during play) so the editor
// can mirror it into its C# transform for display.
EDITOR_INTERFACE void GetEntityPosition(id::id_type entity_id, float* out_xyz)
{
	if (!out_xyz) return;
	out_xyz[0] = out_xyz[1] = out_xyz[2] = 0.0f;
	const game_entity::entity entity{ entity_from_id(entity_id) };
	if (!game_entity::is_alive(entity)) return;
	const auto pos = entity.transform().position();
	out_xyz[0] = pos.x; out_xyz[1] = pos.y; out_xyz[2] = pos.z;
}

EDITOR_INTERFACE id::id_type CreateScene()
{
	return runtime::scene_manager::create_scene();
}

EDITOR_INTERFACE void DestroyScene(id::id_type id)
{
	runtime::scene_manager::destroy_scene(runtime::scene_manager::scene_id{ id });
}

EDITOR_INTERFACE void ActivateScene(id::id_type id)
{
	runtime::scene_manager::activate_scene(runtime::scene_manager::scene_id{ id });
}

EDITOR_INTERFACE void DeactivateScene(id::id_type id)
{
	runtime::scene_manager::deactivate_scene(runtime::scene_manager::scene_id{ id });
}

EDITOR_INTERFACE id::id_type CreateGameEntityInScene(id::id_type scene_id, game_entity_descriptor* descriptor)
{
	if (!descriptor) return id::invalid_id;
	transform::init_info transform_info{ descriptor->transform.to_init_info() };
	const auto entity = runtime::scene_manager::create_entity(runtime::scene_manager::scene_id{ scene_id }, transform_info);
	return entity.get_id();
}

EDITOR_INTERFACE void RemoveGameEntityInScene(id::id_type scene_id, id::id_type entity_id)
{
	const auto entity = entity_from_id(entity_id);
	runtime::scene_manager::remove_entity(runtime::scene_manager::scene_id{ scene_id }, entity);
}

// ResourceManager
EDITOR_INTERFACE id::id_type LoadMesh(const char* path)
{
	return runtime::resource_manager::load_mesh(path).value;
}

EDITOR_INTERFACE id::id_type LoadTexture(const char* path)
{
	return runtime::resource_manager::load_texture(path).value;
}

EDITOR_INTERFACE id::id_type LoadMaterial(const char* path)
{
	return runtime::resource_manager::load_material(path).value;
}

EDITOR_INTERFACE id::id_type LoadShader(const char* path)
{
	return runtime::resource_manager::load_shader(path).value;
}

EDITOR_INTERFACE id::id_type LoadAudio(const char* path)
{
	return runtime::resource_manager::load_audio(path).value;
}

EDITOR_INTERFACE void UnloadResource(id::id_type handle)
{
	runtime::resource_manager::unload(runtime::resource_manager::resource_handle{ handle });
}

// PrefabService
EDITOR_INTERFACE id::id_type LoadPrefab(const char* path)
{
	return runtime::prefab_service::load_prefab(path).value;
}

EDITOR_INTERFACE id::id_type InstantiatePrefab(id::id_type /*scene_id*/, id::id_type prefab_handle, game_entity_descriptor* descriptor)
{
	if (!descriptor) return id::invalid_id;
	transform::init_info transform_info{ descriptor->transform.to_init_info() };
	const auto entity = runtime::prefab_service::instantiate(runtime::prefab_service::prefab_handle{ prefab_handle }, transform_info);
	return entity.get_id();
}

EDITOR_INTERFACE void UnloadPrefab(id::id_type prefab_handle)
{
	runtime::prefab_service::unload(runtime::prefab_service::prefab_handle{ prefab_handle });
}

EDITOR_INTERFACE void InitializeRuntime()
{
	runtime::resource_manager::initialize();
	runtime::prefab_service::initialize();
	runtime::systems::initialize_render();
	runtime::systems::initialize_physics();
	runtime::systems::initialize_audio();
}

EDITOR_INTERFACE void ShutdownRuntime()
{
	runtime::systems::shutdown_audio();
	runtime::systems::shutdown_physics();
	runtime::systems::shutdown_render();
	runtime::prefab_service::shutdown();
	runtime::resource_manager::shutdown();
}

// Advances the whole game simulation by dt seconds (one game tick).
// Call this once per frame while in play mode / from the standalone player,
// before submitting render items. The editor's idle viewport does NOT call it,
// which is exactly why entering play mode "comes alive" and exiting it freezes.
namespace
{
	// Fixed-timestep game clock: the simulation always advances in stable 1/60 s steps regardless of
	// render frame rate (deterministic physics), with an accumulator for leftover time. g_game_time is
	// the elapsed in-game seconds since the last ResetGameTime (Play start).
	constexpr float k_fixed_dt = 1.0f / 60.0f;
	float g_time_accumulator = 0.0f;
	float g_game_time = 0.0f;
}

EDITOR_INTERFACE void StepRuntime(float dt)
{
	if (dt < 0.0f) dt = 0.0f;
	if (dt > 0.25f) dt = 0.25f; // clamp huge spikes (e.g. after a breakpoint)

	g_time_accumulator += dt;
	int steps = 0;
	while (g_time_accumulator >= k_fixed_dt && steps < 8) // cap steps to avoid a spiral of death
	{
		runtime::systems::update_physics(k_fixed_dt);
		g_game_time += k_fixed_dt;
		g_time_accumulator -= k_fixed_dt;
		++steps;
	}

	runtime::systems::update_audio(dt);
}

// Elapsed in-game seconds since the last ResetGameTime (i.e. since Play started).
EDITOR_INTERFACE float GetGameTime()
{
	return g_game_time;
}

// Reset the game clock + fixed-step accumulator (call when Play starts).
EDITOR_INTERFACE void ResetGameTime()
{
	g_game_time = 0.0f;
	g_time_accumulator = 0.0f;
}

// DX12 viewport control
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

// ============== RENDER LOOP API ==============

EDITOR_INTERFACE void StartRenderLoop()
{
	runtime::RenderLoop::instance().start([]() {
		graphics::dx12::DX12Renderer::instance().render_frame();
	});
}

EDITOR_INTERFACE void StopRenderLoop()
{
	runtime::RenderLoop::instance().stop();
}

EDITOR_INTERFACE bool IsRenderLoopRunning()
{
	return runtime::RenderLoop::instance().is_running();
}

EDITOR_INTERFACE void SetTargetFPS(int fps)
{
	runtime::RenderLoop::instance().set_target_fps(fps);
}

EDITOR_INTERFACE int GetTargetFPS()
{
	return runtime::RenderLoop::instance().get_target_fps();
}

EDITOR_INTERFACE void SetRenderLoopVSync(bool enabled)
{
	runtime::RenderLoop::instance().set_vsync(enabled);
	graphics::dx12::DX12Renderer::instance().set_vsync(enabled);
}

EDITOR_INTERFACE bool IsRenderLoopVSyncEnabled()
{
	return runtime::RenderLoop::instance().is_vsync_enabled();
}

EDITOR_INTERFACE float GetDeltaTime()
{
	return runtime::RenderLoop::instance().get_delta_time();
}

EDITOR_INTERFACE float GetTotalTime()
{
	return runtime::RenderLoop::instance().get_total_time();
}

// ============== INPUT SYSTEM API ==============

EDITOR_INTERFACE void InitializeInput()
{
	input::InputSystem::instance().initialize();
}

EDITOR_INTERFACE void ShutdownInput()
{
	input::InputSystem::instance().shutdown();
}

EDITOR_INTERFACE void UpdateInput()
{
	input::InputSystem::instance().update();
}

EDITOR_INTERFACE void ProcessKeyboardEvent(unsigned int key, bool pressed)
{
	input::InputSystem::instance().process_keyboard_event(key, pressed);
}

EDITOR_INTERFACE void ProcessMouseButtonEvent(unsigned int button, bool pressed)
{
	input::InputSystem::instance().process_mouse_button_event(static_cast<input::mouse_button>(button), pressed);
}

EDITOR_INTERFACE void ProcessMouseMoveEvent(float x, float y)
{
	input::InputSystem::instance().process_mouse_move_event(x, y);
}

EDITOR_INTERFACE void ProcessMouseScrollEvent(float delta)
{
	input::InputSystem::instance().process_mouse_scroll_event(delta);
}

EDITOR_INTERFACE bool IsKeyDown(unsigned int key)
{
	return input::InputSystem::instance().is_key_down(static_cast<input::key_code>(key));
}

EDITOR_INTERFACE bool IsKeyPressed(unsigned int key)
{
	return input::InputSystem::instance().is_key_pressed(static_cast<input::key_code>(key));
}

EDITOR_INTERFACE bool IsKeyReleased(unsigned int key)
{
	return input::InputSystem::instance().is_key_released(static_cast<input::key_code>(key));
}

EDITOR_INTERFACE bool IsShiftDown()
{
	return input::InputSystem::instance().is_shift_down();
}

EDITOR_INTERFACE bool IsCtrlDown()
{
	return input::InputSystem::instance().is_ctrl_down();
}

EDITOR_INTERFACE bool IsAltDown()
{
	return input::InputSystem::instance().is_alt_down();
}

EDITOR_INTERFACE bool IsMouseButtonDown(unsigned int button)
{
	return input::InputSystem::instance().is_mouse_button_down(static_cast<input::mouse_button>(button));
}

EDITOR_INTERFACE bool IsMouseButtonPressed(unsigned int button)
{
	return input::InputSystem::instance().is_mouse_button_pressed(static_cast<input::mouse_button>(button));
}

EDITOR_INTERFACE bool IsMouseButtonReleased(unsigned int button)
{
	return input::InputSystem::instance().is_mouse_button_released(static_cast<input::mouse_button>(button));
}

EDITOR_INTERFACE void GetMousePosition(float* x, float* y)
{
	if (x && y) {
		input::InputSystem::instance().get_mouse_position(*x, *y);
	}
}

EDITOR_INTERFACE void GetMouseDelta(float* dx, float* dy)
{
	if (dx && dy) {
		input::InputSystem::instance().get_mouse_delta(*dx, *dy);
	}
}

EDITOR_INTERFACE float GetMouseScrollDelta()
{
	return input::InputSystem::instance().get_mouse_scroll_delta();
}

EDITOR_INTERFACE void SetCursorLocked(bool locked)
{
	input::InputSystem::instance().set_cursor_locked(locked);
}

EDITOR_INTERFACE void SetCursorVisible(bool visible)
{
	input::InputSystem::instance().set_cursor_visible(visible);
}

EDITOR_INTERFACE bool IsCursorLocked()
{
	return input::InputSystem::instance().is_cursor_locked();
}

EDITOR_INTERFACE bool IsCursorVisible()
{
	return input::InputSystem::instance().is_cursor_visible();
}

// Gamepad stubs
EDITOR_INTERFACE bool IsGamepadConnected(unsigned int gamepad_id)
{
	return input::InputSystem::instance().is_gamepad_connected(gamepad_id);
}

EDITOR_INTERFACE bool IsGamepadButtonDown(unsigned int gamepad_id, unsigned int button)
{
	return input::InputSystem::instance().is_gamepad_button_down(gamepad_id, static_cast<input::gamepad_button>(button));
}

EDITOR_INTERFACE float GetGamepadAxis(unsigned int gamepad_id, unsigned int axis)
{
	return input::InputSystem::instance().get_gamepad_axis(gamepad_id, static_cast<input::gamepad_axis>(axis));
}

EDITOR_INTERFACE void SetGamepadVibration(unsigned int gamepad_id, float left_motor, float right_motor)
{
	input::InputSystem::instance().set_gamepad_vibration(gamepad_id, left_motor, right_motor);
}

// ============== CAMERA SYSTEM API ==============

namespace {
	struct camera_descriptor {
		f32 position[3];
		f32 rotation[4];  // Quaternion
		u8 projection;    // 0 = perspective, 1 = orthographic
		f32 field_of_view;
		f32 orthographic_size;
		f32 near_clip;
		f32 far_clip;
		f32 aspect_ratio;
		u8 clear_flags;
		f32 background_color[4];
		s32 depth;
		s32 culling_mask;
		u8 camera_type;  // 0 = game, 1 = main, 2 = editor
		bool is_enabled;
	};
}

EDITOR_INTERFACE id::id_type CreateCamera(camera_descriptor* desc)
{
	if (!desc) return id::invalid_id;
	
	camera::init_info info{};
	memcpy(info.position, desc->position, sizeof(f32) * 3);
	memcpy(info.rotation, desc->rotation, sizeof(f32) * 4);
	info.projection = static_cast<camera::projection_type>(desc->projection);
	info.field_of_view = desc->field_of_view;
	info.orthographic_size = desc->orthographic_size;
	info.near_clip = desc->near_clip;
	info.far_clip = desc->far_clip;
	info.aspect_ratio = desc->aspect_ratio;
	info.clear = static_cast<camera::clear_flags>(desc->clear_flags);
	memcpy(info.background_color, desc->background_color, sizeof(f32) * 4);
	info.depth = desc->depth;
	info.culling_mask = desc->culling_mask;
	info.type = static_cast<camera::camera_type>(desc->camera_type);
	info.is_enabled = desc->is_enabled;
	
	return camera::create(info).get_id();
}

EDITOR_INTERFACE void RemoveCamera(id::id_type camera_id)
{
	camera::remove(camera::component{ camera::camera_id{camera_id} });
}

EDITOR_INTERFACE bool IsCameraAlive(id::id_type camera_id)
{
	return camera::is_alive(camera::camera_id{ camera_id });
}

EDITOR_INTERFACE id::id_type GetMainCamera()
{
	return camera::get_main_camera().get_id();
}

EDITOR_INTERFACE id::id_type GetActiveCamera()
{
	return camera::get_active_camera().get_id();
}

EDITOR_INTERFACE void SetActiveCamera(id::id_type camera_id)
{
	camera::set_active_camera(camera::component{ camera::camera_id{camera_id} });
}

EDITOR_INTERFACE unsigned int GetCameraCount()
{
	return camera::get_camera_count();
}

EDITOR_INTERFACE void SetCameraPosition(id::id_type camera_id, float x, float y, float z)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_position({ x, y, z });
	}
}

EDITOR_INTERFACE void GetCameraPosition(id::id_type camera_id, float* x, float* y, float* z)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid() && x && y && z) {
		auto pos = cam.position();
		*x = pos.x;
		*y = pos.y;
		*z = pos.z;
	}
}

EDITOR_INTERFACE void SetCameraRotation(id::id_type camera_id, float x, float y, float z, float w)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_rotation({ x, y, z, w });
	}
}

EDITOR_INTERFACE void GetCameraRotation(id::id_type camera_id, float* x, float* y, float* z, float* w)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid() && x && y && z && w) {
		auto rot = cam.rotation();
		*x = rot.x;
		*y = rot.y;
		*z = rot.z;
		*w = rot.w;
	}
}

EDITOR_INTERFACE void SetCameraFOV(id::id_type camera_id, float fov)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_field_of_view(fov);
	}
}

EDITOR_INTERFACE float GetCameraFOV(id::id_type camera_id)
{
	camera::component cam{ camera::camera_id{camera_id} };
	return cam.is_valid() ? cam.get_field_of_view() : 60.0f;
}

EDITOR_INTERFACE void SetCameraClipPlanes(id::id_type camera_id, float near_clip, float far_clip)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_near_clip(near_clip);
		cam.set_far_clip(far_clip);
	}
}

EDITOR_INTERFACE void SetCameraProjection(id::id_type camera_id, unsigned char projection)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_projection(static_cast<camera::projection_type>(projection));
	}
}

EDITOR_INTERFACE void SetCameraType(id::id_type camera_id, unsigned char type)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_type(static_cast<camera::camera_type>(type));
	}
}

EDITOR_INTERFACE unsigned char GetCameraType(id::id_type camera_id)
{
	camera::component cam{ camera::camera_id{camera_id} };
	return cam.is_valid() ? static_cast<unsigned char>(cam.get_type()) : 0;
}

EDITOR_INTERFACE void SetCameraEnabled(id::id_type camera_id, bool enabled)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_enabled(enabled);
	}
}

EDITOR_INTERFACE bool IsCameraEnabled(id::id_type camera_id)
{
	camera::component cam{ camera::camera_id{camera_id} };
	return cam.is_valid() ? cam.is_enabled() : false;
}

EDITOR_INTERFACE void SetCameraAspectRatio(id::id_type camera_id, float aspect)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_aspect_ratio(aspect);
	}
}

EDITOR_INTERFACE void SetCameraBackgroundColor(id::id_type camera_id, float r, float g, float b, float a)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_background_color(r, g, b, a);
	}
}

EDITOR_INTERFACE void SetCameraDepth(id::id_type camera_id, int depth)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid()) {
		cam.set_depth(depth);
	}
}

EDITOR_INTERFACE void GetCameraForward(id::id_type camera_id, float* x, float* y, float* z)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid() && x && y && z) {
		auto fwd = cam.get_forward();
		*x = fwd.x;
		*y = fwd.y;
		*z = fwd.z;
	}
}

EDITOR_INTERFACE void GetCameraRight(id::id_type camera_id, float* x, float* y, float* z)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid() && x && y && z) {
		auto right = cam.get_right();
		*x = right.x;
		*y = right.y;
		*z = right.z;
	}
}

EDITOR_INTERFACE void GetCameraUp(id::id_type camera_id, float* x, float* y, float* z)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid() && x && y && z) {
		auto up = cam.get_up();
		*x = up.x;
		*y = up.y;
		*z = up.z;
	}
}

EDITOR_INTERFACE void GetCameraViewMatrix(id::id_type camera_id, float* out_matrix)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid() && out_matrix) {
		DirectX::XMMATRIX view = cam.get_view_matrix();
		DirectX::XMFLOAT4X4 mat;
		DirectX::XMStoreFloat4x4(&mat, view);
		memcpy(out_matrix, &mat, sizeof(float) * 16);
	}
}

EDITOR_INTERFACE void GetCameraProjectionMatrix(id::id_type camera_id, float* out_matrix)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (cam.is_valid() && out_matrix) {
		DirectX::XMMATRIX proj = cam.get_projection_matrix();
		DirectX::XMFLOAT4X4 mat;
		DirectX::XMStoreFloat4x4(&mat, proj);
		memcpy(out_matrix, &mat, sizeof(float) * 16);
	}
}

// Render a camera gizmo (wireframe frustum) at the camera's position
EDITOR_INTERFACE void RenderCameraGizmo(id::id_type camera_id, float r, float g, float b)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (!cam.is_valid()) return;

	auto pos = cam.position();
	auto fwd = cam.get_forward();
	auto right = cam.get_right();
	auto up = cam.get_up();
	
	// Calculate frustum corners for visualization
	f32 fov = cam.get_field_of_view();
	f32 aspect = cam.get_aspect_ratio();
	f32 near_dist = 0.5f;  // Visual near plane
	f32 far_dist = 2.0f;   // Visual far plane
	
	f32 near_height = near_dist * tanf(DirectX::XMConvertToRadians(fov * 0.5f));
	f32 near_width = near_height * aspect;
	f32 far_height = far_dist * tanf(DirectX::XMConvertToRadians(fov * 0.5f));
	f32 far_width = far_height * aspect;
	
	// Render using the DX12 renderer's line rendering
	graphics::dx12::DX12Renderer::instance().render_camera_gizmo(
		{ pos.x, pos.y, pos.z },
		{ fwd.x, fwd.y, fwd.z },
		{ right.x, right.y, right.z },
		{ up.x, up.y, up.z },
		near_width, near_height, far_width, far_height,
		near_dist, far_dist,
		{ r, g, b, 1.0f }
	);
}

// Apply a camera's view/projection to the renderer
EDITOR_INTERFACE void ApplyCameraToRenderer(id::id_type camera_id)
{
	camera::component cam{ camera::camera_id{camera_id} };
	if (!cam.is_valid()) return;

	auto pos = cam.position();
	auto fwd = cam.get_forward();
	auto up = cam.get_up();
	
	// Calculate look-at target
	math::v3 target = { pos.x + fwd.x, pos.y + fwd.y, pos.z + fwd.z };
	
	graphics::dx12::DX12Renderer::instance().set_camera(
		{ pos.x, pos.y, pos.z },
		{ target.x, target.y, target.z },
		{ up.x, up.y, up.z }
	);
	
	// Also set projection parameters
	graphics::dx12::DX12Renderer::instance().set_projection(
		cam.get_field_of_view(),
		cam.get_aspect_ratio(),
		cam.get_near_clip(),
		cam.get_far_clip()
	);
}


// Model Import API
EDITOR_INTERFACE id::id_type ImportModel(const char* filepath)
{
	if (!filepath) return id::invalid_id;
	return graphics::ResourceRegistry::instance().import_model(filepath);
}

EDITOR_INTERFACE id::id_type ImportTexture(const char* filepath)
{
	if (!filepath) return id::invalid_id;
	return graphics::ResourceRegistry::instance().import_texture(filepath);
}

// Multi-Material Import API - returns submesh data via output arrays
// Returns number of submeshes, fills arrays with mesh_ids, material_ids, texture_ids
EDITOR_INTERFACE int ImportModelWithMaterials(
	const char* filepath,
	id::id_type* out_mesh_ids,
	id::id_type* out_material_ids,
	id::id_type* out_texture_ids,
	int max_submeshes)
{
	if (!filepath || !out_mesh_ids || !out_material_ids || !out_texture_ids || max_submeshes <= 0)
		return 0;

	auto result = graphics::ResourceRegistry::instance().import_model_with_materials(filepath);
	if (!result.success)
		return 0;

	int count = static_cast<int>((std::min)(result.submeshes.size(), static_cast<size_t>(max_submeshes)));
	
	for (int i = 0; i < count; i++)
	{
		out_mesh_ids[i] = result.submeshes[i].mesh_id;
		out_material_ids[i] = result.submeshes[i].material_id;
		out_texture_ids[i] = result.submeshes[i].texture_id;
	}

	return count;
}

// In-memory texture import (packed/encrypted asset pak loaded into RAM — no file on disk).
EDITOR_INTERFACE id::id_type ImportTextureFromMemory(const unsigned char* data, int length)
{
	if (!data || length <= 0) return id::invalid_id;
	return graphics::ResourceRegistry::instance().import_texture_from_memory(
		reinterpret_cast<const u8*>(data), static_cast<u64>(length));
}

// In-memory multi-material model import (packed asset pak loaded into RAM). ext_hint = "obj","fbx",...
EDITOR_INTERFACE int ImportModelFromMemoryWithMaterials(
	const unsigned char* data,
	int length,
	const char* ext_hint,
	const char* virtual_dir,
	id::id_type* out_mesh_ids,
	id::id_type* out_material_ids,
	id::id_type* out_texture_ids,
	int max_submeshes)
{
	if (!data || length <= 0 || !out_mesh_ids || !out_material_ids || !out_texture_ids || max_submeshes <= 0)
		return 0;

	auto result = graphics::ResourceRegistry::instance().import_model_with_materials_from_memory(
		reinterpret_cast<const u8*>(data), static_cast<u64>(length),
		ext_hint ? ext_hint : "", virtual_dir ? virtual_dir : "");
	if (!result.success)
		return 0;

	int count = static_cast<int>((std::min)(result.submeshes.size(), static_cast<size_t>(max_submeshes)));
	for (int i = 0; i < count; i++)
	{
		out_mesh_ids[i] = result.submeshes[i].mesh_id;
		out_material_ids[i] = result.submeshes[i].material_id;
		out_texture_ids[i] = result.submeshes[i].texture_id;
	}
	return count;
}

// Get submesh count without importing (for pre-allocation)
EDITOR_INTERFACE int GetModelSubmeshCount(const char* filepath)
{
	if (!filepath) return 0;
	
	auto model_data = graphics::ModelImporter::import_from_file(filepath);
	return static_cast<int>(model_data.submeshes.size());
}

// Get submesh names from model file
EDITOR_INTERFACE int GetModelSubmeshNames(const char* filepath, char** out_names, int max_submeshes, int max_name_length)
{
	if (!filepath || !out_names || max_submeshes <= 0 || max_name_length <= 0) return 0;
	
	auto model_data = graphics::ModelImporter::import_from_file(filepath);
	int count = static_cast<int>((std::min)(model_data.submeshes.size(), static_cast<size_t>(max_submeshes)));
	
	for (int i = 0; i < count; i++)
	{
	std::string name;
		
	// Use mesh name if available, otherwise use material name
	if (!model_data.submeshes[i].name.empty())
	{
	name = model_data.submeshes[i].name;
	}
	else if (model_data.submeshes[i].material_index < model_data.material_names.size())
	{
	name = model_data.material_names[model_data.submeshes[i].material_index];
	}
	else
	{
	name = "Submesh_" + std::to_string(i);
	}
		
	if (out_names[i])
	{
	strncpy_s(out_names[i], max_name_length, name.c_str(), _TRUNCATE);
	}
	}
	
	return count;
}

EDITOR_INTERFACE id::id_type LoadVMesh(const char* filepath)
{
	if (!filepath) return id::invalid_id;
	return graphics::ResourceRegistry::instance().load_vmesh(filepath);
}

EDITOR_INTERFACE bool ExportMeshToVMesh(id::id_type mesh_id, const char* filepath)
{
	if (!filepath) return false;
	return graphics::ResourceRegistry::instance().export_mesh_to_vmesh(mesh_id, filepath);
}

EDITOR_INTERFACE bool HasAssimpSupport()
{
#ifdef VORTEX_USE_ASSIMP
	return true;
#else
	return false;
#endif
}

// Asset Database API
EDITOR_INTERFACE void InitializeAssetDatabase(const char* project_path)
{
	if (!project_path) return;
	runtime::AssetDatabase::instance().initialize_with_project_path(project_path);
}

EDITOR_INTERFACE void InitializeAssetDatabaseWithManifest(const char* manifest_path)
{
	if (!manifest_path) return;
	runtime::AssetDatabase::instance().initialize_with_manifest(manifest_path);
}

EDITOR_INTERFACE void ShutdownAssetDatabase()
{
	runtime::AssetDatabase::instance().shutdown();
}

EDITOR_INTERFACE const char* GetAssetPathByGuid(const char* guid)
{
	if (!guid) return nullptr;
	return runtime::AssetDatabase::instance().get_asset_path_by_guid(guid);
}

EDITOR_INTERFACE bool HasAsset(const char* guid)
{
	if (!guid) return false;
	return runtime::AssetDatabase::instance().has_asset(guid);
}

// GUID-based resource loading
EDITOR_INTERFACE long LoadMeshByGuid(const char* guid)
{
	if (!guid) return 0;
	auto handle = runtime::resource_manager::load_mesh_by_guid(guid);
	return static_cast<long>(handle.value);
}

EDITOR_INTERFACE long LoadTextureByGuid(const char* guid)
{
	if (!guid) return 0;
	auto handle = runtime::resource_manager::load_texture_by_guid(guid);
	return static_cast<long>(handle.value);
}

EDITOR_INTERFACE long LoadMaterialByGuid(const char* guid)
{
	if (!guid) return 0;
	auto handle = runtime::resource_manager::load_material_by_guid(guid);
	return static_cast<long>(handle.value);
}

// MeshRenderer Component API
EDITOR_INTERFACE id::id_type CreateMeshRenderer(id::id_type entity_id, id::id_type mesh_id, id::id_type material_id)
{
	components::mesh_renderer_init_info info{};
	info.mesh_id = mesh_id;
	info.material_id = material_id;
	info.cast_shadows = true;
	info.receive_shadows = true;
	
	return components::mesh_renderer::create(entity_id, info).value;
}

EDITOR_INTERFACE void RemoveMeshRenderer(id::id_type renderer_id)
{
	components::mesh_renderer::remove(components::mesh_renderer_id{ renderer_id });
}

EDITOR_INTERFACE void SetMeshRendererMesh(id::id_type renderer_id, id::id_type mesh_id)
{
	components::mesh_renderer::set_mesh(components::mesh_renderer_id{ renderer_id }, mesh_id);
}

EDITOR_INTERFACE void SetMeshRendererMaterial(id::id_type renderer_id, id::id_type material_id)
{
	components::mesh_renderer::set_material(components::mesh_renderer_id{ renderer_id }, material_id);
}

EDITOR_INTERFACE id::id_type GetMeshRendererMesh(id::id_type renderer_id)
{
	return components::mesh_renderer::get_mesh(components::mesh_renderer_id{ renderer_id });
}

EDITOR_INTERFACE id::id_type GetMeshRendererMaterial(id::id_type renderer_id)
{
	return components::mesh_renderer::get_material(components::mesh_renderer_id{ renderer_id });
}

// Submit all active MeshRenderers to the render queue
EDITOR_INTERFACE void SubmitAllMeshRenderers()
{
	constexpr u32 MAX_RENDERERS = 4096;
	static id::id_type renderer_ids[MAX_RENDERERS];
	
	u32 count = components::mesh_renderer::get_all_renderers(renderer_ids, MAX_RENDERERS);
	
	auto& renderer = graphics::dx12::DX12Renderer::instance();
	
	for (u32 i = 0; i < count; ++i)
	{
		components::mesh_renderer_id renderer_id{ renderer_ids[i] };
		id::id_type mesh_id = components::mesh_renderer::get_mesh(renderer_id);
		id::id_type material_id = components::mesh_renderer::get_material(renderer_id);
		id::id_type entity_id = components::mesh_renderer::get_entity(renderer_id);
		
		if (!id::is_valid(mesh_id) || !id::is_valid(entity_id))
			continue;
		
		// Get entity transform - for now use identity matrix
		// TODO: Get actual transform from entity
		graphics::dx12::RenderItem item{};
		item.mesh_id = mesh_id;
		item.material_id = material_id;
		DirectX::XMStoreFloat4x4(&item.world_matrix, DirectX::XMMatrixIdentity());
		
	renderer.submit_render_item(item);
	}
}

// ============== LIGHTING SYSTEM API ==============

// Clear all dynamic lights (call before submitting new lights each frame)
EDITOR_INTERFACE void ClearLights()
{
	graphics::dx12::DX12Renderer::instance().clear_lights();
}

// Set the primary directional light
EDITOR_INTERFACE void SetDirectionalLight(
	float dirX, float dirY, float dirZ,
	float colorR, float colorG, float colorB,
	float intensity)
{
	graphics::dx12::DX12Renderer::instance().set_directional_light_full(
		{ dirX, dirY, dirZ },
		{ colorR, colorG, colorB },
		intensity
	);
}

// Add a point light (max 16 per frame)
EDITOR_INTERFACE void AddPointLight(
	float posX, float posY, float posZ,
	float colorR, float colorG, float colorB,
	float intensity, float range)
{
	graphics::dx12::DX12Renderer::PointLightData light{};
	light.position = { posX, posY, posZ };
	light.color = { colorR, colorG, colorB };
	light.intensity = intensity;
	light.range = range;
	
	graphics::dx12::DX12Renderer::instance().add_point_light(light);
}

// Add a spot light (max 8 per frame)
EDITOR_INTERFACE void AddSpotLight(
	float posX, float posY, float posZ,
	float dirX, float dirY, float dirZ,
	float colorR, float colorG, float colorB,
	float intensity, float range,
	float spotAngle, float innerSpotAngle)
{
	graphics::dx12::DX12Renderer::SpotLightData light{};
	light.position = { posX, posY, posZ };
	light.direction = { dirX, dirY, dirZ };
	light.color = { colorR, colorG, colorB };
	light.intensity = intensity;
	light.range = range;
	light.spot_angle = spotAngle;
	light.inner_spot_angle = innerSpotAngle;
	
	
	graphics::dx12::DX12Renderer::instance().add_spot_light(light);
}

// Set ambient light strength
EDITOR_INTERFACE void SetAmbientStrength(float strength)
{
	graphics::dx12::DX12Renderer::instance().set_ambient_strength(strength);
}


// ============== SKYBOX API ==============

EDITOR_INTERFACE void SetSkyboxEnabled(bool enabled)
{
	graphics::dx12::DX12Renderer::instance().set_skybox_enabled(enabled);
}

EDITOR_INTERFACE bool IsSkyboxEnabled()
{
	return graphics::dx12::DX12Renderer::instance().is_skybox_enabled();
}

EDITOR_INTERFACE void SetSkyboxMode(unsigned int mode)
{
	graphics::dx12::DX12Renderer::instance().set_skybox_mode(
		static_cast<graphics::dx12::DX12Renderer::SkyboxMode>(mode));
}

EDITOR_INTERFACE unsigned int GetSkyboxMode()
{
	return static_cast<unsigned int>(graphics::dx12::DX12Renderer::instance().get_skybox_mode());
}

EDITOR_INTERFACE void SetSkyboxColors(
	float skyR, float skyG, float skyB,
	float horizonR, float horizonG, float horizonB,
	float groundR, float groundG, float groundB)
{
	graphics::dx12::DX12Renderer::instance().set_skybox_colors(
		{ skyR, skyG, skyB },
		{ horizonR, horizonG, horizonB },
		{ groundR, groundG, groundB }
	);
}

EDITOR_INTERFACE void SetSkyboxSolidColor(float r, float g, float b)
{
	graphics::dx12::DX12Renderer::instance().set_skybox_solid_color({ r, g, b });
}

EDITOR_INTERFACE void SetSkyboxSun(float dirX, float dirY, float dirZ, float colorR, float colorG, float colorB, float intensity)
{
	graphics::dx12::DX12Renderer::instance().set_skybox_sun(
		{ dirX, dirY, dirZ },
		{ colorR, colorG, colorB },
		intensity);
}

// ============== SKYBOX COMPONENT API (Runtime) ==============

namespace {
	struct skybox_descriptor {
		u8 mode; // 0 = solid, 1 = gradient, 2 = cubemap
		f32 sky_color[3];
		f32 horizon_color[3];
		f32 ground_color[3];
		f32 sun_direction[3];
		f32 sun_color[3];
		f32 sun_intensity;
		f32 ambient_intensity;
		f32 exposure;
		bool is_enabled;
	};
}

EDITOR_INTERFACE id::id_type CreateSkyboxComponent(skybox_descriptor* desc)
{
	if (!desc) return id::invalid_id;
	
	skybox::init_info info{};
	info.mode = static_cast<skybox::skybox_mode>(desc->mode);
	memcpy(info.sky_color, desc->sky_color, sizeof(f32) * 3);
	memcpy(info.horizon_color, desc->horizon_color, sizeof(f32) * 3);
	memcpy(info.ground_color, desc->ground_color, sizeof(f32) * 3);
	memcpy(info.sun_direction, desc->sun_direction, sizeof(f32) * 3);
	memcpy(info.sun_color, desc->sun_color, sizeof(f32) * 3);
	info.sun_intensity = desc->sun_intensity;
	info.ambient_intensity = desc->ambient_intensity;
	info.exposure = desc->exposure;
	info.is_enabled = desc->is_enabled;
	
	return skybox::create(info).get_id();
}

EDITOR_INTERFACE void RemoveSkyboxComponent(id::id_type skybox_id)
{
	skybox::remove(skybox::component{ skybox::skybox_id{skybox_id} });
}

EDITOR_INTERFACE void ApplySkyboxToRenderer(id::id_type skybox_id)
{
	skybox::component skybox{ skybox::skybox_id{skybox_id} };
	if (skybox.is_valid())
	{
		skybox.apply_to_renderer();
	}
}

EDITOR_INTERFACE void ApplyActiveSkybox()
{
	auto skybox = skybox::get_active_skybox();
	if (skybox.is_valid())
	{
		skybox.apply_to_renderer();
	}
}

EDITOR_INTERFACE void SetActiveSkyboxComponent(id::id_type skybox_id)
{
	skybox::set_active_skybox(skybox::component{ skybox::skybox_id{skybox_id} });
}
