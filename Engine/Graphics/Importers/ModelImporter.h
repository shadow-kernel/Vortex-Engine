#pragma once

#include "../../Common/CommonHeaders.h"
#include "../Geometry/IMeshGenerator.h"
#include <string>
#include <vector>
#include <memory>

namespace vortex::graphics
{
	struct SubMeshData
	{
	std::vector<VertexPosNormalUV> vertices;
	std::vector<u32> indices;
	u32 material_index{ 0 };
	float base_color[4]{ 0.8f, 0.8f, 0.8f, 1.0f }; // diffuse/base color from the model's material (e.g. Kenney flat colors)
	std::string name;
	std::string diffuse_texture;   // Albedo/Diffuse texture
	std::string normal_texture;    // Normal map
	std::string metallic_texture;  // Metallic map
	std::string roughness_texture; // Roughness map
	std::string ao_texture;        // Ambient Occlusion map
	std::string emissive_texture;  // Emissive map
	};

	struct ImportedModelData
	{
		std::vector<SubMeshData> submeshes;
		std::vector<std::string> material_names;
		std::vector<std::string> texture_paths;
		DirectX::XMFLOAT3 bounds_min{ 0.0f, 0.0f, 0.0f };
		DirectX::XMFLOAT3 bounds_max{ 0.0f, 0.0f, 0.0f };
		std::string name;
		
		bool is_valid() const { return !submeshes.empty(); }
		void clear() 
		{ 
			submeshes.clear(); 
			material_names.clear();
			texture_paths.clear();
		}
	};

	/// <summary>
	/// Imports 3D models from various formats using Assimp.
	/// Supports FBX, OBJ, GLTF, and other common formats.
	/// </summary>
	class ModelImporter
	{
	public:
		ModelImporter() = default;
		~ModelImporter() = default;

		/// <summary>
		/// Import a model from file.
		/// </summary>
		/// <param name="filepath">Path to the model file</param>
		/// <returns>Imported model data or empty if failed</returns>
		static ImportedModelData import_from_file(const std::string& filepath);

		/// <summary>
		/// Import a model from an in-memory buffer (for packed/encrypted asset paks loaded into RAM).
		/// ext_hint is the bare extension ("obj","fbx",...) so Assimp can pick the right importer;
		/// virtual_dir is the model's virtual folder, used only to build relative texture paths.
		/// </summary>
		static ImportedModelData import_from_memory(const u8* data, u64 length,
			const std::string& ext_hint, const std::string& virtual_dir);

		/// <summary>
		/// Check if a file format is supported.
		/// </summary>
		static bool is_format_supported(const std::string& extension);

	private:
	static void calculate_bounds(ImportedModelData& data);
	static void process_node(void* node, void* scene, ImportedModelData& data);
	static SubMeshData process_mesh(void* mesh, void* scene);
	static void extract_materials(void* scene, ImportedModelData& data, const std::string& filepath, bool allow_disk_search = true);
	static void search_textures_in_directory(const std::string& dir, ImportedModelData& data);
	};
}
