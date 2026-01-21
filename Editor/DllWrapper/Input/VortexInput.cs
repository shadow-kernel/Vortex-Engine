using System;
using System.Runtime.InteropServices;

namespace Editor.DllWrapper
{
    /// <summary>
    /// Key codes matching Windows Virtual Key codes.
    /// Used for engine input system integration.
    /// </summary>
    public enum KeyCode : uint
    {
        // Letters
        A = 0x41, B = 0x42, C = 0x43, D = 0x44, E = 0x45, F = 0x46, G = 0x47, H = 0x48,
        I = 0x49, J = 0x4A, K = 0x4B, L = 0x4C, M = 0x4D, N = 0x4E, O = 0x4F, P = 0x50,
        Q = 0x51, R = 0x52, S = 0x53, T = 0x54, U = 0x55, V = 0x56, W = 0x57, X = 0x58,
        Y = 0x59, Z = 0x5A,

        // Numbers
        Num0 = 0x30, Num1 = 0x31, Num2 = 0x32, Num3 = 0x33, Num4 = 0x34,
        Num5 = 0x35, Num6 = 0x36, Num7 = 0x37, Num8 = 0x38, Num9 = 0x39,

        // Numpad
        Numpad0 = 0x60, Numpad1 = 0x61, Numpad2 = 0x62, Numpad3 = 0x63, Numpad4 = 0x64,
        Numpad5 = 0x65, Numpad6 = 0x66, Numpad7 = 0x67, Numpad8 = 0x68, Numpad9 = 0x69,
        NumpadMultiply = 0x6A, NumpadAdd = 0x6B, NumpadSeparator = 0x6C,
        NumpadSubtract = 0x6D, NumpadDecimal = 0x6E, NumpadDivide = 0x6F,

        // Function keys
        F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75,
        F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B,

        // Special keys
        Backspace = 0x08,
        Tab = 0x09,
        Enter = 0x0D,
        Shift = 0x10,
        Ctrl = 0x11,
        Alt = 0x12,
        Pause = 0x13,
        CapsLock = 0x14,
        Escape = 0x1B,
        Space = 0x20,
        PageUp = 0x21,
        PageDown = 0x22,
        End = 0x23,
        Home = 0x24,
        Left = 0x25,
        Up = 0x26,
        Right = 0x27,
        Down = 0x28,
        PrintScreen = 0x2C,
        Insert = 0x2D,
        Delete = 0x2E,

        // Modifier keys
        LeftShift = 0xA0,
        RightShift = 0xA1,
        LeftCtrl = 0xA2,
        RightCtrl = 0xA3,
        LeftAlt = 0xA4,
        RightAlt = 0xA5,

        // Windows keys
        LeftWindows = 0x5B,
        RightWindows = 0x5C,
        Apps = 0x5D,

        None = 0xFF
    }

    /// <summary>
    /// Mouse button identifiers.
    /// </summary>
    public enum MouseButton : uint
    {
        Left = 0,
        Right = 1,
        Middle = 2,
        X1 = 3,
        X2 = 4
    }

    /// <summary>
    /// Gamepad button identifiers (for future controller support).
    /// </summary>
    public enum GamepadButton : uint
    {
        A = 0,
        B = 1,
        X = 2,
        Y = 3,
        LeftBumper = 4,
        RightBumper = 5,
        Back = 6,
        Start = 7,
        LeftStick = 8,
        RightStick = 9,
        DPadUp = 10,
        DPadDown = 11,
        DPadLeft = 12,
        DPadRight = 13
    }

    /// <summary>
    /// Gamepad axis identifiers.
    /// </summary>
    public enum GamepadAxis : uint
    {
        LeftStickX = 0,
        LeftStickY = 1,
        RightStickX = 2,
        RightStickY = 3,
        LeftTrigger = 4,
        RightTrigger = 5
    }

    /// <summary>
    /// VortexAPI - Input System functionality.
    /// Provides engine-level input handling for keyboard, mouse, and gamepads.
    /// </summary>
    public static partial class VortexAPI
    {
        #region Input System Initialization

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void InitializeInput();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ShutdownInput();

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void UpdateInput();

        /// <summary>
        /// Initialize the engine input system.
        /// Call this once at startup after initializing the runtime.
        /// </summary>
        public static void InitInput() => InitializeInput();

        /// <summary>
        /// Shutdown the engine input system.
        /// </summary>
        public static void ShutdownInputSystem() => ShutdownInput();

        /// <summary>
        /// Update input states. Call once per frame at the beginning of the game loop.
        /// </summary>
        public static void UpdateInputState() => UpdateInput();

        #endregion

        #region Input Event Processing

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ProcessKeyboardEvent(uint key, [MarshalAs(UnmanagedType.I1)] bool pressed);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ProcessMouseButtonEvent(uint button, [MarshalAs(UnmanagedType.I1)] bool pressed);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ProcessMouseMoveEvent(float x, float y);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void ProcessMouseScrollEvent(float delta);

        /// <summary>
        /// Feed a keyboard event to the input system.
        /// </summary>
        public static void SendKeyEvent(KeyCode key, bool pressed)
            => ProcessKeyboardEvent((uint)key, pressed);

        /// <summary>
        /// Feed a mouse button event to the input system.
        /// </summary>
        public static void SendMouseButtonEvent(MouseButton button, bool pressed)
            => ProcessMouseButtonEvent((uint)button, pressed);

