# Vortex Engine — Native Render Overhaul (feature/native-gamehost)

Goal: very high, stable FPS with good quality, large maps + many complex entities, full graphics
settings in a 3D options menu, and NVIDIA DLSS 4 (Multi Frame Generation) on RTX 50-series.

`main` stays at the stable v2.0.0 (CompositionTarget, ~60 FPS, fully reliable). All work below lands
on this branch and only merges to `main` once each phase is built **and verified** (capture + click +
scene-switch + resize tests, like the v2.0.0 verification).

---

## Why this rewrite (root cause, proven)
The standalone game ran the DX12 render loop on a **dedicated thread driving a WPF `HwndHost` child
window**. That hits a hard limit: after any swapchain operation (scene switch / resize) the render
thread's `Present` stops updating the display (DWM/airspace), even though the game logic + submit +
`RenderOnce` all run correctly (proven by logs: `PRESS IN=True` → `pending=Match` → `RE-SUBMIT
scene=Match`, yet the screen stayed frozen on the lobby). It is NOT a missing GPU flush (the resize path
already flushes and still froze). It is the cross-thread Present-on-a-WPF-hosted-HWND that's unfixable at
the managed layer. CompositionTarget (UI thread) avoids it but is hard-capped at ~60 FPS.

**Fix:** own the window + swapchain + loop + present + input entirely in native C++ (no WPF HwndHost on
the hot path). Present and window ownership live in the same native context → no freeze, uncapped FPS.

---

## Phase 1 — Native GameHost main-loop  ★ foundation (fixes the freeze + uncaps FPS)
- New native module `Engine/Runtime/GameHost.{h,cpp}`: creates a real Win32 window (`RegisterClassEx` +
  `CreateWindowEx`), its own DX12 swapchain (vsync optional), and runs the loop on the thread that owns
  the window message pump: `PeekMessage`/`DispatchMessage` → step → render → present.
- Per-frame managed callback: `GameHost` invokes a function pointer into the C# gameplay layer each frame
  (`step(dt)` runs scripts + camera; the host renders the submitted scene). Marshalled via a
  `SetGameTickCallback(fnptr)` export so scripts (VortexBehaviour) keep running in C#.
- Input: WM_* in the native wndproc → fed to the C# input/UI host (mouse pos in client space from
  `WM_MOUSEMOVE`, buttons from `WM_LBUTTONDOWN/UP`, keys from `WM_KEYDOWN`). UI hit-test becomes 100%
  reliable (no cross-thread cursor mapping).
- Resize: `WM_SIZE` on the same thread → `ResizeBuffers` on the same thread that presents → no freeze.
- Scene switch: runs between frames on the same thread → no freeze.
- Exports: `RunGameHost(width,height,title)`, `SetGameTickCallback`, `RequestGameHostExit`,
  `SetGameHostVSync`. The C# player (`App.BootPlayer`) launches the GameHost instead of the WPF
  GameWindow; the editor "Play in new window" can keep the WPF path (vsync-capped, fine behind the editor).
- VERIFY: lobby renders; SPIELEN → Match renders (no freeze); maximize/F11/drag-resize stay sharp; FPS
  counter >> 60; ESC menu clickable. Capture + simulated click, multiple runs.

## Phase 2 — Render refactor for large maps + many entities
- Persistent per-instance GPU buffers (already have instancing); add: stable instance buffer ring,
  per-material instance batches built once + updated only on change (dirty flags).
- Frustum cull (have) + **distance cull** against the render-distance setting.
- Indirect draw (`ExecuteIndirect`) for the big static batches to cut CPU draw submission.
- Spatial partition (uniform grid / loose octree) so culling is O(visible), not O(all) — required for
  large maps with many entities.
- VERIFY: a stress scene with thousands of objects holds high FPS; cull stats logged.

## Phase 3 — LOD + render distance (near = full detail, far = cheap)
- Per-mesh LOD chain (LOD0/1/2 + billboard/impostor for very far). Pick LOD by screen-space size /
  distance. Far objects render coarse meshes; beyond render-distance they're culled.
