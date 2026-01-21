using System;
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
    /// High-performance viewport renderer for secondary camera views.
    /// Optimized for minimal GPU readback overhead.
    /// </summary>
    public class ViewportRenderer : IDisposable
    {
        private uint _renderTargetId;
        private int _width;
        private int _height;
        private WriteableBitmap _frontBuffer;
        private WriteableBitmap _backBuffer;
        private bool _isInitialized;
        private bool _needsSwap;
        
        private GameEntity _cameraEntity;
        private DateTime _lastRenderTime = DateTime.MinValue;
        private ECS.Vector3 _lastCameraPosition;
        private ECS.Vector3 _lastCameraRotation;
        private bool _needsFirstRender = true;
        
        /// <summary>
        /// The render interval in milliseconds (default: 100ms = 10 FPS).
        /// </summary>
        public int RenderIntervalMs { get; set; } = 100;
        
        /// <summary>
        /// Gets the front buffer bitmap for display.
        /// </summary>
        public WriteableBitmap Bitmap => _frontBuffer;
        
        /// <summary>
        /// Gets whether the renderer is initialized.
        /// </summary>
        public bool IsInitialized => _isInitialized;
        
        /// <summary>
        /// Gets or sets the camera entity to render from.
        /// </summary>
        public GameEntity CameraEntity
        {
            get => _cameraEntity;
            set
            {
                if (_cameraEntity != value)
                {
                    _cameraEntity = value;
                    _needsFirstRender = true;
                    ResetCameraTracking();
                }
            }
        }
        
        /// <summary>
        /// Initialize the renderer with the specified dimensions.
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
            
            // Double-buffering for smoother updates
            _frontBuffer = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _backBuffer = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            
            _isInitialized = true;
            _needsFirstRender = true;
            
            return true;
        }
        
        /// <summary>
        /// Shutdown the renderer and release resources.
        /// </summary>
        public void Shutdown()
        {
            if (_renderTargetId > 0)
            {
                VortexAPI.DestroySecondaryRenderTarget(_renderTargetId);
                _renderTargetId = 0;
            }
            
            _frontBuffer = null;
            _backBuffer = null;
            _isInitialized = false;
            _cameraEntity = null;
        }
        
        /// <summary>
        /// Render the viewport. Returns true if a new frame was rendered.
        /// Uses internal throttling and dirty checking.
        /// </summary>
        public bool Render()
        {
            if (!_isInitialized || _cameraEntity == null)
                return false;
            
            // Skip if camera hasn't moved and we've already rendered once
            if (!_needsFirstRender && !HasCameraMoved())
                return false;
            
            // Throttle render rate
            var now = DateTime.Now;
            if (!_needsFirstRender && (now - _lastRenderTime).TotalMilliseconds < RenderIntervalMs)
                return false;
            
            _lastRenderTime = now;
            _needsFirstRender = false;
            
            // Perform the actual render
            return RenderInternal();
        }
        
        /// <summary>
        /// Force an immediate render, bypassing all throttling.
        /// </summary>
        public bool ForceRender()
        {
            if (!_isInitialized || _cameraEntity == null)
                return false;
            
            _lastRenderTime = DateTime.Now;
            _needsFirstRender = false;
            
            return RenderInternal();
        }
        
        /// <summary>
        /// Internal render implementation.
        /// </summary>
        private bool RenderInternal()
        {
            var camera = _cameraEntity.GetComponent<Camera>();
            var transform = _cameraEntity.Transform;
            
            if (camera == null || transform == null)
                return false;
            
            // Update tracking
            _lastCameraPosition = transform.LocalPosition;
            _lastCameraRotation = transform.LocalRotation;
            
            // Create camera description
            var camDesc = CreateCameraDescription(transform, camera);
            
            // Render to GPU target (no grid for camera views)
            VortexAPI.RenderToSecondaryTarget(_renderTargetId, camDesc, renderGrid: false);
            
            // Prepare readback
            if (!VortexAPI.PrepareSecondaryRenderTargetReadback(_renderTargetId))
                return false;
            
            // Copy to back buffer
            CopyToBackBuffer();
            
            // Swap buffers
            SwapBuffers();
            
            return true;
        }
        
        /// <summary>
        /// Copy GPU data to back buffer using optimized memory copy.
        /// </summary>
        private void CopyToBackBuffer()
        {
            uint outWidth, outHeight, outRowPitch;
            IntPtr pixelData = VortexAPI.ReadSecondaryRenderTargetPixels(
                _renderTargetId, out outWidth, out outHeight, out outRowPitch);
            
            if (pixelData == IntPtr.Zero)
                return;
            
            try
            {
                _backBuffer.Lock();
                
                IntPtr destBuffer = _backBuffer.BackBuffer;
                int copyWidth = Math.Min(_width, (int)outWidth) * 4;
                int copyHeight = Math.Min(_height, (int)outHeight);
                int srcStride = (int)outRowPitch;
                int dstStride = _backBuffer.BackBufferStride;
                
                // Fast bulk copy using native memcpy
                for (int y = 0; y < copyHeight; y++)
                {
                    IntPtr srcRow = IntPtr.Add(pixelData, y * srcStride);
                    IntPtr dstRow = IntPtr.Add(destBuffer, y * dstStride);
                    CopyMemory(dstRow, srcRow, copyWidth);
                }
                
                _backBuffer.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            }
            finally
            {
                _backBuffer.Unlock();
            }
            
            VortexAPI.ReleaseSecondaryRenderTargetPixels(_renderTargetId);
        }
        
        /// <summary>
        /// Swap front and back buffers.
        /// </summary>
        private void SwapBuffers()
        {
            var temp = _frontBuffer;
            _frontBuffer = _backBuffer;
            _backBuffer = temp;
        }
        
        /// <summary>
        /// Check if the camera has moved since last render.
        /// </summary>
        private bool HasCameraMoved()
        {
            if (_cameraEntity?.Transform == null)
                return false;
            
            var pos = _cameraEntity.Transform.LocalPosition;
            var rot = _cameraEntity.Transform.LocalRotation;
            
            const float threshold = 0.001f;
            
            return Math.Abs(pos.X - _lastCameraPosition.X) > threshold ||
                   Math.Abs(pos.Y - _lastCameraPosition.Y) > threshold ||
                   Math.Abs(pos.Z - _lastCameraPosition.Z) > threshold ||
                   Math.Abs(rot.X - _lastCameraRotation.X) > threshold ||
                   Math.Abs(rot.Y - _lastCameraRotation.Y) > threshold ||
                   Math.Abs(rot.Z - _lastCameraRotation.Z) > threshold;
        }
        
        /// <summary>
        /// Reset camera position tracking.
        /// </summary>
        private void ResetCameraTracking()
        {
            _lastCameraPosition = new ECS.Vector3(float.NaN, float.NaN, float.NaN);
            _lastCameraRotation = new ECS.Vector3(float.NaN, float.NaN, float.NaN);
        }
        
        /// <summary>
        /// Create camera description for the engine.
        /// </summary>
        private VortexAPI.ViewportCameraDesc CreateCameraDescription(
            ECS.Components.Transform transform, Camera camera)
        {
            var pos = transform.LocalPosition;
            var rot = transform.LocalRotation;
            
            // Calculate forward from Euler angles
            float yawRad = rot.Y * (float)Math.PI / 180f;
            float pitchRad = rot.X * (float)Math.PI / 180f;
            
            float forwardX = (float)(Math.Cos(pitchRad) * Math.Sin(yawRad));
            float forwardY = (float)(-Math.Sin(pitchRad));
            float forwardZ = (float)(Math.Cos(pitchRad) * Math.Cos(yawRad));
            
            return VortexAPI.ViewportCameraDesc.CreatePerspective(
                pos.X, pos.Y, pos.Z,
                pos.X + forwardX, pos.Y + forwardY, pos.Z + forwardZ,
                0, 1, 0,
                camera.FieldOfView,
                camera.NearClip,
                camera.FarClip);
        }
        
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, int count);
        
        public void Dispose()
        {
            Shutdown();
        }
    }
}
