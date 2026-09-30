#pragma once

#include "../Engine/Physics/PhysicsWorld.h"

#include "Test.h"

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

// Physics v2 smoke test (issues #100/#101/#102/#105/#106/#107): drives Engine/Physics/PhysicsWorld directly
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

	void run_teardown_tests()
	{
		check("body_count() back to ground + box", physics::body_count() == 2);
		physics::destroy_body(_box);
		check("destroy_body() decrements the count", physics::body_count() == 1 && !physics::body_valid(_box));
		physics::clear();
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
