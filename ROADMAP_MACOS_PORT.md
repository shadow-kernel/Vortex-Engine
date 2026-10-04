# Vortex Engine — macOS Port (`feature/macos-port`)

Goal: the **runtime and the editor run natively on macOS** (Apple Silicon first), from **one code base**
shared with Windows. No Wine / Game Porting Toolkit — a real port with a Metal-capable backend.

`main` stays the Windows build of record (Vortex.slnx / MSBuild). The port lands on this branch phase by
phase; each phase is merged only once it builds **and** its verification step passes on both platforms.

---

## Status

| Phase | Scope | Status |
|-------|-------|--------|
| 0 | Foundation: CMake build, platform layer, portable core + audio on macOS | ✅ done (2026-09-29) |
| 1 | Backend seam: `graphics::Renderer` alias + `systems::render`, DX12 stays the Windows backend | ✅ done (2026-09-29) — a full RHI interface remains a follow-up, see below |
| 2 | macOS backend: SDL3 window/input + SDL GPU (Metal) + MSL shaders, native GameHost | ✅ done (2026-09-29) |
| 3 | Standalone player (.NET 10, `Vortex.Player`) + shared managed core (`Vortex.Core`) | ✅ done (2026-09-29) |
| 4 | Editor: .NET 10 + Avalonia 11 (`Vortex.Editor`), macOS-style UI, native Metal viewport | ✅ first complete version (2026-09-29) |
| 5 | Packaging: `.app` export from the editor ✅; signing / notarization / DMG / CI release | ⏳ |

### What works today (macOS 26, Apple Silicon, Xcode 26 / Apple clang 21)

- **Native engine** — `libVortexEngine.a` + `libVortexAPI.dylib` (all 17 API translation units, 320+ C entry
  points): ECS, importers, resources, physics, audio, and the **SDL GPU / Metal renderer** (`Engine/Graphics/SdlGpu`):
  PBR forward shading, fog, transparency sorting, GPU instancing, LOD, viewmodel layer, gizmos/selection outline,
  grid, skybox (all modes), post-FX uber pass (vignette, grain, chromatic aberration, colour grading, single-pass
  bloom approximation), overlay text via stb_truetype (system fonts, umlauts), secondary render targets with
  readback, frame capture, custom `.metal` material shaders with hot reload, **shadow maps** (directional
  cascades, spot atlas, point cube faces — same tile layout and light-buffer ABI as DX12), **SSAO** (half-res
  depth prepass, Alchemy AO, blur) and the **bloom mip chain** (prefilter, 13-tap downsample, tent upsample).
  `VortexRenderTest` renders and captures a frame on "Apple M3 Pro (metal)".
- **GameHost on SDL3** — own window, keyboard (VK ↔ scancode), relative mouse, text input, focus, F11 fullscreen.
- **Managed core** (`Managed/Vortex.Core`, net10.0) — the framework-free editor/runtime sources compiled as one
  assembly (`VORTEX_CORE`): scene/prefab/material formats, scripting API + Roslyn script compiler with collectible
  load contexts (hot reload), play-mode services, undo/redo, asset actions, viewport session, game packager.
- **Player** (`Managed/Vortex.Player`) — `Templates/Default3D` (Lobby with retained UI, Match with FPS
  controller) and `Templates/HorrorStarter` (viewmodel, HUD, lights, audio) run natively: `--project=… --scene=…`,
  smoke mode `--exit-after=N --capture=frame.bmp`.
- **Editor** (`Managed/Vortex.Editor`) — unified toolbar with traffic-light inset, native macOS menu bar,
  scene hierarchy (search, context menus, drag-drop reparent/reorder, prefab ops, rename), file tree, project
  browser (explorer + type views, import, create material/shader/script/scene/prefab/UI/animation/sound container,
  open, drag to viewport/hierarchy/inspector, rename/delete/reveal), viewport (native Metal NSView, tools W/E/R/X,
  grid/snap/gizmos/colliders/FP-layer toggles, camera selector, single/split/quad layouts with readback views,
  camera preview), inspector (all component editors, add/remove component, script fields, drag-drop assets),
  environment (fog + post-FX), console (level chips, filter), project hub (open/create from templates), project
  settings, build window (`.app` export), source control window, audio mixer, material editor, about, and the
  tool windows for sound containers, collision shapes, bone sockets, UI screens (.vui, live preview in the
  viewport) and animation clips (.vanim, tracks/keys/events, scrub preview). Play mode
  runs in the viewport (non-destructive snapshot/restore, mouse look via CoreGraphics cursor warp, P debug freecam)
  or in the standalone player process. Light + dark theme, system font.
