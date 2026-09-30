# Physics v2 — Jolt Physics module

Native side of the Physics v2 epic (GitHub #100) — issues #101 (vendor Jolt + native bridge), #102 (rigid
body dynamics + script API), #103 (constraints / joints), #105 (character controller v2), #106 (debug draw) and
#107 (compound colliders).

| Piece | Where |
|---|---|
| Engine module (the world) | `Engine/Physics/PhysicsWorld.h` / `.cpp`, namespace `vortex::physics` |
| C ABI exported by `libVortexAPI` / `VortexAPI.dll` | `VortexAPI/Api/PhysicsApi.cpp` (`Physics*` functions) |
| Managed mirror | `Editor/DllWrapper/VortexAPI.Physics.cs`, `Editor/Core/Services/Physics/PhysicsService.cs` |
| Smoke test | `EngineTest/TestPhysics.h` (`VortexPhysicsTest`, ctest `PhysicsSmokeTest`) |
| Build | `VORTEX_ENABLE_JOLT` (root `CMakeLists.txt`), FetchContent block in `Engine/CMakeLists.txt` |

The toy AABB system in `Engine/Runtime/Systems/PhysicsSystem.{h,cpp}` is untouched and still drives the
legacy play-mode behaviour; the managed side switches to this module when `PhysicsInit()` returns 1.

## Architecture

`PhysicsWorld.cpp` owns one lazily created `world` (a plain pointer, created in `init()`, so there is no static
initialisation order to worry about):

* `JPH::PhysicsSystem` with 32 768 max bodies / 65 536 body pairs / 10 240 contact constraints, a
  `JPH::TempAllocatorImpl` of 16 MB and a `JPH::JobSystemThreadPool` with `cores - 1` workers. (The contact
  constraint buffer is carved out of the temp allocator every step — 864 bytes each — which is what bounds it.)
* Layer plumbing (`BroadPhaseLayerInterface`, `ObjectVsBroadPhaseLayerFilter`, `ObjectLayerPairFilter`) plus two
  query-time filters that turn a caller-supplied layer mask into Jolt filters.
* A `ContactListener` whose callbacks (raised from Jolt worker threads during `Update`) push raw events into a
  mutex-protected vector; `get_contacts()` drains that vector on the calling thread and resolves body handles.
* A body slot table (`handle = generation << 16 | slot + 1`, 0 = invalid) mapping handles to `JPH::BodyID`,
  entity id, layer, motion type and trigger flag, with a reverse map `BodyID -> handle` and a small "graveyard"
  so that the `removed` contact events Jolt raises in the update *after* a body was destroyed still report its
  handle and entity id.
* A character slot table of `JPH::CharacterVirtual` instances.
* A constraint (joint) slot table of `JPH::TwoBodyConstraint`s with their body handles, break force, last force and
  broken flag, plus the queue of joints that broke (see "Joints" below).
* Shape and joint wireframe extraction for the editor gizmo layer.

`VortexAPI/Api/PhysicsApi.cpp` is a 1:1 pass-through: every `Physics*` export calls the identically named
`vortex::physics::` function; no logic lives in the API layer. `InitializeRuntime()` / `ShutdownRuntime()`
(`RuntimeApi.cpp`) bring the world up and down next to the legacy physics system; `init()` is idempotent so the
managed `PhysicsService` may call `PhysicsInit()` again. `StepRuntime` does **not** step Jolt — the managed side
calls `PhysicsStep` from its own fixed-step accumulator so script callbacks and contact events line up with the
game loop.

## The ABI (contract)

All entry points are `extern "C"` + `EDITOR_INTERFACE`, plain C types only. The full list with semantics is in
`PhysicsApi.cpp`; the header `PhysicsWorld.h` carries the same functions in snake_case.

* **Units / space**: metres, kilograms, seconds; default gravity (0, -9.81, 0). Engine coordinates are passed
  to Jolt unchanged (a mirrored world is a valid Newtonian world), nothing is flipped anywhere.
* **Quaternions**: `float[4] = {x, y, z, w}`, Hamilton convention (`v' = q v q*`), i.e. what `JPH::Quat`,
  `System.Numerics.Quaternion` and `Vector3.Transform(v, q)` use. Verified by `TestPhysics.h`: the yaw +90°
  quaternion `(0, sin 45°, 0, cos 45°)` rotates (1,0,0) to (0,0,-1), both directly in `JPH::Quat` and through a
  compound body whose +X child ends up at -Z.
