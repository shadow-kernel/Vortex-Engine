#include "PhysicsSystem.h"
#include "../../Components/Entity.h"
#include "../../Components/Transform.h"
#include "../../Utilities/MathTypes.h"
#include <unordered_map>

namespace vortex::runtime::systems {

	namespace {
		struct body
		{
			math::v3 velocity{ 0.0f, 0.0f, 0.0f };
			bool use_gravity{ true };
		};

		// Keyed by full entity id (index + generation), validated with is_alive each tick.
		std::unordered_map<id::id_type, body> g_bodies;

		bool g_initialized{ false };
		float g_sim_time{ 0.0f };

		constexpr float k_gravity = -9.81f;   // m/s^2
		constexpr float k_ground_y = 0.0f;    // simple flat floor at y=0 so things don't fall forever
	}

	bool physics_initialized()
	{
		return g_initialized;
	}

	void initialize_physics()
	{
		g_initialized = true;
		g_sim_time = 0.0f;
		g_bodies.clear();
	}

	void shutdown_physics()
	{
		g_initialized = false;
		g_bodies.clear();
	}

	void set_rigidbody(id::id_type entity_id, bool use_gravity)
	{
		// Upsert; preserve any existing velocity if re-registered mid-play.
		g_bodies[entity_id].use_gravity = use_gravity;
	}

	void clear_rigidbodies()
	{
		g_bodies.clear();
	}

	// Advances the physics simulation by dt seconds. Called once per game tick from StepRuntime
	// (play mode / standalone player), never from the editor's idle viewport render.
	void update_physics(float dt)
	{
		if (!g_initialized) return;
		g_sim_time += dt;
		if (dt <= 0.0f) return;

		for (auto it = g_bodies.begin(); it != g_bodies.end(); )
		{
			const game_entity::entity entity{ game_entity::entity_id{ it->first } };
			if (!game_entity::is_alive(entity)) { it = g_bodies.erase(it); continue; }

			body& b = it->second;
			if (b.use_gravity)
			{
				const transform::component comp{ entity.transform() };
				math::v3 pos{ comp.position() };
				const math::v4 rot{ comp.rotation() };
				const math::v3 scl{ comp.scale() };

				// Semi-implicit Euler gravity integration.
				b.velocity.y += k_gravity * dt;
				pos.y += b.velocity.y * dt;
				if (pos.y <= k_ground_y) { pos.y = k_ground_y; b.velocity.y = 0.0f; }

				transform::init_info info{};
				info.position[0] = pos.x; info.position[1] = pos.y; info.position[2] = pos.z;
				info.rotation[0] = rot.x; info.rotation[1] = rot.y; info.rotation[2] = rot.z; info.rotation[3] = rot.w;
				info.scale[0] = scl.x; info.scale[1] = scl.y; info.scale[2] = scl.z;
				transform::set_transform(entity, info);
			}
			++it;
		}
	}
}
