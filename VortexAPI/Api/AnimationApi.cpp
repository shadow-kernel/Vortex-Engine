#include "../ApiCommon.h"

// ============================================================================================
// Skeletal animation interop (see ANIMATION_SYSTEM_DESIGN.md).
//
// Skeleton/clip queries follow the house two-call size-query idiom (GetModelTriangleData):
// pass null/0 output for the required count, call again with a buffer. Like the rest of
// ImporterApi, each call re-runs the Assimp import — callers cache managed-side (skeletons
// and clips are read once per model per session).
//
// Pose submission is bulk floats per frame (SubmitMeshInstances proved this path at scale).
// ============================================================================================

namespace
{
	using namespace vortex;

	int fill_skeleton_nodes(const graphics::ImportedModelData& model,
		int* out_parents, float* out_local_bind, char** out_names, int max_nodes, int max_name_len)
	{
		const int total = static_cast<int>(model.nodes.size());
		if (!out_parents || max_nodes <= 0) return total;   // size query

		const int count = (std::min)(total, max_nodes);
		for (int i = 0; i < count; ++i)
		{
			const auto& n = model.nodes[i];
			out_parents[i] = n.parent;
			if (out_local_bind) memcpy(out_local_bind + (size_t)i * 16, &n.local_bind, 16 * sizeof(float));
			if (out_names && out_names[i] && max_name_len > 0)
				strncpy_s(out_names[i], max_name_len, n.name.c_str(), _TRUNCATE);
		}
		return count;
	}

	int fill_skeleton_bones(const graphics::ImportedModelData& model,
		int* out_node_indices, float* out_inverse_bind, int max_bones)
	{
		const int total = static_cast<int>(model.bones.size());
		if (!out_node_indices || max_bones <= 0) return total;   // size query

		const int count = (std::min)(total, max_bones);
		for (int i = 0; i < count; ++i)
		{
			const auto& b = model.bones[i];
			out_node_indices[i] = static_cast<int>(b.node_index);
			if (out_inverse_bind) memcpy(out_inverse_bind + (size_t)i * 16, &b.inverse_bind, 16 * sizeof(float));
		}
		return count;
	}

	// Flat float encoding of one clip's channels (managed side parses into the .vanim DTO):
	//   [channelCount] then per channel:
	//   [nodeIndex, posKeyCount, rotKeyCount, scaleKeyCount,
	//    posKeys (t,x,y,z)*, rotKeys (t,x,y,z,w)*, scaleKeys (t,x,y,z)*]
	// Counts/indices ride as floats (exact for values < 2^24 — node/key counts are far below).
	int fill_animation_data(const graphics::ImportedModelData& model, int anim_index, float* out, int max_floats)
	{
		if (anim_index < 0 || anim_index >= static_cast<int>(model.animations.size())) return 0;
		const auto& clip = model.animations[anim_index];

		size_t needed = 1;
		for (const auto& ch : clip.channels)
			needed += 4 + ch.position_keys.size() * 4 + ch.rotation_keys.size() * 5 + ch.scale_keys.size() * 4;
		if (!out || max_floats <= 0) return static_cast<int>(needed);   // size query
		if (static_cast<size_t>(max_floats) < needed) return 0;         // buffer too small — caller re-queries

		int w = 0;
		out[w++] = static_cast<float>(clip.channels.size());
		for (const auto& ch : clip.channels)
		{
			out[w++] = static_cast<float>(ch.node_index);
			out[w++] = static_cast<float>(ch.position_keys.size());
			out[w++] = static_cast<float>(ch.rotation_keys.size());
			out[w++] = static_cast<float>(ch.scale_keys.size());
			for (const auto& k : ch.position_keys) { out[w++] = k.t; out[w++] = k.x; out[w++] = k.y; out[w++] = k.z; }
			for (const auto& k : ch.rotation_keys) { out[w++] = k.t; out[w++] = k.x; out[w++] = k.y; out[w++] = k.z; out[w++] = k.w; }
			for (const auto& k : ch.scale_keys) { out[w++] = k.t; out[w++] = k.x; out[w++] = k.y; out[w++] = k.z; }
		}
		return w;
	}
}

// ---- Skeleton queries (file + in-RAM pak variants) ----

