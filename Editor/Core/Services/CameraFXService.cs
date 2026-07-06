using System;
using System.Collections.Generic;
using SysVec = System.Numerics.Vector3;

namespace Editor.Core.Services
{
    /// <summary>
    /// Procedural camera/attachment feel primitives (#176): additive offset channels composed from
    /// spring-damper IMPULSES (recoil kick, damage flinch) and seeded continuous NOISE (idle sway,
    /// breathing bob), recovered by a configurable spring. Pure math utility — "recoil" is a game
    /// script concept; scripts push impulses, the engine only integrates and composes.
    ///
    /// Targets: the game CAMERA (applied by PlayCameraHelper on top of the camera entity's transform —
    /// the entity transform is never touched) and ATTACHED entities (composed into the bone-socket
    /// offset by BoneSocketService, innermost = weapon-local axes, so the weapon kicks back in the
    /// hand while staying glued). Deterministic per seed; zero per-frame allocations (struct sways,
    /// struct dictionary enumerators, targets allocated once at first use).
    /// </summary>
    public class CameraFXService
    {
        public static CameraFXService Instance { get; } = new CameraFXService();

        private struct Sway { public float PosAmp, RotAmp, Freq; }

        private class Target
        {
            public SysVec Pos, PosVel;          // spring-damper displacement (impulses land here)
            public SysVec Rot, RotVel;          // Euler degrees, engine ZXY convention
            public Sway[] Sways = new Sway[4];  // fixed slots: recoil pattern + idle sway + bob stack
            public int Id;                      // decorrelates noise between targets
        }

        private readonly Target _camera = new Target { Id = 0 };
        private readonly Dictionary<ECS.GameEntity, Target> _entities = new Dictionary<ECS.GameEntity, Target>();
        private float _time;
        private int _seed = 1337;
        private int _nextTargetId = 1;

        // Spring-damper recovery: x'' = -k*x - c*v (semi-implicit Euler). Defaults feel like a snappy
        // pistol; SetSpring(60, 10) reads as a heavy shotgun. Shared across targets (one "feel" per game).
        private float _stiffness = 120f;
        private float _damping = 22f;

        /// <summary>Play ended: forget every channel (the editor camera must not keep shaking).</summary>
        public void Reset()
        {
            ZeroTarget(_camera);
            _entities.Clear();
            _time = 0f;
            _seed = 1337;
            _stiffness = 120f;
            _damping = 22f;
            _nextTargetId = 1;
        }

        private static void ZeroTarget(Target t)
        {
            t.Pos = default(SysVec); t.PosVel = default(SysVec);
            t.Rot = default(SysVec); t.RotVel = default(SysVec);
            for (int i = 0; i < t.Sways.Length; i++) t.Sways[i] = default(Sway);
        }

        // ------------------------------------------------------------------ per-frame tick

        /// <summary>Advance springs + the noise clock. Called once per play tick BEFORE the socket pass
        /// (sockets read entity offsets) — the camera offset is read later by PlayCameraHelper.</summary>
        public void Step(float dt)
        {
            if (dt <= 0f) return;
            _time += dt;
            Integrate(_camera, dt);
            foreach (var kv in _entities) Integrate(kv.Value, dt);
        }

        private void Integrate(Target t, float dt)
        {
            // Sub-step for stability at low frame rates (spring k=120 explodes past ~50ms steps).
            int steps = dt > 0.025f ? (int)Math.Ceiling(dt / 0.025f) : 1;
            float h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                t.PosVel += (-_stiffness * t.Pos - _damping * t.PosVel) * h;
                t.Pos += t.PosVel * h;
                t.RotVel += (-_stiffness * t.Rot - _damping * t.RotVel) * h;
                t.Rot += t.RotVel * h;
            }
        }

        // ------------------------------------------------------------------ script inputs

        /// <summary>Recoil/flinch impulse on the camera: instant displacement, spring-damper recovery.</summary>
        public void KickCamera(SysVec rotDeg, SysVec pos)
        {
            _camera.Rot += rotDeg;
            _camera.Pos += pos;
        }

        /// <summary>Impulse on an (attached) entity — displaces it inside its socket, in weapon-local axes.</summary>
        public void KickEntity(ECS.GameEntity entity, SysVec rotDeg, SysVec pos)
        {
            if (entity == null) return;
            var t = TargetFor(entity);
            t.Rot += rotDeg;
            t.Pos += pos;
        }

