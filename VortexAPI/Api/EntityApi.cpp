#include "../ApiCommon.h"

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

