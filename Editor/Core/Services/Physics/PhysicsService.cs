using System;
using System.Collections.Generic;
using System.ComponentModel;
using Editor.Core.Data;
using Editor.DllWrapper;
using Editor.ECS;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;
using SysVec = System.Numerics.Vector3;
using SysQuat = System.Numerics.Quaternion;
using SysMat = System.Numerics.Matrix4x4;

namespace Editor.Core.Services.Physics
{
    /// <summary>Contact kind reported by the physics world.</summary>
    public enum PhysicsContactKind { Added = 0, Persisted = 1, Removed = 2 }

    /// <summary>A contact between two simulated bodies (solid or trigger), resolved to the owning entities.
    /// <see cref="Normal"/> points from <see cref="EntityA"/> to <see cref="EntityB"/>.</summary>
    public struct PhysicsContactEvent
    {
        public GameEntity EntityA, EntityB;
        public PhysicsContactKind Kind;
        public bool IsTrigger;
        public Vector3 Point, Normal;
        public float Impulse;
    }

    /// <summary>A breakable joint exceeded its Break Force (issue #103): <see cref="Entity"/> owns the
    /// <see cref="Joint"/> component, <see cref="Connected"/> is the other body's entity (null = the world),
    /// <see cref="Force"/> the force in N that broke it. The joint is disabled (re-enable it to repair).</summary>
    public struct PhysicsJointBreakEvent
    {
        public GameEntity Entity;
        public PhysicsJoint Joint;
        public GameEntity Connected;
        public float Force;
    }

    /// <summary>
    /// Physics v2 (Jolt, issues #100/#102/#106/#107): the rigid-body world for play mode. Every entity with an
    /// enabled Collider becomes a body — static by default, kinematic or dynamic when it also carries a Rigidbody
    /// (mass, drag, gravity, freeze axes, physics material). Box/Sphere/Capsule colliders map to Jolt primitives
    /// (several colliders on one entity form a compound), Mesh colliders to a static triangle mesh or a convex hull,
    /// Is-Trigger colliders to sensors. <see cref="Step"/> runs a fixed 60 Hz simulation (at most 4 substeps per
    /// frame), pushes script/animation-moved KINEMATIC bodies, writes every DYNAMIC body's pose back to its entity,
    /// publishes the dynamic bodies to <see cref="CollisionService"/> so the character controller collides with (and
    /// pushes) them, and dispatches contact events through <see cref="ContactHandler"/> (the script runtime routes
    /// them to OnCollisionEnter / OnTriggerEnter/Stay/Exit).
    ///
    /// Joints (#103): after the bodies, every enabled Hinge / Ball / Slider / Fixed / Distance joint component becomes
    /// a Jolt constraint between its entity's body and the connected entity's body (or the world), anchored at the
    /// entity's current world pose (the rest pose). Motor / spring / friction fields and the component's enabled
    /// flag apply live; breakable joints raise <see cref="JointBroken"/>. Runtime joint state (broken, script
    /// motors) lives here, never in the components, so play stays non-destructive.
    ///
    /// Without a Jolt-enabled engine (<see cref="PhysicsNative.Available"/> false — the stub build) every call is a
    /// no-op: the static collision world and the character controller keep working exactly as before, and a single
    /// console notice explains why props don't simulate.
    /// </summary>
    public static class PhysicsService
    {
        /// <summary>Fixed simulation step (seconds).</summary>
        public const float FixedStep = 1f / 60f;
        /// <summary>Upper bound of physics steps per rendered frame (slow frames drop simulation time instead of spiralling).</summary>
        public const int MaxSubsteps = 4;

        // Object layers (native contract).
        public const int LayerStatic = 0, LayerDynamic = 1, LayerCharacter = 2, LayerTrigger = 3, LayerDebris = 4;
        // Motion types (native contract).
        public const int MotionStatic = 0, MotionKinematic = 1, MotionDynamic = 2;
        // Shape types (native contract).
        private const int ShapeBox = 0, ShapeSphere = 1, ShapeCapsule = 2, ShapeCylinder = 3;

        /// <summary>Entities with this tag simulate on the DEBRIS layer (shell casings, small props): they collide
        /// with the world and other props but never block or push the player.</summary>
        public const string DebrisTag = "Debris";

        /// <summary>Most force (N) a walking character shoves a JOINTED body (a door, a lift) with — see
        /// ApplyCharacterPushes. Free props keep the impulse model.</summary>
        public const float CharacterPushForceOnJoints = 800f;

        private sealed class Body
        {
            public uint Id;
            public GameEntity Entity;
            public int Motion;
            public int Layer;
            public bool IsTrigger;
            public bool Secondary;        // the sensor twin of an entity whose primary body is solid
            public SysVec CenterOffset;   // scaled local offset entity origin -> body origin (mesh bodies with a Center)
            public SysVec BoundsCenter;   // centre of the shape bounds in the body frame
            public SysVec BoundsHalf;     // half extents of the shape bounds in the body frame
            public float SphereRadius;    // > 0: one centred sphere — published as a sphere, not a box
            public SysVec Scale;          // entity world scale at creation (readback keeps the entity's own local scale)
            public SysVec LastPos;        // last pose exchanged with the world (creation / push / readback)
            public SysQuat LastRot;
        }

        private struct ChildShape { public int Type; public SysVec Dims; public SysVec LocalPos; public SysQuat LocalRot; }

        /// <summary>A joint component's native constraint. Id 0 = detached: its connected entity left the world
        /// (destroyed / deactivated); it re-attaches when that entity comes back.</summary>
        private sealed class JointRec
        {
            public uint Id;
            public uint BodyA, BodyB;           // native bodies it holds (BodyB 0 = the world)
            public GameEntity Entity;
            public PhysicsJoint Joint;
            public GameEntity Connected;        // null = the world
            public bool Enabled;                // state last pushed to the native joint
            public bool Broken;
            public float PeakForce;             // highest per-frame force seen (tuning Break Force)
            public volatile bool EnabledDirty;  // set by the component's PropertyChanged (inspector, UI thread)
            public volatile bool MotorDirty;
        }

        private static readonly Dictionary<GameEntity, Body> _byEntity = new Dictionary<GameEntity, Body>();
        private static readonly Dictionary<uint, Body> _byId = new Dictionary<uint, Body>();
        private static readonly List<Body> _bodies = new List<Body>();
        private static readonly List<CollisionService.DynamicBody> _publish = new List<CollisionService.DynamicBody>();
        private static readonly HashSet<(uint, uint)> _stayThisFrame = new HashSet<(uint, uint)>();
        private static readonly Dictionary<uint, (SysVec dir, float j)> _pushes = new Dictionary<uint, (SysVec, float)>();
        private static PhysicsContact[] _contactBuf = new PhysicsContact[256];
        private static float[] _dbgBuf;
        private static readonly float[] _f3a = new float[3], _f3b = new float[3], _f3c = new float[3], _f4 = new float[4];
        private static float _accumulator;
        private static bool _loggedUnavailable;
        private static Vector3 _gravity = new Vector3(0f, -9.81f, 0f);

        // Joints (#103)
        private static Scene _scene;
        private static readonly List<PhysicsJoint> _jointComponents = new List<PhysicsJoint>();   // every joint component of the live world
        private static readonly HashSet<PhysicsJoint> _jointComponentSet = new HashSet<PhysicsJoint>();
        private static readonly List<JointRec> _joints = new List<JointRec>();
        private static readonly Dictionary<uint, JointRec> _jointById = new Dictionary<uint, JointRec>();
        private static readonly Dictionary<PhysicsJoint, JointRec> _jointByComponent = new Dictionary<PhysicsJoint, JointRec>();
        private static readonly HashSet<PhysicsJoint> _jointWarned = new HashSet<PhysicsJoint>();
        private static readonly uint[] _brokenIds = new uint[64];
        private static readonly float[] _brokenForces = new float[64];
        private static volatile bool _jointsDirty;

        /// <summary>True when the Jolt world is available in this engine build (see <see cref="PhysicsNative"/>).</summary>
        public static bool Available => PhysicsNative.Available;

        /// <summary>True between <see cref="Build"/> and <see cref="Clear"/> with an available physics world.</summary>
        public static bool IsBuilt { get; private set; }

        /// <summary>Number of managed bodies (primary + sensor twins).</summary>
        public static int BodyCount => _bodies.Count;

        /// <summary>Physics steps run by the last <see cref="Step"/> call (0 when the frame was shorter than a step).</summary>
        public static int LastStepCount { get; private set; }

        /// <summary>Draw the live physics shapes (cyan wire lines) over the play view — View ▸ Physics Debug.</summary>
        public static bool ShowPhysicsDebug;

        /// <summary>Receives every contact the world reported (set by the script runtime; null = events dropped).</summary>
        public static Action<PhysicsContactEvent> ContactHandler;

