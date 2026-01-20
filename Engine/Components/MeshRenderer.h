#pragma once

#include "../Common/CommonHeaders.h"
#include "../Common/Id.h"

namespace vortex::components
{
	struct mesh_renderer_init_info
	{
		id::id_type mesh_id{ id::invalid_id };
		id::id_type material_id{ id::invalid_id };
		bool cast_shadows{ true };
		bool receive_shadows{ true };
	};

	struct mesh_renderer_id
	{
		id::id_type value{ id::invalid_id };
		bool is_valid() const { return id::is_valid(value); }
	};

	namespace mesh_renderer
	{
		mesh_renderer_id create(id::id_type entity_id, const mesh_renderer_init_info& info);
		void remove(mesh_renderer_id id);

		id::id_type get_mesh(mesh_renderer_id id);
		id::id_type get_material(mesh_renderer_id id);
		void set_mesh(mesh_renderer_id id, id::id_type mesh_id);
		void set_material(mesh_renderer_id id, id::id_type material_id);

		bool get_cast_shadows(mesh_renderer_id id);
		void set_cast_shadows(mesh_renderer_id id, bool value);

		id::id_type get_entity(mesh_renderer_id id);
	}
}
