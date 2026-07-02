// The Vortex scripting API. Gameplay scripts (Assets/Scripts/*.cs) derive from VortexBehaviour and
// are compiled + run by the engine on Play (see ScriptRuntime). This API lives in the editor
// assembly so behaviours can actually affect the running game (move their entity, read input, etc.).
// The engine wires the host implementation at runtime; scripts only see the public surface below.
using System;

namespace Vortex
{
    public struct Vector3
    {
        public float X, Y, Z;
        public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static Vector3 Zero => new Vector3(0f, 0f, 0f);
        public static Vector3 One => new Vector3(1f, 1f, 1f);
        public static Vector3 Up => new Vector3(0f, 1f, 0f);
        public static Vector3 Forward => new Vector3(0f, 0f, 1f);
    }

    /// <summary>A UI color (0..1 channels). Use Rgb/Rgba helpers for 0..255 values.</summary>
    public struct Color
    {
        public float R, G, B, A;
        public Color(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
        public static Color Rgb(int r, int g, int b) { return new Color(r / 255f, g / 255f, b / 255f, 1f); }
        public static Color Rgba(int r, int g, int b, int a) { return new Color(r / 255f, g / 255f, b / 255f, a / 255f); }
        public Color WithAlpha(float a) { return new Color(R, G, B, a); }
    }

    /// <summary>Implemented by the engine (ScriptRuntime); lets behaviours touch the live game.</summary>
    public interface IScriptHost
    {
        Vector3 GetPosition(long entityId);
        void SetPosition(long entityId, Vector3 position);
        Vector3 GetRotation(long entityId);
        void SetRotation(long entityId, Vector3 eulerDegrees);
        bool GetKey(string key);

        // Collide-and-slide a character capsule (feet, radius, height) against the scene's colliders.
        // selfId registers this character so other characters can't walk through it (0 = anonymous).
        Vector3 MoveCharacter(Vector3 feet, float radius, float height, Vector3 move, out bool grounded, long selfId);

        // Request switching the active scene by name (deferred — applied by the runtime after this tick).
        void LoadScene(string name);

        // Mouse mode: locked = captured + hidden for mouse-look (gameplay); unlocked = free cursor for UI.
        bool GetCursorLocked();
        void SetCursorLocked(bool locked);

        // Quit the whole game (closes the player / stops play).
        void QuitGame();

        // Set the player camera's vertical field of view (degrees).
        void SetCameraFov(float fovDegrees);

        // Set an entity's base color at runtime (e.g. change color when a trigger is touched).
        void SetEntityColor(long entityId, float r, float g, float b);

        // Skeletal animation: play a clip on an entity's Animator (clip = table name or .vanim path);
        // fade > 0 crossfades from the current pose. State machines are game logic — build them in scripts.
        bool PlayAnimation(long entityId, string clip, float fade);
        void StopAnimation(long entityId);
        void SetAnimationSpeed(long entityId, float speed);
        bool IsAnimationPlaying(long entityId, string clip);
        float GetAnimationTime(long entityId);

        // 2D UI overlay (immediate mode), coordinates in viewport pixels (top-left origin).
        void UIRect(float x, float y, float w, float h, float r, float g, float b, float a, float radius);
        void UIText(float x, float y, float w, float h, string text, float size, float r, float g, float b, float a, int align, int weight);
        void UILine(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thick);
        void UIImage(float x, float y, float w, float h, string path, float r, float g, float b, float a);
        float UIWidth();
        float UIHeight();
        float UIMouseX();
        float UIMouseY();
        bool UIMouseDown();
        bool UIMousePressed();
    }

    /// <summary>
    /// A collision/trigger contact passed to OnTriggerEnter/Stay/Exit and OnCollisionEnter. Identifies the OTHER
    /// entity involved (the one that entered your trigger, or the surface you hit).
    /// </summary>
    public struct TriggerHit
    {
        /// <summary>Script handle of the other entity (0 if it has no script).</summary>
        public long EntityId;
        /// <summary>Name of the other entity.</summary>
        public string Name;
        /// <summary>Tag of the other entity (e.g. "Player", "Enemy").</summary>
        public string Tag;
        public TriggerHit(long id, string name, string tag) { EntityId = id; Name = name ?? ""; Tag = tag ?? ""; }
    }

    /// <summary>
    /// Base class for all gameplay behaviours — like MonoBehaviour. Override Start (called once when
    /// play begins) and Update (called every tick). Move your entity via Position / Translate, read
    /// input via Input.GetKey, and timing via Time.DeltaTime. For collision zones, mark a Collider as
    /// a Trigger and override OnTriggerEnter/OnTriggerStay/OnTriggerExit (e.g. a no-fly zone, or "change
    /// color when touched"); for solid contacts override OnCollisionEnter.
    /// </summary>
    public abstract class VortexBehaviour
    {
        /// <summary>Engine id of the entity this behaviour is attached to (set by the runtime).</summary>
        public long EntityId { get; internal set; }

        /// <summary>The host the engine wires up so behaviours can affect the live game.</summary>
        internal static IScriptHost Host;

        /// <summary>World position of this behaviour's entity (read/write).</summary>
        public Vector3 Position
        {
            get => Host != null ? Host.GetPosition(EntityId) : Vector3.Zero;
            set { Host?.SetPosition(EntityId, value); }
        }

        /// <summary>Move this behaviour's entity by a delta.</summary>
        public void Translate(float dx, float dy, float dz)
        {
            var p = Position; p.X += dx; p.Y += dy; p.Z += dz; Position = p;
        }

        /// <summary>Euler rotation in degrees (X = pitch, Y = yaw, Z = roll) — read/write.</summary>
        public Vector3 Rotation
        {
            get => Host != null ? Host.GetRotation(EntityId) : Vector3.Zero;
            set { Host?.SetRotation(EntityId, value); }
        }

        /// <summary>Rotate this behaviour's entity by a delta (degrees).</summary>
        public void Rotate(float dPitch, float dYaw, float dRoll)
        {
            var r = Rotation; r.X += dPitch; r.Y += dYaw; r.Z += dRoll; Rotation = r;
        }

