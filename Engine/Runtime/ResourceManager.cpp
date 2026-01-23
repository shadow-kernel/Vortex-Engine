#include "ResourceManager.h"
#include "AssetDatabase.h"

#include <unordered_map>
#include <mutex>

namespace vortex::runtime::resource_manager {

	namespace {
		struct resource_entry
		{
			resource_handle handle{};
			u32 ref_count{ 0 };
		};

		static id::id_type g_next_id{ 1 };
		static bool g_initialized{ false };
		static std::unordered_map<std::string, resource_entry> g_cache;
		static std::unordered_map<id::id_type, std::string> g_path_by_handle;
		static std::mutex g_mutex;

		resource_handle make_handle()
		{
			resource_handle handle{};
			handle.value = g_next_id++;
			return handle;
		}

		resource_handle get_or_create(const std::string& key)
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			auto it = g_cache.find(key);
			if (it != g_cache.end())
			{
				++it->second.ref_count;
				return it->second.handle;
			}

			resource_entry entry{};
			entry.handle = make_handle();
			entry.ref_count = 1;
			g_cache.emplace(key, entry);
			g_path_by_handle.emplace(entry.handle.value, key);
			return entry.handle;
		}
	}

	bool is_initialized()
	{
		return g_initialized;
	}

	void reset()
	{
		g_cache.clear();
		g_path_by_handle.clear();
		g_next_id = 1;
	}

	resource_handle load_resource(const std::string& key)
	{
		if (!g_initialized || key.empty()) return {};
		return get_or_create(key);
	}

	void initialize()
	{
		g_initialized = true;
	}

	void shutdown()
	{
		reset();
		g_initialized = false;
	}

	resource_handle load_mesh(const char* path) { return load_resource(path ? path : ""); }
	resource_handle load_texture(const char* path) { return load_resource(path ? path : ""); }
	resource_handle load_material(const char* path) { return load_resource(path ? path : ""); }
	resource_handle load_shader(const char* path) { return load_resource(path ? path : ""); }
	resource_handle load_audio(const char* path) { return load_resource(path ? path : ""); }

	resource_handle load_mesh_by_guid(const char* guid)
	{
		if (!guid) return {};
		const char* path = AssetDatabase::instance().get_asset_path_by_guid(guid);
		return path ? load_mesh(path) : resource_handle{};
	}

	resource_handle load_texture_by_guid(const char* guid)
	{
		if (!guid) return {};
		const char* path = AssetDatabase::instance().get_asset_path_by_guid(guid);
		return path ? load_texture(path) : resource_handle{};
	}

	resource_handle load_material_by_guid(const char* guid)
	{
		if (!guid) return {};
		const char* path = AssetDatabase::instance().get_asset_path_by_guid(guid);
		return path ? load_material(path) : resource_handle{};
	}

	const std::string& resource_path(resource_handle handle)
	{
		static std::string empty;
		if (!handle.is_valid()) return empty;
		std::lock_guard<std::mutex> lock(g_mutex);
		auto it = g_path_by_handle.find(handle.value);
		return it != g_path_by_handle.end() ? it->second : empty;
	}

	u32 get_ref_count(resource_handle handle)
	{
		if (!handle.is_valid()) return 0;
		std::lock_guard<std::mutex> lock(g_mutex);
		auto pathIt = g_path_by_handle.find(handle.value);
		if (pathIt == g_path_by_handle.end()) return 0;

		auto it = g_cache.find(pathIt->second);
		return it != g_cache.end() ? it->second.ref_count : 0;
	}

	void unload(resource_handle handle)
	{
		if (!g_initialized || !handle.is_valid()) return;
		std::lock_guard<std::mutex> lock(g_mutex);
		auto pathIt = g_path_by_handle.find(handle.value);
		if (pathIt == g_path_by_handle.end()) return;
		const std::string key = pathIt->second;

		auto it = g_cache.find(key);
		if (it == g_cache.end()) return;

		if (it->second.ref_count > 0)
		{
			--it->second.ref_count;
		}
		if (it->second.ref_count == 0)
		{
			g_path_by_handle.erase(handle.value);
			g_cache.erase(it);
		}
	}
}
