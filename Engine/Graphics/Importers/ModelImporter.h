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
	// Skinning influences, PARALLEL to `vertices` (empty = rigid submesh). Filled from aiMesh::mBones.
	std::vector<VertexSkin> skin;
	bool has_skin() const { return !skin.empty(); }
	u32 material_index{ 0 };
	float base_color[4]{ 0.8f, 0.8f, 0.8f, 1.0f }; // diffuse/base color from the model's material (e.g. Kenney flat colors)
	float metallic{ 0.0f };                         // PBR metallic factor from the material
	float roughness{ 0.5f };                        // PBR roughness factor from the material
	std::string name;
	std::string diffuse_texture;   // Albedo/Diffuse texture
	std::string normal_texture;    // Normal map
	std::string metallic_texture;  // Metallic map
	std::string roughness_texture; // Roughness map
	std::string ao_texture;        // Ambient Occlusion map
	std::string emissive_texture;  // Emissive map
	// Channel each PBR map is read from (0 R, 1 G, 2 B, 3 A). A texture used for metallic AND roughness is a packed
	// map (glTF metallicRoughness, ORM/ARM): roughness in G, metallic in B, occlusion (often the same file) in R.
	u8 metallic_channel{ 0 };
	u8 roughness_channel{ 0 };
	u8 ao_channel{ 0 };
	bool normal_opengl{ false };   // normal map uses the OpenGL (+Y) convention — glTF's convention
	bool normal_is_bump{ false };  // "normal" came from a HEIGHT/bump slot (OBJ map_Bump): may be grayscale
	// glTF alphaMode / alphaCutoff / doubleSided (#441) in Material terms: blend_mode 0 opaque, 1 alpha blend (BLEND),
	// 3 alpha test (MASK) discarding below alpha_cutoff; two_sided = no back-face culling. Other formats stay opaque.
	u32 blend_mode{ 0 };
	float alpha_cutoff{ 0.0f };
	bool two_sided{ false };
	// glTF emission (#450): emissiveFactor (black = none, the glTF default) and KHR_materials_emissive_strength (1 when
	// absent) — emission = factor * strength * emissive_texture. Other formats leave it black.
	float emissive_color[3]{ 0.0f, 0.0f, 0.0f };
	float emissive_strength{ 1.0f };
	bool emits() const { return emissive_strength > 0.0f && (emissive_color[0] > 0.0f || emissive_color[1] > 0.0f || emissive_color[2] > 0.0f); }
	// What the submesh's material says about its albedo (#453), for the texture-folder scans: 0 = no albedo map (a
	// colour-only material — never textured by a scan), 1 = it names a map (a scan may stand in when that file is
	// missing), 2 = the file has no material for it (Assimp's placeholder, e.g. an OBJ without .mtl — a scan may).
	u8 albedo_source{ 0 };
	};

	// ---- Skeletal animation data (green-field; see ANIMATION_SYSTEM_DESIGN.md) ----
	// The full node hierarchy carries the transforms; the bone palette is the compact subset that
	// vertices reference (u8 indices -> max 255 palette entries). Clips key NODES by index; the
	// managed layer converts node indices to bone NAMES for the .vanim asset (rename-survivable).

	struct SkeletonNodeData
	{
		std::string name;
		s32 parent{ -1 };                      // index into the node array; -1 = root
		DirectX::XMFLOAT4X4 local_bind;        // node's local bind-pose transform (row-vector convention)
	};

	struct SkeletonBoneData
	{
		u32 node_index{ 0 };                   // which hierarchy node this palette entry follows
		DirectX::XMFLOAT4X4 inverse_bind;      // aiBone::mOffsetMatrix (mesh space -> bone space)
	};

	struct AnimVec3Key { float t; float x, y, z; };          // t in SECONDS
	struct AnimQuatKey { float t; float x, y, z, w; };       // t in SECONDS

	struct AnimChannelData
	{
		s32 node_index{ -1 };
		std::vector<AnimVec3Key> position_keys;
		std::vector<AnimQuatKey> rotation_keys;
		std::vector<AnimVec3Key> scale_keys;
	};

	struct AnimationClipData
	{
		std::string name;
		float duration_sec{ 0.0f };
		std::vector<AnimChannelData> channels;
	};

	struct ImportedModelData
	{
		std::vector<SubMeshData> submeshes;
		std::vector<std::string> material_names;
		std::vector<std::string> texture_paths;
		DirectX::XMFLOAT3 bounds_min{ 0.0f, 0.0f, 0.0f };
		DirectX::XMFLOAT3 bounds_max{ 0.0f, 0.0f, 0.0f };
		std::string name;
		bool gltf{ false };   // source was glTF (.gltf/.glb): OpenGL-convention normal maps
		// Skeleton + clips (empty for static models).
		std::vector<SkeletonNodeData> nodes;
		std::vector<SkeletonBoneData> bones;
		std::vector<AnimationClipData> animations;
		bool has_skeleton() const { return !bones.empty(); }
		// What the import could not reproduce, one message each (e.g. a material whose maps need different UV transforms,
		// #470); the editor shows them in its console.
		std::vector<std::string> warnings;

		bool is_valid() const { return !submeshes.empty(); }
		void clear()
		{
			submeshes.clear();
			material_names.clear();
			texture_paths.clear();
			nodes.clear();
			bones.clear();
			animations.clear();
			warnings.clear();
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
		/// left_handed (#352): the in-memory twin of the "<model>.vimport" sidecar the file import reads itself.
		static ImportedModelData import_from_memory(const u8* data, u64 length,
			const std::string& ext_hint, const std::string& virtual_dir, bool left_handed = false);

		/// <summary>
		/// Check if a file format is supported.
		/// </summary>
		static bool is_format_supported(const std::string& extension);

		/// <summary>
		/// Extract textures EMBEDDED in the model (e.g. a .glb that packs its PNG/JPG textures inside the file)
		/// into out_dir as files named "embedded_&lt;i&gt;.&lt;ext&gt;". Returns the written filename per embedded
		/// texture index (empty string if that one couldn't be written / is raw-uncompressed). Lets the importer
		/// turn a self-contained .glb into a model folder with a real textures/ subfolder.
		/// </summary>
		static std::vector<std::string> extract_embedded_textures(const std::string& filepath, const std::string& out_dir);

		/// <summary>
		/// The warnings of the most recent import_from_file / import_from_memory on the calling thread (#470), so a
		/// caller that only gets ids back (the editor's model import) can still show them.
		/// </summary>
		static const std::vector<std::string>& last_warnings();

	private:
	static void calculate_bounds(ImportedModelData& data);
	// parent_transform / world_transform: aiMatrix4x4* of the node chain (#338) — glTF parts keep their placement
	static void process_node(void* node, void* scene, ImportedModelData& data, const void* parent_transform = nullptr);
	static SubMeshData process_mesh(void* mesh, void* scene, ImportedModelData& data, const void* world_transform = nullptr);
	static void extract_materials(void* scene, ImportedModelData& data, const std::string& filepath, bool allow_disk_search = true);
	static void search_textures_in_directory(const std::string& dir, ImportedModelData& data);
	// Skeleton/clip extraction (must run BEFORE process_node so bone lookups can resolve node names).
	static void build_skeleton(void* scene, ImportedModelData& data);
	static void extract_animations(void* scene, ImportedModelData& data);
	};
}
