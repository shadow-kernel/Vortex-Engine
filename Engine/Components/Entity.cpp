#include "Entity.h"
#include "Transform.h"

namespace vortex::game_entity {

	static util::vector<transform::component> transforms;
	static util::vector<id::generation_type> generations;
	static util::deque<entity_id> free_indices;

	entity create_game_entity(const entity_info& entity_info)
	{
		assert(entity_info.transform);
		if (!entity_info.transform) return entity{};

		entity_id id;

		if (free_indices.size() > id::min_deleted_elements)
		{
			id = free_indices.front();
			assert(!is_alive(entity{ id }));
			free_indices.pop_front();
			id = entity_id{ id::new_generation(id) };
			++generations[id::index(id)];
		}
		else
		{
			id = entity_id{ (id::id_type) generations.size() };
			generations.push_back(0);

			// Resize componets
			transforms.emplace_back();
		}

		const entity new_entity{ id };
		const id::id_type index{ id::index(id) };

		// create transform component
		assert(!transforms[index].is_valid());
		transforms[index] = transform::create_transform(*entity_info.transform, new_entity);
		if (!transforms[index].is_valid()) return {};


		return new_entity;
	}

	void remove_game_entity(entity entity)
	{
		const entity_id id{ entity.get_id() };
		const id::id_type index{ id::index(id) };
		assert(is_alive(entity));
		if (is_alive(entity)) {
			transform::remove_transform(transforms[index]);
			transforms[index] = {};
			free_indices.push_back(id);
		}
	}

	bool is_alive(entity entity)
	{
		assert(entity.is_valid());
		const entity_id id{ entity.get_id() };
		const id::id_type index{ id::index(id) };
		assert(index < generations.size());
		assert(generations[index] == id::generation(id));
		return (generations[index] == id::generation(id) && transforms[index].is_valid());
	}

	transform::component entity::transform() const
	{
		assert(is_alive(*this));
		const id::id_type index{ id::index(_id) };
		return transforms[index];
	}

}