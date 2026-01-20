#pragma once

#include "../../Common/CommonHeaders.h"
#include <Windows.h>

// DX12 render backend interface.
// Uses the modular DX12 architecture from Engine/Graphics/DX12/

namespace vortex::runtime::systems::dx12
{
	struct viewport_desc
	{
		HWND hwnd{ nullptr };
		u32 width{ 0 };
		u32 height{ 0 };
	};

	bool initialize(const viewport_desc& desc);
	void shutdown();
	void resize(u32 width, u32 height);
	void render_frame();
}
