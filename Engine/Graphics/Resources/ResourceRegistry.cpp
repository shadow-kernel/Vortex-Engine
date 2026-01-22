#include "ResourceRegistry.h"
#include "../Geometry/MeshGeneratorFactory.h"
#include "../Importers/ModelImporter.h"
#include "../Importers/TextureImporter.h"
#include "../Importers/MeshSerializer.h"

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

		// Create default primitives using generators
		m_default_cube = create_primitive_cube(1.0f);
		m_default_sphere = create_primitive_sphere(0.5f);
		m_default_plane = create_primitive_plane(10.0f, 10.0f);
		m_default_cylinder = create_primitive_cylinder(0.5f, 1.0f);
		m_default_cone = create_primitive_cone(0.5f, 1.0f);

		// Create default white texture
		m_default_white_texture = create_solid_color_texture(0xFFFFFFFF, "White");

		// Create default material
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
		m_meshes.erase(id);
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

	id::id_type ResourceRegistry::create_texture(const TextureDesc& desc, const void* data)
	{
		if (!m_device) return id::invalid_id;

		auto texture = std::make_unique<Texture>();
		if (!texture->create(m_device, desc, data))
		{
			return id::invalid_id;
		}

		id::id_type id = m_next_texture_id++;
		m_textures[id] = std::move(texture);
		return id;
	}

	id::id_type ResourceRegistry::create_solid_color_texture(u32 color, const std::string& name)
	{
		if (!m_device) return id::invalid_id;

		auto texture = std::make_unique<Texture>();
		if (!texture->create_from_color(m_device, color))
		{
			return id::invalid_id;
		}

		id::id_type id = m_next_texture_id++;
		m_textures[id] = std::move(texture);
		return id;
	}

	Texture* ResourceRegistry::get_texture(id::id_type id)
	{
		auto it = m_textures.find(id);
		return it != m_textures.end() ? it->second.get() : nullptr;
	}

	void ResourceRegistry::destroy_texture(id::id_type id)
	{
		m_textures.erase(id);
	}

	std::vector<id::id_type> ResourceRegistry::get_all_texture_ids() const
	{
		std::vector<id::id_type> ids;
		ids.reserve(m_textures.size());
		for (const auto& [id, _] : m_textures)
		{
			ids.push_back(id);
		}
		return ids;
	}

	id::id_type ResourceRegistry::create_material(const std::string& name)
	{
		if (!m_device) return id::invalid_id;

		auto material = std::make_unique<Material>();
		if (!material->create(m_device))
		{
			return id::invalid_id;
		}

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
		m_materials.erase(id);
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

	id::id_type ResourceRegistry::import_model(const std::string& filepath)
	{
		if (!m_device) return id::invalid_id;

		// Import model data
		ImportedModelData model_data = ModelImporter::import_from_file(filepath);
		if (!model_data.is_valid())
		{
			return id::invalid_id;
		}

		// For now, import the first submesh
		// TODO: Support multi-submesh models by creating separate mesh resources
		// or by storing submesh data within a single Mesh object
		return create_mesh_from_submesh(model_data.submeshes[0], model_data.name);
	}

	id::id_type ResourceRegistry::create_mesh_from_submesh(const SubMeshData& submesh, const std::string& name)
	{
		if (submesh.vertices.empty())
			return id::invalid_id;

		MeshData mesh_data;
		mesh_data.vertices = submesh.vertices;
		mesh_data.indices = submesh.indices;

		return create_mesh(mesh_data, name);
	}

	id::id_type ResourceRegistry::import_texture(const std::string& filepath, const std::string& name)
	{
		if (!m_device) return id::invalid_id;

		// Import image data
		ImageData image_data = TextureImporter::import_from_file(filepath);
		if (!image_data.is_valid())
		{
			return id::invalid_id;
		}

		// Create texture descriptor
		TextureDesc desc;
		desc.width = image_data.width;
		desc.height = image_data.height;
		
		// Map format
		switch (image_data.format)
		{
		case ImageFormat::R8:
			desc.format = TextureFormat::R8_UNORM;
			break;
		case ImageFormat::RG8:
			desc.format = TextureFormat::RG8_UNORM;
			break;
		case ImageFormat::RGBA8:
		default:
			desc.format = TextureFormat::RGBA8_UNORM;
			break;
		}

		return create_texture(desc, image_data.pixels.data());
	}

	bool ResourceRegistry::export_mesh_to_vmesh(id::id_type mesh_id, const std::string& filepath)
	{
		// TODO: Implement mesh to ImportedModelData conversion
		// This requires storing more metadata with Mesh objects
		// For now, direct binary mesh export is not supported
		// Use model source files and save with MeshSerializer directly instead
		return false;
	}

	id::id_type ResourceRegistry::load_vmesh(const std::string& filepath)
	{
		if (!m_device) return id::invalid_id;

		// Load from binary .vmesh file
		ImportedModelData model_data = MeshSerializer::load_from_file(filepath);
		if (!model_data.is_valid())
		{
			return id::invalid_id;
		}

		// Import the first submesh
		return create_mesh_from_submesh(model_data.submeshes[0], model_data.name);
	}
}
