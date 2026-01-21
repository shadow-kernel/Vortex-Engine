using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.Core.Services;
using Editor.Core.Services.Rendering;
using Editor.ECS;

namespace Editor.Editors.WorldEditor.Components.CameraPreview
{
    /// <summary>
    /// Standalone camera preview overlay that floats above all other content.
    /// Must be placed inside a Popup to render over HwndHost elements.
    /// </summary>
    public partial class CameraPreviewOverlay : UserControl
    {
        private GameEntity _currentCamera;
        private DateTime _lastRenderTime = DateTime.MinValue;
        
        // Render at 30 FPS for preview
        private const int RenderIntervalMs = 33;
        
        public CameraPreviewOverlay()
        {
            InitializeComponent();
            
            // Subscribe to preview service events
            CameraPreviewService.Instance.PreviewRequested += OnPreviewRequested;
            CameraPreviewService.Instance.PreviewClosed += OnPreviewClosed;
            
            // Subscribe to render updates
            CompositionTarget.Rendering += OnCompositionTargetRendering;
            
            Unloaded += OnUnloaded;
        }
        
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            CameraPreviewService.Instance.PreviewRequested -= OnPreviewRequested;
            CameraPreviewService.Instance.PreviewClosed -= OnPreviewClosed;
        }
        
        private void OnPreviewRequested(object sender, GameEntity cameraEntity)
        {
            ShowPreview(cameraEntity);
        }
        
        private void OnPreviewClosed(object sender, EventArgs e)
        {
            HidePreview();
        }
        
        /// <summary>
        /// Get the parent Popup if this control is inside one.
        /// </summary>
        private Popup GetParentPopup()
        {
            DependencyObject parent = this;
            while (parent != null)
            {
                if (parent is Popup popup)
                    return popup;
                parent = LogicalTreeHelper.GetParent(parent) ?? VisualTreeHelper.GetParent(parent);
            }
            return null;
        }
        
        /// <summary>
        /// Show the camera preview for the specified entity.
        /// </summary>
        public void ShowPreview(GameEntity cameraEntity)
        {
            if (cameraEntity == null)
            {
                HidePreview();
                return;
            }
            
            var camera = cameraEntity.GetComponent<ECS.Components.Rendering.Camera>();
            if (camera == null)
            {
                HidePreview();
                return;
            }
            
            _currentCamera = cameraEntity;
            
            // Update UI
            var isMain = camera.CameraType == ECS.Components.Rendering.CameraType.MainCamera;
            CameraTypeLabel.Text = isMain ? "MAIN CAM" : "GAME CAM";
            CameraNameLabel.Text = cameraEntity.Name;
            CameraDetailsLabel.Text = $"FOV: {camera.FieldOfView:F0}\u00B0";
            
            // Update border color
            PipContainer.BorderBrush = new SolidColorBrush(
                isMain ? Color.FromRgb(155, 89, 182) : Color.FromRgb(86, 156, 214));
            
            // Initialize and render using the new manager
            var manager = ViewportRenderManager.Instance;
            manager.InitializeViewport(ViewportRenderManager.PipViewportIndex);
            manager.SetCamera(ViewportRenderManager.PipViewportIndex, _currentCamera);
            manager.ForceRender(ViewportRenderManager.PipViewportIndex);
            
            UpdateImageSource();
            
            // Show the container and open parent popup
            PipContainer.Visibility = Visibility.Visible;
            LoadingIndicator.Visibility = Visibility.Collapsed;
            
            var popup = GetParentPopup();
            if (popup != null)
            {
                popup.IsOpen = true;
            }
        }
        
        /// <summary>
        /// Hide the camera preview.
        /// </summary>
        public void HidePreview()
        {
            _currentCamera = null;
            PipContainer.Visibility = Visibility.Collapsed;
            
            var popup = GetParentPopup();
            if (popup != null)
            {
                popup.IsOpen = false;
            }
        }
        
        /// <summary>
        /// Called every WPF frame - handles throttled rendering.
        /// </summary>
        private void OnCompositionTargetRendering(object sender, EventArgs e)
        {
            if (_currentCamera == null || PipContainer.Visibility != Visibility.Visible)
                return;
            
            // Throttle rendering
            var now = DateTime.Now;
            if ((now - _lastRenderTime).TotalMilliseconds < RenderIntervalMs)
                return;
            
            _lastRenderTime = now;
            
            // Render using the new manager
            var manager = ViewportRenderManager.Instance;
            manager.SetCamera(ViewportRenderManager.PipViewportIndex, _currentCamera);
            manager.Render(ViewportRenderManager.PipViewportIndex, 0); // No throttle here, we handle it above
            
            UpdateImageSource();
        }
        
        /// <summary>
        /// Update the preview image source.
        /// </summary>
        private void UpdateImageSource()
        {
            var bitmap = ViewportRenderManager.Instance.GetBitmap(ViewportRenderManager.PipViewportIndex);
            if (bitmap != null && PreviewImage.Source != bitmap)
            {
                PreviewImage.Source = bitmap;
            }
        }
        
        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            CameraPreviewService.Instance.ClosePreview();
        }
        
        /// <summary>
        /// Check if the preview is currently visible.
        /// </summary>
        public bool IsPreviewVisible => PipContainer.Visibility == Visibility.Visible;
        
        /// <summary>
        /// Get the currently previewed camera entity.
        /// </summary>
        public GameEntity CurrentCamera => _currentCamera;
    }
}
