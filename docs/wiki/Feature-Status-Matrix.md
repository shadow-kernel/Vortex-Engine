# Feature Status Matrix

The honest, verified current state of Vortex Engine (**v2.5.1**), straight from a full codebase scan. This page is the single source of truth for *what the engine actually does today*.

**Legend:** ✅ complete · 🟡 partial · 🧪 stub (code exists, does nothing real) · ❌ missing (planned milestone linked)

Milestones: [M1 v2.6.0 Audio](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) · [M2 v2.7.0 Horror Essentials](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) · [M3 v2.8.0 Global Asset DB](https://github.com/shadow-kernel/Vortex-Engine/milestone/3) · [M4 v2.9.0 Asset Store & Claude Sound Studio](https://github.com/shadow-kernel/Vortex-Engine/milestone/4) · [M5 v3.0.0 Claude-Native](https://github.com/shadow-kernel/Vortex-Engine/milestone/5) · [M6 v3.1.0 Physics v2](https://github.com/shadow-kernel/Vortex-Engine/milestone/6) · [M7 v3.2.0 AI & Navigation](https://github.com/shadow-kernel/Vortex-Engine/milestone/7) · [M8 v3.3.0 VFX](https://github.com/shadow-kernel/Vortex-Engine/milestone/8) · [M9 v3.4.0 World & Streaming](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) · [M10 v4.0.0 XXL 10x Performance](https://github.com/shadow-kernel/Vortex-Engine/milestone/10)

Browse open work by area: [rendering](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Arendering) · [audio](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aaudio) · [physics](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aphysics) · [scripting](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Ascripting) · [horror-blocker](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Ahorror-blocker)

---

## Rendering (D3D12 native)

| Feature | Status | Notes |
|---|---|---|
| Graphics API (Direct3D 12) | ✅ | Full device/queue/swapchain; native GameHost window verified ~1280–1850 FPS |
| PBR lighting (Cook-Torrance GGX) | ✅ | Directional + point + spot + hemisphere ambient + rim; ACES tonemap in `standard.hlsl` |
| Directional light | ✅ | One per frame, exposed via C# `LightingApi` |
| Point lights | ✅ | 16 max/frame, quadratic falloff, full PBR per light |
| Spot lights | ✅ | 8 max/frame, inner/outer cone smooth fade |
| Tonemapping (ACES) | ✅ | RRT+ODT filmic per pixel before gamma |
| Sky & atmosphere | ✅ | Procedural skybox (SolidColor/Gradient/Texture) + sun disc/glow |
| Render-scale & upscaling | ✅ | [0.25–2.0] offscreen RT + bilinear upscale; DLSS plugs into this slot |
| DLSS Super-Resolution | ✅ | Streamline SDK, hardware-gated, graceful fallback to bilinear |
| DLSS Frame Generation | ✅ | x2/x3/x4 + Reflex/PCL markers; needs RTX 50+ |
| Motion vectors | ✅ | RG16F reprojection pass (camera-only; per-object mvecs → M10) |
| GPU instancing | ✅ | Per-instance world matrices in VB slot 1; batching by (mesh, material) |
| GPU skinning | ✅ | Bone palette SRV (t5), 32K matrices/frame, dual-half buffering |
| Frustum culling | ✅ | Gribb-Hartmann planes, sphere tests, parallel pre-cull >262K instances |
| Distance & density LOD | ✅ | Density thinning + geometric decimated meshes, deterministic (no flicker) |
| Geometric LOD (multi-level) | ✅ | Up to 4 LOD levels per mesh, built at import via vertex clustering |
| Render distance culling | ✅ | Per-instance world-unit cutoff, settable per frame |
| Multithreaded culling & packing | ✅ | Worker pool (max 8) when >2K instances |
| Mesh rendering | ✅ | Rigid + skinned + instanced; queue/sort/batch per frame |
| Material system (PBR props) | ✅ | Base color/metallic/roughness/AO/normal/emissive/unlit; per-material custom shader + PSO cache |
| Texture binding & sampling | ✅ | 5 slots (albedo/normal/metallic/roughness/AO), 16x anisotropic |
| Normal mapping | ✅ | DirectX + OpenGL conventions, TBN in VS, strength per material |
| Grid / gizmo / camera-gizmo rendering | ✅ | Dedicated PSOs; gizmos always-on-top (depth disabled) |
| Wireframe mode | ✅ | Dedicated PSO, toggleable |
| Multi-viewport render targets | ✅ | Up to 8 secondary RTs + CPU readback (previews/thumbnails) |
| Back-buffer capture | ✅ | F12 → real back-buffer BMP (not GDI) |
| 2D UI overlay (D2D/DirectWrite) | ✅ | Rects/text/lines/images/clip over 3D, drives Vortex.UI + VUI |
| Standalone game window + native GameHost | ✅ | Own Win32 thread, message pump, uncapped FPS |
| VSync control / depth testing / emissive-unlit / aniso filtering | ✅ | ALLOW_TEARING present; D32_FLOAT depth |
| Performance telemetry | ✅ | FPS, draw calls, vertex count, cull stats |
| HDR rendering | 🟡 | ACES applied but backbuffer is 8-bit SDR; no HDR output mode (backlog) |
| Transparency & alpha blending | 🧪 | Alpha parsed but **BlendEnable=FALSE in all PSOs** — transparent objects render opaque → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Post-processing pipeline | 🧪 | Only the upscale composite pass; framework + vignette/grain/CA/bloom/LUT/SSAO → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Reflections (SSR/probes) | 🧪 | Ambient sphere-map approximation only; SSR in backlog |
| Shadow mapping | ❌ | No shadows at all — spot (flashlight!), cascaded directional, point cube maps → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Fog (depth/height) | ❌ | → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2); volumetric fog/light shafts → [M8](https://github.com/shadow-kernel/Vortex-Engine/milestone/8) |
| Anti-aliasing (TAA/MSAA/FXAA) | ❌ | None outside DLSS; TAA → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |
| Particle system | ❌ | GPU particles + editor + .vfx asset → [M8](https://github.com/shadow-kernel/Vortex-Engine/milestone/8) |
| Decals | ❌ | Projected blood/damage/grime → [M8](https://github.com/shadow-kernel/Vortex-Engine/milestone/8) |
| Terrain & foliage | ❌ | Sculpting, splat painting, foliage brush → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Global illumination / DXR / mesh shaders / bindless | ❌ | UE5-class items → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) + backlog |

## Audio

The single biggest gap in the engine — editor components exist, the native backend does not.

| Feature | Status | Notes |
|---|---|---|
| AudioSource component (editor) | ✅ | Full property set: clip, volume, pitch, loop, spatial blend, min/max distance, rolloff, priority, pan, reverb mix, doppler, spread; serialized |
| AudioListener component (editor) | ✅ | Serialized, registered in GameEntity |
| Editor UI for audio components | ✅ | Header-bar + hierarchy create commands, icons |
| Audio resource loading | 🧪 | `load_audio()` returns generic resource — no decoding of any format |
| AudioSystem (native) | 🧪 | init/shutdown/update exist; update body is a TODO comment |
| Runtime API wiring | 🧪 | Lifecycle calls present in `RuntimeApi.cpp`, no backend behind them |
| Playback engine (miniaudio, WASAPI, WAV/OGG/MP3) | ❌ | → [M1](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) |
| 3D spatialization (attenuation, panning, doppler) | ❌ | → [M1](https://github.com/shadow-kernel/Vortex-Engine/milestone/1); Steam Audio HRTF/occlusion = v2 issue in M1 |
| Voice management (play/stop/pause, priority, stealing) | ❌ | → [M1](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) |
| Streaming playback (music/ambience) | ❌ | → [M1](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) |
| Mixer (buses, routing, ducking) + Audio Mixer window | ❌ | → [M1](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) |
| Vortex.Audio scripting API | ❌ | → [M1](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) |
| Reverb zones, random sound containers, fades | ❌ | → [M1](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) |
| 3D audio gizmos + edit-mode audition | ❌ | → [M1](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) |

## Assets

| Feature | Status | Notes |
|---|---|---|
| Asset Browser ("Project" tab) | ✅ | Thumbnail grid, tabs (Explorer/Meshes/Models/Textures/Materials/Scripts), search, breadcrumb sync |
| Assimp model import | ✅ | .fbx/.obj/.gltf/.glb/.dae/.3ds/.blend/.vmesh, multi-file, auto texture detection |
| Texture import | ✅ | .png/.jpg/.tga/.bmp/.hdr/.dds via import dialog with tags |
| Material system (.vmat) | ✅ | Full PBR maps + blend modes + custom shader slot + live sphere preview |
| Prefab system (.ventity) | ✅ | Linked instances, Apply/Revert, Prefab Editor with 3D preview |
| Animation assets (.vanim) | ✅ | Create in browser, Keyframe Editor integration |
| UI assets (.vui) | ✅ | Retained-mode JSON, UI Editor integration |
| Per-project AssetDatabase + .vmeta sidecars | ✅ | Stable GUIDs, dependencies, import settings, tags |
| Tags + search | ✅ | Predefined + custom tags, name filter |
| Thumbnails/previews | ✅ | Offscreen D3D12 render-to-bitmap, studio-lit material spheres |
| Import dialog, drag-drop placement, context menus | ✅ | Includes Stress Test tool + standalone Model Viewer |
| VortexPak (.vpak) shipped format | ✅ | DEFLATE + XOR, core pak + per-scene layer paks, AssetVfs mounting |
| Scene streaming (pak layers) | ✅ | Mount/unmount additive scene packs on demand |
| Shader assets (.hlsl) | 🟡 | Templates + custom shader binding; no visual shader editor (opens in VS) |
| Dependency tracking | 🟡 | Dependencies stored in .vmeta; graph unused for impact analysis/cascading deletes (backlog) |
| Asset deletion service | 🟡 | Filesystem delete works; no .vmeta cleanup or dependency invalidation (backlog) |
| Content hashing | ❌ | Only timestamps + file size today; SHA-256 on import → [M3](https://github.com/shadow-kernel/Vortex-Engine/milestone/3) |
| Global/cross-project asset library | ❌ | Machine-wide hash-deduplicated DB + Library tab → [M3](https://github.com/shadow-kernel/Vortex-Engine/milestone/3) |
| Asset Store tab (Poly Haven, ambientCG, Freesound, Mixamo…) | ❌ | Provider framework + 1-click download→import → [M4](https://github.com/shadow-kernel/Vortex-Engine/milestone/4) |
| Claude Sound Studio (AI SFX generation) | ❌ | ElevenLabs backend + Claude prompt orchestration → [M4](https://github.com/shadow-kernel/Vortex-Engine/milestone/4) |
| Bulk operations (batch rename/retag/reimport) | ❌ | Backlog |

## Editor

| Feature | Status | Notes |
|---|---|---|
| WPF shell + AvalonDock docking | ✅ | Borderless DWM chrome, 4-column layout, dark theme |
| Scene Hierarchy / File Explorer / Asset Browser panels | ✅ | Tree + selection service + drag-drop |
| Dynamic Inspector | ✅ | Transform, MeshRenderer, Camera, Light, Skybox, Script, Colliders, Animator + generic fallback |
| Viewport (native DX12 via HwndHost) | ✅ | 60 FPS composition, gizmo interaction, play-mode mouse capture |
| Undo/Redo | ✅ | Command merging (500ms), 100-command limit, Ctrl+Z/Y |
| Material / Mesh / Texture editors | ✅ | Live previews, channel toggles, import settings |
| Collision Editor | ✅ | Box/Sphere/Capsule/Mesh, trigger flag, live wireframe preview |
| Animation/Keyframe Editor | ✅ | Dope sheet, bone tree, pose inspector, event markers, undo per keyframe |
| UI (VUI) Editor | ✅ | Palette, hierarchy, anchor picker, resolution switcher, live preview |
| Git Source Control panel | ✅ | Branch/diff/commit/push/pull/stash/tags via GitService |
| Project Settings / Project Browser / Splash | ✅ | Templates-driven Create dialog (Empty vs 3D Starter) |
| Hot reload (shaders + scripts) | ✅ | On Alt-Tab focus; works in viewport play + external game window |
| Play mode UI + external game window | ✅ | Play/Pause/Stop; external window = native GameHost, uncapped FPS |
| Model Viewer tabs, camera preview PIP, toasts, shortcuts | ✅ | |
| View menu (window toggles) | 🟡 | Toggling works; menu checkmarks don't reflect dock state |
| Audio Mixer window | ❌ | → [M1](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) |
| Particle Editor | ❌ | → [M8](https://github.com/shadow-kernel/Vortex-Engine/milestone/8) |
| Terrain editor / foliage painter | ❌ | → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Behavior-tree editor | ❌ | → [M7](https://github.com/shadow-kernel/Vortex-Engine/milestone/7) |
| Embedded Claude panel + MCP server | ❌ | Claude operates the editor via localhost MCP → [M5](https://github.com/shadow-kernel/Vortex-Engine/milestone/5) |
| Sequencer / plugin system / viewport debug modes | ❌ | Backlog |

## Scripting / Game API

| Feature | Status | Notes |
|---|---|---|
| VortexBehaviour lifecycle (Start/Update/OnDestroy) | ✅ | Engine calls methods; all gameplay lives in project scripts |
| Transform API | ✅ | Position/Rotation/Forward/Right, Translate/Rotate, live viewport updates |
| Input (keyboard, mouse, gamepad) | ✅ | Key names, raw mouse deltas, DualSense sticks/triggers/buttons with focus gating |
| Character movement (Physics.MoveCharacter) | ✅ | Collide-and-slide capsule, Grounded, character-vs-character blocking |
| Trigger/collision events | ✅ | OnTriggerEnter/Stay/Exit + OnCollisionEnter with entity id/name/tag, fired post-Update |
| Skeletal animation API | ✅ | PlayAnimation/crossfade/speed/time + OnAnimationEvent callbacks |
| Scene loading | ✅ | Scene.Load(name) deferred to end of tick |
| Immediate-mode UI (UI.*) + retained VUI (Gui/VuiHandle) | ✅ | Button action auto-routing to `<Screen>Actions` classes |
| Camera FOV, cursor lock, lighting control, render settings | ✅ | Incl. DLSS/FrameGen/render-scale/fullscreen/resolution, all live |
| World.Add runtime geometry, Application.Quit, Time.DeltaTime | ✅ | |
| Hot-reload + in-box C# compilation | ✅ | Recompile on focus; compile errors keep old scripts running |
| Public script fields | 🟡 | Code-editable only; no inspector serialization ([SerializeField]-style) |
| Component/entity access | 🧪 | Own entity only; no GetComponent/Find/hierarchy traversal → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Physics.Raycast for scripts | ❌ | Editor-only today → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Runtime Instantiate/Destroy (prefabs) | ❌ | → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Coroutines/timers (WaitForSeconds, Invoke) | ❌ | → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Event system / messaging | ❌ | → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Save/load (slots + PlayerPrefs-style) | ❌ | → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Debug.Log + dev console + debug draw | ❌ | → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Runtime light control (Vortex.Light + flicker) | ❌ | → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Audio API from scripts | ❌ | → [M1](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) |
| Visual scripting | ❌ | Backlog (research) |

## Physics & Collision

| Feature | Status | Notes |
|---|---|---|
| Collider component (Box/Sphere/Capsule/Mesh) | ✅ | IsTrigger flag, center offset, physics material fields |
| Box (OBB) / Sphere / Capsule colliders | ✅ | Analytic shapes, Y-rotation support |
| Mesh collider (triangle soup) | ✅ | Imported models collide edge-accurately; primitives as analytic shapes |
| Collide-and-slide character movement | ✅ | Iterative depenetration, tunnel-free substepping, grounded detection |
| First-person character controller | ✅ | Quake/Source-style, script-driven (see template PlayerController) |
| Gravity | ✅ | Engine default −20 m/s², tunable per character/rigidbody |
| Trigger volumes + contact events | ✅ | OnTriggerEnter/Stay/Exit + OnCollisionEnter dispatched each tick |
| Raycast service | ✅ | Editor-only (picking/gizmos); script exposure → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Collision Editor window | ✅ | Live preview, auto-fit, type switching |
| AABB broadphase, multi-character support | ✅ | O(n) broadphase, no tree (spatial partition → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9)) |
| Rigidbody component | 🧪 | Only Dynamic+UseGravity simulated; Static/Kinematic present but inert |
| Dynamic rigidbody physics | 🧪 | AABB-only, no rotation/torque/impulses |
| Full rigid-body engine (Jolt) | ❌ | Forces, stacking, moving platforms → [M6](https://github.com/shadow-kernel/Vortex-Engine/milestone/6) |
| Constraints (hinge/ball/slider) | ❌ | The creaking door → [M6](https://github.com/shadow-kernel/Vortex-Engine/milestone/6) |
| Ragdoll | ❌ | → [M6](https://github.com/shadow-kernel/Vortex-Engine/milestone/6) |
| Character controller v2 (stairs/slopes/crouch) | ❌ | Current axis-separated stepping catches on angled surfaces → [M6](https://github.com/shadow-kernel/Vortex-Engine/milestone/6) |
| Compound colliders + working physics materials | ❌ | One collider per entity; friction/bounciness decorative → [M6](https://github.com/shadow-kernel/Vortex-Engine/milestone/6) |

## Animation

| Feature | Status | Notes |
|---|---|---|
| GPU skinning (4-influence LBS) | ✅ | 52-byte vertex, skinned.hlsl, per-frame palette upload |
| Keyframe Editor | ✅ | 3D preview, bone tree, dope sheet, pose inspector, undo, event markers |
| Animation clips (.vanim JSON) | ✅ | FBX import → per-clip files; VFS-aware; shareable across compatible skeletons |
| Crossfade/blending | ✅ | Linear two-pose blend with fade duration |
| Skeleton hierarchy + bind pose | ✅ | Multi-submesh models, skeleton fallback resolution |
| Animation events | ✅ | Markers in .vanim → OnAnimationEvent in scripts (footsteps, attack hits) |
| Script API (Play/Stop/Speed/IsPlaying/Time) | ✅ | Also control other entities via `Vortex.Animation` |
| Animator component + inspector | ✅ | Clip table, DefaultClip, PlayOnStart, drag-drop .vanim |
| Import pipeline (bone extraction, weight limiting) | ✅ | 4 influences, renormalization, max 255 bones |
| Loop flag, autoplay, serialization, VFS, RuntimeDirty fix | ✅ | Shipped-game moving-entity trap fixed |
| Root motion | ❌ | Clips play in place → [M7](https://github.com/shadow-kernel/Vortex-Engine/milestone/7) |
| State machine / blend-tree editor | ❌ | Script-driven only today; visual controller = backlog |
| IK (foot placement, look-at) | ❌ | Backlog |
| Retargeting | ❌ | Clips are skeleton-specific; backlog (multiplies Mixamo value) |
| Skinned mesh LOD + per-object motion vectors | ❌ | Decimator drops weights; skinned meshes ghost under DLSS FG → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |

## VUI (2D UI Engine)

| Feature | Status | Notes |
|---|---|---|
| Retained-mode architecture (document/canvas/element/stack) | ✅ | JSON .vui load/save, per-frame Layout/Update/Render |
| 11 widget kinds | ✅ | Panel, Text, Image, Button, Toggle, Slider, Stepper, TextField, Bar, Crosshair, List (with row pooling) |
| Layout engine | ✅ | 9-point anchoring, %/px offsets, stretch margins, Vertical/Horizontal/Grid containers |
| Input handling | ✅ | Mouse, keyboard text, wheel scroll, keybind capture, top-first hit-test, gameplay blocking |
| Font rendering (DirectWrite) | ✅ | 3 weights, alignment, Unicode |
| Script binding (named-slot Set/Get API) | ✅ | SetValue/SetText/SetList etc.; GetSlider/GetToggle/… |
| Button-to-code wiring | ✅ | ClickAction → auto-routed `<Screen>Actions` class methods |
| Visual builder (UI Editor) | ✅ | Palette, hierarchy, inspector, anchor picker, resolution presets |
| D3D11On12 + D2D rendering backend | ✅ | Image cache, scissor clipping, per-backbuffer bitmap cache |
| Canvas stack (HUD → modal) | ✅ | Push/Pop, cursor-lock + gameplay-block preferences |
| Modal dialogs | 🟡 | Stacking works, no built-in yes/no widget → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Settings persistence schema | 🟡 | Manual wiring per setting (Options.vui + OptionsMenu.cs + Settings.cs) |
| Rich text / responsive layout / data binding | 🟡 | Plain text only; fixed-1080p scaling; one-way manual Set/Get (backlog) |
| Slider value labels | 🧪 | Works via manual Text-element workaround |
| Gamepad/keyboard menu navigation | ❌ | Mouse-only today → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Tooltips + UI sound hooks | ❌ | → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Dropdown, scrollbar, animation/tweens, theming, localization, a11y | ❌ | Backlog (text v2 + styling/theme + localization framework) |

## Performance Tech

| Feature | Status | Notes |
|---|---|---|
| Native GameHost | ✅ | Single-thread Win32 window+pump+render loop; kills the WPF Present-freeze; uncapped FPS |
| 2-pass multithreaded culling | ✅ | Parallel frustum/LOD test → prefix-sum compact → parallel pack; lock-free counters; 262K instance cap |
| Geometric LOD chains | ✅ | LOD0–3 via vertex-cluster decimation; contiguous per-LOD instance slabs |
| GPU instancing (draw-run batching) | ✅ | One DrawIndexedInstanced per (mesh+material) run; max 8192 runs; 63k instances @ 240 FPS verified |
| Pre-cull for extreme instance counts | ✅ | Parallel O(n) cull before sort when queue >262K |
| Render-scale pipeline + DLSS SR/FG (Streamline) | ✅ | Dynamic runtime load, hardware gate, Reflex markers, bilinear fallback |
| Motion vector generation | ✅ | Camera-only reprojection (per-object → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10)) |
| GPU skinning buffers | ✅ | 64K-matrix alternating upload halves, no torn reads |
| Deferred resize & scene switch | ✅ | No Present freeze on resize/scene change |
| Raw-input mouse capture, borderless fullscreen | ✅ | WM_INPUT deltas; F11 toggle preserves ALLOW_TEARING |
| Custom-shader hot reload (PSO cache) | ✅ | Path-keyed PSOs, reload on focus |
| Occlusion culling (HZB) | ❌ | Walls don't hide anything indoors today → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Spatial partition (octree/BVH) | ❌ | Culling is O(all instances) → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Async asset streaming | ❌ | → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Render graph / frame graph | ❌ | Pass sequence is hard-coded (scene→mvec→upscale→UI) → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |
| Bindless resources | ❌ | → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |
| GPU culling + ExecuteIndirect | ❌ | → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |
| Mesh shaders (meshlets) | ❌ | → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |
| Job system (persistent, work-stealing) | ❌ | Culling spawns std::threads per frame → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |
| ECS/data-oriented core, memory arenas, async compute, virtual texturing | ❌ | → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |

## Infrastructure (Build / CI / Release)

| Feature | Status | Notes |
|---|---|---|
| GitHub Actions release pipeline | ✅ | Tag/manual trigger, MSBuild, artifact verification, installer + portable ZIP to Releases |
| Inno Setup installer | ✅ | EN+DE wizard, .NET 4.8 check, .vortex association, silent auto-update support |
| Auto-update system | ✅ | GitHub-API polling, patch=auto / minor-major=ask, changelog aggregation, mutex-safe relay |
| Game export & packaging | ✅ | Debug (loose + hot-reload) and Release (compiled scripts + .vpak) modes; per-game installer |
| Project format versioning & migration | ✅ | Format v2, migration service + major-version warning |
| Version management | ✅ | EngineInfo.cs single source; CI patches installer script |
| Streamline/DLSS prebuild staging | ✅ | Idempotent; build succeeds without SDK (DLSS just disabled) |
| Third-party license notices | ✅ | Assimp, stb_image, NuGet libs; Streamline documented as not bundled |
| Submodules (Streamline + Default3D template) | ✅ | CI recursive checkout |
| Stress test / benchmark harness | 🟡 | Editor dialog + --stress/--benchmark modes; no CI benchmark suite → [M10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) |
| Installer multi-language | 🟡 | EN + DE only; no editor i18n |
| Engine test suite | 🧪 | TestECS proof-of-concept, no assertions, not in CI (backlog: PR-gating tests) |
| Code signing (Authenticode) | ❌ | Unsigned → SmartScreen warnings → [M5](https://github.com/shadow-kernel/Vortex-Engine/milestone/5) |
| Crash reporting + symbol server | ❌ | Exceptions logged locally only → [M5](https://github.com/shadow-kernel/Vortex-Engine/milestone/5) |
| Nightly builds / beta channel | ❌ | Backlog |

## Misc Engine Services

| Feature | Status | Notes |
|---|---|---|
| Scene management + serialization | ✅ | SceneManager + DataSerializer (JSON/binary, polymorphic components) |
| Resource manager | ✅ | Path- and GUID-based loading, reference counting |
| Prefab service (runtime) | ✅ | Instantiate/unload; editor Apply/Revert workflow |
| Game loop & tick | ✅ | Fixed 1/60s accumulator timestep, native GameHost loop with managed tick callback |
| Hot-reload (scripts + shaders) | ✅ | Verified in editor, external window, and Debug builds |
| ECS component model | ✅ | Transform, MeshRenderer, Camera, Skybox, Light, Collider/Rigidbody, AudioSource, Animator, Script — same model editor & runtime |
| Input system | ✅ | Full snapshot: mouse/keyboard/gamepad, capture, event queues for UI |
| Asset management & streaming | 🟡 | GUID manifest + per-scene paks work; no full async chunk streaming → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Save/load game state | 🟡 | Serialization framework exists; no SaveGame/checkpoint API → [M2](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) |
| Audio system | 🧪 | See Audio table → [M1](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) |
| AI & navigation (navmesh, agents, behavior trees, perception) | ❌ | → [M7](https://github.com/shadow-kernel/Vortex-Engine/milestone/7) |
| Level streaming volumes | ❌ | → [M9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| Networking / multiplayer | ❌ | Zero netcode exists; backlog epic (research first, post-v4) |
| Localization framework | ❌ | Backlog |
| Video/cinematic playback | ❌ | Backlog |
| Steam integration | ❌ | Backlog (needed to ship the horror game on Steam) |

---

*Generated from a verified codebase scan (v2.5.1). When a feature ships, update its row here — this page must stay honest.*
