# ============================================================================
# Vortex Engine — cross-platform native build (CMake)
#
# This is the build for macOS / Linux and the future single build for Windows.
# The Visual Studio projects (Vortex.slnx, *.vcxproj) remain the Windows build
# of record until the CMake path has been verified there; see
# ROADMAP_MACOS_PORT.md for the port plan and the per-phase status.
#
#   cmake --preset macos-debug          # configure (Ninja, clang, arm64)
#   cmake --build --preset macos-debug  # build VortexEngine + libVortexAPI + EngineTest
#   ctest --preset macos-debug          # run the audio smoke test
# ============================================================================
cmake_minimum_required(VERSION 3.25)

# Deployment target: left at the SDK default (= the host macOS) for development builds, because Homebrew
# bottles are built for the host version and the linker warns about anything older. Shipping builds set
# -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0 (std::filesystem needs 10.15, Metal 3 needs 13) and build assimp
# from source (vcpkg / FetchContent) instead of Homebrew.

project(VortexEngine LANGUAGES CXX)

list(APPEND CMAKE_MODULE_PATH "${CMAKE_CURRENT_SOURCE_DIR}/cmake")

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)       # the static engine is linked into the VortexAPI shared library
set(CMAKE_CXX_VISIBILITY_PRESET hidden)       # only VORTEX_API_EXPORT symbols leave libVortexAPI
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)         # clangd / IDE support

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
	set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

# Everything (test exe, shared library, runtime DLLs) lands in one folder, like x64/<Config> does on Windows.
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib")

# ---- Options ---------------------------------------------------------------
option(VORTEX_BUILD_API   "Build the VortexAPI shared library (the editor / player interop layer)" ON)
option(VORTEX_BUILD_TESTS "Build EngineTest (audio smoke test / ECS stress test)" ON)

# The DirectX 12 backend only exists on Windows. Everywhere else the engine core builds without a renderer
# until the cross-platform backend lands (ROADMAP_MACOS_PORT.md, Phase 1-2).
if(WIN32)
	set(VORTEX_ENABLE_DX12_DEFAULT ON)
else()
	set(VORTEX_ENABLE_DX12_DEFAULT OFF)
endif()
option(VORTEX_ENABLE_DX12 "Build the DirectX 12 render backend (Windows only)" ${VORTEX_ENABLE_DX12_DEFAULT})
if(VORTEX_ENABLE_DX12 AND NOT WIN32)
	message(FATAL_ERROR "VORTEX_ENABLE_DX12 requires Windows.")
endif()

# Physics v2 (issues #100-#107): Jolt Physics is fetched from GitHub and linked into the engine. OFF builds the
# no-physics stub of Engine/Physics/PhysicsWorld.cpp (PhysicsInit() returns 0), which is also what the Visual
# Studio projects build today. See Engine/Physics/README.md.
option(VORTEX_ENABLE_JOLT "Build the Jolt Physics backend (Physics v2); OFF builds the no-op physics stub" ON)

# AI & Navigation (issues #108-#115): Recast/Detour (recastnavigation) is fetched from GitHub and linked into the
# engine. OFF builds the no-navigation stub of Engine/Navigation/NavigationWorld.cpp (NavInit() returns 0), which is
# also what the Visual Studio projects build today. See Engine/Navigation/NavigationWorld.h.
option(VORTEX_ENABLE_RECAST "Build the Recast/Detour navigation backend (navmesh + crowd); OFF builds the no-op navigation stub" ON)

include(VortexDependencies)

add_subdirectory(Engine)
if(VORTEX_BUILD_API)
	add_subdirectory(VortexAPI)
endif()
if(VORTEX_BUILD_TESTS)
	enable_testing()
	add_subdirectory(EngineTest)
endif()

message(STATUS "Vortex: platform=${CMAKE_SYSTEM_NAME} arch=${CMAKE_SYSTEM_PROCESSOR} dx12=${VORTEX_ENABLE_DX12} jolt=${VORTEX_ENABLE_JOLT} recast=${VORTEX_ENABLE_RECAST} api=${VORTEX_BUILD_API} tests=${VORTEX_BUILD_TESTS}")
