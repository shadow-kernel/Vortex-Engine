using System;
using System.Runtime.InteropServices;
using Editor.Utilities;

namespace Editor.DllWrapper
{
    /// <summary>
    /// One contact event reported by the native physics world (Jolt) — mirrors the C struct <c>PhysicsContact</c>
    /// of the native ABI contract (issue #100/#102). <see cref="NormalX"/>.. point from body A to body B.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PhysicsContact
    {
        /// <summary>Engine entity ids (the value passed as <c>entityId</c> when the body was created).</summary>
        public ulong EntityA, EntityB;
        /// <summary>Native body handles (0 = invalid).</summary>
        public uint BodyA, BodyB;
        /// <summary>World-space contact point.</summary>
        public float PointX, PointY, PointZ;
        /// <summary>Contact normal, pointing from A to B.</summary>
        public float NormalX, NormalY, NormalZ;
        /// <summary>Applied impulse (N·s) — 0 for sensor contacts.</summary>
        public float Impulse;
        /// <summary>0 = added (first touch), 1 = persisted (still touching), 2 = removed (separated).</summary>
        public int Kind;
        /// <summary>1 when either body is a trigger (Jolt sensor): overlap only, no collision response.</summary>
        public int IsTrigger;
    }

    /// <summary>
    /// Physics v2 (Jolt) native entry points — P/Invoke declarations for every function of the native contract
    /// (<c>VortexAPI/Api/PhysicsApi.cpp</c>). Handles are <c>uint</c> (0 = invalid), entity ids <c>ulong</c>, all
    /// vectors are float triples, quaternions <c>{x, y, z, w}</c> (System.Numerics convention). Never call these
    /// directly from gameplay code — <see cref="Editor.Core.Services.Physics.PhysicsService"/> wraps them and
    /// degrades gracefully when the engine was built without Jolt (see <see cref="PhysicsNative"/>).
    /// </summary>
    public static partial class VortexAPI
    {
        #region World

        /// <summary>1 = Jolt world ready, 0 = stub build (no physics). Throws EntryPointNotFound on an old library.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsInit();

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsShutdown();

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsAvailable();

        /// <summary>Destroys every body / character (scene switch, stop play).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsClear();

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsSetGravity(float x, float y, float z);

        /// <summary>ONE fixed step of <paramref name="dt"/> seconds (the caller accumulates); collisionSteps &gt;= 1.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsStep(float dt, int collisionSteps);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsGetBodyCount();

        #endregion

        #region Bodies

        /// <summary>Primitive body. shapeType: 0 box (dims = half extents), 1 sphere (dims[0] radius), 2 capsule
        /// (dims[0] radius, dims[1] half height of the cylinder part), 3 cylinder (radius, half height). motion:
        /// 0 static, 1 kinematic, 2 dynamic. mass &lt;= 0 = density-based. lockFlags bits 0-2 lock position XYZ,
        /// 3-5 lock rotation XYZ.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern uint PhysicsCreateBody(ulong entityId, int shapeType, float[] dims, float[] pos, float[] quat,
            int motion, float mass, float friction, float restitution, float linearDamping, float angularDamping,
            int isTrigger, int layer, uint lockFlags, float gravityFactor);

