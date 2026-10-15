using System;
using System.Collections.Generic;
using System.IO;
using Editor.Core.Data;
using Editor.Core.Editing;
using Editor.Core.Input;
using Editor.Core.Services;
using Editor.Core.UndoRedo;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.Scripting;
using Editor.UI.Vui;

namespace Editor.Core.Viewport
{
    /// <summary>
    /// The editor's main 3D viewport, independent of any UI framework: owns the native render surface, the
    /// per-frame tick (camera → simulation → scene submit → render), selection picking, gizmo dragging, the
    /// in-viewport play mode (non-destructive snapshot/restore, physics registration, mouse-look, retained UI
    /// feed, debug freecam) and the keyboard shortcuts. The UI shell forwards neutral pointer/key events and
    /// calls <see cref="Tick"/> once per frame. (Port of the WPF GamePreviewView logic.)
    /// </summary>
    public sealed class EditorViewportSession
    {
        public static EditorViewportSession Main { get; private set; }

        private IViewportHost _host;
        private bool _initialized;
        private readonly EditorCameraController _camera = EditorCameraController.Instance;
        private Scene _currentScene;
        private object _submittedScene;
        private volatile bool _sceneDirty = true;
        private int _resubmitHoldFrames;
        private bool _gameViewMode;
        private bool _viewingThroughGameCamera;
        private DateTime _lastFrameTime = DateTime.Now;
        private DateTime _lastStatusUpdate = DateTime.MinValue;
        private uint _pixelW, _pixelH;

        // pointer state (logical viewport coordinates, from the shell's events)
        private double _pointerX, _pointerY;
        private bool _lmbDown, _rmbDown;

        // gizmo drag
        private bool _isDraggingGizmo;
        private GizmoAxis _activeGizmoAxis = GizmoAxis.None;
        private double _lastDragX, _lastDragY;
        private Vector3 _dragRawPos, _dragRawRot, _dragRawScale;

        // play-mode simulation (non-destructive)
        private bool _simActive;
        private readonly List<(GameEntity ent, Vector3 start)> _physicsEntities = new List<(GameEntity, Vector3)>();
        private readonly List<(GameEntity ent, Vector3 pos, Vector3 rot, Vector3 scale)> _transformSnapshot = new List<(GameEntity, Vector3, Vector3, Vector3)>();
        private readonly List<(GameEntity ent, float r, float g, float b, float a)> _colorSnapshot = new List<(GameEntity, float, float, float, float)>();
        private PlaySnapshot _componentSnapshot;   // lights a script changed (flicker, flashlight)
        private float _snapCamX, _snapCamY, _snapCamZ, _snapCamYaw, _snapCamPitch, _snapCamRoll;
        private bool _hasCamSnap;
        private Vector3 _extSnapPos, _extSnapRot;

        // game mouse-look
        private bool _mouseCaptured, _mouseJustCaptured, _userUnlockedCursor;
        private bool _cursorHidden;

        // editor-play UI feed
        private bool _playGridHidden, _savedGrid = true, _lmbPrevEd;
        private float _editorFovApplied = float.NaN;
        private float _uiMx, _uiMy; private bool _uiDown, _uiPressed;
        private float _wheelAccum;   // wheel notches since the last script tick (#319)
        private static readonly char[] _vuiCharsEmpty = new char[0];
        private static readonly int[] _navVks = { 0x25, 0x26, 0x27, 0x28, 0x09, 0x0D, 0x20 };
        private readonly bool[] _navPrev = new bool[7];
        private readonly int[] _uiKeys = new int[8];
        private int _uiKeyCount;

        // debug freecam (P) during editor play
        private readonly DebugFreeCamera _debugCam = new DebugFreeCamera();

        /// <summary>Modal preview dialogs that own the shared render queue (viewport pauses while &gt; 0).</summary>
        public static int ActivePreviewDialogs;
        /// <summary>Modeless previews that coexist with the viewport (scene re-submitted every frame while &gt; 0).</summary>
        public static int ActiveCoexistPreviews;

        public event Action<string, string> StatusUpdated;     // (status line, "W x H")
        /// <summary>Split/quad layouts active: secondary views render through readback targets on their own timers.</summary>
        public bool SecondaryViewsActive { get; set; }
        /// <summary>A retained-UI screen the UI editor wants drawn over the edit-mode viewport (null = none).</summary>
        public VuiCanvas PreviewCanvas { get; set; }
        public event Action<bool> FlyModeChanged;
        public event Action<bool> CursorCaptureChanged;
        /// <summary>The camera the viewport looks through changed (null: the free editor camera) — the toolbar's camera
        /// picker follows it, also when a tool switched back to the editor camera (#444).</summary>
        public event Action<GameEntity> ViewThroughChanged;

        public bool IsInitialized => _initialized;
        public bool IsPlaying => PlayModeService.Instance.IsPlaying;
        public bool IsFlyMode => _camera.IsFlyMode;
        public bool IsGameViewMode => _gameViewMode;
        public bool IsViewingThroughGameCamera => _viewingThroughGameCamera;
        public bool IsMouseCaptured => _mouseCaptured;
        public EditorCameraController Camera => _camera;
        public int CurrentFps => VortexAPI.CurrentFPS;

        /// <summary>What the viewport renders through (#444).</summary>
        public enum ViewSource
        {
            /// <summary>The free editor camera (<see cref="Camera"/>).</summary>
            EditorCamera,
            /// <summary>Edit mode in the Game view: the scene's main camera, re-applied every frame.</summary>
            GameView,
            /// <summary>An engine camera chosen in <see cref="CameraService"/> (a scene camera looked through).</summary>
            SceneCamera,
            /// <summary>Play: the game drives the view.</summary>
            Game,
        }

        /// <summary>The camera the next frame renders through — the same choice <see cref="Tick"/> makes, so a tool can say
        /// what an image shows instead of assuming the editor camera (#444). The Game view of a scene without a main
        /// camera leaves the editor camera's view in place.</summary>
        public ViewSource CurrentView
        {
            get
            {
                if (PlayModeService.Instance.State == PlayState.Playing) return ViewSource.Game;
                if (_gameViewMode) return PlayCameraHelper.FindMainCameraEntity(_currentScene ?? ProjectData.Current?.ActiveScene) != null ? ViewSource.GameView : ViewSource.EditorCamera;
                return CameraService.Instance.IsGameCameraActive ? ViewSource.SceneCamera : ViewSource.EditorCamera;
            }
        }

