#include "MeshSerializer.h"
#include <fstream>
#include <cstring>

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
}
