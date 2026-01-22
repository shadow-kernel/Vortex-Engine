#include "ModelImporter.h"
#include <assimp/Importer.hpp>
#include <assimp/scene.h>
#include <assimp/postprocess.h>
#include <algorithm>
#include <limits>

namespace vortex::graphics
{
	ImportedModelData ModelImporter::import_from_file(const std::string& filepath)
	{
		ImportedModelData result;

		Assimp::Importer importer;
		
		// Import with triangulation, normals generation, and UV flipping
		const aiScene* scene = importer.ReadFile(filepath,
			aiProcess_Triangulate |
			aiProcess_GenNormals |
			aiProcess_CalcTangentSpace |
			aiProcess_FlipUVs |
			aiProcess_JoinIdenticalVertices |
			aiProcess_SortByPType);

		if (!scene || scene->mFlags & AI_SCENE_FLAGS_INCOMPLETE || !scene->mRootNode)
		{
			return result; // Return empty on error
		}

		// Extract filename as model name
		size_t last_slash = filepath.find_last_of("/\\");
		size_t last_dot = filepath.find_last_of(".");
		if (last_slash != std::string::npos && last_dot != std::string::npos)
		{
			result.name = filepath.substr(last_slash + 1, last_dot - last_slash - 1);
		}
		else
		{
			result.name = "ImportedModel";
		}

		// Process node hierarchy
		process_node(scene->mRootNode, (void*)scene, result);

		// Calculate bounding box
		calculate_bounds(result);

		return result;
	}

	bool ModelImporter::is_format_supported(const std::string& extension)
	{
		static const std::vector<std::string> supported = {
			".fbx", ".obj", ".gltf", ".glb", ".dae", ".blend", ".3ds", ".ase"
		};

		std::string ext_lower = extension;
		std::transform(ext_lower.begin(), ext_lower.end(), ext_lower.begin(), ::tolower);

		return std::find(supported.begin(), supported.end(), ext_lower) != supported.end();
	}

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

		// Process all meshes in this node
		for (u32 i = 0; i < node->mNumMeshes; i++)
		{
			aiMesh* mesh = scene->mMeshes[node->mMeshes[i]];
			SubMeshData submesh = process_mesh(mesh, (void*)scene);
			if (!submesh.vertices.empty())
			{
				data.submeshes.push_back(std::move(submesh));
			}
		}

		// Recursively process child nodes
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

		// Process vertices
		result.vertices.reserve(mesh->mNumVertices);
		for (u32 i = 0; i < mesh->mNumVertices; i++)
		{
			VertexPosNormalUV vertex;

			// Position
			vertex.position.x = mesh->mVertices[i].x;
			vertex.position.y = mesh->mVertices[i].y;
			vertex.position.z = mesh->mVertices[i].z;

			// Normal
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

			// UV coordinates (use first texture coordinate set)
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

		// Process indices
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
}
