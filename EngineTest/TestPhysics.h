#pragma once

#include "../Engine/Physics/PhysicsWorld.h"

#include "Test.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <iostream>
#include <vector>

#if VORTEX_HAS_JOLT
// The contract's quaternion-convention check is also run directly against JPH::Quat (test d).
#include <Jolt/Jolt.h>
JPH_SUPPRESS_WARNINGS
#endif

using namespace vortex;

// Physics v2 smoke test (issues #100/#101/#102/#103/#105/#106/#107): drives Engine/Physics/PhysicsWorld directly
// (the same module libVortexAPI exports through PhysicsApi.cpp). Non-interactive; prints "ALL PASSED" or
// "FAILURES!" for ctest. Without Jolt in the build the stub contract is verified instead.
class engine_test : public test
{
public:
	bool initialize() override
	{
		std::cout << "Physics Test Initialized\n";
		_available = physics::init();
		std::cout << (_available
			? "[info] Jolt world ready\n"
			: "[info] Jolt not compiled in (VORTEX_HAS_JOLT=0) - verifying the stub contract only\n");
		return true;
	}

	void run() override
	{
		if (!_available)
		{
			run_stub_checks();
		}
		else
		{
			check("init() is idempotent", physics::init());
			check("available() after init", physics::available());
			run_rigid_body_tests();   // (a) settle, (b) raycast, (c) contact events
			run_quaternion_tests();   // (d) convention
			run_character_tests();    // (e) CharacterVirtual
			run_kinematic_and_query_tests();
			run_constraint_tests();   // (f)..(l) joints, issue #103
			run_teardown_tests();
		}

		std::cout << "\nRESULT: " << _passed << "/" << (_passed + _failed)
			<< (_failed == 0 ? " - ALL PASSED\n" : " - FAILURES!\n");
	}

	void shutdown() override
	{
		physics::shutdown();
		check_silent(!physics::available());
		std::cout << "Physics Test Shutdown\n";
	}

private:
	static constexpr f32 k_dt = 1.0f / 60.0f;
	static constexpr u64 k_ground_entity = 1001;
	static constexpr u64 k_box_entity = 2002;
	static constexpr u64 k_probe_entity = 3003;

	static constexpr f32 k_identity[4] = { 0.0f, 0.0f, 0.0f, 1.0f };

	// Runs one fixed step and collects the contact events it produced.
	void step_and_drain(std::vector<physics::contact_event>& out)
	{
		physics::step(k_dt, 1);
		physics::contact_event buf[256];
		s32 n;
		do
		{
			n = physics::get_contacts(buf, 256);
			out.insert(out.end(), buf, buf + n);
		} while (n == 256);
	}

	void run_rigid_body_tests()
	{
		// (a) static ground at y=-0.5 (half extents 10,0.5,10) + dynamic box (half 0.25) at y=3.
		const f32 ground_dims[3] = { 10.0f, 0.5f, 10.0f };
		const f32 ground_pos[3] = { 0.0f, -0.5f, 0.0f };
		_ground = physics::create_body(k_ground_entity, physics::shape_box, ground_dims, ground_pos, k_identity,
			physics::motion_static, 0.0f, 0.6f, 0.0f, 0.0f, 0.0f, false, physics::layer_static, 0, 1.0f);
		check("static ground body created", _ground != 0 && physics::body_valid(_ground));

		const f32 box_dims[3] = { 0.25f, 0.25f, 0.25f };
		const f32 box_pos[3] = { 0.0f, 3.0f, 0.0f };
		_box = physics::create_body(k_box_entity, physics::shape_box, box_dims, box_pos, k_identity,
			physics::motion_dynamic, 0.0f, 0.6f, 0.0f, 0.05f, 0.05f, false, physics::layer_dynamic, 0, 1.0f);
		check("dynamic box body created", _box != 0 && physics::body_valid(_box));
		check("body_count() == 2", physics::body_count() == 2);
		check("body_entity() round trip", physics::body_entity(_box) == k_box_entity && physics::body_entity(_ground) == k_ground_entity);
		check("dynamic body starts active", physics::is_active(_box));
		check("static body reports mass 0", physics::get_mass(_ground) == 0.0f);
		const f32 density_mass = physics::get_mass(_box);
		std::printf("[info] density-based mass of the 0.5 m box: %.3f kg\n", density_mass);
		check("density-based mass > 0 for mass <= 0", density_mass > 0.0f);

		std::vector<physics::contact_event> events;
		for (int i = 0; i < 180; ++i) step_and_drain(events);

		f32 pos[3]{}, quat[4]{}, vel[3]{};
		check("get_body_transform()", physics::get_body_transform(_box, pos, quat));
		physics::get_linear_velocity(_box, vel);
		std::printf("[info] box after 180 steps: pos (%.4f, %.4f, %.4f) vel (%.4f, %.4f, %.4f) active=%d\n",
			pos[0], pos[1], pos[2], vel[0], vel[1], vel[2], physics::is_active(_box) ? 1 : 0);
		check("box came to rest on the ground (|y - 0.25| < 0.02)", std::fabs(pos[1] - 0.25f) < 0.02f);
		check("box did not drift sideways", std::fabs(pos[0]) < 0.01f && std::fabs(pos[2]) < 0.01f);
		const bool nearly_still = std::fabs(vel[0]) < 0.01f && std::fabs(vel[1]) < 0.01f && std::fabs(vel[2]) < 0.01f;
		check("box is sleeping or nearly still", !physics::is_active(_box) || nearly_still);

		// (c) at least one 'added' contact event for the pair (either order).
		bool saw_added = false, saw_persisted = false, normal_ok = false, trigger_flag_ok = true;
		for (const auto& e : events)
		{
			const bool pair = (e.entity_a == k_ground_entity && e.entity_b == k_box_entity) ||
			                  (e.entity_a == k_box_entity && e.entity_b == k_ground_entity);
			if (!pair) continue;
			if (e.kind == physics::contact_added) saw_added = true;
			if (e.kind == physics::contact_persisted) saw_persisted = true;
			if (e.is_trigger != 0) trigger_flag_ok = false;
			// normal points from A to B: ground -> box is +Y, box -> ground is -Y
			const f32 expected_y = (e.entity_a == k_ground_entity) ? 1.0f : -1.0f;
			if (std::fabs(e.normal[1] - expected_y) < 0.01f) normal_ok = true;
			if (e.body_a != 0 && e.body_b != 0 && e.kind == physics::contact_added)
				check_silent(physics::body_entity(e.body_a) == e.entity_a && physics::body_entity(e.body_b) == e.entity_b);
		}
		std::printf("[info] %zu contact events drained during the settle\n", events.size());
		check("contact 'added' event reported for the ground/box pair", saw_added);
		check("contact 'persisted' events reported while resting", saw_persisted);
		check("contact normal points from A to B", normal_ok);
		check("solid contacts are not flagged as trigger", trigger_flag_ok);

		// (b) raycast from (0,5,0) straight down hits the box first (top face at y = 0.5).
		const f32 origin[3] = { 0.0f, 5.0f, 0.0f };
		const f32 down[3] = { 0.0f, -1.0f, 0.0f };
		f32 hit_point[3]{}, hit_normal[3]{}, hit_dist = 0.0f;
		u64 hit_entity = 0; u32 hit_body = 0;
		const bool hit = physics::raycast(origin, down, 100.0f, physics::layer_mask_all,
			hit_point, hit_normal, &hit_dist, &hit_entity, &hit_body);
		std::printf("[info] raycast: hit=%d entity=%llu body=%u dist=%.4f point=(%.3f, %.3f, %.3f) normal=(%.2f, %.2f, %.2f)\n",
			hit ? 1 : 0, (unsigned long long)hit_entity, hit_body, hit_dist,
			hit_point[0], hit_point[1], hit_point[2], hit_normal[0], hit_normal[1], hit_normal[2]);
		check("raycast hits something", hit);
		check("raycast hits the dynamic box first (entity id)", hit_entity == k_box_entity && hit_body == _box);
		check("raycast distance ~ 4.5", std::fabs(hit_dist - 4.5f) < 0.05f);
		check("raycast point on the box top", std::fabs(hit_point[1] - 0.5f) < 0.05f);
		check("raycast normal is +Y", hit_normal[1] > 0.99f);

		// Layer mask: excluding DYNAMIC must make the same ray hit the ground instead.
		hit_entity = 0; hit_body = 0; hit_dist = 0.0f;
		const bool hit_ground = physics::raycast(origin, down, 100.0f, 1u << physics::layer_static,
			hit_point, hit_normal, &hit_dist, &hit_entity, &hit_body);
		check("raycast layer mask skips the box and hits the ground", hit_ground && hit_entity == k_ground_entity && std::fabs(hit_dist - 5.0f) < 0.05f);

		// A ray that starts below everything and points down must miss.
		const f32 below[3] = { 0.0f, -5.0f, 0.0f };
		check("raycast miss returns false", !physics::raycast(below, down, 100.0f, physics::layer_mask_all,
			hit_point, hit_normal, &hit_dist, &hit_entity, &hit_body));
	}

