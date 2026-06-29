using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.DllWrapper;

namespace Editor.Core.Services.Rendering
{
    /// <summary>
    /// Shared offscreen preview renderer for the asset editors. Renders a mesh / material-sphere /
    /// imported model into an engine secondary render target and reads it back as a frozen bitmap,
    /// so Material/Mesh/Model editors can all show a real 3D preview without duplicating the
    /// DX12 offscreen-render plumbing. Returns null if the engine/viewport isn't ready yet (the
    /// caller should fall back to a placeholder). All methods are safe to call off-frame.
    /// </summary>
    public static class AssetPreviewRenderer
    {
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, int count);

        // Reused offscreen render target — creating + destroying a DX12 render target on EVERY preview render
        // (especially during orbit) was a major cost. Cache it by size; recreate only when the size changes.
        private static uint _cachedRt;
        private static int _cachedSize;
        private static uint AcquireTarget(int size)
        {
            if (_cachedRt != 0 && _cachedSize == size) return _cachedRt;
            if (_cachedRt != 0) { try { VortexAPI.DestroySecondaryRenderTarget(_cachedRt); } catch { } _cachedRt = 0; }
            _cachedRt = VortexAPI.CreateSecondaryRenderTarget((uint)size, (uint)size);
            _cachedSize = size;
            return _cachedRt;
        }
        /// <summary>Release the cached preview render target (call when the editor/dialogs close).</summary>
        public static void DestroyPreviewTarget()
        {
            if (_cachedRt != 0) { try { VortexAPI.DestroySecondaryRenderTarget(_cachedRt); } catch { } _cachedRt = 0; _cachedSize = 0; }
        }

        /// <summary>
        /// Renders one or more (sub)meshes with their materials to a square bitmap, framed by the
        /// combined bounding sphere with neutral studio lighting.
        /// </summary>
        public static ImageSource RenderMeshes(long[] meshIds, long[] materialIds, int size)
            => RenderMeshes(meshIds, materialIds, size, 0.74f, 0.62f, 1f);

        /// <summary>Orbit-aware render: yaw/pitch (radians) rotate the camera around the asset, distScale zooms.</summary>
        public static ImageSource RenderMeshes(long[] meshIds, long[] materialIds, int size, float yaw, float pitch, float distScale)
        {
            if (meshIds == null || meshIds.Length == 0) return null;
            uint rt = AcquireTarget(size);   // cached + reused (not created/destroyed per render)
            if (rt == 0) return null;
            try
            {
                float radius = 0.4f, cx = 0, cy = 0, cz = 0; bool gotCenter = false;
                foreach (var m in meshIds)
                {
                    if (VortexAPI.GetMeshBounds(m, out float sx, out float sy, out float sz))
                    {
                        float rr = 0.5f * (float)Math.Sqrt(sx * sx + sy * sy + sz * sz);
                        if (rr > radius) radius = rr;
                    }
                    if (!gotCenter && VortexAPI.GetMeshBoundsCenter(m, out float bx, out float by, out float bz))
                    { cx = bx; cy = by; cz = bz; gotCenter = true; }
                }

                // Neutral studio lighting (the scene rebuilds global light state each frame anyway).
                VortexAPI.ClearAllLights();
                VortexAPI.SetAmbientLightStrength(0.32f);
                VortexAPI.SetDirectionalLightParams(-0.45f, -0.6f, -0.65f, 1f, 0.98f, 0.92f, 3.0f);

                const float fov = 35f;
                float fovHalf = fov * 0.5f * (float)Math.PI / 180f;
                // 0.75 = model fills ~75% of the frame (was 0.58 -> too small). Higher factor -> smaller dist -> bigger.
                float dist = radius / (0.75f * (float)Math.Tan(fovHalf));
                pitch = Math.Max(-1.5f, Math.Min(1.5f, pitch));
                distScale = Math.Max(0.2f, Math.Min(5f, distScale));
                float d = dist * distScale;
                float px = cx + d * (float)(Math.Cos(pitch) * Math.Sin(yaw));
                float py = cy + d * (float)Math.Sin(pitch);
                float pz = cz + d * (float)(Math.Cos(pitch) * Math.Cos(yaw));
                var cam = VortexAPI.ViewportCameraDesc.CreatePerspective(
                    px, py, pz, cx, cy, cz, 0, 1, 0, fov,
                    Math.Max(0.02f, d * 0.01f), d * 4f + 50f);

                float[] idm = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
                for (int i = 0; i < meshIds.Length; i++)
                {
                    long mat = (materialIds != null && i < materialIds.Length && materialIds[i] >= 0)
                        ? materialIds[i] : -1;
                    VortexAPI.SubmitMeshForRendering(meshIds[i], mat, idm);
                }

                // Swap our submitted item into the active queue WITHOUT presenting to the main
                // swapchain (presenting per-render flashed the editor viewport), then render only
                // into the offscreen target.
                VortexAPI.SwapRenderQueue();
                VortexAPI.RenderToSecondaryTarget(rt, cam, false);
                if (!VortexAPI.PrepareSecondaryRenderTargetReadback(rt)) return null;
                return ReadTargetToBitmap(rt);
            }
            catch { return null; }
            // NOTE: the render target is cached + reused (see AcquireTarget) — NOT destroyed per render.
        }

