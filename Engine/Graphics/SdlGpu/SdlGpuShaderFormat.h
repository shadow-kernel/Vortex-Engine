#pragma once
// The shader format the SDL GPU backend consumes on this platform.
//
// Metal compiles MSL at load time, so one .metal source file serves every entrypoint in it and SDL is given
// the entrypoint name. Vulkan consumes SPIR-V, where a module carries exactly one entrypoint called `main`,
// so the build compiles one .spv per entrypoint and encodes which in the file name: "<base>.<Entry>.spv"
// (see Engine/Shaders/glsl and the vortex_compile_shaders CMake step).
//
// Call sites therefore name a shader by its SET ("standard", "grid", "skybox", "postfx", "ssao", "bloom",
// "overlay", "particles") plus an entrypoint, and this header resolves both to a file and an SDL entrypoint.

#include "../../Common/Platform.h"

#include <SDL3/SDL_gpu.h>
#include <string>

namespace vortex::graphics::sdlgpu::shaderfmt
{
#if VORTEX_PLATFORM_APPLE
	inline constexpr SDL_GPUShaderFormat format         = SDL_GPU_SHADERFORMAT_MSL;
	inline constexpr const char*         directory      = "msl";       // Engine/Shaders/<directory>
	inline constexpr const char*         suffix         = ".metal";
	inline constexpr bool                per_entrypoint = false;       // one source file, many entrypoints
	inline constexpr const char*         probe           = "standard.metal";
	inline constexpr bool                is_text        = true;        // SDL wants a NUL-terminated string
#else
	inline constexpr SDL_GPUShaderFormat format         = SDL_GPU_SHADERFORMAT_SPIRV;
	inline constexpr const char*         directory      = "spirv";
	inline constexpr const char*         suffix         = ".spv";
	inline constexpr bool                per_entrypoint = true;        // one module per entrypoint
	inline constexpr const char*         probe           = "standard.PSMain.spv";
	inline constexpr bool                is_text        = false;       // SPIR-V is a binary blob
#endif

	// The file inside the shader directory that holds `entry` of the shader set `base`.
	inline std::string module_file(const std::string& base, const char* entry)
	{
		return per_entrypoint ? base + "." + entry + suffix : base + suffix;
	}

	// What SDL must be told the entrypoint is called: glslc always emits `main`.
	inline const char* entrypoint(const char* entry) { return per_entrypoint ? "main" : entry; }

	// Source extension for a project's own material shader (hot-reloadable, compiled at run time like the
	// DX12 backend compiles .hlsl): .metal on Metal, .glsl on Vulkan.
	inline constexpr const char* material_suffix = per_entrypoint ? ".glsl" : ".metal";
}
