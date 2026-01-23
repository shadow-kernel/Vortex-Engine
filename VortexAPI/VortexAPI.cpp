#ifndef EDITOR_INTERFACE
#define EDITOR_INTERFACE extern "C" __declspec(dllexport)
#endif // !EDITOR_INTERFACE

#include "CommonHeaders.h"
#include "Id.h"
#include "..\Engine\Components\Entity.h"
#include "..\Engine\Components\Transform.h"
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

EDITOR_INTERFACE void DestroyMaterial(id::id_type material_id)
{
	graphics::ResourceRegistry::instance().destroy_material(material_id);
}

// Render item submission
EDITOR_INTERFACE void SubmitRenderItem(id::id_type mesh_id, id::id_type material_id, float* world_matrix)
{
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
