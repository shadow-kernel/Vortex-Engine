#include "../ApiCommon.h"

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

