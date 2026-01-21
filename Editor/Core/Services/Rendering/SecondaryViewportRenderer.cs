using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Rendering;

namespace Editor.Core.Services.Rendering
{
    /// <summary>
    /// Manages a single viewport's GPU rendering pipeline.
    /// Handles render target creation, camera setup, and bitmap updates.
    /// </summary>
    public class SecondaryViewportRenderer : IDisposable
    {
        private uint _renderTargetId;
        private int _width;
        private int _height;
        private WriteableBitmap _bitmap;
        private bool _isInitialized;
        private GameEntity _camera;
        private DateTime _lastRenderTime = DateTime.MinValue;
        
        /// <summary>
        /// Gets the rendered bitmap for display in WPF.
        /// </summary>
        public WriteableBitmap Bitmap => _bitmap;
        
        /// <summary>
        /// Gets whether the renderer is initialized and ready.
        /// </summary>
        public bool IsReady => _isInitialized && _renderTargetId > 0;
        
        /// <summary>
        /// Gets or sets the camera entity to render from.
        /// </summary>
        public GameEntity Camera
        {
            get => _camera;
            set => _camera = value;
        }
        
        /// <summary>
        /// Initialize the renderer with specified dimensions.
        /// </summary>
        public bool Initialize(int width, int height)
        {
            if (_isInitialized && _width == width && _height == height)
                return true;
            
            Shutdown();
            
            _renderTargetId = VortexAPI.CreateSecondaryRenderTarget((uint)width, (uint)height);
            if (_renderTargetId == 0)
                return false;
            
            _width = width;
            _height = height;
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _isInitialized = true;
            
            return true;
        }
        
        /// <summary>
        /// Shutdown and release all resources.
        /// </summary>
        public void Shutdown()
        {
            if (_renderTargetId > 0)
            {
                VortexAPI.DestroySecondaryRenderTarget(_renderTargetId);
                _renderTargetId = 0;
            }
            
            _bitmap = null;
            _isInitialized = false;
            _camera = null;
        }
        
        /// <summary>
        /// Render the viewport. Returns true if a new frame was rendered.
        /// </summary>
        /// <param name="throttleMs">Minimum milliseconds between renders (0 = no throttle)</param>
        public bool Render(int throttleMs = 0)
        {
            if (!_isInitialized || _camera == null)
                return false;
            
            // Optional throttling
            if (throttleMs > 0)
            {
                var now = DateTime.Now;
                if ((now - _lastRenderTime).TotalMilliseconds < throttleMs)
                    return false;
                _lastRenderTime = now;
            }
            
            var camera = _camera.GetComponent<Camera>();
            var transform = _camera.Transform;
            
            if (camera == null || transform == null)
                return false;
            
            // Create camera description
            var camDesc = CreateCameraDescription(transform, camera);
            
            // Render to GPU target
            VortexAPI.RenderToSecondaryTarget(_renderTargetId, camDesc, renderGrid: false);
            
            // Prepare readback
            if (!VortexAPI.PrepareSecondaryRenderTargetReadback(_renderTargetId))
                return false;
            
            // Copy to bitmap
            CopyToBitmap();
            
            return true;
        }
        
        /// <summary>
        /// Force immediate render (ignores throttling).
        /// </summary>
        public bool ForceRender()
        {
            _lastRenderTime = DateTime.MinValue;
            return Render(0);
        }
        
        private VortexAPI.ViewportCameraDesc CreateCameraDescription(
            ECS.Components.Transform transform, Camera camera)
        {
            var pos = transform.LocalPosition;
            var rot = transform.LocalRotation;
            
            // Calculate forward from Euler angles (degrees)
            float yawRad = rot.Y * (float)Math.PI / 180f;
            float pitchRad = rot.X * (float)Math.PI / 180f;
            
            float forwardX = (float)(Math.Cos(pitchRad) * Math.Sin(yawRad));
            float forwardY = (float)(-Math.Sin(pitchRad));
            float forwardZ = (float)(Math.Cos(pitchRad) * Math.Cos(yawRad));
            
            if (camera.Projection == CameraProjection.Orthographic)
            {
                return VortexAPI.ViewportCameraDesc.CreateOrthographic(
                    pos.X, pos.Y, pos.Z,
                    pos.X + forwardX, pos.Y + forwardY, pos.Z + forwardZ,
                    0, 1, 0,
                    camera.OrthographicSize,
                    camera.NearClip,
                    camera.FarClip);
            }
            
            return VortexAPI.ViewportCameraDesc.CreatePerspective(
                pos.X, pos.Y, pos.Z,
                pos.X + forwardX, pos.Y + forwardY, pos.Z + forwardZ,
                0, 1, 0,
                camera.FieldOfView,
                camera.NearClip,
                camera.FarClip);
        }
        
        private void CopyToBitmap()
        {
            uint outWidth, outHeight, outRowPitch;
            IntPtr pixelData = VortexAPI.ReadSecondaryRenderTargetPixels(
                _renderTargetId, out outWidth, out outHeight, out outRowPitch);
            
            if (pixelData == IntPtr.Zero)
                return;
            
            try
            {
                _bitmap.Lock();
                
                IntPtr destBuffer = _bitmap.BackBuffer;
                int copyWidth = Math.Min(_width, (int)outWidth) * 4;
                int copyHeight = Math.Min(_height, (int)outHeight);
                int srcStride = (int)outRowPitch;
                int dstStride = _bitmap.BackBufferStride;
                
                for (int y = 0; y < copyHeight; y++)
                {
                    IntPtr srcRow = IntPtr.Add(pixelData, y * srcStride);
                    IntPtr dstRow = IntPtr.Add(destBuffer, y * dstStride);
                    CopyMemory(dstRow, srcRow, copyWidth);
                }
                
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            }
            finally
            {
                _bitmap.Unlock();
            }
            
            VortexAPI.ReleaseSecondaryRenderTargetPixels(_renderTargetId);
        }
        
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, int count);
        
        public void Dispose()
        {
            Shutdown();
        }
    }
}
