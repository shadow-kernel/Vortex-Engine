// GameHost — SDL3 implementation (macOS / Linux). Same contract as the Win32 GameHost.cpp: a native window,
// the renderer's swapchain, the game loop, present and input all live on ONE thread (the main thread, which
// macOS requires for windows and events). Gameplay stays in the managed tick callback.
#include "GameHost.h"
#include "Systems/RenderBackend.h"
#include "../Graphics/Backend.h"
#include "../Common/Platform.h"
#include <SDL3/SDL.h>
#include <chrono>
#include <deque>
#include <string>

namespace vortex::runtime
{
	namespace
	{
		SDL_Window*        g_window = nullptr;
		volatile bool      g_running = false;
		GameHost::tick_fn  g_tick = nullptr;

		int  g_mx = 0, g_my = 0;          // cursor in window points (the game's UI space)
		bool g_mdown = false;
		int  g_cw = 0, g_ch = 0;          // client size in points
		bool g_captured = false;
		bool g_has_focus = true;
		bool g_focus_gained = false;
		long g_mouse_dx = 0, g_mouse_dy = 0;
		int  g_wheel_accum = 0;
		float g_wheel_frac = 0.0f;
		std::deque<int> g_char_queue;
		std::deque<int> g_key_queue;
		constexpr size_t k_queue_cap = 64;
		bool g_fullscreen = false;
		int  g_pending_w = 0, g_pending_h = 0;
		bool g_resize_pending = false;

		// ---- Windows virtual-key codes <-> SDL scancodes (the managed layer speaks VK_*) ----
		SDL_Scancode vk_to_scancode(int vk)
		{
			if (vk >= 'A' && vk <= 'Z') return (SDL_Scancode)(SDL_SCANCODE_A + (vk - 'A'));
			if (vk >= '1' && vk <= '9') return (SDL_Scancode)(SDL_SCANCODE_1 + (vk - '1'));
			if (vk == '0') return SDL_SCANCODE_0;
			if (vk >= 0x70 && vk <= 0x7B) return (SDL_Scancode)(SDL_SCANCODE_F1 + (vk - 0x70));
			if (vk >= 0x60 && vk <= 0x69) return vk == 0x60 ? SDL_SCANCODE_KP_0 : (SDL_Scancode)(SDL_SCANCODE_KP_1 + (vk - 0x61));
			switch (vk)
			{
			case 0x08: return SDL_SCANCODE_BACKSPACE;
			case 0x09: return SDL_SCANCODE_TAB;
			case 0x0D: return SDL_SCANCODE_RETURN;
			case 0x10: case 0xA0: return SDL_SCANCODE_LSHIFT;
			case 0xA1: return SDL_SCANCODE_RSHIFT;
			case 0x11: case 0xA2: return SDL_SCANCODE_LCTRL;
			case 0xA3: return SDL_SCANCODE_RCTRL;
			case 0x12: case 0xA4: return SDL_SCANCODE_LALT;
			case 0xA5: return SDL_SCANCODE_RALT;
			case 0x13: return SDL_SCANCODE_PAUSE;
			case 0x14: return SDL_SCANCODE_CAPSLOCK;
			case 0x1B: return SDL_SCANCODE_ESCAPE;
			case 0x20: return SDL_SCANCODE_SPACE;
			case 0x21: return SDL_SCANCODE_PAGEUP;
			case 0x22: return SDL_SCANCODE_PAGEDOWN;
			case 0x23: return SDL_SCANCODE_END;
			case 0x24: return SDL_SCANCODE_HOME;
			case 0x25: return SDL_SCANCODE_LEFT;
			case 0x26: return SDL_SCANCODE_UP;
			case 0x27: return SDL_SCANCODE_RIGHT;
			case 0x28: return SDL_SCANCODE_DOWN;
			case 0x2D: return SDL_SCANCODE_INSERT;
			case 0x2E: return SDL_SCANCODE_DELETE;
			case 0x5B: return SDL_SCANCODE_LGUI;
			case 0x5C: return SDL_SCANCODE_RGUI;
			case 0x6A: return SDL_SCANCODE_KP_MULTIPLY;
			case 0x6B: return SDL_SCANCODE_KP_PLUS;
			case 0x6D: return SDL_SCANCODE_KP_MINUS;
			case 0x6E: return SDL_SCANCODE_KP_PERIOD;
			case 0x6F: return SDL_SCANCODE_KP_DIVIDE;
			case 0xBA: return SDL_SCANCODE_SEMICOLON;
			case 0xBB: return SDL_SCANCODE_EQUALS;
			case 0xBC: return SDL_SCANCODE_COMMA;
			case 0xBD: return SDL_SCANCODE_MINUS;
			case 0xBE: return SDL_SCANCODE_PERIOD;
			case 0xBF: return SDL_SCANCODE_SLASH;
			case 0xC0: return SDL_SCANCODE_GRAVE;
			case 0xDB: return SDL_SCANCODE_LEFTBRACKET;
			case 0xDC: return SDL_SCANCODE_BACKSLASH;
			case 0xDD: return SDL_SCANCODE_RIGHTBRACKET;
			case 0xDE: return SDL_SCANCODE_APOSTROPHE;
			default: return SDL_SCANCODE_UNKNOWN;
			}
		}