	void run_quaternion_tests()
	{
		// (d) yaw +90 deg from the standard formula: q = (0, sin(45), 0, cos(45)). Rotating (1,0,0) must give (0,0,-1).
		const f32 half = 0.25f * 3.14159265358979f;
		const f32 yaw90[4] = { 0.0f, std::sin(half), 0.0f, std::cos(half) };

#if VORTEX_HAS_JOLT
		{
			const JPH::Quat q(yaw90[0], yaw90[1], yaw90[2], yaw90[3]);
			const JPH::Vec3 r = q * JPH::Vec3(1.0f, 0.0f, 0.0f);
			std::printf("[info] JPH::Quat yaw90 * (1,0,0) = (%.4f, %.4f, %.4f)\n", r.GetX(), r.GetY(), r.GetZ());
			check("JPH::Quat yaw 90 rotates (1,0,0) to (0,0,-1)",
				std::fabs(r.GetX()) < 1e-4f && std::fabs(r.GetY()) < 1e-4f && std::fabs(r.GetZ() + 1.0f) < 1e-4f);
		}
#endif

		// The same convention through the module: a compound body with ONE child offset to local +X (1.5, 0, 0),
		// rotated by yaw 90, must place that child at world (0, 0, -1.5). A ray down at that spot hits it; a ray
		// down at (0, 0, +1.5) hits the ground instead.
		const f32 child[physics::compound_child_floats] = {
			(f32)physics::shape_box, 0.1f, 0.1f, 0.1f,   // box, half extents
			1.5f, 0.0f, 0.0f,                            // local position
			0.0f, 0.0f, 0.0f, 1.0f };                    // local rotation (identity)
		const f32 origin[3] = { 0.0f, 0.5f, 0.0f };
		const u32 probe = physics::create_compound_body(k_probe_entity, child, 1, origin, yaw90,
			physics::motion_static, 0.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_static, 0, 1.0f);
		check("compound probe body created", probe != 0);

		f32 pos[3]{}, quat[4]{};
		physics::get_body_transform(probe, pos, quat);
		const f32 dot = quat[0] * yaw90[0] + quat[1] * yaw90[1] + quat[2] * yaw90[2] + quat[3] * yaw90[3];
		check("quaternion round trip through get_body_transform", std::fabs(std::fabs(dot) - 1.0f) < 1e-4f);
		check("position round trip through get_body_transform", std::fabs(pos[0]) < 1e-4f && std::fabs(pos[1] - 0.5f) < 1e-4f && std::fabs(pos[2]) < 1e-4f);

		const f32 down[3] = { 0.0f, -1.0f, 0.0f };
		f32 p[3]{}, n[3]{}, d = 0.0f; u64 entity = 0; u32 body = 0;
		const f32 from_minus_z[3] = { 0.0f, 5.0f, -1.5f };
		const bool hit_minus = physics::raycast(from_minus_z, down, 100.0f, physics::layer_mask_all, p, n, &d, &entity, &body);
		std::printf("[info] ray at z=-1.5 hit entity %llu at dist %.3f\n", (unsigned long long)entity, d);
		check("yaw 90 moved the +X child to -Z (engine/Jolt convention agree)", hit_minus && entity == k_probe_entity && std::fabs(d - 4.4f) < 0.05f);

		const f32 from_plus_z[3] = { 0.0f, 5.0f, 1.5f };
		entity = 0;
		const bool hit_plus = physics::raycast(from_plus_z, down, 100.0f, physics::layer_mask_all, p, n, &d, &entity, &body);
		check("nothing of the probe at +Z (ray reaches the ground)", hit_plus && entity == k_ground_entity);

		physics::destroy_body(probe);
		check("probe destroyed", !physics::body_valid(probe));
	}

