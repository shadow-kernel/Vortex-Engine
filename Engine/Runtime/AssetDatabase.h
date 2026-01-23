#pragma once

#include "../Common/CommonHeaders.h"
#include "../Common/Id.h"
#include "AssetManifest.h"
#include <string>
#include <unordered_map>
#include <memory>

namespace vortex::runtime {

	/// <summary>
	/// Runtime asset database that resolves GUIDs to file paths.
	/// Can work with AssetManifest for packaged games or direct file system for editor.
	/// </summary>
	class AssetDatabase
	{
	public:
		static AssetDatabase& instance();

		// Initialize with project path (for editor) or manifest file (for runtime)
		void initialize_with_project_path(const char* project_path);
		void initialize_with_manifest(const char* manifest_path);
		void shutdown();

		// GUID-based asset path resolution
		const char* get_asset_path_by_guid(const char* guid) const;
		bool has_asset(const char* guid) const;

		// Direct path access (for backward compatibility)
		std::string resolve_path(const char* relative_path) const;

		bool is_initialized() const { return m_initialized; }

	private:
		AssetDatabase() = default;

		bool m_initialized{ false };
		std::string m_project_path;
		std::unique_ptr<AssetManifest> m_manifest;

		// Cache for resolved paths
		mutable std::unordered_map<std::string, std::string> m_path_cache;
	};
}
