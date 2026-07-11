using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace Editor.Core.Services
{
    /// <summary>
    /// Manages multiple GPU-rendered viewports for the editor.
    /// Supports up to 5 simultaneous viewports (1 main + 4 secondary).
    /// Each viewport can render from a different camera perspective.
    /// OPTIMIZED: Uses proper throttling and lower resolution for performance.
    /// </summary>
    public class MultiViewportRenderService
    {
        private static MultiViewportRenderService _instance;
        public static MultiViewportRenderService Instance => _instance ?? (_instance = new MultiViewportRenderService());

        // Maximum viewports supported (PIP + 4 split viewports)
        public const int MaxSecondaryViewports = 5;
        
        // Viewport render targets
        private readonly ViewportRenderContext[] _viewports = new ViewportRenderContext[MaxSecondaryViewports];
        
        // Global throttling - reduced to 33ms (~30 FPS) for smooth updates
        private int _currentViewportToRender = 0;
        private DateTime _lastRenderTime = DateTime.MinValue;
        private const int RenderIntervalMs = 33; // ~30 FPS per viewport for smooth performance
        
        private MultiViewportRenderService()
        {
            for (int i = 0; i < MaxSecondaryViewports; i++)
            {
                _viewports[i] = new ViewportRenderContext { Index = i };
            }
        }
        
        /// <summary>
        /// Initialize a viewport for rendering with OPTIMIZED resolution.
        /// </summary>
        public bool InitializeViewport(int viewportIndex, int width, int height)
        {
            if (viewportIndex < 0 || viewportIndex >= MaxSecondaryViewports)
                return false;
            
            var viewport = _viewports[viewportIndex];
            
            // Destroy existing render target if any
            if (viewport.RenderTargetId > 0)
            {
                VortexAPI.DestroySecondaryRenderTarget(viewport.RenderTargetId);
            }
            
            // QUALITY: Higher resolution for sharper previews
            // PIP: 480x270, Split viewports: 960x540 (16:9 aspect ratio)
            int optWidth = viewportIndex == 0 ? 480 : Math.Min(width, 960);
            int optHeight = viewportIndex == 0 ? 270 : Math.Min(height, 540);
            
            // Create new render target
            viewport.RenderTargetId = VortexAPI.CreateSecondaryRenderTarget((uint)optWidth, (uint)optHeight);
            viewport.Width = optWidth;
            viewport.Height = optHeight;
            viewport.IsInitialized = viewport.RenderTargetId > 0;
            
            // Create WriteableBitmap for WPF display
            if (viewport.IsInitialized)
            {
                viewport.Bitmap = new WriteableBitmap(optWidth, optHeight, 96, 96, PixelFormats.Bgra32, null);
            }
            
            return viewport.IsInitialized;
        }
        
        /// <summary>
        /// Shutdown a viewport and release resources.
        /// </summary>
        public void ShutdownViewport(int viewportIndex)
        {
            if (viewportIndex < 0 || viewportIndex >= MaxSecondaryViewports)
                return;
            
            var viewport = _viewports[viewportIndex];
            
            if (viewport.RenderTargetId > 0)
            {
                VortexAPI.DestroySecondaryRenderTarget(viewport.RenderTargetId);
                viewport.RenderTargetId = 0;
            }
            
            viewport.Bitmap = null;
            viewport.IsInitialized = false;
            viewport.CameraEntity = null;
            viewport.UseEditorCamera = false;
        }
        
        /// <summary>
        /// Set the camera entity for a viewport to render from.
        /// </summary>
        public void SetViewportCamera(int viewportIndex, GameEntity cameraEntity)
        {
            if (viewportIndex < 0 || viewportIndex >= MaxSecondaryViewports)
                return;

            var viewport = _viewports[viewportIndex];
            bool cameraChanged = viewport.CameraEntity != cameraEntity;
            viewport.CameraEntity = cameraEntity;
            if (cameraEntity != null) viewport.UseEditorCamera = false;
        }

        /// <summary>
        /// Let a pane follow the editor FREECAM live instead of a scene camera entity — enables the
        /// splitscreen combo "main viewport = FP Preview (In-Game), side pane = free world view".
        /// </summary>
        public void SetViewportEditorCamera(int viewportIndex, bool useEditorCamera)
        {
            if (viewportIndex < 0 || viewportIndex >= MaxSecondaryViewports)
                return;

            var viewport = _viewports[viewportIndex];
            viewport.UseEditorCamera = useEditorCamera;
            if (useEditorCamera) viewport.CameraEntity = null;
        }
        
        /// <summary>
        /// Get the WriteableBitmap for a viewport (for WPF Image binding).
        /// </summary>
        public WriteableBitmap GetViewportBitmap(int viewportIndex)
        {
            if (viewportIndex < 0 || viewportIndex >= MaxSecondaryViewports)
                return null;
            
            return _viewports[viewportIndex].Bitmap;
        }
        
        /// <summary>
        /// Check if a viewport is initialized and ready for rendering.
        /// </summary>
        public bool IsViewportReady(int viewportIndex)
        {
            if (viewportIndex < 0 || viewportIndex >= MaxSecondaryViewports)
                return false;
            
            return _viewports[viewportIndex].IsInitialized;
        }
        
        /// <summary>
        /// Render a viewport. Uses throttling for performance.
        /// </summary>
        public void RenderViewport(int viewportIndex)
        {
            if (viewportIndex < 0 || viewportIndex >= MaxSecondaryViewports)
                return;
            
            var viewport = _viewports[viewportIndex];

            if (!viewport.IsInitialized || viewport.RenderTargetId == 0)
                return;
            if (!viewport.UseEditorCamera && viewport.CameraEntity == null)
                return;

            // Throttle per-viewport (each viewport renders at ~30 FPS)
            var now = DateTime.Now;
            if ((now - viewport.LastRenderTime).TotalMilliseconds < RenderIntervalMs)
                return;

            viewport.LastRenderTime = now;

            VortexAPI.ViewportCameraDesc camDesc;
            if (viewport.UseEditorCamera)
            {
                // Follow the editor freecam live (degrees; same forward math as EditorCameraController).
                var ec = EditorCameraController.Instance;
                float yawRad = ec.Yaw * (float)Math.PI / 180f;
                float pitchRad = ec.Pitch * (float)Math.PI / 180f;
                float fx = (float)(Math.Sin(yawRad) * Math.Cos(pitchRad));
                float fy = (float)(-Math.Sin(pitchRad));
                float fz = (float)(Math.Cos(yawRad) * Math.Cos(pitchRad));
                camDesc = VortexAPI.ViewportCameraDesc.CreatePerspective(
                    ec.PositionX, ec.PositionY, ec.PositionZ,
                    ec.PositionX + fx, ec.PositionY + fy, ec.PositionZ + fz,
                    0, 1, 0,
                    RaycastService.EditorFovYDegrees, 0.1f, 1000f);
            }
            else
            {
                var cameraEntity = viewport.CameraEntity;
                var camera = cameraEntity.GetComponent<Camera>();
                var transform = cameraEntity.Transform;

                if (camera == null || transform == null)
                    return;

                // Create camera description for engine
                camDesc = CreateCameraDescription(cameraEntity, camera, transform);
            }
            
            // Render to the GPU render target (no grid for camera views)
            VortexAPI.RenderToSecondaryTarget(viewport.RenderTargetId, camDesc, renderGrid: false);
            
            // Verify target still exists before readback
            if (!VortexAPI.HasSecondaryRenderTarget(viewport.RenderTargetId))
                return;
            
            // Prepare for CPU readback
            if (!VortexAPI.PrepareSecondaryRenderTargetReadback(viewport.RenderTargetId))
                return;
            
            // Read pixels and copy to bitmap
            CopyRenderTargetToBitmap(viewport);
        }
        
        /// <summary>
        /// Force immediate render of a viewport (bypasses throttling).
        /// Use sparingly - only for initial display.
        /// </summary>
        public void ForceRenderViewport(int viewportIndex)
        {
            if (viewportIndex < 0 || viewportIndex >= MaxSecondaryViewports)
                return;
            
            var viewport = _viewports[viewportIndex];
            viewport.LastRenderTime = DateTime.MinValue; // Reset throttle
            RenderViewport(viewportIndex);
        }
        
        /// <summary>
        /// Create a VortexAPI.ViewportCameraDesc from entity components.
        /// </summary>
        private VortexAPI.ViewportCameraDesc CreateCameraDescription(GameEntity entity, Camera camera, ECS.Components.Transform transform)
        {
            var pos = transform.LocalPosition;
            var rot = transform.LocalRotation;
            
            // Calculate forward direction from Euler angles (in degrees)
            float yawRad = rot.Y * (float)Math.PI / 180f;
            float pitchRad = rot.X * (float)Math.PI / 180f;
            
            // Forward vector (looking down -Z in local space, rotated by yaw and pitch)
            float forwardX = (float)(Math.Cos(pitchRad) * Math.Sin(yawRad));
            float forwardY = (float)(-Math.Sin(pitchRad));
            float forwardZ = (float)(Math.Cos(pitchRad) * Math.Cos(yawRad));
            
            // Target is position + forward
            float targetX = pos.X + forwardX;
            float targetY = pos.Y + forwardY;
            float targetZ = pos.Z + forwardZ;
            
            if (camera.Projection == CameraProjection.Orthographic)
            {
                return VortexAPI.ViewportCameraDesc.CreateOrthographic(
                    pos.X, pos.Y, pos.Z,
                    targetX, targetY, targetZ,
                    0, 1, 0,
                    camera.OrthographicSize,
                    camera.NearClip,
                    camera.FarClip);
            }
            else
            {
                return VortexAPI.ViewportCameraDesc.CreatePerspective(
                    pos.X, pos.Y, pos.Z,
                    targetX, targetY, targetZ,
                    0, 1, 0,
                    camera.FieldOfView,
                    camera.NearClip,
                    camera.FarClip);
            }
        }
        
        /// <summary>
        /// Copy pixels from GPU render target to WriteableBitmap.
        /// </summary>
        private void CopyRenderTargetToBitmap(ViewportRenderContext viewport)
        {
            if (viewport.Bitmap == null || viewport.RenderTargetId == 0)
                return;
            
            try
            {
                uint outWidth, outHeight, outRowPitch;
                IntPtr pixelData = VortexAPI.ReadSecondaryRenderTargetPixels(
                    viewport.RenderTargetId, out outWidth, out outHeight, out outRowPitch);
                
                if (pixelData == IntPtr.Zero)
                    return;
                
                
                int width = viewport.Bitmap.PixelWidth;
                int height = viewport.Bitmap.PixelHeight;
                int dstStride = viewport.Bitmap.BackBufferStride;
                int srcStride = (int)outRowPitch;
                
                viewport.Bitmap.Lock();
                try
                {
                    IntPtr destBuffer = viewport.Bitmap.BackBuffer;
                    int copyWidth = Math.Min(width, (int)outWidth) * 4; // BGRA = 4 bytes per pixel
                    int copyHeight = Math.Min(height, (int)outHeight);
                    
                    // Copy row by row using Marshal
                    for (int y = 0; y < copyHeight; y++)
                    {
                        IntPtr srcRow = IntPtr.Add(pixelData, y * srcStride);
                        IntPtr dstRow = IntPtr.Add(destBuffer, y * dstStride);
                        
                        // Copy bytes using native API
                        CopyMemory(dstRow, srcRow, copyWidth);
                    }
                    
                    viewport.Bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                }
                finally
                {
                    viewport.Bitmap.Unlock();
                }
                
                VortexAPI.ReleaseSecondaryRenderTargetPixels(viewport.RenderTargetId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to copy render target: {ex.Message}");
            }
        }
        
        // Native memory copy for performance
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, int count);
        
        
        /// <summary>
        /// Resize a viewport's render target.
        /// </summary>
        public bool ResizeViewport(int viewportIndex, int newWidth, int newHeight)
        {
            if (viewportIndex < 0 || viewportIndex >= MaxSecondaryViewports)
                return false;
            
            var viewport = _viewports[viewportIndex];
            
            if (!viewport.IsInitialized)
                return InitializeViewport(viewportIndex, newWidth, newHeight);
            
            // Resize the GPU render target
            bool resized = VortexAPI.ResizeSecondaryRenderTarget(viewport.RenderTargetId, (uint)newWidth, (uint)newHeight);
            
            if (resized)
            {
                viewport.Width = newWidth;
                viewport.Height = newHeight;
                viewport.Bitmap = new WriteableBitmap(newWidth, newHeight, 96, 96, PixelFormats.Bgra32, null);
            }
            
            return resized;
        }
        
        /// <summary>
        /// Shutdown all viewports and release resources.
        /// </summary>
        public void ShutdownAll()
        {
            for (int i = 0; i < MaxSecondaryViewports; i++)
            {
                ShutdownViewport(i);
            }
        }
        
        /// <summary>
        /// Internal viewport context storing render state.
        /// </summary>
        private class ViewportRenderContext
        {
            public int Index { get; set; }
            public uint RenderTargetId { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public bool IsInitialized { get; set; }
            public WriteableBitmap Bitmap { get; set; }
            public GameEntity CameraEntity { get; set; }
            /// <summary>Render from the editor freecam (live) instead of a scene camera entity.</summary>
            public bool UseEditorCamera { get; set; }
            public DateTime LastRenderTime { get; set; } = DateTime.MinValue;
        }
    }
}
