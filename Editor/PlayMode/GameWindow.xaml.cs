using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core.Services;
using Editor.DllWrapper;

namespace Editor.PlayMode
{
    /// <summary>
    /// Standalone game window — a real second OS window with its OWN DX12 swapchain (created via
    /// VortexAPI.CreateGameWindow on this window's child HWND; it shares the engine's device/queue).
    /// The editor's play tick runs the simulation + scripts and sets the game camera; this window
    /// renders that scene into its swapchain each frame and owns mouse-look (cursor lock, ESC frees).
    /// </summary>
    public partial class GameWindow : Window
    {
        private bool _ready;
        private bool _mouseCaptured;
        private bool _justCaptured;
        private bool _userReleased; // ESC freed the mouse + paused — don't auto-recapture until a click

        /// <summary>Standalone player: THIS window drives the whole game loop (step engine + run scripts +
        /// submit the scene), because there is no editor GamePreview tick behind it. In-editor "Run in new
        /// window" leaves this false — the editor's viewport tick does the stepping/submitting.</summary>
        public bool OwnsGameLoop;
        private DateTime _lastFrameTime = DateTime.Now;

        // F11 borderless-fullscreen toggle state (saved window chrome so we can restore on exit).
        private bool _fullscreen;
        private bool _f11Prev;
        private WindowStyle _savedStyle;
        private WindowState _savedState;
        private ResizeMode _savedResize;
        private Rect _savedBounds = Rect.Empty;
        private Visibility _savedTitleVis;
        private GridLength _savedTitleRow;

        // Dedicated render thread for the shipped standalone (OwnsGameLoop) so FPS isn't capped by WPF's
        // ~60Hz CompositionTarget. Init + render + shutdown all happen on this one thread (DX12 present must
        // not cross threads). WPF-bound values are cached on the UI thread. Instrumented with file logging.
        private System.Threading.Thread _renderThread;
        private volatile bool _runThread;
        private IntPtr _hwnd;
        private volatile bool _pendingResize;
        private volatile int _cw = 1, _ch = 1;
        private volatile int _ctlx, _ctly, _ccx, _ccy;
        private volatile bool _cActive = true;
        private Editor.Core.Data.ProjectData _proj; // cached on the UI thread; ProjectData.Current verifies the dispatcher
        private object _submittedScene;             // submit the scene once per scene (static world, persistent queue)
        private static readonly string _rlogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vortex_render.log");
        private static void RLog(string m)
        {
            try { System.IO.File.AppendAllText(_rlogPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + m + "\r\n"); } catch { }
        }

        [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINTW p);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [StructLayout(LayoutKind.Sequential)] private struct POINTW { public int X; public int Y; }
        private static bool EscDown() => (GetAsyncKeyState(0x1B) & 0x8000) != 0; // VK_ESCAPE, focus-independent

        public GameWindow()
        {
            InitializeComponent();
            // Release build = shipped game: hide the dev play banner so it's just the game viewport.
            if (PlayModeService.Instance.IsReleaseMode)
            {
                GameTitleBar.Visibility = System.Windows.Visibility.Collapsed;
                TitleRow.Height = new System.Windows.GridLength(0);
                Title = "Game";
            }
            GameViewportHost.OnHostCreated += (s, e) => OnHostCreated();
            GameViewportHost.OnHostDestroying += (s, e) => OnHostDestroying();
            GameViewportHost.OnViewportSizeChanged += (s, e) =>
            {
                UpdateCachedLayout();                         // keep cached size + mouse-center fresh for the render thread
                if (OwnsGameLoop) _pendingResize = true;      // render thread applies it (never resize the swapchain cross-thread)
                else if (_ready) VortexAPI.ResizeGameWindow(W(), H());
            };
            Loaded += (s, e) => { Activate(); Keyboard.Focus(this); };
            Closing += OnClosing;
            PlayModeService.Instance.StateChanged += OnPlayStateChanged;
        }

