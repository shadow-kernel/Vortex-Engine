#include "Transform.h"
#include "Entity.h"
#include "..\Utilities\MathTypes.h"

namespace vortex::transform {
	
	static util::vector<math::v3> positions;
	static util::vector<math::v4> rotations;
	static util::vector<math::v3> scales;


	component vortex::transform::create_transform(const init_info& transform_init_info, game_entity::entity entity)
	{
		assert(entity.is_valid());
		const id::id_type entity_index{ id::index(entity.get_id()) };

		if (positions.size() > entity_index)
		{
			rotations[entity_index] = math::v4(transform_init_info.rotation);
			positions[entity_index] = math::v3(transform_init_info.position);
			scales[entity_index] = math::v3(transform_init_info.scale);
		}
		else
		{
			assert(positions.size() == entity_index);
			positions.emplace_back(transform_init_info.position);
			rotations.emplace_back(transform_init_info.rotation);
			scales.emplace_back(transform_init_info.scale);
		}

		return component{ transform_id{ (id::id_type) positions.size() - 1 }};
	}

	void vortex::transform::remove_transform(component component)
	{
		assert(component.is_valid());
	}

	math::v4 component::rotation() const
	{
		assert(is_valid());
		return rotations[id::index(_id)];
	}

	math::v3 component::position() const
	{
		assert(is_valid());
		return positions[id::index(_id)];
	}

	math::v3 component::scale() const
	{
		assert(is_valid());
		return scales[id::index(_id)];
	}

}