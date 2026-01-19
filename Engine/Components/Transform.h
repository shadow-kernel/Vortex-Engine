#pragma once

#include "ComponentsCommon.h"

namespace vortex::transform {

	struct init_info
	{
		f32 position[3]{};
		f32 rotation[4]{};
		f32 scale[3]{ 1.0f, 1.0f, 1.0f };
	};

	component create_transform(const init_info& transform_init_info, game_entity::entity entity);

	void remove_transform(component component);
}