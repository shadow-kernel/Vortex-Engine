#include "../ApiCommon.h"
#include "../../Engine/Navigation/NavigationWorld.h"   // AI & Navigation (Recast/Detour) — see the header for the contract

#include <cmath>
#include <cstddef>
#include <type_traits>
#include <vector>

// ============================================================================================
// AI & Navigation interop (issues #108-#110) — the extern "C" surface over
// Engine/Navigation/NavigationWorld.{h,cpp} + NavigationBake.cpp.
//
// The signatures below ARE the native ABI contract shared with the managed side
// (Editor/DllWrapper/VortexAPI.Navigation.cs + Editor/Core/Services/AI/NavigationService.cs).
// Every function is a thin pass-through, so the stub build (no Recast in the binary — the Visual
// Studio projects today) exports exactly the same symbols and the managed side can probe
// NavInit() == 0 and keep going without navigation.
//
// Conventions: metres / seconds / degrees, Y up, positions float[3] in world space; `extents` =
// half extents of the box that looks for the navmesh polygon closest to a point (null = (2, 4, 2)).
// Agent handles uint32 (0 = invalid). Booleans are int32 0/1. The navmesh blob (".vnav") is opaque
// to the managed side: NavBake -> NavGetBakeResult -> file -> NavLoad.
// ============================================================================================

namespace nav = vortex::navigation;

typedef struct NavBakeSettings {
	float cellSize, cellHeight, agentHeight, agentRadius, agentMaxClimb, agentMaxSlope;
	float regionMinSize, regionMergeSize, edgeMaxLen, edgeMaxError, detailSampleDist, detailSampleMaxError;
	int32_t vertsPerPoly, tileSize, partition, filterFlags;
	float boundsMin[3], boundsMax[3];   // min > max on any axis = use the geometry bounds
	int32_t sourceFlags;                // opaque: stored with the navmesh for the caller (the editor's geometry options)
} NavBakeSettings;

typedef struct NavBakeStats {
	int32_t tilesX, tilesZ, tileCount, polyCount, vertCount, detailTriCount, inputTriangles, inputBoxes;
	float bakeMs;
	float boundsMin[3], boundsMax[3];
	int32_t dataSize;
} NavBakeStats;

typedef struct NavAgentParams {
	float radius, height, maxSpeed, maxAcceleration, separationWeight, collisionQueryRange, pathOptimizationRange;
	int32_t avoidanceQuality;   // 0 low .. 3 high
	uint32_t updateFlags;       // DT_CROWD_* bits, 0 = default
} NavAgentParams;

typedef struct NavAgentState {
	float position[3], velocity[3], desiredVelocity[3], target[3], nextCorner[3];
	float remainingDistance;    // -1 = no path
	int32_t moveState;          // 0 idle, 1 pending, 2 moving, 3 failed, 4 velocity
	int32_t partial, onNavMesh, cornerCount;
} NavAgentState;

// The API structs are handed to the engine through reinterpret_cast, so the layouts must agree.
static_assert(std::is_standard_layout_v<NavBakeSettings> && std::is_standard_layout_v<nav::bake_settings>);
static_assert(sizeof(NavBakeSettings) == sizeof(nav::bake_settings), "NavBakeSettings / bake_settings layout drift");
static_assert(offsetof(NavBakeSettings, vertsPerPoly) == offsetof(nav::bake_settings, verts_per_poly));
static_assert(offsetof(NavBakeSettings, boundsMax) == offsetof(nav::bake_settings, bounds_max));
static_assert(offsetof(NavBakeSettings, sourceFlags) == offsetof(nav::bake_settings, source_flags));
static_assert(sizeof(NavBakeStats) == sizeof(nav::bake_stats), "NavBakeStats / bake_stats layout drift");
static_assert(offsetof(NavBakeStats, bakeMs) == offsetof(nav::bake_stats, bake_ms));
static_assert(offsetof(NavBakeStats, dataSize) == offsetof(nav::bake_stats, data_size));
static_assert(sizeof(NavAgentParams) == sizeof(nav::agent_params), "NavAgentParams / agent_params layout drift");
static_assert(offsetof(NavAgentParams, updateFlags) == offsetof(nav::agent_params, update_flags));
static_assert(sizeof(NavAgentState) == sizeof(nav::agent_state), "NavAgentState / agent_state layout drift");
static_assert(offsetof(NavAgentState, remainingDistance) == offsetof(nav::agent_state, remaining_distance));
static_assert(offsetof(NavAgentState, cornerCount) == offsetof(nav::agent_state, corner_count));

// ---- World ----

