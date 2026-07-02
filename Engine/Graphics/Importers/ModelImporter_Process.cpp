#include "ModelImporter_Internal.h"

namespace vortex::graphics
{
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

	namespace
	{
		// Assimp matrices are column-vector convention (v' = M * v); the engine is row-vector
		// (mul(vec, mat), DirectXMath) — so conversion is a transpose.
		DirectX::XMFLOAT4X4 to_row_vector(const aiMatrix4x4& m)
		{
			return DirectX::XMFLOAT4X4(
				m.a1, m.b1, m.c1, m.d1,
				m.a2, m.b2, m.c2, m.d2,
				m.a3, m.b3, m.c3, m.d3,
				m.a4, m.b4, m.c4, m.d4);
		}

		s32 find_node_index(const ImportedModelData& data, const char* name)
		{
			for (size_t i = 0; i < data.nodes.size(); ++i)
				if (data.nodes[i].name == name) return (s32)i;
			return -1;
		}

		void collect_nodes(const aiNode* node, s32 parent, ImportedModelData& data)
		{
			SkeletonNodeData nd;
			nd.name = node->mName.C_Str();
			nd.parent = parent;
			nd.local_bind = to_row_vector(node->mTransformation);
			s32 self = (s32)data.nodes.size();
			data.nodes.push_back(std::move(nd));
			for (u32 i = 0; i < node->mNumChildren; ++i)
				collect_nodes(node->mChildren[i], self, data);
		}
	}

	// Build the node hierarchy + compact bone palette. Only runs its full work when the scene actually
	// carries bones or animations, so static models pay nothing.
	void ModelImporter::build_skeleton(void* scene_ptr, ImportedModelData& data)
	{
		const aiScene* scene = static_cast<const aiScene*>(scene_ptr);
		if (!scene || !scene->mRootNode) return;

		bool any_bones = false;
		for (u32 m = 0; m < scene->mNumMeshes; ++m)
			if (scene->mMeshes[m]->mNumBones > 0) { any_bones = true; break; }
		if (!any_bones && scene->mNumAnimations == 0) return;

		collect_nodes(scene->mRootNode, -1, data);

		// Compact bone palette: every aiBone (deduped by node name) gets one entry. Vertex indices are
		// u8, so the palette caps at 255 — beyond that, extra bones are dropped (weights renormalize).
		for (u32 m = 0; m < scene->mNumMeshes; ++m)
		{
			const aiMesh* mesh = scene->mMeshes[m];
			for (u32 b = 0; b < mesh->mNumBones; ++b)
			{
				const aiBone* bone = mesh->mBones[b];
				s32 node = find_node_index(data, bone->mName.C_Str());
				if (node < 0) continue;
				bool known = false;
				for (const auto& e : data.bones)
					if (e.node_index == (u32)node)
					{
						known = true;
						// aiBone offset matrices are MESH-relative: a second mesh attached to a
						// differently-transformed node carries a DIFFERENT offset for the same bone.
						// v1 keeps ONE palette entry per node (first mesh wins) — warn on mismatch so
						// a distorted multi-mesh rig is diagnosable instead of silently wrong.
						DirectX::XMFLOAT4X4 other = to_row_vector(bone->mOffsetMatrix);
						const float* a = &e.inverse_bind._11;
						const float* o = &other._11;
						for (int k = 0; k < 16; ++k)
							if (fabsf(a[k] - o[k]) > 0.001f)
							{
								OutputDebugStringA(("ModelImporter: bone '" + std::string(bone->mName.C_Str())
									+ "' has mesh-dependent offset matrices — multi-mesh rig may deform incorrectly\n").c_str());
								break;
							}
						break;
					}
				if (known) continue;
				if (data.bones.size() >= 255)
				{
					OutputDebugStringA("ModelImporter: bone palette full (255) — extra bones dropped\n");
					break;
				}
				SkeletonBoneData bd;
				bd.node_index = (u32)node;
				bd.inverse_bind = to_row_vector(bone->mOffsetMatrix);
				data.bones.push_back(bd);
			}
		}

		if (!data.bones.empty())
			OutputDebugStringA(("ModelImporter: skeleton — " + std::to_string(data.nodes.size()) + " nodes, "
				+ std::to_string(data.bones.size()) + " bones\n").c_str());
	}

