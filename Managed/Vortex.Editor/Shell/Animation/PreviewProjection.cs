using System;
using System.Numerics;
using Editor.Core.Services.Rendering;
using AvPoint = Avalonia.Point;

namespace VortexEditor.Shell.Animation
{
    /// <summary>
    /// Exact mirror of <see cref="PreviewRenderer"/>'s camera (content framing + orbit + LH look-at + vertical-FOV
    /// perspective, aspect = rendered image) so managed overlays — bone joints, reference grids, pick handles — land
    /// on the rendered pixels. The rendered image is stretched over the whole control, so projection maps NDC to the
    /// control rectangle.
    /// </summary>
    public readonly struct PreviewProjection
    {
        public readonly bool Valid;
        public readonly Vector3 Eye, Center, Right, Up, Forward;
        public readonly float TanHalf, Aspect, Near, Distance, Radius;
        public readonly double Width, Height;

        private PreviewProjection(Vector3 eye, Vector3 center, Vector3 right, Vector3 up, Vector3 fwd, float tanHalf, float aspect, float near, float dist, float radius, double w, double h)
        {
            Valid = true; Eye = eye; Center = center; Right = right; Up = up; Forward = fwd;
            TanHalf = tanHalf; Aspect = aspect; Near = near; Distance = dist; Radius = radius; Width = w; Height = h;
        }

        /// <summary>Build the projection for a scene rendered with <paramref name="cam"/> into an image of imgW x imgH
        /// pixels that is shown stretched over a w x h control. imgW/imgH &lt;= 0 = derive from the control size.</summary>
        public static PreviewProjection Compute(PreviewScene scene, PreviewCamera cam, double w, double h, int imgW = 0, int imgH = 0)
        {
            if (scene == null || w < 2 || h < 2) return default;
            float[] f = PreviewRenderer.ComputeFrame(scene);
            float cx = f[0], cy = f[1], cz = f[2], radius = f[3];
            if (cam.Focus != null && cam.Focus.Length >= 3) { cx = cam.Focus[0]; cy = cam.Focus[1]; cz = cam.Focus[2]; }
            float fov = cam.FovDeg > 1f ? cam.FovDeg : 35f;
            float fovHalf = fov * 0.5f * (float)Math.PI / 180f;
            float aspect = imgW > 0 && imgH > 0 ? (float)imgW / imgH : (float)(w / h);
            float fit = aspect < 1f ? aspect : 1f;
            float tanHalf = (float)Math.Tan(fovHalf);
            float dist = radius / (0.92f * tanHalf * fit);
            float pitch = Math.Max(-1.5f, Math.Min(1.5f, cam.Pitch));
            float ds = Math.Max(0.02f, Math.Min(12f, cam.DistScale <= 0f ? 1f : cam.DistScale));
            float d = dist * ds;
            var center = new Vector3(cx, cy, cz);
            var eye = center + d * new Vector3((float)(Math.Cos(pitch) * Math.Sin(cam.Yaw)), (float)Math.Sin(pitch), (float)(Math.Cos(pitch) * Math.Cos(cam.Yaw)));
            // XMMatrixLookAtLH basis: z = forward, x = up × z, y = z × x
            var z = Vector3.Normalize(center - eye);
            var x = Vector3.Cross(Vector3.UnitY, z);
            if (x.LengthSquared() < 1e-10f) x = Vector3.UnitX; else x = Vector3.Normalize(x);
            var y = Vector3.Cross(z, x);
            return new PreviewProjection(eye, center, x, y, z, tanHalf, aspect, Math.Max(0.005f, d * 0.01f), d, radius, w, h);
        }

        /// <summary>World point -> control coordinates (DIPs). False when behind the near plane.</summary>
        public bool Project(Vector3 p, out AvPoint screen)
        {
            screen = default;
            if (!Valid) return false;
            var v = p - Eye;
            float vz = Vector3.Dot(v, Forward);
            if (vz <= Near) return false;
            float ndcX = Vector3.Dot(v, Right) / (TanHalf * Aspect * vz);
            float ndcY = Vector3.Dot(v, Up) / (TanHalf * vz);
            screen = new AvPoint((ndcX * 0.5 + 0.5) * Width, (0.5 - ndcY * 0.5) * Height);
            return true;
        }

        /// <summary>World units per control pixel at the orbit centre's depth (pan so the content tracks the mouse 1:1).</summary>
        public float WorldPerPixel => Valid ? 2f * Distance * TanHalf / (float)Math.Max(1, Height) : 0f;
    }
}
