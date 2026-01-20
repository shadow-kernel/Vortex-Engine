using System;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components;

namespace Editor.Core.Services
{
    /// <summary>
    /// Service for handling transform gizmos in the editor viewport.
    /// Manages move, rotate, and scale gizmo rendering and interaction.
    /// </summary>
    public class TransformGizmoService
    {
        private static TransformGizmoService _instance;
        public static TransformGizmoService Instance => _instance ?? (_instance = new TransformGizmoService());

        public enum GizmoMode
        {
            Translate,
            Rotate,
            Scale
        }

        public enum GizmoSpace
        {
            Local,
            World
        }

        public enum GizmoAxis
        {
            None,
            X,
            Y,
            Z,
            XY,
            XZ,
            YZ,
            All
        }

        // Current state
        private GizmoMode _currentMode = GizmoMode.Translate;
        private GizmoSpace _currentSpace = GizmoSpace.World;
        private GizmoAxis _activeAxis = GizmoAxis.None;
        private GameEntity _selectedEntity;
        private bool _isDragging;
        private Vector3f _dragStartPosition;
        private ECS.Vector3 _initialPosition;
        private ECS.Vector3 _initialRotation;
        private ECS.Vector3 _initialScale;

        // Settings
        public float GizmoSize { get; set; } = 1.0f;
        public float SnapTranslate { get; set; } = 0.5f;
        public float SnapRotate { get; set; } = 15.0f;
        public float SnapScale { get; set; } = 0.1f;
        public bool SnapEnabled { get; set; } = false;

        public GizmoMode CurrentMode
        {
            get => _currentMode;
            set
            {
                if (_currentMode != value)
                {
                    _currentMode = value;
                    
                    // Sync with VortexAPI for rendering
                    switch (value)
                    {
                        case GizmoMode.Translate:
                            VortexAPI.CurrentGizmoType = VortexAPI.GizmoType.Translate;
                            break;
                        case GizmoMode.Rotate:
                            VortexAPI.CurrentGizmoType = VortexAPI.GizmoType.Rotate;
                            break;
                        case GizmoMode.Scale:
                            VortexAPI.CurrentGizmoType = VortexAPI.GizmoType.Scale;
                            break;
                    }
                    
                    ModeChanged?.Invoke(this, value);
                }
            }
        }

        public GizmoSpace CurrentSpace
        {
            get => _currentSpace;
            set => _currentSpace = value;
        }

        public GameEntity SelectedEntity
        {
            get => _selectedEntity;
            set
            {
                _selectedEntity = value;
                _isDragging = false;
                _activeAxis = GizmoAxis.None;
            }
        }

        public bool IsDragging => _isDragging;

        public event EventHandler<GizmoMode> ModeChanged;
        public event EventHandler<GameEntity> TransformChanged;

        private TransformGizmoService() { }

        /// <summary>
        /// Switch to translate mode (shortcut: W).
        /// </summary>
        public void SetTranslateMode()
        {
            CurrentMode = GizmoMode.Translate;
        }

        /// <summary>
        /// Switch to rotate mode (shortcut: E).
        /// </summary>
        public void SetRotateMode()
        {
            CurrentMode = GizmoMode.Rotate;
        }

        /// <summary>
        /// Switch to scale mode (shortcut: R).
        /// </summary>
        public void SetScaleMode()
        {
            CurrentMode = GizmoMode.Scale;
        }

        /// <summary>
        /// Toggle between local and world space.
        /// </summary>
        public void ToggleSpace()
        {
            CurrentSpace = CurrentSpace == GizmoSpace.Local ? GizmoSpace.World : GizmoSpace.Local;
        }

        /// <summary>
        /// Begin a gizmo drag operation.
        /// </summary>
        public void BeginDrag(GizmoAxis axis, Vector3f mouseWorldPos)
        {
            if (_selectedEntity == null) return;

            _isDragging = true;
            _activeAxis = axis;
            _dragStartPosition = mouseWorldPos;

            var transform = _selectedEntity.Transform;
            if (transform != null)
            {
                _initialPosition = transform.LocalPosition;
                _initialRotation = transform.LocalRotation;
                _initialScale = transform.LocalScale;
            }
        }

        /// <summary>
        /// Update the gizmo drag operation.
        /// </summary>
        public void UpdateDrag(Vector3f mouseWorldPos)
        {
            if (!_isDragging || _selectedEntity == null) return;

            var transform = _selectedEntity.Transform;
            if (transform == null) return;

            float deltaX = mouseWorldPos.X - _dragStartPosition.X;
            float deltaY = mouseWorldPos.Y - _dragStartPosition.Y;
            float deltaZ = mouseWorldPos.Z - _dragStartPosition.Z;

            switch (_currentMode)
            {
                case GizmoMode.Translate:
                    ApplyTranslation(transform, deltaX, deltaY, deltaZ);
                    break;
                case GizmoMode.Rotate:
                    ApplyRotation(transform, deltaX, deltaY, deltaZ);
                    break;
                case GizmoMode.Scale:
                    ApplyScale(transform, deltaX, deltaY, deltaZ);
                    break;
            }

            TransformChanged?.Invoke(this, _selectedEntity);
        }

        /// <summary>
        /// End the gizmo drag operation.
        /// </summary>
        public void EndDrag()
        {
            _isDragging = false;
            _activeAxis = GizmoAxis.None;
        }

