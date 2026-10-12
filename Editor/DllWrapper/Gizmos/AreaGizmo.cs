using System;
using System.Collections.Generic;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>The Area component's outline in the viewport: its box, sphere or cylinder, turned and scaled by the entity's
    /// world matrix, in the area's own colour (brighter and thicker when selected).</summary>
    public static partial class VortexAPI
    {
        private static readonly Dictionary<int, long> _areaMaterials = new Dictionary<int, long>();

        private static long AreaMaterial(float r, float g, float b)
        {
            int key = ((int)(r * 255) << 16) | ((int)(g * 255) << 8) | (int)(b * 255);
            if (_areaMaterials.TryGetValue(key, out var id) && id > 0) return id;
            id = MakeUnlitMaterial(r, g, b);
            if (id > 0) _areaMaterials[key] = id;
            return id;
        }

        /// <summary>Draw an area. <paramref name="world"/> is the entity's world matrix (16 floats, rows = the images of the
        /// unit axes, last row the translation); shape 0 box (sx, sy, sz full sizes), 1 sphere (sx the diameter), 2 cylinder
        /// (sx the diameter, sy the height).</summary>
        public static void RenderAreaGizmo(float[] world, int shape, float sx, float sy, float sz, float r, float g, float b, bool selected)
        {
            if (world == null || world.Length < 16) return;
            if (!_gizmosInitialized) InitializeGizmos();
            if (_gizmoCube == ID.INVALID_ID) return;
            float k = selected ? 1f : 0.55f;
            long material = AreaMaterial(Math.Min(1f, r * k + (selected ? 0.1f : 0f)), Math.Min(1f, g * k + (selected ? 0.1f : 0f)), Math.Min(1f, b * k + (selected ? 0.1f : 0f)));
            if (material <= 0) return;
            float[] Basis(float ex, float ey, float ez) => new float[]
            {
                world[0] * ex, world[1] * ex, world[2] * ex, 0,
                world[4] * ey, world[5] * ey, world[6] * ey, 0,
                world[8] * ez, world[9] * ez, world[10] * ez, 0,
                world[12], world[13], world[14], 1
            };
            if (shape == 1)
            {
                if (_gizmoSphere != ID.INVALID_ID) SubmitGizmoWireForRendering(_gizmoSphere, material, Basis(sx, sx, sx));
                return;
            }
            if (shape == 2)
            {
                // two rings and four uprights, as thin edge boxes
                const int N = 32;
                float rad = sx * 0.5f, hy = sy * 0.5f;
                var seg = new float[6];
                for (int ring = 0; ring < 2; ring++)
                {
                    float y = ring == 0 ? -hy : hy;
                    for (int i = 0; i < N; i++)
                    {
                        double a0 = i * Math.PI * 2.0 / N, a1 = (i + 1) * Math.PI * 2.0 / N;
                        Local(world, (float)Math.Cos(a0) * rad, y, (float)Math.Sin(a0) * rad, seg, 0);
                        Local(world, (float)Math.Cos(a1) * rad, y, (float)Math.Sin(a1) * rad, seg, 3);
                        SubmitDebugSegment(seg, 0, material);
                    }
                }
                for (int i = 0; i < 4; i++)
                {
                    double a = i * Math.PI * 0.5;
                    Local(world, (float)Math.Cos(a) * rad, -hy, (float)Math.Sin(a) * rad, seg, 0);
                    Local(world, (float)Math.Cos(a) * rad, hy, (float)Math.Sin(a) * rad, seg, 3);
                    SubmitDebugSegment(seg, 0, material);
                }
                return;
            }
            SubmitGizmoWireForRendering(_gizmoCube, material, Basis(sx, sy, sz));
        }

        private static void Local(float[] w, float x, float y, float z, float[] into, int at)
        {
            into[at] = w[0] * x + w[4] * y + w[8] * z + w[12];
            into[at + 1] = w[1] * x + w[5] * y + w[9] * z + w[13];
            into[at + 2] = w[2] * x + w[6] * y + w[10] * z + w[14];
        }
    }
}
