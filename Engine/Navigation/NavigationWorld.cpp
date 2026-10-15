#include "NavigationWorld.h"
#include "NavigationFormat.h"

// ============================================================================
// The runtime navigation world: the loaded tiled navmesh (dtNavMesh), one query object for path / point / raycast
// queries and a DetourCrowd for agents. See NavigationWorld.h for the contract; baking lives in
// NavigationBake.cpp. The #else branch at the bottom is the stub the Visual Studio projects (and
// -DVORTEX_ENABLE_RECAST=OFF) compile. Poly refs are 64-bit (#517) and never leave this file: queries and the
// crowd take and hand back world positions only.
// ============================================================================

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#if VORTEX_HAS_RECAST

#include <DetourAlloc.h>
#include <DetourCommon.h>
#include <DetourCrowd.h>
#include <DetourNavMesh.h>
#include <DetourNavMeshQuery.h>

#include "NavigationRebuild.h"

namespace vortex::navigation {

	// .vnav version 2 is Detour tile data with 64-bit poly refs (#517, NavigationFormat.h); Engine/CMakeLists.txt builds
	// Detour and everything that links it with DT_POLYREF64. A build without it would misread every tile.
	static_assert(sizeof(dtPolyRef) == 8, "Vortex navigation needs recastnavigation built with DT_POLYREF64 (#517)");

	namespace {

		void log(const char* fmt, ...)
		{
			char text[512];
			va_list args;
			va_start(args, fmt);
			std::vsnprintf(text, sizeof(text), fmt, args);
			va_end(args);
			std::string line{ "[navigation] " };
			line += text;
			line += '\n';
			platform::debug_output(line.c_str());
		}

		// sizeof(dtLink) with 32-bit poly refs: ref (4) + next (4) + edge, side, bmin, bmax (1 each) — .vnav version 1.
		constexpr size_t k_link_bytes_ref32{ 12 };

		// The byte layout of a Detour tile whose links are link_bytes each: the sections of dtCreateNavMeshData /
		// dtNavMesh::addTile (header, vertices, polygons, links, detail meshes, detail vertices, detail triangles, BV
		// tree, off-mesh connections), every one 4-byte aligned. False when the header is not a Detour tile this build
		// reads: magic, version, a negative count, or counts that dtCreateNavMeshData never writes together — addTile
		// writes links[maxLinkCount - 1], every ground polygon has one detail mesh, the BV tree has two nodes per ground
		// polygon or none.
		struct tile_layout { size_t links_at, links_size, total; };
		bool layout_of(const dtMeshHeader& h, size_t link_bytes, tile_layout& out)
		{
			if (h.magic != DT_NAVMESH_MAGIC || h.version != DT_NAVMESH_VERSION) return false;
			const int counts[] = { h.vertCount, h.polyCount, h.maxLinkCount, h.detailMeshCount, h.detailVertCount,
				h.detailTriCount, h.bvNodeCount, h.offMeshConCount };
			for (int c : counts) if (c < 0) return false;
			if (h.maxLinkCount < 1 || h.detailMeshCount + h.offMeshConCount != h.polyCount
				|| (h.bvNodeCount != 0 && h.bvNodeCount != 2 * h.detailMeshCount)) return false;
			auto a4 = [](size_t n) { return (n + 3) & ~(size_t)3; };
			out.links_at = a4(sizeof(dtMeshHeader)) + a4(sizeof(f32) * 3 * (size_t)h.vertCount) + a4(sizeof(dtPoly) * (size_t)h.polyCount);
			out.links_size = a4(link_bytes * (size_t)h.maxLinkCount);
			out.total = out.links_at + out.links_size + a4(sizeof(dtPolyDetail) * (size_t)h.detailMeshCount)
				+ a4(sizeof(f32) * 3 * (size_t)h.detailVertCount) + a4(4 * (size_t)h.detailTriCount)
				+ a4(sizeof(dtBVNode) * (size_t)h.bvNodeCount) + a4(sizeof(dtOffMeshConnection) * (size_t)h.offMeshConCount);
			return true;
		}

		// The indices addTile and every query follow unchecked: a polygon's vertices (below vertCount) and neighbours
		// inside the tile (below polyCount), each ground polygon's slice of the detail mesh and its triangles' vertices,
		// and the BV tree's polygons and escape offsets. tile = laid out for this build (load_tile); a broken one is
		// refused instead of read out of bounds — a flipped bit in any of them crashed the queries or debug_triangles.
		bool indices_valid(const unsigned char* tile, const dtMeshHeader& h)
		{
			auto a4 = [](size_t n) { return (n + 3) & ~(size_t)3; };
			tile_layout l{};
			layout_of(h, sizeof(dtLink), l);
			const size_t polys_at = a4(sizeof(dtMeshHeader)) + a4(sizeof(f32) * 3 * (size_t)h.vertCount);
			const size_t details_at = l.links_at + l.links_size;
			const size_t tris_at = details_at + a4(sizeof(dtPolyDetail) * (size_t)h.detailMeshCount) + a4(sizeof(f32) * 3 * (size_t)h.detailVertCount);
			const size_t bv_at = tris_at + a4(4 * (size_t)h.detailTriCount);
			for (int i = 0; i < h.polyCount; ++i)
			{
				dtPoly p;
				std::memcpy(&p, tile + polys_at + sizeof(dtPoly) * (size_t)i, sizeof(p));
				if (p.vertCount == 0 || p.vertCount > DT_VERTS_PER_POLYGON) return false;
				for (int j = 0; j < p.vertCount; ++j)
				{
					if (p.verts[j] >= h.vertCount) return false;
					if (p.neis[j] != 0 && !(p.neis[j] & DT_EXT_LINK) && p.neis[j] > h.polyCount) return false;   // neis = index + 1
				}
				if (i >= h.detailMeshCount) continue;   // off-mesh connections have no detail mesh
				dtPolyDetail d;
				std::memcpy(&d, tile + details_at + sizeof(dtPolyDetail) * (size_t)i, sizeof(d));
				if ((size_t)d.vertBase + d.vertCount > (size_t)h.detailVertCount || (size_t)d.triBase + d.triCount > (size_t)h.detailTriCount) return false;
				for (unsigned t = 0; t < d.triCount; ++t)
				{
					const unsigned char* v = tile + tris_at + 4 * ((size_t)d.triBase + t);   // polygon vertices, then the detail mesh's own
					if (v[0] >= p.vertCount + d.vertCount || v[1] >= p.vertCount + d.vertCount || v[2] >= p.vertCount + d.vertCount) return false;
				}
			}
			for (int i = 0; i < h.bvNodeCount; ++i)
			{
				dtBVNode n;
				std::memcpy(&n, tile + bv_at + sizeof(dtBVNode) * (size_t)i, sizeof(n));
				// a leaf names a ground polygon; an inner node's escape offset (-i) lands inside the tree or just past it
				if (n.i >= 0 ? n.i >= h.detailMeshCount : (s64)i - (s64)n.i > (s64)h.bvNodeCount) return false;
			}
			return true;
		}

		// A copy of one stored tile in the layout this build's dtNavMesh::addTile expects (dtAlloc'd; the navmesh frees
		// it), or null when the bytes are not a tile of that .vnav version: the size the header's counts give must be the
		// stored size exactly, so a tile of the other ref width is refused, never misread (#517). Version 1 tiles keep
		// every section; only the links section — runtime state, rebuilt by addTile — grows to this build's dtLink.
		unsigned char* load_tile(const u8* src, size_t size, bool ref32, int& out_size)
		{
			if (size < sizeof(dtMeshHeader)) return nullptr;
			dtMeshHeader h{};
			std::memcpy(&h, src, sizeof(h));
			tile_layout stored{}, now{};
			if (!layout_of(h, ref32 ? k_link_bytes_ref32 : sizeof(dtLink), stored) || stored.total != size) return nullptr;
			layout_of(h, sizeof(dtLink), now);
			if (now.total > 0x7fffffff) return nullptr;
			unsigned char* tile = (unsigned char*)dtAlloc(now.total, DT_ALLOC_PERM);
			if (!tile) return nullptr;
			std::memcpy(tile, src, stored.links_at);
			std::memset(tile + now.links_at, 0, now.links_size);
			std::memcpy(tile + now.links_at + now.links_size, src + stored.links_at + stored.links_size,
				stored.total - stored.links_at - stored.links_size);
			if (!indices_valid(tile, h))
			{
				dtFree(tile);
				return nullptr;
			}
			out_size = (int)now.total;
			return tile;
		}

		constexpr int k_query_nodes{ 4096 };
		constexpr int k_max_path_polys{ 1024 };
		constexpr int k_max_straight{ 512 };
		constexpr int k_max_state_corners{ 64 };
		constexpr unsigned short k_walk_flag{ 1 };
		// #435: a polygon under an active carving obstacle. Every filter excludes it: paths and agents go around, its edge
		// is a wall for the crowd's local boundary.
		constexpr unsigned short k_blocked_flag{ 2 };
		const f32 k_default_extents[3] = { 2.0f, 4.0f, 2.0f };

		// ---- #435: dynamic obstacles and runtime tile rebuilds ------------------------------------------------------------
		constexpr int k_max_proxies{ 8 };   // avoidance circles per obstacle

		struct obstacle_slot
		{
			s32 shape{ obstacle_box };
			f32 center[3]{}, half[3]{}, quat[4]{ 0.0f, 0.0f, 0.0f, 1.0f };
			f32 velocity[3]{};
			u32 flags{ 0 };
			// the carve the tiles are (being) built with
			bool carved{ false };
			f32 carved_center[3]{}, carved_half[3]{}, carved_quat[4]{ 0.0f, 0.0f, 0.0f, 1.0f };
			rebuild::carve_volume volume{};       // world space, grown by the agent radius
			f32 vmin[3]{}, vmax[3]{};             // its AABB
			u8 area{ area_carved };               // the carve area of its polygons
			std::vector<int> proxies;             // crowd slots of its avoidance circles
			u16 generation{ 0 };
			bool used{ false };
		};

		struct tile_job
		{
			s32 tx{ 0 }, tz{ 0 };
			u64 version{ 0 };
			std::shared_ptr<const rebuild::source> src;
			std::vector<rebuild::carve_volume> carves;
		};

