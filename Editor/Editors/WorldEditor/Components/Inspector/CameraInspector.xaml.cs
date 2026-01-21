using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.ECS.Components.Rendering;

namespace Editor.Editors.WorldEditor.Components.Inspector
{
    /// <summary>
    /// Camera component inspector for the World Editor.
    /// Allows editing of camera properties like FOV, clipping planes, projection type, etc.
    /// </summary>
    public partial class CameraInspector : UserControl
    {
        private Camera _camera;
        private bool _isUpdating;

        public CameraInspector()
        {
            InitializeComponent();
        }

        /// <summary>
        /// The camera component being inspected.
        /// </summary>
        public Camera Camera
        {
            get => _camera;
            set
            {
                _camera = value;
                UpdateUI();
            }
        }

        /// <summary>
        /// Event fired when any camera property changes.
        /// </summary>
        public event EventHandler CameraChanged;

        private void UpdateUI()
        {
            if (_camera == null) return;

            _isUpdating = true;

            try
            {
                // Camera Type
                CameraTypeCombo.SelectedIndex = (int)_camera.CameraType;
                UpdateCameraTypeLabel();
                UpdateIconColor();

                // Projection
                ProjectionCombo.SelectedIndex = (int)_camera.Projection;
                UpdateProjectionVisibility();

                // FOV & Ortho Size
                FovInput.Text = _camera.FieldOfView.ToString("F1", CultureInfo.InvariantCulture);
                OrthoSizeInput.Text = _camera.OrthographicSize.ToString("F2", CultureInfo.InvariantCulture);

                // Clipping
                NearClipInput.Text = _camera.NearClip.ToString("F2", CultureInfo.InvariantCulture);
                FarClipInput.Text = _camera.FarClip.ToString("F1", CultureInfo.InvariantCulture);

                // Background Color
                UpdateBackgroundColorPreview();

                // Depth
                DepthInput.Text = _camera.Depth.ToString();
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private void UpdateCameraTypeLabel()
        {
            switch (_camera.CameraType)
            {
                case CameraType.MainCamera:
                    CameraTypeLabel.Text = "(Primary)";
                    break;
                case CameraType.EditorCamera:
                    CameraTypeLabel.Text = "(Editor Only)";
                    break;
                default:
                    CameraTypeLabel.Text = "";
                    break;
            }
        }

        private void UpdateIconColor()
        {
            CameraIcon.Foreground = _camera.CameraType == CameraType.MainCamera 
                ? new SolidColorBrush(Color.FromRgb(155, 89, 182))  // Purple for main
                : new SolidColorBrush(Color.FromRgb(86, 156, 214)); // Blue for others
        }

        private void UpdateProjectionVisibility()
        {
            bool isPerspective = _camera.Projection == CameraProjection.Perspective;
            FovPanel.Visibility = isPerspective ? Visibility.Visible : Visibility.Collapsed;
            OrthoPanel.Visibility = isPerspective ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateBackgroundColorPreview()
        {
            byte r = (byte)Clamp(_camera.BackgroundR * 255, 0, 255);
            byte g = (byte)Clamp(_camera.BackgroundG * 255, 0, 255);
            byte b = (byte)Clamp(_camera.BackgroundB * 255, 0, 255);

            var colorBrush = new SolidColorBrush(Color.FromRgb(r, g, b));
            
            // Find the border inside the button template
            if (BackgroundColorButton.Template.FindName("ColorPreview", BackgroundColorButton) is Border border)
            {
                border.Background = colorBrush;
            }

            ColorValueText.Text = $"({_camera.BackgroundR:F2}, {_camera.BackgroundG:F2}, {_camera.BackgroundB:F2})";
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        #region Event Handlers

        private void CameraType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdating || _camera == null || CameraTypeCombo.SelectedItem == null) return;

            var newType = (CameraType)CameraTypeCombo.SelectedIndex;
            
            // Check if trying to set Main Camera when one already exists
            if (newType == CameraType.MainCamera)
            {
                var existingMainCamera = FindExistingMainCamera();
                if (existingMainCamera != null && existingMainCamera != _camera)
                {
                    // Another Main Camera exists - show warning and revert
                    MainCameraWarning.Visibility = Visibility.Visible;
                    _isUpdating = true;
                    CameraTypeCombo.SelectedIndex = (int)_camera.CameraType;
                    _isUpdating = false;
                    return;
                }
            }
            
            
            MainCameraWarning.Visibility = Visibility.Collapsed;
            _camera.CameraType = newType;
            
            // Sync IsMainCamera flag
            _camera.IsMainCamera = (_camera.CameraType == CameraType.MainCamera);

            UpdateCameraTypeLabel();
            UpdateIconColor();
            CameraChanged?.Invoke(this, EventArgs.Empty);
        }
        
        /// <summary>
        /// Find existing Main Camera in the scene.
        /// </summary>
        private Camera FindExistingMainCamera()
        {
            var scene = SceneService.Instance?.CurrentScene ?? ProjectData.Current?.ActiveScene;
            if (scene?.Entities == null) return null;
            
            foreach (var entity in scene.Entities)
            {
                var cam = FindMainCameraRecursive(entity);
                if (cam != null) return cam;
            }
            return null;
        }
        
        private Camera FindMainCameraRecursive(ECS.GameEntity entity)
        {
            var cam = entity.GetComponent<Camera>();
            if (cam != null && cam.CameraType == CameraType.MainCamera)
                return cam;
            
            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    var found = FindMainCameraRecursive(child);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private void Projection_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdating || _camera == null) return;

            _camera.Projection = (CameraProjection)ProjectionCombo.SelectedIndex;
            UpdateProjectionVisibility();
            CameraChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Fov_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating || _camera == null) return;

            if (TryParseFloat(FovInput.Text, out float fov))
            {
                _camera.FieldOfView = Math.Max(1f, Math.Min(fov, 179f));
                CameraChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OrthoSize_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating || _camera == null) return;

            if (TryParseFloat(OrthoSizeInput.Text, out float size))
            {
                _camera.OrthographicSize = Math.Max(0.1f, size);
                CameraChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void NearClip_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating || _camera == null) return;

            if (TryParseFloat(NearClipInput.Text, out float near))
            {
                _camera.NearClip = Math.Max(0.001f, near);
                CameraChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void FarClip_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating || _camera == null) return;

            if (TryParseFloat(FarClipInput.Text, out float far))
            {
                _camera.FarClip = Math.Max(_camera.NearClip + 0.1f, far);
                CameraChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void BackgroundColor_Click(object sender, RoutedEventArgs e)
        {
            if (_camera == null) return;

            // Simple color picker - in production, you'd use a proper color picker dialog
            // For now, cycle through some preset colors
            float r = _camera.BackgroundR;
            float g = _camera.BackgroundG;
            float b = _camera.BackgroundB;

            // Cycle: Dark Blue -> Dark Gray -> Black -> Dark Blue
            if (b > 0.2f)
            {
                _camera.BackgroundR = 0.15f;
                _camera.BackgroundG = 0.15f;
                _camera.BackgroundB = 0.15f;
            }
            else if (r > 0.1f)
            {
                _camera.BackgroundR = 0f;
                _camera.BackgroundG = 0f;
                _camera.BackgroundB = 0f;
            }
            else
            {
                _camera.BackgroundR = 0.1f;
                _camera.BackgroundG = 0.1f;
                _camera.BackgroundB = 0.3f;
            }

            UpdateBackgroundColorPreview();
            CameraChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Depth_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating || _camera == null) return;

            if (int.TryParse(DepthInput.Text, out int depth))
            {
                _camera.Depth = depth;
                CameraChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void PreviewCamera_Click(object sender, RoutedEventArgs e)
        {
            if (_camera == null) return;

            // Fire event to switch viewport to this camera's view
            PreviewCameraRequested?.Invoke(this, new CameraPreviewEventArgs(_camera));
        }

        #endregion

        private bool TryParseFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Event fired when user requests to preview this camera's view.
        /// </summary>
        public event EventHandler<CameraPreviewEventArgs> PreviewCameraRequested;
    }

    /// <summary>
    /// Event args for camera preview request.
    /// </summary>
    public class CameraPreviewEventArgs : EventArgs
    {
        public Camera Camera { get; }

        public CameraPreviewEventArgs(Camera camera)
        {
            Camera = camera;
        }
    }
}
