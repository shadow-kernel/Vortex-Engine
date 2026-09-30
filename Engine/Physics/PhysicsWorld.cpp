#include "PhysicsWorld.h"

// ============================================================================
// Physics v2 — Jolt Physics world. See PhysicsWorld.h for the module contract and README.md for the
// architecture. The Jolt implementation is guarded by VORTEX_HAS_JOLT; the #else branch at the bottom is
// the stub the Visual Studio projects (and -DVORTEX_ENABLE_JOLT=OFF) compile.
// ============================================================================

#if VORTEX_HAS_JOLT

// Jolt.h must come first (it configures the library); JPH_SUPPRESS_WARNINGS silences the warnings Jolt's
// own headers raise under strict warning levels.
#include <Jolt/Jolt.h>
JPH_SUPPRESS_WARNINGS

#include <Jolt/RegisterTypes.h>
#include <Jolt/Core/Factory.h>
#include <Jolt/Core/JobSystemThreadPool.h>
#include <Jolt/Core/TempAllocator.h>
#include <Jolt/Geometry/AABox.h>
#include <Jolt/Physics/PhysicsSettings.h>
#include <Jolt/Physics/PhysicsSystem.h>
#include <Jolt/Physics/Body/BodyCreationSettings.h>
#include <Jolt/Physics/Body/BodyLock.h>
#include <Jolt/Physics/Character/CharacterVirtual.h>
#include <Jolt/Physics/Collision/BroadPhase/BroadPhaseLayer.h>
#include <Jolt/Physics/Collision/CastResult.h>
#include <Jolt/Physics/Collision/CollideShape.h>
#include <Jolt/Physics/Collision/CollisionCollectorImpl.h>
#include <Jolt/Physics/Collision/ContactListener.h>
#include <Jolt/Physics/Collision/NarrowPhaseQuery.h>
#include <Jolt/Physics/Collision/ObjectLayer.h>
#include <Jolt/Physics/Collision/RayCast.h>
#include <Jolt/Physics/Collision/Shape/BoxShape.h>
#include <Jolt/Physics/Collision/Shape/CapsuleShape.h>
#include <Jolt/Physics/Collision/Shape/CompoundShape.h>
#include <Jolt/Physics/Collision/Shape/ConvexHullShape.h>
#include <Jolt/Physics/Collision/Shape/CylinderShape.h>
#include <Jolt/Physics/Collision/Shape/MeshShape.h>
#include <Jolt/Physics/Collision/Shape/OffsetCenterOfMassShape.h>
#include <Jolt/Physics/Collision/Shape/RotatedTranslatedShape.h>
#include <Jolt/Physics/Collision/Shape/ScaleHelpers.h>
#include <Jolt/Physics/Collision/Shape/ScaledShape.h>
#include <Jolt/Physics/Collision/Shape/SphereShape.h>
#include <Jolt/Physics/Collision/Shape/StaticCompoundShape.h>

#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

namespace vortex::physics {

	namespace {

		// ---- diagnostics ------------------------------------------------------------------------------
		void log(const char* fmt, ...)
		{
			char text[512];
			va_list args;
			va_start(args, fmt);
			std::vsnprintf(text, sizeof(text), fmt, args);
			va_end(args);
			std::string line{ "[physics] " };
			line += text;
			line += '\n';
			platform::debug_output(line.c_str());
		}

		void jolt_trace(const char* fmt, ...)
		{
			char text[512];
			va_list args;
			va_start(args, fmt);
			std::vsnprintf(text, sizeof(text), fmt, args);
			va_end(args);
			std::string line{ "[jolt] " };
			line += text;
			line += '\n';
			platform::debug_output(line.c_str());
		}

#ifdef JPH_ENABLE_ASSERTS
		// Jolt enables its asserts in Debug builds (JPH_DEBUG) even with USE_ASSERTS OFF; its default handler
		// traps. Log and carry on instead — a physics precondition violation must not take the editor down.
		bool jolt_assert_failed(const char* expression, const char* message, const char* file, JPH::uint line)
		{
			log("Jolt assert: %s %s (%s:%u)", expression, message ? message : "", file, line);
			return false;   // false = do not break into the debugger
		}
#endif

		// ---- tuning -------------------------------------------------------------------------------------
		constexpr f32 k_min_dim{ 1.0e-3f };                       // smallest accepted shape dimension (m)
		constexpr JPH::uint k_max_bodies{ 32768 };
		constexpr JPH::uint k_body_mutexes{ 0 };                  // 0 = Jolt default
		constexpr JPH::uint k_max_body_pairs{ 65536 };
		// Update() allocates k_max_contact_constraints * sizeof(ContactConstraint) (~864 B) from the 16 MB temp
		// allocator every step, so this budget must stay well below ~18k.
		constexpr JPH::uint k_max_contact_constraints{ 10240 };
		constexpr size_t k_temp_allocator_bytes{ 16u * 1024u * 1024u };
		constexpr size_t k_max_slots{ 0xFFFF };                   // 16-bit index field in the handle
		constexpr s32 k_mesh_debug_floats_per_body{ 20000 };
		constexpr size_t k_max_pending_contacts{ 65536 };
		constexpr u64 k_graveyard_steps{ 2 };                      // how long a destroyed body stays resolvable for contact events
		constexpr int k_circle_segments{ 24 };
		constexpr f32 k_pi{ 3.14159265358979f };
		constexpr f32 k_two_pi{ 2.0f * k_pi };

		// ---- layers -------------------------------------------------------------------------------------
		constexpr JPH::BroadPhaseLayer k_bp_non_moving{ 0 };
		constexpr JPH::BroadPhaseLayer k_bp_moving{ 1 };
		constexpr JPH::uint k_bp_layer_count{ 2 };

		// Object layer collision matrix (contract): STATIC x {DYNAMIC, CHARACTER, DEBRIS, TRIGGER};
		// DYNAMIC x everything; CHARACTER x {STATIC, DYNAMIC, CHARACTER, TRIGGER}; DEBRIS x {STATIC, DYNAMIC,
		// TRIGGER}; TRIGGER x everything except TRIGGER. Jolt requires the relation to be symmetric.
		constexpr bool k_collision_matrix[layer_count][layer_count] = {
			//                STATIC  DYNAMIC CHARACTER TRIGGER DEBRIS
			/* STATIC    */ { false,  true,   true,     true,   true  },
			/* DYNAMIC   */ { true,   true,   true,     true,   true  },
			/* CHARACTER */ { true,   true,   true,     true,   false },
			/* TRIGGER   */ { true,   true,   true,     false,  true  },
			/* DEBRIS    */ { true,   true,   false,    true,   false },
		};

		constexpr bool collision_matrix_is_symmetric()
		{
			for (s32 a = 0; a < layer_count; ++a)
				for (s32 b = 0; b < layer_count; ++b)
					if (k_collision_matrix[a][b] != k_collision_matrix[b][a]) return false;
			return true;
		}
		static_assert(collision_matrix_is_symmetric(), "the object layer collision matrix must be symmetric");

		class bp_layer_interface final : public JPH::BroadPhaseLayerInterface
		{
		public:
			JPH::uint GetNumBroadPhaseLayers() const override { return k_bp_layer_count; }
			JPH::BroadPhaseLayer GetBroadPhaseLayer(JPH::ObjectLayer layer) const override
			{
				return layer == JPH::ObjectLayer(layer_static) ? k_bp_non_moving : k_bp_moving;
			}
#if defined(JPH_EXTERNAL_PROFILE) || defined(JPH_PROFILE_ENABLED)
			const char* GetBroadPhaseLayerName(JPH::BroadPhaseLayer layer) const override
			{
				return layer == k_bp_non_moving ? "NON_MOVING" : "MOVING";
			}
#endif
		};

		class object_vs_bp_filter final : public JPH::ObjectVsBroadPhaseLayerFilter
		{
		public:
			bool ShouldCollide(JPH::ObjectLayer layer, JPH::BroadPhaseLayer bp_layer) const override
			{
				if (bp_layer == k_bp_non_moving) return layer != JPH::ObjectLayer(layer_static);   // static never vs static
				return true;
			}
		};

		class object_pair_filter final : public JPH::ObjectLayerPairFilter
		{
		public:
			bool ShouldCollide(JPH::ObjectLayer a, JPH::ObjectLayer b) const override
			{
				return a < JPH::ObjectLayer(layer_count) && b < JPH::ObjectLayer(layer_count) && k_collision_matrix[a][b];
			}
		};

