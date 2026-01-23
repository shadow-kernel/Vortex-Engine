#include "AssetDatabase.h"
#include <filesystem>

namespace vortex::runtime {

	AssetDatabase& AssetDatabase::instance()
	{
		static AssetDatabase instance;
		return instance;
	}

	void AssetDatabase::initialize_with_project_path(const char* project_path)
	{
		if (m_initialized)
			shutdown();

		m_project_path = project_path;
		m_initialized = true;
		m_manifest.reset();
	}

	void AssetDatabase::initialize_with_manifest(const char* manifest_path)
	{
		if (m_initialized)
			shutdown();

		m_manifest = std::make_unique<AssetManifest>();
		
		if (!m_manifest->load_from_file(manifest_path))
		{
			m_manifest.reset();
			m_initialized = false;
			return;
		}

		// Extract base path from manifest location
		std::filesystem::path manifest_file_path(manifest_path);
		m_project_path = manifest_file_path.parent_path().string();
		m_initialized = true;
	}

	void AssetDatabase::shutdown()
	{
		m_initialized = false;
		m_project_path.clear();
		m_manifest.reset();
		m_path_cache.clear();
	}

	const char* AssetDatabase::get_asset_path_by_guid(const char* guid) const
	{
		if (!m_initialized || !guid)
			return nullptr;

		std::string guid_str(guid);

		// Check cache first
		auto cache_it = m_path_cache.find(guid_str);
		if (cache_it != m_path_cache.end())
		{
			return cache_it->second.c_str();
		}

		// If we have a manifest, use it
		if (m_manifest)
		{
			const std::string* relative_path = m_manifest->get_asset_path(guid_str);
			if (relative_path)
			{
				std::filesystem::path full_path = std::filesystem::path(m_project_path) / *relative_path;
				
				// Cache it - note: this can invalidate iterators but we're not using any after this point
				auto [it, inserted] = m_path_cache.emplace(guid_str, full_path.string());
				return it->second.c_str();
			}
		}

		// Without manifest, we can't resolve GUIDs
		// (Editor mode would need .vmeta file scanning, which is C# responsibility)
		return nullptr;
	}

	bool AssetDatabase::has_asset(const char* guid) const
	{
		if (!m_initialized || !guid)
			return false;

		if (m_manifest)
		{
			return m_manifest->has_asset(guid);
		}

		// Without manifest, we'd need to scan files
		return false;
	}

	std::string AssetDatabase::resolve_path(const char* relative_path) const
	{
		if (!m_initialized || !relative_path)
			return "";

		std::filesystem::path full_path = std::filesystem::path(m_project_path) / relative_path;
		return full_path.string();
	}
}