- **Smoke tests** — `EngineTest` (audio 51/51), `VortexRenderTest` (Metal frame), player smoke run, editor smoke
  run (`VORTEX_SMOKE_FULL=1`: create/undo/redo/duplicate/delete/add component/create assets/save/split view/
  play+stop/export/scene switch) — all pass on this machine.

### Follow-ups (ordered)

1. **Renderer parity on Metal**: motion vectors (DLSS/frame-gen stay Windows/NVIDIA-only; render-scale
   fallback exists) and multithreaded culling. Shadows, SSAO and the bloom chain are done.
2. **Real RHI**: turn the `graphics::Renderer` alias seam into an abstract interface with DX12 and SDL GPU
   implementations sharing the scene/culling/instancing front end (today the front end is duplicated in
   `SdlGpuRenderer_Scene.cpp`).
3. **Editor sub-tools**: the Avalonia tool windows are simpler than their WPF twins (no drag-on-canvas UI
   layout, no graphical animation timeline) — extend them as needed.
4. **Packaging**: Developer-ID signing + notarization (today: ad-hoc signed `.app` + DMG from
   `tools/macos/make-app.sh`), GitHub release job. ~~Linux build (SDL GPU Vulkan + SPIR-V)~~ ✅ done — see
   **Linux** below; a distributable bundle (AppImage / Flatpak) is still open.
5. **Windows on the new stack**: build `Vortex.Editor` (Avalonia) on Windows with an HWND viewport, so both
   platforms share one editor; keep the WPF editor until parity.

---

## Building on macOS

```bash
brew install cmake ninja assimp sdl3 dotnet   # one-time toolchain (Xcode or the Command Line Tools must be installed)
tools/macos/make-app.sh --install --dmg       # everything: native engine, .NET layer, "Vortex Editor.app" in /Applications
```

`make-app.sh` builds the native engine (`macos-release`), publishes the editor and the player self-contained for
Apple Silicon, bundles them with the shaders and the project templates into `dist/macos/Vortex Editor.app`,
signs it ad-hoc and (with `--install`) copies it to `/Applications` (or `~/Applications`). The app needs neither
the repository nor an installed .NET afterwards; `.vortex` project files open with it.

For development without the bundle (`tools/macos/dev.sh` wraps these; IDE setups for Rider, VS Code and Xcode
are described in `Managed/README.md`):

```bash
cmake --preset macos-debug                    # configure (fetches the pinned DirectXMath release on first run)
cmake --build --preset macos-debug            # -> build/macos-debug/bin/{EngineTest, VortexRenderTest, libVortexAPI.dylib, Shaders/msl}
ctest --preset macos-debug                    # audio smoke test (+ render test with -DVORTEX_RENDER_TESTS=ON)

export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec
dotnet build Managed/Vortex.Managed.slnx      # Vortex.Core, Vortex.Player, Vortex.Editor
Managed/Vortex.Editor/bin/Debug/net10.0/Vortex.Editor            # editor (project hub on first start)
Managed/Vortex.Player/bin/Debug/net10.0/Vortex.Player --project=Templates/Default3D --scene=Match
```

`macos-release` is the optimised twin. Both presets use Ninja and write to `build/<preset>/`. The managed
binaries locate the newest native build tree automatically (or `VORTEX_NATIVE_DIR`).
The Windows build is unchanged: `Vortex.slnx` + `msbuild` as documented in the README. A CMake preset for
Windows exists (`windows-x64`) but has not been verified yet — keep using the solution there.