        /// <summary>A breakable joint broke (raised from <see cref="Step"/> on the game thread, after the step). The
        /// script runtime's OnJointBreak callback hooks in here.</summary>
        public static event Action<PhysicsJointBreakEvent> JointBroken;

        /// <summary>Number of joints in the simulation (attached, including disabled / broken ones).</summary>
        public static int JointCount { get { int n = 0; foreach (var j in _joints) if (j.Id != 0) n++; return n; } }

        /// <summary>World gravity in m/s² (default (0, -9.81, 0)). Reset to the default on every <see cref="Build"/>;
        /// scripts change it via Physics.SetGravity.</summary>
        public static Vector3 Gravity
        {
            get => _gravity;
            set
            {
                _gravity = value;
                if (IsBuilt) { try { VortexAPI.PhysicsSetGravity(value.X, value.Y, value.Z); } catch { } }
            }
        }

        // ------------------------------------------------------------------------------------------ lifecycle

        /// <summary>Create a body for every entity with an enabled Collider (or a Rigidbody, see the class notes)
        /// in <paramref name="scene"/>. Call right after <see cref="CollisionService.Build"/> on play start: dynamic
        /// entities are REMOVED from the static collision world here (they are published as moving shapes instead).</summary>
        public static void Build(Scene scene)
        {
            Clear();
            if (!Available)
            {
                if (!_loggedUnavailable)
                {
                    _loggedUnavailable = true;
                    Log("[Physics] rigid-body physics unavailable: " + PhysicsNative.Reason + " — Rigidbody props stay where they are (static collision + character controller keep working).");
                }
                return;
            }
            IsBuilt = true;
            _accumulator = 0f;
            Gravity = new Vector3(0f, -9.81f, 0f);
            _scene = scene;
            if (scene?.Entities == null) return;
            foreach (var e in scene.Entities) AddRecursive(e);
            foreach (var e in scene.Entities) CollectJoints(e);
            CreatePendingJoints(null);
            PublishDynamicShapes();
            int dyn = 0, kin = 0, stat = 0, trig = 0;
            foreach (var b in _bodies) { if (b.IsTrigger) trig++; else if (b.Motion == MotionDynamic) dyn++; else if (b.Motion == MotionKinematic) kin++; else stat++; }
            Log("[Physics] Jolt world ready: " + _bodies.Count + " bodies (" + dyn + " dynamic, " + kin + " kinematic, " + stat + " static, " + trig + " triggers)"
                + (_jointComponents.Count > 0 ? ", " + JointCount + "/" + _jointComponents.Count + " joints" : ""));
        }

        /// <summary>Destroy every body and joint (play stop, scene switch). Safe to call when nothing was built.</summary>
        public static void Clear()
        {
            if (IsBuilt) { try { VortexAPI.PhysicsClear(); } catch { } }
            foreach (var j in _jointComponents) j.PropertyChanged -= OnJointComponentChanged;
            _jointComponents.Clear(); _jointComponentSet.Clear(); _joints.Clear(); _jointById.Clear(); _jointByComponent.Clear(); _jointWarned.Clear();
            _jointsDirty = false;
            _scene = null;
            _byEntity.Clear(); _byId.Clear(); _bodies.Clear(); _publish.Clear(); _pushes.Clear(); _stayThisFrame.Clear();
            _accumulator = 0f;
            LastStepCount = 0;
            IsBuilt = false;
            try { if (CollisionService.IsBuilt) CollisionService.SetDynamicBodies(null); } catch { }
        }

        /// <summary>Add bodies (and joints) for a runtime-spawned (or re-activated) entity subtree. Call AFTER
        /// <see cref="CollisionService.AddEntityShapes"/> so dynamic entities can be taken out of the static world again.</summary>
        public static void AddEntity(GameEntity root)
        {
            if (!IsBuilt || root == null) return;
            AddRecursive(root);
            CollectJoints(root);
            CreatePendingJoints(root);
            PublishDynamicShapes();
        }

        /// <summary>Remove the bodies of an entity subtree (runtime Destroy / SetActive(false)). Its joints go with
        /// it; joints of other entities connected into the subtree detach until it comes back.</summary>
        public static void RemoveEntity(GameEntity root)
        {
            if (!IsBuilt || root == null) return;
            DetachJoints(root);
            RemoveRecursive(root);
            PublishDynamicShapes();
        }

        /// <summary>Re-sync an entity after a script moved or edited its colliders (Physics.RefreshCollider):
        /// static/kinematic bodies are teleported to the entity's current pose, dynamic entities are removed from the
        /// static collision world again (RefreshCollider re-baked them there).</summary>
        public static void RefreshEntity(GameEntity e)
        {
            if (!IsBuilt || e == null) return;
            if (!_byEntity.TryGetValue(e, out var primary)) { AddEntity(e); return; }
            if (primary.Motion == MotionDynamic)
            {
                try { CollisionService.RemoveEntityShapes(e, false); } catch { }
                return;
            }
            foreach (var b in _bodies)
            {
                if (b.Entity != e || b.Motion == MotionDynamic) continue;
                if (!TryEntityPose(e, b.CenterOffset, out var pos, out var rot)) continue;
                Fill(_f3a, pos); Fill(_f4, rot);
                try { VortexAPI.PhysicsSetBodyTransform(b.Id, _f3a, _f4); } catch { }
                b.LastPos = pos; b.LastRot = rot;
            }
        }

        // ------------------------------------------------------------------------------------------ per frame

        /// <summary>Advance the simulation by one rendered frame: accumulates <paramref name="frameDt"/> into fixed
        /// 1/60 s steps (max 4), applies the player's pushes recorded by the character controller, drives kinematic
        /// bodies from their entity transforms, steps, writes dynamic poses back to the entities, republishes the
        /// dynamic shapes for the character controller and dispatches contacts. Call after the behaviours' Update
        /// and before LateUpdate/animation.</summary>
        public static void Step(float frameDt)
        {
            LastStepCount = 0;
            if (!IsBuilt) return;
            if (frameDt < 0f) frameDt = 0f;
            if (frameDt > 0.25f) frameDt = 0.25f;
            _accumulator += frameDt;
            if (_accumulator < FixedStep)
            {
                // No step this frame (fast frames): keep the pushes for the next step.
                return;
            }

            try
            {
                if (_jointsDirty) SyncJoints();
                ApplyCharacterPushes(frameDt > 1e-4f ? frameDt : FixedStep);
                int steps = 0;
                while (_accumulator >= FixedStep && steps < MaxSubsteps)
                {
                    PushKinematics();
                    VortexAPI.PhysicsStep(FixedStep, 1);
                    _accumulator -= FixedStep;
                    steps++;
                }
                if (_accumulator > FixedStep) _accumulator = FixedStep;   // drop time we can't catch up with
                LastStepCount = steps;

                ReadbackDynamics();
                PublishDynamicShapes();
                DispatchContacts();
                TrackJointForces();
                DispatchBrokenJoints();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Physics] step failed: " + ex.Message);
            }
        }

        /// <summary>Submit the physics wireframe (all bodies) into the gizmo pass when <see cref="ShowPhysicsDebug"/>
        /// is on. Called once per frame by the script runtime after its own debug shapes.</summary>
        public static void SubmitDebugDraw()
        {
            if (!ShowPhysicsDebug || !IsBuilt) return;
            int n = FetchDebugLines();
            if (n > 0) VortexAPI.RenderPhysicsDebugLines(_dbgBuf, n);
        }

        /// <summary>World-space line segments of every body's shape (6 floats per segment: x0 y0 z0 x1 y1 z1) — the
        /// raw feed for a viewport gizmo layer. Empty when no world is built.</summary>
        public static float[] GetDebugLines()
        {
            if (!IsBuilt) return new float[0];
            int n = FetchDebugLines();
            var result = new float[n];
            if (n > 0) Array.Copy(_dbgBuf, result, n);
            return result;
        }

        private static int FetchDebugLines()
        {
            if (_dbgBuf == null) _dbgBuf = new float[6 * 24000];
            try { return Math.Max(0, Math.Min(_dbgBuf.Length, VortexAPI.PhysicsGetDebugLines(_dbgBuf, _dbgBuf.Length))); }
            catch { return 0; }
        }

        // ------------------------------------------------------------------------------------------ body access

        /// <summary>True when the entity has a simulated body of any kind (static, kinematic, dynamic or trigger).</summary>
        public static bool HasBody(GameEntity e) => e != null && _byEntity.ContainsKey(e);

        /// <summary>True when the entity is simulated as a dynamic or kinematic body (a Rigidbody in play).</summary>
        public static bool HasRigidbody(GameEntity e)
            => e != null && _byEntity.TryGetValue(e, out var b) && (b.Motion == MotionDynamic || b.Motion == MotionKinematic);

        /// <summary>Native body handle of an entity's primary body (0 = none).</summary>
        public static uint GetBodyId(GameEntity e) => e != null && _byEntity.TryGetValue(e, out var b) ? b.Id : 0u;

