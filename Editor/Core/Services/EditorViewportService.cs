using System;
using Editor.DllWrapper;

namespace Editor.Core.Services
{
    /// <summary>
    /// Service for managing editor viewport settings like grid, gizmos, and camera.
    /// </summary>
    public class EditorViewportService : IDisposable
    {
        private static EditorViewportService _instance;
        public static EditorViewportService Instance => _instance ?? (_instance = new EditorViewportService());

        private bool _isGridVisible = true;
        private bool _areGizmosVisible = true;
        private bool _areCollidersVisible = true;
        private bool _snapToGrid = false;
        private float _gridSpacing = 1.0f;
        private float _gridExtent = 100.0f;

        private EditorViewportService() { }

        public bool IsGridVisible
        {
            get => _isGridVisible;
            set
            {
                _isGridVisible = value;
                VortexAPI.ShowGrid(value);
                GridVisibilityChanged?.Invoke(this, value);
            }
        }

        public bool AreGizmosVisible
        {
            get => _areGizmosVisible;
            set
            {
                _areGizmosVisible = value;
                VortexAPI.ShowGizmos(value);
                GizmosVisibilityChanged?.Invoke(this, value);
            }
        }

        /// <summary>
        /// Whether the green collider wireframe (ColliderGizmo) is drawn for the selected entity in the viewport.
        /// Purely managed-side — read each frame by SceneRenderService.SubmitOverlays; no native call needed.
        /// </summary>
        public bool AreCollidersVisible
        {
            get => _areCollidersVisible;
            set
            {
                _areCollidersVisible = value;
                CollisionVisibilityChanged?.Invoke(this, value);
            }
        }

        public bool SnapToGrid
        {
            get => _snapToGrid;
            set
            {
                _snapToGrid = value;
                SnapToGridChanged?.Invoke(this, value);
            }
        }

        public float GridSpacing
        {
            get => _gridSpacing;
            set
            {
                _gridSpacing = value;
                UpdateGridSettings();
            }
        }

        public float GridExtent
        {
            get => _gridExtent;
            set
            {
                _gridExtent = value;
                UpdateGridSettings();
            }
        }

        private void UpdateGridSettings()
        {
            VortexAPI.ConfigureGrid(_gridSpacing, 10.0f, _gridExtent);
        }

        public void ToggleGrid()
        {
            IsGridVisible = !IsGridVisible;
        }

        public void ToggleGizmos()
        {
            AreGizmosVisible = !AreGizmosVisible;
        }

        public void ToggleColliders()
        {
            AreCollidersVisible = !AreCollidersVisible;
        }

        public void ToggleSnapToGrid()
        {
            SnapToGrid = !SnapToGrid;
        }

        /// <summary>
        /// Get the snapped position if snap to grid is enabled.
        /// </summary>
        public float SnapValue(float value)
        {
            if (!SnapToGrid) return value;
            return (float)Math.Round(value / _gridSpacing) * _gridSpacing;
        }

        public event EventHandler<bool> GridVisibilityChanged;
        public event EventHandler<bool> GizmosVisibilityChanged;
        public event EventHandler<bool> CollisionVisibilityChanged;
        public event EventHandler<bool> SnapToGridChanged;

        public void Dispose()
        {
            _instance = null;
        }
    }
}
