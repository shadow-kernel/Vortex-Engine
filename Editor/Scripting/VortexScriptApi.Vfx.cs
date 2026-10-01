// Vortex scripting API — visual effects (VFX epic #116). Particle systems authored as .vfx files (Particle Editor),
// placed with a ParticleSystem component or spawned from code. Runtime: Editor.Core.Services.Particles.ParticleService.
using System;
using Editor.Core.Services.Particles;
using Editor.ECS.Components.Rendering;

namespace Vortex
{
    /// <summary>
    /// A spawned effect or beam (<see cref="Vfx.SpawnAt(string, Vector3, Quaternion)"/>, <see cref="Vfx.Beam"/>). Copyable;
    /// the default value is invalid. One-shot effects remove themselves when their particles are gone.
    /// </summary>
    public struct VfxHandle
    {
        public long Id;
        public VfxHandle(long id) { Id = id; }

        /// <summary>True while the effect still exists (emitting or with live particles / a live beam).</summary>
        public bool IsAlive { get { return Id != 0 && ParticleService.IsSpawnedAlive(Id); } }
        /// <summary>Live particles of the effect.</summary>
        public int ParticleCount { get { return Id != 0 ? ParticleService.SpawnedAliveCount(Id) : 0; } }
        /// <summary>Stop emitting; the particles fade out naturally (clear = remove them now).</summary>
        public void Stop(bool clear = false) { if (Id != 0) ParticleService.StopSpawned(Id, clear); }
        /// <summary>Remove the effect immediately.</summary>
        public void Destroy() { if (Id != 0) ParticleService.DestroySpawned(Id); Id = 0; }
        /// <summary>Move the effect (its emitters follow; world-space particles already emitted stay behind).</summary>
        public void SetPose(Vector3 position, Quaternion rotation) { if (Id != 0) ParticleService.MoveSpawned(Id, Vfx.Sys(position), Vfx.Sys(rotation)); }
        public void SetPosition(Vector3 position) { SetPose(position, Quaternion.Identity); }
        /// <summary>Beams: new end points.</summary>
        public void SetPoints(Vector3 from, Vector3 to) { if (Id != 0) ParticleService.SetBeamPoints(Id, Vfx.Sys(from), Vfx.Sys(to)); }
        public override string ToString() { return "VfxHandle(" + Id + ")"; }
    }

    /// <summary>
    /// Visual effects. Entity overloads act on every ParticleSystem component of the entity and its children (a weapon
    /// with a muzzle-flash child: <c>Vfx.Play(weaponId)</c>). Paths are project-relative (<c>"Assets/VFX/Sparks.vfx"</c>).
    /// <code>
    /// Vfx.Burst(muzzle, 20);                                               // sparks from the muzzle entity
    /// Vfx.SpawnAt("Assets/VFX/ImpactDust.vfx", hit.Point, Quaternion.LookRotation(hit.Normal, Vector3.Up));
    /// Vfx.Beam(muzzlePos, hit.Point, "Assets/VFX/Tracer.vfx");            // bullet tracer
    /// </code>
    /// </summary>
    public static class Vfx
    {
        /// <summary>Start (or resume) the entity's effects; finished one-shots restart.</summary>
        public static void Play(long entity) { foreach (var ps in Systems(entity)) ParticleService.Play(ps); }

        /// <summary>Stop emitting (clear = also remove the particles that are alive).</summary>
        public static void Stop(long entity, bool clear = false) { foreach (var ps in Systems(entity)) ParticleService.Stop(ps, clear); }

        /// <summary>Freeze / unfreeze the entity's effects.</summary>
        public static void Pause(long entity, bool paused = true) { foreach (var ps in Systems(entity)) ParticleService.Pause(ps, paused); }

        /// <summary>Clear and play from the start.</summary>
        public static void Restart(long entity) { foreach (var ps in Systems(entity)) ParticleService.Restart(ps); }

        /// <summary>Emit <paramref name="count"/> particles on every emitter of the entity's effects now (also while stopped —
        /// set the component's Play On Start off for effects that only burst from code, e.g. muzzle sparks).</summary>
        public static void Burst(long entity, int count) { foreach (var ps in Systems(entity)) ParticleService.Burst(ps, count); }

        /// <summary>True while any of the entity's effects is emitting.</summary>
        public static bool IsPlaying(long entity)
        {
            foreach (var ps in Systems(entity)) if (ParticleService.IsPlaying(ps)) return true;
            return false;
        }

        /// <summary>Live particles of the entity's effects.</summary>
        public static int ParticleCount(long entity)
        {
            int n = 0;
            foreach (var ps in Systems(entity)) n += ParticleService.AliveCount(ps);
            return n;
        }

        /// <summary>Fire-and-forget effect at a world pose (impacts, explosions, blood). Emits along the rotation's +Z
        /// (use <c>Quaternion.LookRotation(hitNormal, Vector3.Up)</c> for surface impacts). The effect removes itself when
        /// it is done; looping effects run until <see cref="VfxHandle.Stop"/>.</summary>
        public static VfxHandle SpawnAt(string vfxPath, Vector3 position, Quaternion rotation)
        {
            return new VfxHandle(ParticleService.SpawnAt(vfxPath, Sys(position), Sys(rotation)));
        }

        public static VfxHandle SpawnAt(string vfxPath, Vector3 position) { return SpawnAt(vfxPath, position, Quaternion.Identity); }

        /// <summary>SpawnAt with a scale and a render layer (1 = first-person viewmodel layer, e.g. a muzzle flash
        /// spawned at the viewmodel's muzzle).</summary>
        public static VfxHandle SpawnAt(string vfxPath, Vector3 position, Quaternion rotation, float scale, int renderLayer = 0)
        {
            return new VfxHandle(ParticleService.SpawnAt(vfxPath, Sys(position), Sys(rotation), scale, renderLayer));
        }

        /// <summary>A beam between two points — bullet tracers, lasers, lightning. Uses the .vfx's beam section (null =
        /// a default hot tracer streak). duration &gt; 0 overrides the effect's; 0 = the effect's own (a travelling
        /// tracer lasts its flight time).</summary>
        public static VfxHandle Beam(Vector3 from, Vector3 to, string vfxPath = null, float duration = 0f)
        {
            return new VfxHandle(ParticleService.Beam(Sys(from), Sys(to), vfxPath, duration));
        }

        /// <summary>Beam on a render layer (1 = viewmodel layer).</summary>
        public static VfxHandle Beam(Vector3 from, Vector3 to, string vfxPath, float duration, int renderLayer)
        {
            return new VfxHandle(ParticleService.Beam(Sys(from), Sys(to), vfxPath, duration, renderLayer));
        }

        // ------------------------------------------------------------------ helpers
        private static System.Collections.Generic.List<ParticleSystem> Systems(long entity)
        {
            var e = Editor.Scripting.ScriptRuntime.Instance.FindEntityByHandle(entity);
            return e != null ? ParticleService.SystemsOf(e) : new System.Collections.Generic.List<ParticleSystem>();
        }

        internal static System.Numerics.Vector3 Sys(Vector3 v) { return new System.Numerics.Vector3(v.X, v.Y, v.Z); }
        internal static System.Numerics.Quaternion Sys(Quaternion q)
        {
            var r = new System.Numerics.Quaternion(q.X, q.Y, q.Z, q.W);
            return r.LengthSquared() < 1e-12f ? System.Numerics.Quaternion.Identity : System.Numerics.Quaternion.Normalize(r);
        }
    }
}
