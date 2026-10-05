#include "../ApiCommon.h"
#include <cstdint>
#if VORTEX_HAS_SDLGPU
#include <SDL3/SDL.h>
#endif

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
EDITOR_INTERFACE bool GameHostHasFocus() { return runtime::GameHost::has_focus(); }   // input gating for the managed layer
EDITOR_INTERFACE int  GameHostNextChar() { return runtime::GameHost::next_char(); }
EDITOR_INTERFACE int  GameHostNextKeyPressed() { return runtime::GameHost::next_key_pressed(); }
// Borderless-fullscreen toggle (also F11 natively) for the settings menu.
EDITOR_INTERFACE void GameHostToggleFullscreen() { runtime::GameHost::toggle_fullscreen(); }
EDITOR_INTERFACE bool GameHostIsFullscreen() { return runtime::GameHost::is_fullscreen(); }
// Settings menu: window resolution (windowed only) + render-scale (stored; applied by the scaled-RT upscale pass).
EDITOR_INTERFACE void GameHostSetResolution(int w, int h) { runtime::GameHost::set_resolution((uint32_t)w, (uint32_t)h); }
EDITOR_INTERFACE void SetRenderScale(float s) { graphics::Renderer::instance().set_render_scale(s); }
EDITOR_INTERFACE float GetRenderScale() { return graphics::Renderer::instance().render_scale(); }

// DLSS mode: 0=off, 1=Quality, 2=Balanced, 3=Performance, 4=UltraPerformance. Drives the render-scale + the
// slEvaluateFeature upscale slot. Only visible on DLSS-capable GPUs (else the bilinear render-scale upscale).
EDITOR_INTERFACE void SetDlssMode(int mode) { graphics::Renderer::instance().set_dlss_mode(mode); }
EDITOR_INTERFACE int GetDlssMode() { return graphics::Renderer::instance().dlss_mode(); }

// DLSS Frame Generation (separate from SR): 0=off, 1=x2, 2=x3, 3=x4 AI frames inserted at Present. Needs Reflex
// (enabled internally). FrameGenPresentedFps reports DLSSGState.numFramesActuallyPresented for the HUD readout —
// the engine's own FPS counts only REAL frames, so this is how the generated frames become visible.
EDITOR_INTERFACE void SetFrameGenMode(int mode) { graphics::Renderer::instance().set_fg_mode(mode); }
EDITOR_INTERFACE int GetFrameGenMode() { return graphics::Renderer::instance().fg_mode(); }
EDITOR_INTERFACE int FrameGenPresentedFps() { return graphics::Renderer::instance().fg_presented_fps(); }

