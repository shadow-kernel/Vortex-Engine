using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Editor.DllWrapper;
using Editor.ECS.Components.Rendering;

namespace Editor.Editors.WorldEditor.Components.CameraPreview
{
    /// <summary>
    /// A small preview window showing a camera's view.
    /// Can be used to preview game cameras in the editor.
    /// </summary>
    public partial class CameraPreviewControl : UserControl
    {
        private Camera _camera;
        private CameraHandle _engineCamera;

        public CameraPreviewControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// The camera component being previewed.
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
        /// The engine camera handle.
        /// </summary>
        public CameraHandle EngineCamera
        {
            get => _engineCamera;
            set => _engineCamera = value;
        }

        /// <summary>
        /// Name to display in the header.
        /// </summary>
        public string CameraName
        {
            get => CameraNameText.Text;
            set => CameraNameText.Text = value;
        }

        private void UpdateUI()
        {
            if (_camera == null) return;

            // Update info texts
            FovText.Text = $"FOV: {_camera.FieldOfView:F0}°";
            ClipText.Text = $"Clip: {_camera.NearClip:F1} - {_camera.FarClip:F0}";

            // Update type badge
            switch (_camera.CameraType)
            {
                case CameraType.MainCamera:
                    TypeText.Text = "MAIN";
                    TypeBadge.Background = new SolidColorBrush(Color.FromArgb(128, 155, 89, 182)); // Purple
                    CameraIcon.Foreground = new SolidColorBrush(Color.FromRgb(155, 89, 182));
                    break;
                case CameraType.EditorCamera:
                    TypeText.Text = "EDITOR";
                    TypeBadge.Background = new SolidColorBrush(Color.FromArgb(128, 86, 156, 214)); // Blue
                    CameraIcon.Foreground = new SolidColorBrush(Color.FromRgb(86, 156, 214));
                    break;
                default:
                    TypeText.Text = "GAME";
                    TypeBadge.Background = new SolidColorBrush(Color.FromArgb(128, 100, 100, 100)); // Gray
                    CameraIcon.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
                    break;
            }

            // Update projection indicator
            if (_camera.Projection == CameraProjection.Orthographic)
            {
                FovText.Text = $"Ortho: {_camera.OrthographicSize:F1}";
            }
        }

        private void SwitchTo_Click(object sender, RoutedEventArgs e)
        {
            if (_engineCamera.IsValid)
            {
                SwitchToCameraRequested?.Invoke(this, _engineCamera);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Event fired when user wants to switch the main viewport to this camera.
        /// </summary>
        public event EventHandler<CameraHandle> SwitchToCameraRequested;

        /// <summary>
        /// Event fired when user closes this preview.
        /// </summary>
        public event EventHandler CloseRequested;

        /// <summary>
        /// Refresh the preview display.
        /// </summary>
        public void RefreshPreview()
        {
            UpdateUI();
            // In a full implementation, this would trigger a render of the camera's view
            // to a texture that is displayed in the preview area
        }
    }
}
