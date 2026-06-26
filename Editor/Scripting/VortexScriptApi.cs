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

    /// <summary>Implemented by the engine (ScriptRuntime); lets behaviours touch the live game.</summary>
    public interface IScriptHost
    {
        Vector3 GetPosition(long entityId);
        void SetPosition(long entityId, Vector3 position);
        Vector3 GetRotation(long entityId);
        void SetRotation(long entityId, Vector3 eulerDegrees);
        bool GetKey(string key);
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
}