        /// <summary>The entity a native body handle belongs to (null when unknown).</summary>
        public static GameEntity EntityOfBody(uint bodyId) => _byId.TryGetValue(bodyId, out var b) ? b.Entity : null;

        /// <summary>Continuous force in N at the centre of mass (accumulated until the next step). Wakes the body.</summary>
        public static bool AddForce(GameEntity e, Vector3 force)
        {
            if (!TryDynamic(e, out var b)) return false;
            Wake(b); Fill(_f3a, force);
            VortexAPI.PhysicsAddForce(b.Id, _f3a); return true;
        }

        /// <summary>Force in N applied at a world point (adds torque).</summary>
        public static bool AddForceAtPoint(GameEntity e, Vector3 force, Vector3 worldPoint)
        {
            if (!TryDynamic(e, out var b)) return false;
            Wake(b); Fill(_f3a, force); Fill(_f3b, worldPoint);
            VortexAPI.PhysicsAddForceAtPoint(b.Id, _f3a, _f3b); return true;
        }

        /// <summary>Instant impulse in N·s at the centre of mass.</summary>
        public static bool AddImpulse(GameEntity e, Vector3 impulse)
        {
            if (!TryDynamic(e, out var b)) return false;
            Wake(b); Fill(_f3a, impulse);
            VortexAPI.PhysicsAddImpulse(b.Id, _f3a); return true;
        }

        /// <summary>Instant impulse in N·s at a world point (adds spin — a shot hitting a barrel's rim).</summary>
        public static bool AddImpulseAtPoint(GameEntity e, Vector3 impulse, Vector3 worldPoint)
        {
            if (!TryDynamic(e, out var b)) return false;
            Wake(b); Fill(_f3a, impulse); Fill(_f3b, worldPoint);
            VortexAPI.PhysicsAddImpulseAtPoint(b.Id, _f3a, _f3b); return true;
        }

        /// <summary>Torque in N·m (accumulated until the next step).</summary>
        public static bool AddTorque(GameEntity e, Vector3 torque)
        {
            if (!TryDynamic(e, out var b)) return false;
            Wake(b); Fill(_f3a, torque);
            VortexAPI.PhysicsAddTorque(b.Id, _f3a); return true;
        }

        /// <summary>Set the linear velocity (m/s) of a dynamic body.</summary>
        public static bool SetVelocity(GameEntity e, Vector3 v)
        {
            if (!TryDynamic(e, out var b)) return false;
            Wake(b); Fill(_f3a, v);
            VortexAPI.PhysicsSetLinearVelocity(b.Id, _f3a); return true;
        }

        /// <summary>Linear velocity (m/s) of a body — zero for entities without a dynamic/kinematic body.</summary>
        public static Vector3 GetVelocity(GameEntity e)
        {
            if (!TryMoving(e, out var b)) return Vector3.Zero;
            VortexAPI.PhysicsGetLinearVelocity(b.Id, _f3a);
            return new Vector3(_f3a[0], _f3a[1], _f3a[2]);
        }

        /// <summary>Set the angular velocity (rad/s) of a dynamic body.</summary>
        public static bool SetAngularVelocity(GameEntity e, Vector3 v)
        {
            if (!TryDynamic(e, out var b)) return false;
            Wake(b); Fill(_f3a, v);
            VortexAPI.PhysicsSetAngularVelocity(b.Id, _f3a); return true;
        }

        /// <summary>Angular velocity (rad/s) of a body — zero for entities without a dynamic/kinematic body.</summary>
        public static Vector3 GetAngularVelocity(GameEntity e)
        {
            if (!TryMoving(e, out var b)) return Vector3.Zero;
            VortexAPI.PhysicsGetAngularVelocity(b.Id, _f3a);
            return new Vector3(_f3a[0], _f3a[1], _f3a[2]);
        }

        /// <summary>Switch a Rigidbody entity between kinematic (moved by scripts/animation, pushes others) and
        /// dynamic (simulated). A kinematic entity becomes solid in the static collision world at its current pose.</summary>
        public static bool SetKinematic(GameEntity e, bool kinematic)
        {
            if (!TryMoving(e, out var b)) return false;
            int motion = kinematic ? MotionKinematic : MotionDynamic;
            if (b.Motion == motion) return true;
            VortexAPI.PhysicsSetMotionType(b.Id, motion);
            b.Motion = motion;
            b.Layer = LayerFor(e, motion, b.IsTrigger);
            try
            {
                if (kinematic) { CollisionService.RemoveEntityShapes(e); CollisionService.AddEntityShapes(e); }   // subtree out, subtree in (no duplicates)
                else CollisionService.RemoveEntityShapes(e, false);
            }
            catch { }
            Wake(b);
            PublishDynamicShapes();
            return true;
        }

        /// <summary>True when the entity's body is kinematic.</summary>
        public static bool IsKinematic(GameEntity e) => e != null && _byEntity.TryGetValue(e, out var b) && b.Motion == MotionKinematic;

        /// <summary>Wake a sleeping body (Jolt puts resting bodies to sleep; forces and impulses wake them too).</summary>
        public static bool WakeUp(GameEntity e)
        {
            if (!TryMoving(e, out var b)) return false;
            Wake(b); return true;
        }

        /// <summary>True when the body is asleep (at rest) — also true for entities without a moving body.</summary>
        public static bool IsSleeping(GameEntity e)
        {
            if (!TryMoving(e, out var b)) return true;
            try { return VortexAPI.PhysicsIsActive(b.Id) == 0; } catch { return true; }
        }

        /// <summary>Mass in kg of the entity's body (0 for static / none).</summary>
        public static float GetMass(GameEntity e)
        {
            if (!TryMoving(e, out var b)) return 0f;
            try { return VortexAPI.PhysicsGetMass(b.Id); } catch { return 0f; }
        }

        // ------------------------------------------------------------------------------------------ queries

        /// <summary>Layer mask that hits solid bodies only (static, dynamic, character, debris — not triggers).</summary>
        public const uint SolidLayerMask = (1u << LayerStatic) | (1u << LayerDynamic) | (1u << LayerCharacter) | (1u << LayerDebris);

