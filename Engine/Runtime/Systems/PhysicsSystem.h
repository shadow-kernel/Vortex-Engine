#pragma once

#include "../../Common/CommonHeaders.h"
#include "../../Common/Id.h"

namespace vortex::runtime::systems {
	void initialize_physics();
	void shutdown_physics();
	bool physics_initialized();
	void update_physics(float dt);

	// Minimal dynamic-body registry for play mode. An entity registered here gets gravity
	// integration that writes its transform every tick (engine is the authority during play).
	// Registered on Play, cleared on Stop.
	void set_rigidbody(id::id_type entity_id, bool use_gravity);
	void clear_rigidbodies();
}