Optional Steam Audio on macOS: drop `libphonon.dylib` (or `phonon.bundle`) from the Steam Audio SDK next to
`EngineTest` / `libVortexAPI.dylib`; it is loaded on demand like `phonon.dll` on Windows.

---

## Linux (SDL GPU / Vulkan)

The Linux port is the same stack as the macOS one — the whole native engine, `libVortexAPI.so`, the .NET 10
player and the Avalonia editor — with **Vulkan** in place of Metal. Nothing in the engine core, the physics,
navigation, audio or managed layers needed changing; the work was the renderer's shader format, the editor's
viewport embedding and a handful of platform services.

```bash
# Arch Linux (other distributions in README.md and Scripts/linux-dev.sh)
sudo pacman -S --needed cmake ninja shaderc sdl3 assimp dotnet-sdk vulkan-icd-loader
Scripts/linux-dev.sh --release --editor
```

**What differs from the Metal backend**

| | macOS / Metal | Linux / Vulkan |
|---|---|---|
| Shader format | MSL source, compiled by the driver at load | SPIR-V, compiled by `glslc` at **build** time |
| Shader set | `Engine/Shaders/msl/*.metal`, one file per group | `Engine/Shaders/glsl/<base>.<Entry>.{vert,frag}`, one file per entrypoint (a SPIR-V module has a single `main`) |
| Loaded from | `Shaders/msl` | `Shaders/spirv` |
| Custom material shader | `.metal`, VSMain/PSMain | `.glsl`, one file compiled once per stage (`VORTEX_VERTEX_STAGE` / `VORTEX_FRAGMENT_STAGE`) by `glslc` on load — same hot reload |
| Editor viewport | SDL's Metal layer re-parented into the host `NSView` | SDL wraps the toolkit's X11 window (`SDL_PROP_WINDOW_CREATE_X11_WINDOW_NUMBER`) |
| Viewport input | SDL's event listener removed from the responder chain | SDL's `XSelectInput` mask handed back (`release_host_input`) |
| Pointer warp / Caps Lock | CoreGraphics | libX11 (`XWarpPointer` / `XQueryPointer`) |
| Trash | `NSFileManager trashItemAtURL:` | freedesktop.org Trash spec |
| UI font | SF / Helvetica | fontconfig (`fc-match`), with distribution paths as fallback |

`Engine/Graphics/SdlGpu/SdlGpuShaderFormat.h` is the single place that decides the format, the folder and the
entrypoint naming; every call site names a shader set plus an entrypoint and is backend-agnostic.

