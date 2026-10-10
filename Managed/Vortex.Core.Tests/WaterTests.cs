using System;
using Editor.Core.Services.Water;

namespace VortexTests
{
    /// <summary>Water surfaces (#200): the surface mesh carries the ground depth per vertex, stops being wet beyond the bank,
    /// and the component clamps its inputs.</summary>
    public static class WaterTests
    {
        [Test]
        public static void SurfaceCarriesTheDepthOfTheGround(TestContext t)
        {
            // a bowl: ground = -4 m at the centre rising to +2 m at 20 m; water level 0
            Func<float, float, float?> bowl = (x, z) => { float r = (float)Math.Sqrt(x * x + z * z); return -4f + 6f * Math.Min(1f, r / 20f); };
            var s = WaterMeshBuilder.Build(40f, 2f, 0f, 3f, bowl);
            t.True(s.Cells == 20 && s.VertexCount == 21 * 21 && s.TriangleCount == 20 * 20 * 2, "a 40 m surface at 2 m cells is a 20 × 20 grid (" + s.VertexCount + " vertices)");
            int centre = (10 * 21 + 10) * 8;
            t.True(Math.Abs(s.Vertices[centre + 6] - 4f) < 1e-4f, "the centre vertex is 4 m deep (" + s.Vertices[centre + 6] + ")");
            int corner = 0;
            t.True(s.Vertices[corner + 6] < 0f, "the corner vertex lies over the bank: negative depth (" + s.Vertices[corner + 6] + ")");
            t.True(s.WetVertices > 100 && s.WetVertices < 441, "only the basin is wet (" + s.WetVertices + ")");
            t.True(s.Vertices[centre + 1] == 0f && s.Vertices[centre + 4] == 1f, "vertices sit on the surface plane with an up normal");
            t.True(Math.Abs(s.Vertices[centre] - 0f) < 1e-5f && Math.Abs(s.Vertices[centre + 2] - 0f) < 1e-5f, "the grid is centred on the local origin");

            var flat = WaterMeshBuilder.Build(10f, 1f, 5f, 2.5f, (x, z) => null);
            t.True(flat.WetVertices == flat.VertexCount, "without ground every vertex takes the default depth");
            t.True(Math.Abs(flat.Vertices[6] - 2.5f) < 1e-6f, "the default depth is the vertex depth (" + flat.Vertices[6] + ")");
            bool inRange = true;
            foreach (var i in flat.Indices) if (i >= flat.VertexCount) inRange = false;
            t.True(inRange, "indices stay inside the vertex buffer");
        }

        [Test]
        public static void ComponentClampsItsInputs(TestContext t)
        {
            var w = new Editor.ECS.Components.Rendering.Water();
            w.Size = 0.1f; t.True(w.Size == 1f, "size clamps at 1 m");
            w.CellSize = 0.01f; t.True(w.CellSize == 0.25f, "cells clamp at 0.25 m");
            w.Reflection = 2f; t.True(w.Reflection == 1f, "reflection clamps at 1");
            w.WaveHeight = -1f; t.True(w.WaveHeight == 0f, "wave height clamps at 0");
            w.Absorption = 0f; t.True(w.Absorption == 0.1f, "absorption clamps at 0.1 m");
            t.True(w.DeepB > w.DeepR && w.ShallowG > w.ShallowR, "the defaults are a blue-green lake");
        }
    }
}