        /// <summary>Let the editor camera drive the edit-mode view (#444): leaves the Game view (the Scene tab) and stops
        /// looking through a scene camera — otherwise every frame puts that camera back over a pose a tool just set.
        /// Returns what drove the view before (<see cref="ViewSource.EditorCamera"/>: nothing changed). Not while playing.</summary>
        public ViewSource UseEditorCamera()
        {
            var before = CurrentView;
            if (PlayModeService.Instance.IsPlaying) return before;
            if (PlayModeService.Instance.IsGameView) PlayModeService.Instance.SetGameView(false);   // → OnGameViewChanged
            // a camera picked in the toolbar blocks the editor camera's input even where it does not drive the render
            if (_viewingThroughGameCamera || CameraService.Instance.IsGameCameraActive) ViewThroughCamera(null);
            ReapplyEditorCamera();
            return before;
        }

        /// <summary>Push the editor camera's pose to the renderer again (the Game view left the main camera's there).</summary>
        private void ReapplyEditorCamera() =>
            _camera.SetFromEntityTransform(_camera.PositionX, _camera.PositionY, _camera.PositionZ, _camera.Pitch, _camera.Yaw, _camera.Roll);

        public Scene CurrentScene
        {
            get => _currentScene;
            set
            {
                if (ReferenceEquals(_currentScene, value)) return;
                _currentScene = value;
                SceneRenderService.Instance.ClearAllRenderables();
                try { SceneRenderService.EvictModelsUnusedBy(value); } catch { }   // #358
                _sceneDirty = true;
            }
        }

        /// <summary>Force a scene re-submit on the next frames (after an external edit).</summary>
        public static void RequestResubmit()
        {
            var m = Main;
            if (m != null) { m._sceneDirty = true; m._resubmitHoldFrames = 30; }
        }

        // ------------------------------------------------------------------------------------------ lifecycle

        /// <summary>Create the native render surface on the shell's window handle (NSView*/HWND) and start.</summary>
        public bool Initialize(IntPtr nativeHandle, uint pixelWidth, uint pixelHeight, IViewportHost host)
        {
            if (_initialized) return true;
            _host = host;
            _pixelW = Math.Max(1u, pixelWidth);
            _pixelH = Math.Max(1u, pixelHeight);
            if (!VortexAPI.InitRenderViewport(nativeHandle, _pixelW, _pixelH))
            {
                ConsoleService.Instance.LogError("Viewport: the renderer could not be initialised on this window.");
                return false;
            }
            _initialized = true;
            Main = this;
            SceneRenderService.Instance.Initialize();
            _camera.Reset();
            EditorViewportService.Instance.IsGridVisible = true;
            _lastFrameTime = DateTime.Now;

            PlayModeService.Instance.StateChanged += OnPlayModeStateChanged;
            PlayModeService.Instance.GameViewChanged += OnGameViewChanged;
            SelectionService.Instance.FocusRequested += OnFocusRequested;
            SelectionService.Instance.SelectionChanged += OnSelectionChanged;
            SelectionService.Instance.TransformChanged += OnTransformChanged;
            return true;
        }

        public void Shutdown()
        {
            if (!_initialized) return;
            PlayModeService.Instance.StateChanged -= OnPlayModeStateChanged;
            PlayModeService.Instance.GameViewChanged -= OnGameViewChanged;
            SelectionService.Instance.FocusRequested -= OnFocusRequested;
            SelectionService.Instance.SelectionChanged -= OnSelectionChanged;
            SelectionService.Instance.TransformChanged -= OnTransformChanged;
            if (_simActive) EndPlaySimulation();
            ReleaseGameMouse();
            _initialized = false;
            if (ReferenceEquals(Main, this)) Main = null;
            try { VortexAPI.ShutdownRender(); } catch { }
        }

        public void Resize(uint pixelWidth, uint pixelHeight)
        {
            if (!_initialized) return;
            pixelWidth = Math.Max(1u, pixelWidth); pixelHeight = Math.Max(1u, pixelHeight);
            if (pixelWidth == _pixelW && pixelHeight == _pixelH) return;
            _pixelW = pixelWidth; _pixelH = pixelHeight;
            VortexAPI.ResizeRender(_pixelW, _pixelH);
        }

        private void OnSelectionChanged(object s, SelectionEventArgs e) => _sceneDirty = true;
        private void OnTransformChanged(object s, TransformChangedEventArgs e) => _sceneDirty = true;

        // ------------------------------------------------------------------------------------------ per frame

