# Vortex Engine Wiki

**Vortex Engine** is an open-source (MIT) game engine for Windows: a native **C++ Direct3D 12** rendering core, a **C# VortexAPI** scripting layer (gameplay lives in project scripts as `VortexBehaviour`, never hardcoded in the engine), and a **WPF editor** with dockable panels, inspectors, and live viewport. The first shipped game target is a **first-person horror** title; the long-term goal is a UE5-class feature set at ~10x performance via a GPU-driven renderer.

**Current version: v2.5.1**

## What works today

- **D3D12 PBR renderer** — Cook-Torrance GGX, directional + 16 point + 8 spot lights, normal mapping, ACES tonemapping, procedural skybox, GPU instancing, frustum/distance culling, geometric LOD chains, multithreaded culling (63k instances @ 240 FPS; ~1850 FPS in simple scenes)
- **DLSS Super-Resolution + Frame Generation** — NVIDIA Streamline integration, SR quality modes + FG x2/x3/x4 with Reflex, motion-vector pass, graceful fallback to bilinear upscale
- **Skeletal animation** — bone/clip import, GPU skinning (bone palettes in mapped GPU buffers), `.vanim` clips, Animator, Keyframe Editor
- **Prefab system** — `.ventity` JSON prefabs with instantiate/Apply/Revert and a dedicated Prefab Editor
- **VUI 2D UI engine** — retained-mode `.vui` screens (panels/buttons, anchors/stretch), visual UI editor, D2D/DWrite overlay rendering
- **Collision system v1** — CollisionService collide-and-slide character movement, `Vortex.Physics` API, dedicated Collision Editor window
- **Git integration** — GitService, Source Control panel with 2-pane live diff, auto-repo/LFS, Ctrl+S save flow
- **Installer + auto-update** — Windows installer, auto-updater with install + auto-restart, project version gate + step-by-step migration
- **Asset pipeline** — Assimp model import (FBX/OBJ/glTF/…), `.vmat` PBR materials with live previews, per-project AssetDatabase with `.vmeta` GUID sidecars, `.vpak` shipped-game packaging with per-scene streaming layers

## The road ahead

| Milestone | Focus |
|---|---|
| [v2.6.0 – Audio Engine](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) | miniaudio backend, 3D spatial audio, mixer buses, `Vortex.Audio` API, editor audio tooling |
| [v2.7.0 – Horror Essentials](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) | The game-dev-ready gate: shadow mapping (flashlight), fog, post-FX, transparency, triggers/raycasts/Instantiate/coroutines, save/load, Horror Starter template |
| [v2.8.0 – Global Asset DB](https://github.com/shadow-kernel/Vortex-Engine/milestone/3) | PC-wide, SHA-256 content-addressed, deduplicated cross-project asset library + Library tab |
| [v2.9.0 – Asset Store & Claude Sound Studio](https://github.com/shadow-kernel/Vortex-Engine/milestone/4) | In-editor Store tab (Poly Haven, ambientCG, Freesound, Mixamo, …) + Claude-driven SFX generation |
| [v3.0.0 – Claude-Native Engine](https://github.com/shadow-kernel/Vortex-Engine/milestone/5) | In-editor MCP server + embedded Claude panel — Claude builds worlds, materials, scripts |
| [v3.1.0 – Physics v2](https://github.com/shadow-kernel/Vortex-Engine/milestone/6) | Jolt Physics: rigid bodies, constraints (hinged doors), ragdoll, character controller v2 |
| [v3.2.0 – AI & Navigation](https://github.com/shadow-kernel/Vortex-Engine/milestone/7) | Recast/Detour NavMesh, NavAgents, behavior trees, sight/hearing perception |
| [v3.3.0 – VFX](https://github.com/shadow-kernel/Vortex-Engine/milestone/8) | GPU particles + Particle Editor, froxel volumetric fog / light shafts, decals |
| [v3.4.0 – World & Streaming](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) | Terrain, foliage painting, level streaming volumes, HZB occlusion culling |
| [v4.0.0 – XXL 10x Performance](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) | Render graph, bindless, GPU culling + ExecuteIndirect, mesh shaders, job system, ECS, TAA |

Full details per milestone: [[Roadmap]].

## Wiki pages

**Developer Documentation** — how to program the engine
- [[Developer-Guide]] — the entry point: how the layers fit and where everything is documented
- [[Scripting-Getting-Started]] — write your first `VortexBehaviour`, the lifecycle, compile & hot-reload
- [[Scripting-API-Reference]] — every type/method in the `Vortex` namespace (Input, Physics, Audio, UI, …)
- [[Entities-and-Components]] — the `GameEntity` / `Component` model and every component type
- [[Native-DLL-API]] — the complete `VortexAPI.dll` C ABI (the DLL surface)
- [[Managed-Interop-Bindings]] — the C# P/Invoke wrappers over the DLL

**Overview**
- [[Roadmap]] — all 10 milestones + backlog, issue-by-issue
- [[Architecture]] — the 3-layer native/managed engine design
- [[Feature-Status-Matrix]] — per-subsystem maturity (complete / partial / stub / missing)
- [[Horror-Game-Readiness]] — what blocks first-person horror development and when it lands

**Design docs**
- [[Design-Audio-Engine]] — miniaudio-based audio engine v1 (+ Steam Audio v2)
- [[Design-Global-Asset-Database]] — machine-wide content-addressed asset library
- [[Design-Asset-Store-Integrations]] — provider tiers, licenses, download pipeline
- [[Design-Claude-Integration]] — MCP server, embedded chat, Sound Studio
- [[Performance-Master-Plan]] — the path from today's numbers to the 10x GPU-driven renderer

**Process**
- [[Contributing-Workflow]] — labels, milestones, issue conventions, how to pick up work

Browse issues by label: [horror-blocker](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Ahorror-blocker) · [area:audio](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aaudio) · [area:rendering](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Arendering) · [area:claude](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aclaude) · [type:epic](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Atype%3Aepic)