	void run_character_tests()
	{
		// (e) CharacterVirtual at (3,1,3) moved with velocity (1,-9.81,0) for 60 steps stays on the ground and reports grounded.
		const f32 start[3] = { 3.0f, 1.0f, 3.0f };
		const u32 character = physics::character_create(0.35f, 1.8f, start, 45.0f, 0.3f, 70.0f);
		check("character created", character != 0);

		const f32 desired[3] = { 1.0f, -9.81f, 0.0f };
		f32 pos[3]{}, vel[3]{}, ground_normal[3]{}; s32 grounded = 0;
		int grounded_steps = 0;
		for (int i = 0; i < 60; ++i)
		{
			physics::step(k_dt, 1);
			physics::character_move(character, desired, k_dt, pos, vel, &grounded, ground_normal);
			if (grounded) ++grounded_steps;
		}
		std::printf("[info] character after 60 steps: pos (%.4f, %.4f, %.4f) vel (%.3f, %.3f, %.3f) grounded=%d (%d/60 steps) normal (%.2f, %.2f, %.2f)\n",
			pos[0], pos[1], pos[2], vel[0], vel[1], vel[2], grounded, grounded_steps, ground_normal[0], ground_normal[1], ground_normal[2]);
		check("character stays on the ground (|y| < 0.05)", std::fabs(pos[1]) < 0.05f);
		check("character reports grounded", grounded != 0);
		check("character ground normal is +Y", ground_normal[1] > 0.99f);
		check("character walked along +X", pos[0] > 3.5f && pos[0] < 4.2f);
		check("character kept its Z", std::fabs(pos[2] - 3.0f) < 0.01f);

		// Teleport + walking into a wall: the controller must slide / stop, not pass through.
		const f32 wall_dims[3] = { 0.25f, 2.0f, 3.0f };
		const f32 wall_pos[3] = { 8.0f, 2.0f, 3.0f };
		const u32 wall = physics::create_body(4004, physics::shape_box, wall_dims, wall_pos, k_identity,
			physics::motion_static, 0.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_static, 0, 1.0f);
		const f32 near_wall[3] = { 7.0f, 0.0f, 3.0f };
		physics::character_set_position(character, near_wall);
		const f32 rush[3] = { 6.0f, -9.81f, 0.0f };
		for (int i = 0; i < 60; ++i)
		{
			physics::step(k_dt, 1);
			physics::character_move(character, rush, k_dt, pos, vel, &grounded, ground_normal);
		}
		std::printf("[info] character vs wall: x = %.3f (wall face at 7.75)\n", pos[0]);
		check("character is stopped by a static wall", pos[0] < 7.75f - 0.35f + 0.05f && pos[0] > 7.0f);
		physics::destroy_body(wall);

		physics::character_destroy(character);
		f32 after[3] = { -1.0f, -1.0f, -1.0f };
		physics::character_move(character, desired, k_dt, after, vel, &grounded, ground_normal);
		check("moving a destroyed character is a no-op", after[0] == -1.0f);
	}

	void run_kinematic_and_query_tests()
	{
		// Kinematic drive: the body must follow move_kinematic targets after a step.
		const f32 dims[3] = { 0.5f, 0.5f, 0.5f };
		const f32 at[3] = { -5.0f, 2.0f, -5.0f };
		const u32 kin = physics::create_body(5005, physics::shape_box, dims, at, k_identity,
			physics::motion_kinematic, 0.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_dynamic, 0, 1.0f);
		check("kinematic body created", kin != 0);
		const f32 target[3] = { -5.0f, 2.0f, -4.0f };
		physics::move_kinematic(kin, target, k_identity, k_dt);
		physics::step(k_dt, 1);
		f32 pos[3]{}, quat[4]{};
		physics::get_body_transform(kin, pos, quat);
		check("move_kinematic reaches its target after one step", std::fabs(pos[2] + 4.0f) < 1e-3f);
		f32 v[3]{};
		physics::get_linear_velocity(kin, v);
		check("move_kinematic drives with velocity (v.z ~ 60 m/s)", std::fabs(v[2] - 60.0f) < 1.0f);
		physics::destroy_body(kin);

		// Sphere/capsule/cylinder shapes + trigger + overlap query.
		const f32 sphere_dims[3] = { 0.3f, 0.0f, 0.0f };
		const f32 sphere_pos[3] = { 4.0f, 0.3f, -4.0f };
		const u32 sphere = physics::create_body(6006, physics::shape_sphere, sphere_dims, sphere_pos, k_identity,
			physics::motion_dynamic, 2.0f, 0.5f, 0.2f, 0.0f, 0.0f, false, physics::layer_dynamic, 0, 1.0f);
		check("sphere body created", sphere != 0);
		check("explicit mass override (2 kg)", std::fabs(physics::get_mass(sphere) - 2.0f) < 1e-3f);
		const f32 capsule_dims[3] = { 0.2f, 0.4f, 0.0f };
		const f32 capsule_pos[3] = { 5.0f, 0.6f, -4.0f };
		const u32 capsule = physics::create_body(6007, physics::shape_capsule, capsule_dims, capsule_pos, k_identity,
			physics::motion_dynamic, 0.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_debris, 0, 1.0f);
		check("capsule body created", capsule != 0);
		const f32 cylinder_dims[3] = { 0.2f, 0.3f, 0.0f };
		const f32 cylinder_pos[3] = { 6.0f, 0.3f, -4.0f };
		const u32 cylinder = physics::create_body(6008, physics::shape_cylinder, cylinder_dims, cylinder_pos, k_identity,
			physics::motion_dynamic, 0.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_dynamic, physics::lock_rotation_x | physics::lock_rotation_z, 1.0f);
		check("cylinder body created", cylinder != 0);

		const f32 trigger_dims[3] = { 1.5f, 1.0f, 1.5f };
		const f32 trigger_pos[3] = { 5.0f, 0.5f, -4.0f };
		const u32 trigger = physics::create_body(7007, physics::shape_box, trigger_dims, trigger_pos, k_identity,
			physics::motion_static, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, true, physics::layer_trigger, 0, 1.0f);
		check("trigger body created", trigger != 0);

		std::vector<physics::contact_event> events;
		for (int i = 0; i < 30; ++i) step_and_drain(events);
		bool trigger_added = false;
		for (const auto& e : events)
			if (e.is_trigger && e.kind == physics::contact_added && (e.entity_a == 7007 || e.entity_b == 7007)) trigger_added = true;
		check("trigger (sensor) reports 'added' contact events with is_trigger = 1", trigger_added);

		const f32 center[3] = { 5.0f, 0.5f, -4.0f };
		u64 entities[8]{}; u32 bodies[8]{};
		const s32 found = physics::overlap_sphere(center, 1.5f, physics::layer_mask_all, entities, bodies, 8);
		bool has_sphere = false, has_capsule = false, has_cylinder = false, has_trigger = false, has_box = false;
		for (s32 i = 0; i < found; ++i)
		{
			if (entities[i] == 6006) has_sphere = true;
			if (entities[i] == 6007) has_capsule = true;
			if (entities[i] == 6008) has_cylinder = true;
			if (entities[i] == 7007) has_trigger = true;
			if (entities[i] == k_box_entity) has_box = true;
		}
		std::printf("[info] overlap_sphere found %d bodies\n", found);
		check("overlap_sphere finds the three props and the trigger", has_sphere && has_capsule && has_cylinder && has_trigger);
		check("overlap_sphere does not report far-away bodies", !has_box);
		const s32 found_dynamic = physics::overlap_sphere(center, 1.5f, 1u << physics::layer_dynamic, entities, bodies, 8);
		check("overlap_sphere honours the layer mask", found_dynamic == 2);

		// Force / impulse / velocity setters wake the body and move it.
		physics::set_active(sphere, false);
		check("set_active(false) puts the body to sleep", !physics::is_active(sphere));
		const f32 impulse[3] = { 0.0f, 0.0f, 4.0f };
		physics::add_impulse(sphere, impulse);
		check("add_impulse wakes the body", physics::is_active(sphere));
		physics::get_linear_velocity(sphere, v);
		check("impulse / mass = velocity (4 N.s / 2 kg = 2 m/s)", std::fabs(v[2] - 2.0f) < 0.05f);

		// Debug lines: something is drawn, in whole segments, and never more than the buffer.
		std::vector<f32> lines(60000);
		const s32 written = physics::get_debug_lines(lines.data(), (s32)lines.size());
		std::printf("[info] get_debug_lines wrote %d floats (%d segments)\n", written, written / 6);
		check("get_debug_lines writes whole segments", written > 0 && written % 6 == 0 && written <= (s32)lines.size());
		const s32 written_small = physics::get_debug_lines(lines.data(), 20);
		check("get_debug_lines respects a tiny buffer", written_small >= 0 && written_small <= 18 && written_small % 6 == 0);

		physics::destroy_body(trigger);
		physics::destroy_body(sphere);
		physics::destroy_body(capsule);
		physics::destroy_body(cylinder);
		check("destroyed bodies are invalid", !physics::body_valid(sphere) && !physics::body_valid(trigger));
		check("stale handle is rejected after destroy (entity 0)", physics::body_entity(sphere) == 0);
	}

