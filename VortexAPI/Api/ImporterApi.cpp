#include "../ApiCommon.h"
#if VORTEX_HAS_DX12
#include <windows.h>
#include "../../Engine/Graphics/DX12/DX12ShaderCompiler.h"
#endif
#include <deque>
#include <filesystem>
#include <memory>
#include <mutex>

namespace
{
	// The editor asks a model several questions in a row (submesh count, names, texture paths, material props, one
	// GetModelSubmeshTriangleData per part) and each ran a full Assimp parse — a placement was three parses and a mesh
	// collider one per submesh (#357). The last two parsed files stay here, keyed by path|mtime|size, so an unchanged
	// file is parsed once and a re-exported one again. Two entries only: a parsed model can be hundreds of MB.
	struct ParsedModel { std::string key; std::shared_ptr<const graphics::ImportedModelData> data; };
	std::mutex g_parsed_mutex;
	std::deque<ParsedModel> g_parsed;

	std::string parsed_model_key(const char* filepath)
	{
		std::error_code ec;
		const std::filesystem::path p(filepath);
		const auto size = std::filesystem::file_size(p, ec);
		if (ec) return std::string(filepath);
		const auto time = std::filesystem::last_write_time(p, ec);
		if (ec) return std::string(filepath);
		return std::string(filepath) + "|" + std::to_string((long long)time.time_since_epoch().count()) + "|" + std::to_string((unsigned long long)size);
	}

	std::shared_ptr<const graphics::ImportedModelData> parsed_model(const char* filepath)
	{
		const std::string key = parsed_model_key(filepath);
		{
			std::lock_guard<std::mutex> lock(g_parsed_mutex);
			for (size_t i = 0; i < g_parsed.size(); ++i)
			{
				if (g_parsed[i].key != key) continue;
				ParsedModel hit = g_parsed[i];
				if (i != 0) { g_parsed.erase(g_parsed.begin() + i); g_parsed.push_front(hit); }
				return hit.data;
			}
		}
		std::shared_ptr<const graphics::ImportedModelData> data =
			std::make_shared<graphics::ImportedModelData>(graphics::ModelImporter::import_from_file(filepath));
		std::lock_guard<std::mutex> lock(g_parsed_mutex);
		g_parsed.push_front(ParsedModel{ key, data });
		while (g_parsed.size() > 2) g_parsed.pop_back();
		return data;
	}
}

EDITOR_INTERFACE id::id_type ImportModel(const char* filepath)
{
	if (!filepath) return id::invalid_id;
	return graphics::ResourceRegistry::instance().import_model(filepath);
}

EDITOR_INTERFACE id::id_type ImportTexture(const char* filepath)
{
	if (!filepath) return id::invalid_id;
	return graphics::ResourceRegistry::instance().import_texture(filepath);
}

// Multi-Material Import API - returns submesh data via output arrays
// Returns number of submeshes, fills arrays with mesh_ids, material_ids, texture_ids
EDITOR_INTERFACE int ImportModelWithMaterials(
	const char* filepath,
	id::id_type* out_mesh_ids,
	id::id_type* out_material_ids,
	id::id_type* out_texture_ids,
	int max_submeshes)
{
	if (!filepath || !out_mesh_ids || !out_material_ids || !out_texture_ids || max_submeshes <= 0)
		return 0;

	auto result = graphics::ResourceRegistry::instance().import_model_with_materials(filepath);
	if (!result.success)
		return 0;

	int count = static_cast<int>((std::min)(result.submeshes.size(), static_cast<size_t>(max_submeshes)));
	
	for (int i = 0; i < count; i++)
	{
		out_mesh_ids[i] = result.submeshes[i].mesh_id;
		out_material_ids[i] = result.submeshes[i].material_id;
		out_texture_ids[i] = result.submeshes[i].texture_id;
	}

	return count;
}

// Like ImportModelWithMaterials, but also writes one .vmesh per submesh into cache_dir (created when missing) so the
// next start can load the model without Assimp (#364 C). Nothing is written for a skinned model (the format carries
// no bone weights). *out_cached receives the number of files written (0 = not cacheable).
EDITOR_INTERFACE int ImportModelWithMaterialsCached(
	const char* filepath,
	const char* cache_dir,
	id::id_type* out_mesh_ids,
	id::id_type* out_material_ids,
	id::id_type* out_texture_ids,
	int max_submeshes,
	int* out_cached)
{
	if (out_cached) *out_cached = 0;
	if (!filepath || !out_mesh_ids || !out_material_ids || !out_texture_ids || max_submeshes <= 0)
		return 0;

	auto result = graphics::ResourceRegistry::instance().import_model_with_materials_cached(filepath, cache_dir ? cache_dir : "");
	if (!result.success)
		return 0;

	int count = static_cast<int>((std::min)(result.submeshes.size(), static_cast<size_t>(max_submeshes)));
	for (int i = 0; i < count; i++)
	{
		out_mesh_ids[i] = result.submeshes[i].mesh_id;
		out_material_ids[i] = result.submeshes[i].material_id;
		out_texture_ids[i] = result.submeshes[i].texture_id;
	}
	if (out_cached) *out_cached = static_cast<int>(result.cached);
	return count;
}

