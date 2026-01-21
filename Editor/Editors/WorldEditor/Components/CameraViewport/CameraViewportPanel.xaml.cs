using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Rendering;

namespace Editor.Editors.WorldEditor.Components.CameraViewport
{
    /// <summary>
    /// Camera viewport panel that supports multiple camera views and layout modes.
    /// </summary>
    public partial class CameraViewportPanel : UserControl
    {
        public enum ViewportLayout
        {
            Single,
            SplitHorizontal,
            SplitVertical,
            Quad
        }

        private ViewportLayout _currentLayout = ViewportLayout.Single;
        private CameraHandle _activeCamera;
        private bool _showMainCameraPreview;
        private bool _isPreviewingCamera;
        private readonly List<CameraViewportInfo> _availableCameras = new List<CameraViewportInfo>();

        public CameraViewportPanel()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Subscribe to camera changes
            CameraService.Instance.ActiveCameraChanged += OnActiveCameraChanged;
            SelectionService.Instance.SelectionChanged += OnSelectionChanged;
            
            // Initialize with editor camera
            _activeCamera = CameraService.Instance.EditorCamera;
            RefreshCameraList();
            UpdateUI();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CameraService.Instance.ActiveCameraChanged -= OnActiveCameraChanged;
            SelectionService.Instance.SelectionChanged -= OnSelectionChanged;
        }


        /// <summary>
        /// Refresh the list of available cameras (game cameras only, no editor camera).
        /// </summary>
        public void RefreshCameraList()
        {
            _availableCameras.Clear();
            
            // Add Free Camera option (uses EditorCameraController)
            _availableCameras.Add(new CameraViewportInfo
            {
                Name = "Free Camera (Editor)",
                Handle = CameraHandle.Invalid, // Uses EditorCameraController, not engine camera
                IsEditorCamera = true,
                Type = CameraType.GameCamera
            });

            // Add game cameras from scene entities
            var scene = SceneService.Instance?.CurrentScene ?? Core.Data.ProjectData.Current?.ActiveScene;
            if (scene?.Entities != null)
            {
                foreach (var entity in scene.Entities)
                {
                    AddCamerasFromEntity(entity);
                }
            }

            // Update combo box
            CameraSelector.Items.Clear();
            foreach (var cam in _availableCameras)
            {
                var item = new ComboBoxItem
                {
                    Content = cam.IsEditorCamera ? cam.Name : $"{cam.Name} ({(cam.Type == CameraType.MainCamera ? "Main" : "Game")})",
                    Tag = cam,
                    Foreground = GetCameraTypeBrush(cam.Type)
                };
                CameraSelector.Items.Add(item);
            }

            if (CameraSelector.Items.Count > 0)
                CameraSelector.SelectedIndex = 0;
        }
        
