using System;
using Editor.Core.Services.Physics;
using Editor.ECS;

namespace VortexTests
{
    /// <summary>The MeshCollider triangle grid (#342): a terrain-sized collision mesh must not test every triangle
    /// per capsule sample / raycast. These tests confirm the grid gives the SAME answers as the brute-force loop and
    /// touches only a tiny fraction of the triangles.</summary>
    public static class CollisionGridTests
    {
        // A heightfield terrain as a flat world-space triangle soup (x,y,z per vertex, 3 vertices per triangle).
        private static float[] Terrain(int n, float size, float amp)
        {
            float step = size / (n - 1);
            float H(float x, float z) => amp * (float)(Math.Sin(x * 0.15) * Math.Cos(z * 0.15));
            var tris = new System.Collections.Generic.List<float>((n - 1) * (n - 1) * 18);
            void V(float x, float z) { tris.Add(x); tris.Add(H(x, z)); tris.Add(z); }
            for (int j = 0; j < n - 1; j++)
                for (int i = 0; i < n - 1; i++)
                {
                    float x0 = i * step, x1 = (i + 1) * step, z0 = j * step, z1 = (j + 1) * step;
                    V(x0, z0); V(x1, z0); V(x1, z1);     // triangle 1
                    V(x0, z0); V(x1, z1); V(x0, z1);     // triangle 2
                }
            return tris.ToArray();
        }

        private static float Height(float x, float z, float amp) => amp * (float)(Math.Sin(x * 0.15) * Math.Cos(z * 0.15));

        [Test]
        public static void GridBuildsForLargeMeshesOnly(TestContext t)
        {
            t.True(CollisionService.TestGridBuilt(Terrain(80, 200f, 2f)), "a terrain mesh gets a grid");
            // a single quad (2 triangles) is below the threshold — brute force, no grid
            var quad = new float[] { 0,0,0, 1,0,0, 1,0,1,  0,0,0, 1,0,1, 0,0,1 };
            t.False(CollisionService.TestGridBuilt(quad), "a tiny mesh stays brute-force");
        }

        [Test]
        public static void ClosestPointMatchesBruteForce(TestContext t)
        {
            var tris = Terrain(80, 200f, 2f);   // ~12.5k triangles
            const float maxDist = 0.6f;
            var rng = new Random(1234);
            int checkedWithin = 0;
            for (int k = 0; k < 250; k++)
            {
                float x = (float)rng.NextDouble() * 180f + 10f;
                float z = (float)rng.NextDouble() * 180f + 10f;
                float y = Height(x, z, 2f) + (float)(rng.NextDouble() * 1.4 - 0.2);   // around the surface
                var c = new Vector3(x, y, z);

                var qb = CollisionService.TestClosestOnTris(tris, c, maxDist, false, out bool hb);
                var qg = CollisionService.TestClosestOnTris(tris, c, maxDist, true, out bool hg);
                t.True(hb, "brute always finds a nearest triangle");
                float db = Dist(c, qb);
                // the grid must never invent a contact CLOSER than the true global nearest
                if (hg) t.True(Dist(c, qg) >= db - 1e-3f, "grid is never closer than the global nearest");
                // and within the search radius it must find exactly that nearest (no missed contacts)
                if (db <= maxDist)
                {
                    checkedWithin++;
                    t.True(hg, "grid finds the contact when one is within maxDist");
                    t.True(Math.Abs(Dist(c, qg) - db) < 1e-3f, "grid closest distance matches brute force");
                }
            }
            t.True(checkedWithin > 50, "the sample actually exercised in-range contacts (" + checkedWithin + ")");
        }

        [Test]
        public static void ClosestPointIsSelective(TestContext t)
        {
            var tris = Terrain(160, 400f, 2f);   // ~50k triangles (the reported case)
            int total = tris.Length / 9;
            var c = new Vector3(200f, Height(200f, 200f, 2f) + 0.3f, 200f);
            CollisionService.TestClosestOnTris(tris, c, 0.6f, true, out _);
            int tested = CollisionService.DebugLastTriTests;
            t.True(tested > 0 && tested < total / 50, "grid tests a tiny fraction (" + tested + " of " + total + ")");
        }

        [Test]
        public static void RayDownMatchesBruteForce(TestContext t)
        {
            var tris = Terrain(80, 200f, 2f);
            int total = tris.Length / 9;
            var rng = new Random(77);
            for (int k = 0; k < 120; k++)
            {
                float x = (float)rng.NextDouble() * 180f + 10f;
                float z = (float)rng.NextDouble() * 180f + 10f;
                var o = new Vector3(x, 50f, z);
                bool gb = CollisionService.TestRayDownTris(tris, o, 100f, false, out float tb);
                bool gg = CollisionService.TestRayDownTris(tris, o, 100f, true, out float tg);
                t.Equal(gb, gg, "grid and brute agree on whether the down-ray hits");
                if (gb) t.True(Math.Abs(tb - tg) < 1e-3f, "down-ray hit distance matches");
            }
            int tested = CollisionService.DebugLastTriTests;
            t.True(tested < total / 20, "a vertical ray only tests a column (" + tested + " of " + total + ")");
        }

        [Test]
        public static void RaycastMatchesBruteForce(TestContext t)
        {
            var tris = Terrain(100, 260f, 3f);
            int total = tris.Length / 9;
            var rng = new Random(9);
            int hits = 0;
            for (int k = 0; k < 120; k++)
            {
                float x = (float)rng.NextDouble() * 220f + 20f;
                float z = (float)rng.NextDouble() * 220f + 20f;
                var o = new Vector3(x, 40f, z);
                // aim down and slightly sideways at the terrain
                var d = Norm(new Vector3((float)(rng.NextDouble() - 0.5), -1f, (float)(rng.NextDouble() - 0.5)));
                bool gb = CollisionService.TestRaycastTris(tris, o, d, 200f, false, out float tb, out _);
                bool gg = CollisionService.TestRaycastTris(tris, o, d, 200f, true, out float tg, out _);
                t.Equal(gb, gg, "grid and brute agree on whether the ray hits");
                if (gb) { hits++; t.True(Math.Abs(tb - tg) < 2e-3f, "ray hit distance matches (" + tb + " vs " + tg + ")"); }
            }
            t.True(hits > 40, "most rays hit the terrain (" + hits + ")");
            t.True(CollisionService.DebugLastTriTests < total / 10, "an angled ray only walks cells along it (" + CollisionService.DebugLastTriTests + " of " + total + ")");
        }

        private static float Dist(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static Vector3 Norm(Vector3 v)
        {
            float l = (float)Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return l > 1e-8f ? new Vector3(v.X / l, v.Y / l, v.Z / l) : new Vector3(0, -1, 0);
        }
    }
}
