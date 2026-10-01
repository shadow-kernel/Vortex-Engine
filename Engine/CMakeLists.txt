# VortexEngine — the static engine library that links into VortexAPI and into shipped games.
#
# Sources are split by portability. The CORE set builds on every platform; the DX12 set is the Windows
# render backend plus the resource classes that are still typed against ID3D12* (Phase 1 of the port
# moves them behind a backend-neutral interface).

set(VORTEX_ENGINE_CORE_SOURCES
	Common/Platform.cpp
	Components/Camera.cpp
	Components/Entity.cpp
	Components/MeshRenderer.cpp
	Components/Skybox.cpp
	Components/Transform.cpp
	Graphics/Geometry/ConeGenerator.cpp
	Graphics/Geometry/CubeGenerator.cpp
	Graphics/Geometry/CylinderGenerator.cpp
	Graphics/Geometry/MeshDecimator.cpp
	Graphics/Geometry/MeshGeneratorFactory.cpp
	Graphics/Geometry/PlaneGenerator.cpp
	Graphics/Geometry/SphereGenerator.cpp
	Graphics/Importers/MaterialSerializer.cpp
	Graphics/Importers/MeshSerializer.cpp
	Graphics/Importers/ModelImporter.cpp
	Graphics/Importers/ModelImporter_Process.cpp
	Graphics/Importers/TextureImporter.cpp
	Graphics/Particles/ParticleJson.cpp
	Graphics/Particles/ParticleSystem.cpp
	Input/InputSystem.cpp
	Navigation/NavigationBake.cpp
	Navigation/NavigationWorld.cpp
	Physics/PhysicsWorld.cpp
	Runtime/AssetDatabase.cpp
	Runtime/AssetManifest.cpp
	Runtime/PrefabService.cpp
	Runtime/RenderLoop.cpp
	Runtime/ResourceManager.cpp
	Runtime/SceneManager.cpp
	Runtime/Systems/AudioEngine.cpp
	Runtime/Systems/AudioMixer.cpp
	Runtime/Systems/AudioReverb.cpp
	Runtime/Systems/AudioSystem.cpp
	Runtime/Systems/AudioVoices.cpp
	Runtime/Systems/PhysicsSystem.cpp
	Runtime/Systems/RenderBackend.cpp
	Runtime/Systems/RenderSystem.cpp
	Runtime/Systems/SteamAudio.cpp
)

# The SDL GPU backend: Metal on macOS (MSL shaders compiled by the driver at load), Vulkan on Linux later.
set(VORTEX_ENGINE_SDLGPU_SOURCES
	Graphics/SdlGpu/SdlGpuOverlay.cpp
	Graphics/SdlGpu/SdlGpuParticles.cpp
	Graphics/SdlGpu/SdlGpuRenderer.cpp
	Graphics/SdlGpu/SdlGpuRenderer_Scene.cpp
	Graphics/SdlGpu/SdlGpuRenderer_Shadows.cpp
	Graphics/SdlGpu/SdlGpuRenderer_PostFx.cpp
	Graphics/SdlGpu/SdlGpuResources.cpp
	Graphics/SdlGpu/StbTrueType.cpp
	Runtime/GameHost_Sdl.cpp
)