	// ---- joints (issue #103) --------------------------------------------------------------------------------
	// Every joint scene lives far away from the rigid-body scene (x >= 20, where there is no ground) and destroys
	// its bodies again, so the scenes cannot disturb each other.

	struct vec { f32 x, y, z; };

	static vec sub(vec a, vec b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
	static f32 length(vec v) { return std::sqrt(v.x * v.x + v.y * v.y + v.z * v.z); }

	// v' = q v q* (Hamilton, q = {x, y, z, w}) — plain floats, so the file also compiles without Jolt.
	static vec rotate(const f32* q, vec v)
	{
		const f32 tx = 2.0f * (q[1] * v.z - q[2] * v.y), ty = 2.0f * (q[2] * v.x - q[0] * v.z), tz = 2.0f * (q[0] * v.y - q[1] * v.x);
		return { v.x + q[3] * tx + (q[1] * tz - q[2] * ty), v.y + q[3] * ty + (q[2] * tx - q[0] * tz), v.z + q[3] * tz + (q[0] * ty - q[1] * tx) };
	}

	static void conjugate_mul(const f32* a, const f32* b, f32* out)   // out = conj(a) * b
	{
		const f32 ax = -a[0], ay = -a[1], az = -a[2], aw = a[3];
		out[0] = aw * b[0] + ax * b[3] + ay * b[2] - az * b[1];
		out[1] = aw * b[1] - ax * b[2] + ay * b[3] + az * b[0];
		out[2] = aw * b[2] + ax * b[1] - ay * b[0] + az * b[3];
		out[3] = aw * b[3] - ax * b[0] - ay * b[1] - az * b[2];
	}

	static f32 quat_angle_deg(const f32* a, const f32* b)   // angle of the rotation between two unit quaternions
	{
		const f32 dot = std::fabs(a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3]);
		return 2.0f * std::acos(std::min(1.0f, dot)) * 57.2957795f;
	}

	static vec body_pos(u32 body, f32* out_quat = nullptr)
	{
		f32 p[3]{}, q[4]{ 0.0f, 0.0f, 0.0f, 1.0f };
		physics::get_body_transform(body, p, q);
		if (out_quat) { out_quat[0] = q[0]; out_quat[1] = q[1]; out_quat[2] = q[2]; out_quat[3] = q[3]; }
		return { p[0], p[1], p[2] };
	}

	static s32 drain_broken(u32* ids, f32* forces, s32 max)
	{
		return physics::get_broken_constraints(ids, forces, max);
	}

	void run_constraint_tests()
	{
		check("no joints before the joint tests", physics::constraint_count() == 0);
		test_hinge_door();       // (f)
		test_pendulum();         // (g)
		test_slider();           // (h)
		test_fixed_joint();      // (i)
		test_distance_rope();    // (j)
		test_breakable_hinge();  // (k)
		test_joint_api();        // (l) validation, connected bodies don't collide, enable/disable, debug lines
		check("every joint scene cleaned up after itself", physics::constraint_count() == 0);
	}

	// (f) A 1 x 2 x 0.1 m, 20 kg door hinged to the world on a vertical axis through its left edge, limits +-100 deg.
	void test_hinge_door()
	{
		const f32 half[3] = { 0.5f, 1.0f, 0.05f };
		const f32 center[3] = { 20.5f, 1.2f, 0.0f };
		const u32 door = physics::create_body(8001, physics::shape_box, half, center, k_identity,
			physics::motion_dynamic, 20.0f, 0.5f, 0.0f, 0.0f, 0.05f, false, physics::layer_dynamic, 0, 1.0f);
		const f32 pivot[3] = { 20.0f, 1.2f, 0.0f };
		const f32 up[3] = { 0.0f, 1.0f, 0.0f };
		const f32 leaf[3] = { 1.0f, 0.0f, 0.0f };
		const u32 hinge = physics::create_hinge(door, 0, pivot, up, leaf, -100.0f, 100.0f, true, 0.0f, 0.0f, 0.0f);
		check("hinge created (door hinged to the world)", door != 0 && hinge != 0 && physics::constraint_valid(hinge));
		check("constraint_count() == 1", physics::constraint_count() == 1);
		check("a new joint is enabled", physics::constraint_enabled(hinge));

		for (int i = 0; i < 30; ++i) physics::step(k_dt, 1);
		const f32 hanging_angle = physics::get_hinge_angle(hinge);
		const f32 hanging_force = physics::get_constraint_force(hinge);
		std::printf("[info] hanging door: angle %.3f deg, hinge force %.1f N (m*g = %.1f N)\n", hanging_angle, hanging_force, 20.0f * 9.81f);
		check("an unpushed door stays closed (|angle| < 1 deg)", std::fabs(hanging_angle) < 1.0f);
		check("the hinge carries the door's weight (force = m*g +- 10%)", std::fabs(hanging_force - 196.2f) < 19.6f);

		// Push the free edge along +Z: the door turns about -Y, i.e. towards the -100 deg limit.
		const f32 push[3] = { 0.0f, 0.0f, 30.0f };
		const f32 edge[3] = { 21.0f, 1.2f, 0.0f };
		physics::add_impulse_at_point(door, push, edge);
		f32 min_angle = 0.0f, angle_10 = 0.0f, max_pivot_error = 0.0f, max_angle_mismatch = 0.0f;
		for (int i = 0; i < 120; ++i)
		{
			physics::step(k_dt, 1);
			const f32 angle = physics::get_hinge_angle(hinge);
			min_angle = std::min(min_angle, angle);
			if (i == 9) angle_10 = angle;
			f32 q[4];
			const vec p = body_pos(door, q);
			const vec h = rotate(q, { -0.5f, 0.0f, 0.0f });   // the door's hinge edge in world space
			max_pivot_error = std::max(max_pivot_error, length(sub({ p.x + h.x, p.y + h.y, p.z + h.z }, { pivot[0], pivot[1], pivot[2] })));
			const vec leaf_now = rotate(q, { 1.0f, 0.0f, 0.0f });
			const f32 yaw = std::atan2(-leaf_now.z, leaf_now.x) * 57.2957795f;   // +yaw rotates (1,0,0) to (0,0,-1)
			max_angle_mismatch = std::max(max_angle_mismatch, std::fabs(yaw - angle));
		}
		const f32 final_angle = physics::get_hinge_angle(hinge);
		std::printf("[info] pushed door: angle after 10 steps %.1f, min %.2f, final %.2f deg; max pivot error %.2f mm; angle vs yaw %.3f deg\n",
			angle_10, min_angle, final_angle, max_pivot_error * 1000.0f, max_angle_mismatch);
		check("the pushed door swings (angle < -20 deg after 10 steps)", angle_10 < -20.0f);
		check("the door stops at its -100 deg limit (never more than 2 deg past it)", min_angle > -102.0f);
		check("the door rests at the limit (final angle < -95 deg)", final_angle < -95.0f);
		check("the door stays attached (hinge edge within 1 cm of the pivot)", max_pivot_error < 0.01f);
		check("get_hinge_angle() matches the door's actual yaw (+-0.5 deg)", max_angle_mismatch < 0.5f);

		// Velocity motor: +90 deg/s, 200 N·m, for one second.
		physics::set_hinge_motor(hinge, physics::motor_velocity, 90.0f, 200.0f, 0.0f, -1.0f);
		const f32 before = physics::get_hinge_angle(hinge);
		for (int i = 0; i < 60; ++i) physics::step(k_dt, 1);
		const f32 turned = physics::get_hinge_angle(hinge) - before;
		std::printf("[info] velocity motor turned the door by %.1f deg in 1 s\n", turned);
		check("velocity motor turns the door (+90 deg/s for 1 s: 80..95 deg)", turned > 80.0f && turned < 95.0f);

		// Position motor (spring, 2 Hz, critically damped): swing to +45 deg and hold.
		physics::set_hinge_motor(hinge, physics::motor_position, 45.0f, 500.0f, 2.0f, 1.0f);
		for (int i = 0; i < 180; ++i) physics::step(k_dt, 1);
		const f32 held = physics::get_hinge_angle(hinge);
		std::printf("[info] position motor: door at %.2f deg (target 45)\n", held);
		check("position motor holds the door at its target (45 +- 2 deg)", std::fabs(held - 45.0f) < 2.0f);
		physics::set_hinge_motor(hinge, physics::motor_off, 0.0f, 0.0f, 0.0f, -1.0f);

		physics::destroy_body(door);
		check("destroying a body destroys its joints", !physics::constraint_valid(hinge) && physics::constraint_count() == 0);
		check("a destroyed joint reads 0", physics::get_hinge_angle(hinge) == 0.0f && !physics::constraint_enabled(hinge));
	}

