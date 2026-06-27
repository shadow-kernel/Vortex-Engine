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
            GameViewportHost.OnViewportSizeChanged += (s, e) => { if (_ready) { if (OwnsGameLoop) VortexAPI.ResizeRender(W(), H()); else VortexAPI.ResizeGameWindow(W(), H()); } };
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
                bool ok = OwnsGameLoop
                    ? VortexAPI.InitRenderViewport(GameViewportHost.Handle, W(), H())
                    : VortexAPI.CreateGameWindow(GameViewportHost.Handle, W(), H());
                if (ok)
                {
                    _ready = true;
                    if (OwnsGameLoop)
                    {
                        VortexAPI.ShowGrid(false);    // shipped game: no editor grid/gizmos
                        VortexAPI.ShowGizmos(false);
                    }
                    CompositionTarget.Rendering += OnFrame; // mouse is captured lazily in OnFrame (once laid out)
                }
                else Debug.WriteLine("Game render init returned false");
            }
            catch (Exception ex) { Debug.WriteLine("GameWindow create failed: " + ex); }
        }

        // Each frame: feed mouse-look to the gameplay scripts (the editor tick runs the sim + sets the
        // camera) and render the scene into this window's swapchain.
        private void OnFrame(object sender, EventArgs e)
        {
            if (!_ready) return;
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
            try { if (OwnsGameLoop) VortexAPI.ShutdownRender(); else VortexAPI.DestroyGameWindow(); } catch { }
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            CompositionTarget.Rendering -= OnFrame;
            PlayModeService.Instance.StateChanged -= OnPlayStateChanged;
            _ready = false;
            ReleaseGameMouse();
            try { if (OwnsGameLoop) VortexAPI.ShutdownRender(); else VortexAPI.DestroyGameWindow(); } catch { }
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
