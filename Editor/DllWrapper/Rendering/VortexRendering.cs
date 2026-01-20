using System.Runtime.InteropServices;

namespace Editor.DllWrapper
{
    /// <summary>
    /// VortexAPI - Rendering functionality.
    /// </summary>
    public static partial class VortexAPI
    {
        #region Viewport

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool InitializeRenderViewport(System.IntPtr hwnd, uint width, uint height);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ResizeRenderViewport(uint width, uint height);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void RenderFrame();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ShutdownRenderViewport();

        public static bool InitRenderViewport(System.IntPtr hwnd, uint width, uint height) 
            => InitializeRenderViewport(hwnd, width, height);
        public static void ResizeRender(uint width, uint height) => ResizeRenderViewport(width, height);
        public static void RenderOnce() => RenderFrame();
        public static void ShutdownRender() => ShutdownRenderViewport();

        #endregion

        #region Camera

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetCamera(float posX, float posY, float posZ,
            float targetX, float targetY, float targetZ,
            float upX, float upY, float upZ);

        public static void SetViewCamera(float posX, float posY, float posZ,
            float targetX = 0, float targetY = 0, float targetZ = 0,
            float upX = 0, float upY = 1, float upZ = 0)
        {
            SetCamera(posX, posY, posZ, targetX, targetY, targetZ, upX, upY, upZ);
        }

        #endregion

        #region Render Item Submission

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SubmitRenderItem(long meshId, long materialId, float[] worldMatrix);

        public static void SubmitMeshForRendering(long meshId, long materialId, float[] worldMatrix = null)
        {
            SubmitRenderItem(meshId, materialId, worldMatrix);
        }

        #endregion

        #region Grid & Gizmos Visibility

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetGridVisible([MarshalAs(UnmanagedType.I1)] bool visible);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetGridSettings(float spacing, float majorLineInterval, float extent);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetGizmosVisible([MarshalAs(UnmanagedType.I1)] bool visible);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetVSync([MarshalAs(UnmanagedType.I1)] bool enabled);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsVSyncEnabled();

        private static bool _gridVisible = true;
        private static bool _gizmosVisible = true;
        private static bool _vsyncEnabled = false;

        public static bool IsGridVisible => _gridVisible;
        public static bool AreGizmosVisible => _gizmosVisible;
        public static bool IsVSyncOn => _vsyncEnabled;

        public static void ShowGrid(bool visible)
        {
            _gridVisible = visible;
            try { SetGridVisible(visible); } catch { }
        }

        public static void ConfigureGrid(float spacing = 1.0f, float majorLineInterval = 10.0f, float extent = 100.0f)
        {
            try { SetGridSettings(spacing, majorLineInterval, extent); } catch { }
        }

        public static void ShowGizmos(bool visible)
        {
            _gizmosVisible = visible;
            try { SetGizmosVisible(visible); } catch { }
        }

        public static void SetVSyncEnabled(bool enabled)
        {
            _vsyncEnabled = enabled;
            try { SetVSync(enabled); } catch { }
        }

        #endregion

        #region Performance Statistics

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int GetCurrentFPS();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int GetDrawCallCount();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int GetVertexCount();

        /// <summary>
        /// Get current FPS from the engine.
        /// </summary>
        public static int CurrentFPS
        {
            get { try { return GetCurrentFPS(); } catch { return 0; } }
        }

        /// <summary>
        /// Get draw call count from the engine.
        /// </summary>
        public static int DrawCalls
        {
            get { try { return GetDrawCallCount(); } catch { return 0; } }
        }

        /// <summary>
        /// Get vertex count from the engine.
        /// </summary>
        public static int VertexCount
        {
            get { try { return GetVertexCount(); } catch { return 0; } }
        }

        #endregion
    }
}