		struct tile_result
		{
			s32 tx{ 0 }, tz{ 0 };
			u64 version{ 0 };
			std::vector<u8> data;
			bool ok{ false };
			f32 ms{ 0.0f };
			std::string error;
		};

		// One thread builds the tiles: a tile takes milliseconds, so the game thread never waits for one; it only swaps the
		// finished ones in (rebuild_update). The thread lives while there are jobs — it ends when they run out (kick starts
		// the next) — so no thread is left to join when the library goes away.
		struct tile_worker
		{
			std::thread thread;
			std::mutex mutex;
			std::deque<tile_job> jobs;
			std::deque<tile_result> results;
			bool running{ false };
			bool stop{ false };

			~tile_worker() { shutdown(); }

			// after jobs were queued: a thread unless one is working through them
			void kick()
			{
				{
					std::lock_guard<std::mutex> lock(mutex);
					if (running || jobs.empty()) return;
					running = true;
				}
				if (thread.joinable()) thread.join();   // the previous one, done
				thread = std::thread([this] { run(); });
			}

			// drops what is queued or done; a build in progress finishes first
			void shutdown()
			{
				{
					std::lock_guard<std::mutex> lock(mutex);
					stop = true;
					jobs.clear();
				}
				if (thread.joinable()) thread.join();
				std::lock_guard<std::mutex> lock(mutex);
				results.clear();
				running = false;
				stop = false;
			}

			void run()
			{
				for (;;)
				{
					tile_job job;
					{
						std::lock_guard<std::mutex> lock(mutex);
						if (stop || jobs.empty()) { running = false; return; }
						job = std::move(jobs.front());
						jobs.pop_front();
					}
					tile_result r;
					r.tx = job.tx;
					r.tz = job.tz;
					r.version = job.version;
					const auto t0 = std::chrono::steady_clock::now();
					r.ok = job.src && rebuild::build_tile(*job.src, job.tx, job.tz, job.carves, r.data, r.error);
					r.ms = (f32)std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
					std::lock_guard<std::mutex> lock(mutex);
					results.push_back(std::move(r));
				}
			}
		};

		struct world_state
		{
			dtNavMesh* mesh{ nullptr };
			dtNavMeshQuery* query{ nullptr };
			dtCrowd* crowd{ nullptr };
			dtQueryFilter filter;
			format::file_header header{};
			std::vector<u8> blob;                // the loaded data (data_copy hands it back)
			u16 generation[max_agents]{};
			bool proxy[max_agents]{};            // #435: crowd slots that are an obstacle's avoidance circles
			f32 replan_wait[max_agents]{};       // #435: seconds before a tile swap may re-plan the agent again

			// #435
			std::shared_ptr<const rebuild::source> source;
			std::vector<obstacle_slot> obstacles;
			std::unordered_map<s64, u64> requested;   // tile -> the version of the latest build asked for
			std::unordered_map<s64, u64> landed;      // tile -> the version in the navmesh (absent = the baked one)
			std::unordered_set<s64> dirty;            // tiles to queue at the next rebuild_update
			std::unordered_map<s64, u64> queued;      // tile -> the version queued on the worker, not swapped in yet
			u64 serial{ 0 };
			rebuild_stats stats{};
			s32 revision{ 0 };                        // never reset: a load is a change too
			bool pool_full_logged{ false };
			tile_worker worker;                       // last: its thread stops before the rest goes
		};
		world_state g;

		u32 g_rand_state{ 0x9E3779B9u };
		float frand()
		{
			// xorshift32 -> [0, 1)
			u32 x = g_rand_state;
			x ^= x << 13; x ^= x >> 17; x ^= x << 5;
			g_rand_state = x ? x : 0x9E3779B9u;
			return (float)(x >> 8) * (1.0f / 16777216.0f);
		}

		inline const f32* ext(const f32* e) { return e ? e : k_default_extents; }
		inline bool finite3(const f32* v) { return v && v[0] == v[0] && v[1] == v[1] && v[2] == v[2] && std::fabs(v[0]) < 3.0e38f && std::fabs(v[1]) < 3.0e38f && std::fabs(v[2]) < 3.0e38f; }

		bool nearest(const f32* pos, const f32* extents, dtPolyRef& ref, f32* out)
		{
			ref = 0;
			if (!g.query || !finite3(pos)) return false;
			dtVcopy(out, pos);
			if (dtStatusFailed(g.query->findNearestPoly(pos, ext(extents), &g.filter, &ref, out))) { ref = 0; return false; }
			return ref != 0;
		}

		inline u32 make_handle(int idx) { return ((u32)g.generation[idx] << 16) | (u32)(idx + 1); }

		int slot_of(u32 handle)
		{
			if (!g.crowd || handle == 0) return -1;
			const int idx = (int)(handle & 0xFFFFu) - 1;
			if (idx < 0 || idx >= max_agents) return -1;
			if ((handle >> 16) != (u32)g.generation[idx]) return -1;
			const dtCrowdAgent* ag = g.crowd->getAgent(idx);
			return ag && ag->active ? idx : -1;
		}

		void to_params(const agent_params* p, dtCrowdAgentParams& o)
		{
			std::memset(&o, 0, sizeof(o));
			agent_params d{};
			if (!p) { d.radius = 0.4f; d.height = 1.8f; d.max_speed = 3.5f; d.max_acceleration = 8.0f; d.separation_weight = 2.0f; d.avoidance_quality = 3; p = &d; }
			o.radius = std::max(0.05f, p->radius);
			o.height = std::max(0.1f, p->height);
			o.maxAcceleration = std::max(0.0f, p->max_acceleration);
			o.maxSpeed = std::max(0.0f, p->max_speed);
			o.collisionQueryRange = p->collision_query_range > 0.0f ? p->collision_query_range : o.radius * 12.0f;
			o.pathOptimizationRange = p->path_optimization_range > 0.0f ? p->path_optimization_range : o.radius * 30.0f;
			o.separationWeight = std::max(0.0f, p->separation_weight);
			o.updateFlags = p->update_flags != 0 ? (unsigned char)(p->update_flags & 0xFFu)
				: (unsigned char)(DT_CROWD_ANTICIPATE_TURNS | DT_CROWD_OPTIMIZE_VIS | DT_CROWD_OPTIMIZE_TOPO | DT_CROWD_OBSTACLE_AVOIDANCE | DT_CROWD_SEPARATION);
			o.obstacleAvoidanceType = (unsigned char)std::clamp(p->avoidance_quality, 0, 3);
			o.queryFilterType = 0;
			o.userData = nullptr;
		}

		void setup_crowd(f32 baked_radius)
		{
			g.crowd = dtAllocCrowd();
			if (!g.crowd) return;
			const f32 max_radius = std::max(1.0f, baked_radius * 2.5f);
			if (!g.crowd->init(max_agents, max_radius, g.mesh))
			{
				log("crowd init failed");
				dtFreeCrowd(g.crowd);
				g.crowd = nullptr;
				return;
			}
			// Obstacle avoidance quality presets (RecastDemo's CrowdTool): low, medium, good, high.
			dtObstacleAvoidanceParams p;
			std::memcpy(&p, g.crowd->getObstacleAvoidanceParams(0), sizeof(p));
			const unsigned char divs[4] = { 5, 5, 7, 7 }, rings[4] = { 2, 2, 2, 3 }, depth[4] = { 1, 2, 3, 3 };
			for (int i = 0; i < 4; ++i)
			{
				p.velBias = 0.5f;
				p.adaptiveDivs = divs[i];
				p.adaptiveRings = rings[i];
				p.adaptiveDepth = depth[i];
				g.crowd->setObstacleAvoidanceParams(i, &p);
			}
			if (dtQueryFilter* f = g.crowd->getEditableFilter(0))
			{
				f->setIncludeFlags(k_walk_flag);
				f->setExcludeFlags(k_blocked_flag);   // #435
			}
		}

		// Remaining corners of an agent's corridor (the crowd itself only looks 4 corners ahead).
		int corridor_corners(int idx, f32* verts, unsigned char* flags, dtPolyRef* polys, int max)
		{
			dtCrowdAgent* ag = g.crowd->getEditableAgent(idx);
			if (!ag || ag->state != DT_CROWDAGENT_STATE_WALKING || ag->corridor.getPathCount() == 0) return 0;
			return ag->corridor.findCorners(verts, flags, polys, max, g.query, &g.filter);
		}
	}

	bool available() { return true; }

	void unload()
	{
		// #435: the builds of this navmesh and its obstacles go with it
		g.worker.shutdown();
		g.source.reset();
		g.obstacles.clear();
		g.requested.clear();
		g.landed.clear();
		g.dirty.clear();
		g.queued.clear();
		std::fill(std::begin(g.proxy), std::end(g.proxy), false);
		std::fill(std::begin(g.replan_wait), std::end(g.replan_wait), 0.0f);
		g.stats = rebuild_stats{};
		g.pool_full_logged = false;
		if (g.crowd) { dtFreeCrowd(g.crowd); g.crowd = nullptr; }
		if (g.query) { dtFreeNavMeshQuery(g.query); g.query = nullptr; }
		if (g.mesh) { dtFreeNavMesh(g.mesh); g.mesh = nullptr; }
		g.header = format::file_header{};
		std::vector<u8>().swap(g.blob);
		for (auto& gen : g.generation) gen = (u16)(gen + 1);   // every old agent handle is stale now
		++g.revision;
	}

	bool loaded() { return g.mesh != nullptr && g.query != nullptr; }

