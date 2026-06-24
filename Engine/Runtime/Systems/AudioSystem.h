#pragma once

#include "../../Common/CommonHeaders.h"

namespace vortex::runtime::systems {
	void initialize_audio();
	void shutdown_audio();
	bool audio_initialized();
	void update_audio(float dt);
}
