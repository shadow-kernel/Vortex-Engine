using System;
using System.Collections.Generic;
using System.Numerics;
using SysVec = System.Numerics.Vector3;

namespace Editor.Core.Animation
{
    /// <summary>
    /// Bone sockets: drives entities attached to skeleton bones (BoneAttachment component or the
    /// runtime Attach/Detach script API — ONE code path for both). Runs right after
    /// AnimationService.Step each play tick: for every socketed entity the desired world transform
    /// (socket offset x bone model-world x skinned-entity world) is written BACK into the entity's
    /// Transform (converted to local against its real parent), so children, colliders, scripts and
    /// the RuntimeDirty standalone re-submit all follow with zero extra wiring.
    ///
    /// Conventions: row-vector math throughout (matches AnimationService / DirectXMath); the engine's
    /// Euler order is ZXY — local rotation matrix = Rz * Rx * Ry (see SceneRenderService.BuildWorldMatrix).
    /// Runtime attachments live only here (cleared on play end) — play NEVER mutates authored components.
    /// </summary>
    public class BoneSocketService
    {
        public static BoneSocketService Instance { get; } = new BoneSocketService();

        private class RuntimeSocket
        {
            public ECS.GameEntity Target;          // null = nearest ancestor with an Animator
            public string Bone;
            public SysVec OffsetPos;
            public SysVec OffsetRotEuler;
            public SysVec OffsetScale;             // captured from the entity at Attach time (prefab scale survives)
            public bool Detached;                  // masks an authored BoneAttachment while set
            // Local TRS before the FIRST runtime attach — restored by Detach(keepWorldPosition: false).
            public ECS.Vector3 RestoreT, RestoreR, RestoreS;
        }

        private readonly Dictionary<ECS.GameEntity, RuntimeSocket> _runtime = new Dictionary<ECS.GameEntity, RuntimeSocket>();
        private readonly HashSet<string> _warned = new HashSet<string>();
        private Dictionary<Guid, ECS.GameEntity> _byId;
        private Data.Scene _scene;

        /// <summary>Play ended / scene switched: drop every runtime attachment and warning memo.</summary>
        public void ResetRuntime()
        {
            _runtime.Clear();
            _warned.Clear();
            _byId = null;
            _scene = null;
        }

        // ------------------------------------------------------------------ per-frame pass

        private class Job
        {
            public ECS.GameEntity Entity;
            public ECS.GameEntity ExplicitTarget;  // runtime target, or component target resolved by id
            public string Bone;
            public SysVec OffsetPos, OffsetRotEuler, OffsetScale;
            public int Depth;
        }

        /// <summary>
        /// Apply every socket in the scene (called after AnimationService.Step so bone worlds are this
        /// frame's pose). Chained attachments (lantern on weapon on hand) are applied in dependency order.
        /// </summary>
        public void Apply(Data.Scene scene)
        {
            if (scene?.Entities == null) return;
            if (!ReferenceEquals(scene, _scene)) { _scene = scene; _byId = null; }

            List<Job> jobs = null;
            foreach (var root in scene.Entities) Collect(root, ref jobs);
            if (jobs == null) return;

            foreach (var j in jobs) j.Depth = ChainDepth(j.Entity, 0);
            if (jobs.Count > 1) jobs.Sort((a, b) => a.Depth.CompareTo(b.Depth));
            foreach (var j in jobs) ApplyJob(j);
        }

        /// <summary>Apply ONE entity's authored socket immediately (editor authoring: "Snap to Bone").</summary>
        public bool ApplyOne(Data.Scene scene, ECS.GameEntity entity)
        {
            if (entity == null) return false;
            if (scene != null && !ReferenceEquals(scene, _scene)) { _scene = scene; _byId = null; }
            var job = JobFor(entity);
            return job != null && ApplyJob(job);
        }