		int scancode_to_vk(SDL_Scancode sc)
		{
			if (sc >= SDL_SCANCODE_A && sc <= SDL_SCANCODE_Z) return 'A' + (sc - SDL_SCANCODE_A);
			if (sc >= SDL_SCANCODE_1 && sc <= SDL_SCANCODE_9) return '1' + (sc - SDL_SCANCODE_1);
			if (sc == SDL_SCANCODE_0) return '0';
			if (sc >= SDL_SCANCODE_F1 && sc <= SDL_SCANCODE_F12) return 0x70 + (sc - SDL_SCANCODE_F1);
			if (sc >= SDL_SCANCODE_KP_1 && sc <= SDL_SCANCODE_KP_9) return 0x61 + (sc - SDL_SCANCODE_KP_1);
			switch (sc)
			{
			case SDL_SCANCODE_KP_0: return 0x60;
			case SDL_SCANCODE_BACKSPACE: return 0x08;
			case SDL_SCANCODE_TAB: return 0x09;
			case SDL_SCANCODE_RETURN: case SDL_SCANCODE_KP_ENTER: return 0x0D;
			case SDL_SCANCODE_LSHIFT: return 0xA0;
			case SDL_SCANCODE_RSHIFT: return 0xA1;
			case SDL_SCANCODE_LCTRL: return 0xA2;
			case SDL_SCANCODE_RCTRL: return 0xA3;
			case SDL_SCANCODE_LALT: return 0xA4;
			case SDL_SCANCODE_RALT: return 0xA5;
			case SDL_SCANCODE_PAUSE: return 0x13;
			case SDL_SCANCODE_CAPSLOCK: return 0x14;
			case SDL_SCANCODE_ESCAPE: return 0x1B;
			case SDL_SCANCODE_SPACE: return 0x20;
			case SDL_SCANCODE_PAGEUP: return 0x21;
			case SDL_SCANCODE_PAGEDOWN: return 0x22;
			case SDL_SCANCODE_END: return 0x23;
			case SDL_SCANCODE_HOME: return 0x24;
			case SDL_SCANCODE_LEFT: return 0x25;
			case SDL_SCANCODE_UP: return 0x26;
			case SDL_SCANCODE_RIGHT: return 0x27;
			case SDL_SCANCODE_DOWN: return 0x28;
			case SDL_SCANCODE_INSERT: return 0x2D;
			case SDL_SCANCODE_DELETE: return 0x2E;
			case SDL_SCANCODE_LGUI: return 0x5B;
			case SDL_SCANCODE_RGUI: return 0x5C;
			case SDL_SCANCODE_KP_MULTIPLY: return 0x6A;
			case SDL_SCANCODE_KP_PLUS: return 0x6B;
			case SDL_SCANCODE_KP_MINUS: return 0x6D;
			case SDL_SCANCODE_KP_PERIOD: return 0x6E;
			case SDL_SCANCODE_KP_DIVIDE: return 0x6F;
			case SDL_SCANCODE_SEMICOLON: return 0xBA;
			case SDL_SCANCODE_EQUALS: return 0xBB;
			case SDL_SCANCODE_COMMA: return 0xBC;
			case SDL_SCANCODE_MINUS: return 0xBD;
			case SDL_SCANCODE_PERIOD: return 0xBE;
			case SDL_SCANCODE_SLASH: return 0xBF;
			case SDL_SCANCODE_GRAVE: return 0xC0;
			case SDL_SCANCODE_LEFTBRACKET: return 0xDB;
			case SDL_SCANCODE_BACKSLASH: return 0xDC;
			case SDL_SCANCODE_RIGHTBRACKET: return 0xDD;
			case SDL_SCANCODE_APOSTROPHE: return 0xDE;
			default: return 0;
			}
		}