// Per-material custom shaders: bind a .hlsl (absolute path) to a material so the 3D pass uses a per-material PSO;
// empty path clears it (revert to built-in). ReloadMaterialShaders recompiles any whose .hlsl changed on disk
// (hot-reload; call on window focus or before a material-preview render).
EDITOR_INTERFACE void SetMaterialShader(int material_id, const char* hlsl_path)
{
#if VORTEX_HAS_DX12
	std::wstring w;
	if (hlsl_path && *hlsl_path)
	{
		int n = MultiByteToWideChar(CP_UTF8, 0, hlsl_path, -1, nullptr, 0);
		if (n > 1) { w.resize(n - 1); MultiByteToWideChar(CP_UTF8, 0, hlsl_path, -1, &w[0], n); }
	}
	graphics::Renderer::instance().set_material_shader((uint32_t)material_id, w);
#else
	// SDL GPU backend: custom material shaders are .metal (MSL) files on macOS; .hlsl is not compiled there.
	graphics::Renderer::instance().set_material_shader((uint32_t)material_id, std::string(hlsl_path ? hlsl_path : ""));
#endif
}
EDITOR_INTERFACE int ReloadMaterialShaders() { return graphics::Renderer::instance().reload_dirty_shaders(); }
// Compile a custom material shader without binding it (Claude's validate_shader / write_shader): 1 = both stages
// compile and build a pipeline with the engine's layout, 0 = not — the compiler output goes to errors (UTF-8,
// NUL-terminated, truncated to cap).
EDITOR_INTERFACE int ValidateMaterialShader(const char* path, char* errors, int cap)
{
	std::string err;
	bool ok;
#if VORTEX_HAS_DX12
	std::wstring w;
	if (path && *path)
	{
		int n = MultiByteToWideChar(CP_UTF8, 0, path, -1, nullptr, 0);
		if (n > 1) { w.resize(n - 1); MultiByteToWideChar(CP_UTF8, 0, path, -1, &w[0], n); }
	}
	ok = graphics::Renderer::instance().validate_material_shader(w, err);
#else
	ok = graphics::Renderer::instance().validate_material_shader(std::string(path ? path : ""), err);
#endif
	if (errors && cap > 0)
	{
		size_t len = err.size() < (size_t)(cap - 1) ? err.size() : (size_t)(cap - 1);
		for (size_t i = 0; i < len; ++i) errors[i] = err[i];
		errors[len] = 0;
	}
	return ok ? 1 : 0;
}
// Cheap no-compile check so the editor only shows the hot-reload overlay when a shader ACTUALLY changed on disk.
EDITOR_INTERFACE bool AnyMaterialShaderDirty() { return graphics::Renderer::instance().any_material_shader_dirty(); }

// GPU capability — the DLSS hardware gate. The options UI shows DLSS only when GpuSupportsDlss() is true; on
// every other machine the render-scale slider is the universal fallback.
#if VORTEX_HAS_DX12
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
#else
EDITOR_INTERFACE int GpuVendorId() { return (int)graphics::Renderer::instance().gpu_vendor_id(); }
EDITOR_INTERFACE bool GpuSupportsDlss() { return graphics::Renderer::instance().dlss_capable(); }
EDITOR_INTERFACE int GpuName(char* buf, int cap)
{
	if (!buf || cap <= 0) return 0;
	const std::string& name = graphics::Renderer::instance().gpu_name();
	int n = (int)name.size(); if (n > cap - 1) n = cap - 1;
	memcpy(buf, name.data(), (size_t)n);
	buf[n] = '\0';
	return n;
}
#endif

// UTF-8 twin of RunGameHost for hosts where wchar_t is not UTF-16 (macOS/Linux .NET). Same semantics.
EDITOR_INTERFACE bool RunGameHostUtf8(unsigned int width, unsigned int height, const char* title_utf8)
{
	return runtime::GameHost::run_utf8(width, height, title_utf8);
}

// ---- Gamepads for the .NET hosts (cross-platform editor, Vortex.Player) --------------------------------------------
// One controller snapshot in the convention the gameplay API uses (Vortex.Input): XInput button bits, sticks -1..1 with
// Y up and the XInput dead zones, triggers 0..1. Returns 1 while a gamepad is connected. The SDL builds (macOS, Linux)
// read SDL3's gamepad API — Xbox, PlayStation, Switch Pro and the rest of SDL's mappings, USB and Bluetooth; the DX12
// build returns 0 and the Windows hosts read XInput / the DualSense HID from managed code.
struct VortexGamepadState
{
	int32_t connected;
	uint16_t buttons;
	uint16_t reserved;
	float lx, ly, rx, ry, lt, rt;
};

#if VORTEX_HAS_SDLGPU
namespace
{
	float gamepad_stick(Sint16 v, float dead)
	{
		float f = (float)v;
		if (f > dead) f = (f - dead) / (32767.0f - dead);
		else if (f < -dead) f = (f + dead) / (32768.0f - dead);
		else f = 0.0f;
		return f < -1.0f ? -1.0f : (f > 1.0f ? 1.0f : f);
	}
	float gamepad_trigger(Sint16 v)   // SDL 0..32767; the XInput threshold of 30/255
	{
		const float t = 30.0f / 255.0f * 32767.0f;
		return v <= t ? 0.0f : ((float)v - t) / (32767.0f - t);
	}
}
#endif

