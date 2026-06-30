#include "ResourceRegistry_Internal.h"

namespace vortex::graphics
{
	id::id_type ResourceRegistry::import_model(const std::string& filepath)
	{
		if (!m_device) 
		{
			OutputDebugStringA("ResourceRegistry::import_model - Device not initialized!\n");
			return id::invalid_id;
		}

		OutputDebugStringA(("ResourceRegistry: Importing model: " + filepath + "\n").c_str());

		ImportedModelData data = ModelImporter::import_from_file(filepath);
		if (!data.is_valid())
		{
			OutputDebugStringA("ResourceRegistry: ModelImporter returned invalid data!\n");
			return id::invalid_id;
		}

		OutputDebugStringA(("ResourceRegistry: Model has " + std::to_string(data.submeshes.size()) + " submeshes\n").c_str());

		MeshData combined_data;
		u32 index_offset = 0;

		for (const auto& submesh : data.submeshes)
		{
			for (const auto& vertex : submesh.vertices)
			{
				combined_data.vertices.push_back(vertex);
			}
			for (auto idx : submesh.indices)
			{
				combined_data.indices.push_back(idx + index_offset);
			}
			index_offset += static_cast<u32>(submesh.vertices.size());
		}

		return create_mesh(combined_data, data.name);
	}


	id::id_type ResourceRegistry::import_texture(const std::string& filepath, const std::string& name)
	{
		if (!m_device) return id::invalid_id;

		ImageData image_data = TextureImporter::import_from_file(filepath);
		if (!image_data.is_valid())
		{
			OutputDebugStringA(("Failed to load texture: " + filepath + "\n").c_str());
			return id::invalid_id;
		}
		return create_texture_from_image(image_data, filepath);
	}


	id::id_type ResourceRegistry::import_texture_from_memory(const u8* data, u64 length, const std::string& name)
	{
		if (!m_device) return id::invalid_id;

		ImageData image_data = TextureImporter::import_from_memory(data, length);
		if (!image_data.is_valid())
		{
			OutputDebugStringA("Failed to load texture from memory\n");
			return id::invalid_id;
		}
		return create_texture_from_image(image_data, name.empty() ? std::string("memtex") : name);
	}


	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::import_model_with_materials(const std::string& filepath)
	{
		MultiMaterialImportResult result;
		result.success = false;

		if (!m_device)
		{
			OutputDebugStringA("ResourceRegistry not initialized\n");
			return result;
		}

		OutputDebugStringA(("=== Multi-Material Import: " + filepath + " ===\n").c_str());

		// Import model - ModelImporter now handles texture assignment
		ImportedModelData model_data = ModelImporter::import_from_file(filepath);
		if (!model_data.is_valid())
		{
			OutputDebugStringA("Import failed - no valid data\n");
			return result;
		}
		return build_model_result(model_data);
	}


	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::import_model_with_materials_from_memory(
		const u8* data, u64 length, const std::string& ext_hint, const std::string& virtual_dir)
	{
		MultiMaterialImportResult result;
		result.success = false;
		if (!m_device)
		{
			OutputDebugStringA("ResourceRegistry not initialized\n");
			return result;
		}

		OutputDebugStringA(("=== Multi-Material Import (memory): ." + ext_hint + " ===\n").c_str());
		ImportedModelData model_data = ModelImporter::import_from_memory(data, length, ext_hint, virtual_dir);
		if (!model_data.is_valid())
		{
			OutputDebugStringA("Import from memory failed - no valid data\n");
			return result;
		}
		return build_model_result(model_data);
	}


	ResourceRegistry::MultiMaterialImportResult ResourceRegistry::build_model_result(ImportedModelData& model_data)
	{
		MultiMaterialImportResult result;
		result.success = false;

		result.model_name = model_data.name;
		OutputDebugStringA(("Model: " + model_data.name + ", " +
			std::to_string(model_data.submeshes.size()) + " submeshes\n").c_str());

		for (size_t i = 0; i < model_data.submeshes.size(); i++)
		{
			const auto& submesh = model_data.submeshes[i];
			SubmeshImportResult sub_result;
			sub_result.material_index = submesh.material_index;
			sub_result.name = submesh.name.empty() ? ("Submesh_" + std::to_string(i)) : submesh.name;

			OutputDebugStringA(("Processing: " + sub_result.name + "\n").c_str());

			// Create mesh
			sub_result.mesh_id = create_mesh_from_submesh(submesh, sub_result.name);
			if (sub_result.mesh_id == id::invalid_id)
			{
				continue;
			}

			// Create material
			sub_result.material_id = create_material(sub_result.name + "_material");
			if (sub_result.material_id == id::invalid_id)
			{
				continue;
			}

			auto* mat = get_material(sub_result.material_id);
			if (mat)
			{
				mat->set_base_color({ submesh.base_color[0], submesh.base_color[1], submesh.base_color[2], submesh.base_color[3] });
			}

			// Load texture - ModelImporter already assigned the correct path
			if (!submesh.diffuse_texture.empty())
			{
				OutputDebugStringA(("  Texture: " + submesh.diffuse_texture + "\n").c_str());
				sub_result.texture_id = import_texture(submesh.diffuse_texture, sub_result.name + "_tex");
				if (sub_result.texture_id != id::invalid_id && mat)
				{
					auto* tex = get_texture(sub_result.texture_id);
					if (tex)
					{
						mat->set_albedo_texture(tex);
						OutputDebugStringA("  Texture bound OK\n");
					}
				}
			}
			else
			{
				OutputDebugStringA("  No texture assigned\n");
			}

			result.submeshes.push_back(sub_result);
		}

		result.success = !result.submeshes.empty();
		OutputDebugStringA(("=== Import Complete: " + std::to_string(result.submeshes.size()) + " submeshes ===\n").c_str());

		return result;
	}


}