	// (g) Physical pendulum: a 1 kg ball (r = 0.1 m) on a ball joint 1 m below the pivot, released at 10 deg.
	// T = 2 pi sqrt(I_pivot / (m g L)) with I_pivot = 2/5 m r^2 + m L^2.
	void test_pendulum()
	{
		const f32 L = 1.0f, r = 0.1f, theta0 = 10.0f * 3.14159265f / 180.0f;
		const f32 pivot[3] = { 30.0f, 5.0f, 0.0f };
		const f32 start[3] = { 30.0f + L * std::sin(theta0), 5.0f - L * std::cos(theta0), 0.0f };
		const f32 dims[3] = { r, 0.0f, 0.0f };
		const u32 bob = physics::create_body(8101, physics::shape_sphere, dims, start, k_identity,
			physics::motion_dynamic, 1.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_dynamic, 0, 1.0f);
		const u32 ball = physics::create_ball_joint(bob, 0, pivot, nullptr, 0.0f, 0.0f, 0.0f, false, 0.0f);
		check("ball joint created (pendulum)", bob != 0 && ball != 0);

		f32 prev_x = start[0] - pivot[0], first = -1.0f, last = -1.0f, t = 0.0f, max_len_error = 0.0f;
		int crossings = 0;
		for (int i = 0; i < 600; ++i)   // 10 s
		{
			physics::step(k_dt, 1);
			t += k_dt;
			const vec p = body_pos(bob);
			max_len_error = std::max(max_len_error, std::fabs(length(sub(p, { pivot[0], pivot[1], pivot[2] })) - L));
			const f32 x = p.x - pivot[0];
			if ((prev_x > 0.0f && x <= 0.0f) || (prev_x < 0.0f && x >= 0.0f))
			{
				const f32 tc = t - k_dt + k_dt * prev_x / (prev_x - x);   // interpolated crossing time
				if (first < 0.0f) first = tc;
				last = tc;
				++crossings;
			}
			prev_x = x;
		}
		const f32 expected = 2.0f * 3.14159265f * std::sqrt((0.4f * r * r + L * L) / (9.81f * L));
		const f32 measured = crossings > 1 ? 2.0f * (last - first) / f32(crossings - 1) : 0.0f;
		std::printf("[info] pendulum: %d crossings, period %.4f s (expected %.4f s, %+.2f%%), max length error %.2f mm\n",
			crossings, measured, expected, 100.0f * (measured - expected) / expected, max_len_error * 1000.0f);
		check("the pendulum keeps swinging (>= 8 half swings in 10 s)", crossings >= 8);
		check("pendulum period matches 2 pi sqrt(I / (m g L)) within 10%", std::fabs(measured - expected) < 0.1f * expected);
		check("the ball joint keeps the bob on its sphere (|length - L| < 1 cm)", max_len_error < 0.01f);
		physics::destroy_body(bob);
	}

	// (h) A 2 kg box on a horizontal slider along +X, limits [-1, +1] m, kicked diagonally; then its motors.
	void test_slider()
	{
		const f32 half[3] = { 0.25f, 0.25f, 0.25f };
		const f32 at[3] = { 40.0f, 3.0f, 0.0f };
		const u32 box = physics::create_body(8201, physics::shape_box, half, at, k_identity,
			physics::motion_dynamic, 2.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_dynamic, 0, 1.0f);
		const f32 axis[3] = { 1.0f, 0.0f, 0.0f };
		const u32 slider = physics::create_slider(box, 0, at, axis, -1.0f, 1.0f, true, physics::motor_off, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f);
		check("slider created", box != 0 && slider != 0);

		const f32 kick[3] = { 6.0f, 6.0f, 6.0f };   // 3 m/s along every axis: only X may survive
		physics::add_impulse(box, kick);
		f32 max_off_axis = 0.0f, max_position = -10.0f, max_rotation = 0.0f, max_mismatch = 0.0f;
		for (int i = 0; i < 120; ++i)
		{
			physics::step(k_dt, 1);
			f32 q[4];
			const vec p = body_pos(box, q);
			max_off_axis = std::max(max_off_axis, std::sqrt((p.y - at[1]) * (p.y - at[1]) + (p.z - at[2]) * (p.z - at[2])));
			max_rotation = std::max(max_rotation, quat_angle_deg(q, k_identity));
			const f32 position = physics::get_slider_position(slider);
			max_position = std::max(max_position, position);
			max_mismatch = std::max(max_mismatch, std::fabs(position - (p.x - at[0])));
		}
		std::printf("[info] slider: max position %.4f m, off-axis %.2f mm, rotation %.3f deg, position vs x %.2f mm\n",
			max_position, max_off_axis * 1000.0f, max_rotation, max_mismatch * 1000.0f);
		check("the slider body moves only along its axis (off-axis < 1 cm)", max_off_axis < 0.01f);
		check("the slider body does not rotate (< 0.5 deg)", max_rotation < 0.5f);
		check("the slider reaches and respects its +1 m limit (0.95..1.02 m)", max_position > 0.95f && max_position < 1.02f);
		check("get_slider_position() = displacement along the axis", max_mismatch < 1.0e-3f);

		physics::set_slider_motor(slider, physics::motor_velocity, -0.5f, 100.0f, 0.0f, -1.0f);
		for (int i = 0; i < 300; ++i) physics::step(k_dt, 1);   // 5 s at 0.5 m/s covers the 2 m to the other limit
		const f32 driven = physics::get_slider_position(slider);
		physics::set_slider_motor(slider, physics::motor_position, 0.25f, 0.0f, 2.0f, 1.0f);
		for (int i = 0; i < 180; ++i) physics::step(k_dt, 1);
		const f32 sprung = physics::get_slider_position(slider);
		std::printf("[info] slider motors: velocity -> %.3f m (limit -1), position spring -> %.3f m (target 0.25)\n", driven, sprung);
		check("velocity motor drives the slider to its -1 m limit", std::fabs(driven + 1.0f) < 0.02f);
		check("position motor springs the slider to its target (0.25 +- 0.02 m)", std::fabs(sprung - 0.25f) < 0.02f);
		physics::destroy_body(box);
	}