* **Handles**: bodies and characters are `uint32_t`, 0 = invalid. Stale handles (destroyed body, reused slot)
  are rejected by the generation check — every function silently no-ops / returns 0 on an invalid handle.
* **Entity ids**: `uint64_t`, stored as Jolt body user data; queries and contact events report them.
* **Motion**: 0 static, 1 kinematic, 2 dynamic. **Shapes**: 0 box (dims = half extents), 1 sphere (radius),
  2 capsule (radius, half height of the cylinder part), 3 cylinder (radius, half height). Dimensions are clamped
  to >= 1 mm; a capsule with half height 0 becomes a sphere; convex radii are shrunk for tiny shapes.
* **Mass**: `mass <= 0` → Jolt's density-based mass; `mass > 0` → `MassProperties::ScaleToMass` (inertia
  scaled to the given mass, `EOverrideMassProperties::MassAndInertiaProvided`).
* **Lock flags** (bitmask): bit0-2 lock position X/Y/Z, bit3-5 lock rotation X/Y/Z — the same bit layout as
  `JPH::EAllowedDOFs`. Locking all six is rejected (logged) because Jolt cannot simulate a body with no DOFs.
* **Contacts**: `PhysicsContact` = `{entityA, entityB, bodyA, bodyB, point[3], normal[3] (A→B), impulse,
  kind (0 added / 1 persisted / 2 removed), isTrigger}`. Events are reported per Jolt sub-shape pair (one per body
  pair for primitive shapes); persisted events come every step; `impulse` is an estimate (approach velocity ×
  effective mass — Jolt only exposes solver impulses indirectly). `PhysicsGetContacts` writes up to `maxCount`
  events, removes exactly those from the queue and returns the count; loop until the count is smaller than the
  buffer to drain everything. The backlog is capped at 65 536 events (oldest dropped).
* **Character controller**: `PhysicsCharacterCreate(radius, height, feetPos, maxSlopeDeg, stepHeight, mass)`
  builds a `CharacterVirtual` with a capsule whose origin is at the feet, a supporting volume that only accepts
  the lower hemisphere and an inner kinematic body in the CHARACTER layer (so rigid bodies and queries see the
  character). `PhysicsCharacterMove` sets the desired velocity (horizontal move + vertical from gravity/jump —
  the controller does not add gravity itself) and runs `ExtendedUpdate` (stick-to-floor 0.5 m, stair step =
  `stepHeight`). `outVelocity` is the velocity the controller used (desired velocity with components into steep
  slopes cancelled); when `outGrounded` is 1 the game should reset its vertical velocity before the next call.
  The character is moved in `PhysicsCharacterMove`, independent of `PhysicsStep`; call both once per fixed step.
* **Debug lines**: `PhysicsGetDebugLines` fills `x0 y0 z0 x1 y1 z1` segments of every enabled joint first (see
  "Joints"), then every body's shape (boxes: 12 edges; spheres: 3 circles; capsules: rings + edges + cap arcs;
  cylinders: rings + edges; convex hulls: face edges; meshes and everything else: Jolt's triangle iterator, capped at
  20 000 floats per body) plus the character capsules, and returns the number of floats written (whole segments only).

## Joints (constraints, issue #103)

