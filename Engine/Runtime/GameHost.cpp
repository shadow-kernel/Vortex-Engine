#include "GameHost.h"
#include "Systems/RenderSystemDX12.h"
#include "../Graphics/DX12/DX12Renderer.h"
#include "../Graphics/DX12/DX12Streamline.h"   // Reflex + PCL markers for DLSS Frame Generation

#include <Windows.h>
#include <windowsx.h> // GET_X_LPARAM / GET_Y_LPARAM
#include <chrono>
#include <deque>

namespace vortex::runtime
{
    namespace
    {
        HWND               g_hwnd = nullptr;
        volatile bool      g_running = false;
        GameHost::tick_fn  g_tick = nullptr;

        // Input snapshot, written by the wndproc (same thread as the loop → no locking needed).
        int  g_mx = 0, g_my = 0;
        bool g_mdown = false;
        int  g_cw = 0, g_ch = 0;

        // FPS mouse-look capture. Raw Input accumulates relative deltas (no per-frame cursor repositioning,
        // which was throttling FPS); ClipCursor confines the hidden cursor to the window; focus gating frees
        // it on Alt-Tab. g_mouse_dx/dy are accumulated in WM_INPUT and consumed (reset) once per frame.
        bool g_captured = false;
        bool g_has_focus = true;
        long g_mouse_dx = 0, g_mouse_dy = 0;

        // Retained-UI input event queues (same thread as the tick that drains them → no locking). Bounded so a
        // burst of input while the game is paused can't grow unboundedly; overflow drops the oldest event.
        int               g_wheel_accum = 0;       // accumulated wheel notches (+up / -down)
        std::deque<int>   g_char_queue;             // typed characters (WM_CHAR) for text fields
        std::deque<int>   g_key_queue;              // edge-pressed VKs (WM_KEYDOWN, non-repeat) for keybind capture
        constexpr size_t  k_queue_cap = 64;

        // Deferred resize: WM_SIZE only records the new size; the actual swapchain resize runs in the main loop
        // AFTER the message pump, so it never fires re-entrantly mid-SetWindowPos (the F11/maximize transition
        // must be SETTLED before we ResizeBuffers + re-present, or the flip-model display stays frozen).
        int  g_pending_w = 0, g_pending_h = 0;
        bool g_resize_pending = false;

        // Borderless-fullscreen state (to restore the windowed placement/style)
        bool g_fullscreen = false;
        WINDOWPLACEMENT g_prev_placement{ sizeof(WINDOWPLACEMENT) };
        LONG g_prev_style = 0;