// Load a model from the folder ImportModelWithMaterialsCached wrote (#364 C): meshes from submesh_N.vmesh, materials
// from materials.vmc — no Assimp. Returns the submesh count, 0 when the cache is missing, incomplete or stale.
EDITOR_INTERFACE int ImportModelFromCache(
	const char* cache_dir,
	id::id_type* out_mesh_ids,
	id::id_type* out_material_ids,
	id::id_type* out_texture_ids,
	int max_submeshes)
{
	if (!cache_dir || !*cache_dir || !out_mesh_ids || !out_material_ids || !out_texture_ids || max_submeshes <= 0)
		return 0;

	auto result = graphics::ResourceRegistry::instance().import_model_from_cache(cache_dir);
	if (!result.success)
		return 0;

	int count = static_cast<int>((std::min)(result.submeshes.size(), static_cast<size_t>(max_submeshes)));
	for (int i = 0; i < count; i++)
	{
		out_mesh_ids[i] = result.submeshes[i].mesh_id;
		out_material_ids[i] = result.submeshes[i].material_id;
		out_texture_ids[i] = result.submeshes[i].texture_id;
	}
	return count;
}

// In-memory texture import (packed/encrypted asset pak loaded into RAM — no file on disk).
EDITOR_INTERFACE id::id_type ImportTextureFromMemory(const unsigned char* data, int length)
{
	if (!data || length <= 0) return id::invalid_id;
	return graphics::ResourceRegistry::instance().import_texture_from_memory(
		reinterpret_cast<const u8*>(data), static_cast<u64>(length));
}

// In-memory multi-material model import (packed asset pak loaded into RAM). ext_hint = "obj","fbx",...
EDITOR_INTERFACE int ImportModelFromMemoryWithMaterials(
	const unsigned char* data,
	int length,
	const char* ext_hint,
	const char* virtual_dir,
	id::id_type* out_mesh_ids,
	id::id_type* out_material_ids,
	id::id_type* out_texture_ids,
	int max_submeshes)
{
	if (!data || length <= 0 || !out_mesh_ids || !out_material_ids || !out_texture_ids || max_submeshes <= 0)
		return 0;

	auto result = graphics::ResourceRegistry::instance().import_model_with_materials_from_memory(
		reinterpret_cast<const u8*>(data), static_cast<u64>(length),
		ext_hint ? ext_hint : "", virtual_dir ? virtual_dir : "");
	if (!result.success)
		return 0;

	int count = static_cast<int>((std::min)(result.submeshes.size(), static_cast<size_t>(max_submeshes)));
	for (int i = 0; i < count; i++)
	{
		out_mesh_ids[i] = result.submeshes[i].mesh_id;
		out_material_ids[i] = result.submeshes[i].material_id;
		out_texture_ids[i] = result.submeshes[i].texture_id;
	}
	return count;
}

namespace
{
	// Triangle vertex positions (x,y,z per vertex, 3 verts/triangle, indices expanded) of ONE submesh, or of the whole
	// model when submesh < 0. out_positions=null (or max_floats=0) is a size query -> the required float count.
	int write_triangles(const graphics::ImportedModelData& model, int submesh, float* out_positions, int max_floats)
	{
		size_t total_idx = 0;
		for (size_t i = 0; i < model.submeshes.size(); ++i)
			if (submesh < 0 || (size_t)submesh == i) total_idx += model.submeshes[i].indices.size();
		int needed = static_cast<int>(total_idx * 3);
		if (!out_positions || max_floats <= 0) return needed;
		int w = 0;
		for (size_t i = 0; i < model.submeshes.size(); ++i)
		{
			if (submesh >= 0 && (size_t)submesh != i) continue;
			const auto& sm = model.submeshes[i];
			for (u32 idx : sm.indices)
			{
				if (idx >= sm.vertices.size()) continue;
				if (w + 3 > max_floats) return w;
				const auto& p = sm.vertices[idx].position;
				out_positions[w++] = p.x; out_positions[w++] = p.y; out_positions[w++] = p.z;
			}
		}
		return w;
	}
}

