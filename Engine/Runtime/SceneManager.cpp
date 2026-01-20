#include "SceneManager.h"

#include <algorithm>

namespace vortex::runtime::scene_manager {

	namespace detail {
		struct scene_data
		{
			util::vector<game_entity::entity> entities;
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

		detail::scenes[id::index(id)].entities.push_back(entity);
		return entity;
	}

	void remove_entity(scene_id id, game_entity::entity entity)
	{
		if (game_entity::is_alive(entity))
		{
			game_entity::remove_game_entity(entity);
		}

		if (!is_scene_alive(id)) return;

		auto& entities = detail::scenes[id::index(id)].entities;
		entities.erase(std::remove_if(entities.begin(), entities.end(), [entity](const game_entity::entity& e)
		{
			return e.get_id() == entity.get_id();
		}), entities.end());
	}
}
