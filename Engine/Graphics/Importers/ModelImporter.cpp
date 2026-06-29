#include "ModelImporter.h"

#ifdef VORTEX_USE_ASSIMP
#include <assimp/Importer.hpp>
#include <assimp/scene.h>
#include <assimp/postprocess.h>
#endif

#include <algorithm>
#include <limits>
#include <Windows.h>

namespace vortex::graphics
{
#ifdef VORTEX_USE_ASSIMP
	ImportedModelData ModelImporter::import_from_file(const std::string& filepath)
	{
		ImportedModelData result;

		Assimp::Importer importer;
		
		const aiScene* scene = importer.ReadFile(filepath,
			aiProcess_Triangulate |
			aiProcess_GenNormals |
			aiProcess_CalcTangentSpace |
			aiProcess_FlipUVs |
			aiProcess_JoinIdenticalVertices |
			aiProcess_SortByPType);

		if (!scene || scene->mFlags & AI_SCENE_FLAGS_INCOMPLETE || !scene->mRootNode)
		{
			OutputDebugStringA(("ModelImporter: Failed to load " + filepath + "\n").c_str());
			return result;
		}

		// Extract filename as model name
		size_t last_slash = filepath.find_last_of("/\\");
		size_t last_dot = filepath.find_last_of(".");
		
		if (last_dot != std::string::npos && 
			(last_slash == std::string::npos || last_dot > last_slash))
		{
			size_t start = (last_slash != std::string::npos) ? last_slash + 1 : 0;
			result.name = filepath.substr(start, last_dot - start);
		}
		else
		{
			result.name = "ImportedModel";
		}

		OutputDebugStringA(("ModelImporter: Loading " + result.name + "\n").c_str());

		// Process node hierarchy
		process_node(scene->mRootNode, (void*)scene, result);

		// Extract materials and assign textures to submeshes
		extract_materials((void*)scene, result, filepath);

		// Calculate bounding box
		calculate_bounds(result);

		OutputDebugStringA(("ModelImporter: Loaded " + std::to_string(result.submeshes.size()) + " submeshes\n").c_str());

		return result;
	}
	ImportedModelData ModelImporter::import_from_memory(const u8* data, u64 length,
		const std::string& ext_hint, const std::string& virtual_dir)
	{
		ImportedModelData result;
		if (!data || length == 0) return result;

		Assimp::Importer importer;
		const aiScene* scene = importer.ReadFileFromMemory(data, (size_t)length,
			aiProcess_Triangulate |
			aiProcess_GenNormals |
			aiProcess_CalcTangentSpace |
			aiProcess_FlipUVs |
			aiProcess_JoinIdenticalVertices |
			aiProcess_SortByPType,
			ext_hint.c_str());

		if (!scene || scene->mFlags & AI_SCENE_FLAGS_INCOMPLETE || !scene->mRootNode)
		{
			OutputDebugStringA("ModelImporter: Failed to load model from memory\n");
			return result;
		}

		result.name = "MemoryModel";
		process_node(scene->mRootNode, (void*)scene, result);

		// Build relative texture paths off the virtual folder; never touch the disk (assets are in the pak).
		std::string base = virtual_dir;
		if (!base.empty() && base.back() != '/' && base.back() != '\\') base += "/";
		extract_materials((void*)scene, result, base + "model", /*allow_disk_search*/ false);

		calculate_bounds(result);
		return result;
	}
#else
	ImportedModelData ModelImporter::import_from_file(const std::string& filepath)
	{
		return ImportedModelData();
	}
	ImportedModelData ModelImporter::import_from_memory(const u8*, u64, const std::string&, const std::string&)
	{
		return ImportedModelData();
	}
#endif