        private void OnHostCreated()
        {
            try
            {
                // Standalone player: be the PRIMARY render viewport (creates the D3D device + main swapchain).
                // In-editor "Run in new window": a SECONDARY swapchain riding on the editor's existing device.
                if (OwnsGameLoop)
                {
                    // Shipped standalone: drive the game from a DEDICATED render thread (uncapped FPS).
                    try { System.IO.File.WriteAllText(_rlogPath, ""); } catch { } // fresh log
                    _hwnd = GameViewportHost.Handle;
                    _proj = Editor.Core.Data.ProjectData.Current; // capture on the UI thread for the render thread
                    RLog("OnHostCreated: OwnsGameLoop, hwnd=" + _hwnd + " size=" + W() + "x" + H() + " proj=" + (_proj != null));
                    UpdateCachedLayout();
                    _runThread = true;
                    _renderThread = new System.Threading.Thread(RenderThreadLoop) { IsBackground = true, Name = "VortexRender" };
                    _renderThread.Start();
                }
                else if (VortexAPI.CreateGameWindow(GameViewportHost.Handle, W(), H()))
                {
                    _ready = true;
                    CompositionTarget.Rendering += OnFrame; // editor "Run in new window": UI-thread tick
                }
                else Debug.WriteLine("Game render init returned false");
            }
            catch (Exception ex) { Debug.WriteLine("GameWindow create failed: " + ex); }
        }

        private void UpdateCachedLayout()
        {
            try
            {
                if (GameViewportHost == null) return;
                double w = GameViewportHost.ActualWidth, h = GameViewportHost.ActualHeight;
                if (w < 1 || h < 1) return;
                _cw = (int)w; _ch = (int)h;
                var tl = GameViewportHost.PointToScreen(new Point(0, 0));
                var c = GameViewportHost.PointToScreen(new Point(w / 2.0, h / 2.0));
                _ctlx = (int)tl.X; _ctly = (int)tl.Y; _ccx = (int)c.X; _ccy = (int)c.Y;
            }
            catch { }
        }

        // Dedicated standalone render/game loop — creates the viewport, runs uncapped, shuts down (all one thread).
        private void RenderThreadLoop()
        {
            RLog("thread: started");
            try
            {
                bool ok = VortexAPI.InitRenderViewport(_hwnd, (uint)Math.Max(1, _cw), (uint)Math.Max(1, _ch));
                RLog("thread: InitRenderViewport returned " + ok);
                if (!ok) return;
                VortexAPI.ShowGrid(false);
                VortexAPI.ShowGizmos(false);
                _ready = true;
            }
            catch (Exception ex) { RLog("thread: init EXCEPTION " + ex); return; }

            int frame = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double last = sw.Elapsed.TotalSeconds;
            while (_runThread)
            {
                double now = sw.Elapsed.TotalSeconds;
                float dt = (float)(now - last); last = now;
                if (dt < 0f) dt = 0f; else if (dt > 0.1f) dt = 0.1f;
                try { OwnedFrame(dt); }
                catch (Exception ex) { if (frame < 3) RLog("thread: frame EXCEPTION " + ex); }
                frame++;
                if (frame == 1 || frame == 60 || frame == 600) RLog("thread: rendered frame " + frame + " (dt=" + dt.ToString("0.0000") + ")");
            }
            RLog("thread: exiting after " + frame + " frames");
            try { VortexAPI.ShutdownRender(); } catch { }
        }