EDITOR_INTERFACE int32_t NavInit(void)          { return nav::available() ? 1 : 0; }   // 1 = Recast/Detour compiled in, 0 = stub
EDITOR_INTERFACE void    NavGetDefaultSettings(NavBakeSettings* out) { nav::default_settings(reinterpret_cast<nav::bake_settings*>(out)); }

// ---- Baking (thread-safe: may run on a worker thread while the game thread queries the loaded navmesh) ----

// World-space triangle soup (verts xyz, tris = vertex index triples, triAreas one byte per triangle or null) plus solid
// oriented boxes (10 floats each: centre, half extents, quaternion xyzw; boxAreas or null). Area 0 = obstacle only,
// 1..63 = walkable by slope (63 = default ground). Returns the .vnav size in bytes (> 0) or a negative error:
// -1 no geometry, -2 bad settings, -3 cancelled, -4 failed, -5 nothing walkable, -6 stub, -7 too many tiles, -8 busy.
EDITOR_INTERFACE int32_t NavBake(const float* verts, int32_t vertCount, const int32_t* tris, int32_t triCount, const uint8_t* triAreas,
	const float* boxes, int32_t boxCount, const uint8_t* boxAreas, const NavBakeSettings* settings, NavBakeStats* outStats)
{
	return nav::bake(verts, vertCount, tris, triCount, triAreas, boxes, boxCount, boxAreas,
		reinterpret_cast<const nav::bake_settings*>(settings), reinterpret_cast<nav::bake_stats*>(outStats));
}
EDITOR_INTERFACE float   NavGetBakeProgress(void) { return nav::bake_progress(); }          // 0..1
EDITOR_INTERFACE void    NavCancelBake(void)      { nav::bake_cancel(); }
EDITOR_INTERFACE int32_t NavGetBakeResult(uint8_t* out, int32_t maxBytes) { return nav::bake_result_copy(out, maxBytes); }   // null = size
EDITOR_INTERFACE void    NavClearBakeResult(void) { nav::bake_result_clear(); }

// ---- The loaded navmesh ----

EDITOR_INTERFACE int32_t NavLoad(const uint8_t* data, int32_t size) { return nav::load(data, size) ? 1 : 0; }   // drops every agent
EDITOR_INTERFACE void    NavUnload(void)   { nav::unload(); }
EDITOR_INTERFACE int32_t NavIsLoaded(void) { return nav::loaded() ? 1 : 0; }
EDITOR_INTERFACE int32_t NavGetInfo(NavBakeSettings* outSettings, NavBakeStats* outStats)
{
	return nav::get_info(reinterpret_cast<nav::bake_settings*>(outSettings), reinterpret_cast<nav::bake_stats*>(outStats)) ? 1 : 0;
}
EDITOR_INTERFACE int32_t NavGetData(uint8_t* out, int32_t maxBytes) { return nav::data_copy(out, maxBytes); }   // null = size

// ---- Queries ----

// Straight (corner) path, start and end included; returns the point count, *outStatus 0 invalid / 1 complete / 2 partial.
EDITOR_INTERFACE int32_t NavFindPath(const float* start, const float* end, const float* extents, float* outPoints, int32_t maxPoints, int32_t* outStatus)
{
	return nav::find_path(start, end, extents, outPoints, maxPoints, outStatus);
}
EDITOR_INTERFACE int32_t NavClosestPoint(const float* pos, const float* extents, float* outPoint) { return nav::closest_point(pos, extents, outPoint) ? 1 : 0; }
// 1 = the navmesh boundary blocks the segment (outHit / outNormal / outT describe the hit), 0 = clear to `end`.
EDITOR_INTERFACE int32_t NavRaycast(const float* start, const float* end, const float* extents, float* outHit, float* outNormal, float* outT)
{
	return nav::raycast(start, end, extents, outHit, outNormal, outT) ? 1 : 0;
}
EDITOR_INTERFACE int32_t NavRandomPoint(float* outPoint) { return nav::random_point(outPoint) ? 1 : 0; }
EDITOR_INTERFACE int32_t NavRandomPointAround(const float* center, float radius, const float* extents, float* outPoint)
{
	return nav::random_point_around(center, radius, extents, outPoint) ? 1 : 0;
}
EDITOR_INTERFACE void    NavSetRandomSeed(uint32_t seed) { nav::set_random_seed(seed); }

// ---- Debug geometry ----

EDITOR_INTERFACE int32_t NavGetDebugTriangles(float* out, int32_t maxFloats) { return nav::debug_triangles(out, maxFloats); }        // 9 floats / triangle
EDITOR_INTERFACE int32_t NavGetDebugLines(float* out, int32_t maxFloats, int32_t flags) { return nav::debug_lines(out, maxFloats, flags); }   // 6 floats / segment