        /// <summary>
        /// Cancel the current drag and revert changes.
        /// </summary>
        public void CancelDrag()
        {
            if (!_isDragging || _selectedEntity == null) return;

            var transform = _selectedEntity.Transform;
            if (transform != null)
            {
                transform.LocalPosition = _initialPosition;
                transform.LocalRotation = _initialRotation;
                transform.LocalScale = _initialScale;
            }

            _isDragging = false;
            _activeAxis = GizmoAxis.None;
        }

        private void ApplyTranslation(Transform transform, float dx, float dy, float dz)
        {
            var newPos = _initialPosition;

            switch (_activeAxis)
            {
                case GizmoAxis.X:
                    newPos = new ECS.Vector3(_initialPosition.X + dx, _initialPosition.Y, _initialPosition.Z);
                    break;
                case GizmoAxis.Y:
                    newPos = new ECS.Vector3(_initialPosition.X, _initialPosition.Y + dy, _initialPosition.Z);
                    break;
                case GizmoAxis.Z:
                    newPos = new ECS.Vector3(_initialPosition.X, _initialPosition.Y, _initialPosition.Z + dz);
                    break;
                case GizmoAxis.XY:
                    newPos = new ECS.Vector3(_initialPosition.X + dx, _initialPosition.Y + dy, _initialPosition.Z);
                    break;
                case GizmoAxis.XZ:
                    newPos = new ECS.Vector3(_initialPosition.X + dx, _initialPosition.Y, _initialPosition.Z + dz);
                    break;
                case GizmoAxis.YZ:
                    newPos = new ECS.Vector3(_initialPosition.X, _initialPosition.Y + dy, _initialPosition.Z + dz);
                    break;
                case GizmoAxis.All:
                    newPos = new ECS.Vector3(_initialPosition.X + dx, _initialPosition.Y + dy, _initialPosition.Z + dz);
                    break;
            }

            if (SnapEnabled)
            {
                newPos = new ECS.Vector3(
                    SnapValue(newPos.X, SnapTranslate),
                    SnapValue(newPos.Y, SnapTranslate),
                    SnapValue(newPos.Z, SnapTranslate));
            }

            transform.LocalPosition = newPos;
        }

        private void ApplyRotation(Transform transform, float dx, float dy, float dz)
        {
            float rotationSpeed = 100.0f; // degrees per unit
            var newRot = _initialRotation;

            switch (_activeAxis)
            {
                case GizmoAxis.X:
                    newRot = new ECS.Vector3(_initialRotation.X + dy * rotationSpeed, _initialRotation.Y, _initialRotation.Z);
                    break;
                case GizmoAxis.Y:
                    newRot = new ECS.Vector3(_initialRotation.X, _initialRotation.Y + dx * rotationSpeed, _initialRotation.Z);
                    break;
                case GizmoAxis.Z:
                    newRot = new ECS.Vector3(_initialRotation.X, _initialRotation.Y, _initialRotation.Z + dx * rotationSpeed);
                    break;
            }

            if (SnapEnabled)
            {
                newRot = new ECS.Vector3(
                    SnapValue(newRot.X, SnapRotate),
                    SnapValue(newRot.Y, SnapRotate),
                    SnapValue(newRot.Z, SnapRotate));
            }

            transform.LocalRotation = newRot;
        }

        private void ApplyScale(Transform transform, float dx, float dy, float dz)
        {
            float scaleSpeed = 1.0f;
            var newScale = _initialScale;

            switch (_activeAxis)
            {
                case GizmoAxis.X:
                    newScale = new ECS.Vector3(Math.Max(0.01f, _initialScale.X + dx * scaleSpeed), _initialScale.Y, _initialScale.Z);
                    break;
                case GizmoAxis.Y:
                    newScale = new ECS.Vector3(_initialScale.X, Math.Max(0.01f, _initialScale.Y + dy * scaleSpeed), _initialScale.Z);
                    break;
                case GizmoAxis.Z:
                    newScale = new ECS.Vector3(_initialScale.X, _initialScale.Y, Math.Max(0.01f, _initialScale.Z + dz * scaleSpeed));
                    break;
                case GizmoAxis.All:
                    float uniformDelta = (dx + dy + dz) / 3.0f;
                    newScale = new ECS.Vector3(
                        Math.Max(0.01f, _initialScale.X + uniformDelta * scaleSpeed),
                        Math.Max(0.01f, _initialScale.Y + uniformDelta * scaleSpeed),
                        Math.Max(0.01f, _initialScale.Z + uniformDelta * scaleSpeed));
                    break;
            }

            if (SnapEnabled)
            {
                newScale = new ECS.Vector3(
                    SnapValue(newScale.X, SnapScale),
                    SnapValue(newScale.Y, SnapScale),
                    SnapValue(newScale.Z, SnapScale));
            }

            transform.LocalScale = newScale;
        }

        private float SnapValue(float value, float snapSize)
        {
            return (float)Math.Round(value / snapSize) * snapSize;
        }


        /// <summary>
        /// Submit gizmo geometry for rendering.
        /// Called each frame when gizmos are visible.
        /// </summary>
        public void SubmitGizmoForRendering()
        {
            if (_selectedEntity == null || !EditorViewportService.Instance.AreGizmosVisible) return;

            var transform = _selectedEntity.Transform;
            if (transform == null) return;

            var pos = transform.LocalPosition;

            // TODO: Submit actual gizmo geometry to the renderer
            // This would require additional API calls for line/arrow rendering
            // For now, we track the state and the engine can render gizmos natively
        }
    }
}