        private void OwnedFrame(float dt)
        {
            if (!_ready) return;
            if (_pendingResize) { _pendingResize = false; try { VortexAPI.ResizeRender((uint)Math.Max(1, _cw), (uint)Math.Max(1, _ch)); } catch { } }
            bool playing = PlayModeService.Instance.State == PlayState.Playing;

            bool f11 = (GetAsyncKeyState(0x7A) & 0x8000) != 0;
            if (f11 && !_f11Prev) Dispatcher.BeginInvoke(new Action(ToggleFullscreen));
            _f11Prev = f11;

            bool wantLock = playing && Editor.Scripting.ScriptRuntime.Instance.CursorLocked;
            float dx = 0f, dy = 0f;
            if (wantLock && _cActive)
            {
                if (!_mouseCaptured) { _mouseCaptured = true; _justCaptured = true; ShowCursor(false); SetCursorPos(_ccx, _ccy); }
                if (GetCursorPos(out POINTW p))
                {
                    if (_justCaptured) _justCaptured = false;
                    else { dx = p.X - _ccx; dy = p.Y - _ccy; if (dx > 200f) dx = 200f; else if (dx < -200f) dx = -200f; if (dy > 200f) dy = 200f; else if (dy < -200f) dy = -200f; }
                    SetCursorPos(_ccx, _ccy);
                }
            }
            else if (_mouseCaptured) { _mouseCaptured = false; ShowCursor(true); }
            Vortex.Input.MouseDeltaX = dx; Vortex.Input.MouseDeltaY = dy;

            float uw = _cw, uh = _ch, mx = 0f, my = 0f;
            if (GetCursorPos(out POINTW cp)) { mx = cp.X - _ctlx; my = cp.Y - _ctly; }
            bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0; bool pressed = down && !_lmbPrev; _lmbPrev = down;
            Editor.Scripting.ScriptRuntime.Instance.SetUIFrame(uw, uh, mx, my, down, pressed);
            VortexAPI.UIBegin(uw, uh);

            if (playing)
            {
                VortexAPI.StepEngineRuntime(dt);
                Editor.Scripting.ScriptRuntime.Instance.Update(dt);
                Editor.Core.Services.GameRuntime.ProcessPendingSceneSwitch();
            }
            var scene = _proj != null ? _proj.ActiveScene : null; // cached project — no dispatcher check
            Editor.Core.Services.PlayCameraHelper.ApplyMainCamera(scene);
            // Submit the scene ONCE (and again on scene switch). The world is static — only the camera moves —
            // and the native render queue now persists, so skipping the per-frame scene walk + interop is the
            // main FPS lever. (Dynamic/moving meshes would need a dirty flag; none today.)
            if (scene != null && !ReferenceEquals(scene, _submittedScene))
            {
                Editor.Core.Services.SceneRenderService.Instance.SubmitScene(scene);
                _submittedScene = scene;
            }
            VortexAPI.RenderOnce();
        }

        private void StopRenderThread()
        {
            _runThread = false;
            var t = _renderThread; _renderThread = null;
            if (t != null && t.IsAlive) { try { t.Join(900); } catch { } }
            _ready = false;
        }

