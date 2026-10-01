#include "NavigationWorld.h"
#include "NavigationFormat.h"

// ============================================================================
// Navmesh baking (issue #109): world-space triangles + solid boxes -> tiled Detour navmesh -> ".vnav" blob.
// The pipeline per tile is the classic Recast one (Sample_TileMesh): rasterise -> filter -> compact -> erode by
// the agent radius -> regions -> contours -> polygon mesh -> detail mesh -> dtCreateNavMeshData. Tiles are
// independent, so they are built on a small worker pool; the result is only published (bake_result_copy) when
// every tile finished, and nothing here touches the loaded runtime navmesh (NavigationWorld.cpp).
// See NavigationWorld.h for the contract; the #else branch at the bottom is the stub.
// ============================================================================

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace vortex::navigation {

	void default_settings(bake_settings* out)
	{
		if (!out) return;
		bake_settings s{};
		s.cell_size = 0.2f;
		s.cell_height = 0.1f;
		s.agent_height = 1.8f;
		s.agent_radius = 0.4f;
		s.agent_max_climb = 0.4f;
		s.agent_max_slope = 45.0f;
		s.region_min_size = 8.0f;
		s.region_merge_size = 20.0f;
		s.edge_max_len = 12.0f;
		s.edge_max_error = 1.3f;
		s.detail_sample_dist = 6.0f;
		s.detail_sample_max_error = 1.0f;
		s.verts_per_poly = 6;
		s.tile_size = 48;
		s.partition = partition_watershed;
		s.filter_flags = filter_all;
		s.bounds_min[0] = s.bounds_min[1] = s.bounds_min[2] = 1.0f;    // min > max = use the geometry bounds
		s.bounds_max[0] = s.bounds_max[1] = s.bounds_max[2] = -1.0f;
		*out = s;
	}
}

#if VORTEX_HAS_RECAST

#include <Recast.h>
#include <DetourAlloc.h>
#include <DetourCommon.h>
#include <DetourNavMesh.h>
#include <DetourNavMeshBuilder.h>

namespace vortex::navigation {

	namespace {

		void log(const char* fmt, ...)
		{
			char text[1024];
			va_list args;
			va_start(args, fmt);
			std::vsnprintf(text, sizeof(text), fmt, args);
			va_end(args);
			std::string line{ "[navigation] " };
			line += text;
			line += '\n';
			platform::debug_output(line.c_str());
		}

		// Recast build context: collects warnings / errors of a tile build (Recast's own logging) so a failed tile
		// can say why. One per worker thread.
		class bake_context final : public rcContext
		{
		public:
			bake_context() : rcContext(true) {}
			std::string errors;
		protected:
			void doLog(const rcLogCategory category, const char* msg, const int len) override
			{
				if (category == RC_LOG_PROGRESS || errors.size() > 2048) return;
				errors.append(category == RC_LOG_ERROR ? "error: " : "warning: ");
				errors.append(msg, (size_t)std::max(0, len));
				errors.push_back(';');
				errors.push_back(' ');
			}
		};

		// ---- global bake state (only one bake at a time) --------------------------------------------------------
		std::atomic<bool> g_running{ false };
		std::atomic<bool> g_cancel{ false };
		std::atomic<s32> g_tiles_done{ 0 };
		std::atomic<s32> g_tiles_total{ 0 };
		std::mutex g_result_mutex;
		std::vector<u8> g_result;

		constexpr f32 k_pi{ 3.14159265358979f };

