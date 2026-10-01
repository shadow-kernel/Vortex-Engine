#pragma once

// ============================================================================
// AI & Navigation — the Recast/Detour navigation world (GitHub issues #108 epic, #109 navmesh baking,
// #110 NavAgent + pathfinding API, crowd steering / local avoidance).
//
// This is the engine-side module behind the extern "C" exports in VortexAPI/Api/NavigationApi.cpp; the two
// mirror each other one to one (the API layer is a thin pass-through) and the managed side
// (Editor/DllWrapper/VortexAPI.Navigation.cs + Editor/Core/Services/AI/NavigationService.cs) is the only caller.
//
// Pieces
//   * bake   (NavigationBake.cpp)  world-space geometry -> tiled Detour navmesh, serialized as the ".vnav" blob.
//            Input = a triangle soup (level meshes, mesh colliders; one-sided: counter-clockwise seen from the
//            walkable side, the engine / assimp convention, i.e. (v1-v0)x(v2-v0) points up on a floor) plus solid
//            oriented boxes (box colliders, primitives), which are voxelised as SOLID volumes so a crate or a wall
//            block never gets a navmesh island inside it. Tiles are built in parallel; the bake never touches the
//            loaded runtime navmesh, so it may run on a worker thread while the game thread keeps querying.
//   * world  (NavigationWorld.cpp) the loaded navmesh (dtNavMesh + dtNavMeshQuery), path / point / raycast queries,
//            a DetourCrowd with up to max_agents agents (steering along path corridors, obstacle avoidance,
//            separation) and debug geometry for the viewport.
//
// Conventions (the contract, shared with the managed side):
//   * metres / seconds / degrees, Y up, engine world coordinates are handed to Recast unchanged.
//   * positions are float[3]; "extents" are the half extents of the box used to find the navmesh polygon closest
//     to a point (null = the module default (2, 4, 2)).
//   * agent handles are u32, 0 = invalid (generation << 16 | slot + 1); stale handles are rejected.
//   * all calls come from the single game / UI thread, except bake(), bake_progress() and bake_cancel(), which
//     are thread-safe (only one bake runs at a time; a second concurrent bake() fails with bake_error_busy).
//   * the .vnav blob is little-endian and versioned (data_version); load() rejects anything else.
//
// Without Recast in the build (VORTEX_HAS_RECAST undefined / 0 — the Visual Studio projects, or
// -DVORTEX_ENABLE_RECAST=OFF) the .cpp files compile the stub: available() is false, bake() returns
// bake_error_unavailable, load() fails, every query returns 0 / false and every agent call is a no-op, so the
// managed side can detect "no navigation" and degrade gracefully (agents simply don't move).
// ============================================================================

#include "../Common/CommonHeaders.h"

#ifndef VORTEX_HAS_RECAST
#define VORTEX_HAS_RECAST 0
#endif

namespace vortex::navigation {

	// ---- ABI constants ---------------------------------------------------------------------------------
	inline constexpr u32 data_magic{ 0x56414E56u };      // "VNAV" (little-endian bytes 'V','N','A','V')
	inline constexpr s32 data_version{ 1 };

	// Region partitioning (Recast): watershed = best quality (default), monotone = fastest (long thin polygons),
	// layers = good for tiles with many overlapping floors.
	inline constexpr s32 partition_watershed{ 0 };
	inline constexpr s32 partition_monotone{ 1 };
	inline constexpr s32 partition_layers{ 2 };

	// Heightfield filters (bake_settings::filter_flags).
	inline constexpr s32 filter_low_hanging_obstacles{ 1 << 0 };   // walk over curbs / low steps (< max climb)
	inline constexpr s32 filter_ledge_spans{ 1 << 1 };             // no navmesh hanging over ledges
	inline constexpr s32 filter_low_height_spans{ 1 << 2 };        // no navmesh under low ceilings (< agent height)
	inline constexpr s32 filter_all{ 7 };

	// Areas: the per-triangle / per-box area id. 0 = obstacle only (rasterised as solid, never walkable);
	// 1..63 = walkable when the surface slope allows it. 63 is the default ground; it becomes Detour area 0.
	inline constexpr u8 area_obstacle{ 0 };
	inline constexpr u8 area_ground{ 63 };

	// Path status (find_path)
	inline constexpr s32 path_invalid{ 0 };    // no navmesh, or start / end not near the navmesh
	inline constexpr s32 path_complete{ 1 };
	inline constexpr s32 path_partial{ 2 };    // the end is unreachable: the path leads to the closest reachable point

