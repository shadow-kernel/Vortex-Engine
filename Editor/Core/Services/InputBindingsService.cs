using System;
using System.Collections.Generic;
using System.Windows.Input;
using Editor.DllWrapper;

namespace Editor.Core.Services
{
    /// <summary>
    /// Target for input forwarding in play mode.
    /// </summary>
    public enum InputTarget
    {
        MainCamera,
        SelectedEntity,
        PlayerController
    }

    /// <summary>
    /// Mouse button for fly mode activation.
    /// </summary>
    public enum FlyModeMouseButton
    {
        Right,
        Middle,
        Left
    }

    /// <summary>
    /// Service for managing input bindings and forwarding input to the engine.
    /// </summary>
    public class InputBindingsService
    {
        private static InputBindingsService _instance;
        public static InputBindingsService Instance => _instance ?? (_instance = new InputBindingsService());

        private readonly Dictionary<string, Key> _keyBindings = new Dictionary<string, Key>();
        private bool _isInitialized;

        // Settings
        public bool EnableGameInputForwarding { get; set; } = true;
        public bool LockCursorInPlayMode { get; set; } = true;
        public bool InvertY { get; set; }
        public bool InvertX { get; set; }
        public InputTarget InputTarget { get; set; } = InputTarget.MainCamera;
        public FlyModeMouseButton FlyModeButton { get; set; } = FlyModeMouseButton.Right;

        private InputBindingsService()
        {
            ResetToDefaults();
        }

        /// <summary>
        /// Initialize the input system.
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;
            
            VortexAPI.InitInput();
            _isInitialized = true;
        }

        /// <summary>
        /// Shutdown the input system.
        /// </summary>
        public void Shutdown()
        {
            if (!_isInitialized) return;
            
            VortexAPI.ShutdownInputSystem();
            _isInitialized = false;
        }

        /// <summary>
        /// Update input state. Call once per frame.
        /// </summary>
        public void Update()
        {
            if (!_isInitialized) return;
            VortexAPI.UpdateInputState();
        }

        /// <summary>
        /// Reset all bindings to defaults.
        /// </summary>
        public void ResetToDefaults()
        {
            _keyBindings.Clear();
            _keyBindings["Forward"] = Key.W;
            _keyBindings["Backward"] = Key.S;
            _keyBindings["Left"] = Key.A;
            _keyBindings["Right"] = Key.D;
            _keyBindings["Up"] = Key.E;
            _keyBindings["Down"] = Key.Q;
            _keyBindings["Sprint"] = Key.LeftShift;
            _keyBindings["Jump"] = Key.Space;
            _keyBindings["Crouch"] = Key.LeftCtrl;
            _keyBindings["Interact"] = Key.F;
            _keyBindings["Reload"] = Key.R;
            _keyBindings["Pause"] = Key.Escape;
        }

        /// <summary>
        /// Set a key binding.
        /// </summary>
        public void SetBinding(string action, Key key)
        {
            _keyBindings[action] = key;
        }

        /// <summary>
        /// Get a key binding.
        /// </summary>
        public Key GetBinding(string action)
        {
            return _keyBindings.TryGetValue(action, out var key) ? key : Key.None;
        }

        /// <summary>
        /// Check if an action's key is currently pressed.
        /// </summary>
        public bool IsActionPressed(string action)
        {
            if (!_keyBindings.TryGetValue(action, out var key)) return false;
            return Keyboard.IsKeyDown(key);
        }

        /// <summary>
        /// Forward a WPF key event to the engine input system.
        /// </summary>
        public void ForwardKeyEvent(Key key, bool pressed)
        {
            if (!EnableGameInputForwarding || !_isInitialized) return;
            
            var keyCode = ConvertWpfKeyToEngine(key);
            if (keyCode != KeyCode.None)
            {
                VortexAPI.SendKeyEvent(keyCode, pressed);
            }
        }

        /// <summary>
        /// Forward a mouse button event to the engine input system.
        /// </summary>
        public void ForwardMouseButtonEvent(DllWrapper.MouseButton button, bool pressed)
        {
            if (!EnableGameInputForwarding || !_isInitialized) return;
            VortexAPI.SendMouseButtonEvent(button, pressed);
        }

        /// <summary>
        /// Forward a mouse move event to the engine input system.
        /// </summary>
        public void ForwardMouseMoveEvent(float x, float y)
        {
            if (!EnableGameInputForwarding || !_isInitialized) return;
            VortexAPI.SendMouseMoveEvent(x, y);
        }