		void update_client_size()
		{
			if (!g_window) return;
			SDL_GetWindowSize(g_window, &g_cw, &g_ch);
		}

		void handle_event(const SDL_Event& e)
		{
			switch (e.type)
			{
			case SDL_EVENT_QUIT:
			case SDL_EVENT_WINDOW_CLOSE_REQUESTED:
				g_running = false;
				break;
			case SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
				update_client_size();
				g_pending_w = e.window.data1; g_pending_h = e.window.data2;
				g_resize_pending = g_pending_w > 0 && g_pending_h > 0;
				break;
			case SDL_EVENT_WINDOW_RESIZED:
				update_client_size();
				break;
			case SDL_EVENT_WINDOW_FOCUS_GAINED:
				g_has_focus = true;
				g_focus_gained = true;
				break;
			case SDL_EVENT_WINDOW_FOCUS_LOST:
				g_has_focus = false;
				if (g_captured) { g_captured = false; SDL_SetWindowRelativeMouseMode(g_window, false); }
				break;
			case SDL_EVENT_KEY_DOWN:
				if (!e.key.repeat)
				{
					if (e.key.scancode == SDL_SCANCODE_F11) GameHost::toggle_fullscreen();
					const int vk = scancode_to_vk(e.key.scancode);
					if (vk)
					{
						if (g_key_queue.size() >= k_queue_cap) g_key_queue.pop_front();
						g_key_queue.push_back(vk);
					}
				}
				break;
			case SDL_EVENT_TEXT_INPUT:
			{
				// UTF-8 -> code points for the retained UI's text fields
				const char* s = e.text.text;
				while (s && *s)
				{
					const unsigned char c = (unsigned char)*s;
					int cp, extra;
					if (c < 0x80) { cp = c; extra = 0; }
					else if ((c & 0xE0) == 0xC0) { cp = c & 0x1F; extra = 1; }
					else if ((c & 0xF0) == 0xE0) { cp = c & 0x0F; extra = 2; }
					else { cp = c & 0x07; extra = 3; }
					++s;
					for (int k = 0; k < extra && *s; ++k, ++s) cp = (cp << 6) | ((unsigned char)*s & 0x3F);
					if (g_char_queue.size() >= k_queue_cap) g_char_queue.pop_front();
					g_char_queue.push_back(cp);
				}
				break;
			}
			case SDL_EVENT_MOUSE_MOTION:
				g_mx = (int)e.motion.x; g_my = (int)e.motion.y;
				if (g_captured) { g_mouse_dx += (long)e.motion.xrel; g_mouse_dy += (long)e.motion.yrel; }
				break;
			case SDL_EVENT_MOUSE_BUTTON_DOWN:
				if (e.button.button == SDL_BUTTON_LEFT) g_mdown = true;
				break;
			case SDL_EVENT_MOUSE_BUTTON_UP:
				if (e.button.button == SDL_BUTTON_LEFT) g_mdown = false;
				break;
			case SDL_EVENT_MOUSE_WHEEL:
				g_wheel_frac += e.wheel.y;
				while (g_wheel_frac >= 1.0f) { ++g_wheel_accum; g_wheel_frac -= 1.0f; }
				while (g_wheel_frac <= -1.0f) { --g_wheel_accum; g_wheel_frac += 1.0f; }
				break;
			default:
				break;
			}
		}
	}

