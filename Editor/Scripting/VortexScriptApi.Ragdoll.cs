// Ragdoll scripting API (GitHub #104). The engine builds and simulates the ragdoll; WHEN a character goes limp is
// gameplay, so it stays in project scripts. Entities are addressed by their script handle (EntityId, Scene.Find,
// RaycastHit.EntityId). Examples are C# 5 (they compile in every editor).
using Editor.Core.Services.Physics;
using Editor.Scripting;
using SysVec = System.Numerics.Vector3;

namespace Vortex
{
    /// <summary>
    /// Ragdoll physics for animated characters. Give the character a <b>Ragdoll</b> component (it needs an Animator and a
    /// rigged mesh), then switch it on from its script — usually on death:
    /// <code>
    /// public class Enemy : VortexBehaviour
    /// {
    ///     public float Health = 100f;
    ///     public override void OnMessage(string message, object arg)
    ///     {
    ///         if (message == "damage" &amp;&amp; arg is float &amp;&amp; Health &gt; 0f)
    ///         {
    ///             Health -= (float)arg;
    ///             if (Health &lt;= 0f) Ragdoll.Activate(EntityId);   // falls from its current pose
    ///         }
    ///     }
    /// }
    /// </code>
    /// Shots keep working on the body: <c>Physics.AddImpulseAtPoint(hit.EntityId, …)</c> pushes the limb that was hit,
    /// and <c>Physics.Raycast</c> hits the limbs (the hit reports the character's entity).
    /// </summary>
    public static class Ragdoll
    {
        private static Editor.ECS.GameEntity E(long entity) { return ScriptRuntime.Instance.FindEntityByHandle(entity); }
        private static SysVec V(Vector3 v) { return new SysVec(v.X, v.Y, v.Z); }

        /// <summary>Turn the character into a ragdoll at its current pose (it keeps its momentum). False when the
        /// entity has no recognisable humanoid skeleton or physics is not running — the console says why.</summary>
        public static bool Activate(long entity) { return RagdollService.Activate(E(entity)); }

        /// <summary>Ragdoll, and shove the body part nearest <paramref name="point"/> with <paramref name="impulse"/>
        /// (N·s, world space) — e.g. <c>Ragdoll.Activate(id, shotDir * 60f, hit.Point)</c>.</summary>
        public static bool Activate(long entity, Vector3 impulse, Vector3 point) { return RagdollService.Activate(E(entity), V(impulse), V(point)); }

        /// <summary>Hand the character back to its Animator (the physics bodies are removed). The entity stays where it
        /// is: move it to <see cref="Position"/> first if it should get up where it fell.</summary>
        public static void Deactivate(long entity) { RagdollService.Deactivate(E(entity)); }

        /// <summary>Is the character a ragdoll right now?</summary>
        public static bool IsActive(long entity) { return RagdollService.IsActive(E(entity)); }

        /// <summary>Shove the body part nearest <paramref name="point"/> (N·s). False when the entity is not a ragdoll.</summary>
        public static bool AddImpulse(long entity, Vector3 impulse, Vector3 point) { return RagdollService.AddImpulseAtPoint(E(entity), V(impulse), V(point)); }

        /// <summary>Push the whole body (every part by its share of the mass) — explosions.</summary>
        public static bool AddImpulse(long entity, Vector3 impulse) { return RagdollService.AddImpulse(E(entity), V(impulse)); }

        /// <summary>Where the body lies: the pelvis of an active ragdoll, else the entity's position.</summary>
        public static Vector3 Position(long entity)
        {
            var p = RagdollService.PelvisPosition(E(entity));
            return new Vector3(p.X, p.Y, p.Z);
        }
    }
}
