#pragma once

#include "../../Common/CommonHeaders.h"
#include "../../Common/Id.h"
#include "Mesh.h"
#include "Texture.h"
#include "Material.h"
#include "../Geometry/IMeshGenerator.h"
#include "../Importers/ModelImporter.h"
#include "../Importers/TextureImporter.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <unordered_map>
#include <memory>
#include <string>
#include <vector>

namespace vortex::graphics
{
	using Microsoft::WRL::ComPtr;
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
		id::id_type create_inverted_sphere(float radius = 0.5f, u32 slices = 32, u32 stacks = 16);
		id::id_type create_primitive_plane(float width = 1.0f, float depth = 1.0f);
		id::id_type create_primitive_cylinder(float radius = 0.5f, float height = 1.0f, u32 slices = 32);
		id::id_type create_primitive_cone(float radius = 0.5f, float height = 1.0f, u32 slices = 32);
		Mesh* get_mesh(id::id_type id);
		void destroy_mesh(id::id_type id);
		std::vector<id::id_type> get_all_mesh_ids() const;

		// Geometric LOD: each imported base mesh gets a chain of decimated lower-poly meshes for distant
		// rendering. lods[0] = the full-res base (unchanged id, so existing scenes keep working).
		struct LodChain
		{
			id::id_type lods[4]{ id::invalid_id, id::invalid_id, id::invalid_id, id::invalid_id };
			u32 lod_count{ 1 };
			float radius{ 1.0f };   // base mesh radius (local units) — for distance thresholds
		};
		const LodChain* get_lod_chain(id::id_type base_mesh_id) const;

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
		/// <summary>Import a texture from an in-memory buffer (packed asset pak loaded into RAM).</summary>
		id::id_type import_texture_from_memory(const u8* data, u64 length, const std::string& name = "");
		bool export_mesh_to_vmesh(id::id_type mesh_id, const std::string& filepath);
		id::id_type load_vmesh(const std::string& filepath);

		// Multi-material import result structure
		struct SubmeshImportResult
		{
			id::id_type mesh_id{ id::invalid_id };
			id::id_type material_id{ id::invalid_id };
			id::id_type texture_id{ id::invalid_id };
			u32 material_index{ 0 };
			std::string name;
		};

		struct MultiMaterialImportResult
		{
			std::vector<SubmeshImportResult> submeshes;
			std::string model_name;
			bool success{ false };
		};

		// Import model with separate meshes and materials per submesh
		MultiMaterialImportResult import_model_with_materials(const std::string& filepath);
		/// <summary>Import a multi-material model from an in-memory buffer (packed asset pak loaded into RAM).</summary>
		MultiMaterialImportResult import_model_with_materials_from_memory(const u8* data, u64 length,
			const std::string& ext_hint, const std::string& virtual_dir);

		// Default resources
		id::id_type default_cube_mesh() const { return m_default_cube; }
		id::id_type default_sphere_mesh() const { return m_default_sphere; }
		id::id_type default_plane_mesh() const { return m_default_plane; }
		id::id_type default_white_texture() const { return m_default_white_texture; }
		id::id_type default_material() const { return m_default_material; }

		bool is_initialized() const { return m_device != nullptr; }

		// Get SRV descriptor heap for rendering
		ID3D12DescriptorHeap* srv_heap() const { return m_srv_heap.Get(); }

	private:
		ResourceRegistry() = default;

		id::id_type create_mesh_from_submesh(const SubMeshData& submesh, const std::string& name);
		void register_lod_chain(id::id_type base_mesh_id, const SubMeshData& submesh, const std::string& name);
		std::unordered_map<id::id_type, LodChain> m_lod_chains;
		// Shared cores so the file-based and in-memory import paths build resources identically.
		id::id_type create_texture_from_image(ImageData& image_data, const std::string& label);
		MultiMaterialImportResult build_model_result(ImportedModelData& model_data);

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

		// SRV Descriptor Heap for texture sampling
		ComPtr<ID3D12DescriptorHeap> m_srv_heap;
		UINT m_srv_descriptor_size{ 0 };
		UINT m_next_srv_index{ 0 };
		static constexpr UINT MAX_SRV_DESCRIPTORS = 1024;

		bool create_srv_heap();
		void assign_srv_to_texture(Texture* texture);
	};
}