		// Query-time filters built from a layer mask (bit i = object layer i).
		class mask_object_filter final : public JPH::ObjectLayerFilter
		{
		public:
			explicit mask_object_filter(u32 mask) : _mask{ mask } {}
			bool ShouldCollide(JPH::ObjectLayer layer) const override { return layer < 32 && ((_mask >> layer) & 1u) != 0; }
		private:
			u32 _mask;
		};

		class mask_bp_filter final : public JPH::BroadPhaseLayerFilter
		{
		public:
			explicit mask_bp_filter(u32 mask) : _mask{ mask } {}
			bool ShouldCollide(JPH::BroadPhaseLayer layer) const override
			{
				const u32 static_bit = 1u << layer_static;
				return layer == k_bp_non_moving ? (_mask & static_bit) != 0 : (_mask & ~static_bit) != 0;
			}
		private:
			u32 _mask;
		};

		// ---- contact events ---------------------------------------------------------------------------
		// Raw event as captured on a Jolt worker thread; body handles / missing entity ids are resolved on
		// the calling thread when the queue is drained (get_contacts).
		struct queued_contact
		{
			JPH::BodyID body_a;
			JPH::BodyID body_b;
			u64 entity_a{ 0 };
			u64 entity_b{ 0 };
			f32 point[3]{};
			f32 normal[3]{};
			f32 impulse{ 0.0f };
			s32 kind{ 0 };
			s32 is_trigger{ 0 };   // -1 = unknown yet (removed events carry body ids only)
		};

		class contact_listener final : public JPH::ContactListener
		{
		public:
			void OnContactAdded(const JPH::Body& a, const JPH::Body& b, const JPH::ContactManifold& manifold, JPH::ContactSettings&) override
			{
				push(a, b, manifold, contact_added);
			}

			void OnContactPersisted(const JPH::Body& a, const JPH::Body& b, const JPH::ContactManifold& manifold, JPH::ContactSettings&) override
			{
				push(a, b, manifold, contact_persisted);
			}

			void OnContactRemoved(const JPH::SubShapeIDPair& pair) override
			{
				queued_contact c{};
				c.body_a = pair.GetBody1ID();
				c.body_b = pair.GetBody2ID();
				c.kind = contact_removed;
				c.is_trigger = -1;
				std::lock_guard<std::mutex> lock{ _mutex };
				_queue.push_back(c);
			}

			// Moves everything captured since the last call to `out` (calling thread).
			void drain(std::vector<queued_contact>& out)
			{
				std::lock_guard<std::mutex> lock{ _mutex };
				if (_queue.empty()) return;
				out.insert(out.end(), _queue.begin(), _queue.end());
				_queue.clear();
			}

			void clear()
			{
				std::lock_guard<std::mutex> lock{ _mutex };
				_queue.clear();
			}

		private:
			static f32 inverse_mass(const JPH::Body& body)
			{
				if (!body.IsDynamic()) return 0.0f;
				const JPH::MotionProperties* mp = body.GetMotionPropertiesUnchecked();
				return mp ? mp->GetInverseMassUnchecked() : 0.0f;
			}

			void push(const JPH::Body& a, const JPH::Body& b, const JPH::ContactManifold& manifold, s32 kind)
			{
				queued_contact c{};
				c.body_a = a.GetID();
				c.body_b = b.GetID();
				c.entity_a = a.GetUserData();
				c.entity_b = b.GetUserData();
				c.kind = kind;
				c.is_trigger = (a.IsSensor() || b.IsSensor()) ? 1 : 0;

				// Representative contact point: the centroid of the manifold on body A.
				JPH::Vec3 point{ JPH::Vec3(manifold.mBaseOffset) };
				const JPH::uint count = manifold.mRelativeContactPointsOn1.size();
				if (count > 0)
				{
					point = JPH::Vec3::sZero();
					for (JPH::uint i = 0; i < count; ++i) point += JPH::Vec3(manifold.GetWorldSpaceContactPointOn1(i));
					point = point / f32(count);
				}
				const JPH::Vec3 normal = manifold.mWorldSpaceNormal;   // direction to push B out of A: A -> B

				// Jolt reports impulses only through the solver; estimate the normal impulse of the pair from the
				// approach velocity and the effective mass (good enough for impact sounds / damage thresholds).
				if (c.is_trigger == 0)
				{
					const JPH::Vec3 relative = b.GetPointVelocity(point) - a.GetPointVelocity(point);
					const f32 approach = -relative.Dot(normal);
					if (approach > 0.0f)
					{
						const f32 inverse_sum = inverse_mass(a) + inverse_mass(b);
						if (inverse_sum > 0.0f) c.impulse = approach / inverse_sum;
					}
				}

				c.point[0] = point.GetX(); c.point[1] = point.GetY(); c.point[2] = point.GetZ();
				c.normal[0] = normal.GetX(); c.normal[1] = normal.GetY(); c.normal[2] = normal.GetZ();

				std::lock_guard<std::mutex> lock{ _mutex };
				_queue.push_back(c);
			}

			std::mutex _mutex;
			std::vector<queued_contact> _queue;
		};

		// ---- handle tables ------------------------------------------------------------------------------
		// handle = generation << 16 | (slot index + 1); generation starts at 1 so a handle is never 0.
		struct body_slot
		{
			JPH::BodyID id;
			u64 entity{ 0 };
			u16 generation{ 1 };
			u8 layer{ 0 };
			u8 motion{ 0 };
			bool is_trigger{ false };
			bool must_be_static{ false };
			bool used{ false };
		};

		struct character_slot
		{
			JPH::Ref<JPH::CharacterVirtual> character;
			f32 step_height{ 0.3f };
			u16 generation{ 1 };
			bool used{ false };
		};

		// A destroyed body stays resolvable for a couple of steps so the 'removed' contact events Jolt raises
		// in the following update still report its handle / entity.
		struct grave
		{
			u32 handle{ 0 };
			u64 entity{ 0 };
			bool is_trigger{ false };
			u64 step{ 0 };
		};

		constexpr u32 make_handle(u32 index, u16 generation) { return (u32(generation) << 16) | (index + 1u); }
		constexpr u32 handle_index(u32 handle) { return (handle & 0xFFFFu) - 1u; }
		constexpr u16 handle_generation(u32 handle) { return u16(handle >> 16); }

		template<class slot_type>
		slot_type* resolve_slot(std::vector<slot_type>& slots, u32 handle)
		{
			if ((handle & 0xFFFFu) == 0) return nullptr;
			const u32 index = handle_index(handle);
			if (index >= slots.size()) return nullptr;
			slot_type& slot = slots[index];
			if (!slot.used || slot.generation != handle_generation(handle)) return nullptr;
			return &slot;
		}

		template<class slot_type>
		u32 allocate_slot(std::vector<slot_type>& slots, std::vector<u32>& free_list, slot_type*& out_slot)
		{
			u32 index;
			if (!free_list.empty())
			{
				index = free_list.back();
				free_list.pop_back();
			}
			else
			{
				if (slots.size() >= k_max_slots) { out_slot = nullptr; return 0; }
				index = u32(slots.size());
				slots.emplace_back();
			}
			slot_type& slot = slots[index];
			slot.used = true;
			out_slot = &slot;
			return make_handle(index, slot.generation);
		}

		template<class slot_type>
		void release_slot(std::vector<slot_type>& slots, std::vector<u32>& free_list, u32 index)
		{
			u16 generation = u16(slots[index].generation + 1);
			if (generation == 0) generation = 1;
			slots[index] = slot_type{};   // drops references (the character) and clears the payload
			slots[index].generation = generation;
			free_list.push_back(index);
		}

		int worker_thread_count()
		{
			const int cores = int(std::thread::hardware_concurrency());
			return std::max(1, cores - 1);
		}

		// ---- the world ------------------------------------------------------------------------------------
		struct world
		{
			JPH::TempAllocatorImpl temp_allocator{ k_temp_allocator_bytes };
			JPH::JobSystemThreadPool job_system{ JPH::cMaxPhysicsJobs, JPH::cMaxPhysicsBarriers, worker_thread_count() };
			bp_layer_interface bp_interface;
			object_vs_bp_filter object_vs_bp;
			object_pair_filter object_pair;
			contact_listener listener;
			JPH::PhysicsSystem system;   // after the interfaces it keeps pointers to (destroyed before them)