        /// <summary>One editor frame: camera → (play: input, engine step, scripts, UI) → submit → render.</summary>
        public void Tick()
        {
            if (!_initialized) return;
            if (ActivePreviewDialogs > 0 && PlayModeService.Instance.State != PlayState.Playing) return;

            bool playing = PlayModeService.Instance.State == PlayState.Playing;
            var now = DateTime.Now;
            float dt = (float)(now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;
            if (dt > 0.25f) dt = 0.25f;

            if (playing) { /* the game drives the view (applied after the scripts ran) */ }
            else if (_gameViewMode) { ApplyMainCameraView(); NotifyFly(false); }
            else if (!CameraService.Instance.IsGameCameraActive) { _camera.Update(dt); NotifyFly(_camera.IsFlyMode); }
            else
            {
                var active = CameraService.Instance.ActiveCamera;
                if (active.IsValid) VortexAPI.ApplyEngineCameraToRenderer(active);
                NotifyFly(false);
            }

            if (!playing)
                AudioPreviewService.Instance.Tick(_camera.PositionX, _camera.PositionY, _camera.PositionZ, _camera.Yaw, _camera.Pitch);

            double uw = _host != null ? _host.Width : 0, uh = _host != null ? _host.Height : 0;
            if (playing)
            {
                bool external = PlayModeService.Instance.IsExternalWindow;
                // send_input's commands of this tick, on the play clock (#457): its keys and mouse buttons count as held through
                // HostInput, its mouse movement, wheel notches and UI cursor are added here — never the OS cursor
                var sim = HostInput.Simulated.Tick(dt);
                Vortex.Input.SetSimulated(sim.dx, sim.dy, sim.scroll);   // they reach the game with another app in front, too
                if (!external)
                {
                    UpdateGameMouseLook();
                    _debugCam.PreScripts(_currentScene, !PlayModeService.Instance.IsReleaseMode, _mouseCaptured || (_host != null && _host.IsWindowActive));
                }
                if (uw > 1 && uh > 1)
                {
                    if (!_playGridHidden) { _savedGrid = VortexAPI.IsGridVisible; VortexAPI.ShowGrid(false); _playGridHidden = true; }
                    FeedEditorUI();
                    VortexAPI.UIBegin((float)uw, (float)uh);   // the same point space the scripts see (#454)
                }

                VortexAPI.StepEngineRuntime(dt);
                ReadbackPhysics();
                // this tick's wheel notches, 0 on a tick without any (#319): the wheel event used to set ScrollDelta and
                // nothing reset it, so a zoom or weapon-switch script kept scrolling after one notch
                Vortex.Input.ScrollDelta = _wheelAccum; _wheelAccum = 0f;
                ScriptRuntime.Instance.Update(dt);
                GameRuntime.ProcessPendingSceneSwitch();
                AudioPlaybackService.Instance.Tick();
                if (external) PlayCameraHelper.ApplyPose(_extSnapPos, _extSnapRot);
                else if (!_debugCam.ApplyView(dt)) ApplyMainCameraView();

                if (!external && uw > 1 && uh > 1 && !PlayModeService.Instance.IsReleaseMode) _debugCam.DrawHint((float)uw, (float)uh);

                if (uw > 1 && uh > 1 && VuiStack.Instance.HasActiveScreens)
                {
                    var vin = new VuiInput { Mx = _uiMx, My = _uiMy, Down = _uiDown, Pressed = _uiPressed, Wheel = 0, Chars = _vuiCharsEmpty, CharCount = 0, KeyEvents = _uiKeys, KeyCount = _uiKeyCount };
                    VuiStack.Instance.TickAll((float)uw, (float)uh, vin);
                    var acts = VuiStack.Instance.ConsumeFiredActions();
                    if (acts != null) ScriptRuntime.Instance.InvokeUiActions(acts);
                }

                // a script's Application.Quit() — from Update, LateUpdate, a coroutine or a UI button — stops play here,
                // after the whole tick, through the same Stop as the toolbar and exit_play_mode (#410)
                if (ScriptRuntime.Instance.ConsumeQuitRequest()) EditorSession.Instance.Stop();
            }
            else
            {
                if (_editorFovApplied != RaycastService.EditorFovYDegrees)
                {
                    VortexAPI.SetViewFOV(RaycastService.EditorFovYDegrees);
                    VortexAPI.SetViewClipPlanes(RaycastService.EditorNearClip, RaycastService.EditorFarClip);   // the game may have set its own (#327)
                    _editorFovApplied = RaycastService.EditorFovYDegrees;
                }
                if (_playGridHidden) { VortexAPI.ShowGrid(_savedGrid); _playGridHidden = false; }
                // the grid lies on y = 0: seen from underneath it is a lattice across the sky (a world built below 0, like the
                // oasis whose floor is at −5 m) — it stays off while the camera is under it and comes back when the camera rises
                bool gridWanted = EditorViewportService.Instance.IsGridVisible && _camera != null && _camera.PositionY >= -0.01f;
                if (gridWanted != VortexAPI.IsGridVisible) VortexAPI.ShowGrid(gridWanted);
                // UI editor preview: lay out + draw the screen through the engine's retained-UI renderer.
                var preview = PreviewCanvas;
                if (preview != null && uw > 1 && uh > 1)
                {
                    try
                    {
                        VortexAPI.UIBegin((float)uw, (float)uh);
                        preview.Layout((float)uw, (float)uh);
                        preview.Render();
                    }
                    catch { }
                }
            }

            var activeScene = ProjectData.Current?.ActiveScene;
            if (activeScene != null && !ReferenceEquals(activeScene, _currentScene)) CurrentScene = activeScene;

            var sceneToRender = _currentScene ?? ProjectData.Current?.ActiveScene;
            if (sceneToRender != null)
            {
                if (_resubmitHoldFrames > 0) _resubmitHoldFrames--;
                bool stressDirty = StressTestService.Dirty;
                bool dynamicDirty = playing && (SceneRenderService.RuntimeDirty || Editor.Core.Animation.AnimationService.Instance.HasActiveAnimators);
                bool needSubmit = IsPlaying || _sceneDirty || dynamicDirty || ActivePreviewDialogs > 0 || ActiveCoexistPreviews > 0
                                  || _resubmitHoldFrames > 0 || stressDirty || !ReferenceEquals(sceneToRender, _submittedScene);
                if (needSubmit)
                {
                    SceneRenderService.Instance.SubmitScene(sceneToRender);
                    if (StressTestService.Active) { StressTestService.Submit(); StressTestService.ClearDirty(); }
                    if (WorldService.HasItems) { WorldService.Submit(); WorldService.ClearDirty(); }
                    _submittedScene = sceneToRender;
                    _sceneDirty = false;
                    SceneRenderService.RuntimeDirty = false;
                }
                if (!playing) SceneRenderService.Instance.SubmitOverlays(sceneToRender);
            }

            VortexAPI.RenderOnce();

            if ((now - _lastStatusUpdate).TotalMilliseconds >= 500)
            {
                _lastStatusUpdate = now;
                PublishStatus();
            }
        }

        private bool _flyShown;
        private void NotifyFly(bool fly)
        {
            if (fly == _flyShown) return;
            _flyShown = fly;
            FlyModeChanged?.Invoke(fly);
        }

        private void PublishStatus()
        {
            var pms = PlayModeService.Instance;
            string status = $"FPS {VortexAPI.CurrentFPS}   ·   Draw Calls {VortexAPI.DrawCalls}   ·   Vertices {VortexAPI.VertexCount:N0}";
            if (pms.IsPlaying && pms.IsExternalWindow)
            {
                float t = VortexAPI.GameTime();
                status += $"   ·   ▶ {((int)t) / 60:00}:{((int)t) % 60:00}   ·   Game runs in its own window";
            }
            else if (pms.IsPlaying)
            {
                float t = VortexAPI.GameTime();
                status += $"   ·   ▶ {((int)t) / 60:00}:{((int)t) % 60:00}";
                status += _mouseCaptured ? "   ·   Esc releases the mouse" : "   ·   Click to capture the mouse";
            }
            else if (_gameViewMode)
                status += "   ·   Press Play to start";
            string res = _host != null ? $"{(int)_host.Width} × {(int)_host.Height}" + (_host.Scaling > 1.01 ? $"  @{_host.Scaling:0.#}x" : "") : "";
            StatusUpdated?.Invoke(status, res);
        }

        // ------------------------------------------------------------------------------------------ pointer input

        private bool _terrainStroke;   // #124: the left button is sculpting / painting the selected terrain
        private bool _foliageStroke;   // #125: the left button is painting / erasing the selected foliage

        /// <param name="button">0 = left, 1 = right, 2 = middle</param>
        public void OnPointerDown(int button, double x, double y, bool alt, bool ctrl, bool shift)
        {
            _pointerX = x; _pointerY = y;
            if (button == 0) _lmbDown = true; else if (button == 1) _rmbDown = true;

            if (IsPlaying)
            {
                _host?.FocusViewport();
                _userUnlockedCursor = false;
                return;
            }
            _host?.FocusViewport();

            if (button == 1 && !_viewingThroughGameCamera) SetCursorHidden(true);
            if (!_viewingThroughGameCamera) _camera.OnMouseDown(button == 1, new PointD(x, y));

            if (button == 0 && !alt && !_rmbDown && !_viewingThroughGameCamera)
            {
                // #124: with a terrain tool active and a terrain selected, the left button sculpts / paints under the cursor
                if (Editor.Core.Services.Terrain.TerrainToolService.Active && _host != null && _host.Width > 0 && _host.Height > 0)
                {
                    float tnx = (float)(x / _host.Width), tny = (float)(y / _host.Height), taspect = (float)(_host.Width / _host.Height);
                    if (Editor.Core.Services.Terrain.TerrainToolService.TryBeginStroke(tnx, tny, taspect, shift, ctrl)) { _terrainStroke = true; return; }
                }
                if (Editor.Core.Services.Foliage.FoliageToolService.Active && _host != null && _host.Width > 0 && _host.Height > 0)
                {
                    float fnx = (float)(x / _host.Width), fny = (float)(y / _host.Height), faspect = (float)(_host.Width / _host.Height);
                    if (Editor.Core.Services.Foliage.FoliageToolService.TryBeginStroke(fnx, fny, faspect, shift, ctrl)) { _foliageStroke = true; return; }
                }
                var selected = SelectionService.Instance.SelectedEntity;
                if (selected != null && VortexAPI.AreGizmosVisible && selected.Transform != null && _host != null && _host.Width > 0 && _host.Height > 0)
                {
                    var transform = selected.Transform;
                    float nx = (float)(x / _host.Width), ny = (float)(y / _host.Height);
                    float aspect = (float)(_host.Width / _host.Height);
                    var gizmoPos = new Vector3f(transform.LocalPosition.X, transform.LocalPosition.Y, transform.LocalPosition.Z);
                    float gizmoScale = RaycastService.ComputeGizmoScale(gizmoPos);
                    var axis = RaycastService.Instance.PickGizmoAxis(nx, ny, gizmoPos, aspect, gizmoScale);
                    if (axis != GizmoAxis.None)
                    {
                        _isDraggingGizmo = true;
                        _activeGizmoAxis = axis;
                        _lastDragX = x; _lastDragY = y;
                        _dragRawPos = transform.LocalPosition;
                        _dragRawRot = transform.LocalRotation;
                        _dragRawScale = transform.LocalScale;
                        VortexAPI.IsDraggingGizmo = true;
                        VortexAPI.DraggingAxis = axis;
                        return;
                    }
                }
                HandleEntityPicking(x, y, ctrl, shift);
            }
        }

        public void OnPointerUp(int button, double x, double y)
        {
            _pointerX = x; _pointerY = y;
            if (button == 0) _lmbDown = false; else if (button == 1) _rmbDown = false;
            if (IsPlaying) return;
            if (button == 1) SetCursorHidden(false);
            if (!_viewingThroughGameCamera) _camera.OnMouseUp(button == 1);
            if (_terrainStroke && button == 0)
            {
                _terrainStroke = false;
                Editor.Core.Services.Terrain.TerrainToolService.EndStroke();
            }
            if (_foliageStroke && button == 0)
            {
                _foliageStroke = false;
                Editor.Core.Services.Foliage.FoliageToolService.EndStroke();
            }
            if (_isDraggingGizmo && button == 0)
            {
                _isDraggingGizmo = false;
                _activeGizmoAxis = GizmoAxis.None;
                VortexAPI.IsDraggingGizmo = false;
                VortexAPI.DraggingAxis = GizmoAxis.None;
            }
        }

        public void OnPointerMove(double x, double y)
        {
            double px = _pointerX, py = _pointerY;
            _pointerX = x; _pointerY = y;
            if (IsPlaying)
            {
                HostInput.Simulated.ReleaseCursor();   // the real pointer moved: it is the UI cursor again (#457)
                // Captured mouse-look: the delta comes from the pointer's offset to the centre before re-centering.
                if (_mouseCaptured && _host != null) _lookAccumX += x - _host.Width * 0.5; 
                if (_mouseCaptured && _host != null) _lookAccumY += y - _host.Height * 0.5;
                return;
            }
            if (_terrainStroke && _lmbDown)
            {
                if (_host != null && _host.Width > 0 && _host.Height > 0)
                    Editor.Core.Services.Terrain.TerrainToolService.ContinueStroke((float)(x / _host.Width), (float)(y / _host.Height), (float)(_host.Width / _host.Height), false, false);
                return;
            }
            if (_foliageStroke && _lmbDown)
            {
                if (_host != null && _host.Width > 0 && _host.Height > 0)
                    Editor.Core.Services.Foliage.FoliageToolService.ContinueStroke((float)(x / _host.Width), (float)(y / _host.Height), (float)(_host.Width / _host.Height), false, false);
                return;
            }
            if (_isDraggingGizmo && _lmbDown)
            {
                var selected = SelectionService.Instance.SelectedEntity;
                if (selected != null && selected.Transform != null && _host != null && _host.Width > 0 && _host.Height > 0)
                {
                    float nx = (float)(x / _host.Width), ny = (float)(y / _host.Height);
                    float lx = (float)(_lastDragX / _host.Width), ly = (float)(_lastDragY / _host.Height);
                    var transform = selected.Transform;
                    var entityPos = new Vector3f(transform.LocalPosition.X, transform.LocalPosition.Y, transform.LocalPosition.Z);
                    var delta = RaycastService.Instance.CalculateAxisDragDelta(nx, ny, lx, ly, entityPos, _activeGizmoAxis);
                    bool snap = EditorViewportService.Instance.SnapToGrid;
                    switch (TransformGizmoService.Instance.CurrentMode)
                    {
                        case TransformGizmoService.GizmoMode.Translate:
                        {
                            _dragRawPos = new Vector3(_dragRawPos.X + delta.X, _dragRawPos.Y + delta.Y, _dragRawPos.Z + delta.Z);
                            var np = _dragRawPos;
                            if (snap) np = new Vector3(EditorViewportService.Instance.SnapValue(np.X), EditorViewportService.Instance.SnapValue(np.Y), EditorViewportService.Instance.SnapValue(np.Z));
                            transform.LocalPosition = np;
                            break;
                        }
                        case TransformGizmoService.GizmoMode.Rotate:
                        {
                            float rotDelta = (delta.X + delta.Y + delta.Z) * 50.0f;
                            var raw = _dragRawRot;
                            switch (_activeGizmoAxis)
                            {
                                case GizmoAxis.X: raw = new Vector3(raw.X + rotDelta, raw.Y, raw.Z); break;
                                case GizmoAxis.Y: raw = new Vector3(raw.X, raw.Y + rotDelta, raw.Z); break;
                                case GizmoAxis.Z: raw = new Vector3(raw.X, raw.Y, raw.Z + rotDelta); break;
                            }
                            _dragRawRot = raw;
                            transform.LocalRotation = snap ? new Vector3(SnapTo(raw.X, 15f), SnapTo(raw.Y, 15f), SnapTo(raw.Z, 15f)) : raw;
                            break;
                        }
                        case TransformGizmoService.GizmoMode.Scale:
                        {
                            float scaleDelta = (delta.X + delta.Y + delta.Z) * 0.5f;
                            var raw = _dragRawScale;
                            switch (_activeGizmoAxis)
                            {
                                case GizmoAxis.X: raw = new Vector3(Math.Max(0.01f, raw.X + scaleDelta), raw.Y, raw.Z); break;
                                case GizmoAxis.Y: raw = new Vector3(raw.X, Math.Max(0.01f, raw.Y + scaleDelta), raw.Z); break;
                                case GizmoAxis.Z: raw = new Vector3(raw.X, raw.Y, Math.Max(0.01f, raw.Z + scaleDelta)); break;
                            }
                            _dragRawScale = raw;
                            transform.LocalScale = snap
                                ? new Vector3(Math.Max(0.01f, SnapTo(raw.X, 0.25f)), Math.Max(0.01f, SnapTo(raw.Y, 0.25f)), Math.Max(0.01f, SnapTo(raw.Z, 0.25f)))
                                : raw;
                            break;
                        }
                    }
                    SelectionService.Instance.NotifyTransformChanged();
                    _lastDragX = x; _lastDragY = y;
                }
            }
            else
            {
                if (Editor.Core.Services.Terrain.TerrainToolService.Active && _host != null && _host.Width > 0 && _host.Height > 0)
                    Editor.Core.Services.Terrain.TerrainToolService.UpdateHover((float)(x / _host.Width), (float)(y / _host.Height), (float)(_host.Width / _host.Height));
                if (Editor.Core.Services.Foliage.FoliageToolService.Active && _host != null && _host.Width > 0 && _host.Height > 0)
                    Editor.Core.Services.Foliage.FoliageToolService.UpdateHover((float)(x / _host.Width), (float)(y / _host.Height), (float)(_host.Width / _host.Height));
                UpdateGizmoHover(x, y);
                if (!_viewingThroughGameCamera) _camera.OnMouseMove(new PointD(x, y));
            }
        }

        public void OnPointerWheel(int delta)
        {
            if (IsPlaying) { _wheelAccum += delta / 120f; return; }
            _wheelAccum = 0f;
            if (!_viewingThroughGameCamera) _camera.OnMouseWheel(delta);
        }

        private static float SnapTo(float value, float increment) => increment <= 0f ? value : (float)Math.Round(value / increment) * increment;

        private void UpdateGizmoHover(double x, double y)
        {
            if (_host == null || _host.Width <= 0 || _host.Height <= 0) { VortexAPI.HoveredAxis = GizmoAxis.None; return; }
            var selected = SelectionService.Instance.SelectedEntity;
            if (selected != null && selected.Transform != null && VortexAPI.AreGizmosVisible)
            {
                float nx = (float)(x / _host.Width), ny = (float)(y / _host.Height);
                float aspect = (float)(_host.Width / _host.Height);
                var t = selected.Transform;
                var gizmoPos = new Vector3f(t.LocalPosition.X, t.LocalPosition.Y, t.LocalPosition.Z);
                float gizmoScale = RaycastService.ComputeGizmoScale(gizmoPos);
                VortexAPI.HoveredAxis = RaycastService.Instance.PickGizmoAxis(nx, ny, gizmoPos, aspect, gizmoScale);
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

        /// <summary>Entity under a viewport point (same ray pick as click selection); null over empty space.</summary>
        public GameEntity PickEntityAt(double x, double y)
        {
            if (_host == null || _host.Width <= 0 || _host.Height <= 0) return null;
            var scene = _currentScene ?? ProjectData.Current?.ActiveScene;
            if (scene == null) return null;
            return RaycastService.Instance.PickEntity((float)(x / _host.Width), (float)(y / _host.Height), scene, (float)(_host.Width / _host.Height));
        }

        private void HandleEntityPicking(double x, double y, bool ctrl, bool shift)
        {
            var picked = PickEntityAt(x, y);
            if (picked != null) SelectionService.Instance.Select(picked);
            else if (!ctrl && !shift) SelectionService.Instance.ClearSelection();
        }

        // ------------------------------------------------------------------------------------------ keyboard

        /// <summary>Key pressed while the viewport has focus. Returns true when the viewport consumed it.</summary>
        public bool OnKeyDown(int vk, bool ctrl, bool alt, bool shift)
        {
            if (vk == 0x1B && !IsPlaying)   // Esc: deselect + drop focus
            {
                SelectionService.Instance.ClearSelection();
                _host?.ClearFocus();
                SetCursorHidden(false);
                return true;
            }
            if (IsPlaying) return false;
            if (!_viewingThroughGameCamera) _camera.OnKeyDown(vk, ctrl || alt);
            if (ctrl || alt) return false;
            bool tools = !_viewingThroughGameCamera && !_camera.IsFlyMode;
            switch (vk)
            {
                case 0x57: if (tools) { TransformGizmoService.Instance.SetTranslateMode(); return true; } break;   // W
                case 0x45: if (tools) { TransformGizmoService.Instance.SetRotateMode(); return true; } break;      // E
                case 0x52: if (tools) { TransformGizmoService.Instance.SetScaleMode(); return true; } break;       // R
                case 0x47: EditorViewportService.Instance.IsGridVisible = !EditorViewportService.Instance.IsGridVisible; return true; // G
                case 0x46: if (tools) { FocusOnSelected(); return true; } break;                                   // F
                case 0x24: if (!_viewingThroughGameCamera) { _camera.Reset(); return true; } break;               // Home
                case 0x58: if (tools) { TransformGizmoService.Instance.ToggleSpace(); return true; } break;        // X
            }
            return false;
        }

        public void OnKeyUp(int vk)
        {
            if (IsPlaying) return;
            if (!_viewingThroughGameCamera) _camera.OnKeyUp(vk);
        }

        public void FocusOnSelected()
        {
            var e = SelectionService.Instance.SelectedEntity;
            if (e?.Transform != null) { var p = e.Transform.LocalPosition; _camera.FocusOn(p.X, p.Y, p.Z, 8.0f); }
        }

        public void FocusOn(float x, float y, float z, float distance = 5f) => _camera.FocusOn(x, y, z, distance);

        private void OnFocusRequested(object sender, SelectionEventArgs e)
        {
            if (_viewingThroughGameCamera) return;
            var t = e.SelectedEntity?.Transform;
            if (t != null) { var p = t.LocalPosition; _camera.FocusOn(p.X, p.Y, p.Z, 8.0f); }
        }

        // ------------------------------------------------------------------------------------------ camera choice

        /// <summary>Look through a scene camera entity (null = back to the free editor camera).</summary>
        public void ViewThroughCamera(GameEntity cameraEntity)
        {
            if (cameraEntity == null)
            {
                _viewingThroughGameCamera = false;
                CameraService.Instance.SwitchToEditorCamera();
                SetCursorHidden(false);
                ViewThroughChanged?.Invoke(null);
                return;
            }
            var cam = cameraEntity.GetComponent<Editor.ECS.Components.Rendering.Camera>();
            if (cam == null) return;
            CameraService.Instance.SwitchToGameCamera(cameraEntity.Id);
            _viewingThroughGameCamera = true;
            _sceneDirty = true;
            ViewThroughChanged?.Invoke(cameraEntity);
        }

        // ------------------------------------------------------------------------------------------ play mode

        private void OnGameViewChanged(bool active)
        {
            _gameViewMode = active;
            // back to the Scene view while editing: the renderer still holds the main camera's pose (the editor camera only
            // pushes its own when it moves) — show the editor camera at once (#444)
            if (!active && !PlayModeService.Instance.IsPlaying) ReapplyEditorCamera();
            PublishStatus();
        }

        private void OnPlayModeStateChanged(object sender, PlayState state)
        {
            if (state == PlayState.Playing)
            {
                BeginPlaySimulation();
                AudioPlaybackService.Instance.ResumeAll();
                VortexAPI.ShowGrid(false);
                VortexAPI.ShowGizmos(false);
                if (PlayModeService.Instance.IsExternalWindow)
                {
                    var t = PlayCameraHelper.FindMainCamera(_currentScene ?? ProjectData.Current?.ActiveScene);
                    if (t != null) { _extSnapPos = t.LocalPosition; _extSnapRot = t.LocalRotation; }
                }
                else _userUnlockedCursor = false;
                PublishStatus();
                return;
            }
            if (state == PlayState.Paused)
            {
                AudioPlaybackService.Instance.PauseAll();
                ReleaseGameMouse();
                PublishStatus();
                return;
            }
            _debugCam.Reset();
            ReleaseGameMouse();
            EndPlaySimulation();
            _editorFovApplied = float.NaN;
            VortexAPI.ShowGrid(EditorViewportService.Instance.IsGridVisible);
            VortexAPI.ShowGizmos(EditorViewportService.Instance.AreGizmosVisible);
            PublishStatus();
        }

        private double _lookAccumX, _lookAccumY;

        private void UpdateGameMouseLook()
        {
            bool authorityWantsCapture = VuiStack.Instance.WantsCursorCapture(ScriptRuntime.Instance.CursorLocked);
            if (!authorityWantsCapture) _userUnlockedCursor = false;
            else if (HostInput.IsKeyDown(0x1B)) _userUnlockedCursor = true;

            bool wantCapture = authorityWantsCapture && !_userUnlockedCursor && (_host == null || _host.IsWindowActive);
            if (wantCapture && !_mouseCaptured) CaptureGameMouse();
            else if (!wantCapture && _mouseCaptured) ReleaseGameMouse();

            float dx = 0f, dy = 0f;
            if (_mouseCaptured && _host != null)
            {
                if (_mouseJustCaptured) { _mouseJustCaptured = false; _lookAccumX = _lookAccumY = 0; }
                else
                {
                    dx = (float)(_lookAccumX * _host.Scaling);
                    dy = (float)(_lookAccumY * _host.Scaling);
                    if (dx > 200f) dx = 200f; else if (dx < -200f) dx = -200f;
                    if (dy > 200f) dy = 200f; else if (dy < -200f) dy = -200f;
                }
                _lookAccumX = _lookAccumY = 0;
                if (dx != 0f || dy != 0f) _host.WarpCursorToCenter();
            }
            Vortex.Input.MouseDeltaX = dx;
            Vortex.Input.MouseDeltaY = dy;
        }

        private void CaptureGameMouse()
        {
            if (_mouseCaptured || _host == null) return;
            _mouseCaptured = true;
            _mouseJustCaptured = true;
            _lookAccumX = _lookAccumY = 0;
            SetCursorHidden(true);
            _host.WarpCursorToCenter();
            CursorCaptureChanged?.Invoke(true);
        }

        private void ReleaseGameMouse()
        {
            if (!_mouseCaptured) return;
            _mouseCaptured = false;
            SetCursorHidden(false);
            Vortex.Input.MouseDeltaX = 0f;
            Vortex.Input.MouseDeltaY = 0f;
            CursorCaptureChanged?.Invoke(false);
        }

        private void SetCursorHidden(bool hidden)
        {
            if (_cursorHidden == hidden) return;
            _cursorHidden = hidden;
            _host?.SetCursorHidden(hidden);
        }

        /// <summary>The play view's UI frame (#454): the viewport's LOGICAL size and the pointer as the shell reports it —
        /// Avalonia DIPs, i.e. points on macOS — the unit the standalone player reports too (its window's points), plus the
        /// device pixels per point. Never the render size: on a Retina display that is twice as large, the editor's
        /// UI.Width would differ from the player's and a HUD laid out in it would shrink to half.</summary>
        internal static (float W, float H, float MouseX, float MouseY, float Scale) UiFrame(IViewportHost host, double pointerX, double pointerY)
        {
            if (host == null || host.Width <= 1 || host.Height <= 1) return (0f, 0f, 0f, 0f, 1f);
            double s = host.Scaling > 0 ? host.Scaling : 1.0;
            return ((float)host.Width, (float)host.Height, (float)pointerX, (float)pointerY, (float)s);
        }

        private void FeedEditorUI()
        {
            // send_input's cursor and left button reach the game's UI like the real ones (#457)
            var sim = HostInput.Simulated;
            var f = UiFrame(_host, sim.HasCursor ? sim.CursorX : _pointerX, sim.HasCursor ? sim.CursorY : _pointerY);
            float mx = f.MouseX, my = f.MouseY;
            bool down = _lmbDown || sim.IsDown(0x01);
            bool pressed = down && !_lmbPrevEd;
            _lmbPrevEd = down;
            _uiMx = mx; _uiMy = my; _uiDown = down; _uiPressed = pressed;

            _uiKeyCount = 0;
            for (int i = 0; i < _navVks.Length; i++)
            {
                bool held = HostInput.IsKeyDown(_navVks[i]);
                if (held && !_navPrev[i] && _uiKeyCount < _uiKeys.Length) _uiKeys[_uiKeyCount++] = _navVks[i];
                _navPrev[i] = held;
            }
            ScriptRuntime.Instance.SetUIFrame(f.W, f.H, mx, my, down, pressed, f.Scale);
        }

        private void BeginPlaySimulation()
        {
            if (_simActive) return;
            _simActive = true;
            _physicsEntities.Clear();
            SnapshotSceneForPlay();

            VortexAPI.ClearAllRigidbodies();
            VortexAPI.ClearAllColliders();
            VortexAPI.ResetGameClock();

            var scene = _currentScene ?? ProjectData.Current?.ActiveScene;
            // Physics v2 (#100): with a Jolt-enabled engine the rigid bodies are built by PhysicsService inside
            // ScriptRuntime.Begin (Collider + Rigidbody -> simulated body, poses written back each step). The old
            // toy AABB rigidbodies of the native runtime only remain as the fallback for a stub (no-Jolt) build.
            if (!PhysicsNative.Available && scene?.Entities != null)
                foreach (var e in scene.Entities) RegisterPhysicsRecursive(e);

            EnsurePlayerControllerOnMainCamera(scene);
            HostInput.Simulated.Clear();   // a session starts without simulated input, on its own play clock (#457)
            AudioPreviewService.Instance.Stop();
            AudioPlaybackService.Instance.BeginPlay(scene);
            VuiStack.Instance.Reset();   // a session starts without screens, like the standalone player (#477)
            PlaySession.Begin();         // from here on: which files play writes (#423)
            // the play view's UI frame before any Start(): UI.Width / Height / Scale are real from a script's first line —
            // the per-tick feed comes one frame later, so Start() saw zeros (first play) or the last session's size (#454)
            var f = UiFrame(_host, _pointerX, _pointerY);
            ScriptRuntime.Instance.SetUIFrame(f.W, f.H, f.MouseX, f.MouseY, false, false, f.Scale);
            ScriptRuntime.Instance.BeginPlaySession(scene);   // a new session reads the saves from disk again (#452)
        }

        private void EnsurePlayerControllerOnMainCamera(Scene scene)
        {
            var camEnt = FindMainCameraEntity(scene);
            if (camEnt == null) return;
            if (camEnt.GetComponent<Editor.ECS.Components.Scripting.Script>() != null) return;
            var root = ProjectData.Current?.Path;
            if (string.IsNullOrEmpty(root)) return;
            var rel = ScriptingService.EnsurePlayerController(root);
            if (!string.IsNullOrEmpty(rel))
                camEnt.AddComponentDirect(new Editor.ECS.Components.Scripting.Script(camEnt, rel));
        }

        private static GameEntity FindMainCameraEntity(Scene scene)
        {
            if (scene?.Entities == null) return null;
            foreach (var e in scene.Entities) { var r = FindMainCamEntityRec(e); if (r != null) return r; }
            return null;
        }

        private static GameEntity FindMainCamEntityRec(GameEntity e)
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
                float hx = Math.Max(0.05f, Math.Abs(s.X) * 0.5f);
                float hy = Math.Max(0.05f, Math.Abs(s.Y) * 0.5f);
                float hz = Math.Max(0.05f, Math.Abs(s.Z) * 0.5f);
                var rb = e.GetComponent<Editor.ECS.Components.Physics.Rigidbody>();
                if (rb != null && rb.BodyType == Editor.ECS.Components.Physics.RigidbodyType.Dynamic && Editor.Utilities.ID.IsValid(e.EntityId))
                {
                    _physicsEntities.Add((e, p));
                    VortexAPI.RegisterRigidbody(e.EntityId, rb.UseGravity, hx, hy, hz);
                }
                else VortexAPI.AddStaticBox(p.X, p.Y, p.Z, hx, hy, hz);
            }
            if (e.Children != null)
                foreach (var c in e.Children) RegisterPhysicsRecursive(c);
        }

        /// <summary>Legacy toy-physics readback (stub build only): mirrors the native AABB bodies' positions into the
        /// entities. With Jolt available <c>_physicsEntities</c> stays empty — PhysicsService writes the poses.</summary>
        private void ReadbackPhysics()
        {
            if (!_simActive || _physicsEntities.Count == 0) return;
            for (int i = 0; i < _physicsEntities.Count; i++)
            {
                var ent = _physicsEntities[i].ent;
                if (ent?.Transform == null || !Editor.Utilities.ID.IsValid(ent.EntityId)) continue;
                ent.Transform.SetLocalPositionFromEngine(VortexAPI.ReadEntityPosition(ent.EntityId));
            }
        }

        private void SnapshotSceneForPlay()
        {
            _snapCamX = _camera.PositionX; _snapCamY = _camera.PositionY; _snapCamZ = _camera.PositionZ;
            _snapCamYaw = _camera.Yaw; _snapCamPitch = _camera.Pitch; _snapCamRoll = _camera.Roll;
            _hasCamSnap = true;
            _transformSnapshot.Clear();
            _colorSnapshot.Clear();
            var scene = _currentScene ?? ProjectData.Current?.ActiveScene;
            // every loaded scene, not just the played one: a tool or the hierarchy may edit another one while playing (#423)
            var scenes = new List<Scene>();
            if (scene != null) scenes.Add(scene);
            var project = ProjectData.Current;
            if (project?.Scenes != null)
                foreach (var s in project.Scenes) if (s != null && s.IsLoaded && !scenes.Contains(s)) scenes.Add(s);
            _componentSnapshot = PlaySnapshot.Take(scenes);
            _undoAtPlay = UndoRedoManager.Instance.SaveHistory();
            if (scene?.Entities != null)
                foreach (var e in scene.Entities) SnapshotTransformRecursive(e);
        }

        private UndoRedoManager.History _undoAtPlay;

        /// <summary>Whether Stop puts this scene back: it was loaded when play started (#423). False outside play.</summary>
        public static bool RevertsOnStop(Scene scene) => Main?._componentSnapshot?.Covers(scene) == true;

        private void SnapshotTransformRecursive(GameEntity e)
        {
            if (e == null) return;
            if (e.Transform != null)
                _transformSnapshot.Add((e, e.Transform.LocalPosition, e.Transform.LocalRotation, e.Transform.LocalScale));
            var mr = e.GetComponent<Editor.ECS.Components.Rendering.MeshRenderer>();
            if (mr != null) _colorSnapshot.Add((e, mr.ColorR, mr.ColorG, mr.ColorB, mr.ColorA));
            if (e.Children != null)
                foreach (var c in e.Children) SnapshotTransformRecursive(c);
        }

        private void EndPlaySimulation()
        {
            if (!_simActive) return;
            _simActive = false;
            AudioPlaybackService.Instance.EndPlay();
            ScriptRuntime.Instance.End();
            ScriptRuntime.Instance.ConsumeQuitRequest();   // play is stopping anyway: no quit may carry over (#410)
            HostInput.Simulated.Clear();                   // no simulated key stays held into editing (#457)
            Vortex.Input.SetSimulated(0f, 0f, 0f);
            VuiStack.Instance.Reset();   // the session's screens go with its scripts, and its cached canvases (#477)
            PlaySession.End();
            VortexAPI.ClearAllRigidbodies();
            var snapshot = _componentSnapshot;
            _componentSnapshot = null;
            snapshot?.Restore();   // after OnDestroy: whatever play changed in the loaded scenes
            // the undo steps made while playing describe the scenes Stop just put back: undoing one afterwards re-broke the
            // hierarchy (#423) — like Unity, they go with the session
            int undoDropped = UndoRedoManager.Instance.RestoreHistory(_undoAtPlay);
            _undoAtPlay = null;
            AfterPlay(snapshot, undoDropped);
            foreach (var s in _transformSnapshot)
            {
                if (s.ent?.Transform == null) continue;
                s.ent.Transform.LocalPosition = s.pos;
                s.ent.Transform.LocalRotation = s.rot;
                s.ent.Transform.LocalScale = s.scale;
            }
            _transformSnapshot.Clear();
            _physicsEntities.Clear();
            foreach (var cs in _colorSnapshot) SceneRenderService.Instance.SetEntityColor(cs.ent, cs.r, cs.g, cs.b, cs.a);
            _colorSnapshot.Clear();
            if (_hasCamSnap)
            {
                _camera.SetFromEntityTransform(_snapCamX, _snapCamY, _snapCamZ, _snapCamPitch, _snapCamYaw, _snapCamRoll);
                _hasCamSnap = false;
            }
            _sceneDirty = true;
        }

        /// <summary>What Stop does beyond the snapshot (#423): prefabs written while playing re-sync their instances (the
        /// snapshot put the pre-play ones back), data files play created for entities that are gone are deleted, no selection
        /// points at a removed entity, and one Console line says what was removed and reverted — and that files written while
        /// playing stay on disk (play edits made in the hierarchy, by drag and drop, paste or duplicate are gone too).</summary>
        private static void AfterPlay(PlaySnapshot snap, int undoDropped)
        {
            var project = ProjectData.Current;
            int prefabs = 0;
            foreach (var path in PlaySession.Written())
            {
                if (!path.EndsWith(PrefabService.PrefabExtension, StringComparison.OrdinalIgnoreCase)) continue;
                try { PrefabService.Instance.ReloadInstancesFromPrefab(path); prefabs++; }
                catch (Exception ex) { ConsoleService.Instance.LogWarning("Stop: could not re-sync the instances of " + Path.GetFileName(path) + ": " + ex.Message); }
            }
            var loaded = new List<Scene>();
            if (project?.Scenes != null) foreach (var s in project.Scenes) if (s != null && s.IsLoaded) loaded.Add(s);
            var deleted = PlaySession.DeleteOrphanedDataFiles(loaded, out string kept);
            DropSelectionOfRemoved(snap);

            var parts = new List<string>();
            if (snap != null && snap.Removed.Count > 0)
            {
                var names = new List<string>();
                foreach (var e in snap.Removed) names.Add(e.Name);
                parts.Add("removed " + Count(snap.Removed.Count, "entity", "entities") + " added while playing (" + Few(names) + ")");
            }
            if (snap != null && snap.PutBack > 0) parts.Add("put back " + Count(snap.PutBack, "entity", "entities") + " moved or deleted while playing");
            if (snap != null && snap.SettingsReverted.Count > 0)
            {
                var names = new List<string>();
                foreach (var s in snap.SettingsReverted) names.Add("'" + s.Name + "'");
                parts.Add("reverted the environment of " + string.Join(", ", names));
            }
            if (undoDropped > 0) parts.Add("dropped " + Count(undoDropped, "undo step", "undo steps") + " made while playing");
            if (prefabs > 0) parts.Add("re-synced the instances of " + Count(prefabs, "prefab", "prefabs") + " saved while playing");
            if (deleted.Count > 0)
            {
                var names = new List<string>();
                foreach (var f in deleted) names.Add(Path.GetFileName(f));
                parts.Add("deleted " + Count(deleted.Count, "data file", "data files") + " created for them (" + Few(names) + ")");
            }
            if (kept != null) parts.Add(kept);
            var stay = PlaySession.FilesWrittenSince(project?.Path, PlaySession.StartedUtc);
            if (parts.Count == 0 && stay.Count == 0) return;
            string files = "files written while playing stay on disk";
            if (stay.Count > 0)
            {
                var names = new List<string>();
                foreach (var f in stay) names.Add(Path.GetFileName(f));
                files += " (" + stay.Count + ": " + Few(names) + ")";
            }
            ConsoleService.Instance.Log("Stop: " + (parts.Count > 0 ? string.Join("; ", parts) + "; " : "") + files + ".");
        }

        private static string Count(int n, string one, string many) => n + " " + (n == 1 ? one : many);

        private static string Few(List<string> names) => names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.GetRange(0, 3)) + ", …";

