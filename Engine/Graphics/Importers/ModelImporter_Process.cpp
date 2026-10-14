#include "../../Common/VerboseLog.h"
#include "ModelImporter_Internal.h"
#include "TextureImporter.h"   // has_translucent_texels: a BLEND that cannot show through (#441)
#include <cctype>
#include <cstdio>
#include <cstring>
#include <vector>

namespace
{
	// true when the file exists and holds exactly these bytes
	bool same_file_bytes(const std::string& path, const void* bytes, size_t count)
	{
		FILE* f = nullptr;
		if (fopen_s(&f, path.c_str(), "rb") != 0 || !f) return false;
		fseek(f, 0, SEEK_END);
		const long size = ftell(f);
		bool same = size >= 0 && (size_t)size == count;
		if (same && count > 0)
		{
			fseek(f, 0, SEEK_SET);
			std::vector<unsigned char> buf(count);
			same = fread(buf.data(), 1, count, f) == count && memcmp(buf.data(), bytes, count) == 0;
		}
		fclose(f);
		return same;
	}
}

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

		// ---- glTF KHR_texture_transform (#470) ----
		// The engine samples every map of a material with ONE UV set, the vertex UVs, so a texture's offset / rotation /
		// scale is baked into them. Assimp's glTF2 importer stores the extension as an aiUVTransform rewritten for its
		// own pivot convention, and aiProcess_FlipUVs flips that once more (translation.y and rotation negated);
		// aiProcess_TransformUVCoords bakes it wrongly into our flipped UVs (only a pure offset came out right, and maps
		// with different transforms collapsed every vertex onto one UV). So the glTF values are recovered from it and
		// applied as the glTF spec defines them, uv' = offset + R(rotation) * (scale * uv) with R = [c s; -s c], to the
		// UVs in glTF's own convention — which the two V flips (Assimp's glTF reader, aiProcess_FlipUVs) restore.
		struct UvTransform { bool has{ false }; float ox{ 0.0f }, oy{ 0.0f }, rotation{ 0.0f }, sx{ 1.0f }, sy{ 1.0f }; };

		UvTransform gltf_uv_transform(const aiMaterial* mat, aiTextureType type)
		{
			UvTransform x;
			aiUVTransform t;
			if (mat->Get(AI_MATKEY_UVTRANSFORM(type, 0), t) != AI_SUCCESS) return x;
			const float c = cosf(t.mRotation), s = sinf(t.mRotation);
			x.has = true;
			x.rotation = t.mRotation;
			x.sx = t.mScaling.x;
			x.sy = t.mScaling.y;
			// inverse of glTF2Importer: translation = (sx/2 (1 - c + s) + offset.x, sy/2 (s + c - 1) + 1 - sy - offset.y),
			// with translation.y negated by aiProcess_FlipUVs
			x.ox = t.mTranslation.x - 0.5f * x.sx * (1.0f - c + s);
			x.oy = t.mTranslation.y + 0.5f * x.sy * (s + c - 1.0f) + 1.0f - x.sy;
			return x;
		}

		void apply_uv_transform(const UvTransform& x, float& u, float& v)
		{
			const float c = cosf(x.rotation), s = sinf(x.rotation);
			const float su = x.sx * u, sv = x.sy * v;
			u = x.ox + c * su + s * sv;
			v = x.oy - s * su + c * sv;
		}

		bool same_transform(const UvTransform& a, const UvTransform& b)
		{
			if (a.has != b.has) return false;
			if (!a.has) return true;
			auto eq = [](float p, float q) { return fabsf(p - q) <= 1e-5f * (std::max)(1.0f, (std::max)(fabsf(p), fabsf(q))); };
			return eq(a.ox, b.ox) && eq(a.oy, b.oy) && eq(a.rotation, b.rotation) && eq(a.sx, b.sx) && eq(a.sy, b.sy);
		}

		// the UV set a texture slot reads (glTF texCoord); a set the mesh does not have counts as set 0
		int uv_source(const aiMaterial* mat, aiTextureType type, const aiMesh* mesh)
		{
			int set = 0;
			mat->Get(AI_MATKEY_UVWSRC(type, 0), set);
			return (set >= 0 && set < AI_MAX_NUMBER_OF_TEXTURECOORDS && mesh->mTextureCoords[set]) ? set : 0;
		}

		bool same_uvs(const aiMesh* mesh, int a, int b)
		{
			if (a == b) return true;
			const aiVector3D* p = mesh->mTextureCoords[a];
			const aiVector3D* q = mesh->mTextureCoords[b];
			if (!p || !q) return false;
			for (u32 i = 0; i < mesh->mNumVertices; ++i)
				if (fabsf(p[i].x - q[i].x) > 1e-6f || fabsf(p[i].y - q[i].y) > 1e-6f) return false;
			return true;
		}

		// The UV set and transform a glTF mesh's vertex UVs carry: those of its base colour map, or of the first map present
		// when there is none. A map that needs another transform, or a UV set with other data, is sampled with them too —
		// one warning per material says so (#470).
		void plan_gltf_uvs(const aiMaterial* mat, const aiMesh* mesh, int& set, UvTransform& xf, ImportedModelData& data)
		{
			static const aiTextureType slots[] = { aiTextureType_DIFFUSE, aiTextureType_NORMALS, aiTextureType_METALNESS,
				aiTextureType_DIFFUSE_ROUGHNESS, aiTextureType_AMBIENT_OCCLUSION, aiTextureType_LIGHTMAP, aiTextureType_EMISSIVE };
			static const char* slot_names[] = { "base colour", "normal", "metallic", "roughness", "occlusion", "occlusion", "emissive" };
			constexpr int slot_count = sizeof(slots) / sizeof(slots[0]);
			int primary = -1;
			for (int i = 0; i < slot_count && primary < 0; ++i)
				if (mat->GetTextureCount(slots[i]) > 0) primary = i;
			if (primary < 0) return;   // untextured: the UVs stay as they are
			set = uv_source(mat, slots[primary], mesh);
			xf = gltf_uv_transform(mat, slots[primary]);

			bool conflict = false;
			for (int i = 0; i < slot_count && !conflict; ++i)
			{
				if (i == primary || mat->GetTextureCount(slots[i]) == 0) continue;
				conflict = !same_uvs(mesh, set, uv_source(mat, slots[i], mesh)) || !same_transform(xf, gltf_uv_transform(mat, slots[i]));
			}
			if (!conflict) return;
			aiString name;
			mat->Get(AI_MATKEY_NAME, name);
			const std::string warning = "glTF material '" + std::string(name.C_Str()) + "': its texture maps use different UV "
				"transforms or UV sets (KHR_texture_transform / texCoord); the engine samples every map with one UV set, so all of "
				"them use the " + slot_names[primary] + " map's";
			for (const auto& w : data.warnings) if (w == warning) return;
			data.warnings.push_back(warning);
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
								VORTEX_VLOG(("ModelImporter: bone '" + std::string(bone->mName.C_Str())
									+ "' has mesh-dependent offset matrices — multi-mesh rig may deform incorrectly\n").c_str());
								break;
							}
						break;
					}
				if (known) continue;
				if (data.bones.size() >= 255)
				{
					VORTEX_VLOG("ModelImporter: bone palette full (255) — extra bones dropped\n");
					break;
				}
				SkeletonBoneData bd;
				bd.node_index = (u32)node;
				bd.inverse_bind = to_row_vector(bone->mOffsetMatrix);
				data.bones.push_back(bd);
			}
		}

		if (!data.bones.empty())
			VORTEX_VLOG(("ModelImporter: skeleton — " + std::to_string(data.nodes.size()) + " nodes, "
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
			VORTEX_VLOG(("ModelImporter: " + std::to_string(data.animations.size()) + " animation clip(s)\n").c_str());
	}

	void ModelImporter::process_node(void* node_ptr, void* scene_ptr, ImportedModelData& data, const void* parent_transform)
	{
		aiNode* node = static_cast<aiNode*>(node_ptr);
		const aiScene* scene = static_cast<const aiScene*>(scene_ptr);
		// The node's world transform (#338). Every mesh used to be read in its own node space, so the wheels of a
		// glTF car — separate objects with their own pivots — all sat at the model origin.
		const aiMatrix4x4 world = parent_transform
			? (*static_cast<const aiMatrix4x4*>(parent_transform)) * node->mTransformation
			: node->mTransformation;

		for (u32 i = 0; i < node->mNumMeshes; i++)
		{
			aiMesh* mesh = scene->mMeshes[node->mMeshes[i]];
			SubMeshData submesh = process_mesh(mesh, (void*)scene, data, &world);
			if (!submesh.vertices.empty())
			{
				data.submeshes.push_back(std::move(submesh));
			}
		}

		for (u32 i = 0; i < node->mNumChildren; i++)
		{
			process_node(node->mChildren[i], scene_ptr, data, &world);
		}
	}

	SubMeshData ModelImporter::process_mesh(void* mesh_ptr, void* scene_ptr, ImportedModelData& data, const void* world_transform)
	{
		aiMesh* mesh = static_cast<aiMesh*>(mesh_ptr);
		SubMeshData result;

		result.name = mesh->mName.C_Str();
		result.material_index = mesh->mMaterialIndex;

		// glTF only (#338): unskinned geometry is baked into the node's world space so the parts keep their placement.
		// Skinned meshes stay in mesh space (their bind matrices refer to it, the skeleton carries the placement), and
		// FBX keeps the old reading — its root carries axis/unit conversions that every existing scene already
		// compensates for.
		const aiMatrix4x4* world = static_cast<const aiMatrix4x4*>(world_transform);
		const bool bake = data.gltf && world && !world->IsIdentity() && mesh->mNumBones == 0;
		aiMatrix3x3 normal_m;
		bool flip_winding = false;
		if (bake)
		{
			normal_m = aiMatrix3x3(*world);
			flip_winding = normal_m.Determinant() < 0.0f;   // a mirrored node turns the triangles inside out
			normal_m.Inverse().Transpose();
		}

		// glTF: the vertex UVs are the base colour map's UV set with its KHR_texture_transform baked in (#470) — a
		// Sketchfab scan quantises its UVs into 0..1/16 and scales every map by 16; read raw, every island sampled a
		// neighbouring piece of the atlas
		const aiScene* scene = static_cast<const aiScene*>(scene_ptr);
		int uv_set = 0;
		UvTransform uv_xf;
		if (data.gltf && scene && mesh->mMaterialIndex < scene->mNumMaterials)
			plan_gltf_uvs(scene->mMaterials[mesh->mMaterialIndex], mesh, uv_set, uv_xf, data);
		const aiVector3D* uvs = mesh->mTextureCoords[uv_set] ? mesh->mTextureCoords[uv_set] : mesh->mTextureCoords[0];

		result.vertices.reserve(mesh->mNumVertices);
		for (u32 i = 0; i < mesh->mNumVertices; i++)
		{
			VertexPosNormalUV vertex;

			aiVector3D p = mesh->mVertices[i];
			if (bake) p = (*world) * p;
			vertex.position.x = p.x;
			vertex.position.y = p.y;
			vertex.position.z = p.z;

			if (mesh->HasNormals())
			{
				aiVector3D nrm = mesh->mNormals[i];
				if (bake) { nrm = normal_m * nrm; nrm.NormalizeSafe(); }
				vertex.normal.x = nrm.x;
				vertex.normal.y = nrm.y;
				vertex.normal.z = nrm.z;
			}
			else
			{
				vertex.normal = { 0.0f, 1.0f, 0.0f };
			}

			if (uvs)
			{
				float u = uvs[i].x, v = uvs[i].y;
				if (uv_xf.has) apply_uv_transform(uv_xf, u, v);
				vertex.uv.x = u;
				vertex.uv.y = v;
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
			if (flip_winding && face.mNumIndices == 3)
			{
				result.indices.push_back(face.mIndices[0]);
				result.indices.push_back(face.mIndices[2]);
				result.indices.push_back(face.mIndices[1]);
				continue;
			}
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

		// Unique per-model prefix for extracted embedded textures. Without this, two GLBs sharing a folder
		// (e.g. the weapon body + its magazine + ejected casings) both write "embedded_0.png" and CLOBBER each
		// other -> wrong texture bleeds onto the wrong model. Prefixing with the model's file stem isolates them.
		std::string model_stem;
		{
			std::string base = (last_slash != std::string::npos) ? filepath.substr(last_slash + 1) : filepath;
			size_t dot = base.find_last_of('.');
			if (dot != std::string::npos) base = base.substr(0, dot);
			for (size_t i = 0; i < base.size(); ++i)
				model_stem += isalnum((unsigned char)base[i]) ? (char)base[i] : '_';
			if (model_stem.empty()) model_stem = "model";
		}

		// Resolve a texture reference to a real, loadable file path. EMBEDDED glTF/GLB textures come back
		// from assimp as a marker like "*0" (or a name GetEmbeddedTexture matches); `model_dir + "*0"` is a
		// bogus path, so the model rendered untextured (WHITE / flat plastic). Extract the embedded blob to a
		// real file next to the model and return that; loose textures keep the model_dir prefix.
		auto resolve_tex = [&](const char* raw) -> std::string {
			if (!raw || !raw[0]) return std::string();
			const aiTexture* emb = scene->GetEmbeddedTexture(raw);
			if (emb)
			{
				if (!allow_disk_search) return std::string(raw);   // pak/in-memory: keep marker for the managed resolver
				std::string ext;
				for (int c = 0; c < 8 && emb->achFormatHint[c]; ++c)
					if (isalnum((unsigned char)emb->achFormatHint[c])) ext += (char)tolower((unsigned char)emb->achFormatHint[c]);
				if (ext.empty()) ext = (emb->mHeight == 0) ? "png" : "bin";
				std::string tag;
				for (const char* p = raw; *p; ++p) if (isalnum((unsigned char)*p)) tag += *p;
				if (tag.empty()) tag = "0";
				std::string full = model_dir + "embedded_" + model_stem + "_" + tag + "." + ext;
				if (emb->mHeight == 0 && emb->mWidth > 0 && emb->pcData)   // compressed blob (PNG/JPG): mWidth = byte count
				{
					// written only when the bytes differ: every import used to rewrite the file, which changed its mtime
					// and with it the texture cache key, so each placement uploaded the embedded maps again (#357)
					if (!same_file_bytes(full, emb->pcData, emb->mWidth))
					{
						FILE* f = nullptr;
						if (fopen_s(&f, full.c_str(), "wb") == 0 && f) { fwrite(emb->pcData, 1, emb->mWidth, f); fclose(f); }
					}
				}
				return full;
			}
			return model_dir + raw;   // loose file on disk next to the model
		};

		// Does a material's albedo image have a texel that is not fully opaque (#441)? `raw` is the reference as the file
		// names it, `resolved` what resolve_tex made of it. An embedded image is read from its extracted copy (the verdict
		// is cached per file) or from the model; a file from disk or the shipped game's pak. Unreadable counts as yes.
		auto albedo_translucent = [&](const std::string& raw, const std::string& resolved) -> bool {
			if (raw.empty()) return false;
			bool known = false, translucent = false;
			if (const aiTexture* emb = scene->GetEmbeddedTexture(raw.c_str()))
			{
				if (emb->mHeight > 0)   // raw texels (ARGB8888)
				{
					for (u32 t = 0, n = emb->mWidth * emb->mHeight; t < n; ++t)
						if (emb->pcData[t].a < 255) return true;
					return false;
				}
				if (allow_disk_search) translucent = TextureImporter::has_translucent_texels(resolved, known);
				if (!known) translucent = TextureImporter::has_translucent_texels(reinterpret_cast<const u8*>(emb->pcData), emb->mWidth, known);
				return !known || translucent;
			}
			translucent = TextureImporter::has_translucent_texels(resolved, known);
			return !known || translucent;
		};

		VORTEX_VLOG(("ModelImporter: Found " + std::to_string(scene->mNumMaterials) + " materials\n").c_str());

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

			// glTF alphaMode + doubleSided (#441): MASK cuts out below alphaCutoff (glTF default 0.5), BLEND blends. They were
			// never read, so every leaf card, fence and window of a glTF imported opaque. Other formats carry no such keys.
			// doubleSided is honoured for the materials the file marks MASK or BLEND only: exporters write it on nearly every
			// material (125 of the Open World template's 136), and an OPAQUE closed mesh drawn without back-face culling only
			// costs GPU time — thousands of rock instances in the main pass. Opaque materials keep their culling.
			u32 blend_mode = 0;
			float alpha_cutoff = 0.0f;
			bool two_sided = false;
			bool gltf_blend = false;
			if (data.gltf)
			{
				aiString alpha_mode;
				bool see_through = false;   // MASK or BLEND in the file
				if (material->Get(AI_MATKEY_GLTF_ALPHAMODE, alpha_mode) == AI_SUCCESS)
				{
					if (strcmp(alpha_mode.C_Str(), "MASK") == 0)
					{
						float cutoff = 0.5f;
						material->Get(AI_MATKEY_GLTF_ALPHACUTOFF, cutoff);
						blend_mode = 3;
						alpha_cutoff = cutoff > 0.0f ? cutoff : 0.0f;
						see_through = true;
					}
					else if (strcmp(alpha_mode.C_Str(), "BLEND") == 0)
					{
						blend_mode = 1;
						gltf_blend = true;
						see_through = true;
					}
				}
				int double_sided = 0;
				if (see_through && material->Get(AI_MATKEY_TWOSIDED, double_sided) == AI_SUCCESS) two_sided = double_sided != 0;
			}

			// glTF emission (#450): emissiveFactor (black by default: an emissive map alone does not glow) and
			// KHR_materials_emissive_strength — the sidecar .vmat gets them as EmissiveColor / EmissiveStrength
			float emissive[3] = { 0.0f, 0.0f, 0.0f };
			float emissive_strength = 1.0f;
			if (data.gltf)
			{
				aiColor3D e(0.0f, 0.0f, 0.0f);
				if (material->Get(AI_MATKEY_COLOR_EMISSIVE, e) == AI_SUCCESS) { emissive[0] = e.r; emissive[1] = e.g; emissive[2] = e.b; }
				float strength = 1.0f;
				if (material->Get(AI_MATKEY_EMISSIVE_INTENSITY, strength) == AI_SUCCESS && strength >= 0.0f) emissive_strength = strength;
			}

			VORTEX_VLOG(("  Material " + std::to_string(i) + ": " + mat_name + "\n").c_str());

			// Get diffuse texture path from material
			std::string diffuse_path;
			std::string diffuse_raw;   // as the file names it ("*0" for an embedded image)
			if (material->GetTextureCount(aiTextureType_DIFFUSE) > 0)
			{
			aiString tex_path;
			material->GetTexture(aiTextureType_DIFFUSE, 0, &tex_path);
			diffuse_raw = tex_path.C_Str();
			diffuse_path = resolve_tex(tex_path.C_Str());
			data.texture_paths.push_back(diffuse_path);
			VORTEX_VLOG(("    Diffuse: " + diffuse_path + "\n").c_str());
			}
			// Also check for AMBIENT texture (sometimes used as diffuse in OBJ)
			else if (material->GetTextureCount(aiTextureType_AMBIENT) > 0)
			{
			aiString tex_path;
			material->GetTexture(aiTextureType_AMBIENT, 0, &tex_path);
			diffuse_path = resolve_tex(tex_path.C_Str());
			data.texture_paths.push_back(diffuse_path);
			VORTEX_VLOG(("    Ambient (as diffuse): " + diffuse_path + "\n").c_str());
			}
			
			// Get normal map path
			std::string normal_path;
			bool normal_is_bump = false;
			if (material->GetTextureCount(aiTextureType_NORMALS) > 0)
			{
				aiString tex_path;
				material->GetTexture(aiTextureType_NORMALS, 0, &tex_path);
				normal_path = resolve_tex(tex_path.C_Str());
			}
			else if (material->GetTextureCount(aiTextureType_HEIGHT) > 0)
			{
				aiString tex_path;
				material->GetTexture(aiTextureType_HEIGHT, 0, &tex_path);
				normal_path = resolve_tex(tex_path.C_Str());
				normal_is_bump = true;
			}

			// PBR slots — each model is individual, so read whichever the material actually has (empty if none).
			// The editor builds its slot UI dynamically from these per-material results.
			auto read_tex = [&](aiTextureType t) -> std::string {
				if (material->GetTextureCount(t) > 0) { aiString p; material->GetTexture(t, 0, &p); return resolve_tex(p.C_Str()); }
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
			// One texture for metallic AND roughness can only be a packed map; every common packing (glTF
			// metallicRoughness, ORM/ARM) keeps roughness in G and metallic in B. Occlusion stays R (glTF spec).
			const bool packed_mr = !metallic_path.empty() && metallic_path == roughness_path;

			// #453: a material without an albedo map is colour-only and keeps its colour — only a map it names but that is
			// missing, or no material from the file at all (Assimp's placeholder for an OBJ without .mtl), may be stood in
			// for by a texture found next to the model. A glTF always describes its materials (its default one included).
			const bool placeholder = !data.gltf && mat_name == AI_DEFAULT_MATERIAL_NAME;
			const u8 albedo_source = !diffuse_path.empty() ? 1 : (placeholder ? 2 : 0);

			// A BLEND that cannot show anything through is drawn opaque (#441): a base colour alpha of 1 and an albedo
			// image without a translucent texel (none at all, no alpha channel, or an alpha of 255 everywhere). Exporters
			// mark whole crates, fence wire and lamp glass BLEND whose alpha is 1 throughout, and the sorted transparent
			// pass gives them no shadow, no SSAO and no depth. The image is decoded once per file version; the verdict is
			// part of the import cache. Its doubleSided stays (the file marks it BLEND).
			if (gltf_blend && ca >= 1.0f && !albedo_translucent(diffuse_raw, diffuse_path)) blend_mode = 0;

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
					submesh.metallic_channel = packed_mr ? 2 : 0;
					submesh.roughness_channel = packed_mr ? 1 : 0;
					submesh.ao_channel = 0;
					submesh.normal_opengl = data.gltf;
					submesh.normal_is_bump = normal_is_bump;
					submesh.blend_mode = blend_mode;
					submesh.alpha_cutoff = alpha_cutoff;
					submesh.two_sided = two_sided;
					submesh.emissive_color[0] = emissive[0]; submesh.emissive_color[1] = emissive[1]; submesh.emissive_color[2] = emissive[2];
					submesh.emissive_strength = emissive_strength;
					submesh.albedo_source = albedo_source;

					// Use material name if submesh name is empty
					if (submesh.name.empty())
					{
						submesh.name = mat_name;
					}
					
					VORTEX_VLOG(("    Assigned to submesh: " + submesh.name + "\n").c_str());
				}
			}
		}
		
		// If no textures found in model, search in directory (disk only — skipped for in-memory/pak loads). Never for a glTF:
		// it describes every material, and the scan gave its colour-only parts some image from the folder (#453).
		if (allow_disk_search && !data.gltf)
		{
			bool has_valid_texture = false;
			for (const auto& tex_path : data.texture_paths)
			{
			if (!tex_path.empty())
			{
			std::error_code fs_error;
			if (std::filesystem::is_regular_file(std::filesystem::path(tex_path), fs_error))
			{
			has_valid_texture = true;
			break;
			}
			}
			}

			if (data.texture_paths.empty() || !has_valid_texture)
			{
			VORTEX_VLOG("ModelImporter: No valid textures found, searching directory...\n");
			search_textures_in_directory(model_dir, data);
			}
		}
	}
	
	void ModelImporter::search_textures_in_directory(const std::string& dir, ImportedModelData& data)
	{
		if (dir.empty()) return;
		
		// Collect all color textures in directory
		std::vector<std::string> color_textures;
		
		std::error_code fs_error;
		for (const auto& entry : std::filesystem::directory_iterator(std::filesystem::path(dir), fs_error))
		{
			{
				std::string fn = entry.path().filename().string();
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
				              fnl.find("_metal") != std::string::npos ||
				              fnl.rfind("embedded_", 0) == 0;   // a model's own extracted maps, bound by their role (#453)
				
				if (is_img && is_col && !is_bad)
				{
					color_textures.push_back(dir + fn);
					VORTEX_VLOG(("  Found texture: " + fn + "\n").c_str());
				}
			}
		}
		
		// directory order is the file system's (hash order on APFS): sorted, the same folder gives the same pick (#453) —
		// ignoring case like the editor's list (ordinal upper case, as .NET's OrdinalIgnoreCase), exact order on a tie
		std::sort(color_textures.begin(), color_textures.end(), [](const std::string& a, const std::string& b) {
			const size_t n = (std::min)(a.size(), b.size());
			for (size_t k = 0; k < n; ++k)
			{
				const int ca = toupper((unsigned char)a[k]), cb = toupper((unsigned char)b[k]);
				if (ca != cb) return ca < cb;
			}
			return a.size() != b.size() ? a.size() < b.size() : a < b;
		});
		
		// Assign textures to submeshes
		for (size_t i = 0; i < data.submeshes.size(); i++)
		{
			auto& submesh = data.submeshes[i];
			
			// only where the model wants a texture it does not have (#453): a map its material names but that is missing,
			// or no material in the file — a colour-only material keeps its colour
			std::error_code exists_error;
			const bool wanted = submesh.albedo_source == 2
				|| (submesh.albedo_source == 1 && !std::filesystem::is_regular_file(std::filesystem::path(submesh.diffuse_texture), exists_error));
			if (!wanted) continue;
			
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
					VORTEX_VLOG(("  Matched " + submesh.name + " -> " + tex + "\n").c_str());
					break;
				}
			}
			
			// Fallback: assign by index
			if (!found && i < color_textures.size())
			{
				submesh.diffuse_texture = color_textures[i];
				VORTEX_VLOG(("  Index match " + submesh.name + " -> " + color_textures[i] + "\n").c_str());
			}
		}
	}
#endif
}
