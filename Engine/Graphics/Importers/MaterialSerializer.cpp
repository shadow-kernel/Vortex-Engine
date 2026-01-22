#include "MaterialSerializer.h"
#include <fstream>
#include <cstring>

namespace vortex::graphics
{
	bool MaterialSerializer::save_to_file(const MaterialData& data, const std::string& filepath)
	{
		if (!data.is_valid())
			return false;

		std::ofstream file(filepath, std::ios::binary);
		if (!file.is_open())
			return false;

		VMaterialHeader header;
		header.properties = data.properties;
		strncpy_s(header.name, data.name.c_str(), sizeof(header.name) - 1);
		strncpy_s(header.albedo_texture, data.albedo_texture_path.c_str(), sizeof(header.albedo_texture) - 1);
		strncpy_s(header.normal_texture, data.normal_texture_path.c_str(), sizeof(header.normal_texture) - 1);
		strncpy_s(header.metallic_roughness_texture, data.metallic_roughness_texture_path.c_str(), 
			sizeof(header.metallic_roughness_texture) - 1);

		file.write(reinterpret_cast<const char*>(&header), sizeof(VMaterialHeader));
		file.close();

		return true;
	}

	MaterialData MaterialSerializer::load_from_file(const std::string& filepath)
	{
		MaterialData result;

		std::ifstream file(filepath, std::ios::binary);
		if (!file.is_open())
			return result;

		VMaterialHeader header;
		file.read(reinterpret_cast<char*>(&header), sizeof(VMaterialHeader));

		// Validate magic and version
		if (header.magic != VMAT_MAGIC || header.version != VMAT_VERSION)
		{
			return result;
		}

		result.name = header.name;
		result.properties = header.properties;
		result.albedo_texture_path = header.albedo_texture;
		result.normal_texture_path = header.normal_texture;
		result.metallic_roughness_texture_path = header.metallic_roughness_texture;

		file.close();
		return result;
	}
}