        /// <summary>An entity created while playing is gone after the restore (#423) — and so is every selection of it or of
        /// something under it: the primary selection, the hierarchy's multi-selection and each scene's own (a stale entry made
        /// Duplicate add a copy after Stop).</summary>
        private static void DropSelectionOfRemoved(PlaySnapshot snap)
        {
            if (snap == null || snap.Removed.Count == 0) return;
            var removed = new HashSet<GameEntity>(snap.Removed, ReferenceEqualityComparer.Instance);
            bool Gone(GameEntity e)
            {
                for (int depth = 0; e != null && depth < 512; e = e.Parent, depth++) if (removed.Contains(e)) return true;
                return false;
            }
            try
            {
                var hierarchy = EditorSession.Instance.Hierarchy;
                if (hierarchy.SelectedEntities != null)
                    foreach (var e in new List<GameEntity>(hierarchy.SelectedEntities)) if (Gone(e)) hierarchy.RemoveFromSelection(e);
                if (Gone(hierarchy.SelectedEntity)) hierarchy.SelectedEntity = null;
            }
            catch { }
            if (Gone(SelectionService.Instance.SelectedEntity)) SelectionService.Instance.ClearSelection();
            var project = ProjectData.Current;
            if (project?.Scenes != null)
                foreach (var s in project.Scenes) if (s != null && Gone(s.SelectedEntity)) s.SelectedEntity = null;
        }

        private void ApplyMainCameraView() => PlayCameraHelper.ApplyMainCamera(_currentScene ?? ProjectData.Current?.ActiveScene);
    }
}