		struct v3 { f32 x, y, z; };
		inline v3 sub(const v3& a, const v3& b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
		inline v3 cross(const v3& a, const v3& b) { return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x }; }
		inline f32 dot(const v3& a, const v3& b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
		inline f32 length(const v3& a) { return std::sqrt(dot(a, a)); }

		// v' = q v q* (Hamilton convention, like System.Numerics and the physics module)
		inline v3 rotate(const f32* q, const v3& v)
		{
			const v3 u{ q[0], q[1], q[2] };
			const f32 w = q[3];
			const v3 t = cross(u, v);
			const v3 t2{ 2.0f * t.x, 2.0f * t.y, 2.0f * t.z };
			const v3 c = cross(u, t2);
			return { v.x + w * t2.x + c.x, v.y + w * t2.y + c.y, v.z + w * t2.z + c.z };
		}

		inline bool finite(f32 v) { return v == v && v < 3.0e38f && v > -3.0e38f; }

		// A solid oriented box, pre-processed once for every tile: world-space triangles (outward winding, area by
		// slope), its world AABB, and the frame for the per-column interior fill.
		struct solid_box
		{
			v3 center;
			v3 axis[3];          // unit axes (rotation columns)
			f32 half[3];
			f32 tri_verts[12 * 9];
			u8 tri_areas[12];
			v3 bmin, bmax;
			u8 area;             // user area (0 = obstacle)
		};

		// The 6 faces of a unit cube with outward counter-clockwise winding (the same vertex order as
		// Engine/Graphics/Geometry/CubeGenerator.cpp: (v1-v0)x(v2-v0) points out of the box).
		constexpr f32 k_cube_faces[6][4][3] = {
			{ {-1,-1, 1}, { 1,-1, 1}, { 1, 1, 1}, {-1, 1, 1} },   // +Z
			{ { 1,-1,-1}, {-1,-1,-1}, {-1, 1,-1}, { 1, 1,-1} },   // -Z
			{ {-1, 1, 1}, { 1, 1, 1}, { 1, 1,-1}, {-1, 1,-1} },   // +Y
			{ {-1,-1,-1}, { 1,-1,-1}, { 1,-1, 1}, {-1,-1, 1} },   // -Y
			{ { 1,-1, 1}, { 1,-1,-1}, { 1, 1,-1}, { 1, 1, 1} },   // +X
			{ {-1,-1,-1}, {-1,-1, 1}, {-1, 1, 1}, {-1, 1,-1} },   // -X
		};

		u8 slope_area(const v3& a, const v3& b, const v3& c, u8 area, f32 walkable_cos)
		{
			if (area == area_obstacle) return RC_NULL_AREA;
			const v3 n = cross(sub(b, a), sub(c, a));
			const f32 len = length(n);
			if (!(len > 1.0e-12f)) return RC_NULL_AREA;
			return (n.y / len) > walkable_cos ? area : (u8)RC_NULL_AREA;
		}

		bool prepare_box(const f32* src, u8 area, f32 walkable_cos, solid_box& out)
		{
			for (int i = 0; i < box_floats; ++i) if (!finite(src[i])) return false;
			out.center = { src[0], src[1], src[2] };
			for (int i = 0; i < 3; ++i) out.half[i] = std::max(std::fabs(src[3 + i]), 1.0e-3f);
			f32 q[4] = { src[6], src[7], src[8], src[9] };
			f32 ql = std::sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
			if (ql < 1.0e-6f) { q[0] = q[1] = q[2] = 0.0f; q[3] = 1.0f; }
			else for (f32& c : q) c /= ql;
			out.axis[0] = rotate(q, { 1, 0, 0 });
			out.axis[1] = rotate(q, { 0, 1, 0 });
			out.axis[2] = rotate(q, { 0, 0, 1 });
			out.area = area;

			out.bmin = { 3.0e38f, 3.0e38f, 3.0e38f };
			out.bmax = { -3.0e38f, -3.0e38f, -3.0e38f };
			int t = 0;
			for (int f = 0; f < 6; ++f)
			{
				v3 w[4];
				for (int k = 0; k < 4; ++k)
				{
					const f32* l = k_cube_faces[f][k];
					w[k] = {
						out.center.x + out.axis[0].x * l[0] * out.half[0] + out.axis[1].x * l[1] * out.half[1] + out.axis[2].x * l[2] * out.half[2],
						out.center.y + out.axis[0].y * l[0] * out.half[0] + out.axis[1].y * l[1] * out.half[1] + out.axis[2].y * l[2] * out.half[2],
						out.center.z + out.axis[0].z * l[0] * out.half[0] + out.axis[1].z * l[1] * out.half[1] + out.axis[2].z * l[2] * out.half[2] };
					out.bmin = { std::min(out.bmin.x, w[k].x), std::min(out.bmin.y, w[k].y), std::min(out.bmin.z, w[k].z) };
					out.bmax = { std::max(out.bmax.x, w[k].x), std::max(out.bmax.y, w[k].y), std::max(out.bmax.z, w[k].z) };
				}
				const int quad[2][3] = { { 0, 1, 2 }, { 0, 2, 3 } };
				for (int h = 0; h < 2; ++h, ++t)
				{
					const v3& a = w[quad[h][0]];
					const v3& b = w[quad[h][1]];
					const v3& c = w[quad[h][2]];
					f32* d = &out.tri_verts[t * 9];
					d[0] = a.x; d[1] = a.y; d[2] = a.z; d[3] = b.x; d[4] = b.y; d[5] = b.z; d[6] = c.x; d[7] = c.y; d[8] = c.z;
					out.tri_areas[t] = slope_area(a, b, c, area, walkable_cos);
				}
			}
			return true;
		}

		// Vertical extent of the box along the line (x, *, z) -> [lo, hi]; top_cos = |normal.y| of the face that
		// bounds it from above. False when the line misses the box.
		bool box_column(const solid_box& b, f32 x, f32 z, f32& lo, f32& hi, f32& top_cos)
		{
			lo = -3.0e38f; hi = 3.0e38f; top_cos = 1.0f;
			const v3 rel{ x - b.center.x, -b.center.y, z - b.center.z };
			for (int i = 0; i < 3; ++i)
			{
				const f32 k = dot(rel, b.axis[i]);   // projection at y = 0
				const f32 m = b.axis[i].y;          // d(projection) / dy
				const f32 h = b.half[i];
				if (std::fabs(m) < 1.0e-6f)
				{
					if (k < -h || k > h) return false;
					continue;
				}
				f32 y0 = (-h - k) / m, y1 = (h - k) / m;
				if (y0 > y1) std::swap(y0, y1);
				if (y0 > lo) lo = y0;
				if (y1 < hi) { hi = y1; top_cos = std::fabs(m); }
				if (lo >= hi) return false;
			}
			return hi > lo && hi < 3.0e38f && lo > -3.0e38f;
		}

		struct bake_job
		{
			bake_settings s;
			rcConfig base;                         // shared part of the per-tile config
			f32 bmin[3], bmax[3];                  // whole bake bounds
			s32 tiles_x, tiles_z;
			f32 tile_world;                        // tile side in metres
			s32 max_polys_per_tile;
			f32 walkable_cos;

			const f32* verts;
			const s32* tris;
			std::vector<u8> tri_area;              // final area per triangle (slope applied)
			std::vector<solid_box> boxes;

			std::vector<std::vector<s32>> tile_tris;
			std::vector<std::vector<s32>> tile_boxes;

			struct tile_out
			{
				std::vector<u8> data;
				s32 polys{ 0 }, verts{ 0 }, detail_tris{ 0 };
				bool failed{ false };
				std::string error;
			};
			std::vector<tile_out> out;
		};

		void build_tile(bake_job& job, s32 tx, s32 tz, bake_job::tile_out& result)
		{
			const s32 index = tz * job.tiles_x + tx;
			const auto& tri_list = job.tile_tris[index];
			const auto& box_list = job.tile_boxes[index];
			if (tri_list.empty() && box_list.empty()) return;

			bake_context ctx;
			rcConfig cfg = job.base;
			cfg.bmin[0] = job.bmin[0] + tx * job.tile_world;
			cfg.bmin[1] = job.bmin[1];
			cfg.bmin[2] = job.bmin[2] + tz * job.tile_world;
			cfg.bmax[0] = job.bmin[0] + (tx + 1) * job.tile_world;
			cfg.bmax[1] = job.bmax[1];
			cfg.bmax[2] = job.bmin[2] + (tz + 1) * job.tile_world;
			cfg.bmin[0] -= cfg.borderSize * cfg.cs;
			cfg.bmin[2] -= cfg.borderSize * cfg.cs;
			cfg.bmax[0] += cfg.borderSize * cfg.cs;
			cfg.bmax[2] += cfg.borderSize * cfg.cs;

			rcHeightfield* solid = rcAllocHeightfield();
			rcCompactHeightfield* chf = nullptr;
			rcContourSet* cset = nullptr;
			rcPolyMesh* pmesh = nullptr;
			rcPolyMeshDetail* dmesh = nullptr;
			auto cleanup = [&]()
			{
				if (solid) rcFreeHeightField(solid);
				if (chf) rcFreeCompactHeightfield(chf);
				if (cset) rcFreeContourSet(cset);
				if (pmesh) rcFreePolyMesh(pmesh);
				if (dmesh) rcFreePolyMeshDetail(dmesh);
				solid = nullptr; chf = nullptr; cset = nullptr; pmesh = nullptr; dmesh = nullptr;
			};
			auto fail = [&](const char* what)
			{
				result.failed = true;
				result.error = std::string(what) + (ctx.errors.empty() ? std::string() : " (" + ctx.errors + ")");
				cleanup();
			};

			if (!solid || !rcCreateHeightfield(&ctx, *solid, cfg.width, cfg.height, cfg.bmin, cfg.bmax, cfg.cs, cfg.ch))
			{
				fail("heightfield allocation");
				return;
			}

			// Triangle soup (surface rasterisation).
			for (s32 t : tri_list)
			{
				const s32* ti = &job.tris[t * 3];
				if (!rcRasterizeTriangle(&ctx, &job.verts[ti[0] * 3], &job.verts[ti[1] * 3], &job.verts[ti[2] * 3],
					job.tri_area[t], *solid, cfg.walkableClimb))
				{
					fail("rasterise triangles");
					return;
				}
			}

			// Solid boxes: surfaces (conservative at the edges) + the interior of every column whose centre is inside.
			const f32 ich = 1.0f / cfg.ch;
			for (s32 bi : box_list)
			{
				const solid_box& b = job.boxes[bi];
				for (int t = 0; t < 12; ++t)
				{
					const f32* v = &b.tri_verts[t * 9];
					if (!rcRasterizeTriangle(&ctx, v, v + 3, v + 6, b.tri_areas[t], *solid, cfg.walkableClimb))
					{
						fail("rasterise boxes");
						return;
					}
				}
				const int x0 = std::max(0, (int)std::floor((b.bmin.x - cfg.bmin[0]) / cfg.cs));
				const int x1 = std::min(cfg.width - 1, (int)std::floor((b.bmax.x - cfg.bmin[0]) / cfg.cs));
				const int z0 = std::max(0, (int)std::floor((b.bmin.z - cfg.bmin[2]) / cfg.cs));
				const int z1 = std::min(cfg.height - 1, (int)std::floor((b.bmax.z - cfg.bmin[2]) / cfg.cs));
				const f32 by = cfg.bmax[1] - cfg.bmin[1];
				for (int z = z0; z <= z1; ++z)
				{
					for (int x = x0; x <= x1; ++x)
					{
						const f32 cx = cfg.bmin[0] + (x + 0.5f) * cfg.cs;
						const f32 cz = cfg.bmin[2] + (z + 0.5f) * cfg.cs;
						f32 lo, hi, top_cos;
						if (!box_column(b, cx, cz, lo, hi, top_cos)) continue;
						f32 smin = lo - cfg.bmin[1], smax = hi - cfg.bmin[1];
						if (smax < 0.0f || smin > by) continue;
						if (smin < 0.0f) smin = 0.0f;
						if (smax > by) smax = by;
						const int ismin = rcClamp((int)std::floor(smin * ich), 0, RC_SPAN_MAX_HEIGHT);
						if (ismin >= RC_SPAN_MAX_HEIGHT) continue;
						const int ismax = rcClamp((int)std::ceil(smax * ich), ismin + 1, RC_SPAN_MAX_HEIGHT);
						const u8 area = (b.area != area_obstacle && top_cos > job.walkable_cos) ? b.area : (u8)RC_NULL_AREA;
						if (!rcAddSpan(&ctx, *solid, x, z, (unsigned short)ismin, (unsigned short)ismax, area, cfg.walkableClimb))
						{
							fail("box interior spans");
							return;
						}
					}
				}
			}

			if (job.s.filter_flags & filter_low_hanging_obstacles) rcFilterLowHangingWalkableObstacles(&ctx, cfg.walkableClimb, *solid);
			if (job.s.filter_flags & filter_ledge_spans) rcFilterLedgeSpans(&ctx, cfg.walkableHeight, cfg.walkableClimb, *solid);
			if (job.s.filter_flags & filter_low_height_spans) rcFilterWalkableLowHeightSpans(&ctx, cfg.walkableHeight, *solid);

			chf = rcAllocCompactHeightfield();
			if (!chf || !rcBuildCompactHeightfield(&ctx, cfg.walkableHeight, cfg.walkableClimb, *solid, *chf))
			{
				fail("compact heightfield");
				return;
			}
			rcFreeHeightField(solid);
			solid = nullptr;
			if (!rcErodeWalkableArea(&ctx, cfg.walkableRadius, *chf))
			{
				fail("erode");
				return;
			}

			bool regions_ok = false;
			switch (job.s.partition)
			{
			case partition_monotone:
				regions_ok = rcBuildRegionsMonotone(&ctx, *chf, cfg.borderSize, cfg.minRegionArea, cfg.mergeRegionArea);
				break;
			case partition_layers:
				regions_ok = rcBuildLayerRegions(&ctx, *chf, cfg.borderSize, cfg.minRegionArea);
				break;
			default:
				regions_ok = rcBuildDistanceField(&ctx, *chf) && rcBuildRegions(&ctx, *chf, cfg.borderSize, cfg.minRegionArea, cfg.mergeRegionArea);
				break;
			}
			if (!regions_ok)
			{
				fail("regions");
				return;
			}

			cset = rcAllocContourSet();
			if (!cset || !rcBuildContours(&ctx, *chf, cfg.maxSimplificationError, cfg.maxEdgeLen, *cset))
			{
				fail("contours");
				return;
			}
			if (cset->nconts == 0)
			{
				cleanup();
				return;   // nothing walkable in this tile
			}

			pmesh = rcAllocPolyMesh();
			if (!pmesh || !rcBuildPolyMesh(&ctx, *cset, cfg.maxVertsPerPoly, *pmesh))
			{
				fail("polygon mesh");
				return;
			}
			dmesh = rcAllocPolyMeshDetail();
			if (!dmesh || !rcBuildPolyMeshDetail(&ctx, *pmesh, *chf, cfg.detailSampleDist, cfg.detailSampleMaxError, *dmesh))
			{
				fail("detail mesh");
				return;
			}
			if (pmesh->npolys == 0)
			{
				cleanup();
				return;
			}
			if (pmesh->nverts >= 0xffff)
			{
				fail("too many vertices in one tile (lower the tile size)");
				return;
			}
			if (pmesh->npolys > job.max_polys_per_tile)
			{
				fail("too many polygons in one tile (lower the tile size)");
				return;
			}

			// Every polygon Recast emits is walkable (null-area spans are never meshed): all get the "walk" flag, and
			// Recast's default ground area (63) becomes Detour area 0. Custom areas 1..62 keep their id.
			for (int i = 0; i < pmesh->npolys; ++i)
			{
				pmesh->flags[i] = 1;
				if (pmesh->areas[i] == RC_WALKABLE_AREA) pmesh->areas[i] = 0;
			}

			dtNavMeshCreateParams params{};
			params.verts = pmesh->verts;
			params.vertCount = pmesh->nverts;
			params.polys = pmesh->polys;
			params.polyAreas = pmesh->areas;
			params.polyFlags = pmesh->flags;
			params.polyCount = pmesh->npolys;
			params.nvp = pmesh->nvp;
			params.detailMeshes = dmesh->meshes;
			params.detailVerts = dmesh->verts;
			params.detailVertsCount = dmesh->nverts;
			params.detailTris = dmesh->tris;
			params.detailTriCount = dmesh->ntris;
			params.offMeshConCount = 0;
			params.walkableHeight = job.s.agent_height;
			params.walkableRadius = job.s.agent_radius;
			params.walkableClimb = job.s.agent_max_climb;
			params.tileX = tx;
			params.tileY = tz;
			params.tileLayer = 0;
			rcVcopy(params.bmin, pmesh->bmin);
			rcVcopy(params.bmax, pmesh->bmax);
			params.cs = cfg.cs;
			params.ch = cfg.ch;
			params.buildBvTree = true;

			unsigned char* nav_data = nullptr;
			int nav_size = 0;
			if (!dtCreateNavMeshData(&params, &nav_data, &nav_size) || !nav_data || nav_size <= 0)
			{
				fail("dtCreateNavMeshData");
				return;
			}
			result.data.assign(nav_data, nav_data + nav_size);
			dtFree(nav_data);
			result.polys = pmesh->npolys;
			result.verts = pmesh->nverts;
			result.detail_tris = dmesh->ntris;
			cleanup();
		}

		bool sane(const bake_settings& s)
		{
			const f32 f[] = { s.cell_size, s.cell_height, s.agent_height, s.agent_radius, s.agent_max_climb, s.agent_max_slope,
				s.region_min_size, s.region_merge_size, s.edge_max_len, s.edge_max_error, s.detail_sample_dist, s.detail_sample_max_error };
			for (f32 v : f) if (!finite(v)) return false;
			return s.cell_size >= 0.01f && s.cell_size <= 10.0f && s.cell_height >= 0.01f && s.cell_height <= 10.0f
				&& s.agent_height > 0.0f && s.agent_radius >= 0.0f && s.agent_max_climb >= 0.0f
				&& s.agent_max_slope >= 0.0f && s.agent_max_slope < 90.0f;
		}
	}