	// (i) Two boxes welded together, thrown with spin: their relative pose must not change.
	void test_fixed_joint()
	{
		const f32 half[3] = { 0.25f, 0.25f, 0.25f };
		const f32 pa[3] = { 50.0f, 8.0f, 0.0f };
		const f32 pb[3] = { 50.6f, 8.2f, 0.0f };
		const f32 s = std::sin(0.26179939f), c = std::cos(0.26179939f);   // 30 deg yaw for B
		const f32 qb0[4] = { 0.0f, s, 0.0f, c };
		const u32 a = physics::create_body(8301, physics::shape_box, half, pa, k_identity,
			physics::motion_dynamic, 3.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_dynamic, 0, 1.0f);
		const u32 b = physics::create_body(8302, physics::shape_box, half, pb, qb0,
			physics::motion_dynamic, 1.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_dynamic, 0, 1.0f);
		const u32 weld = physics::create_fixed(a, b, nullptr, 0.0f);
		check("fixed joint created between two dynamic bodies", a != 0 && b != 0 && weld != 0);

		f32 qa[4], qb[4], rel0[4];
		vec a0 = body_pos(a, qa), b0 = body_pos(b, qb);
		conjugate_mul(qa, qb, rel0);
		const f32 qa_start[4] = { qa[0], qa[1], qa[2], qa[3] };
		const f32 inv_a0[4] = { -qa[0], -qa[1], -qa[2], qa[3] };
		const vec rel_pos0 = rotate(inv_a0, sub(b0, a0));

		const f32 hit[3] = { 0.0f, 6.0f, 4.0f };
		const f32 where[3] = { 50.0f, 8.25f, 0.25f };
		physics::add_impulse_at_point(a, hit, where);
		f32 max_pos_error = 0.0f, max_rot_error = 0.0f;
		for (int i = 0; i < 90; ++i)
		{
			physics::step(k_dt, 1);
			const vec pa_now = body_pos(a, qa), pb_now = body_pos(b, qb);
			const f32 inv_a[4] = { -qa[0], -qa[1], -qa[2], qa[3] };
			const vec rel_pos = rotate(inv_a, sub(pb_now, pa_now));
			max_pos_error = std::max(max_pos_error, length(sub(rel_pos, rel_pos0)));
			f32 rel[4];
			conjugate_mul(qa, qb, rel);
			max_rot_error = std::max(max_rot_error, quat_angle_deg(rel, rel0));
		}
		const f32 spun = quat_angle_deg(qa, qa_start);
		const f32 moved = length(sub(body_pos(a), a0));
		std::printf("[info] welded pair: moved %.2f m, spun %.1f deg; relative error %.2f mm / %.3f deg\n",
			moved, spun, max_pos_error * 1000.0f, max_rot_error);
		check("the welded pair actually tumbles (moved > 1 m, spun > 20 deg)", moved > 1.0f && spun > 20.0f);
		check("the fixed joint keeps the relative position (< 1 cm)", max_pos_error < 0.01f);
		check("the fixed joint keeps the relative rotation (< 1 deg)", max_rot_error < 1.0f);
		physics::destroy_body(a);
		physics::destroy_body(b);
	}

	// (j) Ropes: a 2 kg ball on a 2 m distance joint (min 0 = it can go slack) from a world point.
	void test_distance_rope()
	{
		const f32 anchor[3] = { 60.0f, 6.0f, 0.0f };
		const vec anchor_v = { anchor[0], anchor[1], anchor[2] };
		const f32 dims[3] = { 0.15f, 0.0f, 0.0f };

		// 1) Taut rope released horizontally (max length -1 = the 2 m at creation): swings down, never longer than 2 m.
		const f32 side[3] = { 62.0f, 6.0f, 0.0f };
		u32 ball = physics::create_body(8401, physics::shape_sphere, dims, side, k_identity,
			physics::motion_dynamic, 2.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_dynamic, 0, 1.0f);
		u32 rope = physics::create_distance(ball, 0, side, anchor, 0.0f, -1.0f, 0.0f, 0.0f, 0.0f);
		check("distance joint (rope) created", ball != 0 && rope != 0);
		f32 max_d = 0.0f, lowest = 10.0f, force = 0.0f;
		for (int i = 0; i < 180; ++i)
		{
			physics::step(k_dt, 1);
			const vec p = body_pos(ball);
			max_d = std::max(max_d, length(sub(p, anchor_v)));
			lowest = std::min(lowest, p.y);
			force = std::max(force, physics::get_constraint_force(rope));
		}
		std::printf("[info] taut rope swing: max length %.4f m (2 m), lowest point y = %.3f, peak rope force %.0f N (3 m g = %.0f N)\n",
			max_d, lowest, force, 3.0f * 2.0f * 9.81f);
		check("a taut swinging rope never gets longer than its length (<= 2 m + 5 mm)", max_d <= 2.005f);
		check("... it really swung through the bottom (y < 4.05)", lowest < 4.05f);
		check("rope force at the bottom of a 90 deg swing ~ 3 m g (+-15%)", std::fabs(force - 58.86f) < 0.15f * 58.86f);
		physics::destroy_body(ball);

		// 2) Slack rope: start 1 m below the anchor, thrown down and sideways; flies free until the rope snaps taut.
		//    Jolt engages a distance limit only once it is exceeded, so the snap overshoots by up to one step of
		//    motion (v * dt); the next step pulls it back.
		const f32 start[3] = { 60.0f, 5.0f, 0.0f };
		ball = physics::create_body(8402, physics::shape_sphere, dims, start, k_identity,
			physics::motion_dynamic, 2.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_dynamic, 0, 1.0f);
		rope = physics::create_distance(ball, 0, start, anchor, 0.0f, 2.0f, 0.0f, 0.0f, 0.0f);
		const f32 throw_v[3] = { 1.5f, -2.0f, 0.0f };
		physics::set_linear_velocity(ball, throw_v);
		f32 min_d = 10.0f, after_snap = 0.0f;
		max_d = 0.0f;
		int snap = -1;
		for (int i = 0; i < 180; ++i)
		{
			physics::step(k_dt, 1);
			const f32 d = length(sub(body_pos(ball), anchor_v));
			max_d = std::max(max_d, d);
			min_d = std::min(min_d, d);
			if (snap < 0 && d > 1.995f) snap = i;
			if (snap >= 0 && i >= snap + 2) after_snap = std::max(after_snap, d);
		}
		std::printf("[info] slack rope: distance %.4f .. %.4f m, snapped taut at step %d, max length from 2 steps after the snap on %.4f m\n",
			min_d, max_d, snap, after_snap);
		check("a slack rope does not pull (the ball flies freely below 2 m first)", min_d < 1.1f && snap > 5);
		check("the rope snaps taut and the snap overshoots by at most one step of motion (<= 2.1 m)", snap >= 0 && max_d <= 2.1f);
		check("from the second step after the snap on the rope holds its length (<= 2.01 m)", after_snap > 1.9f && after_snap <= 2.01f);
		physics::destroy_body(ball);
	}

