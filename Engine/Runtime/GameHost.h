#pragma once
#include <cstdint>

// GameHost — the native standalone game host (Phase 1 of the native render overhaul).
//
// Owns a real Win32 window + its DX12 swapchain + the game loop + present + input, ALL on one native
// thread. This is the fix for the WPF-HwndHost render-thread Present-freeze: because the window message
// pump, ResizeBuffers, and Present all live on the same thread, there is no cross-thread DXGI hazard, so
// scene switches and resizes no longer freeze the display — and the loop is uncapped (vsync optional).
//
// Gameplay still lives in C#: each frame the host calls a managed tick callback (run scripts + aim the
// camera + submit the scene); the host then renders the submitted scene + presents.
namespace vortex::runtime
{
    class GameHost
    {
    public:
        using tick_fn = void(*)(float dt); // managed callback: advance the game one frame (scripts/camera/submit)

        // Create the window + swapchain and run the loop until the window closes or request_exit(). Blocks.
        static bool run(uint32_t width, uint32_t height, const wchar_t* title);
        static void request_exit();
        static void set_tick_callback(tick_fn fn);
        static void set_vsync(bool enabled);

        // Input snapshot for the managed UI/gameplay (filled from the native wndproc; client-space pixels).
        static int  mouse_x();
        static int  mouse_y();
        static bool mouse_down();      // left button
        static int  client_width();
        static int  client_height();
        static bool key_down(int vk);  // virtual-key currently held

        // Event queues for the retained UI (drained once per frame on the tick thread — same thread as the
        // wndproc, so no locking). mouse_wheel returns + clears the accumulated notch delta; next_char pops the
        // next typed character (-1 if none) for text fields; next_key_pressed pops the next edge-pressed VK
        // (0 if none) for keybind capture.
        static int  mouse_wheel();
        static int  next_char();
        static int  next_key_pressed();

        // FPS mouse-look capture: while captured, the host hides the cursor and re-centers it every frame,
        // reporting the per-frame delta via mouse_dx/dy — so the cursor never leaves the window and look is
        // unbounded. Released (menu open) shows + frees the cursor so UI is clickable. Driven by the game.
        static void set_mouse_captured(bool captured);
        static bool mouse_captured();
        static int  mouse_dx();        // captured: cursor delta from center this frame (else 0)
        static int  mouse_dy();

        // Borderless-fullscreen toggle (also bound to F11 natively). Safe (no exclusive mode) -> ALLOW_TEARING ok.
        static void toggle_fullscreen();
        static bool is_fullscreen();
    };
}
