#include "MeshSerializer.h"
#include <fstream>
#include <cstring>
#include <filesystem>
#include <limits>
#include <algorithm>

namespace vortex::graphics
{
	bool MeshSerializer::save_to_file(const ImportedModelData& data, const std::string& filepath)
	{
		if (!data.is_valid())
			return false;

		std::ofstream file(filepath, std::ios::binary);
		if (!file.is_open())
			return false;

		// Write header
		VMeshHeader header;
		header.submesh_count = static_cast<u32>(data.submeshes.size());
		header.bounds_min = data.bounds_min;
		header.bounds_max = data.bounds_max;
		strncpy_s(header.name, data.name.c_str(), sizeof(header.name) - 1);

		file.write(reinterpret_cast<const char*>(&header), sizeof(VMeshHeader));

		// Write each submesh
		for (const auto& submesh : data.submeshes)
		{
			VMeshSubMesh submesh_header;
			submesh_header.vertex_count = static_cast<u32>(submesh.vertices.size());
			submesh_header.index_count = static_cast<u32>(submesh.indices.size());
			submesh_header.material_index = submesh.material_index;
			strncpy_s(submesh_header.name, submesh.name.c_str(), sizeof(submesh_header.name) - 1);

			file.write(reinterpret_cast<const char*>(&submesh_header), sizeof(VMeshSubMesh));

			// Write vertices
			file.write(reinterpret_cast<const char*>(submesh.vertices.data()),
				submesh.vertices.size() * sizeof(VertexPosNormalUV));

			// Write indices
			file.write(reinterpret_cast<const char*>(submesh.indices.data()),
				submesh.indices.size() * sizeof(u32));
		}

		file.close();
		return true;
	}

	ImportedModelData MeshSerializer::load_from_file(const std::string& filepath)
	{
		ImportedModelData result;

		std::ifstream file(filepath, std::ios::binary);
		if (!file.is_open())
			return result;

		// Read header
		VMeshHeader header;
		file.read(reinterpret_cast<char*>(&header), sizeof(VMeshHeader));
		
		// Check if read was successful
		if (!file.good())
		{
			return result;
		}

		// Validate magic and version
		if (header.magic != VMESH_MAGIC || header.version != VMESH_VERSION)
		{
			return result;
		}

		result.name = header.name;
		result.bounds_min = header.bounds_min;
		result.bounds_max = header.bounds_max;
		result.submeshes.reserve(header.submesh_count);

		// Read each submesh
		for (u32 i = 0; i < header.submesh_count; ++i)
		{
			VMeshSubMesh submesh_header;
			file.read(reinterpret_cast<char*>(&submesh_header), sizeof(VMeshSubMesh));

			SubMeshData submesh;
			submesh.name = submesh_header.name;
			submesh.material_index = submesh_header.material_index;

			// Read vertices
			submesh.vertices.resize(submesh_header.vertex_count);
			file.read(reinterpret_cast<char*>(submesh.vertices.data()),
				submesh_header.vertex_count * sizeof(VertexPosNormalUV));

			// Read indices
			submesh.indices.resize(submesh_header.index_count);
			file.read(reinterpret_cast<char*>(submesh.indices.data()),
				submesh_header.index_count * sizeof(u32));

			result.submeshes.push_back(std::move(submesh));
		}

		file.close();
		return result;
	}

	bool MeshSerializer::write_string(std::ofstream& file, const std::string& str, size_t max_length)
	{
		std::vector<char> buffer(max_length, 0);
		strncpy_s(buffer.data(), max_length, str.c_str(), max_length - 1);
		file.write(buffer.data(), max_length);
		return file.good();
	}

	std::string MeshSerializer::read_string(std::ifstream& file, size_t max_length)
	{
		std::vector<char> buffer(max_length, 0);
		file.read(buffer.data(), max_length);
		return std::string(buffer.data());
	}

