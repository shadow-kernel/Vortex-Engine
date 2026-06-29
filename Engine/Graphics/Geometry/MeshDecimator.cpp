#include "MeshDecimator.h"
#include <unordered_map>
#include <algorithm>
#include <cmath>

namespace vortex::graphics
{
	using namespace DirectX;

	MeshData decimate_vertex_cluster(
		const std::vector<VertexPosNormalUV>& verts,
		const std::vector<u32>& indices,
		const XMFLOAT3& bmin,
		const XMFLOAT3& bmax,
		unsigned grid_res)
	{
		MeshData out;
		if (verts.empty() || indices.size() < 3 || grid_res < 2) return out;

		const float ex = bmax.x - bmin.x, ey = bmax.y - bmin.y, ez = bmax.z - bmin.z;
		const float longest = (std::max)(ex, (std::max)(ey, ez));
		if (longest <= 1e-6f) return out;
		const float cell = longest / static_cast<float>(grid_res);
		if (cell <= 1e-9f) return out;

		const int nx = (std::max)(1, static_cast<int>(std::ceil(ex / cell)));
		const int ny = (std::max)(1, static_cast<int>(std::ceil(ey / cell)));
		const int nz = (std::max)(1, static_cast<int>(std::ceil(ez / cell)));

		struct Cell { XMFLOAT3 pos{ 0, 0, 0 }; XMFLOAT3 nrm{ 0, 0, 0 }; XMFLOAT2 uv{ 0, 0 }; int count{ 0 }; int outIndex{ -1 }; };
		std::unordered_map<long long, Cell> cells;
		cells.reserve(verts.size());
		std::vector<long long> vertCell(verts.size());

		auto keyOf = [&](const XMFLOAT3& p) -> long long
		{
			int ix = static_cast<int>((p.x - bmin.x) / cell); if (ix < 0) ix = 0; else if (ix >= nx) ix = nx - 1;
			int iy = static_cast<int>((p.y - bmin.y) / cell); if (iy < 0) iy = 0; else if (iy >= ny) iy = ny - 1;
			int iz = static_cast<int>((p.z - bmin.z) / cell); if (iz < 0) iz = 0; else if (iz >= nz) iz = nz - 1;
			return static_cast<long long>(ix) | (static_cast<long long>(iy) << 21) | (static_cast<long long>(iz) << 42);
		};

		// Accumulate each vertex into its cell.
		for (size_t i = 0; i < verts.size(); ++i)
		{
			const long long k = keyOf(verts[i].position);
			vertCell[i] = k;
			Cell& c = cells[k];
			c.pos.x += verts[i].position.x; c.pos.y += verts[i].position.y; c.pos.z += verts[i].position.z;
			c.nrm.x += verts[i].normal.x;   c.nrm.y += verts[i].normal.y;   c.nrm.z += verts[i].normal.z;
			c.uv.x += verts[i].uv.x;         c.uv.y += verts[i].uv.y;
			++c.count;
		}

		// One welded vertex per occupied cell (centroid + averaged normal/UV).
		out.vertices.reserve(cells.size());
		for (auto& kv : cells)
		{
			Cell& c = kv.second;
			const float inv = 1.0f / static_cast<float>(c.count);
			VertexPosNormalUV v;
			v.position = { c.pos.x * inv, c.pos.y * inv, c.pos.z * inv };
			XMVECTOR n = XMVector3Normalize(XMVectorSet(c.nrm.x, c.nrm.y, c.nrm.z, 0.0f));
			XMStoreFloat3(&v.normal, n);
			if (!std::isfinite(v.normal.x) || !std::isfinite(v.normal.y) || !std::isfinite(v.normal.z))
				v.normal = { 0.0f, 1.0f, 0.0f };
			v.uv = { c.uv.x * inv, c.uv.y * inv };
			c.outIndex = static_cast<int>(out.vertices.size());
			out.vertices.push_back(v);
		}

		// Rebuild triangles through the cluster map; drop triangles whose corners collapsed together.
		out.indices.reserve(indices.size());
		for (size_t t = 0; t + 2 < indices.size(); t += 3)
		{
			const long long ka = vertCell[indices[t]];
			const long long kb = vertCell[indices[t + 1]];
			const long long kc = vertCell[indices[t + 2]];
			if (ka == kb || kb == kc || ka == kc) continue; // degenerate after clustering
			out.indices.push_back(static_cast<u32>(cells[ka].outIndex));
			out.indices.push_back(static_cast<u32>(cells[kb].outIndex));
			out.indices.push_back(static_cast<u32>(cells[kc].outIndex));
		}

		if (out.indices.empty()) out.clear();
		return out;
	}
}