        /// <summary>Static triangle mesh (convex == 0, motion must be 0) or convex hull (convex == 1, may be dynamic).
        /// verts = xyz triples in LOCAL space; scale is applied inside.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern uint PhysicsCreateMeshBody(ulong entityId, float[] verts, int vertCount, uint[] indices, int indexCount,
            float[] pos, float[] quat, float[] scale, int convex, int motion, float mass, float friction, float restitution,
            int isTrigger, int layer);

        /// <summary>Compound of boxes/spheres/capsules on ONE body: <paramref name="children"/> holds 11 floats per
        /// child (shapeType, dims xyz, localPos xyz, localQuat xyzw).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern uint PhysicsCreateCompoundBody(ulong entityId, float[] children, int count, float[] pos, float[] quat,
            int motion, float mass, float friction, float restitution, float linearDamping, float angularDamping,
            int isTrigger, int layer, uint lockFlags, float gravityFactor);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsDestroyBody(uint body);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsBodyValid(uint body);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern ulong PhysicsGetBodyEntity(uint body);

        /// <summary>Teleport (activates the body).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsSetBodyTransform(uint body, float[] pos, float[] quat);

        /// <summary>Kinematic drive: sets the velocity that reaches the pose in <paramref name="dt"/> seconds, so the
        /// body pushes dynamic bodies on its way.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsMoveKinematic(uint body, float[] pos, float[] quat, float dt);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsGetBodyTransform(uint body, [In, Out] float[] outPos, [In, Out] float[] outQuat);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsSetLinearVelocity(uint body, float[] v);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsGetLinearVelocity(uint body, [In, Out] float[] outV);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsSetAngularVelocity(uint body, float[] v);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsGetAngularVelocity(uint body, [In, Out] float[] outV);

        /// <summary>Force in N at the centre of mass (accumulated until the next step).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsAddForce(uint body, float[] f);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsAddForceAtPoint(uint body, float[] f, float[] worldPoint);

        /// <summary>Impulse in N·s (instant velocity change).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsAddImpulse(uint body, float[] impulse);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsAddImpulseAtPoint(uint body, float[] impulse, float[] worldPoint);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsAddTorque(uint body, float[] torque);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsSetMotionType(uint body, int motion);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsSetGravityFactor(uint body, float factor);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsSetFriction(uint body, float friction);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsSetRestitution(uint body, float restitution);

        /// <summary>#107: how the body combines friction / restitution with what it touches — 0 average, 1 minimum,
        /// 2 multiply, 3 maximum (the PhysicsMaterial's combine fields).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsSetCombineModes(uint body, int frictionMode, int restitutionMode);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsSetDamping(uint body, float linear, float angular);

        /// <summary>1 = wake up, 0 = put to sleep.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsSetActive(uint body, int active);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsIsActive(uint body);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern float PhysicsGetMass(uint body);

        #endregion

        #region Queries

        /// <summary>Closest hit along a unit direction. layerMask bit i = object layer i included (0 static,
        /// 1 dynamic, 2 character, 3 trigger, 4 debris). Returns 1 on hit.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsRaycast(float[] origin, float[] dir, float maxDist, uint layerMask,
            [In, Out] float[] outPoint, [In, Out] float[] outNormal, out float outDist, out ulong outEntity, out uint outBody);

        /// <summary>Every body overlapping a sphere (up to maxCount); returns the number written.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsOverlapSphere(float[] center, float radius, uint layerMask,
            [In, Out] ulong[] outEntities, [In, Out] uint[] outBodies, int maxCount);

        /// <summary>Contact events since the previous drain (sensor AND solid contacts). Returns the count written.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsGetContacts([In, Out] PhysicsContact[] buffer, int maxCount);

        #endregion

        #region Character (issue #105 — API only in this round)

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern uint PhysicsCharacterCreate(float radius, float height, float[] pos, float maxSlopeDeg, float stepHeight, float mass);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsCharacterDestroy(uint character);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsCharacterSetPosition(uint character, float[] pos);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsCharacterMove(uint character, float[] desiredVelocity, float dt,
            [In, Out] float[] outPos, [In, Out] float[] outVelocity, out int outGrounded, [In, Out] float[] outGroundNormal);

        #endregion

        #region Constraints / joints (issue #103)

        // Joint handles are uint (0 = invalid). bodyA = the jointed body, bodyB = the connected body or 0 = the world.
        // Points / axes are WORLD space at creation; the pose at creation is the rest pose (hinge angle 0, slider
        // position 0). Angles in degrees, breakForce in N (<= 0 = unbreakable). The two bodies of an enabled joint
        // never collide with each other; destroying a body destroys its joints.

        /// <summary>Hinge (doors): rotation about <paramref name="axis"/> through <paramref name="pivot"/>;
        /// <paramref name="normal"/> = reference direction of angle 0 (null = any). Limits: min in [-180, 0], max in
        /// [0, 180]. motorMaxTorque &gt; 0 starts a velocity motor (motorTargetVel deg/s; 0 = friction).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern uint PhysicsCreateHinge(uint bodyA, uint bodyB, float[] pivot, float[] axis, float[] normal,
            float minDeg, float maxDeg, int useLimits, float motorTargetVel, float motorMaxTorque, float breakForce);

        /// <summary>Ball joint at <paramref name="point"/>; with useLimits a swing cone (half angle) around
        /// <paramref name="twistAxis"/> (null = towards bodyA's centre of mass) plus a twist range.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern uint PhysicsCreateBallJoint(uint bodyA, uint bodyB, float[] point, float[] twistAxis,
            float swingLimitDeg, float twistMinDeg, float twistMaxDeg, int useLimits, float breakForce);

        /// <summary>Slider: translation along <paramref name="axis"/> only. Limits (m) relative to the creation pose
        /// (min &lt;= 0 &lt;= max). motorMode 0 off / 1 velocity (m/s) / 2 position (m, spring frequency + damping);
        /// motorMaxForce &lt;= 0 = unlimited.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern uint PhysicsCreateSlider(uint bodyA, uint bodyB, float[] point, float[] axis,
            float minPos, float maxPos, int useLimits, int motorMode, float motorTarget, float motorMaxForce,
            float springFrequency, float springDamping, float breakForce);

        /// <summary>Weld: keeps the current relative pose; point = anchor (null = automatic).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern uint PhysicsCreateFixed(uint bodyA, uint bodyB, float[] point, float breakForce);

        /// <summary>Rope / rod: |pointA - pointB| stays in [minDistance, maxDistance] (negative = the distance at
        /// creation); springFrequency &gt; 0 = soft limits (bungee).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern uint PhysicsCreateDistance(uint bodyA, uint bodyB, float[] pointA, float[] pointB,
            float minDistance, float maxDistance, float springFrequency, float springDamping, float breakForce);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsDestroyConstraint(uint joint);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsConstraintValid(uint joint);

        /// <summary>1 = enable (also repairs a broken joint), 0 = disable.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsSetConstraintEnabled(uint joint, int enabled);

        /// <summary>0 when disabled or broken.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsGetConstraintEnabled(uint joint);

        /// <summary>mode 0 off / 1 velocity (target deg/s) / 2 position (target deg); maxTorque &lt;= 0 = unlimited;
        /// frequency &lt;= 0 = 2 Hz, damping &lt; 0 = 1 (the position spring).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsSetHingeMotor(uint joint, int mode, float target, float maxTorque, float frequency, float damping);

        /// <summary>mode 0 off / 1 velocity (target m/s) / 2 position (target m); maxForce &lt;= 0 = unlimited.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern void PhysicsSetSliderMotor(uint joint, int mode, float target, float maxForce, float frequency, float damping);

        /// <summary>Degrees, 0 at creation (bodyA relative to bodyB about the axis, right hand).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern float PhysicsGetHingeAngle(uint joint);

        /// <summary>Metres along the axis, 0 at creation.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern float PhysicsGetSliderPosition(uint joint);

        /// <summary>Linear force (N) the joint applied in the last step (hinge / ball / weld pivot, slider
        /// perpendicular + limit, rope tension).</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern float PhysicsGetConstraintForce(uint joint);

        /// <summary>Joints that broke since the last call (+ the breaking force in N); drains what it writes and
        /// returns the count.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsGetBrokenConstraints([In, Out] uint[] outJoints, [In, Out] float[] outForces, int maxCount);

        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsGetConstraintCount();

        #endregion

        #region Debug draw (issue #106)

        /// <summary>World-space wireframe segments of every joint gizmo and body (6 floats per segment:
        /// x0 y0 z0 x1 y1 z1). Fills up to maxFloats and returns the number of floats written.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsGetDebugLines([In, Out] float[] buffer, int maxFloats);

        /// <summary>#106: the segments plus one kind byte per segment — 0 static, 1 kinematic, 2 dynamic, 3 sleeping,
        /// 4 joint, 5 character.</summary>
        [DllImport(_dllName, CallingConvention = _cc)]
        public static extern int PhysicsGetDebugLinesEx([In, Out] float[] buffer, int maxFloats, [In, Out] byte[] kinds, int maxKinds);

        private static long _physicsDebugMaterial = ID.INVALID_ID;   // cyan unlit net: the physics world, not the authored collider
        // #106: one unlit material per debug kind — static grey, kinematic blue, dynamic cyan, sleeping dim, joints
        // yellow, characters green, contacts orange
        private static readonly long[] _physicsDebugKindMaterials = new long[7];
        private static readonly float[][] _physicsDebugKindColors =
        {
            new[] { 0.55f, 0.55f, 0.6f }, new[] { 0.3f, 0.6f, 1.0f }, new[] { 0.25f, 0.85f, 1.0f }, new[] { 0.15f, 0.4f, 0.5f },
            new[] { 1.0f, 0.85f, 0.2f }, new[] { 0.3f, 1.0f, 0.4f }, new[] { 1.0f, 0.5f, 0.1f },
        };

        /// <summary>Draw debug segments colour-coded by kind (#106): <paramref name="kinds"/> holds one byte per segment
        /// (0 static, 1 kinematic, 2 dynamic, 3 sleeping, 4 joint, 5 character, 6 contact); a kind whose bit is not in
        /// <paramref name="kindMask"/> is skipped.</summary>
        public static void RenderPhysicsDebugLines(float[] segments, int floatCount, byte[] kinds, int kindMask, int maxSegments = 12000)
        {
            if (segments == null || floatCount < 6) return;
            if (!_gizmosInitialized) InitializeGizmos();
            if (_gizmoCube == ID.INVALID_ID) return;
            int segs = Math.Min(floatCount / 6, maxSegments);
            for (int i = 0; i < segs; i++)
            {
                int kind = kinds != null && i < kinds.Length ? Math.Min((int)kinds[i], _physicsDebugKindMaterials.Length - 1) : 2;
                if ((kindMask & (1 << kind)) == 0) continue;
                if (_physicsDebugKindMaterials[kind] <= 0)
                {
                    var c = _physicsDebugKindColors[kind];
                    _physicsDebugKindMaterials[kind] = MakeUnlitMaterial(c[0], c[1], c[2]);
                    if (_physicsDebugKindMaterials[kind] <= 0) continue;
                }
                SubmitDebugSegment(segments, i * 6, _physicsDebugKindMaterials[kind]);
            }
        }

        /// <summary>Draw physics debug segments (as returned by <see cref="PhysicsGetDebugLines"/>) as thin
        /// always-on-top wire boxes through the gizmo pass — the same technique the script Debug.DrawLine uses, so
        /// it works in the editor viewport, the play window and the standalone player. Cyan, to tell the live Jolt
        /// shapes apart from the green authored-collider nets. <paramref name="floatCount"/> = floats to consume;
        /// the segment count is capped so a huge mesh collider can't stall a frame.</summary>
        public static void RenderPhysicsDebugLines(float[] segments, int floatCount, int maxSegments = 12000)
        {
            if (segments == null || floatCount < 6) return;
            if (!_gizmosInitialized) InitializeGizmos();
            if (_gizmoCube == ID.INVALID_ID) return;
            if (_physicsDebugMaterial == ID.INVALID_ID) _physicsDebugMaterial = MakeUnlitMaterial(0.25f, 0.85f, 1.0f);
            if (_physicsDebugMaterial == ID.INVALID_ID) return;

            int segs = Math.Min(floatCount / 6, maxSegments);
            for (int i = 0; i < segs; i++) SubmitDebugSegment(segments, i * 6, _physicsDebugMaterial);
        }

        private static readonly float[] _debugSegmentMatrix = new float[16];

        /// <summary>One thin always-on-top wire box along the segment at <paramref name="o"/> (6 floats).</summary>
        private static void SubmitDebugSegment(float[] segments, int o, long material)
        {
            var m = _debugSegmentMatrix;
            const float T = 0.015f;   // line thickness (m)
            {
                float ax = segments[o], ay = segments[o + 1], az = segments[o + 2];
                float bx = segments[o + 3], by = segments[o + 4], bz = segments[o + 5];
                float dx = bx - ax, dy = by - ay, dz = bz - az;
                float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len < 1e-5f) return;
                float ix = dx / len, iy = dy / len, iz = dz / len;
                // Orthonormal frame around the segment direction (row-vector basis, like the script debug lines).
                float ux = 0f, uy = 1f, uz = 0f;
                if (Math.Abs(iy) > 0.99f) { uy = 0f; uz = 1f; }
                float rx = uy * iz - uz * iy, ry = uz * ix - ux * iz, rz = ux * iy - uy * ix;
                float rl = (float)Math.Sqrt(rx * rx + ry * ry + rz * rz);
                if (rl < 1e-6f) return;
                rx /= rl; ry /= rl; rz /= rl;
                float upx = iy * rz - iz * ry, upy = iz * rx - ix * rz, upz = ix * ry - iy * rx;
                m[0] = rx * T; m[1] = ry * T; m[2] = rz * T; m[3] = 0f;
                m[4] = upx * T; m[5] = upy * T; m[6] = upz * T; m[7] = 0f;
                m[8] = ix * len; m[9] = iy * len; m[10] = iz * len; m[11] = 0f;
                m[12] = (ax + bx) * 0.5f; m[13] = (ay + by) * 0.5f; m[14] = (az + bz) * 0.5f; m[15] = 1f;
                SubmitGizmoWireForRendering(_gizmoCube, material, m);
            }
        }

        #endregion
    }

    /// <summary>
    /// Availability gate for the native physics world. <see cref="Available"/> calls <c>PhysicsInit()</c> exactly
    /// once and remembers the answer: a stub build (Windows .vcxproj without Jolt) returns 0, an engine library that
    /// predates the physics API throws <see cref="EntryPointNotFoundException"/>, a missing library throws
    /// <see cref="DllNotFoundException"/> — all three read as "not available" and the managed side keeps the legacy
    /// behaviour (static collision world + character controller only). <see cref="Reason"/> says why.
    /// </summary>
    public static class PhysicsNative
    {
        private static int _state;   // 0 = not probed, 1 = available, -1 = unavailable

        /// <summary>True when the Jolt world is up and every physics entry point can be called.</summary>
        public static bool Available
        {
            get
            {
                if (_state == 0) Probe();
                return _state > 0;
            }
        }

        /// <summary>Why physics is unavailable ("" when it is) — for the one-time console notice.</summary>
        public static string Reason { get; private set; } = "";

        /// <summary>True once <see cref="Available"/> has been evaluated (the answer is cached for the process).</summary>
        public static bool Probed => _state != 0;

        private static void Probe()
        {
            try
            {
                int r = VortexAPI.PhysicsInit();
                _state = r != 0 ? 1 : -1;
                Reason = r != 0 ? "" : "engine built without Jolt (stub PhysicsInit returned 0)";
            }
            catch (EntryPointNotFoundException)
            {
                _state = -1;
                Reason = "engine library has no physics entry points (rebuild the native engine)";
            }
            catch (DllNotFoundException)
            {
                _state = -1;
                Reason = "engine library not found";
            }
            catch (Exception ex)
            {
                _state = -1;
                Reason = "PhysicsInit failed: " + ex.Message;
            }
        }

        /// <summary>Forget the cached probe (tests / after swapping the native library at runtime).</summary>
        public static void ResetProbe() { _state = 0; Reason = ""; }

        /// <summary>Tear the physics world down (process exit, before the engine runtime shuts down). No-op when it
        /// was never initialised; a later <see cref="Available"/> probes (and initialises) again.</summary>
        public static void Shutdown()
        {
            if (_state <= 0) return;
            try { VortexAPI.PhysicsShutdown(); } catch { }
            _state = 0; Reason = "";
        }
    }
}