        void do_toggle_fullscreen()
        {
            if (!g_hwnd) return;
            if (!g_fullscreen)
            {
                g_prev_style = GetWindowLongW(g_hwnd, GWL_STYLE);
                GetWindowPlacement(g_hwnd, &g_prev_placement);
                MONITORINFO mi{ sizeof(mi) };
                GetMonitorInfoW(MonitorFromWindow(g_hwnd, MONITOR_DEFAULTTONEAREST), &mi);
                SetWindowLongW(g_hwnd, GWL_STYLE, WS_POPUP | WS_VISIBLE);
                SetWindowPos(g_hwnd, HWND_TOP, mi.rcMonitor.left, mi.rcMonitor.top,
                    mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top,
                    SWP_FRAMECHANGED | SWP_SHOWWINDOW);   // borderless (NOT exclusive) -> ALLOW_TEARING stays valid
                g_fullscreen = true;
            }
            else
            {
                SetWindowLongW(g_hwnd, GWL_STYLE, g_prev_style ? g_prev_style : (WS_OVERLAPPEDWINDOW | WS_VISIBLE));
                SetWindowPlacement(g_hwnd, &g_prev_placement);
                SetWindowPos(g_hwnd, nullptr, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                g_fullscreen = false;
            }
            // WM_SIZE from the style/size change drives systems::dx12::resize on this thread.
        }

        void clip_cursor_to_window()
        {
            if (!g_hwnd) return;
            RECT rc; GetClientRect(g_hwnd, &rc);
            POINT tl{ rc.left, rc.top }, br{ rc.right, rc.bottom };
            ClientToScreen(g_hwnd, &tl); ClientToScreen(g_hwnd, &br);
            RECT screen{ tl.x, tl.y, br.x, br.y };
            ClipCursor(&screen);
        }

        LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
        {
            switch (msg)
            {
            case WM_SIZE:
                if (wp != SIZE_MINIMIZED)
                {
                    g_cw = LOWORD(lp); g_ch = HIWORD(lp);
                    // Defer the resize to the main loop (after the pump) so it runs once the window has SETTLED,
                    // not re-entrantly inside SetWindowPos — required for the flip-model present to rebind DWM.
                    if (g_running && g_cw > 0 && g_ch > 0) { g_pending_w = g_cw; g_pending_h = g_ch; g_resize_pending = true; }
                }
                return 0;
            case WM_KEYDOWN:
                // Ignore auto-repeat (bit 30 = previous key state): F11 toggles fullscreen; every fresh press is
                // also queued for keybind capture.
                if (!(lp & (1 << 30)))
                {
                    if (wp == VK_F11) do_toggle_fullscreen();
                    if (g_key_queue.size() >= k_queue_cap) g_key_queue.pop_front();
                    g_key_queue.push_back((int)wp);
                }
                return 0;
            case WM_MOUSEWHEEL:
                g_wheel_accum += GET_WHEEL_DELTA_WPARAM(wp) / WHEEL_DELTA;   // +1 per notch up, -1 down
                return 0;
            case WM_CHAR:
                if (g_char_queue.size() >= k_queue_cap) g_char_queue.pop_front();
                g_char_queue.push_back((int)wp);     // typed character for focused text fields
                return 0;
            case WM_MOUSEMOVE: g_mx = GET_X_LPARAM(lp); g_my = GET_Y_LPARAM(lp); return 0;
            case WM_INPUT:
            {
                // Relative mouse motion for FPS look — no cursor repositioning (that was killing FPS).
                UINT sz = 0;
                GetRawInputData((HRAWINPUT)lp, RID_INPUT, nullptr, &sz, sizeof(RAWINPUTHEADER));
                if (sz > 0 && sz <= sizeof(RAWINPUT))
                {
                    RAWINPUT ri{};
                    if (GetRawInputData((HRAWINPUT)lp, RID_INPUT, &ri, &sz, sizeof(RAWINPUTHEADER)) == sz
                        && ri.header.dwType == RIM_TYPEMOUSE
                        && (ri.data.mouse.usFlags & MOUSE_MOVE_ABSOLUTE) == 0)
                    {
                        g_mouse_dx += ri.data.mouse.lLastX;
                        g_mouse_dy += ri.data.mouse.lLastY;
                    }
                }
                return DefWindowProcW(hwnd, msg, wp, lp);   // WM_INPUT must reach DefWindowProc for cleanup
            }
            case WM_LBUTTONDOWN: g_mdown = true;  SetCapture(hwnd); return 0;
            case WM_LBUTTONUP:   g_mdown = false; ReleaseCapture();  return 0;
            case WM_SETFOCUS:
                g_has_focus = true;
                return 0;
            case WM_KILLFOCUS:
                // Lost focus (Alt+Tab): release + show the cursor and un-clip it so it's never stuck. The game
                // tick won't re-capture while unfocused (set_mouse_captured gates on g_has_focus).
                g_has_focus = false;
                if (g_captured) { g_captured = false; ShowCursor(TRUE); }
                ClipCursor(nullptr);
                return 0;
            case WM_CLOSE:   g_running = false; return 0;
            case WM_DESTROY: PostQuitMessage(0); return 0;
            }
            return DefWindowProcW(hwnd, msg, wp, lp);
        }
    }

    void GameHost::set_tick_callback(tick_fn fn) { g_tick = fn; }
    void GameHost::request_exit() { g_running = false; }
    void GameHost::set_vsync(bool enabled) { graphics::dx12::DX12Renderer::instance().set_vsync(enabled); }

    int  GameHost::mouse_x() { return g_mx; }
    int  GameHost::mouse_y() { return g_my; }
    bool GameHost::mouse_down() { return g_mdown; }
    int  GameHost::client_width() { return g_cw; }
    int  GameHost::client_height() { return g_ch; }
    bool GameHost::key_down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

    int  GameHost::mouse_wheel() { int w = g_wheel_accum; g_wheel_accum = 0; return w; }
    int  GameHost::next_char() { if (g_char_queue.empty()) return -1; int c = g_char_queue.front(); g_char_queue.pop_front(); return c; }
    int  GameHost::next_key_pressed() { if (g_key_queue.empty()) return 0; int k = g_key_queue.front(); g_key_queue.pop_front(); return k; }

    void GameHost::set_mouse_captured(bool captured)
    {
        if (captured && !g_has_focus) captured = false;   // never capture while unfocused (Alt-Tab) -> no stuck cursor
        if (captured == g_captured) return;
        g_captured = captured;
        ShowCursor(captured ? FALSE : TRUE);   // counter-balanced: only toggled on state change
        if (captured) clip_cursor_to_window(); else ClipCursor(nullptr);
        g_mouse_dx = 0; g_mouse_dy = 0;        // drop any stale delta across the transition
    }
    bool GameHost::mouse_captured() { return g_captured; }
    int  GameHost::mouse_dx() { return g_mouse_dx; }
    int  GameHost::mouse_dy() { return g_mouse_dy; }
    void GameHost::toggle_fullscreen() { do_toggle_fullscreen(); }
    bool GameHost::is_fullscreen() { return g_fullscreen; }

