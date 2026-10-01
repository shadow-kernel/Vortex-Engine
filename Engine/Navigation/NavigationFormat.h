#pragma once

// The ".vnav" blob (serialized tiled navmesh) shared by NavigationBake.cpp (writer) and NavigationWorld.cpp
// (reader). Private to Engine/Navigation — the managed side treats the blob as opaque bytes.
//
//   file_header
//   tile_count x { tile_header, tile data (tile_header::size bytes, padded to a multiple of 4) }
//
// Every field is a 4-byte scalar, so the layout has no padding and is identical on every little-endian target
// (x64 Windows, arm64 macOS). The tile data itself is Detour's native tile format (dtCreateNavMeshData).

#include "NavigationWorld.h"

namespace vortex::navigation::format {

	struct file_header
	{
		u32 magic;                 // data_magic
		s32 version;               // data_version
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