// Collision: return a model's TRIANGLE vertex positions (x,y,z per vertex, 3 verts/triangle, all submeshes,
// indices expanded) so the managed collision system can build edge-accurate mesh colliders that match what's
// rendered. Pass out_positions=null (or max_floats=0) for a size query -> returns the required float count;
// otherwise returns the number of floats written. Local model space (the caller applies the entity transform).
EDITOR_INTERFACE int GetModelTriangleData(const char* filepath, float* out_positions, int max_floats)
{
	if (!filepath) return 0;
	auto model = parsed_model(filepath);
	return write_triangles(*model, -1, out_positions, max_floats);
}

// Same as GetModelTriangleData but for a model whose bytes live in the in-RAM asset pak (shipped game).
EDITOR_INTERFACE int GetModelTriangleDataFromMemory(const unsigned char* data, int length, const char* ext_hint,
	float* out_positions, int max_floats)
{
	if (!data || length <= 0) return 0;
	auto model = graphics::ModelImporter::import_from_memory(
		reinterpret_cast<const u8*>(data), static_cast<u64>(length), ext_hint ? ext_hint : "", "");
	return write_triangles(model, -1, out_positions, max_floats);
}

// ONE submesh's triangles (#362): a placed "model.glb#submeshN" part collides with its own geometry, not the whole
// model's. submesh < 0 = all, like GetModelTriangleData.
EDITOR_INTERFACE int GetModelSubmeshTriangleData(const char* filepath, int submesh, float* out_positions, int max_floats)
{
	if (!filepath) return 0;
	auto model = parsed_model(filepath);
	return write_triangles(*model, submesh, out_positions, max_floats);
}

EDITOR_INTERFACE int GetModelSubmeshTriangleDataFromMemory(const unsigned char* data, int length, const char* ext_hint,
	int submesh, float* out_positions, int max_floats)
{
	if (!data || length <= 0) return 0;
	auto model = graphics::ModelImporter::import_from_memory(
		reinterpret_cast<const u8*>(data), static_cast<u64>(length), ext_hint ? ext_hint : "", "");
	return write_triangles(model, submesh, out_positions, max_floats);
}

// Get submesh count without importing (for pre-allocation)
EDITOR_INTERFACE int GetModelSubmeshCount(const char* filepath)
{
	if (!filepath) return 0;
	
	auto parsed = parsed_model(filepath);
	const auto& model_data = *parsed;
	return static_cast<int>(model_data.submeshes.size());
}

// Get submesh names from model file
EDITOR_INTERFACE int GetModelSubmeshNames(const char* filepath, char** out_names, int max_submeshes, int max_name_length)
{
	if (!filepath || !out_names || max_submeshes <= 0 || max_name_length <= 0) return 0;
	
	auto parsed = parsed_model(filepath);
	const auto& model_data = *parsed;
	int count = static_cast<int>((std::min)(model_data.submeshes.size(), static_cast<size_t>(max_submeshes)));
	
	for (int i = 0; i < count; i++)
	{
	std::string name;
		
	// Use mesh name if available, otherwise use material name
	if (!model_data.submeshes[i].name.empty())
	{
	name = model_data.submeshes[i].name;
	}
	else if (model_data.submeshes[i].material_index < model_data.material_names.size())
	{
	name = model_data.material_names[model_data.submeshes[i].material_index];
	}
	else
	{
	name = "Submesh_" + std::to_string(i);
	}
		
	if (out_names[i])
	{
	strncpy_s(out_names[i], max_name_length, name.c_str(), _TRUNCATE);
	}
	}

	return count;
}

// Per-submesh PBR texture paths the model's materials actually reference (empty string when a slot has none).
// The editor INTERPRETS each model from this — every model is individual, so the dialog builds its slots from
// whatever each material has. Paths are absolute (model_dir + assimp name). Returns the submesh count.
EDITOR_INTERFACE int GetModelTexturePaths(const char* filepath,
	char** out_albedo, char** out_normal, char** out_metallic,
	char** out_roughness, char** out_ao, char** out_emissive,
	int max_submeshes, int max_len)
{
	if (!filepath || max_submeshes <= 0 || max_len <= 0) return 0;
	auto parsed = parsed_model(filepath);
	const auto& model_data = *parsed;
	int count = static_cast<int>((std::min)(model_data.submeshes.size(), static_cast<size_t>(max_submeshes)));
	for (int i = 0; i < count; i++)
	{
		const auto& s = model_data.submeshes[i];
		if (out_albedo && out_albedo[i])       strncpy_s(out_albedo[i], max_len, s.diffuse_texture.c_str(), _TRUNCATE);
		if (out_normal && out_normal[i])       strncpy_s(out_normal[i], max_len, s.normal_texture.c_str(), _TRUNCATE);
		if (out_metallic && out_metallic[i])   strncpy_s(out_metallic[i], max_len, s.metallic_texture.c_str(), _TRUNCATE);
		if (out_roughness && out_roughness[i]) strncpy_s(out_roughness[i], max_len, s.roughness_texture.c_str(), _TRUNCATE);
		if (out_ao && out_ao[i])               strncpy_s(out_ao[i], max_len, s.ao_texture.c_str(), _TRUNCATE);
		if (out_emissive && out_emissive[i])   strncpy_s(out_emissive[i], max_len, s.emissive_texture.c_str(), _TRUNCATE);
	}
	return count;
}