	s32 bake(const f32* verts, s32 vert_count, const s32* tris, s32 tri_count, const u8* tri_areas,
		const f32* boxes, s32 box_count, const u8* box_areas, const bake_settings* settings, bake_stats* out_stats)
	{
		if (out_stats) *out_stats = bake_stats{};
		bool expected = false;
		if (!g_running.compare_exchange_strong(expected, true)) return bake_error_busy;
		struct running_guard { ~running_guard() { g_running = false; } } guard;
		g_cancel = false;
		g_tiles_done = 0;
		g_tiles_total = 0;

		const auto t0 = std::chrono::steady_clock::now();
		bake_job job{};
		if (settings) job.s = *settings; else default_settings(&job.s);
		bake_settings& s = job.s;
		if (!sane(s)) { log("bake: invalid settings"); return bake_error_bad_settings; }
		s.verts_per_poly = rcClamp(s.verts_per_poly, 3, (int)DT_VERTS_PER_POLYGON);
		s.tile_size = rcClamp(s.tile_size, 16, 1024);
		s.region_min_size = std::max(0.0f, s.region_min_size);
		s.region_merge_size = std::max(0.0f, s.region_merge_size);
		s.edge_max_len = std::max(0.0f, s.edge_max_len);
		s.edge_max_error = std::max(0.1f, s.edge_max_error);
		s.detail_sample_dist = std::max(0.0f, s.detail_sample_dist);
		s.detail_sample_max_error = std::max(0.0f, s.detail_sample_max_error);
		if (s.partition < partition_watershed || s.partition > partition_layers) s.partition = partition_watershed;

		if (vert_count < 0 || tri_count < 0 || box_count < 0) return bake_error_no_geometry;
		if (tri_count > 0 && (!verts || !tris)) return bake_error_no_geometry;
		if (box_count > 0 && !boxes) return bake_error_no_geometry;
		job.verts = verts;
		job.tris = tris;
		job.walkable_cos = std::cos(s.agent_max_slope / 180.0f * k_pi);

		// Validate the triangles, apply the slope test and gather the geometry bounds.
		f32 gmin[3] = { 3.0e38f, 3.0e38f, 3.0e38f }, gmax[3] = { -3.0e38f, -3.0e38f, -3.0e38f };
		auto grow = [&](f32 x, f32 y, f32 z)
		{
			gmin[0] = std::min(gmin[0], x); gmin[1] = std::min(gmin[1], y); gmin[2] = std::min(gmin[2], z);
			gmax[0] = std::max(gmax[0], x); gmax[1] = std::max(gmax[1], y); gmax[2] = std::max(gmax[2], z);
		};
		job.tri_area.assign((size_t)tri_count, (u8)RC_NULL_AREA);
		std::vector<s32> tri_index;   // triangles that passed validation
		tri_index.reserve((size_t)tri_count);
		for (s32 t = 0; t < tri_count; ++t)
		{
			const s32* ti = &tris[t * 3];
			if (ti[0] < 0 || ti[1] < 0 || ti[2] < 0 || ti[0] >= vert_count || ti[1] >= vert_count || ti[2] >= vert_count) continue;
			const f32* a = &verts[ti[0] * 3];
			const f32* b = &verts[ti[1] * 3];
			const f32* c = &verts[ti[2] * 3];
			bool ok = true;
			for (int k = 0; k < 3; ++k) ok = ok && finite(a[k]) && finite(b[k]) && finite(c[k]);
			if (!ok) continue;
			const u8 user = tri_areas ? (u8)std::min<u32>(tri_areas[t], area_ground) : area_ground;
			job.tri_area[(size_t)t] = slope_area({ a[0], a[1], a[2] }, { b[0], b[1], b[2] }, { c[0], c[1], c[2] }, user, job.walkable_cos);
			grow(a[0], a[1], a[2]); grow(b[0], b[1], b[2]); grow(c[0], c[1], c[2]);
			tri_index.push_back(t);
		}

		job.boxes.reserve((size_t)box_count);
		for (s32 i = 0; i < box_count; ++i)
		{
			solid_box b{};
			const u8 user = box_areas ? (u8)std::min<u32>(box_areas[i], area_ground) : area_ground;
			if (!prepare_box(&boxes[i * box_floats], user, job.walkable_cos, b)) continue;
			grow(b.bmin.x, b.bmin.y, b.bmin.z);
			grow(b.bmax.x, b.bmax.y, b.bmax.z);
			job.boxes.push_back(b);
		}
		if (tri_index.empty() && job.boxes.empty())
		{
			log("bake: no geometry");
			return bake_error_no_geometry;
		}

		// Bounds: explicit (min < max on every axis) or the geometry's.
		const bool explicit_bounds = s.bounds_min[0] < s.bounds_max[0] && s.bounds_min[1] < s.bounds_max[1] && s.bounds_min[2] < s.bounds_max[2];
		for (int k = 0; k < 3; ++k)
		{
			job.bmin[k] = explicit_bounds ? s.bounds_min[k] : gmin[k];
			job.bmax[k] = explicit_bounds ? s.bounds_max[k] : gmax[k];
		}
		// A flat world still needs a y range for the spans; keep the tile grid aligned to the cell size.
		if (job.bmax[1] - job.bmin[1] < s.cell_height * 4.0f) job.bmax[1] = job.bmin[1] + s.cell_height * 4.0f;

		int gw = 0, gh = 0;
		rcCalcGridSize(job.bmin, job.bmax, s.cell_size, &gw, &gh);
		job.tiles_x = std::max(1, (gw + s.tile_size - 1) / s.tile_size);
		job.tiles_z = std::max(1, (gh + s.tile_size - 1) / s.tile_size);
		const s32 tile_total = job.tiles_x * job.tiles_z;
		const int tile_bits = rcMin((int)dtIlog2(dtNextPow2((unsigned int)tile_total)), 14);
		if (tile_total > (1 << 14))
		{
			log("bake: %d x %d tiles is more than the 16384 a navmesh supports - raise the tile size or the cell size", job.tiles_x, job.tiles_z);
			return bake_error_too_many_tiles;
		}
		const int poly_bits = 22 - tile_bits;
		job.max_polys_per_tile = 1 << poly_bits;
		job.tile_world = s.tile_size * s.cell_size;

		rcConfig& cfg = job.base;
		std::memset(&cfg, 0, sizeof(cfg));
		cfg.cs = s.cell_size;
		cfg.ch = s.cell_height;
		cfg.walkableSlopeAngle = s.agent_max_slope;
		cfg.walkableHeight = std::max(3, (int)std::ceil(s.agent_height / cfg.ch));
		cfg.walkableClimb = std::min(cfg.walkableHeight - 1, (int)std::floor(s.agent_max_climb / cfg.ch));
		cfg.walkableRadius = (int)std::ceil(s.agent_radius / cfg.cs);
		cfg.maxEdgeLen = (int)(s.edge_max_len / s.cell_size);
		cfg.maxSimplificationError = s.edge_max_error;
		cfg.minRegionArea = (int)rcSqr(s.region_min_size);
		cfg.mergeRegionArea = (int)rcSqr(s.region_merge_size);
		cfg.maxVertsPerPoly = s.verts_per_poly;
		cfg.tileSize = s.tile_size;
		cfg.borderSize = cfg.walkableRadius + 3;
		cfg.width = cfg.tileSize + cfg.borderSize * 2;
		cfg.height = cfg.tileSize + cfg.borderSize * 2;
		cfg.detailSampleDist = s.detail_sample_dist < 0.9f ? 0.0f : s.cell_size * s.detail_sample_dist;
		cfg.detailSampleMaxError = s.cell_height * s.detail_sample_max_error;

		// Bin the geometry into the tiles whose (border-expanded) bounds it overlaps.
		const f32 border = cfg.borderSize * cfg.cs;
		job.tile_tris.assign((size_t)tile_total, {});
		job.tile_boxes.assign((size_t)tile_total, {});
		auto tile_range = [&](f32 minx, f32 maxx, f32 minz, f32 maxz, int& tx0, int& tx1, int& tz0, int& tz1) -> bool
		{
			tx0 = (int)std::floor((minx - border - job.bmin[0]) / job.tile_world);
			tx1 = (int)std::floor((maxx + border - job.bmin[0]) / job.tile_world);
			tz0 = (int)std::floor((minz - border - job.bmin[2]) / job.tile_world);
			tz1 = (int)std::floor((maxz + border - job.bmin[2]) / job.tile_world);
			if (tx1 < 0 || tz1 < 0 || tx0 >= job.tiles_x || tz0 >= job.tiles_z) return false;
			tx0 = std::max(tx0, 0); tz0 = std::max(tz0, 0);
			tx1 = std::min(tx1, job.tiles_x - 1); tz1 = std::min(tz1, job.tiles_z - 1);
			return true;
		};
		for (s32 t : tri_index)
		{
			const s32* ti = &tris[t * 3];
			f32 minx = 3.0e38f, maxx = -3.0e38f, minz = 3.0e38f, maxz = -3.0e38f;
			for (int k = 0; k < 3; ++k)
			{
				const f32* v = &verts[ti[k] * 3];
				minx = std::min(minx, v[0]); maxx = std::max(maxx, v[0]);
				minz = std::min(minz, v[2]); maxz = std::max(maxz, v[2]);
			}
			int tx0, tx1, tz0, tz1;
			if (!tile_range(minx, maxx, minz, maxz, tx0, tx1, tz0, tz1)) continue;
			for (int tz = tz0; tz <= tz1; ++tz)
				for (int tx = tx0; tx <= tx1; ++tx) job.tile_tris[(size_t)(tz * job.tiles_x + tx)].push_back(t);
		}
		for (s32 i = 0; i < (s32)job.boxes.size(); ++i)
		{
			const solid_box& b = job.boxes[(size_t)i];
			int tx0, tx1, tz0, tz1;
			if (!tile_range(b.bmin.x, b.bmax.x, b.bmin.z, b.bmax.z, tx0, tx1, tz0, tz1)) continue;
			for (int tz = tz0; tz <= tz1; ++tz)
				for (int tx = tx0; tx <= tx1; ++tx) job.tile_boxes[(size_t)(tz * job.tiles_x + tx)].push_back(i);
		}

		// Build the tiles on a worker pool.
		job.out.assign((size_t)tile_total, {});
		g_tiles_total = tile_total;
		std::atomic<s32> next{ 0 };
		auto worker = [&]()
		{
			for (;;)
			{
				if (g_cancel) return;
				const s32 i = next.fetch_add(1);
				if (i >= tile_total) return;
				build_tile(job, i % job.tiles_x, i / job.tiles_x, job.out[(size_t)i]);
				g_tiles_done.fetch_add(1);
			}
		};
		unsigned hw = std::thread::hardware_concurrency();
		int thread_count = (int)std::max(1u, std::min(hw == 0 ? 4u : hw, 16u));
		thread_count = std::min(thread_count, tile_total);
		std::vector<std::thread> pool;
		for (int i = 1; i < thread_count; ++i) pool.emplace_back(worker);
		worker();
		for (auto& th : pool) th.join();
		if (g_cancel)
		{
			log("bake: cancelled");
			return bake_error_cancelled;
		}

		// Assemble the blob.
		bake_stats st{};
		st.tiles_x = job.tiles_x;
		st.tiles_z = job.tiles_z;
		st.input_triangles = (s32)tri_index.size();
		st.input_boxes = (s32)job.boxes.size();
		s32 failed = 0;
		size_t total = sizeof(format::file_header);
		for (s32 i = 0; i < tile_total; ++i)
		{
			const auto& o = job.out[(size_t)i];
			if (o.failed)
			{
				if (failed++ < 8) log("bake: tile (%d, %d) failed: %s", i % job.tiles_x, i / job.tiles_x, o.error.c_str());
				continue;
			}
			if (o.data.empty()) continue;
			st.tile_count++;
			st.poly_count += o.polys;
			st.vert_count += o.verts;
			st.detail_tri_count += o.detail_tris;
			total += sizeof(format::tile_header) + (size_t)format::padded((s32)o.data.size());
		}
		if (st.tile_count == 0)
		{
			log("bake: nothing walkable (%d triangles, %d boxes, %d failed tiles)", st.input_triangles, st.input_boxes, failed);
			return failed > 0 ? bake_error_failed : bake_error_empty;
		}
		if (total > (size_t)0x7fffffff) return bake_error_failed;
		for (int k = 0; k < 3; ++k) { st.bounds_min[k] = job.bmin[k]; st.bounds_max[k] = job.bmax[k]; }
		st.data_size = (s32)total;

		std::vector<u8> blob(total, 0);
		format::file_header hdr{};
		hdr.magic = data_magic;
		hdr.version = data_version;
		hdr.settings = s;
		hdr.orig[0] = job.bmin[0]; hdr.orig[1] = job.bmin[1]; hdr.orig[2] = job.bmin[2];
		hdr.tile_width = job.tile_world;
		hdr.tile_height = job.tile_world;
		hdr.max_tiles = 1 << tile_bits;
		hdr.max_polys = 1 << poly_bits;
		hdr.tile_count = st.tile_count;
		size_t off = sizeof(hdr);
		for (s32 i = 0; i < tile_total; ++i)
		{
			const auto& o = job.out[(size_t)i];
			if (o.failed || o.data.empty()) continue;
			format::tile_header th{ i % job.tiles_x, i / job.tiles_x, 0, (s32)o.data.size() };
			std::memcpy(&blob[off], &th, sizeof(th));
			off += sizeof(th);
			std::memcpy(&blob[off], o.data.data(), o.data.size());
			off += (size_t)format::padded((s32)o.data.size());
		}
		const auto t1 = std::chrono::steady_clock::now();
		st.bake_ms = (f32)std::chrono::duration<double, std::milli>(t1 - t0).count();
		hdr.stats = st;
		std::memcpy(blob.data(), &hdr, sizeof(hdr));

		{
			std::lock_guard<std::mutex> lock(g_result_mutex);
			g_result.swap(blob);
		}
		if (out_stats) *out_stats = st;
		log("baked %d/%d tiles, %d polygons, %d vertices from %d triangles + %d boxes in %.1f ms (%d bytes%s)",
			st.tile_count, tile_total, st.poly_count, st.vert_count, st.input_triangles, st.input_boxes, st.bake_ms, st.data_size,
			failed > 0 ? ", some tiles failed" : "");
		return st.data_size;
	}

