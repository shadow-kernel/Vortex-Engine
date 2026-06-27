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

        [DllImport(_dllName, CallingConvention = _cc, EntryPoint = "SwapRenderQueue")]
        private static extern void SwapRenderQueueNative();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ShutdownRenderViewport();

        public static bool InitRenderViewport(System.IntPtr hwnd, uint width, uint height) 
            => InitializeRenderViewport(hwnd, width, height);
        public static void ResizeRender(uint width, uint height) => ResizeRenderViewport(width, height);
        public static void RenderOnce() => RenderFrame();
        /// <summary>
        /// Swap the render queue without presenting to the main swapchain — for offscreen
        /// thumbnail/preview rendering so it never flashes the editor viewport.
        /// </summary>
        public static void SwapRenderQueue() => SwapRenderQueueNative();
        public static void ShutdownRender() => ShutdownRenderViewport();

        #endregion

        #region Standalone Game Window (second swapchain)

        [DllImport(_dllName, CallingConvention = _cc, EntryPoint = "CreateGameWindow")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateGameWindowNative(System.IntPtr hwnd, uint width, uint height);

        [DllImport(_dllName, CallingConvention = _cc, EntryPoint = "RenderGameWindow")]
        private static extern void RenderGameWindowNative();

        [DllImport(_dllName, CallingConvention = _cc, EntryPoint = "ResizeGameWindow")]
        private static extern void ResizeGameWindowNative(uint width, uint height);

        [DllImport(_dllName, CallingConvention = _cc, EntryPoint = "DestroyGameWindow")]
        private static extern void DestroyGameWindowNative();

        [DllImport(_dllName, CallingConvention = _cc, EntryPoint = "IsGameWindowActive")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsGameWindowActiveNative();

        /// <summary>Create a second DX12 swapchain on the given window handle (the standalone game window).</summary>
        public static bool CreateGameWindow(System.IntPtr hwnd, uint width, uint height) => CreateGameWindowNative(hwnd, width, height);
        /// <summary>Render the current scene (through the current camera) into the game window, then present it.</summary>
        public static void RenderGameWindow() => RenderGameWindowNative();
        public static void ResizeGameWindow(uint width, uint height) => ResizeGameWindowNative(width, height);
        public static void DestroyGameWindow() => DestroyGameWindowNative();
        public static bool IsGameWindowActive() => IsGameWindowActiveNative();

        #endregion

        #region 2D UI Overlay (Direct2D/DirectWrite over the 3D)

        [DllImport(_dllName, CallingConvention = _cc, EntryPoint = "UIBegin")]
        private static extern void UIBeginNative(float w, float h);

        [DllImport(_dllName, CallingConvention = _cc, EntryPoint = "UIRect")]
        private static extern void UIRectNative(float x, float y, float w, float h, float r, float g, float b, float a, float radius);

        [DllImport(_dllName, CallingConvention = _cc, EntryPoint = "UIText", CharSet = CharSet.Unicode)]
        private static extern void UITextNative(float x, float y, float w, float h,
            [MarshalAs(UnmanagedType.LPWStr)] string text, float size, float r, float g, float b, float a, int align, int weight);

        [DllImport(_dllName, CallingConvention = _cc, EntryPoint = "UILine")]
        private static extern void UILineNative(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thick);

        /// <summary>Start a new UI frame (clears last frame's commands + sets the viewport size).</summary>
        public static void UIBegin(float w, float h) { try { UIBeginNative(w, h); } catch { } }
        public static void UIRect(float x, float y, float w, float h, float r, float g, float b, float a, float radius) { try { UIRectNative(x, y, w, h, r, g, b, a, radius); } catch { } }
        public static void UIText(float x, float y, float w, float h, string text, float size, float r, float g, float b, float a, int align, int weight) { try { UITextNative(x, y, w, h, text ?? "", size, r, g, b, a, align, weight); } catch { } }
        public static void UILine(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thick) { try { UILineNative(x1, y1, x2, y2, r, g, b, a, thick); } catch { } }

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

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetWireframeMode([MarshalAs(UnmanagedType.I1)] bool enabled);

        private static bool _wireframeMode = false;
        public static bool IsWireframeMode => _wireframeMode;

        public static void SetWireframe(bool enabled)
        {
            _wireframeMode = enabled;
            try { SetWireframeMode(enabled); } catch { }
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

        #region Lighting System

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ClearLights();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetDirectionalLight(
            float dirX, float dirY, float dirZ,
            float colorR, float colorG, float colorB,
            float intensity);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AddPointLight(
            float posX, float posY, float posZ,
            float colorR, float colorG, float colorB,
            float intensity, float range);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void AddSpotLight(
            float posX, float posY, float posZ,
            float dirX, float dirY, float dirZ,
            float colorR, float colorG, float colorB,
            float intensity, float range,
            float spotAngle, float innerSpotAngle);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetAmbientStrength(float strength);

        /// <summary>
        /// Clear all dynamic lights. Call at the beginning of each frame before submitting new lights.
        /// </summary>
        public static void ClearAllLights()
        {
            try { ClearLights(); } catch { }
        }

        /// <summary>
        /// Set the primary directional light (sun light).
        /// </summary>
        public static void SetDirectionalLightParams(
            float dirX, float dirY, float dirZ,
            float colorR, float colorG, float colorB,
            float intensity)
        {
            try { SetDirectionalLight(dirX, dirY, dirZ, colorR, colorG, colorB, intensity); } catch { }
        }

        /// <summary>
        /// Add a point light to the scene. Maximum 16 per frame.
        /// </summary>
        public static void SubmitPointLight(
            float posX, float posY, float posZ,
            float colorR, float colorG, float colorB,
            float intensity, float range)
        {
            try { AddPointLight(posX, posY, posZ, colorR, colorG, colorB, intensity, range); } catch { }
        }

        /// <summary>
        /// Add a spot light to the scene. Maximum 8 per frame.
        /// </summary>
        public static void SubmitSpotLight(
            float posX, float posY, float posZ,
            float dirX, float dirY, float dirZ,
            float colorR, float colorG, float colorB,
            float intensity, float range,
            float spotAngle, float innerSpotAngle)
        {
            try 
            { 
                AddSpotLight(posX, posY, posZ, dirX, dirY, dirZ, 
                    colorR, colorG, colorB, intensity, range, spotAngle, innerSpotAngle); 
            } 
            catch { }
        }

        /// <summary>
        /// Set ambient light strength (0.0 - 1.0).
        /// </summary>
        public static void SetAmbientLightStrength(float strength)
        {
            try { SetAmbientStrength(strength); } catch { }
        }

        #endregion

        #region Skybox

        /// <summary>
        /// Skybox rendering modes
        /// </summary>
        public enum SkyboxMode : uint
        {
            SolidColor = 0,
            Gradient = 1,
            Texture = 2
        }

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetSkyboxEnabled([MarshalAs(UnmanagedType.I1)] bool enabled);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsSkyboxEnabled();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetSkyboxMode(uint mode);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern uint GetSkyboxMode();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetSkyboxColors(
            float skyR, float skyG, float skyB,
            float horizonR, float horizonG, float horizonB,
            float groundR, float groundG, float groundB);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetSkyboxSolidColor(float r, float g, float b);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetSkyboxSun(float dirX, float dirY, float dirZ, 
            float colorR, float colorG, float colorB, float intensity);

        private static bool _skyboxEnabled = false;
        public static bool IsSkyboxOn => _skyboxEnabled;

        /// <summary>
        /// Enable or disable the skybox rendering.
        /// </summary>
        public static void EnableSkybox(bool enabled)
        {
            _skyboxEnabled = enabled;
            try { SetSkyboxEnabled(enabled); } catch { }
        }

        /// <summary>
        /// Set the skybox rendering mode.
        /// </summary>
        public static void SetSkyboxRenderMode(SkyboxMode mode)
        {
            try { SetSkyboxMode((uint)mode); } catch { }
        }

        /// <summary>
        /// Set skybox gradient colors (linear RGB).
        /// </summary>
        public static void SetSkyboxGradient(
            float skyR, float skyG, float skyB,
            float horizonR, float horizonG, float horizonB,
            float groundR, float groundG, float groundB)
        {
            try 
            { 
                SetSkyboxColors(skyR, skyG, skyB, horizonR, horizonG, horizonB, groundR, groundG, groundB); 
            } 
            catch { }
        }

        /// <summary>
        /// Set skybox to a single solid color (linear RGB).
        /// </summary>
        public static void SetSkyboxColor(float r, float g, float b)
        {
            try { SetSkyboxSolidColor(r, g, b); } catch { }
        }

        /// <summary>
        /// Configure the sun for skybox rendering.
        /// </summary>
        public static void ConfigureSkyboxSun(float dirX, float dirY, float dirZ, 
            float colorR, float colorG, float colorB, float intensity)
        {
            try { SetSkyboxSun(dirX, dirY, dirZ, colorR, colorG, colorB, intensity); } catch { }
        }

        #endregion

        #region Runtime Skybox Component

        /// <summary>
        /// Descriptor for creating a runtime skybox component.
        /// Must match the C++ skybox_descriptor structure layout exactly.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct SkyboxDescriptor
        {
            public byte Mode; // 0 = solid, 1 = gradient, 2 = cubemap
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public float[] SkyColor;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public float[] HorizonColor;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public float[] GroundColor;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public float[] SunDirection;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public float[] SunColor;
            public float SunIntensity;
            public float AmbientIntensity;
            public float Exposure;
            [MarshalAs(UnmanagedType.I1)]
            public bool IsEnabled;

            public static SkyboxDescriptor CreateGradient(
                float skyR, float skyG, float skyB,
                float horizonR, float horizonG, float horizonB,
                float groundR, float groundG, float groundB,
                float exposure = 1.0f)
            {
                return new SkyboxDescriptor
                {
                    Mode = 1, // Gradient
                    SkyColor = new[] { skyR, skyG, skyB },
                    HorizonColor = new[] { horizonR, horizonG, horizonB },
                    GroundColor = new[] { groundR, groundG, groundB },
                    SunDirection = new[] { -0.5f, -0.7f, 0.5f },
                    SunColor = new[] { 1.0f, 0.95f, 0.8f },
                    SunIntensity = 1.0f,
                    AmbientIntensity = 0.3f,
                    Exposure = exposure,
                    IsEnabled = true
                };
            }

            public static SkyboxDescriptor CreateSolidColor(float r, float g, float b, float exposure = 1.0f)
            {
                return new SkyboxDescriptor
                {
                    Mode = 0, // Solid color
                    SkyColor = new[] { r, g, b },
                    HorizonColor = new[] { r, g, b },
                    GroundColor = new[] { r, g, b },
                    SunDirection = new[] { -0.5f, -0.7f, 0.5f },
                    SunColor = new[] { 1.0f, 0.95f, 0.8f },
                    SunIntensity = 0.0f, // No sun for solid color
                    AmbientIntensity = 0.3f,
                    Exposure = exposure,
                    IsEnabled = true
                };
            }
        }

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreateSkyboxComponent(ref SkyboxDescriptor desc);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void RemoveSkyboxComponent(long skyboxId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ApplySkyboxToRenderer(long skyboxId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ApplyActiveSkybox();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetActiveSkyboxComponent(long skyboxId);

        /// <summary>
        /// Create a runtime skybox component (for game builds without editor).
        /// </summary>
        public static long CreateRuntimeSkybox(SkyboxDescriptor desc)
        {
            try { return CreateSkyboxComponent(ref desc); } catch { return -1; }
        }

        /// <summary>
        /// Remove a runtime skybox component.
        /// </summary>
        public static void RemoveRuntimeSkybox(long skyboxId)
        {
            try { RemoveSkyboxComponent(skyboxId); } catch { }
        }

        /// <summary>
        /// Apply a specific skybox to the renderer.
        /// </summary>
        public static void ApplyRuntimeSkybox(long skyboxId)
        {
            try { ApplySkyboxToRenderer(skyboxId); } catch { }
        }

        /// <summary>
        /// Apply the currently active skybox to the renderer.
        /// </summary>
        public static void ApplyActiveRuntimeSkybox()
        {
            try { ApplyActiveSkybox(); } catch { }
        }

        /// <summary>
        /// Set which skybox is the active one.
        /// </summary>
        public static void SetActiveRuntimeSkybox(long skyboxId)
        {
            try { SetActiveSkyboxComponent(skyboxId); } catch { }
        }

        #endregion
    }
}