        private void AddCamerasFromEntity(GameEntity entity)
        {
            var cam = entity.GetComponent<Camera>();
            if (cam != null)
            {
                _availableCameras.Add(new CameraViewportInfo
                {
                    Name = entity.Name,
                    Handle = CameraHandle.Invalid, // Will be resolved when selected
                    EntityId = entity.Id,
                    IsEditorCamera = false,
                    Type = cam.CameraType
                });
            }
            
            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    AddCamerasFromEntity(child);
                }
            }
        }

        private Brush GetCameraTypeBrush(CameraType type)
        {
            switch (type)
            {
                case CameraType.MainCamera:
                    return new SolidColorBrush(Color.FromRgb(155, 89, 182)); // Purple
                case CameraType.EditorCamera:
                    return new SolidColorBrush(Color.FromRgb(86, 156, 214)); // Blue
                default:
                    return new SolidColorBrush(Color.FromRgb(197, 197, 197)); // White
            }
        }

        private void UpdateUI()
        {
            // Update camera info display
            var camInfo = GetSelectedCameraInfo();
            if (camInfo != null)
            {
                ActiveCameraName.Text = camInfo.Name;
                ActiveCameraIcon.Foreground = GetCameraTypeBrush(camInfo.Type);

                if (camInfo.IsEditorCamera)
                {
                    // Show editor camera position from EditorCameraController
                    var controller = EditorCameraController.Instance;
                    CameraPositionText.Text = $" ({controller.PositionX:F1}, {controller.PositionY:F1}, {controller.PositionZ:F1})";
                }
                else if (camInfo.Handle.IsValid)
                {
                    var pos = VortexAPI.GetEngineCameraPosition(camInfo.Handle);
                    CameraPositionText.Text = $" ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})";
                }
            }

            // Update layout button states
            SingleViewBtn.Style = _currentLayout == ViewportLayout.Single 
                ? FindResource("ActiveViewportButton") as Style 
                : FindResource("ViewportButton") as Style;
            SplitHorizontalBtn.Style = _currentLayout == ViewportLayout.SplitHorizontal 
                ? FindResource("ActiveViewportButton") as Style 
                : FindResource("ViewportButton") as Style;
            QuadViewBtn.Style = _currentLayout == ViewportLayout.Quad 
                ? FindResource("ActiveViewportButton") as Style 
                : FindResource("ViewportButton") as Style;
        }

        private CameraViewportInfo GetSelectedCameraInfo()
        {
            if (CameraSelector.SelectedItem is ComboBoxItem item && item.Tag is CameraViewportInfo info)
                return info;
            return _availableCameras.Count > 0 ? _availableCameras[0] : null;
        }

        #region Event Handlers

        private void CameraSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var camInfo = GetSelectedCameraInfo();
            if (camInfo == null) return;
            
            if (camInfo.IsEditorCamera)
            {
                // Switch back to free camera (editor camera controller)
                _isPreviewingCamera = false;
                _activeCamera = CameraHandle.Invalid;
                CameraService.Instance.SwitchToEditorCamera();
            }
            else
            {
                // Switch to a game camera - enable preview mode
                _isPreviewingCamera = true;
                
                // Get the actual camera handle from CameraService
                var cameraHandle = CameraService.Instance.GetEntityCamera(camInfo.EntityId);
                
                if (!cameraHandle.IsValid)
                {
                    // Camera not yet registered - find entity and register it
                    var scene = SceneService.Instance?.CurrentScene ?? Core.Data.ProjectData.Current?.ActiveScene;
                    var entity = scene?.FindEntityById(camInfo.EntityId);
                    if (entity != null)
                    {
                        var cameraComponent = entity.GetComponent<Camera>();
                        var transformComponent = entity.GetComponent<ECS.Components.Transform>();
                        if (cameraComponent != null && transformComponent != null)
                        {
                            cameraHandle = CameraService.Instance.RegisterCamera(entity, cameraComponent);
                        }
                    }
                }
                
                if (cameraHandle.IsValid)
                {
                    _activeCamera = cameraHandle;
                    // Apply the camera (with its FOV, clip planes, etc.) to the renderer
                    CameraService.Instance.SetActiveCamera(cameraHandle);
                }
            }
            UpdateUI();
        }

        private void OnActiveCameraChanged(object sender, CameraHandle camera)
        {
            _activeCamera = camera;
            UpdateUI();
        }

        private void OnSelectionChanged(object sender, SelectionEventArgs e)
        {
            // Refresh camera list when selection changes
            RefreshCameraList();
            
            
            // If selected entity has a camera, show preview option
            if (e.SelectedEntity != null)
            {
                var camera = e.SelectedEntity.GetComponent<Camera>();
                if (camera != null)
                {
                    // Show PIP preview for any camera
                    _showMainCameraPreview = true;
                    CameraPreviewPip.Visibility = Visibility.Visible;
                }
                else
                {
                    _showMainCameraPreview = false;
                    CameraPreviewPip.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                _showMainCameraPreview = false;
                CameraPreviewPip.Visibility = Visibility.Collapsed;
            }
        }

        private void SingleView_Click(object sender, RoutedEventArgs e)
        {
            SetLayout(ViewportLayout.Single);
        }

        private void SplitHorizontal_Click(object sender, RoutedEventArgs e)
        {
            SetLayout(ViewportLayout.SplitHorizontal);
        }

        private void QuadView_Click(object sender, RoutedEventArgs e)
        {
            SetLayout(ViewportLayout.Quad);
        }

        private void InputSettings_Click(object sender, RoutedEventArgs e)
        {
            // Open input settings dialog
            var dialog = new InputSettingsDialog();
            dialog.Owner = Window.GetWindow(this);
            dialog.ShowDialog();
        }

        private void ClosePipPreview_Click(object sender, RoutedEventArgs e)
        {
            _showMainCameraPreview = false;
            CameraPreviewPip.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region Layout Management

        private void SetLayout(ViewportLayout layout)
        {
            _currentLayout = layout;
            UpdateViewportLayout();
            UpdateUI();
        }

        private void UpdateViewportLayout()
        {
            // Clear existing layout
            ViewportContainer.Children.Clear();
            ViewportContainer.RowDefinitions.Clear();
            ViewportContainer.ColumnDefinitions.Clear();

            switch (_currentLayout)
            {
                case ViewportLayout.Single:
                    CreateSingleViewport();
                    break;
                case ViewportLayout.SplitHorizontal:
                    CreateSplitHorizontalViewport();
                    break;
                case ViewportLayout.SplitVertical:
                    CreateSplitVerticalViewport();
                    break;
                case ViewportLayout.Quad:
                    CreateQuadViewport();
                    break;
            }
        }

        private void CreateSingleViewport()
        {
            ViewportContainer.Children.Add(MainViewport);
        }

        private void CreateSplitHorizontalViewport()
        {
            ViewportContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            ViewportContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var topViewport = CreateViewportBorder("Top View", "Editor Camera");
            var bottomViewport = CreateViewportBorder("Front View", "Editor Camera");

            Grid.SetRow(topViewport, 0);
            Grid.SetRow(bottomViewport, 1);

            ViewportContainer.Children.Add(topViewport);
            ViewportContainer.Children.Add(bottomViewport);
        }

        private void CreateSplitVerticalViewport()
        {
            ViewportContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ViewportContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var leftViewport = CreateViewportBorder("Left View", "Editor Camera");
            var rightViewport = CreateViewportBorder("Right View", "Editor Camera");

            Grid.SetColumn(leftViewport, 0);
            Grid.SetColumn(rightViewport, 1);

            ViewportContainer.Children.Add(leftViewport);
            ViewportContainer.Children.Add(rightViewport);
        }

        private void CreateQuadViewport()
        {
            ViewportContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            ViewportContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            ViewportContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ViewportContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var topLeft = CreateViewportBorder("Perspective", "Editor Camera");
            var topRight = CreateViewportBorder("Top", "Orthographic");
            var bottomLeft = CreateViewportBorder("Front", "Orthographic");
            var bottomRight = CreateViewportBorder("Right", "Orthographic");

            Grid.SetRow(topLeft, 0); Grid.SetColumn(topLeft, 0);
            Grid.SetRow(topRight, 0); Grid.SetColumn(topRight, 1);
            Grid.SetRow(bottomLeft, 1); Grid.SetColumn(bottomLeft, 0);
            Grid.SetRow(bottomRight, 1); Grid.SetColumn(bottomRight, 1);

            ViewportContainer.Children.Add(topLeft);
            ViewportContainer.Children.Add(topRight);
            ViewportContainer.Children.Add(bottomLeft);
            ViewportContainer.Children.Add(bottomRight);
        }

        private Border CreateViewportBorder(string viewName, string cameraName)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(10, 10, 10)),
                Margin = new Thickness(1)
            };

            var grid = new Grid();
            
            // Label overlay
            var labelBorder = new Border
            {
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
                Padding = new Thickness(8, 4, 8, 4)
            };

            var label = new TextBlock
            {
                Text = viewName,
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };

            labelBorder.Child = label;
            grid.Children.Add(labelBorder);
            border.Child = grid;

            return border;
        }

        #endregion

        /// <summary>
        /// Update the input mode indicator.
        /// </summary>
        public void SetInputMode(bool isActive, string modeName = "FLY MODE")
        {
            InputModeBadge.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
            InputModeText.Text = modeName;
        }

        /// <summary>
        /// Update viewport statistics.
        /// </summary>
        public void UpdateStats(int fps, int drawCalls)
        {
            ViewportStats.Text = $"FPS: {fps} | Draw: {drawCalls}";
        }

        /// <summary>
        /// Show the main camera preview PIP.
        /// </summary>
        public void ShowMainCameraPreview(bool show)
        {
            _showMainCameraPreview = show;
            CameraPreviewPip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Info about a camera available in the viewport.
    /// </summary>
    public class CameraViewportInfo
    {
        public string Name { get; set; }
        public CameraHandle Handle { get; set; }
        public Guid EntityId { get; set; }
        public bool IsEditorCamera { get; set; }
        public CameraType Type { get; set; }
    }
}
