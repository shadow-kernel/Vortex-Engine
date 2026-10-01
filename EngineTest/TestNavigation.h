#pragma once

#include "../Engine/Navigation/NavigationWorld.h"

#include "Test.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <iostream>
#include <vector>

using namespace vortex;

// AI & Navigation smoke test (issues #109 / #110): drives Engine/Navigation directly (the module libVortexAPI exports
// through NavigationApi.cpp). Bakes a small level — a triangle floor, a wall (solid box), a ramp up to a platform,
// an unreachable high platform and a triangle-mesh fence — then checks paths, queries, serialization and the crowd.
// Non-interactive; prints "ALL PASSED" or "FAILURES!" for ctest. Without Recast the stub contract is verified.
class engine_test : public test
{
public:
	bool initialize() override
	{
		std::cout << "Navigation Test Initialized\n";
		_available = navigation::available();
		std::cout << (_available ? "[info] Recast/Detour compiled in\n"
			: "[info] Recast not compiled in (VORTEX_HAS_RECAST=0) - verifying the stub contract only\n");
		return true;
	}

	void run() override
	{
		if (!_available) run_stub_checks();
		else
		{
			if (run_bake_tests())
			{
				run_path_tests();
				run_query_tests();
				run_serialization_tests();
				run_crowd_tests();
			}
			run_error_tests();
		}
		std::cout << "\nRESULT: " << _passed << "/" << (_passed + _failed)
			<< (_failed == 0 ? " - ALL PASSED\n" : " - FAILURES!\n");
	}

	void shutdown() override
	{
		navigation::unload();
		navigation::bake_result_clear();
		std::cout << "Navigation Test Shutdown\n";
	}

private:
	static constexpr f32 k_dt = 1.0f / 60.0f;
	static constexpr f32 k_radius = 0.4f;
	// The wall: x in [-0.5, 0.5], z in [-6, 6], 2 m high.
	static constexpr f32 k_wall_half_x = 0.5f;
	static constexpr f32 k_wall_half_z = 6.0f;

	std::vector<f32> _verts;
	std::vector<s32> _tris;
	std::vector<f32> _boxes;
	std::vector<u8> _blob;
	navigation::bake_settings _settings{};

	// ---- level ------------------------------------------------------------------------------------------------

	void add_quad(const f32* a, const f32* b, const f32* c, const f32* d)   // a,b,c,d counter-clockwise from the walkable side
	{
		const s32 base = (s32)_verts.size() / 3;
		for (const f32* v : { a, b, c, d }) { _verts.push_back(v[0]); _verts.push_back(v[1]); _verts.push_back(v[2]); }
		_tris.insert(_tris.end(), { base, base + 1, base + 2, base, base + 2, base + 3 });
	}

	void add_box(f32 cx, f32 cy, f32 cz, f32 hx, f32 hy, f32 hz, const f32* quat = nullptr)
	{
		const f32 q[4] = { quat ? quat[0] : 0.0f, quat ? quat[1] : 0.0f, quat ? quat[2] : 0.0f, quat ? quat[3] : 1.0f };
		_boxes.insert(_boxes.end(), { cx, cy, cz, hx, hy, hz, q[0], q[1], q[2], q[3] });
	}

	void build_level()
	{
		// Floor: 30 x 30 m quad at y = 0 (triangle soup). Seen from above (+Y) the order below is counter-clockwise
		// in the engine's convention: (v1-v0)x(v2-v0) points up.
		const f32 f0[3] = { -15, 0, -15 }, f1[3] = { -15, 0, 15 }, f2[3] = { 15, 0, 15 }, f3[3] = { 15, 0, -15 };
		add_quad(f0, f1, f2, f3);
		// Fence (triangle soup, vertical, both sides): z = -3, x in [-14, -9], 1.5 m high.
		const f32 w0[3] = { -14, 0, -3 }, w1[3] = { -9, 0, -3 }, w2[3] = { -9, 1.5f, -3 }, w3[3] = { -14, 1.5f, -3 };
		add_quad(w0, w1, w2, w3);
		add_quad(w1, w0, w3, w2);
		// Wall (solid box): 1 x 2 x 12 m in the middle of the floor.
		add_box(0.0f, 1.0f, 0.0f, k_wall_half_x, 1.0f, k_wall_half_z);
		// Platform 5 x 5 m, top at y = 2 (solid box) ...
		add_box(10.0f, 1.0f, 10.0f, 2.5f, 1.0f, 2.5f);
		// ... reached by a ramp from z = 2.5 (y = 0) to z = 7.5 (y = 2): a thin box pitched by -21.8 deg about X.
		const f32 len = std::sqrt(2.0f * 2.0f + 5.0f * 5.0f);
		const f32 a = -std::asin(2.0f / len);
		const f32 q[4] = { std::sin(a * 0.5f), 0.0f, 0.0f, std::cos(a * 0.5f) };
		const f32 up_y = std::cos(a), up_z = std::sin(a);   // the ramp's local +Y in world space
		add_box(10.0f, 1.0f - 0.1f * up_y, 5.0f - 0.1f * up_z, 1.5f, 0.1f, len * 0.5f, q);
		// Unreachable platform: top at y = 3, no ramp.
		add_box(-10.0f, 1.5f, 10.0f, 2.0f, 1.5f, 2.0f);
	}