        /// <summary>
        /// Forward a mouse scroll event to the engine input system.
        /// </summary>
        public void ForwardMouseScrollEvent(float delta)
        {
            if (!EnableGameInputForwarding || !_isInitialized) return;
            VortexAPI.SendMouseScrollEvent(delta);
        }

        /// <summary>
        /// Convert WPF Key to Engine KeyCode.
        /// </summary>
        private KeyCode ConvertWpfKeyToEngine(Key key)
        {
            switch (key)
            {
                // Letters
                case Key.A: return KeyCode.A;
                case Key.B: return KeyCode.B;
                case Key.C: return KeyCode.C;
                case Key.D: return KeyCode.D;
                case Key.E: return KeyCode.E;
                case Key.F: return KeyCode.F;
                case Key.G: return KeyCode.G;
                case Key.H: return KeyCode.H;
                case Key.I: return KeyCode.I;
                case Key.J: return KeyCode.J;
                case Key.K: return KeyCode.K;
                case Key.L: return KeyCode.L;
                case Key.M: return KeyCode.M;
                case Key.N: return KeyCode.N;
                case Key.O: return KeyCode.O;
                case Key.P: return KeyCode.P;
                case Key.Q: return KeyCode.Q;
                case Key.R: return KeyCode.R;
                case Key.S: return KeyCode.S;
                case Key.T: return KeyCode.T;
                case Key.U: return KeyCode.U;
                case Key.V: return KeyCode.V;
                case Key.W: return KeyCode.W;
                case Key.X: return KeyCode.X;
                case Key.Y: return KeyCode.Y;
                case Key.Z: return KeyCode.Z;

                // Numbers
                case Key.D0: return KeyCode.Num0;
                case Key.D1: return KeyCode.Num1;
                case Key.D2: return KeyCode.Num2;
                case Key.D3: return KeyCode.Num3;
                case Key.D4: return KeyCode.Num4;
                case Key.D5: return KeyCode.Num5;
                case Key.D6: return KeyCode.Num6;
                case Key.D7: return KeyCode.Num7;
                case Key.D8: return KeyCode.Num8;
                case Key.D9: return KeyCode.Num9;

                // Function keys
                case Key.F1: return KeyCode.F1;
                case Key.F2: return KeyCode.F2;
                case Key.F3: return KeyCode.F3;
                case Key.F4: return KeyCode.F4;
                case Key.F5: return KeyCode.F5;
                case Key.F6: return KeyCode.F6;
                case Key.F7: return KeyCode.F7;
                case Key.F8: return KeyCode.F8;
                case Key.F9: return KeyCode.F9;
                case Key.F10: return KeyCode.F10;
                case Key.F11: return KeyCode.F11;
                case Key.F12: return KeyCode.F12;

                // Special keys
                case Key.Space: return KeyCode.Space;
                case Key.Enter: return KeyCode.Enter;
                case Key.Tab: return KeyCode.Tab;
                case Key.Back: return KeyCode.Backspace;
                case Key.Escape: return KeyCode.Escape;
                case Key.LeftShift: return KeyCode.LeftShift;
                case Key.RightShift: return KeyCode.RightShift;
                case Key.LeftCtrl: return KeyCode.LeftCtrl;
                case Key.RightCtrl: return KeyCode.RightCtrl;
                case Key.LeftAlt: return KeyCode.LeftAlt;
                case Key.RightAlt: return KeyCode.RightAlt;
                case Key.Left: return KeyCode.Left;
                case Key.Right: return KeyCode.Right;
                case Key.Up: return KeyCode.Up;
                case Key.Down: return KeyCode.Down;

                default: return KeyCode.None;
            }
        }

        /// <summary>
        /// Get the current input state summary for debugging.
        /// </summary>
        public string GetInputStateSummary()
        {
            if (!_isInitialized) return "Input not initialized";

            var pos = VortexAPI.GetMousePos();
            return $"Mouse: ({pos.X:F0}, {pos.Y:F0}) | WASD: {(IsActionPressed("Forward") ? "W" : "")}" +
                   $"{(IsActionPressed("Left") ? "A" : "")}{(IsActionPressed("Backward") ? "S" : "")}" +
                   $"{(IsActionPressed("Right") ? "D" : "")}";
        }
    }
}