        private void Collect(ECS.GameEntity e, ref List<Job> jobs)
        {
            if (e == null || !e.IsActive) return;
            var job = JobFor(e);
            if (job != null) (jobs = jobs ?? new List<Job>()).Add(job);
            if (e.Children != null)
                foreach (var c in e.Children) Collect(c, ref jobs);
        }

        /// <summary>The entity's effective socket: runtime entry wins over the authored component.</summary>
        private Job JobFor(ECS.GameEntity e)
        {
            if (_runtime.TryGetValue(e, out var rt))
            {
                if (rt.Detached) return null;
                return new Job
                {
                    Entity = e,
                    ExplicitTarget = rt.Target,
                    Bone = rt.Bone,
                    OffsetPos = rt.OffsetPos,
                    OffsetRotEuler = rt.OffsetRotEuler,
                    OffsetScale = rt.OffsetScale
                };
            }

            var comp = e.GetComponent<ECS.Components.Animation.BoneAttachment>();
            if (comp == null || !comp.IsEnabled || string.IsNullOrEmpty(comp.BoneName)) return null;

            ECS.GameEntity explicitTarget = null;
            if (!string.IsNullOrEmpty(comp.TargetEntityId) && Guid.TryParse(comp.TargetEntityId, out var gid))
                explicitTarget = FindById(gid);

            return new Job
            {
                Entity = e,
                ExplicitTarget = explicitTarget,
                Bone = comp.BoneName,
                OffsetPos = new SysVec(comp.OffsetPosition.X, comp.OffsetPosition.Y, comp.OffsetPosition.Z),
                OffsetRotEuler = new SysVec(comp.OffsetRotation.X, comp.OffsetRotation.Y, comp.OffsetRotation.Z),
                OffsetScale = new SysVec(comp.OffsetScale.X, comp.OffsetScale.Y, comp.OffsetScale.Z)
            };
        }

        private bool ApplyJob(Job j)
        {
            var target = j.ExplicitTarget ?? FindAncestorAnimator(j.Entity);
            if (target == null) return false;
            if (!TryGetBoneWorld(target, j.Bone, out var boneWorld))
            {
                WarnOnce(j.Entity.Name + "/" + j.Bone, "[BoneSocket] bone '" + j.Bone + "' not found on target '" + target.Name + "' — attachment idles");
                return false;
            }

            var scale = new SysVec(
                Math.Abs(j.OffsetScale.X) < 1e-6f ? 1f : j.OffsetScale.X,
                Math.Abs(j.OffsetScale.Y) < 1e-6f ? 1f : j.OffsetScale.Y,
                Math.Abs(j.OffsetScale.Z) < 1e-6f ? 1f : j.OffsetScale.Z);
            Matrix4x4 offset = Matrix4x4.CreateScale(scale)
                             * EulerZXY(j.OffsetRotEuler)
                             * Matrix4x4.CreateTranslation(j.OffsetPos);
            WriteWorldToTransform(j.Entity, offset * boneWorld);
            return true;
        }

        // ------------------------------------------------------------------ bone queries (script API + weapon raycasts)

        /// <summary>
        /// WORLD-space matrix of a bone: this frame's animated pose when the target is playing, else the
        /// bind pose. `target` is the entity carrying the Animator (or any entity — ancestors are searched).
        /// </summary>
        public bool TryGetBoneWorld(ECS.GameEntity target, string bone, out Matrix4x4 world)
        {
            world = Matrix4x4.Identity;
            if (target == null || string.IsNullOrEmpty(bone)) return false;

            var owner = FindAnimatorOwner(target) ?? target;
            var svc = AnimationService.Instance;
            if (!svc.TryGetNodeWorlds(owner, out var skeleton, out var worlds)) return false;

            int idx = skeleton.FindNode(bone);
            if (idx < 0 || worlds == null || idx >= worlds.Length) return false;

            // Bone worlds are MODEL space — frame them by the rendered skinned mesh entity's world,
            // exactly like the skinning shader does (vertex x palette x instance world).
            var meshEntity = svc.FindSkinnedMeshEntity(owner) ?? owner;
            world = worlds[idx] * EntityWorld(meshEntity);
            return true;
        }