	// ---- bake ---------------------------------------------------------------------------------------------------

	bool run_bake_tests()
	{
		build_level();
		navigation::default_settings(&_settings);
		_settings.cell_size = 0.2f;
		_settings.cell_height = 0.1f;
		_settings.agent_radius = k_radius;
		_settings.tile_size = 32;   // several tiles on purpose (tiled navmesh + tile-crossing paths)
		navigation::bake_stats stats{};
		const s32 size = navigation::bake(_verts.data(), (s32)_verts.size() / 3, _tris.data(), (s32)_tris.size() / 3, nullptr,
			_boxes.data(), (s32)_boxes.size() / navigation::box_floats, nullptr, &_settings, &stats);
		std::printf("[info] bake: %d bytes, %d x %d tiles (%d used), %d polys, %d verts, %d detail tris, %d tris + %d boxes, %.1f ms\n",
			size, stats.tiles_x, stats.tiles_z, stats.tile_count, stats.poly_count, stats.vert_count, stats.detail_tri_count,
			stats.input_triangles, stats.input_boxes, stats.bake_ms);
		check("bake succeeds", size > 0);
		if (size <= 0) return false;
		check("bake stats: several tiles", stats.tiles_x * stats.tiles_z >= 4 && stats.tile_count >= 4);
		check("bake stats: polygons + input counted", stats.poly_count > 10 && stats.input_triangles == 6 && stats.input_boxes == 4);
		check("bake progress reaches 1", navigation::bake_progress() >= 0.999f);
		check("bake is fast (< 5 s)", stats.bake_ms < 5000.0f);

		_blob.assign((size_t)size, 0);
		check("bake_result_copy returns the whole blob", navigation::bake_result_copy(_blob.data(), size) == size
			&& navigation::bake_result_copy(nullptr, 0) == size);
		check("load the baked navmesh", navigation::load(_blob.data(), size) && navigation::loaded());
		return navigation::loaded();
	}

	// ---- paths --------------------------------------------------------------------------------------------------

