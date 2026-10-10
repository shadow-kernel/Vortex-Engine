using System;
using System.Numerics;
using Editor.Core.Terrain;

namespace VortexTests
{
    /// <summary>The heightfield terrain's data layer (#124): the brushes touch only their radius, the file round-trips, the
    /// raycast finds the surface, the chunk meshes carry their skirts and bounds, and the LOD / resolution helpers snap.</summary>
    public static class TerrainTests
    {
        [Test]
        public static void RaiseTouchesOnlyTheRadiusAndTheFileRoundTrips(TestContext t)
        {
            var d = new TerrainData(65);
            var r = d.Raise(32f, 32f, 10f, 4f, 0f);
            t.True(Math.Abs(d.Get(32, 32) - 4f) < 1e-5f, "the centre rises by the full amount (" + d.Get(32, 32) + ")");
            t.True(d.Get(32, 37) > 0.5f && d.Get(32, 37) < 4f, "halfway out the brush fades (" + d.Get(32, 37) + ")");
            t.True(Math.Abs(d.Get(32, 43)) < 1e-6f, "outside the radius nothing moves");
            t.True(Math.Abs(d.Get(0, 0)) < 1e-6f && Math.Abs(d.Get(64, 64)) < 1e-6f, "the corners stay flat");
            t.True(r.X0 == 22 && r.X1 == 42 && r.Z0 == 22 && r.Z1 == 42, "the dirty rect is the brush's bounding box (" + r + ")");
            t.True(Math.Abs(d.MaxHeight - 4f) < 1e-5f && Math.Abs(d.MinHeight) < 1e-6f, "the height range follows the stroke");

            d.Paint(10f, 10f, 4f, 2, 1f, 0f);
            t.True(d.DominantLayer(10, 10) == 2, "painting makes layer 2 dominant at the centre (" + d.DominantLayer(10, 10) + ")");
            t.True(d.DominantLayer(40, 40) == 0, "elsewhere layer 0 stays");
            float w0, w1, w2, w3;
            d.Weights(10, 10, out w0, out w1, out w2, out w3);
            t.True(w2 > 0.99f, "the weights are normalised (" + w2 + ")");

            var bytes = d.ToBytes();
            var back = TerrainData.FromBytes(bytes);
            t.True(back != null && back.Resolution == 65, "the file loads back");
            bool same = true;
            for (int i = 0; i < d.Heights.Length && same; i++) same = Math.Abs(d.Heights[i] - back.Heights[i]) < 1e-7f;
            for (int i = 0; i < d.Splat.Length && same; i++) same = d.Splat[i] == back.Splat[i];
            t.True(same, "heights and splat survive the round trip");
            t.True(TerrainData.FromBytes(new byte[] { 1, 2, 3 }) == null, "garbage is rejected");

            var small = d.Resample(33);
            t.True(small.Resolution == 33 && Math.Abs(small.Get(16, 16) - 4f) < 1e-4f, "resampling keeps the hill (" + small.Get(16, 16) + ")");
        }

        [Test]
        public static void SmoothAndFlattenConverge(TestContext t)
        {
            var d = new TerrainData(65);
            d.Raise(32f, 32f, 6f, 10f, 0.9f);   // a hard-edged plateau
            float before = d.Get(32, 27) - d.Get(32, 26);   // the plateau's rim: 10 m inside, 0 outside
            for (int i = 0; i < 6; i++) d.Smooth(32f, 26.5f, 6f, 1f, 0f);
            float after = d.Get(32, 27) - d.Get(32, 26);
            t.True(Math.Abs(after) < Math.Abs(before), "smoothing softens the edge (" + before + " -> " + after + ")");

            for (int i = 0; i < 12; i++) d.Flatten(32f, 32f, 20f, 2f, 1f, 0f);
            t.True(Math.Abs(d.Get(32, 32) - 2f) < 0.05f, "flatten converges on the target (" + d.Get(32, 32) + ")");
        }

        [Test]
        public static void RaycastFindsTheSurface(TestContext t)
        {
            var d = new TerrainData(65);
            d.Raise(32f, 32f, 12f, 5f, 0f);
            Vector3 hit;
            t.True(d.Raycast(new Vector3(32f, 50f, 32f), new Vector3(0f, -1f, 0f), 1f, 200f, out hit), "a ray from above hits");
            t.True(Math.Abs(hit.Y - 5f) < 0.02f && Math.Abs(hit.X - 32f) < 1e-3f, "at the hill's top (" + hit + ")");
            t.True(d.Raycast(new Vector3(-10f, 2f, 32f), new Vector3(1f, -0.05f, 0f), 1f, 200f, out hit), "a grazing ray from outside the box hits");
            t.True(hit.X > 0f && hit.X < 64f && Math.Abs(hit.Y - d.SampleHeight(hit.X, hit.Z)) < 0.05f, "on the surface (" + hit + ")");
            t.True(!d.Raycast(new Vector3(32f, 50f, 32f), new Vector3(0f, 1f, 0f), 1f, 200f, out hit), "a ray pointing away misses");
            t.True(!d.Raycast(new Vector3(100f, 50f, 100f), new Vector3(0f, -1f, 0f), 1f, 200f, out hit), "a ray beside the terrain misses");
            t.True(d.Raycast(new Vector3(32f, 50f, 32f), new Vector3(0f, -1f, 0f), 1f, 10f, out hit) == false, "the max distance is honoured");

            var n = d.Normal(32, 32, 1f);
            t.True(n.Y > 0.99f, "the hill's top is flat (" + n + ")");
            var side = d.Normal(32, 24, 1f);
            t.True(side.Z < -0.05f && side.Y > 0.5f, "the slope's normal leans down the hill (" + side + ")");
        }

