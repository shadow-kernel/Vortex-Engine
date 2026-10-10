using System;
using System.Numerics;

namespace Editor.Core.Terrain
{
    /// <summary>One chunk mesh in the engine's vertex layout (8 floats: position, normal, uv) with its local bounds.</summary>
    public sealed class TerrainChunkMesh
    {
        public float[] Vertices;
        public uint[] Indices;
        public Vector3 Min, Max;
        public int VertexCount => Vertices.Length / 8;
        public int TriangleCount => Indices.Length / 3;
    }

    /// <summary>
    /// Builds the render meshes of a terrain (#124): the terrain is cut into chunks of <see cref="ChunkCells"/> × <see cref="ChunkCells"/>
    /// cells, each built at three LODs (every sample, every second, every fourth). Every chunk carries a vertical skirt around
    /// its edge (both windings) deep enough to cover the height difference between its own edge and a coarser neighbour's,
    /// so LOD seams never show as cracks. Normals come from the full-resolution heights, UVs run 0..1 across the whole
    /// terrain (the splat map). Chunk positions are LOCAL to the terrain (corner at the origin); the terrain's world matrix
    /// is the instance transform.
    /// </summary>
    public static class TerrainMeshBuilder
    {
        public const int ChunkCells = 32;
        public const int LodCount = 3;

        public static int ChunksPerSide(int resolution) => Math.Max(1, (resolution - 1) / ChunkCells);

        /// <summary>The chunk index range a sample rectangle touches (inclusive; a shared edge dirties both neighbours).</summary>
        public static void ChunkRange(SampleRect r, int resolution, out int cx0, out int cz0, out int cx1, out int cz1)
        {
            int n = ChunksPerSide(resolution);
            cx0 = Math.Max(0, (r.X0 - 1) / ChunkCells); cz0 = Math.Max(0, (r.Z0 - 1) / ChunkCells);
            cx1 = Math.Min(n - 1, r.X1 / ChunkCells);   cz1 = Math.Min(n - 1, r.Z1 / ChunkCells);
            if (r.IsEmpty) { cx1 = cx0 - 1; cz1 = cz0 - 1; }
        }

        public static TerrainChunkMesh Build(TerrainData data, int chunkX, int chunkZ, int lod, float cellSize)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            lod = Math.Max(0, Math.Min(LodCount - 1, lod));
            int stride = 1 << lod;
            int cells = ChunkCells / stride;          // cells per chunk side at this LOD
            int verts = cells + 1;                    // samples per side
            int res = data.Resolution;
            int baseX = chunkX * ChunkCells, baseZ = chunkZ * ChunkCells;
            float invRes = 1f / (res - 1);

            int gridVerts = verts * verts;
            int skirtVerts = 4 * verts;
            var v = new float[(gridVerts + skirtVerts) * 8];
            var idx = new uint[cells * cells * 6 + 4 * cells * 12];
            float minH = float.MaxValue, maxH = float.MinValue;

            // the grid
            for (int j = 0; j < verts; j++)
            {
                int sz = Math.Min(res - 1, baseZ + j * stride);
                for (int i = 0; i < verts; i++)
                {
                    int sx = Math.Min(res - 1, baseX + i * stride);
                    float h = data.Get(sx, sz);
                    if (h < minH) minH = h;
                    if (h > maxH) maxH = h;
                    var n = data.Normal(sx, sz, cellSize);
                    int o = (j * verts + i) * 8;
                    v[o] = sx * cellSize; v[o + 1] = h; v[o + 2] = sz * cellSize;
                    v[o + 3] = n.X; v[o + 4] = n.Y; v[o + 5] = n.Z;
                    v[o + 6] = sx * invRes; v[o + 7] = sz * invRes;
                }
            }
            int k = 0;
            for (int j = 0; j < cells; j++)
                for (int i = 0; i < cells; i++)
                {
                    uint a = (uint)(j * verts + i), b = a + 1, c = a + (uint)verts, d = c + 1;
                    idx[k++] = a; idx[k++] = c; idx[k++] = b;
                    idx[k++] = b; idx[k++] = c; idx[k++] = d;
                }

