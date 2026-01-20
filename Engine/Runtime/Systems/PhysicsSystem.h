#pragma once

#include "../../Common/CommonHeaders.h"

namespace vortex::runtime::systems {
	void initialize_physics();
	void shutdown_physics();
	bool physics_initialized();
}
