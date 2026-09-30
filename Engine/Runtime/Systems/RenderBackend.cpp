#include "RenderBackend.h"
#include "../../Graphics/Backend.h"

namespace vortex::runtime::systems::render
{
	bool initialize(const viewport_desc& desc)
	{
		graphics::backend::RendererDesc renderer_desc{};
#if VORTEX_HAS_DX12
		renderer_desc.hwnd = reinterpret_cast<HWND>(desc.native_window);
#else
		renderer_desc.native_window = desc.native_window;
		renderer_desc.native_is_sdl_window = desc.native_is_sdl_window;
#endif
		renderer_desc.width = desc.width;
		renderer_desc.height = desc.height;
		return graphics::Renderer::instance().initialize(renderer_desc);
	}

	void shutdown() { graphics::Renderer::instance().shutdown(); }
	void resize(u32 width, u32 height) { graphics::Renderer::instance().resize(width, height); }
	void render_frame() { graphics::Renderer::instance().render_frame(); }
	bool initialized() { return graphics::Renderer::instance().is_initialized(); }
}