        /// <summary>Bone world position + rotation (engine ZXY Euler degrees) for script consumption.</summary>
        public bool TryGetBoneTransform(ECS.GameEntity target, string bone, out SysVec pos, out SysVec rotEuler)
        {
            pos = default(SysVec); rotEuler = default(SysVec);
            if (!TryGetBoneWorld(target, bone, out var m)) return false;
            pos = m.Translation;
            rotEuler = ToEulerZXY(m);
            return true;
        }

        // ------------------------------------------------------------------ runtime attach/detach (script API)

        /// <summary>
        /// Attach an entity to a bone at runtime (weapon pickup). Snaps to the given bone-local offset.
        /// False on cycles (target already attached — directly or transitively — to `entity`), missing
        /// inputs, or when the target resolves to no skeleton. Refreshes the entity's collision shapes.
        /// </summary>
        public bool Attach(ECS.GameEntity entity, ECS.GameEntity target, string bone, SysVec offsetPos, SysVec offsetRotEuler)
        {
            if (entity == null || string.IsNullOrEmpty(bone)) return false;

            // Cycle guard: walk the target's effective socket chain — attaching A to B while B hangs off A
            // would feed back through the socket pass forever.
            var cur = target;
            int hops = 0;
            while (cur != null && hops++ < 16)
            {
                if (ReferenceEquals(cur, entity)) return false;
                cur = EffectiveTargetOf(cur);
            }

            if (!_runtime.TryGetValue(entity, out var rt))
            {
                rt = new RuntimeSocket();
                var t = entity.Transform;
                if (t != null) { rt.RestoreT = t.LocalPosition; rt.RestoreR = t.LocalRotation; rt.RestoreS = t.LocalScale; }
                _runtime[entity] = rt;
            }
            {
                // The entity keeps ITS scale in the hand (a 0.5-scaled pistol prefab stays 0.5-scaled).
                var ts = entity.Transform;
                rt.OffsetScale = ts != null
                    ? new SysVec(ts.LocalScale.X, ts.LocalScale.Y, ts.LocalScale.Z)
                    : SysVec.One;
            }
            rt.Target = target;
            rt.Bone = bone;
            rt.OffsetPos = offsetPos;
            rt.OffsetRotEuler = offsetRotEuler;
            rt.Detached = false;

            // Snap NOW (mid-frame) so a same-frame raycast/render sees the weapon in the hand,
            // then refresh its collision shapes at the new location.
            if (_scene != null) ApplyOne(_scene, entity);
            RefreshCollision(entity);
            return true;
        }

        /// <summary>
        /// Detach a runtime- OR component-attached entity. keepWorldPosition=true leaves it exactly where
        /// the socket last put it (no pop); false restores the local TRS it had before the first runtime
        /// attach (holster-to-origin). Refreshes collision shapes.
        /// </summary>
        public bool Detach(ECS.GameEntity entity, bool keepWorldPosition)
        {
            if (entity == null) return false;

            bool hadRuntime = _runtime.TryGetValue(entity, out var rt);
            bool hasComponent = entity.GetComponent<ECS.Components.Animation.BoneAttachment>() != null;
            if (!hadRuntime && !hasComponent) return false;

            if (!hadRuntime)
            {
                // Component-authored socket: mask it with a detached runtime entry (authored data untouched).
                rt = new RuntimeSocket();
                var t0 = entity.Transform;
                if (t0 != null) { rt.RestoreT = t0.LocalPosition; rt.RestoreR = t0.LocalRotation; rt.RestoreS = t0.LocalScale; }
                _runtime[entity] = rt;
            }
            rt.Detached = true;

            if (!keepWorldPosition)
            {
                var t = entity.Transform;
                if (t != null)
                {
                    t.LocalPosition = rt.RestoreT;
                    t.LocalRotation = rt.RestoreR;
                    t.LocalScale = rt.RestoreS;
                }
            }
            RefreshCollision(entity);
            return true;
        }