	bool ModelImporter::is_format_supported(const std::string& extension)
	{
		static const std::vector<std::string> supported = {
			".fbx", ".obj", ".gltf", ".glb", ".dae", ".blend", ".3ds", ".ase"
		};

		std::string ext_lower = extension;
		std::transform(ext_lower.begin(), ext_lower.end(), ext_lower.begin(), ::tolower);

		return std::find(supported.begin(), supported.end(), ext_lower) != supported.end();
	}

#ifdef VORTEX_USE_ASSIMP
	void ModelImporter::calculate_bounds(ImportedModelData& data)
	{
		if (data.submeshes.empty())
			return;

		DirectX::XMFLOAT3 min_bound{ FLT_MAX, FLT_MAX, FLT_MAX };
		DirectX::XMFLOAT3 max_bound{ -FLT_MAX, -FLT_MAX, -FLT_MAX };

		for (const auto& submesh : data.submeshes)
		{
			for (const auto& vertex : submesh.vertices)
			{
				min_bound.x = std::min(min_bound.x, vertex.position.x);
				min_bound.y = std::min(min_bound.y, vertex.position.y);
				min_bound.z = std::min(min_bound.z, vertex.position.z);

				max_bound.x = std::max(max_bound.x, vertex.position.x);
				max_bound.y = std::max(max_bound.y, vertex.position.y);
				max_bound.z = std::max(max_bound.z, vertex.position.z);
			}
		}

		data.bounds_min = min_bound;
		data.bounds_max = max_bound;
	}

	void ModelImporter::process_node(void* node_ptr, void* scene_ptr, ImportedModelData& data)
	{
		aiNode* node = static_cast<aiNode*>(node_ptr);
		const aiScene* scene = static_cast<const aiScene*>(scene_ptr);

		for (u32 i = 0; i < node->mNumMeshes; i++)
		{
			aiMesh* mesh = scene->mMeshes[node->mMeshes[i]];
			SubMeshData submesh = process_mesh(mesh, (void*)scene);
			if (!submesh.vertices.empty())
			{
				data.submeshes.push_back(std::move(submesh));
			}
		}

		for (u32 i = 0; i < node->mNumChildren; i++)
		{
			process_node(node->mChildren[i], scene_ptr, data);
		}
	}

	SubMeshData ModelImporter::process_mesh(void* mesh_ptr, void* scene_ptr)
	{
		aiMesh* mesh = static_cast<aiMesh*>(mesh_ptr);
		SubMeshData result;

		result.name = mesh->mName.C_Str();
		result.material_index = mesh->mMaterialIndex;

		result.vertices.reserve(mesh->mNumVertices);
		for (u32 i = 0; i < mesh->mNumVertices; i++)
		{
			VertexPosNormalUV vertex;

			vertex.position.x = mesh->mVertices[i].x;
			vertex.position.y = mesh->mVertices[i].y;
			vertex.position.z = mesh->mVertices[i].z;

			if (mesh->HasNormals())
			{
				vertex.normal.x = mesh->mNormals[i].x;
				vertex.normal.y = mesh->mNormals[i].y;
				vertex.normal.z = mesh->mNormals[i].z;
			}
			else
			{
				vertex.normal = { 0.0f, 1.0f, 0.0f };
			}

			if (mesh->mTextureCoords[0])
			{
				vertex.uv.x = mesh->mTextureCoords[0][i].x;
				vertex.uv.y = mesh->mTextureCoords[0][i].y;
			}
			else
			{
				vertex.uv = { 0.0f, 0.0f };
			}

			result.vertices.push_back(vertex);
		}

		for (u32 i = 0; i < mesh->mNumFaces; i++)
		{
			aiFace face = mesh->mFaces[i];
			for (u32 j = 0; j < face.mNumIndices; j++)
			{
				result.indices.push_back(face.mIndices[j]);
			}
		}

		return result;
	}