	static f32 dist(const f32* a, const f32* b) { const f32 d[3] = { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; return std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]); }

	static bool inside_wall(f32 x, f32 z, f32 margin)
	{
		return std::fabs(x) < k_wall_half_x + margin && std::fabs(z) < k_wall_half_z + margin;
	}

	// Euclidean distance (xz) from a point to the wall footprint (0 inside).
	static f32 wall_distance(f32 x, f32 z)
	{
		const f32 dx = std::max(0.0f, std::fabs(x) - k_wall_half_x);
		const f32 dz = std::max(0.0f, std::fabs(z) - k_wall_half_z);
		return std::sqrt(dx * dx + dz * dz);
	}

	// Smallest wall distance along a polyline.
	static f32 polyline_wall_clearance(const std::vector<f32>& pts, s32 n)
	{
		f32 best = 1.0e9f;
		for (s32 i = 0; i + 1 < n; ++i)
		{
			const f32* a = &pts[(size_t)i * 3];
			const f32* b = &pts[(size_t)(i + 1) * 3];
			for (int s = 0; s <= 100; ++s)
			{
				const f32 t = s / 100.0f;
				best = std::min(best, wall_distance(a[0] + (b[0] - a[0]) * t, a[2] + (b[2] - a[2]) * t));
			}
		}
		return best;
	}

	void run_path_tests()
	{
		std::vector<f32> pts(256 * 3);
		s32 status = -1;

		// (a) around the wall
		const f32 s[3] = { -5, 0, 0 }, e[3] = { 5, 0, 0 };
		s32 n = navigation::find_path(s, e, nullptr, pts.data(), 256, &status);
		f32 len = 0.0f, max_abs_z = 0.0f;
		for (s32 i = 0; i + 1 < n; ++i) len += dist(&pts[(size_t)i * 3], &pts[(size_t)(i + 1) * 3]);
		for (s32 i = 0; i < n; ++i) max_abs_z = std::max(max_abs_z, std::fabs(pts[(size_t)i * 3 + 2]));
		std::printf("[info] path around the wall: %d points, length %.2f m, max |z| %.2f, status %d\n", n, len, max_abs_z, status);
		for (s32 i = 0; i < n; ++i) std::printf("[info]   (%.2f, %.2f, %.2f)\n", pts[(size_t)i * 3], pts[(size_t)i * 3 + 1], pts[(size_t)i * 3 + 2]);
		check("path around the wall: complete", status == navigation::path_complete && n >= 3);
		check("path starts at the start and ends at the goal", n >= 2 && dist(&pts[0], s) < 0.3f && dist(&pts[(size_t)(n - 1) * 3], e) < 0.3f);
		check("path goes around the wall end (|z| > 6)", max_abs_z > k_wall_half_z);
		// The navmesh is eroded by the agent radius; contour simplification (edge_max_error = 1.3 cells = 0.26 m) may cut
		// the rounded corner, so the corner of a string-pulled path keeps at least radius - 0.26 m from the wall.
		const f32 clearance = polyline_wall_clearance(pts, n);
		std::printf("[info] path clearance to the wall: %.3f m (radius %.2f)\n", clearance, k_radius);
		check("path keeps the agent radius from the wall (minus the simplification tolerance)", clearance > k_radius - 0.27f);
		check("path is longer than the straight line (detour)", len > 15.0f && len < 20.0f);

		// count query
		check("find_path with a null buffer returns the point count", navigation::find_path(s, e, nullptr, nullptr, 0, &status) == n);

		// (b) up the ramp onto the platform
		const f32 s2[3] = { 0, 0, -10 }, e2[3] = { 10, 2, 10 };
		n = navigation::find_path(s2, e2, nullptr, pts.data(), 256, &status);
		bool via_ramp = false;
		for (s32 i = 0; i + 1 < n; ++i)
		{
			const f32* a = &pts[(size_t)i * 3];
			const f32* b = &pts[(size_t)(i + 1) * 3];
			for (int k = 0; k <= 20; ++k)
			{
				const f32 t = k / 20.0f;
				const f32 x = a[0] + (b[0] - a[0]) * t, z = a[2] + (b[2] - a[2]) * t, y = a[1] + (b[1] - a[1]) * t;
				if (x > 8.4f && x < 11.6f && z > 3.0f && z < 7.0f && y > 0.3f && y < 1.8f) via_ramp = true;
			}
		}
		std::printf("[info] path up the ramp: %d points, status %d, end (%.2f, %.2f, %.2f)\n", n, status,
			n > 0 ? pts[(size_t)(n - 1) * 3] : 0.0f, n > 0 ? pts[(size_t)(n - 1) * 3 + 1] : 0.0f, n > 0 ? pts[(size_t)(n - 1) * 3 + 2] : 0.0f);
		check("path onto the platform: complete", status == navigation::path_complete && n >= 2);
		check("path ends on the platform (y ~ 2)", n >= 2 && std::fabs(pts[(size_t)(n - 1) * 3 + 1] - 2.0f) < 0.35f);
		check("path climbs the ramp", via_ramp);

		// (c) the high platform (3 m, no ramp) is unreachable: partial path to the closest point
		const f32 e3[3] = { -10, 3, 10 };
		n = navigation::find_path(s2, e3, nullptr, pts.data(), 256, &status);
		std::printf("[info] path to the unreachable platform: %d points, status %d\n", n, status);
		check("unreachable platform: partial path", status == navigation::path_partial && n >= 1);
		check("unreachable platform: path stays on the floor", n >= 1 && pts[(size_t)(n - 1) * 3 + 1] < 0.5f);

		// (d) around the triangle-mesh fence
		const f32 s4[3] = { -12, 0, -5 }, e4[3] = { -12, 0, -1 };
		n = navigation::find_path(s4, e4, nullptr, pts.data(), 256, &status);
		f32 max_x = -100.0f;
		for (s32 i = 0; i < n; ++i) max_x = std::max(max_x, pts[(size_t)i * 3]);
		std::printf("[info] path around the fence: %d points, status %d, max x %.2f\n", n, status, max_x);
		check("path around the triangle fence: complete, goes past its end", status == navigation::path_complete && n >= 3 && max_x > -9.2f);

		// (e) no path from outside the navmesh
		const f32 far_away[3] = { 500, 0, 500 };
		n = navigation::find_path(far_away, e, nullptr, pts.data(), 256, &status);
		check("start far off the navmesh: invalid, no points", n == 0 && status == navigation::path_invalid);
	}

	void run_query_tests()
	{
		f32 p[3]{};
		const f32 above[3] = { 3, 1.5f, 3 };
		check("closest_point snaps onto the floor", navigation::closest_point(above, nullptr, p) && std::fabs(p[1]) < 0.2f
			&& std::fabs(p[0] - 3) < 0.05f && std::fabs(p[2] - 3) < 0.05f);
		const f32 in_wall[3] = { 0, 1, 0 };
		check("closest_point next to the wall stays outside it", navigation::closest_point(in_wall, nullptr, p) && !inside_wall(p[0], p[2], k_radius - 0.05f));
		const f32 nowhere[3] = { 500, 0, 500 };
		check("closest_point far away fails", !navigation::closest_point(nowhere, nullptr, p));

		// raycast along the surface: blocked by the wall (eroded by the radius), clear alongside it.
		const f32 s[3] = { -5, 0, 0 }, e[3] = { 5, 0, 0 };
		f32 hit[3]{}, normal[3]{}, t = 0.0f;
		const bool blocked = navigation::raycast(s, e, nullptr, hit, normal, &t);
		std::printf("[info] raycast: blocked=%d hit=(%.2f, %.2f, %.2f) normal=(%.2f, %.2f, %.2f) t=%.3f\n", blocked ? 1 : 0,
			hit[0], hit[1], hit[2], normal[0], normal[1], normal[2], t);
		check("raycast through the wall is blocked", blocked && t > 0.3f && t < 0.5f);
		check("raycast hit sits at the eroded wall face", std::fabs(hit[0] + (k_wall_half_x + k_radius)) < 0.25f && normal[0] < -0.9f);
		const f32 s2[3] = { -5, 0, -9 }, e2[3] = { 5, 0, -9 };
		check("raycast beside the wall is clear", !navigation::raycast(s2, e2, nullptr, hit, normal, &t) && t == 1.0f);

		// random points
		navigation::set_random_seed(1234);
		int on_mesh = 0, near_center = 0;
		for (int i = 0; i < 50; ++i)
		{
			f32 r[3]{}, c[3]{};
			if (navigation::random_point(r) && navigation::closest_point(r, nullptr, c) && dist(r, c) < 0.05f) ++on_mesh;
			const f32 center[3] = { -8, 0, -8 };
			if (navigation::random_point_around(center, 3.0f, nullptr, r))
			{
				const f32 dx = r[0] - center[0], dz = r[2] - center[2];
				if (std::sqrt(dx * dx + dz * dz) <= 3.0f + 0.01f && std::fabs(r[1]) < 0.2f) ++near_center;
			}
		}
		std::printf("[info] random points: %d/50 on the navmesh, %d/50 within 3 m of (-8, 0, -8)\n", on_mesh, near_center);
		check("random_point lands on the navmesh", on_mesh == 50);
		check("random_point_around stays near the centre", near_center >= 45);

		// debug geometry
		const s32 tri_floats = navigation::debug_triangles(nullptr, 0);
		const s32 line_floats = navigation::debug_lines(nullptr, 0, 1);
		const s32 all_lines = navigation::debug_lines(nullptr, 0, 3);
		std::vector<f32> buf((size_t)std::max(tri_floats, all_lines));
		const s32 got = navigation::debug_triangles(buf.data(), tri_floats);
		std::printf("[info] debug geometry: %d triangles, %d boundary segments, %d segments with internal edges\n", tri_floats / 9, line_floats / 6, all_lines / 6);
		check("debug triangles: count query matches the fill", tri_floats > 0 && tri_floats % 9 == 0 && got == tri_floats);
		check("debug lines: boundary + internal edges", line_floats > 0 && line_floats % 6 == 0 && all_lines > line_floats);
	}

	void run_serialization_tests()
	{
		navigation::bake_settings s{};
		navigation::bake_stats st{};
		check("get_info of the loaded navmesh", navigation::get_info(&s, &st) && std::fabs(s.agent_radius - k_radius) < 1e-6f
			&& st.data_size == (s32)_blob.size() && st.poly_count > 0);
		std::vector<u8> copy((size_t)navigation::data_copy(nullptr, 0));
		check("data_copy returns the loaded blob", !copy.empty() && navigation::data_copy(copy.data(), (s32)copy.size()) == (s32)copy.size() && copy == _blob);

		// A corrupt blob is rejected (and leaves nothing loaded); the good one loads again.
		std::vector<u8> bad = _blob;
		bad[0] ^= 0xFF;
		check("corrupt blob is rejected", !navigation::load(bad.data(), (s32)bad.size()) && !navigation::loaded());
		check("truncated blob is rejected", !navigation::load(_blob.data(), (s32)_blob.size() / 2) && !navigation::loaded());
		check("reload of the saved blob", navigation::load(copy.data(), (s32)copy.size()) && navigation::loaded());
		std::vector<f32> pts(64 * 3);
		s32 status = 0;
		const f32 a[3] = { -5, 0, 0 }, b[3] = { 5, 0, 0 };
		check("paths work after the reload", navigation::find_path(a, b, nullptr, pts.data(), 64, &status) >= 3 && status == navigation::path_complete);
	}

	// ---- crowd --------------------------------------------------------------------------------------------------

	navigation::agent_params params(f32 speed = 3.5f) const
	{
		navigation::agent_params p{};
		p.radius = k_radius;
		p.height = 1.8f;
		p.max_speed = speed;
		p.max_acceleration = 8.0f;
		p.separation_weight = 2.0f;
		p.avoidance_quality = 3;
		return p;
	}

	void run_crowd_tests()
	{
		// (a) one agent walks around the wall to the goal
		const auto p = params();
		const f32 start[3] = { -5, 0, 0 }, goal[3] = { 5, 0, 0 };
		const u32 agent = navigation::agent_add(start, &p);
		check("agent added", agent != 0 && navigation::agent_valid(agent) && navigation::agent_count() == 1);
		navigation::agent_state st{};
		check("new agent is idle on the navmesh", navigation::agent_get_state(agent, &st) && st.move_state == navigation::move_idle && st.on_navmesh == 1);
		check("set_target accepted (asynchronous)", navigation::agent_set_target(agent, goal));
		navigation::agent_get_state(agent, &st);
		const bool pending_first = st.move_state == navigation::move_pending || st.move_state == navigation::move_moving;
		check("state right after set_target is pending (or already moving)", pending_first);

		bool saw_moving = false;
		f32 min_clearance = 1.0e9f;
		f32 first_remaining = -1.0f, max_speed_seen = 0.0f;
		int arrive_step = -1;
		for (int i = 0; i < 60 * 20; ++i)
		{
			navigation::crowd_update(k_dt);
			navigation::agent_get_state(agent, &st);
			if (st.move_state == navigation::move_moving) saw_moving = true;
			if (first_remaining < 0.0f && st.remaining_distance > 0.0f) first_remaining = st.remaining_distance;
			min_clearance = std::min(min_clearance, wall_distance(st.position[0], st.position[2]));
			max_speed_seen = std::max(max_speed_seen, std::sqrt(st.velocity[0] * st.velocity[0] + st.velocity[2] * st.velocity[2]));
			if (arrive_step < 0 && dist(st.position, goal) < 0.3f) arrive_step = i;
		}
		std::printf("[info] crowd agent: arrived after %.2f s, first remaining %.2f m, top speed %.2f m/s, wall clearance %.3f m, final (%.2f, %.2f, %.2f)\n",
			arrive_step / 60.0f, first_remaining, max_speed_seen, min_clearance, st.position[0], st.position[1], st.position[2]);
		check("agent follows a valid path (moving state seen)", saw_moving);
		check("remaining distance ~ the path length (detour around the wall)", first_remaining > 14.0f && first_remaining < 20.0f);
		check("agent reaches the goal", arrive_step >= 0 && dist(st.position, goal) < 0.3f);
		check("agent keeps its radius from the wall (minus the simplification tolerance)", min_clearance > k_radius - 0.27f);
		check("agent respects its max speed", max_speed_seen <= 3.5f + 0.05f && max_speed_seen > 2.5f);
		check("arrival takes ~ path length / speed", arrive_step > 60 * 4 && arrive_step < 60 * 9);
		check("remaining distance ~ 0 at the goal", st.remaining_distance >= 0.0f && st.remaining_distance < 0.3f);

		std::vector<f32> corners(32 * 3);
		check("reset_target stops the agent", navigation::agent_reset_target(agent) && navigation::agent_get_state(agent, &st)
			&& st.move_state == navigation::move_idle && navigation::agent_get_corners(agent, corners.data(), 32) == 0);

		// (b) teleport keeps the handle, clears the path
		const f32 tp[3] = { -10, 0, -10 };
		check("teleport", navigation::agent_teleport(agent, tp) && navigation::agent_get_state(agent, &st)
			&& dist(st.position, tp) < 0.05f && st.move_state == navigation::move_idle);
		const f32 moved[3] = { -9.5f, 0, -10 };
		check("move_position nudges along the surface", navigation::agent_move_position(agent, moved) && navigation::agent_get_state(agent, &st)
			&& dist(st.position, moved) < 0.05f);
		const f32 onto_ramp_goal[3] = { 10, 2, 10 };
		navigation::agent_set_target(agent, onto_ramp_goal);
		for (int i = 0; i < 30; ++i) navigation::crowd_update(k_dt);
		const s32 nc = navigation::agent_get_corners(agent, corners.data(), 32);
		check("agent_get_corners lists the remaining path", nc >= 1 && dist(&corners[(size_t)(nc - 1) * 3], onto_ramp_goal) < 0.4f);
		for (int i = 0; i < 60 * 25; ++i) navigation::crowd_update(k_dt);
		navigation::agent_get_state(agent, &st);
		std::printf("[info] ramp walk: final (%.2f, %.2f, %.2f)\n", st.position[0], st.position[1], st.position[2]);
		check("agent walks up the ramp onto the platform", dist(st.position, onto_ramp_goal) < 0.5f);

		// (c) unreachable target: partial, ends at the closest reachable point
		const f32 high[3] = { -10, 3, 10 };
		check("unreachable target accepted", navigation::agent_set_target(agent, high));
		for (int i = 0; i < 60 * 30; ++i) navigation::crowd_update(k_dt);
		navigation::agent_get_state(agent, &st);
		std::printf("[info] unreachable: partial=%d state=%d final (%.2f, %.2f, %.2f)\n", st.partial, st.move_state, st.position[0], st.position[1], st.position[2]);
		check("unreachable target: partial flag, stays on the floor next to the platform", st.partial == 1 && st.position[1] < 0.5f
			&& std::fabs(st.position[0] + 10.0f) < 3.0f && std::fabs(st.position[2] - 10.0f) < 3.0f);
		navigation::agent_remove(agent);
		check("removed agent handle is stale", !navigation::agent_valid(agent) && navigation::agent_count() == 0
			&& !navigation::agent_get_state(agent, &st));

		// (d) two agents head-on through the same lane: local avoidance keeps them apart, both arrive.
		const f32 a0[3] = { -6, 0, -9 }, a1[3] = { 6, 0, -9 };
		const u32 A = navigation::agent_add(a0, &p);
		const u32 B = navigation::agent_add(a1, &p);
		navigation::agent_set_target(A, a1);
		navigation::agent_set_target(B, a0);
		f32 min_sep = 100.0f;
		navigation::agent_state sa{}, sb{};
		for (int i = 0; i < 60 * 15; ++i)
		{
			navigation::crowd_update(k_dt);
			navigation::agent_get_state(A, &sa);
			navigation::agent_get_state(B, &sb);
			const f32 dx = sa.position[0] - sb.position[0], dz = sa.position[2] - sb.position[2];
			min_sep = std::min(min_sep, std::sqrt(dx * dx + dz * dz));
		}
		std::printf("[info] head-on pair: min separation %.3f m (radii sum %.2f), A (%.2f, %.2f) B (%.2f, %.2f)\n",
			min_sep, 2 * k_radius, sa.position[0], sa.position[2], sb.position[0], sb.position[2]);
		check("head-on agents never interpenetrate (min separation > 0.9 x radii)", min_sep > 0.9f * 2.0f * k_radius);
		check("both head-on agents arrive", dist(sa.position, a1) < 0.5f && dist(sb.position, a0) < 0.5f);

		// (e) crowd_clear + velocity control
		navigation::crowd_clear();
		check("crowd_clear removes every agent", navigation::agent_count() == 0 && !navigation::agent_valid(A) && !navigation::agent_valid(B));
		const u32 v = navigation::agent_add(a0, &p);
		const f32 vel[3] = { 2, 0, 0 };
		check("velocity control", navigation::agent_set_velocity(v, vel) && navigation::agent_get_state(v, &st) && st.move_state == navigation::move_velocity);
		for (int i = 0; i < 60; ++i) navigation::crowd_update(k_dt);
		navigation::agent_get_state(v, &st);
		check("velocity-driven agent moved ~2 m along +X", st.position[0] > a0[0] + 1.5f && st.position[0] < a0[0] + 2.3f);
		navigation::crowd_clear();

		// (f) load() drops agents and invalidates their handles
		const u32 w = navigation::agent_add(a0, &p);
		navigation::load(_blob.data(), (s32)_blob.size());
		check("reload drops agents", w != 0 && !navigation::agent_valid(w) && navigation::agent_count() == 0);
	}

	void run_error_tests()
	{
		navigation::bake_stats st{};
		check("bake without geometry fails", navigation::bake(nullptr, 0, nullptr, 0, nullptr, nullptr, 0, nullptr, nullptr, &st) == navigation::bake_error_no_geometry);
		navigation::bake_settings bad{};
		navigation::default_settings(&bad);
		bad.cell_size = 0.0f;
		const f32 box[10] = { 0, 0, 0, 1, 1, 1, 0, 0, 0, 1 };
		check("bake with bad settings fails", navigation::bake(nullptr, 0, nullptr, 0, nullptr, box, 1, nullptr, &bad, &st) == navigation::bake_error_bad_settings);
		// A single steep (60 deg) slab: geometry but nothing walkable.
		const f32 a = 60.0f * 3.14159265f / 180.0f;
		const f32 steep[10] = { 0, 0, 0, 3, 0.1f, 3, std::sin(a * 0.5f), 0, 0, std::cos(a * 0.5f) };
		navigation::bake_settings s{};
		navigation::default_settings(&s);
		check("bake of a too-steep slab reports 'nothing walkable'", navigation::bake(nullptr, 0, nullptr, 0, nullptr, steep, 1, nullptr, &s, &st) == navigation::bake_error_empty);
		const u32 no_agent = 12345;
		check("stale / unknown agent handles are rejected", !navigation::agent_valid(no_agent) && !navigation::agent_set_target(no_agent, box));
	}

	// ---- stub -----------------------------------------------------------------------------------------------------

	void run_stub_checks()
	{
		navigation::bake_stats st{};
		const f32 box[10] = { 0, 0, 0, 1, 1, 1, 0, 0, 0, 1 };
		check("stub: bake reports unavailable", navigation::bake(nullptr, 0, nullptr, 0, nullptr, box, 1, nullptr, nullptr, &st) == navigation::bake_error_unavailable);
		const u8 junk[64] = {};
		check("stub: load fails, nothing loaded", !navigation::load(junk, 64) && !navigation::loaded());
		f32 p[3]{};
		s32 status = 7;
		check("stub: queries return nothing", navigation::find_path(p, p, nullptr, p, 1, &status) == 0 && status == navigation::path_invalid
			&& !navigation::closest_point(p, nullptr, p) && !navigation::random_point(p));
		const navigation::agent_params ap{};
		check("stub: agents are never created", navigation::agent_add(p, &ap) == 0 && navigation::agent_count() == 0);
		navigation::crowd_update(k_dt);
		navigation::crowd_clear();
		check("stub: crowd update / clear are harmless", true);
	}

	void check(const char* what, bool ok)
	{
		std::cout << (ok ? "[PASS] " : "[FAIL] ") << what << "\n";
		ok ? ++_passed : ++_failed;
	}

	bool _available{ false };
	int _passed{ 0 };
	int _failed{ 0 };
};
