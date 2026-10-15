#pragma once

// Runtime tile rebuilds for dynamic obstacles (#435). Private to Engine/Navigation: NavigationBake.cpp builds a tile with
// the bake's own pipeline (the same Recast steps and settings, so a tile rebuilt without obstacles is the tile the bake
// wrote, byte for byte), NavigationWorld.cpp keeps the obstacles, runs the builds on a worker thread and swaps the tiles
// into the loaded navmesh.

#include "NavigationFormat.h"

#include <memory>
#include <string>
#include <vector>

namespace vortex::navigation::rebuild {

	// The volume a carving obstacle covers, in world space. Its walkable surface — from an agent height below the
	// obstacle's bottom (no agent fits under it) up to its top, over its footprint grown by the bake's agent radius —
	// becomes `area` (one of area_carved_first .. area_carved): polygons of their own, which the runtime blocks while the
	// obstacle is active.
	struct carve_volume
	{
		f32 hull[8 * 3];     // the grown footprint: a convex polygon, x / y / z triples (y unused), counter-clockwise from above
		s32 hull_count;      // 3..8 corners; 0 = a cylinder
		f32 center[2];       // cylinder: centre x / z
		f32 radius;          // cylinder: radius, grown by the agent radius
		f32 ymin, ymax;      // the heights whose walkable surface it covers
		u8 area;             // the area its surface becomes: area_carved_first .. area_carved
	};

	// The bake input of the loaded navmesh, binned into its tile grid (the bounds, grid and settings of its header).
	// Immutable once made: the worker threads read it while the game thread may already make the next one.
	struct source;

	// null when the header describes no navmesh or nothing of the geometry lies in the grid.
	std::shared_ptr<const source> make_source(const format::file_header& header, const f32* verts, s32 vert_count, const s32* tris,
		s32 tri_count, const u8* tri_areas, const f32* boxes, s32 box_count, const u8* box_areas);
	// make_source in two steps: the copy of the input (fast — the caller's arrays may go right after), then the binning into
	// the tile grid (tens of milliseconds for a large level: any thread). copy_source: null for a header without a navmesh;
	// prepare_source: false when nothing of the geometry lies in the grid.
	std::shared_ptr<source> copy_source(const format::file_header& header, const f32* verts, s32 vert_count, const s32* tris,
		s32 tri_count, const u8* tri_areas, const f32* boxes, s32 box_count, const u8* box_areas);
	bool prepare_source(source& s);
	s32 source_triangles(const source& s);
	s32 source_boxes(const source& s);
	// Bytes the source keeps (its geometry and tile lists).
	size_t source_bytes(const source& s);

	// The tiles whose heightfield (the tile plus its border) reaches into the xz rectangle of [bmin, bmax]; false when none.
	bool tile_range(const source& s, const f32* bmin, const f32* bmax, s32& tx0, s32& tz0, s32& tx1, s32& tz1);

	// Tile (tx, tz) built the way the bake built it, the carve volumes marked: Detour tile data, empty when nothing in the
	// tile is walkable. False when the build failed (the reason in error).
	bool build_tile(const source& s, s32 tx, s32 tz, const std::vector<carve_volume>& carves, std::vector<u8>& out, std::string& error);
}