	// (k) Door with a 2000 N breakable hinge: a small push must not break it, a big impulse must (reported once).
	void test_breakable_hinge()
	{
		const f32 half[3] = { 0.5f, 1.0f, 0.05f };
		const f32 center[3] = { 70.5f, 1.2f, 0.0f };
		const u32 door = physics::create_body(8501, physics::shape_box, half, center, k_identity,
			physics::motion_dynamic, 20.0f, 0.5f, 0.0f, 0.0f, 0.05f, false, physics::layer_dynamic, 0, 1.0f);
		const f32 pivot[3] = { 70.0f, 1.2f, 0.0f };
		const f32 up[3] = { 0.0f, 1.0f, 0.0f };
		const u32 hinge = physics::create_hinge(door, 0, pivot, up, nullptr, -100.0f, 100.0f, true, 0.0f, 0.0f, 2000.0f);
		check("breakable hinge created", door != 0 && hinge != 0);

		u32 ids[4]{}; f32 forces[4]{};
		const f32 small[3] = { 0.0f, 0.0f, 5.0f };
		const f32 edge[3] = { 71.0f, 1.2f, 0.0f };
		physics::add_impulse_at_point(door, small, edge);
		f32 peak = 0.0f;
		for (int i = 0; i < 30; ++i) { physics::step(k_dt, 1); peak = std::max(peak, physics::get_constraint_force(hinge)); }
		std::printf("[info] small push: peak hinge force %.0f N (break force 2000 N)\n", peak);
		check("a small push does not break a 2000 N hinge", drain_broken(ids, forces, 4) == 0 && physics::constraint_enabled(hinge));

		const f32 big[3] = { 0.0f, 0.0f, 400.0f };   // 20 m/s at the centre of mass: the pivot must stop ~100 N·s in one step
		physics::add_impulse(door, big);
		physics::step(k_dt, 1);
		const s32 n = drain_broken(ids, forces, 4);
		std::printf("[info] big impulse: %d joint(s) broke, force %.0f N\n", n, n > 0 ? forces[0] : 0.0f);
		check("a big impulse breaks the hinge and it is reported", n == 1 && ids[0] == hinge && forces[0] > 2000.0f);
		check("the broken hinge is disabled, its handle stays valid", physics::constraint_valid(hinge) && !physics::constraint_enabled(hinge));
		check("a break is reported only once", drain_broken(ids, forces, 4) == 0);
		for (int i = 0; i < 30; ++i) physics::step(k_dt, 1);
		f32 q[4];
		const vec p = body_pos(door, q);
		const vec h = rotate(q, { -0.5f, 0.0f, 0.0f });
		const f32 off_pivot = length(sub({ p.x + h.x, p.y + h.y, p.z + h.z }, { pivot[0], pivot[1], pivot[2] }));
		std::printf("[info] broken door flew %.2f m off its pivot\n", off_pivot);
		check("the broken door is free (hinge edge > 1 m from the pivot)", off_pivot > 1.0f);
		physics::destroy_body(door);
	}

