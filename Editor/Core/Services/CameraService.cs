using System;
using System.Collections.Generic;
using System.Linq;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Rendering;

namespace Editor.Core.Services
{
    /// <summary>
    /// Manages all cameras in the scene and provides camera-related operations.
    /// Handles the relationship between Editor Camera components and Engine cameras.
    /// </summary>
    public class CameraService
    {
        private static CameraService _instance;
        public static CameraService Instance => _instance ?? (_instance = new CameraService());

        private readonly Dictionary<Guid, CameraHandle> _entityCameras = new Dictionary<Guid, CameraHandle>();
        private CameraHandle _editorCamera = CameraHandle.Invalid;
        private CameraHandle _activeCamera = CameraHandle.Invalid;
        private bool _isGameCameraActive;

        private CameraService() { }

        /// <summary>
        /// Initialize the camera service.
        /// </summary>
        public void Initialize()
        {
            // Create the default editor camera
            var desc = new CameraDescriptor
            {
                Position = new float[] { 0, 5, -10 },
                Rotation = new float[] { 0, 0, 0, 1 },
                Projection = (byte)CameraProjectionType.Perspective,
                FieldOfView = 60f,
                OrthographicSize = 5f,
                NearClip = 0.1f,
                FarClip = 1000f,
                AspectRatio = 16f / 9f,
                ClearFlags = (byte)CameraClearFlagsType.SolidColor,
                BackgroundColor = new float[] { 0.1f, 0.1f, 0.15f, 1f },
                Depth = -100, // Editor camera has lowest depth
                CullingMask = -1,
                CameraType = (byte)CameraTypeEnum.EditorCamera,
                IsEnabled = true
            };

            _editorCamera = VortexAPI.CreateEngineCamera(desc);
            _activeCamera = _editorCamera;
        }

        /// <summary>
        /// Shutdown and cleanup.
        /// </summary>
        public void Shutdown()
        {
            // Destroy all entity cameras
            foreach (var handle in _entityCameras.Values)
            {
                if (handle.IsValid)
                    VortexAPI.DestroyEngineCamera(handle);
            }
            _entityCameras.Clear();

            // Destroy editor camera
            if (_editorCamera.IsValid)
            {
                VortexAPI.DestroyEngineCamera(_editorCamera);
                _editorCamera = CameraHandle.Invalid;
            }
        }

        /// <summary>
        /// The editor's free-fly camera.
        /// </summary>
        public CameraHandle EditorCamera => _editorCamera;

        /// <summary>
        /// Currently active camera for rendering.
        /// </summary>
        public CameraHandle ActiveCamera => _activeCamera;

        /// <summary>
        /// Whether a game camera is currently active (vs editor camera).
        /// </summary>
        public bool IsGameCameraActive => _isGameCameraActive;

        /// <summary>
        /// Register a camera component from an entity.
        /// </summary>
        public CameraHandle RegisterCamera(GameEntity entity, Camera cameraComponent)
        {
            if (entity == null || cameraComponent == null) return CameraHandle.Invalid;

            var transform = entity.GetComponent<Transform>();
            if (transform == null) return CameraHandle.Invalid;

            // Check if already registered
            if (_entityCameras.TryGetValue(entity.Id, out var existingHandle))
            {
                // Update the existing camera
                UpdateCamera(existingHandle, cameraComponent, transform);
                return existingHandle;
            }

            // Create new engine camera
            var desc = CreateDescriptor(cameraComponent, transform);
            var handle = VortexAPI.CreateEngineCamera(desc);
            
            if (handle.IsValid)
            {
                _entityCameras[entity.Id] = handle;
                cameraComponent.EngineCameraId = handle.Id;

                // If this is the main camera and no active game camera, make it active
                if (cameraComponent.CameraType == CameraType.MainCamera && !_isGameCameraActive)
                {
                    SetActiveCamera(handle);
                }
            }

            return handle;
        }

        /// <summary>
        /// Unregister a camera when entity is deleted.
        /// </summary>
        public void UnregisterCamera(Guid entityId)
        {
            if (_entityCameras.TryGetValue(entityId, out var handle))
            {
                // If this was the active camera, switch back to editor
                if (_activeCamera.Id == handle.Id)
                {
                    SetActiveCamera(_editorCamera);
                    _isGameCameraActive = false;
                }

                VortexAPI.DestroyEngineCamera(handle);
                _entityCameras.Remove(entityId);
            }
        }

        /// <summary>
        /// Update camera properties.
        /// </summary>
        public void UpdateCamera(CameraHandle handle, Camera camera, Transform transform)
        {
            if (!handle.IsValid) return;

            VortexAPI.SetEngineCameraPosition(handle,
                transform.LocalPosition.X,
                transform.LocalPosition.Y,
                transform.LocalPosition.Z);

            var quat = EulerToQuaternion(
                transform.LocalRotation.X,
                transform.LocalRotation.Y,
                transform.LocalRotation.Z);
            VortexAPI.SetEngineCameraRotation(handle, quat[0], quat[1], quat[2], quat[3]);

            VortexAPI.SetEngineCameraFOV(handle, camera.FieldOfView);
            VortexAPI.SetEngineCameraClipPlanes(handle, camera.NearClip, camera.FarClip);
            VortexAPI.SetEngineCameraProjection(handle, (CameraProjectionType)camera.Projection);
            VortexAPI.SetEngineCameraType(handle, (CameraTypeEnum)camera.CameraType);
            VortexAPI.SetEngineCameraBackgroundColor(handle,
                camera.BackgroundR, camera.BackgroundG, camera.BackgroundB, 1f);
            VortexAPI.SetEngineCameraDepth(handle, camera.Depth);
        }

