using System;
using Editor.Core.Services;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>
    /// VortexAPI - Transform Gizmo rendering (Move tool).
    /// </summary>
    public static partial class VortexAPI
    {
        #region Gizmo State

        private static long _gizmoCube = ID.INVALID_ID;
        private static long _gizmoCone = ID.INVALID_ID;
        private static long _gizmoSphere = ID.INVALID_ID;
        private static long _gizmoMaterialRed = ID.INVALID_ID;
        private static long _gizmoMaterialGreen = ID.INVALID_ID;
        private static long _gizmoMaterialBlue = ID.INVALID_ID;
        private static long _gizmoMaterialRedHighlight = ID.INVALID_ID;
        private static long _gizmoMaterialGreenHighlight = ID.INVALID_ID;
        private static long _gizmoMaterialBlueHighlight = ID.INVALID_ID;
        private static long _gizmoMaterialYellow = ID.INVALID_ID;
        private static long _outlineMaterial = ID.INVALID_ID;

        // Gizmo constants
        public const float GIZMO_LENGTH = 1.2f;
        public const float GIZMO_THICKNESS = 0.04f;
        public const float GIZMO_ARROW_SIZE = 0.15f;
        public const float GIZMO_ARROW_LENGTH = 0.3f;

        private static bool _gizmosInitialized = false;

        // Interaction state
        public static GizmoAxis HoveredAxis { get; set; } = GizmoAxis.None;
        public static bool IsDraggingGizmo { get; set; } = false;
        public static GizmoAxis DraggingAxis { get; set; } = GizmoAxis.None;

        // Gizmo mode
        public enum GizmoType { Translate, Rotate, Scale }
        public static GizmoType CurrentGizmoType { get; set; } = GizmoType.Translate;

        #endregion

        #region Initialization

        public static void InitializeGizmos()
        {
            if (_gizmosInitialized) return;

            try
            {
                _gizmoCube = CreatePrimitiveCube(1.0f);
                _gizmoCone = CreatePrimitiveCone(0.5f, 1.0f);
                _gizmoSphere = CreatePrimitiveSphere(0.5f);

                _gizmoMaterialRed = CreateMaterial();
                _gizmoMaterialGreen = CreateMaterial();
                _gizmoMaterialBlue = CreateMaterial();
                _gizmoMaterialRedHighlight = CreateMaterial();
                _gizmoMaterialGreenHighlight = CreateMaterial();
                _gizmoMaterialBlueHighlight = CreateMaterial();
                _gizmoMaterialYellow = CreateMaterial();
                _outlineMaterial = CreateMaterial();

                SetMaterialColor(_gizmoMaterialRed, 0.95f, 0.2f, 0.2f, 1.0f);
                SetMaterialColor(_gizmoMaterialGreen, 0.2f, 0.95f, 0.2f, 1.0f);
                SetMaterialColor(_gizmoMaterialBlue, 0.2f, 0.4f, 0.95f, 1.0f);
                SetMaterialColor(_gizmoMaterialRedHighlight, 1.0f, 0.6f, 0.4f, 1.0f);
                SetMaterialColor(_gizmoMaterialGreenHighlight, 0.6f, 1.0f, 0.4f, 1.0f);
                SetMaterialColor(_gizmoMaterialBlueHighlight, 0.4f, 0.7f, 1.0f, 1.0f);
                SetMaterialColor(_gizmoMaterialYellow, 1.0f, 1.0f, 0.3f, 1.0f);
                SetMaterialColor(_outlineMaterial, 1.0f, 0.6f, 0.2f, 1.0f);

                _gizmosInitialized = true;
            }
            catch { }
        }

        #endregion

        #region Main Render Entry Point

        /// <summary>
        /// Render the appropriate gizmo based on current mode.
        /// Gizmo is positioned at object surface, not center.
        /// </summary>
        public static void RenderGizmo(float posX, float posY, float posZ, float objScaleY, float scale = 1.0f)
        {
            // Position gizmo at top of object (surface) instead of center
            float gizmoPosY = posY + objScaleY * 0.5f;

            switch (CurrentGizmoType)
            {
                case GizmoType.Translate:
                    RenderTransformGizmo(posX, gizmoPosY, posZ, scale);
                    break;
                case GizmoType.Rotate:
                    RenderRotationGizmo(posX, gizmoPosY, posZ, scale);
                    break;
                case GizmoType.Scale:
                    RenderScaleGizmo(posX, gizmoPosY, posZ, scale);
                    break;
            }
        }

        #endregion

        #region Transform Gizmo (Move)

        public static void RenderTransformGizmo(float posX, float posY, float posZ, float scale = 1.0f)
        {
            if (!_gizmosInitialized) InitializeGizmos();
            if (_gizmoCube == ID.INVALID_ID) return;

            float len = GIZMO_LENGTH * scale;
            float thick = GIZMO_THICKNESS * scale;
            float arrowSize = GIZMO_ARROW_SIZE * scale;
            float arrowLen = GIZMO_ARROW_LENGTH * scale;

            if (IsDraggingGizmo && DraggingAxis != GizmoAxis.None)
            {
                RenderDraggingAxis(posX, posY, posZ, scale);
                return;
            }

            long matX = HoveredAxis == GizmoAxis.X ? _gizmoMaterialRedHighlight : _gizmoMaterialRed;
            long matY = HoveredAxis == GizmoAxis.Y ? _gizmoMaterialGreenHighlight : _gizmoMaterialGreen;
            long matZ = HoveredAxis == GizmoAxis.Z ? _gizmoMaterialBlueHighlight : _gizmoMaterialBlue;

            // X axis
            SubmitMeshForRendering(_gizmoCube, matX, BuildAxisMatrix(posX + len * 0.5f, posY, posZ, len, thick, thick));
            SubmitMeshForRendering(_gizmoCone, matX, BuildArrowMatrix(posX + len, posY, posZ, arrowSize, arrowLen, 0));

            // Y axis
            SubmitMeshForRendering(_gizmoCube, matY, BuildAxisMatrix(posX, posY + len * 0.5f, posZ, thick, len, thick));
            SubmitMeshForRendering(_gizmoCone, matY, BuildArrowMatrix(posX, posY + len, posZ, arrowSize, arrowLen, 1));

            // Z axis
            SubmitMeshForRendering(_gizmoCube, matZ, BuildAxisMatrix(posX, posY, posZ + len * 0.5f, thick, thick, len));
            SubmitMeshForRendering(_gizmoCone, matZ, BuildArrowMatrix(posX, posY, posZ + len, arrowSize, arrowLen, 2));
        }

        private static void RenderDraggingAxis(float posX, float posY, float posZ, float scale)
        {
            float extendedLen = 50.0f;
            float thick = GIZMO_THICKNESS * scale * 0.5f;
            float arrowSize = GIZMO_ARROW_SIZE * scale;
            float arrowLen = GIZMO_ARROW_LENGTH * scale;

            long mat;
            switch (DraggingAxis)
            {
                case GizmoAxis.X:
                    mat = _gizmoMaterialRedHighlight;
                    SubmitMeshForRendering(_gizmoCube, _gizmoMaterialYellow, BuildAxisMatrix(posX, posY, posZ, extendedLen * 2, thick, thick));
                    SubmitMeshForRendering(_gizmoCone, mat, BuildArrowMatrix(posX + GIZMO_LENGTH * scale, posY, posZ, arrowSize, arrowLen, 0));
                    break;
                case GizmoAxis.Y:
                    mat = _gizmoMaterialGreenHighlight;
                    SubmitMeshForRendering(_gizmoCube, _gizmoMaterialYellow, BuildAxisMatrix(posX, posY, posZ, thick, extendedLen * 2, thick));
                    SubmitMeshForRendering(_gizmoCone, mat, BuildArrowMatrix(posX, posY + GIZMO_LENGTH * scale, posZ, arrowSize, arrowLen, 1));
                    break;
                case GizmoAxis.Z:
                    mat = _gizmoMaterialBlueHighlight;
                    SubmitMeshForRendering(_gizmoCube, _gizmoMaterialYellow, BuildAxisMatrix(posX, posY, posZ, thick, thick, extendedLen * 2));
                    SubmitMeshForRendering(_gizmoCone, mat, BuildArrowMatrix(posX, posY, posZ + GIZMO_LENGTH * scale, arrowSize, arrowLen, 2));
                    break;
            }
        }

        #endregion

        #region Matrix Builders

        private static float[] BuildAxisMatrix(float px, float py, float pz, float sx, float sy, float sz)
        {
            return new float[] { sx, 0, 0, 0, 0, sy, 0, 0, 0, 0, sz, 0, px, py, pz, 1 };
        }

        private static float[] BuildArrowMatrix(float px, float py, float pz, float size, float length, int axis)
        {
            float r = size, h = length;
            switch (axis)
            {
                case 0: return new float[] { h, 0, 0, 0, 0, r, 0, 0, 0, 0, r, 0, px, py, pz, 1 };
                case 1: return new float[] { r, 0, 0, 0, 0, h, 0, 0, 0, 0, r, 0, px, py, pz, 1 };
                case 2: return new float[] { r, 0, 0, 0, 0, r, 0, 0, 0, 0, h, 0, px, py, pz, 1 };
                default: return BuildAxisMatrix(px, py, pz, size, size, size);
            }
        }

        #endregion
    }
}
