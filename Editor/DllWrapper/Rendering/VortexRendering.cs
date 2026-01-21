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

        #region Multi-Viewport Rendering

        /// <summary>
        /// Camera parameters for secondary viewport rendering.
        /// Must match the C++ viewport_camera_desc structure layout exactly.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct ViewportCameraDesc
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public float[] Position;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public float[] Target;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public float[] Up;
            public float FovDegrees;
            public float NearClip;
            public float FarClip;
            [MarshalAs(UnmanagedType.I1)]
            public bool Orthographic;
            // Padding to match C++ struct alignment (bool is 1 byte, then 3 bytes padding before float)
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            private byte[] _padding;
            public float OrthoSize;

            public static ViewportCameraDesc CreateOrthographic(
                float posX, float posY, float posZ,
                float targetX, float targetY, float targetZ,
                float upX, float upY, float upZ,
                float orthoSize, float nearClip = 0.1f, float farClip = 1000f)
            {
                return new ViewportCameraDesc
                {
                    Position = new[] { posX, posY, posZ },
                    Target = new[] { targetX, targetY, targetZ },
                    Up = new[] { upX, upY, upZ },
                    FovDegrees = 60f,
                    NearClip = nearClip,
                    FarClip = farClip,
                    Orthographic = true,
                    _padding = new byte[3],
                    OrthoSize = orthoSize
                };
            }

            public static ViewportCameraDesc CreatePerspective(
                float posX, float posY, float posZ,
                float targetX, float targetY, float targetZ,
                float upX, float upY, float upZ,
                float fovDegrees, float nearClip = 0.1f, float farClip = 1000f)
            {
                return new ViewportCameraDesc
                {
                    Position = new[] { posX, posY, posZ },
                    Target = new[] { targetX, targetY, targetZ },
                    Up = new[] { upX, upY, upZ },
                    FovDegrees = fovDegrees,
                    NearClip = nearClip,
                    FarClip = farClip,
                    Orthographic = false,
                    _padding = new byte[3],
                    OrthoSize = 10f
                };
            }
        }

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern uint CreateRenderTarget(uint width, uint height);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void DestroyRenderTarget(uint targetId);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool ResizeRenderTarget(uint targetId, uint width, uint height);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool HasRenderTarget(uint targetId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void RenderToTarget(uint targetId, ref ViewportCameraDesc camera, 
            [MarshalAs(UnmanagedType.I1)] bool renderGrid);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool PrepareRenderTargetReadback(uint targetId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern System.IntPtr ReadRenderTargetPixels(uint targetId, 
            out uint outWidth, out uint outHeight, out uint outRowPitch);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ReleaseRenderTargetPixels(uint targetId);

        /// <summary>
        /// Create an offscreen render target for secondary viewport rendering.
        /// Returns a unique ID (0 on failure).
        /// </summary>
        public static uint CreateSecondaryRenderTarget(uint width, uint height)
        {
            try { return CreateRenderTarget(width, height); } catch { return 0; }
        }

        /// <summary>
        /// Destroy a render target by ID.
        /// </summary>
        public static void DestroySecondaryRenderTarget(uint targetId)
        {
            try { DestroyRenderTarget(targetId); } catch { }
        }

        /// <summary>
        /// Resize a render target.
        /// </summary>
        public static bool ResizeSecondaryRenderTarget(uint targetId, uint width, uint height)
        {
            try { return ResizeRenderTarget(targetId, width, height); } catch { return false; }
        }

        /// <summary>
        /// Check if a render target exists.
        /// </summary>
        public static bool HasSecondaryRenderTarget(uint targetId)
        {
            try { return HasRenderTarget(targetId); } catch { return false; }
        }

        /// <summary>
        /// Render the scene to a secondary render target with a specific camera.
        /// </summary>
        public static void RenderToSecondaryTarget(uint targetId, ViewportCameraDesc camera, bool renderGrid = false)
        {
            try { RenderToTarget(targetId, ref camera, renderGrid); } catch { }
        }

        /// <summary>
        /// Prepare render target data for CPU readback.
        /// Must be called before ReadRenderTargetPixels.
        /// </summary>
        public static bool PrepareSecondaryRenderTargetReadback(uint targetId)
        {
            try { return PrepareRenderTargetReadback(targetId); } catch { return false; }
        }

        /// <summary>
        /// Read pixel data from a render target.
        /// Returns pointer to RGBA8 pixel data. Call ReleaseSecondaryRenderTargetPixels when done.
        /// </summary>
        public static System.IntPtr ReadSecondaryRenderTargetPixels(uint targetId, 
            out uint width, out uint height, out uint rowPitch)
        {
            width = height = rowPitch = 0;
            try { return ReadRenderTargetPixels(targetId, out width, out height, out rowPitch); } 
            catch { return System.IntPtr.Zero; }
        }

        /// <summary>
        /// Release the mapped pixel data.
        /// </summary>
        public static void ReleaseSecondaryRenderTargetPixels(uint targetId)
        {
            try { ReleaseRenderTargetPixels(targetId); } catch { }
        }

        #endregion
    }
}