        /// <summary>Unit forward vector in world space, derived from this entity's yaw + pitch.</summary>
        public Vector3 Forward
        {
            get
            {
                var r = Rotation;
                double yaw = r.Y * Math.PI / 180.0, pitch = r.X * Math.PI / 180.0;
                return new Vector3(
                    (float)(Math.Sin(yaw) * Math.Cos(pitch)),
                    (float)(-Math.Sin(pitch)),
                    (float)(Math.Cos(yaw) * Math.Cos(pitch)));
            }
        }

        /// <summary>Unit right vector in world space (horizontal), derived from this entity's yaw.</summary>
        public Vector3 Right
        {
            get
            {
                double yaw = Rotation.Y * Math.PI / 180.0;
                return new Vector3((float)Math.Cos(yaw), 0f, (float)(-Math.Sin(yaw)));
            }
        }

        /// <summary>Set this entity's base color at runtime — e.g. flash a color when a trigger is touched.</summary>
        public void SetColor(float r, float g, float b) { Host?.SetEntityColor(EntityId, r, g, b); }

        /// <summary>This entity's AudioSource component as a script handle (Play/Stop/Pause/
        /// Resume, live Volume/Pitch), or null if the entity has none.</summary>
        public AudioSource GetAudioSource()
        {
            var entity = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(EntityId);
            var component = entity?.GetComponent<Editor.ECS.Components.Audio.AudioSource>();
            return component != null ? new AudioSource(component) : null;
        }

        /// <summary>Play an animation clip on this entity's Animator. Pass a clip NAME from the Animator's
        /// clip table (e.g. "Walk") or a .vanim path. fade &gt; 0 crossfades from the current pose (seconds).
        /// Returns false when the entity has no Animator / the clip can't be found.</summary>
        public bool PlayAnimation(string clip, float fade = 0f) { return Host != null && Host.PlayAnimation(EntityId, clip, fade); }

        /// <summary>Freeze this entity's animation on the current pose.</summary>
        public void StopAnimation() { Host?.StopAnimation(EntityId); }

        /// <summary>Playback speed multiplier for this entity's animation (1 = authored speed).</summary>
        public void SetAnimationSpeed(float speed) { Host?.SetAnimationSpeed(EntityId, speed); }

        /// <summary>Is an animation playing? Pass a clip name to ask about that clip specifically.</summary>
        public bool IsAnimationPlaying(string clip = null) { return Host != null && Host.IsAnimationPlaying(EntityId, clip); }

        /// <summary>Current playback time (seconds) of this entity's animation.</summary>
        public float AnimationTime { get { return Host != null ? Host.GetAnimationTime(EntityId) : 0f; } }

        public virtual void Start() { }
        public virtual void Update(float dt) { }
        public virtual void OnDestroy() { }

        /// <summary>Called when the playing clip crosses one of its EVENT markers (authored in the Keyframe
        /// Editor) — e.g. footstep sounds, attack hit frames. The marker's name is passed.</summary>
        public virtual void OnAnimationEvent(string name) { }

        /// <summary>Called once when another character first enters this entity's TRIGGER collider.</summary>
        public virtual void OnTriggerEnter(TriggerHit other) { }
        /// <summary>Called every tick while another character stays inside this entity's TRIGGER collider.</summary>
        public virtual void OnTriggerStay(TriggerHit other) { }
        /// <summary>Called once when another character leaves this entity's TRIGGER collider.</summary>
        public virtual void OnTriggerExit(TriggerHit other) { }
        /// <summary>Called once when a character first touches this entity's SOLID (non-trigger) collider.</summary>
        public virtual void OnCollisionEnter(TriggerHit other) { }
    }

    /// <summary>Keyboard + mouse input. Key names match WPF keys, e.g. "W", "Space", "LeftShift".</summary>
    public static class Input
    {
        internal static IScriptHost Host;
        public static bool GetKey(string key) => Host != null && Host.GetKey(key);

        /// <summary>Mouse movement since the last tick, in pixels (only non-zero while the game has
        /// captured the cursor — i.e. in play before ESC). Use it for mouse-look. Forced to 0 while a screen that
        /// opted into freezing gameplay (BlocksGameplay) is up, so mouse-look stops with movement.</summary>
        public static float MouseDeltaX { get { return (Editor.UI.Vui.VuiStack.Instance.GameplayInputBlocked || !WindowFocused) ? 0f : _mouseDeltaX; } internal set { _mouseDeltaX = value; } }
        public static float MouseDeltaY { get { return (Editor.UI.Vui.VuiStack.Instance.GameplayInputBlocked || !WindowFocused) ? 0f : _mouseDeltaY; } internal set { _mouseDeltaY = value; } }
        private static float _mouseDeltaX, _mouseDeltaY;

        // ---- Window focus: ALL input (keyboard, mouse, controller) is dead unless OUR window is the foreground
        // window. Works everywhere — in-editor play, the external game window, and an exported debug/release build
        // (they're all in this process) — so an unfocused/alt-tabbed game can't be driven by stray global input. ----
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        private static bool _focusApiMissing;
        /// <summary>True only while this app's window is the foreground window. Input is ignored otherwise.</summary>
        public static bool WindowFocused
        {
            get
            {
                if (_focusApiMissing) return true;
                try { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid == GetCurrentProcessId(); }
                catch { _focusApiMissing = true; return true; }
            }
        }

        // ---- Gamepad / controller (Windows.Gaming.Input incl. PlayStation, XInput fallback). Polled once per tick.
        // Sticks/triggers are normalized to -1..1 / 0..1 with dead zones; frozen to neutral while a gameplay-blocking
        // UI screen is up OR the window isn't focused, so the pad can't drive the player through a menu / in the bg. ----
        private static bool _padOn;
        private static float _lx, _ly, _rx, _ry, _lt, _rt;
        private static ushort _buttons, _prevButtons;
        private static bool Gated { get { return Editor.UI.Vui.VuiStack.Instance.GameplayInputBlocked || !WindowFocused; } }

