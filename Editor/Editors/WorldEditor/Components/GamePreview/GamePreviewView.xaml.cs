using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.ECS;

namespace Editor.Editors.WorldEditor.Components.GamePreview
{
    /// <summary>
    /// Viewport for rendering the game scene in the Editor.
    /// Renders at 60 FPS (WPF native rate) for stability.
    /// For high FPS gameplay, use the Play button to open a separate Game Window.
    /// </summary>
    public partial class GamePreviewView : UserControl
    {
        private ViewportHost _host;
        private bool _isRendererInitialized;
        private Scene _currentScene;
        private DateTime _lastFrameTime = DateTime.Now;
        private EditorCameraController _cameraController;

        // Gizmo dragging state
        private bool _isDraggingGizmo;
        private GizmoAxis _activeGizmoAxis = GizmoAxis.None;
        private Point _lastDragPos;

        public GamePreviewView()
        {
            InitializeComponent();
            _host = (ViewportHost)FindName("ViewportHostElement");
            _cameraController = EditorCameraController.Instance;


            if (_host != null)
            {
                _host.OnHostCreated += OnHostCreated;
                _host.OnHostDestroying += OnHostDestroying;
                _host.OnViewportSizeChanged += OnHostSizeChanged;
            }

            // Camera input events - register on UserControl
            this.MouseDown += OnViewportMouseDown;
            this.MouseUp += OnViewportMouseUp;
            this.MouseMove += OnViewportMouseMove;
            this.MouseWheel += OnViewportMouseWheel;
            this.Focusable = true;
            this.KeyDown += OnViewportKeyDown;
            this.KeyUp += OnViewportKeyUp;







            Loaded += OnViewLoaded;
            Unloaded += OnViewUnloaded;
            
            // Use WPF CompositionTarget for rendering at 60 FPS (stable, no flicker)
            CompositionTarget.Rendering += OnCompositionTargetRendering;
        }

        private DateTime _lastStatusUpdate = DateTime.Now;

        /// <summary>
        /// Called by WPF at 60 FPS - submits scene data, updates camera, and renders.
        /// </summary>
        private void OnCompositionTargetRendering(object sender, EventArgs e)
        {
            if (!_isRendererInitialized) return;

            // Update camera
            var now = DateTime.Now;
            float deltaTime = (float)(now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;
            _cameraController.Update(deltaTime);

            // Submit scene data for rendering
            var sceneToRender = _currentScene ?? ProjectData.Current?.ActiveScene;
            if (sceneToRender != null)
            {
                SceneRenderService.Instance.SubmitScene(sceneToRender);
            }

            // Render the frame
            VortexAPI.RenderOnce();

            // Update status bar periodically
            if ((now - _lastStatusUpdate).TotalMilliseconds >= 500)
            {
                _lastStatusUpdate = now;
                UpdateStatusBar();
            }
        }

        private void OnViewLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnViewLoaded;

            
            // Sync toggle buttons with service state - use FindName since WPF generates these from XAML
            var gridToggle = FindName("GridToggleBtn") as ToggleButton;
            var gizmosToggle = FindName("GizmosToggleBtn") as ToggleButton;
            var snapToggle = FindName("SnapToggleBtn") as ToggleButton;
            
            if (gridToggle != null) gridToggle.IsChecked = EditorViewportService.Instance.IsGridVisible;
            if (gizmosToggle != null) gizmosToggle.IsChecked = EditorViewportService.Instance.AreGizmosVisible;
            if (snapToggle != null) snapToggle.IsChecked = EditorViewportService.Instance.SnapToGrid;
            
            // Subscribe to service changes
            EditorViewportService.Instance.GridVisibilityChanged += (s, visible) => 
            {
                var btn = FindName("GridToggleBtn") as ToggleButton;
                if (btn != null) btn.IsChecked = visible;
            };
            EditorViewportService.Instance.GizmosVisibilityChanged += (s, visible) => 
            {
                var btn = FindName("GizmosToggleBtn") as ToggleButton;
                if (btn != null) btn.IsChecked = visible;
            };
            EditorViewportService.Instance.SnapToGridChanged += (s, snap) => 
            {
                var btn = FindName("SnapToggleBtn") as ToggleButton;
                if (btn != null) btn.IsChecked = snap;
            };
        }

