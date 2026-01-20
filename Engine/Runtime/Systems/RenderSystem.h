#pragma once

#include "../../Common/CommonHeaders.h"

namespace vortex::runtime::systems {
	void initialize_render();
	void shutdown_render();
	bool render_initialized();
}
