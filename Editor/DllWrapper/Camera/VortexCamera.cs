using System;
using System.Runtime.InteropServices;

namespace Editor.DllWrapper
{
    /// <summary>
    /// Camera projection type.
    /// </summary>
    public enum CameraProjectionType : byte
    {
        Perspective = 0,
        Orthographic = 1
    }

    /// <summary>
    /// Camera clear flags - what to clear before rendering.
    /// </summary>
    public enum CameraClearFlagsType : byte
    {
        Skybox = 0,
        SolidColor = 1,
        DepthOnly = 2,
        Nothing = 3
    }

    /// <summary>
    /// Camera type - determines priority and behavior.
    /// </summary>
    public enum CameraTypeEnum : byte
    {
        /// <summary>Regular game camera</summary>
        GameCamera = 0,
        /// <summary>The primary player camera (shown in purple in editor)</summary>
        MainCamera = 1,
        /// <summary>Editor-only camera (not included in game builds)</summary>
        EditorCamera = 2
    }

    /// <summary>
    /// Descriptor for creating cameras in the engine.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CameraDescriptor
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public float[] Position;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public float[] Rotation;  // Quaternion (x, y, z, w)

        public byte Projection;
        public float FieldOfView;
        public float OrthographicSize;
        public float NearClip;
        public float FarClip;
        public float AspectRatio;
        public byte ClearFlags;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public float[] BackgroundColor;

        public int Depth;
        public int CullingMask;
        public byte CameraType;

        [MarshalAs(UnmanagedType.I1)]
        public bool IsEnabled;

        /// <summary>
        /// Create a default camera descriptor.
        /// </summary>
        public static CameraDescriptor Default => new CameraDescriptor
        {
            Position = new float[] { 0, 0, -10 },
            Rotation = new float[] { 0, 0, 0, 1 },
            Projection = (byte)CameraProjectionType.Perspective,
            FieldOfView = 60f,
            OrthographicSize = 5f,
            NearClip = 0.1f,
            FarClip = 1000f,
            AspectRatio = 16f / 9f,
            ClearFlags = (byte)CameraClearFlagsType.Skybox,
            BackgroundColor = new float[] { 0.1f, 0.1f, 0.2f, 1f },
            Depth = 0,
            CullingMask = -1,
            CameraType = (byte)CameraTypeEnum.GameCamera,
            IsEnabled = true
        };