			std::vector<body_slot> bodies;
			std::vector<u32> free_bodies;
			s32 live_bodies{ 0 };
			std::unordered_map<u32, u32> handle_by_body;   // BodyID::GetIndexAndSequenceNumber() -> handle
			std::unordered_map<u32, grave> graveyard;

			std::vector<character_slot> characters;        // after `system`: CharacterVirtual's destructor talks to it
			std::vector<u32> free_characters;

			std::vector<queued_contact> pending;           // drained events not yet handed out
			size_t pending_cursor{ 0 };

			u64 step_index{ 0 };
			bool static_bodies_added{ false };             // OptimizeBroadPhase before the next step
			bool update_error_logged{ false };

			world()
			{
				system.Init(k_max_bodies, k_body_mutexes, k_max_body_pairs, k_max_contact_constraints, bp_interface, object_vs_bp, object_pair);
				system.SetContactListener(&listener);
				system.SetGravity(JPH::Vec3(0.0f, -9.81f, 0.0f));
				// Jolt lets resting bodies sink 2 cm by default; 5 mm keeps small props (0.25 m boxes) visually on the floor.
				JPH::PhysicsSettings settings = system.GetPhysicsSettings();
				settings.mPenetrationSlop = 0.005f;
				system.SetPhysicsSettings(settings);
			}
		};

		// Constant-initialised pointer: no static initialisation order hazard, the world is created lazily in init().
		world* g_world{ nullptr };

		// ---- conversions / sanitising ----------------------------------------------------------------------
		bool finite(f32 v) { return std::isfinite(v); }
		f32 sane(f32 v, f32 fallback) { return finite(v) ? v : fallback; }

		JPH::Vec3 to_vec3(const f32* v, JPH::Vec3Arg fallback = JPH::Vec3::sZero())
		{
			if (!v || !finite(v[0]) || !finite(v[1]) || !finite(v[2])) return fallback;
			return JPH::Vec3(v[0], v[1], v[2]);
		}

		JPH::Quat to_quat(const f32* q)
		{
			if (!q || !finite(q[0]) || !finite(q[1]) || !finite(q[2]) || !finite(q[3])) return JPH::Quat::sIdentity();
			const JPH::Quat quat(q[0], q[1], q[2], q[3]);
			const f32 length = quat.Length();
			if (!(length > 1.0e-6f)) return JPH::Quat::sIdentity();
			return quat / length;
		}

		void store3(f32* out, JPH::Vec3Arg v)
		{
			if (!out) return;
			out[0] = v.GetX(); out[1] = v.GetY(); out[2] = v.GetZ();
		}

		void store4(f32* out, JPH::QuatArg q)
		{
			if (!out) return;
			out[0] = q.GetX(); out[1] = q.GetY(); out[2] = q.GetZ(); out[3] = q.GetW();
		}

		f32 dim(const f32* dims, int i)
		{
			const f32 v = dims ? sane(dims[i], k_min_dim) : k_min_dim;
			return std::max(v, k_min_dim);
		}

		JPH::EMotionType to_motion_type(s32 motion)
		{
			switch (motion)
			{
			case motion_kinematic: return JPH::EMotionType::Kinematic;
			case motion_dynamic:   return JPH::EMotionType::Dynamic;
			default:               return JPH::EMotionType::Static;
			}
		}

		s32 clamp_motion(s32 motion) { return motion < motion_static ? motion_static : (motion > motion_dynamic ? motion_dynamic : motion); }

		// Lock flag bits (contract) coincide with Jolt's EAllowedDOFs bits: translation xyz = 1,2,4, rotation xyz = 8,16,32.
		JPH::EAllowedDOFs to_allowed_dofs(u32 lock_flags)
		{
			u8 allowed = u8(0x3Fu & ~(lock_flags & 0x3Fu));
			if (allowed == 0)
			{
				log("lock flags 0x%X freeze every degree of freedom - ignored (use a static or kinematic body)", lock_flags);
				allowed = 0x3Fu;
			}
			return static_cast<JPH::EAllowedDOFs>(allowed);
		}

		// Triggers always live in the TRIGGER layer; the STATIC layer (NON_MOVING broadphase) is reserved for
		// static bodies, so a moving body asking for it is placed in DYNAMIC (same collision partners).
		s32 normalise_layer(s32 layer, s32 motion, bool is_trigger)
		{
			if (is_trigger) return layer_trigger;
			if (layer < 0 || layer >= layer_count) layer = motion == motion_static ? layer_static : layer_dynamic;
			if (layer == layer_static && motion != motion_static) layer = layer_dynamic;
			return layer;
		}

		// ---- shape factories --------------------------------------------------------------------------------
		JPH::ShapeRefC finish(const JPH::ShapeSettings& settings, const char* what)
		{
			const JPH::Shape::ShapeResult result = settings.Create();
			if (result.HasError())
			{
				log("%s shape: %s", what, result.GetError().c_str());
				return nullptr;
			}
			return result.Get();
		}

		JPH::ShapeRefC make_primitive(s32 shape_type, const f32* dims)
		{
			switch (shape_type)
			{
			case shape_box:
			{
				const JPH::Vec3 half(dim(dims, 0), dim(dims, 1), dim(dims, 2));
				const f32 convex_radius = std::min(JPH::cDefaultConvexRadius, 0.5f * half.ReduceMin());
				return finish(JPH::BoxShapeSettings(half, convex_radius), "box");
			}
			case shape_sphere:
				return finish(JPH::SphereShapeSettings(dim(dims, 0)), "sphere");
			case shape_capsule:
			{
				const f32 radius = dim(dims, 0);
				const f32 half_height = dims ? std::max(sane(dims[1], 0.0f), 0.0f) : 0.0f;
				if (half_height < 1.0e-4f) return finish(JPH::SphereShapeSettings(radius), "capsule");
				return finish(JPH::CapsuleShapeSettings(half_height, radius), "capsule");
			}
			case shape_cylinder:
			{
				const f32 radius = dim(dims, 0);
				const f32 half_height = dim(dims, 1);
				const f32 convex_radius = std::min(JPH::cDefaultConvexRadius, 0.5f * std::min(radius, half_height));
				return finish(JPH::CylinderShapeSettings(half_height, radius, convex_radius), "cylinder");
			}
			default:
				log("unknown shape type %d", shape_type);
				return nullptr;
			}
		}

		// ---- body creation core -----------------------------------------------------------------------------
		u32 add_body(world& w, u64 entity_id, const JPH::ShapeRefC& shape, const f32* pos, const f32* quat,
			s32 motion, f32 mass, f32 friction, f32 restitution, f32 linear_damping, f32 angular_damping,
			bool is_trigger, s32 layer, u32 lock_flags, f32 gravity_factor)
		{
			if (!shape) return 0;

			motion = clamp_motion(motion);
			if (shape->MustBeStatic() && motion != motion_static)
			{
				log("entity %llu: shape must be static - motion type forced to static", (unsigned long long)entity_id);
				motion = motion_static;
			}
			layer = normalise_layer(layer, motion, is_trigger);

			JPH::BodyCreationSettings settings(shape, to_vec3(pos), to_quat(quat), to_motion_type(motion), JPH::ObjectLayer(layer));
			settings.mUserData = entity_id;
			settings.mFriction = std::max(sane(friction, 0.5f), 0.0f);
			settings.mRestitution = std::max(sane(restitution, 0.0f), 0.0f);
			settings.mLinearDamping = std::max(sane(linear_damping, 0.05f), 0.0f);
			settings.mAngularDamping = std::max(sane(angular_damping, 0.05f), 0.0f);
			settings.mGravityFactor = sane(gravity_factor, 1.0f);
			settings.mIsSensor = is_trigger;
			settings.mAllowDynamicOrKinematic = !shape->MustBeStatic();   // lets set_motion_type switch later
			settings.mAllowedDOFs = to_allowed_dofs(lock_flags);
			if (motion == motion_dynamic && mass > 0.0f && finite(mass))
			{
				JPH::MassProperties properties = shape->GetMassProperties();
				properties.ScaleToMass(mass);
				settings.mOverrideMassProperties = JPH::EOverrideMassProperties::MassAndInertiaProvided;
				settings.mMassPropertiesOverride = properties;
			}

			JPH::BodyInterface& bodies = w.system.GetBodyInterface();
			JPH::Body* body = bodies.CreateBody(settings);
			if (!body)
			{
				log("entity %llu: Jolt body pool exhausted (%u bodies)", (unsigned long long)entity_id, k_max_bodies);
				return 0;
			}

			body_slot* slot = nullptr;
			const u32 handle = allocate_slot(w.bodies, w.free_bodies, slot);
			if (!slot)
			{
				bodies.DestroyBody(body->GetID());
				log("entity %llu: body handle table exhausted", (unsigned long long)entity_id);
				return 0;
			}

			bodies.AddBody(body->GetID(), motion == motion_static ? JPH::EActivation::DontActivate : JPH::EActivation::Activate);

			slot->id = body->GetID();
			slot->entity = entity_id;
			slot->layer = u8(layer);
			slot->motion = u8(motion);
			slot->is_trigger = is_trigger;
			slot->must_be_static = shape->MustBeStatic();
			w.handle_by_body[body->GetID().GetIndexAndSequenceNumber()] = handle;
			if (layer == layer_static) w.static_bodies_added = true;
			++w.live_bodies;
			return handle;
		}