SDL GPU normalises the coordinate system across backends ("SDL will automatically convert the coordinate system
behind the scenes" — `SDL_gpu.h`), so the GLSL set is a **math-identical** port of the MSL set: no clip-space Y
flip and no winding change. The uniform blocks are byte-matched to the same C++ structs the DX12 cbuffers use —
the one trap is that std140 gives a scalar array a 16-byte stride, so MSL's tightly packed `float pad[3]` is
spelled out as individual scalars (see `Engine/Shaders/glsl/include/standard_common.glsl`).

**Verified on this machine** (Arch, kernel 7.2, RTX 5070, Vulkan 1.4, SDL 3.4.16, .NET 10): `ctest` 4/4 incl.
audio 51/51, `VortexRenderTest` renders and captures a frame on "NVIDIA GeForce RTX 5070 (vulkan)", the player
runs `Templates/Default3D` at ~3000 FPS, and the editor runs with the native viewport embedded in its window.

**Viewport input** is the one thing both backends have to undo. SDL grabs the input of the window it wraps —
on macOS by making itself the next responder, on X11 by selecting the full event mask, and `ButtonPress` can be
selected by only ONE X11 client per window. The editor never pumps SDL's event loop, so everything SDL grabbed
was dropped: clicks in the viewport did nothing and right-drag never started the fly camera. `claim_window()`
therefore hands the input back (`release_host_input`, re-applied after a resize because SDL re-selects its mask
on some window operations), and X11 then delivers those events to the toolkit's own window.

Set `VORTEX_INPUT_TRACE=1` to print every pointer and key event the viewport control receives, with the
control's screen rect — the first question when viewport input misbehaves is always whether the toolkit saw
the event at all.

**Open on Linux**: Wayland without XWayland (the editor's embedded viewport needs an X11 window; the standalone
player runs natively on Wayland), DLSS / frame generation (NVIDIA + Windows only), and a distributable bundle.

---

## What is portable, what is not (assessment, 2026-09-29)

| Layer | Size | macOS |
|-------|------|-------|
| Engine core: ECS, importers, resource logic, physics, audio | ~10k lines C++ | portable — done in Phase 0 |
| DX12 renderer + UI overlay (Direct2D / DirectWrite via D3D11On12) | ~9.6k lines | needs a second backend (Phase 2) |
| `Mesh` / `Texture` / `Material` / `ResourceRegistry` | ~1.8k lines | typed against `ID3D12*` — move behind the RHI (Phase 1) |
| `GameHost` + `InputSystem` | ~1.2k lines | Win32 window, message pump, cursor — replaced by SDL3 (Phase 2) |
| Shaders | 10 HLSL files, ~1.2k lines | HLSL stays the authoring language; DXC + SPIRV-Cross / SDL_shadercross produce MSL (Phase 2) |
| Streamline / DLSS 4 | 1 module | NVIDIA + Windows only; compiled out elsewhere (render-scale fallback exists) |
| VortexAPI (extern "C" shim) | 238 lines + 18 API TUs | trivial: export macro + no `DllMain`; 6 TUs already build on macOS |
| Editor (WPF, .NET Framework 4.8, AvalonDock, HwndHost) | ~75k lines C#, 33 XAML views | WPF does not exist on macOS → Avalonia UI + modern .NET (Phase 4) |
| Assimp, miniaudio, stb, Steam Audio | — | all have macOS builds |

---

## Decisions

Taken (2026-09-29): **SDL3 GPU on Metal** as the second backend (DX12 stays on Windows), **Avalonia + .NET 10** for the editor with a shared framework-free core. The original assessment follows for reference.

1. **Graphics backend for macOS.** Recommended: a small RHI (render hardware interface) with two backends —
   the existing DX12 backend stays on Windows (keeps DLSS 4 + Streamline), and **SDL3 GPU** becomes the
   second backend (native Metal on macOS, Vulkan on Linux, D3D12 fallback on Windows). Alternatives:
   Metal via metal-cpp (cleanest on macOS, MetalFX as the DLSS analogue, most code, Objective-C++);
   Vulkan + MoltenVK (translation layer, most boilerplate); SDL GPU only (least code, DLSS gone).
2. **Editor framework.** Recommended: **Avalonia UI** on .NET 8/9 — closest to WPF (XAML, MVVM, bindings),
   `Dock.Avalonia` replaces AvalonDock, `NativeControlHost` replaces `HwndHost` (an `NSView` on macOS that
   the renderer draws into). The Windows editor moves to the same code base.
3. **Order.** Runtime first, editor second: games exported on Windows run on macOS before the expensive
   editor port starts, and the backend is validated early.
4. **Platform layer.** **SDL3** for windows, input and gamepads on every platform — replaces the Win32
   GameHost, XInput, `Windows.Gaming.Input` and the hand-written DualSense HID code with one path.
5. **Build.** CMake for the native code (done), SDK-style `.csproj` + `dotnet build` for the managed side.

---

## Phases

Effort is a rough single-developer estimate; the order of magnitude is months, not days.

### Phase 0 — Foundation ✅
- `Engine/Common/Platform.h|.cpp`: the only place that knows the OS (debug output, env vars, dynamic
  libraries, temp/exe directories, timers, process memory) + MSVC secure-CRT shims for clang/gcc.
- CMake (`CMakeLists.txt`, `cmake/`, `CMakePresets.json`): portable-core / DX12 source split, DirectXMath
  fetched on non-Windows (+ `ThirdParty/sal/sal.h` stub), Assimp from Homebrew, Steam Audio headers.
- Source fixes: MSVC-only integer suffixes, backslash include paths, `_WIN64`-guarded math types,
  Win32 file enumeration → `std::filesystem`, wide-path calls guarded per platform, `phonon` via `dlopen`.
- VERIFY: `EngineTest` audio smoke test passes on macOS (51/51) — done.

### Phase 1 — RHI (render hardware interface) — ~2 weeks
- Extract a backend-neutral interface from `DX12Renderer` (device, swapchain, buffers, textures, pipelines,
  render targets, submit/present, gizmo + UI overlay hooks). `Mesh` / `Texture` / `Material` /
  `ResourceRegistry` lose their `ID3D12*` members (opaque backend handles instead).
- DX12 becomes the first implementation; `Skybox::apply_to_renderer`, `RenderLoop`, `MultiViewport`,
  the API TUs and the editor keep working unchanged on Windows.
- VERIFY: Windows build green, editor + exported game render identically (capture compare).

### Phase 2 — macOS backend — 4-6 weeks
- SDL3 for window, events, cursor capture, gamepads (also on Windows: replaces `GameHost`'s Win32 code).
- SDL GPU backend (Metal on macOS). Shader pipeline: HLSL → DXC → SPIR-V → SPIRV-Cross → MSL (or
  SDL_shadercross), at build time for the engine shaders and at run time for per-material `.hlsl`
  hot-reload. Text overlay without DirectWrite (font atlas via stb_truetype / FreeType).
- VERIFY: the Default3D template scene renders on macOS; capture compare against Windows.

### Phase 3 — Player — ~1 week
- A small .NET player instead of the renamed editor executable (`GameExporter.cs` already lists it as TODO);
  `.app` bundle export with the engine dylib, shaders and paks inside.
- VERIFY: a game exported on Windows starts and plays on macOS.

### Phase 4 — Editor — 8-12 weeks
- .NET 8/9 + Avalonia port of the 33 views; Roslyn replaces CodeDOM for script compilation; SDL3 gamepads
  replace WinRT/XInput/HID; Avalonia `StorageProvider` replaces WinForms dialogs; settings without the
  registry; `open -R` / Finder instead of `explorer.exe`; Rider / VS Code instead of `devenv`.
- VERIFY: the editor opens the templates, edits, plays and exports on both platforms.

### Phase 5 — Packaging — ~1 week
- Code signing + notarization (Apple Developer account), DMG, `macos-latest` release job.

---

## Conventions for the port (apply to all new native code)

- No `<Windows.h>` outside `Graphics/DX12/`, `Runtime/GameHost.cpp` and `Common/Platform.cpp`.
  Use `vortex::platform::*` (`Engine/Common/Platform.h`).
- Guard renderer-specific code with `#if VORTEX_HAS_DX12`; the CMake build defines `VORTEX_NO_DX12` off-Windows.
- Include paths use forward slashes; no MSVC-only literals (`ui64`), pragmas go behind `#ifdef _MSC_VER`.
- File paths: `std::filesystem` for enumeration/queries, `'/'` as the separator when joining.
- Interop boundary: pass UTF-8 `const char*`, never `wchar_t*` (32-bit on macOS; the C# side marshals
  UTF-16). `[DllImport("VortexAPI")]` without `.dll` lets .NET resolve `libVortexAPI.dylib` on macOS.
- Windows-only API translation units are listed explicitly in `VortexAPI/CMakeLists.txt`; when a TU stops
  needing DX12, move it to the core list.

## Known gotchas found in the assessment

- `RunGameHost` takes `const wchar_t*` while C# marshals UTF-16 — garbage on macOS until the boundary is UTF-8.
- `ScriptRuntime` / `GameExporter` compile scripts with CodeDOM (`CSharpCodeProvider`), which modern .NET
  does not ship — Roslyn (`Microsoft.CodeAnalysis.CSharp`) replaces it.
- The exported game is today the renamed editor executable plus `player.vortex`; macOS needs a real `.app`.
- The UI overlay (FPS, menu text) is Direct2D/DirectWrite via D3D11On12 — needs a font atlas on macOS.
- Gamepads: XInput, `Windows.Gaming.Input` and DualSense HID are three Windows-only paths — SDL3 covers all.
- `libVortexAPI.dylib` also exports four libc++ `std::filesystem` template helpers (weak, harmless); an
  exported-symbols list can hide them later.
