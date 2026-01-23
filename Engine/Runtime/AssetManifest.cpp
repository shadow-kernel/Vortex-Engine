#include "AssetManifest.h"
#include <fstream>
#include <filesystem>

namespace vortex::runtime {

	bool AssetManifest::load_from_file(const char* manifest_path)
	{
		std::ifstream file(manifest_path, std::ios::binary);
		if (!file.is_open())
			return false;

		// Simple binary format:
		// [version:u32][entry_count:u32]
		// For each entry: [guid_len:u32][guid:char*][path_len:u32][path:char*][type:u32]

		u32 version = 0;
		file.read(reinterpret_cast<char*>(&version), sizeof(u32));
		
		if (version != 1)
			return false;

		u32 entry_count = 0;
		file.read(reinterpret_cast<char*>(&entry_count), sizeof(u32));

		m_assets.clear();
		m_assets.reserve(entry_count);

		for (u32 i = 0; i < entry_count; ++i)
		{
			asset_manifest_entry entry;

			// Read GUID
			u32 guid_len = 0;
			file.read(reinterpret_cast<char*>(&guid_len), sizeof(u32));
			entry.guid.resize(guid_len);
			file.read(&entry.guid[0], guid_len);

			// Read path
			u32 path_len = 0;
			file.read(reinterpret_cast<char*>(&path_len), sizeof(u32));
			entry.relative_path.resize(path_len);
			file.read(&entry.relative_path[0], path_len);

			// Read type
			file.read(reinterpret_cast<char*>(&entry.asset_type), sizeof(u32));

			m_assets[entry.guid] = entry;
		}

		// Store base path (directory of manifest)
		std::filesystem::path manifest_file_path(manifest_path);
		m_base_path = manifest_file_path.parent_path().string();

		return true;
	}

	const std::string* AssetManifest::get_asset_path(const std::string& guid) const
	{
		auto it = m_assets.find(guid);
		if (it == m_assets.end())
			return nullptr;

		return &it->second.relative_path;
	}

	std::vector<std::string> AssetManifest::get_assets_by_type(u32 asset_type) const
	{
		std::vector<std::string> result;
		for (const auto& [guid, entry] : m_assets)
		{
			if (entry.asset_type == asset_type)
				result.push_back(guid);
		}
		return result;
	}

	bool AssetManifest::has_asset(const std::string& guid) const
	{
		return m_assets.find(guid) != m_assets.end();
	}
}
