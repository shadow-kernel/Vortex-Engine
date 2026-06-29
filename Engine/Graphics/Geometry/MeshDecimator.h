#pragma once

#include "IMeshGenerator.h"
#include <DirectXMath.h>
#include <vector>

namespace vortex::graphics
{
	/// <summary>
	/// Vertex-clustering mesh decimation: overlay a uniform grid (grid_res cells on the longest bbox axis),
	/// collapse every vertex in a cell to that cell's centroid (averaging normal + UV), then rebuild the
	/// triangles through the cluster map, dropping triangles whose corners collapsed together. Robust for
	/// ARBITRARY imported meshes (no manifold/2-sided requirement) and O(verts+indices) — used to build the
	/// low-poly distant LOD levels. Returns an empty MeshData if the input is too small or collapses away.
	/// </summary>
	MeshData decimate_vertex_cluster(
		const std::vector<VertexPosNormalUV>& verts,
		const std::vector<u32>& indices,
		const DirectX::XMFLOAT3& bmin,
		const DirectX::XMFLOAT3& bmax,
		unsigned grid_res);
}
