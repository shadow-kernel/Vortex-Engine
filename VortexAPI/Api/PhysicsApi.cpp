#include "../ApiCommon.h"

#include <cstddef>
#include <type_traits>

// ============================================================================================
// Physics v2 interop (Jolt) — the extern "C" surface over Engine/Physics/PhysicsWorld.{h,cpp}.
//
// The signatures below ARE the native ABI contract shared with the managed side
// (Editor/DllWrapper/VortexAPI.Physics.cs + PhysicsService.cs); see Engine/Physics/README.md.
// Every function is a thin pass-through: no logic lives here, so the stub build (no Jolt in the
// binary — the Visual Studio projects today) exports exactly the same symbols and the managed side
// can probe PhysicsInit() == 0 and keep its legacy behaviour.
//
// Conventions: metres/kg/s, quaternions {x, y, z, w}, body handles u32 (0 = invalid), entity ids
// u64, motion 0 static / 1 kinematic / 2 dynamic, shape 0 box / 1 sphere / 2 capsule / 3 cylinder,
// layers 0 STATIC / 1 DYNAMIC / 2 CHARACTER / 3 TRIGGER / 4 DEBRIS, lock flags bit0..2 position
// xyz + bit3..5 rotation xyz. Joints (#103): handles u32 (0 = invalid), angles in degrees, motor
// mode 0 off / 1 velocity / 2 position.
// ============================================================================================

namespace phys = vortex::physics;

// Contact events since the previous PhysicsGetContacts drain (sensor and solid contacts).
typedef struct PhysicsContact {
	uint64_t entityA, entityB; uint32_t bodyA, bodyB;
	float point[3]; float normal[3]; float impulse;   // normal points from A to B
	int32_t kind;      // 0 = added, 1 = persisted, 2 = removed
	int32_t isTrigger; // 1 when either body is a sensor
} PhysicsContact;

// PhysicsGetContacts hands the engine's queue out through a reinterpret_cast, so the two layouts must agree.
static_assert(std::is_standard_layout_v<PhysicsContact> && std::is_standard_layout_v<phys::contact_event>);
static_assert(sizeof(PhysicsContact) == sizeof(phys::contact_event), "PhysicsContact / contact_event layout drift");
static_assert(offsetof(PhysicsContact, entityB) == offsetof(phys::contact_event, entity_b));
static_assert(offsetof(PhysicsContact, bodyA) == offsetof(phys::contact_event, body_a));
static_assert(offsetof(PhysicsContact, bodyB) == offsetof(phys::contact_event, body_b));
static_assert(offsetof(PhysicsContact, point) == offsetof(phys::contact_event, point));
static_assert(offsetof(PhysicsContact, normal) == offsetof(phys::contact_event, normal));
static_assert(offsetof(PhysicsContact, impulse) == offsetof(phys::contact_event, impulse));
static_assert(offsetof(PhysicsContact, kind) == offsetof(phys::contact_event, kind));
static_assert(offsetof(PhysicsContact, isTrigger) == offsetof(phys::contact_event, is_trigger));

// ---- World ----

EDITOR_INTERFACE int32_t PhysicsInit(void)                 { return phys::init() ? 1 : 0; }   // 1 = Jolt world ready, 0 = stub / no physics
EDITOR_INTERFACE void    PhysicsShutdown(void)             { phys::shutdown(); }
EDITOR_INTERFACE int32_t PhysicsAvailable(void)            { return phys::available() ? 1 : 0; }
EDITOR_INTERFACE void    PhysicsClear(void)                { phys::clear(); }
EDITOR_INTERFACE void    PhysicsSetGravity(float x, float y, float z) { phys::set_gravity(x, y, z); }
EDITOR_INTERFACE void    PhysicsStep(float dt, int32_t collisionSteps) { phys::step(dt, collisionSteps); }   // ONE fixed step (caller accumulates)
EDITOR_INTERFACE int32_t PhysicsGetBodyCount(void)         { return phys::body_count(); }

// ---- Bodies ----

EDITOR_INTERFACE uint32_t PhysicsCreateBody(uint64_t entityId, int32_t shapeType, const float* dims /*3*/,
	const float* pos /*3*/, const float* quat /*4 xyzw*/,
	int32_t motion, float mass, float friction, float restitution,
	float linearDamping, float angularDamping, int32_t isTrigger, int32_t layer,
	uint32_t lockFlags, float gravityFactor)
{
	return phys::create_body(entityId, shapeType, dims, pos, quat, motion, mass, friction, restitution,
		linearDamping, angularDamping, isTrigger != 0, layer, lockFlags, gravityFactor);
}