        /// <summary>
        /// Set the current scene to render.
        /// </summary>
        public Scene CurrentScene
        {
            get => _currentScene;
            set
            {
                if (_currentScene != value)
                {
                    _currentScene = value;
                    SceneRenderService.Instance.ClearAllRenderables();
                }
            }
        }

        #region Camera Input Handling

        private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
        {
            this.Focus();
            this.CaptureMouse();
            
            // Get position relative to the viewport host, not the entire control
            var pos = _host != null ? e.GetPosition(_host) : e.GetPosition(this);
            
            _cameraController.OnMouseDown(e, pos);
            
            // Left click - check for gizmo first, then entity picking
            if (e.LeftButton == MouseButtonState.Pressed && 
                !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) &&
                e.RightButton != MouseButtonState.Pressed &&
                e.MiddleButton != MouseButtonState.Pressed)
            {
                // Check if clicking on gizmo axis
                var selected = SelectionService.Instance.SelectedEntity;
                if (selected != null)
                {
                    var transform = selected.Transform;
                    if (transform != null && _host != null && _host.ActualWidth > 0 && _host.ActualHeight > 0)
                    {
                        float normalizedX = (float)(pos.X / _host.ActualWidth);
                        float normalizedY = (float)(pos.Y / _host.ActualHeight);
                        float aspectRatio = (float)(_host.ActualWidth / _host.ActualHeight);
                        
                        // Gizmo is positioned at object surface (top), not center!
                        float gizmoPosY = transform.LocalPosition.Y + transform.LocalScale.Y * 0.5f;
                        var gizmoPos = new Vector3f(transform.LocalPosition.X, 
                                                     gizmoPosY, 
                                                     transform.LocalPosition.Z);
                        
                        var axis = RaycastService.Instance.PickGizmoAxis(normalizedX, normalizedY, gizmoPos, aspectRatio, 1.0f);
                        if (axis != GizmoAxis.None)
                        {
                            _isDraggingGizmo = true;
                            _activeGizmoAxis = axis;
                            _lastDragPos = pos;
                            VortexAPI.IsDraggingGizmo = true;
                            VortexAPI.DraggingAxis = axis;
                            e.Handled = true;
                            return;
                        }
                    }
                }
                
                // No gizmo hit, try entity picking
                HandleEntityPicking(pos);
            }
            
            e.Handled = true;
        }

        private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
        {
            this.ReleaseMouseCapture();
            _cameraController.OnMouseUp(e);
            
            // End gizmo dragging
            if (_isDraggingGizmo)
            {
                _isDraggingGizmo = false;
                _activeGizmoAxis = GizmoAxis.None;
                VortexAPI.IsDraggingGizmo = false;
                VortexAPI.DraggingAxis = GizmoAxis.None;
            }
            
            e.Handled = true;
        }

