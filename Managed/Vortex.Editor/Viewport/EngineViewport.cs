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
    /// Hosts the native engine render surface inside the Avalonia tree (an NSView on macOS, an X11 window
    /// on Linux, an HWND on Windows),
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
        /// <summary>The native child view/window handle the engine renders into (NSView* on macOS, XID on X11).</summary>
        public IntPtr NativeHandle => _handle != null ? _handle.Handle : IntPtr.Zero;
        /// <summary>Pointer presses that reached this control (diagnostics for the smoke run).</summary>
        public int PointerPressCount { get; private set; }

        public EngineViewport()
        {
            Focusable = true;
            ClipToBounds = true;
            HostInput.KeyDown = vk => _keysDown.Contains(vk);
            HostInput.CapsLock = ViewportCursor.CapsLockOn;
            GamepadInput.Install();   // controllers in editor play (DualSense HID / XInput on Windows, SDL3 elsewhere)
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
            RouteNativeInput();
            var (w, h) = PixelSize();
            if (!_session.Initialize(_handle.Handle, w, h, this)) return;
            _session.FlyModeChanged += fly => Cursor = fly ? new Cursor(StandardCursorType.None) : Cursor.Default;
            StartFrameLoop();
        }

        /// <summary>Windows: clicks over the render window must reach the editor window, where the input handlers
        /// below take them (see <see cref="Win32ViewportInput"/>). macOS restores the responder chain natively.</summary>
        private void RouteNativeInput()
        {
            if (!OperatingSystem.IsWindows() || _handle == null) return;
            var top = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            int n = Win32ViewportInput.MakeTransparent(_handle.Handle, top);
            if (n > 0) Trace("win32: " + n + " window(s) between the render window and the editor window pass the mouse on");
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
            if (_trace)
            {
                try
                {
                    var origin = this.PointToScreen(new Point(0, 0));
                    Trace($"viewport rect on screen: {origin.X},{origin.Y} {Bounds.Width:0}x{Bounds.Height:0} (scaling {Scaling})");
                }
                catch (Exception ex) { Trace("viewport rect unavailable: " + ex.Message); }
            }
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

        private int _rectTraceTick;
        private void OnFrame(TimeSpan _)
        {
            if (!_frameLoopRunning) return;
            if (_trace && (_rectTraceTick++ % 120) == 0)
            {
                try
                {
                    var o = this.PointToScreen(new Point(0, 0));
                    Trace($"viewport rect on screen: {o.X},{o.Y} {Bounds.Width:0}x{Bounds.Height:0} (scaling {Scaling})");
                }
                catch { }
            }
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
            Dispatcher.UIThread.Post(RouteNativeInput, DispatcherPriority.Loaded);   // a new holder window after a re-dock
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

        // VORTEX_INPUT_TRACE=1 prints every pointer/key event the viewport sees. The native child surface sits
        // above the Avalonia tree, so "did the toolkit get this event at all" is the first question whenever
        // viewport input misbehaves on a platform.
        private static readonly bool _trace =
            Environment.GetEnvironmentVariable("VORTEX_INPUT_TRACE") == "1";
        private static void Trace(string msg)
        {
            if (_trace) Console.WriteLine("[input] " + msg);
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

        /// <summary>Mouse button index (0 left, 1 right, 2 middle) -> the Windows virtual-key code the gameplay
        /// scripts query through Input.GetKey("LButton"/"RButton"/"MButton").</summary>
        private static int ButtonVk(int b) => b == 0 ? 0x01 : (b == 1 ? 0x02 : (b == 2 ? 0x04 : 0));

        // A click shorter than a game frame (trackpad tap, quick Magic Mouse click) would go down AND up between two
        // ticks and the game would never see it — so every press stays "down" for at least MinHoldMs.
        private const int MinHoldMs = 60;
        private static readonly Dictionary<int, long> _pressTick = new Dictionary<int, long>();
        private static readonly Dictionary<int, int> _pressSeq = new Dictionary<int, int>();

        private static void ButtonDown(int vk)
        {
            _keysDown.Add(vk);
            _pressTick[vk] = Environment.TickCount64;
            _pressSeq[vk] = (_pressSeq.TryGetValue(vk, out int n) ? n : 0) + 1;
        }

        private static void ButtonUp(int vk)
        {
            long held = _pressTick.TryGetValue(vk, out long t0) ? Environment.TickCount64 - t0 : MinHoldMs;
            if (held >= MinHoldMs) { _keysDown.Remove(vk); return; }
            int seq = _pressSeq.TryGetValue(vk, out int n) ? n : 0;
            DispatcherTimer.RunOnce(() => { if (_pressSeq.TryGetValue(vk, out int now) && now == seq) _keysDown.Remove(vk); },
                TimeSpan.FromMilliseconds(MinHoldMs - held));
        }

        private void TopLevelPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (_trace)
            {
                var raw = e.GetPosition(this);
                Trace($"pressed kind={e.GetCurrentPoint(this).Properties.PointerUpdateKind} local=({raw.X:0},{raw.Y:0}) bounds={Bounds.Width:0}x{Bounds.Height:0}");
            }
            if (!TryLocalPoint(e, out var p)) { _hasFocus = false; Trace("  -> outside bounds, ignored"); return; }
            var props = e.GetCurrentPoint(this).Properties;
            int b = ButtonIndex(props.PointerUpdateKind);
            if (b < 0) return;
            // The game reads mouse buttons through the same key-state set as the keyboard (HostInput.KeyDown): without
            // this, Input.GetKey("LButton") / ("RButton") was always false in editor play — no firing, no aiming.
            int bvk = ButtonVk(b);
            if (bvk != 0) ButtonDown(bvk);
            _hasFocus = true;
            PointerPressCount++;
            _lastPointer = p;
            RememberScreenPointer(e);
            var m = e.KeyModifiers;
            Trace($"  -> session.OnPointerDown(button={b})");
            _session.OnPointerDown(b, p.X, p.Y, m.HasFlag(KeyModifiers.Alt), m.HasFlag(KeyModifiers.Control) || m.HasFlag(KeyModifiers.Meta), m.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }

        private void TopLevelPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            var p = e.GetPosition(this);
            int b = ButtonIndex(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
            if (b < 0) return;
            int bvk = ButtonVk(b);
            if (bvk != 0) ButtonUp(bvk);   // always, even when released outside the viewport (no stuck trigger)
            _session.OnPointerUp(b, p.X, p.Y);
        }

        /// <summary>Diagnostics for the smoke run: is this virtual key (keyboard or mouse button) currently down?</summary>
        public static bool IsVirtualKeyDown(int vk) => _keysDown.Contains(vk);

        private void TopLevelPointerMoved(object sender, PointerEventArgs e)
        {
            var p = e.GetPosition(this);
            bool inside = p.X >= 0 && p.Y >= 0 && p.X < Bounds.Width && p.Y < Bounds.Height;
            if (_trace) Trace($"moved local=({p.X:0},{p.Y:0}) inside={inside} fly={_session.IsFlyMode}");
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

        /// <summary>The virtual key of a key event. The number row goes by PHYSICAL position (#336): on a German or
        /// AZERTY layout the Key for the "1" cap is not D1, so Input.GetKey("1") never fired there. Everything else
        /// keeps the layout's Key.</summary>
        private static int VkFromKey(KeyEventArgs e)
        {
            switch (e.PhysicalKey)
            {
                case PhysicalKey.Digit0: return '0'; case PhysicalKey.Digit1: return '1'; case PhysicalKey.Digit2: return '2';
                case PhysicalKey.Digit3: return '3'; case PhysicalKey.Digit4: return '4'; case PhysicalKey.Digit5: return '5';
                case PhysicalKey.Digit6: return '6'; case PhysicalKey.Digit7: return '7'; case PhysicalKey.Digit8: return '8';
                case PhysicalKey.Digit9: return '9';
            }
            return KeyNames.VirtualKeyFromName(e.Key.ToString());
        }

        private void TopLevelKeyDown(object sender, KeyEventArgs e)
        {
            int vk = VkFromKey(e);
            Trace($"keydown {e.Key} vk=0x{vk:X} hasFocus={_hasFocus}");
            if (vk != 0) { _keysDown.Add(vk); HostInput.NotifyKeyDown(vk); }   // the latch sees taps shorter than a frame (#337)
            if (!_hasFocus || vk == 0) return;
            if (_topLevel?.FocusManager?.GetFocusedElement() is TextBox) return;   // typing in a text field
            var m = e.KeyModifiers;
            if (_session.OnKeyDown(vk, m.HasFlag(KeyModifiers.Control) || m.HasFlag(KeyModifiers.Meta), m.HasFlag(KeyModifiers.Alt), m.HasFlag(KeyModifiers.Shift)))
                e.Handled = true;
        }

        private void TopLevelKeyUp(object sender, KeyEventArgs e)
        {
            int vk = VkFromKey(e);
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
            ViewportCursor.SetHidden(hidden);
            Cursor = hidden ? new Cursor(StandardCursorType.None) : Cursor.Default;
        }

        public void WarpCursorToCenter()
        {
            if (!ViewportCursor.CanWarp) return;
            try
            {
                if (!_warpCalibrated && ViewportCursor.TryGetPosition(out double cx, out double cy) && _lastScreenPointer != default)
                {
                    // Avalonia reports screen positions in device pixels on some backends and points on others
                    // (X11 is pixels, macOS is points): pick whichever matches the platform's own reading.
                    double dx1 = Math.Abs(cx - _lastScreenPointer.X) + Math.Abs(cy - _lastScreenPointer.Y);
                    double s = Scaling;
                    double dx2 = Math.Abs(cx - _lastScreenPointer.X / s) + Math.Abs(cy - _lastScreenPointer.Y / s);
                    _warpScale = dx2 < dx1 ? 1.0 / s : 1.0;
                    _warpCalibrated = true;
                }
                var center = this.PointToScreen(new Point(Bounds.Width / 2, Bounds.Height / 2));
                ViewportCursor.Warp(center.X * _warpScale, center.Y * _warpScale);
                _lastPointer = new Point(Bounds.Width / 2, Bounds.Height / 2);
            }
            catch { }
        }

        public void FocusViewport() { _hasFocus = true; try { Focus(); } catch { } }
        public void ClearFocus() { _hasFocus = false; try { _topLevel?.FocusManager?.ClearFocus(); } catch { } }
        public void ShowToast(string message) => ToastRequested?.Invoke(message);
    }
}
