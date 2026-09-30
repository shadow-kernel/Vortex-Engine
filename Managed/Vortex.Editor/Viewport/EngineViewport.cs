using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Editor.Core.Input;
using Editor.Core.Viewport;

namespace VortexEditor.Viewport
{
    /// <summary>
    /// Hosts the native engine render surface inside the Avalonia tree (an NSView on macOS, an HWND on Windows),
    /// drives the <see cref="EditorViewportSession"/> once per composited frame and translates Avalonia input
    /// into the session's neutral pointer/key calls. Also maintains the physical key table for HostInput.
    /// </summary>
    public sealed class EngineViewport : NativeControlHost, IViewportHost
    {
        public static EngineViewport Current { get; private set; }

        private readonly EditorViewportSession _session = new EditorViewportSession();
        private IPlatformHandle _handle;
        private bool _frameLoopRunning;
        private bool _pointerInside;
        private bool _hasFocus;
        private TopLevel _topLevel;
        private static readonly HashSet<int> _keysDown = new HashSet<int>();
        private double _warpScale = 1.0;      // Avalonia screen px → CG points (auto-calibrated)
        private bool _warpCalibrated;
        private PixelPoint _lastScreenPointer;
        private Point _lastPointer;

        public EditorViewportSession Session => _session;
        public event Action<string> ToastRequested;
        /// <summary>The native child view/window handle the engine renders into (NSView* on macOS).</summary>
        public IntPtr NativeHandle => _handle != null ? _handle.Handle : IntPtr.Zero;
        /// <summary>Pointer presses that reached this control (diagnostics for the smoke run).</summary>
        public int PointerPressCount { get; private set; }

        public EngineViewport()
        {
            Focusable = true;
            ClipToBounds = true;
            HostInput.KeyDown = vk => _keysDown.Contains(vk);
            HostInput.CapsLock = MacCursor.CapsLockOn;
            HostInput.WindowFocused = () => _topLevel is Window w ? w.IsActive : true;
        }

        // ---------------------------------------------------------------- native surface

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            _handle = base.CreateNativeControlCore(parent);
            Current = this;
            Dispatcher.UIThread.Post(InitializeRenderer, DispatcherPriority.Loaded);
            return _handle;
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            StopFrameLoop();
            _session.Shutdown();
            if (ReferenceEquals(Current, this)) Current = null;
            base.DestroyNativeControlCore(control);
        }

        private void InitializeRenderer()
        {
            if (_handle == null || _session.IsInitialized) return;
            var (w, h) = PixelSize();
            if (!_session.Initialize(_handle.Handle, w, h, this)) return;
            _session.FlyModeChanged += fly => Cursor = fly ? new Cursor(StandardCursorType.None) : Cursor.Default;
            StartFrameLoop();
        }

        private (uint, uint) PixelSize()
        {
            double s = Scaling;
            return ((uint)Math.Max(1, Bounds.Width * s), (uint)Math.Max(1, Bounds.Height * s));
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (_session.IsInitialized) { var (w, h) = PixelSize(); _session.Resize(w, h); }
        }

        // ---------------------------------------------------------------- frame loop

        private void StartFrameLoop()
        {
            if (_frameLoopRunning) return;
            _frameLoopRunning = true;
            _topLevel = TopLevel.GetTopLevel(this);
            RequestFrame();
        }

        private void StopFrameLoop() => _frameLoopRunning = false;

        private void RequestFrame()
        {
            if (!_frameLoopRunning) return;
            var tl = _topLevel ?? TopLevel.GetTopLevel(this);
            if (tl != null) tl.RequestAnimationFrame(OnFrame);
            else DispatcherTimer.RunOnce(() => OnFrame(TimeSpan.Zero), TimeSpan.FromMilliseconds(16), DispatcherPriority.Render);
        }

        private void OnFrame(TimeSpan _)
        {
            if (!_frameLoopRunning) return;
            try { _session.Tick(); }
            catch (Exception ex) { Editor.Core.Services.ConsoleService.Instance.LogError("Viewport tick: " + ex.Message); }
            RequestFrame();
        }

