#pragma once

// ============================================================================
// Physics v2 — the Jolt Physics world (GitHub issues #100 epic, #101 native bridge, #102 rigid bodies,
// #103 constraints, #105 character controller, #106 debug draw, #107 compound colliders).
//
// This is the engine-side module behind the extern "C" exports in VortexAPI/Api/PhysicsApi.cpp; the
// two mirror each other one to one and both follow the shared native ABI contract (see README.md next to
// this file). Everything here is plain C types so the API layer is a thin pass-through.
//
// Conventions (from the contract):
//   * metres / kilograms / seconds, default gravity (0, -9.81, 0); engine coordinates are handed to Jolt
//     UNCHANGED (a mirrored world is still a valid Newtonian world — nothing is flipped anywhere).
//   * quaternions are float[4] = {x, y, z, w}, Hamilton convention (v' = q·v·q*), i.e. what JPH::Quat and
//     System.Numerics.Quaternion use. Yaw +90° rotates (1,0,0) to (0,0,-1) — TestPhysics.h asserts this.
//   * body handles are u32, 0 = invalid; the entity id (u64) rides along as Jolt body user data so queries
//     and contact events can report entities.
//   * all calls come from the single engine/game thread. Jolt raises contact callbacks from its worker
//     threads; the module queues them and hands them out on the calling thread in get_contacts().
//
// Without Jolt in the build (VORTEX_HAS_JOLT undefined / 0 — the Visual Studio projects, or
// -DVORTEX_ENABLE_JOLT=OFF) the .cpp compiles the stub: init() returns false and every other function is a
// harmless no-op returning 0 / false, so the managed side can detect "no physics" and keep the legacy path.
// ============================================================================

#include "../Common/CommonHeaders.h"

#ifndef VORTEX_HAS_JOLT
#define VORTEX_HAS_JOLT 0
#endif

namespace vortex::physics {

	// ---- ABI constants ---------------------------------------------------------------------------------
	// Motion types
	inline constexpr s32 motion_static{ 0 };
	inline constexpr s32 motion_kinematic{ 1 };
	inline constexpr s32 motion_dynamic{ 2 };

	// Shape types (dims = 3 floats): box = half extents xyz; sphere = radius; capsule = radius + half height of
	// the cylinder part (total height 2 * (r + half)); cylinder = radius + half height.
	inline constexpr s32 shape_box{ 0 };
	inline constexpr s32 shape_sphere{ 1 };
	inline constexpr s32 shape_capsule{ 2 };
	inline constexpr s32 shape_cylinder{ 3 };

