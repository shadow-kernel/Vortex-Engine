using System;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>
    /// VortexAPI - Camera Gizmo Rendering.
    /// Renders a Unity-style floating camera with FOV frustum lines.
    /// </summary>
    public static partial class VortexAPI
    {
        private static long _cameraMesh = ID.INVALID_ID;
        private static long _cameraMainMaterial = ID.INVALID_ID;  // Purple for main camera
        private static long _cameraGameMaterial = ID.INVALID_ID;  // Blue for game cameras
        private static bool _cameraGizmoInitialized;

        /// <summary>
        /// Initialize camera gizmo resources.
        /// </summary>
        private static void InitializeCameraGizmo()
        {
            if (_cameraGizmoInitialized) return;

            // Create a small cube for the camera body
            _cameraMesh = CreateCubeMesh(0.3f);

            // Purple material for main camera
            _cameraMainMaterial = CreateNewMaterial();
            if (_cameraMainMaterial != ID.INVALID_ID)
            {
                SetMaterialBaseColor(_cameraMainMaterial, 0.608f, 0.349f, 0.714f, 1.0f); // #9B59B6
            }

            // Blue material for game cameras
            _cameraGameMaterial = CreateNewMaterial();
            if (_cameraGameMaterial != ID.INVALID_ID)
            {
                SetMaterialBaseColor(_cameraGameMaterial, 0.337f, 0.612f, 0.839f, 1.0f); // #569CD6
            }


            _cameraGizmoInitialized = true;
        }

        /// <summary>
        /// Render a simple camera icon (just the body, no frustum) for non-selected cameras.
        /// </summary>
        public static void RenderCameraIcon(float posX, float posY, float posZ,
            float rotX, float rotY, float rotZ, bool isMainCamera)
        {
            if (!_cameraGizmoInitialized) InitializeCameraGizmo();
            if (_cameraMesh == ID.INVALID_ID) return;

            long material = isMainCamera ? _cameraMainMaterial : _cameraGameMaterial;
            if (material == ID.INVALID_ID) return;

            // Just render the camera body cube
            float[] worldMatrix = BuildRotatedWorldMatrix(posX, posY, posZ, 0.25f, 0.25f, 0.25f, rotX, rotY, rotZ);
            SubmitGizmoForRendering(_cameraMesh, material, worldMatrix);
            
            // Add a small direction indicator (short forward line)
            float radX = rotX * (float)(Math.PI / 180.0);
            float radY = rotY * (float)(Math.PI / 180.0);
            float radZ = rotZ * (float)(Math.PI / 180.0);

            float cosX = (float)Math.Cos(radX), sinX = (float)Math.Sin(radX);
            float cosY = (float)Math.Cos(radY), sinY = (float)Math.Sin(radY);
            float cosZ = (float)Math.Cos(radZ), sinZ = (float)Math.Sin(radZ);

            // Forward direction
            float r20 = cosX * sinY;
            float r21 = -sinX;
            float r22 = cosX * cosY;

            float forwardX = r20, forwardY = r21, forwardZ = r22;
            
            // Short forward line
            float[] start = new float[] { posX, posY, posZ };
            float[] end = new float[] { posX + forwardX * 0.5f, posY + forwardY * 0.5f, posZ + forwardZ * 0.5f };
            RenderFrustumLine(start, end, 0.02f, material);
        }

        /// <summary>
        /// Render a camera gizmo with frustum lines at the given position and rotation.
        /// </summary>
        /// <param name="posX">Camera position X</param>
        /// <param name="posY">Camera position Y</param>
        /// <param name="posZ">Camera position Z</param>
        /// <param name="rotX">Camera rotation X (degrees)</param>
        /// <param name="rotY">Camera rotation Y (degrees)</param>
        /// <param name="rotZ">Camera rotation Z (degrees)</param>
        /// <param name="fov">Field of view in degrees</param>
        /// <param name="aspectRatio">Aspect ratio (width/height)</param>
        /// <param name="isMainCamera">True for purple main camera, false for blue game camera</param>
        public static void RenderCameraGizmo(float posX, float posY, float posZ,
            float rotX, float rotY, float rotZ,
            float fov, float aspectRatio, bool isMainCamera)
        {
            if (!_cameraGizmoInitialized) InitializeCameraGizmo();
            if (_cameraMesh == ID.INVALID_ID) return;

            // Select material based on camera type
            long material = isMainCamera ? _cameraMainMaterial : _cameraGameMaterial;
            if (material == ID.INVALID_ID) return;

            // Pre-calculate rotation
            float radX = rotX * (float)(Math.PI / 180.0);
            float radY = rotY * (float)(Math.PI / 180.0);
            float radZ = rotZ * (float)(Math.PI / 180.0);

            float cosX = (float)Math.Cos(radX), sinX = (float)Math.Sin(radX);
            float cosY = (float)Math.Cos(radY), sinY = (float)Math.Sin(radY);
            float cosZ = (float)Math.Cos(radZ), sinZ = (float)Math.Sin(radZ);

            // Rotation matrix (ZXY order)
            float r00 = cosZ * cosY + sinZ * sinX * sinY;
            float r01 = sinZ * cosX;
            float r02 = -cosZ * sinY + sinZ * sinX * cosY;
            float r10 = -sinZ * cosY + cosZ * sinX * sinY;
            float r11 = cosZ * cosX;
            float r12 = sinZ * sinY + cosZ * sinX * cosY;
            float r20 = cosX * sinY;
            float r21 = -sinX;
            float r22 = cosX * cosY;

            // Forward, right, up vectors from rotation
            float forwardX = r20, forwardY = r21, forwardZ = r22;
            float rightX = r00, rightY = r01, rightZ = r02;
            float upX = r10, upY = r11, upZ = r12;

            // 1. Render camera body (small cube at position)
            float[] worldMatrix = BuildRotatedWorldMatrix(posX, posY, posZ, 0.3f, 0.3f, 0.3f, rotX, rotY, rotZ);
            SubmitGizmoForRendering(_cameraMesh, material, worldMatrix);

            // 2. Calculate frustum corners for visualization
            float nearDist = 0.5f;   // Visual near plane distance
            float farDist = 2.0f;    // Visual far plane distance
            
            float fovRad = fov * (float)(Math.PI / 180.0);
            float nearHeight = nearDist * (float)Math.Tan(fovRad * 0.5f);
            float nearWidth = nearHeight * aspectRatio;
            float farHeight = farDist * (float)Math.Tan(fovRad * 0.5f);
            float farWidth = farHeight * aspectRatio;

            // Calculate frustum corners in world space
            // Near plane corners
            float[][] nearCorners = new float[4][];
            nearCorners[0] = TransformPoint(posX, posY, posZ, forwardX, forwardY, forwardZ, 
                rightX, rightY, rightZ, upX, upY, upZ, -nearWidth, nearHeight, nearDist);   // Top-left
            nearCorners[1] = TransformPoint(posX, posY, posZ, forwardX, forwardY, forwardZ, 
                rightX, rightY, rightZ, upX, upY, upZ, nearWidth, nearHeight, nearDist);    // Top-right
            nearCorners[2] = TransformPoint(posX, posY, posZ, forwardX, forwardY, forwardZ, 
                rightX, rightY, rightZ, upX, upY, upZ, nearWidth, -nearHeight, nearDist);   // Bottom-right
            nearCorners[3] = TransformPoint(posX, posY, posZ, forwardX, forwardY, forwardZ, 
                rightX, rightY, rightZ, upX, upY, upZ, -nearWidth, -nearHeight, nearDist);  // Bottom-left

            // Far plane corners
            float[][] farCorners = new float[4][];
            farCorners[0] = TransformPoint(posX, posY, posZ, forwardX, forwardY, forwardZ, 
                rightX, rightY, rightZ, upX, upY, upZ, -farWidth, farHeight, farDist);
            farCorners[1] = TransformPoint(posX, posY, posZ, forwardX, forwardY, forwardZ, 
                rightX, rightY, rightZ, upX, upY, upZ, farWidth, farHeight, farDist);
            farCorners[2] = TransformPoint(posX, posY, posZ, forwardX, forwardY, forwardZ, 
                rightX, rightY, rightZ, upX, upY, upZ, farWidth, -farHeight, farDist);
            farCorners[3] = TransformPoint(posX, posY, posZ, forwardX, forwardY, forwardZ, 
                rightX, rightY, rightZ, upX, upY, upZ, -farWidth, -farHeight, farDist);

            // 3. Render frustum lines
            float lineThickness = 0.015f;

            // Lines from camera to near plane corners
            float[] camPos = new float[] { posX, posY, posZ };
            RenderFrustumLine(camPos, nearCorners[0], lineThickness, material);
            RenderFrustumLine(camPos, nearCorners[1], lineThickness, material);
            RenderFrustumLine(camPos, nearCorners[2], lineThickness, material);
            RenderFrustumLine(camPos, nearCorners[3], lineThickness, material);

            // Near plane rectangle
            RenderFrustumLine(nearCorners[0], nearCorners[1], lineThickness, material);
            RenderFrustumLine(nearCorners[1], nearCorners[2], lineThickness, material);
            RenderFrustumLine(nearCorners[2], nearCorners[3], lineThickness, material);
            RenderFrustumLine(nearCorners[3], nearCorners[0], lineThickness, material);

            // Lines from near to far plane corners
            RenderFrustumLine(nearCorners[0], farCorners[0], lineThickness, material);
            RenderFrustumLine(nearCorners[1], farCorners[1], lineThickness, material);
            RenderFrustumLine(nearCorners[2], farCorners[2], lineThickness, material);
            RenderFrustumLine(nearCorners[3], farCorners[3], lineThickness, material);

            // Far plane rectangle
            RenderFrustumLine(farCorners[0], farCorners[1], lineThickness, material);
            RenderFrustumLine(farCorners[1], farCorners[2], lineThickness, material);
            RenderFrustumLine(farCorners[2], farCorners[3], lineThickness, material);
            RenderFrustumLine(farCorners[3], farCorners[0], lineThickness, material);

            // NOTE: Removed semi-transparent frustum plane rendering for cleaner visualization
            // Only thin lines are rendered now
        }

        private static float[] TransformPoint(float posX, float posY, float posZ,
            float fwdX, float fwdY, float fwdZ,
            float rightX, float rightY, float rightZ,
            float upX, float upY, float upZ,
            float localRight, float localUp, float localForward)
        {
            return new float[]
            {
                posX + rightX * localRight + upX * localUp + fwdX * localForward,
                posY + rightY * localRight + upY * localUp + fwdY * localForward,
                posZ + rightZ * localRight + upZ * localUp + fwdZ * localForward
            };
        }

        private static void RenderFrustumLine(float[] p1, float[] p2, float thickness, long material)
        {
            if (_gizmoCube == ID.INVALID_ID) return;

            // Calculate midpoint and length
            float midX = (p1[0] + p2[0]) * 0.5f;
            float midY = (p1[1] + p2[1]) * 0.5f;
            float midZ = (p1[2] + p2[2]) * 0.5f;

            float dx = p2[0] - p1[0];
            float dy = p2[1] - p1[1];
            float dz = p2[2] - p1[2];

            float length = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length < 0.001f) return;

            // Normalize direction
            dx /= length; dy /= length; dz /= length;

            // Calculate rotation to align cube with line direction
            float yaw = (float)Math.Atan2(dx, dz) * (180.0f / (float)Math.PI);
            float pitch = (float)Math.Asin(-dy) * (180.0f / (float)Math.PI);

            float[] worldMatrix = BuildRotatedWorldMatrix(midX, midY, midZ, thickness, thickness, length, pitch, yaw, 0);
            SubmitGizmoForRendering(_gizmoCube, material, worldMatrix);
        }

        private static void RenderFrustumPlane(float[][] corners, long material)
        {
            // Render as two triangles using the gizmo cube scaled very thin
            // This is a simple approximation - a proper implementation would use a quad mesh
            if (_gizmoCube == ID.INVALID_ID) return;

            // Calculate center and dimensions
            float centerX = (corners[0][0] + corners[1][0] + corners[2][0] + corners[3][0]) * 0.25f;
            float centerY = (corners[0][1] + corners[1][1] + corners[2][1] + corners[3][1]) * 0.25f;
            float centerZ = (corners[0][2] + corners[1][2] + corners[2][2] + corners[3][2]) * 0.25f;

            float width = (float)Math.Sqrt(
                Math.Pow(corners[1][0] - corners[0][0], 2) +
                Math.Pow(corners[1][1] - corners[0][1], 2) +
                Math.Pow(corners[1][2] - corners[0][2], 2));

            float height = (float)Math.Sqrt(
                Math.Pow(corners[3][0] - corners[0][0], 2) +
                Math.Pow(corners[3][1] - corners[0][1], 2) +
                Math.Pow(corners[3][2] - corners[0][2], 2));

            // Calculate rotation from normal
            float nx = (corners[1][1] - corners[0][1]) * (corners[3][2] - corners[0][2]) -
                       (corners[1][2] - corners[0][2]) * (corners[3][1] - corners[0][1]);
            float ny = (corners[1][2] - corners[0][2]) * (corners[3][0] - corners[0][0]) -
                       (corners[1][0] - corners[0][0]) * (corners[3][2] - corners[0][2]);
            float nz = (corners[1][0] - corners[0][0]) * (corners[3][1] - corners[0][1]) -
                       (corners[1][1] - corners[0][1]) * (corners[3][0] - corners[0][0]);

            float nLen = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nLen < 0.001f) return;

            float yaw = (float)Math.Atan2(nx / nLen, nz / nLen) * (180.0f / (float)Math.PI);
            float pitch = (float)Math.Asin(-ny / nLen) * (180.0f / (float)Math.PI);

            float[] worldMatrix = BuildRotatedWorldMatrix(centerX, centerY, centerZ, width, height, 0.01f, pitch, yaw, 0);
            SubmitGizmoForRendering(_gizmoCube, material, worldMatrix);
        }

        private static float[] BuildRotatedWorldMatrix(float posX, float posY, float posZ,
            float scaleX, float scaleY, float scaleZ, float rotX, float rotY, float rotZ)
        {
            float radX = rotX * (float)(Math.PI / 180.0);
            float radY = rotY * (float)(Math.PI / 180.0);
            float radZ = rotZ * (float)(Math.PI / 180.0);

            float cosX = (float)Math.Cos(radX), sinX = (float)Math.Sin(radX);
            float cosY = (float)Math.Cos(radY), sinY = (float)Math.Sin(radY);
            float cosZ = (float)Math.Cos(radZ), sinZ = (float)Math.Sin(radZ);

            // Rotation matrix (ZXY order)
            float r00 = cosZ * cosY + sinZ * sinX * sinY;
            float r01 = sinZ * cosX;
            float r02 = -cosZ * sinY + sinZ * sinX * cosY;
            float r10 = -sinZ * cosY + cosZ * sinX * sinY;
            float r11 = cosZ * cosX;
            float r12 = sinZ * sinY + cosZ * sinX * cosY;
            float r20 = cosX * sinY;
            float r21 = -sinX;
            float r22 = cosX * cosY;

            return new float[]
            {
                scaleX * r00, scaleX * r01, scaleX * r02, 0,
                scaleY * r10, scaleY * r11, scaleY * r12, 0,
                scaleZ * r20, scaleZ * r21, scaleZ * r22, 0,
                posX,         posY,         posZ,         1
            };
        }
    }
}