        /// <summary>Continuous seeded noise on a camera slot (0..3). Amplitudes 0 clear the slot.</summary>
        public void SwayCamera(int slot, float posAmp, float rotAmpDeg, float freq)
        {
            SetSway(_camera, slot, posAmp, rotAmpDeg, freq);
        }

        public void SwayEntity(ECS.GameEntity entity, int slot, float posAmp, float rotAmpDeg, float freq)
        {
            if (entity == null) return;
            SetSway(TargetFor(entity), slot, posAmp, rotAmpDeg, freq);
        }

        private static void SetSway(Target t, int slot, float posAmp, float rotAmpDeg, float freq)
        {
            if (slot < 0 || slot >= t.Sways.Length) return;
            t.Sways[slot] = new Sway { PosAmp = posAmp, RotAmp = rotAmpDeg, Freq = freq <= 0f ? 1f : freq };
        }

        /// <summary>Recovery feel: stiffness (pull-back strength) + damping (overshoot kill).
        /// (120, 22) = snappy pistol; (60, 10) = heavy shotgun with a wobble.</summary>
        public void SetSpring(float stiffness, float damping)
        {
            _stiffness = Math.Max(stiffness, 1f);
            _damping = Math.Max(damping, 0f);
        }

        /// <summary>Noise seed — replays are stable for a fixed seed.</summary>
        public void SetSeed(int seed) { _seed = seed; }

        private Target TargetFor(ECS.GameEntity e)
        {
            if (!_entities.TryGetValue(e, out var t))
            {
                t = new Target { Id = _nextTargetId++ };
                _entities[e] = t;
            }
            return t;
        }

        // ------------------------------------------------------------------ composed outputs

        /// <summary>Camera offset this frame (position + Euler degrees). False when everything is idle
        /// (PlayCameraHelper skips the extra math entirely).</summary>
        public bool TryGetCameraOffset(out SysVec pos, out SysVec rotDeg)
            => TryGetOffset(_camera, out pos, out rotDeg);

        /// <summary>Socket-space offset for an attached entity (BoneSocketService composes it innermost).</summary>
        public bool TryGetEntityOffset(ECS.GameEntity entity, out SysVec pos, out SysVec rotDeg)
        {
            pos = default(SysVec); rotDeg = default(SysVec);
            if (entity == null || _entities.Count == 0 || !_entities.TryGetValue(entity, out var t)) return false;
            return TryGetOffset(t, out pos, out rotDeg);
        }

        private bool TryGetOffset(Target t, out SysVec pos, out SysVec rotDeg)
        {
            pos = t.Pos;
            rotDeg = t.Rot;
            bool active = t.Pos.LengthSquared() > 1e-10f || t.Rot.LengthSquared() > 1e-8f;
            for (int i = 0; i < t.Sways.Length; i++)
            {
                var s = t.Sways[i];
                if (s.PosAmp == 0f && s.RotAmp == 0f) continue;
                active = true;
                float u = _time * s.Freq;
                if (s.PosAmp != 0f)
                    pos += new SysVec(
                        s.PosAmp * Noise(t.Id, i, 0, u),
                        s.PosAmp * Noise(t.Id, i, 1, u),
                        s.PosAmp * Noise(t.Id, i, 2, u));
                if (s.RotAmp != 0f)
                    rotDeg += new SysVec(
                        s.RotAmp * Noise(t.Id, i, 3, u),
                        s.RotAmp * Noise(t.Id, i, 4, u),
                        s.RotAmp * Noise(t.Id, i, 5, u));
            }
            return active;
        }

        // ------------------------------------------------------------------ deterministic value noise

        /// <summary>Smooth value noise in [-1,1]: hashed lattice values, smoothstep-interpolated.
        /// Fully determined by (seed, target, slot, axis, u) — replay-stable, allocation-free.</summary>
        private float Noise(int target, int slot, int axis, float u)
        {
            int i0 = (int)Math.Floor(u);
            float f = u - i0;
            float a = Hash01(i0, target, slot, axis);
            float b = Hash01(i0 + 1, target, slot, axis);
            float w = f * f * (3f - 2f * f);
            return (a + (b - a) * w) * 2f - 1f;
        }

        private float Hash01(int i, int target, int slot, int axis)
        {
            unchecked
            {
                uint h = (uint)_seed;
                h = h * 374761393u + (uint)i * 668265263u;
                h = h * 2246822519u + (uint)target * 2654435761u;
                h = h * 3266489917u + (uint)(slot * 97 + axis) * 974634617u;
                h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }
    }
}
