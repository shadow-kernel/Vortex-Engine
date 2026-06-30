#include "../ApiCommon.h"

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
