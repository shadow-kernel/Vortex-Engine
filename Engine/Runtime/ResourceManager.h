#pragma once

#include "../Common/CommonHeaders.h"
#include "../Common/Id.h"
#include <string>

namespace vortex::runtime::resource_manager {

	struct resource_handle
	{
		id::id_type value{ id::invalid_id };
		bool is_valid() const { return id::is_valid(value); }
	};

	bool is_initialized();
	void reset();

	void initialize();
	void shutdown();

	resource_handle load_mesh(const char* path);
	resource_handle load_texture(const char* path);
	resource_handle load_material(const char* path);
	resource_handle load_shader(const char* path);
	resource_handle load_audio(const char* path);
	void unload(resource_handle handle);

	// Convenience helper for tests/internal use
	resource_handle load_resource(const std::string& key);
	const std::string& resource_path(resource_handle handle);
}