// Static triangle mesh (convex == 0) or convex hull (convex == 1). verts = xyz triples in LOCAL space, scale
// applied inside. Triangle meshes must be static (motion 0); convex hulls may be dynamic.
EDITOR_INTERFACE uint32_t PhysicsCreateMeshBody(uint64_t entityId, const float* verts, int32_t vertCount,
	const uint32_t* indices, int32_t indexCount, const float* pos, const float* quat,
	const float* scale /*3*/, int32_t convex, int32_t motion, float mass,
	float friction, float restitution, int32_t isTrigger, int32_t layer)
{
	return phys::create_mesh_body(entityId, verts, vertCount, indices, indexCount, pos, quat, scale,
		convex != 0, motion, mass, friction, restitution, isTrigger != 0, layer);
}

// Compound of boxes/spheres/capsules on ONE body (issue #107): count children, each child = shapeType + dims(3) +
// localPos(3) + localQuat(4) = 11 floats per child in `children` (shapeType stored as float).
EDITOR_INTERFACE uint32_t PhysicsCreateCompoundBody(uint64_t entityId, const float* children, int32_t count,
	const float* pos, const float* quat, int32_t motion, float mass,
	float friction, float restitution, float linearDamping, float angularDamping,
	int32_t isTrigger, int32_t layer, uint32_t lockFlags, float gravityFactor)
{
	return phys::create_compound_body(entityId, children, count, pos, quat, motion, mass, friction, restitution,
		linearDamping, angularDamping, isTrigger != 0, layer, lockFlags, gravityFactor);
}

EDITOR_INTERFACE void     PhysicsDestroyBody(uint32_t body)   { phys::destroy_body(body); }
EDITOR_INTERFACE int32_t  PhysicsBodyValid(uint32_t body)     { return phys::body_valid(body) ? 1 : 0; }
EDITOR_INTERFACE uint64_t PhysicsGetBodyEntity(uint32_t body) { return phys::body_entity(body); }

EDITOR_INTERFACE void    PhysicsSetBodyTransform(uint32_t body, const float* pos, const float* quat) { phys::set_body_transform(body, pos, quat); }   // teleport (activates)
EDITOR_INTERFACE void    PhysicsMoveKinematic(uint32_t body, const float* pos, const float* quat, float dt) { phys::move_kinematic(body, pos, quat, dt); } // kinematic drive with velocity
EDITOR_INTERFACE int32_t PhysicsGetBodyTransform(uint32_t body, float* outPos /*3*/, float* outQuat /*4*/) { return phys::get_body_transform(body, outPos, outQuat) ? 1 : 0; }
EDITOR_INTERFACE void    PhysicsSetLinearVelocity(uint32_t body, const float* v)   { phys::set_linear_velocity(body, v); }
EDITOR_INTERFACE void    PhysicsGetLinearVelocity(uint32_t body, float* outV)      { phys::get_linear_velocity(body, outV); }
EDITOR_INTERFACE void    PhysicsSetAngularVelocity(uint32_t body, const float* v)  { phys::set_angular_velocity(body, v); }
EDITOR_INTERFACE void    PhysicsGetAngularVelocity(uint32_t body, float* outV)     { phys::get_angular_velocity(body, outV); }
EDITOR_INTERFACE void    PhysicsAddForce(uint32_t body, const float* f)            { phys::add_force(body, f); }                       // N, at centre of mass
EDITOR_INTERFACE void    PhysicsAddForceAtPoint(uint32_t body, const float* f, const float* worldPoint) { phys::add_force_at_point(body, f, worldPoint); }
EDITOR_INTERFACE void    PhysicsAddImpulse(uint32_t body, const float* impulse)    { phys::add_impulse(body, impulse); }               // N·s
EDITOR_INTERFACE void    PhysicsAddImpulseAtPoint(uint32_t body, const float* impulse, const float* worldPoint) { phys::add_impulse_at_point(body, impulse, worldPoint); }
EDITOR_INTERFACE void    PhysicsAddTorque(uint32_t body, const float* torque)      { phys::add_torque(body, torque); }
EDITOR_INTERFACE void    PhysicsSetMotionType(uint32_t body, int32_t motion)       { phys::set_motion_type(body, motion); }
EDITOR_INTERFACE void    PhysicsSetGravityFactor(uint32_t body, float factor)      { phys::set_gravity_factor(body, factor); }
EDITOR_INTERFACE void    PhysicsSetFriction(uint32_t body, float friction)         { phys::set_friction(body, friction); }
EDITOR_INTERFACE void    PhysicsSetRestitution(uint32_t body, float restitution)   { phys::set_restitution(body, restitution); }
EDITOR_INTERFACE void    PhysicsSetDamping(uint32_t body, float linear, float angular) { phys::set_damping(body, linear, angular); }
EDITOR_INTERFACE void    PhysicsSetActive(uint32_t body, int32_t active)           { phys::set_active(body, active != 0); }            // wake / sleep
EDITOR_INTERFACE int32_t PhysicsIsActive(uint32_t body)                            { return phys::is_active(body) ? 1 : 0; }
EDITOR_INTERFACE float   PhysicsGetMass(uint32_t body)                             { return phys::get_mass(body); }