            // skirts: a copy of each edge moved down by the chunk's height range (+ two coarse cells of margin), both windings
            float depth = (maxH - minH) + 2f * cellSize * stride;
            int sb = gridVerts;   // first skirt vertex
            void Edge(Func<int, int> gridIndex, int edge)
            {
                int first = sb + edge * verts;
                for (int t = 0; t < verts; t++)
                {
                    int src = gridIndex(t) * 8, dst = (first + t) * 8;
                    Array.Copy(v, src, v, dst, 8);
                    v[dst + 1] -= depth;
                }
                for (int t = 0; t < cells; t++)
                {
                    uint top0 = (uint)gridIndex(t), top1 = (uint)gridIndex(t + 1);
                    uint bot0 = (uint)(first + t), bot1 = (uint)(first + t + 1);
                    idx[k++] = top0; idx[k++] = bot0; idx[k++] = top1;
                    idx[k++] = top1; idx[k++] = bot0; idx[k++] = bot1;
                    idx[k++] = top0; idx[k++] = top1; idx[k++] = bot0;
                    idx[k++] = top1; idx[k++] = bot1; idx[k++] = bot0;
                }
            }
            Edge(t => t, 0);                              // z = 0 edge
            Edge(t => (verts - 1) * verts + t, 1);        // z = max edge
            Edge(t => t * verts, 2);                      // x = 0 edge
            Edge(t => t * verts + (verts - 1), 3);        // x = max edge

            float x0 = baseX * cellSize, z0 = baseZ * cellSize;
            float x1 = Math.Min(res - 1, baseX + ChunkCells) * cellSize, z1 = Math.Min(res - 1, baseZ + ChunkCells) * cellSize;
            return new TerrainChunkMesh
            {
                Vertices = v,
                Indices = idx,
                Min = new Vector3(x0, minH - depth, z0),
                Max = new Vector3(x1, maxH, z1)
            };
        }

        /// <summary>The LOD a chunk should render at for a camera this far (metres) from its centre.</summary>
        public static int LodFor(float distance, float lodDistance)
        {
            if (lodDistance <= 0f) return 0;
            if (distance < lodDistance) return 0;
            if (distance < lodDistance * 2f) return 1;
            return 2;
        }

        /// <summary>Flat triangle soup of the whole terrain in LOCAL space (9 floats per triangle), every <paramref name="stride"/>-th
        /// sample — the collision world's and the navmesh bake's view of the terrain.</summary>
        public static float[] Triangles(TerrainData data, float cellSize, int stride = 1)
        {
            int res = data.Resolution;
            stride = Math.Max(1, stride);
            int cells = (res - 1) / stride;
            var tris = new float[cells * cells * 2 * 9];
            int k = 0;
            for (int j = 0; j < cells; j++)
            {
                int z0 = j * stride, z1 = Math.Min(res - 1, z0 + stride);
                for (int i = 0; i < cells; i++)
                {
                    int x0 = i * stride, x1 = Math.Min(res - 1, x0 + stride);
                    float ax = x0 * cellSize, az = z0 * cellSize, bx = x1 * cellSize, bz = z1 * cellSize;
                    float ha = data.Get(x0, z0), hb = data.Get(x1, z0), hc = data.Get(x0, z1), hd = data.Get(x1, z1);
                    // (a, c, b) and (b, c, d) like the render mesh
                    tris[k++] = ax; tris[k++] = ha; tris[k++] = az;   tris[k++] = ax; tris[k++] = hc; tris[k++] = bz;   tris[k++] = bx; tris[k++] = hb; tris[k++] = az;
                    tris[k++] = bx; tris[k++] = hb; tris[k++] = az;   tris[k++] = ax; tris[k++] = hc; tris[k++] = bz;   tris[k++] = bx; tris[k++] = hd; tris[k++] = bz;
                }
            }
            return tris;
        }
    }
}