    void GameHost::set_resolution(uint32_t w, uint32_t h)
    {
        if (!g_hwnd || g_fullscreen || w == 0 || h == 0) return;  // windowed only; ignore in borderless-fullscreen
        RECT rc{ 0, 0, (LONG)w, (LONG)h };
        DWORD style = (DWORD)GetWindowLongW(g_hwnd, GWL_STYLE);
        AdjustWindowRect(&rc, style, FALSE);                       // outer size whose CLIENT area is w x h
        // Resize in place; the WM_SIZE this posts is handled by the deferred resize in the main loop (settled,
        // same thread as present) -> swapchain ResizeBuffers. Keep position/z; don't steal focus.
        SetWindowPos(g_hwnd, nullptr, 0, 0, rc.right - rc.left, rc.bottom - rc.top,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    bool GameHost::run(uint32_t width, uint32_t height, const wchar_t* title)
    {
        HINSTANCE inst = GetModuleHandleW(nullptr);

        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.style = CS_HREDRAW | CS_VREDRAW;
        wc.lpfnWndProc = WndProc;
        wc.hInstance = inst;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);
        wc.lpszClassName = L"VortexGameHostWindow";
        RegisterClassExW(&wc);

        // Resizable, maximizable window sized so the CLIENT area is width x height.
        RECT rc{ 0, 0, (LONG)width, (LONG)height };
        AdjustWindowRect(&rc, WS_OVERLAPPEDWINDOW, FALSE);
        g_hwnd = CreateWindowExW(0, wc.lpszClassName, title ? title : L"Vortex",
            WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
            rc.right - rc.left, rc.bottom - rc.top, nullptr, nullptr, inst, nullptr);
        if (!g_hwnd) return false;

        // Deliberately DO NOT show the window yet. DX12 + DLSS/NGX init below takes ~seconds; if the window were
        // visible now the user would stare at a black rectangle the whole time. We keep it hidden, render the first
        // real frame into the (hidden) swapchain, then reveal it already-rendered — no black flash. (See the loop.)

        // Register for raw mouse input (relative deltas for FPS look — delivered to the focused window via WM_INPUT).
        {
            RAWINPUTDEVICE rid{};
            rid.usUsagePage = 0x01;   // generic desktop
            rid.usUsage = 0x02;       // mouse
            rid.dwFlags = 0;
            rid.hwndTarget = g_hwnd;
            RegisterRawInputDevices(&rid, 1, sizeof(rid));
        }

        RECT cr{}; GetClientRect(g_hwnd, &cr);
        g_cw = cr.right - cr.left; g_ch = cr.bottom - cr.top;

        systems::dx12::viewport_desc desc{};
        desc.hwnd = g_hwnd; desc.width = (u32)g_cw; desc.height = (u32)g_ch;
        if (!systems::dx12::initialize(desc)) { DestroyWindow(g_hwnd); g_hwnd = nullptr; return false; }

        g_running = true;
        bool g_window_shown = false;   // reveal the window only once, after the first frame is rendered
        auto last = std::chrono::high_resolution_clock::now();

        MSG msg{};
        while (g_running)
        {
            // 1) pump native window messages (resize/input/close) on THIS thread
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) { g_running = false; break; }
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            if (!g_running) break;

            // 1b) deferred resize — runs here (window settled, not re-entrant in SetWindowPos), same thread as present.
            if (g_resize_pending)
            {
                g_resize_pending = false;
                if (g_pending_w > 0 && g_pending_h > 0)
                    systems::dx12::resize((u32)g_pending_w, (u32)g_pending_h);
            }

            // 2) delta time
            auto now = std::chrono::high_resolution_clock::now();
            float dt = std::chrono::duration<float>(now - last).count();
            last = now;
            if (dt < 0.f) dt = 0.f; else if (dt > 0.1f) dt = 0.1f;

            // DLSS Frame Generation: Reflex sleep + PCL latency markers. All no-ops unless the user enabled Frame
            // Generation, so the loop is byte-for-byte unchanged otherwise. SimStart/End bracket the managed tick;
            // render_frame emits the RenderSubmit + Present markers around its own GPU submit + present.
            static uint32_t fg_frame = 0;
            auto& sl = graphics::dx12::DX12Streamline::instance();
            sl.frame_begin(fg_frame++);                                              // token + Reflex sleep + SimulationStart

            // 3) advance the game in managed code (scripts + camera + submit the scene). Raw mouse deltas were
            // accumulated in WM_INPUT during the pump above; the tick reads them via mouse_dx/dy, then we consume.
            if (g_tick) g_tick(dt);
            g_mouse_dx = 0; g_mouse_dy = 0;
            sl.frame_marker(1 /*eSimulationEnd*/);

            // 4) render the submitted scene + present — same thread as the pump/resize, so never freezes
            systems::dx12::render_frame();

            // Reveal the window the instant the FIRST real frame is on the swap-chain — so it appears already
            // rendered (no black flash). The managed splash stays up until now and closes from the next tick.
            if (!g_window_shown)
            {
                g_window_shown = true;
                ShowWindow(g_hwnd, SW_SHOW);
                SetForegroundWindow(g_hwnd);
                UpdateWindow(g_hwnd);
            }
        }

        systems::dx12::shutdown();
        if (g_hwnd) { DestroyWindow(g_hwnd); g_hwnd = nullptr; }
        return true;
    }
}