// ---- Queries (layerMask bit i = object layer i included, default ~0) ----

EDITOR_INTERFACE int32_t PhysicsRaycast(const float* origin, const float* dir /*unit*/, float maxDist, uint32_t layerMask,
	float* outPoint, float* outNormal, float* outDist, uint64_t* outEntity, uint32_t* outBody)
{
	return phys::raycast(origin, dir, maxDist, layerMask, outPoint, outNormal, outDist, outEntity, outBody) ? 1 : 0;
}

EDITOR_INTERFACE int32_t PhysicsOverlapSphere(const float* center, float radius, uint32_t layerMask,
	uint64_t* outEntities, uint32_t* outBodies, int32_t maxCount)
{
	return phys::overlap_sphere(center, radius, layerMask, outEntities, outBodies, maxCount);
}

// Returns the count written and drains those events from the queue.
EDITOR_INTERFACE int32_t PhysicsGetContacts(PhysicsContact* buffer, int32_t maxCount)
{
	return phys::get_contacts(reinterpret_cast<phys::contact_event*>(buffer), maxCount);
}

// ---- Character controller (Jolt CharacterVirtual, issue #105) ----

EDITOR_INTERFACE uint32_t PhysicsCharacterCreate(float radius, float height, const float* pos, float maxSlopeDeg, float stepHeight, float mass)
{
	return phys::character_create(radius, height, pos, maxSlopeDeg, stepHeight, mass);
}

EDITOR_INTERFACE void PhysicsCharacterDestroy(uint32_t c)                          { phys::character_destroy(c); }
EDITOR_INTERFACE void PhysicsCharacterSetPosition(uint32_t c, const float* pos)    { phys::character_set_position(c, pos); }

// desiredVelocity = what the game wants this frame (horizontal move + vertical from gravity/jump); the
// controller slides along walls, walks stairs (stepHeight), sticks to floors and pushes DYNAMIC bodies.
EDITOR_INTERFACE void PhysicsCharacterMove(uint32_t c, const float* desiredVelocity, float dt,
	float* outPos, float* outVelocity, int32_t* outGrounded, float* outGroundNormal)
{
	phys::character_move(c, desiredVelocity, dt, outPos, outVelocity, outGrounded, outGroundNormal);
}

// ---- Constraints / joints (issue #103) ----
// bodyA = the jointed body (valid handle), bodyB = the connected body or 0 = the world. Points / axes are WORLD
// space at creation; the pose at creation is the rest pose (hinge angle 0, slider position 0). breakForce (N) > 0
// = breakable: a joint whose linear force exceeds it is disabled and reported by PhysicsGetBrokenConstraints.
// The two bodies of an enabled joint never collide with each other. Destroying a body destroys its joints.

// Hinge (doors): rotation about `axis` through `pivot`; `normal` = reference direction of angle 0 (null / parallel
// = any). Limits in degrees, min in [-180, 0], max in [0, 180]. motorMaxTorque > 0 starts a velocity motor
// (motorTargetVel deg/s; 0 acts as friction).
EDITOR_INTERFACE uint32_t PhysicsCreateHinge(uint32_t bodyA, uint32_t bodyB, const float* pivot /*3*/, const float* axis /*3*/,
	const float* normal /*3*/, float minDeg, float maxDeg, int32_t useLimits, float motorTargetVel, float motorMaxTorque, float breakForce)
{
	return phys::create_hinge(bodyA, bodyB, pivot, axis, normal, minDeg, maxDeg, useLimits != 0, motorTargetVel, motorMaxTorque, breakForce);
}

// Ball / socket at `point` (Jolt PointConstraint); with useLimits a swing cone (half angle swingLimitDeg) around
// twistAxis (null = from the point towards bodyA's centre of mass) + twist range (Jolt SwingTwistConstraint).
EDITOR_INTERFACE uint32_t PhysicsCreateBallJoint(uint32_t bodyA, uint32_t bodyB, const float* point /*3*/, const float* twistAxis /*3*/,
	float swingLimitDeg, float twistMinDeg, float twistMaxDeg, int32_t useLimits, float breakForce)
{
	return phys::create_ball_joint(bodyA, bodyB, point, twistAxis, swingLimitDeg, twistMinDeg, twistMaxDeg, useLimits != 0, breakForce);
}