	bool load(const u8* data, s32 size)
	{
		unload();
		if (!data || size < (s32)sizeof(format::file_header)) { log("load: no data"); return false; }
		format::file_header hdr{};
		std::memcpy(&hdr, data, sizeof(hdr));
		if (hdr.magic != data_magic) { log("load: not a navmesh (.vnav) blob"); return false; }
		// #517: version 1 was baked with 32-bit poly refs; its tiles are re-laid out below (load_tile)
		const bool ref32 = hdr.version == format::version_ref32;
		if (hdr.version != data_version && !ref32) { log("load: unsupported .vnav version %d (expected %d) - rebake", hdr.version, data_version); return false; }
		if (hdr.tile_count < 0 || hdr.max_tiles <= 0 || hdr.max_polys <= 0 || !(hdr.tile_width > 0.0f)) { log("load: corrupt header"); return false; }
		// The tile pool is sized by tile_count (tiles are found by their grid position, and with 64-bit refs no ref bits
		// come from the pool size), so tile_count must be what the bytes can hold — every tile is a tile_header and at
		// least a dtMeshHeader. One flipped bit used to allocate gigabytes before the tile table was read. max_tiles sizes
		// nothing any more; version 2 writes it equal to tile_count.
		const size_t max_tiles_in_file = ((size_t)size - sizeof(hdr)) / (sizeof(format::tile_header) + sizeof(dtMeshHeader));
		if ((size_t)hdr.tile_count > max_tiles_in_file || (!ref32 && hdr.max_tiles != hdr.tile_count))
		{
			log("load: corrupt header (%d tiles, max_tiles %d, in %d bytes)", hdr.tile_count, hdr.max_tiles, size);
			return false;
		}

		dtNavMesh* mesh = dtAllocNavMesh();
		if (!mesh) return false;
		dtNavMeshParams params{};
		dtVcopy(params.orig, hdr.orig);
		params.tileWidth = hdr.tile_width;
		params.tileHeight = hdr.tile_height;
		// #435: spare slots for tiles a runtime rebuild creates where the bake had nothing walkable (a wall blown away)
		params.maxTiles = std::max(1, hdr.tile_count) + std::clamp(hdr.tile_count / 4, 16, 4096);
		params.maxPolys = hdr.max_polys;
		if (dtStatusFailed(mesh->init(&params)))
		{
			// Only out of memory. A failed init leaves maxTiles set over a tile array it never allocated (or never cleared),
			// which Detour's destructor would walk: the half-built mesh is dropped rather than crash dtFreeNavMesh.
			log("load: dtNavMesh::init failed for %d tiles (out of memory)", params.maxTiles);
			return false;
		}

		size_t off = sizeof(hdr);
		for (s32 i = 0; i < hdr.tile_count; ++i)
		{
			format::tile_header th{};
			if (off + sizeof(th) > (size_t)size) { dtFreeNavMesh(mesh); log("load: truncated tile table"); return false; }
			std::memcpy(&th, data + off, sizeof(th));
			off += sizeof(th);
			if (th.size <= 0 || off + (size_t)th.size > (size_t)size) { dtFreeNavMesh(mesh); log("load: truncated tile %d", i); return false; }
			int tile_size = 0;
			unsigned char* tile = load_tile(data + off, (size_t)th.size, ref32, tile_size);
			if (!tile)
			{
				dtFreeNavMesh(mesh);
				log("load: tile %d (%d, %d) is not Detour tile data of .vnav version %d - rebake", i, th.tx, th.tz, hdr.version);
				return false;
			}
			if (dtStatusFailed(mesh->addTile(tile, tile_size, DT_TILE_FREE_DATA, 0, nullptr)))
			{
				dtFree(tile);
				dtFreeNavMesh(mesh);
				log("load: tile %d (%d, %d) rejected", i, th.tx, th.tz);
				return false;
			}
			off += (size_t)format::padded(th.size);
		}

		g.mesh = mesh;
		g.query = dtAllocNavMeshQuery();
		if (!g.query || dtStatusFailed(g.query->init(g.mesh, k_query_nodes)))
		{
			log("load: query init failed");
			unload();
			return false;
		}
		g.filter.setIncludeFlags(k_walk_flag);
		g.filter.setExcludeFlags(k_blocked_flag);   // #435
		g.header = hdr;
		g.blob.assign(data, data + size);
		setup_crowd(hdr.settings.agent_radius);
		if (ref32) log("load: .vnav version 1 (32-bit poly refs, %d tiles) re-laid out for 64-bit refs - a rebake stores version %d", hdr.tile_count, data_version);
		return true;
	}

	bool get_info(bake_settings* out_settings, bake_stats* out_stats)
	{
		if (!loaded()) return false;
		if (out_settings) *out_settings = g.header.settings;
		if (out_stats) *out_stats = g.header.stats;
		return true;
	}

	s32 data_copy(u8* out, s32 max_bytes)
	{
		const s32 size = (s32)g.blob.size();
		if (!out || max_bytes <= 0) return size;
		const s32 n = std::min(size, max_bytes);
		if (n > 0) std::memcpy(out, g.blob.data(), (size_t)n);
		return n;
	}

	// ---- queries -------------------------------------------------------------------------------------------

	s32 find_path(const f32* start, const f32* end, const f32* extents, f32* out_points, s32 max_points, s32* out_status)
	{
		if (out_status) *out_status = path_invalid;
		if (!loaded() || !finite3(start) || !finite3(end)) return 0;
		dtPolyRef sref = 0, eref = 0;
		f32 spos[3], epos[3];
		if (!nearest(start, extents, sref, spos) || !nearest(end, extents, eref, epos)) return 0;

		dtPolyRef path[k_max_path_polys];
		int npath = 0;
		const dtStatus st = g.query->findPath(sref, eref, spos, epos, &g.filter, path, &npath, k_max_path_polys);
		if (dtStatusFailed(st) || npath <= 0) return 0;
		const bool partial = path[npath - 1] != eref || dtStatusDetail(st, DT_PARTIAL_RESULT);
		f32 goal[3];
		dtVcopy(goal, epos);
		if (path[npath - 1] != eref) g.query->closestPointOnPoly(path[npath - 1], epos, goal, nullptr);

		static thread_local f32 straight[k_max_straight * 3];
		unsigned char flags[k_max_straight];
		dtPolyRef polys[k_max_straight];
		int nstraight = 0;
		if (dtStatusFailed(g.query->findStraightPath(spos, goal, path, npath, straight, flags, polys, &nstraight, k_max_straight, 0)) || nstraight <= 0)
			return 0;
		if (out_status) *out_status = partial ? path_partial : path_complete;
		if (!out_points || max_points <= 0) return nstraight;
		const int n = std::min(nstraight, (int)max_points);
		std::memcpy(out_points, straight, sizeof(f32) * 3 * (size_t)n);
		if (n < nstraight) dtVcopy(&out_points[(n - 1) * 3], &straight[(nstraight - 1) * 3]);   // keep the goal as the last point
		return n;
	}

	bool closest_point(const f32* pos, const f32* extents, f32* out_point)
	{
		f32 p[3];
		dtPolyRef ref = 0;
		if (!nearest(pos, extents, ref, p)) return false;
		if (out_point) dtVcopy(out_point, p);
		return true;
	}

	bool raycast(const f32* start, const f32* end, const f32* extents, f32* out_hit, f32* out_normal, f32* out_t)
	{
		if (out_t) *out_t = 0.0f;
		if (out_normal) dtVset(out_normal, 0.0f, 0.0f, 0.0f);
		if (!loaded() || !finite3(end)) return false;
		dtPolyRef sref = 0;
		f32 spos[3];
		if (!nearest(start, extents, sref, spos))
		{
			if (out_hit && start) dtVcopy(out_hit, start);
			return true;   // not on the navmesh at all: blocked right away
		}
		f32 t = 0.0f, n[3] = { 0, 0, 0 };
		dtPolyRef path[256];
		int npath = 0;
		if (dtStatusFailed(g.query->raycast(sref, spos, end, &g.filter, &t, n, path, &npath, 256)))
		{
			if (out_hit) dtVcopy(out_hit, spos);
			return true;
		}
		const bool blocked = t <= 1.0f;
		const f32 tt = blocked ? t : 1.0f;
		f32 hit[3];
		dtVlerp(hit, spos, end, tt);
		// The ray walks the surface in 2D: lift the hit onto the last polygon it crossed.
		if (npath > 0)
		{
			f32 h = hit[1];
			if (dtStatusSucceed(g.query->getPolyHeight(path[npath - 1], hit, &h))) hit[1] = h;
		}
		if (out_hit) dtVcopy(out_hit, hit);
		if (out_normal && blocked) dtVcopy(out_normal, n);
		if (out_t) *out_t = tt;
		return blocked;
	}

	bool random_point(f32* out_point)
	{
		if (!loaded() || !out_point) return false;
		dtPolyRef ref = 0;
		f32 pt[3];
		if (dtStatusFailed(g.query->findRandomPoint(&g.filter, frand, &ref, pt)) || !ref) return false;
		dtVcopy(out_point, pt);
		return true;
	}

	bool random_point_around(const f32* center, f32 radius, const f32* extents, f32* out_point)
	{
		if (!loaded() || !out_point || !(radius > 0.0f)) return false;
		dtPolyRef sref = 0;
		f32 spos[3];
		if (!nearest(center, extents, sref, spos)) return false;
		// findRandomPointAroundCircle picks a random polygon (reachable from the centre) touching the circle and a random
		// point in it; large polygons often put that point outside the radius. Retry a few times, then pull the point
		// in along the surface (moveAlongSurface stops at walls, so the result stays reachable and on the navmesh).
		f32 best[3];
		bool any = false, inside = false;
		for (int attempt = 0; attempt < 4 && !inside; ++attempt)
		{
			dtPolyRef ref = 0;
			f32 pt[3];
			if (dtStatusFailed(g.query->findRandomPointAroundCircle(sref, spos, radius, &g.filter, frand, &ref, pt)) || !ref) continue;
			dtVcopy(best, pt);
			any = true;
			inside = dtVdist2DSqr(pt, spos) <= radius * radius;
		}
		if (!any) return false;
		if (!inside)
		{
			const f32 d = std::sqrt(dtVdist2DSqr(best, spos));
			const f32 k = d > 1e-6f ? (radius * 0.999f) / d : 0.0f;
			f32 target[3] = { spos[0] + (best[0] - spos[0]) * k, spos[1], spos[2] + (best[2] - spos[2]) * k };
			f32 moved[3];
			dtPolyRef visited[64];
			int nvisited = 0;
			if (dtStatusSucceed(g.query->moveAlongSurface(sref, spos, target, &g.filter, moved, visited, &nvisited, 64)) && nvisited > 0)
			{
				f32 h = moved[1];
				if (dtStatusSucceed(g.query->getPolyHeight(visited[nvisited - 1], moved, &h))) moved[1] = h;
				dtVcopy(best, moved);
			}
		}
		dtVcopy(out_point, best);
		return true;
	}

