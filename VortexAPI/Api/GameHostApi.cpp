#include "../ApiCommon.h"

EDITOR_INTERFACE bool RunGameHost(unsigned int width, unsigned int height, const wchar_t* title)
{
	return runtime::GameHost::run(width, height, title);
}
EDITOR_INTERFACE void SetGameTickCallback(void(*fn)(float))
{
	runtime::GameHost::set_tick_callback(reinterpret_cast<runtime::GameHost::tick_fn>(fn));
}
EDITOR_INTERFACE void RequestGameHostExit() { runtime::GameHost::request_exit(); }
EDITOR_INTERFACE void SetGameHostVSync(bool enabled) { runtime::GameHost::set_vsync(enabled); }
EDITOR_INTERFACE int  GameHostMouseX() { return runtime::GameHost::mouse_x(); }
EDITOR_INTERFACE int  GameHostMouseY() { return runtime::GameHost::mouse_y(); }
EDITOR_INTERFACE bool GameHostMouseDown() { return runtime::GameHost::mouse_down(); }
EDITOR_INTERFACE int  GameHostClientWidth() { return runtime::GameHost::client_width(); }
EDITOR_INTERFACE int  GameHostClientHeight() { return runtime::GameHost::client_height(); }
EDITOR_INTERFACE bool GameHostKeyDown(int vk) { return runtime::GameHost::key_down(vk); }
// FPS mouse-look capture (hide + re-center the cursor; report per-frame delta). Driven by the game.
EDITOR_INTERFACE void SetGameHostMouseCaptured(bool captured) { runtime::GameHost::set_mouse_captured(captured); }
EDITOR_INTERFACE bool GameHostMouseCaptured() { return runtime::GameHost::mouse_captured(); }
EDITOR_INTERFACE int  GameHostMouseDX() { return runtime::GameHost::mouse_dx(); }
EDITOR_INTERFACE int  GameHostMouseDY() { return runtime::GameHost::mouse_dy(); }
// Retained-UI input: wheel notches this frame; next typed char (-1 if none); next edge-pressed VK (0 if none).
EDITOR_INTERFACE int  GameHostMouseWheel() { return runtime::GameHost::mouse_wheel(); }
EDITOR_INTERFACE bool GameHostConsumeFocusGained() { return runtime::GameHost::consume_focus_gained(); } // Alt-Tab back -> hot-reload
EDITOR_INTERFACE int  GameHostNextChar() { return runtime::GameHost::next_char(); }
EDITOR_INTERFACE int  GameHostNextKeyPressed() { return runtime::GameHost::next_key_pressed(); }
// Borderless-fullscreen toggle (also F11 natively) for the settings menu.
EDITOR_INTERFACE void GameHostToggleFullscreen() { runtime::GameHost::toggle_fullscreen(); }
EDITOR_INTERFACE bool GameHostIsFullscreen() { return runtime::GameHost::is_fullscreen(); }
// Settings menu: window resolution (windowed only) + render-scale (stored; applied by the scaled-RT upscale pass).
EDITOR_INTERFACE void GameHostSetResolution(int w, int h) { runtime::GameHost::set_resolution((uint32_t)w, (uint32_t)h); }
EDITOR_INTERFACE void SetRenderScale(float s) { graphics::dx12::DX12Renderer::instance().set_render_scale(s); }
EDITOR_INTERFACE float GetRenderScale() { return graphics::dx12::DX12Renderer::instance().render_scale(); }

// DLSS mode: 0=off, 1=Quality, 2=Balanced, 3=Performance, 4=UltraPerformance. Drives the render-scale + the
// slEvaluateFeature upscale slot. Only visible on DLSS-capable GPUs (else the bilinear render-scale upscale).
EDITOR_INTERFACE void SetDlssMode(int mode) { graphics::dx12::DX12Renderer::instance().set_dlss_mode(mode); }
EDITOR_INTERFACE int GetDlssMode() { return graphics::dx12::DX12Renderer::instance().dlss_mode(); }

// DLSS Frame Generation (separate from SR): 0=off, 1=x2, 2=x3, 3=x4 AI frames inserted at Present. Needs Reflex
// (enabled internally). FrameGenPresentedFps reports DLSSGState.numFramesActuallyPresented for the HUD readout —
// the engine's own FPS counts only REAL frames, so this is how the generated frames become visible.
EDITOR_INTERFACE void SetFrameGenMode(int mode) { graphics::dx12::DX12Renderer::instance().set_fg_mode(mode); }
EDITOR_INTERFACE int GetFrameGenMode() { return graphics::dx12::DX12Renderer::instance().fg_mode(); }
EDITOR_INTERFACE int FrameGenPresentedFps() { return graphics::dx12::DX12Renderer::instance().fg_presented_fps(); }

// Per-material custom shaders: bind a .hlsl (absolute path) to a material so the 3D pass uses a per-material PSO;
// empty path clears it (revert to built-in). ReloadMaterialShaders recompiles any whose .hlsl changed on disk
// (hot-reload; call on window focus or before a material-preview render).
EDITOR_INTERFACE void SetMaterialShader(int material_id, const char* hlsl_path)
{
	std::wstring w;
	if (hlsl_path && *hlsl_path)
	{
		int n = MultiByteToWideChar(CP_UTF8, 0, hlsl_path, -1, nullptr, 0);
		if (n > 1) { w.resize(n - 1); MultiByteToWideChar(CP_UTF8, 0, hlsl_path, -1, &w[0], n); }
	}
	graphics::dx12::DX12Renderer::instance().set_material_shader((uint32_t)material_id, w);
}
EDITOR_INTERFACE int ReloadMaterialShaders() { return graphics::dx12::DX12Renderer::instance().reload_dirty_shaders(); }

// GPU capability — the DLSS hardware gate. The options UI shows DLSS only when GpuSupportsDlss() is true; on
// every other machine the render-scale slider is the universal fallback.
EDITOR_INTERFACE int GpuVendorId() { return (int)graphics::dx12::DX12Core::instance().adapter_vendor_id(); }
EDITOR_INTERFACE bool GpuSupportsDlss() { return graphics::dx12::DX12Core::instance().dlss_capable(); }
EDITOR_INTERFACE int GpuName(char* buf, int cap)
{
	if (!buf || cap <= 0) return 0;
	const std::wstring& w = graphics::dx12::DX12Core::instance().adapter_name();
	int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), buf, cap - 1, nullptr, nullptr);
	if (n < 0) n = 0; if (n > cap - 1) n = cap - 1;
	buf[n] = '\0';
	return n;
}

