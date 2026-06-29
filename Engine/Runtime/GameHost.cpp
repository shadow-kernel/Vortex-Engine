#include "GameHost.h"
#include "Systems/RenderSystemDX12.h"
#include "../Graphics/DX12/DX12Renderer.h"

#include <Windows.h>
#include <windowsx.h> // GET_X_LPARAM / GET_Y_LPARAM
#include <chrono>

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

        LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
        {
            switch (msg)
            {
            case WM_SIZE:
                if (wp != SIZE_MINIMIZED)
                {
                    g_cw = LOWORD(lp); g_ch = HIWORD(lp);
                    // Resize on THIS thread (the one that also presents) — the whole point: no DXGI freeze.
                    if (g_running && g_cw > 0 && g_ch > 0)
                        systems::dx12::resize((u32)g_cw, (u32)g_ch);
                }
                return 0;
            case WM_MOUSEMOVE: g_mx = GET_X_LPARAM(lp); g_my = GET_Y_LPARAM(lp); return 0;
            case WM_LBUTTONDOWN: g_mdown = true;  SetCapture(hwnd); return 0;
            case WM_LBUTTONUP:   g_mdown = false; ReleaseCapture();  return 0;
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

        ShowWindow(g_hwnd, SW_SHOW);
        UpdateWindow(g_hwnd);

        RECT cr{}; GetClientRect(g_hwnd, &cr);
        g_cw = cr.right - cr.left; g_ch = cr.bottom - cr.top;

        systems::dx12::viewport_desc desc{};
        desc.hwnd = g_hwnd; desc.width = (u32)g_cw; desc.height = (u32)g_ch;
        if (!systems::dx12::initialize(desc)) { DestroyWindow(g_hwnd); g_hwnd = nullptr; return false; }

        g_running = true;
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

            // 2) delta time
            auto now = std::chrono::high_resolution_clock::now();
            float dt = std::chrono::duration<float>(now - last).count();
            last = now;
            if (dt < 0.f) dt = 0.f; else if (dt > 0.1f) dt = 0.1f;

            // 3) advance the game in managed code (scripts + camera + submit the scene)
            if (g_tick) g_tick(dt);

            // 4) render the submitted scene + present — same thread as the pump/resize, so never freezes
            systems::dx12::render_frame();
        }

        systems::dx12::shutdown();
        if (g_hwnd) { DestroyWindow(g_hwnd); g_hwnd = nullptr; }
        return true;
    }
}
