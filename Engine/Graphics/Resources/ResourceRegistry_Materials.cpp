#include "ResourceRegistry_Internal.h"

namespace vortex::graphics
{
	id::id_type ResourceRegistry::create_material(const std::string& name)
	{
		if (!m_device) return id::invalid_id;

		auto material = std::make_unique<Material>();

		id::id_type id = m_next_material_id++;
		m_materials[id] = std::move(material);
		return id;
	}


	Material* ResourceRegistry::get_material(id::id_type id)
	{
		auto it = m_materials.find(id);
		return it != m_materials.end() ? it->second.get() : nullptr;
	}


	void ResourceRegistry::destroy_material(id::id_type id)
	{
		auto it = m_materials.find(id);
		if (it != m_materials.end())
		{
			m_materials.erase(it);
		}
	}


	std::vector<id::id_type> ResourceRegistry::get_all_material_ids() const
	{
		std::vector<id::id_type> ids;
		ids.reserve(m_materials.size());
		for (const auto& [id, _] : m_materials)
		{
			ids.push_back(id);
		}
		return ids;
	}


}