        /// <summary>True while a controller is connected.</summary>
        public static bool GamepadConnected { get { return _padOn; } }
        /// <summary>Left stick, -1..1 (X right, Y up). 0 when gated by a blocking UI screen.</summary>
        public static float LeftStickX  { get { return Gated ? 0f : _lx; } }
        public static float LeftStickY  { get { return Gated ? 0f : _ly; } }
        /// <summary>Right stick, -1..1 (for look). 0 when gated.</summary>
        public static float RightStickX { get { return Gated ? 0f : _rx; } }
        public static float RightStickY { get { return Gated ? 0f : _ry; } }
        /// <summary>Triggers, 0..1.</summary>
        public static float LeftTrigger  { get { return Gated ? 0f : _lt; } }
        public static float RightTrigger { get { return Gated ? 0f : _rt; } }

        /// <summary>Is a controller button held? Names: A B X Y LB RB Back Start LeftStick RightStick
        /// DPadUp DPadDown DPadLeft DPadRight.</summary>
        public static bool GetGamepadButton(string name) { return !Gated && (_buttons & MaskOf(name)) != 0; }
        /// <summary>Was a controller button pressed THIS tick (edge)?</summary>
        public static bool GetGamepadButtonDown(string name)
        { ushort m = MaskOf(name); return !Gated && (_buttons & m) != 0 && (_prevButtons & m) == 0; }

        private static ushort MaskOf(string n)
        {
            if (string.IsNullOrEmpty(n)) return 0;
            switch (n.ToLowerInvariant())
            {
                case "dpadup": return 0x0001; case "dpaddown": return 0x0002;
                case "dpadleft": return 0x0004; case "dpadright": return 0x0008;
                case "start": return 0x0010; case "back": return 0x0020;
                case "leftstick": return 0x0040; case "rightstick": return 0x0080;
                case "lb": case "leftshoulder": return 0x0100; case "rb": case "rightshoulder": return 0x0200;
                case "a": return 0x1000; case "b": return 0x2000; case "x": return 0x4000; case "y": return 0x8000;
                default: return 0;
            }
        }

        /// <summary>Poll the first connected controller once per tick. Order: Windows.Gaming.Input.Gamepad
        /// (normalized — Xbox + any pad Windows maps as a gamepad, incl. DualSense on Win11) → RawGameController
        /// (a Sony DualSense/DualShock that wasn't mapped as a Gamepad, by vendor id) → XInput (last resort).
        /// Windows.Gaming.Input handles USB + Bluetooth + the DualSense HID internally, so a PS5 pad "just works"
        /// with no extra software.</summary>
        internal static void PollGamepad()
        {
            _prevButtons = _buttons;

            // No controller input while our window isn't focused.
            if (!WindowFocused) { _padOn = false; _buttons = 0; _lx = _ly = _rx = _ry = _lt = _rt = 0f; return; }

            if (!_wgiMissing)
            {
                try { if (PollWgi()) return; }               // Xbox or (Win11) DualSense via Windows.Gaming.Input
                catch (System.IO.FileNotFoundException) { _wgiMissing = true; }
                catch (TypeLoadException) { _wgiMissing = true; }
                catch (MissingMethodException) { _wgiMissing = true; }
                catch { /* transient WinRT error — fall through this frame instead of going dead */ }
            }

            // Direct DualSense/DualShock HID — deterministic, works even when Windows.Gaming.Input doesn't surface a
            // PS5 pad over USB (the reported "controller not accepted"). Isolated + guarded; a failure just falls on.
            try
            {
                if (Editor.Scripting.DualSenseHid.Poll())
                {
                    _padOn = true;
                    _lx = Editor.Scripting.DualSenseHid.LX; _ly = Editor.Scripting.DualSenseHid.LY;
                    _rx = Editor.Scripting.DualSenseHid.RX; _ry = Editor.Scripting.DualSenseHid.RY;
                    _lt = Editor.Scripting.DualSenseHid.L2; _rt = Editor.Scripting.DualSenseHid.R2;
                    _buttons = Editor.Scripting.DualSenseHid.Buttons;
                    return;
                }
            }
            catch { }

            // Last resort -> XInput (Xbox, or a DualSense mapped via Steam Input / DS4Windows).
            PollXInput();
        }

        // Windows.Gaming.Input: Gamepad first (normalized), else RawGameController (a PlayStation pad Windows didn't
        // surface as a Gamepad). Returns true only if a controller was actually found + read.
        private static bool PollWgi()
        {
            var pads = Windows.Gaming.Input.Gamepad.Gamepads;
            if (pads != null && pads.Count > 0)
            {
                var r = pads[0].GetCurrentReading();
                _padOn = true;
                _lx = Dead((float)r.LeftThumbstickX);
                _ly = Dead((float)r.LeftThumbstickY);
                _rx = Dead((float)r.RightThumbstickX);
                _ry = Dead((float)r.RightThumbstickY);
                _lt = Clamp01((float)r.LeftTrigger);
                _rt = Clamp01((float)r.RightTrigger);
                _buttons = MapWgiButtons(r.Buttons);
                return true;
            }
            return PollRawSony();
        }

        private static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
        private static float Dead(float v) { const float d = 0.16f; if (v > d) return (v - d) / (1f - d); if (v < -d) return (v + d) / (1f - d); return 0f; }