- "Render Distance" setting drives the far plane + LOD distances.
- VERIFY: distant objects visibly use cheaper LOD; near objects full detail; FPS scales with distance.

## Phase 4 — Graphics settings + 3D Options menu (fully in UI)
- Settings model (persisted in project settings + a runtime SettingsService): Render Distance, Quality
  Preset (Low/Med/High/Ultra), Shadow quality, Anti-aliasing (off/FXAA/TAA), Texture quality, VSync,
  Resolution Scale, Max FPS, DLSS mode (Phase 5).
- A real **Options** screen in the lobby (the 3D menu already exists; wire the OPTIONS tab to a settings
  panel drawn with the UI overlay; live-apply each setting through the SettingsService → engine).
- VERIFY: each setting changes the renderer live; persists across restart.

## Phase 5 — NVIDIA DLSS 4 (Multi Frame Generation) — RTX 50-series
- Integrate the **NVIDIA Streamline SDK** (`sl.interposer`, `sl.dlss`, `sl.dlss_g`) into the DX12
  renderer. Requires the Streamline + DLSS SDK binaries in the repo (`ThirdParty/Streamline/`) and an
  RTX 50-series GPU (user has RTX 5070 ✓).
- Feed Streamline: color, depth, motion vectors, jitter, exposure; create the swapchain via the SL proxy.
- DLSS-SR (super resolution, quality/balanced/perf) + DLSS-G frame generation (2x/3x/**4x**).
- Settings entry (Phase 4): DLSS Off / Quality / Balanced / Performance + Frame Gen 2x/3x/4x.
- VERIFY: DLSS on a 50-series GPU multiplies FPS; off-path unaffected on other GPUs.
- NOTE: this phase needs the proprietary SDK present; it is the largest single integration and lands last.

---

## Phase 1 — implementation findings (investigated, scoped)
Native code surveyed; the exact build plan:
- A native `RenderLoop` already exists (`Engine/Runtime/RenderLoop.{h,cpp}`) — it runs `render_frame()` on a
  `std::thread`. BUT it renders into the swapchain created on the **WPF `HwndHost` child window** → same
  Present-freeze. So the loop is fine; the **window** is the problem.
- The renderer's `initialize_render_viewport(HWND, w, h)` (VortexAPI `InitializeRenderViewport`) creates the
  device + swapchain from **any** HWND. → GameHost REUSES it on a NEW native window — no renderer rewrite.
- New `Engine/Runtime/GameHost.{h,cpp}` (single thread owns everything):
  1. `RegisterClassEx` + `CreateWindowEx` → a real native Win32 window (resizable, maximizable).
  2. `DX12Renderer::initialize_render_viewport(hwnd, w, h)` on that window.
  3. Loop on THIS thread: `PeekMessage`/`Translate`/`Dispatch` → `tick_cb(dt)` (managed) → `render_frame()`
     → present. Present + wndproc + resize all on one thread → **no cross-thread DXGI / no freeze**.
  4. wndproc: `WM_SIZE` → `resize(w,h)` (same thread); `WM_MOUSEMOVE/LBUTTON*` → cache mouse/buttons;
     `WM_KEYDOWN/UP` → key state; `WM_CLOSE` → exit. Fed to C# via getters or pushed into the tick.
- New exports (VortexAPI): `RunGameHost(w,h,title)` (blocks, runs the loop), `SetGameTickCallback(void(*)(float))`
  (managed delegate marshalled to a fnptr — runs scripts + camera + submit each frame), `RequestGameHostExit()`,
  `SetGameHostVSync(bool)`, plus host-input getters (`GameHostMouseX/Y/Down`, key state).
- C# side: `App.BootPlayer` for the shipped game launches the GameHost (mount pak → set tick callback →
  `RunGameHost`) instead of the WPF `GameWindow`. The tick callback = today's OwnedFrame body minus the WPF
  mouse mapping (input now comes from the native wndproc). Editor "Play in new window" keeps the WPF path.
- Build: add GameHost.{h,cpp} to `Engine.vcxproj`; exports to `VortexAPI.cpp`; tick-callback delegate +
  P/Invokes to the C# DllWrapper.
- VERIFY (gate before merge): lobby renders; SPIELEN→Match renders (no freeze); maximize/F11/drag sharp;
  FPS >> 60; ESC menu clickable — capture + simulated click, multiple runs.

## Status
- [x] Branch + roadmap.
- [x] Phase 1 architecture investigation + precise implementation plan (above).
- [x] Phase 1a — native GameHost module written + **builds green** (`Engine/Runtime/GameHost.{h,cpp}`:
      native Win32 window + reuses `systems::dx12::initialize/resize/render_frame` on it + one-thread loop
      (pump → tick callback → render+present) + wndproc input). Exports added to VortexAPI
      (`RunGameHost`, `SetGameTickCallback`, `RequestGameHostExit`, `SetGameHostVSync`, `GameHostMouseX/Y/Down`,
      `GameHostClientWidth/Height`, `GameHostKeyDown`). Registered in Engine.vcxproj. Whole solution compiles.
- [x] Phase 1b — C# launch wiring DONE + the core VERIFIED: P/Invokes (VortexRendering.cs) + managed
      `GameHostTick` (input/UI + step + scripts + camera + submit) + `BootPlayer` now launches `RunGameHost`
      (hidden WPF window only holds the project DataContext). RESULT (measured): native window renders the
      lobby; **uncapped FPS = 165 in lobby (was hard-capped 60)**; SPIELEN click registers reliably (native
      client-space mouse, `PRESS IN=True`); `Scene.Load` fires (`pending=Match`); `ActiveScene` switches
      (`scene=Match`); the loop **keeps rendering with NO FREEZE** (54–97 FPS in Match). The whole reason for
      this rewrite — the Present-freeze on scene-switch/resize — is GONE. ✓
- [x] Phase 1c — scene-switch visual load: **RESOLVED, and the real root cause was a measurement artifact**.
      The earlier "lobby still renders after the switch" was NOT a real present bug — it was a **GDI
      window-capture artifact**: the swapchain is `DXGI_SWAP_EFFECT_FLIP_DISCARD`, and `CopyFromScreen`/
      `PrintWindow` read the window's GDI *redirection surface*, which flip-model presents never update, so
      automated screenshots froze on the last lobby frame while the **actual on-screen image was the correct
      Match scene the whole time** (proven by a native back-buffer capture: forest island + "HP 100" HUD +
      crosshair render perfectly). Verified via a new reliable path: `request_capture()` →
      `CaptureFrame(path)` export → bound to **F12** in `GameHostTick` (copies the real back buffer to a
      32-bit BMP). Also landed a correctness hook, `on_scene_switch()` (export `OnSceneSwitch`, called from
      `GameRuntime.SwitchScene` before `ClearAllRenderables`): GPU flush + `UIOverlay::invalidate_targets()` +
      clear render/submit queue + reset per-buffer fences → no in-flight use-after-free when `DeleteMesh`
      frees the old scene's buffers, no stale overlay carry-over.
- [x] Phase 1d — full uncap beyond monitor refresh: `DX12Swapchain` now creates with
      `DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING` (queried via `IDXGIFactory5::CheckFeatureSupport`), `ResizeBuffers`
      carries the same flag, and `present()` passes `DXGI_PRESENT_ALLOW_TEARING` when **!vsync**. Removed the
      per-frame submit: `GameHostTick` now **submits a scene ONCE** (re-submits only when the active scene
      reference changes) and relies on `swap_render_queue` reusing the render queue — the camera is set
      separately, so a static scene re-renders every frame with no 300-entity walk / ~470 P/Invokes.
      **MEASURED (standalone exe, verified via F12 native capture): lobby ~1280 FPS, Match ~1850 FPS** (was
      165 / 54–97). Target of ≥1500 FPS met. Match geometry renders correctly at speed.
- [ ] Phases 2–5 (dirty-flag re-submit for animated entities, spatial culling/LOD, 3D options menu, DLSS 4).

Each phase is built + verified + committed before the next. Merges to `main` only when a phase is green.
