#include "PhysicsSystem.h"
#include "../../Components/Entity.h"
#include "../../Components/Transform.h"
#include "../../Utilities/MathTypes.h"
#include <unordered_map>
#include <vector>

namespace vortex::runtime::systems {

	namespace {
		struct aabb { math::v3 mn; math::v3 mx; };

		struct body
		{
			math::v3 velocity{ 0.0f, 0.0f, 0.0f };
			math::v3 half{ 0.5f, 0.5f, 0.5f };
			bool use_gravity{ true };
		};

		// Dynamic bodies (falling rigidbodies) keyed by full entity id.
		std::unordered_map<id::id_type, body> g_bodies;
		// Static world colliders (level geometry) the dynamic bodies + the character collide with.
		std::vector<aabb> g_statics;

		// Single player character (the play-mode camera body).
		bool g_char_active{ false };
		math::v3 g_char_pos{ 0.0f, 0.0f, 0.0f };
		math::v3 g_char_vel{ 0.0f, 0.0f, 0.0f };
		math::v3 g_char_half{ 0.4f, 0.9f, 0.4f };
		bool g_char_grounded{ false };

		bool g_initialized{ false };
		float g_sim_time{ 0.0f };

		constexpr float k_gravity = -20.0f;     // a touch snappier than -9.81 for game feel
		constexpr float k_ground_y = 0.0f;      // flat base floor
		constexpr float k_jump_speed = 7.5f;

		bool overlaps(const math::v3& pos, const math::v3& half, const aabb& s)
		{
			return (pos.x - half.x) < s.mx.x && (pos.x + half.x) > s.mn.x &&
			       (pos.y - half.y) < s.mx.y && (pos.y + half.y) > s.mn.y &&
			       (pos.z - half.z) < s.mx.z && (pos.z + half.z) > s.mn.z;
		}

		// Integrate position by velocity*dt one axis at a time, resolving against every static box
		// (and the y=0 floor). Sets `grounded` when the box is resting on something. Shared by the
		// dynamic rigidbodies and the player character so collision is identical for both.
		void move_and_collide(math::v3& pos, math::v3& vel, const math::v3& half, float dt, bool& grounded)
		{
			grounded = false;

			// --- Y ---
			pos.y += vel.y * dt;
			for (const auto& s : g_statics)
			{
				if (overlaps(pos, half, s))
				{
					if (vel.y <= 0.0f) { pos.y = s.mx.y + half.y; grounded = true; }
					else { pos.y = s.mn.y - half.y; }
					vel.y = 0.0f;
				}
			}
			if (pos.y - half.y < k_ground_y) { pos.y = k_ground_y + half.y; if (vel.y < 0.0f) vel.y = 0.0f; grounded = true; }

			// --- X ---
			pos.x += vel.x * dt;
			for (const auto& s : g_statics)
			{
				if (overlaps(pos, half, s))
				{
					if (vel.x > 0.0f) pos.x = s.mn.x - half.x;
					else if (vel.x < 0.0f) pos.x = s.mx.x + half.x;
					vel.x = 0.0f;
				}
			}

			// --- Z ---
			pos.z += vel.z * dt;
			for (const auto& s : g_statics)
			{
				if (overlaps(pos, half, s))
				{
					if (vel.z > 0.0f) pos.z = s.mn.z - half.z;
					else if (vel.z < 0.0f) pos.z = s.mx.z + half.z;
					vel.z = 0.0f;
				}
			}
		}
	}

	bool physics_initialized() { return g_initialized; }

	void initialize_physics()
	{
		g_initialized = true;
		g_sim_time = 0.0f;
		g_bodies.clear();
		g_statics.clear();
		g_char_active = false;
	}

	void shutdown_physics()
	{
		g_initialized = false;
		g_bodies.clear();
		g_statics.clear();
		g_char_active = false;
	}

	void set_rigidbody(id::id_type entity_id, bool use_gravity, float hx, float hy, float hz)
	{
		body& b = g_bodies[entity_id];
		b.use_gravity = use_gravity;
		b.half = math::v3{ hx > 0.001f ? hx : 0.5f, hy > 0.001f ? hy : 0.5f, hz > 0.001f ? hz : 0.5f };
	}

	void clear_rigidbodies() { g_bodies.clear(); }

	void register_static_box(float cx, float cy, float cz, float hx, float hy, float hz)
	{
		g_statics.push_back(aabb{ math::v3{ cx - hx, cy - hy, cz - hz }, math::v3{ cx + hx, cy + hy, cz + hz } });
	}

	void clear_colliders() { g_statics.clear(); }

	void character_init(float x, float y, float z, float hx, float hy, float hz)
	{
		g_char_active = true;
		g_char_pos = math::v3{ x, y, z };
		g_char_vel = math::v3{ 0.0f, 0.0f, 0.0f };
		g_char_half = math::v3{ hx, hy, hz };
		g_char_grounded = false;
	}

	void character_move(float wish_x, float wish_z, bool jump, float dt)
	{
		if (!g_char_active) return;
		g_char_vel.x = wish_x;            // direct horizontal control (no inertia, snappy)
		g_char_vel.z = wish_z;
		g_char_vel.y += k_gravity * dt;   // gravity
		if (jump && g_char_grounded) g_char_vel.y = k_jump_speed;
		move_and_collide(g_char_pos, g_char_vel, g_char_half, dt, g_char_grounded);
	}

	void character_get_position(float* out_xyz)
	{
		if (!out_xyz) return;
		out_xyz[0] = g_char_pos.x; out_xyz[1] = g_char_pos.y; out_xyz[2] = g_char_pos.z;
	}

	bool character_grounded() { return g_char_grounded; }

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
			const transform::component comp{ entity.transform() };
			math::v3 pos{ comp.position() };
			const math::v4 rot{ comp.rotation() };
			const math::v3 scl{ comp.scale() };

			if (b.use_gravity) b.velocity.y += k_gravity * dt;

			bool grounded = false;
			move_and_collide(pos, b.velocity, b.half, dt, grounded);

			transform::init_info info{};
			info.position[0] = pos.x; info.position[1] = pos.y; info.position[2] = pos.z;
			info.rotation[0] = rot.x; info.rotation[1] = rot.y; info.rotation[2] = rot.z; info.rotation[3] = rot.w;
			info.scale[0] = scl.x; info.scale[1] = scl.y; info.scale[2] = scl.z;
			transform::set_transform(entity, info);
			++it;
		}
	}
}
