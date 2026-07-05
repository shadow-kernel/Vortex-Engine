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

        /// <summary>#49: when true (and AreCollidersVisible), EVERY entity's collider is drawn — not just
        /// the selection. Level-building view: all trigger zones + walkable bounds at a glance.</summary>
        public bool ShowAllColliders { get; set; }

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

    /// <summary>Per-model import settings persisted in a tiny sidecar file (&lt;model&gt;.vimport) next to the model.
    /// Currently just a default placement scale set in the Model Editor and applied when the model is dropped into a
    /// scene, so an over/under-sized source model comes in at the size you want without hand-scaling every copy.</summary>
    public static class ModelImportSettings
    {
        public static float LoadDefaultScale(string modelPath)
        {
            try
            {
                var p = SidecarPath(modelPath);
                if (p == null || !System.IO.File.Exists(p)) return 1.0f;
                var txt = System.IO.File.ReadAllText(p);
                var m = System.Text.RegularExpressions.Regex.Match(txt, "\"defaultScale\"\\s*:\\s*([0-9eE+\\-.]+)");
                if (m.Success && float.TryParse(m.Groups[1].Value,
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0.0001f)
                    return v;
            }
            catch { }
            return 1.0f;
        }

        public static void SaveDefaultScale(string modelPath, float scale)
        {
            try
            {
                var p = SidecarPath(modelPath);
                if (p == null) return;
                if (scale <= 0.0001f) scale = 1.0f;
                System.IO.File.WriteAllText(p,
                    "{\n  \"defaultScale\": " + scale.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "\n}\n");
            }
            catch { }
        }

        /// <summary>The .vimport sidecar next to the model. Normalises relative paths against the project root and
        /// strips any "#submeshN" suffix so the model editor and the drop handler always resolve the SAME file.</summary>
        private static string SidecarPath(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath)) return null;
            string full = modelPath;
            try
            {
                int hash = full.IndexOf('#');
                if (hash >= 0) full = full.Substring(0, hash);
                if (!System.IO.Path.IsPathRooted(full))
                {
                    var proj = Editor.Core.Data.ProjectData.Current?.Path;
                    if (!string.IsNullOrEmpty(proj)) full = System.IO.Path.Combine(proj, full);
                }
            }
            catch { }
            return full + ".vimport";
        }
    }
}
