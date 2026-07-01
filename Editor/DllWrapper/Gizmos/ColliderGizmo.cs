using System;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>Green wireframe collider gizmos (box / sphere / capsule) drawn in the editor viewport via the
    /// always-on-top gizmo pass, so you can see an entity's collider exactly where it sits. Reuses the gizmo cube +
    /// the edge-matrix helper from the selection outline; only the material (green) differs.</summary>
    public static partial class VortexAPI
    {
        private static long _colliderMaterial = ID.INVALID_ID;

        private static void EnsureColliderMaterial()
        {
            if (!_gizmosInitialized) InitializeGizmos();
            if (_colliderMaterial == ID.INVALID_ID && _gizmoCube != ID.INVALID_ID)
            {
                _colliderMaterial = CreateMaterial();
                if (_colliderMaterial != ID.INVALID_ID) SetMaterialColor(_colliderMaterial, 0.25f, 0.95f, 0.45f, 1.0f);
            }
        }

        private static void ColEdge(float[] p1, float[] p2)
        {
            if (_colliderMaterial == ID.INVALID_ID) return;
            float dx = p2[0] - p1[0], dy = p2[1] - p1[1], dz = p2[2] - p1[2];
            float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 0.0005f) return;
            dx /= len; dy /= len; dz /= len;
            float mx = (p1[0] + p2[0]) * 0.5f, my = (p1[1] + p2[1]) * 0.5f, mz = (p1[2] + p2[2]) * 0.5f;
            SubmitGizmoForRendering(_gizmoCube, _colliderMaterial, BuildEdgeMatrix(mx, my, mz, dx, dy, dz, len, 0.025f));
        }

        public static void RenderColliderBox(float cx, float cy, float cz, float hx, float hy, float hz, float rotYdeg)
        {
            EnsureColliderMaterial(); if (_colliderMaterial == ID.INVALID_ID) return;
            double r = rotYdeg * Math.PI / 180.0; float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
            Func<float, float, float, float[]> pt = (x, y, z) => new float[] { cx + x * c + z * s, cy + y, cz - x * s + z * c };
            float[][] v = { pt(-hx,-hy,-hz), pt(hx,-hy,-hz), pt(hx,-hy,hz), pt(-hx,-hy,hz),
                            pt(-hx,hy,-hz),  pt(hx,hy,-hz),  pt(hx,hy,hz),  pt(-hx,hy,hz) };
            ColEdge(v[0], v[1]); ColEdge(v[1], v[2]); ColEdge(v[2], v[3]); ColEdge(v[3], v[0]);
            ColEdge(v[4], v[5]); ColEdge(v[5], v[6]); ColEdge(v[6], v[7]); ColEdge(v[7], v[4]);
            ColEdge(v[0], v[4]); ColEdge(v[1], v[5]); ColEdge(v[2], v[6]); ColEdge(v[3], v[7]);
        }

        // plane: 0 = XZ (horizontal), 1 = XY, 2 = YZ
        private static void ColRing(float cx, float cy, float cz, float rad, int plane)
        {
            const int seg = 22; float[] prev = null;
            for (int i = 0; i <= seg; i++)
            {
                double a = i * 2 * Math.PI / seg; float u = (float)Math.Cos(a) * rad, w = (float)Math.Sin(a) * rad;
                float[] p = plane == 0 ? new float[] { cx + u, cy, cz + w }
                          : plane == 1 ? new float[] { cx + u, cy + w, cz }
                                       : new float[] { cx, cy + u, cz + w };
                if (prev != null) ColEdge(prev, p); prev = p;
            }
        }

        public static void RenderColliderSphere(float cx, float cy, float cz, float rad)
        {
            EnsureColliderMaterial(); if (_colliderMaterial == ID.INVALID_ID) return;
            ColRing(cx, cy, cz, rad, 0); ColRing(cx, cy, cz, rad, 1); ColRing(cx, cy, cz, rad, 2);
        }

        public static void RenderColliderCapsule(float cx, float cy, float cz, float rad, float halfH)
        {
            EnsureColliderMaterial(); if (_colliderMaterial == ID.INVALID_ID) return;
            ColRing(cx, cy + halfH, cz, rad, 0); ColRing(cx, cy - halfH, cz, rad, 0);
            ColEdge(new float[] { cx + rad, cy - halfH, cz }, new float[] { cx + rad, cy + halfH, cz });
            ColEdge(new float[] { cx - rad, cy - halfH, cz }, new float[] { cx - rad, cy + halfH, cz });
            ColEdge(new float[] { cx, cy - halfH, cz + rad }, new float[] { cx, cy + halfH, cz + rad });
            ColEdge(new float[] { cx, cy - halfH, cz - rad }, new float[] { cx, cy + halfH, cz - rad });
            ColRing(cx, cy + halfH, cz, rad, 1); ColRing(cx, cy - halfH, cz, rad, 1);
        }
    }
}
