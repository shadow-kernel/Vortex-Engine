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
// and the import cache — glTF alpha modes (#441), colour-only materials and the texture-folder fallback (#453),
// KHR_texture_transform and UV sets (#470), emission (#450). Headless; prints "ALL PASSED" or "FAILURES!" and exits
// non-zero.
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
		opaque_blend_is_opaque();
		other_formats_stay_opaque();
		cache_keeps_alpha();
		colour_only_materials_stay_untextured();
		folder_fallback_only_where_wanted();
		texture_transforms();
		texture_transform_conflicts();
		emission();
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

	// The quad every generated glTF is made of: its corners and their TEXCOORD_0 (TEXCOORD_1 is the same, or kUv1 when the
	// sets should differ).
	static constexpr float kPos[4][3] = { { 0, 0, 0 }, { 1, 0, 0 }, { 1, 1, 0 }, { 0, 1, 0 } };
	static constexpr float kUv[4][2] = { { 0.1f, 0.2f }, { 0.6f, 0.25f }, { 0.65f, 0.9f }, { 0.05f, 0.7f } };
	static constexpr float kUv1[4][2] = { { 0.5f, 0.5f }, { 0.9f, 0.5f }, { 0.9f, 0.9f }, { 0.5f, 0.9f } };

	// A glTF quad per material (POSITION, TEXCOORD_0, TEXCOORD_1, indices; the buffer as a data URI). `materials`,
	// `textures` and `images` are the JSON array contents; quad i uses material i.
	static std::string gltf(const std::string& materials, int material_count,
		const std::string& textures = "", const std::string& images = "", const std::string& extensions_used = "")
	{
		std::vector<int> prim_materials;
		for (int i = 0; i < material_count; ++i) prim_materials.push_back(i);
		return gltf_quads(materials, prim_materials, textures, images, extensions_used, false);
	}

	// One quad per entry of `prim_materials` (its material index); `distinct_uv1` gives TEXCOORD_1 other data than set 0.
	static std::string gltf_quads(const std::string& materials, const std::vector<int>& prim_materials,
		const std::string& textures, const std::string& images, const std::string& extensions_used, bool distinct_uv1)
	{
		static const unsigned short idx[6] = { 0, 1, 2, 0, 2, 3 };
		std::vector<unsigned char> buf;
		auto put = [&](const void* p, size_t n) { buf.insert(buf.end(), (const unsigned char*)p, (const unsigned char*)p + n); };
		put(kPos, sizeof(kPos)); put(kUv, sizeof(kUv)); put(distinct_uv1 ? kUv1 : kUv, sizeof(kUv)); put(idx, sizeof(idx));
		std::string prims;
		for (size_t i = 0; i < prim_materials.size(); ++i)
			prims += std::string(i ? "," : "") + "{\"attributes\":{\"POSITION\":0,\"TEXCOORD_0\":1,\"TEXCOORD_1\":2},\"indices\":3,\"material\":" + std::to_string(prim_materials[i]) + "}";
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
			"{\"name\":\"Solid\",\"alphaMode\":\"OPAQUE\",\"alphaCutoff\":0.7,\"doubleSided\":false},"
			"{\"name\":\"Rock\",\"doubleSided\":true},"
			"{\"name\":\"Pane\",\"alphaMode\":\"BLEND\",\"doubleSided\":true,\"pbrMetallicRoughness\":{\"baseColorFactor\":[1,1,1,0.3]}}", 7));
		const ImportedModelData d = ModelImporter::import_from_file(path);
		check(d.submeshes.size() == 7, "seven quads imported (" + std::to_string(d.submeshes.size()) + ")");
		const SubMeshData* leaves = by_material(d, "Leaves");
		const SubMeshData* glass = by_material(d, "Glass");
		const SubMeshData* plain = by_material(d, "Plain");
		const SubMeshData* fence = by_material(d, "Fence");
		const SubMeshData* solid = by_material(d, "Solid");
		const SubMeshData* rock = by_material(d, "Rock");
		const SubMeshData* pane = by_material(d, "Pane");
		check(leaves && leaves->blend_mode == 3 && near(leaves->alpha_cutoff, 0.3f), "MASK cuts out at its alphaCutoff");
		check(leaves && leaves->two_sided, "doubleSided draws both faces of a MASK material");
		check(glass && glass->blend_mode == 1 && glass->alpha_cutoff == 0.0f && !glass->two_sided, "BLEND blends, one-sided");
		check(pane && pane->blend_mode == 1 && pane->two_sided, "...and doubleSided draws both faces of a BLEND material");
		check(plain && plain->blend_mode == 0 && plain->alpha_cutoff == 0.0f && !plain->two_sided, "no alphaMode = opaque");
		check(fence && fence->blend_mode == 3 && near(fence->alpha_cutoff, 0.5f), "MASK without a cutoff uses the glTF default 0.5");
		check(solid && solid->blend_mode == 0 && solid->alpha_cutoff == 0.0f, "OPAQUE ignores an alphaCutoff");
		check(rock && rock->blend_mode == 0 && !rock->two_sided, "an OPAQUE material keeps back-face culling despite doubleSided");
	}

	// An uncompressed TGA (stb reads it; a glTF reader does not look at the format) of w x h texels, 24 or 32 bits.
	static std::vector<unsigned char> tga(int w, int h, int bits, unsigned char alpha_left, unsigned char alpha_right)
	{
		std::vector<unsigned char> b(18, 0);
		b[2] = 2;   // uncompressed true colour
		b[12] = (unsigned char)(w & 255); b[13] = (unsigned char)(w >> 8);
		b[14] = (unsigned char)(h & 255); b[15] = (unsigned char)(h >> 8);
		b[16] = (unsigned char)bits;
		b[17] = (unsigned char)(bits == 32 ? 8 : 0);
		for (int y = 0; y < h; ++y)
			for (int x = 0; x < w; ++x)
			{
				b.push_back(40); b.push_back(160); b.push_back(40);   // B G R
				if (bits == 32) b.push_back(x < w / 2 ? alpha_left : alpha_right);
			}
		return b;
	}

	// #441 review: a BLEND that cannot show anything through — base colour alpha 1 and an albedo without a translucent
	// texel — is drawn opaque (shadows, SSAO and depth stay); a translucent image or a factor below 1 keeps BLEND
	void opaque_blend_is_opaque()
	{
		std::printf("BLEND that is really opaque (#441)\n");
		const auto dir = m_dir / "blend";
		std::error_code ec;
		std::filesystem::create_directories(dir, ec);
		auto put = [&](const char* name, const std::vector<unsigned char>& bytes) { std::ofstream(dir / name, std::ios::binary).write((const char*)bytes.data(), (std::streamsize)bytes.size()); };
		put("rgb.tga", tga(8, 8, 24, 255, 255));
		put("rgba_full.tga", tga(8, 8, 32, 255, 255));
		put("rgba_holes.tga", tga(8, 8, 32, 0, 255));
		const std::vector<unsigned char> holes = tga(8, 8, 32, 0, 255), full = tga(8, 8, 32, 255, 255);
		const std::string mats =
			"{\"name\":\"Crate\",\"alphaMode\":\"BLEND\",\"doubleSided\":true},"
			"{\"name\":\"WireRgb\",\"alphaMode\":\"BLEND\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}},"
			"{\"name\":\"WireFull\",\"alphaMode\":\"BLEND\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":1}}},"
			"{\"name\":\"Lace\",\"alphaMode\":\"BLEND\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":2}}},"
			"{\"name\":\"Tinted\",\"alphaMode\":\"BLEND\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[1,1,1,0.5],\"baseColorTexture\":{\"index\":0}}},"
			"{\"name\":\"EmbeddedFull\",\"alphaMode\":\"BLEND\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":3}}},"
			"{\"name\":\"EmbeddedHoles\",\"alphaMode\":\"BLEND\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":4}}}";
		const std::string images =
			"{\"uri\":\"rgb.tga\"},{\"uri\":\"rgba_full.tga\"},{\"uri\":\"rgba_holes.tga\"},"
			"{\"uri\":\"data:image/png;base64," + base64(full) + "\"},{\"uri\":\"data:image/png;base64," + base64(holes) + "\"}";
		const std::string textures = "{\"source\":0},{\"source\":1},{\"source\":2},{\"source\":3},{\"source\":4}";
		const std::string path = (dir / "blend.gltf").string();
		std::ofstream(path, std::ios::binary) << gltf(mats, 7, textures, images);
		const ImportedModelData d = ModelImporter::import_from_file(path);
		auto mode = [&](const char* name) { const SubMeshData* s = by_material(d, name); return s ? (int)s->blend_mode : -1; };
		check(mode("Crate") == 0, "BLEND, alpha 1, no image: opaque");
		const SubMeshData* crate = by_material(d, "Crate");
		check(crate && crate->two_sided, "...keeping the doubleSided of a material the file marks BLEND");
		check(mode("WireRgb") == 0, "BLEND, alpha 1, an image without an alpha channel: opaque");
		check(mode("WireFull") == 0, "BLEND, alpha 1, an alpha channel of 255 everywhere: opaque");
		check(mode("Lace") == 1, "an image with transparent texels keeps BLEND");
		check(mode("Tinted") == 1, "a base colour alpha below 1 keeps BLEND");
		check(mode("EmbeddedFull") == 0, "an embedded opaque image: opaque");
		check(mode("EmbeddedHoles") == 1, "an embedded image with transparent texels keeps BLEND");
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

		// a cache written before the alpha state (v1), the texture transforms (v2, #470), the emission (v3, #450) or the
		// opaque-BLEND / doubleSided rules (v4) were stored: rejected, so the model is imported again
		const auto vmc = std::filesystem::path(dir) / "materials.vmc";
		for (u32 version = 1; version <= 4; ++version)
		{
			{
				std::fstream f(vmc, std::ios::in | std::ios::out | std::ios::binary);
				f.seekp(4);
				f.write(reinterpret_cast<const char*>(&version), sizeof(version));
			}
			ImportedModelData old;
			check(!MeshSerializer::load_submeshes_from_dir(dir, old), "a version " + std::to_string(version) + " cache is not used");
		}
	}

	// #453: a glTF material without baseColorTexture keeps its colour — no image from the folder, not even a colour-named
	// one, and the glTF default material (a primitive without "material") neither
	void colour_only_materials_stay_untextured()
	{
		std::printf("colour-only materials (#453)\n");
		const auto dir = m_dir / "car";
		std::error_code ec;
		std::filesystem::create_directories(dir, ec);
		std::ofstream(dir / "paint_albedo.png", std::ios::binary) << "not really a png";   // what the folder scan looks for
		std::ofstream(dir / "body.png", std::ios::binary) << "not really a png";
		const std::string doc = gltf(
			"{\"name\":\"Body\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}},\"normalTexture\":{\"index\":0}},"
			"{\"name\":\"Rubber\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.05,0.05,0.05,1]}}", 2,
			"{\"source\":0}", "{\"uri\":\"body.png\"}");
		const std::string path = (dir / "car.gltf").string();
		std::ofstream(path, std::ios::binary) << doc;
		const ImportedModelData d = ModelImporter::import_from_file(path);
		const SubMeshData* body = by_material(d, "Body");
		const SubMeshData* rubber = by_material(d, "Rubber");
		check(body && body->albedo_source == 1 && body->diffuse_texture.find("body.png") != std::string::npos, "the textured material keeps its own map");
		check(rubber && rubber->albedo_source == 0, "the colour-only material is reported as such");
		check(rubber && rubber->diffuse_texture.empty(), "and gets no image from the folder");
		check(rubber && near(rubber->base_color[0], 0.05f), "it keeps its base colour");

		// a glTF of colour-only materials next to a colour-named image: the importer's own folder scan stays out too
		const auto props = m_dir / "props";
		std::filesystem::create_directories(props, ec);
		std::ofstream(props / "wood_albedo.png", std::ios::binary) << "not really a png";
		const std::string flat = (props / "flat.gltf").string();
		std::ofstream(flat, std::ios::binary) << gltf("{\"name\":\"Red\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[1,0,0,1]}},{\"name\":\"Blue\"}", 2);
		const ImportedModelData f = ModelImporter::import_from_file(flat);
		bool untextured = f.submeshes.size() == 2;
		for (const auto& s : f.submeshes) untextured = untextured && s.diffuse_texture.empty() && s.albedo_source == 0;
		check(untextured, "an all-colour glTF takes no image from its folder");
	}

	// #453: the folder scan stands in only for a map a material names but that is missing, or for a file without
	// materials (an OBJ without .mtl) — in a stable order
	void folder_fallback_only_where_wanted()
	{
		std::printf("folder fallback (#453)\n");
		std::error_code ec;
		const auto plain = m_dir / "plain";
		std::filesystem::create_directories(plain, ec);
		std::ofstream(plain / "B_albedo.png", std::ios::binary) << "x";   // before "a_..." in a case-sensitive order
		std::ofstream(plain / "a_albedo.png", std::ios::binary) << "x";
		std::ofstream(plain / "embedded_other_0_albedo.png", std::ios::binary) << "x";   // another model's extracted map
		const std::string bare = (plain / "bare.obj").string();
		std::ofstream(bare, std::ios::binary) << "o Thing\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n";
		const ImportedModelData b = ModelImporter::import_from_file(bare);
		check(b.submeshes.size() == 1 && b.submeshes[0].albedo_source == 2, "an OBJ without .mtl has no material from the file");
		check(b.submeshes.size() == 1 && b.submeshes[0].diffuse_texture == (plain / "a_albedo.png").string(),
			"it takes the first colour texture in name order ignoring case, never an extracted one (" + (b.submeshes.empty() ? std::string() : b.submeshes[0].diffuse_texture) + ")");

		const auto kit = m_dir / "kit";
		std::filesystem::create_directories(kit, ec);
		std::ofstream(kit / "wood_albedo.png", std::ios::binary) << "x";
		std::ofstream(kit / "zz_albedo.png", std::ios::binary) << "x";   // the one the colour-only part used to get by index
		std::ofstream(kit / "kit.mtl", std::ios::binary) << "newmtl Wood\nKd 1 1 1\nmap_Kd C:/Users/artist/textures/wood_final.png\nnewmtl Paint\nKd 0.8 0.1 0.1\n";
		const std::string obj = (kit / "kit.obj").string();
		std::ofstream(obj, std::ios::binary) << "mtllib kit.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nv 1 1 0\no Plank\nusemtl Wood\nf 1 2 3\no Panel\nusemtl Paint\nf 2 4 3\n";
		const ImportedModelData k = ModelImporter::import_from_file(obj);
		const SubMeshData* wood = by_material(k, "Wood");
		const SubMeshData* paint = by_material(k, "Paint");
		check(wood && wood->albedo_source == 1 && wood->diffuse_texture == (kit / "wood_albedo.png").string(), "a missing map is stood in for by the folder's texture");
		check(paint && paint->albedo_source == 0 && paint->diffuse_texture.empty(), "the colour-only material next to it stays untextured");
	}

	// ---- #470: KHR_texture_transform ----

	// the glTF spec, as the Khronos sample viewer (and Blender, three.js) apply it: uv' = offset + R * (scale * uv) with
	// R = [cos sin; -sin cos] — glTF's UV origin is the top-left corner, as the engine's
	static void reference_uv(float ox, float oy, float r, float sx, float sy, float u, float v, float& ou, float& ov)
	{
		const float c = std::cos(r), s = std::sin(r);
		ou = ox + c * sx * u + s * sy * v;
		ov = oy - s * sx * u + c * sy * v;
	}

	static std::string transform_json(float ox, float oy, float r, float sx, float sy)
	{
		char b[256];
		std::snprintf(b, sizeof(b), "\"extensions\":{\"KHR_texture_transform\":{\"offset\":[%.9g,%.9g],\"rotation\":%.9g,\"scale\":[%.9g,%.9g]}}", ox, oy, r, sx, sy);
		return b;
	}

	// the corner of the quad a vertex sits on (-1 when none)
	static int corner(const VertexPosNormalUV& v)
	{
		for (int k = 0; k < 4; ++k)
			if (near(v.position.x, kPos[k][0]) && near(v.position.y, kPos[k][1]) && near(v.position.z, kPos[k][2])) return k;
		return -1;
	}

	// the largest distance of a submesh's UVs from uv(corner)
	template <typename F> static float uv_error(const SubMeshData* s, F expected)
	{
		if (!s || s->vertices.empty()) return 1e9f;
		float worst = 0.0f;
		for (const auto& v : s->vertices)
		{
			const int k = corner(v);
			if (k < 0) return 1e9f;
			float eu, ev;
			expected(k, eu, ev);
			worst = (std::max)(worst, (std::max)(std::fabs(v.uv.x - eu), std::fabs(v.uv.y - ev)));
		}
		return worst;
	}

	void texture_transforms()
	{
		std::printf("KHR_texture_transform (#470)\n");
		struct Case { const char* name; float ox, oy, r, sx, sy; };
		const Case cases[] = {
			{ "Offset", 0.25f, -0.5f, 0.0f, 1.0f, 1.0f },
			{ "Scale", 0.0f, 0.0f, 0.0f, 2.0f, 3.0f },
			{ "Rotation", 0.0f, 0.0f, 0.3f, 1.0f, 1.0f },
			{ "All", 0.25f, 0.5f, 0.3f, 2.0f, 3.0f },
			{ "Scan", 1.9e-05f, 1.90735e-05f, 0.0f, 15.9317f, 16.0031f },   // a Sketchfab photogrammetry scan
			{ "Fir", 0.0f, 0.9f, 0.0f, 1.2f, 0.1f },                         // Poly Haven fir_tree_01
		};
		std::string mats;
		for (const auto& c : cases)
			mats += "{\"name\":\"" + std::string(c.name) + "\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0," + transform_json(c.ox, c.oy, c.r, c.sx, c.sy) + "}}},";
		mats += "{\"name\":\"Plain\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}},{\"name\":\"Colour\"}";
		const int n = (int)(sizeof(cases) / sizeof(cases[0])) + 2;
		const std::string path = write("transforms.gltf", gltf(mats, n, "{\"source\":0}", "{\"uri\":\"t.png\"}", "\"KHR_texture_transform\""));
		const ImportedModelData d = ModelImporter::import_from_file(path);
		check((int)d.submeshes.size() == n, "every quad imported");
		for (const auto& c : cases)
		{
			const float err = uv_error(by_material(d, c.name), [&](int k, float& u, float& v) { reference_uv(c.ox, c.oy, c.r, c.sx, c.sy, kUv[k][0], kUv[k][1], u, v); });
			check(err < 1e-4f, std::string(c.name) + ": the UVs carry the transform as glTF defines it (off by " + std::to_string(err) + ")");
		}
		auto raw = [](int k, float& u, float& v) { u = kUv[k][0]; v = kUv[k][1]; };
		check(uv_error(by_material(d, "Plain"), raw) < 1e-6f, "a map without a transform: the UVs as stored");
		check(uv_error(by_material(d, "Colour"), raw) < 1e-6f, "an untextured material: the UVs as stored");
		check(d.warnings.empty(), "one transform per material: nothing to warn about");
	}

	void texture_transform_conflicts()
	{
		std::printf("UV sets and conflicting transforms (#470)\n");
		const std::string x16 = transform_json(1.9e-05f, 1.90735e-05f, 0.0f, 15.9317f, 16.0031f);
		const std::string x2 = transform_json(0.0f, 0.0f, 0.0f, 2.0f, 2.0f);
		const std::string x4 = transform_json(0.0f, 0.0f, 0.0f, 4.0f, 4.0f);
		// a scan's pattern: every map on its own TEXCOORD (byte-identical data) with the same x16 transform; and a material
		// whose normal map tiles differently from its base colour, used by two quads
		const std::string mats =
			"{\"name\":\"Scan\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0," + x16 + "},"
			"\"metallicRoughnessTexture\":{\"index\":0,\"texCoord\":1," + x16 + "}},"
			"\"normalTexture\":{\"index\":0,\"texCoord\":1," + x16 + "},\"emissiveTexture\":{\"index\":0,\"texCoord\":1," + x16 + "},"
			"\"occlusionTexture\":{\"index\":0,\"texCoord\":1," + x16 + "}},"
			"{\"name\":\"Mixed\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0," + x2 + "}},\"normalTexture\":{\"index\":0," + x4 + "}}";
		const std::string path = write("scan.gltf", gltf_quads(mats, { 0, 1, 1 }, "{\"source\":0}", "{\"uri\":\"t.png\"}", "\"KHR_texture_transform\"", false));
		const ImportedModelData d = ModelImporter::import_from_file(path);
		const float err16 = uv_error(by_material(d, "Scan"), [](int k, float& u, float& v) { reference_uv(1.9e-05f, 1.90735e-05f, 0.0f, 15.9317f, 16.0031f, kUv[k][0], kUv[k][1], u, v); });
		check(err16 < 1e-4f, "a scan's maps on identical UV sets with one transform: transformed, no conflict");
		const float err2 = uv_error(by_material(d, "Mixed"), [](int k, float& u, float& v) { u = 2.0f * kUv[k][0]; v = 2.0f * kUv[k][1]; });
		check(err2 < 1e-4f, "different transforms per map: the base colour's is kept");
		size_t mixed = 0, scan = 0;
		for (const auto& w : d.warnings)
		{
			if (w.find("'Mixed'") != std::string::npos) ++mixed;
			if (w.find("'Scan'") != std::string::npos) ++scan;
		}
		check(mixed == 1, "one warning names the material, however many meshes use it (" + std::to_string(d.warnings.size()) + " warning(s))");
		check(scan == 0, "the scan's material is not warned about");
		check(ModelImporter::last_warnings() == d.warnings, "the warnings stay readable after the import (for the editor's console)");

		// an explicit identity transform is no transform: the UVs stay, and a map of the same material without one is no conflict
		const std::string identity = transform_json(0.0f, 0.0f, 0.0f, 1.0f, 1.0f);
		const ImportedModelData idm = ModelImporter::import_from_file(write("identity.gltf", gltf(
			"{\"name\":\"Identity\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0," + identity + "}},\"normalTexture\":{\"index\":0}}", 1,
			"{\"source\":0}", "{\"uri\":\"t.png\"}", "\"KHR_texture_transform\"")));
		check(idm.warnings.empty(), "an explicit identity next to a map without a transform: no warning");
		check(uv_error(by_material(idm, "Identity"), [](int k, float& u, float& v) { u = kUv[k][0]; v = kUv[k][1]; }) < 1e-6f, "...and the UVs as stored");

		// lightmapped: the base colour on set 0, the occlusion map on set 1 with other data — it gets set 0 too, so warn;
		// and a base colour that reads set 1 takes the vertex UVs from set 1
		const std::string sets =
			"{\"name\":\"Lightmapped\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}},\"occlusionTexture\":{\"index\":0,\"texCoord\":1}},"
			"{\"name\":\"SecondSet\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0,\"texCoord\":1}}}";
		const ImportedModelData s = ModelImporter::import_from_file(write("sets.gltf", gltf_quads(sets, { 0, 1 }, "{\"source\":0}", "{\"uri\":\"t.png\"}", "", true)));
		bool lightmapped = false;
		for (const auto& w : s.warnings) if (w.find("'Lightmapped'") != std::string::npos) lightmapped = true;
		check(lightmapped, "a map on a UV set with other data is warned about");
		check(uv_error(by_material(s, "Lightmapped"), [](int k, float& u, float& v) { u = kUv[k][0]; v = kUv[k][1]; }) < 1e-6f, "...and the base colour's set is used");
		check(uv_error(by_material(s, "SecondSet"), [](int k, float& u, float& v) { u = kUv1[k][0]; v = kUv1[k][1]; }) < 1e-6f, "a base colour on TEXCOORD_1 takes the vertex UVs from it");
	}

	// #450: glTF emissiveFactor and KHR_materials_emissive_strength reach the submesh (and the import cache)
	void emission()
	{
		std::printf("glTF emission (#450)\n");
		const std::string path = write("emission.gltf", gltf(
			"{\"name\":\"Neon\",\"emissiveFactor\":[1,0.5,0.25],\"extensions\":{\"KHR_materials_emissive_strength\":{\"emissiveStrength\":4}}},"
			"{\"name\":\"Lamp\",\"emissiveFactor\":[1,0.72,0.4]},"
			"{\"name\":\"Screen\",\"emissiveTexture\":{\"index\":0}},"
			"{\"name\":\"Dull\"}", 4, "{\"source\":0}", "{\"uri\":\"t.png\"}", "\"KHR_materials_emissive_strength\""));
		ImportedModelData d = ModelImporter::import_from_file(path);
		auto emits = [&](const char* name, float r, float g, float b, float strength)
		{
			const SubMeshData* s = by_material(d, name);
			return s && near(s->emissive_color[0], r) && near(s->emissive_color[1], g) && near(s->emissive_color[2], b) && near(s->emissive_strength, strength);
		};
		check(emits("Neon", 1.0f, 0.5f, 0.25f, 4.0f), "emissiveFactor with KHR_materials_emissive_strength");
		check(emits("Lamp", 1.0f, 0.72f, 0.4f, 1.0f), "emissiveFactor alone: strength 1");
		check(emits("Screen", 0.0f, 0.0f, 0.0f, 1.0f), "an emissive map without a factor: black, it does not glow (glTF default)");
		check(emits("Dull", 0.0f, 0.0f, 0.0f, 1.0f), "no emission");

		const std::string dir = (m_dir / "cache_emission").string();
		check(MeshSerializer::save_submeshes_to_dir(d, dir) == 4, "cached");
		ImportedModelData back;
		bool same = MeshSerializer::load_submeshes_from_dir(dir, back) && back.submeshes.size() == d.submeshes.size();
		for (size_t i = 0; same && i < d.submeshes.size(); ++i)
			same = back.submeshes[i].emissive_color[0] == d.submeshes[i].emissive_color[0] && back.submeshes[i].emissive_color[1] == d.submeshes[i].emissive_color[1]
				&& back.submeshes[i].emissive_color[2] == d.submeshes[i].emissive_color[2] && back.submeshes[i].emissive_strength == d.submeshes[i].emissive_strength;
		check(same, "the emission survives the import cache");

		// other formats keep their old reading: an OBJ "Ke" is not taken over
		write("glow.mtl", "newmtl Glow\nKd 1 1 1\nKe 1 0 0\n");
		const ImportedModelData o = ModelImporter::import_from_file(write("glow.obj", "mtllib glow.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl Glow\nf 1 2 3\n"));
		check(o.submeshes.size() == 1 && o.submeshes[0].emissive_color[0] == 0.0f, "an OBJ's emissive colour is left as before");
	}
};