	void set_random_seed(u32 seed) { g_rand_state = seed ? seed : 0x9E3779B9u; }

	// ---- debug geometry ------------------------------------------------------------------------------------

	s32 debug_triangles(f32* out, s32 max_floats)
	{
		if (!g.mesh) return 0;
		const dtNavMesh& mesh = *g.mesh;
		s32 written = 0;
		for (int i = 0; i < mesh.getMaxTiles(); ++i)
		{
			const dtMeshTile* tile = mesh.getTile(i);
			if (!tile || !tile->header) continue;
			for (int p = 0; p < tile->header->polyCount; ++p)
			{
				const dtPoly& poly = tile->polys[p];
				if (poly.getType() == DT_POLYTYPE_OFFMESH_CONNECTION) continue;
				if (poly.flags & k_blocked_flag) continue;   // #435: under an active carving obstacle — a hole in the navmesh
				const dtPolyDetail& pd = tile->detailMeshes[p];
				for (int j = 0; j < pd.triCount; ++j)
				{
					const unsigned char* t = &tile->detailTris[(pd.triBase + j) * 4];
					if (out && written + 9 > max_floats) return written;
					for (int k = 0; k < 3; ++k)
					{
						const f32* v = t[k] < poly.vertCount
							? &tile->verts[poly.verts[t[k]] * 3]
							: &tile->detailVerts[(pd.vertBase + t[k] - poly.vertCount) * 3];
						if (out) { out[written] = v[0]; out[written + 1] = v[1]; out[written + 2] = v[2]; }
						written += 3;
					}
				}
			}
		}
		return written;
	}

	s32 debug_lines(f32* out, s32 max_floats, s32 flags)
	{
		if (!g.mesh) return 0;
		if (flags == 0) flags = 1;
		const dtNavMesh& mesh = *g.mesh;
		s32 written = 0;
		for (int i = 0; i < mesh.getMaxTiles(); ++i)
		{
			const dtMeshTile* tile = mesh.getTile(i);
			if (!tile || !tile->header) continue;
			for (int p = 0; p < tile->header->polyCount; ++p)
			{
				const dtPoly& poly = tile->polys[p];
				if (poly.getType() == DT_POLYTYPE_OFFMESH_CONNECTION) continue;
				if (poly.flags & k_blocked_flag) continue;   // #435: a hole while its obstacle is active (its rim is a boundary)
				for (int j = 0; j < poly.vertCount; ++j)
				{
					const unsigned short nei = poly.neis[j];
					bool boundary;
					if (nei == 0) boundary = true;
					else if (nei & DT_EXT_LINK)
					{
						// Tile edge: a boundary unless a link to the neighbouring tile exists for it (then it is an
						// internal edge, drawn from both tiles).
						boundary = true;
						for (unsigned int k = poly.firstLink; k != DT_NULL_LINK; k = tile->links[k].next)
							if (tile->links[k].edge == j)
							{
								unsigned short other = 0;
								boundary = dtStatusSucceed(mesh.getPolyFlags(tile->links[k].ref, &other)) && (other & k_blocked_flag);
								break;
							}
					}
					else if (tile->polys[nei - 1].flags & k_blocked_flag) boundary = true;   // drawn from this side only
					else
					{
						boundary = false;
						if ((int)(nei - 1) < p) continue;   // internal edge inside the tile: draw once
					}
					if (boundary ? !(flags & 1) : !(flags & 2)) continue;
					if (out && written + 6 > max_floats) return written;
					const f32* a = &tile->verts[poly.verts[j] * 3];
					const f32* b = &tile->verts[poly.verts[(j + 1) % poly.vertCount] * 3];
					if (out)
					{
						out[written] = a[0]; out[written + 1] = a[1]; out[written + 2] = a[2];
						out[written + 3] = b[0]; out[written + 4] = b[1]; out[written + 5] = b[2];
					}
					written += 6;
				}
			}
		}
		return written;
	}

	// ---- crowd ---------------------------------------------------------------------------------------------

	u32 agent_add(const f32* pos, const agent_params* params)
	{
		if (!g.crowd || !finite3(pos)) return 0;
		dtCrowdAgentParams p;
		to_params(params, p);
		const int idx = g.crowd->addAgent(pos, &p);
		if (idx < 0) { log("agent_add: the crowd is full (%d agents)", max_agents); return 0; }
		g.generation[idx] = (u16)(g.generation[idx] + 1);
		g.proxy[idx] = false;
		g.replan_wait[idx] = 0.0f;
		return make_handle(idx);
	}

	void agent_remove(u32 agent)
	{
		const int idx = slot_of(agent);
		if (idx >= 0) g.crowd->removeAgent(idx);
	}

	bool agent_valid(u32 agent) { return slot_of(agent) >= 0; }

	bool agent_set_params(u32 agent, const agent_params* params)
	{
		const int idx = slot_of(agent);
		if (idx < 0 || !params) return false;
		dtCrowdAgentParams p;
		to_params(params, p);
		g.crowd->updateAgentParameters(idx, &p);
		return true;
	}

	bool agent_set_target(u32 agent, const f32* target)
	{
		const int idx = slot_of(agent);
		if (idx < 0 || !finite3(target)) return false;
		dtPolyRef ref = 0;
		f32 p[3];
		if (!nearest(target, nullptr, ref, p)) return false;
		return g.crowd->requestMoveTarget(idx, ref, p);
	}

	bool agent_reset_target(u32 agent)
	{
		const int idx = slot_of(agent);
		return idx >= 0 && g.crowd->resetMoveTarget(idx);
	}

	bool agent_set_velocity(u32 agent, const f32* velocity)
	{
		const int idx = slot_of(agent);
		return idx >= 0 && finite3(velocity) && g.crowd->requestMoveVelocity(idx, velocity);
	}

	bool agent_teleport(u32 agent, const f32* pos)
	{
		const int idx = slot_of(agent);
		if (idx < 0 || !finite3(pos)) return false;
		dtCrowdAgent* ag = g.crowd->getEditableAgent(idx);
		// The same reset dtCrowd::addAgent does, without losing the slot (and so the handle).
		f32 p[3];
		dtPolyRef ref = 0;
		dtVcopy(p, pos);
		if (dtStatusFailed(g.query->findNearestPoly(pos, g.crowd->getQueryHalfExtents(), &g.filter, &ref, p)) || !ref)
		{
			dtVcopy(p, pos);
			ref = 0;
		}
		ag->corridor.reset(ref, p);
		ag->boundary.reset();
		ag->partial = false;
		ag->topologyOptTime = 0.0f;
		ag->targetReplanTime = 0.0f;
		ag->nneis = 0;
		ag->ncorners = 0;
		dtVset(ag->dvel, 0, 0, 0);
		dtVset(ag->nvel, 0, 0, 0);
		dtVset(ag->vel, 0, 0, 0);
		dtVcopy(ag->npos, p);
		ag->desiredSpeed = 0.0f;
		ag->state = ref ? DT_CROWDAGENT_STATE_WALKING : DT_CROWDAGENT_STATE_INVALID;
		ag->targetState = DT_CROWDAGENT_TARGET_NONE;
		return ref != 0;
	}

	bool agent_move_position(u32 agent, const f32* pos)
	{
		const int idx = slot_of(agent);
		if (idx < 0 || !finite3(pos)) return false;
		dtCrowdAgent* ag = g.crowd->getEditableAgent(idx);
		if (ag->state != DT_CROWDAGENT_STATE_WALKING) return false;
		if (!ag->corridor.movePosition(pos, g.query, &g.filter)) return false;
		dtVcopy(ag->npos, ag->corridor.getPos());
		return true;
	}

	s32 agent_follow(u32 agent, const f32* pos, const f32* velocity)
	{
		const int idx = slot_of(agent);
		if (idx < 0 || !finite3(pos)) return 0;
		dtCrowdAgent* ag = g.crowd->getEditableAgent(idx);
		// How far the surface walk may end from the entity and still be "where it is": an entity's capsule may stand up to
		// its radius beyond the navmesh, which is eroded by the agent radius (a character against a wall).
		const f32 slack = std::max(0.3f, ag->params.radius * 1.5f);
		constexpr f32 k_follow_reach{ 10.0f };   // farther in one tick is a teleport: no surface walk (it visits 16 polygons at most)
		bool slid = false;
		if (ag->state == DT_CROWDAGENT_STATE_WALKING && dtVdist2DSqr(ag->npos, pos) <= k_follow_reach * k_follow_reach
			&& ag->corridor.movePosition(pos, g.query, &g.filter))
		{
			const f32* walked = ag->corridor.getPos();
			slid = dtVdist2DSqr(walked, pos) <= slack * slack;
			if (!slid)
			{
				// short of the entity: just off the navmesh next to where the walk ended (a slide), or somewhere the walk
				// cannot reach — the far side of a wall, another floor (placed there below)
				dtPolyRef ref = 0;
				f32 nearest[3];
				slid = dtStatusSucceed(g.query->findNearestPoly(pos, g.crowd->getQueryHalfExtents(), &g.filter, &ref, nearest)) && ref != 0
					&& dtVdist2DSqr(nearest, walked) <= slack * slack && std::fabs(nearest[1] - walked[1]) <= ag->params.height * 0.5f;
			}
			dtVcopy(ag->npos, walked);
		}
		if (!slid)
		{
			// placed where the entity is; the target (or a velocity request) survives the reset
			const bool target = ag->targetState != DT_CROWDAGENT_TARGET_NONE && ag->targetState != DT_CROWDAGENT_TARGET_FAILED
				&& ag->targetState != DT_CROWDAGENT_TARGET_VELOCITY && ag->targetRef != 0;
			const bool steered = ag->targetState == DT_CROWDAGENT_TARGET_VELOCITY;
			const dtPolyRef target_ref = ag->targetRef;
			f32 target_pos[3];
			dtVcopy(target_pos, ag->targetPos);   // (the requested velocity under DT_CROWDAGENT_TARGET_VELOCITY)
			if (!agent_teleport(agent, pos)) return 0;
			if (target) g.crowd->requestMoveTarget(idx, target_ref, target_pos);
			else if (steered) g.crowd->requestMoveVelocity(idx, target_pos);
		}
		if (finite3(velocity)) dtVcopy(ag->vel, velocity);   // what the entity really does: others avoid that, steering starts from it
		return slid ? 1 : 2;
	}

