#pragma once

#include "../Engine/Graphics/Importers/ModelImporter.h"
#include "../Engine/Graphics/Importers/MeshSerializer.h"

#include "Test.h"

#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

using namespace vortex::graphics;

// Model import (VortexImportTest): small glTF / OBJ files written by the test, read through the engine's Assimp importer
// and the import cache — glTF alpha modes (#441). Headless; prints "ALL PASSED" or "FAILURES!" and exits non-zero.
class engine_test : public test
{
public:
	bool initialize() override
	{
		std::error_code ec;
		m_dir = std::filesystem::temp_directory_path(ec) / ("vortex-import-test-" + std::to_string(
			(long long)std::chrono::steady_clock::now().time_since_epoch().count()));
		std::filesystem::create_directories(m_dir, ec);
		return !ec;
	}

	void run() override
	{
		alpha_modes();
		other_formats_stay_opaque();
		cache_keeps_alpha();
		std::printf(m_failures ? "FAILURES!\n" : "ALL PASSED\n");
	}

	void shutdown() override
	{
		std::error_code ec;
		std::filesystem::remove_all(m_dir, ec);
		if (m_failures) std::exit(1);
	}

private:
	std::filesystem::path m_dir;
	int m_failures{ 0 };

	void check(bool ok, const std::string& what)
	{
		std::printf("  %-4s %s\n", ok ? "ok" : "FAIL", what.c_str());
		if (!ok) ++m_failures;
	}

	static bool near(float a, float b, float eps = 1e-5f) { return std::fabs(a - b) <= eps; }

	std::string write(const std::string& name, const std::string& text)
	{
		const auto p = m_dir / name;
		std::ofstream f(p, std::ios::binary);
		f.write(text.data(), (std::streamsize)text.size());
		return p.string();
	}

	static std::string base64(const std::vector<unsigned char>& d)
	{
		static const char* t = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
		std::string o;
		for (size_t i = 0; i < d.size(); i += 3)
		{
			const unsigned v = (unsigned)d[i] << 16 | (i + 1 < d.size() ? (unsigned)d[i + 1] << 8 : 0u) | (i + 2 < d.size() ? (unsigned)d[i + 2] : 0u);
			o += t[(v >> 18) & 63]; o += t[(v >> 12) & 63];
			o += i + 1 < d.size() ? t[(v >> 6) & 63] : '=';
			o += i + 2 < d.size() ? t[v & 63] : '=';
		}
		return o;
	}