        private void OnViewportMouseMove(object sender, MouseEventArgs e)
        {
            // Get position relative to the viewport host
            var pos = _host != null ? e.GetPosition(_host) : e.GetPosition(this);
            
            // Handle gizmo dragging
            if (_isDraggingGizmo && e.LeftButton == MouseButtonState.Pressed)
            {
                var selected = SelectionService.Instance.SelectedEntity;
                if (selected != null && selected.Transform != null && _host != null && _host.ActualWidth > 0 && _host.ActualHeight > 0)
                {
                    float normalizedX = (float)(pos.X / _host.ActualWidth);
                    float normalizedY = (float)(pos.Y / _host.ActualHeight);
                    float lastNormalizedX = (float)(_lastDragPos.X / _host.ActualWidth);
                    float lastNormalizedY = (float)(_lastDragPos.Y / _host.ActualHeight);
                    
                    var transform = selected.Transform;
                    var entityPos = new Vector3f(transform.LocalPosition.X, 
                                                  transform.LocalPosition.Y, 
                                                  transform.LocalPosition.Z);
                    
                    var delta = RaycastService.Instance.CalculateAxisDragDelta(
                        normalizedX, normalizedY, 
                        lastNormalizedX, lastNormalizedY,
                        entityPos, _activeGizmoAxis);
                    
                    // Apply delta based on current gizmo mode
                    var gizmoMode = TransformGizmoService.Instance.CurrentMode;
                    
                    switch (gizmoMode)
                    {
                        case TransformGizmoService.GizmoMode.Translate:
                            transform.LocalPosition = new ECS.Vector3(
                                transform.LocalPosition.X + delta.X,
                                transform.LocalPosition.Y + delta.Y,
                                transform.LocalPosition.Z + delta.Z);
                            break;
                            
                        case TransformGizmoService.GizmoMode.Rotate:
                            // Convert delta to rotation (degrees)
                            float rotSpeed = 50.0f;
                            float rotDelta = (delta.X + delta.Y + delta.Z) * rotSpeed;
                            switch (_activeGizmoAxis)
                            {
                                case GizmoAxis.X:
                                    transform.LocalRotation = new ECS.Vector3(
                                        transform.LocalRotation.X + rotDelta,
                                        transform.LocalRotation.Y,
                                        transform.LocalRotation.Z);
                                    break;
                                case GizmoAxis.Y:
                                    transform.LocalRotation = new ECS.Vector3(
                                        transform.LocalRotation.X,
                                        transform.LocalRotation.Y + rotDelta,
                                        transform.LocalRotation.Z);
                                    break;
                                case GizmoAxis.Z:
                                    transform.LocalRotation = new ECS.Vector3(
                                        transform.LocalRotation.X,
                                        transform.LocalRotation.Y,
                                        transform.LocalRotation.Z + rotDelta);
                                    break;
                            }
                            break;
                            
                        case TransformGizmoService.GizmoMode.Scale:
                            // Convert delta to scale
                            float scaleSpeed = 0.5f;
                            float scaleDelta = (delta.X + delta.Y + delta.Z) * scaleSpeed;
                            switch (_activeGizmoAxis)
                            {
                                case GizmoAxis.X:
                                    transform.LocalScale = new ECS.Vector3(
                                        Math.Max(0.01f, transform.LocalScale.X + scaleDelta),
                                        transform.LocalScale.Y,
                                        transform.LocalScale.Z);
                                    break;
                                case GizmoAxis.Y:
                                    transform.LocalScale = new ECS.Vector3(
                                        transform.LocalScale.X,
                                        Math.Max(0.01f, transform.LocalScale.Y + scaleDelta),
                                        transform.LocalScale.Z);
                                    break;
                                case GizmoAxis.Z:
                                    transform.LocalScale = new ECS.Vector3(
                                        transform.LocalScale.X,
                                        transform.LocalScale.Y,
                                        Math.Max(0.01f, transform.LocalScale.Z + scaleDelta));
                                    break;
                            }
                            break;
                    }
                    
                    // Notify inspector to update in real-time
                    SelectionService.Instance.NotifyTransformChanged();
                    
                    _lastDragPos = pos;
                }
            }
            else
            {
                // Update gizmo hover state
                UpdateGizmoHover(pos);
                
                _cameraController.OnMouseMove(pos);
            }
        }