// Slider (platforms, drawers): translation along `axis` only. Limits in metres relative to the creation pose
// (min <= 0 <= max). motorMode 0 off / 1 velocity (motorTarget m/s) / 2 position (motorTarget m, spring
// springFrequency Hz + springDamping); motorMaxForce <= 0 = unlimited.
EDITOR_INTERFACE uint32_t PhysicsCreateSlider(uint32_t bodyA, uint32_t bodyB, const float* point /*3*/, const float* axis /*3*/,
	float minPos, float maxPos, int32_t useLimits, int32_t motorMode, float motorTarget, float motorMaxForce,
	float springFrequency, float springDamping, float breakForce)
{
	return phys::create_slider(bodyA, bodyB, point, axis, minPos, maxPos, useLimits != 0, motorMode, motorTarget, motorMaxForce,
		springFrequency, springDamping, breakForce);
}

// Weld: keeps the current relative pose. point = anchor (null = automatic).
EDITOR_INTERFACE uint32_t PhysicsCreateFixed(uint32_t bodyA, uint32_t bodyB, const float* point /*3 or null*/, float breakForce)
{
	return phys::create_fixed(bodyA, bodyB, point, breakForce);
}

// Rope / rod: keeps |pointA - pointB| in [minDistance, maxDistance] (negative = the distance at creation);
// springFrequency > 0 makes the limits soft (a bungee).
EDITOR_INTERFACE uint32_t PhysicsCreateDistance(uint32_t bodyA, uint32_t bodyB, const float* pointA /*3*/, const float* pointB /*3*/,
	float minDistance, float maxDistance, float springFrequency, float springDamping, float breakForce)
{
	return phys::create_distance(bodyA, bodyB, pointA, pointB, minDistance, maxDistance, springFrequency, springDamping, breakForce);
}

EDITOR_INTERFACE void    PhysicsDestroyConstraint(uint32_t joint)                    { phys::destroy_constraint(joint); }
EDITOR_INTERFACE int32_t PhysicsConstraintValid(uint32_t joint)                      { return phys::constraint_valid(joint) ? 1 : 0; }
EDITOR_INTERFACE void    PhysicsSetConstraintEnabled(uint32_t joint, int32_t enabled) { phys::set_constraint_enabled(joint, enabled != 0); }   // 1 also repairs a broken joint
EDITOR_INTERFACE int32_t PhysicsGetConstraintEnabled(uint32_t joint)                 { return phys::constraint_enabled(joint) ? 1 : 0; }     // 0 = disabled or broken

// Motors: mode 0 off / 1 velocity / 2 position; target deg/s | deg (hinge), m/s | m (slider); maxTorque / maxForce
// <= 0 = unlimited; frequency <= 0 = 2 Hz, damping < 0 = 1 (the position-mode spring).
EDITOR_INTERFACE void PhysicsSetHingeMotor(uint32_t joint, int32_t mode, float target, float maxTorque, float frequency, float damping)
{
	phys::set_hinge_motor(joint, mode, target, maxTorque, frequency, damping);
}

EDITOR_INTERFACE void PhysicsSetSliderMotor(uint32_t joint, int32_t mode, float target, float maxForce, float frequency, float damping)
{
	phys::set_slider_motor(joint, mode, target, maxForce, frequency, damping);
}

EDITOR_INTERFACE float   PhysicsGetHingeAngle(uint32_t joint)      { return phys::get_hinge_angle(joint); }        // degrees, 0 at creation
EDITOR_INTERFACE float   PhysicsGetSliderPosition(uint32_t joint)  { return phys::get_slider_position(joint); }    // metres, 0 at creation
EDITOR_INTERFACE float   PhysicsGetConstraintForce(uint32_t joint) { return phys::get_constraint_force(joint); }   // N, last step

// Joints that broke since the last call: up to maxCount handles (+ the breaking force in N when outForces is not
// null); removes the written ones from the queue and returns the count.
EDITOR_INTERFACE int32_t PhysicsGetBrokenConstraints(uint32_t* outJoints, float* outForces, int32_t maxCount)
{
	return phys::get_broken_constraints(outJoints, outForces, maxCount);
}

EDITOR_INTERFACE int32_t PhysicsGetConstraintCount(void) { return phys::constraint_count(); }

// ---- Debug (issue #106): world-space line segments of every joint gizmo and body shape wireframe ----
// Fills up to maxFloats (6 floats per segment: x0 y0 z0 x1 y1 z1); returns the number of floats written.
EDITOR_INTERFACE int32_t PhysicsGetDebugLines(float* buffer, int32_t maxFloats)
{
	return phys::get_debug_lines(buffer, maxFloats);
}