        /// <summary>
        /// Renders a sphere with the given engine material — the canonical material preview.
        /// </summary>
        public static ImageSource RenderMaterialSphere(long materialId, int size)
            => RenderMaterialSphere(materialId, size, 0.74f, 0.62f, 1f);

        public static ImageSource RenderMaterialSphere(long materialId, int size, float yaw, float pitch, float distScale)
        {
            long sphere = VortexAPI.CreateSphereMesh(0.62f);
            if (sphere < 0) return null;
            try { return RenderMeshes(new[] { sphere }, new[] { materialId }, size, yaw, pitch, distScale); }
            finally { try { VortexAPI.DeleteMesh(sphere); } catch { } }
        }

        /// <summary>
        /// Imports a model file and renders all its submeshes with their imported materials.
        /// </summary>
        public static ImageSource RenderModel(string fullPath, int size)
        {
            if (string.IsNullOrEmpty(fullPath) || !System.IO.File.Exists(fullPath)) return null;
            try
            {
                var subs = VortexAPI.ImportModelWithMaterialsFromFile(fullPath);
                if (subs == null || subs.Length == 0) return null;
                var meshes = new long[subs.Length];
                var mats = new long[subs.Length];
                for (int i = 0; i < subs.Length; i++) { meshes[i] = subs[i].MeshId; mats[i] = subs[i].MaterialId; }
                return RenderMeshes(meshes, mats, size);
            }
            catch { return null; }
        }

        private static ImageSource ReadTargetToBitmap(uint rt)
        {
            IntPtr src = VortexAPI.ReadSecondaryRenderTargetPixels(rt, out uint w, out uint h, out uint pitch);
            if (src == IntPtr.Zero || w == 0 || h == 0) { VortexAPI.ReleaseSecondaryRenderTargetPixels(rt); return null; }
            try
            {
                var wb = new WriteableBitmap((int)w, (int)h, 96, 96, PixelFormats.Bgra32, null);
                wb.Lock();
                int copyW = (int)w * 4;
                for (int y = 0; y < (int)h; y++)
                {
                    IntPtr s = IntPtr.Add(src, y * (int)pitch);
                    IntPtr d = IntPtr.Add(wb.BackBuffer, y * wb.BackBufferStride);
                    CopyMemory(d, s, copyW);
                }
                wb.AddDirtyRect(new Int32Rect(0, 0, (int)w, (int)h));
                wb.Unlock();
                wb.Freeze();
                return wb;
            }
            finally { VortexAPI.ReleaseSecondaryRenderTargetPixels(rt); }
        }
    }
}