		void remove_body(world& w, u32 handle, body_slot& slot)
		{
			JPH::BodyInterface& bodies = w.system.GetBodyInterface();
			if (bodies.IsAdded(slot.id)) bodies.RemoveBody(slot.id);
			bodies.DestroyBody(slot.id);
			const u32 key = slot.id.GetIndexAndSequenceNumber();
			w.handle_by_body.erase(key);
			w.graveyard[key] = grave{ handle, slot.entity, slot.is_trigger, w.step_index };
			release_slot(w.bodies, w.free_bodies, handle_index(handle));
			--w.live_bodies;
		}

		// Body id -> (handle, entity, trigger) for query results and contact events; falls back to the graveyard.
		void resolve_body(const world& w, const JPH::BodyID& id, u32& handle, u64& entity, bool& is_trigger)
		{
			const u32 key = id.GetIndexAndSequenceNumber();
			if (const auto it = w.handle_by_body.find(key); it != w.handle_by_body.end())
			{
				handle = it->second;
				const body_slot& slot = w.bodies[handle_index(handle)];
				entity = slot.entity;
				is_trigger = slot.is_trigger;
				return;
			}
			if (const auto it = w.graveyard.find(key); it != w.graveyard.end())
			{
				handle = it->second.handle;
				entity = it->second.entity;
				is_trigger = it->second.is_trigger;
				return;
			}
			handle = 0;
			entity = 0;
			is_trigger = false;
		}

		u32 handle_of(const world& w, const JPH::BodyID& id)
		{
			const auto it = w.handle_by_body.find(id.GetIndexAndSequenceNumber());
			return it != w.handle_by_body.end() ? it->second : 0;
		}

		body_slot* find_body(u32 handle)
		{
			return g_world ? resolve_slot(g_world->bodies, handle) : nullptr;
		}

		character_slot* find_character(u32 handle)
		{
			if (!g_world) return nullptr;
			character_slot* slot = resolve_slot(g_world->characters, handle);
			return slot && slot->character ? slot : nullptr;
		}

		// ---- debug wireframes ---------------------------------------------------------------------------------
		struct line_sink
		{
			f32* buffer;
			s32 capacity;
			s32 written{ 0 };
			s32 body_mesh_floats{ 0 };   // per-body budget for triangle soups

			bool full() const { return written + 6 > capacity; }

			bool add(JPH::Vec3Arg a, JPH::Vec3Arg b)
			{
				if (full()) return false;
				f32* out = buffer + written;
				out[0] = a.GetX(); out[1] = a.GetY(); out[2] = a.GetZ();
				out[3] = b.GetX(); out[4] = b.GetY(); out[5] = b.GetZ();
				written += 6;
				return true;
			}
		};

		// Arc of a circle in the plane spanned by u/v (local space), transformed by `world`.
		void draw_arc(line_sink& sink, const JPH::Mat44& world, JPH::Vec3Arg center, JPH::Vec3Arg u, JPH::Vec3Arg v,
			f32 radius, f32 angle0, f32 angle1, int segments)
		{
			auto point = [&](f32 angle) { return world * (center + u * (radius * std::cos(angle)) + v * (radius * std::sin(angle))); };
			JPH::Vec3 previous = point(angle0);
			for (int i = 1; i <= segments; ++i)
			{
				const JPH::Vec3 current = point(angle0 + (angle1 - angle0) * f32(i) / f32(segments));
				if (!sink.add(previous, current)) return;
				previous = current;
			}
		}

		void draw_box(line_sink& sink, const JPH::Mat44& world, JPH::Vec3Arg half)
		{
			JPH::Vec3 corner[8];
			for (int i = 0; i < 8; ++i)
				corner[i] = world * JPH::Vec3((i & 1) ? half.GetX() : -half.GetX(), (i & 2) ? half.GetY() : -half.GetY(), (i & 4) ? half.GetZ() : -half.GetZ());
			static constexpr int edges[12][2] = { {0,1},{2,3},{4,5},{6,7}, {0,2},{1,3},{4,6},{5,7}, {0,4},{1,5},{2,6},{3,7} };
			for (const auto& e : edges)
				if (!sink.add(corner[e[0]], corner[e[1]])) return;
		}

		void draw_sphere(line_sink& sink, const JPH::Mat44& world, JPH::Vec3Arg center, f32 radius)
		{
			const JPH::Vec3 x = JPH::Vec3::sAxisX(), y = JPH::Vec3::sAxisY(), z = JPH::Vec3::sAxisZ();
			draw_arc(sink, world, center, x, y, radius, 0.0f, k_two_pi, k_circle_segments);
			draw_arc(sink, world, center, x, z, radius, 0.0f, k_two_pi, k_circle_segments);
			draw_arc(sink, world, center, y, z, radius, 0.0f, k_two_pi, k_circle_segments);
		}

		// Capsule / cylinder along the local Y axis: rings at +-half_height, four edges, optional hemispherical caps.
		void draw_capsule_or_cylinder(line_sink& sink, const JPH::Mat44& world, f32 radius, f32 half_height, bool round_caps)
		{
			const JPH::Vec3 x = JPH::Vec3::sAxisX(), y = JPH::Vec3::sAxisY(), z = JPH::Vec3::sAxisZ();
			const JPH::Vec3 top(0.0f, half_height, 0.0f), bottom(0.0f, -half_height, 0.0f);
			draw_arc(sink, world, top, x, z, radius, 0.0f, k_two_pi, k_circle_segments);
			draw_arc(sink, world, bottom, x, z, radius, 0.0f, k_two_pi, k_circle_segments);
			const JPH::Vec3 offsets[4] = { x * radius, -x * radius, z * radius, -z * radius };
			for (const JPH::Vec3& o : offsets)
				if (!sink.add(world * (top + o), world * (bottom + o))) return;
			if (round_caps)
			{
				draw_arc(sink, world, top, x, y, radius, 0.0f, k_pi, k_circle_segments / 2);
				draw_arc(sink, world, top, z, y, radius, 0.0f, k_pi, k_circle_segments / 2);
				draw_arc(sink, world, bottom, x, y, radius, k_pi, k_two_pi, k_circle_segments / 2);
				draw_arc(sink, world, bottom, z, y, radius, k_pi, k_two_pi, k_circle_segments / 2);
			}
		}

		void draw_hull(line_sink& sink, const JPH::Mat44& world, JPH::Vec3Arg scale, const JPH::ConvexHullShape& hull)
		{
			const JPH::Vec3 com = hull.GetCenterOfMass();   // hull points are stored relative to the centre of mass
			std::vector<JPH::uint> indices;
			for (JPH::uint face = 0; face < hull.GetNumFaces(); ++face)
			{
				const JPH::uint count = hull.GetNumVerticesInFace(face);
				if (count < 2) continue;
				indices.resize(count);
				hull.GetFaceVertices(face, count, indices.data());
				for (JPH::uint i = 0; i < count; ++i)
				{
					const JPH::uint a = indices[i], b = indices[(i + 1) % count];
					if (a > b) continue;   // every edge belongs to two faces with opposite winding: draw it once
					const JPH::Vec3 pa = world * (scale * (hull.GetPoint(a) + com));
					const JPH::Vec3 pb = world * (scale * (hull.GetPoint(b) + com));
					if (!sink.add(pa, pb)) return;
				}
			}
		}

