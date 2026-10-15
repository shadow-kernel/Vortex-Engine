#pragma once

#include "../Engine/Navigation/NavigationWorld.h"
#include "../Engine/Navigation/NavigationFormat.h"
#include "../Engine/Navigation/NavigationRebuild.h"

#include "Test.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <iostream>
#include <string>
#include <thread>
#include <vector>

#if VORTEX_HAS_RECAST
#include <DetourNavMesh.h>   // the Detour tile layout, to write the .vnav of an engine from before #517
#endif

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
				run_follow_tests();
				run_obstacle_tests();
				run_tall_level_tests();   // replaces the loaded navmesh
				run_legacy_file_tests();
				run_corrupt_file_tests();
				run_large_level_tests();
				run_failed_tile_report_tests();
				run_tile_grid_tests();
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
		check("a level that fits one heightfield column leaves no bake notes", bake_notes().empty());

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

	// ---- agents whose entity moves itself (#434: Update Position off) ----------------------------------------------------
	// The game moves the entity (here: by the agent's desired velocity, the way a script would); every frame the agent
	// follows it before the crowd step. It never fights the entity, keeps its path, avoids others, and re-plans after a jump.

	struct follower
	{
		u32 agent{ 0 };
		f32 pos[3]{};
		f32 vel[3]{};
	};

	// One frame of a follower: the agent follows the entity, the crowd steps, the "script" moves the entity by the desired
	// velocity. Returns agent_follow's answer (1 slid, 2 placed).
	static s32 follow_step(follower& f)
	{
		const s32 r = navigation::agent_follow(f.agent, f.pos, f.vel);
		return r;
	}

	static void move_by_desired(follower& f, bool move_entity)
	{
		navigation::agent_state st{};
		navigation::agent_get_state(f.agent, &st);
		for (int k = 0; k < 3; ++k) f.vel[k] = move_entity ? st.desired_velocity[k] : 0.0f;
		f.vel[1] = 0.0f;
		for (int k = 0; k < 3; ++k) f.pos[k] += f.vel[k] * k_dt;
		f.pos[1] = st.position[1];   // the "script" keeps its feet on the ground under it
	}

	void run_follow_tests()
	{
		const auto p = params();
		const f32 start[3] = { -5, 0, 0 }, goal[3] = { 5, 0, 0 };

		// (a) the entity stands still: the agent stays with it, its desired velocity leads along the path at full speed
		follower f{ navigation::agent_add(start, &p), { -5, 0, 0 }, {} };
		navigation::agent_set_target(f.agent, goal);
		navigation::agent_state st{};
		bool stayed = true;
		for (int i = 0; i < 30; ++i)
		{
			follow_step(f);
			navigation::crowd_update(k_dt);
			navigation::agent_get_state(f.agent, &st);
			move_by_desired(f, false);
		}
		navigation::agent_follow(f.agent, f.pos, f.vel);
		navigation::agent_get_state(f.agent, &st);
		stayed = dist(st.position, f.pos) < 0.05f;
		const f32 to_corner[3] = { st.next_corner[0] - f.pos[0], 0.0f, st.next_corner[2] - f.pos[2] };
		const f32 corner_len = std::sqrt(to_corner[0] * to_corner[0] + to_corner[2] * to_corner[2]);
		const f32 dv = std::sqrt(st.desired_velocity[0] * st.desired_velocity[0] + st.desired_velocity[2] * st.desired_velocity[2]);
		const f32 along = corner_len > 1e-4f && dv > 1e-4f ? (st.desired_velocity[0] * to_corner[0] + st.desired_velocity[2] * to_corner[2]) / (corner_len * dv) : 0.0f;
		std::printf("[info] follower standing still: agent at (%.2f, %.2f), desired velocity %.2f m/s, %.2f along the next corner, %d corners\n",
			st.position[0], st.position[2], dv, along, st.corner_count);
		check("follower: an entity that stands still keeps its agent with it", stayed);
		check("follower: its desired velocity leads to the next corner at full speed", along > 0.8f && dv > 3.0f && dv < 3.6f);

		// (b) the entity is moved by the desired velocity: it walks around the wall to the goal, the agent always slides
		bool always_slid = true;
		f32 clearance = 1.0e9f;
		int arrive = -1;
		for (int i = 0; i < 60 * 12 && arrive < 0; ++i)
		{
			always_slid = follow_step(f) == 1 && always_slid;
			navigation::crowd_update(k_dt);
			move_by_desired(f, true);
			clearance = std::min(clearance, wall_distance(f.pos[0], f.pos[2]));
			if (dist(f.pos, goal) < 0.4f) arrive = i;
		}
		std::printf("[info] follower walked by its desired velocity: arrived after %.2f s, wall clearance %.3f m, always slid %d\n",
			arrive / 60.0f, clearance, always_slid ? 1 : 0);
		check("follower: moved by its desired velocity the entity reaches the goal around the wall", arrive > 0);
		check("follower: … keeping its radius from the wall (minus the simplification tolerance)", clearance > k_radius - 0.27f);
		check("follower: … and the agent followed it along the navmesh every frame (never re-planned)", always_slid);

		// (c) the entity jumps through the wall: the agent is placed on the other side and keeps its target
		const f32 near_wall[3] = { -2, 0, 0 }, beyond[3] = { 2, 0, 0 };
		navigation::agent_teleport(f.agent, near_wall);
		navigation::agent_set_target(f.agent, goal);
		for (int i = 0; i < 10; ++i) navigation::crowd_update(k_dt);
		std::copy(beyond, beyond + 3, f.pos);
		const f32 still[3] = { 0, 0, 0 };
		const s32 jumped = navigation::agent_follow(f.agent, f.pos, still);
		for (int i = 0; i < 20; ++i) navigation::crowd_update(k_dt);
		navigation::agent_get_state(f.agent, &st);
		std::printf("[info] follower through the wall: follow %d, state %d, remaining %.2f m\n", jumped, st.move_state, st.remaining_distance);
		check("follower: an entity moved through the wall is followed by placing the agent there", jumped == 2);
		check("follower: … which keeps its target and re-plans from there (~3 m straight, not around the wall)",
			st.move_state == navigation::move_moving && st.remaining_distance > 2.0f && st.remaining_distance < 3.6f);

		// (d) an entity just off the navmesh (a capsule pressed against the wall, inside the eroded margin) is still a slide
		const f32 hugging[3] = { -0.6f, 0, 2 };
		const f32 approach[3] = { -1.5f, 0, 2 };
		navigation::agent_teleport(f.agent, approach);
		navigation::agent_set_target(f.agent, goal);
		for (int i = 0; i < 10; ++i) navigation::crowd_update(k_dt);
		const s32 hugged = navigation::agent_follow(f.agent, hugging, still);
		navigation::agent_get_state(f.agent, &st);
		check("follower: an entity against the wall, off the eroded navmesh, keeps its path (a slide, no re-plan)",
			hugged == 1 && st.move_state == navigation::move_moving);
		navigation::crowd_clear();

		// (e) two followers head-on: moved by their desired velocities they pass each other without touching
		follower a{ navigation::agent_add(start, &p), {}, {} }, b{};
		const f32 a0[3] = { -6, 0, -9 }, a1[3] = { 6, 0, -9 };
		navigation::agent_teleport(a.agent, a0);
		std::copy(a0, a0 + 3, a.pos);
		b.agent = navigation::agent_add(a1, &p);
		std::copy(a1, a1 + 3, b.pos);
		navigation::agent_set_target(a.agent, a1);
		navigation::agent_set_target(b.agent, a0);
		f32 min_sep = 100.0f;
		for (int i = 0; i < 60 * 15; ++i)
		{
			follow_step(a);
			follow_step(b);
			navigation::crowd_update(k_dt);
			move_by_desired(a, true);
			move_by_desired(b, true);
			const f32 dx = a.pos[0] - b.pos[0], dz = a.pos[2] - b.pos[2];
			min_sep = std::min(min_sep, std::sqrt(dx * dx + dz * dz));
		}
		std::printf("[info] head-on followers: min separation %.3f m, A (%.2f, %.2f) B (%.2f, %.2f)\n", min_sep, a.pos[0], a.pos[2], b.pos[0], b.pos[2]);
		check("followers head-on: their desired velocities avoid each other (min separation > 0.9 x radii)", min_sep > 0.9f * 2.0f * k_radius);
		check("followers head-on: both arrive", dist(a.pos, a1) < 0.6f && dist(b.pos, a0) < 0.6f);
		navigation::crowd_clear();
	}

	// ---- dynamic obstacles and runtime tile rebuilds (#435) ---------------------------------------------------------------
	// The test level's wall (x -0.5..0.5, z -6..6) has a gap at each end to the floor's edge (z 6..15 and -15..-6). A box in
	// the north gap closes it; paths and walking agents go south; removing it, or switching it off, opens the gap again.

	static f32 path_extreme_z(const f32* a, const f32* b, s32* status, bool max)
	{
		std::vector<f32> pts(256 * 3);
		*status = navigation::path_invalid;
		const s32 n = navigation::find_path(a, b, nullptr, pts.data(), 256, status);
		f32 z = max ? -1.0e9f : 1.0e9f;
		for (s32 i = 0; i < n; ++i) z = max ? std::max(z, pts[(size_t)i * 3 + 2]) : std::min(z, pts[(size_t)i * 3 + 2]);
		return n > 0 ? z : 0.0f;
	}

	// The north gap's blocker: 1 m thick, z 6.2 .. 14.8.
	static constexpr f32 k_gate_center[3] = { 0.0f, 1.0f, 10.5f };
	static constexpr f32 k_gate_half[3] = { 0.5f, 1.0f, 4.3f };

	static bool baked_tiles_match(const std::vector<u8>& blob, const navigation::rebuild::source& src, int& compared)
	{
		namespace format = navigation::format;
		format::file_header h{};
		std::memcpy(&h, blob.data(), sizeof(h));
		size_t off = sizeof(h);
		compared = 0;
		const std::vector<navigation::rebuild::carve_volume> none;
		for (s32 i = 0; i < h.tile_count; ++i)
		{
			format::tile_header th{};
			std::memcpy(&th, &blob[off], sizeof(th));
			off += sizeof(th);
			std::vector<u8> built;
			std::string error;
			if (!navigation::rebuild::build_tile(src, th.tx, th.tz, none, built, error)) return false;
			if (built.size() != (size_t)th.size || std::memcmp(built.data(), &blob[off], built.size()) != 0) return false;
			off += (size_t)format::padded(th.size);
			++compared;
		}
		return true;
	}

	void run_obstacle_tests()
	{
		navigation::load(_blob.data(), (s32)_blob.size());
		const s32 vcount = (s32)_verts.size() / 3, tcount = (s32)_tris.size() / 3, bcount = (s32)_boxes.size() / navigation::box_floats;
		const f32 west[3] = { -5, 0, 3 }, east[3] = { 5, 0, 3 };
		s32 status = 0;

		// (a) the bake's own pipeline: every tile rebuilt from the same geometry without obstacles is the baked tile
		navigation::format::file_header header{};
		std::memcpy(&header, _blob.data(), sizeof(header));
		const auto src = navigation::rebuild::make_source(header, _verts.data(), vcount, _tris.data(), tcount, nullptr, _boxes.data(), bcount, nullptr);
		int compared = 0;
		check("rebuild: a source made from the bake's geometry", src != nullptr);
		check("rebuild: every tile rebuilt without obstacles is the baked tile, byte for byte", src && baked_tiles_match(_blob, *src, compared) && compared >= 4);

		check("obstacles need a source: without one a carving obstacle is never ready and changes nothing", [&]
		{
			const u32 o = navigation::obstacle_add(navigation::obstacle_box, k_gate_center, k_gate_half, nullptr, navigation::obstacle_carve | navigation::obstacle_active);
			navigation::rebuild_flush(50.0f);
			const bool waiting = o != 0 && navigation::obstacle_valid(o) && !navigation::obstacle_ready(o) && navigation::obstacle_count() == 1
				&& path_extreme_z(west, east, &status, true) > 6.0f;
			navigation::obstacle_remove(o);
			return waiting && navigation::obstacle_count() == 0 && !navigation::obstacle_valid(o);
		}());
		check("rebuild_set_source takes the bake's geometry", navigation::rebuild_set_source(_verts.data(), vcount, _tris.data(), tcount, nullptr, _boxes.data(), bcount, nullptr)
			&& navigation::rebuild_has_source());
		const f32 north_before = path_extreme_z(west, east, &status, true);
		check("rebuild: from z 3 the path around the wall takes the north gap", status == navigation::path_complete && north_before > 6.0f);

		// (b) a box in the north gap: the path goes south once its tiles are rebuilt — measured from the add to the swap
		const auto t0 = std::chrono::steady_clock::now();
		const u32 gate = navigation::obstacle_add(navigation::obstacle_box, k_gate_center, k_gate_half, nullptr, navigation::obstacle_carve | navigation::obstacle_active);
		const bool flushed = navigation::rebuild_flush(2000.0f);
		const d64 latency = std::chrono::duration<d64, std::milli>(std::chrono::steady_clock::now() - t0).count();
		navigation::rebuild_stats st{};
		navigation::rebuild_get_stats(&st);
		const f32 south = path_extreme_z(west, east, &status, false);
		std::printf("[info] obstacle in the north gap: rebuilt %d tiles in %.1f ms (worker: %.2f ms the slowest tile; game thread: %.2f ms the slowest swap), path now via z %.2f\n",
			st.tiles_swapped, latency, st.build_ms_max, st.swap_ms_max, south);
		check("obstacle: added, rebuilt and ready well within 100 ms", gate != 0 && flushed && navigation::obstacle_ready(gate) && latency < 100.0);
		check("obstacle: the path now goes around the south end", status == navigation::path_complete && south < -6.0f
			&& path_extreme_z(west, east, &status, true) < 6.0f);
		const f32 in_gate[3] = { 0.0f, 0.0f, 10.5f };
		f32 near[3]{};
		check("obstacle: a point in the gap snaps out of the blocked polygons", navigation::closest_point(in_gate, nullptr, near)
			&& (std::fabs(near[0]) > 0.5f + k_radius - 0.3f || near[2] < 6.2f - k_radius + 0.3f));

		// (c) a door: switching the obstacle off and on flips its polygons at once — no tile is rebuilt
		const s32 built_before = st.tiles_built;
		navigation::obstacle_set(gate, nullptr, nullptr, nullptr, navigation::obstacle_carve, nullptr);   // open
		const f32 open_z = path_extreme_z(west, east, &status, true);
		const bool open_now = status == navigation::path_complete && open_z > 6.0f && navigation::rebuild_pending() == 0;
		navigation::obstacle_set(gate, nullptr, nullptr, nullptr, navigation::obstacle_carve | navigation::obstacle_active, nullptr);   // shut
		const f32 shut_z = path_extreme_z(west, east, &status, true);
		const bool shut_now = status == navigation::path_complete && shut_z < 6.0f && navigation::rebuild_pending() == 0;
		navigation::rebuild_get_stats(&st);
		check("door: opened (obstacle off) the north gap is free at once", open_now);
		check("door: shut again it is blocked at once", shut_now);
		check("door: neither needed a tile rebuild", st.tiles_built == built_before);

		// (d) an agent on its way through the north gap turns south when the gap shuts in front of it
		navigation::obstacle_set(gate, nullptr, nullptr, nullptr, navigation::obstacle_carve, nullptr);
		const auto p = params();
		const u32 walker = navigation::agent_add(west, &p);
		navigation::agent_set_target(walker, east);
		navigation::agent_state ws{};
		for (int i = 0; i < 40; ++i) navigation::crowd_update(k_dt);
		navigation::agent_get_state(walker, &ws);
		const f32 at_shut[3] = { ws.position[0], ws.position[1], ws.position[2] };
		navigation::obstacle_set(gate, nullptr, nullptr, nullptr, navigation::obstacle_carve | navigation::obstacle_active, nullptr);
		f32 max_z_after = -1.0e9f;
		int arrive = -1;
		for (int i = 0; i < 60 * 15 && arrive < 0; ++i)
		{
			navigation::rebuild_update(1.0f);
			navigation::crowd_update(k_dt);
			navigation::agent_get_state(walker, &ws);
			max_z_after = std::max(max_z_after, ws.position[2]);
			if (dist(ws.position, east) < 0.3f) arrive = i;
		}
		std::printf("[info] agent heading north when the gate shut at (%.2f, %.2f): highest z after %.2f, arrived after %.2f s\n",
			at_shut[0], at_shut[2], max_z_after, arrive / 60.0f);
		check("agent: walking to the north gap when it shuts, it turns around and arrives by the south end", at_shut[2] > 3.0f && arrive > 0 && max_z_after < 6.3f);
		navigation::crowd_clear();

		// (e) removing the obstacle reopens the gap (its tiles are rebuilt without it — the baked tiles again)
		navigation::obstacle_remove(gate);
		const bool removed = navigation::rebuild_flush(2000.0f);
		const f32 reopened = path_extreme_z(west, east, &status, true);
		check("obstacle removed: the north gap is open again", removed && status == navigation::path_complete && reopened > 6.0f);
		std::vector<u8> now((size_t)navigation::data_copy(nullptr, 0));
		check("obstacle removed: (the loaded blob is the bake's — rebuilds never touch it)", navigation::data_copy(now.data(), (s32)now.size()) == (s32)_blob.size() && now == _blob);

		// (f) a moving carving obstacle: re-carved every frame for a second, the worker drops what a newer pose supersedes,
		// the game thread only swaps — at the end the navmesh matches where it stopped
		navigation::rebuild_get_stats(&st);
		const s32 swaps0 = st.tiles_swapped;
		const u32 cart = navigation::obstacle_add(navigation::obstacle_box, k_gate_center, k_gate_half, nullptr, navigation::obstacle_carve | navigation::obstacle_active);
		d64 worst_frame = 0.0;
		for (int i = 0; i < 60; ++i)
		{
			const f32 c[3] = { -10.0f + 0.15f * (f32)i, 1.0f, 10.5f };
			const f32 cart_half[3] = { 1.0f, 1.0f, 1.0f };
			const auto f0 = std::chrono::steady_clock::now();
			navigation::obstacle_set(cart, c, cart_half, nullptr, navigation::obstacle_carve | navigation::obstacle_active, nullptr);
			navigation::rebuild_update(1.0f);
			worst_frame = std::max(worst_frame, std::chrono::duration<d64, std::milli>(std::chrono::steady_clock::now() - f0).count());
			std::this_thread::sleep_for(std::chrono::milliseconds(2));
		}
		navigation::rebuild_flush(2000.0f);
		navigation::rebuild_get_stats(&st);
		std::printf("[info] obstacle moved every frame for 60 frames: %d tiles swapped, %d builds superseded, worst game-thread frame %.2f ms\n",
			st.tiles_swapped - swaps0, st.tiles_superseded, worst_frame);
		const f32 under_cart[3] = { -1.15f, 0.0f, 10.5f }, beside_cart[3] = { -4.5f, 0.0f, 10.5f };
		const f32 ext[3] = { 0.1f, 1.0f, 0.1f };
		// (closest_point alone would not do: Detour finds the nearest polygon among those whose bounds overlap the box)
		auto on_navmesh = [&](const f32* q)
		{
			f32 r[3]{};
			return navigation::closest_point(q, ext, r) && std::fabs(r[0] - q[0]) < 0.05f && std::fabs(r[2] - q[2]) < 0.05f;
		};
		{
			char row[40]{};
			for (int k = 0; k <= 30; ++k)
			{
				const f32 q[3] = { -6.0f + 0.25f * (f32)k, 0.0f, 10.5f };
				row[k] = on_navmesh(q) ? '.' : '#';
			}
			std::printf("[info] the navmesh along z 10.5, x -6 .. 1.5 (# = carved): %s\n", row);
		}
		check("moving obstacle: the game thread never spent more than a few ms on it", worst_frame < 8.0);
		check("moving obstacle: where it stopped is blocked, where it passed is free", !on_navmesh(under_cart) && on_navmesh(beside_cart));
		navigation::obstacle_remove(cart);
		navigation::rebuild_flush(2000.0f);

		// (g) an avoiding obstacle (a parked car the navmesh ignores): an agent walking the lane steers around it
		const f32 lane0[3] = { -6, 0, -9 }, lane1[3] = { 6, 0, -9 };
		const f32 car_center[3] = { 0.0f, 0.75f, -9.0f }, car_half[3] = { 2.2f, 0.75f, 0.9f };
		const u32 car = navigation::obstacle_add(navigation::obstacle_box, car_center, car_half, nullptr, navigation::obstacle_avoid | navigation::obstacle_active);
		const u32 driver = navigation::agent_add(lane0, &p);
		navigation::agent_set_target(driver, lane1);
		f32 closest = 1.0e9f;
		bool inside = false;
		for (int i = 0; i < 60 * 10; ++i)
		{
			navigation::crowd_update(k_dt);
			navigation::agent_get_state(driver, &ws);
			const f32 dx = std::max(0.0f, std::fabs(ws.position[0] - car_center[0]) - car_half[0]);
			const f32 dz = std::max(0.0f, std::fabs(ws.position[2] - car_center[2]) - car_half[2]);
			closest = std::min(closest, std::sqrt(dx * dx + dz * dz));
			inside = inside || (dx == 0.0f && dz == 0.0f);
		}
		std::printf("[info] lane past an avoided car: closest %.2f m from its sides, final (%.2f, %.2f), %d agents counted\n",
			closest, ws.position[0], ws.position[2], navigation::agent_count());
		check("avoiding obstacle: the navmesh is untouched (no rebuild)", navigation::rebuild_pending() == 0 && navigation::obstacle_ready(car));
		check("avoiding obstacle: the agent steers around the car, never into it, and arrives", !inside && dist(ws.position, lane1) < 0.5f);
		check("avoiding obstacle: its circles are no agents", navigation::agent_count() == 1);
		navigation::obstacle_remove(car);
		navigation::crowd_clear();

		// (h) a re-bake over destruction: the geometry without the wall, the tiles over it rebuilt — paths go straight through
		std::vector<f32> boxes_without_wall(_boxes.begin() + navigation::box_floats, _boxes.end());   // (the wall is the first box)
		const f32 wall_min[3] = { -0.5f, 0.0f, -6.0f }, wall_max[3] = { 0.5f, 2.0f, 6.0f };
		const bool rebaked = navigation::rebuild_set_source(_verts.data(), vcount, _tris.data(), tcount, nullptr,
			boxes_without_wall.data(), (s32)boxes_without_wall.size() / navigation::box_floats, nullptr)
			&& navigation::rebuild_tiles(wall_min, wall_max) && navigation::rebuild_flush(2000.0f);
		std::vector<f32> pts(64 * 3);
		const f32 w0[3] = { -5, 0, 0 }, e0[3] = { 5, 0, 0 };
		const s32 straight = navigation::find_path(w0, e0, nullptr, pts.data(), 64, &status);
		check("rebake: with the wall gone from the geometry the path goes straight through", rebaked && status == navigation::path_complete && straight == 2);
		navigation::load(_blob.data(), (s32)_blob.size());   // back to the bake for whatever runs next
		check("load drops the source and the obstacles", !navigation::rebuild_has_source() && navigation::obstacle_count() == 0);
	}

	// ---- tall levels (#451) ---------------------------------------------------------------------------------------

	static std::string bake_notes()
	{
		std::string s((size_t)std::max(0, navigation::bake_notes_copy(nullptr, 0)) + 1, '\0');
		const s32 n = navigation::bake_notes_copy(s.data(), (s32)s.size());
		s.resize((size_t)std::max(0, n));
		return s;
	}

	// Bakes boxes (+ optional triangles) and loads the result; the blob size, or <= 0.
	static s32 bake_and_load(const std::vector<f32>& verts, const std::vector<s32>& tris, const std::vector<f32>& boxes,
		const navigation::bake_settings& s, navigation::bake_stats& st)
	{
		const s32 size = navigation::bake(verts.empty() ? nullptr : verts.data(), (s32)verts.size() / 3, tris.empty() ? nullptr : tris.data(),
			(s32)tris.size() / 3, nullptr, boxes.empty() ? nullptr : boxes.data(), (s32)boxes.size() / navigation::box_floats, nullptr, &s, &st);
		if (size <= 0) return size;
		std::vector<u8> blob((size_t)size);
		navigation::bake_result_copy(blob.data(), size);
		return navigation::load(blob.data(), size) ? size : -100;
	}

	static bool on_navmesh_at(f32 x, f32 y, f32 z, f32 tolerance)
	{
		const f32 q[3] = { x, y, z };
		f32 p[3]{};
		return navigation::closest_point(q, nullptr, p) && std::fabs(p[1] - y) < tolerance && std::fabs(p[0] - x) < 0.2f && std::fabs(p[2] - z) < 0.2f;
	}

	static s32 path_status(f32 ax, f32 ay, f32 az, f32 bx, f32 by, f32 bz, f32* end_y = nullptr)
	{
		std::vector<f32> pts(256 * 3);
		const f32 a[3] = { ax, ay, az }, b[3] = { bx, by, bz };
		s32 status = -1;
		const s32 n = navigation::find_path(a, b, nullptr, pts.data(), 256, &status);
		if (end_y) *end_y = n > 0 ? pts[(size_t)(n - 1) * 3 + 1] : -1.0e9f;
		return n > 0 ? status : navigation::path_invalid;
	}

	// One heightfield column holds RC_SPAN_MAX_HEIGHT (8191) cells of the cell height — 819 m at the default 0.1 m. The
	// bake used to drop (squash onto the top cell) everything higher above the lowest geometry and still report success.
	void run_tall_level_tests()
	{
		navigation::bake_settings s{};
		navigation::default_settings(&s);   // cell 0.2, cell height 0.1, tiles of 48 cells
		std::vector<f32> boxes;
		auto box = [&](f32 cx, f32 cy, f32 cz, f32 hx, f32 hy, f32 hz) { boxes.insert(boxes.end(), { cx, cy, cz, hx, hy, hz, 0, 0, 0, 1 }); };
		navigation::bake_stats st{};

		// (a) the report's level: a floor at y = 0 and one 1000 m higher, 100 m to the side.
		box(0.0f, -0.2f, 0.0f, 10.0f, 0.2f, 10.0f);
		box(100.0f, 999.8f, 0.0f, 10.0f, 0.2f, 10.0f);
		s32 size = bake_and_load({}, {}, boxes, s, st);
		std::printf("[info] floors at 0 and 1000 m: %d bytes, %d polys in %d tiles; notes: \"%s\"\n", size, st.poly_count, st.tile_count, bake_notes().c_str());
		check("tall level (floors at y 0 and 1000): bake succeeds", size > 0);
		check("tall level: the upper floor has navmesh at its own height", on_navmesh_at(100.0f, 1000.0f, 0.0f, 0.3f));
		check("tall level: the lower floor keeps its navmesh", on_navmesh_at(0.0f, 0.0f, 0.0f, 0.3f));
		check("tall level: a path across the upper floor", path_status(95.0f, 1000.0f, -5.0f, 105.0f, 1000.0f, 5.0f) == navigation::path_complete);
		check("tall level: nothing lost, no bake notes (each tile fits its own height window)", bake_notes().empty());

		// (b) a tile that is itself taller than a column: a 4 x 4 m pillar from the floor up to y = 1500. That tile is
		// built with a coarser cell height and the bake says so; the pillar top and the floor around it keep their navmesh.
		boxes.clear();
		box(0.0f, -0.2f, 0.0f, 10.0f, 0.2f, 10.0f);
		box(3.0f, 750.0f, 3.0f, 2.0f, 750.0f, 2.0f);
		size = bake_and_load({}, {}, boxes, s, st);
		const std::string notes = bake_notes();
		std::printf("[info] 1500 m pillar: %d bytes, %d polys in %d tiles; notes: \"%s\"\n", size, st.poly_count, st.tile_count, notes.c_str());
		check("1500 m pillar: bake succeeds", size > 0);
		check("1500 m pillar: its top has navmesh", on_navmesh_at(3.0f, 1500.0f, 3.0f, 0.4f));
		check("1500 m pillar: the floor beside it (same tile) and further away keeps its navmesh",
			on_navmesh_at(7.5f, 0.0f, 7.5f, 0.3f) && on_navmesh_at(-6.0f, 0.0f, -6.0f, 0.3f));
		check("1500 m pillar: a path past it through the coarser tile", path_status(-8.0f, 0.0f, -8.0f, 8.0f, 0.0f, 8.0f) == navigation::path_complete);
		check("1500 m pillar: the bake notes name the coarser cell height", notes.find("coarser Cell Height") != std::string::npos);

		// (c) the regular test level plus a platform 1200 m up and away from it: every tile now gets its own height window,
		// and the level's paths stay what they were.
		std::vector<f32> level_boxes = _boxes;
		level_boxes.insert(level_boxes.end(), { 40.0f, 1200.0f, 40.0f, 3.0f, 0.5f, 3.0f, 0, 0, 0, 1 });
		size = bake_and_load(_verts, _tris, level_boxes, _settings, st);
		std::printf("[info] test level + platform at 1200 m: %d bytes, %d polys in %d tiles\n", size, st.poly_count, st.tile_count);
		check("windowed test level: bake succeeds", size > 0);
		f32 end_y = 0.0f;
		check("windowed test level: path around the wall", path_status(-5.0f, 0.0f, 0.0f, 5.0f, 0.0f, 0.0f) == navigation::path_complete);
		check("windowed test level: path up the ramp onto the platform", path_status(0.0f, 0.0f, -10.0f, 10.0f, 2.0f, 10.0f, &end_y) == navigation::path_complete
			&& std::fabs(end_y - 2.0f) < 0.35f);
		check("windowed test level: the high platform has navmesh", on_navmesh_at(40.0f, 1200.5f, 40.0f, 0.3f));
	}

	// ---- 64-bit poly refs, large levels, failed tiles (#517) ----------------------------------------------------------

	static navigation::format::file_header header_of(const std::vector<u8>& blob)
	{
		navigation::format::file_header h{};
		if (blob.size() >= sizeof(h)) std::memcpy(&h, blob.data(), sizeof(h));
		return h;
	}

	// Before #517 the 32-bit poly ref split 22 bits between the tile index — the tile count of the whole bounds grid,
	// rounded up to a power of two, at most 14 bits — and the polygon index: the polygons one tile could hold.
	static int old_tile_bits(s32 grid_tiles)
	{
		int bits = 0;
		while ((1 << bits) < grid_tiles) ++bits;
		return std::min(bits, 14);
	}
	static s32 old_polys_per_tile(s32 grid_tiles) { return 1 << (22 - old_tile_bits(grid_tiles)); }

	// Bakes and copies the blob out (nothing is loaded). The blob size, or the bake error.
	static s32 bake_blob(const std::vector<f32>& verts, const std::vector<s32>& tris, const std::vector<f32>& boxes,
		const navigation::bake_settings& s, navigation::bake_stats& st, std::vector<u8>& blob)
	{
		const s32 size = navigation::bake(verts.empty() ? nullptr : verts.data(), (s32)verts.size() / 3, tris.empty() ? nullptr : tris.data(),
			(s32)tris.size() / 3, nullptr, boxes.empty() ? nullptr : boxes.data(), (s32)boxes.size() / navigation::box_floats, nullptr, &s, &st);
		blob.clear();
		if (size > 0)
		{
			blob.resize((size_t)size);
			navigation::bake_result_copy(blob.data(), size);
		}
		return size;
	}

	static size_t count_of(const std::string& text, const char* what)
	{
		size_t n = 0;
		for (size_t at = text.find(what); at != std::string::npos; at = text.find(what, at + 1)) ++n;
		return n;
	}