        private void UpdateGizmoHover(Point pos)
        {
            if (_host == null || _host.ActualWidth <= 0 || _host.ActualHeight <= 0)
            {
                VortexAPI.HoveredAxis = GizmoAxis.None;
                return;
            }
            
            var selected = SelectionService.Instance.SelectedEntity;
            if (selected != null && selected.Transform != null)
            {
                float normalizedX = (float)(pos.X / _host.ActualWidth);
                float normalizedY = (float)(pos.Y / _host.ActualHeight);
                float aspectRatio = (float)(_host.ActualWidth / _host.ActualHeight);
                
                // Gizmo is positioned at object surface (top), not center!
                float gizmoPosY = selected.Transform.LocalPosition.Y + selected.Transform.LocalScale.Y * 0.5f;
                var gizmoPos = new Vector3f(
                    selected.Transform.LocalPosition.X,
                    gizmoPosY,
                    selected.Transform.LocalPosition.Z);
                
                var hoveredAxis = RaycastService.Instance.PickGizmoAxis(normalizedX, normalizedY, gizmoPos, aspectRatio, 1.0f);
                VortexAPI.HoveredAxis = hoveredAxis;
                VortexAPI.IsDraggingGizmo = _isDraggingGizmo;
                VortexAPI.DraggingAxis = _activeGizmoAxis;
            }
            else
            {
                VortexAPI.HoveredAxis = GizmoAxis.None;
                VortexAPI.IsDraggingGizmo = false;
                VortexAPI.DraggingAxis = GizmoAxis.None;
            }
        }

        private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
        {
            _cameraController.OnMouseWheel(e.Delta);
            e.Handled = true;
        }

        private void OnViewportKeyDown(object sender, KeyEventArgs e)
        {
            // Gizmo mode shortcuts (only when not in camera fly mode)
            if (!_cameraController.IsFlyMode)
            {
                switch (e.Key)
                {
                    case Key.W:
                        TransformGizmoService.Instance.SetTranslateMode();
                        e.Handled = true;
                        return;
                    case Key.E:
                        TransformGizmoService.Instance.SetRotateMode();
                        e.Handled = true;
                        return;
                    case Key.R:
                        TransformGizmoService.Instance.SetScaleMode();
                        e.Handled = true;
                        return;
                }
            }
            
            // Forward to camera controller for WASD movement
            _cameraController.OnKeyDown(e.Key);
        }

        private void OnViewportKeyUp(object sender, KeyEventArgs e)
        {
            _cameraController.OnKeyUp(e.Key);
        }