		// Generic path (meshes, height fields, anything without a dedicated wireframe): Jolt's triangle iterator.
		void draw_triangles(line_sink& sink, const JPH::Shape& shape, const JPH::Mat44& world, JPH::Vec3Arg scale)
		{
			JPH::Shape::GetTrianglesContext context;
			const JPH::Vec3 com_world = world * (scale * shape.GetCenterOfMass());
			shape.GetTrianglesStart(context, JPH::AABox::sBiggest(), com_world, world.GetQuaternion(), scale);

			constexpr int k_batch = 32;   // Shape::cGetTrianglesMinTrianglesRequested
			JPH::Float3 vertices[k_batch * 3];
			for (;;)
			{
				const int count = shape.GetTrianglesNext(context, k_batch, vertices);
				if (count <= 0) return;
				for (int t = 0; t < count; ++t)
				{
					if (sink.body_mesh_floats + 18 > k_mesh_debug_floats_per_body) return;
					const JPH::Vec3 a(vertices[t * 3 + 0]), b(vertices[t * 3 + 1]), c(vertices[t * 3 + 2]);
					if (!sink.add(a, b) || !sink.add(b, c) || !sink.add(c, a)) return;
					sink.body_mesh_floats += 18;
				}
			}
		}

		// `world` maps the shape's ORIGIN space (not its centre of mass) to world space and carries no scale;
		// `scale` is applied to local points before the transform.
		void draw_shape(line_sink& sink, const JPH::Shape* shape, const JPH::Mat44& world, JPH::Vec3Arg scale)
		{
			if (!shape || sink.full()) return;
			switch (shape->GetSubType())
			{
			case JPH::EShapeSubType::Box:
				draw_box(sink, world, scale * static_cast<const JPH::BoxShape*>(shape)->GetHalfExtent());
				break;
			case JPH::EShapeSubType::Sphere:
				draw_sphere(sink, world, JPH::Vec3::sZero(), scale.GetX() * static_cast<const JPH::SphereShape*>(shape)->GetRadius());
				break;
			case JPH::EShapeSubType::Capsule:
			{
				const auto* capsule = static_cast<const JPH::CapsuleShape*>(shape);
				draw_capsule_or_cylinder(sink, world, scale.GetX() * capsule->GetRadius(), scale.GetY() * capsule->GetHalfHeightOfCylinder(), true);
				break;
			}
			case JPH::EShapeSubType::Cylinder:
			{
				const auto* cylinder = static_cast<const JPH::CylinderShape*>(shape);
				draw_capsule_or_cylinder(sink, world, scale.GetX() * cylinder->GetRadius(), scale.GetY() * cylinder->GetHalfHeight(), false);
				break;
			}
			case JPH::EShapeSubType::ConvexHull:
				draw_hull(sink, world, scale, *static_cast<const JPH::ConvexHullShape*>(shape));
				break;
			case JPH::EShapeSubType::StaticCompound:
			case JPH::EShapeSubType::MutableCompound:
			{
				const auto* compound = static_cast<const JPH::CompoundShape*>(shape);
				const JPH::Vec3 com = compound->GetCenterOfMass();
				for (JPH::uint i = 0; i < compound->GetNumSubShapes(); ++i)
				{
					const JPH::CompoundShape::SubShape& sub = compound->GetSubShape(i);
					const JPH::Shape* child = sub.mShape.GetPtr();
					if (!child) continue;
					// sub.GetPositionCOM() is the child's centre of mass relative to the compound's centre of mass
					const JPH::Quat rotation = sub.GetRotation();
					const JPH::Vec3 child_scale = sub.TransformScale(scale);
					const JPH::Mat44 child_world = world
						* JPH::Mat44::sRotationTranslation(rotation, scale * (com + sub.GetPositionCOM()))
						* JPH::Mat44::sTranslation(-(child_scale * child->GetCenterOfMass()));
					draw_shape(sink, child, child_world, child_scale);
				}
				break;
			}
			case JPH::EShapeSubType::Scaled:
			{
				const auto* scaled = static_cast<const JPH::ScaledShape*>(shape);
				draw_shape(sink, scaled->GetInnerShape(), world, scale * scaled->GetScale());
				break;
			}
			case JPH::EShapeSubType::RotatedTranslated:
			{
				const auto* rt = static_cast<const JPH::RotatedTranslatedShape*>(shape);
				const JPH::Quat rotation = rt->GetRotation();
				const JPH::Vec3 inner_scale = JPH::ScaleHelpers::IsUniformScale(scale) ? JPH::Vec3(scale) : JPH::ScaleHelpers::RotateScale(rotation, scale);
				draw_shape(sink, rt->GetInnerShape(), world * JPH::Mat44::sRotationTranslation(rotation, scale * rt->GetPosition()), inner_scale);
				break;
			}
			case JPH::EShapeSubType::OffsetCenterOfMass:
				draw_shape(sink, static_cast<const JPH::OffsetCenterOfMassShape*>(shape)->GetInnerShape(), world, scale);
				break;
			default:
				draw_triangles(sink, *shape, world, scale);
				break;
			}
		}
	}

	// =====================================================================================================
	// World
	// =====================================================================================================

	bool init()
	{
		if (g_world) return true;

		JPH::RegisterDefaultAllocator();
		JPH::Trace = jolt_trace;
		JPH_IF_ENABLE_ASSERTS(JPH::AssertFailed = jolt_assert_failed;)
		if (!JPH::Factory::sInstance) JPH::Factory::sInstance = new JPH::Factory();
		JPH::RegisterTypes();

		g_world = new world();
		log("Jolt Physics world ready (%d worker threads, %u max bodies)", worker_thread_count(), k_max_bodies);
		return true;
	}

	void shutdown()
	{
		if (!g_world) return;
		clear();
		delete g_world;
		g_world = nullptr;

		JPH::UnregisterTypes();
		delete JPH::Factory::sInstance;
		JPH::Factory::sInstance = nullptr;
	}

	bool available() { return g_world != nullptr; }

	void clear()
	{
		world* w = g_world;
		if (!w) return;

		// Characters first: their inner bodies live in the same body manager.
		for (character_slot& slot : w->characters) slot.character = nullptr;
		w->characters.clear();
		w->free_characters.clear();

		JPH::BodyInterface& bodies = w->system.GetBodyInterface();
		for (body_slot& slot : w->bodies)
		{
			if (!slot.used) continue;
			if (bodies.IsAdded(slot.id)) bodies.RemoveBody(slot.id);
			bodies.DestroyBody(slot.id);
		}
		w->bodies.clear();
		w->free_bodies.clear();
		w->live_bodies = 0;
		w->handle_by_body.clear();
		w->graveyard.clear();

		w->listener.clear();
		w->pending.clear();
		w->pending_cursor = 0;
		w->static_bodies_added = false;
	}

	void set_gravity(f32 x, f32 y, f32 z)
	{
		if (!g_world) return;
		g_world->system.SetGravity(JPH::Vec3(sane(x, 0.0f), sane(y, -9.81f), sane(z, 0.0f)));
	}

	void step(f32 dt, s32 collision_steps)
	{
		world* w = g_world;
		if (!w || !finite(dt) || dt <= 0.0f) return;
		if (collision_steps < 1) collision_steps = 1;

		// Static geometry was added since the last step: rebuild the NON_MOVING tree once (Jolt only rebuilds
		// the moving tree incrementally).
		if (w->static_bodies_added)
		{
			w->system.OptimizeBroadPhase();
			w->static_bodies_added = false;
		}

		const JPH::EPhysicsUpdateError error = w->system.Update(dt, collision_steps, &w->temp_allocator, &w->job_system);
		if (error != JPH::EPhysicsUpdateError::None && !w->update_error_logged)
		{
			log("Jolt update reported error flags 0x%X (contact / body pair buffers full - some contacts were dropped)", u32(error));
			w->update_error_logged = true;
		}

		// Forget destroyed bodies once Jolt can no longer raise contact events for them.
		for (auto it = w->graveyard.begin(); it != w->graveyard.end();)
			it = (w->step_index - it->second.step > k_graveyard_steps) ? w->graveyard.erase(it) : std::next(it);
		++w->step_index;
	}

	s32 body_count() { return g_world ? g_world->live_bodies : 0; }

	// =====================================================================================================
	// Bodies
	// =====================================================================================================

