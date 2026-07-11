#include "../../Common/VerboseLog.h"
#include "ModelImporter_Internal.h"

namespace vortex::graphics
{
#ifdef VORTEX_USE_ASSIMP
	ImportedModelData ModelImporter::import_from_file(const std::string& filepath)
	{
		ImportedModelData result;

		Assimp::Importer importer;
		// FBX: collapse the $AssimpFbx$ pivot pseudo-node chains (Translation/PreRotation/...) into the
		// real nodes. Without this a Mixamo rig imports as ~200 unreadable pseudo-nodes; with it the
		// skeleton is the ~65 actual bones (animation channels are remapped by Assimp automatically).
		importer.SetPropertyBool(AI_CONFIG_IMPORT_FBX_PRESERVE_PIVOTS, false);

		const aiScene* scene = importer.ReadFile(filepath,
			aiProcess_Triangulate |
			aiProcess_GenNormals |
			aiProcess_CalcTangentSpace |
			aiProcess_FlipUVs |
			aiProcess_JoinIdenticalVertices |
			aiProcess_SortByPType |
			aiProcess_LimitBoneWeights);   // max 4 influences per vertex (matches the 52-byte skinned vertex)

		// Do NOT reject AI_SCENE_FLAGS_INCOMPLETE: animation-only FBX (Mixamo "Without Skin" pack clips) carry
		// no mesh, so Assimp marks the scene INCOMPLETE — but the skeleton node hierarchy and the animation
		// curves ARE present, and those are exactly what we extract. Only a null scene / missing root is fatal.
		if (!scene || !scene->mRootNode)
		{
			VORTEX_VLOG(("ModelImporter: Failed to load " + filepath + "\n").c_str());
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

		VORTEX_VLOG(("ModelImporter: Loading " + result.name + "\n").c_str());

		// Skeleton FIRST: process_mesh resolves aiBone node names against the node table.
		build_skeleton((void*)scene, result);

		// Process node hierarchy
		process_node(scene->mRootNode, (void*)scene, result);

		// Animation clips (keys converted to seconds; channels resolved to node indices).
		extract_animations((void*)scene, result);

		// Extract materials and assign textures to submeshes
		extract_materials((void*)scene, result, filepath);

		// Calculate bounding box
		calculate_bounds(result);

		VORTEX_VLOG(("ModelImporter: Loaded " + std::to_string(result.submeshes.size()) + " submeshes\n").c_str());

		return result;
	}
	ImportedModelData ModelImporter::import_from_memory(const u8* data, u64 length,
		const std::string& ext_hint, const std::string& virtual_dir)
	{
		ImportedModelData result;
		if (!data || length == 0) return result;

		Assimp::Importer importer;
		importer.SetPropertyBool(AI_CONFIG_IMPORT_FBX_PRESERVE_PIVOTS, false);   // see import_from_file
		const aiScene* scene = importer.ReadFileFromMemory(data, (size_t)length,
			aiProcess_Triangulate |
			aiProcess_GenNormals |
			aiProcess_CalcTangentSpace |
			aiProcess_FlipUVs |
			aiProcess_JoinIdenticalVertices |
			aiProcess_SortByPType |
			aiProcess_LimitBoneWeights,
			ext_hint.c_str());

		if (!scene || scene->mFlags & AI_SCENE_FLAGS_INCOMPLETE || !scene->mRootNode)
		{
			VORTEX_VLOG("ModelImporter: Failed to load model from memory\n");
			return result;
		}

		result.name = "MemoryModel";
		build_skeleton((void*)scene, result);
		process_node(scene->mRootNode, (void*)scene, result);
		extract_animations((void*)scene, result);

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

	std::vector<std::string> ModelImporter::extract_embedded_textures(const std::string& filepath, const std::string& out_dir)
	{
		std::vector<std::string> names;
#ifdef VORTEX_USE_ASSIMP
		Assimp::Importer importer;
		// No post-processing: we only need the embedded texture blobs, not geometry.
		const aiScene* scene = importer.ReadFile(filepath, 0);
		if (!scene || scene->mNumTextures == 0) return names;

		names.resize(scene->mNumTextures);
		for (unsigned i = 0; i < scene->mNumTextures; ++i)
		{
			const aiTexture* tex = scene->mTextures[i];
			if (!tex) continue;

			// mHeight == 0 => the texture is stored compressed (PNG/JPG/...). mWidth is the byte count and
			// achFormatHint is the format ("png", "jpg", ...). Just write the bytes to a real file. Raw
			// uncompressed textures (mHeight > 0) are rare for glTF and are skipped (name left empty).
			if (tex->mHeight == 0 && tex->mWidth > 0 && tex->pcData)
			{
				std::string ext;
				for (int c = 0; c < 8 && tex->achFormatHint[c]; ++c)
					if (isalnum((unsigned char)tex->achFormatHint[c])) ext += (char)tolower((unsigned char)tex->achFormatHint[c]);
				if (ext.empty()) ext = "bin";

				std::string fname = "embedded_" + std::to_string(i) + "." + ext;
				std::string full = out_dir + "\\" + fname;

				FILE* f = nullptr;
				if (fopen_s(&f, full.c_str(), "wb") == 0 && f)
				{
					fwrite(tex->pcData, 1, tex->mWidth, f);
					fclose(f);
					names[i] = fname;
				}
			}
		}
#endif
		return names;
	}

}