	bool agent_get_state(u32 agent, agent_state* out)
	{
		const int idx = slot_of(agent);
		if (idx < 0 || !out) return false;
		const dtCrowdAgent* ag = g.crowd->getAgent(idx);
		agent_state s{};
		dtVcopy(s.position, ag->npos);
		dtVcopy(s.velocity, ag->vel);
		dtVcopy(s.desired_velocity, ag->nvel);   // #434: after local avoidance (dvel is the path-following part before it)
		s.on_navmesh = ag->state == DT_CROWDAGENT_STATE_WALKING ? 1 : 0;
		s.partial = ag->partial ? 1 : 0;
		switch (ag->targetState)
		{
		case DT_CROWDAGENT_TARGET_NONE: s.move_state = move_idle; break;
		case DT_CROWDAGENT_TARGET_FAILED: s.move_state = move_failed; break;
		case DT_CROWDAGENT_TARGET_VALID: s.move_state = move_moving; break;
		case DT_CROWDAGENT_TARGET_VELOCITY: s.move_state = move_velocity; break;
		default: s.move_state = move_pending; break;
		}
		if (ag->targetState == DT_CROWDAGENT_TARGET_VELOCITY || ag->targetState == DT_CROWDAGENT_TARGET_NONE) dtVcopy(s.target, ag->npos);
		else dtVcopy(s.target, ag->targetPos);
		dtVcopy(s.next_corner, ag->ncorners > 0 ? ag->cornerVerts : ag->npos);
		s.remaining_distance = -1.0f;
		if (ag->targetState == DT_CROWDAGENT_TARGET_VALID)
		{
			f32 verts[k_max_state_corners * 3];
			unsigned char flags[k_max_state_corners];
			dtPolyRef polys[k_max_state_corners];
			const int n = corridor_corners(idx, verts, flags, polys, k_max_state_corners);
			f32 dist = 0.0f;
			const f32* prev = ag->npos;
			for (int i = 0; i < n; ++i)
			{
				dist += dtVdist(prev, &verts[i * 3]);
				prev = &verts[i * 3];
			}
			// Corners cut off by the buffer: the rest as the crow flies.
			if (n == 0 || !(flags[n - 1] & DT_STRAIGHTPATH_END)) dist += dtVdist(prev, ag->corridor.getTarget());
			s.remaining_distance = dist;
			s.corner_count = n;
			if (n > 0) dtVcopy(s.next_corner, verts);
		}
		*out = s;
		return true;
	}

	s32 agent_get_corners(u32 agent, f32* out_points, s32 max_points)
	{
		const int idx = slot_of(agent);
		if (idx < 0 || !out_points || max_points <= 0) return 0;
		const dtCrowdAgent* ag = g.crowd->getAgent(idx);
		if (ag->targetState != DT_CROWDAGENT_TARGET_VALID) return 0;
		const int cap = std::min((int)max_points, k_max_straight);
		std::vector<f32> verts((size_t)cap * 3);
		std::vector<unsigned char> flags((size_t)cap);
		std::vector<dtPolyRef> polys((size_t)cap);
		const int n = corridor_corners(idx, verts.data(), flags.data(), polys.data(), cap);
		if (n > 0) std::memcpy(out_points, verts.data(), sizeof(f32) * 3 * (size_t)n);
		return n;
	}

	// ---- dynamic obstacles and runtime tile rebuilds (#435) --------------------------------------------------------------

	namespace {

		s64 tile_key(s32 tx, s32 tz) { return ((s64)tz << 32) | (s64)(u32)tx; }

		// v' = q v q* (Hamilton convention, as the bake's boxes)
		void rotate_q(const f32* q, const f32* v, f32* out)
		{
			const f32 tx = 2.0f * (q[1] * v[2] - q[2] * v[1]);
			const f32 ty = 2.0f * (q[2] * v[0] - q[0] * v[2]);
			const f32 tz = 2.0f * (q[0] * v[1] - q[1] * v[0]);
			out[0] = v[0] + q[3] * tx + (q[1] * tz - q[2] * ty);
			out[1] = v[1] + q[3] * ty + (q[2] * tx - q[0] * tz);
			out[2] = v[2] + q[3] * tz + (q[0] * ty - q[1] * tx);
		}

		struct pt2 { f32 x, z; };

		// Convex hull of n points (x, z) — Andrew's monotone chain, counter-clockwise; returns the corner count (out holds
		// n + 1 points).
		int hull_2d(pt2* pts, int n, pt2* out)
		{
			std::sort(pts, pts + n, [](const pt2& a, const pt2& b) { return a.x < b.x || (a.x == b.x && a.z < b.z); });
			auto cross = [](const pt2& o, const pt2& a, const pt2& b) { return (a.x - o.x) * (b.z - o.z) - (a.z - o.z) * (b.x - o.x); };
			int k = 0;
			for (int i = 0; i < n; ++i)
			{
				while (k >= 2 && cross(out[k - 2], out[k - 1], pts[i]) <= 1.0e-9f) --k;
				out[k++] = pts[i];
			}
			for (int i = n - 2, lower = k + 1; i >= 0; --i)
			{
				while (k >= lower && cross(out[k - 2], out[k - 1], pts[i]) <= 1.0e-9f) --k;
				out[k++] = pts[i];
			}
			return std::max(0, k - 1);   // the last point repeats the first
		}

		bool same3(const f32* a, const f32* b, f32 eps) { return std::fabs(a[0] - b[0]) <= eps && std::fabs(a[1] - b[1]) <= eps && std::fabs(a[2] - b[2]) <= eps; }

		// A carve area no other carving obstacle touching this one (its new AABB, two cells around) uses — their polygons
		// never merge, so each one's switch blocks exactly its own surface. It keeps its area while that stays free; past
		// eight obstacles touching each other they share.
		u8 pick_area(const obstacle_slot& o)
		{
			const f32 m = 2.0f * g.header.settings.cell_size;
			u32 taken = 0;
			for (const auto& other : g.obstacles)
			{
				if (&other == &o || !other.used || !other.carved) continue;
				if (other.vmin[0] > o.vmax[0] + m || other.vmax[0] < o.vmin[0] - m || other.vmin[2] > o.vmax[2] + m
					|| other.vmax[2] < o.vmin[2] - m || other.vmin[1] > o.vmax[1] + m || other.vmax[1] < o.vmin[1] - m) continue;
				taken |= 1u << (other.area - area_carved_first);
			}
			if (o.carved && !(taken & (1u << (o.area - area_carved_first)))) return o.area;
			for (u32 a = area_carved_first; a <= area_carved; ++a)
				if (!(taken & (1u << (a - area_carved_first)))) return (u8)a;
			return o.area;
		}

		// What an obstacle at its pose covers: its footprint grown by the bake's agent radius (the corners mitred), the
		// walkable surface from an agent height below its bottom (no agent fits under it) up to its top.
		void make_volume(const obstacle_slot& o, rebuild::carve_volume& v, f32* vmin, f32* vmax)
		{
			v = rebuild::carve_volume{};
			const f32 r = std::max(0.0f, g.header.settings.agent_radius);
			const f32 h = std::max(0.0f, g.header.settings.agent_height);
			if (o.shape == obstacle_cylinder)
			{
				const f32 hh = std::max(1.0e-3f, o.half[1]);
				v.center[0] = o.center[0];
				v.center[1] = o.center[2];
				v.radius = std::max(1.0e-3f, o.half[0]) + r;
				v.ymin = o.center[1] - hh - h;
				v.ymax = o.center[1] + hh;
				vmin[0] = v.center[0] - v.radius; vmin[1] = v.ymin; vmin[2] = v.center[1] - v.radius;
				vmax[0] = v.center[0] + v.radius; vmax[1] = v.ymax; vmax[2] = v.center[1] + v.radius;
				return;
			}
			pt2 pts[8], hull[9];
			f32 ylo = 3.0e38f, yhi = -3.0e38f;
			for (int i = 0; i < 8; ++i)
			{
				const f32 local[3] = { (i & 1) ? o.half[0] : -o.half[0], (i & 2) ? o.half[1] : -o.half[1], (i & 4) ? o.half[2] : -o.half[2] };
				f32 w[3];
				rotate_q(o.quat, local, w);
				pts[i] = { o.center[0] + w[0], o.center[2] + w[2] };
				ylo = std::min(ylo, o.center[1] + w[1]);
				yhi = std::max(yhi, o.center[1] + w[1]);
			}
			int n = hull_2d(pts, 8, hull);
			if (n < 3)
			{
				// seen edge-on (a plank standing on its narrow side): its xz bounds
				f32 x0 = 3.0e38f, x1 = -3.0e38f, z0 = 3.0e38f, z1 = -3.0e38f;
				for (const pt2& q : pts) { x0 = std::min(x0, q.x); x1 = std::max(x1, q.x); z0 = std::min(z0, q.z); z1 = std::max(z1, q.z); }
				x1 = std::max(x1, x0 + 1.0e-3f); z1 = std::max(z1, z0 + 1.0e-3f);
				hull[0] = { x0, z0 }; hull[1] = { x1, z0 }; hull[2] = { x1, z1 }; hull[3] = { x0, z1 };
				n = 4;
			}
			// push every edge out by r; a corner moves along the bisector by r / cos(half its turn) (mitre)
			vmin[0] = vmin[2] = 3.0e38f; vmax[0] = vmax[2] = -3.0e38f;
			for (int i = 0; i < n; ++i)
			{
				const pt2& a = hull[(i + n - 1) % n];
				const pt2& b = hull[i];
				const pt2& c = hull[(i + 1) % n];
				f32 e1[2] = { b.x - a.x, b.z - a.z }, e2[2] = { c.x - b.x, c.z - b.z };
				const f32 l1 = std::sqrt(e1[0] * e1[0] + e1[1] * e1[1]), l2 = std::sqrt(e2[0] * e2[0] + e2[1] * e2[1]);
				// outward normals of a counter-clockwise polygon in (x, z): (dz, -dx)
				f32 n1[2] = { l1 > 1.0e-9f ? e1[1] / l1 : 0.0f, l1 > 1.0e-9f ? -e1[0] / l1 : 0.0f };
				f32 n2[2] = { l2 > 1.0e-9f ? e2[1] / l2 : 0.0f, l2 > 1.0e-9f ? -e2[0] / l2 : 0.0f };
				const f32 k = std::max(0.25f, 1.0f + n1[0] * n2[0] + n1[1] * n2[1]);
				const f32 x = b.x + (n1[0] + n2[0]) * r / k, z = b.z + (n1[1] + n2[1]) * r / k;
				v.hull[i * 3] = x; v.hull[i * 3 + 1] = 0.0f; v.hull[i * 3 + 2] = z;
				vmin[0] = std::min(vmin[0], x); vmax[0] = std::max(vmax[0], x);
				vmin[2] = std::min(vmin[2], z); vmax[2] = std::max(vmax[2], z);
			}
			v.hull_count = n;
			v.ymin = ylo - h;
			v.ymax = yhi;
			vmin[1] = v.ymin;
			vmax[1] = v.ymax;
		}

