#pragma once

#include "../../Common/CommonHeaders.h"
#include "../../Common/Id.h"
#include "Mesh.h"
#include "Texture.h"
#include "Material.h"
#include "../Geometry/IMeshGenerator.h"
#include <unordered_map>
#include <memory>
#include <string>
#include <vector>

namespace vortex::graphics
{
	/// <summary>
	/// Central registry for all GPU resources.
	/// Manages lifetime and provides access by ID.
	/// </summary>
	class ResourceRegistry
	{
	public:
		static ResourceRegistry& instance();

		void initialize(ID3D12Device* device);
		void shutdown();

		// Mesh management
		id::id_type create_mesh(const MeshData& data, const std::string& name = "");
		id::id_type create_mesh_from_generator(const IMeshGenerator& generator);
		id::id_type create_primitive_cube(float size = 1.0f);
		id::id_type create_primitive_sphere(float radius = 0.5f, u32 slices = 32, u32 stacks = 16);
		id::id_type create_primitive_plane(float width = 1.0f, float depth = 1.0f);
		id::id_type create_primitive_cylinder(float radius = 0.5f, float height = 1.0f, u32 slices = 32);
		id::id_type create_primitive_cone(float radius = 0.5f, float height = 1.0f, u32 slices = 32);
		Mesh* get_mesh(id::id_type id);
		void destroy_mesh(id::id_type id);
		std::vector<id::id_type> get_all_mesh_ids() const;

		// Texture management
		id::id_type create_texture(const TextureDesc& desc, const void* data = nullptr);
		id::id_type create_solid_color_texture(u32 color, const std::string& name = "");
		Texture* get_texture(id::id_type id);
		void destroy_texture(id::id_type id);
		std::vector<id::id_type> get_all_texture_ids() const;

		// Material management
		id::id_type create_material(const std::string& name = "");
		Material* get_material(id::id_type id);
		void destroy_material(id::id_type id);
		std::vector<id::id_type> get_all_material_ids() const;

		// Import management
		id::id_type import_model(const std::string& filepath);
		id::id_type import_texture(const std::string& filepath, const std::string& name = "");
		bool export_mesh_to_vmesh(id::id_type mesh_id, const std::string& filepath);
		id::id_type load_vmesh(const std::string& filepath);

		// Default resources
		id::id_type default_cube_mesh() const { return m_default_cube; }
		id::id_type default_sphere_mesh() const { return m_default_sphere; }
		id::id_type default_plane_mesh() const { return m_default_plane; }
		id::id_type default_white_texture() const { return m_default_white_texture; }
		id::id_type default_material() const { return m_default_material; }

		bool is_initialized() const { return m_device != nullptr; }

	private:
		ResourceRegistry() = default;

		ID3D12Device* m_device{ nullptr };

		std::unordered_map<id::id_type, std::unique_ptr<Mesh>> m_meshes;
		std::unordered_map<id::id_type, std::unique_ptr<Texture>> m_textures;
		std::unordered_map<id::id_type, std::unique_ptr<Material>> m_materials;

		id::id_type m_next_mesh_id{ 1 };
		id::id_type m_next_texture_id{ 1 };
		id::id_type m_next_material_id{ 1 };

		id::id_type m_default_cube{ id::invalid_id };
		id::id_type m_default_sphere{ id::invalid_id };
		id::id_type m_default_plane{ id::invalid_id };
		id::id_type m_default_cylinder{ id::invalid_id };
		id::id_type m_default_cone{ id::invalid_id };
		id::id_type m_default_white_texture{ id::invalid_id };
		id::id_type m_default_material{ id::invalid_id };
	};
}