	f32 bake_progress()
	{
		const s32 total = g_tiles_total.load();
		if (total <= 0) return g_running ? 0.0f : 1.0f;
		return std::min(1.0f, (f32)g_tiles_done.load() / (f32)total);
	}

	void bake_cancel() { if (g_running) g_cancel = true; }

	s32 bake_result_copy(u8* out, s32 max_bytes)
	{
		std::lock_guard<std::mutex> lock(g_result_mutex);
		const s32 size = (s32)g_result.size();
		if (!out || max_bytes <= 0) return size;
		const s32 n = std::min(size, max_bytes);
		if (n > 0) std::memcpy(out, g_result.data(), (size_t)n);
		return n;
	}

	void bake_result_clear()
	{
		std::lock_guard<std::mutex> lock(g_result_mutex);
		std::vector<u8>().swap(g_result);
	}
}

#else // !VORTEX_HAS_RECAST — the stub (Visual Studio projects, -DVORTEX_ENABLE_RECAST=OFF)

namespace vortex::navigation {

	s32 bake(const f32*, s32, const s32*, s32, const u8*, const f32*, s32, const u8*, const bake_settings*, bake_stats* out_stats)
	{
		if (out_stats) *out_stats = bake_stats{};
		return bake_error_unavailable;
	}
	f32  bake_progress() { return 0.0f; }
	void bake_cancel() {}
	s32  bake_result_copy(u8*, s32) { return 0; }
	void bake_result_clear() {}
}

#endif