        // ---------------------------------------------------------------- input (window-level, tunnelled)

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _topLevel = TopLevel.GetTopLevel(this);
            if (_topLevel == null) return;
            // The native child view sits above the Avalonia tree, so pointer events are taken at the window level
            // and routed here while the pointer is over our bounds (keys while the viewport is "armed" by a click).
            _topLevel.AddHandler(PointerPressedEvent, TopLevelPointerPressed, RoutingStrategies.Tunnel);
            _topLevel.AddHandler(PointerReleasedEvent, TopLevelPointerReleased, RoutingStrategies.Tunnel);
            _topLevel.AddHandler(PointerMovedEvent, TopLevelPointerMoved, RoutingStrategies.Tunnel);
            _topLevel.AddHandler(PointerWheelChangedEvent, TopLevelPointerWheel, RoutingStrategies.Tunnel);
            _topLevel.AddHandler(KeyDownEvent, TopLevelKeyDown, RoutingStrategies.Tunnel);
            _topLevel.AddHandler(KeyUpEvent, TopLevelKeyUp, RoutingStrategies.Tunnel);
            if (_topLevel is Window win) win.Deactivated += OnWindowDeactivated;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_topLevel != null)
            {
                _topLevel.RemoveHandler(PointerPressedEvent, TopLevelPointerPressed);
                _topLevel.RemoveHandler(PointerReleasedEvent, TopLevelPointerReleased);
                _topLevel.RemoveHandler(PointerMovedEvent, TopLevelPointerMoved);
                _topLevel.RemoveHandler(PointerWheelChangedEvent, TopLevelPointerWheel);
                _topLevel.RemoveHandler(KeyDownEvent, TopLevelKeyDown);
                _topLevel.RemoveHandler(KeyUpEvent, TopLevelKeyUp);
                if (_topLevel is Window win) win.Deactivated -= OnWindowDeactivated;
            }
            base.OnDetachedFromVisualTree(e);
        }

        private void OnWindowDeactivated(object sender, EventArgs e)
        {
            _keysDown.Clear();
        }

        private bool TryLocalPoint(PointerEventArgs e, out Point p)
        {
            p = e.GetPosition(this);
            return p.X >= 0 && p.Y >= 0 && p.X < Bounds.Width && p.Y < Bounds.Height;
        }

        private static int ButtonIndex(PointerUpdateKind kind)
        {
            switch (kind)
            {
                case PointerUpdateKind.LeftButtonPressed: case PointerUpdateKind.LeftButtonReleased: return 0;
                case PointerUpdateKind.RightButtonPressed: case PointerUpdateKind.RightButtonReleased: return 1;
                case PointerUpdateKind.MiddleButtonPressed: case PointerUpdateKind.MiddleButtonReleased: return 2;
                default: return -1;
            }
        }

        private void TopLevelPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (!TryLocalPoint(e, out var p)) { _hasFocus = false; return; }
            var props = e.GetCurrentPoint(this).Properties;
            int b = ButtonIndex(props.PointerUpdateKind);
            if (b < 0) return;
            _hasFocus = true;
            PointerPressCount++;
            _lastPointer = p;
            RememberScreenPointer(e);
            var m = e.KeyModifiers;
            _session.OnPointerDown(b, p.X, p.Y, m.HasFlag(KeyModifiers.Alt), m.HasFlag(KeyModifiers.Control) || m.HasFlag(KeyModifiers.Meta), m.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }

        private void TopLevelPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            var p = e.GetPosition(this);
            int b = ButtonIndex(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
            if (b < 0) return;
            _session.OnPointerUp(b, p.X, p.Y);
        }

        private void TopLevelPointerMoved(object sender, PointerEventArgs e)
        {
            var p = e.GetPosition(this);
            bool inside = p.X >= 0 && p.Y >= 0 && p.X < Bounds.Width && p.Y < Bounds.Height;
            _pointerInside = inside;
            if (!inside && !_session.IsFlyMode && !_session.IsMouseCaptured) return;
            _lastPointer = p;
            RememberScreenPointer(e);
            _session.OnPointerMove(p.X, p.Y);
        }

        private void TopLevelPointerWheel(object sender, PointerWheelEventArgs e)
        {
            if (!TryLocalPoint(e, out _)) return;
            _session.OnPointerWheel((int)Math.Round(e.Delta.Y * 120));
            e.Handled = true;
        }

        private void RememberScreenPointer(PointerEventArgs e)
        {
            try { _lastScreenPointer = this.PointToScreen(_lastPointer); } catch { }
        }

        private static int VkFromKey(Key key) => KeyNames.VirtualKeyFromName(key.ToString());

        private void TopLevelKeyDown(object sender, KeyEventArgs e)
        {
            int vk = VkFromKey(e.Key);
            if (vk != 0) _keysDown.Add(vk);
            if (!_hasFocus || vk == 0) return;
            if (_topLevel?.FocusManager?.GetFocusedElement() is TextBox) return;   // typing in a text field
            var m = e.KeyModifiers;
            if (_session.OnKeyDown(vk, m.HasFlag(KeyModifiers.Control) || m.HasFlag(KeyModifiers.Meta), m.HasFlag(KeyModifiers.Alt), m.HasFlag(KeyModifiers.Shift)))
                e.Handled = true;
        }

        private void TopLevelKeyUp(object sender, KeyEventArgs e)
        {
            int vk = VkFromKey(e.Key);
            if (vk != 0) _keysDown.Remove(vk);
            if (!_hasFocus || vk == 0) return;
            _session.OnKeyUp(vk);
        }

        // ---------------------------------------------------------------- IViewportHost

        public double Width => Bounds.Width;
        public double Height => Bounds.Height;
        public double Scaling => (_topLevel ?? TopLevel.GetTopLevel(this))?.RenderScaling ?? 1.0;
        public bool IsWindowActive => _topLevel is Window w ? w.IsActive : true;

        public void SetCursorHidden(bool hidden)
        {
            if (MacCursor.IsMac) MacCursor.SetHidden(hidden);
            Cursor = hidden ? new Cursor(StandardCursorType.None) : Cursor.Default;
        }

        public void WarpCursorToCenter()
        {
            if (!MacCursor.IsMac) return;
            try
            {
                if (!_warpCalibrated && MacCursor.TryGetPosition(out double cx, out double cy) && _lastScreenPointer != default)
                {
                    // Avalonia reports screen positions in device pixels on some backends and points on others.
                    double dx1 = Math.Abs(cx - _lastScreenPointer.X) + Math.Abs(cy - _lastScreenPointer.Y);
                    double s = Scaling;
                    double dx2 = Math.Abs(cx - _lastScreenPointer.X / s) + Math.Abs(cy - _lastScreenPointer.Y / s);
                    _warpScale = dx2 < dx1 ? 1.0 / s : 1.0;
                    _warpCalibrated = true;
                }
                var center = this.PointToScreen(new Point(Bounds.Width / 2, Bounds.Height / 2));
                MacCursor.Warp(center.X * _warpScale, center.Y * _warpScale);
                _lastPointer = new Point(Bounds.Width / 2, Bounds.Height / 2);
            }
            catch { }
        }

        public void FocusViewport() { _hasFocus = true; try { Focus(); } catch { } }
        public void ClearFocus() { _hasFocus = false; try { _topLevel?.FocusManager?.ClearFocus(); } catch { } }
        public void ShowToast(string message) => ToastRequested?.Invoke(message);
    }
}
