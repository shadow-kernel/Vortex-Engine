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
            try { SetRenderLoopVSync(enabled); } catch { }
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

        #region Render Loop (Engine-side timing)

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void StartRenderLoop();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void StopRenderLoop();

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsRenderLoopRunning();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetTargetFPS(int fps);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern int GetTargetFPS();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetRenderLoopVSync([MarshalAs(UnmanagedType.I1)] bool enabled);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsRenderLoopVSyncEnabled();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern float GetDeltaTime();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern float GetTotalTime();

        /// <summary>
        /// Start the engine's render loop on a dedicated thread.
        /// </summary>
        public static void StartEngineRenderLoop()
        {
            try { StartRenderLoop(); } catch { }
        }

        /// <summary>
        /// Stop the engine's render loop.
        /// </summary>
        public static void StopEngineRenderLoop()
        {
            try { StopRenderLoop(); } catch { }
        }

        /// <summary>
        /// Check if the engine's render loop is running.
        /// </summary>
        public static bool IsEngineRenderLoopRunning
        {
            get { try { return IsRenderLoopRunning(); } catch { return false; } }
        }

        /// <summary>
        /// Set the target FPS for the engine's render loop (0 = unlimited).
        /// </summary>
        public static void SetEngineTargetFPS(int fps)
        {
            try { SetTargetFPS(fps); } catch { }
        }

        /// <summary>
        /// Get the current target FPS.
        /// </summary>
        public static int EngineTargetFPS
        {
            get { try { return GetTargetFPS(); } catch { return 0; } }
        }

        /// <summary>
        /// Get delta time from the engine.
        /// </summary>
        public static float DeltaTime
        {
            get { try { return GetDeltaTime(); } catch { return 0.016f; } }
        }

        /// <summary>
        /// Get total elapsed time from the engine.
        /// </summary>
        public static float TotalTime
        {
            get { try { return GetTotalTime(); } catch { return 0f; } }
        }

        #endregion
    }
}