set(VORTEX_ENGINE_DX12_SOURCES
	Graphics/DX12/CullWorkerPool.h
	Graphics/DX12/DX12CommandQueue.cpp
	Graphics/DX12/DX12Core.cpp
	Graphics/DX12/DX12DepthBuffer.cpp
	Graphics/DX12/DX12Geometry.cpp
	Graphics/DX12/DX12GridPipeline.cpp
	Graphics/DX12/DX12MotionVectorPipeline.cpp
	Graphics/DX12/DX12Pipeline.cpp
	Graphics/DX12/DX12Pipeline3D.cpp
	Graphics/DX12/DX12PostFxChain.cpp
	Graphics/DX12/DX12Renderer.cpp
	Graphics/DX12/DX12Renderer_3DScene.cpp
	Graphics/DX12/DX12Renderer_Grid.cpp
	Graphics/DX12/DX12Renderer_Init.cpp
	Graphics/DX12/DX12Renderer_Lights.cpp
	Graphics/DX12/DX12Renderer_Queue.cpp
	Graphics/DX12/DX12Renderer_RenderTargets.cpp
	Graphics/DX12/DX12Renderer_Shadows.cpp
	Graphics/DX12/DX12Renderer_Skybox.cpp
	Graphics/DX12/DX12Renderer_Ssao.cpp
	Graphics/DX12/DX12RenderTarget.cpp
	Graphics/DX12/DX12ShaderCompiler.cpp
	Graphics/DX12/DX12SkyboxPipeline.cpp
	Graphics/DX12/DX12Streamline.cpp
	Graphics/DX12/DX12Swapchain.cpp
	Graphics/DX12/DX12UpscalePipeline.cpp
	Graphics/DX12/UIOverlay.cpp
	Graphics/Resources/Material.cpp
	Graphics/Resources/Mesh.cpp
	Graphics/Resources/ResourceRegistry.cpp
	Graphics/Resources/ResourceRegistry_Import.cpp
	Graphics/Resources/ResourceRegistry_Lod.cpp
	Graphics/Resources/ResourceRegistry_Materials.cpp
	Graphics/Resources/ResourceRegistry_Textures.cpp
	Graphics/Resources/Texture.cpp
	Runtime/GameHost.cpp
	Runtime/Systems/RenderSystemDX12.cpp
)

add_library(VortexEngine STATIC ${VORTEX_ENGINE_CORE_SOURCES})
add_library(vortex::engine ALIAS VortexEngine)

target_include_directories(VortexEngine
	PUBLIC
		"${CMAKE_CURRENT_SOURCE_DIR}"            # #include "Engine/..." style from dependants
		"${CMAKE_CURRENT_SOURCE_DIR}/Common"     # #include "CommonHeaders.h" (what the .vcxproj files do)
	PRIVATE
		"${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty" # miniaudio, stb
)

target_compile_definitions(VortexEngine
	PUBLIC
		VORTEX_USE_ASSIMP
		$<$<CONFIG:Debug>:_DEBUG>                # MSVC defines this for the debug CRT; Id.h / asserts key off it
)

target_link_libraries(VortexEngine
	PUBLIC
		vortex::directxmath
		vortex::assimp
		vortex::steamaudio
		Threads::Threads
		${CMAKE_DL_LIBS}
)

if(VORTEX_ENABLE_DX12)
	target_sources(VortexEngine PRIVATE ${VORTEX_ENGINE_DX12_SOURCES})
	target_include_directories(VortexEngine PRIVATE "${CMAKE_SOURCE_DIR}/ThirdParty/Streamline/include")
	target_compile_definitions(VortexEngine PUBLIC WIN32 _LIB UNICODE _UNICODE)
	target_link_libraries(VortexEngine PUBLIC d3d12 dxgi d3d11 d2d1 dwrite windowscodecs dxguid ole32)
else()
	# macOS / Linux: the SDL GPU backend. brew install sdl3 (macOS) / libsdl3-dev (Linux).
	find_package(SDL3 CONFIG REQUIRED)
	target_sources(VortexEngine PRIVATE ${VORTEX_ENGINE_SDLGPU_SOURCES})
	target_compile_definitions(VortexEngine PUBLIC VORTEX_NO_DX12 VORTEX_HAS_SDLGPU=1)
	target_link_libraries(VortexEngine PUBLIC SDL3::SDL3)
	if(NOT MSVC)
		set_source_files_properties(Graphics/SdlGpu/StbTrueType.cpp PROPERTIES COMPILE_OPTIONS "-w")
	endif()
endif()