	void ModelImporter::extract_materials(void* scene_ptr, ImportedModelData& data, const std::string& filepath, bool allow_disk_search)
	{
		const aiScene* scene = static_cast<const aiScene*>(scene_ptr);
		if (!scene) return;

		std::string model_dir;
		size_t last_slash = filepath.find_last_of("/\\");
		if (last_slash != std::string::npos)
		{
			model_dir = filepath.substr(0, last_slash + 1);
		}

		OutputDebugStringA(("ModelImporter: Found " + std::to_string(scene->mNumMaterials) + " materials\n").c_str());

		for (u32 i = 0; i < scene->mNumMaterials; i++)
		{
			aiMaterial* material = scene->mMaterials[i];
			
			aiString name;
			material->Get(AI_MATKEY_NAME, name);
			std::string mat_name = name.C_Str();
			data.material_names.push_back(mat_name);

			// Read the material's flat base/diffuse color — Kenney + most glTF/OBJ assets are colored, not textured.
			float cr = 0.8f, cg = 0.8f, cb = 0.8f, ca = 1.0f;
			aiColor4D mcol;
			if (material->Get(AI_MATKEY_COLOR_DIFFUSE, mcol) == AI_SUCCESS) { cr = mcol.r; cg = mcol.g; cb = mcol.b; ca = mcol.a; }
			if (material->Get(AI_MATKEY_BASE_COLOR, mcol) == AI_SUCCESS) { cr = mcol.r; cg = mcol.g; cb = mcol.b; ca = mcol.a; } // glTF PBR wins

			// PBR metallic/roughness factors (glTF / FBX-PBR); defaults if the material has none.
			float mat_metallic = 0.0f, mat_roughness = 0.5f;
			material->Get(AI_MATKEY_METALLIC_FACTOR, mat_metallic);
			material->Get(AI_MATKEY_ROUGHNESS_FACTOR, mat_roughness);

			OutputDebugStringA(("  Material " + std::to_string(i) + ": " + mat_name + "\n").c_str());

			// Get diffuse texture path from material
			std::string diffuse_path;
			if (material->GetTextureCount(aiTextureType_DIFFUSE) > 0)
			{
			aiString tex_path;
			material->GetTexture(aiTextureType_DIFFUSE, 0, &tex_path);
			diffuse_path = model_dir + tex_path.C_Str();
			data.texture_paths.push_back(diffuse_path);
			OutputDebugStringA(("    Diffuse: " + diffuse_path + "\n").c_str());
			}
			// Also check for AMBIENT texture (sometimes used as diffuse in OBJ)
			else if (material->GetTextureCount(aiTextureType_AMBIENT) > 0)
			{
			aiString tex_path;
			material->GetTexture(aiTextureType_AMBIENT, 0, &tex_path);
			diffuse_path = model_dir + tex_path.C_Str();
			data.texture_paths.push_back(diffuse_path);
			OutputDebugStringA(("    Ambient (as diffuse): " + diffuse_path + "\n").c_str());
			}
			
			// Get normal map path
			std::string normal_path;
			if (material->GetTextureCount(aiTextureType_NORMALS) > 0)
			{
				aiString tex_path;
				material->GetTexture(aiTextureType_NORMALS, 0, &tex_path);
				normal_path = model_dir + tex_path.C_Str();
			}
			else if (material->GetTextureCount(aiTextureType_HEIGHT) > 0)
			{
				aiString tex_path;
				material->GetTexture(aiTextureType_HEIGHT, 0, &tex_path);
				normal_path = model_dir + tex_path.C_Str();
			}

			// PBR slots — each model is individual, so read whichever the material actually has (empty if none).
			// The editor builds its slot UI dynamically from these per-material results.
			auto read_tex = [&](aiTextureType t) -> std::string {
				if (material->GetTextureCount(t) > 0) { aiString p; material->GetTexture(t, 0, &p); return model_dir + p.C_Str(); }
				return std::string();
			};
			std::string metallic_path = read_tex(aiTextureType_METALNESS);
			std::string roughness_path = read_tex(aiTextureType_DIFFUSE_ROUGHNESS);
			std::string ao_path = read_tex(aiTextureType_AMBIENT_OCCLUSION);
			if (ao_path.empty()) ao_path = read_tex(aiTextureType_LIGHTMAP);
			std::string emissive_path = read_tex(aiTextureType_EMISSIVE);
			// glTF packs metallic+roughness in one texture under UNKNOWN; use it for both if the dedicated ones are empty.
			if (metallic_path.empty() && roughness_path.empty())
			{
				std::string mr = read_tex(aiTextureType_UNKNOWN);
				if (!mr.empty()) { metallic_path = mr; roughness_path = mr; }
			}

			// Assign textures to all submeshes using this material
			for (auto& submesh : data.submeshes)
			{
				if (submesh.material_index == i)
				{
					submesh.base_color[0] = cr; submesh.base_color[1] = cg; submesh.base_color[2] = cb; submesh.base_color[3] = ca;
					submesh.metallic = mat_metallic;
					submesh.roughness = mat_roughness;
					submesh.diffuse_texture = diffuse_path;
					submesh.normal_texture = normal_path;
					submesh.metallic_texture = metallic_path;
					submesh.roughness_texture = roughness_path;
					submesh.ao_texture = ao_path;
					submesh.emissive_texture = emissive_path;

					// Use material name if submesh name is empty
					if (submesh.name.empty())
					{
						submesh.name = mat_name;
					}
					
					OutputDebugStringA(("    Assigned to submesh: " + submesh.name + "\n").c_str());
				}
			}
		}
		
		// If no textures found in model, search in directory (disk only — skipped for in-memory/pak loads).
		if (allow_disk_search)
		{
			bool has_valid_texture = false;
			for (const auto& tex_path : data.texture_paths)
			{
			if (!tex_path.empty())
			{
			DWORD attribs = GetFileAttributesA(tex_path.c_str());
			if (attribs != INVALID_FILE_ATTRIBUTES && !(attribs & FILE_ATTRIBUTE_DIRECTORY))
			{
			has_valid_texture = true;
			break;
			}
			}
			}

			if (data.texture_paths.empty() || !has_valid_texture)
			{
			OutputDebugStringA("ModelImporter: No valid textures found, searching directory...\n");
			search_textures_in_directory(model_dir, data);
			}
		}
	}
	