	u32 create_body(u64 entity_id, s32 shape_type, const f32* dims, const f32* pos, const f32* quat,
		s32 motion, f32 mass, f32 friction, f32 restitution,
		f32 linear_damping, f32 angular_damping, bool is_trigger, s32 layer,
		u32 lock_flags, f32 gravity_factor)
	{
		if (!g_world) return 0;
		const JPH::ShapeRefC shape = make_primitive(shape_type, dims);
		return add_body(*g_world, entity_id, shape, pos, quat, motion, mass, friction, restitution,
			linear_damping, angular_damping, is_trigger, layer, lock_flags, gravity_factor);
	}

	u32 create_mesh_body(u64 entity_id, const f32* verts, s32 vert_count,
		const u32* indices, s32 index_count, const f32* pos, const f32* quat,
		const f32* scale, bool convex, s32 motion, f32 mass,
		f32 friction, f32 restitution, bool is_trigger, s32 layer)
	{
		if (!g_world || !verts || vert_count < 3) return 0;

		const JPH::Vec3 s = to_vec3(scale, JPH::Vec3::sOne());
		const bool mirrored = (s.GetX() * s.GetY() * s.GetZ()) < 0.0f;   // odd number of negative axes flips the winding

		JPH::ShapeRefC shape;
		if (convex)
		{
			JPH::Array<JPH::Vec3> points;
			points.reserve(size_t(vert_count));
			for (s32 i = 0; i < vert_count; ++i)
			{
				const JPH::Vec3 p = to_vec3(verts + size_t(i) * 3);
				points.push_back(s * p);
			}
			shape = finish(JPH::ConvexHullShapeSettings(points, JPH::cDefaultConvexRadius), "convex hull");
		}
		else
		{
			if (!indices || index_count < 3) return 0;
			JPH::VertexList vertices;
			vertices.reserve(size_t(vert_count));
			for (s32 i = 0; i < vert_count; ++i)
			{
				const JPH::Vec3 p = s * to_vec3(verts + size_t(i) * 3);
				vertices.push_back(JPH::Float3(p.GetX(), p.GetY(), p.GetZ()));
			}
			JPH::IndexedTriangleList triangles;
			triangles.reserve(size_t(index_count / 3));
			for (s32 t = 0; t + 2 < index_count; t += 3)
			{
				const u32 a = indices[t], b = indices[t + 1], c = indices[t + 2];
				if (a >= u32(vert_count) || b >= u32(vert_count) || c >= u32(vert_count)) continue;
				if (mirrored) triangles.push_back(JPH::IndexedTriangle(a, c, b, 0));
				else          triangles.push_back(JPH::IndexedTriangle(a, b, c, 0));
			}
			if (triangles.empty()) return 0;
			JPH::MeshShapeSettings settings(std::move(vertices), std::move(triangles));
			settings.Sanitize();   // drops degenerate / duplicate triangles
			shape = finish(settings, "triangle mesh");
			if (motion != motion_static)
			{
				log("entity %llu: triangle meshes must be static - motion type forced to static", (unsigned long long)entity_id);
				motion = motion_static;
			}
		}

		return add_body(*g_world, entity_id, shape, pos, quat, motion, mass, friction, restitution,
			0.05f, 0.05f, is_trigger, layer, 0, 1.0f);
	}

	u32 create_compound_body(u64 entity_id, const f32* children, s32 count,
		const f32* pos, const f32* quat, s32 motion, f32 mass,
		f32 friction, f32 restitution, f32 linear_damping, f32 angular_damping,
		bool is_trigger, s32 layer, u32 lock_flags, f32 gravity_factor)
	{
		if (!g_world || !children || count < 1) return 0;

		JPH::StaticCompoundShapeSettings compound;
		s32 added = 0;
		for (s32 i = 0; i < count; ++i)
		{
			const f32* child = children + size_t(i) * compound_child_floats;
			const s32 shape_type = s32(sane(child[0], -1.0f));
			const JPH::ShapeRefC shape = make_primitive(shape_type, child + 1);
			if (!shape) continue;
			compound.AddShape(to_vec3(child + 4), to_quat(child + 7), shape.GetPtr());
			++added;
		}
		if (added == 0) return 0;

		const JPH::ShapeRefC shape = finish(compound, "compound");
		return add_body(*g_world, entity_id, shape, pos, quat, motion, mass, friction, restitution,
			linear_damping, angular_damping, is_trigger, layer, lock_flags, gravity_factor);
	}

	void destroy_body(u32 body)
	{
		body_slot* slot = find_body(body);
		if (!slot) return;
		remove_body(*g_world, body, *slot);
	}

	bool body_valid(u32 body) { return find_body(body) != nullptr; }

	u64 body_entity(u32 body)
	{
		const body_slot* slot = find_body(body);
		return slot ? slot->entity : 0;
	}

	void set_body_transform(u32 body, const f32* pos, const f32* quat)
	{
		body_slot* slot = find_body(body);
		if (!slot) return;
		g_world->system.GetBodyInterface().SetPositionAndRotation(slot->id, to_vec3(pos), to_quat(quat), JPH::EActivation::Activate);
	}

	void move_kinematic(u32 body, const f32* pos, const f32* quat, f32 dt)
	{
		body_slot* slot = find_body(body);
		if (!slot) return;
		JPH::BodyInterface& bodies = g_world->system.GetBodyInterface();
		if (slot->motion == motion_static || !finite(dt) || dt <= 0.0f)
		{
			bodies.SetPositionAndRotation(slot->id, to_vec3(pos), to_quat(quat), JPH::EActivation::Activate);
			return;
		}
		bodies.MoveKinematic(slot->id, to_vec3(pos), to_quat(quat), dt);
	}

	bool get_body_transform(u32 body, f32* out_pos, f32* out_quat)
	{
		const body_slot* slot = find_body(body);
		if (!slot) return false;
		JPH::RVec3 position;
		JPH::Quat rotation;
		g_world->system.GetBodyInterface().GetPositionAndRotation(slot->id, position, rotation);
		store3(out_pos, JPH::Vec3(position));
		store4(out_quat, rotation);
		return true;
	}

	void set_linear_velocity(u32 body, const f32* v)
	{
		if (const body_slot* slot = find_body(body))
			g_world->system.GetBodyInterface().SetLinearVelocity(slot->id, to_vec3(v));
	}

	void get_linear_velocity(u32 body, f32* out_v)
	{
		const body_slot* slot = find_body(body);
		store3(out_v, slot ? g_world->system.GetBodyInterface().GetLinearVelocity(slot->id) : JPH::Vec3::sZero());
	}

	void set_angular_velocity(u32 body, const f32* v)
	{
		if (const body_slot* slot = find_body(body))
			g_world->system.GetBodyInterface().SetAngularVelocity(slot->id, to_vec3(v));
	}

	void get_angular_velocity(u32 body, f32* out_v)
	{
		const body_slot* slot = find_body(body);
		store3(out_v, slot ? g_world->system.GetBodyInterface().GetAngularVelocity(slot->id) : JPH::Vec3::sZero());
	}

	void add_force(u32 body, const f32* f)
	{
		if (const body_slot* slot = find_body(body))
			g_world->system.GetBodyInterface().AddForce(slot->id, to_vec3(f));
	}

	void add_force_at_point(u32 body, const f32* f, const f32* world_point)
	{
		if (const body_slot* slot = find_body(body))
			g_world->system.GetBodyInterface().AddForce(slot->id, to_vec3(f), to_vec3(world_point));
	}

	void add_impulse(u32 body, const f32* impulse)
	{
		if (const body_slot* slot = find_body(body))
			g_world->system.GetBodyInterface().AddImpulse(slot->id, to_vec3(impulse));
	}

	void add_impulse_at_point(u32 body, const f32* impulse, const f32* world_point)
	{
		if (const body_slot* slot = find_body(body))
			g_world->system.GetBodyInterface().AddImpulse(slot->id, to_vec3(impulse), to_vec3(world_point));
	}

	void add_torque(u32 body, const f32* torque)
	{
		if (const body_slot* slot = find_body(body))
			g_world->system.GetBodyInterface().AddTorque(slot->id, to_vec3(torque));
	}

