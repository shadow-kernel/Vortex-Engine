#include "ResourceRegistry_Internal.h"

namespace vortex::graphics
{
	ResourceRegistry& ResourceRegistry::instance()
	{
		static ResourceRegistry inst;
		return inst;
	}


	void ResourceRegistry::initialize(ID3D12Device* device)
	{
		if (!device) return;
		m_device = device;

		create_srv_heap();

		m_default_cube = create_primitive_cube(1.0f);
		m_default_sphere = create_primitive_sphere(0.5f);
		m_default_plane = create_primitive_plane(10.0f, 10.0f);
		m_default_cylinder = create_primitive_cylinder(0.5f, 1.0f);
		m_default_cone = create_primitive_cone(0.5f, 1.0f);

		m_default_white_texture = create_solid_color_texture(0xFFFFFFFF, "White");
		m_default_material = create_material("Default");
	}


	void ResourceRegistry::shutdown()
	{
		m_meshes.clear();
		m_textures.clear();
		m_materials.clear();

		m_default_cube = id::invalid_id;
		m_default_sphere = id::invalid_id;
		m_default_plane = id::invalid_id;
		m_default_cylinder = id::invalid_id;
		m_default_cone = id::invalid_id;
		m_default_white_texture = id::invalid_id;
		m_default_material = id::invalid_id;

		m_device = nullptr;
	}


	id::id_type ResourceRegistry::create_mesh(const MeshData& data, const std::string& name)
	{
		if (!m_device) return id::invalid_id;

		auto mesh = std::make_unique<Mesh>();
		if (!mesh->create(m_device, data))
		{
			return id::invalid_id;
		}
		mesh->set_name(name);

		id::id_type id = m_next_mesh_id++;
		m_meshes[id] = std::move(mesh);
		return id;
	}


	id::id_type ResourceRegistry::create_mesh_from_generator(const IMeshGenerator& generator)
	{
		if (!m_device) return id::invalid_id;

		auto mesh = std::make_unique<Mesh>();
		if (!mesh->create_from_generator(m_device, generator))
		{
			return id::invalid_id;
		}

		id::id_type id = m_next_mesh_id++;
		m_meshes[id] = std::move(mesh);
		return id;
	}


	id::id_type ResourceRegistry::create_primitive_cube(float size)
	{
		auto generator = MeshGeneratorFactory::create_cube(size);
		return create_mesh_from_generator(*generator);
	}


	id::id_type ResourceRegistry::create_primitive_sphere(float radius, u32 slices, u32 stacks)
	{
		auto generator = MeshGeneratorFactory::create_sphere(radius, slices, stacks);
		return create_mesh_from_generator(*generator);
	}


	id::id_type ResourceRegistry::create_inverted_sphere(float radius, u32 slices, u32 stacks)
	{
		// Create sphere and then invert the winding order for skybox rendering
		auto generator = MeshGeneratorFactory::create_sphere(radius, slices, stacks);
		
		// Generate the mesh data
		std::vector<VertexPosNormalUV> vertices;
		std::vector<u32> indices;
		generator->generate(vertices, indices);
		
		// Invert normals (point inward for skybox)
		for (auto& v : vertices)
		{
			v.normal.x = -v.normal.x;
			v.normal.y = -v.normal.y;
			v.normal.z = -v.normal.z;
		}
		
		// Reverse winding order (swap indices in each triangle)
		for (size_t i = 0; i + 2 < indices.size(); i += 3)
		{
			std::swap(indices[i + 1], indices[i + 2]);
		}
		
		// Create mesh from modified data
		MeshData mesh_data;
		mesh_data.vertices.reserve(vertices.size());
		for (const auto& v : vertices)
		{
			mesh_data.vertices.push_back({
				{ v.position.x, v.position.y, v.position.z },
				{ v.normal.x, v.normal.y, v.normal.z },
				{ v.uv.x, v.uv.y }
			});
		}
		mesh_data.indices = std::move(indices);
		
		return create_mesh(mesh_data, "InvertedSphere");
	}


	id::id_type ResourceRegistry::create_primitive_plane(float width, float depth)
	{
		auto generator = MeshGeneratorFactory::create_plane(width, depth);
		return create_mesh_from_generator(*generator);
	}


	id::id_type ResourceRegistry::create_primitive_cylinder(float radius, float height, u32 slices)
	{
		auto generator = MeshGeneratorFactory::create_cylinder(radius, height, slices);
		return create_mesh_from_generator(*generator);
	}


	id::id_type ResourceRegistry::create_primitive_cone(float radius, float height, u32 slices)
	{
		auto generator = MeshGeneratorFactory::create_cone(radius, height, slices);
		return create_mesh_from_generator(*generator);
	}


	Mesh* ResourceRegistry::get_mesh(id::id_type id)
	{
		auto it = m_meshes.find(id);
		return it != m_meshes.end() ? it->second.get() : nullptr;
	}


	void ResourceRegistry::destroy_mesh(id::id_type id)
	{
		auto it = m_meshes.find(id);
		if (it != m_meshes.end())
		{
			m_meshes.erase(it);
		}
	}


	std::vector<id::id_type> ResourceRegistry::get_all_mesh_ids() const
	{
		std::vector<id::id_type> ids;
		ids.reserve(m_meshes.size());
		for (const auto& [id, _] : m_meshes)
		{
			ids.push_back(id);
		}
		return ids;
	}


}
