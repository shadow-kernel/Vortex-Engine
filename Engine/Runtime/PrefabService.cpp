#include "PrefabService.h"

#include <unordered_map>

namespace vortex::runtime::prefab_service {

	namespace {
		struct prefab_entry
		{
			prefab_handle handle{};
			std::string path;
			u32 ref_count{ 0 };
		};

		static id::id_type g_next_id{ 1 };
		static bool g_initialized{ false };
		static std::unordered_map<std::string, prefab_entry> g_prefabs_by_key;
		static std::unordered_map<id::id_type, std::string> g_prefab_path_by_handle;

		prefab_handle allocate_handle()
		{
			prefab_handle handle{};
			handle.value = g_next_id++;
			return handle;
		}
	}

	bool is_initialized()
	{
		return g_initialized;
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

	void reset()
	{
		g_prefabs_by_key.clear();
		g_prefab_path_by_handle.clear();
		g_next_id = 1;
	}

	prefab_handle load_prefab_key(const std::string& key)
	{
		if (!g_initialized || key.empty()) return {};

		auto it = g_prefabs_by_key.find(key);
		if (it != g_prefabs_by_key.end())
		{
			++it->second.ref_count;
			return it->second.handle;
		}

		prefab_entry entry{};
		entry.handle = allocate_handle();
		entry.path = key;
		entry.ref_count = 1;

		g_prefabs_by_key.emplace(key, entry);
		g_prefab_path_by_handle.emplace(entry.handle.value, key);
		return entry.handle;
	}

	prefab_handle load_prefab(const char* path)
	{
		return load_prefab_key(path ? path : "");
	}

	const std::string& prefab_path(prefab_handle handle)
	{
		static std::string empty;
		if (!handle.is_valid()) return empty;
		auto it = g_prefab_path_by_handle.find(handle.value);
		return it != g_prefab_path_by_handle.end() ? it->second : empty;
	}

	game_entity::entity instantiate(prefab_handle handle, const transform::init_info& info)
	{
		if (!g_initialized || !handle.is_valid()) return {};
		transform::init_info non_const{ info };
		game_entity::entity_info entity_info{ &non_const };
		return game_entity::create_game_entity(entity_info);
	}

	void unload(prefab_handle handle)
	{
		if (!g_initialized || !handle.is_valid()) return;

		auto itHandle = g_prefab_path_by_handle.find(handle.value);
		if (itHandle == g_prefab_path_by_handle.end()) return;
		const std::string key = itHandle->second;

		auto it = g_prefabs_by_key.find(key);
		if (it == g_prefabs_by_key.end()) return;

		if (it->second.ref_count > 0)
		{
			--it->second.ref_count;
		}
		if (it->second.ref_count == 0)
		{
			g_prefab_path_by_handle.erase(handle.value);
			g_prefabs_by_key.erase(it);
		}
	}
}
