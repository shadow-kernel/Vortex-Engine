# EngineTest — console harnesses. Main.cpp picks the test through TEST_AUDIO / TEST_ECS / TEST_RENDER / TEST_PHYSICS /
# TEST_NAVIGATION;
# each test lives in its own header, so only Main.cpp is compiled per executable.
add_executable(EngineTest Main.cpp)
target_include_directories(EngineTest PRIVATE "${CMAKE_SOURCE_DIR}/Engine/Common")
target_compile_definitions(EngineTest PRIVATE TEST_AUDIO=1)
target_link_libraries(EngineTest PRIVATE vortex::engine)

# Opens a native window, renders a lit scene through the platform backend and captures a frame.
add_executable(VortexRenderTest Main.cpp)
target_include_directories(VortexRenderTest PRIVATE "${CMAKE_SOURCE_DIR}/Engine/Common")
target_compile_definitions(VortexRenderTest PRIVATE TEST_RENDER=1)
target_link_libraries(VortexRenderTest PRIVATE vortex::engine)

# Physics v2 smoke test: Jolt world, rigid bodies, raycast, contact events, quaternion convention, character.
# Without Jolt (VORTEX_ENABLE_JOLT=OFF) it verifies the stub contract instead.
add_executable(VortexPhysicsTest Main.cpp)
target_include_directories(VortexPhysicsTest PRIVATE "${CMAKE_SOURCE_DIR}/Engine/Common")
target_compile_definitions(VortexPhysicsTest PRIVATE TEST_PHYSICS=1)
target_link_libraries(VortexPhysicsTest PRIVATE vortex::engine)

# VFX (#116): particle simulation, emission, bursts, lifetime, determinism, JSON, beams, collision (headless).
add_executable(VortexParticleTest Main.cpp)
target_include_directories(VortexParticleTest PRIVATE "${CMAKE_SOURCE_DIR}/Engine/Common")
target_compile_definitions(VortexParticleTest PRIVATE TEST_PARTICLES=1)
target_link_libraries(VortexParticleTest PRIVATE vortex::engine)
# AI & Navigation smoke test (#109/#110): Recast bake (triangles + solid boxes, tiled), paths around obstacles and up a
# ramp, queries, .vnav round trip, DetourCrowd agents. Without Recast (VORTEX_ENABLE_RECAST=OFF) the stub contract.
add_executable(VortexNavigationTest Main.cpp)
target_include_directories(VortexNavigationTest PRIVATE "${CMAKE_SOURCE_DIR}/Engine/Common")
target_compile_definitions(VortexNavigationTest PRIVATE TEST_NAVIGATION=1)
target_link_libraries(VortexNavigationTest PRIVATE vortex::engine)

# Primitive geometry (#77 follow-up): every triangle of cube / plane / sphere / cylinder / cone faces outward (headless).
add_executable(VortexGeometryTest Main.cpp)
target_include_directories(VortexGeometryTest PRIVATE "${CMAKE_SOURCE_DIR}/Engine/Common")
target_compile_definitions(VortexGeometryTest PRIVATE TEST_GEOMETRY=1)
target_link_libraries(VortexGeometryTest PRIVATE vortex::engine)

# Model import (#441 #450 #453 #470): small glTF / OBJ files through the Assimp importer and the import cache (headless).
add_executable(VortexImportTest Main.cpp)
target_include_directories(VortexImportTest PRIVATE "${CMAKE_SOURCE_DIR}/Engine/Common")
target_compile_definitions(VortexImportTest PRIVATE TEST_IMPORT=1)
target_link_libraries(VortexImportTest PRIVATE vortex::engine)

# Render budgets (#415, #416): the bone-palette sharing / budget and the light selection the renderers run, headless.
add_executable(VortexRenderBudgetTest Main.cpp)
target_include_directories(VortexRenderBudgetTest PRIVATE "${CMAKE_SOURCE_DIR}/Engine/Common")
target_compile_definitions(VortexRenderBudgetTest PRIVATE TEST_RENDER_BUDGETS=1)
target_link_libraries(VortexRenderBudgetTest PRIVATE vortex::engine)

if(NOT MSVC)
	target_compile_options(VortexRenderBudgetTest PRIVATE -Wall -Wno-unused-parameter)
	target_compile_options(VortexGeometryTest PRIVATE -Wall -Wno-unused-parameter)
	target_compile_options(VortexImportTest PRIVATE -Wall -Wno-unused-parameter)
	target_compile_options(VortexParticleTest PRIVATE -Wall -Wno-unused-parameter)
	target_compile_options(EngineTest PRIVATE -Wall -Wno-unused-parameter)
	target_compile_options(VortexRenderTest PRIVATE -Wall -Wno-unused-parameter)
	target_compile_options(VortexPhysicsTest PRIVATE -Wall -Wno-unused-parameter)
	target_compile_options(VortexNavigationTest PRIVATE -Wall -Wno-unused-parameter)