	// Object layers. Bit i of a query layer mask selects layer i. Broadphase: STATIC -> NON_MOVING, the rest -> MOVING.
	inline constexpr s32 layer_static{ 0 };      // world geometry
	inline constexpr s32 layer_dynamic{ 1 };
	inline constexpr s32 layer_character{ 2 };
	inline constexpr s32 layer_trigger{ 3 };     // Jolt sensor: reports contacts, no collision response
	inline constexpr s32 layer_debris{ 4 };      // shells, small props: collide with STATIC + DYNAMIC only
	inline constexpr s32 layer_count{ 5 };
	inline constexpr u32 layer_mask_all{ 0xFFFF'FFFFu };

	// Lock flags (bitmask): freeze the body on an axis.
	inline constexpr u32 lock_position_x{ 1u << 0 };
	inline constexpr u32 lock_position_y{ 1u << 1 };
	inline constexpr u32 lock_position_z{ 1u << 2 };
	inline constexpr u32 lock_rotation_x{ 1u << 3 };
	inline constexpr u32 lock_rotation_y{ 1u << 4 };
	inline constexpr u32 lock_rotation_z{ 1u << 5 };

	// Contact event kinds
	inline constexpr s32 contact_added{ 0 };
	inline constexpr s32 contact_persisted{ 1 };
	inline constexpr s32 contact_removed{ 2 };

	// Constraint (joint) types, issue #103. A "ball" joint is a Jolt PointConstraint without limits and a
	// SwingTwistConstraint with limits.
	inline constexpr s32 constraint_hinge{ 0 };
	inline constexpr s32 constraint_ball{ 1 };
	inline constexpr s32 constraint_slider{ 2 };
	inline constexpr s32 constraint_fixed{ 3 };
	inline constexpr s32 constraint_distance{ 4 };

	// Motor modes of hinge / slider joints.
	inline constexpr s32 motor_off{ 0 };
	inline constexpr s32 motor_velocity{ 1 };   // drive to a target velocity (deg/s or m/s) with a force / torque limit
	inline constexpr s32 motor_position{ 2 };   // spring to a target angle / position (deg or m): frequency + damping

	// One queued contact event. Layout-identical to the C ABI's PhysicsContact (PhysicsApi.cpp static_asserts it).
	struct contact_event
	{
		u64 entity_a;
		u64 entity_b;
		u32 body_a;
		u32 body_b;
		f32 point[3];
		f32 normal[3];      // points from A to B
		f32 impulse;        // estimated normal impulse of the pair (N·s); 0 for sensors and resting contacts
		s32 kind;           // contact_added / contact_persisted / contact_removed
		s32 is_trigger;     // 1 when either body is a sensor
	};

	// ---- World ------------------------------------------------------------------------------------------
	bool init();                                      // idempotent; false = stub / no physics in this build
	void shutdown();
	bool available();                                 // result of the last init()
	void clear();                                     // destroys every body and character (scene switch, stop play)
	void set_gravity(f32 x, f32 y, f32 z);
	void step(f32 dt, s32 collision_steps);           // ONE fixed step of dt (the caller accumulates); collision_steps >= 1
	s32  body_count();

	// ---- Bodies -----------------------------------------------------------------------------------------
	u32 create_body(u64 entity_id, s32 shape_type, const f32* dims /*3*/,
		const f32* pos /*3*/, const f32* quat /*4 xyzw*/,
		s32 motion, f32 mass, f32 friction, f32 restitution,
		f32 linear_damping, f32 angular_damping, bool is_trigger, s32 layer,
		u32 lock_flags, f32 gravity_factor);

	// Static triangle mesh (convex == false) or convex hull (convex == true). verts = xyz triples in LOCAL space,
	// scale is applied here. Triangle meshes must be static (motion 0); convex hulls may be dynamic.
	u32 create_mesh_body(u64 entity_id, const f32* verts, s32 vert_count,
		const u32* indices, s32 index_count, const f32* pos, const f32* quat,
		const f32* scale /*3*/, bool convex, s32 motion, f32 mass,
		f32 friction, f32 restitution, bool is_trigger, s32 layer);

	// Static height field for a terrain (#124): heights = sample_count x sample_count metres, row z / column x, one
	// sample every cell_size metres from the body origin along +X / +Z (Jolt HeightFieldShape; the sample count is
	// padded to Jolt's block size with "no collision" samples, so any count >= 2 works).
	u32 create_heightfield_body(u64 entity_id, const f32* heights, s32 sample_count,
		const f32* pos, const f32* quat, f32 cell_size, f32 friction, f32 restitution, s32 layer);

	// Compound of boxes / spheres / capsules / cylinders on ONE body (issue #107). Each child is 11 floats in
	// `children`: shape type (as float) + dims(3) + local position(3) + local quaternion(4).
	inline constexpr s32 compound_child_floats{ 11 };
	u32 create_compound_body(u64 entity_id, const f32* children, s32 count,
		const f32* pos, const f32* quat, s32 motion, f32 mass,
		f32 friction, f32 restitution, f32 linear_damping, f32 angular_damping,
		bool is_trigger, s32 layer, u32 lock_flags, f32 gravity_factor);

	void destroy_body(u32 body);
	bool body_valid(u32 body);
	u64  body_entity(u32 body);

	void set_body_transform(u32 body, const f32* pos, const f32* quat);            // teleport (activates)
	void move_kinematic(u32 body, const f32* pos, const f32* quat, f32 dt);        // velocity-based kinematic drive
	bool get_body_transform(u32 body, f32* out_pos /*3*/, f32* out_quat /*4*/);
	void set_linear_velocity(u32 body, const f32* v);
	void get_linear_velocity(u32 body, f32* out_v);
	void set_angular_velocity(u32 body, const f32* v);
	void get_angular_velocity(u32 body, f32* out_v);
	void add_force(u32 body, const f32* f);                                        // N, at the centre of mass
	void add_force_at_point(u32 body, const f32* f, const f32* world_point);
	void add_impulse(u32 body, const f32* impulse);                                // N·s
	void add_impulse_at_point(u32 body, const f32* impulse, const f32* world_point);
	void add_torque(u32 body, const f32* torque);
	void set_motion_type(u32 body, s32 motion);
	void set_gravity_factor(u32 body, f32 factor);
	void set_friction(u32 body, f32 friction);
	void set_restitution(u32 body, f32 restitution);
	// #107: how two touching bodies combine friction / restitution — 0 average, 1 minimum, 2 multiply, 3 maximum
	void set_combine_modes(u32 body, s32 friction_mode, s32 restitution_mode);
	void set_damping(u32 body, f32 linear, f32 angular);
	void set_active(u32 body, bool active);                                        // wake / sleep
	bool is_active(u32 body);
	f32  get_mass(u32 body);                                                       // 0 for static bodies

	// ---- Queries ----------------------------------------------------------------------------------------
	bool raycast(const f32* origin, const f32* dir /*unit*/, f32 max_dist, u32 layer_mask,
		f32* out_point, f32* out_normal, f32* out_dist, u64* out_entity, u32* out_body);
	s32  overlap_sphere(const f32* center, f32 radius, u32 layer_mask,
		u64* out_entities, u32* out_bodies, s32 max_count);

	// Contact events since the previous drain (sensor and solid contacts). Writes up to max_count events, removes
	// the written ones from the queue and returns the count written.
	s32  get_contacts(contact_event* buffer, s32 max_count);

	// ---- Character controller (Jolt CharacterVirtual, issue #105) ---------------------------------------
	// `pos` is the character's feet; radius/height describe the capsule (height = total height).
	u32  character_create(f32 radius, f32 height, const f32* pos, f32 max_slope_deg, f32 step_height, f32 mass);
	void character_destroy(u32 character);
	void character_set_position(u32 character, const f32* pos);
	// desired_velocity = what the game wants this frame (horizontal move + vertical from gravity/jump). The
	// controller slides along walls, walks stairs (step_height), sticks to floors and pushes DYNAMIC bodies.
	void character_move(u32 character, const f32* desired_velocity, f32 dt,
		f32* out_pos, f32* out_velocity, s32* out_grounded, f32* out_ground_normal);

	// ---- Constraints / joints (issue #103) ---------------------------------------------------------------
	// Handles are u32, 0 = invalid (same scheme as bodies). `body_a` is the jointed body (must be valid), `body_b`
	// the body it is connected to, or 0 = the static world. Positions and axes are WORLD space and describe the
	// joint at creation: the pose at creation is the rest pose (hinge angle 0, slider position 0). Angles are
	// degrees, positions metres, forces N, torques N·m. break_force > 0 makes the joint breakable: when the linear
	// force the joint applied in a step exceeds it, the joint is disabled and reported by get_broken_constraints().
	// Bodies connected by an enabled joint do not collide with each other. Destroying a body destroys its joints.
	//
	// Hinge: rotation about `axis` through `pivot`. `normal` (perpendicular to the axis; null / parallel = any)
	// is the reference direction angle 0 is drawn from. Limits [min_deg, max_deg] with min in [-180, 0] and max
	// in [0, 180] (clamped). motor_max_torque > 0 starts a velocity motor (motor_target_vel in deg/s; a target of
	// 0 acts as friction). get_hinge_angle() = rotation of body_a relative to body_b about the axis (right hand).
	u32  create_hinge(u32 body_a, u32 body_b, const f32* pivot /*3*/, const f32* axis /*3*/, const f32* normal /*3*/,
		f32 min_deg, f32 max_deg, bool use_limits, f32 motor_target_vel, f32 motor_max_torque, f32 break_force);
	// Ball / socket at `point`. With use_limits: a swing cone of swing_limit_deg (half angle) around `twist_axis`
	// (null = from the point towards body_a's centre of mass) and a twist range [twist_min_deg, twist_max_deg].
	u32  create_ball_joint(u32 body_a, u32 body_b, const f32* point /*3*/, const f32* twist_axis /*3*/,
		f32 swing_limit_deg, f32 twist_min_deg, f32 twist_max_deg, bool use_limits, f32 break_force);
	// Prismatic joint: translation along `axis` only (no rotation). Limits [min_pos, max_pos] relative to the
	// creation pose (min <= 0 <= max, clamped). motor_mode: motor_off / motor_velocity (target m/s) /
	// motor_position (target m, spring of spring_frequency Hz + spring_damping); motor_max_force <= 0 = unlimited.
	u32  create_slider(u32 body_a, u32 body_b, const f32* point /*3*/, const f32* axis /*3*/,
		f32 min_pos, f32 max_pos, bool use_limits, s32 motor_mode, f32 motor_target, f32 motor_max_force,
		f32 spring_frequency, f32 spring_damping, f32 break_force);
	// Weld: keeps the current relative pose. point = the joint's anchor (null = automatic, between the bodies).
	u32  create_fixed(u32 body_a, u32 body_b, const f32* point /*3, may be null*/, f32 break_force);
	// Rope / rod: keeps |point_a - point_b| in [min_distance, max_distance] (negative = the distance at creation).
	// point_a is attached to body_a, point_b to body_b (or fixed in the world). spring_frequency > 0 = soft limits.
	u32  create_distance(u32 body_a, u32 body_b, const f32* point_a /*3*/, const f32* point_b /*3*/,
		f32 min_distance, f32 max_distance, f32 spring_frequency, f32 spring_damping, f32 break_force);

	void destroy_constraint(u32 constraint);
	bool constraint_valid(u32 constraint);
	void set_constraint_enabled(u32 constraint, bool enabled);   // re-enabling a broken joint repairs it
	bool constraint_enabled(u32 constraint);                     // false when disabled or broken
	// Motors (mode motor_off / motor_velocity / motor_position): target deg/s | deg (hinge), m/s | m (slider);
	// max torque / force <= 0 = unlimited; frequency <= 0 = 2 Hz, damping < 0 = 1 (position mode spring).
	void set_hinge_motor(u32 constraint, s32 mode, f32 target, f32 max_torque, f32 frequency, f32 damping);
	void set_slider_motor(u32 constraint, s32 mode, f32 target, f32 max_force, f32 frequency, f32 damping);
	f32  get_hinge_angle(u32 constraint);       // degrees, 0 at creation (0 for other joint types)
	f32  get_slider_position(u32 constraint);   // metres along the axis, 0 at creation (0 for other joint types)
	f32  get_constraint_force(u32 constraint);  // linear force (N) the joint applied in the last step
	// Joints that broke since the last call: writes up to max_count handles (+ the force that broke each one when
	// out_forces is not null), removes them from the queue and returns the count written.
	s32  get_broken_constraints(u32* out_constraints, f32* out_forces, s32 max_count);
	s32  constraint_count();

	// ---- Debug (issue #106) -----------------------------------------------------------------------------
	// World-space wireframe segments (6 floats per segment: x0 y0 z0 x1 y1 z1): every enabled joint (anchor
	// marker, axis, limit arc / range, cone) first, then every body's shape and the character capsules.
	// Fills up to max_floats and returns the number of floats written. Meshes are capped at 20k floats per body.
	s32  get_debug_lines(f32* buffer, s32 max_floats);
	// #106: like get_debug_lines, plus one kind per segment (0 static, 1 kinematic, 2 dynamic, 3 sleeping, 4 joint,
	// 5 character) for the editor's colour-coded, switchable debug layers
	s32  get_debug_lines_ex(f32* buffer, s32 max_floats, u8* kinds, s32 max_kinds);
}
