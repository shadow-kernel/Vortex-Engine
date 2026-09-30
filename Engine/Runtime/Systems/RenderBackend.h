#pragma once

#include "../../Common/CommonHeaders.h"

// Backend-neutral render-system entry points: attach the renderer to a native window, resize it,
// render one frame, tear it down. Replaces the DX12-only RenderSystemDX12 interface for every caller
// outside the backends (the VortexAPI viewport functions and the native GameHost).
namespace vortex::runtime::systems::render
{
	struct viewport_desc
	{
		// Windows: an HWND. macOS: an NSView* (the editor embeds the viewport in its own window) or an
		// SDL_Window* created by the GameHost. Linux: an SDL_Window*.
		void* native_window{ nullptr };
		bool native_is_sdl_window{ false };   // true when native_window is an SDL_Window* (the native GameHost)
		u32 width{ 0 };
		u32 height{ 0 };
	};

	bool initialize(const viewport_desc& desc);
	void shutdown();
	void resize(u32 width, u32 height);
	void render_frame();
	bool initialized();
}
