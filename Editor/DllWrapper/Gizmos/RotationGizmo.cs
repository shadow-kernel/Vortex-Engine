using System;
using Editor.Core.Services;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>
    /// VortexAPI - Rotation Gizmo rendering (Rotate tool).
    /// Uses smooth circles with many segments.
    /// </summary>
    public static partial class VortexAPI
    {
        private const int ROTATION_CIRCLE_SEGMENTS = 64; // Smooth circles

        public static void RenderRotationGizmo(float posX, float posY, float posZ, float scale = 1.0f)
        {
            if (!_gizmosInitialized) InitializeGizmos();
            if (_gizmoSphere == ID.INVALID_ID) return;

            float radius = GIZMO_LENGTH * scale;

            long matX = HoveredAxis == GizmoAxis.X ? _gizmoMaterialRedHighlight : _gizmoMaterialRed;
            long matY = HoveredAxis == GizmoAxis.Y ? _gizmoMaterialGreenHighlight : _gizmoMaterialGreen;
            long matZ = HoveredAxis == GizmoAxis.Z ? _gizmoMaterialBlueHighlight : _gizmoMaterialBlue;

            if (IsDraggingGizmo && DraggingAxis != GizmoAxis.None)
            {
                int axisIdx = (int)DraggingAxis - 1;
                long mat = DraggingAxis == GizmoAxis.X ? matX : (DraggingAxis == GizmoAxis.Y ? matY : matZ);
                RenderSmoothCircle(posX, posY, posZ, radius, axisIdx, mat);
                return;
            }

            // X rotation circle (in YZ plane)
            RenderSmoothCircle(posX, posY, posZ, radius, 0, matX);
            // Y rotation circle (in XZ plane)
            RenderSmoothCircle(posX, posY, posZ, radius, 1, matY);
            // Z rotation circle (in XY plane)
            RenderSmoothCircle(posX, posY, posZ, radius, 2, matZ);
        }

        /// <summary>
        /// Render a smooth circle using many small spheres.
        /// </summary>
        private static void RenderSmoothCircle(float cx, float cy, float cz, float radius, int axis, long material)
        {
            float sphereSize = 0.03f; // Small spheres for smooth appearance

            for (int i = 0; i < ROTATION_CIRCLE_SEGMENTS; i++)
            {
                float angle = (float)i / ROTATION_CIRCLE_SEGMENTS * (float)(Math.PI * 2);

                float x = 0, y = 0, z = 0;
                switch (axis)
                {
                    case 0: // X axis - circle in YZ plane
                        y = (float)Math.Cos(angle) * radius;
                        z = (float)Math.Sin(angle) * radius;
                        break;
                    case 1: // Y axis - circle in XZ plane
                        x = (float)Math.Cos(angle) * radius;
                        z = (float)Math.Sin(angle) * radius;
                        break;
                    case 2: // Z axis - circle in XY plane
                        x = (float)Math.Cos(angle) * radius;
                        y = (float)Math.Sin(angle) * radius;
                        break;
                }

                float[] matrix = BuildAxisMatrix(cx + x, cy + y, cz + z, sphereSize, sphereSize, sphereSize);
                SubmitMeshForRendering(_gizmoSphere, material, matrix);
            }
        }
    }
}