        private void HandleEntityPicking(Point screenPos)
        {
            if (_host == null || _host.ActualWidth <= 0 || _host.ActualHeight <= 0) return;

            // Normalize screen coordinates
            float normalizedX = (float)(screenPos.X / _host.ActualWidth);
            float normalizedY = (float)(screenPos.Y / _host.ActualHeight);
            float aspectRatio = (float)(_host.ActualWidth / _host.ActualHeight);

            // Get scene
            var scene = _currentScene ?? ProjectData.Current?.ActiveScene;
            if (scene == null) return;

            // Pick entity with correct aspect ratio
            var pickedEntity = RaycastService.Instance.PickEntity(normalizedX, normalizedY, scene, aspectRatio);

            if (pickedEntity != null)
            {
                SelectionService.Instance.Select(pickedEntity);
            }
            else if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                     !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                // Clear selection if clicking on empty space (without modifiers)
                SelectionService.Instance.ClearSelection();
            }
        }

        #endregion


        private void OnHostCreated(object sender, EventArgs e)
        {
            if (_host?.IsHandleValid == true)
            {
                var width = (uint)Math.Max(1, _host.ActualWidth);
                var height = (uint)Math.Max(1, _host.ActualHeight);

                if (VortexAPI.InitRenderViewport(_host.Handle, width, height))
                {
                    _isRendererInitialized = true;
                    SceneRenderService.Instance.Initialize();

                    // Initialize camera controller and apply to engine
                    _cameraController.Reset();

                    // Initialize grid visibility
                    EditorViewportService.Instance.IsGridVisible = true;
                    
                    // Initial status bar update
                    UpdateStatusBar();
                }
            }
        }

        private void OnHostDestroying(object sender, EventArgs e)
        {
            _isRendererInitialized = false;
            
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            
            // Now safe to shutdown the engine
            SceneRenderService.Instance.Shutdown();
            VortexAPI.ShutdownRender();
        }

        private void OnViewUnloaded(object sender, RoutedEventArgs e)
        {
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
        }

        private void OnHostSizeChanged(object sender, EventArgs e)
        {
            if (_host?.IsHandleValid == true && _isRendererInitialized)
            {
                var width = (uint)Math.Max(1, _host.ActualWidth);
                var height = (uint)Math.Max(1, _host.ActualHeight);
                VortexAPI.ResizeRender(width, height);
            }
        }

        private void UpdateStatusBar()
        {
            // Access named elements directly - they are auto-generated by XAML parser
            if (StatusText != null)
            {
                // Get stats from engine
                int fps = VortexAPI.CurrentFPS;
                int drawCalls = VortexAPI.DrawCalls;
                int vertices = VortexAPI.VertexCount;
                
                StatusText.Text = $"FPS: {fps} | Draw Calls: {drawCalls} | Vertices: {vertices}";
            }
            
            
            
            if (ResolutionText != null && _host != null)
            {
                int w = (int)_host.ActualWidth;
                int h = (int)_host.ActualHeight;
                ResolutionText.Text = $"{w} x {h}";
            }
        }

        /// <summary>
        /// Focus camera on a point.
        /// </summary>
        public void FocusOn(float x, float y, float z, float distance = 5.0f)
        {
            _cameraController.FocusOn(x, y, z, distance);
        }

        /// <summary>
        /// Gets the current FPS from the Engine.
        /// </summary>
        public int CurrentFps => VortexAPI.CurrentFPS;

        #region Keyboard Shortcuts

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            
            // Forward WASD to camera
            _cameraController.OnKeyDown(e.Key);

            switch (e.Key)
            {
                // Gizmo mode shortcuts
                case Key.W:
                    if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        TransformGizmoService.Instance.SetTranslateMode();
                        e.Handled = true;
                    }
                    break;

                case Key.E:
                    if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        TransformGizmoService.Instance.SetRotateMode();
                        e.Handled = true;
                    }
                    break;

                case Key.R:
                    if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        TransformGizmoService.Instance.SetScaleMode();
                        e.Handled = true;
                    }
                    break;

                // Toggle grid
                case Key.G:
                    if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        EditorViewportService.Instance.IsGridVisible = !EditorViewportService.Instance.IsGridVisible;
                        e.Handled = true;
                    }
                    break;

                // Focus on selected (F key)
                case Key.F:
                    if (!_cameraController.IsFlyMode)
                    {
                        FocusOnSelected();
                        e.Handled = true;
                    }
                    break;

                // Reset camera (Home key)
                case Key.Home:
                    _cameraController.Reset();
                    e.Handled = true;
                    break;

                // Toggle space (local/world)
                case Key.X:
                    if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        TransformGizmoService.Instance.ToggleSpace();
                        e.Handled = true;
                    }
                    break;
            }
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);
            _cameraController.OnKeyUp(e.Key);
        }

        private void FocusOnSelected()
        {
            var selectedEntity = SelectionService.Instance.SelectedEntity;
            if (selectedEntity?.Transform != null)
            {
                var pos = selectedEntity.Transform.LocalPosition;
                _cameraController.FocusOn(pos.X, pos.Y, pos.Z, 8.0f);
            }
        }

        #endregion

        #region Toolbar Event Handlers

        private void OnMoveToolClick(object sender, RoutedEventArgs e)
        {
            TransformGizmoService.Instance.SetTranslateMode();
        }

        private void OnRotateToolClick(object sender, RoutedEventArgs e)
        {
            TransformGizmoService.Instance.SetRotateMode();
        }

        private void OnScaleToolClick(object sender, RoutedEventArgs e)
        {
            TransformGizmoService.Instance.SetScaleMode();
        }

        private void OnGridToggle(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggle)
            {
                EditorViewportService.Instance.IsGridVisible = toggle.IsChecked == true;
            }
        }

        private void OnSnapToggle(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggle)
            {
                EditorViewportService.Instance.SnapToGrid = toggle.IsChecked == true;
            }
        }

        private void OnGizmosToggle(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggle)
            {
                EditorViewportService.Instance.AreGizmosVisible = toggle.IsChecked == true;
            }
        }

        #endregion
    }
}
