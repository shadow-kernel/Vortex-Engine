using System;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>
    /// VortexAPI - Selection outline rendering.
    /// </summary>
    public static partial class VortexAPI
    {
        /// <summary>
        /// Render an orange selection outline around a selected object with rotation.
        /// </summary>
        public static void RenderSelectionOutline(float posX, float posY, float posZ, 
            float scaleX, float scaleY, float scaleZ,
            float rotX, float rotY, float rotZ)
        {
            if (!_gizmosInitialized) InitializeGizmos();
            if (_gizmoCube == ID.INVALID_ID || _outlineMaterial == ID.INVALID_ID) return;

            float edgeThickness = 0.05f;   // thicker, clearly-visible selection box (also renders always-on-top)
            float halfX = scaleX * 0.5f + edgeThickness;
            float halfY = scaleY * 0.5f + edgeThickness;
            float halfZ = scaleZ * 0.5f + edgeThickness;

            // Pre-calculate rotation matrix
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

            // Define the 8 corners of the bounding box in local space
            float[][] corners = new float[][]
            {
                new float[] { -halfX, -halfY, -halfZ },
                new float[] {  halfX, -halfY, -halfZ },
                new float[] {  halfX, -halfY,  halfZ },
                new float[] { -halfX, -halfY,  halfZ },
                new float[] { -halfX,  halfY, -halfZ },
                new float[] {  halfX,  halfY, -halfZ },
                new float[] {  halfX,  halfY,  halfZ },
                new float[] { -halfX,  halfY,  halfZ }
            };

            // Transform corners to world space
            float[][] worldCorners = new float[8][];
            for (int i = 0; i < 8; i++)
            {
                float lx = corners[i][0], ly = corners[i][1], lz = corners[i][2];
                worldCorners[i] = new float[]
                {
                    posX + r00 * lx + r10 * ly + r20 * lz,
                    posY + r01 * lx + r11 * ly + r21 * lz,
                    posZ + r02 * lx + r12 * ly + r22 * lz
                };
            }

            // Draw 12 edges connecting the corners
            // Bottom face edges: 0-1, 1-2, 2-3, 3-0
            RenderEdgeLine(worldCorners[0], worldCorners[1], edgeThickness);
            RenderEdgeLine(worldCorners[1], worldCorners[2], edgeThickness);
            RenderEdgeLine(worldCorners[2], worldCorners[3], edgeThickness);
            RenderEdgeLine(worldCorners[3], worldCorners[0], edgeThickness);

            // Top face edges: 4-5, 5-6, 6-7, 7-4
            RenderEdgeLine(worldCorners[4], worldCorners[5], edgeThickness);
            RenderEdgeLine(worldCorners[5], worldCorners[6], edgeThickness);
            RenderEdgeLine(worldCorners[6], worldCorners[7], edgeThickness);
            RenderEdgeLine(worldCorners[7], worldCorners[4], edgeThickness);

            // Vertical edges: 0-4, 1-5, 2-6, 3-7
            RenderEdgeLine(worldCorners[0], worldCorners[4], edgeThickness);
            RenderEdgeLine(worldCorners[1], worldCorners[5], edgeThickness);
            RenderEdgeLine(worldCorners[2], worldCorners[6], edgeThickness);
            RenderEdgeLine(worldCorners[3], worldCorners[7], edgeThickness);
        }

        private static void RenderEdgeLine(float[] p1, float[] p2, float thickness)
        {
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

            // Build rotation matrix to align Y axis with edge direction
            // We need to rotate the cube so its Y axis points along (dx, dy, dz)
            float[] matrix = BuildEdgeMatrix(midX, midY, midZ, dx, dy, dz, length, thickness);
            SubmitGizmoForRendering(_gizmoCube, _outlineMaterial, matrix);
        }

        private static float[] BuildEdgeMatrix(float px, float py, float pz, float dx, float dy, float dz, float length, float thickness)
        {
            // We want the Y axis of the cube to point along (dx, dy, dz)
            // Find perpendicular vectors for X and Z
            float ax, ay, az;
            if (Math.Abs(dy) < 0.9f)
            {
                // Cross with world up
                ax = dz; ay = 0; az = -dx;
            }
            else
            {
                // Cross with world forward
                ax = 1; ay = 0; az = 0;
            }
            float aLen = (float)Math.Sqrt(ax * ax + ay * ay + az * az);
            if (aLen > 0.001f) { ax /= aLen; ay /= aLen; az /= aLen; }

            // Z axis = cross(direction, X axis)
            float bx = dy * az - dz * ay;
            float by = dz * ax - dx * az;
            float bz = dx * ay - dy * ax;

            // Scale: X and Z are thickness, Y is length
            return new float[]
            {
                ax * thickness, ay * thickness, az * thickness, 0,
                dx * length,    dy * length,    dz * length,    0,
                bx * thickness, by * thickness, bz * thickness, 0,
                px, py, pz, 1
            };
        }

        // Keep backward compatible overload
        public static void RenderSelectionOutline(float posX, float posY, float posZ, float scaleX, float scaleY, float scaleZ)
        {
            RenderSelectionOutline(posX, posY, posZ, scaleX, scaleY, scaleZ, 0, 0, 0);
        }
    }
}