| Export | Jolt constraint | Notes |
|---|---|---|
| `PhysicsCreateHinge(bodyA, bodyB, pivot, axis, normal, minDeg, maxDeg, useLimits, motorTargetVel, motorMaxTorque, breakForce)` | `HingeConstraint` | limits min ∈ [-180, 0], max ∈ [0, 180] (clamped; min == max is widened by 1 mrad); `motorMaxTorque > 0` starts a velocity motor (deg/s, 0 = friction) |
| `PhysicsCreateBallJoint(bodyA, bodyB, point, twistAxis, swingLimitDeg, twistMinDeg, twistMaxDeg, useLimits, breakForce)` | `PointConstraint` / `SwingTwistConstraint` (with limits) | cone half angle around `twistAxis` (null = from the point towards bodyA's centre of mass) |
| `PhysicsCreateSlider(bodyA, bodyB, point, axis, minPos, maxPos, useLimits, motorMode, motorTarget, motorMaxForce, springFrequency, springDamping, breakForce)` | `SliderConstraint` | limits in metres relative to the creation pose (min ≤ 0 ≤ max, clamped) |
| `PhysicsCreateFixed(bodyA, bodyB, point /*null = auto*/, breakForce)` | `FixedConstraint` | keeps the current relative pose |
| `PhysicsCreateDistance(bodyA, bodyB, pointA, pointB, minDistance, maxDistance, springFrequency, springDamping, breakForce)` | `DistanceConstraint` | negative min / max = the distance at creation; min 0 = a rope that can go slack; spring > 0 = soft limits |
| `PhysicsDestroyConstraint`, `PhysicsConstraintValid`, `PhysicsSetConstraintEnabled` (1 also repairs a broken joint), `PhysicsGetConstraintEnabled`, `PhysicsGetConstraintCount` | | |
| `PhysicsSetHingeMotor` / `PhysicsSetSliderMotor(joint, mode, target, maxTorque or maxForce, frequency, damping)` | motor state + `MotorSettings` | mode 0 off / 1 velocity (deg/s, m/s) / 2 position (deg, m; spring frequency ≤ 0 → 2 Hz, damping < 0 → 1); limit ≤ 0 = unlimited |
| `PhysicsGetHingeAngle` (deg) / `PhysicsGetSliderPosition` (m) / `PhysicsGetConstraintForce` (N, last step) | `GetCurrentAngle` / `GetCurrentPosition` / Lagrange multipliers | |
| `PhysicsGetBrokenConstraints(uint* joints, float* forces /*may be null*/, maxCount)` | | drains the break queue, returns the count |

* **Bodies / space**: `bodyA` is the jointed body (a valid handle), `bodyB` the connected body or 0 = the world
  (`Body::sFixedToWorld`). Jolt's body 1 is the CONNECTED body and body 2 the jointed one, so Jolt's "body 2 relative
  to body 1" angle / position is the jointed body's. Everything is `EConstraintSpace::WorldSpace` with identical
  frames for both bodies, so **the pose at creation is the rest pose** (hinge angle 0, slider position 0, weld pose).
  The hinge `normal` only picks where angle 0 is drawn (perpendicularised; null / parallel = any perpendicular).
* **Angles** are degrees (API-facing, like the inspector), positions metres, forces N, torques N·m. The hinge angle is
  right-handed about the axis (yaw +90° about +Y turns (1,0,0) to (0,0,-1), as everywhere else).
* **Breakable joints**: after every `PhysicsStep` the module reads each solved joint's positional Lagrange
  multipliers (impulse of the last solver sub-step, N·s) and divides by the sub-step length: the pivot force of
  hinges / ball joints / welds, the perpendicular + limit force of sliders, the axial force of ropes (torques are not
  included). `breakForce > 0` and a force above it disables the joint, wakes its bodies and queues `{handle, force}`
  for `PhysicsGetBrokenConstraints`. The handle stays valid; re-enabling repairs (warm start reset so stale
  multipliers can't re-break it). `PhysicsGetConstraintForce` exposes the same number for tuning.
* **Connected bodies don't collide**: while a joint between two real bodies is enabled, `OnContactValidate` rejects
  that pair (reference-counted pair table, written only between steps; contact caches of the pair are invalidated on
  every change). Disabled / broken joints stop filtering.
* **Solver iterations**: joints set `mNumVelocityStepsOverride = 30` / `mNumPositionStepsOverride = 10` (Jolt defaults
  10 / 2) — Jolt raises only the iterations of islands that contain a joint. With the defaults a door slammed against
  its limit overshot by 2.6°, bounced back ~16° and its pivot opened by 17 mm; now < 0.1° / 0 / 0.4 mm.
* **Lifetime**: destroying a body destroys its joints first (Jolt keeps raw body pointers); `PhysicsClear` removes all.
* **Debug draw**: anchor marker (plus a line to bodyA's attachment point when they drift apart — the rope of a distance
  joint), hinge axis + limit arc + current-angle spoke, slider travel range with limit ticks, swing cone of limited
  ball joints, weld arms to the centres of mass.
* **Stub**: every joint export returns 0 / no-ops (checked by `VortexPhysicsTest` in the stub build).

## Layers

| Object layer | Value | Broadphase | Collides with |
|---|---|---|---|
| STATIC | 0 | NON_MOVING | DYNAMIC, CHARACTER, TRIGGER, DEBRIS |
| DYNAMIC | 1 | MOVING | everything |
| CHARACTER | 2 | MOVING | STATIC, DYNAMIC, CHARACTER, TRIGGER |
| TRIGGER | 3 | MOVING | everything except TRIGGER |
| DEBRIS | 4 | MOVING | STATIC, DYNAMIC, TRIGGER |

The matrix is a `constexpr` table in `PhysicsWorld.cpp` with a `static_assert` that it is symmetric (Jolt
requires that). Query layer masks use bit i for layer i (`~0u` = everything).

Two normalisations keep Jolt's broadphase honest: a body with `isTrigger` is always placed in TRIGGER (and is a
Jolt sensor — contacts are reported, no response), and a moving (kinematic/dynamic) body that asks for STATIC is
placed in DYNAMIC, because NON_MOVING is only rebuilt by `OptimizeBroadPhase` (which the module runs before the
next step whenever static bodies were added). `PhysicsSetMotionType` moves a body between STATIC and DYNAMIC
accordingly — the two layers have identical partners, so this is invisible to gameplay. Triangle-mesh bodies are
always static (Jolt's `MustBeStatic`); asking for another motion type is logged and ignored.

## Threading

Jolt raises contact callbacks from its worker threads while `PhysicsStep` runs. The listener only does local
math and a mutex-protected `push_back`; the queue is handed out on the calling thread by `PhysicsGetContacts`.
All other API calls must come from the single engine/game thread (the managed side already guarantees this).
Jolt's `Trace` is routed to `platform::debug_output` with a `[jolt]` prefix; in Debug builds Jolt enables its
asserts (`JPH_DEBUG`) even with `USE_ASSERTS OFF`, so the module installs an assert handler that logs and
continues instead of trapping.

## The stub (no Jolt in the build)

`PhysicsWorld.cpp` is split with `#if VORTEX_HAS_JOLT`. Without the macro (the Visual Studio projects, or
`-DVORTEX_ENABLE_JOLT=OFF`) the `#else` branch compiles: `init()` returns `false` (`PhysicsInit()` → 0),
`available()` is `false`, every creator returns 0, every query returns 0/false, and every setter is a no-op.
The export list of `VortexAPI` is identical in both builds, so the managed P/Invoke declarations never change;
`PhysicsService` probes `PhysicsInit()` once, logs, and keeps the legacy behaviour. `VortexPhysicsTest` detects
the stub and verifies exactly this contract instead of the simulation.

## Build

* CMake (macOS/Linux, the future Windows build): `option(VORTEX_ENABLE_JOLT ... ON)` in the root
  `CMakeLists.txt`. `Engine/CMakeLists.txt` declares `FetchContent_Declare(JoltPhysics GIT_REPOSITORY
  https://github.com/jrouwe/JoltPhysics.git GIT_TAG v5.3.0 SOURCE_SUBDIR Build)` (shallow clone), forces Jolt's
  option variables into the cache **before** `FetchContent_MakeAvailable` (Jolt's `Build/CMakeLists.txt` reads
  them as it is added): `OVERRIDE_CXX_FLAGS OFF` (essential — otherwise Jolt rewrites `CMAKE_CXX_FLAGS_<CONFIG>`
  for its directory with its own `-Werror` set), `CROSS_PLATFORM_DETERMINISTIC OFF`, `INTERPROCEDURAL_OPTIMIZATION
  OFF`, `ENABLE_ALL_WARNINGS OFF`, `USE_ASSERTS OFF`, `DOUBLE_PRECISION OFF`, `GENERATE_DEBUG_SYMBOLS ON`,
  `CPP_EXCEPTIONS_ENABLED OFF`, `CPP_RTTI_ENABLED OFF`, `OBJECT_LAYER_BITS 16`, `USE_STATIC_MSVC_RUNTIME_LIBRARY
  OFF`, `ENABLE_OBJECT_STREAM OFF`, `TARGET_UNIT_TESTS/HELLO_WORLD/PERFORMANCE_TEST/SAMPLES/VIEWER OFF`. The
  `Jolt` target is linked `PUBLIC` into `VortexEngine` (a static library — its consumers need Jolt at link time
  and must see the same `JPH_*` configuration macros) and `VORTEX_HAS_JOLT=1` is defined `PUBLIC`.
  Jolt's `-fno-rtti` / `-fno-exceptions` only affect its own directory scope; the only options it propagates are
  x86 ISA flags (irrelevant on arm64) and `-pthread`.
* Sources: `Engine/Physics/PhysicsWorld.cpp` is in `VORTEX_ENGINE_CORE_SOURCES`; `VortexAPI/Api/PhysicsApi.cpp`
  is in `VORTEX_API_SOURCES`; `EngineTest/CMakeLists.txt` builds `VortexPhysicsTest` (`TEST_PHYSICS=1`) and
  registers `PhysicsSmokeTest` with ctest.

```
cmake --preset macos-debug && cmake --build --preset macos-debug
./build/macos-debug/bin/VortexPhysicsTest          # or: ctest --preset macos-debug -R PhysicsSmokeTest
nm -gU build/macos-debug/bin/libVortexAPI.dylib | grep -c ' _Physics'   # 57 exports (41 + 16 joint exports)
```

### Enabling Jolt in the Visual Studio build (later)

`Engine.vcxproj` / `VortexAPI.vcxproj` / `EngineTest.vcxproj` list the new files and build the stub because
`VORTEX_HAS_JOLT` is not defined there. To switch the Windows build of record to Jolt:

1. Build Jolt v5.3.0 for MSVC once (e.g. `cmake -S Build -B out -G "Visual Studio 17 2022" -A x64` in a Jolt
   checkout with the option list above, or via vcpkg `joltphysics`), producing `Jolt.lib` for Debug and Release
   with the **same** options as the CMake build — `OBJECT_LAYER_BITS=16`, single precision, no RTTI/exceptions,
   dynamic MSVC runtime (`USE_STATIC_MSVC_RUNTIME_LIBRARY OFF`, matching `/MD`/`/MDd` of the engine).
2. In `Engine.vcxproj`: add the Jolt checkout root to `AdditionalIncludeDirectories`, and add
   `VORTEX_HAS_JOLT=1;JPH_OBJECT_LAYER_BITS=16` to `PreprocessorDefinitions` (Debug additionally
   `JPH_DEBUG_RENDERER;JPH_PROFILE_ENABLED` if the Jolt build had them; `_DEBUG` / `NDEBUG` must match). The ISA
   defines must match the library build as well (`JPH_USE_AVX2`, `JPH_USE_SSE4_1`, ... — Jolt's `Jolt.cmake`
   prints the set it uses); the Jolt headers check consistency at runtime in `RegisterTypes()`.
3. In `VortexAPI.vcxproj` (and `EngineTest.vcxproj`): link `Jolt.lib` (Debug/Release variants) and add the same
   preprocessor definitions, because both include `PhysicsWorld.h` through the engine and `TestPhysics.h`
   includes `<Jolt/Jolt.h>` directly.
4. Nothing in the managed side changes; `PhysicsInit()` starts returning 1.

The long-term plan (ROADMAP_MACOS_PORT.md) is the single CMake build on Windows, where all of this is already
handled by `VORTEX_ENABLE_JOLT`.

## Known limitations / follow-ups

* Contact events are per Jolt sub-shape pair. A body pair with several manifolds (compound or mesh bodies)
  raises several `added`/`removed` events; pair-level enter/exit needs a counter on the managed side (or here).
* Sensors detect active bodies only (Jolt semantics); a character's inner body is moved as a kinematic body and
  will register with triggers that are DYNAMIC-facing; sleeping bodies inside a trigger do not re-report.
* `PhysicsGetDebugLines` walks every body every call; call it only while the gizmo layer is visible.
* Physics materials (#107) are represented by per-body friction/restitution; per-child materials of compounds are
  not yet exposed.
* Jolt engages joint limits only once they are exceeded (no speculative limits). Hinge and slider limits are
  re-evaluated in the position solve, so the extra position iterations correct them within the step; a distance
  joint caches its points at the start of the step, so a slack rope that snaps taut overshoots by up to one step of
  motion (v · dt, ~3 cm at 2 m/s) and is back on length the next step (`TestPhysics.h` (j) checks both).
* Break forces measure linear force only; a separate break torque is not implemented.