	void ModelImporter::extract_animations(void* scene_ptr, ImportedModelData& data)
	{
		const aiScene* scene = static_cast<const aiScene*>(scene_ptr);
		if (!scene || scene->mNumAnimations == 0 || data.nodes.empty()) return;

		for (u32 a = 0; a < scene->mNumAnimations; ++a)
		{
			const aiAnimation* anim = scene->mAnimations[a];
			double tps = anim->mTicksPerSecond > 0.0 ? anim->mTicksPerSecond : 25.0;

			AnimationClipData clip;
			clip.name = anim->mName.length > 0 ? anim->mName.C_Str() : ("Clip" + std::to_string(a));
			clip.duration_sec = (float)(anim->mDuration / tps);

			for (u32 c = 0; c < anim->mNumChannels; ++c)
			{
				const aiNodeAnim* ch = anim->mChannels[c];
				s32 node = find_node_index(data, ch->mNodeName.C_Str());
				if (node < 0) continue;

				AnimChannelData channel;
				channel.node_index = node;
				channel.position_keys.reserve(ch->mNumPositionKeys);
				for (u32 k = 0; k < ch->mNumPositionKeys; ++k)
				{
					const auto& key = ch->mPositionKeys[k];
					channel.position_keys.push_back({ (float)(key.mTime / tps), key.mValue.x, key.mValue.y, key.mValue.z });
				}
				channel.rotation_keys.reserve(ch->mNumRotationKeys);
				for (u32 k = 0; k < ch->mNumRotationKeys; ++k)
				{
					const auto& key = ch->mRotationKeys[k];
					channel.rotation_keys.push_back({ (float)(key.mTime / tps), key.mValue.x, key.mValue.y, key.mValue.z, key.mValue.w });
				}
				channel.scale_keys.reserve(ch->mNumScalingKeys);
				for (u32 k = 0; k < ch->mNumScalingKeys; ++k)
				{
					const auto& key = ch->mScalingKeys[k];
					channel.scale_keys.push_back({ (float)(key.mTime / tps), key.mValue.x, key.mValue.y, key.mValue.z });
				}
				clip.channels.push_back(std::move(channel));
			}

			if (!clip.channels.empty())
				data.animations.push_back(std::move(clip));
		}

		if (!data.animations.empty())
			OutputDebugStringA(("ModelImporter: " + std::to_string(data.animations.size()) + " animation clip(s)\n").c_str());
	}

	void ModelImporter::process_node(void* node_ptr, void* scene_ptr, ImportedModelData& data)
	{
		aiNode* node = static_cast<aiNode*>(node_ptr);
		const aiScene* scene = static_cast<const aiScene*>(scene_ptr);

		for (u32 i = 0; i < node->mNumMeshes; i++)
		{
			aiMesh* mesh = scene->mMeshes[node->mMeshes[i]];
			SubMeshData submesh = process_mesh(mesh, (void*)scene, data);
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

	SubMeshData ModelImporter::process_mesh(void* mesh_ptr, void* scene_ptr, ImportedModelData& data)
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

		// Skinning: distribute each bone's weights into the per-vertex 4-slot table (LimitBoneWeights
		// already capped influences at 4; the smallest-weight replacement below is belt-and-braces).
		if (mesh->mNumBones > 0 && !data.bones.empty())
		{
			result.skin.resize(mesh->mNumVertices);
			for (u32 b = 0; b < mesh->mNumBones; ++b)
			{
				const aiBone* bone = mesh->mBones[b];
				s32 node = find_node_index(data, bone->mName.C_Str());
				if (node < 0) continue;
				s32 palette = -1;
				for (size_t p = 0; p < data.bones.size(); ++p)
					if (data.bones[p].node_index == (u32)node) { palette = (s32)p; break; }
				if (palette < 0) continue;   // dropped by the 255-bone cap

				for (u32 w = 0; w < bone->mNumWeights; ++w)
				{
					const aiVertexWeight& vw = bone->mWeights[w];
					if (vw.mVertexId >= result.skin.size() || vw.mWeight <= 0.0f) continue;
					VertexSkin& vs = result.skin[vw.mVertexId];
					int slot = -1;
					for (int s = 0; s < 4; ++s)
						if (vs.bone_weights[s] == 0.0f) { slot = s; break; }
					if (slot < 0)
					{
						int smallest = 0;
						for (int s = 1; s < 4; ++s)
							if (vs.bone_weights[s] < vs.bone_weights[smallest]) smallest = s;
						if (vs.bone_weights[smallest] < vw.mWeight) slot = smallest;
					}
					if (slot >= 0)
					{
						vs.bone_indices[slot] = (u8)palette;
						vs.bone_weights[slot] = vw.mWeight;
					}
				}
			}

			// Normalize (sum -> 1). Vertices no bone touched fall back to full weight on palette entry 0.
			for (auto& vs : result.skin)
			{
				float sum = vs.bone_weights[0] + vs.bone_weights[1] + vs.bone_weights[2] + vs.bone_weights[3];
				if (sum > 0.0001f)
				{
					for (int s = 0; s < 4; ++s) vs.bone_weights[s] /= sum;
				}
				else
				{
					vs.bone_indices[0] = 0;
					vs.bone_weights[0] = 1.0f;
				}
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