        /// <summary>Entities currently socketed (component or runtime) to bones of `target`.</summary>
        public List<ECS.GameEntity> GetAttachedTo(ECS.GameEntity target)
        {
            var result = new List<ECS.GameEntity>();
            if (_scene?.Entities == null || target == null) return result;
            CollectAttachedTo(_scene.Entities, target, result);
            return result;
        }

        private void CollectAttachedTo(IEnumerable<ECS.GameEntity> list, ECS.GameEntity target, List<ECS.GameEntity> result)
        {
            foreach (var e in list)
            {
                if (e == null) continue;
                var job = JobFor(e);
                if (job != null && ReferenceEquals(job.ExplicitTarget ?? FindAncestorAnimator(e), target))
                    result.Add(e);
                if (e.Children != null) CollectAttachedTo(e.Children, target, result);
            }
        }

        // ------------------------------------------------------------------ editor authoring (#172)

        /// <summary>The entity this attachment resolves to right now (explicit id or ancestor Animator).
        /// The ancestor path needs no scene — a null scene only disables the by-id lookup.</summary>
        public ECS.GameEntity ResolveTargetOf(Data.Scene scene, ECS.GameEntity entity)
        {
            if (entity == null) return null;
            if (scene != null && !ReferenceEquals(scene, _scene)) { _scene = scene; _byId = null; }
            var comp = entity.GetComponent<ECS.Components.Animation.BoneAttachment>();
            if (comp != null && !string.IsNullOrEmpty(comp.TargetEntityId) && Guid.TryParse(comp.TargetEntityId, out var gid))
            {
                var byId = _scene != null ? FindById(gid) : null;
                if (byId != null) return byId;
            }
            return FindAncestorAnimator(entity);
        }

        /// <summary>Skeleton bone names of the attachment's resolved target (hierarchy order) — the
        /// inspector's bone dropdown. Empty when no skeleton resolves.</summary>
        public string[] GetBoneNamesFor(Data.Scene scene, ECS.GameEntity entity)
        {
            var target = ResolveTargetOf(scene, entity);
            if (target == null) return new string[0];
            if (!AnimationService.Instance.TryGetNodeWorlds(target, out var skeleton, out _)) return new string[0];
            var names = new string[skeleton.Nodes.Length];
            for (int i = 0; i < skeleton.Nodes.Length; i++) names[i] = skeleton.Nodes[i].Name;
            return names;
        }

        /// <summary>
        /// Authoring flow "place visually, then capture": compute the offset that keeps the entity's
        /// CURRENT world transform when socketed to its configured bone, and write it into the component.
        /// (offset = entityWorld x inverse(boneWorld)). False when bone/skeleton can't resolve.
        /// </summary>
        public bool CaptureOffsetFromCurrentPose(Data.Scene scene, ECS.GameEntity entity)
        {
            if (entity == null) return false;
            if (scene != null && !ReferenceEquals(scene, _scene)) { _scene = scene; _byId = null; }
            var comp = entity.GetComponent<ECS.Components.Animation.BoneAttachment>();
            if (comp == null || string.IsNullOrEmpty(comp.BoneName)) return false;
            var target = ResolveTargetOf(scene, entity);
            if (target == null) return false;
            if (!TryGetBoneWorld(target, comp.BoneName, out var boneWorld)) return false;
            if (!Matrix4x4.Invert(boneWorld, out var inv)) return false;

            Matrix4x4 offset = EntityWorld(entity) * inv;
            if (!Matrix4x4.Decompose(offset, out var s, out var q, out var t))
            {
                t = offset.Translation;
                q = Quaternion.CreateFromRotationMatrix(NormalizeBasis(offset));
                s = SysVec.One;
            }
            var euler = ToEulerZXY(Matrix4x4.CreateFromQuaternion(q));
            comp.OffsetPosition = new ECS.Vector3(t.X, t.Y, t.Z);
            comp.OffsetRotation = new ECS.Vector3(euler.X, euler.Y, euler.Z);
            comp.OffsetScale = new ECS.Vector3(s.X, s.Y, s.Z);
            return true;
        }

