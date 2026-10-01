#include "NavigationWorld.h"
#include "NavigationFormat.h"

// ============================================================================
// The runtime navigation world: the loaded tiled navmesh (dtNavMesh), one query object for path / point / raycast
// queries and a DetourCrowd for agents. See NavigationWorld.h for the contract; baking lives in
// NavigationBake.cpp. The #else branch at the bottom is the stub the Visual Studio projects (and
// -DVORTEX_ENABLE_RECAST=OFF) compile.
// ============================================================================

#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#if VORTEX_HAS_RECAST

#include <DetourAlloc.h>
#include <DetourCommon.h>
#include <DetourCrowd.h>
#include <DetourNavMesh.h>
#include <DetourNavMeshQuery.h>

namespace vortex::navigation {

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

		constexpr int k_query_nodes{ 4096 };
		constexpr int k_max_path_polys{ 1024 };
		constexpr int k_max_straight{ 512 };
		constexpr int k_max_state_corners{ 64 };
		constexpr unsigned short k_walk_flag{ 1 };
		const f32 k_default_extents[3] = { 2.0f, 4.0f, 2.0f };

		struct world_state
		{
			dtNavMesh* mesh{ nullptr };
			dtNavMeshQuery* query{ nullptr };
			dtCrowd* crowd{ nullptr };
			dtQueryFilter filter;
			format::file_header header{};
			std::vector<u8> blob;                // the loaded data (data_copy hands it back)
			u16 generation[max_agents]{};
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
				f->setExcludeFlags(0);
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
		if (g.crowd) { dtFreeCrowd(g.crowd); g.crowd = nullptr; }
		if (g.query) { dtFreeNavMeshQuery(g.query); g.query = nullptr; }
		if (g.mesh) { dtFreeNavMesh(g.mesh); g.mesh = nullptr; }
		g.header = format::file_header{};
		std::vector<u8>().swap(g.blob);
		for (auto& gen : g.generation) gen = (u16)(gen + 1);   // every old agent handle is stale now
	}

	bool loaded() { return g.mesh != nullptr && g.query != nullptr; }

	bool load(const u8* data, s32 size)
	{
		unload();
		if (!data || size < (s32)sizeof(format::file_header)) { log("load: no data"); return false; }
		format::file_header hdr{};
		std::memcpy(&hdr, data, sizeof(hdr));
		if (hdr.magic != data_magic) { log("load: not a navmesh (.vnav) blob"); return false; }
		if (hdr.version != data_version) { log("load: unsupported .vnav version %d (expected %d) - rebake", hdr.version, data_version); return false; }
		if (hdr.tile_count < 0 || hdr.max_tiles <= 0 || hdr.max_polys <= 0 || !(hdr.tile_width > 0.0f)) { log("load: corrupt header"); return false; }

		dtNavMesh* mesh = dtAllocNavMesh();
		if (!mesh) return false;
		dtNavMeshParams params{};
		dtVcopy(params.orig, hdr.orig);
		params.tileWidth = hdr.tile_width;
		params.tileHeight = hdr.tile_height;
		params.maxTiles = hdr.max_tiles;
		params.maxPolys = hdr.max_polys;
		if (dtStatusFailed(mesh->init(&params))) { dtFreeNavMesh(mesh); log("load: dtNavMesh::init failed"); return false; }

		size_t off = sizeof(hdr);
		for (s32 i = 0; i < hdr.tile_count; ++i)
		{
			format::tile_header th{};
			if (off + sizeof(th) > (size_t)size) { dtFreeNavMesh(mesh); log("load: truncated tile table"); return false; }
			std::memcpy(&th, data + off, sizeof(th));
			off += sizeof(th);
			if (th.size <= 0 || off + (size_t)th.size > (size_t)size) { dtFreeNavMesh(mesh); log("load: truncated tile %d", i); return false; }
			unsigned char* tile = (unsigned char*)dtAlloc((size_t)th.size, DT_ALLOC_PERM);
			if (!tile) { dtFreeNavMesh(mesh); return false; }
			std::memcpy(tile, data + off, (size_t)th.size);
			if (dtStatusFailed(mesh->addTile(tile, th.size, DT_TILE_FREE_DATA, 0, nullptr)))
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
		g.filter.setExcludeFlags(0);
		g.header = hdr;
		g.blob.assign(data, data + size);
		setup_crowd(hdr.settings.agent_radius);
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
							if (tile->links[k].edge == j) { boundary = false; break; }
					}
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

	bool agent_get_state(u32 agent, agent_state* out)
	{
		const int idx = slot_of(agent);
		if (idx < 0 || !out) return false;
		const dtCrowdAgent* ag = g.crowd->getAgent(idx);
		agent_state s{};
		dtVcopy(s.position, ag->npos);
		dtVcopy(s.velocity, ag->vel);
		dtVcopy(s.desired_velocity, ag->dvel);
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

	s32 agent_count()
	{
		if (!g.crowd) return 0;
		s32 n = 0;
		for (int i = 0; i < g.crowd->getAgentCount(); ++i)
		{
			const dtCrowdAgent* ag = g.crowd->getAgent(i);
			if (ag && ag->active) ++n;
		}
		return n;
	}

	void crowd_update(f32 dt)
	{
		if (!g.crowd || !(dt > 0.0f)) return;
		if (dt > 0.1f) dt = 0.1f;   // a hitch must not fling agents through walls
		g.crowd->update(dt, nullptr);
	}

	void crowd_clear()
	{
		if (!g.crowd) return;
		for (int i = 0; i < g.crowd->getAgentCount(); ++i)
		{
			const dtCrowdAgent* ag = g.crowd->getAgent(i);
			if (ag && ag->active) { g.crowd->removeAgent(i); g.generation[i] = (u16)(g.generation[i] + 1); }
		}
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
	bool agent_get_state(u32, agent_state*) { return false; }
	s32  agent_get_corners(u32, f32*, s32) { return 0; }
	s32  agent_count() { return 0; }
	void crowd_update(f32) {}
	void crowd_clear() {}
}

#endif
