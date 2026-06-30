#include "../ApiCommon.h"

EDITOR_INTERFACE void StartRenderLoop()
{
	runtime::RenderLoop::instance().start([]() {
		graphics::dx12::DX12Renderer::instance().render_frame();
	});
}

EDITOR_INTERFACE void StopRenderLoop()
{
	runtime::RenderLoop::instance().stop();
}

EDITOR_INTERFACE bool IsRenderLoopRunning()
{
	return runtime::RenderLoop::instance().is_running();
}

EDITOR_INTERFACE void SetTargetFPS(int fps)
{
	runtime::RenderLoop::instance().set_target_fps(fps);
}

EDITOR_INTERFACE int GetTargetFPS()
{
	return runtime::RenderLoop::instance().get_target_fps();
}

EDITOR_INTERFACE void SetRenderLoopVSync(bool enabled)
{
	runtime::RenderLoop::instance().set_vsync(enabled);
	graphics::dx12::DX12Renderer::instance().set_vsync(enabled);
}

EDITOR_INTERFACE bool IsRenderLoopVSyncEnabled()
{
	return runtime::RenderLoop::instance().is_vsync_enabled();
}

EDITOR_INTERFACE float GetDeltaTime()
{
	return runtime::RenderLoop::instance().get_delta_time();
}

EDITOR_INTERFACE float GetTotalTime()
{
	return runtime::RenderLoop::instance().get_total_time();
}

// ============== INPUT SYSTEM API ==============