	namespace
	{
		// materials.vmc: the material records of a cached model (#364 C), next to its submesh_N.vmesh files — what
		// build_model_result reads from each submesh besides its geometry.
		constexpr u32 kCacheMagic = 0x434D4D56;   // "VMMC"
		constexpr u32 kCacheVersion = 1;
		constexpr u32 kMaxString = 1u << 16;
		constexpr u32 kMaxSubmeshes = 4096;

		void put_u32(std::ofstream& f, u32 v) { f.write(reinterpret_cast<const char*>(&v), sizeof(v)); }
		void put_f32(std::ofstream& f, float v) { f.write(reinterpret_cast<const char*>(&v), sizeof(v)); }
		void put_u8(std::ofstream& f, u8 v) { f.write(reinterpret_cast<const char*>(&v), sizeof(v)); }
		void put_str(std::ofstream& f, const std::string& s)
		{
			put_u32(f, static_cast<u32>(s.size()));
			f.write(s.data(), static_cast<std::streamsize>(s.size()));
		}
		bool get_u32(std::ifstream& f, u32& v) { f.read(reinterpret_cast<char*>(&v), sizeof(v)); return f.good(); }
		bool get_f32(std::ifstream& f, float& v) { f.read(reinterpret_cast<char*>(&v), sizeof(v)); return f.good(); }
		bool get_u8(std::ifstream& f, u8& v) { f.read(reinterpret_cast<char*>(&v), sizeof(v)); return f.good(); }
		bool get_str(std::ifstream& f, std::string& s)
		{
			u32 n = 0;
			if (!get_u32(f, n) || n > kMaxString) return false;
			s.resize(n);
			if (n) f.read(&s[0], static_cast<std::streamsize>(n));
			return f.good();
		}
		std::string cache_file(const std::string& dir, const std::string& name)
		{
			return (std::filesystem::path(dir) / name).string();
		}
		std::string submesh_file(const std::string& dir, size_t i)
		{
			return cache_file(dir, "submesh_" + std::to_string(i) + ".vmesh");
		}
	}

	u32 MeshSerializer::save_submeshes_to_dir(ImportedModelData& data, const std::string& dir)
	{
		if (!data.is_valid() || dir.empty() || data.submeshes.size() > kMaxSubmeshes) return 0;
		for (const auto& sm : data.submeshes)
			if (sm.has_skin() || sm.vertices.empty()) return 0;   // no bone weights in the format: not cacheable

		std::error_code ec;
		std::filesystem::create_directories(std::filesystem::path(dir), ec);

		// one single-submesh .vmesh per submesh — the geometry is moved out and back, never copied
		ImportedModelData one;
		one.submeshes.resize(1);
		u32 written = 0;
		for (size_t i = 0; i < data.submeshes.size(); ++i)
		{
			auto& src = data.submeshes[i];
			auto& dst = one.submeshes[0];
			dst.vertices.swap(src.vertices);
			dst.indices.swap(src.indices);
			dst.material_index = src.material_index;
			dst.name = src.name.empty() ? ("Submesh_" + std::to_string(i)) : src.name;
			one.name = dst.name;

			const float inf = std::numeric_limits<float>::max();
			one.bounds_min = { inf, inf, inf };
			one.bounds_max = { -inf, -inf, -inf };
			for (const auto& v : dst.vertices)
			{
				one.bounds_min.x = (std::min)(one.bounds_min.x, v.position.x); one.bounds_max.x = (std::max)(one.bounds_max.x, v.position.x);
				one.bounds_min.y = (std::min)(one.bounds_min.y, v.position.y); one.bounds_max.y = (std::max)(one.bounds_max.y, v.position.y);
				one.bounds_min.z = (std::min)(one.bounds_min.z, v.position.z); one.bounds_max.z = (std::max)(one.bounds_max.z, v.position.z);
			}

			const bool ok = save_to_file(one, submesh_file(dir, i));

			src.vertices.swap(dst.vertices);
			src.indices.swap(dst.indices);
			if (!ok) return 0;
			++written;
		}

		std::ofstream f(cache_file(dir, "materials.vmc"), std::ios::binary);
		if (!f.is_open()) return 0;
		put_u32(f, kCacheMagic);
		put_u32(f, kCacheVersion);
		put_u32(f, written);
		put_u8(f, data.gltf ? 1 : 0);
		put_str(f, data.name);
		for (const auto& sm : data.submeshes)
		{
			put_u32(f, sm.material_index);
			for (float c : sm.base_color) put_f32(f, c);
			put_f32(f, sm.metallic);
			put_f32(f, sm.roughness);
			put_u8(f, sm.metallic_channel);
			put_u8(f, sm.roughness_channel);
			put_u8(f, sm.ao_channel);
			put_u8(f, sm.normal_opengl ? 1 : 0);
			put_u8(f, sm.normal_is_bump ? 1 : 0);
			put_str(f, sm.diffuse_texture);
			put_str(f, sm.normal_texture);
			put_str(f, sm.metallic_texture);
			put_str(f, sm.roughness_texture);
			put_str(f, sm.ao_texture);
			put_str(f, sm.emissive_texture);
		}
		f.close();
		return f.good() ? written : 0;
	}

