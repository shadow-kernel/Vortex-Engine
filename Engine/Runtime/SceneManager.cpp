#include "SceneManager.h"

#include <unordered_map>

namespace vortex::runtime::scene_manager {

	namespace detail {
		struct scene_data
		{
			util::vector<game_entity::entity> entities;
			// entity id -> its slot in `entities`: removing one entity is O(1) (swap with the last, pop). A scan per
			// removal made deactivating a scene quadratic — 8,000 entities took 0.26 s in a debug build.
			std::unordered_map<id::id_type, u32> slot_of;
			bool active{ false };
		};

		static util::vector<scene_data> scenes;
		static util::vector<id::generation_type> generations;
		static util::deque<scene_id> free_indices;
	}

	scene_id create_scene()
	{
		scene_id id;

		if (detail::free_indices.size() > id::min_deleted_elements)
		{
			id = detail::free_indices.front();
			detail::free_indices.pop_front();
			id = scene_id{ id::new_generation(id) };
			++detail::generations[id::index(id)];
		}
		else
		{
			id = scene_id{ (id::id_type)detail::generations.size() };
			detail::generations.push_back(0);
			detail::scenes.emplace_back();
		}

		return id;
	}

	bool is_scene_alive(scene_id id)
	{
		const auto index = id::index(id);
		if (index >= detail::generations.size()) return false;
		if (detail::generations[index] != id::generation(id)) return false;
		return true;
	}

	void destroy_scene(scene_id id)
	{
		if (!is_scene_alive(id)) return;
		const auto index = id::index(id);

		for (auto entity : detail::scenes[index].entities)
		{
			if (game_entity::is_alive(entity))
			{
				game_entity::remove_game_entity(entity);
			}
		}

		detail::scenes[index].entities.clear();
		detail::scenes[index].active = false;
		detail::scenes[index] = {};
		detail::free_indices.push_back(id);
	}

	void activate_scene(scene_id id)
	{
		if (!is_scene_alive(id)) return;
		detail::scenes[id::index(id)].active = true;
	}

	void deactivate_scene(scene_id id)
	{
		if (!is_scene_alive(id)) return;
		detail::scenes[id::index(id)].active = false;
	}

	game_entity::entity create_entity(scene_id id, const transform::init_info& info)
	{
		if (!is_scene_alive(id)) return {};

		transform::init_info non_const_info{ info };
		game_entity::entity_info entity_info{ &non_const_info };
		const auto entity = game_entity::create_game_entity(entity_info);
		if (!entity.is_valid()) return {};

		auto& scene = detail::scenes[id::index(id)];
		scene.slot_of[entity.get_id()] = (u32)scene.entities.size();
		scene.entities.push_back(entity);
		return entity;
	}

	void remove_entity(scene_id id, game_entity::entity entity)
	{
		if (game_entity::is_alive(entity))
		{
			game_entity::remove_game_entity(entity);
		}

		if (!is_scene_alive(id)) return;

		auto& scene = detail::scenes[id::index(id)];
		const auto it = scene.slot_of.find(entity.get_id());
		if (it == scene.slot_of.end()) return;
		const u32 slot = it->second;
		scene.slot_of.erase(it);
		const u32 last = (u32)scene.entities.size() - 1;
		if (slot != last)
		{
			scene.entities[slot] = scene.entities[last];
			scene.slot_of[scene.entities[slot].get_id()] = slot;
		}
		scene.entities.pop_back();
	}
}
