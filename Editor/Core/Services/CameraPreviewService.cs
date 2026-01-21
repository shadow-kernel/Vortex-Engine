using System;
using Editor.ECS;

namespace Editor.Core.Services
{
    /// <summary>
    /// Service for managing camera preview PIP (Picture-in-Picture) display.
    /// Allows showing camera previews via double-click or context menu.
    /// </summary>
    public class CameraPreviewService
    {
        private static CameraPreviewService _instance;
        public static CameraPreviewService Instance => _instance ?? (_instance = new CameraPreviewService());

        /// <summary>
        /// Event raised when a camera preview should be shown.
        /// </summary>
        public event EventHandler<GameEntity> PreviewRequested;

        /// <summary>
        /// Event raised when the camera preview should be hidden.
        /// </summary>
        public event EventHandler PreviewClosed;

        /// <summary>
        /// The currently previewed camera entity (null if none).
        /// </summary>
        public GameEntity CurrentPreviewCamera { get; private set; }

        /// <summary>
        /// Request to show a camera preview for the specified camera entity.
        /// </summary>
        public void ShowPreview(GameEntity cameraEntity)
        {
            if (cameraEntity == null) return;
            
            var camera = cameraEntity.GetComponent<ECS.Components.Rendering.Camera>();
            if (camera == null) return;

            CurrentPreviewCamera = cameraEntity;
            PreviewRequested?.Invoke(this, cameraEntity);
        }

        /// <summary>
        /// Close the camera preview.
        /// </summary>
        public void ClosePreview()
        {
            CurrentPreviewCamera = null;
            PreviewClosed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Toggle the camera preview for the specified entity.
        /// </summary>
        public void TogglePreview(GameEntity cameraEntity)
        {
            if (CurrentPreviewCamera == cameraEntity)
            {
                ClosePreview();
            }
            else
            {
                ShowPreview(cameraEntity);
            }
        }
    }
}