		// Is p (a polygon's centre) in the volume — within `tolerance` (a cell: polygons follow the cells the volume marked)?
		bool in_volume(const rebuild::carve_volume& v, const f32* p, f32 tolerance)
		{
			if (p[1] < v.ymin - tolerance || p[1] > v.ymax + tolerance) return false;
			if (v.hull_count == 0)
			{
				const f32 dx = p[0] - v.center[0], dz = p[2] - v.center[1];
				return dx * dx + dz * dz <= (v.radius + tolerance) * (v.radius + tolerance);
			}
			// convex, counter-clockwise: inside every edge, `tolerance` beyond it allowed
			for (int i = 0; i < v.hull_count; ++i)
			{
				const f32* a = &v.hull[i * 3];
				const f32* b = &v.hull[((i + 1) % v.hull_count) * 3];
				const f32 ex = b[0] - a[0], ez = b[2] - a[2];
				const f32 len = std::sqrt(ex * ex + ez * ez);
				if (len < 1.0e-9f) continue;
				const f32 outside = ((p[0] - a[0]) * ez - (p[2] - a[2]) * ex) / len;   // distance beyond the edge (outward normal (ez, -ex))
				if (outside > tolerance) return false;
			}
			return true;
		}

		obstacle_slot* find_obstacle(u32 handle)
		{
			const u32 idx = (handle & 0xFFFFu) - 1u;
			if ((handle & 0xFFFFu) == 0 || idx >= g.obstacles.size()) return nullptr;
			obstacle_slot& o = g.obstacles[idx];
			return o.used && o.generation == (u16)(handle >> 16) ? &o : nullptr;
		}

		bool blocks(const obstacle_slot& o) { return o.used && o.carved && (o.flags & obstacle_active) != 0; }

		// The flags of a tile's carved polygons: blocked while inside an active carving obstacle, walkable otherwise.
		void apply_flags(const dtMeshTile* tile)
		{
			if (!tile || !tile->header) return;
			const dtPolyRef base = g.mesh->getPolyRefBase(tile);
			const f32 tolerance = 2.0f * g.header.settings.cell_size;   // polygon edges follow the marked cells within ~1.3 cells
			for (int i = 0; i < tile->header->polyCount; ++i)
			{
				const dtPoly& poly = tile->polys[i];
				if (poly.getType() == DT_POLYTYPE_OFFMESH_CONNECTION || poly.vertCount == 0) continue;
				if (poly.getArea() < area_carved_first || poly.getArea() > area_carved) continue;
				f32 c[3] = { 0.0f, 0.0f, 0.0f };
				for (int k = 0; k < poly.vertCount; ++k) dtVadd(c, c, &tile->verts[poly.verts[k] * 3]);
				dtVscale(c, c, 1.0f / (f32)poly.vertCount);
				bool blocked = false;
				for (const auto& o : g.obstacles)
					if (blocks(o) && in_volume(o.volume, c, tolerance)) { blocked = true; break; }
				const unsigned short flags = blocked ? (unsigned short)(k_walk_flag | k_blocked_flag) : k_walk_flag;
				if (poly.flags != flags) ++g.revision;
				g.mesh->setPolyFlags(base | (dtPolyRef)i, flags);
			}
		}

		// The flags of every tile over [bmin, bmax] — at once, no rebuild: a door shut or opened.
		void refresh_flags(const f32* bmin, const f32* bmax)
		{
			if (!g.mesh) return;
			int tx0, tz0, tx1, tz1;
			g.mesh->calcTileLoc(bmin, &tx0, &tz0);
			g.mesh->calcTileLoc(bmax, &tx1, &tz1);
			for (int tz = tz0; tz <= tz1; ++tz)
				for (int tx = tx0; tx <= tx1; ++tx)
				{
					const dtMeshTile* tiles[4];
					const int n = g.mesh->getTilesAt(tx, tz, tiles, 4);
					for (int i = 0; i < n; ++i) apply_flags(tiles[i]);
				}
		}

		// Agents whose way runs through [bmin, bmax] — the box from where they stand to their target, 2 m bigger, touches it
		// — ask for their path again: their corridor may be blocked now, or a shorter way open. A door (now) re-plans them at
		// once; tile swaps at most every half second each — an obstacle moving every frame would otherwise restart their
		// path search every frame, and a long one would never finish (the crowd's own check re-plans a blocked corridor
		// ahead of an agent anyway).
		void replan_near(const f32* bmin, const f32* bmax, bool now)
		{
			if (!g.crowd || !g.query) return;
			constexpr f32 margin{ 2.0f };
			constexpr f32 k_swap_replan_interval{ 0.5f };
			for (int i = 0; i < g.crowd->getAgentCount(); ++i)
			{
				if (g.proxy[i] || (!now && g.replan_wait[i] > 0.0f)) continue;
				dtCrowdAgent* ag = g.crowd->getEditableAgent(i);
				if (!ag || !ag->active || ag->state != DT_CROWDAGENT_STATE_WALKING) continue;
				if (ag->targetState == DT_CROWDAGENT_TARGET_NONE || ag->targetState == DT_CROWDAGENT_TARGET_FAILED
					|| ag->targetState == DT_CROWDAGENT_TARGET_VELOCITY) continue;
				const f32 x0 = std::min(ag->npos[0], ag->targetPos[0]) - margin, x1 = std::max(ag->npos[0], ag->targetPos[0]) + margin;
				const f32 z0 = std::min(ag->npos[2], ag->targetPos[2]) - margin, z1 = std::max(ag->npos[2], ag->targetPos[2]) + margin;
				if (x1 < bmin[0] || x0 > bmax[0] || z1 < bmin[2] || z0 > bmax[2]) continue;
				dtPolyRef ref = 0;
				f32 nearest[3], target[3];
				dtVcopy(target, ag->targetPos);
				if (dtStatusSucceed(g.query->findNearestPoly(target, g.crowd->getQueryHalfExtents(), &g.filter, &ref, nearest)) && ref)
				{
					g.crowd->requestMoveTarget(i, ref, target);
					g.replan_wait[i] = k_swap_replan_interval;
				}
			}
		}

		// Tiles over [bmin, bmax] are built again at the next rebuild_update (nothing without a source: rebuild_set_source
		// marks every carving obstacle's tiles once it has one).
		void mark_dirty(const f32* bmin, const f32* bmax)
		{
			if (!g.source) return;
			s32 tx0, tz0, tx1, tz1;
			if (!rebuild::tile_range(*g.source, bmin, bmax, tx0, tz0, tx1, tz1)) return;
			for (s32 tz = tz0; tz <= tz1; ++tz)
				for (s32 tx = tx0; tx <= tx1; ++tx)
				{
					const s64 key = tile_key(tx, tz);
					g.requested[key] = ++g.serial;
					g.dirty.insert(key);
				}
		}

		bool tile_waiting(const f32* bmin, const f32* bmax)
		{
			if (!g.source) return true;
			s32 tx0, tz0, tx1, tz1;
			if (!rebuild::tile_range(*g.source, bmin, bmax, tx0, tz0, tx1, tz1)) return false;
			for (s32 tz = tz0; tz <= tz1; ++tz)
				for (s32 tx = tx0; tx <= tx1; ++tx)
				{
					const s64 key = tile_key(tx, tz);
					if (g.dirty.count(key) || g.queued.count(key)) return true;
				}
			return false;
		}

		// The volumes of the carving obstacles whose tiles include (tx, tz).
		void carves_over(s32 tx, s32 tz, std::vector<rebuild::carve_volume>& out)
		{
			out.clear();
			for (const auto& o : g.obstacles)
			{
				if (!o.used || !o.carved) continue;
				s32 x0, z0, x1, z1;
				if (rebuild::tile_range(*g.source, o.vmin, o.vmax, x0, z0, x1, z1) && tx >= x0 && tx <= x1 && tz >= z0 && tz <= z1)
					out.push_back(o.volume);
			}
		}

		// A finished tile replaces the old one (the same grid cell); its carved polygons get their flags at once.
		bool swap_tile(s32 tx, s32 tz, const std::vector<u8>& data)
		{
			const dtTileRef old = g.mesh->getTileRefAt(tx, tz, 0);
			if (old && dtStatusFailed(g.mesh->removeTile(old, nullptr, nullptr))) return false;
			if (data.empty()) return true;   // nothing walkable left in it
			unsigned char* buf = (unsigned char*)dtAlloc(data.size(), DT_ALLOC_PERM);
			if (!buf) return false;
			std::memcpy(buf, data.data(), data.size());
			dtTileRef ref = 0;
			if (dtStatusFailed(g.mesh->addTile(buf, (int)data.size(), DT_TILE_FREE_DATA, 0, &ref)))
			{
				// only a tile the bake left empty can fail here (its old slot was not freed): the pool is full
				dtFree(buf);
				if (!g.pool_full_logged)
				{
					g.pool_full_logged = true;
					log("rebuild: tile (%d, %d) has no room in the navmesh's tile pool - bake the level again to grow it", tx, tz);
				}
				return false;
			}
			++g.revision;
			apply_flags(g.mesh->getTileByRef(ref));
			return true;
		}