        /// <summary>
        /// Feed a mouse move event to the input system.
        /// </summary>
        public static void SendMouseMoveEvent(float x, float y)
            => ProcessMouseMoveEvent(x, y);

        /// <summary>
        /// Feed a mouse scroll event to the input system.
        /// </summary>
        public static void SendMouseScrollEvent(float delta)
            => ProcessMouseScrollEvent(delta);

        #endregion

        #region Keyboard Queries

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsKeyDown(uint key);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsKeyPressed(uint key);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsKeyReleased(uint key);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsShiftDown();

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsCtrlDown();

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsAltDown();

        /// <summary>
        /// Check if a key is currently held down.
        /// </summary>
        public static bool GetKeyDown(KeyCode key) => IsKeyDown((uint)key);

        /// <summary>
        /// Check if a key was just pressed this frame.
        /// </summary>
        public static bool GetKeyPressed(KeyCode key) => IsKeyPressed((uint)key);

        /// <summary>
        /// Check if a key was just released this frame.
        /// </summary>
        public static bool GetKeyReleased(KeyCode key) => IsKeyReleased((uint)key);

        /// <summary>
        /// Check if any Shift key is down.
        /// </summary>
        public static bool GetShiftDown() => IsShiftDown();

        /// <summary>
        /// Check if any Ctrl key is down.
        /// </summary>
        public static bool GetCtrlDown() => IsCtrlDown();

        /// <summary>
        /// Check if any Alt key is down.
        /// </summary>
        public static bool GetAltDown() => IsAltDown();

        #endregion

        #region Mouse Queries

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsMouseButtonDown(uint button);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsMouseButtonPressed(uint button);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsMouseButtonReleased(uint button);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void GetMousePosition(out float x, out float y);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void GetMouseDelta(out float dx, out float dy);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern float GetMouseScrollDelta();

        /// <summary>
        /// Check if a mouse button is held down.
        /// </summary>
        public static bool GetMouseButtonDown(MouseButton button) => IsMouseButtonDown((uint)button);

        /// <summary>
        /// Check if a mouse button was just pressed this frame.
        /// </summary>
        public static bool GetMouseButtonPressed(MouseButton button) => IsMouseButtonPressed((uint)button);

        /// <summary>
        /// Check if a mouse button was just released this frame.
        /// </summary>
        public static bool GetMouseButtonReleased(MouseButton button) => IsMouseButtonReleased((uint)button);

        /// <summary>
        /// Get current mouse position.
        /// </summary>
        public static (float X, float Y) GetMousePos()
        {
            GetMousePosition(out float x, out float y);
            return (x, y);
        }

        /// <summary>
        /// Get mouse movement since last frame.
        /// </summary>
        public static (float DeltaX, float DeltaY) GetMouseMovement()
        {
            GetMouseDelta(out float dx, out float dy);
            return (dx, dy);
        }

        /// <summary>
        /// Get mouse scroll wheel delta.
        /// </summary>
        public static float GetMouseScroll() => GetMouseScrollDelta();

        #endregion

        #region Cursor Control

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetCursorLocked([MarshalAs(UnmanagedType.I1)] bool locked);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetCursorVisible([MarshalAs(UnmanagedType.I1)] bool visible);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsCursorLocked();

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsCursorVisible();

        /// <summary>
        /// Lock cursor to center of window (for FPS-style camera control).
        /// </summary>
        public static void LockCursor(bool locked) => SetCursorLocked(locked);

        /// <summary>
        /// Show/hide the cursor.
        /// </summary>
        public static void ShowCursor(bool visible) => SetCursorVisible(visible);

        /// <summary>
        /// Check if cursor is locked.
        /// </summary>
        public static bool CursorLocked => IsCursorLocked();

        /// <summary>
        /// Check if cursor is visible.
        /// </summary>
        public static bool CursorVisible => IsCursorVisible();

        #endregion

        #region Gamepad (Stub for future)

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsGamepadConnected(uint gamepadId);

        [DllImport(_dllName, CallingConvention = _cc)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsGamepadButtonDown(uint gamepadId, uint button);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern float GetGamepadAxis(uint gamepadId, uint axis);

        [DllImport(_dllName, CallingConvention = _cc)]
        private static extern void SetGamepadVibration(uint gamepadId, float leftMotor, float rightMotor);

        /// <summary>
        /// Check if a gamepad is connected.
        /// </summary>
        public static bool IsGamepadActive(int gamepadId)
            => IsGamepadConnected((uint)gamepadId);

        /// <summary>
        /// Check if a gamepad button is down.
        /// </summary>
        public static bool GetGamepadButtonDown(int gamepadId, GamepadButton button)
            => IsGamepadButtonDown((uint)gamepadId, (uint)button);

        /// <summary>
        /// Get gamepad axis value (-1.0 to 1.0).
        /// </summary>
        public static float GetGamepadAxisValue(int gamepadId, GamepadAxis axis)
            => GetGamepadAxis((uint)gamepadId, (uint)axis);

        /// <summary>
        /// Set gamepad vibration (0.0 to 1.0).
        /// </summary>
        public static void SetGamepadRumble(int gamepadId, float leftMotor, float rightMotor)
            => SetGamepadVibration((uint)gamepadId, leftMotor, rightMotor);

        #endregion
    }
}