EDITOR_INTERFACE int GetModelSkeletonNodes(const char* filepath,
	int* out_parents, float* out_local_bind, char** out_names, int max_nodes, int max_name_len)
{
	if (!filepath) return 0;
	auto model = graphics::ModelImporter::import_from_file(filepath);
	return fill_skeleton_nodes(model, out_parents, out_local_bind, out_names, max_nodes, max_name_len);
}

EDITOR_INTERFACE int GetModelSkeletonNodesFromMemory(const unsigned char* data, int length, const char* ext_hint,
	int* out_parents, float* out_local_bind, char** out_names, int max_nodes, int max_name_len)
{
	if (!data || length <= 0) return 0;
	auto model = graphics::ModelImporter::import_from_memory(
		reinterpret_cast<const u8*>(data), static_cast<u64>(length), ext_hint ? ext_hint : "", "");
	return fill_skeleton_nodes(model, out_parents, out_local_bind, out_names, max_nodes, max_name_len);
}

EDITOR_INTERFACE int GetModelSkeletonBones(const char* filepath,
	int* out_node_indices, float* out_inverse_bind, int max_bones)
{
	if (!filepath) return 0;
	auto model = graphics::ModelImporter::import_from_file(filepath);
	return fill_skeleton_bones(model, out_node_indices, out_inverse_bind, max_bones);
}

EDITOR_INTERFACE int GetModelSkeletonBonesFromMemory(const unsigned char* data, int length, const char* ext_hint,
	int* out_node_indices, float* out_inverse_bind, int max_bones)
{
	if (!data || length <= 0) return 0;
	auto model = graphics::ModelImporter::import_from_memory(
		reinterpret_cast<const u8*>(data), static_cast<u64>(length), ext_hint ? ext_hint : "", "");
	return fill_skeleton_bones(model, out_node_indices, out_inverse_bind, max_bones);
}

// ---- Animation clip queries (editor-side; shipped games load .vanim JSON instead) ----

EDITOR_INTERFACE int GetModelAnimationCount(const char* filepath)
{
	if (!filepath) return 0;
	auto model = graphics::ModelImporter::import_from_file(filepath);
	return static_cast<int>(model.animations.size());
}

// Returns 1 on success; fills the clip name (UTF-8, truncated) and duration in seconds.
EDITOR_INTERFACE int GetModelAnimationInfo(const char* filepath, int anim_index,
	char* out_name, int name_cap, float* out_duration_sec)
{
	if (!filepath || anim_index < 0) return 0;
	auto model = graphics::ModelImporter::import_from_file(filepath);
	if (anim_index >= static_cast<int>(model.animations.size())) return 0;
	const auto& clip = model.animations[anim_index];
	if (out_name && name_cap > 0) strncpy_s(out_name, name_cap, clip.name.c_str(), _TRUNCATE);
	if (out_duration_sec) *out_duration_sec = clip.duration_sec;
	return 1;
}

EDITOR_INTERFACE int GetModelAnimationData(const char* filepath, int anim_index, float* out, int max_floats)
{
	if (!filepath) return 0;
	auto model = graphics::ModelImporter::import_from_file(filepath);
	return fill_animation_data(model, anim_index, out, max_floats);
}

// ---- Runtime ----

// Is this registered mesh skinned (carries the 52-byte vertex + weight data)?
EDITOR_INTERFACE bool IsMeshSkinned(id::id_type mesh_id)
{
	auto* mesh = graphics::ResourceRegistry::instance().get_mesh(mesh_id);
	return mesh && mesh->is_skinned();
}

// Submit a skinned mesh for this frame: world matrix (row-major float[16]) + bone palette
// (bone_count row-major 4x4s, each = inverseBind * boneWorld). Re-submit every frame the pose changes.
EDITOR_INTERFACE void SubmitSkinnedMeshForRendering(id::id_type mesh_id, id::id_type material_id,
	const float* world_matrix, const float* bone_matrices, int bone_count)
{
	if (!world_matrix || !bone_matrices || bone_count <= 0) return;
	graphics::dx12::DX12Renderer::instance().submit_skinned_item(
		mesh_id, material_id, world_matrix, bone_matrices, static_cast<u32>(bone_count));
}
