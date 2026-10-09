# VortexAPI — the extern "C" interop layer the editor / player call through P/Invoke.
# Windows: VortexAPI.dll   macOS: libVortexAPI.dylib   Linux: libVortexAPI.so
#
# Every translation unit talks to the renderer through graphics::Renderer (Graphics/Backend.h), so the
# whole API builds against the DX12 backend on Windows and the SDL GPU backend elsewhere.

set(VORTEX_API_SOURCES
	MultiViewportAPI.cpp
	Api/AnimationApi.cpp
	Api/AssetDatabaseApi.cpp
	Api/AudioApi.cpp
	Api/CameraApi.cpp
	Api/EntityApi.cpp
	Api/GameHostApi.cpp
	Api/ImporterApi.cpp
	Api/InputApi.cpp
	Api/DecalApi.cpp
	Api/LightingApi.cpp
	Api/MeshRendererApi.cpp
	Api/ParticleApi.cpp
	Api/NavigationApi.cpp
	Api/PhysicsApi.cpp
	Api/RenderApi.cpp
	Api/RenderLoopApi.cpp
	Api/ResourceApi.cpp
	Api/RuntimeApi.cpp
	Api/SceneApi.cpp
	Api/SkyboxApi.cpp
)

add_library(VortexAPI SHARED ${VORTEX_API_SOURCES})
if(WIN32)
	target_sources(VortexAPI PRIVATE DllMain.cpp)   # CRT debug hooks; Windows only
endif()

target_include_directories(VortexAPI PRIVATE "${CMAKE_SOURCE_DIR}/Engine/Common")
target_compile_definitions(VortexAPI PRIVATE VORTEXAPI_EXPORTS VORTEX_CMAKE_BUILD $<$<BOOL:${WIN32}>:_WINDOWS _USRDLL>)
target_link_libraries(VortexAPI PRIVATE vortex::engine)

# The .NET side loads "VortexAPI": keep the base name identical on every platform.
set_target_properties(VortexAPI PROPERTIES OUTPUT_NAME "VortexAPI")

if(MSVC)
	target_compile_options(VortexAPI PRIVATE /W3 /permissive-)
else()
	target_compile_options(VortexAPI PRIVATE -Wall -Wno-unused-parameter)
	# A missing engine symbol must fail the build here, not at P/Invoke time (Apple's ld already defaults
	# to -undefined error for dynamic libraries).
	if(NOT APPLE)
		target_link_options(VortexAPI PRIVATE "-Wl,--no-undefined")
	endif()
endif()
