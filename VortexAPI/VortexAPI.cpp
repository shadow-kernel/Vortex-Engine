#ifndef EDITOR_INTERFACE
#define EDITOR_INTERFACE extern "C" __declspec(dllexport)
#endif // !EDITOR_INTERFACE

#include "CommonHeaders.h"
#include "Id.h"
#include "..\Engine\Components\Entity.h"
#include "..\Engine\Components\Transform.h"
#include "..\Engine\Runtime\SceneManager.h"
#include "..\Engine\Runtime\ResourceManager.h"
#include "..\Engine\Runtime\PrefabService.h"
#include "..\Engine\Runtime\RenderLoop.h"
#include "..\Engine\Runtime\Systems\RenderSystem.h"
#include "..\Engine\Runtime\Systems\RenderSystemDX12.h"
#include "..\Engine\Runtime\Systems\PhysicsSystem.h"
#include "..\Engine\Runtime\Systems\AudioSystem.h"
#include "..\Engine\Graphics\Resources\ResourceRegistry.h"
#include "..\Engine\Graphics\DX12\DX12Renderer.h"

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