	void ModelImporter::search_textures_in_directory(const std::string& dir, ImportedModelData& data)
	{
		if (dir.empty()) return;
		
		// Collect all color textures in directory
		std::vector<std::string> color_textures;
		
		WIN32_FIND_DATAA fd;
		HANDLE h = FindFirstFileA((dir + "*.*").c_str(), &fd);
		if (h != INVALID_HANDLE_VALUE)
		{
			do {
				std::string fn = fd.cFileName;
				std::string fnl = fn;
				std::transform(fnl.begin(), fnl.end(), fnl.begin(), ::tolower);
				
				bool is_img = fnl.find(".png") != std::string::npos || 
				              fnl.find(".jpg") != std::string::npos || 
				              fnl.find(".tga") != std::string::npos;
				bool is_col = fnl.find("_col") != std::string::npos || 
				              fnl.find("col.") != std::string::npos || 
				              fnl.find("diffuse") != std::string::npos ||
				              fnl.find("albedo") != std::string::npos;
				bool is_bad = fnl.find("_nor") != std::string::npos || 
				              fnl.find("_ao") != std::string::npos || 
				              fnl.find("_rough") != std::string::npos ||
				              fnl.find("_metal") != std::string::npos;
				
				if (is_img && is_col && !is_bad)
				{
					color_textures.push_back(dir + fn);
					OutputDebugStringA(("  Found texture: " + fn + "\n").c_str());
				}
			} while (FindNextFileA(h, &fd));
			FindClose(h);
		}
		
		// Assign textures to submeshes
		for (size_t i = 0; i < data.submeshes.size(); i++)
		{
			auto& submesh = data.submeshes[i];
			
			// Try to match by name first
			std::string search_name = submesh.name;
			std::transform(search_name.begin(), search_name.end(), search_name.begin(), ::tolower);
			std::replace(search_name.begin(), search_name.end(), ' ', '_');
			
			bool found = false;
			for (const auto& tex : color_textures)
			{
				std::string tex_lower = tex;
				std::transform(tex_lower.begin(), tex_lower.end(), tex_lower.begin(), ::tolower);
				
				if (tex_lower.find(search_name) != std::string::npos)
				{
					submesh.diffuse_texture = tex;
					found = true;
					OutputDebugStringA(("  Matched " + submesh.name + " -> " + tex + "\n").c_str());
					break;
				}
			}
			
			// Fallback: assign by index
			if (!found && i < color_textures.size())
			{
				submesh.diffuse_texture = color_textures[i];
				OutputDebugStringA(("  Index match " + submesh.name + " -> " + color_textures[i] + "\n").c_str());
			}
		}
	}
#endif
}
