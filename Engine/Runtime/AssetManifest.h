#pragma once

#include "../Common/CommonHeaders.h"
#include "../Common/Id.h"
#include <string>
#include <unordered_map>
#include <vector>

namespace vortex::runtime {

	/// <summary>
	/// Runtime asset manifest - maps GUIDs to asset paths.
	/// Used by exported games to load assets by GUID.
	/// </summary>
	struct asset_manifest_entry
	{
		std::string guid;
		std::string relative_path;
		u32 asset_type;
	};

	class AssetManifest
	{
	public:
		AssetManifest() = default;

		// Load manifest from binary file
		bool load_from_file(const char* manifest_path);

		// Get asset path by GUID
		const std::string* get_asset_path(const std::string& guid) const;

		// Get all assets of a specific type
		std::vector<std::string> get_assets_by_type(u32 asset_type) const;

		// Check if asset exists
		bool has_asset(const std::string& guid) const;

	private:
		std::unordered_map<std::string, asset_manifest_entry> m_assets;
		std::string m_base_path;
	};
}
