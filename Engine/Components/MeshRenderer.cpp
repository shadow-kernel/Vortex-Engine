#include "MeshRenderer.h"
#include <vector>
#include <unordered_map>

namespace vortex::components::mesh_renderer
{
	namespace
	{
		struct mesh_renderer_data
		{
			id::id_type entity_id{ id::invalid_id };
			id::id_type mesh_id{ id::invalid_id };
			id::id_type material_id{ id::invalid_id };
			bool cast_shadows{ true };
			bool receive_shadows{ true };
		};

		std::vector<mesh_renderer_data> g_renderers;
		std::vector<id::id_type> g_free_ids;
		std::unordered_map<id::id_type, id::id_type> g_entity_to_renderer;
	}

	mesh_renderer_id create(id::id_type entity_id, const mesh_renderer_init_info& info)
	{
		mesh_renderer_data data{};
		data.entity_id = entity_id;
		data.mesh_id = info.mesh_id;
		data.material_id = info.material_id;
		data.cast_shadows = info.cast_shadows;
		data.receive_shadows = info.receive_shadows;

		id::id_type id;
		if (!g_free_ids.empty())
		{
			id = g_free_ids.back();
			g_free_ids.pop_back();
			g_renderers[id] = data;
		}
		else
		{
			id = static_cast<id::id_type>(g_renderers.size());
			g_renderers.push_back(data);
		}

		g_entity_to_renderer[entity_id] = id;
		return mesh_renderer_id{ id };
	}

	void remove(mesh_renderer_id id)
	{
		if (!id.is_valid() || id.value >= g_renderers.size()) return;

		auto& data = g_renderers[id.value];
		g_entity_to_renderer.erase(data.entity_id);
		data = {};
		g_free_ids.push_back(id.value);
	}

	id::id_type get_mesh(mesh_renderer_id id)
	{
		if (!id.is_valid() || id.value >= g_renderers.size()) return id::invalid_id;
		return g_renderers[id.value].mesh_id;
	}

	id::id_type get_material(mesh_renderer_id id)
	{
		if (!id.is_valid() || id.value >= g_renderers.size()) return id::invalid_id;
		return g_renderers[id.value].material_id;
	}

	void set_mesh(mesh_renderer_id id, id::id_type mesh_id)
	{
		if (!id.is_valid() || id.value >= g_renderers.size()) return;
		g_renderers[id.value].mesh_id = mesh_id;
	}

	void set_material(mesh_renderer_id id, id::id_type material_id)
	{
		if (!id.is_valid() || id.value >= g_renderers.size()) return;
		g_renderers[id.value].material_id = material_id;
	}

	bool get_cast_shadows(mesh_renderer_id id)
	{
		if (!id.is_valid() || id.value >= g_renderers.size()) return false;
		return g_renderers[id.value].cast_shadows;
	}

	void set_cast_shadows(mesh_renderer_id id, bool value)
	{
		if (!id.is_valid() || id.value >= g_renderers.size()) return;
		g_renderers[id.value].cast_shadows = value;
	}

	id::id_type get_entity(mesh_renderer_id id)
	{
		if (!id.is_valid() || id.value >= g_renderers.size()) return id::invalid_id;
		return g_renderers[id.value].entity_id;
	}
}

namespace vortex::components::mesh_renderer
{
	// Query functions for render system
	u32 get_all_renderers(id::id_type* out_ids, u32 max_count)
	{
		u32 count = 0;
		for (id::id_type i = 0; i < g_renderers.size() && count < max_count; ++i)
		{
			if (id::is_valid(g_renderers[i].entity_id))
			{
				out_ids[count++] = i;
			}
		}
		return count;
	}
}