# ---- Jolt Physics (Physics v2, issues #100/#101) -------------------------------------------------------
# Vendored with FetchContent (pinned tag). Jolt's Build/CMakeLists.txt reads its option variables while it is
# being added, so every one of them is forced into the cache BEFORE FetchContent_MakeAvailable. OVERRIDE_CXX_FLAGS
# OFF is essential: with it ON Jolt rewrites CMAKE_CXX_FLAGS_<CONFIG> for its directory with its own -Werror set.
# The remaining options turn off everything that is not the library (tests, samples, viewer, RTTI, exceptions,
# object streams) and pin the ABI-relevant ones (JPH_OBJECT_LAYER_BITS=16, single precision, no asserts).
# See Engine/Physics/README.md for how the stub (VORTEX_ENABLE_JOLT=OFF / the .vcxproj build) behaves.
if(VORTEX_ENABLE_JOLT)
	include(FetchContent)
	FetchContent_Declare(JoltPhysics
		GIT_REPOSITORY https://github.com/jrouwe/JoltPhysics.git
		GIT_TAG        v5.3.0
		GIT_SHALLOW    TRUE
		SOURCE_SUBDIR  Build)

	set(_vortex_jolt_options
		OVERRIDE_CXX_FLAGS                OFF
		CROSS_PLATFORM_DETERMINISTIC      OFF
		INTERPROCEDURAL_OPTIMIZATION      OFF
		ENABLE_ALL_WARNINGS               OFF
		USE_ASSERTS                       OFF
		DOUBLE_PRECISION                  OFF
		GENERATE_DEBUG_SYMBOLS            ON
		CPP_EXCEPTIONS_ENABLED            OFF
		CPP_RTTI_ENABLED                  OFF
		OBJECT_LAYER_BITS                 16
		USE_STATIC_MSVC_RUNTIME_LIBRARY   OFF
		ENABLE_OBJECT_STREAM              OFF
		TARGET_UNIT_TESTS                 OFF
		TARGET_HELLO_WORLD                OFF
		TARGET_PERFORMANCE_TEST           OFF
		TARGET_SAMPLES                    OFF
		TARGET_VIEWER                     OFF)
	list(LENGTH _vortex_jolt_options _vortex_jolt_option_count)
	math(EXPR _vortex_jolt_last "${_vortex_jolt_option_count} - 1")
	foreach(_i RANGE 0 ${_vortex_jolt_last} 2)
		math(EXPR _j "${_i} + 1")
		list(GET _vortex_jolt_options ${_i} _name)
		list(GET _vortex_jolt_options ${_j} _value)
		if(_name STREQUAL "OBJECT_LAYER_BITS")
			set(${_name} ${_value} CACHE STRING "Jolt: set by Vortex (Engine/CMakeLists.txt)" FORCE)
		else()
			set(${_name} ${_value} CACHE BOOL "Jolt: set by Vortex (Engine/CMakeLists.txt)" FORCE)
		endif()
	endforeach()

	FetchContent_MakeAvailable(JoltPhysics)

	# The engine is a static library, so its consumers (libVortexAPI, EngineTest) link Jolt through it and see
	# the same JPH_* configuration macros Jolt was compiled with.
	target_link_libraries(VortexEngine PUBLIC Jolt)
	target_compile_definitions(VortexEngine PUBLIC VORTEX_HAS_JOLT=1)
	message(STATUS "Vortex: Jolt Physics v5.3.0 enabled (${joltphysics_SOURCE_DIR})")
endif()