        // ------------------------------------------------------------------ resolution helpers

        private ECS.GameEntity EffectiveTargetOf(ECS.GameEntity e)
        {
            var job = JobFor(e);
            return job == null ? null : (job.ExplicitTarget ?? FindAncestorAnimator(e));
        }

        private int ChainDepth(ECS.GameEntity e, int depth)
        {
            if (depth >= 8) return depth;   // runaway/cycle cap — Attach() guards real cycles
            var t = EffectiveTargetOf(e);
            return t == null ? depth : ChainDepth(t, depth + 1);
        }

        private static ECS.GameEntity FindAnimatorOwner(ECS.GameEntity e)
        {
            while (e != null)
            {
                var a = e.GetComponent<ECS.Components.Animation.Animator>();
                if (a != null && a.IsEnabled) return e;
                e = e.Parent;
            }
            return null;
        }

        /// <summary>Nearest ANCESTOR (excluding self) with an enabled Animator — the default socket target.</summary>
        private static ECS.GameEntity FindAncestorAnimator(ECS.GameEntity e) => FindAnimatorOwner(e?.Parent);

        private ECS.GameEntity FindById(Guid id)
        {
            if (_byId == null)
            {
                _byId = new Dictionary<Guid, ECS.GameEntity>();
                if (_scene?.Entities != null)
                    foreach (var root in _scene.Entities) IndexById(root);
            }
            return _byId.TryGetValue(id, out var e) ? e : null;
        }

        private void IndexById(ECS.GameEntity e)
        {
            if (e == null) return;
            _byId[e.Id] = e;
            if (e.Children != null)
                foreach (var c in e.Children) IndexById(c);
        }

        private static void RefreshCollision(ECS.GameEntity e)
        {
            try
            {
                Editor.Core.Services.Physics.CollisionService.RemoveEntityShapes(e);
                if (e.IsActive) Editor.Core.Services.Physics.CollisionService.AddEntityShapes(e);
            }
            catch { }
        }

        private void WarnOnce(string key, string message)
        {
            if (_warned.Add(key)) System.Diagnostics.Debug.WriteLine(message);
        }

        // ------------------------------------------------------------------ transform math (engine ZXY convention)

        /// <summary>Entity world matrix from the Transform parent chain — byte-compatible with
        /// SceneRenderService.BuildWorldMatrixWithParent (S * Rz*Rx*Ry * T per level, row-vector).</summary>
        public static Matrix4x4 EntityWorld(ECS.GameEntity e)
        {
            Matrix4x4 world = Matrix4x4.Identity;
            while (e != null)
            {
                var t = e.Transform;
                if (t != null)
                {
                    var p = t.LocalPosition; var r = t.LocalRotation; var s = t.LocalScale;
                    Matrix4x4 local = Matrix4x4.CreateScale(s.X, s.Y, s.Z)
                                    * EulerZXY(new SysVec(r.X, r.Y, r.Z))
                                    * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
                    world = world * local;
                }
                e = e.Parent;
            }
            return world;
        }

