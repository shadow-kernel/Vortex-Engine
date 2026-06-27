using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.Core.Data;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.Editors.WorldEditor.DragDrop;

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
        private bool _isInitialized;

        // Gizmo dragging state
        private bool _isDraggingGizmo;
        private GizmoAxis _activeGizmoAxis = GizmoAxis.None;
        private Point _lastDragPos;

        // Drag and drop handler
        private ViewportDropHandler _dropHandler;

        // Game-mode mouse capture: while playing, the cursor is locked to the viewport + hidden and its
        // motion is fed to gameplay scripts (mouse-look). ESC frees it; clicking the viewport re-locks it.
        private bool _mouseCaptured;
        private bool _mouseJustCaptured;
        private bool _gameViewMode; // "Game" tab selected: clean view + placeholder until Play is pressed
        private ECS.Vector3 _extSnapPos; // frozen main-camera pose: the editor's placeholder while the game runs externally
        private ECS.Vector3 _extSnapRot;
        private bool IsPlaying => Editor.Core.Services.PlayModeService.Instance.State == Editor.Core.Services.PlayState.Playing;

        public GamePreviewView()
        {
            InitializeComponent();
            
            // Initialize camera controller FIRST before any event subscriptions
            _cameraController = EditorCameraController.Instance;
            if (_cameraController == null)
            {
                // Fallback - create a new instance if singleton not available
                System.Diagnostics.Debug.WriteLine("Warning: EditorCameraController.Instance was null, using fallback");
            }
            
            _host = (ViewportHost)FindName("ViewportHostElement");

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

            // Enable drag and drop
            this.AllowDrop = true;
            this.DragEnter += OnDragEnter;
            this.DragOver += OnDragOver;
            this.Drop += OnDrop;

            Loaded += OnViewLoaded;
            Unloaded += OnViewUnloaded;
            
            // PIP preview is now handled by CameraPreviewOverlay in WorldEditorView
            // No need to subscribe here - the overlay handles its own events
            
            // Use WPF CompositionTarget for rendering at 60 FPS (stable, no flicker)
            CompositionTarget.Rendering += OnCompositionTargetRendering;

            // While the standalone game window is playing, suspend this viewport so two
            // DX12 viewports don't fight over the single swapchain; reclaim it on Stop.
            Editor.Core.Services.PlayModeService.Instance.StateChanged += OnPlayModeStateChanged;
            Editor.Core.Services.PlayModeService.Instance.GameViewChanged += OnGameViewChanged;

            // Auto-frame newly created entities (Unity-style focus-on-create).
            SelectionService.Instance.FocusRequested += OnFocusRequested;
        }
        
        // Flag to prevent camera jumping when refreshing camera list
        private bool _isRefreshingCameraList;
        
        // Track last camera count to detect changes
        private int _lastCameraCount = -1;

        private DateTime _lastStatusUpdate = DateTime.Now;

        /// <summary>
        /// Called by WPF at 60 FPS - submits scene data, updates camera, and renders.
        /// </summary>
        private void OnCompositionTargetRendering(object sender, EventArgs e)
        {
            if (!_isRendererInitialized || _cameraController == null) return;

            // While playing, the game runs IN this viewport (rendered through the main camera). Only a
            // true Paused state halts the simulation; we still render every frame either way.
            bool playing = Editor.Core.Services.PlayModeService.Instance.State == Editor.Core.Services.PlayState.Playing;

            // Update camera
            var now = DateTime.Now;
            float deltaTime = (float)(now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;
            
            if (playing)
            {
                // Play mode: the GAME drives everything. The viewport view is the scene's MAIN CAMERA,
                // which a gameplay script may move (movement/look are game-side, NOT hardcoded here).
                // The actual view is applied below, after the scripts have run this frame.
            }
            // Game tab selected but not started yet: preview the static main-camera framing (the
            // "Press Play" placeholder sits on top). No fly camera here.
            else if (_gameViewMode)
            {
                ApplyMainCameraView();
                UpdateFlyModeIndicator(false);
            }
            // Edit mode (default): the build/fly camera — right-drag to look, WASD to fly around.
            else if (!CameraService.Instance.IsGameCameraActive)
            {
                _cameraController.Update(deltaTime);
                // Update fly mode indicator
                UpdateFlyModeIndicator(_cameraController.IsFlyMode);
            }
            else
            {
                // Apply the active game camera's view and projection (including FOV) every frame
                var activeCamera = CameraService.Instance.ActiveCamera;
                if (activeCamera.IsValid)
                {
                    VortexAPI.ApplyEngineCameraToRenderer(activeCamera);
                }
                UpdateFlyModeIndicator(false);
            }



            // Advance the running game one tick, then mirror the engine's (physics-updated) transforms
            // back into the C# transforms so the viewport shows the live simulation.
            if (playing)
            {
                bool external = Editor.Core.Services.PlayModeService.Instance.IsExternalWindow;
                // The external game window feeds mouse-look + sets its own camera; otherwise the editor does.
                if (!external)
                    UpdateGameMouseLook();                                 // lock/feed mouse delta to scripts; ESC frees
                VortexAPI.StepEngineRuntime(deltaTime);
                ReadbackPhysics();
                Editor.Scripting.ScriptRuntime.Instance.Update(deltaTime); // run gameplay scripts (movement, etc.)
                if (external)
                    Editor.Core.Services.PlayCameraHelper.ApplyPose(_extSnapPos, _extSnapRot); // editor = frozen placeholder
                else
                    ApplyMainCameraView();                                 // editor = live game view
            }

            // Submit scene data for rendering
            var sceneToRender = _currentScene ?? ProjectData.Current?.ActiveScene;
            if (sceneToRender != null)
            {
                SceneRenderService.Instance.SubmitScene(sceneToRender);
            }

            // Render the frame
            VortexAPI.RenderOnce();
            
            // Update secondary viewports (throttled internally)
            UpdateSecondaryViewports();
            
            // PIP preview is now handled by CameraPreviewOverlay

            // Update status bar periodically
            if ((now - _lastStatusUpdate).TotalMilliseconds >= 500)
            {
                _lastStatusUpdate = now;
                UpdateStatusBar();
                
                // Also check if camera list needs refresh (every 500ms)
                CheckAndRefreshCameraList(sceneToRender);
            }
        }
        
        /// <summary>
        /// Check if cameras have been added/removed and refresh the dropdown if needed.
        /// </summary>
        private void CheckAndRefreshCameraList(Scene scene)
        {
            if (scene?.Entities == null) return;
            
            int currentCameraCount = CountCamerasInScene(scene);
            if (currentCameraCount != _lastCameraCount)
            {
                _lastCameraCount = currentCameraCount;
                RefreshCameraList();
            }
        }
        
        private int CountCamerasInScene(Scene scene)
        {
            int count = 0;
            if (scene?.Entities == null) return 0;
            
            foreach (var entity in scene.Entities)
            {
                count += CountCamerasRecursive(entity);
            }
            return count;
        }
        
        private int CountCamerasRecursive(GameEntity entity)
        {
            int count = 0;
            if (entity.GetComponent<ECS.Components.Rendering.Camera>() != null)
                count++;
            
            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    count += CountCamerasRecursive(child);
                }
            }
            return count;
        }

        private void OnViewLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnViewLoaded;

            // Initial camera list refresh
            RefreshCameraList();
            
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
            
            // Subscribe to scene changes to refresh camera list
            SceneService.Instance.SceneLoaded += (s, scene) => RefreshCameraList();
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
                    _dropHandler = null; // Reset drop handler for new scene
                    SceneRenderService.Instance.ClearAllRenderables();
                }
            }
        }

        #region Camera Input Handling

        private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
        {
            // While playing, the viewport drives the GAME, not the editor camera/selection. A click
            // (re)locks the mouse to the game if ESC had freed it.
            if (IsPlaying)
            {
                this.Focus();
                if (!_mouseCaptured) CaptureGameMouse();
                e.Handled = true;
                return;
            }

            this.Focus();
            this.CaptureMouse();
            
            // Get position relative to the viewport host, not the entire control
            var pos = _host != null ? e.GetPosition(_host) : e.GetPosition(this);
            
            // Only allow camera movement when in Free Camera mode (not viewing through a game camera)
            if (!_isViewingThroughGameCamera)
            {
                _cameraController?.OnMouseDown(e, pos);
            }
            
            // Left click - check for gizmo first, then entity picking
            // Block all viewport interaction when viewing through a game camera
            if (e.LeftButton == MouseButtonState.Pressed && 
                !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) &&
                e.RightButton != MouseButtonState.Pressed &&
                e.MiddleButton != MouseButtonState.Pressed &&
                !_isViewingThroughGameCamera)  // Only allow selection in Free Camera mode
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
            if (IsPlaying) { e.Handled = true; return; } // game owns the mouse while playing
            this.ReleaseMouseCapture();
            
            // Only allow camera control when in Free Camera mode
            if (!_isViewingThroughGameCamera)
            {
                _cameraController?.OnMouseUp(e);
            }
            
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
            if (IsPlaying) return; // mouse-look is handled in UpdateGameMouseLook while playing
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
                
                // Only allow camera movement when in Free Camera mode
                if (!_isViewingThroughGameCamera)
                {
                    _cameraController?.OnMouseMove(pos);
                }
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
            // Only allow camera movement when in Free Camera mode
            if (!_isViewingThroughGameCamera)
            {
                _cameraController?.OnMouseWheel(e.Delta);
            }
            e.Handled = true;
        }

        private void OnViewportKeyDown(object sender, KeyEventArgs e)
        {
            // Gizmo mode shortcuts (only when not in camera fly mode AND in Free Camera mode)
            if (_cameraController != null && !_cameraController.IsFlyMode && !_isViewingThroughGameCamera)
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
            
            // Forward to camera controller for WASD movement (only in Free Camera mode)
            if (!_isViewingThroughGameCamera)
            {
                _cameraController?.OnKeyDown(e.Key);
            }
        }

        private void OnViewportKeyUp(object sender, KeyEventArgs e)
        {
            // Only forward key up when in Free Camera mode
            if (!_isViewingThroughGameCamera)
            {
                _cameraController?.OnKeyUp(e.Key);
            }
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

        #region Viewport Click Handlers
        
        private void OnMainViewportClick(object sender, MouseButtonEventArgs e)
        {
            SetActiveViewport(0);
        }
        
        private void OnSecondaryViewportClick(object sender, MouseButtonEventArgs e)
        {
            SetActiveViewport(1);
        }
        
        private void OnThirdViewportClick(object sender, MouseButtonEventArgs e)
        {
            SetActiveViewport(2);
        }
        
        private void OnFourthViewportClick(object sender, MouseButtonEventArgs e)
        {
            SetActiveViewport(3);
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
                    _isInitialized = true;
                    SceneRenderService.Instance.Initialize();

                    // Initialize camera controller and apply to engine
                    _cameraController?.Reset();

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
            SelectionService.Instance.FocusRequested -= OnFocusRequested;
        }

        /// <summary>
        /// When play stops, reclaim the DX12 viewport from the (closing) game window and
        /// resume editor rendering. Deferred to the dispatcher so it runs after the game
        /// window's ShutdownRender has completed in the same event cycle.
        /// </summary>
        private void OnPlayModeStateChanged(object sender, Editor.Core.Services.PlayState state)
        {
            // Play now runs IN this viewport (it reuses the editor's own swapchain — no teardown/re-init
            // and no second window). We only manage the play-simulation lifecycle here.
            if (state == Editor.Core.Services.PlayState.Playing)
            {
                BeginPlaySimulation();
                SetGameViewportLock(true);   // hide the viewport toolbar while the game runs
                VortexAPI.ShowGrid(false);   // the editor grid/gizmos are build aids — not the game
                VortexAPI.ShowGizmos(false);
                if (Editor.Core.Services.PlayModeService.Instance.IsExternalWindow)
                {
                    // The game runs in the external window. Freeze the editor viewport on the main
                    // camera's start pose so it shows a placeholder, NOT a second live render.
                    var t = Editor.Core.Services.PlayCameraHelper.FindMainCamera(_currentScene ?? ProjectData.Current?.ActiveScene);
                    if (t != null) { _extSnapPos = t.LocalPosition; _extSnapRot = t.LocalRotation; }
                }
                else
                {
                    CaptureGameMouse();      // in-viewport play: lock + hide cursor -> mouse-look (ESC frees)
                }
                UpdateGamePlaceholder();     // hide the "Press Play" overlay
                return;
            }
            if (state == Editor.Core.Services.PlayState.Paused) { ReleaseGameMouse(); UpdateGamePlaceholder(); return; }

            // Editing (Stopped): end the sim, free the mouse. Stay clean if we're still on the Game tab
            // (then the placeholder returns); otherwise restore the editor toolbar.
            ReleaseGameMouse();
            EndPlaySimulation();
            VortexAPI.ShowGrid(true);    // restore editor build aids
            VortexAPI.ShowGizmos(true);
            SetGameViewportLock(_gameViewMode);
            UpdateGamePlaceholder();
        }

        /// <summary>"Game" tab selected/cleared (independent of playing): hide the toolbar + show the
        /// "Press Play" placeholder; the viewport previews the static main-camera framing.</summary>
        private void OnGameViewChanged(bool active)
        {
            _gameViewMode = active;
            SetGameViewportLock(active || IsPlaying);
            UpdateGamePlaceholder();
        }

        private void UpdateGamePlaceholder()
        {
            // The viewport shows the static main-camera preview; the "Press Play" hint lives in the
            // status bar (a WPF overlay can't paint over the engine's child HWND — airspace).
            UpdateStatusBar();
            UpdateExternalBanner();
        }

        /// <summary>While the game runs in the external window, show the confined Row-0 banner "Läuft im
        /// externen Fenster" (part of the editor window — NOT a floating Popup, so it never covers the
        /// game window) and always stays visible while external play is active.</summary>
        private void UpdateExternalBanner()
        {
            if (ExternalBanner == null || ToolbarRow == null) return;
            var pms = Editor.Core.Services.PlayModeService.Instance;
            bool external = pms.IsExternalWindow && pms.IsPlaying;
            ExternalBanner.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
            if (external)
            {
                ToolbarRow.Height = new GridLength(34);            // show the banner row
                if (ViewportToolbar != null) ViewportToolbar.Visibility = Visibility.Collapsed;
            }
            // Non-external cases (edit toolbar / in-viewport play) are handled by SetGameViewportLock.
        }

        /// <summary>Game mouse-look + cursor lock. While captured the cursor is hidden and re-centered
        /// every frame; the per-frame motion is fed to gameplay scripts via Vortex.Input.MouseDeltaX/Y.
        /// ESC frees the cursor (control back to you); clicking the viewport re-locks it.</summary>
        private void UpdateGameMouseLook()
        {
            float dx = 0f, dy = 0f;
            if (_mouseCaptured)
            {
                if (Keyboard.IsKeyDown(Key.Escape)) { ReleaseGameMouse(); }
                else if (TryGetViewportCenter(out double cx, out double cy) && GetCursorPos(out POINTW p))
                {
                    if (_mouseJustCaptured)
                    {
                        // Skip the first frame: the cursor hasn't been re-centered yet, so its delta
                        // would be a huge jump that flings the camera.
                        _mouseJustCaptured = false;
                    }
                    else
                    {
                        dx = (float)(p.X - cx);
                        dy = (float)(p.Y - cy);
                        // Clamp absurd spikes (alt-tab, focus loss) so the view never whips around.
                        if (dx > 200f) dx = 200f; else if (dx < -200f) dx = -200f;
                        if (dy > 200f) dy = 200f; else if (dy < -200f) dy = -200f;
                    }
                    SetCursorPos((int)cx, (int)cy); // re-center so motion is continuous (FPS-style)
                }
            }
            Vortex.Input.MouseDeltaX = dx;
            Vortex.Input.MouseDeltaY = dy;
        }

        private void CaptureGameMouse()
        {
            if (_mouseCaptured || !IsLoaded) return;
            if (!TryGetViewportCenter(out double cx, out double cy)) return;
            _mouseCaptured = true;
            _mouseJustCaptured = true;
            Mouse.Capture(this);
            ShowCursor(false);
            SetCursorPos((int)cx, (int)cy);
        }

        private void ReleaseGameMouse()
        {
            if (!_mouseCaptured) return;
            _mouseCaptured = false;
            ShowCursor(true);
            if (Mouse.Captured == this) Mouse.Capture(null);
            Vortex.Input.MouseDeltaX = 0f;
            Vortex.Input.MouseDeltaY = 0f;
        }

        /// <summary>Screen-space center of the main viewport panel (physical pixels), for cursor lock.</summary>
        private bool TryGetViewportCenter(out double x, out double y)
        {
            x = y = 0;
            var el = MainViewportPanel;
            if (el == null || el.ActualWidth < 2 || el.ActualHeight < 2 || !el.IsVisible) return false;
            try
            {
                var c = el.PointToScreen(new Point(el.ActualWidth / 2.0, el.ActualHeight / 2.0));
                x = c.X; y = c.Y; return true;
            }
            catch { return false; }
        }

        /// <summary>Disable the viewport's camera-switch / split / PIP controls while the game runs,
        /// so you can't change cameras or split mid-play (re-enabled on Stop).</summary>
        private void SetGameViewportLock(bool locked)
        {
            // Game/Play mode: hide the entire editor viewport toolbar (tools, grid, gizmos, layout,
            // camera selector, PIP) + the corner label, so the player sees a clean game view.
            if (ViewportToolbar != null) ViewportToolbar.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
            if (ToolbarRow != null) ToolbarRow.Height = locked ? new GridLength(0) : new GridLength(28);
            if (MainViewportLabel != null) MainViewportLabel.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        }

        [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINTW p);
        [StructLayout(LayoutKind.Sequential)] private struct POINTW { public int X; public int Y; }

        // --- Play-mode physics simulation (engine-driven) ---
        private bool _simActive;
        private readonly System.Collections.Generic.List<(GameEntity ent, Vector3 start)> _physicsEntities
            = new System.Collections.Generic.List<(GameEntity, Vector3)>();

        /// <summary>On Play: register every Dynamic-Rigidbody entity with the engine physics tick and
        /// snapshot its start position so Stop can restore it.</summary>
        private void BeginPlaySimulation()
        {
            if (_simActive) return; // ignore Resume (Paused->Playing) so we don't re-snapshot mid-fall
            _simActive = true;
            _physicsEntities.Clear();
            VortexAPI.ClearAllRigidbodies();
            VortexAPI.ClearAllColliders();
            VortexAPI.ResetGameClock(); // start the game timer at 0 for this play session

            // Register scene geometry as solid static colliders / dynamic bodies (generic engine physics —
            // gravity, collision, "stand on it"). Gameplay itself stays in scripts.
            var scene = _currentScene ?? ProjectData.Current?.ActiveScene;
            if (scene?.Entities != null)
                foreach (var e in scene.Entities) RegisterPhysicsRecursive(e);

            // Make the game playable: if the player (main camera) has no script yet, attach the project's
            // PlayerController. The movement logic lives 100% in that editable project script (not the
            // engine) — this just scaffolds it so a freshly-loaded project is controllable on Play.
            EnsurePlayerControllerOnMainCamera(scene);

            // Compile + start the gameplay scripts (VortexBehaviour.Start on every Script component).
            // Player movement, camera control and all gameplay live in scripts — the editor only runs them.
            Editor.Scripting.ScriptRuntime.Instance.Begin(scene);
        }

        /// <summary>If the main camera has no Script component, write + attach the project's editable
        /// PlayerController so the game is controllable. Movement stays in the project script.</summary>
        private void EnsurePlayerControllerOnMainCamera(Scene scene)
        {
            var camEnt = FindMainCameraEntity(scene);
            if (camEnt == null) return;
            if (camEnt.GetComponent<Editor.ECS.Components.Scripting.Script>() != null) return; // already scripted
            var root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root)) return;
            var rel = Editor.Core.Services.ScriptingService.EnsurePlayerController(root);
            if (!string.IsNullOrEmpty(rel))
                camEnt.AddComponentDirect(new Editor.ECS.Components.Scripting.Script(camEnt, rel));
        }

        private GameEntity FindMainCameraEntity(Scene scene)
        {
            if (scene?.Entities == null) return null;
            foreach (var e in scene.Entities) { var r = FindMainCamEntityRec(e); if (r != null) return r; }
            return null;
        }

        private GameEntity FindMainCamEntityRec(GameEntity e)
        {
            if (e == null) return null;
            var cam = e.GetComponent<Editor.ECS.Components.Rendering.Camera>();
            if (cam != null && cam.IsMainCamera) return e;
            if (e.Children != null)
                foreach (var c in e.Children) { var r = FindMainCamEntityRec(c); if (r != null) return r; }
            return null;
        }

        private void RegisterPhysicsRecursive(GameEntity e)
        {
            if (e == null) return;

            var mr = e.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>();
            if (mr != null && e.Transform != null)
            {
                var s = e.Transform.LocalScale;
                var p = e.Transform.LocalPosition;
                // Half-extents from scale (primitives are ~unit-sized). Clamp so flat planes still
                // collide as a thin slab.
                float hx = Math.Max(0.05f, Math.Abs(s.X) * 0.5f);
                float hy = Math.Max(0.05f, Math.Abs(s.Y) * 0.5f);
                float hz = Math.Max(0.05f, Math.Abs(s.Z) * 0.5f);

                var rb = e.GetComponent<Editor.ECS.Components.Physics.Rigidbody>();
                if (rb != null
                    && rb.BodyType == Editor.ECS.Components.Physics.RigidbodyType.Dynamic
                    && Editor.Utilities.ID.IsValid(e.EntityId))
                {
                    _physicsEntities.Add((e, p));
                    VortexAPI.RegisterRigidbody(e.EntityId, rb.UseGravity, hx, hy, hz);
                }
                else
                {
                    // Static level geometry -> a solid collider you can stand on / can't pass through.
                    VortexAPI.AddStaticBox(p.X, p.Y, p.Z, hx, hy, hz);
                }
            }

            if (e.Children != null)
                foreach (var c in e.Children) RegisterPhysicsRecursive(c);
        }

        /// <summary>Each play frame: mirror the engine's physics-updated positions back to the C#
        /// transforms (display only — no write-back to the engine).</summary>
        private void ReadbackPhysics()
        {
            if (!_simActive) return;
            for (int i = 0; i < _physicsEntities.Count; i++)
            {
                var ent = _physicsEntities[i].ent;
                if (ent?.Transform == null || !Editor.Utilities.ID.IsValid(ent.EntityId)) continue;
                ent.Transform.SetLocalPositionFromEngine(VortexAPI.ReadEntityPosition(ent.EntityId));
            }
        }

        /// <summary>On Stop: clear the engine bodies and restore each entity's pre-play position.</summary>
        private void EndPlaySimulation()
        {
            if (!_simActive) return;
            _simActive = false;
            Editor.Scripting.ScriptRuntime.Instance.End(); // stop gameplay scripts (OnDestroy)
            VortexAPI.ClearAllRigidbodies();
            foreach (var (ent, start) in _physicsEntities)
            {
                if (ent?.Transform != null) ent.Transform.LocalPosition = start; // restores + re-syncs to engine
            }
            _physicsEntities.Clear();
            // The edit/fly camera is untouched during play (play renders through the main camera),
            // so on Stop the viewport simply resumes the build camera — nothing to restore.
        }

        /// <summary>
        /// Play-mode view: render through the scene's MAIN CAMERA using its CURRENT transform, so a
        /// gameplay script that moves the player/camera drives the viewport. There is no editor-side
        /// movement — that's the game's job (a script reads input and moves its entity).
        /// </summary>
        private void ApplyMainCameraView()
        {
            // Shared with the external game window so both render through the scene's main camera the
            // same way (pitch clamped to avoid a degenerate "langer Strich" look-at).
            Editor.Core.Services.PlayCameraHelper.ApplyMainCamera(_currentScene ?? ProjectData.Current?.ActiveScene);
        }

        private Editor.ECS.Components.Transform FindMainCameraTransform()
        {
            var scene = _currentScene ?? ProjectData.Current?.ActiveScene;
            if (scene?.Entities == null) return null;
            foreach (var e in scene.Entities)
            {
                var t = FindMainCamRecursive(e);
                if (t != null) return t;
            }
            return null;
        }

        private Editor.ECS.Components.Transform FindMainCamRecursive(GameEntity e)
        {
            if (e == null) return null;
            var cam = e.GetComponent<Editor.ECS.Components.Rendering.Camera>();
            if (cam != null && cam.IsMainCamera && e.Transform != null)
                return e.Transform;
            if (e.Children != null)
                foreach (var c in e.Children)
                {
                    var t = FindMainCamRecursive(c);
                    if (t != null) return t;
                }
            return null;
        }

        #region Drag and Drop

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            EnsureDropHandler();
            if (_dropHandler != null && _dropHandler.CanAcceptDrop(e.Data))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            EnsureDropHandler();
            if (_dropHandler != null && _dropHandler.CanAcceptDrop(e.Data))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            EnsureDropHandler();
            if (_dropHandler != null && _dropHandler.CanAcceptDrop(e.Data))
            {
                var dropPos = e.GetPosition(this);
                _dropHandler.HandleDrop(e.Data, dropPos);
            }
            e.Handled = true;
        }

        private void EnsureDropHandler()
        {
            if (_dropHandler == null && _currentScene != null)
            {
                _dropHandler = new ViewportDropHandler(_currentScene);
            }
        }

        #endregion

        private void OnHostSizeChanged(object sender, EventArgs e)
        {
            if (_host?.IsHandleValid == true && _isRendererInitialized)
            {
                var width = (uint)Math.Max(1, _host.ActualWidth);
                var height = (uint)Math.Max(1, _host.ActualHeight);
                VortexAPI.ResizeRender(width, height);
            }
            UpdateExternalBanner();
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
                
                string status = $"FPS: {fps} | Draw Calls: {drawCalls} | Vertices: {vertices}";
                if (Editor.Core.Services.PlayModeService.Instance.IsPlaying
                    && Editor.Core.Services.PlayModeService.Instance.IsExternalWindow)
                {
                    float t = VortexAPI.GameTime();
                    status += $"  |  ▶ {((int)t) / 60:00}:{((int)t) % 60:00}  ·  Spiel läuft im externen Fenster (ESC dort = Maus frei)";
                }
                else if (Editor.Core.Services.PlayModeService.Instance.IsPlaying)
                {
                    float t = VortexAPI.GameTime();
                    status += $"  |  ▶ {((int)t) / 60:00}:{((int)t) % 60:00}";
                    status += _mouseCaptured ? "  |  ESC: Maus frei" : "  |  Klick: Maus fangen";
                }
                else if (_gameViewMode)
                {
                    status += "  |  ▶ Press Play to start  ·  WASD move · Mouse look · Space jump · Ctrl crouch";
                }
                StatusText.Text = status;
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
            
            // Forward WASD to camera (only in Free Camera mode)
            if (!_isViewingThroughGameCamera)
            {
                _cameraController?.OnKeyDown(e.Key);
            }

            switch (e.Key)
            {
                // Gizmo mode shortcuts (only in Free Camera mode)
                case Key.W:
                    if (!_isViewingThroughGameCamera && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        TransformGizmoService.Instance.SetTranslateMode();
                        e.Handled = true;
                    }
                    break;

                case Key.E:
                    if (!_isViewingThroughGameCamera && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        TransformGizmoService.Instance.SetRotateMode();
                        e.Handled = true;
                    }
                    break;

                case Key.R:
                    if (!_isViewingThroughGameCamera && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
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

                // Focus on selected (F key) - only in Free Camera mode
                case Key.F:
                    if (!_isViewingThroughGameCamera && (_cameraController == null || !_cameraController.IsFlyMode))
                    {
                        FocusOnSelected();
                        e.Handled = true;
                    }
                    break;

                // Reset camera (Home key) - only in Free Camera mode
                case Key.Home:
                    if (!_isViewingThroughGameCamera)
                    {
                        _cameraController?.Reset();
                        e.Handled = true;
                    }
                    break;

                // Toggle space (local/world) - only in Free Camera mode
                case Key.X:
                    if (!_isViewingThroughGameCamera && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
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
            
            // Only forward key up when in Free Camera mode
            if (!_isViewingThroughGameCamera)
            {
                _cameraController?.OnKeyUp(e.Key);
            }
        }

        private void FocusOnSelected()
        {
            var selectedEntity = SelectionService.Instance.SelectedEntity;
            if (selectedEntity?.Transform != null)
            {
                var pos = selectedEntity.Transform.LocalPosition;
                _cameraController?.FocusOn(pos.X, pos.Y, pos.Z, 8.0f);
            }
        }

        /// <summary>
        /// Frame a just-created entity so it fills the view (Unity-style focus-on-create).
        /// Only moves the editor fly camera, never the game camera.
        /// </summary>
        private void OnFocusRequested(object sender, SelectionEventArgs e)
        {
            if (_isViewingThroughGameCamera) return;
            var t = e.SelectedEntity?.Transform;
            if (t != null)
            {
                var pos = t.LocalPosition;
                _cameraController?.FocusOn(pos.X, pos.Y, pos.Z, 8.0f);
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

        #region Viewport Layout

        public enum ViewportLayout { Single, SplitVertical, SplitHorizontal, Quad }
        private ViewportLayout _currentLayout = ViewportLayout.Single;
        private bool _isPlayMode;

        private void OnSingleViewClick(object sender, RoutedEventArgs e)
        {
            SetViewportLayout(ViewportLayout.Single);
        }

        private void OnSplitVerticalClick(object sender, RoutedEventArgs e)
        {
            SetViewportLayout(ViewportLayout.SplitVertical);
        }
        
        private void OnSplitHorizontalClick(object sender, RoutedEventArgs e)
        {
            SetViewportLayout(ViewportLayout.SplitHorizontal);
        }

        private void OnQuadViewClick(object sender, RoutedEventArgs e)
        {
            SetViewportLayout(ViewportLayout.Quad);
        }

        private void SetViewportLayout(ViewportLayout layout)
        {
            _currentLayout = layout;
            UpdateLayoutButtonStyles();
            ApplyViewportLayout();
        }
        
        /// <summary>
        /// Apply the current viewport layout by adjusting the viewport container.
        /// Note: Full multi-viewport requires multiple render targets in the engine.
        /// Apply the current viewport layout by adjusting the grid columns and rows.
        /// </summary>
        private void ApplyViewportLayout()
        {
            // Access the grid column/row definitions
            var mainColumn = MainViewportColumn;
            var splitColumn = SplitColumn;
            var topRow = TopViewportRow;
            var bottomRow = BottomViewportRow;
            
            switch (_currentLayout)
            {
                case ViewportLayout.Single:
                    // Single view - full size main viewport
                    if (splitColumn != null) splitColumn.Width = new GridLength(0);
                    if (bottomRow != null) bottomRow.Height = new GridLength(0);
                    if (MainViewportPanel != null) 
                    {
                        Grid.SetRowSpan(MainViewportPanel, 2);
                        Grid.SetColumnSpan(MainViewportPanel, 2);
                    }
                    
                    // Hide all secondary panels
                    if (SecondaryViewportPanel != null) SecondaryViewportPanel.Visibility = Visibility.Collapsed;
                    if (ThirdViewportPanel != null) ThirdViewportPanel.Visibility = Visibility.Collapsed;
                    if (FourthViewportPanel != null) FourthViewportPanel.Visibility = Visibility.Collapsed;
                    if (VerticalSplitter != null) VerticalSplitter.Visibility = Visibility.Collapsed;
                    
                    // Cleanup render targets when switching back to single view
                    CleanupSecondaryRenderTargets();
                    break;
                    
                case ViewportLayout.SplitVertical:
                    // Split vertical - two columns (Left/Right)
                    if (splitColumn != null) splitColumn.Width = new GridLength(1, GridUnitType.Star);
                    if (bottomRow != null) bottomRow.Height = new GridLength(0);
                    if (MainViewportPanel != null) 
                    {
                        Grid.SetRowSpan(MainViewportPanel, 2);
                        Grid.SetColumnSpan(MainViewportPanel, 1);
                    }
                    
                    // Show only secondary panel (right side)
                    if (SecondaryViewportPanel != null)
                    {
                        SecondaryViewportPanel.Visibility = Visibility.Visible;
                        Grid.SetRow(SecondaryViewportPanel, 0);
                        Grid.SetColumn(SecondaryViewportPanel, 1);
                        Grid.SetRowSpan(SecondaryViewportPanel, 2);
                        Grid.SetColumnSpan(SecondaryViewportPanel, 1);
                    }
                    if (ThirdViewportPanel != null) ThirdViewportPanel.Visibility = Visibility.Collapsed;
                    if (FourthViewportPanel != null) FourthViewportPanel.Visibility = Visibility.Collapsed;
                    if (VerticalSplitter != null) VerticalSplitter.Visibility = Visibility.Visible;
                    
                    // Initialize render targets and populate view selector
                    InitializeSecondaryRenderTargets();
                    RefreshSecondaryViewCameraList();
                    
                    // Force immediate placeholder render for secondary viewport
                    if (SecondaryViewportImage != null && _secondaryViewCamera == null)
                    {
                        RenderPlaceholderToImage(SecondaryViewportImage, ref _secondaryBitmap, "Select a camera");
                    }
                    break;
                    
                case ViewportLayout.SplitHorizontal:
                    // Split horizontal - two rows (Top/Bottom)
                    if (splitColumn != null) splitColumn.Width = new GridLength(0);
                    if (bottomRow != null) bottomRow.Height = new GridLength(1, GridUnitType.Star);
                    if (MainViewportPanel != null) 
                    {
                        Grid.SetRowSpan(MainViewportPanel, 1);
                        Grid.SetColumnSpan(MainViewportPanel, 2);
                    }
                    
                    // Use ThirdViewportPanel for bottom (since it's in Row 1)
                    if (ThirdViewportPanel != null)
                    {
                        ThirdViewportPanel.Visibility = Visibility.Visible;
                        Grid.SetRow(ThirdViewportPanel, 1);
                        Grid.SetColumn(ThirdViewportPanel, 0);
                        Grid.SetRowSpan(ThirdViewportPanel, 1);
                        Grid.SetColumnSpan(ThirdViewportPanel, 2);
                    }
                    if (SecondaryViewportPanel != null) SecondaryViewportPanel.Visibility = Visibility.Collapsed;
                    if (FourthViewportPanel != null) FourthViewportPanel.Visibility = Visibility.Collapsed;
                    if (VerticalSplitter != null) VerticalSplitter.Visibility = Visibility.Collapsed;
                    
                    // Initialize render targets and populate view selector
                    InitializeSecondaryRenderTargets();
                    RefreshThirdViewCameraList();
                    
                    // Force immediate placeholder render
                    if (ThirdViewportImage != null && _thirdViewCamera == null)
                    {
                        RenderPlaceholderToImage(ThirdViewportImage, ref _thirdBitmap, "Select a camera");
                    }
                    break;
                    
                case ViewportLayout.Quad:
                    // Quad view - 2x2 grid
                    if (splitColumn != null) splitColumn.Width = new GridLength(1, GridUnitType.Star);
                    if (bottomRow != null) bottomRow.Height = new GridLength(1, GridUnitType.Star);
                    if (MainViewportPanel != null) 
                    {
                        Grid.SetRowSpan(MainViewportPanel, 1);
                        Grid.SetColumnSpan(MainViewportPanel, 1);
                    }
                    
                    // Show all secondary panels in correct positions
                    if (SecondaryViewportPanel != null)
                    {
                        SecondaryViewportPanel.Visibility = Visibility.Visible;
                        Grid.SetRow(SecondaryViewportPanel, 0);
                        Grid.SetColumn(SecondaryViewportPanel, 1);
                        Grid.SetRowSpan(SecondaryViewportPanel, 1);
                        Grid.SetColumnSpan(SecondaryViewportPanel, 1);
                    }
                    if (ThirdViewportPanel != null) 
                    {
                        ThirdViewportPanel.Visibility = Visibility.Visible;
                        Grid.SetRow(ThirdViewportPanel, 1);
                        Grid.SetColumn(ThirdViewportPanel, 0);
                        Grid.SetRowSpan(ThirdViewportPanel, 1);
                        Grid.SetColumnSpan(ThirdViewportPanel, 1);
                    }
                    if (FourthViewportPanel != null) 
                    {
                        FourthViewportPanel.Visibility = Visibility.Visible;
                        Grid.SetRow(FourthViewportPanel, 1);
                        Grid.SetColumn(FourthViewportPanel, 1);
                        Grid.SetRowSpan(FourthViewportPanel, 1);
                        Grid.SetColumnSpan(FourthViewportPanel, 1);
                    }
                    if (VerticalSplitter != null) VerticalSplitter.Visibility = Visibility.Visible;
                    
                    // Initialize render targets and populate all view selectors
                    InitializeSecondaryRenderTargets();
                    RefreshSecondaryViewCameraList();
                    RefreshThirdViewCameraList();
                    RefreshFourthViewCameraList();
                    
                    // Force immediate placeholder renders for all viewports
                    if (SecondaryViewportImage != null && _secondaryViewCamera == null)
                    {
                        RenderPlaceholderToImage(SecondaryViewportImage, ref _secondaryBitmap, "Select a camera");
                    }
                    if (ThirdViewportImage != null && _thirdViewCamera == null)
                    {
                        RenderPlaceholderToImage(ThirdViewportImage, ref _thirdBitmap, "Select a camera");
                    }
                    if (FourthViewportImage != null && _fourthViewCamera == null)
                    {
                        RenderPlaceholderToImage(FourthViewportImage, ref _fourthBitmap, "Select a camera");
                    }
                    break;
            }
        }

        private void UpdateLayoutButtonStyles()
        {
            // Update button foreground colors based on selection
            var activeColor = new SolidColorBrush(Color.FromRgb(63, 169, 245)); // #3FA9F5
            var inactiveColor = new SolidColorBrush(Color.FromRgb(128, 128, 128)); // #808080

            if (SingleViewBtn != null)
                SingleViewBtn.Foreground = _currentLayout == ViewportLayout.Single ? activeColor : inactiveColor;
            if (SplitVerticalBtn != null)
                SplitVerticalBtn.Foreground = _currentLayout == ViewportLayout.SplitVertical ? activeColor : inactiveColor;
            if (SplitHorizontalBtn != null)
                SplitHorizontalBtn.Foreground = _currentLayout == ViewportLayout.SplitHorizontal ? activeColor : inactiveColor;
            if (QuadViewBtn != null)
                QuadViewBtn.Foreground = _currentLayout == ViewportLayout.Quad ? activeColor : inactiveColor;
        }

        #endregion

        #region Camera Selection & Preview
        
        /// <summary>
        /// Show/Hide the PIP camera preview.
        /// </summary>
        private void OnShowPipClick(object sender, RoutedEventArgs e)
        {
            // Find the first camera in the scene
            var scene = _currentScene ?? ProjectData.Current?.ActiveScene;
            if (scene?.Entities == null) return;
            
            // If a camera is already being previewed, toggle it off
            if (CameraPreviewService.Instance.CurrentPreviewCamera != null)
            {
                CameraPreviewService.Instance.ClosePreview();
                return;
            }
            
            // Find the first available camera
            var camera = FindFirstCamera(scene);
            if (camera != null)
            {
                CameraPreviewService.Instance.ShowPreview(camera);
            }
        }
        
        private ECS.GameEntity FindFirstCamera(Scene scene)
        {
            foreach (var entity in scene.Entities)
            {
                var result = FindCameraInHierarchy(entity);
                if (result != null) return result;
            }
            return null;
        }
        
        private ECS.GameEntity FindCameraInHierarchy(ECS.GameEntity entity)
        {
            if (entity.GetComponent<ECS.Components.Rendering.Camera>() != null)
                return entity;
            
            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    var result = FindCameraInHierarchy(child);
                    if (result != null) return result;
                }
            }
            return null;
        }
        
        // Saved Free Camera position for when we return from viewing through a game camera
        private float _savedFreeCamPosX, _savedFreeCamPosY, _savedFreeCamPosZ;
        private float _savedFreeCamYaw, _savedFreeCamPitch;
        private bool _hasSavedFreeCamPosition;
        
        // Saved grid visibility state
        private bool _savedGridVisibility;
        
        // Flag to indicate we're viewing through a game camera (no movement allowed)
        private bool _isViewingThroughGameCamera;

        private void OnCameraSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Skip if we're just refreshing the list programmatically
            if (_isRefreshingCameraList) return;
            
            if (CameraSelector.SelectedIndex > 0)
            {
                // User selected a game camera - switch viewport to that camera's view
                if (CameraSelector.SelectedItem is ComboBoxItem item && item.Tag is ECS.GameEntity cameraEntity)
                {
                    var camera = cameraEntity.GetComponent<ECS.Components.Rendering.Camera>();
                    if (camera != null)
                    {
                        // Save current Free Camera position before switching (only if not already viewing through a game camera)
                        if (!_isViewingThroughGameCamera && _cameraController != null)
                        {
                            _savedFreeCamPosX = _cameraController.PositionX;
                            _savedFreeCamPosY = _cameraController.PositionY;
                            _savedFreeCamPosZ = _cameraController.PositionZ;
                            _savedFreeCamYaw = _cameraController.Yaw;
                            _savedFreeCamPitch = _cameraController.Pitch;
                            _hasSavedFreeCamPosition = true;
                            
                            // Save and hide grid
                            _savedGridVisibility = EditorViewportService.Instance.IsGridVisible;
                        }
                        
                        // Get camera position and rotation
                        var pos = cameraEntity.Transform.LocalPosition;
                        var rot = cameraEntity.Transform.LocalRotation;
                        
                        // Use the new method that properly handles all 3 rotation axes (pitch, yaw, roll)
                        // Entity Euler: rot.X = pitch, rot.Y = yaw, rot.Z = roll
                        _cameraController?.SetFromEntityTransform(
                            pos.X, pos.Y, pos.Z,
                            rot.X, rot.Y, rot.Z);
                        
                        // Mark that we're viewing through a game camera (disable movement)
                        _isViewingThroughGameCamera = true;
                        
                        // Clear all selections - no gizmos should be visible when viewing through a game camera
                        SelectionService.Instance.ClearSelection();
                        
                        // Hide the grid when viewing through a game camera
                        EditorViewportService.Instance.IsGridVisible = false;
                        
                        // DON'T show PIP automatically - user must double-click or use context menu
                        // ShowCameraPreview(cameraEntity);
                        
                        // DON'T select the camera entity - we don't want the FOV gizmo to appear
                        // The user is just "looking through" this camera, not editing it
                    }
                }
            }
            else
            {
                // Free Camera selected - restore saved position and enable movement
                _isViewingThroughGameCamera = false;
                
                // Restore saved Free Camera position
                if (_hasSavedFreeCamPosition && _cameraController != null)
                {
                    _cameraController.SetPositionAndRotation(
                        _savedFreeCamPosX, _savedFreeCamPosY, _savedFreeCamPosZ,
                        _savedFreeCamYaw, _savedFreeCamPitch);
                    
                    // Restore grid visibility
                    EditorViewportService.Instance.IsGridVisible = _savedGridVisibility;
                }
                
                // PIP is now handled by CameraPreviewOverlay
                // Just close via the service
                CameraPreviewService.Instance.ClosePreview();
            }
        }

        /// <summary>
        /// Refresh the camera selector with all cameras in the scene.
        /// </summary>
        public void RefreshCameraList()
        {
            if (CameraSelector == null) return;
            
            // Prevent camera jumping while refreshing
            _isRefreshingCameraList = true;
            
            try
            {
                CameraSelector.Items.Clear();
                
                // Add free camera option
                var freeItem = new ComboBoxItem 
                { 
                    Content = "Free Camera",
                    Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128))
                };
                CameraSelector.Items.Add(freeItem);
                
                // Find all cameras in scene
                var scene = _currentScene ?? ProjectData.Current?.ActiveScene;
                if (scene?.Entities != null)
                {
                    foreach (var entity in scene.Entities)
                    {
                        AddCamerasFromEntity(entity);
                    }
                }
                
                CameraSelector.SelectedIndex = 0;
            }
            finally
            {
                _isRefreshingCameraList = false;
            }
        }

        private void AddCamerasFromEntity(ECS.GameEntity entity)
        {
            var camera = entity.GetComponent<ECS.Components.Rendering.Camera>();
            if (camera != null)
            {
                var isMain = camera.CameraType == ECS.Components.Rendering.CameraType.MainCamera;
                var item = new ComboBoxItem
                {
                    Content = $"{entity.Name} ({(isMain ? "Main" : "Game")})",
                    Tag = entity,
                    Foreground = isMain 
                        ? new SolidColorBrush(Color.FromRgb(155, 89, 182))  // Purple
                        : new SolidColorBrush(Color.FromRgb(86, 156, 214))   // Blue
                };
                CameraSelector.Items.Add(item);
            }
            
            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    AddCamerasFromEntity(child);
                }
            }
        }

        // PIP preview is now handled by CameraPreviewOverlay in WorldEditorView
        // These methods are kept for backwards compatibility but do nothing

        #endregion

        #region Play Mode & Input Forwarding

        /// <summary>
        /// Enter play mode - forward inputs to main camera.
        /// </summary>
        public void EnterPlayMode()
        {
            _isPlayMode = true;
            if (PlayModeBadge != null)
                PlayModeBadge.Visibility = Visibility.Visible;
            
            // Initialize input forwarding
            InputBindingsService.Instance.Initialize();
            InputBindingsService.Instance.EnableGameInputForwarding = true;
        }

        /// <summary>
        /// Exit play mode - return to editor controls.
        /// </summary>
        public void ExitPlayMode()
        {
            _isPlayMode = false;
            if (PlayModeBadge != null)
                PlayModeBadge.Visibility = Visibility.Collapsed;
            
            InputBindingsService.Instance.EnableGameInputForwarding = false;
        }

        /// <summary>
        /// Toggle play mode.
        /// </summary>
        public void TogglePlayMode()
        {
            if (_isPlayMode)
                ExitPlayMode();
            else
                EnterPlayMode();
        }

        /// <summary>
        /// Update fly mode indicator.
        /// </summary>
        public void UpdateFlyModeIndicator(bool isActive)
        {
            if (FlyModeBadge != null)
                FlyModeBadge.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region Secondary Viewport / Split View
        
        // Render target IDs for secondary viewports
        private uint _secondaryRenderTargetId;
        private uint _thirdRenderTargetId;
        private uint _fourthRenderTargetId;
        
        // WriteableBitmaps for displaying rendered content
        private WriteableBitmap _secondaryBitmap;
        private WriteableBitmap _thirdBitmap;
        private WriteableBitmap _fourthBitmap;
        
        // Currently selected camera for secondary views
        private ECS.GameEntity _secondaryViewCamera;
        private ECS.GameEntity _thirdViewCamera;
        private ECS.GameEntity _fourthViewCamera;
        
        // Active viewport tracking (0 = main, 1 = secondary, 2 = third, 3 = fourth)
        private int _activeViewportIndex = 0;
        
        // Render timing for passive viewports (66ms = ~15 FPS)
        private DateTime _lastPassiveRenderTime = DateTime.MinValue;
        private const int PassiveViewportRenderIntervalMs = 66;
        
        /// <summary>
        /// Handle view selection change in secondary viewport.
        /// </summary>
        private void OnSecondaryViewCameraChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SecondaryViewCameraSelector?.SelectedItem is ComboBoxItem item && item.Tag is ECS.GameEntity cameraEntity)
            {
                _secondaryViewCamera = cameraEntity;
                UpdateViewInfo(SecondaryViewCameraInfo, SecondaryViewDetails, SecondaryViewLabel, cameraEntity);
                // Hide overlay when camera is selected
                if (SecondaryViewportOverlayInfo != null)
                    SecondaryViewportOverlayInfo.Visibility = Visibility.Collapsed;
                
                // Force immediate GPU render of the camera view (viewport index 1)
                RenderViewportWithGPU(1, cameraEntity, SecondaryViewportImage);
            }
            else
            {
                _secondaryViewCamera = null;
                MultiViewportRenderService.Instance.SetViewportCamera(1, null);
                
                if (SecondaryViewCameraInfo != null)
                    SecondaryViewCameraInfo.Text = "Select a camera";
                if (SecondaryViewDetails != null)
                    SecondaryViewDetails.Text = "Use the dropdown above";
                if (SecondaryViewLabel != null)
                    SecondaryViewLabel.Text = "No Camera";
                // Show overlay when no camera
                if (SecondaryViewportOverlayInfo != null)
                    SecondaryViewportOverlayInfo.Visibility = Visibility.Visible;
                
                // Show placeholder image
                if (SecondaryViewportImage != null)
                {
                    RenderPlaceholderToImage(SecondaryViewportImage, ref _secondaryBitmap, "Select a camera");
                }
            }
        }
        
        /// <summary>
        /// Handle view selection change in third viewport.
        /// </summary>
        private void OnThirdViewCameraChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThirdViewCameraSelector?.SelectedItem is ComboBoxItem item && item.Tag is ECS.GameEntity cameraEntity)
            {
                _thirdViewCamera = cameraEntity;
                UpdateViewInfo(ThirdViewInfo, null, ThirdViewLabel, cameraEntity);
                if (ThirdViewportOverlayInfo != null)
                    ThirdViewportOverlayInfo.Visibility = Visibility.Collapsed;
                
                // Force immediate GPU render (viewport index 2)
                RenderViewportWithGPU(2, cameraEntity, ThirdViewportImage);
            }
            else
            {
                _thirdViewCamera = null;
                MultiViewportRenderService.Instance.SetViewportCamera(2, null);
                
                if (ThirdViewInfo != null)
                    ThirdViewInfo.Text = "Select a camera";
                if (ThirdViewLabel != null)
                    ThirdViewLabel.Text = "No Camera";
                if (ThirdViewportOverlayInfo != null)
                    ThirdViewportOverlayInfo.Visibility = Visibility.Visible;
                
                if (ThirdViewportImage != null)
                {
                    RenderPlaceholderToImage(ThirdViewportImage, ref _thirdBitmap, "Select a camera");
                }
            }
        }
        
        /// <summary>
        /// Handle view selection change in fourth viewport.
        /// </summary>
        private void OnFourthViewCameraChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FourthViewCameraSelector?.SelectedItem is ComboBoxItem item && item.Tag is ECS.GameEntity cameraEntity)
            {
                _fourthViewCamera = cameraEntity;
                UpdateViewInfo(FourthViewInfo, null, FourthViewLabel, cameraEntity);
                if (FourthViewportOverlayInfo != null)
                    FourthViewportOverlayInfo.Visibility = Visibility.Collapsed;
                
                // Force immediate GPU render (viewport index 3)
                RenderViewportWithGPU(3, cameraEntity, FourthViewportImage);
            }
            else
            {
                _fourthViewCamera = null;
                MultiViewportRenderService.Instance.SetViewportCamera(3, null);
                
                if (FourthViewInfo != null)
                    FourthViewInfo.Text = "Select a camera";
                if (FourthViewLabel != null)
                    FourthViewLabel.Text = "No Camera";
                if (FourthViewportOverlayInfo != null)
                    FourthViewportOverlayInfo.Visibility = Visibility.Visible;
                
                if (FourthViewportImage != null)
                {
                    RenderPlaceholderToImage(FourthViewportImage, ref _fourthBitmap, "Select a camera");
                }
            }
        }
        
        /// <summary>
        /// Update the view info display for a viewport with camera info.
        /// </summary>
        private void UpdateViewInfo(TextBlock infoBlock, TextBlock detailsBlock, TextBlock labelBlock, 
                                    ECS.GameEntity cameraEntity)
        {
            if (cameraEntity == null) return;
            
            var camera = cameraEntity.GetComponent<ECS.Components.Rendering.Camera>();
            if (camera != null)
            {
                if (labelBlock != null)
                    labelBlock.Text = cameraEntity.Name;
                
                if (infoBlock != null)
                {
                    var pos = cameraEntity.Transform?.LocalPosition ?? new ECS.Vector3(0, 0, 0);
                    infoBlock.Text = cameraEntity.Name;
                    if (detailsBlock != null)
                    {
                        detailsBlock.Text = $"FOV: {camera.FieldOfView:F0}� | Pos: ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})";
                    }
                }
            }
        }
        
        /// <summary>
        /// Close the secondary viewport and return to single view.
        /// </summary>
        private void OnCloseSecondaryView(object sender, RoutedEventArgs e)
        {
            _currentLayout = ViewportLayout.Single;
            UpdateLayoutButtonStyles();
            ApplyViewportLayout();
            CleanupSecondaryRenderTargets();
        }
        
        /// <summary>
        /// Populate the secondary view camera selector with scene cameras only.
        /// </summary>
        private void RefreshSecondaryViewCameraList()
        {
            PopulateCameraSelector(SecondaryViewCameraSelector);
        }
        
        /// <summary>
        /// Populate the third view camera selector.
        /// </summary>
        private void RefreshThirdViewCameraList()
        {
            PopulateCameraSelector(ThirdViewCameraSelector);
        }
        
        /// <summary>
        /// Populate the fourth view camera selector.
        /// </summary>
        private void RefreshFourthViewCameraList()
        {
            PopulateCameraSelector(FourthViewCameraSelector);
        }
        
        /// <summary>
        /// Helper to populate a view selector ComboBox with scene cameras only.
        /// </summary>
        private void PopulateCameraSelector(ComboBox selector)
        {
            if (selector == null) return;
            
            selector.Items.Clear();
            
            // Add placeholder
            var placeholderItem = new ComboBoxItem
            {
                Content = "Select Camera...",
                Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128))
            };
            selector.Items.Add(placeholderItem);
            
            // Add cameras from scene
            var scene = _currentScene ?? ProjectData.Current?.ActiveScene;
            if (scene?.Entities != null)
            {
                foreach (var entity in scene.Entities)
                {
                    AddCamerasToViewSelector(selector, entity);
                }
            }
            
            selector.SelectedIndex = 0;
        }
        
        private void AddCamerasToViewSelector(ComboBox selector, ECS.GameEntity entity)
        {
            var camera = entity.GetComponent<ECS.Components.Rendering.Camera>();
            if (camera != null)
            {
                var isMain = camera.CameraType == ECS.Components.Rendering.CameraType.MainCamera;
                var item = new ComboBoxItem
                {
                    Content = $"?? {entity.Name} ({(isMain ? "Main" : "Game")})",
                    Tag = entity,
                    Foreground = isMain
                        ? new SolidColorBrush(Color.FromRgb(155, 89, 182))
                        : new SolidColorBrush(Color.FromRgb(86, 156, 214))
                };
                selector.Items.Add(item);
            }
            
            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    AddCamerasToViewSelector(selector, child);
                }
            }
        }
        
        /// <summary>
        /// Create camera parameters from a game camera entity.
        /// </summary>
        private VortexAPI.ViewportCameraDesc CreateCameraFromEntity(ECS.GameEntity cameraEntity)
        {
            if (cameraEntity == null || cameraEntity.Transform == null)
            {
                // Return a default perspective camera
                return VortexAPI.ViewportCameraDesc.CreatePerspective(0, 5, -10, 0, 0, 0, 0, 1, 0, 60f);
            }
            
            var camera = cameraEntity.GetComponent<ECS.Components.Rendering.Camera>();
            var pos = cameraEntity.Transform.LocalPosition;
            var rot = cameraEntity.Transform.LocalRotation;
            
            // Calculate forward direction from rotation (Euler angles in degrees)
            float yaw = rot.Y * (float)Math.PI / 180f;
            float pitch = rot.X * (float)Math.PI / 180f;
            
            float forwardX = (float)(Math.Cos(pitch) * Math.Sin(yaw));
            float forwardY = (float)(-Math.Sin(pitch));
            float forwardZ = (float)(Math.Cos(pitch) * Math.Cos(yaw));
            
            // Target is position + forward
            float targetX = pos.X + forwardX;
            float targetY = pos.Y + forwardY;
            float targetZ = pos.Z + forwardZ;
            
            float fov = camera?.FieldOfView ?? 60f;
            float nearClip = camera?.NearClip ?? 0.1f;
            float farClip = camera?.FarClip ?? 1000f;
            
            return VortexAPI.ViewportCameraDesc.CreatePerspective(
                pos.X, pos.Y, pos.Z,
                targetX, targetY, targetZ,
                0, 1, 0,
                fov, nearClip, farClip);
        }
        
        /// <summary>
        /// Initialize render targets for secondary viewports using MultiViewportRenderService.
        /// Viewport indices: 0 = PIP, 1 = Secondary, 2 = Third, 3 = Fourth
        /// QUALITY: Using 960x540 for split viewports (16:9 aspect ratio)
        /// </summary>
        private void InitializeSecondaryRenderTargets()
        {
            // QUALITY: Higher resolution for sharper previews
            const int targetWidth = 960;
            const int targetHeight = 540;
            
            var service = MultiViewportRenderService.Instance;
            
            // Initialize based on layout
            switch (_currentLayout)
            {
                case ViewportLayout.SplitVertical:
                    // Only secondary viewport needed
                    if (!service.IsViewportReady(1))
                    {
                        service.InitializeViewport(1, targetWidth, targetHeight);
                    }
                    break;
                    
                case ViewportLayout.SplitHorizontal:
                    // Only third viewport needed (bottom)
                    if (!service.IsViewportReady(2))
                    {
                        service.InitializeViewport(2, targetWidth, targetHeight);
                    }
                    break;
                    
                case ViewportLayout.Quad:
                    // All three secondary viewports needed
                    if (!service.IsViewportReady(1))
                    {
                        service.InitializeViewport(1, targetWidth, targetHeight);
                    }
                    if (!service.IsViewportReady(2))
                    {
                        service.InitializeViewport(2, targetWidth, targetHeight);
                    }
                    if (!service.IsViewportReady(3))
                    {
                        service.InitializeViewport(3, targetWidth, targetHeight);
                    }
                    break;
            }
        }
        
        /// <summary>
        /// Cleanup render targets when closing split view.
        /// </summary>
        private void CleanupSecondaryRenderTargets()
        {
            var service = MultiViewportRenderService.Instance;
            
            // Shutdown secondary viewports (keep PIP - index 0)
            service.ShutdownViewport(1);
            service.ShutdownViewport(2);
            service.ShutdownViewport(3);
            
            // Also cleanup legacy resources if they exist
            if (_secondaryRenderTargetId > 0)
            {
                VortexAPI.DestroySecondaryRenderTarget(_secondaryRenderTargetId);
                _secondaryRenderTargetId = 0;
                _secondaryBitmap = null;
            }
            if (_thirdRenderTargetId > 0)
            {
                VortexAPI.DestroySecondaryRenderTarget(_thirdRenderTargetId);
                _thirdRenderTargetId = 0;
                _thirdBitmap = null;
            }
            if (_fourthRenderTargetId > 0)
            {
                VortexAPI.DestroySecondaryRenderTarget(_fourthRenderTargetId);
                _fourthRenderTargetId = 0;
                _fourthBitmap = null;
            }
            
            _secondaryViewCamera = null;
            _thirdViewCamera = null;
            _fourthViewCamera = null;
        }
        
        /// <summary>
        /// Render and update secondary viewports with GPU-accelerated camera views.
        /// Uses MultiViewportRenderService for proper GPU rendering.
        /// OPTIMIZED: Only renders when a camera is selected, no continuous placeholder rendering.
        /// </summary>
        private void UpdateSecondaryViewports()
        {
            if (_currentLayout == ViewportLayout.Single) return;
            
            // The MultiViewportRenderService handles its own throttling
            // Viewport indices: 0 = PIP, 1 = Secondary, 2 = Third, 3 = Fourth
            
            // Update secondary viewport (Split Vertical and Quad) - Index 1
            // ONLY render when camera is selected - placeholder is rendered once on camera change
            if (_currentLayout == ViewportLayout.SplitVertical || _currentLayout == ViewportLayout.Quad)
            {
                if (_secondaryViewCamera != null && SecondaryViewportImage != null)
                {
                    RenderViewportWithGPU(1, _secondaryViewCamera, SecondaryViewportImage);
                    if (SecondaryViewportOverlayInfo != null)
                        SecondaryViewportOverlayInfo.Visibility = Visibility.Collapsed;
                }
                // No else - placeholder is rendered once in OnSecondaryViewCameraChanged
            }
            
            // Update third viewport (Split Horizontal and Quad) - Index 2
            if (_currentLayout == ViewportLayout.SplitHorizontal || _currentLayout == ViewportLayout.Quad)
            {
                if (_thirdViewCamera != null && ThirdViewportImage != null)
                {
                    RenderViewportWithGPU(2, _thirdViewCamera, ThirdViewportImage);
                    if (ThirdViewportOverlayInfo != null)
                        ThirdViewportOverlayInfo.Visibility = Visibility.Collapsed;
                }
                // No else - placeholder is rendered once in OnThirdViewCameraChanged
            }
            
            // Update fourth viewport (Quad only) - Index 3
            if (_currentLayout == ViewportLayout.Quad)
            {
                if (_fourthViewCamera != null && FourthViewportImage != null)
                {
                    RenderViewportWithGPU(3, _fourthViewCamera, FourthViewportImage);
                    if (FourthViewportOverlayInfo != null)
                        FourthViewportOverlayInfo.Visibility = Visibility.Collapsed;
                }
                // No else - placeholder is rendered once in OnFourthViewCameraChanged
            }
        }
        
        /// <summary>
        /// Render a viewport using GPU acceleration via MultiViewportRenderService.
        /// Uses higher resolution for better quality.
        /// </summary>
        private void RenderViewportWithGPU(int viewportIndex, ECS.GameEntity cameraEntity, System.Windows.Controls.Image imageControl)
        {
            if (cameraEntity == null || imageControl == null) return;
            
            var service = MultiViewportRenderService.Instance;
            
            // Initialize viewport if not ready - QUALITY: Higher resolution
            if (!service.IsViewportReady(viewportIndex))
            {
                // PIP uses 480x270, split viewports use 960x540 (16:9 aspect ratio)
                int width = viewportIndex == 0 ? 480 : 960;
                int height = viewportIndex == 0 ? 270 : 540;
                service.InitializeViewport(viewportIndex, width, height);
            }
            
            // Set camera and render
            service.SetViewportCamera(viewportIndex, cameraEntity);
            service.RenderViewport(viewportIndex);
            
            // Update image source
            var bitmap = service.GetViewportBitmap(viewportIndex);
            if (bitmap != null && imageControl.Source != bitmap)
            {
                imageControl.Source = bitmap;
            }
        }
        
        /// <summary>
        /// Legacy method - now uses MultiViewportRenderService internally.
        /// </summary>
        private void RenderCameraToImage(ECS.GameEntity cameraEntity, System.Windows.Controls.Image imageControl, ref WriteableBitmap bitmap)
        {
            // Determine viewport index based on which image control this is
            int viewportIndex = 1; // Default to secondary
            if (imageControl == ThirdViewportImage) viewportIndex = 2;
            else if (imageControl == FourthViewportImage) viewportIndex = 3;
            
            RenderViewportWithGPU(viewportIndex, cameraEntity, imageControl);
        }
        
        /// <summary>
        /// Copy pixels from GPU render target to WriteableBitmap.
        /// </summary>
        private bool CopyRenderTargetToBitmap(uint targetId, WriteableBitmap bitmap)
        {
            if (bitmap == null || targetId == 0) return false;
            
            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = bitmap.BackBufferStride;
            
            try
            {
                // Read pixels from engine - returns pointer to pixel data
                uint outWidth, outHeight, outRowPitch;
                IntPtr pixelData = VortexAPI.ReadSecondaryRenderTargetPixels(targetId, out outWidth, out outHeight, out outRowPitch);
                
                if (pixelData == IntPtr.Zero) return false;
                
                bitmap.Lock();
                try
                {
                    // Copy pixel data row by row (handle different row pitches)
                    IntPtr destBuffer = bitmap.BackBuffer;
                    int copyWidth = Math.Min(width, (int)outWidth) * 4; // BGRA = 4 bytes
                    int srcStride = (int)outRowPitch;
                    int dstStride = stride;
                    
                    for (int y = 0; y < Math.Min(height, (int)outHeight); y++)
                    {
                        IntPtr srcRow = IntPtr.Add(pixelData, y * srcStride);
                        IntPtr dstRow = IntPtr.Add(destBuffer, y * dstStride);
                        
                        // Copy one row
                        for (int i = 0; i < copyWidth; i++)
                        {
                            Marshal.WriteByte(dstRow, i, Marshal.ReadByte(srcRow, i));
                        }
                    }
                    
                    bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                }
                finally
                {
                    bitmap.Unlock();
                }
                
                // Release the engine's pixel buffer
                VortexAPI.ReleaseSecondaryRenderTargetPixels(targetId);
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Render a simulated camera view when GPU rendering is not available.
        /// Shows a visual representation of what the camera would see.
        /// </summary>
        private void RenderSimulatedCameraView(WriteableBitmap bitmap, ECS.GameEntity cameraEntity, ECS.Components.Rendering.Camera camera)
        {
            if (bitmap == null) return;
            
            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            
            bitmap.Lock();
            try
            {
                IntPtr backBuffer = bitmap.BackBuffer;
                int stride = bitmap.BackBufferStride;
                
                // Dark blue-gray background (similar to main viewport)
                byte bgR = 26, bgG = 26, bgB = 30;
                
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int offset = y * stride + x * 4;
                        Marshal.WriteByte(backBuffer, offset + 0, bgB);
                        Marshal.WriteByte(backBuffer, offset + 1, bgG);
                        Marshal.WriteByte(backBuffer, offset + 2, bgR);
                        Marshal.WriteByte(backBuffer, offset + 3, 255);
                    }
                }
                
                // Draw grid pattern (similar to main viewport)
                byte gridColor = 40;
                int gridSpacing = 20;
                for (int y = 0; y < height; y += gridSpacing)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int offset = y * stride + x * 4;
                        Marshal.WriteByte(backBuffer, offset + 0, gridColor);
                        Marshal.WriteByte(backBuffer, offset + 1, gridColor);
                        Marshal.WriteByte(backBuffer, offset + 2, gridColor);
                    }
                }
                for (int x = 0; x < width; x += gridSpacing)
                {
                    for (int y = 0; y < height; y++)
                    {
                        int offset = y * stride + x * 4;
                        Marshal.WriteByte(backBuffer, offset + 0, gridColor);
                        Marshal.WriteByte(backBuffer, offset + 1, gridColor);
                        Marshal.WriteByte(backBuffer, offset + 2, gridColor);
                    }
                }
                
                // Draw scene objects as simple shapes from this camera's perspective
                var scene = _currentScene ?? ProjectData.Current?.ActiveScene;
                if (scene?.Entities != null && cameraEntity?.Transform != null)
                {
                    var camPos = cameraEntity.Transform.LocalPosition;
                    var camRot = cameraEntity.Transform.LocalRotation;
                    float fov = camera?.FieldOfView ?? 60f;
                    
                    foreach (var entity in scene.Entities)
                    {
                        DrawEntityInPreview(backBuffer, stride, width, height, entity, camPos, camRot, fov, cameraEntity);
                    }
                }
                
                // Draw crosshair at center
                int cx = width / 2;
                int cy = height / 2;
                DrawLine(backBuffer, stride, width, height, cx - 15, cy, cx - 5, cy, 180, 180, 180);
                DrawLine(backBuffer, stride, width, height, cx + 5, cy, cx + 15, cy, 180, 180, 180);
                DrawLine(backBuffer, stride, width, height, cx, cy - 15, cx, cy - 5, 180, 180, 180);
                DrawLine(backBuffer, stride, width, height, cx, cy + 5, cx, cy + 15, 180, 180, 180);
                
                // Draw camera frustum border
                DrawRect(backBuffer, stride, width, height, 0, 0, width, 2, 80, 80, 80); // Top
                DrawRect(backBuffer, stride, width, height, 0, height - 2, width, height, 80, 80, 80); // Bottom
                DrawRect(backBuffer, stride, width, height, 0, 0, 2, height, 80, 80, 80); // Left
                DrawRect(backBuffer, stride, width, height, width - 2, 0, width, height, 80, 80, 80); // Right
                
                bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
            finally
            {
                bitmap.Unlock();
            }
        }
        
        /// <summary>
        /// Draw an entity in the camera preview as a simple projected shape.
        /// </summary>
        private void DrawEntityInPreview(IntPtr buffer, int stride, int width, int height, 
            ECS.GameEntity entity, ECS.Vector3 camPos, ECS.Vector3 camRot, float fov, ECS.GameEntity cameraEntity)
        {
            if (entity == null || entity.Transform == null) return;
            if (entity == cameraEntity) return; // Don't draw the camera itself
            
            var pos = entity.Transform.LocalPosition;
            
            // Simple projection: transform world position to screen space
            // Vector from camera to entity
            float dx = pos.X - camPos.X;
            float dy = pos.Y - camPos.Y;
            float dz = pos.Z - camPos.Z;
            
            // Apply camera rotation (simplified - just yaw for now)
            float yawRad = camRot.Y * (float)Math.PI / 180f;
            float cosYaw = (float)Math.Cos(-yawRad);
            float sinYaw = (float)Math.Sin(-yawRad);
            
            float rx = dx * cosYaw - dz * sinYaw;
            float rz = dx * sinYaw + dz * cosYaw;
            float ry = dy;
            
            // Only draw if in front of camera
            if (rz <= 0.1f) return;
            
            // Project to screen
            float fovRad = fov * (float)Math.PI / 180f;
            float scale = (height / 2f) / (float)Math.Tan(fovRad / 2f);
            
            int screenX = width / 2 + (int)(rx * scale / rz);
            int screenY = height / 2 - (int)(ry * scale / rz);
            
            // Determine size based on distance
            float distance = (float)Math.Sqrt(rx * rx + ry * ry + rz * rz);
            int size = Math.Max(2, Math.Min(30, (int)(20f / distance * 5f)));
            
            // Choose color based on entity type
            byte r = 200, g = 200, b = 200;
            
            if (entity.GetComponent<ECS.Components.Rendering.Camera>() != null)
            {
                r = 86; g = 156; b = 214; // Blue for cameras
            }
            else if (entity.GetComponent<ECS.Components.Lighting.Light>() != null)
            {
                r = 255; g = 215; b = 0; // Gold for lights
            }
            else if (entity.GetComponent<ECS.Components.Rendering.MeshRenderer>() != null)
            {
                r = 180; g = 180; b = 180; // Gray for meshes
            }
            
            // Draw filled rectangle for the entity
            DrawRect(buffer, stride, width, height, 
                screenX - size/2, screenY - size/2, 
                screenX + size/2, screenY + size/2, 
                r, g, b);
            
            // Draw children recursively
            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    DrawEntityInPreview(buffer, stride, width, height, child, camPos, camRot, fov, cameraEntity);
                }
            }
        }
        
        /// <summary>
        /// Render a placeholder image when no camera is selected.
        /// Uses higher resolution for quality.
        /// </summary>
        private void RenderPlaceholderToImage(System.Windows.Controls.Image imageControl, ref WriteableBitmap bitmap, string message)
        {
            if (imageControl == null) return;
            
            // QUALITY: Use same resolution as viewports (960x540)
            const int targetWidth = 960;
            const int targetHeight = 540;
            
            if (bitmap == null || bitmap.PixelWidth != targetWidth || bitmap.PixelHeight != targetHeight)
            {
                bitmap = new WriteableBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Bgra32, null);
            }
            
            // Always ensure the image source is set
            if (imageControl.Source != bitmap)
            {
                imageControl.Source = bitmap;
            }
            
            bitmap.Lock();
            try
            {
                IntPtr backBuffer = bitmap.BackBuffer;
                int stride = bitmap.BackBufferStride;
                
                // Dark gray background with subtle grid pattern
                for (int y = 0; y < targetHeight; y++)
                {
                    for (int x = 0; x < targetWidth; x++)
                    {
                        int offset = y * stride + x * 4;
                        
                        // Grid pattern instead of checkerboard
                        bool isGridLine = (x % 40 == 0) || (y % 40 == 0);
                        byte gray = isGridLine ? (byte)35 : (byte)26;
                        
                        Marshal.WriteByte(backBuffer, offset + 0, gray);
                        Marshal.WriteByte(backBuffer, offset + 1, gray);
                        Marshal.WriteByte(backBuffer, offset + 2, gray);
                        Marshal.WriteByte(backBuffer, offset + 3, 255);
                    }
                }
                
                // Draw camera icon in center
                int cx = targetWidth / 2;
                int cy = targetHeight / 2;
                
                // Camera body
                DrawRect(backBuffer, stride, targetWidth, targetHeight, cx - 35, cy - 25, cx + 25, cy + 25, 60, 60, 60);
                // Camera lens
                DrawRect(backBuffer, stride, targetWidth, targetHeight, cx + 25, cy - 15, cx + 45, cy + 15, 80, 80, 80);
                // Viewfinder
                DrawRect(backBuffer, stride, targetWidth, targetHeight, cx - 25, cy - 35, cx - 5, cy - 25, 50, 50, 50);
                
                // Draw "Select Camera" text area
                DrawRect(backBuffer, stride, targetWidth, targetHeight, cx - 80, cy + 40, cx + 80, cy + 60, 40, 40, 40);
                
                bitmap.AddDirtyRect(new Int32Rect(0, 0, targetWidth, targetHeight));
            }
            finally
            {
                bitmap.Unlock();
            }
        }
        
        /// <summary>
        /// Helper to draw a line on the bitmap.
        /// </summary>
        private void DrawLine(IntPtr buffer, int stride, int width, int height, int x1, int y1, int x2, int y2, byte r, byte g, byte b)
        {
            int dx = Math.Abs(x2 - x1);
            int dy = Math.Abs(y2 - y1);
            int sx = x1 < x2 ? 1 : -1;
            int sy = y1 < y2 ? 1 : -1;
            int err = dx - dy;
            
            while (true)
            {
                if (x1 >= 0 && x1 < width && y1 >= 0 && y1 < height)
                {
                    int offset = y1 * stride + x1 * 4;
                    Marshal.WriteByte(buffer, offset + 0, b);
                    Marshal.WriteByte(buffer, offset + 1, g);
                    Marshal.WriteByte(buffer, offset + 2, r);
                    Marshal.WriteByte(buffer, offset + 3, 255);
                }
                
                if (x1 == x2 && y1 == y2) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x1 += sx; }
                if (e2 < dx) { err += dx; y1 += sy; }
            }
        }
        
        /// <summary>
        /// Helper to draw a filled rectangle on the bitmap.
        /// </summary>
        private void DrawRect(IntPtr buffer, int stride, int width, int height, int x1, int y1, int x2, int y2, byte r, byte g, byte b)
        {
            for (int y = Math.Max(0, y1); y < Math.Min(height, y2); y++)
            {
                for (int x = Math.Max(0, x1); x < Math.Min(width, x2); x++)
                {
                    int offset = y * stride + x * 4;
                    Marshal.WriteByte(buffer, offset + 0, b);
                    Marshal.WriteByte(buffer, offset + 1, g);
                    Marshal.WriteByte(buffer, offset + 2, r);
                    Marshal.WriteByte(buffer, offset + 3, 255);
                }
            }
        }
        
        /// <summary>
        /// Copy rendered pixels from GPU render target to WPF Image control.
        /// </summary>
        private void CopyRenderTargetToImage(uint targetId, System.Windows.Controls.Image imageControl, ref WriteableBitmap bitmap)
        {
            if (imageControl == null || targetId == 0) return;
            
            const int testWidth = 512;
            const int testHeight = 384;
            
            // Create bitmap if needed
            if (bitmap == null || bitmap.PixelWidth != testWidth || bitmap.PixelHeight != testHeight)
            {
                bitmap = new WriteableBitmap(testWidth, testHeight, 96, 96, PixelFormats.Bgra32, null);
                imageControl.Source = bitmap;
            }
            
            // First test: Generate a test pattern to verify WPF pipeline works
            bitmap.Lock();
            try
            {
                int stride = bitmap.BackBufferStride;
                IntPtr backBuffer = bitmap.BackBuffer;
                
                // Generate a gradient test pattern
                for (int y = 0; y < testHeight; y++)
                {
                    for (int x = 0; x < testWidth; x++)
                    {
                        int offset = y * stride + x * 4;
                        byte b = (byte)(x * 255 / testWidth);  // Blue gradient horizontal
                        byte g = (byte)(y * 255 / testHeight); // Green gradient vertical
                        byte r = 128; // Fixed red
                        byte a = 255;
                        
                        Marshal.WriteByte(backBuffer, offset + 0, b);     // B
                        Marshal.WriteByte(backBuffer, offset + 1, g);     // G
                        Marshal.WriteByte(backBuffer, offset + 2, r);     // R
                        Marshal.WriteByte(backBuffer, offset + 3, a);     // A
                    }
                }
                
                bitmap.AddDirtyRect(new Int32Rect(0, 0, testWidth, testHeight));
            }
            finally
            {
                bitmap.Unlock();
            }
        }
        
        /// <summary>
        /// Set the active viewport when user clicks into it.
        /// </summary>
        private void SetActiveViewport(int viewportIndex)
        {
            _activeViewportIndex = viewportIndex;
            
            // Update visual indicators for active viewport
            UpdateActiveViewportIndicators();
        }
        
        /// <summary>
        /// Update visual indicators to show which viewport is active.
        /// </summary>
        private void UpdateActiveViewportIndicators()
        {
            var activeBorderColor = new SolidColorBrush(Color.FromRgb(63, 169, 245)); // Blue
            var inactiveBorderColor = new SolidColorBrush(Color.FromRgb(54, 54, 64)); // subtle #363640

            // In single-viewport mode there is only one view, so a bright active frame is
            // pointless clutter — keep the subtle dark border for a clean look. The blue
            // active indicator is only meaningful when multiple viewports are visible.
            bool multi = _currentLayout != ViewportLayout.Single;

            if (MainViewportPanel != null)
            {
                bool active = multi && _activeViewportIndex == 0;
                MainViewportPanel.BorderBrush = active ? activeBorderColor : inactiveBorderColor;
                MainViewportPanel.BorderThickness = new Thickness(1);
            }
            if (SecondaryViewportPanel != null)
            {
                SecondaryViewportPanel.BorderBrush = _activeViewportIndex == 1 ? activeBorderColor : inactiveBorderColor;
                SecondaryViewportPanel.BorderThickness = _activeViewportIndex == 1 ? new Thickness(2) : new Thickness(1);
            }
            if (ThirdViewportPanel != null)
            {
                ThirdViewportPanel.BorderBrush = _activeViewportIndex == 2 ? activeBorderColor : inactiveBorderColor;
                ThirdViewportPanel.BorderThickness = _activeViewportIndex == 2 ? new Thickness(2) : new Thickness(1);
            }
            if (FourthViewportPanel != null)
            {
                FourthViewportPanel.BorderBrush = _activeViewportIndex == 3 ? activeBorderColor : inactiveBorderColor;
                FourthViewportPanel.BorderThickness = _activeViewportIndex == 3 ? new Thickness(2) : new Thickness(1);
            }
        }

        #endregion
    }
}