        private static ushort MapWgiButtons(Windows.Gaming.Input.GamepadButtons b)
        {
            var W = Windows.Gaming.Input.GamepadButtons.None;
            ushort m = 0;
            if ((b & Windows.Gaming.Input.GamepadButtons.A) != W) m |= 0x1000;             // PS: Cross
            if ((b & Windows.Gaming.Input.GamepadButtons.B) != W) m |= 0x2000;             // PS: Circle
            if ((b & Windows.Gaming.Input.GamepadButtons.X) != W) m |= 0x4000;             // PS: Square
            if ((b & Windows.Gaming.Input.GamepadButtons.Y) != W) m |= 0x8000;             // PS: Triangle
            if ((b & Windows.Gaming.Input.GamepadButtons.LeftShoulder) != W) m |= 0x0100;  // L1
            if ((b & Windows.Gaming.Input.GamepadButtons.RightShoulder) != W) m |= 0x0200; // R1
            if ((b & Windows.Gaming.Input.GamepadButtons.DPadUp) != W) m |= 0x0001;
            if ((b & Windows.Gaming.Input.GamepadButtons.DPadDown) != W) m |= 0x0002;
            if ((b & Windows.Gaming.Input.GamepadButtons.DPadLeft) != W) m |= 0x0004;
            if ((b & Windows.Gaming.Input.GamepadButtons.DPadRight) != W) m |= 0x0008;
            if ((b & Windows.Gaming.Input.GamepadButtons.Menu) != W) m |= 0x0010;          // Start / PS: Options
            if ((b & Windows.Gaming.Input.GamepadButtons.View) != W) m |= 0x0020;          // Back / PS: Create
            if ((b & Windows.Gaming.Input.GamepadButtons.LeftThumbstick) != W) m |= 0x0040;
            if ((b & Windows.Gaming.Input.GamepadButtons.RightThumbstick) != W) m |= 0x0080;
            return m;
        }

        // A PlayStation pad (DualSense/DualShock) that Windows didn't surface as a Gamepad — read it raw and map the
        // standard HID layout to the Xbox-style bitmask so scripts stay controller-agnostic. Prefer a Sony device;
        // otherwise take the first controller with sticks (covers other HID pads too).
        private static bool PollRawSony()
        {
            var raws = Windows.Gaming.Input.RawGameController.RawGameControllers;
            if (raws == null || raws.Count == 0) return false;
            Windows.Gaming.Input.RawGameController rc = null;
            foreach (var c in raws) { if (c.HardwareVendorId == 0x054C) { rc = c; break; } } // Sony
            if (rc == null) foreach (var c in raws) { if (c.AxisCount >= 4) { rc = c; break; } }
            if (rc == null) return false;
            {
                var btns = new bool[rc.ButtonCount];
                var sws = new Windows.Gaming.Input.GameControllerSwitchPosition[rc.SwitchCount];
                var ax = new double[rc.AxisCount];
                rc.GetCurrentReading(btns, sws, ax);
                _padOn = true;
                // DualSense/standard HID gamepad axis order: [0]=LX [1]=LY [2]=RX [3]=RY [4]=L2 [5]=R2 (0..1; sticks 0.5=center).
                _lx = ax.Length > 0 ? Dead((float)(ax[0] * 2 - 1)) : 0f;
                _ly = ax.Length > 1 ? Dead((float)-(ax[1] * 2 - 1)) : 0f; // HID Y is down-positive -> invert
                _rx = ax.Length > 2 ? Dead((float)(ax[2] * 2 - 1)) : 0f;
                _ry = ax.Length > 3 ? Dead((float)-(ax[3] * 2 - 1)) : 0f;
                _lt = ax.Length > 4 ? Clamp01((float)ax[4]) : 0f;
                _rt = ax.Length > 5 ? Clamp01((float)ax[5]) : 0f;
                ushort m = 0;
                if (Btn(btns, 1)) m |= 0x1000; // Cross  -> A
                if (Btn(btns, 2)) m |= 0x2000; // Circle -> B
                if (Btn(btns, 0)) m |= 0x4000; // Square -> X
                if (Btn(btns, 3)) m |= 0x8000; // Triangle -> Y
                if (Btn(btns, 4)) m |= 0x0100; // L1
                if (Btn(btns, 5)) m |= 0x0200; // R1
                if (Btn(btns, 9)) m |= 0x0010; // Options -> Start
                if (Btn(btns, 8)) m |= 0x0020; // Create  -> Back
                if (Btn(btns, 10)) m |= 0x0040; // L3
                if (Btn(btns, 11)) m |= 0x0080; // R3
                if (sws.Length > 0)
                {
                    switch (sws[0])
                    {
                        case Windows.Gaming.Input.GameControllerSwitchPosition.Up:        m |= 0x0001; break;
                        case Windows.Gaming.Input.GameControllerSwitchPosition.UpRight:   m |= 0x0001 | 0x0008; break;
                        case Windows.Gaming.Input.GameControllerSwitchPosition.Right:     m |= 0x0008; break;
                        case Windows.Gaming.Input.GameControllerSwitchPosition.DownRight: m |= 0x0002 | 0x0008; break;
                        case Windows.Gaming.Input.GameControllerSwitchPosition.Down:      m |= 0x0002; break;
                        case Windows.Gaming.Input.GameControllerSwitchPosition.DownLeft:  m |= 0x0002 | 0x0004; break;
                        case Windows.Gaming.Input.GameControllerSwitchPosition.Left:      m |= 0x0004; break;
                        case Windows.Gaming.Input.GameControllerSwitchPosition.UpLeft:    m |= 0x0001 | 0x0004; break;
                    }
                }
                _buttons = m;
                return true;
            }
            return false;
        }

        private static bool Btn(bool[] a, int i) { return i >= 0 && i < a.Length && a[i]; }
        private static bool _wgiMissing;

        // ---- XInput fallback (only if WinRT is unavailable) ----
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger; public byte bRightTrigger; public short sThumbLX; public short sThumbLY; public short sThumbRX; public short sThumbRY; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
        [System.Runtime.InteropServices.DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);
        private static bool _xinputMissing;

        private static void PollXInput()
        {
            if (_xinputMissing) { _padOn = false; return; }
            try
            {
                for (uint i = 0; i < 4; i++)
                {
                    XINPUT_STATE s;
                    if (XInputGetState(i, out s) == 0)
                    {
                        _padOn = true;
                        _buttons = s.Gamepad.wButtons;
                        _lx = Stick(s.Gamepad.sThumbLX, 7849);
                        _ly = Stick(s.Gamepad.sThumbLY, 7849);
                        _rx = Stick(s.Gamepad.sThumbRX, 8689);
                        _ry = Stick(s.Gamepad.sThumbRY, 8689);
                        _lt = Trigger(s.Gamepad.bLeftTrigger);
                        _rt = Trigger(s.Gamepad.bRightTrigger);
                        return;
                    }
                }
                _padOn = false; _buttons = 0; _lx = _ly = _rx = _ry = _lt = _rt = 0f;
            }
            catch (DllNotFoundException) { _xinputMissing = true; _padOn = false; }
            catch { _padOn = false; }
        }