        /// <summary>Write a desired WORLD matrix into the entity's Transform as local TRS (against its
        /// real parent). Property setters push to the native engine + set RuntimeDirty — the standalone
        /// GameHost re-submit contract.</summary>
        private static void WriteWorldToTransform(ECS.GameEntity entity, Matrix4x4 desiredWorld)
        {
            var t = entity.Transform;
            if (t == null) return;

            Matrix4x4 local = desiredWorld;
            var parentWorld = EntityWorld(entity.Parent);
            if (Matrix4x4.Invert(parentWorld, out var inv)) local = desiredWorld * inv;

            SysVec scale, trans; Quaternion rot;
            if (!Matrix4x4.Decompose(local, out scale, out rot, out trans))
            {
                // Shear (non-uniform parent scale under rotation): keep translation + best-effort rotation.
                trans = local.Translation;
                rot = Quaternion.CreateFromRotationMatrix(NormalizeBasis(local));
                scale = SysVec.One;
            }
            var euler = ToEulerZXY(Matrix4x4.CreateFromQuaternion(rot));

            // Epsilon-gate the writes: each setter fires PropertyChanged + a native transform push.
            var cp = t.LocalPosition; var cr = t.LocalRotation; var cs = t.LocalScale;
            if (!Near(cp.X, trans.X) || !Near(cp.Y, trans.Y) || !Near(cp.Z, trans.Z))
                t.LocalPosition = new ECS.Vector3(trans.X, trans.Y, trans.Z);
            if (!NearAngle(cr.X, euler.X) || !NearAngle(cr.Y, euler.Y) || !NearAngle(cr.Z, euler.Z))
                t.LocalRotation = new ECS.Vector3(euler.X, euler.Y, euler.Z);
            if (!Near(cs.X, scale.X) || !Near(cs.Y, scale.Y) || !Near(cs.Z, scale.Z))
                t.LocalScale = new ECS.Vector3(scale.X, scale.Y, scale.Z);
        }

        private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-5f;
        private static bool NearAngle(float a, float b)
        {
            float d = (a - b) % 360f;
            if (d > 180f) d -= 360f; else if (d < -180f) d += 360f;
            return Math.Abs(d) < 1e-3f;
        }

        /// <summary>Engine Euler (degrees, ZXY) -> rotation matrix. Matches SceneRenderService.BuildWorldMatrix
        /// exactly: R = Rz * Rx * Ry in System.Numerics row-vector convention.</summary>
        public static Matrix4x4 EulerZXY(SysVec eulerDeg)
        {
            const float toRad = (float)(Math.PI / 180.0);
            return Matrix4x4.CreateRotationZ(eulerDeg.Z * toRad)
                 * Matrix4x4.CreateRotationX(eulerDeg.X * toRad)
                 * Matrix4x4.CreateRotationY(eulerDeg.Y * toRad);
        }

        /// <summary>Rotation matrix -> engine Euler degrees (ZXY). Inverse of <see cref="EulerZXY"/>:
        /// sinX = -M32; Z from M12/M22; Y from M31/M33; gimbal fallback at |sinX| ~ 1.</summary>
        public static SysVec ToEulerZXY(Matrix4x4 m)
        {
            const float toDeg = (float)(180.0 / Math.PI);
            float sinX = -m.M32;
            if (sinX > 1f) sinX = 1f; else if (sinX < -1f) sinX = -1f;
            float x = (float)Math.Asin(sinX);
            float y, z;
            if (Math.Abs(sinX) < 0.99999f)
            {
                z = (float)Math.Atan2(m.M12, m.M22);
                y = (float)Math.Atan2(m.M31, m.M33);
            }
            else
            {
                z = (float)Math.Atan2(-m.M21, m.M11);
                y = 0f;
            }
            return new SysVec(x * toDeg, y * toDeg, z * toDeg);
        }

        private static Matrix4x4 NormalizeBasis(Matrix4x4 m)
        {
            var r0 = SysVec.Normalize(new SysVec(m.M11, m.M12, m.M13));
            var r1 = SysVec.Normalize(new SysVec(m.M21, m.M22, m.M23));
            var r2 = SysVec.Normalize(new SysVec(m.M31, m.M32, m.M33));
            return new Matrix4x4(
                r0.X, r0.Y, r0.Z, 0f,
                r1.X, r1.Y, r1.Z, 0f,
                r2.X, r2.Y, r2.Z, 0f,
                0f, 0f, 0f, 1f);
        }
    }
}