EDITOR_INTERFACE int32_t PollGamepad(VortexGamepadState* out)
{
	if (!out) return 0;
	*out = VortexGamepadState{};
#if VORTEX_HAS_SDLGPU
	static bool s_init = false, s_ok = false;
	static SDL_Gamepad* s_pad = nullptr;
	if (!s_init)
	{
		s_init = true;
		s_ok = SDL_InitSubSystem(SDL_INIT_GAMEPAD);
		// Polled, never event-driven: the editor does not drain SDL's event queue, so keep pad events out of it.
		if (s_ok) { SDL_SetGamepadEventsEnabled(false); SDL_SetJoystickEventsEnabled(false); }
	}
	if (!s_ok) return 0;
	SDL_UpdateGamepads();
	if (s_pad && !SDL_GamepadConnected(s_pad)) { SDL_CloseGamepad(s_pad); s_pad = nullptr; }
	if (!s_pad)
	{
		int count = 0;
		SDL_JoystickID* ids = SDL_GetGamepads(&count);
		if (ids && count > 0) s_pad = SDL_OpenGamepad(ids[0]);
		SDL_free(ids);
		if (!s_pad) return 0;
	}

	struct { SDL_GamepadButton b; uint16_t bit; } const map[] = {
		{ SDL_GAMEPAD_BUTTON_DPAD_UP, 0x0001 }, { SDL_GAMEPAD_BUTTON_DPAD_DOWN, 0x0002 },
		{ SDL_GAMEPAD_BUTTON_DPAD_LEFT, 0x0004 }, { SDL_GAMEPAD_BUTTON_DPAD_RIGHT, 0x0008 },
		{ SDL_GAMEPAD_BUTTON_START, 0x0010 }, { SDL_GAMEPAD_BUTTON_BACK, 0x0020 },
		{ SDL_GAMEPAD_BUTTON_LEFT_STICK, 0x0040 }, { SDL_GAMEPAD_BUTTON_RIGHT_STICK, 0x0080 },
		{ SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, 0x0100 }, { SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER, 0x0200 },
		{ SDL_GAMEPAD_BUTTON_SOUTH, 0x1000 }, { SDL_GAMEPAD_BUTTON_EAST, 0x2000 },     // A / Cross, B / Circle
		{ SDL_GAMEPAD_BUTTON_WEST, 0x4000 }, { SDL_GAMEPAD_BUTTON_NORTH, 0x8000 },     // X / Square, Y / Triangle
	};
	uint16_t buttons = 0;
	for (const auto& m : map)
		if (SDL_GetGamepadButton(s_pad, m.b)) buttons |= m.bit;

	out->connected = 1;
	out->buttons = buttons;
	out->lx = gamepad_stick(SDL_GetGamepadAxis(s_pad, SDL_GAMEPAD_AXIS_LEFTX), 7849.0f);
	out->ly = -gamepad_stick(SDL_GetGamepadAxis(s_pad, SDL_GAMEPAD_AXIS_LEFTY), 7849.0f);    // SDL: down is +
	out->rx = gamepad_stick(SDL_GetGamepadAxis(s_pad, SDL_GAMEPAD_AXIS_RIGHTX), 8689.0f);
	out->ry = -gamepad_stick(SDL_GetGamepadAxis(s_pad, SDL_GAMEPAD_AXIS_RIGHTY), 8689.0f);
	out->lt = gamepad_trigger(SDL_GetGamepadAxis(s_pad, SDL_GAMEPAD_AXIS_LEFT_TRIGGER));
	out->rt = gamepad_trigger(SDL_GetGamepadAxis(s_pad, SDL_GAMEPAD_AXIS_RIGHT_TRIGGER));
	return 1;
#else
	return 0;
#endif
}