	void GameHost::set_tick_callback(tick_fn fn) { g_tick = fn; }
	void GameHost::request_exit() { g_running = false; }
	void GameHost::set_vsync(bool enabled) { graphics::Renderer::instance().set_vsync(enabled); }

	int  GameHost::mouse_x() { return g_mx; }
	int  GameHost::mouse_y() { return g_my; }
	bool GameHost::mouse_down() { return g_mdown; }
	int  GameHost::client_width() { return g_cw; }
	int  GameHost::client_height() { return g_ch; }

	bool GameHost::key_down(int vk)
	{
		if (vk == 0x01 || vk == 0x02 || vk == 0x04)
		{
			const SDL_MouseButtonFlags buttons = SDL_GetMouseState(nullptr, nullptr);
			if (vk == 0x01) return (buttons & SDL_BUTTON_LMASK) != 0;
			if (vk == 0x02) return (buttons & SDL_BUTTON_RMASK) != 0;
			return (buttons & SDL_BUTTON_MMASK) != 0;
		}
		const bool* state = SDL_GetKeyboardState(nullptr);
		if (!state) return false;
		const SDL_Scancode sc = vk_to_scancode(vk);
		if (sc == SDL_SCANCODE_UNKNOWN) return false;
		if (state[sc]) return true;
		if (vk == 0x10) return state[SDL_SCANCODE_RSHIFT];
		if (vk == 0x11) return state[SDL_SCANCODE_RCTRL];
		if (vk == 0x12) return state[SDL_SCANCODE_RALT];
		return false;
	}

	int  GameHost::mouse_wheel() { int w = g_wheel_accum; g_wheel_accum = 0; return w; }
	bool GameHost::consume_focus_gained() { bool f = g_focus_gained; g_focus_gained = false; return f; }
	bool GameHost::has_focus() { return g_has_focus && g_window != nullptr; }
	int  GameHost::next_char() { if (g_char_queue.empty()) return -1; int c = g_char_queue.front(); g_char_queue.pop_front(); return c; }
	int  GameHost::next_key_pressed() { if (g_key_queue.empty()) return 0; int k = g_key_queue.front(); g_key_queue.pop_front(); return k; }

	void GameHost::set_mouse_captured(bool captured)
	{
		if (captured && !g_has_focus) captured = false;
		if (captured == g_captured) return;
		g_captured = captured;
		if (g_window) SDL_SetWindowRelativeMouseMode(g_window, captured);
		g_mouse_dx = 0; g_mouse_dy = 0;
	}
	bool GameHost::mouse_captured() { return g_captured; }
	int  GameHost::mouse_dx() { return (int)g_mouse_dx; }
	int  GameHost::mouse_dy() { return (int)g_mouse_dy; }

	void GameHost::toggle_fullscreen()
	{
		if (!g_window) return;
		g_fullscreen = !g_fullscreen;
		SDL_SetWindowFullscreen(g_window, g_fullscreen);
	}
	bool GameHost::is_fullscreen() { return g_fullscreen; }