        /// <summary>
        /// Get the camera handle for an entity.
        /// </summary>
        public CameraHandle GetEntityCamera(Guid entityId)
        {
            return _entityCameras.TryGetValue(entityId, out var handle) ? handle : CameraHandle.Invalid;
        }

        /// <summary>
        /// Set the active camera for rendering.
        /// </summary>
        public void SetActiveCamera(CameraHandle camera)
        {
            if (!camera.IsValid) return;
            
            _activeCamera = camera;
            _isGameCameraActive = camera.Id != _editorCamera.Id;
            
            VortexAPI.SetEngineActiveCamera(camera);
            VortexAPI.ApplyEngineCameraToRenderer(camera);
            
            ActiveCameraChanged?.Invoke(this, camera);
        }

        /// <summary>
        /// Switch back to the editor camera.
        /// </summary>
        public void SwitchToEditorCamera()
        {
            SetActiveCamera(_editorCamera);
            _isGameCameraActive = false;
        }

        /// <summary>
        /// Switch to a specific game camera.
        /// </summary>
        public void SwitchToGameCamera(Guid entityId)
        {
            if (_entityCameras.TryGetValue(entityId, out var handle))
            {
                SetActiveCamera(handle);
            }
        }

        /// <summary>
        /// Get the main camera (first camera with MainCamera type).
        /// </summary>
        public CameraHandle GetMainCamera()
        {
            return VortexAPI.GetEngineMainCamera();
        }

        /// <summary>
        /// Get all registered game cameras.
        /// </summary>
        public IEnumerable<(Guid EntityId, CameraHandle Handle)> GetAllGameCameras()
        {
            return _entityCameras.Select(kv => (kv.Key, kv.Value));
        }

        /// <summary>
        /// Update the editor camera position.
        /// </summary>
        public void SetEditorCameraPosition(float x, float y, float z)
        {
            if (_editorCamera.IsValid)
            {
                VortexAPI.SetEngineCameraPosition(_editorCamera, x, y, z);
                
                if (!_isGameCameraActive)
                {
                    VortexAPI.ApplyEngineCameraToRenderer(_editorCamera);
                }
            }
        }

        /// <summary>
        /// Update the editor camera rotation.
        /// </summary>
        public void SetEditorCameraRotation(float x, float y, float z, float w)
        {
            if (_editorCamera.IsValid)
            {
                VortexAPI.SetEngineCameraRotation(_editorCamera, x, y, z, w);
                
                if (!_isGameCameraActive)
                {
                    VortexAPI.ApplyEngineCameraToRenderer(_editorCamera);
                }
            }
        }

        /// <summary>
        /// Render camera gizmo for selected camera entity.
        /// </summary>
        public void RenderCameraGizmoForEntity(Guid entityId)
        {
            if (!_entityCameras.TryGetValue(entityId, out var handle)) return;

            var type = VortexAPI.GetEngineCameraType(handle);
            
            // Main camera = purple, others = blue
            if (type == CameraTypeEnum.MainCamera)
            {
                VortexAPI.RenderEngineCameraGizmo(handle, 0.608f, 0.349f, 0.714f);
            }
            else
            {
                VortexAPI.RenderEngineCameraGizmo(handle, 0.337f, 0.612f, 0.839f);
            }
        }

        /// <summary>
        /// Event fired when active camera changes.
        /// </summary>
        public event EventHandler<CameraHandle> ActiveCameraChanged;

        private CameraDescriptor CreateDescriptor(Camera camera, Transform transform)
        {
            var quat = EulerToQuaternion(
                transform.LocalRotation.X,
                transform.LocalRotation.Y,
                transform.LocalRotation.Z);

            return new CameraDescriptor
            {
                Position = new float[] { transform.LocalPosition.X, transform.LocalPosition.Y, transform.LocalPosition.Z },
                Rotation = quat,
                Projection = (byte)camera.Projection,
                FieldOfView = camera.FieldOfView,
                OrthographicSize = camera.OrthographicSize,
                NearClip = camera.NearClip,
                FarClip = camera.FarClip,
                AspectRatio = 16f / 9f,
                ClearFlags = (byte)camera.ClearFlags,
                BackgroundColor = new float[] { camera.BackgroundR, camera.BackgroundG, camera.BackgroundB, 1f },
                Depth = camera.Depth,
                CullingMask = camera.CullingMask,
                CameraType = (byte)camera.CameraType,
                IsEnabled = true
            };
        }

        private float[] EulerToQuaternion(float pitch, float yaw, float roll)
        {
            double p = pitch * Math.PI / 180.0 * 0.5;
            double y = yaw * Math.PI / 180.0 * 0.5;
            double r = roll * Math.PI / 180.0 * 0.5;

            double sinP = Math.Sin(p), cosP = Math.Cos(p);
            double sinY = Math.Sin(y), cosY = Math.Cos(y);
            double sinR = Math.Sin(r), cosR = Math.Cos(r);

            return new float[]
            {
                (float)(cosR * sinP * cosY + sinR * cosP * sinY),
                (float)(cosR * cosP * sinY - sinR * sinP * cosY),
                (float)(sinR * cosP * cosY - cosR * sinP * sinY),
                (float)(cosR * cosP * cosY + sinR * sinP * sinY)
            };
        }
    }
}