        /// <summary>Closest solid hit along <paramref name="direction"/> (normalised inside). Returns the hit point,
        /// unit normal, entity and distance. <paramref name="layerMask"/> is the OBJECT-layer mask (bit i = layer i).</summary>
        public static bool Raycast(Vector3 origin, Vector3 direction, float maxDist, out Vector3 point, out Vector3 normal,
            out GameEntity entity, out float distance, uint layerMask = SolidLayerMask)
        {
            point = origin; normal = new Vector3(0f, 1f, 0f); entity = null; distance = 0f;
            if (!IsBuilt || maxDist <= 0f) return false;
            float len = (float)Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z);
            if (len < 1e-6f) return false;
            _f3a[0] = origin.X; _f3a[1] = origin.Y; _f3a[2] = origin.Z;
            _f3b[0] = direction.X / len; _f3b[1] = direction.Y / len; _f3b[2] = direction.Z / len;
            float dist; ulong entId; uint body;
            int hit;
            try { hit = VortexAPI.PhysicsRaycast(_f3a, _f3b, maxDist, layerMask, _f3c, _f4, out dist, out entId, out body); }
            catch { return false; }
            if (hit == 0) return false;
            point = new Vector3(_f3c[0], _f3c[1], _f3c[2]);
            normal = new Vector3(_f4[0], _f4[1], _f4[2]);
            distance = dist;
            entity = EntityOfBody(body) ?? FindByEntityId(entId);
            return true;
        }

        /// <summary>Every entity whose body overlaps the sphere (solid layers by default; deduplicated).</summary>
        public static List<GameEntity> OverlapSphere(Vector3 center, float radius, uint layerMask = SolidLayerMask, int maxCount = 256)
        {
            var result = new List<GameEntity>();
            if (!IsBuilt || radius <= 0f) return result;
            var ents = new ulong[Math.Max(1, maxCount)];
            var bodies = new uint[ents.Length];
            _f3a[0] = center.X; _f3a[1] = center.Y; _f3a[2] = center.Z;
            int n;
            try { n = VortexAPI.PhysicsOverlapSphere(_f3a, radius, layerMask, ents, bodies, ents.Length); }
            catch { return result; }
            for (int i = 0; i < n && i < ents.Length; i++)
            {
                var e = EntityOfBody(bodies[i]) ?? FindByEntityId(ents[i]);
                if (e != null && !result.Contains(e)) result.Add(e);
            }
            return result;
        }

        // ------------------------------------------------------------------------------------------ joints (#103)
        // Script API building blocks (Physics.SetHingeMotor(entity, …), joint queries): they act on the entity's
        // first joint of the requested kind, or on a specific component. Runtime overrides only — the components
        // are never written, so play stays non-destructive; an inspector edit of the motor fields re-applies them.

        /// <summary>True when the joint component is simulated (attached to its bodies; it may be disabled / broken).</summary>
        public static bool HasJoint(PhysicsJoint joint) => TryJoint(joint, out _);

        /// <summary>True when the joint exceeded its Break Force (it stays disabled until re-enabled).</summary>
        public static bool IsJointBroken(PhysicsJoint joint) => TryJoint(joint, out var r) && r.Broken;

        /// <summary>Enable / disable a joint at runtime; enabling a broken joint repairs it (the bodies are pulled back
        /// to the joint's rest pose).</summary>
        public static bool SetJointEnabled(PhysicsJoint joint, bool enabled)
        {
            if (!TryJoint(joint, out var r)) return false;
            try { VortexAPI.PhysicsSetConstraintEnabled(r.Id, enabled ? 1 : 0); } catch { return false; }
            r.Enabled = enabled;
            if (enabled) r.Broken = false;
            return true;
        }

        /// <summary>Hinge angle in degrees relative to the play-start pose (0 when the entity has no hinge).</summary>
        public static float GetHingeAngle(GameEntity e) => GetHingeAngle(FirstJoint<HingeJoint>(e));
        public static float GetHingeAngle(PhysicsJoint joint)
        {
            if (!TryJoint(joint, out var r) || !(joint is HingeJoint)) return 0f;
            try { return VortexAPI.PhysicsGetHingeAngle(r.Id); } catch { return 0f; }
        }

        /// <summary>Slider position in metres along its axis relative to the play-start pose.</summary>
        public static float GetSliderPosition(GameEntity e) => GetSliderPosition(FirstJoint<SliderJoint>(e));
        public static float GetSliderPosition(PhysicsJoint joint)
        {
            if (!TryJoint(joint, out var r) || !(joint is SliderJoint)) return 0f;
            try { return VortexAPI.PhysicsGetSliderPosition(r.Id); } catch { return 0f; }
        }

        /// <summary>Linear force in N the joint applied in the last physics step (tune Break Force with it).</summary>
        public static float GetJointForce(PhysicsJoint joint)
        {
            if (!TryJoint(joint, out var r)) return 0f;
            try { return VortexAPI.PhysicsGetConstraintForce(r.Id); } catch { return 0f; }
        }

        /// <summary>Highest force in N the joint carried since play start (sampled every frame) — a door slamming
        /// into its stop, a lift starting with its load. Set Break Force above what normal use peaks at.</summary>
        public static float GetJointPeakForce(PhysicsJoint joint) => TryJoint(joint, out var r) ? r.PeakForce : 0f;

        /// <summary>Drive the entity's hinge: Velocity = <paramref name="target"/> deg/s, Position = spring to
        /// <paramref name="target"/> deg (frequency Hz + damping ratio), Off = free (no friction). maxTorque in N·m
        /// (0 = unlimited).</summary>
        public static bool SetHingeMotor(GameEntity e, JointMotorMode mode, float target, float maxTorque, float frequency = 2f, float damping = 1f)
        {
            if (!TryJoint(FirstJoint<HingeJoint>(e), out var r)) return false;
            try { VortexAPI.PhysicsSetHingeMotor(r.Id, (int)mode, target, maxTorque, frequency, damping); } catch { return false; }
            return true;
        }

        /// <summary>Drive the entity's slider: Velocity = <paramref name="target"/> m/s, Position = spring to
        /// <paramref name="target"/> m. maxForce in N (0 = unlimited).</summary>
        public static bool SetSliderMotor(GameEntity e, JointMotorMode mode, float target, float maxForce, float frequency = 2f, float damping = 1f)
        {
            if (!TryJoint(FirstJoint<SliderJoint>(e), out var r)) return false;
            try { VortexAPI.PhysicsSetSliderMotor(r.Id, (int)mode, target, maxForce, frequency, damping); } catch { return false; }
            return true;
        }

        private static T FirstJoint<T>(GameEntity e) where T : PhysicsJoint
        {
            if (e?.Components == null) return null;
            foreach (var c in e.Components) if (c is T t && _jointByComponent.ContainsKey(t)) return t;
            return null;
        }

        private static bool TryJoint(PhysicsJoint joint, out JointRec rec)
        {
            rec = null;
            return IsBuilt && joint != null && _jointByComponent.TryGetValue(joint, out rec) && rec.Id != 0;
        }

        /// <summary>Remember every joint component of a subtree (active entities) and watch it for live edits.</summary>
        private static void CollectJoints(GameEntity e)
        {
            if (e == null || !e.IsActive) return;
            if (e.Components != null)
                foreach (var c in e.Components)
                    if (c is PhysicsJoint j && _jointComponentSet.Add(j))
                    {
                        _jointComponents.Add(j);
                        j.PropertyChanged += OnJointComponentChanged;
                    }
            if (e.Children != null) foreach (var ch in e.Children) CollectJoints(ch);
        }

        /// <summary>Create every known joint that is not simulated yet. Detached joints (their connected entity went
        /// away) re-attach when <paramref name="added"/> (a subtree that just came back) contains that entity.</summary>
        private static void CreatePendingJoints(GameEntity added)
        {
            for (int i = 0; i < _jointComponents.Count; i++)
            {
                var j = _jointComponents[i];
                if (_jointByComponent.TryGetValue(j, out var rec))
                {
                    if (rec.Id != 0 || added == null) continue;
                    var c = j.ResolveConnectedEntity(_scene?.Entities, out _);
                    if (c == null || !IsInSubtree(c, added) || !_byEntity.ContainsKey(c)) continue;
                    ForgetRec(rec);
                }
                try { TryCreateJoint(j); }
                catch (Exception ex) { WarnJoint(j, "creation failed: " + ex.Message); }
            }
        }

        private static bool TryCreateJoint(PhysicsJoint j)
        {
            var e = j.Entity;
            if (e == null || !j.IsEnabled || !ActiveInHierarchy(e)) return false;   // enabling it during play attaches it then
            if (!_byEntity.TryGetValue(e, out var own))
            {
                WarnJoint(j, "needs a Collider on its entity (and a Dynamic Rigidbody to be moved by the joint) - joint skipped");
                return false;
            }
            var connected = j.ResolveConnectedEntity(_scene?.Entities, out bool notFound);
            if (notFound) { WarnJoint(j, "connected entity '" + j.ConnectedEntity + "' not found - joint skipped"); return false; }
            if (ReferenceEquals(connected, e)) { WarnJoint(j, "is connected to its own entity - joint skipped"); return false; }
            // A connected entity without a body is static scenery: the joint holds on to the world at the same spot.
            uint bodyB = connected != null && _byEntity.TryGetValue(connected, out var other) ? other.Id : 0u;

            var world = Animation.BoneSocketService.EntityWorld(e);
            var rot = RotationOf(world);
            var anchor = SysVec.Transform(ToSys(j.Anchor), world);   // local (scaled, like Collider.Center) -> world
            Fill(_f3a, anchor);
            uint id = 0;
            switch (j)
            {
                case HingeJoint h:
                {
                    // Angle 0 is drawn along the leaf: from the hinge towards the body's centre.
                    var toBody = own.LastPos + SysVec.Transform(own.BoundsCenter, own.LastRot) - anchor;
                    Fill(_f3b, WorldDir(rot, h.Axis, SysVec.UnitY)); Fill(_f3c, toBody);
                    id = VortexAPI.PhysicsCreateHinge(own.Id, bodyB, _f3a, _f3b, _f3c, h.MinAngle, h.MaxAngle, h.UseLimits ? 1 : 0, 0f, 0f, j.BreakForce);
                    break;
                }
                case BallJoint b:
                    Fill(_f3b, WorldDir(rot, b.Axis, -SysVec.UnitY));
                    id = VortexAPI.PhysicsCreateBallJoint(own.Id, bodyB, _f3a, _f3b, b.SwingLimit, b.TwistMin, b.TwistMax, b.UseLimits ? 1 : 0, j.BreakForce);
                    break;
                case SliderJoint s:
                    Fill(_f3b, WorldDir(rot, s.Axis, SysVec.UnitX));
                    id = VortexAPI.PhysicsCreateSlider(own.Id, bodyB, _f3a, _f3b, s.MinPosition, s.MaxPosition, s.UseLimits ? 1 : 0,
                        0, 0f, 0f, s.SpringFrequency, s.SpringDamping, j.BreakForce);
                    break;
                case FixedJoint _:
                    id = VortexAPI.PhysicsCreateFixed(own.Id, bodyB, _f3a, j.BreakForce);
                    break;
                case DistanceJoint d:
                {
                    // The other end: local to the connected entity, or a world offset from the anchor for the world.
                    var end = connected != null
                        ? SysVec.Transform(ToSys(d.ConnectedAnchor), Animation.BoneSocketService.EntityWorld(connected))
                        : anchor + ToSys(d.ConnectedAnchor);
                    Fill(_f3b, end);
                    id = VortexAPI.PhysicsCreateDistance(own.Id, bodyB, _f3a, _f3b, d.MinDistance, d.MaxDistance, d.SpringFrequency, d.SpringDamping, j.BreakForce);
                    break;
                }
                default:
                    return false;
            }
            if (id == 0) { WarnJoint(j, "the physics world refused the joint"); return false; }

            var rec = new JointRec { Id = id, BodyA = own.Id, BodyB = bodyB, Entity = e, Joint = j, Connected = bodyB != 0 ? connected : null, Enabled = true };
            _joints.Add(rec);
            _jointById[id] = rec;
            _jointByComponent[j] = rec;
            ApplyMotor(rec);
            return true;
        }

        /// <summary>Motor / spring / friction from the component. Friction = a zero-speed velocity motor whose
        /// torque / force limit is the friction.</summary>
        private static void ApplyMotor(JointRec rec)
        {
            if (rec.Id == 0) return;
            if (rec.Joint is HingeJoint h)
            {
                int mode = (int)h.MotorMode;
                float target = h.MotorMode == JointMotorMode.Position ? h.TargetAngle : h.TargetVelocity, limit = h.MaxTorque;
                if (h.MotorMode == JointMotorMode.Off && h.Friction > 0f) { mode = (int)JointMotorMode.Velocity; target = 0f; limit = h.Friction; }
                VortexAPI.PhysicsSetHingeMotor(rec.Id, mode, target, limit, h.SpringFrequency, h.SpringDamping);
            }
            else if (rec.Joint is SliderJoint s)
            {
                int mode = (int)s.MotorMode;
                float target = s.MotorMode == JointMotorMode.Position ? s.TargetPosition : s.TargetVelocity, limit = s.MaxForce;
                if (s.MotorMode == JointMotorMode.Off && s.Friction > 0f) { mode = (int)JointMotorMode.Velocity; target = 0f; limit = s.Friction; }
                VortexAPI.PhysicsSetSliderMotor(rec.Id, mode, target, limit, s.SpringFrequency, s.SpringDamping);
            }
        }

        /// <summary>Inspector edits during play: the enabled flag and the motor fields apply live (next step); the
        /// geometry (anchor, axis, limits, connected entity) is fixed at creation.</summary>
        private static void OnJointComponentChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is PhysicsJoint j && _jointByComponent.TryGetValue(j, out var rec))
            {
                if (e.PropertyName == nameof(PhysicsJoint.IsEnabled)) rec.EnabledDirty = true;
                else rec.MotorDirty = true;
            }
            _jointsDirty = true;   // not simulated yet: enabling it attaches it at the current pose
        }

        private static void SyncJoints()
        {
            _jointsDirty = false;
            foreach (var rec in _joints)
            {
                if (rec.Id == 0) continue;
                if (rec.EnabledDirty)
                {
                    rec.EnabledDirty = false;
                    bool want = rec.Joint.IsEnabled;
                    if (want != rec.Enabled) SetJointEnabled(rec.Joint, want);
                }
                if (rec.MotorDirty)
                {
                    rec.MotorDirty = false;
                    try { ApplyMotor(rec); } catch { }
                }
            }
            CreatePendingJoints(null);
        }

        private static void TrackJointForces()
        {
            for (int i = 0; i < _joints.Count; i++)
            {
                var r = _joints[i];
                if (r.Id == 0 || !r.Enabled) continue;
                float f = VortexAPI.PhysicsGetConstraintForce(r.Id);   // before the break check: a breaking force counts too
                if (f > r.PeakForce) r.PeakForce = f;
            }
        }

        private static void DispatchBrokenJoints()
        {
            if (_joints.Count == 0) return;
            int n;
            do
            {
                n = VortexAPI.PhysicsGetBrokenConstraints(_brokenIds, _brokenForces, _brokenIds.Length);
                for (int i = 0; i < n && i < _brokenIds.Length; i++)
                {
                    if (!_jointById.TryGetValue(_brokenIds[i], out var rec)) continue;
                    rec.Broken = true;
                    rec.Enabled = false;
                    if (_brokenForces[i] > rec.PeakForce) rec.PeakForce = _brokenForces[i];
                    Log("[Physics] " + rec.Joint.DisplayName + " on '" + rec.Entity?.Name + "' broke: "
                        + _brokenForces[i].ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " N > break force "
                        + rec.Joint.BreakForce.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " N");
                    try
                    {
                        JointBroken?.Invoke(new PhysicsJointBreakEvent { Entity = rec.Entity, Joint = rec.Joint, Connected = rec.Connected, Force = _brokenForces[i] });
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Physics] JointBroken handler: " + ex.Message); }
                }
            } while (n >= _brokenIds.Length);
        }

        /// <summary>Before a subtree's bodies are destroyed: its own joints go (and are forgotten until the subtree is
        /// added again), joints of other entities connected into it detach (Id 0) until it comes back.</summary>
        private static void DetachJoints(GameEntity root)
        {
            for (int i = _joints.Count - 1; i >= 0; i--)
            {
                var rec = _joints[i];
                bool own = IsInSubtree(rec.Entity, root);
                if (!own && !(rec.Connected != null && IsInSubtree(rec.Connected, root))) continue;
                if (rec.Id != 0) { try { VortexAPI.PhysicsDestroyConstraint(rec.Id); } catch { } _jointById.Remove(rec.Id); rec.Id = 0; }
                if (own) ForgetRec(rec);
            }
            for (int i = _jointComponents.Count - 1; i >= 0; i--)
            {
                var j = _jointComponents[i];
                if (!IsInSubtree(j.Entity, root)) continue;
                j.PropertyChanged -= OnJointComponentChanged;
                _jointComponents.RemoveAt(i);
                _jointComponentSet.Remove(j);
                _jointWarned.Remove(j);
            }
        }

        private static void ForgetRec(JointRec rec)
        {
            if (rec.Id != 0) _jointById.Remove(rec.Id);
            _joints.Remove(rec);
            _jointByComponent.Remove(rec.Joint);
        }

        /// <summary>True when an enabled joint holds the body (doors, lifts, ropes).</summary>
        private static bool IsJointed(uint bodyId)
        {
            for (int i = 0; i < _joints.Count; i++)
            {
                var r = _joints[i];
                if (r.Id != 0 && r.Enabled && (r.BodyA == bodyId || r.BodyB == bodyId)) return true;
            }
            return false;
        }

        private static bool IsInSubtree(GameEntity e, GameEntity root)
        {
            for (var p = e; p != null; p = p.Parent) if (ReferenceEquals(p, root)) return true;
            return false;
        }

        private static bool ActiveInHierarchy(GameEntity e)
        {
            for (var p = e; p != null; p = p.Parent) if (!p.IsActive) return false;
            return true;
        }

        private static SysVec WorldDir(SysQuat rot, Vector3 local, SysVec fallback)
        {
            var v = ToSys(local);
            if (v.LengthSquared() < 1e-10f) v = fallback;
            return SysVec.Normalize(SysVec.Transform(v, rot));
        }

        private static void WarnJoint(PhysicsJoint j, string msg)
        {
            if (j == null || !_jointWarned.Add(j)) return;   // once per component and play session
            Warn(j.Entity, j.DisplayName + " " + msg);
        }

        // ------------------------------------------------------------------------------------------ build helpers

        private static void AddRecursive(GameEntity e)
        {
            if (e == null) return;
            if (e.IsActive && !_byEntity.ContainsKey(e))
            {
                try { CreateBodiesFor(e); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Physics] body for '" + e.Name + "' failed: " + ex.Message); }
            }
            if (e.Children != null && e.IsActive) foreach (var c in e.Children) AddRecursive(c);
        }

        private static void RemoveRecursive(GameEntity e)
        {
            if (e == null) return;
            if (_byEntity.ContainsKey(e))
            {
                for (int i = _bodies.Count - 1; i >= 0; i--)
                {
                    var b = _bodies[i];
                    if (b.Entity != e) continue;
                    try { VortexAPI.PhysicsDestroyBody(b.Id); } catch { }
                    _byId.Remove(b.Id);
                    _bodies.RemoveAt(i);
                }
                _byEntity.Remove(e);
            }
            if (e.Children != null) foreach (var c in e.Children) RemoveRecursive(c);
        }

        private static void CreateBodiesFor(GameEntity e)
        {
            var colliders = e.GetComponents<Collider>();
            var solids = new List<Collider>();
            var triggers = new List<Collider>();
            if (colliders != null)
                foreach (var c in colliders)
                    if (c != null && c.IsEnabled) { if (c.IsTrigger) triggers.Add(c); else solids.Add(c); }

            var rb = e.GetComponent<Rigidbody>();
            if (rb != null && !rb.IsEnabled) rb = null;
            int motion = rb == null ? MotionStatic
                : rb.BodyType == RigidbodyType.Dynamic ? MotionDynamic
                : rb.BodyType == RigidbodyType.Kinematic ? MotionKinematic : MotionStatic;

            bool derivedFromMesh = false;
            if (solids.Count == 0 && triggers.Count == 0)
            {
                // A Rigidbody without any Collider: the render mesh's shape stands in (a Collider is still the
                // recommended setup — this keeps "just add a Rigidbody to the crate" working).
                if (rb == null || e.GetComponent<MeshRenderer>() == null) return;
                derivedFromMesh = true;
            }

            var world = Animation.BoneSocketService.EntityWorld(e);
            var scale = ScaleOf(world);
            var rot = RotationOf(world);
            var pos = world.Translation;
            ulong entityId = unchecked((ulong)e.EntityId);

            Body primary = null;
            if (solids.Count > 0 || derivedFromMesh)
            {
                primary = CreateGroup(e, entityId, derivedFromMesh ? null : solids, motion, rb, pos, rot, scale, false);
                if (primary != null) Register(e, primary, false);
            }
            if (triggers.Count > 0)
            {
                // Sensor twin: static for level triggers, kinematic (driven from the entity pose each step) when the
                // entity moves — so a trigger volume glued to a moving prop follows it.
                int sensorMotion = motion == MotionStatic ? MotionStatic : MotionKinematic;
                var sensor = CreateGroup(e, entityId, triggers, sensorMotion, rb, pos, rot, scale, true);
                if (sensor != null) Register(e, sensor, primary != null);
            }

            // Dynamic entities leave the static collision world (CollisionService.Build baked them at their start
            // pose): the character collides with their LIVE pose through the published dynamic shapes instead.
            if (primary != null && primary.Motion == MotionDynamic)
            {
                try { CollisionService.RemoveEntityShapes(e, false); } catch { }
            }
        }

        private static void Register(GameEntity e, Body b, bool secondary)
        {
            b.Secondary = secondary;
            _bodies.Add(b);
            _byId[b.Id] = b;
            if (!secondary) _byEntity[e] = b;
        }

        /// <summary>Create one body from a group of colliders (null = derive from the MeshRenderer).</summary>
        private static Body CreateGroup(GameEntity e, ulong entityId, List<Collider> group, int motion, Rigidbody rb,
            SysVec pos, SysQuat rot, SysVec scale, bool isTrigger)
        {
            int layer = LayerFor(e, motion, isTrigger);
            float mass = rb != null ? rb.Mass : 0f;
            float linDamp = rb != null ? Math.Max(0f, rb.Drag) : 0f;
            float angDamp = rb != null ? Math.Max(0f, rb.AngularDrag) : 0.05f;
            float gravityFactor = rb == null || rb.UseGravity ? 1f : 0f;
            uint lockFlags = LockFlagsOf(rb);
            PhysicsMaterial mat = null;
            if (group != null) foreach (var c in group) if (c.Material != null) { mat = c.Material; break; }
            float friction = mat != null ? Clamp(mat.Friction, 0f, 5f) : 0.5f;
            float restitution = mat != null ? Clamp(mat.Bounciness, 0f, 1f) : 0f;

            var children = new List<ChildShape>();
            MeshCollider meshCol = null;
            Collider baseMeshCol = null;
            if (group != null)
            {
                foreach (var c in group)
                {
                    if (TryPrimitive(c, scale, out var cs)) { children.Add(cs); continue; }
                    if (c is MeshCollider mc) { if (meshCol == null) meshCol = mc; else Warn(e, "only one Mesh Collider per entity is simulated"); }
                    else if (baseMeshCol == null) baseMeshCol = c;   // plain Collider with type Mesh/Convex
                }
            }

            var mr = e.GetComponent<MeshRenderer>();
            bool wantMesh = group == null || meshCol != null || (baseMeshCol != null && children.Count == 0);
            if (wantMesh)
            {
                string meshPath = meshCol != null && !string.IsNullOrEmpty(meshCol.MeshPath) ? meshCol.MeshPath : mr?.MeshPath;
                var centerCol = (Collider)meshCol ?? baseMeshCol;
                var center = centerCol != null ? Mul(ToSys(centerCol.Center), scale) : SysVec.Zero;
                bool convex = meshCol != null ? meshCol.Convex : (baseMeshCol != null ? baseMeshCol.ColliderType == ColliderType.Convex : motion != MotionStatic);
                if (!string.IsNullOrEmpty(meshPath) && meshPath.StartsWith("Primitive:", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryPrimitiveFromMeshPath(meshPath, scale, center, out var pcs)) children.Add(pcs);
                }
                else if (children.Count == 0)
                {
                    var body = CreateMeshBody(e, entityId, meshPath, center, convex, motion, mass, friction, restitution, isTrigger, layer, pos, rot, scale);
                    return body;
                }
                else Warn(e, "Mesh Collider ignored: it is combined with primitive colliders on the same entity");
            }
            if (children.Count == 0) return null;

            Fill(_f3a, pos); Fill(_f4, rot);
            uint id;
            if (children.Count == 1 && IsCentred(children[0]))
            {
                var cs = children[0];
                Fill(_f3b, cs.Dims);
                id = VortexAPI.PhysicsCreateBody(entityId, cs.Type, _f3b, _f3a, _f4, motion, mass, friction, restitution,
                    linDamp, angDamp, isTrigger ? 1 : 0, layer, lockFlags, gravityFactor);
            }
            else
            {
                var buf = new float[children.Count * 11];
                for (int i = 0; i < children.Count; i++)
                {
                    var cs = children[i]; int o = i * 11;
                    buf[o] = cs.Type;
                    buf[o + 1] = cs.Dims.X; buf[o + 2] = cs.Dims.Y; buf[o + 3] = cs.Dims.Z;
                    buf[o + 4] = cs.LocalPos.X; buf[o + 5] = cs.LocalPos.Y; buf[o + 6] = cs.LocalPos.Z;
                    buf[o + 7] = cs.LocalRot.X; buf[o + 8] = cs.LocalRot.Y; buf[o + 9] = cs.LocalRot.Z; buf[o + 10] = cs.LocalRot.W;
                }
                id = VortexAPI.PhysicsCreateCompoundBody(entityId, buf, children.Count, _f3a, _f4, motion, mass, friction, restitution,
                    linDamp, angDamp, isTrigger ? 1 : 0, layer, lockFlags, gravityFactor);
            }
            if (id == 0) { Warn(e, "physics body creation failed"); return null; }

            var b = new Body { Id = id, Entity = e, Motion = motion, Layer = layer, IsTrigger = isTrigger, Scale = scale, LastPos = pos, LastRot = rot };
            ComputeBounds(children, out b.BoundsCenter, out b.BoundsHalf);
            if (children.Count == 1 && children[0].Type == ShapeSphere && IsCentred(children[0])) b.SphereRadius = children[0].Dims.X;
            return b;
        }

        private static Body CreateMeshBody(GameEntity e, ulong entityId, string meshPath, SysVec center, bool convex, int motion,
            float mass, float friction, float restitution, bool isTrigger, int layer, SysVec pos, SysQuat rot, SysVec scale)
        {
            if (string.IsNullOrEmpty(meshPath)) { Warn(e, "Mesh Collider has no mesh (set a Mesh Renderer or a mesh path)"); return null; }
            float[] tris = null;
            try { tris = CollisionService.MeshTriangleProvider?.Invoke(meshPath); } catch { }
            if (tris == null || tris.Length < 9) { Warn(e, "Mesh Collider: no triangles for '" + meshPath + "'"); return null; }
            if (!convex && motion != MotionStatic)
            {
                // Triangle meshes can only be static in Jolt — a moving mesh collider simulates as its convex hull.
                convex = true;
            }
            int vertCount = tris.Length / 3;
            var indices = new uint[vertCount];
            for (uint i = 0; i < indices.Length; i++) indices[i] = i;

            var bodyPos = pos + SysVec.Transform(center, rot);
            Fill(_f3a, bodyPos); Fill(_f4, rot); Fill(_f3b, scale);
            uint id = VortexAPI.PhysicsCreateMeshBody(entityId, tris, vertCount, indices, indices.Length, _f3a, _f4, _f3b,
                convex ? 1 : 0, motion, mass, friction, restitution, isTrigger ? 1 : 0, layer);
            if (id == 0) { Warn(e, "mesh body creation failed for '" + meshPath + "'"); return null; }

            var mn = new SysVec(float.MaxValue); var mx = new SysVec(float.MinValue);
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                var v = new SysVec(tris[i] * scale.X, tris[i + 1] * scale.Y, tris[i + 2] * scale.Z);
                mn = SysVec.Min(mn, v); mx = SysVec.Max(mx, v);
            }
            return new Body
            {
                Id = id, Entity = e, Motion = motion, Layer = layer, IsTrigger = isTrigger, Scale = scale,
                CenterOffset = center, LastPos = bodyPos, LastRot = rot,
                BoundsCenter = (mn + mx) * 0.5f, BoundsHalf = (mx - mn) * 0.5f
            };
        }

        private static bool TryPrimitive(Collider c, SysVec scale, out ChildShape cs)
        {
            cs = default(ChildShape);
            cs.LocalPos = Mul(ToSys(c.Center), scale);
            cs.LocalRot = SysQuat.Identity;
            var a = Abs(scale);
            if (c is BoxCollider box)
            {
                cs.Type = ShapeBox;
                cs.Dims = new SysVec(Math.Max(0.005f, Math.Abs(box.Size.X * 0.5f) * a.X), Math.Max(0.005f, Math.Abs(box.Size.Y * 0.5f) * a.Y), Math.Max(0.005f, Math.Abs(box.Size.Z * 0.5f) * a.Z));
                return true;
            }
            if (c is SphereCollider sph)
            {
                cs.Type = ShapeSphere;
                cs.Dims = new SysVec(Math.Max(0.005f, Math.Abs(sph.Radius) * Math.Max(a.X, Math.Max(a.Y, a.Z))), 0f, 0f);
                return true;
            }
            if (c is CapsuleCollider cap)
            {
                cs.Type = ShapeCapsule;
                float axisScale, r;
                if (cap.Direction == 0) { r = Math.Abs(cap.Radius) * Math.Max(a.Y, a.Z); axisScale = a.X; cs.LocalRot = SysQuat.CreateFromAxisAngle(SysVec.UnitZ, -(float)Math.PI * 0.5f); }
                else if (cap.Direction == 2) { r = Math.Abs(cap.Radius) * Math.Max(a.X, a.Y); axisScale = a.Z; cs.LocalRot = SysQuat.CreateFromAxisAngle(SysVec.UnitX, (float)Math.PI * 0.5f); }
                else { r = Math.Abs(cap.Radius) * Math.Max(a.X, a.Z); axisScale = a.Y; }
                r = Math.Max(0.005f, r);
                float half = Math.Max(0f, Math.Abs(cap.Height) * 0.5f * axisScale - r);
                cs.Dims = new SysVec(r, half, 0f);
                return true;
            }
            return false;   // Mesh / base collider
        }

        private static bool TryPrimitiveFromMeshPath(string meshPath, SysVec scale, SysVec center, out ChildShape cs)
        {
            cs = default(ChildShape);
            cs.LocalPos = center; cs.LocalRot = SysQuat.Identity;
            var a = Abs(scale);
            var prim = meshPath.Substring("Primitive:".Length).ToLowerInvariant();
            switch (prim)
            {
                case "cube": cs.Type = ShapeBox; cs.Dims = new SysVec(0.5f * a.X, 0.5f * a.Y, 0.5f * a.Z); return true;
                case "plane": case "quad": cs.Type = ShapeBox; cs.Dims = new SysVec(0.5f * a.X, 0.05f, 0.5f * a.Z); return true;
                case "sphere": cs.Type = ShapeSphere; cs.Dims = new SysVec(0.5f * Math.Max(a.X, Math.Max(a.Y, a.Z)), 0f, 0f); return true;
                case "capsule":
                    {
                        float r = 0.5f * Math.Max(a.X, a.Z);
                        cs.Type = ShapeCapsule; cs.Dims = new SysVec(r, Math.Max(0f, 0.5f * a.Y - r), 0f); return true;
                    }
                case "cylinder": case "cone":
                    cs.Type = ShapeCylinder; cs.Dims = new SysVec(0.5f * Math.Max(a.X, a.Z), 0.5f * a.Y, 0f); return true;
            }
            return false;
        }

        private static bool IsCentred(ChildShape cs)
            => cs.LocalPos.LengthSquared() < 1e-10f && Math.Abs(cs.LocalRot.W) > 0.99999f;

        private static void ComputeBounds(List<ChildShape> children, out SysVec center, out SysVec half)
        {
            var mn = new SysVec(float.MaxValue); var mx = new SysVec(float.MinValue);
            foreach (var cs in children)
            {
                SysVec ext;
                switch (cs.Type)
                {
                    case ShapeSphere: ext = new SysVec(cs.Dims.X); break;
                    case ShapeCapsule:
                        {
                            // extent along the (rotated) capsule axis = r + half height, r across
                            var axis = SysVec.Abs(SysVec.Transform(SysVec.UnitY, cs.LocalRot));
                            ext = new SysVec(cs.Dims.X) + axis * cs.Dims.Y; break;
                        }
                    case ShapeCylinder: ext = new SysVec(cs.Dims.X, cs.Dims.Y, cs.Dims.X); break;
                    default: ext = cs.Dims; break;
                }
                mn = SysVec.Min(mn, cs.LocalPos - ext); mx = SysVec.Max(mx, cs.LocalPos + ext);
            }
            center = (mn + mx) * 0.5f; half = (mx - mn) * 0.5f;
        }

        private static int LayerFor(GameEntity e, int motion, bool isTrigger)
        {
            if (isTrigger) return LayerTrigger;
            if (motion == MotionStatic) return LayerStatic;
            if (motion == MotionDynamic && string.Equals(e.Tag, DebrisTag, StringComparison.OrdinalIgnoreCase)) return LayerDebris;
            return LayerDynamic;
        }

        private static uint LockFlagsOf(Rigidbody rb)
        {
            if (rb == null) return 0u;
            uint f = 0;
            if (rb.FreezePositionX) f |= 1u << 0;
            if (rb.FreezePositionY) f |= 1u << 1;
            if (rb.FreezePositionZ) f |= 1u << 2;
            if (rb.FreezeRotationX) f |= 1u << 3;
            if (rb.FreezeRotationY) f |= 1u << 4;
            if (rb.FreezeRotationZ) f |= 1u << 5;
            return f;
        }

        // ------------------------------------------------------------------------------------------ step helpers

        /// <summary>Turn the character controller's pushes against dynamic shapes into impulses on the bodies, so the
        /// player shoves barrels and crates instead of bouncing off immovable ghosts. Impulse = the spec's
        /// 80 kg × relative speed along the normal × 0.3, capped at 60 N·s — and additionally at body mass × relative
        /// speed, so a light box never leaves faster than the player pushed it.</summary>
        private static void ApplyCharacterPushes(float frameDt)
        {
            var contacts = CollisionService.CharacterContacts;
            if (contacts.Count == 0) return;
            _pushes.Clear();
            float invDt = 1f / Math.Max(frameDt, 1e-4f);
            for (int i = 0; i < contacts.Count; i++)
            {
                var c = contacts[i];
                if (!_byId.TryGetValue(c.BodyId, out var b) || b.Motion != MotionDynamic) continue;
                var into = -SysVec.Normalize(ToSys(c.Normal));
                if (float.IsNaN(into.X)) continue;
                var vChar = ToSys(c.Move) * invDt;
                VortexAPI.PhysicsGetLinearVelocity(b.Id, _f3a);
                var vBody = new SysVec(_f3a[0], _f3a[1], _f3a[2]);
                float rel = SysVec.Dot(vChar - vBody, into);
                if (rel <= 0.01f) continue;
                float mass = 0f;
                try { mass = VortexAPI.PhysicsGetMass(b.Id); } catch { }
                float j = Math.Min(60f, 80f * rel * 0.3f);
                if (mass > 0f) j = Math.Min(j, mass * rel);
                // A jointed body (a door) may not be able to give way — pressed against its hinge limit it would
                // soak up the full 60 N·s every frame (~3600 N) and snap a breakable joint just by walking into it.
                // Cap the shove at what a person pushes with.
                // (Pushes are applied once per frame that steps, i.e. at most every FixedStep.)
                if (_joints.Count > 0 && IsJointed(b.Id)) j = Math.Min(j, CharacterPushForceOnJoints * Math.Max(frameDt, FixedStep));
                if (!_pushes.TryGetValue(b.Id, out var prev) || j > prev.j) _pushes[b.Id] = (into, j);
            }
            contacts.Clear();
            foreach (var kv in _pushes)
            {
                var b = _byId[kv.Key];
                Wake(b);
                Fill(_f3a, kv.Value.dir * kv.Value.j);
                VortexAPI.PhysicsAddImpulse(b.Id, _f3a);
            }
            _pushes.Clear();
        }

        /// <summary>Drive kinematic bodies to their entity's current world pose (velocity-based, so they push
        /// dynamic bodies). A kinematic entity that moved is also re-baked in the static collision world so the
        /// character controller sees the platform/door where it is now.</summary>
        private static void PushKinematics()
        {
            for (int i = 0; i < _bodies.Count; i++)
            {
                var b = _bodies[i];
                if (b.Motion != MotionKinematic) continue;
                if (!TryEntityPose(b.Entity, b.CenterOffset, out var pos, out var rot)) continue;
                bool moved = (pos - b.LastPos).LengthSquared() > 1e-10f || Math.Abs(SysQuat.Dot(rot, b.LastRot)) < 0.9999999f;
                Fill(_f3a, pos); Fill(_f4, rot);
                VortexAPI.PhysicsMoveKinematic(b.Id, _f3a, _f4, FixedStep);   // same pose = velocity 0 (stops the body)
                if (moved)
                {
                    b.LastPos = pos; b.LastRot = rot;
                    if (!b.Secondary && !b.IsTrigger)
                    {
                        try { CollisionService.RemoveEntityShapes(b.Entity); CollisionService.AddEntityShapes(b.Entity); } catch { }   // whole subtree: children moved too
                    }
                }
            }
        }

        /// <summary>Copy every awake dynamic body's pose to its entity (world → local against the parent chain).</summary>
        private static void ReadbackDynamics()
        {
            bool any = false;
            for (int i = 0; i < _bodies.Count; i++)
            {
                var b = _bodies[i];
                if (b.Motion != MotionDynamic || b.Secondary || b.IsTrigger) continue;
                if (VortexAPI.PhysicsIsActive(b.Id) == 0) continue;   // asleep: pose unchanged
                if (VortexAPI.PhysicsGetBodyTransform(b.Id, _f3a, _f4) == 0) continue;
                var pos = new SysVec(_f3a[0], _f3a[1], _f3a[2]);
                var rot = new SysQuat(_f4[0], _f4[1], _f4[2], _f4[3]);
                if (float.IsNaN(pos.X) || float.IsNaN(rot.W)) continue;
                if ((pos - b.LastPos).LengthSquared() < 1e-12f && Math.Abs(SysQuat.Dot(rot, b.LastRot)) > 0.99999999f) continue;
                b.LastPos = pos; b.LastRot = rot;
                WritePose(b, pos, rot);
                any = true;
            }
            if (any) SceneRenderService.RuntimeDirty = true;
        }

        private static void WritePose(Body b, SysVec bodyPos, SysQuat rot)
        {
            var e = b.Entity;
            var t = e?.Transform;
            if (t == null) return;
            var entityPos = bodyPos - SysVec.Transform(b.CenterOffset, rot);
            var world = SysMat.CreateScale(b.Scale) * SysMat.CreateFromQuaternion(rot) * SysMat.CreateTranslation(entityPos);
            var local = world;
            var parentWorld = Animation.BoneSocketService.EntityWorld(e.Parent);
            if (SysMat.Invert(parentWorld, out var inv)) local = world * inv;
            var euler = Animation.BoneSocketService.ToEulerZXY(Animation.BoneSocketService.NormalizeBasis(local));
            t.LocalPosition = new Vector3(local.Translation.X, local.Translation.Y, local.Translation.Z);
            t.LocalRotation = new Vector3(euler.X, euler.Y, euler.Z);
        }

        /// <summary>Publish the live pose of every dynamic (non-debris, non-trigger) body to the character collision world.</summary>
        private static void PublishDynamicShapes()
        {
            if (!CollisionService.IsBuilt) return;
            _publish.Clear();
            for (int i = 0; i < _bodies.Count; i++)
            {
                var b = _bodies[i];
                if (b.Motion != MotionDynamic || b.Secondary || b.IsTrigger || b.Layer == LayerDebris) continue;
                var rot = b.LastRot;
                var center = b.LastPos + SysVec.Transform(b.BoundsCenter, rot);
                _publish.Add(new CollisionService.DynamicBody
                {
                    BodyId = b.Id, Owner = b.Entity,
                    Center = ToEcs(center), HalfExtents = ToEcs(b.BoundsHalf),
                    AxisX = ToEcs(SysVec.Transform(SysVec.UnitX, rot)),
                    AxisY = ToEcs(SysVec.Transform(SysVec.UnitY, rot)),
                    AxisZ = ToEcs(SysVec.Transform(SysVec.UnitZ, rot)),
                    SphereRadius = b.SphereRadius
                });
            }
            CollisionService.SetDynamicBodies(_publish);
        }

        private static void DispatchContacts()
        {
            _stayThisFrame.Clear();
            int n;
            do
            {
                n = VortexAPI.PhysicsGetContacts(_contactBuf, _contactBuf.Length);
                if (n <= 0) break;
                if (ContactHandler != null)
                {
                    for (int i = 0; i < n && i < _contactBuf.Length; i++)
                    {
                        var c = _contactBuf[i];
                        var kind = (PhysicsContactKind)c.Kind;
                        bool trigger = c.IsTrigger != 0;
                        if (!trigger && kind != PhysicsContactKind.Added) continue;   // solids: only the first touch has a script callback
                        var a = EntityOfBody(c.BodyA) ?? FindByEntityId(c.EntityA);
                        var b = EntityOfBody(c.BodyB) ?? FindByEntityId(c.EntityB);
                        if (a == null || b == null || ReferenceEquals(a, b)) continue;
                        if (kind == PhysicsContactKind.Persisted)
                        {
                            // Jolt reports "still touching" every step — at most one Stay per pair per frame.
                            var key = c.BodyA < c.BodyB ? (c.BodyA, c.BodyB) : (c.BodyB, c.BodyA);
                            if (!_stayThisFrame.Add(key)) continue;
                        }
                        try
                        {
                            ContactHandler(new PhysicsContactEvent
                            {
                                EntityA = a, EntityB = b, Kind = kind, IsTrigger = trigger,
                                Point = new Vector3(c.PointX, c.PointY, c.PointZ),
                                Normal = new Vector3(c.NormalX, c.NormalY, c.NormalZ),
                                Impulse = c.Impulse
                            });
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Physics] contact handler: " + ex.Message); }
                    }
                }
            } while (n >= _contactBuf.Length);
        }

        // ------------------------------------------------------------------------------------------ small helpers

        private static bool TryDynamic(GameEntity e, out Body b)
        {
            b = null;
            if (!IsBuilt || e == null || !_byEntity.TryGetValue(e, out b)) return false;
            return b.Motion == MotionDynamic;
        }

        private static bool TryMoving(GameEntity e, out Body b)
        {
            b = null;
            if (!IsBuilt || e == null || !_byEntity.TryGetValue(e, out b)) return false;
            return b.Motion == MotionDynamic || b.Motion == MotionKinematic;
        }

        private static void Wake(Body b) { try { VortexAPI.PhysicsSetActive(b.Id, 1); } catch { } }

        private static bool TryEntityPose(GameEntity e, SysVec centerOffset, out SysVec pos, out SysQuat rot)
        {
            pos = SysVec.Zero; rot = SysQuat.Identity;
            if (e == null || e.Transform == null) return false;
            var world = Animation.BoneSocketService.EntityWorld(e);
            rot = RotationOf(world);
            pos = world.Translation + SysVec.Transform(centerOffset, rot);
            return !float.IsNaN(pos.X) && !float.IsNaN(rot.W);
        }

        private static SysVec ScaleOf(SysMat m)
            => new SysVec(new SysVec(m.M11, m.M12, m.M13).Length(), new SysVec(m.M21, m.M22, m.M23).Length(), new SysVec(m.M31, m.M32, m.M33).Length());

        private static SysQuat RotationOf(SysMat m)
        {
            var q = SysQuat.CreateFromRotationMatrix(Animation.BoneSocketService.NormalizeBasis(m));
            return SysQuat.Normalize(q);
        }

        private static GameEntity FindByEntityId(ulong entityId)
        {
            long id = unchecked((long)entityId);
            for (int i = 0; i < _bodies.Count; i++)
                if (_bodies[i].Entity != null && _bodies[i].Entity.EntityId == id) return _bodies[i].Entity;
            return null;
        }

        private static void Fill(float[] a, SysVec v) { a[0] = v.X; a[1] = v.Y; a[2] = v.Z; }
        private static void Fill(float[] a, Vector3 v) { a[0] = v.X; a[1] = v.Y; a[2] = v.Z; }
        private static void Fill(float[] a, SysQuat q) { a[0] = q.X; a[1] = q.Y; a[2] = q.Z; a[3] = q.W; }
        private static SysVec ToSys(Vector3 v) => new SysVec(v.X, v.Y, v.Z);
        private static Vector3 ToEcs(SysVec v) => new Vector3(v.X, v.Y, v.Z);
        private static SysVec Mul(SysVec a, SysVec b) => new SysVec(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
        private static SysVec Abs(SysVec v) => new SysVec(Math.Abs(v.X), Math.Abs(v.Y), Math.Abs(v.Z));
        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        private static void Log(string msg)
        {
            System.Diagnostics.Debug.WriteLine(msg);
            try { ConsoleService.Instance.Log(msg); } catch { }
        }

        private static void Warn(GameEntity e, string msg)
        {
            var text = "[Physics] '" + (e?.Name ?? "?") + "': " + msg;
            System.Diagnostics.Debug.WriteLine(text);
            try { ConsoleService.Instance.LogWarning(text); } catch { }
        }
    }
}