	// Agent move state (agent_state::move_state)
	inline constexpr s32 move_idle{ 0 };       // no target
	inline constexpr s32 move_pending{ 1 };    // path request queued / being computed (SetDestination returned)
	inline constexpr s32 move_moving{ 2 };     // following a valid path (possibly partial)
	inline constexpr s32 move_failed{ 3 };     // the target is not on / near the navmesh
	inline constexpr s32 move_velocity{ 4 };   // manual velocity control (agent_set_velocity)

	// Bake errors (negative results of bake())
	inline constexpr s32 bake_error_no_geometry{ -1 };
	inline constexpr s32 bake_error_bad_settings{ -2 };
	inline constexpr s32 bake_error_cancelled{ -3 };
	inline constexpr s32 bake_error_failed{ -4 };        // Recast / Detour failure (see the log)
	inline constexpr s32 bake_error_empty{ -5 };         // geometry, but nothing walkable
	inline constexpr s32 bake_error_unavailable{ -6 };   // stub build
	inline constexpr s32 bake_error_too_many_tiles{ -7 };// raise the tile size or the cell size
	inline constexpr s32 bake_error_busy{ -8 };          // another bake is running

	inline constexpr s32 box_floats{ 10 };               // centre xyz, half extents xyz, rotation quaternion xyzw
	inline constexpr s32 max_agents{ 256 };

	// Bake parameters. Layout-identical to the C ABI's NavBakeSettings (NavigationApi.cpp static_asserts it).
	struct bake_settings
	{
		f32 cell_size;               // xz voxel size (m). Smaller = more precise, slower. ~ agent_radius / 2..3
		f32 cell_height;             // y voxel size (m)
		f32 agent_height;            // m: minimum clearance
		f32 agent_radius;            // m: the walkable area is eroded by this (agents never clip walls)
		f32 agent_max_climb;         // m: step height
		f32 agent_max_slope;         // degrees
		f32 region_min_size;         // cells (side): smaller islands are removed
		f32 region_merge_size;       // cells (side): smaller regions are merged into neighbours
		f32 edge_max_len;            // m, 0 = unlimited
		f32 edge_max_error;          // cells: contour simplification tolerance
		f32 detail_sample_dist;      // cells: height detail sampling (< 0.9 = none)
		f32 detail_sample_max_error; // cells (of cell_height)
		s32 verts_per_poly;          // 3..6
		s32 tile_size;               // cells per tile side (16..1024)
		s32 partition;               // partition_*
		s32 filter_flags;            // filter_* bits
		f32 bounds_min[3];           // bake bounds; when min > max on any axis the geometry bounds are used
		f32 bounds_max[3];
		s32 source_flags;            // opaque to the module: stored with the navmesh (the editor keeps its geometry
		                             // source options here so a rebake reuses them)
	};

	// What a bake produced / what the loaded navmesh contains. Layout-identical to NavBakeStats.
	struct bake_stats
	{
		s32 tiles_x, tiles_z;        // tile grid
		s32 tile_count;              // tiles that contain polygons
		s32 poly_count;
		s32 vert_count;
		s32 detail_tri_count;
		s32 input_triangles;
		s32 input_boxes;
		f32 bake_ms;
		f32 bounds_min[3];
		f32 bounds_max[3];
		s32 data_size;               // bytes of the serialized navmesh (.vnav)
	};

	// Crowd agent parameters. Layout-identical to NavAgentParams.
	struct agent_params
	{
		f32 radius;                  // m (steering / separation; the navmesh itself is baked for one radius)
		f32 height;                  // m
		f32 max_speed;               // m/s
		f32 max_acceleration;        // m/s²
		f32 separation_weight;       // how strongly this agent keeps its distance to others (0 = none)
		f32 collision_query_range;   // m, <= 0: radius * 12
		f32 path_optimization_range; // m, <= 0: radius * 30
		s32 avoidance_quality;       // 0 low .. 3 high (obstacle avoidance sampling)
		u32 update_flags;            // DT_CROWD_* bits; 0 = anticipate turns + avoidance + separation + optimise
	};