	// (l) Validation, collision filtering between connected bodies, enable / disable, debug lines.
	void test_joint_api()
	{
		const f32 half[3] = { 0.25f, 0.25f, 0.25f };
		const f32 pa[3] = { 80.0f, 8.0f, 0.0f };
		const f32 pb[3] = { 80.3f, 8.0f, 0.0f };   // overlaps A by 0.2 m
		const u32 a = physics::create_body(8601, physics::shape_box, half, pa, k_identity,
			physics::motion_dynamic, 1.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_dynamic, 0, 1.0f);
		const u32 b = physics::create_body(8602, physics::shape_box, half, pb, k_identity,
			physics::motion_dynamic, 1.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_dynamic, 0, 1.0f);
		const f32 mid[3] = { 80.15f, 8.0f, 0.0f };
		const f32 up[3] = { 0.0f, 1.0f, 0.0f };

		check("invalid body A is rejected", physics::create_hinge(0, 0, mid, up, nullptr, 0.0f, 0.0f, false, 0.0f, 0.0f, 0.0f) == 0);
		check("invalid body B is rejected", physics::create_ball_joint(a, 0xDEAD0001u, mid, nullptr, 0.0f, 0.0f, 0.0f, false, 0.0f) == 0);
		check("a joint from a body to itself is rejected", physics::create_fixed(a, a, nullptr, 0.0f) == 0);
		check("rejected joints leave no trace", physics::constraint_count() == 0);

		// Connected bodies do not collide: two overlapping boxes on a ball joint fall together untouched.
		const u32 joint = physics::create_ball_joint(a, b, mid, nullptr, 0.0f, 0.0f, 0.0f, false, 0.0f);
		check("ball joint between two dynamic bodies", joint != 0);
		std::vector<physics::contact_event> events;
		for (int i = 0; i < 20; ++i) step_and_drain(events);
		bool touched = false;
		for (const auto& e : events)
			if ((e.entity_a == 8601 && e.entity_b == 8602) || (e.entity_a == 8602 && e.entity_b == 8601)) touched = true;
		const f32 gap = length(sub(body_pos(b), body_pos(a)));
		std::printf("[info] joined overlapping boxes: distance %.4f m (0.3 at start), contact events between them: %s\n", gap, touched ? "yes" : "no");
		check("bodies connected by a joint do not collide (no contact events)", !touched);
		check("... and are not pushed apart (distance stays 0.3 m)", std::fabs(gap - 0.3f) < 0.01f);

		// Disabling the joint makes them ordinary overlapping bodies again: they collide and separate.
		physics::set_constraint_enabled(joint, false);
		check("set_constraint_enabled(false)", !physics::constraint_enabled(joint) && physics::constraint_valid(joint));
		events.clear();
		for (int i = 0; i < 20; ++i) step_and_drain(events);
		touched = false;
		for (const auto& e : events)
			if (e.kind == physics::contact_added && ((e.entity_a == 8601 && e.entity_b == 8602) || (e.entity_a == 8602 && e.entity_b == 8601))) touched = true;
		const f32 apart = length(sub(body_pos(b), body_pos(a)));
		std::printf("[info] after disabling the joint: distance %.4f m, contact: %s\n", apart, touched ? "yes" : "no");
		check("a disabled joint's bodies collide again (contact added, pushed apart)", touched && apart > 0.4f);
		physics::set_constraint_enabled(joint, true);
		check("set_constraint_enabled(true) re-enables", physics::constraint_enabled(joint));

		// Debug lines: joints are drawn first, starting with the anchor marker centred on the joint as seen from
		// the connected body (B, Jolt body 1): B's pose applied to the anchor's offset from B at creation.
		std::vector<f32> lines(60000);
		const s32 written = physics::get_debug_lines(lines.data(), (s32)lines.size());
		const vec marker = { (lines[0] + lines[3]) * 0.5f, (lines[1] + lines[4]) * 0.5f, (lines[2] + lines[5]) * 0.5f };
		f32 qb[4];
		const vec pb_now = body_pos(b, qb);
		const vec offset = rotate(qb, { mid[0] - pb[0], mid[1] - pb[1], mid[2] - pb[2] });
		const vec expected = { pb_now.x + offset.x, pb_now.y + offset.y, pb_now.z + offset.z };
		std::printf("[info] debug lines with a joint: %d floats, first segment centred at (%.3f, %.3f, %.3f), joint at (%.3f, %.3f, %.3f)\n",
			written, marker.x, marker.y, marker.z, expected.x, expected.y, expected.z);
		check("debug lines start with the joint gizmo (anchor marker on the joint)", written > 0 && written % 6 == 0 &&
			length(sub(marker, expected)) < 1.0e-3f);
		physics::destroy_constraint(joint);
		check("destroy_constraint() removes the joint", !physics::constraint_valid(joint) && physics::constraint_count() == 0);
		physics::destroy_constraint(joint);   // twice: a stale handle is a no-op
		physics::set_constraint_enabled(joint, true);
		check("stale joint handles are rejected", !physics::constraint_valid(joint) && physics::get_constraint_force(joint) == 0.0f);

		// A joint that is still alive when clear() runs goes with it (checked in the teardown).
		physics::destroy_body(a);
		physics::destroy_body(b);
	}

	void run_teardown_tests()
	{
		check("body_count() back to ground + box", physics::body_count() == 2);
		physics::destroy_body(_box);
		check("destroy_body() decrements the count", physics::body_count() == 1 && !physics::body_valid(_box));
		{
			const f32 half[3] = { 0.2f, 0.2f, 0.2f };
			const f32 at[3] = { 90.0f, 3.0f, 0.0f };
			const u32 swing = physics::create_body(9001, physics::shape_box, half, at, k_identity,
				physics::motion_dynamic, 1.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_dynamic, 0, 1.0f);
			const f32 up[3] = { 0.0f, 1.0f, 0.0f };
			const u32 hinge = physics::create_hinge(swing, 0, at, up, nullptr, 0.0f, 0.0f, false, 0.0f, 0.0f, 0.0f);
			physics::clear();
			check("clear() removes every joint", physics::constraint_count() == 0 && hinge != 0 && !physics::constraint_valid(hinge));
		}
		check("clear() removes every body", physics::body_count() == 0 && !physics::body_valid(_ground));
		physics::step(k_dt, 1);   // stepping an empty world must be fine
		check("world still available after clear()", physics::available());
	}

	void run_stub_checks()
	{
		const f32 dims[3] = { 0.5f, 0.5f, 0.5f };
		const f32 pos[3] = { 0.0f, 0.0f, 0.0f };
		check("stub: init() returns false", !physics::init());
		check("stub: available() is false", !physics::available());
		check("stub: create_body returns 0", physics::create_body(1, physics::shape_box, dims, pos, k_identity,
			physics::motion_dynamic, 0.0f, 0.5f, 0.0f, 0.0f, 0.0f, false, physics::layer_dynamic, 0, 1.0f) == 0);
		check("stub: body_count() is 0", physics::body_count() == 0);
		const f32 down[3] = { 0.0f, -1.0f, 0.0f };
		f32 p[3]{}, n[3]{}, d = 0.0f; u64 entity = 0; u32 body = 0;
		check("stub: raycast returns false", !physics::raycast(pos, down, 10.0f, physics::layer_mask_all, p, n, &d, &entity, &body));
		physics::contact_event buf[4];
		check("stub: get_contacts returns 0", physics::get_contacts(buf, 4) == 0);
		check("stub: character_create returns 0", physics::character_create(0.35f, 1.8f, pos, 45.0f, 0.3f, 70.0f) == 0);
		f32 lines[12];
		check("stub: get_debug_lines returns 0", physics::get_debug_lines(lines, 12) == 0);
		const f32 up[3] = { 0.0f, 1.0f, 0.0f };
		check("stub: joint creators return 0",
			physics::create_hinge(1, 0, pos, up, nullptr, -90.0f, 90.0f, true, 0.0f, 0.0f, 0.0f) == 0 &&
			physics::create_ball_joint(1, 0, pos, nullptr, 45.0f, -10.0f, 10.0f, true, 0.0f) == 0 &&
			physics::create_slider(1, 0, pos, up, -1.0f, 1.0f, true, physics::motor_off, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f) == 0 &&
			physics::create_fixed(1, 2, nullptr, 0.0f) == 0 &&
			physics::create_distance(1, 0, pos, up, 0.0f, 2.0f, 0.0f, 0.0f, 0.0f) == 0);
		u32 broken[2]; f32 forces[2];
		check("stub: joint queries return 0", physics::constraint_count() == 0 && !physics::constraint_valid(1) &&
			physics::get_hinge_angle(1) == 0.0f && physics::get_slider_position(1) == 0.0f &&
			physics::get_broken_constraints(broken, forces, 2) == 0);
		physics::set_hinge_motor(1, physics::motor_velocity, 90.0f, 10.0f, 0.0f, -1.0f);
		physics::set_constraint_enabled(1, false);
		physics::destroy_constraint(1);
		physics::step(k_dt, 1);
		physics::clear();
		check("stub: step/clear are harmless no-ops", true);
	}

	void check(const char* what, bool ok)
	{
		std::cout << (ok ? "[PASS] " : "[FAIL] ") << what << "\n";
		ok ? ++_passed : ++_failed;
	}

	void check_silent(bool ok)
	{
		if (!ok) check("(silent consistency check)", false);
	}

	bool _available{ false };
	u32 _ground{ 0 };
	u32 _box{ 0 };
	int _passed{ 0 };
	int _failed{ 0 };
};