	bool MeshSerializer::load_submeshes_from_dir(const std::string& dir, ImportedModelData& out)
	{
		out.clear();
		std::ifstream f(cache_file(dir, "materials.vmc"), std::ios::binary);
		if (!f.is_open()) return false;

		u32 magic = 0, version = 0, count = 0;
		u8 gltf = 0;
		if (!get_u32(f, magic) || !get_u32(f, version) || !get_u32(f, count)) return false;
		if (magic != kCacheMagic || version != kCacheVersion || count == 0 || count > kMaxSubmeshes) return false;
		if (!get_u8(f, gltf) || !get_str(f, out.name)) return false;
		out.gltf = gltf != 0;

		const float inf = std::numeric_limits<float>::max();
		out.bounds_min = { inf, inf, inf };
		out.bounds_max = { -inf, -inf, -inf };
		out.submeshes.reserve(count);
		for (u32 i = 0; i < count; ++i)
		{
			ImportedModelData one = load_from_file(submesh_file(dir, i));
			if (!one.is_valid()) { out.clear(); return false; }
			SubMeshData sm = std::move(one.submeshes[0]);

			u8 flags[5]{};
			if (!get_u32(f, sm.material_index)
				|| !get_f32(f, sm.base_color[0]) || !get_f32(f, sm.base_color[1]) || !get_f32(f, sm.base_color[2]) || !get_f32(f, sm.base_color[3])
				|| !get_f32(f, sm.metallic) || !get_f32(f, sm.roughness)
				|| !get_u8(f, flags[0]) || !get_u8(f, flags[1]) || !get_u8(f, flags[2]) || !get_u8(f, flags[3]) || !get_u8(f, flags[4])
				|| !get_str(f, sm.diffuse_texture) || !get_str(f, sm.normal_texture) || !get_str(f, sm.metallic_texture)
				|| !get_str(f, sm.roughness_texture) || !get_str(f, sm.ao_texture) || !get_str(f, sm.emissive_texture))
			{
				out.clear();
				return false;
			}
			sm.metallic_channel = flags[0];
			sm.roughness_channel = flags[1];
			sm.ao_channel = flags[2];
			sm.normal_opengl = flags[3] != 0;
			sm.normal_is_bump = flags[4] != 0;

			out.bounds_min.x = (std::min)(out.bounds_min.x, one.bounds_min.x); out.bounds_max.x = (std::max)(out.bounds_max.x, one.bounds_max.x);
			out.bounds_min.y = (std::min)(out.bounds_min.y, one.bounds_min.y); out.bounds_max.y = (std::max)(out.bounds_max.y, one.bounds_max.y);
			out.bounds_min.z = (std::min)(out.bounds_min.z, one.bounds_min.z); out.bounds_max.z = (std::max)(out.bounds_max.z, one.bounds_max.z);
			out.submeshes.push_back(std::move(sm));
		}
		return out.is_valid();
	}
}
