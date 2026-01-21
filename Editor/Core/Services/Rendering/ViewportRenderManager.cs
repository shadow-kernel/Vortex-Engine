using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using Editor.ECS;

namespace Editor.Core.Services.Rendering
{
    /// <summary>
    /// Manages multiple secondary viewport renderers.
    /// Provides a centralized API for viewport rendering with proper resource management.
    /// </summary>
    public class ViewportRenderManager
    {
        private static ViewportRenderManager _instance;
        public static ViewportRenderManager Instance => _instance ?? (_instance = new ViewportRenderManager());
        
        /// <summary>
        /// Viewport indices:
        /// 0 = PIP (Camera Preview)
        /// 1-3 = Split view viewports
        /// </summary>
        public const int PipViewportIndex = 0;
        public const int MaxViewports = 4;
        
        private readonly SecondaryViewportRenderer[] _renderers;
        // QUALITY: Higher resolution for sharper previews
        // PIP: 480x270 (16:9, was 320x180)
        // Split: 960x540 (16:9, was 640x480)
        private readonly int[] _defaultWidths = { 480, 960, 960, 960 };
        private readonly int[] _defaultHeights = { 270, 540, 540, 540 };
        
        private ViewportRenderManager()
        {
            _renderers = new SecondaryViewportRenderer[MaxViewports];
            for (int i = 0; i < MaxViewports; i++)
            {
                _renderers[i] = new SecondaryViewportRenderer();
            }
        }
        
        /// <summary>
        /// Initialize a viewport with default or custom dimensions.
        /// </summary>
        public bool InitializeViewport(int index, int? width = null, int? height = null)
        {
            if (index < 0 || index >= MaxViewports)
                return false;
            
            int w = width ?? _defaultWidths[index];
            int h = height ?? _defaultHeights[index];
            
            return _renderers[index].Initialize(w, h);
        }
        
        /// <summary>
        /// Set the camera for a viewport.
        /// </summary>
        public void SetCamera(int index, GameEntity camera)
        {
            if (index < 0 || index >= MaxViewports)
                return;
            
            _renderers[index].Camera = camera;
        }
        
        /// <summary>
        /// Get the camera for a viewport.
        /// </summary>
        public GameEntity GetCamera(int index)
        {
            if (index < 0 || index >= MaxViewports)
                return null;
            
            return _renderers[index].Camera;
        }
        
        /// <summary>
        /// Render a viewport with optional throttling.
        /// </summary>
        public bool Render(int index, int throttleMs = 33)
        {
            if (index < 0 || index >= MaxViewports)
                return false;
            
            if (!_renderers[index].IsReady)
            {
                InitializeViewport(index);
            }
            
            return _renderers[index].Render(throttleMs);
        }
        
        /// <summary>
        /// Force immediate render of a viewport.
        /// </summary>
        public bool ForceRender(int index)
        {
            if (index < 0 || index >= MaxViewports)
                return false;
            
            if (!_renderers[index].IsReady)
            {
                InitializeViewport(index);
            }
            
            return _renderers[index].ForceRender();
        }
        
        /// <summary>
        /// Get the bitmap for a viewport.
        /// </summary>
        public WriteableBitmap GetBitmap(int index)
        {
            if (index < 0 || index >= MaxViewports)
                return null;
            
            return _renderers[index].Bitmap;
        }
        
        /// <summary>
        /// Check if a viewport is ready for rendering.
        /// </summary>
        public bool IsReady(int index)
        {
            if (index < 0 || index >= MaxViewports)
                return false;
            
            return _renderers[index].IsReady;
        }
        
        /// <summary>
        /// Shutdown a specific viewport.
        /// </summary>
        public void ShutdownViewport(int index)
        {
            if (index < 0 || index >= MaxViewports)
                return;
            
            _renderers[index].Shutdown();
        }
        
        /// <summary>
        /// Shutdown all viewports.
        /// </summary>
        public void ShutdownAll()
        {
            for (int i = 0; i < MaxViewports; i++)
            {
                _renderers[i].Shutdown();
            }
        }
    }
}