		// Avoidance circles along an obstacle's footprint: x, z, radius each; their height and the height of their base.
		int proxy_circles(const obstacle_slot& o, f32 (*out)[3], f32& height, f32& base)
		{
			if (o.shape == obstacle_cylinder)
			{
				out[0][0] = o.center[0]; out[0][1] = o.center[2]; out[0][2] = std::max(0.05f, o.half[0]);
				height = 2.0f * std::max(0.05f, o.half[1]);
				base = o.center[1] - 0.5f * height;
				return 1;
			}
			f32 ax[3], az[3], ay[3];
			const f32 ux[3] = { o.half[0], 0.0f, 0.0f }, uy[3] = { 0.0f, o.half[1], 0.0f }, uz[3] = { 0.0f, 0.0f, o.half[2] };
			rotate_q(o.quat, ux, ax);
			rotate_q(o.quat, uy, ay);
			rotate_q(o.quat, uz, az);
			const f32 la = std::sqrt(ax[0] * ax[0] + ax[2] * ax[2]), lb = std::sqrt(az[0] * az[0] + az[2] * az[2]);
			const f32* along = la >= lb ? ax : az;
			const f32 half_len = std::max(la, lb), half_wide = std::max(0.1f, std::min(la, lb));
			const int n = std::clamp((int)std::ceil(half_len / half_wide), 1, k_max_proxies);
			const f32 step = half_len / (f32)n;                                   // each circle covers 2 x step of the length
			const f32 radius = std::sqrt(step * step + half_wide * half_wide);    // … and the whole width
			const f32 dir[2] = { half_len > 1.0e-6f ? along[0] / half_len : 1.0f, half_len > 1.0e-6f ? along[2] / half_len : 0.0f };
			for (int k = 0; k < n; ++k)
			{
				const f32 t = -half_len + step * (2.0f * (f32)k + 1.0f);
				out[k][0] = o.center[0] + dir[0] * t;
				out[k][1] = o.center[2] + dir[1] * t;
				out[k][2] = radius;
			}
			const f32 ext = std::fabs(ax[1]) + std::fabs(ay[1]) + std::fabs(az[1]);
			height = std::max(0.1f, 2.0f * ext);
			base = o.center[1] - ext;
			return n;
		}

		// The circles of an avoiding obstacle as crowd slots: never steered (their state is invalid, so the crowd does not
		// move them), but every agent sees them as neighbours — avoidance, separation, collision — moving with its velocity.
		void sync_proxies(obstacle_slot& o)
		{
			if (!g.crowd) { o.proxies.clear(); return; }
			const bool want = o.used && (o.flags & obstacle_avoid) && (o.flags & obstacle_active);
			f32 circles[k_max_proxies][3];
			f32 height = 1.0f, base = 0.0f;
			const int n = want ? proxy_circles(o, circles, height, base) : 0;
			while ((int)o.proxies.size() > n)
			{
				const int idx = o.proxies.back();
				o.proxies.pop_back();
				g.crowd->removeAgent(idx);
				g.proxy[idx] = false;
				g.generation[idx] = (u16)(g.generation[idx] + 1);
			}
			for (int k = 0; k < n; ++k)
			{
				const f32 pos[3] = { circles[k][0], base, circles[k][1] };
				dtCrowdAgentParams p{};
				p.radius = circles[k][2];
				p.height = height;
				p.collisionQueryRange = p.radius * 2.0f;
				p.pathOptimizationRange = p.radius;
				p.updateFlags = 0;
				if (k >= (int)o.proxies.size())
				{
					const int idx = g.crowd->addAgent(pos, &p);
					if (idx < 0) { log("obstacle: no crowd slot left for its avoidance circles (%d agents)", max_agents); break; }
					g.proxy[idx] = true;
					g.generation[idx] = (u16)(g.generation[idx] + 1);   // an agent handle of the slot's last user is stale
					o.proxies.push_back(idx);
				}
				else g.crowd->updateAgentParameters(o.proxies[k], &p);
				dtCrowdAgent* ag = g.crowd->getEditableAgent(o.proxies[k]);
				ag->state = DT_CROWDAGENT_STATE_INVALID;
				ag->targetState = DT_CROWDAGENT_TARGET_NONE;
				dtVcopy(ag->npos, pos);
				dtVcopy(ag->vel, o.velocity);
				dtVcopy(ag->dvel, o.velocity);
				dtVcopy(ag->nvel, o.velocity);
			}
		}

		void place_proxies(f32)
		{
			for (auto& o : g.obstacles)
				if (o.used && (o.flags & obstacle_avoid) && (o.flags & obstacle_active)) sync_proxies(o);
		}

		void remove_obstacle(obstacle_slot& o)
		{
			if (o.carved)
			{
				mark_dirty(o.vmin, o.vmax);
				o.carved = false;
				refresh_flags(o.vmin, o.vmax);
				replan_near(o.vmin, o.vmax, true);
			}
			o.flags = 0;
			sync_proxies(o);
			o.used = false;
			o.generation = (u16)(o.generation + 1);
		}
	}

	bool rebuild_set_source(const f32* verts, s32 vert_count, const s32* tris, s32 tri_count, const u8* tri_areas,
		const f32* boxes, s32 box_count, const u8* box_areas)
	{
		if (!loaded()) return false;
		auto src = rebuild::make_source(g.header, verts, vert_count, tris, tri_count, tri_areas, boxes, box_count, box_areas);
		if (!src)
		{
			log("rebuild: the geometry does not fit the loaded navmesh - nothing to rebuild tiles from");
			return false;
		}
		g.source = std::move(src);
		g.stats.source_triangles = rebuild::source_triangles(*g.source);
		g.stats.source_boxes = rebuild::source_boxes(*g.source);
		g.stats.source_kbytes = (s32)((rebuild::source_bytes(*g.source) + 1023) / 1024);
		for (auto& o : g.obstacles) if (o.used && o.carved) mark_dirty(o.vmin, o.vmax);   // built from the new geometry
		return true;
	}

	bool rebuild_has_source() { return g.source != nullptr; }

	void rebuild_clear_source()
	{
		g.source.reset();
		for (const s64 key : g.dirty) g.requested.erase(key);   // never built now (builds already queued still land)
		g.dirty.clear();
		g.stats.source_triangles = g.stats.source_boxes = g.stats.source_kbytes = 0;
	}

	bool rebuild_tiles(const f32* bmin, const f32* bmax)
	{
		if (!g.source || !finite3(bmin) || !finite3(bmax)) return false;
		mark_dirty(bmin, bmax);
		return true;
	}

	s32 rebuild_update(f32 budget_ms)
	{
		if (!loaded()) return 0;
		const auto t0 = std::chrono::steady_clock::now();
		// 1) builds for the changed tiles, with the carve volumes over each
		if (!g.dirty.empty() && g.source)
		{
			std::vector<tile_job> batch;
			batch.reserve(g.dirty.size());
			for (const s64 key : g.dirty)
			{
				tile_job job;
				job.tx = (s32)(u32)(key & 0xFFFFFFFF);
				job.tz = (s32)(key >> 32);
				job.version = g.requested[key];
				job.src = g.source;
				carves_over(job.tx, job.tz, job.carves);
				g.queued[key] = job.version;
				batch.push_back(std::move(job));
			}
			{
				std::lock_guard<std::mutex> lock(g.worker.mutex);
				// a job of the same tile still waiting for the worker is superseded by the new one
				const size_t before = g.worker.jobs.size();
				g.worker.jobs.erase(std::remove_if(g.worker.jobs.begin(), g.worker.jobs.end(),
					[](const tile_job& j) { return g.dirty.count(tile_key(j.tx, j.tz)) != 0; }), g.worker.jobs.end());
				g.stats.tiles_superseded += (s32)(before - g.worker.jobs.size());
				for (auto& job : batch) g.worker.jobs.push_back(std::move(job));
			}
			g.dirty.clear();
			g.worker.kick();
		}
		// 2) finished tiles in, within the budget (at least one per call)
		s32 swapped = 0;
		f32 changed_min[3] = { 3.0e38f, 3.0e38f, 3.0e38f }, changed_max[3] = { -3.0e38f, -3.0e38f, -3.0e38f };
		for (;;)
		{
			tile_result r;
			{
				std::lock_guard<std::mutex> lock(g.worker.mutex);
				if (g.worker.results.empty()) break;
				r = std::move(g.worker.results.front());
				g.worker.results.pop_front();
			}
			++g.stats.tiles_built;
			g.stats.build_ms_last = r.ms;
			g.stats.build_ms_max = std::max(g.stats.build_ms_max, r.ms);
			const s64 key = tile_key(r.tx, r.tz);
			const auto want = g.requested.find(key);
			const auto in = g.landed.find(key);
			if (want == g.requested.end() || (in != g.landed.end() && in->second >= r.version)) { ++g.stats.tiles_superseded; continue; }
			// An older build than the latest asked for still goes in — it is newer than the tile's: an obstacle moving every
			// frame would otherwise never be carved (every build superseded before it lands). The latest follows.
			if (want->second == r.version) g.queued.erase(key);
			if (!r.ok)
			{
				++g.stats.tiles_failed;
				log("rebuild: tile (%d, %d) failed - %s; it keeps its old navmesh", r.tx, r.tz, r.error.c_str());
				continue;
			}
			if (!swap_tile(r.tx, r.tz, r.data)) { ++g.stats.tiles_failed; continue; }
			g.landed[key] = r.version;
			++swapped;
			++g.stats.tiles_swapped;
			const f32 tw = g.header.tile_width;
			changed_min[0] = std::min(changed_min[0], g.header.orig[0] + tw * (f32)r.tx);
			changed_min[2] = std::min(changed_min[2], g.header.orig[2] + tw * (f32)r.tz);
			changed_max[0] = std::max(changed_max[0], g.header.orig[0] + tw * (f32)(r.tx + 1));
			changed_max[2] = std::max(changed_max[2], g.header.orig[2] + tw * (f32)(r.tz + 1));
			const f32 spent = (f32)std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
			if (spent >= budget_ms) break;
		}
		if (swapped > 0)
		{
			replan_near(changed_min, changed_max, false);
			g.stats.swap_ms_last = (f32)std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
			g.stats.swap_ms_max = std::max(g.stats.swap_ms_max, g.stats.swap_ms_last);
		}
		return swapped;
	}

