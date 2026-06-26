#pragma once

#include "../../Common/CommonHeaders.h"
#include "../../Common/Id.h"

namespace vortex::runtime::systems {
	void initialize_physics();
	void shutdown_physics();
	bool physics_initialized();
	void update_physics(float dt);

	// Dynamic-body registry for play mode. A registered entity gets gravity + AABB collision
	// (half-extents) that writes its transform every tick. Registered on Play, cleared on Stop.
	void set_rigidbody(id::id_type entity_id, bool use_gravity, float hx, float hy, float hz);
	void clear_rigidbodies();

	// Static world colliders (level geometry) — boxes that dynamic bodies + the character rest on.
	void register_static_box(float cx, float cy, float cz, float hx, float hy, float hz);
	void clear_colliders();

	// Single player character (the play-mode camera body): gravity + AABB collision so it stands on
	// the floor/boxes and can't pass through them.
	void character_init(float x, float y, float z, float hx, float hy, float hz);
	void character_move(float wish_x, float wish_z, bool jump, float dt);
	void character_get_position(float* out_xyz);
	bool character_grounded();
}
