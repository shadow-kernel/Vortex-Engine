#pragma once

// ============================================================================
// Render backend selection.
//
// Code outside the backends (VortexAPI, runtime systems, components) talks to
// the renderer through these aliases:
//
//   graphics::Renderer            the renderer singleton class (instance())
//   graphics::backend::RenderItem / ViewportCamera / MAX_*   shared value types
//   graphics::ResourceRegistry / Mesh / Texture / Material   GPU resources
//
// Windows: the DirectX 12 backend (Graphics/DX12, Graphics/Resources).
// macOS / Linux: the SDL GPU backend (Graphics/SdlGpu) — Metal on macOS,
// Vulkan on Linux. Both backends expose the same method names, so the code
// that uses these aliases compiles unchanged against either.
// ============================================================================
#include "../Common/Platform.h"

#if VORTEX_HAS_DX12
	#include "DX12/DX12Renderer.h"
	#include "DX12/DX12Core.h"
	#include "Resources/ResourceRegistry.h"
	namespace vortex::graphics
	{
		using Renderer = dx12::DX12Renderer;
		namespace backend = dx12;
		inline constexpr const char* backend_name = "DirectX 12";
	}
#elif VORTEX_HAS_SDLGPU
	#include "SdlGpu/SdlGpuRenderer.h"
	#include "SdlGpu/SdlGpuResources.h"
	namespace vortex::graphics
	{
		using Renderer = sdlgpu::SdlGpuRenderer;
		namespace backend = sdlgpu;
		inline constexpr const char* backend_name = "SDL GPU";
	}
#else
	#error "No render backend: build with the DX12 backend (Windows) or the SDL GPU backend (VORTEX_HAS_SDLGPU)."
#endif