#if VORTEX_HAS_RECAST
	// The .vnav an engine from before #517 wrote for the same navmesh: Detour built with 32-bit poly refs reserves a
	// 12-byte dtLink per link in every tile (zeroed, as dtCreateNavMeshData leaves it: Detour builds the links on load)
	// where this build reserves sizeof(dtLink); version 1; max_tiles / max_polys the old split of the ref's 22 bits.
	static std::vector<u8> as_version1(const std::vector<u8>& blob)
	{
		namespace format = navigation::format;
		auto a4 = [](size_t n) { return (n + 3) & ~(size_t)3; };
		format::file_header h = header_of(blob);
		std::vector<u8> out(sizeof(h), 0);
		size_t off = sizeof(h);
		for (s32 i = 0; i < h.tile_count; ++i)
		{
			format::tile_header th{};
			std::memcpy(&th, &blob[off], sizeof(th));
			off += sizeof(th);
			const u8* src = &blob[off];
			dtMeshHeader mh{};
			std::memcpy(&mh, src, sizeof(mh));
			const size_t links_at = a4(sizeof(dtMeshHeader)) + a4(sizeof(f32) * 3 * (size_t)mh.vertCount) + a4(sizeof(dtPoly) * (size_t)mh.polyCount);
			const size_t links_now = a4(sizeof(dtLink) * (size_t)mh.maxLinkCount), links_ref32 = a4(12 * (size_t)mh.maxLinkCount);
			std::vector<u8> tile(src, src + links_at);
			tile.resize(links_at + links_ref32, 0);
			tile.insert(tile.end(), src + links_at + links_now, src + th.size);
			off += (size_t)format::padded(th.size);
			th.size = (s32)tile.size();
			const u8* t = reinterpret_cast<const u8*>(&th);
			out.insert(out.end(), t, t + sizeof(th));
			out.insert(out.end(), tile.begin(), tile.end());
			out.resize(out.size() + (size_t)(format::padded(th.size) - th.size), 0);
		}
		const s32 grid = h.stats.tiles_x * h.stats.tiles_z;
		h.version = format::version_ref32;
		h.max_tiles = 1 << old_tile_bits(grid);
		h.max_polys = old_polys_per_tile(grid);
		h.stats.data_size = (s32)out.size();
		std::memcpy(out.data(), &h, sizeof(h));
		return out;
	}