        private static float Stick(short v, int dead)
        {
            float f = v;
            if (f > dead) f = (f - dead) / (32767f - dead);
            else if (f < -dead) f = (f + dead) / (32768f - dead);
            else f = 0f;
            return f < -1f ? -1f : (f > 1f ? 1f : f);
        }
        private static float Trigger(byte t) { return t <= 30 ? 0f : (t - 30) / (255f - 30f); }
    }

    /// <summary>Frame timing.</summary>
    public static class Time
    {
        /// <summary>Seconds since the last tick (the runtime sets this each frame).</summary>
        public static float DeltaTime { get; internal set; }
    }

    /// <summary>
    /// Scene control. Generic engine API — the GAME decides WHEN/WHICH scene (e.g. lobby PLAY -&gt; "Match",
    /// death -&gt; "Lobby"). The switch is deferred to the end of the current tick so it's safe to call from
    /// inside a behaviour's Update. Scene names match the scenes authored in the project.
    /// </summary>
    public static class Scene
    {
        internal static IScriptHost Host;
        public static void Load(string name) { if (Host != null) Host.LoadScene(name); }
    }

    /// <summary>Mouse mode. Locked = captured + hidden for mouse-look (gameplay). Unlocked = free cursor so
    /// the player can click the UI (lobby / ESC menu / shop). The game sets this; the engine enforces it.</summary>
    public static class Cursor
    {
        internal static IScriptHost Host;
        public static bool Locked
        {
            get { return Host != null && Host.GetCursorLocked(); }
            set { if (Host != null) Host.SetCursorLocked(value); }
        }
    }

    /// <summary>Application-level control for the game.</summary>
    public static class Application
    {
        internal static IScriptHost Host;
        /// <summary>Quit the game (closes the standalone player / stops play).</summary>
        public static void Quit() { if (Host != null) Host.QuitGame(); }
    }

    /// <summary>Player view camera control (the live game/play view).</summary>
    public static class Camera
    {
        internal static IScriptHost Host;
        /// <summary>Vertical field of view in degrees (clamped 30–120 by the engine).</summary>
        public static void SetFieldOfView(float fovDegrees) { if (Host != null) Host.SetCameraFov(fovDegrees); }
    }

    /// <summary>
    /// Collision. <see cref="MoveCharacter"/> resolves a character capsule (feet position, radius, height) against
    /// the scene's Collider components with collide-and-slide: the ground is solid, you can't walk through
    /// walls/props/models, and you can't clip through even up close. <see cref="Grounded"/> is true when the last
    /// move ended resting on a surface (use it to reset jumping/gravity). Add Colliders to your level objects in
    /// the editor; the character itself needs no collider — you pass its capsule to MoveCharacter each frame.
    /// </summary>
    public static class Physics
    {
        internal static IScriptHost Host;
        private static bool _grounded;
        public static bool Grounded { get { return _grounded; } }

        /// <summary>Move a character capsule (feet = the entity/eye base, radius, total height) by <paramref name="move"/>
        /// and return the collision-resolved feet position. Call it each frame with your desired displacement
        /// (input + gravity). No collision world yet → returns feet+move unchanged.</summary>
        public static Vector3 MoveCharacter(Vector3 feet, float radius, float height, Vector3 move)
        {
            return MoveCharacter(feet, radius, height, move, 0);
        }

        /// <summary>As above, but <paramref name="characterId"/> (e.g. your entity's EntityId) registers this
        /// character so OTHER characters can't walk through it — for multiplayer / multiple actors.</summary>
        public static Vector3 MoveCharacter(Vector3 feet, float radius, float height, Vector3 move, long characterId)
        {
            if (Host == null) return new Vector3(feet.X + move.X, feet.Y + move.Y, feet.Z + move.Z);
            bool g; var r = Host.MoveCharacter(feet, radius, height, move, out g, characterId); _grounded = g; return r;
        }
    }

    /// <summary>
    /// Skeletal animation on OTHER entities (your own entity has PlayAnimation() directly on the
    /// behaviour). Clip = a name from the target's Animator clip table (e.g. "Walk") or a .vanim path.
    /// Animation state machines are game logic — build them in scripts with these calls.
    /// </summary>
    public static class Animation
    {
        internal static IScriptHost Host;

        /// <summary>Play a clip on an entity's Animator; fade &gt; 0 crossfades (seconds).</summary>
        public static bool Play(long entityId, string clip, float fade = 0f)
            { return Host != null && Host.PlayAnimation(entityId, clip, fade); }

        /// <summary>Freeze an entity's animation on its current pose.</summary>
        public static void Stop(long entityId) { if (Host != null) Host.StopAnimation(entityId); }

        /// <summary>Playback speed multiplier (1 = authored speed).</summary>
        public static void SetSpeed(long entityId, float speed) { if (Host != null) Host.SetAnimationSpeed(entityId, speed); }

        /// <summary>Is an animation playing on the entity? Pass a clip name to ask about that clip.</summary>
        public static bool IsPlaying(long entityId, string clip = null)
            { return Host != null && Host.IsAnimationPlaying(entityId, clip); }

        /// <summary>Current playback time in seconds.</summary>
        public static float Time(long entityId) { return Host != null ? Host.GetAnimationTime(entityId) : 0f; }
    }

    /// <summary>
    /// Immediate-mode 2D UI drawn by the engine OVER the 3D (same swapchain — works over the live game).
    /// Call these from a behaviour's Update each frame; coordinates are viewport pixels (top-left origin).
    /// This is the generic engine UI; a game builds its own lobby/HUD with it (no UI code in the engine).
    /// </summary>
    public static class UI
    {
        internal static IScriptHost Host;

