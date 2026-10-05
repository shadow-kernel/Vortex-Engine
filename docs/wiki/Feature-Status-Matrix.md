# Feature Status Matrix

The honest, verified current state of Vortex Engine (**v3.0.0**), re-verified against the code in October 2026. This page is the single source of truth for *what the engine actually does today*.

**Legend:** ✅ complete · 🟡 partial · 🧪 stub (code exists, does nothing real) · ❌ missing (planned milestone linked)

**Reading the notes:** Vortex renders through **DX12** on Windows and **SDL GPU** on macOS (Metal) and Linux (Vulkan). It has two editors on one shared C# core: the **WPF editor** (`Editor/`, Windows only) and the cross-platform **Avalonia editor** (`Managed/Vortex.Editor`; #183 plans to retire the WPF editor). When a feature works on one backend, build or editor only, the note says so.

Shipped milestones: [M1 v2.6.0 Audio](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) · [M2 v2.7.0 Horror Essentials](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) · [M3 v2.9.0 Global Asset DB](https://github.com/shadow-kernel/Vortex-Engine/milestone/3) · [M4 v2.10.0 Asset Store & Claude Sound Studio](https://github.com/shadow-kernel/Vortex-Engine/milestone/4) (v2.8.0, the macOS/Linux + Jolt release, had no milestone)

Open milestones: [M5 v3.0.0 Claude-Native](https://github.com/shadow-kernel/Vortex-Engine/milestone/5) · [M6 v3.1.0 Physics v2](https://github.com/shadow-kernel/Vortex-Engine/milestone/6) · [M7 v3.2.0 AI & Navigation](https://github.com/shadow-kernel/Vortex-Engine/milestone/7) · [M8 v3.3.0 VFX](https://github.com/shadow-kernel/Vortex-Engine/milestone/8) · [M9 v3.4.0 World & Streaming](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) · [M11 v3.5.0 Multiplayer I: Netcode](https://github.com/shadow-kernel/Vortex-Engine/milestone/11) · [M12 v3.6.0 Animation v2 & Characters](https://github.com/shadow-kernel/Vortex-Engine/milestone/12) · [M13 v3.7.0 Shooter Framework](https://github.com/shadow-kernel/Vortex-Engine/milestone/13) · [M14 v3.8.0 Multiplayer II: Online Services](https://github.com/shadow-kernel/Vortex-Engine/milestone/14) · [M15 v3.9.0 AAA Rendering](https://github.com/shadow-kernel/Vortex-Engine/milestone/15) · [M10 v4.0.0 XXL 10x Performance](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) · [M16 v4.1.0 Production & LiveOps](https://github.com/shadow-kernel/Vortex-Engine/milestone/16) · [M17 v4.2.0 Battle Royale Systems](https://github.com/shadow-kernel/Vortex-Engine/milestone/17) · [M18 v5.0.0 Battle Royale (16+ Players)](https://github.com/shadow-kernel/Vortex-Engine/milestone/18)

Browse open work by area: [rendering](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Arendering) · [audio](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aaudio) · [physics](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aphysics) · [animation](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aanimation) · [ai](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aai) · [scripting](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Ascripting) · [ui-vui](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aui-vui) · [editor](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aeditor) · [claude](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aclaude) · [assets](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aassets) · [networking](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Anetworking) · [build-ci](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Abuild-ci) · [br-blocker](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Abr-blocker)

---

## Platforms

The Windows binaries (installer, portable ZIP and runtime pack, used by both editors) come from the MSBuild solution `Vortex.slnx`. That build compiles Jolt and Recast as no-op stubs (#182), and the DX12 renderer draws no particles. The CMake builds for macOS and Linux have Jolt, Recast and particle rendering.

| Component | Windows (DX12) | macOS (Metal) | Linux (Vulkan) |
|---|---|---|---|
| Native engine | ✅ `Vortex.slnx` (MSBuild); the CMake `windows-x64` preset is unverified | ✅ CMake `macos-*` presets (Homebrew SDL3 + Assimp) | 🟡 CMake `linux-*` presets, `glslc` for SPIR-V; not in CI |
| Jolt physics + Recast navigation | ❌ no-op stubs in the MSBuild build | ✅ Jolt 5.3.0, Recast/Detour 1.6.0 | ✅ same as macOS |
| Particle rendering | ❌ simulated, never drawn | ✅ | ✅ |
| Standalone player (`Vortex.Player`) | 🟡 in the installer (`Editor\player`), never run in CI | ✅ inside the app bundle | 🟡 build from source |
| WPF editor (classic) | 🟡 .NET Framework 4.8; kept as *Vortex Engine (Classic)* for v3.0, removed in v3.1 | — | — |
| Avalonia editor (the editor) | ✅ the default since v3.0: DX12 in a child HWND (click routing, mouse look), gamepads, installer + self-update; CI smoke from the installed layout (WARP) gates PRs | ✅ Metal view; CI smoke gates the app build | 🟡 X11 (XWayland) window + Vulkan; from source, no CI |
| Game export | ✅ WPF: Windows; Avalonia: `.exe` + `.zip` | 🟡 Avalonia: `.app` + `.dmg` (ad-hoc signed, SDL3/Assimp bundled) | 🟡 Avalonia: folder + `.desktop` + `run.sh` (source build) |
| Distribution | 🟡 installer + portable ZIP from CI, unsigned | 🟡 DMG from CI (arm64, macOS 26+), not notarised; Homebrew libraries bundled (`tools/macos/bundle-dylibs.sh`) | ❌ build from source |
| CI | ✅ PR gate: MSBuild + managed build + managed tests | ✅ PR gate: CMake + native tests + managed tests; app/DMG on main and tags | ❌ no job |

Exports for another OS need that OS's runtime pack (`tools/make-runtime-pack.*`); CI only builds the Windows pack.

## Rendering (DX12 · SDL GPU: Metal / Vulkan)

| Feature | Status | Notes |
|---|---|---|
| Graphics API (DX12 + SDL GPU) | ✅ | DX12 on Windows (WARP fallback via `VORTEX_DX12_WARP`); SDL GPU on macOS (Metal/MSL) and Linux (Vulkan/SPIR-V) behind one interface (`Engine/Graphics/Backend.h`) |
| PBR lighting (Cook-Torrance GGX) | ✅ | Directional + point + spot + hemisphere ambient + rim + sky reflections; same math in `standard.hlsl`, `standard.metal` and GLSL |
| Directional light | ✅ | One per frame (`Lighting.SetDirectional`), with cascaded shadows |
| Point lights | ✅ | 16 max/frame, windowed inverse-square falloff, full PBR per light |
| Spot lights | ✅ | 8 max/frame, inner/outer cone smooth fade |
| Area lights | 🧪 | `LightType.Area` exists on the Light component but is never sent to the renderer |
| Tonemapping (ACES) | ✅ | ACES fit + gamma 2.2 inside the material shader (unlit materials: Reinhard) |
| Sky & atmosphere | 🟡 | Procedural gradient sky + sun disc/glow on both backends; native texture/cubemap sky is a TODO (the editor draws texture skies on an unlit sphere); physical sky → [M15](https://github.com/shadow-kernel/Vortex-Engine/milestone/15) |
| Render-scale & upscaling | ✅ | [0.25–2.0] offscreen RT + bilinear upscale on both backends |
| DLSS Super-Resolution | 🟡 | DX12 + RTX only (Streamline, hardware-gated, bilinear fallback); needs the Streamline DLLs staged at build time, CI-built releases ship without them |
| DLSS Frame Generation | 🟡 | x2/x3/x4 + Reflex markers; same DX12 + DLL caveat; no per-GPU frame-generation check in code |
| Motion vectors | 🟡 | Camera-only RG16F reprojection, DX12 only, recorded only for DLSS; per-object → [M12](https://github.com/shadow-kernel/Vortex-Engine/milestone/12) |
| GPU instancing | ✅ | Per-instance world matrices in a second vertex stream; one draw per (mesh, material, LOD) run |
| GPU skinning | ✅ | 4-bone LBS on both; DX12 32K matrices/frame (double-buffered halves), SDL GPU 64K |
| Frustum culling | ✅ | Bounding spheres vs. 6 planes; the parallel pre-cull above 262K instances is DX12 only |
| Distance & density LOD | ✅ | Deterministic 1/2 and 1/4 thinning by distance (no flicker) |
| Geometric LOD (multi-level) | ✅ | Up to 4 levels, decimated at import; non-skinned meshes only |
| Render distance culling | ✅ | `SetRenderDistance`, 0 = off |
| Multithreaded culling & packing | 🟡 | DX12: persistent worker pool (1–8 workers) from 2,048 instances; SDL GPU culls on one thread |
| Mesh rendering | ✅ | Rigid + skinned + instanced; queue/sort/batch per frame |
| Material system (PBR props) | ✅ | Base color/metallic/roughness/AO/normal/height, unlit, UV tiling, packed ORM, blend mode, per-material custom shader; emissive only lights unlit materials (no emissive map) |
| Texture binding & sampling | ✅ | 6 material slots (adds height), 16x anisotropic, mip chains on both backends; no BCn compression → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Normal + parallax mapping | ✅ | DirectX + OpenGL conventions, per-pixel tangent frame, strength per material; single-step parallax |
| Grid / gizmo / camera-gizmo rendering | ✅ | Grid + always-on-top gizmos on both; the camera frustum is drawn from gizmo meshes (native `RenderCameraGizmo` is an empty stub) |
| Wireframe mode | ✅ | Dedicated pipeline on both backends |
| Multi-viewport render targets | ✅ | Up to 8 secondary RTs + CPU readback (previews/thumbnails); DX12 draws them item by item (no culling or instancing), SDL GPU runs the full scene path |
| Back-buffer capture | ✅ | Final frame incl. UI to BMP (F12 in the hosts), both backends |
| 2D UI overlay | ✅ | DX12: D3D11On12 + D2D/DirectWrite; SDL GPU: own overlay (`SdlGpuOverlay.cpp`, stb_truetype text); drives Vortex.UI + VUI |
| Standalone game window + native GameHost | ✅ | Win32 (DX12) or SDL3 (Metal/Vulkan): window, pump, tick and render on one thread, uncapped FPS |
| VSync control / depth testing / emissive-unlit / aniso filtering | ✅ | VSync off = tearing (DX12) or immediate/mailbox (SDL GPU); D32_FLOAT depth |
| Performance telemetry | ✅ | FPS, draw calls, vertices, instances tested/drawn on both; frame-generation FPS on DX12 |
| First-person viewmodel layer | ✅ | Own FOV (10–120°) and 0.01 m near plane, drawn after a depth clear, both backends |
| Camera component in the main view | 🟡 | Main and game views always render 0.1–1000 m perspective; Camera near/far/orthographic only apply to secondary viewports (FOV works) |
| HDR rendering | 🟡 | Float lighting, but tonemapping in the material shader, 8-bit targets, SDR swapchains; HDR output → [M15](https://github.com/shadow-kernel/Vortex-Engine/milestone/15) |
| Transparency & alpha blending | ✅ | Alpha + additive blend pipelines on both, depth write off, back-to-front sort (up to 4,096 draws); skinned and custom-shader materials stay opaque |
| Alpha test / cutout | 🧪 | "Cutout" + cutoff can be authored, but renders opaque (no clip path, `MaterialService.cs`) |
| Post-processing pipeline | ✅ | Pass chain between scene and UI on both backends; per scene (`SceneSettings`, Environment panel) or `PostFx` script API; editor viewport only in preview |
| Post-FX: bloom | ✅ | Soft-knee bright pass + down/up mip chain (DX12: 6 float levels, SDL GPU: 5 8-bit levels) |
| Post-FX: vignette / film grain / chromatic aberration | ✅ | One combined pass, both backends |
| Post-FX: color grading | ✅ | Exposure, contrast, saturation, temperature, tint; no LUTs or auto-exposure → [M15](https://github.com/shadow-kernel/Vortex-Engine/milestone/15) |
| SSAO | ✅ | Half-res depth prepass, 12 samples + blur, darkens ambient only; caster cap DX12 16,384 / SDL GPU 4,096 |
| Depth of field / motion blur | ❌ | → [M15](https://github.com/shadow-kernel/Vortex-Engine/milestone/15) |
| Reflections (SSR/probes) | 🟡 | Metals reflect the gradient sky (Fresnel, roughness-blurred) on both; no SSR, probes or cubemaps → [M15](https://github.com/shadow-kernel/Vortex-Engine/milestone/15) |
| Shadows: directional (cascaded) | ✅ | 3 cascades x 2048², stabilised, 3x3 PCF, 80 m default distance (settable from scripts), both backends |
| Shadows: spot (the flashlight) | ✅ | Up to 4 per frame, 2048² atlas tiles, one hardware-filtered tap (hard edges), both backends |
| Shadows: point | ✅ | Up to 2 lights, 6 x 1024² faces in an atlas (no cube maps), 3x3 PCF, both backends |
| Shadow settings + casters | 🟡 | Strength + bias work; Soft = Hard, per-light resolution and normal bias are ignored; skinned meshes, the viewmodel and transparent materials cast no shadows |
| Fog (depth/height) | ✅ | Exp² distance fog + height falloff on both backends; per scene or `Atmosphere.SetFog`; volumetric fog → [M8](https://github.com/shadow-kernel/Vortex-Engine/milestone/8) |
| Anti-aliasing (TAA/MSAA/FXAA) | ❌ | None outside DLSS (every pipeline is single-sample); TAA → [M15](https://github.com/shadow-kernel/Vortex-Engine/milestone/15) |
| Particle system | 🟡 | Drawn only by SDL GPU (Metal/Vulkan); DX12 simulates but draws nothing (see VFX) |
| Decals | ❌ | → [M8](https://github.com/shadow-kernel/Vortex-Engine/milestone/8) |
| Terrain & foliage | ❌ | Sculpting, splat painting, foliage brush → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Global illumination / DXR / mesh shaders / bindless | ❌ | Fixed hemisphere ambient; GI → [M15](https://github.com/shadow-kernel/Vortex-Engine/milestone/15); mesh shaders + bindless → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10); DXR backlog |

## Audio

The miniaudio engine from v2.6.0 runs the same way in both editors and the player on every platform; Steam Audio is an optional extra.

| Feature | Status | Notes |
|---|---|---|
| AudioSource component | ✅ | 19 serialized fields: clip or `.vsndc` container, volume, pitch, loop, play on awake, mute, spatial blend, min/max distance, rolloff, priority, pan, reverb mix, doppler, spread, streaming, output bus, HRTF, occlusion |
| AudioListener component | ✅ | The first enabled listener hears; falls back to the main camera |
| Reverb Zone component | ✅ | Sphere or box with a soft edge; zones blend by listener position into one global Freeverb reverb |
| Editor UI for audio components | ✅ | Both editors: create/add commands, inspectors with Preview, drag-assign clips, viewport icons; WPF can't edit box-zone extents |
| Audio import + decoding | ✅ | miniaudio 0.11.22 + stb_vorbis: `.wav`, `.flac`, `.mp3`, `.ogg` (no Opus); waveform thumbnails |
| Playback engine (device + mixing) | ✅ | `Engine/Runtime/Systems/AudioEngine.cpp`: WASAPI / Core Audio / PulseAudio-ALSA-JACK; silent no-device mode (CI) |
| Runtime API wiring | ✅ | `VortexAPI/Api/AudioApi.cpp` → `VortexAudio.cs`, ticked by `StepRuntime` (play mode + player) |
| 3D spatialization (attenuation, panning, doppler) | ✅ | miniaudio spatializer: log/linear rolloff with min/max distance, panning, doppler, spatial blend; "Custom" rolloff acts as logarithmic |
| Voice management (play/stop/pause, priority, stealing) | ✅ | 32 voices (hard-coded), priority 0–256 with stealing, live volume/pitch/pan; no virtual voices |
| Streaming playback (music/ambience) | ✅ | Per AudioSource, always for music; shipped builds stream from the `.vpak` entry in memory |
| Mixer (buses, routing, ducking) | ✅ | Fixed Master/Music/SFX/Ambience/UI buses: volume, mute, peak/RMS meters, ducking rules; `ProjectSettings/AudioMixer.json`, packed into exports |
| Audio Mixer window | ✅ | Both editors (faders, mute/solo, meters, ducking); the Steam Audio switch is in the Avalonia editor only |
| Vortex.Audio scripting API | ✅ | `Audio.PlayOneShot/PlayOneShot2D`, bus volumes, `Audio.Music` (play/crossfade/stop), `GetAudioSource()` handle with fades |
| Random sound containers (`.vsndc`) | ✅ | Weighted random, no immediate repeat, pitch/volume ranges; editor in both editors; no sequence/switch containers |
| Fades | ✅ | FadeIn / FadeOut / FadeTo / crossfade as per-sample ramps |
| 3D audio gizmos + edit-mode audition | ✅ | Speaker/listener icons, range spheres, zone bounds; inspector Preview, browser click-to-play, container Roll |
| Player audio settings | ✅ | Shipped games persist bus volumes (`GameAudioSettings`); `Settings.SetMasterVolume` drives the Master bus |
| Steam Audio (HRTF + occlusion) | 🟡 | Optional: the phonon runtime is loaded if present, but no build ships it (fetch script is Windows-only); opt-in per project + source; HRTF voices get the engine's distance falloff but no doppler or spread |
| DSP effects, mixer snapshots, custom buses, device choice | ❌ | No filters/EQ/compressor, fixed bus list, default output device only |
| Audio tests | ✅ | `EngineTest/TestAudio.h` (ctest `AudioSmokeTest`) on macOS CI; not run on Windows or Linux |

## Assets

| Feature | Status | Notes |
|---|---|---|
| Asset Browser ("Project" tab) | ✅ | Both editors: thumbnail grid, kind tabs, search, breadcrumb sync |
| Assimp model import | ✅ | .fbx/.obj/.gltf/.glb/.dae/.3ds/.blend/.vmesh, multi-file, auto texture detection |
| Texture import | 🟡 | stb_image: .png/.jpg/.tga/.bmp/.psd/.hdr (decoded to 8-bit); `.dds` is listed but can't be decoded; no BCn compression → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Audio import | ✅ | .wav/.mp3/.ogg/.flac with waveform thumbnails and click-to-play in both editors |
| Material system (.vmat) | ✅ | Full PBR maps + blend modes + custom shader slot + live preview; store materials keep their real-world size (Avalonia `MaterialFit`) |
| Prefab system (.ventity) | ✅ | Linked instances, Apply/Revert in both editors; Avalonia adds an override bar, Unpack and a Prefab Editor window |
| Animation (.vanim), UI (.vui), VFX (.vfx), sound container (.vsndc) assets | ✅ | Created in the browser, each with its own editor (the VFX editor is Avalonia only) |
| Per-project AssetDatabase + .vmeta sidecars | ✅ | Stable GUIDs, import settings, tags, content hash, license/author/source, Sound Studio recipe |
| Tags + search | ✅ | Predefined + custom tags, name filter; tag filter in the Avalonia editor |
| Thumbnails/previews | ✅ | Offscreen render + readback on DX12 and SDL GPU; disk cache in `.ve/thumbs` (Avalonia) |
| Import dialog, drag-drop placement, context menus | ✅ | The Avalonia import dialog flags files already in the library |
| VortexPak (.vpak) shipped format | ✅ | DEFLATE + XOR, core pak + per-scene layer paks, AssetVfs mounting |
| Scene streaming (pak layers) | ✅ | Each scene's pak is mounted when the scene loads |
| Shader assets | 🟡 | One file per backend: `.hlsl` (DX12), `.metal` (macOS), `.glsl` (Linux), no translation; validation + hot reload; no visual shader editor |
| Content hashing | ✅ | SHA-256 of every import in its `.vmeta` (`Editor/Core/Assets/Library/ContentHash.cs`), plus a backfill command |
| Global/cross-project asset library | ✅ | Machine-wide SQLite catalog + SHA-256 blob store; Library tab, bundles, maintenance, project indexer, tag manager in the Avalonia editor; WPF only registers its imports |
| Asset Store tab | ✅ | Avalonia only: Poly Haven, ambientCG, Kenney, poly.pizza, Freesound (HQ previews), Sketchfab in-app + Mixamo/Sonniss guided; queue, resume, checksums |
| License tracking + credits | ✅ | License/author/source per asset, badges, NC/ND filter, license check before builds, `CREDITS.md` (Avalonia export only) |
| Claude Sound Studio (AI SFX generation) | ✅ | Avalonia only: procedural (offline), ElevenLabs, fal.ai and Stability backends, optional Claude prompt design; takes go to the library with their recipe |
| Dependency tracking | 🧪 | `.vmeta` has a dependency list, but nothing fills it (`AddDependency` has no callers) |
| Asset deletion | 🟡 | Both browsers delete file + `.vmeta` (Avalonia: OS trash, undoable); no in-use check; `AssetDeletionService` is unused; dependency UI is backlog |
| Bulk operations (batch rename/retag/reimport) | 🟡 | Avalonia: multi-select delete/duplicate/move, library bulk tagging + tag manager; no batch rename or reimport (backlog) |

## Editor

| Feature | Status | Notes |
|---|---|---|
| Editor shells | ✅ | Avalonia (the editor on every platform): fixed 3-column layout with splitters, the macOS menu bar or a menu row above the toolbar (Windows / Linux), the toolbar as the window's title bar, System/Dark/Light themes; WPF (*Vortex Engine (Classic)*, Windows, until v3.1): AvalonDock panes, borderless DWM chrome, dark theme |
| Scene Hierarchy / File Explorer / Asset Browser panels | ✅ | Both editors: tree + selection service + drag-drop |
| Dynamic Inspector | ✅ | Avalonia: typed cards for every component + editable generic fallback; WPF has no cards for Rigidbody, joints, ragdoll, particles, AI, Hand Pose/Look-At/Foot IK |
| Viewport | ✅ | WPF: DX12 via HwndHost; Avalonia: Metal view (macOS), X11 window + Vulkan (Linux), DX12 child HWND (Windows, new) |
| Undo/Redo | ✅ | Shared manager: command merging (500 ms), 300-command limit; History window in the Avalonia editor |
| Material / Mesh / Texture / Model editors | ✅ | Both editors: live previews, channel toggles, import settings; Avalonia adds *Fit to Selected Object* |
| Collision Editor | ✅ | Both editors; Avalonia also edits the physics material and Rigidbody |
| Animation/Keyframe Editor | ✅ | Both editors: dope sheet, bone tree, pose inspector, event markers, undo per keyframe |
| Socket Editor | ✅ | Both editors: bone picker, offsets, live preview |
| UI (VUI) Editor | ✅ | Both editors; Avalonia adds drag/resize, undo, Test mode, tooltip and sound fields |
| Audio Mixer + Sound Container editor | ✅ | Both editors |
| VFX (particle) editor | ✅ | Avalonia only: `.vfx` emitters + beam, live preview, hot reload on save; no undo |
| Navigation window | ✅ | Avalonia only: bake + navmesh overlay; needs a Recast build (see AI & Navigation) |
| Git Source Control | ✅ | Both editors: branch/diff/commit/push/pull/stash/tags via GitService |
| Project Settings / Project Hub / Splash | ✅ | WPF: Project Browser + splash; Avalonia: Project Hub + Settings; templates: Empty, 3D Starter, Horror Starter, Tactical Shooter |
| Update project from template | ✅ | Avalonia: preview, line diffs, per-folder choice, backup; WPF: message-box preview + backup; skips `Assets/VFX`, `Characters`, `Weapons` |
| Project compatibility gate | ✅ | Both editors: older projects upgrade after a backup, projects from a newer engine are refused |
| Hot reload (shaders + scripts) | ✅ | On focus; editor play, external window and Debug exports |
| Play mode UI + external game window | ✅ | WPF: in-process DX12 window; Avalonia: Scene/Game tabs + *Play in Standalone Player* (`Vortex.Player` process) |
| Build / export window | ✅ | Avalonia: macOS/Windows/Linux targets, runtime packs, branding, license check; WPF: Windows only, no license check |
| Model Viewer, camera preview PIP, toasts, shortcuts | ✅ | WPF: model viewer tabs; Avalonia: Asset Viewer window + shortcut sheet |
| Embedded Claude panel + MCP server | ✅ | Avalonia only (see Claude Integration) |
| Crash guard | 🟡 | Avalonia: a throwing handler is logged (`logs/editor-errors.log`) and the editor keeps running; the WPF editor has none |
| View menu (window toggles) | 🟡 | WPF menu checkmarks don't reflect dock state; Avalonia's 9 panel toggles (Cmd/Ctrl+1–9) are accurate |
| Viewport debug modes | 🟡 | Avalonia: wireframe, physics debug, all colliders, post-FX preview, navmesh overlay; WPF: collider toggles; no lit-only/overdraw modes (backlog) |
| Terrain editor / foliage painter | ❌ | → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Behavior-tree editor | ❌ | → [M7](https://github.com/shadow-kernel/Vortex-Engine/milestone/7) |
| Sequencer / plugin system | ❌ | Backlog |

## Claude Integration

All of it lives in the Avalonia editor (`Managed/Vortex.Editor/Claude/`) on every platform; the WPF editor has none of it.

| Feature | Status | Notes |
|---|---|---|
| MCP server | ✅ | `McpHost.cs`: Kestrel, Streamable HTTP (stateless) on `http://127.0.0.1:7420/mcp`, loopback + Host/Origin guard; off until enabled, port per user |
| Tool sets | ✅ | 68 tools in 8 sets: Assets 8, Audio 8, Editor 4, Materials 8 (incl. shaders), Scenes 19, Scripts 7, Viewport 7, World 7; reference: [[Claude-Tools]] (generated from the code) |
| Undo model + safety | ✅ | `ToolHost.cs`: one call at a time on the UI thread, one undo step "Claude: …" per call, failed calls roll back; file writes stay in `Assets/` |
| Dry runs | ✅ | Changing tools take `dry_run` (run, report, roll back); undo/redo, internet tools and non-undoable writers excluded |
| Operations log + revert | ✅ | *Tools ▸ Claude ▸ Operations…*: every call with client, input, result and file diffs; Revert per operation |
| Embedded Claude panel | ✅ | `ClaudePanel.cs` + `Vortex.Core/Claude/ClaudeChat.cs`: streamed tool loop with the same tools, tool cards with images, Allow / Always allow / Deny; user's own Anthropic key |
| Claude Code / Desktop hookup | ✅ | *Tools ▸ Claude ▸ Connect Claude Code / Desktop…*: `claude mcp add` command, writes the project's `.mcp.json` (keeps other servers), Claude Desktop config via `mcp-remote` |
| Viewport capture | ✅ | `capture_viewport` returns an image of the editor viewport (`ViewportCapture.cs`) |
| Tests | ✅ | `Vortex.Core.Tests/ClaudeTests.cs` (chat loop, approvals, `.mcp.json`); the `mcp,claude` editor smoke gates the macOS app build, informational on Windows |

## Scripting / Game API

| Feature | Status | Notes |
|---|---|---|
| VortexBehaviour lifecycle (Start/Update/OnDestroy) | ✅ | Plus LateUpdate, OnMessage, OnAnimationEvent, OnTrigger*/OnCollisionEnter; no FixedUpdate/OnEnable; only the first Script component per entity runs |
| Transform API | ✅ | Position/Rotation/Scale, Translate/Rotate, Forward/Right, world poses, Quaternion math; any entity via `Scene.*Of` |
| Input (keyboard, mouse, gamepad) | 🟡 | Keyboard + mouse everywhere; gamepads in every host — WPF: Windows.Gaming.Input + DualSense HID + XInput; cross-platform editor + `Vortex.Player`: DualSense HID + XInput on Windows, SDL3 (Xbox / PlayStation / Switch Pro …) on macOS + Linux (`GamepadTest` in CTest); rebinding → [M13](https://github.com/shadow-kernel/Vortex-Engine/milestone/13) |
| Character movement (Physics.MoveCharacter) | ✅ | Managed collide-and-slide capsule, Grounded, `SetCharacterOptions(stepHeight, maxSlope)`; works without Jolt |
| Trigger/collision events | ✅ | OnTriggerEnter/Stay/Exit + OnCollisionEnter from characters and Jolt contacts; no OnCollisionStay/Exit |
| Skeletal animation API | ✅ | Play/crossfade/speed/time, layers, synced groups, bone overrides, IK targets, sockets, OnAnimationEvent |
| Scene loading | ✅ | `Scene.Load(name)` deferred to end of tick + `Scene.Loading` event; synchronous, no additive/async loading |
| Immediate-mode UI (UI.*) + retained VUI (Gui/VuiHandle) | ✅ | Button actions auto-routed to `<Screen>Actions` classes; `Gui.Confirm` modal |
| Camera FOV, cursor lock, lighting control, render settings | ✅ | `Camera.SetFieldOfView`, `Cursor.Locked`, `Lighting`, `Atmosphere`, `PostFx`, `Settings` (VSync, fullscreen, resolution, render scale, DLSS/FG on DX12) |
| World.Add runtime geometry, Application.Quit, Time.DeltaTime | ✅ | `World.Add` is render-only (no collision); `Time` has only DeltaTime |
| Hot-reload + in-box C# compilation | ✅ | WPF: CodeDom (C# 5); Avalonia editor + player: Roslyn (latest C#); compile errors keep the old scripts running |
| Public script fields | ✅ | Both inspectors edit int/float/bool/string/enum/Vector3/arrays; per-instance values saved in scenes and prefabs |
| Entity queries + component access | 🟡 | `Scene.Find/FindByTag/Parent/Children`, `GetBehaviour<T>`, Light and AudioSource handles; no generic GetComponent/AddComponent |
| Entity enable/disable | ✅ | `Scene.SetActive/IsActive/SetRendererEnabled/SetColliderEnabled` |
| Physics.Raycast for scripts | ✅ | `RaycastHit` (point, normal, distance, entity, name, tag) + layer mask; Jolt world or managed collision |
| Runtime Instantiate/Destroy (prefabs) | ✅ | `Scene.Instantiate(".ventity", pos, yaw)` / `Scene.Destroy`; undone when editor play stops |
| Coroutines/timers (WaitForSeconds, Invoke) | ✅ | `StartCoroutine` + `WaitForSeconds`, `Invoke/InvokeRepeating/CancelInvokes`; no WaitUntil |
| Event system / messaging | ✅ | `SendMessage` → `OnMessage`, typed `Events.Subscribe/Publish<T>` |
| Save/load (slots + PlayerPrefs-style) | ✅ | `Save.UseSlot`, typed Set/Get, `Flush`; saved at play end and on scene switch |
| Debug.Log + dev console + debug draw | ✅ | Log/Warning/Error → Console; F9 in-game console (display only); `Debug.DrawLine/DrawRay/DrawSphere` on both backends |
| Runtime light control (Vortex.Light + flicker) | ✅ | `GetLight()` / `Scene.GetLight(id)`: enabled, intensity, range, cone, color, shadows; `Light.Flicker(t, speed)` |
| Camera FX | 🟡 | `CameraFX.Kick/Sway/SetSpring` (recoil, bob) for the camera and attached entities; no flash/fade |
| Audio API from scripts | ✅ | See the Audio table |
| Physics, ragdoll, AI and VFX APIs | 🟡 | `Physics.AddForce/AddImpulse/OverlapSphere…`, `Ragdoll.*`, `Navigation.*`, `Perception.*`, `Vfx.*`; Jolt/Recast calls are no-ops in the Windows MSBuild build, `Vfx` draws only on SDL GPU |
| Visual scripting | ❌ | Backlog (research) |

## Physics & Collision

| Feature | Status | Notes |
|---|---|---|
| Collider component (Box/Sphere/Capsule/Mesh) | ✅ | IsTrigger, center offset, Convex, friction/bounciness (material fields editable in the Avalonia editor only) |
| Box (OBB) / Sphere / Capsule colliders | ✅ | Managed collision: analytic, Y-rotation only; Jolt: full rotation |
| Mesh collider (triangle soup) | ✅ | Managed: exact triangles; Jolt: static mesh, moving or Convex → convex hull |
| Collide-and-slide character movement | ✅ | Managed capsule in every build: substepped depenetration, 0.35 m step-up, 50° slope limit, ground snap |
| First-person character controller | ✅ | Script-driven (template `PlayerController` / `CoDMovement`) |
| Gravity | ✅ | Jolt default −9.81 m/s² (`Physics.SetGravity`); the player's gravity comes from its controller script |
| Trigger volumes + contact events | ✅ | Characters and Jolt bodies raise OnTriggerEnter/Stay/Exit + OnCollisionEnter each tick |
| Raycasts | ✅ | Editor picking (`RaycastService`) + script `Physics.Raycast` against Jolt or the managed world |
| Collision Editor window | ✅ | Live preview, auto-fit, type switching; both editors |
| Broadphase, multi-character support | ✅ | Managed O(n) AABB reject + capsule-vs-capsule; Jolt bodies use Jolt's broadphase |
| Full rigid-body engine (Jolt) | 🟡 | Jolt 5.3.0 in CMake builds: 60 Hz fixed step (max 4 substeps), sleeping, render interpolation; Windows MSBuild build = stub, props stay put (#182) → [M6](https://github.com/shadow-kernel/Vortex-Engine/milestone/6) |
| Rigidbody component | 🟡 | Dynamic/Kinematic/Static, mass, drag, gravity, freeze axes (Jolt); its `Interpolation` and `CollisionDetection` fields are ignored |
| Dynamic rigid-body physics | 🟡 | Rotation, forces, torque, impulses (at a point), velocities, kinematic switch; the player pushes props; CMake builds only |
| Collision layers | ✅ | Static/Dynamic/Character/Trigger/Debris; `Debris`-tagged props and ragdoll parts never block the player |
| Constraints (hinge/ball/slider/fixed/distance) | 🟡 | Limits, motors, springs, break force; Avalonia inspector cards only, no script control; CMake builds only |
| Ragdoll | 🟡 | From any humanoid rig at its current pose; `Ragdoll.Activate/AddImpulse`; authored in the Avalonia editor; CMake builds only |
| Character controller v2 (stairs/slopes/crouch) | 🟡 | The managed controller steps and slides; crouch/slide live in template scripts; Jolt `CharacterVirtual` exists but is unused → [M6](https://github.com/shadow-kernel/Vortex-Engine/milestone/6) |
| Compound colliders + working physics materials | 🟡 | Primitive colliders on one entity form one body; friction/restitution applied (first collider's material); the character ignores materials → [M6](https://github.com/shadow-kernel/Vortex-Engine/milestone/6) |
| Overlap queries | 🟡 | `Physics.OverlapSphere` only |
| Physics debug draw | 🟡 | Jolt shapes and joints as wire lines on both backends; toggle in the Avalonia editor only → [M6](https://github.com/shadow-kernel/Vortex-Engine/milestone/6) |
| Continuous collision detection | ❌ | The Rigidbody field is not passed to Jolt |
| Vehicles | ❌ | → [M17](https://github.com/shadow-kernel/Vortex-Engine/milestone/17) |

## Animation

| Feature | Status | Notes |
|---|---|---|
| GPU skinning (4-influence LBS) | ✅ | 52-byte vertex; `skinned.hlsl` on DX12, MSL/GLSL `VSSkinned` on SDL GPU |
| Keyframe Editor | ✅ | Both editors: 3D preview, bone tree, dope sheet, pose inspector, undo, event markers |
| Animation clips (.vanim JSON) | ✅ | FBX import → per-clip files; VFS-aware; shareable across skeletons with the same bone names |
| Crossfade/blending | ✅ | Two-pose crossfade with fade duration |
| Bone-masked layers | ✅ | `PlayAnimationLayered(clip, layer, "Spine1+")`: upper-body override over locomotion |
| Synced multi-entity playback | ✅ | `Animation.PlaySynced` locks character + weapon clips to one clock |
| Skeleton hierarchy + bind pose | ✅ | Multi-submesh models, skeleton fallback resolution |
| Animation events | ✅ | Markers in .vanim → OnAnimationEvent in scripts (footsteps, attack hits) |
| Script API (Play/Stop/Speed/IsPlaying/Time) | ✅ | Plus bone overrides, IK and sockets; other entities via `Vortex.Animation` |
| Animator component + inspector | ✅ | Both editors: clip table, DefaultClip, PlayOnStart, drag-drop .vanim |
| Import pipeline (bone extraction, weight limiting) | ✅ | 4 influences, renormalization, max 255 bones |
| Loop flag, autoplay, serialization, VFS, RuntimeDirty fix | ✅ | Shipped-game moving-entity trap fixed |
| IK (foot placement, look-at) | ✅ | Foot IK, Look-At IK, Two-Bone IK, Hand Pose; Avalonia has all cards (WPF: Two-Bone IK only) |
| Bone sockets / attachments | ✅ | BoneAttachment component + runtime `Attach/Detach`; Socket Editor in both editors |
| Root motion | ❌ | Clips play in place → [M7](https://github.com/shadow-kernel/Vortex-Engine/milestone/7) |
| State machine / blend-tree editor | ❌ | Script-driven only → [M12](https://github.com/shadow-kernel/Vortex-Engine/milestone/12) |
| Retargeting | ❌ | Tracks bind by exact bone name (`RigMap` only detects bone roles) → [M12](https://github.com/shadow-kernel/Vortex-Engine/milestone/12) |
| Skinned mesh LOD + per-object motion vectors | ❌ | No LOD for skinned meshes; camera-only motion vectors → [M12](https://github.com/shadow-kernel/Vortex-Engine/milestone/12) |

## AI & Navigation

Merged as work in progress; the navmesh parts need Recast, which only the CMake builds (macOS, Linux) compile.

| Feature | Status | Notes |
|---|---|---|
| Navmesh baking | 🟡 | Recast/Detour 1.6.0, tiled + parallel, saved as `<Scene>.vnav`; stub in the Windows MSBuild build |
| Path queries | 🟡 | Paths, closest point, navmesh raycast, random points (`Navigation.*`); CMake builds only |
| NavAgent (crowd steering + avoidance) | 🟡 | DetourCrowd, up to 256 agents, stopping distance, turning; CMake builds only |
| AI Perception (sight + hearing) | ✅ | Vision cone + line-of-sight rays, `MakeNoise` hearing, memory of last seen/heard; managed, every build |
| Patrol paths | ✅ | Waypoints, loop/ping-pong/once, wait times; following the route is scripted |
| AI script API | ✅ | `Navigation`, `Perception` (OnMessage + event bus), `PatrolPath` |
| Editor UI | 🟡 | Avalonia: Navigation window (bake, progress, navmesh overlay) + AI components; WPF: none |
| Behavior trees | ❌ | → [M7](https://github.com/shadow-kernel/Vortex-Engine/milestone/7) |
| Off-mesh links / obstacles / area costs | ❌ | Off-mesh connections are hard-coded to 0 |
| Navigation test | ✅ | `TestNavigation.h` (ctest `NavigationSmokeTest`) |

## VFX

| Feature | Status | Notes |
|---|---|---|
| Particle simulation | ✅ | CPU, structure-of-arrays, multithreaded, deterministic per seed, up to 2²⁰ particles per emitter |
| Emitter features | ✅ | Rate/distance/bursts, 7 shapes, curves, gradients, noise, flipbook, 4 render modes, 3 blend modes, trails, beams |
| Particle rendering | 🟡 | SDL GPU (Metal/Vulkan): billboards, ribbons, beams, soft particles, depth collision, lit, fog, viewmodel layer; DX12 draws nothing, so no particles on Windows |
| `.vfx` asset | ✅ | JSON that is also the native schema (`VfxAsset.cs`) |
| VFX editor | ✅ | Avalonia only: emitters + beam, live preview, hot reload on save; no undo |
| Particle System component | ✅ | Avalonia card with play/restart; WPF shows the component name only |
| VFX script API | ✅ | `Vfx.Play/Stop/Burst/SpawnAt/Beam` |
| Particle test | ✅ | `TestParticles.h` (ctest `ParticleSmokeTest`) |
| GPU (compute) particles | ❌ | The simulation is CPU-only → [M8](https://github.com/shadow-kernel/Vortex-Engine/milestone/8) |
| Decals | ❌ | Flat horizontal particles are the only "splats" → [M8](https://github.com/shadow-kernel/Vortex-Engine/milestone/8) |
| Volumetric fog / light shafts | ❌ | → [M8](https://github.com/shadow-kernel/Vortex-Engine/milestone/8) |

## VUI (2D UI Engine)

| Feature | Status | Notes |
|---|---|---|
| Retained-mode architecture (document/canvas/element/stack) | ✅ | JSON .vui load/save, per-frame Layout/Update/Render; one implementation for both editors and the player |
| 11 widget kinds | ✅ | Panel, Text, Image, Bar, Button, Slider, Toggle, Stepper, TextField, List (with row pooling), Crosshair |
| Layout engine | ✅ | 9-point anchors + pivot, %/px offsets, stretch margins, Vertical/Horizontal/Grid containers, design-resolution scaling, clipping |
| Input handling | ✅ | Mouse, keyboard text, wheel scroll, keybind capture, top-first hit-test, gameplay blocking; in-viewport editor play passes no typed text or wheel |
| Font rendering | ✅ | DX12: DirectWrite (3 weights, Unicode); SDL GPU: stb_truetype with system fonts (regular + bold, Latin-1 + symbols) |
| Script binding (named-slot Set/Get API) | ✅ | SetValue/SetText/SetList etc.; GetSlider/GetToggle/GetText/GetCapturedKey |
| Button-to-code wiring | ✅ | ClickAction → auto-routed `<Screen>Actions` class methods, generated by both UI editors |
| Visual builder (UI Editor) | ✅ | Both editors; Avalonia adds drag/resize, undo, Test mode, tooltip and sound fields |
| Rendering backend | ✅ | DX12: D3D11On12 + D2D, image cache, scissor clipping; SDL GPU: own overlay (rects, lines, images, clipping) |
| Canvas stack (HUD → modal) | ✅ | Push/Pop, cursor-lock + gameplay-block preferences; the top screen gets input first |
| Modal dialogs | ✅ | `Gui.Confirm(title, msg, onYes, onNo)`: blocking yes/no modal, keyboard/pad navigable; no OK-only alert or text prompt |
| Gamepad/keyboard menu navigation | ✅ | Focus system: arrows/Tab/Enter, D-pad/A/stick with repeat, focus ring; with a pad on every platform |
| Tooltips + UI sound hooks | ✅ | Per-element tooltip + click/hover sounds stored in .vui; authored in the Avalonia UI editor only |
| Settings persistence schema | 🟡 | The element `TargetSetting` field is unused; only audio bus volumes persist automatically, the rest via `Save.*`; settings menus → [M13](https://github.com/shadow-kernel/Vortex-Engine/milestone/13) |
| Rich text / responsive layout / data binding | 🟡 | Plain text only; anchors + uniform scaling; one-way Set/Get + List repeater; rich text → [M16](https://github.com/shadow-kernel/Vortex-Engine/milestone/16), responsive layout v2 → [M13](https://github.com/shadow-kernel/Vortex-Engine/milestone/13) |
| Slider value labels | ❌ | No built-in label; a script-driven Text element is the workaround |
| Dropdown, scrollbar, animation/tweens, theming, localization, a11y | ❌ | Wheel scrolling only; theming → [M13](https://github.com/shadow-kernel/Vortex-Engine/milestone/13), localization + accessibility → [M16](https://github.com/shadow-kernel/Vortex-Engine/milestone/16) |

## Performance Tech

| Feature | Status | Notes |
|---|---|---|
| Native GameHost | ✅ | Win32 (DX12) or SDL3 (Metal/Vulkan): window, pump, tick and render on one thread; kills the WPF Present-freeze; uncapped FPS |
| 2-pass multithreaded culling | 🟡 | DX12: parallel count → prefix-sum → parallel pack, lock-free counters; SDL GPU runs the same passes on one thread; 262K instance cap |
| Geometric LOD chains | ✅ | LOD0–3 via vertex-cluster decimation; contiguous per-LOD instance slabs |
| GPU instancing (draw-run batching) | ✅ | One instanced draw per (mesh+material) run; max 8,192 runs on DX12 |
| Pre-cull for extreme instance counts | 🟡 | DX12: parallel O(n) cull before sort above 262K; SDL GPU drops instances beyond 262K |
| Render-scale pipeline + DLSS SR/FG (Streamline) | 🟡 | Render scale on both backends; DLSS, frame generation and Reflex on DX12 only, and only with staged Streamline DLLs |
| Motion vector generation | 🟡 | Camera-only, DX12 only, only while DLSS runs; per-object → [M12](https://github.com/shadow-kernel/Vortex-Engine/milestone/12) |
| GPU skinning buffers | ✅ | DX12: 64K-matrix alternating upload halves, no torn reads; SDL GPU: one 64K buffer via a recycled transfer buffer |
| Deferred resize & scene switch | ✅ | Both hosts defer resizes past the pump; no Present freeze on resize/scene change |
| Raw-input mouse capture, borderless fullscreen | ✅ | Win32: WM_INPUT deltas + cursor clip; SDL: relative mouse mode; F11 on both |
| Custom-shader hot reload (PSO cache) | ✅ | Cache keyed by path + modification time on both backends; the last working shader survives a failed reload |
| Occlusion culling (HZB) | ❌ | Walls don't hide anything indoors today → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Spatial partition (octree/BVH) | ❌ | Culling is O(all instances) → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Async asset streaming | ❌ | Imports decode textures in parallel but block; texture/mesh streaming → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Render graph / frame graph | ❌ | Pass order is hard-coded (shadows → SSAO → scene → post-FX → UI) → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |
| Bindless resources | ❌ | → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |
| GPU culling + ExecuteIndirect | ❌ | → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |
| Mesh shaders (meshlets) | ❌ | → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |
| Job system (persistent, work-stealing) | ❌ | Culling uses a persistent worker pool (DX12 only, no work stealing); particles, Jolt and navigation have their own pools → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |
| In-engine profiler | ❌ | Telemetry counters only; GPU timestamps + profiler window → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |
| ECS/data-oriented core, memory arenas, async compute, virtual texturing | ❌ | → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |

## Infrastructure (Build / CI / Release)

| Feature | Status | Notes |
|---|---|---|
| GitHub Actions release pipeline | ✅ | Tag `v*`: `build-release.yml` (MSBuild, stages the Avalonia editor, installer + portable ZIP); `build-macos.yml` attaches the DMG; Windows runtime pack as an artifact |
| PR test gate | ✅ | `pr-tests.yml` on Windows (MSBuild + managed build + tests) and macOS (CMake + native tests + managed tests); docs-only changes skip the builds |
| Inno Setup installer | ✅ | EN+DE wizard, .NET 4.8 check, .vortex association, silent auto-update; ships the WPF editor plus the Avalonia editor + player in `{app}\Editor` (#183) |
| macOS app bundle + DMG | 🟡 | `tools/macos/make-app.sh`: self-contained editor + player with their Homebrew libraries bundled (`bundle-dylibs.sh`), ad-hoc signed, not notarised; minimum macOS = the build machine's |
| Auto-update system | 🟡 | WPF (installed builds): GitHub polling, patch = silent, minor/major = ask; Avalonia: *Check for Updates* opens the release page |
| Game export & packaging | ✅ | Debug (live project, hot reload) and Release (`.vpak` + compiled scripts); Avalonia: macOS/Windows/Linux targets, branding, `CREDITS.md`; WPF: Windows only |
| Runtime packs | 🟡 | `tools/make-runtime-pack.*` per OS; CI only builds the Windows pack (artifact) |
| Project format versioning & migration | ✅ | Format v2, backup + migration service; both editors refuse projects from a newer engine |
| Project templates + template packs | ✅ | Empty, 3D Starter, Horror Starter, Tactical Shooter (submodules); installed editors download `Template-<Id>.zip` release assets; packs are uploaded by hand, no checksum |
| Version management | ✅ | `EngineInfo.cs` single source (2.10.0 until the v3.0.0 bump); CI patches the installer version from the tag |
| Streamline/DLSS prebuild staging | 🟡 | Idempotent, never fails the build; needs a local Streamline SDK, CI has none, so release builds ship without DLSS |
| Third-party license notices | 🟡 | Covers Assimp, stb_image, miniaudio, Steam Audio, Recast, Jolt, SDL3, Avalonia, Roslyn, MCP SDK and more; stb_truetype and ANGLE are missing; claims SDL3 is bundled into the app |
| Submodules | ✅ | Streamline + three templates (Default3D, HorrorStarter, TacticalShooter) |
| Native engine tests | ✅ | `EngineTest`: audio, physics, particles, navigation, geometry in ctest (render test opt-in); run on macOS CI only |
| Managed tests | ✅ | `Managed/Vortex.Core.Tests`: 50 tests (library, store, Sound Studio, Claude, scene/pak/VUI, templates, wiki samples) on Windows + macOS |
| Editor smoke suite | 🟡 | 80+ self-registering checks (`--smoke`); CI runs only the `mcp,claude` subset |
| Stress test / benchmark harness | 🟡 | Editor dialogs in both editors + WPF `--stress`/`--benchmark` modes; `Vortex.Player` has none; no CI benchmark suite → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |
| Installer multi-language | 🟡 | EN + DE only; no editor i18n |
| Code signing (Authenticode / notarisation) | ❌ | Windows binaries unsigned (SmartScreen warnings); macOS app + exports ad-hoc signed only → [M5](https://github.com/shadow-kernel/Vortex-Engine/milestone/5) |
| Crash reporting + symbol server | ❌ | Local logs only (Avalonia `CrashGuard`); no dumps or upload → [M5](https://github.com/shadow-kernel/Vortex-Engine/milestone/5) |
| Nightly builds / beta channel | ❌ | The updater skips prereleases → [M16](https://github.com/shadow-kernel/Vortex-Engine/milestone/16) |
| Linux CI + packages | ❌ | No Linux job; Linux builds from source |

## Misc Engine Services

| Feature | Status | Notes |
|---|---|---|
| Scene management + serialization | ✅ | SceneManager + DataSerializer (binary XML, polymorphic components); per-scene pak mounted on switch; round-trip test |
| Resource manager | ✅ | Path- and GUID-based loading (mesh, texture, material, shader, audio), reference counting |
| Prefab service (runtime) | ✅ | `.ventity` instantiate/apply/revert; `Scene.Instantiate` at runtime |
| Game loop & tick | ✅ | Native `StepRuntime`: fixed 1/60 s accumulator (max 8 steps); Jolt steps at 60 Hz in `PhysicsService`; scripts get variable dt |
| Hot-reload (scripts + shaders) | ✅ | Both editors, the external window and Debug exports; off in Release exports |
| ECS component model | ✅ | 32 types, same model in editor and runtime: rendering, Light, colliders + Rigidbody + 5 joints + Ragdoll, audio, Animator + sockets + IK, AI, Script |
| Input system | 🟡 | Keyboard/mouse on every host (Win32 raw input, SDL3); gamepads on every host (see Scripting); no action maps / rebinding yet (#237) |
| Asset management & streaming | 🟡 | GUID manifest + per-scene paks work; no async or texture streaming → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Save/load game state | 🟡 | `Save` slots + typed keys (one file per slot); no automatic scene/entity snapshot |
| Audio system | ✅ | See the Audio table |
| AI & navigation | 🟡 | See the AI & Navigation table |
| Level streaming volumes | ❌ | Whole-scene pak mounting only → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Networking / multiplayer | ❌ | No netcode (the only server is the loopback MCP server) → [M11](https://github.com/shadow-kernel/Vortex-Engine/milestone/11) |
| Localization framework | ❌ | → [M16](https://github.com/shadow-kernel/Vortex-Engine/milestone/16) |
| Video/cinematic playback | ❌ | → [M16](https://github.com/shadow-kernel/Vortex-Engine/milestone/16) |
| Steam integration | ❌ | No Steamworks code (Steam Audio is unrelated) → [M14](https://github.com/shadow-kernel/Vortex-Engine/milestone/14) |

---

*Re-verified against the code in October 2026 (v3.0.0). When a feature ships, update its row here — this page must stay honest.*