	void GameHost::set_resolution(uint32_t w, uint32_t h)
	{
		if (!g_window || g_fullscreen || w == 0 || h == 0) return;
		SDL_SetWindowSize(g_window, (int)w, (int)h);
	}

	bool GameHost::run_utf8(uint32_t width, uint32_t height, const char* title_utf8)
	{
		if (!SDL_WasInit(SDL_INIT_VIDEO) && !SDL_Init(SDL_INIT_VIDEO | SDL_INIT_GAMEPAD))
		{
			platform::debug_output((std::string("[gamehost] SDL_Init failed: ") + SDL_GetError() + "\n").c_str());
			return false;
		}
		// Hidden until the first frame is rendered (no black flash while the renderer initializes).
		g_window = SDL_CreateWindow(title_utf8 && *title_utf8 ? title_utf8 : "Vortex", (int)width, (int)height,
			SDL_WINDOW_RESIZABLE | SDL_WINDOW_HIGH_PIXEL_DENSITY | SDL_WINDOW_HIDDEN);
		if (!g_window)
		{
			platform::debug_output((std::string("[gamehost] SDL_CreateWindow failed: ") + SDL_GetError() + "\n").c_str());
			return false;
		}
		update_client_size();
		int pw = 0, ph = 0;
		SDL_GetWindowSizeInPixels(g_window, &pw, &ph);

		systems::render::viewport_desc desc{};
		desc.native_window = g_window;
		desc.native_is_sdl_window = true;
		desc.width = (u32)pw; desc.height = (u32)ph;
		if (!systems::render::initialize(desc))
		{
			SDL_DestroyWindow(g_window); g_window = nullptr;
			return false;
		}
		SDL_StartTextInput(g_window);

		g_running = true;
		bool shown = false;
		auto last = std::chrono::high_resolution_clock::now();
		while (g_running)
		{
			SDL_Event e;
			while (SDL_PollEvent(&e)) handle_event(e);
			if (!g_running) break;

			if (g_resize_pending)
			{
				g_resize_pending = false;
				systems::render::resize((u32)g_pending_w, (u32)g_pending_h);
			}

			auto now = std::chrono::high_resolution_clock::now();
			float dt = std::chrono::duration<float>(now - last).count();
			last = now;
			if (dt < 0.f) dt = 0.f; else if (dt > 0.1f) dt = 0.1f;

			if (g_tick) g_tick(dt);
			g_mouse_dx = 0; g_mouse_dy = 0;

			systems::render::render_frame();

			if (!shown)
			{
				shown = true;
				SDL_ShowWindow(g_window);
				SDL_RaiseWindow(g_window);
			}
		}

		SDL_StopTextInput(g_window);
		systems::render::shutdown();
		if (g_window) { SDL_DestroyWindow(g_window); g_window = nullptr; }
		g_captured = false;
		return true;
	}

	bool GameHost::run(uint32_t width, uint32_t height, const wchar_t* title)
	{
		std::string utf8;
		for (const wchar_t* s = title; s && *s; ++s)
		{
			const uint32_t cp = (uint32_t)*s;
			if (cp < 0x80) utf8.push_back((char)cp);
			else if (cp < 0x800) { utf8.push_back((char)(0xC0 | (cp >> 6))); utf8.push_back((char)(0x80 | (cp & 0x3F))); }
			else if (cp < 0x10000) { utf8.push_back((char)(0xE0 | (cp >> 12))); utf8.push_back((char)(0x80 | ((cp >> 6) & 0x3F))); utf8.push_back((char)(0x80 | (cp & 0x3F))); }
			else { utf8.push_back((char)(0xF0 | (cp >> 18))); utf8.push_back((char)(0x80 | ((cp >> 12) & 0x3F))); utf8.push_back((char)(0x80 | ((cp >> 6) & 0x3F))); utf8.push_back((char)(0x80 | (cp & 0x3F))); }
		}
		return run_utf8(width, height, utf8.c_str());
	}
}