	// A glTF quad per material (POSITION, TEXCOORD_0, TEXCOORD_1, indices; the buffer as a data URI). `materials`,
	// `textures` and `images` are the JSON array contents; quad i uses material i.
	static std::string gltf(const std::string& materials, int material_count,
		const std::string& textures = "", const std::string& images = "", const std::string& extensions_used = "")
	{
		static const float pos[4][3] = { { 0, 0, 0 }, { 1, 0, 0 }, { 1, 1, 0 }, { 0, 1, 0 } };
		static const float uv[4][2] = { { 0.1f, 0.2f }, { 0.6f, 0.25f }, { 0.65f, 0.9f }, { 0.05f, 0.7f } };
		static const unsigned short idx[6] = { 0, 1, 2, 0, 2, 3 };
		std::vector<unsigned char> buf;
		auto put = [&](const void* p, size_t n) { buf.insert(buf.end(), (const unsigned char*)p, (const unsigned char*)p + n); };
		put(pos, sizeof(pos)); put(uv, sizeof(uv)); put(uv, sizeof(uv)); put(idx, sizeof(idx));
		std::string prims;
		for (int i = 0; i < material_count; ++i)
			prims += std::string(i ? "," : "") + "{\"attributes\":{\"POSITION\":0,\"TEXCOORD_0\":1,\"TEXCOORD_1\":2},\"indices\":3,\"material\":" + std::to_string(i) + "}";
		return "{\"asset\":{\"version\":\"2.0\"}," + (extensions_used.empty() ? std::string() : "\"extensionsUsed\":[" + extensions_used + "],") +
			"\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}],"
			"\"meshes\":[{\"primitives\":[" + prims + "]}],"
			"\"materials\":[" + materials + "],"
			+ (textures.empty() ? std::string() : "\"textures\":[" + textures + "],")
			+ (images.empty() ? std::string() : "\"images\":[" + images + "],") +
			"\"buffers\":[{\"byteLength\":" + std::to_string(buf.size()) + ",\"uri\":\"data:application/octet-stream;base64," + base64(buf) + "\"}],"
			"\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":48},{\"buffer\":0,\"byteOffset\":48,\"byteLength\":32},"
			"{\"buffer\":0,\"byteOffset\":80,\"byteLength\":32},{\"buffer\":0,\"byteOffset\":112,\"byteLength\":12}],"
			"\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":4,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]},"
			"{\"bufferView\":1,\"componentType\":5126,\"count\":4,\"type\":\"VEC2\"},{\"bufferView\":2,\"componentType\":5126,\"count\":4,\"type\":\"VEC2\"},"
			"{\"bufferView\":3,\"componentType\":5123,\"count\":6,\"type\":\"SCALAR\"}]}";
	}

	// The submesh whose material carries this name (nullptr when there is none).
	static const SubMeshData* by_material(const ImportedModelData& d, const char* name)
	{
		for (const auto& s : d.submeshes)
			if (s.material_index < d.material_names.size() && d.material_names[s.material_index] == name) return &s;
		return nullptr;
	}

	// #441: glTF MASK -> alpha test at alphaCutoff (default 0.5), BLEND -> alpha blend, doubleSided -> two-sided
	void alpha_modes()
	{
		std::printf("glTF alpha modes (#441)\n");
		const std::string path = write("alpha.gltf", gltf(
			"{\"name\":\"Leaves\",\"alphaMode\":\"MASK\",\"alphaCutoff\":0.3,\"doubleSided\":true},"
			"{\"name\":\"Glass\",\"alphaMode\":\"BLEND\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.8,0.9,1,0.4]}},"
			"{\"name\":\"Plain\"},"
			"{\"name\":\"Fence\",\"alphaMode\":\"MASK\"},"
			"{\"name\":\"Solid\",\"alphaMode\":\"OPAQUE\",\"alphaCutoff\":0.7,\"doubleSided\":false}", 5));
		const ImportedModelData d = ModelImporter::import_from_file(path);
		check(d.submeshes.size() == 5, "five quads imported (" + std::to_string(d.submeshes.size()) + ")");
		const SubMeshData* leaves = by_material(d, "Leaves");
		const SubMeshData* glass = by_material(d, "Glass");
		const SubMeshData* plain = by_material(d, "Plain");
		const SubMeshData* fence = by_material(d, "Fence");
		const SubMeshData* solid = by_material(d, "Solid");
		check(leaves && leaves->blend_mode == 3 && near(leaves->alpha_cutoff, 0.3f), "MASK cuts out at its alphaCutoff");
		check(leaves && leaves->two_sided, "doubleSided draws both faces");
		check(glass && glass->blend_mode == 1 && glass->alpha_cutoff == 0.0f && !glass->two_sided, "BLEND blends, one-sided");
		check(plain && plain->blend_mode == 0 && plain->alpha_cutoff == 0.0f && !plain->two_sided, "no alphaMode = opaque");
		check(fence && fence->blend_mode == 3 && near(fence->alpha_cutoff, 0.5f), "MASK without a cutoff uses the glTF default 0.5");
		check(solid && solid->blend_mode == 0 && solid->alpha_cutoff == 0.0f, "OPAQUE ignores an alphaCutoff");
	}

	// formats without glTF alpha modes import as before: an OBJ material with "d 0.5" stays opaque and one-sided
	void other_formats_stay_opaque()
	{
		std::printf("other formats (#441)\n");
		write("tri.mtl", "newmtl Half\nKd 1 0 0\nd 0.5\n");
		const std::string path = write("tri.obj", "mtllib tri.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl Half\nf 1 2 3\n");
		const ImportedModelData d = ModelImporter::import_from_file(path);
		check(d.submeshes.size() == 1, "the OBJ imports");
		check(!d.submeshes.empty() && d.submeshes[0].blend_mode == 0 && !d.submeshes[0].two_sided, "an OBJ material stays opaque and one-sided");
	}

	// the import cache (materials.vmc) carries the alpha state; a cache from before (#441) is not used
	void cache_keeps_alpha()
	{
		std::printf("import cache (#441)\n");
		const std::string path = write("cached.gltf", gltf(
			"{\"name\":\"Leaves\",\"alphaMode\":\"MASK\",\"alphaCutoff\":0.25,\"doubleSided\":true},"
			"{\"name\":\"Glass\",\"alphaMode\":\"BLEND\"}", 2));
		ImportedModelData d = ModelImporter::import_from_file(path);
		const std::string dir = (m_dir / "cache").string();
		check(MeshSerializer::save_submeshes_to_dir(d, dir) == 2, "two submeshes cached");
		ImportedModelData back;
		const bool loaded = MeshSerializer::load_submeshes_from_dir(dir, back);
		check(loaded && back.submeshes.size() == 2, "the cache loads");
		bool same = loaded && back.submeshes.size() == d.submeshes.size();
		for (size_t i = 0; same && i < d.submeshes.size(); ++i)
			same = back.submeshes[i].blend_mode == d.submeshes[i].blend_mode && back.submeshes[i].alpha_cutoff == d.submeshes[i].alpha_cutoff
				&& back.submeshes[i].two_sided == d.submeshes[i].two_sided;
		check(same, "blend mode, cutoff and two-sidedness survive the cache");

		// a cache written before the alpha state was stored (version 1): rejected, so the model is imported again
		const auto vmc = std::filesystem::path(dir) / "materials.vmc";
		{
			std::fstream f(vmc, std::ios::in | std::ios::out | std::ios::binary);
			const u32 v1 = 1;
			f.seekp(4);
			f.write(reinterpret_cast<const char*>(&v1), sizeof(v1));
		}
		ImportedModelData old;
		check(!MeshSerializer::load_submeshes_from_dir(dir, old), "a version 1 cache is not used");
	}
};