endif()

# Runs the audio smoke test: opens the default output device, or falls back to silent mode on CI.
add_test(NAME AudioSmokeTest COMMAND EngineTest)
set_tests_properties(AudioSmokeTest PROPERTIES
	FAIL_REGULAR_EXPRESSION "FAILURES!"
	PASS_REGULAR_EXPRESSION "ALL PASSED"
	TIMEOUT 600)

add_test(NAME ParticleSmokeTest COMMAND VortexParticleTest)
set_tests_properties(ParticleSmokeTest PROPERTIES
	FAIL_REGULAR_EXPRESSION "FAILURES!"
	PASS_REGULAR_EXPRESSION "ALL PASSED"
	TIMEOUT 120)

add_test(NAME PhysicsSmokeTest COMMAND VortexPhysicsTest)
set_tests_properties(PhysicsSmokeTest PROPERTIES
	FAIL_REGULAR_EXPRESSION "FAILURES!"
	PASS_REGULAR_EXPRESSION "ALL PASSED"
	TIMEOUT 120)

add_test(NAME GeometryTest COMMAND VortexGeometryTest)
set_tests_properties(GeometryTest PROPERTIES
	FAIL_REGULAR_EXPRESSION "FAILURES!"
	PASS_REGULAR_EXPRESSION "ALL PASSED"
	TIMEOUT 60)

add_test(NAME RenderBudgetTest COMMAND VortexRenderBudgetTest)
set_tests_properties(RenderBudgetTest PROPERTIES
	FAIL_REGULAR_EXPRESSION "FAILURES!"
	PASS_REGULAR_EXPRESSION "ALL PASSED"
	TIMEOUT 60)

add_test(NAME NavigationSmokeTest COMMAND VortexNavigationTest)
set_tests_properties(NavigationSmokeTest PROPERTIES
	FAIL_REGULAR_EXPRESSION "FAILURES!"
	PASS_REGULAR_EXPRESSION "ALL PASSED"
	TIMEOUT 120)

add_test(NAME ImportTest COMMAND VortexImportTest)
set_tests_properties(ImportTest PROPERTIES
	FAIL_REGULAR_EXPRESSION "FAILURES!"
	PASS_REGULAR_EXPRESSION "ALL PASSED"
	TIMEOUT 60)

# Gamepads for the .NET hosts: VortexAPI's PollGamepad driven by an SDL virtual gamepad (headless; SDL builds only).
if(TARGET VortexAPI AND NOT VORTEX_ENABLE_DX12)
	add_executable(VortexGamepadTest TestGamepad.cpp)
	target_link_libraries(VortexGamepadTest PRIVATE VortexAPI vortex::engine)   # the engine carries SDL3 publicly
	add_test(NAME GamepadTest COMMAND VortexGamepadTest)
	set_tests_properties(GamepadTest PROPERTIES
		FAIL_REGULAR_EXPRESSION "FAILURES!"
		PASS_REGULAR_EXPRESSION "ALL PASSED"
		TIMEOUT 60)
endif()

# The render test needs a display (a window server): opt in with -DVORTEX_RENDER_TESTS=ON.
option(VORTEX_RENDER_TESTS "Register the windowed render smoke test with ctest" OFF)
if(VORTEX_RENDER_TESTS)
	add_test(NAME RenderSmokeTest COMMAND VortexRenderTest)
	set_tests_properties(RenderSmokeTest PROPERTIES
		FAIL_REGULAR_EXPRESSION "FAILURES!"
		PASS_REGULAR_EXPRESSION "ALL PASSED"
		TIMEOUT 120)
endif()

# Game entities (headless): unique ids also on reused slots, each transform on its own entity, removing entities (a
# whole scene's worth, in any order) never touching another, mesh-renderer slots handed out once.
add_executable(VortexEntityTest TestEntities.cpp)
target_include_directories(VortexEntityTest PRIVATE "${CMAKE_SOURCE_DIR}/Engine/Common")
target_link_libraries(VortexEntityTest PRIVATE vortex::engine)
if(NOT MSVC)
	target_compile_options(VortexEntityTest PRIVATE -Wall -Wno-unused-parameter)
endif()
add_test(NAME EntityTest COMMAND VortexEntityTest)
set_tests_properties(EntityTest PROPERTIES
	FAIL_REGULAR_EXPRESSION "FAILURES!"
	PASS_REGULAR_EXPRESSION "ALL PASSED"
	TIMEOUT 120)