        [Test]
        public static void ChunkMeshesCarrySkirtsBoundsAndLods(TestContext t)
        {
            var d = new TerrainData(65);
            d.Raise(32f, 32f, 12f, 5f, 0f);
            t.True(TerrainMeshBuilder.ChunksPerSide(65) == 2 && TerrainMeshBuilder.ChunksPerSide(129) == 4, "32-cell chunks");

            var m0 = TerrainMeshBuilder.Build(d, 0, 0, 0, 1f);
            t.True(m0.VertexCount == 33 * 33 + 4 * 33, "LOD0: grid + four skirt edges (" + m0.VertexCount + ")");
            t.True(m0.TriangleCount == 32 * 32 * 2 + 4 * 32 * 4, "LOD0 triangles incl. double-sided skirts (" + m0.TriangleCount + ")");
            t.True(m0.Min.X == 0f && m0.Max.X == 32f && m0.Min.Z == 0f && m0.Max.Z == 32f, "the chunk covers its 32 m");
            t.True(m0.Max.Y >= d.Get(32, 32) - 1e-4f && m0.Min.Y < 0f, "bounds hold the hill and reach below the skirts (" + m0.Min.Y + " .. " + m0.Max.Y + ")");
            // uv runs 0..1 across the whole terrain
            float uMax = m0.Vertices[(32 * 33 + 32) * 8 + 6], vMax = m0.Vertices[(32 * 33 + 32) * 8 + 7];
            t.True(Math.Abs(uMax - 0.5f) < 1e-5f && Math.Abs(vMax - 0.5f) < 1e-5f, "the first chunk's far corner is uv 0.5 (" + uMax + ", " + vMax + ")");
            // every index is in range
            bool inRange = true;
            foreach (var i in m0.Indices) if (i >= m0.VertexCount) inRange = false;
            t.True(inRange, "indices stay inside the vertex buffer");

            var m2 = TerrainMeshBuilder.Build(d, 1, 1, 2, 1f);
            t.True(m2.VertexCount == 9 * 9 + 4 * 9, "LOD2: every fourth sample (" + m2.VertexCount + ")");
            t.True(m2.TriangleCount == 8 * 8 * 2 + 4 * 8 * 4, "LOD2 triangles (" + m2.TriangleCount + ")");
            t.True(m2.Min.X == 32f && m2.Max.X == 64f, "the second chunk starts at 32 m");

            t.True(TerrainMeshBuilder.LodFor(10f, 48f) == 0 && TerrainMeshBuilder.LodFor(60f, 48f) == 1 && TerrainMeshBuilder.LodFor(200f, 48f) == 2, "LOD by distance");
            TerrainMeshBuilder.ChunkRange(new SampleRect { X0 = 30, Z0 = 2, X1 = 34, Z1 = 3 }, 65, out int cx0, out int cz0, out int cx1, out int cz1);
            t.True(cx0 == 0 && cx1 == 1 && cz0 == 0 && cz1 == 0, "a rect across the chunk seam dirties both chunks (" + cx0 + ".." + cx1 + ")");

            var tris = TerrainMeshBuilder.Triangles(d, 1f, 1);
            t.True(tris.Length == 64 * 64 * 2 * 9, "the collision soup covers every cell (" + tris.Length + ")");
            var coarse = TerrainMeshBuilder.Triangles(d, 1f, 4);
            t.True(coarse.Length == 16 * 16 * 2 * 9, "a stride thins the soup (" + coarse.Length + ")");
        }

        [Test]
        public static void ResolutionSnapsToLegalValues(TestContext t)
        {
            t.True(Editor.ECS.Components.Rendering.Terrain.SnapResolution(100) == 129, "100 -> 129");
            t.True(Editor.ECS.Components.Rendering.Terrain.SnapResolution(5) == 33, "5 -> 33");
            t.True(Editor.ECS.Components.Rendering.Terrain.SnapResolution(600) == 513, "600 -> 513");
            t.True(Editor.ECS.Components.Rendering.Terrain.SnapResolution(100000) == 2049, "huge -> 2049");
            var c = new Editor.ECS.Components.Rendering.Terrain { Size = 128f, Resolution = 129 };
            t.True(Math.Abs(c.CellSize - 1f) < 1e-6f, "128 m / 128 cells = 1 m");
            t.True(Math.Abs(TerrainData.Falloff(0f, 10f, 0f) - 1f) < 1e-6f && TerrainData.Falloff(10f, 10f, 0f) == 0f && Math.Abs(TerrainData.Falloff(5f, 10f, 0f) - 0.5f) < 1e-6f, "the falloff is 1 at the centre, 0 at the rim, ½ halfway");
        }
    }
}
