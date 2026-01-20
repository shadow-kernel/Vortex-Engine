#pragma once

#include "../Common/CommonHeaders.h"
#include "../Common/Id.h"
#include "../Components/Entity.h"
#include "../Components/Transform.h"
#include <string>

namespace vortex::runtime::prefab_service {

	struct prefab_handle
	{
		id::id_type value{ id::invalid_id };
		bool is_valid() const { return id::is_valid(value); }
	};

	bool is_initialized();
	void reset();

	void initialize();
	void shutdown();

	prefab_handle load_prefab(const char* path);
	game_entity::entity instantiate(prefab_handle handle, const transform::init_info& transform);
	void unload(prefab_handle handle);

	// convenience for internal/tests
	prefab_handle load_prefab_key(const std::string& key);
	const std::string& prefab_path(prefab_handle handle);
}
