#pragma once

// The ".vnav" blob (serialized tiled navmesh) shared by NavigationBake.cpp (writer) and NavigationWorld.cpp
// (reader). Private to Engine/Navigation — the managed side treats the blob as opaque bytes.
//
//   file_header
//   tile_count x { tile_header, tile data (tile_header::size bytes, padded to a multiple of 4) }
//
// Every field is a 4-byte scalar, so the layout has no padding and is identical on every little-endian target
// (x64 Windows, arm64 macOS). The tile data itself is Detour's native tile format (dtCreateNavMeshData).
//
// Versions. The header layout is the same in both, reserved words included (the editor reads the settings straight
// from offset 8); the tile data differs, because a dtLink holds a dtPolyRef and every tile reserves room for its links:
//   1  Detour built with 32-bit poly refs: a dtLink is 12 bytes. max_tiles / max_polys were the powers of two that
//      split the ref's 22 tile + polygon bits. NavigationWorld.cpp re-lays such tiles out on load — the links section
//      is runtime state (dtCreateNavMeshData leaves it zeroed, dtNavMesh::addTile rebuilds every link), so nothing
//      is lost.
//   2  (#517) 64-bit poly refs (DT_POLYREF64, 16 salt / 28 tile / 20 polygon bits): a dtLink is 16 bytes. max_tiles
//      = the tiles stored, max_polys = the most polygons in one of them (Detour no longer derives ref bits from them).

#include "NavigationWorld.h"

namespace vortex::navigation::format {

	inline constexpr s32 version_ref32{ 1 };   // tile data of a 32-bit poly ref build (see above)

	struct file_header
	{
		u32 magic;                 // data_magic
		s32 version;               // data_version (or version_ref32)
		bake_settings settings;    // what the navmesh was baked with (the bake window shows it again)
		bake_stats stats;
		f32 orig[3];               // dtNavMeshParams
		f32 tile_width;
		f32 tile_height;
		s32 max_tiles;
		s32 max_polys;
		s32 tile_count;
		s32 reserved[4];
	};

	struct tile_header
	{
		s32 tx;
		s32 tz;
		s32 layer;
		s32 size;                  // bytes of Detour tile data that follow
	};

	static_assert(sizeof(bake_settings) == 23 * 4, "bake_settings must stay padding-free");
	static_assert(sizeof(bake_stats) == 16 * 4, "bake_stats must stay padding-free");
	static_assert(sizeof(file_header) == 2 * 4 + sizeof(bake_settings) + sizeof(bake_stats) + 12 * 4, "file_header layout");
	static_assert(sizeof(tile_header) == 16, "tile_header layout");

	inline s32 padded(s32 size) { return (size + 3) & ~3; }
}
