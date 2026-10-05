using System;
using Editor.ECS.Components;

namespace Editor.ECS
{
    /// <summary>
    /// World-space math matching the renderer (SceneRenderService): row-major 4×4 matrices for row vectors
    /// (p' = p · M), local = Scale · Rz · Rx · Ry · Translation with Euler angles in degrees, and
    /// world = local · parentWorld. Used by audio (sources, zones and the listener on child entities) and the Claude
    /// tools. Row 1 of a matrix is the entity's up axis, row 2 its forward axis.
    /// </summary>
    public static class TransformMath
    {
        public static float[] Local(Vector3 pos, Vector3 rotDeg, Vector3 scale)
        {
            double d = Math.PI / 180.0;
            float cx = (float)Math.Cos(rotDeg.X * d), sx = (float)Math.Sin(rotDeg.X * d);
            float cy = (float)Math.Cos(rotDeg.Y * d), sy = (float)Math.Sin(rotDeg.Y * d);
            float cz = (float)Math.Cos(rotDeg.Z * d), sz = (float)Math.Sin(rotDeg.Z * d);
            float r00 = cz * cy + sz * sx * sy, r01 = sz * cx, r02 = -cz * sy + sz * sx * cy;
            float r10 = -sz * cy + cz * sx * sy, r11 = cz * cx, r12 = sz * sy + cz * sx * cy;
            float r20 = cx * sy, r21 = -sx, r22 = cx * cy;
            return new[]
            {
                scale.X * r00, scale.X * r01, scale.X * r02, 0,
                scale.Y * r10, scale.Y * r11, scale.Y * r12, 0,
                scale.Z * r20, scale.Z * r21, scale.Z * r22, 0,
                pos.X, pos.Y, pos.Z, 1,
            };
        }

        public static float[] Local(Transform t) => t == null ? Identity() : Local(t.LocalPosition, t.LocalRotation, t.LocalScale);

        /// <summary>The entity's world matrix (its local matrix times every parent's).</summary>
        public static float[] World(GameEntity e)
        {
            if (e?.Transform == null) return Identity();
            var m = Local(e.Transform);
            return e.Parent == null ? m : Multiply(m, World(e.Parent));
        }

        public static Vector3 WorldPosition(GameEntity e)
        {
            var m = World(e);
            return new Vector3(m[12], m[13], m[14]);
        }

        public static Vector3 TransformPoint(float[] m, Vector3 p) => new Vector3(
            p.X * m[0] + p.Y * m[4] + p.Z * m[8] + m[12],
            p.X * m[1] + p.Y * m[5] + p.Z * m[9] + m[13],
            p.X * m[2] + p.Y * m[6] + p.Z * m[10] + m[14]);

        /// <summary>A matrix row as a unit vector: row 1 = the entity's up, row 2 = its forward (scale removed).</summary>
        public static Vector3 Axis(float[] m, int row)
        {
            float x = m[row * 4], y = m[row * 4 + 1], z = m[row * 4 + 2];
            float len = Len(x, y, z);
            return len < 1e-12f ? new Vector3(0, row == 1 ? 1 : 0, row == 2 ? 1 : 0) : new Vector3(x / len, y / len, z / len);
        }

        public static float[] Identity() => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public static float[] Multiply(float[] a, float[] b)
        {
            var r = new float[16];
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                {
                    float s = 0;
                    for (int k = 0; k < 4; k++) s += a[row * 4 + k] * b[k * 4 + col];
                    r[row * 4 + col] = s;
                }
            return r;
        }

        /// <summary>Inverse of an affine matrix (null when singular, e.g. a zero scale).</summary>
        public static float[] InverseAffine(float[] m)
        {
            // 3×3 part
            float a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], i = m[10];
            float A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
            float det = a * A + b * B + c * C;
            if (Math.Abs(det) < 1e-12f) return null;
            float inv = 1f / det;
            var r = new float[16];
            r[0] = A * inv; r[1] = -(b * i - c * h) * inv; r[2] = (b * f - c * e) * inv;
            r[4] = B * inv; r[5] = (a * i - c * g) * inv; r[6] = -(a * f - c * d) * inv;
            r[8] = C * inv; r[9] = -(a * h - b * g) * inv; r[10] = (a * e - b * d) * inv;
            float tx = m[12], ty = m[13], tz = m[14];
            r[12] = -(tx * r[0] + ty * r[4] + tz * r[8]);
            r[13] = -(tx * r[1] + ty * r[5] + tz * r[9]);
            r[14] = -(tx * r[2] + ty * r[6] + tz * r[10]);
            r[15] = 1;
            return r;
        }

        /// <summary>Position, Euler angles (degrees, the engine's Rz·Rx·Ry order) and scale of an affine matrix
        /// (no shear — true for the editor's transforms unless a parent is scaled non-uniformly AND rotated).</summary>
        public static void Decompose(float[] m, out Vector3 pos, out Vector3 rotDeg, out Vector3 scale)
        {
            pos = new Vector3(m[12], m[13], m[14]);
            float sx = Len(m[0], m[1], m[2]), sy = Len(m[4], m[5], m[6]), sz = Len(m[8], m[9], m[10]);
            // a mirrored matrix: put the sign on X
            float det = m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]);
            if (det < 0) sx = -sx;
            scale = new Vector3(sx, sy, sz);
            float r00 = Div(m[0], sx), r01 = Div(m[1], sx);
            float r11 = Div(m[5], sy);
            float r20 = Div(m[8], sz), r21 = Div(m[9], sz), r22 = Div(m[10], sz);
            // r21 = -sin X; r20 = cos X · sin Y; r22 = cos X · cos Y; r01 = sin Z · cos X; r11 = cos Z · cos X
            double x = Math.Asin(Math.Max(-1.0, Math.Min(1.0, -r21)));
            double y, z;
            if (Math.Abs(Math.Cos(x)) > 1e-5)
            {
                y = Math.Atan2(r20, r22);
                z = Math.Atan2(r01, r11);
            }
            else
            {
                // gimbal lock: fold Z into Y
                y = Math.Atan2(-Div(m[2], sx), r00);
                z = 0;
            }
            double k = 180.0 / Math.PI;
            rotDeg = new Vector3(Clean(x * k), Clean(y * k), Clean(z * k));
        }

        private static float Len(float x, float y, float z) => (float)Math.Sqrt(x * x + y * y + z * z);
        private static float Div(float v, float s) => Math.Abs(s) < 1e-12f ? 0 : v / s;
        private static float Clean(double deg) { float f = (float)Math.Round(deg, 4); return f == 0 ? 0 : f; }

        /// <summary>Yaw/pitch (degrees, editor-camera convention: forward = (sin yaw·cos pitch, −sin pitch,
        /// cos yaw·cos pitch)) that look from <paramref name="from"/> at <paramref name="to"/>.</summary>
        public static void LookAngles(Vector3 from, Vector3 to, out float yaw, out float pitch)
        {
            float dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
            float len = Len(dx, dy, dz);
            if (len < 1e-6f) { yaw = 0; pitch = 0; return; }
            yaw = (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
            pitch = (float)(-Math.Asin(dy / len) * 180.0 / Math.PI);
        }
    }
}