        // Each frame: feed mouse-look to the gameplay scripts (the editor tick runs the sim + sets the
        // camera) and render the scene into this window's swapchain.
        private void OnFrame(object sender, EventArgs e)
        {
            if (!_ready) return;

            // F11 toggles borderless fullscreen (focus-independent, edge-triggered like ESC).
            bool f11 = (GetAsyncKeyState(0x7A) & 0x8000) != 0; // VK_F11
            if (f11 && !_f11Prev) ToggleFullscreen();
            _f11Prev = f11;

            var pms = PlayModeService.Instance;
            bool playing = pms.State == PlayState.Playing;

            // The GAME owns the mouse mode (Vortex.Cursor.Locked): locked = captured + hidden for mouse-look
            // (gameplay); unlocked = free cursor so the player can click the UI (lobby / ESC menu / shop).
            // ESC no longer pauses — a game script toggles Cursor.Locked instead. The sim keeps running.
            // Capture in BOTH external-play modes: standalone player AND the editor's "Run in new window".
            bool wantLock = playing && Editor.Scripting.ScriptRuntime.Instance.CursorLocked;

            float dx = 0f, dy = 0f;
            if (wantLock && IsActive)
            {
                if (!_mouseCaptured) CaptureGameMouse();
                if (_mouseCaptured && GetCursorPos(out POINTW p) && Center(out int cx, out int cy))
                {
                    if (_justCaptured) _justCaptured = false;
                    else
                    {
                        dx = p.X - cx; dy = p.Y - cy;
                        if (dx > 200f) dx = 200f; else if (dx < -200f) dx = -200f;
                        if (dy > 200f) dy = 200f; else if (dy < -200f) dy = -200f;
                    }
                    SetCursorPos(cx, cy);
                }
            }
            else if (_mouseCaptured)
            {
                ReleaseGameMouse(); // free the cursor for UI
            }
            Vortex.Input.MouseDeltaX = dx;
            Vortex.Input.MouseDeltaY = dy;

            try
            {
                var scene = Editor.Core.Data.ProjectData.Current?.ActiveScene;

                if (OwnsGameLoop)
                {
                    // No editor tick behind us — run the FULL game loop here: advance the engine + gameplay
                    // scripts, aim the main camera, then submit the scene so the render queue isn't empty.
                    var now = DateTime.Now;
                    float dt = (float)(now - _lastFrameTime).TotalSeconds;
                    _lastFrameTime = now;
                    if (dt < 0f) dt = 0f; else if (dt > 0.1f) dt = 0.1f; // clamp after stalls

                    // Feed the UI frame (viewport size + mouse) and start a fresh UI command list so scripts
                    // can draw the lobby/HUD this frame.
                    float uw = (float)W(), uh = (float)H();
                    FeedUIFrame(uw, uh);
                    VortexAPI.UIBegin(uw, uh);

                    if (playing)
                    {
                        VortexAPI.StepEngineRuntime(dt);
                        Editor.Scripting.ScriptRuntime.Instance.Update(dt);
                        Editor.Core.Services.GameRuntime.ProcessPendingSceneSwitch(); // a script may have called Scene.Load
                    }
                    // The active scene may have changed above — re-read it for camera + submit.
                    scene = Editor.Core.Data.ProjectData.Current != null ? Editor.Core.Data.ProjectData.Current.ActiveScene : null;
                    Editor.Core.Services.PlayCameraHelper.ApplyMainCamera(scene);
                    if (scene != null) Editor.Core.Services.SceneRenderService.Instance.SubmitScene(scene);
                }
                else
                {
                    // In-editor "Run in new window": the editor's GamePreview tick steps + submits the scene;
                    // we only re-aim this window's view at the live main camera each frame.
                    Editor.Core.Services.PlayCameraHelper.ApplyMainCamera(scene);
                }

                if (OwnsGameLoop) VortexAPI.RenderOnce();      // primary present
                else VortexAPI.RenderGameWindow();             // secondary swapchain present
            }
            catch (Exception ex) { Debug.WriteLine("RenderGameWindow: " + ex); }
        }

        // Borderless fullscreen: drop the window chrome + dev banner and maximize; F11 again restores.
        // The DX12 swapchain auto-resizes via GameViewportHost.OnViewportSizeChanged, so the render
        // follows the new size with no manual resize call.
        private void ToggleFullscreen()
        {
            try
            {
                if (!_fullscreen)
                {
                    _savedStyle = WindowStyle; _savedState = WindowState; _savedResize = ResizeMode;
                    _savedBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
                    _savedTitleVis = GameTitleBar.Visibility; _savedTitleRow = TitleRow.Height;

                    GameTitleBar.Visibility = System.Windows.Visibility.Collapsed;
                    TitleRow.Height = new GridLength(0);
                    WindowStyle = WindowStyle.None;
                    ResizeMode = ResizeMode.NoResize;
                    if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // force a re-layout
                    WindowState = WindowState.Maximized;
                    _fullscreen = true;
                }
                else
                {
                    GameTitleBar.Visibility = _savedTitleVis;
                    TitleRow.Height = _savedTitleRow;
                    WindowStyle = _savedStyle;
                    ResizeMode = _savedResize;
                    WindowState = _savedState;
                    if (!_savedBounds.IsEmpty && _savedState != WindowState.Maximized)
                    {
                        Left = _savedBounds.Left; Top = _savedBounds.Top;
                        Width = _savedBounds.Width; Height = _savedBounds.Height;
                    }
                    _fullscreen = false;
                }
            }
            catch (Exception ex) { Debug.WriteLine("ToggleFullscreen: " + ex); }
        }

        private bool _lmbPrev;