        /// <summary>
        /// Create a main camera descriptor.
        /// </summary>
        public static CameraDescriptor MainCameraDefault => new CameraDescriptor
        {
            Position = new float[] { 0, 1, -10 },
            Rotation = new float[] { 0, 0, 0, 1 },
            Projection = (byte)CameraProjectionType.Perspective,
            FieldOfView = 60f,
            OrthographicSize = 5f,
            NearClip = 0.1f,
            FarClip = 1000f,
            AspectRatio = 16f / 9f,
            ClearFlags = (byte)CameraClearFlagsType.Skybox,
            BackgroundColor = new float[] { 0.1f, 0.1f, 0.2f, 1f },
            Depth = -1,  // Main camera renders first
            CullingMask = -1,
            CameraType = (byte)CameraTypeEnum.MainCamera,
            IsEnabled = true
        };
    }

    /// <summary>
    /// Handle for an engine camera.
    /// </summary>
    public struct CameraHandle
    {
        public long Id;
        public bool IsValid => Id != -1 && Id != 0;

        public static CameraHandle Invalid => new CameraHandle { Id = -1 };
    }

    /// <summary>
    /// VortexAPI - Camera System functionality.
    /// Provides engine-level camera management.
    /// </summary>
    public static partial class VortexAPI
    {
        #region Camera Creation/Destruction

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long CreateCamera(ref CameraDescriptor descriptor);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void RemoveCamera(long cameraId);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsCameraAlive(long cameraId);

        /// <summary>
        /// Create a new camera in the engine.
        /// </summary>
        public static CameraHandle CreateEngineCamera(CameraDescriptor descriptor)
        {
            var id = CreateCamera(ref descriptor);
            return new CameraHandle { Id = id };
        }

        /// <summary>
        /// Create a new camera with default settings.
        /// </summary>
        public static CameraHandle CreateEngineCamera()
        {
            var desc = CameraDescriptor.Default;
            return CreateEngineCamera(desc);
        }

        /// <summary>
        /// Create a main camera.
        /// </summary>
        public static CameraHandle CreateMainCamera()
        {
            var desc = CameraDescriptor.MainCameraDefault;
            return CreateEngineCamera(desc);
        }

        /// <summary>
        /// Remove a camera from the engine.
        /// </summary>
        public static void DestroyEngineCamera(CameraHandle handle)
        {
            if (handle.IsValid)
                RemoveCamera(handle.Id);
        }

        /// <summary>
        /// Check if a camera is still valid.
        /// </summary>
        public static bool IsEngineCameraValid(CameraHandle handle)
            => handle.IsValid && IsCameraAlive(handle.Id);

        #endregion

        #region Camera Queries

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long GetMainCamera();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern long GetActiveCamera();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetActiveCamera(long cameraId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern uint GetCameraCount();

        /// <summary>
        /// Get the main camera (first camera with MainCamera type).
        /// </summary>
        public static CameraHandle GetEngineMainCamera()
            => new CameraHandle { Id = GetMainCamera() };

        /// <summary>
        /// Get the currently active camera.
        /// </summary>
        public static CameraHandle GetEngineActiveCamera()
            => new CameraHandle { Id = GetActiveCamera() };

        /// <summary>
        /// Set the active camera for rendering.
        /// </summary>
        public static void SetEngineActiveCamera(CameraHandle handle)
        {
            if (handle.IsValid)
                SetActiveCamera(handle.Id);
        }

        /// <summary>
        /// Get the number of cameras in the engine.
        /// </summary>
        public static int GetEngineCameraCount()
            => (int)GetCameraCount();

        #endregion

        #region Camera Transform

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetCameraPosition(long cameraId, float x, float y, float z);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void GetCameraPosition(long cameraId, out float x, out float y, out float z);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetCameraRotation(long cameraId, float x, float y, float z, float w);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void GetCameraRotation(long cameraId, out float x, out float y, out float z, out float w);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void GetCameraForward(long cameraId, out float x, out float y, out float z);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void GetCameraRight(long cameraId, out float x, out float y, out float z);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void GetCameraUp(long cameraId, out float x, out float y, out float z);

        /// <summary>
        /// Set camera position.
        /// </summary>
        public static void SetEngineCameraPosition(CameraHandle handle, float x, float y, float z)
        {
            if (handle.IsValid)
                SetCameraPosition(handle.Id, x, y, z);
        }

        /// <summary>
        /// Get camera position.
        /// </summary>
        public static (float X, float Y, float Z) GetEngineCameraPosition(CameraHandle handle)
        {
            if (!handle.IsValid) return (0, 0, 0);
            GetCameraPosition(handle.Id, out float x, out float y, out float z);
            return (x, y, z);
        }

        /// <summary>
        /// Set camera rotation (quaternion).
        /// </summary>
        public static void SetEngineCameraRotation(CameraHandle handle, float x, float y, float z, float w)
        {
            if (handle.IsValid)
                SetCameraRotation(handle.Id, x, y, z, w);
        }

        /// <summary>
        /// Get camera rotation (quaternion).
        /// </summary>
        public static (float X, float Y, float Z, float W) GetEngineCameraRotation(CameraHandle handle)
        {
            if (!handle.IsValid) return (0, 0, 0, 1);
            GetCameraRotation(handle.Id, out float x, out float y, out float z, out float w);
            return (x, y, z, w);
        }

        /// <summary>
        /// Get camera forward direction.
        /// </summary>
        public static (float X, float Y, float Z) GetEngineCameraForward(CameraHandle handle)
        {
            if (!handle.IsValid) return (0, 0, 1);
            GetCameraForward(handle.Id, out float x, out float y, out float z);
            return (x, y, z);
        }

        /// <summary>
        /// Get camera right direction.
        /// </summary>
        public static (float X, float Y, float Z) GetEngineCameraRight(CameraHandle handle)
        {
            if (!handle.IsValid) return (1, 0, 0);
            GetCameraRight(handle.Id, out float x, out float y, out float z);
            return (x, y, z);
        }

        /// <summary>
        /// Get camera up direction.
        /// </summary>
        public static (float X, float Y, float Z) GetEngineCameraUp(CameraHandle handle)
        {
            if (!handle.IsValid) return (0, 1, 0);
            GetCameraUp(handle.Id, out float x, out float y, out float z);
            return (x, y, z);
        }

        #endregion

        #region Camera Properties

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetCameraFOV(long cameraId, float fov);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern float GetCameraFOV(long cameraId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetCameraClipPlanes(long cameraId, float nearClip, float farClip);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetCameraProjection(long cameraId, byte projection);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetCameraType(long cameraId, byte type);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern byte GetCameraType(long cameraId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetCameraEnabled(long cameraId, [MarshalAs(UnmanagedType.I1)] bool enabled);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsCameraEnabled(long cameraId);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetCameraAspectRatio(long cameraId, float aspect);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetCameraBackgroundColor(long cameraId, float r, float g, float b, float a);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetCameraDepth(long cameraId, int depth);

        /// <summary>
        /// Set camera field of view (degrees).
        /// </summary>
        public static void SetEngineCameraFOV(CameraHandle handle, float fov)
        {
            if (handle.IsValid)
                SetCameraFOV(handle.Id, fov);
        }

        /// <summary>
        /// Get camera field of view (degrees).
        /// </summary>
        public static float GetEngineCameraFOV(CameraHandle handle)
            => handle.IsValid ? GetCameraFOV(handle.Id) : 60f;

        /// <summary>
        /// Set camera near and far clip planes.
        /// </summary>
        public static void SetEngineCameraClipPlanes(CameraHandle handle, float nearClip, float farClip)
        {
            if (handle.IsValid)
                SetCameraClipPlanes(handle.Id, nearClip, farClip);
        }

        /// <summary>
        /// Set camera projection type.
        /// </summary>
        public static void SetEngineCameraProjection(CameraHandle handle, CameraProjectionType projection)
        {
            if (handle.IsValid)
                SetCameraProjection(handle.Id, (byte)projection);
        }

        /// <summary>
        /// Set camera type.
        /// </summary>
        public static void SetEngineCameraType(CameraHandle handle, CameraTypeEnum type)
        {
            if (handle.IsValid)
                SetCameraType(handle.Id, (byte)type);
        }

        /// <summary>
        /// Get camera type.
        /// </summary>
        public static CameraTypeEnum GetEngineCameraType(CameraHandle handle)
            => handle.IsValid ? (CameraTypeEnum)GetCameraType(handle.Id) : CameraTypeEnum.GameCamera;

        /// <summary>
        /// Enable/disable camera.
        /// </summary>
        public static void SetEngineCameraEnabled(CameraHandle handle, bool enabled)
        {
            if (handle.IsValid)
                SetCameraEnabled(handle.Id, enabled);
        }

        /// <summary>
        /// Check if camera is enabled.
        /// </summary>
        public static bool IsEngineCameraEnabled(CameraHandle handle)
            => handle.IsValid && IsCameraEnabled(handle.Id);

        /// <summary>
        /// Set camera aspect ratio.
        /// </summary>
        public static void SetEngineCameraAspectRatio(CameraHandle handle, float aspect)
        {
            if (handle.IsValid)
                SetCameraAspectRatio(handle.Id, aspect);
        }

        /// <summary>
        /// Set camera background color.
        /// </summary>
        public static void SetEngineCameraBackgroundColor(CameraHandle handle, float r, float g, float b, float a)
        {
            if (handle.IsValid)
                SetCameraBackgroundColor(handle.Id, r, g, b, a);
        }

        /// <summary>
        /// Set camera render depth (lower = rendered first).
        /// </summary>
        public static void SetEngineCameraDepth(CameraHandle handle, int depth)
        {
            if (handle.IsValid)
                SetCameraDepth(handle.Id, depth);
        }

        #endregion

        #region Camera Matrices and Gizmos

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void GetCameraViewMatrix(long cameraId, [MarshalAs(UnmanagedType.LPArray, SizeConst = 16)] float[] outMatrix);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void GetCameraProjectionMatrix(long cameraId, [MarshalAs(UnmanagedType.LPArray, SizeConst = 16)] float[] outMatrix);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void RenderCameraGizmo(long cameraId, float r, float g, float b);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ApplyCameraToRenderer(long cameraId);

        /// <summary>
        /// Get camera view matrix (4x4, row-major).
        /// </summary>
        public static float[] GetEngineCameraViewMatrix(CameraHandle handle)
        {
            var matrix = new float[16];
            if (handle.IsValid)
                GetCameraViewMatrix(handle.Id, matrix);
            return matrix;
        }

        /// <summary>
        /// Get camera projection matrix (4x4, row-major).
        /// </summary>
        public static float[] GetEngineCameraProjectionMatrix(CameraHandle handle)
        {
            var matrix = new float[16];
            if (handle.IsValid)
                GetCameraProjectionMatrix(handle.Id, matrix);
            return matrix;
        }

        /// <summary>
        /// Render a wireframe camera frustum gizmo.
        /// </summary>
        public static void RenderEngineCameraGizmo(CameraHandle handle, float r, float g, float b)
        {
            if (handle.IsValid)
                RenderCameraGizmo(handle.Id, r, g, b);
        }

        /// <summary>
        /// Apply this camera's view and projection to the renderer.
        /// Use this for camera preview.
        /// </summary>
        public static void ApplyEngineCameraToRenderer(CameraHandle handle)
        {
            if (handle.IsValid)
                ApplyCameraToRenderer(handle.Id);
        }

        #endregion
    }
}