// Engine mesh (render resource) of the loaded navmesh surface, lifted by yOffset — the editor draws it through the
// gizmo pass (SubmitGizmoWireItem = the walkable triangles as a tinted net). id::invalid_id when there is no navmesh
// or no renderer. Release it with DestroyMesh.
EDITOR_INTERFACE id::id_type NavCreateDebugMesh(float yOffset)
{
	const int32_t floats = nav::debug_triangles(nullptr, 0);
	if (floats < 9) return id::invalid_id;
	std::vector<float> tris((size_t)floats);
	const int32_t n = nav::debug_triangles(tris.data(), floats);
	if (n < 9) return id::invalid_id;
	graphics::MeshData data;
	data.vertices.reserve((size_t)(n / 3));
	data.indices.reserve((size_t)(n / 3));
	for (int32_t t = 0; t + 9 <= n; t += 9)
	{
		const float* a = &tris[(size_t)t];
		const float e0[3] = { a[3] - a[0], a[4] - a[1], a[5] - a[2] };
		const float e1[3] = { a[6] - a[0], a[7] - a[1], a[8] - a[2] };
		float nx = e0[1] * e1[2] - e0[2] * e1[1], ny = e0[2] * e1[0] - e0[0] * e1[2], nz = e0[0] * e1[1] - e0[1] * e1[0];
		const float len = std::sqrt(nx * nx + ny * ny + nz * nz);
		if (len > 1e-12f) { nx /= len; ny /= len; nz /= len; } else { nx = 0.0f; ny = 1.0f; nz = 0.0f; }
		for (int k = 0; k < 3; ++k)
		{
			graphics::VertexPosNormalUV v{};
			v.position = { a[k * 3], a[k * 3 + 1] + yOffset, a[k * 3 + 2] };
			v.normal = { nx, ny, nz };
			v.uv = { 0.0f, 0.0f };
			data.indices.push_back((u32)data.vertices.size());
			data.vertices.push_back(v);
		}
	}
	return graphics::ResourceRegistry::instance().create_mesh(data, "NavMeshDebug");
}

// ---- Crowd agents ----

EDITOR_INTERFACE uint32_t NavAgentAdd(const float* pos, const NavAgentParams* params)
{
	return nav::agent_add(pos, reinterpret_cast<const nav::agent_params*>(params));
}
EDITOR_INTERFACE void    NavAgentRemove(uint32_t agent)                        { nav::agent_remove(agent); }
EDITOR_INTERFACE int32_t NavAgentValid(uint32_t agent)                         { return nav::agent_valid(agent) ? 1 : 0; }
EDITOR_INTERFACE int32_t NavAgentSetParams(uint32_t agent, const NavAgentParams* params)
{
	return nav::agent_set_params(agent, reinterpret_cast<const nav::agent_params*>(params)) ? 1 : 0;
}
EDITOR_INTERFACE int32_t NavAgentSetTarget(uint32_t agent, const float* target)   { return nav::agent_set_target(agent, target) ? 1 : 0; }   // async path request
EDITOR_INTERFACE int32_t NavAgentResetTarget(uint32_t agent)                      { return nav::agent_reset_target(agent) ? 1 : 0; }
EDITOR_INTERFACE int32_t NavAgentSetVelocity(uint32_t agent, const float* velocity) { return nav::agent_set_velocity(agent, velocity) ? 1 : 0; }
EDITOR_INTERFACE int32_t NavAgentTeleport(uint32_t agent, const float* pos)       { return nav::agent_teleport(agent, pos) ? 1 : 0; }
EDITOR_INTERFACE int32_t NavAgentMovePosition(uint32_t agent, const float* pos)   { return nav::agent_move_position(agent, pos) ? 1 : 0; }
EDITOR_INTERFACE int32_t NavAgentGetState(uint32_t agent, NavAgentState* out)
{
	return nav::agent_get_state(agent, reinterpret_cast<nav::agent_state*>(out)) ? 1 : 0;
}
EDITOR_INTERFACE int32_t NavAgentGetCorners(uint32_t agent, float* outPoints, int32_t maxPoints) { return nav::agent_get_corners(agent, outPoints, maxPoints); }
EDITOR_INTERFACE int32_t NavGetAgentCount(void) { return nav::agent_count(); }
EDITOR_INTERFACE void    NavCrowdUpdate(float dt) { nav::crowd_update(dt); }
EDITOR_INTERFACE void    NavCrowdClear(void)      { nav::crowd_clear(); }
