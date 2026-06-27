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

        [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINTW p);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [StructLayout(LayoutKind.Sequential)] private struct POINTW { public int X; public int Y; }
        private static bool EscDown() => (GetAsyncKeyState(0x1B) & 0x8000) != 0; // VK_ESCAPE, focus-independent

        public GameWindow()
        {
            InitializeComponent();
            GameViewportHost.OnHostCreated += (s, e) => OnHostCreated();
            GameViewportHost.OnHostDestroying += (s, e) => OnHostDestroying();
            GameViewportHost.OnViewportSizeChanged += (s, e) => { if (_ready) VortexAPI.ResizeGameWindow(W(), H()); };
            Loaded += (s, e) => { Activate(); Keyboard.Focus(this); };
            Closing += OnClosing;
            PlayModeService.Instance.StateChanged += OnPlayStateChanged;
        }

        private void OnHostCreated()
        {
            try
            {
                if (VortexAPI.CreateGameWindow(GameViewportHost.Handle, W(), H()))
                {
                    _ready = true;
                    CompositionTarget.Rendering += OnFrame; // mouse is captured lazily in OnFrame (once laid out)
                }
                else Debug.WriteLine("CreateGameWindow returned false");
            }
            catch (Exception ex) { Debug.WriteLine("GameWindow create failed: " + ex); }
        }

        // Each frame: feed mouse-look to the gameplay scripts (the editor tick runs the sim + sets the
        // camera) and render the scene into this window's swapchain.
        private void OnFrame(object sender, EventArgs e)
        {
            if (!_ready) return;

            // Lazily lock the mouse once the window is laid out + active (at host-create time the
            // viewport size is still 0, so capturing there would no-op).
            if (IsActive && !_mouseCaptured) CaptureGameMouse();

            float dx = 0f, dy = 0f;
            if (_mouseCaptured && IsActive)
            {
                if (EscDown()) { ReleaseGameMouse(); } // global key state — works even with the native HWND focused
                else if (GetCursorPos(out POINTW p) && Center(out int cx, out int cy))
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
            Vortex.Input.MouseDeltaX = dx;
            Vortex.Input.MouseDeltaY = dy;

            try
            {
                // Set THIS window's view to the live main camera, then render it into our own swapchain.
                // (The editor viewport shows a frozen placeholder; only this window renders the live game.)
                Editor.Core.Services.PlayCameraHelper.ApplyMainCamera(Editor.Core.Data.ProjectData.Current?.ActiveScene);
                VortexAPI.RenderGameWindow();
            }
            catch (Exception ex) { Debug.WriteLine("RenderGameWindow: " + ex); }
        }

        private void OnHostDestroying()
        {
            CompositionTarget.Rendering -= OnFrame;
            _ready = false;
            try { VortexAPI.DestroyGameWindow(); } catch { }
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            CompositionTarget.Rendering -= OnFrame;
            PlayModeService.Instance.StateChanged -= OnPlayStateChanged;
            _ready = false;
            ReleaseGameMouse();
            try { VortexAPI.DestroyGameWindow(); } catch { }
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
            if (!_mouseCaptured) CaptureGameMouse(); // click in the window re-locks the mouse
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape) ReleaseGameMouse(); // ESC frees the mouse (Stop closes the window)
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