	void set_motion_type(u32 body, s32 motion)
	{
		body_slot* slot = find_body(body);
		if (!slot) return;
		motion = clamp_motion(motion);
		if (slot->must_be_static && motion != motion_static)
		{
			log("entity %llu: shape must be static - set_motion_type ignored", (unsigned long long)slot->entity);
			return;
		}
		JPH::BodyInterface& bodies = g_world->system.GetBodyInterface();
		bodies.SetMotionType(slot->id, to_motion_type(motion), motion == motion_static ? JPH::EActivation::DontActivate : JPH::EActivation::Activate);
		slot->motion = u8(motion);

		// Keep the STATIC (NON_MOVING) layer for static bodies only; STATIC and DYNAMIC have the same partners.
		if (motion != motion_static && slot->layer == layer_static)
		{
			slot->layer = u8(layer_dynamic);
			bodies.SetObjectLayer(slot->id, JPH::ObjectLayer(layer_dynamic));
		}
		else if (motion == motion_static && slot->layer == layer_dynamic)
		{
			slot->layer = u8(layer_static);
			bodies.SetObjectLayer(slot->id, JPH::ObjectLayer(layer_static));
			g_world->static_bodies_added = true;
		}
	}

	void set_gravity_factor(u32 body, f32 factor)
	{
		if (const body_slot* slot = find_body(body))
			g_world->system.GetBodyInterface().SetGravityFactor(slot->id, sane(factor, 1.0f));
	}

	void set_friction(u32 body, f32 friction)
	{
		if (const body_slot* slot = find_body(body))
			g_world->system.GetBodyInterface().SetFriction(slot->id, std::max(sane(friction, 0.5f), 0.0f));
	}

	void set_restitution(u32 body, f32 restitution)
	{
		if (const body_slot* slot = find_body(body))
			g_world->system.GetBodyInterface().SetRestitution(slot->id, std::max(sane(restitution, 0.0f), 0.0f));
	}

	void set_damping(u32 body, f32 linear, f32 angular)
	{
		const body_slot* slot = find_body(body);
		if (!slot) return;
		JPH::BodyLockWrite lock(g_world->system.GetBodyLockInterface(), slot->id);
		if (!lock.Succeeded()) return;
		JPH::MotionProperties* mp = lock.GetBody().GetMotionPropertiesUnchecked();
		if (!mp) return;
		mp->SetLinearDamping(std::max(sane(linear, 0.05f), 0.0f));
		mp->SetAngularDamping(std::max(sane(angular, 0.05f), 0.0f));
	}

	void set_active(u32 body, bool active)
	{
		const body_slot* slot = find_body(body);
		if (!slot) return;
		JPH::BodyInterface& bodies = g_world->system.GetBodyInterface();
		if (active) bodies.ActivateBody(slot->id);
		else        bodies.DeactivateBody(slot->id);
	}

	bool is_active(u32 body)
	{
		const body_slot* slot = find_body(body);
		return slot && g_world->system.GetBodyInterface().IsActive(slot->id);
	}

	f32 get_mass(u32 body)
	{
		const body_slot* slot = find_body(body);
		if (!slot) return 0.0f;
		JPH::BodyLockRead lock(g_world->system.GetBodyLockInterface(), slot->id);
		if (!lock.Succeeded()) return 0.0f;
		const JPH::Body& b = lock.GetBody();
		if (b.IsStatic()) return 0.0f;
		const JPH::MotionProperties* mp = b.GetMotionPropertiesUnchecked();
		const f32 inverse = mp ? mp->GetInverseMassUnchecked() : 0.0f;
		return inverse > 0.0f ? 1.0f / inverse : 0.0f;
	}

	// =====================================================================================================
	// Queries
	// =====================================================================================================

	bool raycast(const f32* origin, const f32* dir, f32 max_dist, u32 layer_mask,
		f32* out_point, f32* out_normal, f32* out_dist, u64* out_entity, u32* out_body)
	{
		const world* w = g_world;
		if (!w || !origin || !dir || !finite(max_dist) || max_dist <= 0.0f) return false;
		JPH::Vec3 direction = to_vec3(dir);
		if (direction.LengthSq() < 1.0e-12f) return false;
		direction = direction.Normalized();

		const JPH::RRayCast ray(to_vec3(origin), direction * max_dist);
		JPH::RayCastResult hit;
		const mask_bp_filter bp_filter(layer_mask);
		const mask_object_filter object_filter(layer_mask);
		if (!w->system.GetNarrowPhaseQuery().CastRay(ray, hit, bp_filter, object_filter)) return false;

		const JPH::Vec3 point = JPH::Vec3(ray.GetPointOnRay(hit.mFraction));
		JPH::Vec3 normal = -direction;
		u64 entity = 0;
		{
			JPH::BodyLockRead lock(w->system.GetBodyLockInterface(), hit.mBodyID);
			if (lock.Succeeded())
			{
				const JPH::Body& body = lock.GetBody();
				normal = body.GetWorldSpaceSurfaceNormal(hit.mSubShapeID2, point);
				entity = body.GetUserData();
			}
		}

		store3(out_point, point);
		store3(out_normal, normal);
		if (out_dist) *out_dist = hit.mFraction * max_dist;
		if (out_entity) *out_entity = entity;
		if (out_body) *out_body = handle_of(*w, hit.mBodyID);
		return true;
	}

	s32 overlap_sphere(const f32* center, f32 radius, u32 layer_mask,
		u64* out_entities, u32* out_bodies, s32 max_count)
	{
		const world* w = g_world;
		if (!w || !center || !finite(radius) || radius <= 0.0f || max_count <= 0) return 0;

		JPH::SphereShape sphere(radius);
		sphere.SetEmbedded();   // stack-allocated shape: opt out of reference counting
		JPH::AllHitCollisionCollector<JPH::CollideShapeCollector> collector;
		const JPH::CollideShapeSettings settings;
		const mask_bp_filter bp_filter(layer_mask);
		const mask_object_filter object_filter(layer_mask);
		w->system.GetNarrowPhaseQuery().CollideShape(&sphere, JPH::Vec3::sOne(), JPH::Mat44::sTranslation(to_vec3(center)),
			settings, JPH::RVec3::sZero(), collector, bp_filter, object_filter);

		// One entry per body (compound / mesh shapes report several hits per body).
		std::vector<u32> seen;
		s32 written = 0;
		for (const JPH::CollideShapeResult& hit : collector.mHits)
		{
			const u32 key = hit.mBodyID2.GetIndexAndSequenceNumber();
			if (std::find(seen.begin(), seen.end(), key) != seen.end()) continue;
			seen.push_back(key);
			if (written >= max_count) break;
			if (out_entities) out_entities[written] = w->system.GetBodyInterface().GetUserData(hit.mBodyID2);
			if (out_bodies) out_bodies[written] = handle_of(*w, hit.mBodyID2);
			++written;
		}
		return written;
	}

	s32 get_contacts(contact_event* buffer, s32 max_count)
	{
		world* w = g_world;
		if (!w) return 0;

		// Pull what the worker threads captured since the last call, then bound the backlog.
		w->listener.drain(w->pending);
		if (w->pending.size() - w->pending_cursor > k_max_pending_contacts)
		{
			w->pending.erase(w->pending.begin(), w->pending.end() - std::ptrdiff_t(k_max_pending_contacts));
			w->pending_cursor = 0;
		}
		if (!buffer || max_count <= 0) return 0;

		s32 written = 0;
		while (written < max_count && w->pending_cursor < w->pending.size())
		{
			const queued_contact& q = w->pending[w->pending_cursor++];
			contact_event& e = buffer[written++];

			u32 handle_a, handle_b;
			u64 entity_a, entity_b;
			bool trigger_a, trigger_b;
			resolve_body(*w, q.body_a, handle_a, entity_a, trigger_a);
			resolve_body(*w, q.body_b, handle_b, entity_b, trigger_b);

			e.entity_a = q.kind == contact_removed ? entity_a : q.entity_a;
			e.entity_b = q.kind == contact_removed ? entity_b : q.entity_b;
			e.body_a = handle_a;
			e.body_b = handle_b;
			e.point[0] = q.point[0]; e.point[1] = q.point[1]; e.point[2] = q.point[2];
			e.normal[0] = q.normal[0]; e.normal[1] = q.normal[1]; e.normal[2] = q.normal[2];
			e.impulse = q.impulse;
			e.kind = q.kind;
			e.is_trigger = q.is_trigger >= 0 ? q.is_trigger : ((trigger_a || trigger_b) ? 1 : 0);
		}

		if (w->pending_cursor >= w->pending.size())
		{
			w->pending.clear();
			w->pending_cursor = 0;
		}
		return written;
	}