        /// <summary>Viewport size in pixels.</summary>
        public static float Width { get { return Host != null ? Host.UIWidth() : 0f; } }
        public static float Height { get { return Host != null ? Host.UIHeight() : 0f; } }
        /// <summary>Mouse position in viewport pixels (top-left origin).</summary>
        public static float MouseX { get { return Host != null ? Host.UIMouseX() : 0f; } }
        public static float MouseY { get { return Host != null ? Host.UIMouseY() : 0f; } }
        public static bool MouseDown { get { return Host != null && Host.UIMouseDown(); } }

        /// <summary>Filled rectangle (radius &gt; 0 = rounded).</summary>
        public static void Rect(float x, float y, float w, float h, Color c, float radius)
        {
            if (Host != null) Host.UIRect(x, y, w, h, c.R, c.G, c.B, c.A, radius);
        }
        public static void Rect(float x, float y, float w, float h, Color c) { Rect(x, y, w, h, c, 0f); }

        /// <summary>Text in a box. align: 0 left, 1 center, 2 right. weight: 400/600/700.</summary>
        public static void Text(string text, float x, float y, float w, float h, float size, Color c, int align, int weight)
        {
            if (Host != null) Host.UIText(x, y, w, h, text, size, c.R, c.G, c.B, c.A, align, weight);
        }
        public static void Text(string text, float x, float y, float w, float h, float size, Color c) { Text(text, x, y, w, h, size, c, 0, 600); }

        public static void Line(float x1, float y1, float x2, float y2, Color c, float thick)
        {
            if (Host != null) Host.UILine(x1, y1, x2, y2, c.R, c.G, c.B, c.A, thick);
        }

        /// <summary>Textured quad (PNG/JPG). path = absolute or project-relative. tint multiplies; tint.A = opacity.</summary>
        public static void Image(string path, float x, float y, float w, float h, Color tint)
        {
            if (Host != null) Host.UIImage(x, y, w, h, path, tint.R, tint.G, tint.B, tint.A);
        }
        public static void Image(string path, float x, float y, float w, float h) { Image(path, x, y, w, h, new Color(1f, 1f, 1f, 1f)); }

        /// <summary>True while the cursor is inside the box.</summary>
        public static bool Hover(float x, float y, float w, float h)
        {
            float mx = MouseX, my = MouseY;
            return mx >= x && mx <= x + w && my >= y && my <= y + h;
        }

        /// <summary>A clickable button (returns true on the click). Lightens on hover.</summary>
        public static bool Button(float x, float y, float w, float h, string label, Color bg, Color fg, float size, float radius)
        {
            bool hover = Hover(x, y, w, h);
            Color face = hover ? new Color(Clamp01(bg.R + 0.09f), Clamp01(bg.G + 0.09f), Clamp01(bg.B + 0.09f), bg.A) : bg;
            Rect(x, y, w, h, face, radius);
            Text(label, x, y, w, h, size, fg, 1, 700);
            return hover && Host != null && Host.UIMousePressed();
        }

        private static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
    }

    /// <summary>A loaded retained-UI screen (.vui). Drive named slots + read events by stable id; gameplay logic
    /// stays in the script — the canvas is just a renderer/router.</summary>
    public sealed class VuiHandle
    {
        internal Editor.UI.Vui.VuiCanvas C;
        internal VuiHandle(Editor.UI.Vui.VuiCanvas c) { C = c; }
        public bool IsValid { get { return C != null; } }
        public void Show() { if (C != null) Editor.UI.Vui.VuiStack.Instance.Show(C); }
        public void Hide() { if (C != null) Editor.UI.Vui.VuiStack.Instance.Hide(C); }
        public void SetValue(string id, float v) { if (C != null) C.SetValue(id, v); }
        public void SetText(string id, string t) { if (C != null) C.SetText(id, t); }
        public void SetVisible(string id, bool v) { if (C != null) C.SetVisible(id, v); }
        public void SetColor(string id, Color c) { if (C != null) C.SetColor(id, c.R, c.G, c.B, c.A); }
        public void SetImage(string id, string asset) { if (C != null) C.SetImage(id, asset); }
        public void SetList(string id, System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyDictionary<string, string>> rows) { if (C != null) C.SetList(id, rows); }
        public bool WasClicked(string id) { return C != null && C.WasClicked(id); }
        public float GetSlider(string id) { return C != null ? C.GetSlider(id) : 0f; }
        public bool GetToggle(string id) { return C != null && C.GetToggle(id); }
        public string GetText(string id) { return C != null ? C.GetText(id) : ""; }
        public int GetStep(string id) { return C != null ? C.GetStep(id) : 0; }
        public int GetCapturedKey(string id) { return C != null ? C.GetCapturedKey(id) : 0; }
    }

    /// <summary>Retained-mode 2D UI: load .vui screens, stack them, drive them by id. Sits beside the immediate-mode
    /// <see cref="UI"/> facade (both draw into the same frame). Gameplay stays in scripts.</summary>
    public static class Gui
    {
        public static VuiHandle Load(string name) { return new VuiHandle(Editor.UI.Vui.VuiStack.Instance.Load(name)); }
        public static VuiHandle Push(string name) { return new VuiHandle(Editor.UI.Vui.VuiStack.Instance.Push(name)); }
        public static void Pop() { Editor.UI.Vui.VuiStack.Instance.Pop(); }
        public static bool HasScreens { get { return Editor.UI.Vui.VuiStack.Instance.HasActiveScreens; } }
    }

    /// <summary>Generic engine settings a script's options menu applies (the UI only surfaces values; the script
    /// reads the widgets + calls these). Resolution / render-scale / volume land with feature #3 (ESC menu).</summary>
    public static class Settings
    {
        public static void SetVSync(bool on) { Editor.DllWrapper.VortexAPI.SetGameHostVSync(on); }
        public static void ToggleFullscreen() { Editor.DllWrapper.VortexAPI.GameHostToggleFullscreen(); }
        public static bool IsFullscreen { get { return Editor.DllWrapper.VortexAPI.GameHostIsFullscreen(); } }

        /// <summary>Set fullscreen to a specific state (idempotent — toggles only if it differs).</summary>
        public static void SetFullscreen(bool on) { if (on != IsFullscreen) ToggleFullscreen(); }