	// A crowd agent's current state. Layout-identical to NavAgentState.
	struct agent_state
	{
		f32 position[3];             // on the navmesh surface
		f32 velocity[3];             // actual velocity (m/s)
		f32 desired_velocity[3];     // path-following velocity before avoidance
		f32 target[3];               // current move target (clamped to the reachable end for partial paths)
		f32 next_corner[3];          // next path corner (== position when there is none)
		f32 remaining_distance;      // along the path corners to the target (m); -1 = no path
		s32 move_state;              // move_*
		s32 partial;                 // 1 = the requested target is unreachable; heading to the closest point
		s32 on_navmesh;              // 1 = standing on a valid polygon
		s32 corner_count;            // corners left on the path (incl. the end), capped at 64
	};

	void default_settings(bake_settings* out);

	// ---- Baking ---------------------------------------------------------------------------------------------
	// verts: vert_count xyz triples; tris: tri_count vertex index triples; tri_areas: one area id per triangle or
	// null (= area_ground, walkable by slope). boxes: box_count * box_floats; box_areas: per box or null.
	// Returns the size of the produced .vnav blob (> 0, fetch it with bake_result_copy) or a bake_error_*.
	s32  bake(const f32* verts, s32 vert_count, const s32* tris, s32 tri_count, const u8* tri_areas,
	          const f32* boxes, s32 box_count, const u8* box_areas,
	          const bake_settings* settings, bake_stats* out_stats);
	f32  bake_progress();                                  // 0..1 of the running (or last) bake
	void bake_cancel();                                    // the running bake stops at the next tile
	s32  bake_result_copy(u8* out, s32 max_bytes);         // copies the last result; null / 0 = query the size
	void bake_result_clear();

	// ---- Runtime world --------------------------------------------------------------------------------------
	bool available();                                      // Recast/Detour compiled in
	bool load(const u8* data, s32 size);                   // replaces the navmesh, drops every agent
	void unload();
	bool loaded();
	bool get_info(bake_settings* out_settings, bake_stats* out_stats);
	s32  data_copy(u8* out, s32 max_bytes);                // serialized blob of the loaded navmesh; null = size

	// ---- Queries (loaded navmesh) ---------------------------------------------------------------------------
	// Straight path (corner points, start and end included) from start to end, at most max_points. Returns the
	// number of points; out_status = path_*. Corners are smoothed along the polygon corridor (string pulling).
	s32  find_path(const f32* start, const f32* end, const f32* extents, f32* out_points, s32 max_points, s32* out_status);
	bool closest_point(const f32* pos, const f32* extents, f32* out_point);
	// Walks the navmesh surface from start towards end: true = a wall (navmesh boundary) was hit before end.
	// out_hit = hit point (or end), out_normal = wall normal (xz), out_t = fraction 0..1 of the segment.
	bool raycast(const f32* start, const f32* end, const f32* extents, f32* out_hit, f32* out_normal, f32* out_t);
	bool random_point(f32* out_point);                     // anywhere on the navmesh (area weighted)
	bool random_point_around(const f32* center, f32 radius, const f32* extents, f32* out_point);   // reachable from center
	void set_random_seed(u32 seed);

	// ---- Debug geometry -------------------------------------------------------------------------------------
	s32  debug_triangles(f32* out, s32 max_floats);        // detail triangles, 9 floats each; null = float count
	// flags: bit0 outer boundary edges, bit1 internal polygon edges. 6 floats per segment; null = float count.
	s32  debug_lines(f32* out, s32 max_floats, s32 flags);

	// ---- Crowd -----------------------------------------------------------------------------------------------
	u32  agent_add(const f32* pos, const agent_params* params);   // placed on the closest polygon; 0 = failed
	void agent_remove(u32 agent);
	bool agent_valid(u32 agent);
	bool agent_set_params(u32 agent, const agent_params* params);
	bool agent_set_target(u32 agent, const f32* target);          // asynchronous: the path is computed in updates
	bool agent_reset_target(u32 agent);                            // stop (keeps sliding to a halt)
	bool agent_set_velocity(u32 agent, const f32* velocity);      // manual steering (still avoids others)
	bool agent_teleport(u32 agent, const f32* pos);               // place anywhere; clears the path
	bool agent_move_position(u32 agent, const f32* pos);          // nudge along the surface (collision correction)
	bool agent_get_state(u32 agent, agent_state* out);
	s32  agent_get_corners(u32 agent, f32* out_points, s32 max_points);   // remaining path corners
	s32  agent_count();
	void crowd_update(f32 dt);
	void crowd_clear();                                            // remove every agent
}