        // Feed the script UI host the viewport size + mouse (in render pixels, top-left origin) so scripts
        // can draw + hit-test. Mouse is best-effort (DPI-scaled from the host).
        private void FeedUIFrame(float renderW, float renderH)
        {
            float mx = 0f, my = 0f;
            try
            {
                if (GameViewportHost != null && GameViewportHost.ActualWidth > 1 && GetCursorPos(out POINTW cp))
                {
                    var tl = GameViewportHost.PointToScreen(new Point(0, 0));
                    float sx = renderW / (float)GameViewportHost.ActualWidth;
                    float sy = renderH / (float)GameViewportHost.ActualHeight;
                    mx = (float)(cp.X - tl.X) * sx;
                    my = (float)(cp.Y - tl.Y) * sy;
                }
            }
            catch { }
            bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0; // VK_LBUTTON
            bool pressed = down && !_lmbPrev;
            _lmbPrev = down;
            Editor.Scripting.ScriptRuntime.Instance.SetUIFrame(renderW, renderH, mx, my, down, pressed);
        }

        private void OnHostDestroying()
        {
            CompositionTarget.Rendering -= OnFrame;
            _ready = false;
            if (OwnsGameLoop) StopRenderThread();   // the render thread shuts down the renderer itself
            else { try { VortexAPI.DestroyGameWindow(); } catch { } }
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            CompositionTarget.Rendering -= OnFrame;
            PlayModeService.Instance.StateChanged -= OnPlayStateChanged;
            _ready = false;
            if (OwnsGameLoop) StopRenderThread();
            ReleaseGameMouse();
            if (!OwnsGameLoop) { try { VortexAPI.DestroyGameWindow(); } catch { } }
            PlayModeService.Instance.IsExternalWindow = false;
            if (PlayModeService.Instance.State != PlayState.Editing)
                PlayModeService.Instance.Stop();
        }

        private void OnPlayStateChanged(object sender, PlayState state)
        {
            if (state == PlayState.Editing) { Close(); return; }
            bool paused = state == PlayState.Paused;
            StatusText.Text = paused ? "PAUSED" : "PLAYING";
            StatusDot.Fill = new SolidColorBrush(paused
                ? Color.FromRgb(0xFF, 0xD6, 0x0A) : Color.FromRgb(0x32, 0xD7, 0x4B));
        }

        // ---- mouse-look capture (cursor lock + hide; ESC frees, click re-locks) ----
        private void CaptureGameMouse()
        {
            if (_mouseCaptured || !Center(out int cx, out int cy)) return;
            _mouseCaptured = true;
            _justCaptured = true;
            ShowCursor(false);
            SetCursorPos(cx, cy);
        }

        private void ReleaseGameMouse()
        {
            if (!_mouseCaptured) return;
            _mouseCaptured = false;
            ShowCursor(true);
            Vortex.Input.MouseDeltaX = 0f;
            Vortex.Input.MouseDeltaY = 0f;
        }

        private bool Center(out int x, out int y)
        {
            x = y = 0;
            if (GameViewportHost == null || GameViewportHost.ActualWidth < 2 || GameViewportHost.ActualHeight < 2) return false;
            try
            {
                var c = GameViewportHost.PointToScreen(new Point(GameViewportHost.ActualWidth / 2.0, GameViewportHost.ActualHeight / 2.0));
                x = (int)c.X; y = (int)c.Y; return true;
            }
            catch { return false; }
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            // Mouse capture is driven by Vortex.Cursor.Locked (set by the game), NOT by clicks — so clicking
            // a button in the lobby / ESC menu never grabs the cursor.
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left && !_mouseCaptured) DragMove();
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            if (PlayModeService.Instance.State == PlayState.Paused) PlayModeService.Instance.Resume();
            else PlayModeService.Instance.Pause();
        }

        private void Stop_Click(object sender, RoutedEventArgs e) => Close();

        private uint W() => (uint)Math.Max(1, (int)GameViewportHost.ActualWidth);
        private uint H() => (uint)Math.Max(1, (int)GameViewportHost.ActualHeight);
    }
}
