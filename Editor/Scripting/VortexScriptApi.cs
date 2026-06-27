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

        // Request switching the active scene by name (deferred — applied by the runtime after this tick).
        void LoadScene(string name);

        // Mouse mode: locked = captured + hidden for mouse-look (gameplay); unlocked = free cursor for UI.
        bool GetCursorLocked();
        void SetCursorLocked(bool locked);

        // 2D UI overlay (immediate mode), coordinates in viewport pixels (top-left origin).
        void UIRect(float x, float y, float w, float h, float r, float g, float b, float a, float radius);
        void UIText(float x, float y, float w, float h, string text, float size, float r, float g, float b, float a, int align, int weight);
        void UILine(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thick);
        float UIWidth();
        float UIHeight();
        float UIMouseX();
        float UIMouseY();
        bool UIMouseDown();
        bool UIMousePressed();
    }

    /// <summary>
    /// Base class for all gameplay behaviours — like MonoBehaviour. Override Start (called once when
    /// play begins) and Update (called every tick). Move your entity via Position / Translate, read
    /// input via Input.GetKey, and timing via Time.DeltaTime.
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

        public virtual void Start() { }
        public virtual void Update(float dt) { }
        public virtual void OnDestroy() { }
    }

    /// <summary>Keyboard + mouse input. Key names match WPF keys, e.g. "W", "Space", "LeftShift".</summary>
    public static class Input
    {
        internal static IScriptHost Host;
        public static bool GetKey(string key) => Host != null && Host.GetKey(key);

        /// <summary>Mouse movement since the last tick, in pixels (only non-zero while the game has
        /// captured the cursor — i.e. in play before ESC). Use it for mouse-look.</summary>
        public static float MouseDeltaX { get; internal set; }
        public static float MouseDeltaY { get; internal set; }
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
}