// Per-submesh PBR material PROPERTIES (base color RGBA + metallic + roughness factors) the model actually has,
// so the editor shows the real material (not flat defaults). out_base_colors is max_submeshes*4 floats.
EDITOR_INTERFACE int GetModelMaterialProps(const char* filepath,
	float* out_base_colors, float* out_metallic, float* out_roughness, int max_submeshes)
{
	if (!filepath || max_submeshes <= 0) return 0;
	auto parsed = parsed_model(filepath);
	const auto& model_data = *parsed;
	int count = static_cast<int>((std::min)(model_data.submeshes.size(), static_cast<size_t>(max_submeshes)));
	for (int i = 0; i < count; i++)
	{
		const auto& s = model_data.submeshes[i];
		if (out_base_colors) { out_base_colors[i * 4 + 0] = s.base_color[0]; out_base_colors[i * 4 + 1] = s.base_color[1]; out_base_colors[i * 4 + 2] = s.base_color[2]; out_base_colors[i * 4 + 3] = s.base_color[3]; }
		if (out_metallic)  out_metallic[i] = s.metallic;
		if (out_roughness) out_roughness[i] = s.roughness;
	}
	return count;
}

// Extract textures EMBEDDED in the model (e.g. a .glb that packs its PNG/JPG inside) into out_dir as real files,
// filling out_names[i] with the written filename per embedded-texture index ("" if it couldn't be written).
// Returns the number of embedded textures. Lets the importer give a self-contained .glb a real textures/ folder.
EDITOR_INTERFACE int ExtractEmbeddedTextures(const char* filepath, const char* out_dir,
	char** out_names, int max_textures, int max_len)
{
	if (!filepath || !out_dir) return 0;
	auto names = graphics::ModelImporter::extract_embedded_textures(filepath, out_dir);
	int count = static_cast<int>(names.size());
	if (count > max_textures) count = max_textures;
	for (int i = 0; i < count; i++)
		if (out_names && out_names[i]) strncpy_s(out_names[i], max_len, names[i].c_str(), _TRUNCATE);
	return count;
}

EDITOR_INTERFACE id::id_type LoadVMesh(const char* filepath)
{
	if (!filepath) return id::invalid_id;
	return graphics::ResourceRegistry::instance().load_vmesh(filepath);
}

EDITOR_INTERFACE bool ExportMeshToVMesh(id::id_type mesh_id, const char* filepath)
{
	if (!filepath) return false;
	return graphics::ResourceRegistry::instance().export_mesh_to_vmesh(mesh_id, filepath);
}

// Precompile the engine's built-in shaders to <out_bin_dir>/*.cso for a Release export: the shipped game then
// carries compiled bytecode only (no readable .hlsl source, no per-launch D3DCompile). Reads source from the
// engine's resolved shaders dir; uses d3dcompiler only (no device state), so it's safe from the editor process.
// Returns the number of .cso blobs written (0 = precompiler unavailable -> caller ships loose .hlsl as fallback).
EDITOR_INTERFACE int PrecompileBuiltinShaders(const char* out_bin_dir_utf8)
{
#if VORTEX_HAS_DX12
	if (!out_bin_dir_utf8 || !*out_bin_dir_utf8) return 0;
	int n = MultiByteToWideChar(CP_UTF8, 0, out_bin_dir_utf8, -1, nullptr, 0);
	if (n <= 0) return 0;
	std::wstring w(static_cast<size_t>(n - 1), L'\0');
	MultiByteToWideChar(CP_UTF8, 0, out_bin_dir_utf8, -1, &w[0], n);
	return graphics::dx12::DX12ShaderCompiler::precompile_builtins(w);
#else
	(void)out_bin_dir_utf8;
	return 0;   // Metal shaders ship as MSL source and are compiled by the driver at load — nothing to precompile.
#endif
}

EDITOR_INTERFACE bool HasAssimpSupport()
{
#ifdef VORTEX_USE_ASSIMP
	return true;
#else
	return false;
#endif
}

// Asset Database API
