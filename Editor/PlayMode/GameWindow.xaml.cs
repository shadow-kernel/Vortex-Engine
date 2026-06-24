using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core.Services;
using Editor.DllWrapper;
using Editor.Editors.WorldEditor.Components.GamePreview;

namespace Editor.PlayMode
{
    /// <summary>
    /// Standalone play window — the engine renders the running game here while the
    /// editor freezes (Unreal "Play in New Window" style). It hosts its own DX12
    /// viewport, drives the game tick (StepRuntime) + render each frame, and forwards
    /// keyboard input to the engine input system.
    /// </summary>
    public partial class GameWindow : Window
    {
        private bool _viewportReady;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastSeconds;

        public GameWindow()
        {
            InitializeComponent();

            GameViewportHost.OnHostCreated += (s, e) => OnViewportCreated();
            GameViewportHost.OnHostDestroying += (s, e) => OnViewportDestroying();
            GameViewportHost.OnViewportSizeChanged += (s, e) => OnViewportResized();

            Loaded += (s, e) => Keyboard.Focus(this);
            Closing += OnClosing;
            PlayModeService.Instance.StateChanged += OnPlayStateChanged;
        }

        private void OnViewportCreated()
        {
            try
            {
                VortexAPI.InitEngineRuntime();   // boot physics/audio/resource systems for the game tick
                VortexAPI.InitRenderViewport(GameViewportHost.Handle, ViewportWidth(), ViewportHeight());
                VortexAPI.InitInput();
                VortexAPI.LockCursor(true);
                VortexAPI.ShowCursor(false);
                _viewportReady = true;
                _lastSeconds = _clock.Elapsed.TotalSeconds;
                CompositionTarget.Rendering += OnFrame;
            }
            catch (Exception ex) { Debug.WriteLine("GameWindow viewport init failed: " + ex); }
        }

        private void OnViewportResized()
        {
            if (!_viewportReady) return;
            try { VortexAPI.ResizeRender(ViewportWidth(), ViewportHeight()); }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        private void OnFrame(object sender, EventArgs e)
        {
            if (!_viewportReady) return;
            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)(now - _lastSeconds);
            _lastSeconds = now;
            try
            {
                VortexAPI.UpdateInputState();
                if (PlayModeService.Instance.State == PlayState.Playing)
                    VortexAPI.StepEngineRuntime(dt);
                VortexAPI.RenderOnce();
            }
            catch (Exception ex) { Debug.WriteLine("GameWindow frame error: " + ex); }
        }

        private void OnViewportDestroying()
        {
            CompositionTarget.Rendering -= OnFrame;
            _viewportReady = false;
            try { VortexAPI.ShutdownRender(); } catch { /* shutting down */ }
        }

        private void OnPlayStateChanged(object sender, PlayState state)
        {
            // If something else stops play (e.g. the editor Stop button), close.
            if (state == PlayState.Editing)
            {
                Close();
                return;
            }
            bool paused = state == PlayState.Paused;
            StatusText.Text = paused ? "PAUSED" : "PLAYING";
            StatusDot.Fill = new SolidColorBrush(paused
                ? Color.FromRgb(0xFF, 0xD6, 0x0A)   // amber
                : Color.FromRgb(0x32, 0xD7, 0x4B));  // green
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            CompositionTarget.Rendering -= OnFrame;
            PlayModeService.Instance.StateChanged -= OnPlayStateChanged;
            _viewportReady = false;
            try
            {
                VortexAPI.LockCursor(false);
                VortexAPI.ShowCursor(true);
                VortexAPI.ShutdownRender();
            }
            catch { /* shutting down */ }
            if (PlayModeService.Instance.State != PlayState.Editing)
                PlayModeService.Instance.Stop();
        }

        // ---- input forwarding (keyboard) ----
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape) { Close(); return; }
            if (e.IsRepeat) return;
            VortexAPI.SendKeyEvent((KeyCode)KeyInterop.VirtualKeyFromKey(e.Key), true);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            VortexAPI.SendKeyEvent((KeyCode)KeyInterop.VirtualKeyFromKey(e.Key), false);
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                DragMove();
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            if (PlayModeService.Instance.State == PlayState.Paused)
                PlayModeService.Instance.Resume();
            else
                PlayModeService.Instance.Pause();
        }

        private void Stop_Click(object sender, RoutedEventArgs e) => Close();

        private uint ViewportWidth()  => (uint)Math.Max(1, (int)GameViewportHost.ActualWidth);
        private uint ViewportHeight() => (uint)Math.Max(1, (int)GameViewportHost.ActualHeight);
    }
}
