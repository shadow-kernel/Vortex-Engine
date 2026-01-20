using System;
using Editor.Core.Services;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>
    /// VortexAPI - Scale Gizmo rendering (Scale tool).
    /// </summary>
    public static partial class VortexAPI
    {
        public static void RenderScaleGizmo(float posX, float posY, float posZ, float scale = 1.0f)
        {
            if (!_gizmosInitialized) InitializeGizmos();
            if (_gizmoCube == ID.INVALID_ID) return;

            float len = GIZMO_LENGTH * scale;
            float thick = GIZMO_THICKNESS * scale;
            float cubeSize = GIZMO_ARROW_SIZE * scale * 0.8f;

            long matX = HoveredAxis == GizmoAxis.X ? _gizmoMaterialRedHighlight : _gizmoMaterialRed;
            long matY = HoveredAxis == GizmoAxis.Y ? _gizmoMaterialGreenHighlight : _gizmoMaterialGreen;
            long matZ = HoveredAxis == GizmoAxis.Z ? _gizmoMaterialBlueHighlight : _gizmoMaterialBlue;

            if (IsDraggingGizmo && DraggingAxis != GizmoAxis.None)
            {
                RenderScaleDraggingAxis(posX, posY, posZ, scale);
                return;
            }

            // X axis with cube at end
            SubmitMeshForRendering(_gizmoCube, matX, BuildAxisMatrix(posX + len * 0.5f, posY, posZ, len, thick, thick));
            SubmitMeshForRendering(_gizmoCube, matX, BuildAxisMatrix(posX + len, posY, posZ, cubeSize, cubeSize, cubeSize));

            // Y axis with cube at end
            SubmitMeshForRendering(_gizmoCube, matY, BuildAxisMatrix(posX, posY + len * 0.5f, posZ, thick, len, thick));
            SubmitMeshForRendering(_gizmoCube, matY, BuildAxisMatrix(posX, posY + len, posZ, cubeSize, cubeSize, cubeSize));

            // Z axis with cube at end
            SubmitMeshForRendering(_gizmoCube, matZ, BuildAxisMatrix(posX, posY, posZ + len * 0.5f, thick, thick, len));
            SubmitMeshForRendering(_gizmoCube, matZ, BuildAxisMatrix(posX, posY, posZ + len, cubeSize, cubeSize, cubeSize));

            // Center cube for uniform scaling
            SubmitMeshForRendering(_gizmoCube, _gizmoMaterialYellow, BuildAxisMatrix(posX, posY, posZ, cubeSize * 0.8f, cubeSize * 0.8f, cubeSize * 0.8f));
        }

        private static void RenderScaleDraggingAxis(float posX, float posY, float posZ, float scale)
        {
            float extendedLen = 50.0f;
            float thick = GIZMO_THICKNESS * scale * 0.5f;
            float cubeSize = GIZMO_ARROW_SIZE * scale * 0.8f;

            switch (DraggingAxis)
            {
                case GizmoAxis.X:
                    SubmitMeshForRendering(_gizmoCube, _gizmoMaterialYellow, BuildAxisMatrix(posX, posY, posZ, extendedLen * 2, thick, thick));
                    SubmitMeshForRendering(_gizmoCube, _gizmoMaterialRedHighlight, BuildAxisMatrix(posX + GIZMO_LENGTH * scale, posY, posZ, cubeSize, cubeSize, cubeSize));
                    break;
                case GizmoAxis.Y:
                    SubmitMeshForRendering(_gizmoCube, _gizmoMaterialYellow, BuildAxisMatrix(posX, posY, posZ, thick, extendedLen * 2, thick));
                    SubmitMeshForRendering(_gizmoCube, _gizmoMaterialGreenHighlight, BuildAxisMatrix(posX, posY + GIZMO_LENGTH * scale, posZ, cubeSize, cubeSize, cubeSize));
                    break;
                case GizmoAxis.Z:
                    SubmitMeshForRendering(_gizmoCube, _gizmoMaterialYellow, BuildAxisMatrix(posX, posY, posZ, thick, thick, extendedLen * 2));
                    SubmitMeshForRendering(_gizmoCube, _gizmoMaterialBlueHighlight, BuildAxisMatrix(posX, posY, posZ + GIZMO_LENGTH * scale, cubeSize, cubeSize, cubeSize));
                    break;
            }
        }
    }
}