        /// <summary>Field of view in degrees (the renderer-global projection FOV).</summary>
        public static void SetFieldOfView(float degrees) { Camera.SetFieldOfView(degrees); }

        /// <summary>Window resolution (windowed only); resizes the client area + swapchain.</summary>
        public static void SetResolution(int width, int height) { Editor.DllWrapper.VortexAPI.GameHostSetResolution(width, height); }

        /// <summary>Render scale 0.25..2.0 — the 3D scene renders into a scaled offscreen RT then upscales (perf).
        /// 1.0 = native. Stored in the renderer now; the scaled-RT upscale pass applies it.</summary>
        public static void SetRenderScale(float scale) { Editor.DllWrapper.VortexAPI.SetRenderScale(scale); }
        public static float RenderScale { get { return Editor.DllWrapper.VortexAPI.GetRenderScale(); } }

        /// <summary>DLSS quality: 0=Off, 1=Quality, 2=Balanced, 3=Performance, 4=Ultra Performance. A non-off mode
        /// renders the 3D at a lower resolution and AI-upscales it to native (big perf win on RTX). Only takes
        /// visible effect when <see cref="DlssSupported"/> is true; otherwise it falls back to a bilinear upscale.</summary>
        public static void SetDlssMode(int mode) { Editor.DllWrapper.VortexAPI.SetDlssMode(mode); }
        public static int DlssMode { get { return Editor.DllWrapper.VortexAPI.GetDlssMode(); } }

        /// <summary>DLSS Frame Generation: 0=Off, 1=x2, 2=x3, 3=x4 — the GPU inserts N AI-generated frames at Present
        /// per real frame (smoother motion). SEPARATE from <see cref="SetDlssMode"/> (super-resolution). Enables
        /// Reflex internally. Needs <see cref="DlssSupported"/>; best at LOW real framerates with VSync on.</summary>
        public static void SetFrameGenMode(int mode) { Editor.DllWrapper.VortexAPI.SetFrameGenMode(mode); }
        public static int FrameGenMode { get { return Editor.DllWrapper.VortexAPI.GetFrameGenMode(); } }
        /// <summary>Smoothed PRESENTED-FPS rate (real + AI-generated frames/sec) — the "Shown FPS" with Frame Gen on.
        /// Accumulated once per frame in the engine, so reading it from multiple places is safe. 0 when FG is off.</summary>
        public static int FrameGenPresentedFps { get { return Editor.DllWrapper.VortexAPI.FrameGenPresentedFps(); } }

        /// <summary>Current REAL (rendered) frames per second — the engine frame counter. With Frame Gen on this stays
        /// at the rendered rate (the generated frames show up in <see cref="FrameGenPresentedFps"/>, not here).</summary>
        public static int CurrentFps { get { return Editor.DllWrapper.VortexAPI.CurrentFPS; } }

        /// <summary>Master volume 0..1. Stored here until the (XAudio2) sound engine reads it — audio is still a stub.</summary>
        public static float MasterVolume { get; private set; } = 1f;
        public static void SetMasterVolume(float v) { MasterVolume = v < 0f ? 0f : (v > 1f ? 1f : v); }

        /// <summary>The selected GPU's name (e.g. "NVIDIA GeForce RTX 5070").</summary>
        public static string GpuName { get { return Editor.DllWrapper.VortexAPI.GpuName(); } }
        /// <summary>True only on an NVIDIA RTX GPU — gate DLSS options on this (render-scale is the universal fallback).</summary>
        public static bool DlssSupported { get { return Editor.DllWrapper.VortexAPI.GpuSupportsDlss(); } }
    }

    /// <summary>Lighting/atmosphere control for game scripts — flicker, lightning, mood. With submit-once a static
    /// scene keeps whatever the script last set, so per-frame changes here drive a living, flickering environment.</summary>
    public static class Lighting
    {
        /// <summary>Global ambient strength (0 = pitch black, 1 = flat-lit). Dip it for darkness/flicker.</summary>
        public static void SetAmbient(float strength) { Editor.DllWrapper.VortexAPI.SetAmbientLightStrength(strength); }
        /// <summary>The sun/key directional light: direction (dx,dy,dz), color (r,g,b 0..1), and intensity.</summary>
        public static void SetDirectional(float dx, float dy, float dz, float r, float g, float b, float intensity)
            { Editor.DllWrapper.VortexAPI.SetDirectionalLightParams(dx, dy, dz, r, g, b, intensity); }
        public static void ClearLights() { Editor.DllWrapper.VortexAPI.ClearAllLights(); }
    }

    /// <summary>Script-driven world geometry — assemble a level/backdrop from meshes without authoring a scene
    /// file. Add(meshPath, x,y,z, yawDeg, scale) places a model; placements persist until Clear(). Render-only
    /// (no collision yet) — perfect for greybox levels + the lobby's creepy motel backdrop.</summary>
    public static class World
    {
        public static void Add(string meshPath, float x, float y, float z, float yawDegrees, float scale)
            { Editor.Core.Services.WorldService.Add(Resolve(meshPath), x, y, z, yawDegrees, scale); }
        public static void Clear() { Editor.Core.Services.WorldService.Clear(); }

        private static string Resolve(string p)
        {
            try
            {
                if (System.IO.File.Exists(p)) return p;
                var proj = Editor.Core.Data.ProjectData.Current != null ? Editor.Core.Data.ProjectData.Current.Path : null;
                if (!string.IsNullOrEmpty(proj)) { var f = System.IO.Path.Combine(proj, p); if (System.IO.File.Exists(f)) return f; }
            }
            catch { }
            return p;
        }
    }