	s32 rebuild_pending() { return (s32)(g.dirty.size() + g.queued.size()); }

	bool rebuild_flush(f32 timeout_ms)
	{
		const auto t0 = std::chrono::steady_clock::now();
		for (;;)
		{
			rebuild_update(1.0e9f);
			if (rebuild_pending() == 0) return true;
			if (!g.source) return false;
			if (std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() > timeout_ms) return false;
			std::this_thread::sleep_for(std::chrono::milliseconds(1));
		}
	}

	void rebuild_get_stats(rebuild_stats* out)
	{
		if (!out) return;
		g.stats.pending = rebuild_pending();
		g.stats.obstacles = obstacle_count();
		g.stats.revision = g.revision;
		*out = g.stats;
	}

	u32 obstacle_add(s32 shape, const f32* center, const f32* half, const f32* quat, u32 flags)
	{
		if (!loaded() || (shape != obstacle_box && shape != obstacle_cylinder) || !finite3(center) || !finite3(half)) return 0;
		size_t idx = 0;
		while (idx < g.obstacles.size() && g.obstacles[idx].used) ++idx;
		if (idx == g.obstacles.size())
		{
			if ((s32)g.obstacles.size() >= max_obstacles) { log("obstacle_add: %d obstacles already", max_obstacles); return 0; }
			g.obstacles.emplace_back();
		}
		obstacle_slot& o = g.obstacles[idx];
		const u16 generation = (u16)(o.generation + 1 == 0 ? 1 : o.generation + 1);
		o = obstacle_slot{};
		o.used = true;
		o.generation = generation;
		o.shape = shape;
		const u32 handle = ((u32)generation << 16) | (u32)(idx + 1);
		const f32 identity[4] = { 0.0f, 0.0f, 0.0f, 1.0f };
		obstacle_set(handle, center, half, quat ? quat : identity, flags, nullptr);
		return handle;
	}

	bool obstacle_set(u32 obstacle, const f32* center, const f32* half, const f32* quat, u32 flags, const f32* velocity)
	{
		obstacle_slot* o = find_obstacle(obstacle);
		if (!o) return false;
		const bool was_carved = o->carved;
		const bool was_active = (o->flags & obstacle_active) != 0;
		f32 old_min[3], old_max[3];
		dtVcopy(old_min, o->vmin);
		dtVcopy(old_max, o->vmax);
		if (finite3(center)) dtVcopy(o->center, center);
		if (finite3(half)) for (int k = 0; k < 3; ++k) o->half[k] = std::max(1.0e-3f, std::fabs(half[k]));
		if (quat && quat[0] == quat[0] && quat[1] == quat[1] && quat[2] == quat[2] && quat[3] == quat[3])
		{
			const f32 l = std::sqrt(quat[0] * quat[0] + quat[1] * quat[1] + quat[2] * quat[2] + quat[3] * quat[3]);
			if (l > 1.0e-6f) for (int k = 0; k < 4; ++k) o->quat[k] = quat[k] / l;
		}
		if (finite3(velocity)) dtVcopy(o->velocity, velocity); else dtVset(o->velocity, 0.0f, 0.0f, 0.0f);
		o->flags = flags;

		// the carve: built into the tiles where the obstacle is when it carves; a move re-carves (old and new place)
		bool carve_changed = false;
		if (flags & obstacle_carve)
		{
			const bool moved = !o->carved || !same3(o->center, o->carved_center, 1.0e-4f) || !same3(o->half, o->carved_half, 1.0e-4f)
				|| std::fabs(o->quat[0] - o->carved_quat[0]) > 1.0e-5f || std::fabs(o->quat[1] - o->carved_quat[1]) > 1.0e-5f
				|| std::fabs(o->quat[2] - o->carved_quat[2]) > 1.0e-5f || std::fabs(o->quat[3] - o->carved_quat[3]) > 1.0e-5f;
			if (moved)
			{
				if (o->carved) mark_dirty(o->vmin, o->vmax);
				make_volume(*o, o->volume, o->vmin, o->vmax);
				o->area = pick_area(*o);
				o->volume.area = o->area;
				dtVcopy(o->carved_center, o->center);
				dtVcopy(o->carved_half, o->half);
				std::copy(o->quat, o->quat + 4, o->carved_quat);
				o->carved = true;
				mark_dirty(o->vmin, o->vmax);
				carve_changed = true;
			}
		}
		else if (o->carved)
		{
			mark_dirty(o->vmin, o->vmax);   // its split polygons merge again
			o->carved = false;
			carve_changed = true;
		}
		const bool active = (flags & obstacle_active) != 0;
		if (carve_changed || active != was_active)
		{
			// blocking follows at once wherever the obstacle's polygons already exist — a door shut or opened needs no rebuild
			f32 lo[3] = { 3.0e38f, 3.0e38f, 3.0e38f }, hi[3] = { -3.0e38f, -3.0e38f, -3.0e38f };
			auto grow = [&](const f32* a, const f32* b) { for (int k = 0; k < 3; ++k) { lo[k] = std::min(lo[k], a[k]); hi[k] = std::max(hi[k], b[k]); } };
			if (was_carved) grow(old_min, old_max);
			if (o->carved) grow(o->vmin, o->vmax);
			if (lo[0] <= hi[0])
			{
				refresh_flags(lo, hi);
				replan_near(lo, hi, true);
			}
		}
		sync_proxies(*o);
		return true;
	}

	void obstacle_remove(u32 obstacle)
	{
		if (obstacle_slot* o = find_obstacle(obstacle)) remove_obstacle(*o);
	}

	bool obstacle_valid(u32 obstacle) { return find_obstacle(obstacle) != nullptr; }

	bool obstacle_ready(u32 obstacle)
	{
		const obstacle_slot* o = find_obstacle(obstacle);
		if (!o) return false;
		if (!(o->flags & obstacle_carve)) return true;
		return o->carved && !tile_waiting(o->vmin, o->vmax);
	}

	s32 obstacle_count()
	{
		s32 n = 0;
		for (const auto& o : g.obstacles) if (o.used) ++n;
		return n;
	}

	void obstacle_clear()
	{
		for (auto& o : g.obstacles) if (o.used) remove_obstacle(o);
	}

	s32 agent_count()
	{
		if (!g.crowd) return 0;
		s32 n = 0;
		for (int i = 0; i < g.crowd->getAgentCount(); ++i)
		{
			const dtCrowdAgent* ag = g.crowd->getAgent(i);
			if (ag && ag->active && !g.proxy[i]) ++n;
		}
		return n;
	}

	void crowd_update(f32 dt)
	{
		if (!g.crowd || !(dt > 0.0f)) return;
		if (dt > 0.1f) dt = 0.1f;   // a hitch must not fling agents through walls
		place_proxies(dt);          // #435: the avoidance circles where their obstacles are
		g.crowd->update(dt, nullptr);
	}

	void crowd_clear()
	{
		if (!g.crowd) return;
		for (int i = 0; i < g.crowd->getAgentCount(); ++i)
		{
			const dtCrowdAgent* ag = g.crowd->getAgent(i);
			if (ag && ag->active) { g.crowd->removeAgent(i); g.generation[i] = (u16)(g.generation[i] + 1); }
			g.proxy[i] = false;
		}
		for (auto& o : g.obstacles) o.proxies.clear();   // (made again for the obstacles that still avoid)
	}
}

#else // !VORTEX_HAS_RECAST — the stub (Visual Studio projects, -DVORTEX_ENABLE_RECAST=OFF)

namespace vortex::navigation {

	bool available() { return false; }
	bool load(const u8*, s32) { return false; }
	void unload() {}
	bool loaded() { return false; }
	bool get_info(bake_settings*, bake_stats*) { return false; }
	s32  data_copy(u8*, s32) { return 0; }
	s32  find_path(const f32*, const f32*, const f32*, f32*, s32, s32* out_status) { if (out_status) *out_status = path_invalid; return 0; }
	bool closest_point(const f32*, const f32*, f32*) { return false; }
	bool raycast(const f32*, const f32*, const f32*, f32*, f32*, f32* out_t) { if (out_t) *out_t = 0.0f; return false; }
	bool random_point(f32*) { return false; }
	bool random_point_around(const f32*, f32, const f32*, f32*) { return false; }
	void set_random_seed(u32) {}
	s32  debug_triangles(f32*, s32) { return 0; }
	s32  debug_lines(f32*, s32, s32) { return 0; }
	u32  agent_add(const f32*, const agent_params*) { return 0; }
	void agent_remove(u32) {}
	bool agent_valid(u32) { return false; }
	bool agent_set_params(u32, const agent_params*) { return false; }
	bool agent_set_target(u32, const f32*) { return false; }
	bool agent_reset_target(u32) { return false; }
	bool agent_set_velocity(u32, const f32*) { return false; }
	bool agent_teleport(u32, const f32*) { return false; }
	bool agent_move_position(u32, const f32*) { return false; }
	s32  agent_follow(u32, const f32*, const f32*) { return 0; }
	bool agent_get_state(u32, agent_state*) { return false; }
	s32  agent_get_corners(u32, f32*, s32) { return 0; }
	s32  agent_count() { return 0; }
	void crowd_update(f32) {}
	void crowd_clear() {}
	bool rebuild_set_source(const f32*, s32, const s32*, s32, const u8*, const f32*, s32, const u8*) { return false; }
	bool rebuild_has_source() { return false; }
	void rebuild_clear_source() {}
	bool rebuild_tiles(const f32*, const f32*) { return false; }
	s32  rebuild_update(f32) { return 0; }
	bool rebuild_flush(f32) { return true; }
	s32  rebuild_pending() { return 0; }
	void rebuild_get_stats(rebuild_stats* out) { if (out) *out = rebuild_stats{}; }
	u32  obstacle_add(s32, const f32*, const f32*, const f32*, u32) { return 0; }
	bool obstacle_set(u32, const f32*, const f32*, const f32*, u32, const f32*) { return false; }
	void obstacle_remove(u32) {}
	bool obstacle_valid(u32) { return false; }
	bool obstacle_ready(u32) { return false; }
	s32  obstacle_count() { return 0; }
	void obstacle_clear() {}
}

#endif