# ---- Recast/Detour (AI & Navigation, issues #108-#110) ---------------------------------------------------
# recastnavigation (zlib licence, THIRD-PARTY-NOTICES.md) vendored with FetchContent at a pinned tag. Only the
# libraries are built: Recast (voxelisation -> polygon mesh), Detour (tiled navmesh + queries) and DetourCrowd
# (steering + local avoidance). The demo / tests / examples are switched off BEFORE the subdirectory is added.
# Its CMakeLists still declares cmake_minimum_required(3.1), which CMake >= 4 refuses without a policy floor:
# CMAKE_POLICY_VERSION_MINIMUM is set for the duration of the add only. 32-bit poly refs (default) — the ABI
# (VortexAPI/Api/NavigationApi.cpp) never exposes a dtPolyRef, so this can change without touching managed code.
# Without it (-DVORTEX_ENABLE_RECAST=OFF, the .vcxproj build) Engine/Navigation compiles the no-op stub.
if(VORTEX_ENABLE_RECAST)
	include(FetchContent)
	FetchContent_Declare(recastnavigation
		GIT_REPOSITORY https://github.com/recastnavigation/recastnavigation.git
		GIT_TAG        v1.6.0
		GIT_SHALLOW    TRUE)

	foreach(_opt RECASTNAVIGATION_DEMO RECASTNAVIGATION_TESTS RECASTNAVIGATION_EXAMPLES
			RECASTNAVIGATION_DT_POLYREF64 RECASTNAVIGATION_DT_VIRTUAL_QUERYFILTER)
		set(${_opt} OFF CACHE BOOL "recastnavigation: set by Vortex (Engine/CMakeLists.txt)" FORCE)
	endforeach()
	set(_vortex_saved_policy_min "${CMAKE_POLICY_VERSION_MINIMUM}")
	set(_vortex_saved_warn_deprecated "${CMAKE_WARN_DEPRECATED}")
	set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
	set(CMAKE_WARN_DEPRECATED OFF)
	FetchContent_MakeAvailable(recastnavigation)
	set(CMAKE_POLICY_VERSION_MINIMUM "${_vortex_saved_policy_min}")
	set(CMAKE_WARN_DEPRECATED "${_vortex_saved_warn_deprecated}")

	# Third-party code: no warnings from it in our build log.
	foreach(_rc_target Recast Detour DetourCrowd)
		if(TARGET ${_rc_target})
			if(MSVC)
				target_compile_options(${_rc_target} PRIVATE /w)
			else()
				target_compile_options(${_rc_target} PRIVATE -w)
			endif()
		endif()
	endforeach()

	target_link_libraries(VortexEngine PUBLIC Recast Detour DetourCrowd)
	target_compile_definitions(VortexEngine PUBLIC VORTEX_HAS_RECAST=1)
	message(STATUS "Vortex: Recast/Detour v1.6.0 enabled (${recastnavigation_SOURCE_DIR})")
endif()

# The Metal shaders are loaded from Shaders/msl next to the binaries at run time (the DX12 backend loads its
# .hlsl the same way on Windows): mirror the source tree's Shaders folder into the output folder.
add_custom_command(TARGET VortexEngine POST_BUILD
	COMMAND ${CMAKE_COMMAND} -E copy_directory "${CMAKE_CURRENT_SOURCE_DIR}/Shaders" "${CMAKE_BINARY_DIR}/bin/Shaders"
	COMMENT "Copying shaders next to the binaries")

if(APPLE)
	# miniaudio's Core Audio backend (it dlopen's the frameworks itself, linking them is belt and braces)
	target_link_libraries(VortexEngine PUBLIC
		"-framework CoreFoundation" "-framework CoreAudio" "-framework AudioToolbox" "-framework AudioUnit"
		objc)   # objc_msgSend: the SDL GPU backend marks a host NSWindow opaque before wrapping its view
endif()

if(MSVC)
	target_compile_options(VortexEngine PRIVATE /W3 /permissive- /Zc:__cplusplus)
else()
	target_compile_options(VortexEngine PRIVATE -Wall -Wno-unused-parameter -Wno-unused-function -Wno-missing-braces)
	# Single-header third-party code (miniaudio + stb_vorbis live in AudioEngine.cpp, stb_image in TextureImporter.cpp)
	# Optimised even in Debug: an unoptimised stb_image needs seconds per 4K PNG, which made a debug editor take
	# ~20 s to open a scene with PBR materials.
	set_source_files_properties(Runtime/Systems/AudioEngine.cpp Graphics/Importers/TextureImporter.cpp
		PROPERTIES COMPILE_OPTIONS "-w;-O2")
	# The particle simulation is a hot loop over up to 10^5-10^6 particles per frame: keep it optimised in Debug
	# builds too (an -O0 build is ~8x slower there), like a physics engine that is always built optimised.
	set_source_files_properties(Graphics/Particles/ParticleSystem.cpp
		PROPERTIES COMPILE_OPTIONS "$<$<CONFIG:Debug>:-O2>")
endif()