#endif

	// Navmeshes baked before the switch to 64-bit poly refs — .vnav version 1, the templates ship them — still load
	// (their tiles are re-laid out); a tile of the other ref width under either version number is refused, not misread.
	void run_legacy_file_tests()
	{
#if VORTEX_HAS_RECAST
		const auto h2 = header_of(_blob);
		check("a bake writes .vnav version 2 with 64-bit poly refs", h2.version == navigation::data_version && navigation::data_version == 2 && sizeof(dtPolyRef) == 8);
		check("version 2 sizes the tile pool by the tiles it stores", h2.max_tiles == h2.tile_count && h2.max_polys > 0);
		std::vector<f32> pts(256 * 3);
		s32 status = 0;
		const f32 a[3] = { -5, 0, 0 }, b[3] = { 5, 0, 0 };
		navigation::load(_blob.data(), (s32)_blob.size());
		const s32 n2 = navigation::find_path(a, b, nullptr, pts.data(), 256, &status);
		const std::vector<f32> path2(pts.begin(), pts.begin() + std::max(0, n2) * 3);

		const std::vector<u8> v1 = as_version1(_blob);
		std::printf("[info] the test level as .vnav version 1: %zu bytes, as version 2: %zu bytes\n", v1.size(), _blob.size());
		navigation::bake_stats st{};
		check("a version 1 .vnav (32-bit poly refs) still loads", navigation::load(v1.data(), (s32)v1.size())
			&& navigation::get_info(nullptr, &st) && st.poly_count == h2.stats.poly_count && st.tile_count == h2.tile_count);
		const s32 n1 = navigation::find_path(a, b, nullptr, pts.data(), 256, &status);
		check("the version 1 navmesh finds the same path", n1 == n2 && n1 >= 3 && status == navigation::path_complete
			&& std::equal(path2.begin(), path2.end(), pts.begin()));
		std::vector<u8> copy((size_t)navigation::data_copy(nullptr, 0));
		navigation::data_copy(copy.data(), (s32)copy.size());
		check("data_copy hands back the version 1 file as loaded", copy == v1);
		const auto p = params();
		const u32 agent = navigation::agent_add(a, &p);
		navigation::agent_set_target(agent, b);
		for (int i = 0; i < 60 * 10; ++i) navigation::crowd_update(k_dt);
		navigation::agent_state ast{};
		check("an agent walks around the wall on the version 1 navmesh", navigation::agent_get_state(agent, &ast) && dist(ast.position, b) < 0.3f);
		navigation::crowd_clear();

		std::vector<u8> bad = v1;
		s32 version = navigation::data_version;
		std::memcpy(bad.data() + 4, &version, sizeof(version));
		check("32-bit tile data marked version 2 is refused, not misread", !navigation::load(bad.data(), (s32)bad.size()) && !navigation::loaded());
		bad = _blob;
		version = navigation::format::version_ref32;
		std::memcpy(bad.data() + 4, &version, sizeof(version));
		check("64-bit tile data marked version 1 is refused, not misread", !navigation::load(bad.data(), (s32)bad.size()) && !navigation::loaded());
		version = navigation::data_version + 1;
		std::memcpy(bad.data() + 4, &version, sizeof(version));
		check("an unknown .vnav version is refused", !navigation::load(bad.data(), (s32)bad.size()) && !navigation::loaded());
#endif
	}

	// A corrupt header sizes nothing: tile_count sizes the tile pool, so it is held to what the file can hold — one flipped
	// bit (25 -> 16,777,241) used to allocate gigabytes before the tile table was read — and max_tiles sizes nothing (a
	// flipped bit there crashed Detour on main too). Tiles whose indices point outside them are refused, not read.
	void run_corrupt_file_tests()
	{
#if VORTEX_HAS_RECAST
		namespace format = navigation::format;
		auto with = [](std::vector<u8> blob, size_t at, auto value) { std::memcpy(&blob[at], &value, sizeof(value)); return blob; };
		auto loads = [](const std::vector<u8>& blob) { return navigation::load(blob.data(), (s32)blob.size()) && navigation::loaded(); };
		const auto h = header_of(_blob);
		const size_t tile_count_at = offsetof(format::file_header, tile_count), max_tiles_at = offsetof(format::file_header, max_tiles);

		const auto t0 = std::chrono::steady_clock::now();
		const bool flipped = loads(with(_blob, tile_count_at, (s32)(h.tile_count | (1 << 24))));
		const d64 ms = std::chrono::duration<d64, std::milli>(std::chrono::steady_clock::now() - t0).count();
		std::printf("[info] tile_count %d -> %d: refused in %.2f ms\n", h.tile_count, h.tile_count | (1 << 24), ms);
		check("a tile_count with a flipped high bit is refused at once, nothing sized by it", !flipped && ms < 100.0);
		check("version 2: a max_tiles other than tile_count is refused", !loads(with(_blob, max_tiles_at, (s32)(h.tile_count + 1))));
		const std::vector<u8> v1 = as_version1(_blob);
		check("version 1: max_tiles sizes nothing, a flipped high bit there still loads",
			loads(with(v1, max_tiles_at, (s32)(header_of(v1).max_tiles | (1 << 25)))));

		// the first polygon of the first tile: a vertex index past the tile's vertices, then a neighbour past its polygons
		dtMeshHeader mh{};
		const size_t tile_at = sizeof(format::file_header) + sizeof(format::tile_header);
		std::memcpy(&mh, &_blob[tile_at], sizeof(mh));
		const size_t poly_at = tile_at + ((sizeof(dtMeshHeader) + 3) & ~(size_t)3) + ((sizeof(f32) * 3 * (size_t)mh.vertCount + 3) & ~(size_t)3);
		check("a polygon whose vertex index points past its tile is refused",
			!loads(with(_blob, poly_at + offsetof(dtPoly, verts), (unsigned short)mh.vertCount)));
		check("a polygon whose neighbour index points past its tile is refused",
			!loads(with(_blob, poly_at + offsetof(dtPoly, neis), (unsigned short)(mh.polyCount + 1))));
		// the first detail triangle (the first polygon's) and the first BV tree node, past what they may name
		auto a4 = [](size_t n) { return (n + 3) & ~(size_t)3; };
		const size_t details_at = poly_at + a4(sizeof(dtPoly) * (size_t)mh.polyCount) + a4(sizeof(dtLink) * (size_t)mh.maxLinkCount);
		const size_t tris_at = details_at + a4(sizeof(dtPolyDetail) * (size_t)mh.detailMeshCount) + a4(sizeof(f32) * 3 * (size_t)mh.detailVertCount);
		const size_t bv_at = tris_at + a4(4 * (size_t)mh.detailTriCount);
		check("a detail triangle whose vertex is past its detail mesh is refused", mh.detailTriCount > 0 && !loads(with(_blob, tris_at, (u8)250)));
		check("a BV tree leaf that names a polygon past the tile is refused",
			mh.bvNodeCount > 0 && !loads(with(_blob, bv_at + offsetof(dtBVNode, i), (int)mh.detailMeshCount)));
		check("the untouched file still loads", loads(_blob));
#endif
	}

	// Paddi's level in #517: bake bounds of 1,550 x 16,050 m at Cell Size 0.15 and Tile Size 512 — 76.8 m tiles, a grid of
	// 21 x 209 = 4,389. The 32-bit poly ref left each of its tiles 512 polygons (1,024 when the bounds were narrowed under
	// 4,096 tiles) and dropped every denser tile. Here a forest clearing — 1,600 tree trunks on 72 x 72 m, inside one
	// tile — needs a few thousand; a valley floor and a second region 15.8 km away make the grid real.
	void run_large_level_tests()
	{
		navigation::bake_settings s{};
		navigation::default_settings(&s);
		s.cell_size = 0.15f;
		s.cell_height = 0.25f;
		s.tile_size = 512;
		const f32 bmin[3] = { 500.0f, -70.0f, -15800.0f }, bmax[3] = { 2050.0f, 1900.0f, 250.0f };
		std::copy(bmin, bmin + 3, s.bounds_min);
		std::copy(bmax, bmax + 3, s.bounds_max);
		const f32 tile = s.cell_size * (f32)s.tile_size;

		std::vector<f32> boxes;
		auto box = [&](f32 cx, f32 cy, f32 cz, f32 hx, f32 hy, f32 hz) { boxes.insert(boxes.end(), { cx, cy, cz, hx, hy, hz, 0, 0, 0, 1 }); };
		const f32 cx = bmin[0] + 5.5f * tile, cz = bmin[2] + 100.5f * tile;   // the middle of tile (5, 100)
		box(cx, -0.5f, cz, 36.0f, 0.5f, 36.0f);                               // the clearing, clear of the neighbours' borders
		for (int i = 0; i < 40; ++i)
			for (int k = 0; k < 40; ++k)
				box(cx - 35.1f + 1.8f * (f32)i, 1.5f, cz - 35.1f + 1.8f * (f32)k, 0.25f, 1.5f, 0.25f);   // trunks, 1.3 m apart
		box(1200.0f, 299.5f, -12000.0f, 30.0f, 0.5f, 30.0f);                 // a valley floor
		box(1900.0f, 1199.5f, 100.0f, 20.0f, 0.5f, 20.0f);                   // the other region: 15.8 km away, 1.2 km up

		std::vector<u8> blob;
		navigation::bake_stats st{};
		const s32 size = bake_blob({}, {}, boxes, s, st, blob);
		const auto h = header_of(blob);
		const s32 grid = st.tiles_x * st.tiles_z;
		const std::string notes = bake_notes();
		std::printf("[info] 1,550 x 16,050 m level: %d bytes in %.0f ms, %d x %d = %d tiles (%d with navmesh), %d polygons, the densest tile %d"
			" (the 32-bit poly ref allowed %d); notes: \"%s\"\n", size, st.bake_ms, st.tiles_x, st.tiles_z, grid, st.tile_count, st.poly_count,
			h.max_polys, old_polys_per_tile(grid), notes.c_str());
		check("large level: bakes", size > 0);
		check("large level: Paddi's grid (21 x 209 tiles of 76.8 m)", st.tiles_x == 21 && st.tiles_z == 209);
		check("large level: no tile failed, no notes", notes.empty());
		check("large level: the forest tile holds more polygons than the 32-bit poly ref allowed this grid (dropped before #517)",
			h.max_polys > old_polys_per_tile(grid) && h.max_polys > 1024);
		check("large level: loads", size > 0 && navigation::load(blob.data(), size));
		check("large level: navmesh between the trunks, on the valley floor and in the far region", on_navmesh_at(cx - 34.2f, 0.0f, cz - 34.2f, 0.3f)
			&& on_navmesh_at(1200.0f, 300.0f, -12000.0f, 0.3f) && on_navmesh_at(1900.0f, 1200.0f, 100.0f, 0.3f));
		check("large level: a path through the forest", path_status(cx - 34.2f, 0.0f, cz - 34.2f, cx + 34.2f, 0.0f, cz + 34.2f) == navigation::path_complete);
	}

	// A bake reports every failed tile: how many, then per reason the count and the world-space rectangles of the first
	// tiles (the rest counted) — not the first tile only. Platforms of 2 x 2 cells, one cell apart, are a region each
	// with 4 contour vertices: 40 m tiles of them need ~70,000 vertices, more than the 65,533 a tile can hold. The
	// 4 x 3 tiles of the field fail; the floor in the fifth tile column bakes.
	void run_failed_tile_report_tests()
	{
		navigation::bake_settings s{};
		navigation::default_settings(&s);
		s.cell_size = 0.1f;
		s.tile_size = 400;
		s.agent_radius = 0.0f;        // no erosion: the platforms keep their 2 x 2 cells
		s.region_min_size = 0.0f;     // and every one stays a region of its own
		s.region_merge_size = 0.0f;
		s.partition = navigation::partition_monotone;   // watershed seeds no region where every cell is an edge cell
		s.filter_flags = 0;           // the ledge filter would take their edge cells
		const f32 bmin[3] = { 0.0f, -1.0f, 0.0f }, bmax[3] = { 200.0f, 2.0f, 120.0f };
		std::copy(bmin, bmin + 3, s.bounds_min);
		std::copy(bmax, bmax + 3, s.bounds_max);

		std::vector<f32> verts;
		std::vector<s32> tris;
		auto quad = [&](f32 x0, f32 z0, f32 x1, f32 z1)   // walkable side up: (v1 - v0) x (v2 - v0) points +y
		{
			const s32 base = (s32)verts.size() / 3;
			verts.insert(verts.end(), { x0, 0.0f, z0, x0, 0.0f, z1, x1, 0.0f, z1, x1, 0.0f, z0 });
			tris.insert(tris.end(), { base, base + 1, base + 2, base, base + 2, base + 3 });
		};
		for (int i = 0; i < 533; ++i)
			for (int k = 0; k < 400; ++k)
				quad(0.3f * (f32)i + 0.01f, 0.3f * (f32)k + 0.01f, 0.3f * (f32)i + 0.19f, 0.3f * (f32)k + 0.19f);
		quad(161.0f, 1.0f, 199.0f, 119.0f);

		std::vector<u8> blob;
		navigation::bake_stats st{};
		const s32 size = bake_blob(verts, tris, {}, s, st, blob);
		const std::string notes = bake_notes();
		std::printf("[info] platform field: %d bytes in %.0f ms, %d x %d tiles (%d with navmesh); notes:\n%s\n", size, st.bake_ms,
			st.tiles_x, st.tiles_z, st.tile_count, notes.c_str());
		check("failed tiles: the rest of the level still bakes", size > 0 && navigation::load(blob.data(), size) && on_navmesh_at(180.0f, 0.0f, 60.0f, 0.3f));
		check("failed tiles: how many", notes.find("12 tile(s) failed to build and have no navmesh.") != std::string::npos);
		check("failed tiles: the count per reason names the real limit",
			notes.find("12 tile(s) - too many vertices in one tile (at most 65533 fit;") != std::string::npos);
		check("failed tiles: each listed with its world-space rectangle", notes.find("tile (0, 0) at x 0.0..40.0, z 0.0..40.0 (") != std::string::npos
			&& notes.find("tile (1, 2) at x 40.0..80.0, z 80.0..120.0 (") != std::string::npos);
		check("failed tiles: the first 10 listed, the rest counted", count_of(notes, ") at x ") == 10 && notes.find("; ... and 2 more.") != std::string::npos);
		check("failed tiles: the remedies lower what one tile holds (a coarser Cell Size alone makes the tile bigger)",
			notes.find("lower the Tile Size or raise the Max Edge Error; raise the Cell Size only together with a lower Tile Size") != std::string::npos
			&& notes.find("(lower the tile size)") == std::string::npos);
	}

	// The 16,384 tiles a bake used to allow came from the 32-bit poly ref as well. Paddi's next try in #517 — Tile Size
	// 256 on the same bounds, 41 x 418 = 17,138 tiles — was refused outright; the grid now only bounds the bake's own
	// bookkeeping (max_bake_tiles).
	void run_tile_grid_tests()
	{
		navigation::bake_settings s{};
		navigation::default_settings(&s);
		s.cell_size = 0.15f;
		s.cell_height = 0.25f;
		s.tile_size = 256;
		const f32 bmin[3] = { 500.0f, -70.0f, -15800.0f }, bmax[3] = { 2050.0f, 1900.0f, 250.0f };
		std::copy(bmin, bmin + 3, s.bounds_min);
		std::copy(bmax, bmax + 3, s.bounds_max);
		const std::vector<f32> floor_box = { 1200.0f, 299.5f, -12000.0f, 30.0f, 0.5f, 30.0f, 0, 0, 0, 1 };
		std::vector<u8> blob;
		navigation::bake_stats st{};
		const s32 size = bake_blob({}, {}, floor_box, s, st, blob);
		std::printf("[info] Tile Size 256 over the same bounds: %d bytes, %d x %d tiles, %.0f ms\n", size, st.tiles_x, st.tiles_z, st.bake_ms);
		check("17,138 tiles (more than the old 16,384) bake", size > 0 && st.tiles_x == 41 && st.tiles_z == 418);

		const f32 huge_min[3] = { 0.0f, 0.0f, 0.0f }, huge_max[3] = { 100000.0f, 10.0f, 100000.0f };   // 100 x 100 km
		std::copy(huge_min, huge_min + 3, s.bounds_min);
		std::copy(huge_max, huge_max + 3, s.bounds_max);
		s.cell_size = 0.2f;
		s.tile_size = 16;
		const auto t0 = std::chrono::steady_clock::now();
		const s32 refused = bake_blob({}, {}, floor_box, s, st, blob);
		const d64 refused_ms = std::chrono::duration<d64, std::milli>(std::chrono::steady_clock::now() - t0).count();
		check("a grid beyond max_bake_tiles is refused up front", refused == navigation::bake_error_too_many_tiles && refused_ms < 1000.0);
		s.bounds_max[0] = s.bounds_max[2] = 1.0e12f;
		check("bounds whose cell count overflows an int are refused, not wrapped", bake_blob({}, {}, floor_box, s, st, blob) == navigation::bake_error_too_many_tiles);
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