    /// <summary>
    /// Game audio for scripts. Clip paths are project-relative ("Assets/Audio/scream.wav")
    /// and resolve identically in editor play mode and shipped .vpak builds. One-shots use
    /// pooled voices that auto-reclaim — nothing to hold on to or free.
    /// <code>
    /// // Jump-scare stinger when the player trips a trigger:
    /// public class ScareTrigger : VortexBehaviour
    /// {
    ///     public override void OnTriggerEnter(TriggerHit hit)
    ///     {
    ///         if (hit.Tag != "Player") return;
    ///         Audio.PlayOneShot("Assets/Audio/stinger.wav", Position, 1f);   // 3D, at this entity
    ///         Audio.Music.CrossFade("Assets/Audio/chase.ogg", 2f);           // chase music sneaks in
    ///     }
    /// }
    /// </code>
    /// </summary>
    public static class Audio
    {
        /// <summary>Play a positional (3D) one-shot at a world position — no entity needed.
        /// Distance attenuation uses sensible defaults (min 1, max 500, logarithmic).</summary>
        public static void PlayOneShot(string clipPath, Vector3 position, float volume = 1f, float pitch = 1f)
            => Editor.Core.Services.AudioPlaybackService.Instance.PlayOneShot(clipPath, position.X, position.Y, position.Z, volume, pitch);

        /// <summary>Play a flat 2D one-shot (UI clicks, stingers) — no position, no attenuation.</summary>
        public static void PlayOneShot2D(string clipPath, float volume = 1f, float pitch = 1f)
            => Editor.Core.Services.AudioPlaybackService.Instance.PlayOneShot2D(clipPath, volume, pitch);

        /// <summary>Mixer bus volume by name ("Master", "Music", "SFX", "Ambience", "UI"),
        /// 0..1 — the settings-screen sliders call this. Applies in real time.</summary>
        public static void SetBusVolume(string busName, float volume)
        {
            var bus = Editor.DllWrapper.VortexAudio.BusIndexFromName(busName);
            if (bus >= 0)
            {
                Editor.DllWrapper.VortexAudio.SetBusVolume(bus, volume);
                // Shipped game: persist the player's choice so it survives a restart (#20). No-op in the editor.
                Editor.Core.Services.GameAudioSettings.Instance.Persist();
            }
        }

        public static float GetBusVolume(string busName)
        {
            var bus = Editor.DllWrapper.VortexAudio.BusIndexFromName(busName);
            return bus >= 0 ? Editor.DllWrapper.VortexAudio.GetBusVolume(bus) : 1f;
        }

        /// <summary>The music channel: one streamed, looping track at priority 0 (never stolen),
        /// with fade-in and crossfade. Fades are frame-ticked ramps for now (native envelopes
        /// arrive with the fade-envelope feature); the API shape is final.</summary>
        public static class Music
        {
            /// <summary>Start a track, fading in over fadeInSeconds (0 = immediate). A track
            /// that is already playing is faded out quickly and replaced.</summary>
            public static void Play(string clipPath, float fadeInSeconds = 0f)
                => Editor.Core.Services.AudioPlaybackService.Instance.MusicPlay(clipPath, fadeInSeconds);

            /// <summary>Fade the current track out while the new one fades in, overlapping.</summary>
            public static void CrossFade(string clipPath, float seconds)
                => Editor.Core.Services.AudioPlaybackService.Instance.MusicCrossFade(clipPath, seconds);

            public static void Stop(float fadeOutSeconds = 0f)
                => Editor.Core.Services.AudioPlaybackService.Instance.MusicStop(fadeOutSeconds);

            public static bool IsPlaying
                => Editor.Core.Services.AudioPlaybackService.Instance.MusicIsPlaying;

            /// <summary>Music channel volume (multiplies the per-track fades).</summary>
            public static float Volume
            {
                get => Editor.Core.Services.AudioPlaybackService.Instance.MusicVolume;
                set => Editor.Core.Services.AudioPlaybackService.Instance.MusicVolume = value;
            }
        }
    }

    /// <summary>
    /// Script-side handle to an entity's AudioSource component — get it via
    /// <see cref="VortexBehaviour.GetAudioSource"/>. Play/Stop/Pause/Resume control the
    /// component's voice; Volume/Pitch write through to the component, so inspector and
    /// script always agree.
    /// </summary>
    public sealed class AudioSource
    {
        private readonly Editor.ECS.Components.Audio.AudioSource _component;
        internal AudioSource(Editor.ECS.Components.Audio.AudioSource component) { _component = component; }

        /// <summary>(Re)start this source's clip from the beginning — works regardless of PlayOnAwake.</summary>
        public void Play() => Editor.Core.Services.AudioPlaybackService.Instance.ScriptPlay(_component);
        public void Stop() => Editor.Core.Services.AudioPlaybackService.Instance.ScriptStop(_component);
        public void Pause() => Editor.Core.Services.AudioPlaybackService.Instance.ScriptPause(_component);
        public void Resume() => Editor.Core.Services.AudioPlaybackService.Instance.ScriptResume(_component);
        public bool IsPlaying => Editor.Core.Services.AudioPlaybackService.Instance.ScriptIsPlaying(_component);

        /// <summary>(Re)start silent and glide to full volume over <paramref name="seconds"/> —
        /// ambience swells, creeping dread. Sample-accurate, no zipper noise.</summary>
        public void FadeIn(float seconds) => Editor.Core.Services.AudioPlaybackService.Instance.ScriptFadeIn(_component, seconds);

        /// <summary>Glide to silence over <paramref name="seconds"/>, then stop and free the voice.</summary>
        public void FadeOut(float seconds) => Editor.Core.Services.AudioPlaybackService.Instance.ScriptFadeOut(_component, seconds);

        /// <summary>Glide the fade envelope (0..1, on top of Volume) to a live target —
        /// duck a heartbeat under dialogue, swell a drone. Retargets smoothly mid-fade.</summary>
        public void FadeTo(float target, float seconds) => Editor.Core.Services.AudioPlaybackService.Instance.ScriptFadeTo(_component, target, seconds);

        /// <summary>Live volume (0..1) — audible immediately while playing.</summary>
        public float Volume { get => _component.Volume; set => _component.Volume = value; }
        /// <summary>Live pitch — audible immediately while playing.</summary>
        public float Pitch { get => _component.Pitch; set => _component.Pitch = value; }
        public bool Loop { get => _component.Loop; set => _component.Loop = value; }
        /// <summary>Project-relative clip path; takes effect on the next Play().</summary>
        public string Clip { get => _component.AudioClipPath; set => _component.AudioClipPath = value; }
    }
}
