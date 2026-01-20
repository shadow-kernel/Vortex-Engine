#include "RenderSystemDX12.h"
#include "../../Graphics/DX12/DX12Renderer.h"

namespace vortex::runtime::systems::dx12
{
	bool initialize(const viewport_desc& desc)
	{
		graphics::dx12::RendererDesc renderer_desc{};
		renderer_desc.hwnd = desc.hwnd;
		renderer_desc.width = desc.width;
		renderer_desc.height = desc.height;
		return graphics::dx12::DX12Renderer::instance().initialize(renderer_desc);
	}

	void shutdown()
	{
		graphics::dx12::DX12Renderer::instance().shutdown();
	}

	void resize(u32 width, u32 height)
	{
		graphics::dx12::DX12Renderer::instance().resize(width, height);
	}

	void render_frame()
	{
		graphics::dx12::DX12Renderer::instance().render_frame();
	}
}
