using System;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>Green collider gizmos (box / sphere / capsule / mesh-bounds) drawn in the editor viewport and the
    /// Collision Editor preview via the always-on-top gizmo pass, so you SEE an entity's collision shape exactly where
    /// it sits. Rendered as a FINE WIREFRAME NET (one draw per whole shape through the wire gizmo PSO — same look as
    /// the audio range spheres, just green) instead of a sparse edge frame, so the collision volume reads clearly in
    /// 3D. The material is UNLIT so the green stays bright and readable from every angle / in shadow.</summary>
    public static partial class VortexAPI
    {
        private static long _colliderMaterial = ID.INVALID_ID;   // bright unlit green net
        private static long _gizmoCylinder = ID.INVALID_ID;      // capsule body (radius 0.5, height 1.0, Y axis)

        private static void EnsureColliderResources()
        {
            if (!_gizmosInitialized) InitializeGizmos();
            if (_colliderMaterial == ID.INVALID_ID && _gizmoCube != ID.INVALID_ID)
                _colliderMaterial = MakeUnlitMaterial(0.25f, 0.95f, 0.45f);
            if (_gizmoCylinder == ID.INVALID_ID)
                _gizmoCylinder = CreateCylinderMesh(0.5f, 1.0f);
        }

        /// <summary>Axis-aligned-then-Y-rotated wireframe-net box (unit _gizmoCube scaled to full extents). Only Y
        /// rotation, matching the runtime collision test which rotates colliders about Y.</summary>
        public static void RenderColliderBox(float cx, float cy, float cz, float hx, float hy, float hz, float rotYdeg)
        {
            EnsureColliderResources();
            if (_colliderMaterial == ID.INVALID_ID || _gizmoCube == ID.INVALID_ID) return;
            double r = rotYdeg * Math.PI / 180.0; float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
            // Rows are the Y-rotated unit axes scaled to the box's full extents (local x maps to (c,0,-s), z to (s,0,c)).
            SubmitGizmoWireForRendering(_gizmoCube, _colliderMaterial, BuildBasisMatrix(
                cx, cy, cz,
                c, 0f, -s, hx * 2f,
                0f, 1f, 0f, hy * 2f,
                s, 0f, c, hz * 2f));
        }

        public static void RenderColliderSphere(float cx, float cy, float cz, float rad)
        {
            EnsureColliderResources();
            AudioWireSphere(cx, cy, cz, rad, _colliderMaterial);   // shared wire-net sphere (radius clamp inside)
        }

        /// <summary>Draw the entity's actual render mesh as a green wireframe net at its world transform — the faithful
        /// collision preview for a Mesh Collider (the collision mesh IS the render mesh here), so a round object shows a
        /// round green net instead of a box around its bounds.</summary>
        public static void RenderColliderMeshWire(long meshId, float[] worldMatrix)
        {
            EnsureColliderResources();
            if (_colliderMaterial == ID.INVALID_ID || meshId < 0 || worldMatrix == null) return;
            SubmitGizmoWireForRendering(meshId, _colliderMaterial, worldMatrix);
        }

        /// <summary>Capsule = a wire-net cylinder body plus a wire-net sphere at each cap. halfH is the half-height of
        /// the CYLINDRICAL section (caps sit at cy +/- halfH); a zero-height capsule collapses to a single sphere.</summary>
        public static void RenderColliderCapsule(float cx, float cy, float cz, float rad, float halfH)
        {
            EnsureColliderResources();
            if (_colliderMaterial == ID.INVALID_ID) return;
            if (halfH > 0.005f && _gizmoCylinder != ID.INVALID_ID)
            {
                // Cylinder body (mesh radius 0.5, height 1.0 along Y -> scale to diameter / full cylinder height).
                SubmitGizmoWireForRendering(_gizmoCylinder, _colliderMaterial, BuildBasisMatrix(
                    cx, cy, cz,
                    1f, 0f, 0f, rad * 2f,
                    0f, 1f, 0f, halfH * 2f,
                    0f, 0f, 1f, rad * 2f));
                AudioWireSphere(cx, cy + halfH, cz, rad, _colliderMaterial);
                AudioWireSphere(cx, cy - halfH, cz, rad, _colliderMaterial);
            }
            else
            {
                AudioWireSphere(cx, cy, cz, rad, _colliderMaterial);
            }
        }
    }
}