	// =====================================================================================================
	// Character controller (CharacterVirtual)
	// =====================================================================================================

	u32 character_create(f32 radius, f32 height, const f32* pos, f32 max_slope_deg, f32 step_height, f32 mass)
	{
		world* w = g_world;
		if (!w) return 0;

		radius = std::max(sane(radius, 0.35f), 0.05f);
		height = std::max(sane(height, 1.8f), 2.0f * radius);
		const f32 cylinder_half = 0.5f * (height - 2.0f * radius);

		// Capsule along Y, shifted so the character's position is at its feet.
		JPH::ShapeRefC capsule = cylinder_half > 1.0e-4f
			? JPH::ShapeRefC(new JPH::CapsuleShape(cylinder_half, radius))
			: JPH::ShapeRefC(new JPH::SphereShape(radius));
		const JPH::ShapeRefC shape = finish(JPH::RotatedTranslatedShapeSettings(JPH::Vec3(0.0f, 0.5f * height, 0.0f), JPH::Quat::sIdentity(), capsule.GetPtr()), "character");
		if (!shape) return 0;

		JPH::CharacterVirtualSettings settings;
		settings.mShape = shape;
		settings.mMaxSlopeAngle = JPH::DegreesToRadians(std::clamp(sane(max_slope_deg, 45.0f), 0.0f, 89.0f));
		settings.mMass = sane(mass, 70.0f) > 0.0f ? sane(mass, 70.0f) : 70.0f;
		settings.mSupportingVolume = JPH::Plane(JPH::Vec3::sAxisY(), -radius);   // only the lower hemisphere counts as ground contact
		settings.mInnerBodyShape = shape;                                          // a kinematic body so rigid bodies / queries see the character
		settings.mInnerBodyLayer = JPH::ObjectLayer(layer_character);

		character_slot* slot = nullptr;
		const u32 handle = allocate_slot(w->characters, w->free_characters, slot);
		if (!slot) return 0;
		slot->character = new JPH::CharacterVirtual(&settings, to_vec3(pos), JPH::Quat::sIdentity(), 0, &w->system);
		slot->step_height = std::max(sane(step_height, 0.3f), 0.0f);
		return handle;
	}

	void character_destroy(u32 character)
	{
		character_slot* slot = find_character(character);
		if (!slot) return;
		release_slot(g_world->characters, g_world->free_characters, handle_index(character));
	}

	void character_set_position(u32 character, const f32* pos)
	{
		if (character_slot* slot = find_character(character))
			slot->character->SetPosition(to_vec3(pos));
	}

	void character_move(u32 character, const f32* desired_velocity, f32 dt,
		f32* out_pos, f32* out_velocity, s32* out_grounded, f32* out_ground_normal)
	{
		character_slot* slot = find_character(character);
		if (!slot) return;
		world* w = g_world;
		JPH::CharacterVirtual& c = *slot->character;

		c.SetLinearVelocity(to_vec3(desired_velocity));
		if (finite(dt) && dt > 0.0f)
		{
			JPH::CharacterVirtual::ExtendedUpdateSettings settings;
			settings.mWalkStairsStepUp = JPH::Vec3(0.0f, slot->step_height, 0.0f);
			c.ExtendedUpdate(dt, w->system.GetGravity(), settings,
				w->system.GetDefaultBroadPhaseLayerFilter(JPH::ObjectLayer(layer_character)),
				w->system.GetDefaultLayerFilter(JPH::ObjectLayer(layer_character)),
				JPH::BodyFilter{}, JPH::ShapeFilter{}, w->temp_allocator);
		}

		store3(out_pos, JPH::Vec3(c.GetPosition()));
		store3(out_velocity, c.GetLinearVelocity());
		if (out_grounded) *out_grounded = c.GetGroundState() == JPH::CharacterBase::EGroundState::OnGround ? 1 : 0;
		store3(out_ground_normal, c.GetGroundNormal());
	}

	// =====================================================================================================
	// Debug
	// =====================================================================================================

	s32 get_debug_lines(f32* buffer, s32 max_floats)
	{
		const world* w = g_world;
		if (!w || !buffer || max_floats < 6) return 0;

		line_sink sink{ buffer, max_floats };
		const JPH::BodyLockInterface& lock_interface = w->system.GetBodyLockInterface();
		for (const body_slot& slot : w->bodies)
		{
			if (!slot.used) continue;
			JPH::BodyLockRead lock(lock_interface, slot.id);
			if (!lock.Succeeded()) continue;
			const JPH::Body& body = lock.GetBody();
			sink.body_mesh_floats = 0;
			draw_shape(sink, body.GetShape(), JPH::Mat44(body.GetWorldTransform()), JPH::Vec3::sOne());
			if (sink.full()) break;
		}
		for (const character_slot& slot : w->characters)
		{
			if (!slot.used || !slot.character) continue;
			sink.body_mesh_floats = 0;
			const JPH::Mat44 world_transform = JPH::Mat44::sRotationTranslation(slot.character->GetRotation(), JPH::Vec3(slot.character->GetPosition()));
			draw_shape(sink, slot.character->GetShape(), world_transform, JPH::Vec3::sOne());
			if (sink.full()) break;
		}
		return sink.written;
	}
}

#else // !VORTEX_HAS_JOLT

// ============================================================================
// Stub: no Jolt in this build (the Visual Studio projects, or -DVORTEX_ENABLE_JOLT=OFF). init() reports
// "no physics" and everything else is a harmless no-op so the managed side keeps its legacy behaviour.
// ============================================================================

namespace vortex::physics {

	namespace {
		void zero3(f32* out) { if (out) out[0] = out[1] = out[2] = 0.0f; }
	}

	bool init() { return false; }
	void shutdown() {}
	bool available() { return false; }
	void clear() {}
	void set_gravity(f32, f32, f32) {}
	void step(f32, s32) {}
	s32  body_count() { return 0; }

	u32 create_body(u64, s32, const f32*, const f32*, const f32*, s32, f32, f32, f32, f32, f32, bool, s32, u32, f32) { return 0; }
	u32 create_mesh_body(u64, const f32*, s32, const u32*, s32, const f32*, const f32*, const f32*, bool, s32, f32, f32, f32, bool, s32) { return 0; }
	u32 create_compound_body(u64, const f32*, s32, const f32*, const f32*, s32, f32, f32, f32, f32, f32, bool, s32, u32, f32) { return 0; }
	void destroy_body(u32) {}
	bool body_valid(u32) { return false; }
	u64  body_entity(u32) { return 0; }

	void set_body_transform(u32, const f32*, const f32*) {}
	void move_kinematic(u32, const f32*, const f32*, f32) {}
	bool get_body_transform(u32, f32*, f32*) { return false; }
	void set_linear_velocity(u32, const f32*) {}
	void get_linear_velocity(u32, f32* out_v) { zero3(out_v); }
	void set_angular_velocity(u32, const f32*) {}
	void get_angular_velocity(u32, f32* out_v) { zero3(out_v); }
	void add_force(u32, const f32*) {}
	void add_force_at_point(u32, const f32*, const f32*) {}
	void add_impulse(u32, const f32*) {}
	void add_impulse_at_point(u32, const f32*, const f32*) {}
	void add_torque(u32, const f32*) {}
	void set_motion_type(u32, s32) {}
	void set_gravity_factor(u32, f32) {}
	void set_friction(u32, f32) {}
	void set_restitution(u32, f32) {}
	void set_damping(u32, f32, f32) {}
	void set_active(u32, bool) {}
	bool is_active(u32) { return false; }
	f32  get_mass(u32) { return 0.0f; }

	bool raycast(const f32*, const f32*, f32, u32, f32*, f32*, f32*, u64*, u32*) { return false; }
	s32  overlap_sphere(const f32*, f32, u32, u64*, u32*, s32) { return 0; }
	s32  get_contacts(contact_event*, s32) { return 0; }

	u32  character_create(f32, f32, const f32*, f32, f32, f32) { return 0; }
	void character_destroy(u32) {}
	void character_set_position(u32, const f32*) {}
	void character_move(u32, const f32*, f32, f32*, f32*, s32*, f32*) {}

	s32  get_debug_lines(f32*, s32) { return 0; }
}

#endif // VORTEX_HAS_JOLT
