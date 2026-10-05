<div align="center">

<img src="https://raw.githubusercontent.com/shadow-kernel/Vortex-Engine/main/Editor/Assets/Images/Logo.png" alt="Vortex Engine" width="130"/>

# Vortex Engine Wiki

### Build worlds. Import anything. Press ▶ Play.

[![Website](https://img.shields.io/badge/WEBSITE-engine.vortexstudio.dev-6C5CE7?style=for-the-badge)](https://engine.vortexstudio.dev)
[![Platform](https://img.shields.io/badge/PLATFORM-Windows%20%C2%B7%20macOS%20%C2%B7%20Linux-0078D6?style=for-the-badge)](Getting-Started)
[![Graphics](https://img.shields.io/badge/GRAPHICS-DirectX%2012%20%C2%B7%20Metal%20%C2%B7%20Vulkan-00A6FB?style=for-the-badge)](Architecture)
[![Claude](https://img.shields.io/badge/CLAUDE-MCP%20server%20%2B%20panel-D97757?style=for-the-badge)](Claude-Integration)
[![License](https://img.shields.io/badge/LICENSE-MIT-3DA639?style=for-the-badge)](https://github.com/shadow-kernel/Vortex-Engine/blob/main/LICENSE)

**📖 New here? Start with [[Getting-Started]] — your first scare in an hour. Then [[Horror-Essentials]] · [[Scripting-Getting-Started]] · [[Scripting-API-Reference]]**

</div>

---

**Vortex Engine** is an open-source (MIT) game engine for Windows, macOS and Linux: a native **C++** core with
**Direct3D 12**, **Metal** and **Vulkan** renderers, gameplay written as C# scripts (`VortexBehaviour`, never hardcoded
in the engine), and an editor that **Claude** can operate — through the built-in MCP server from Claude Code or Claude
Desktop, or in the editor's own Claude panel. The first game target is **first-person horror**; the long-term goal is a
16+ player Battle Royale at Black Ops quality. Releases: [GitHub Releases](https://github.com/shadow-kernel/Vortex-Engine/releases).

## What works today

- **Rendering** — PBR (Cook-Torrance GGX) on Direct3D 12, Metal and Vulkan; directional, point and spot lights with
  shadow maps; height fog; bloom, SSAO, vignette, film grain, colour grading, chromatic aberration; custom per-material
  shaders with live error reporting; GPU instancing and LOD; DLSS Super-Resolution + Frame Generation on NVIDIA/Windows
- **Editor** — the cross-platform editor (Avalonia, .NET 10) on macOS, Linux and Windows, plus the WPF editor on
  Windows: scene hierarchy, inspector, prefabs, full undo/redo, material/model/animation/collision/UI editors, Git panel
- **Gameplay scripting** — C# `VortexBehaviour`s with hot reload; input incl. gamepads, triggers, raycasts,
  `Instantiate`/`Destroy`, coroutines, save slots, scene loading, immediate-mode and retained UI
- **Audio** — 3D sound sources, sound containers with variation, reverb zones, a mixer with buses, ducking and meters
  ([[Audio]]); the Claude **Sound Studio** generates sounds from a description ([[Sound-Studio]])
- **Physics & animation** — collide-and-slide character movement; rigid bodies, joints and ragdolls on Jolt (macOS and
  Linux builds — the Visual Studio build gets Jolt in v3.1); skeletal animation with IK and hand poses
- **Assets** — Assimp import (FBX, glTF, OBJ …), a machine-wide content-addressed **library** shared by all projects
  ([[Asset-Library]]), and the **Asset Store** tab with Poly Haven, ambientCG, Kenney, Freesound, Sketchfab and more,
  licenses tracked into the build ([[Asset-Store]])
- **Claude** — 67 MCP tools: build scenes, materials, shaders, scripts, sound and worlds; play-test and look at the
  result; every change is an undo step, with dry runs and an operations log ([[Claude-Integration]], [[Claude-Tools]])
- **Shipping** — Build Game for Windows, macOS and Linux from any platform: packed `.vpak` assets, branding, credits,
  a license check; project templates with updates ([[Project-Templates]])

## The road ahead

| Milestone | Focus |
|---|---|
| [v2.6.0 – Audio Engine](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) | ✅ miniaudio backend, 3D spatial audio, mixer buses, `Vortex.Audio` API, editor audio tooling ([[Audio]]) |
| [v2.7.0 – Horror Essentials](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) | ✅ The game-dev-ready gate: shadow mapping (flashlight), fog, post-FX, transparency, triggers/raycasts/Instantiate/coroutines, save/load, Horror Starter template ([[Horror-Essentials]]) |
| [v2.8.0 – Windows · macOS · Linux](https://github.com/shadow-kernel/Vortex-Engine/releases/tag/v2.8.0) | ✅ macOS (Metal) + Linux (Vulkan) ports, Physics v2 (Jolt), editor-first hands |
| [v2.9.0 – Global Asset Database](https://github.com/shadow-kernel/Vortex-Engine/milestone/3) | ✅ PC-wide, SHA-256 content-addressed, deduplicated cross-project asset library + Library tab ([[Asset-Library]]) |
| [v2.10.0 – Asset Store & Claude Sound Studio](https://github.com/shadow-kernel/Vortex-Engine/milestone/4) | ✅ In-editor Store tab (Poly Haven, ambientCG, Kenney, Freesound, Sketchfab, Mixamo, …) ([[Asset-Store]]) + Claude Sound Studio ([[Sound-Studio]]) |
| [v3.0.0 – Claude-Native Engine](https://github.com/shadow-kernel/Vortex-Engine/milestone/5) | In-editor MCP server ([[Claude-Integration]], [[Claude-Tools]]) + embedded Claude panel; the cross-platform editor on Windows, template packs + updates, CI test gate, signing, crash reports |
| [v3.1.0 – Physics v2](https://github.com/shadow-kernel/Vortex-Engine/milestone/6) | Ragdolls ✅, constraints ✅, player on Jolt CharacterVirtual, Jolt in the Visual Studio build |
| [v3.2.0 – AI & Navigation](https://github.com/shadow-kernel/Vortex-Engine/milestone/7) | Recast/Detour NavMesh, NavAgents, behavior trees, perception, Tactical Shooter combat bots |
| [v3.3.0 – VFX](https://github.com/shadow-kernel/Vortex-Engine/milestone/8) | GPU particles + Particle Editor, volumetric fog, decals, weapon VFX for every camera |
| [v3.4.0 – World & Streaming](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) | Terrain, foliage + impostors, water, HZB culling, world partition + HLOD, **texture streaming**, BCn |
| [v3.5.0 – Multiplayer I: Netcode](https://github.com/shadow-kernel/Vortex-Engine/milestone/11) | Dedicated server, replication, prediction, lag compensation, interest management |
| [v3.6.0 – Animation v2 & Characters](https://github.com/shadow-kernel/Vortex-Engine/milestone/12) | State machine + blend spaces, aim offsets, retargeting, animation LOD for 64 characters |
| [v3.7.0 – Shooter Framework](https://github.com/shadow-kernel/Vortex-Engine/milestone/13) | Weapon assets, attachments, ballistics, damage + armor, loot, throwables, HUD, input + aim assist |
| [v3.8.0 – Multiplayer II: Online Services](https://github.com/shadow-kernel/Vortex-Engine/milestone/14) | Steam, parties, matchmaking, server hosting, voice chat, persistence, anti-cheat basics |
| [v3.9.0 – AAA Rendering](https://github.com/shadow-kernel/Vortex-Engine/milestone/15) | TAA + upscaling, physical sky, dynamic GI, reflections, exposure/grading, scopes, no PSO hitches |
| [v4.0.0 – XXL 10x Performance](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) | Render graph, bindless, GPU culling + ExecuteIndirect, mesh shaders, job system, ECS |
| [v4.1.0 – Production & LiveOps](https://github.com/shadow-kernel/Vortex-Engine/milestone/16) | Nightlies, crash reports, telemetry, delta patches, localization, accessibility |
| [v4.2.0 – Battle Royale Systems](https://github.com/shadow-kernel/Vortex-Engine/milestone/17) | Vehicles, drop plane + parachute, zone, loot distribution, match flow, squads, BR bots |
| [v5.0.0 – Battle Royale (16+ Players)](https://github.com/shadow-kernel/Vortex-Engine/milestone/18) | A complete 16+ player Battle Royale template at Black Ops quality — the engine's certification |

Full details per milestone: [[Roadmap]].

## Wiki pages

**Start here**
- [[Getting-Started]] — install, create a game from the Horror Starter, add a scare, build it
- [[Project-Templates]] — the templates, how their content arrives, updating a game from its template
- [[Horror-Essentials]] — shadows, fog, post-FX, triggers, raycasts, spawning, coroutines, saving, scene changes
- [[Audio]] — sound sources, containers, reverb, the mixer, music, scripting sound · [[Audio-Anleitung-DE]] (Deutsch)

**Guides**
- [[Claude-Integration]] — connect Claude Code / Desktop, the Claude panel, operations, dry runs and reverting
- [[Claude-Tools]] — every MCP tool with its parameters (generated from the code)
- [[Asset-Library]] — the machine-wide asset library and the Library tab
- [[Asset-Store]] — the store's sources, keys, downloads and licenses
- [[Sound-Studio]] — generate sound effects and loops from a description

**Developer documentation** — how to program the engine
- [[Developer-Guide]] — the entry point: how the layers fit and where everything is documented
- [[Scripting-Getting-Started]] — write your first `VortexBehaviour`, the lifecycle, compile & hot-reload
- [[Scripting-API-Reference]] — every type/method in the `Vortex` namespace (Input, Physics, Audio, UI, …)
- [[Entities-and-Components]] — the `GameEntity` / `Component` model and every component type
- [[Native-DLL-API]] — the `VortexAPI` C ABI (the native library surface)
- [[Managed-Interop-Bindings]] — the C# P/Invoke wrappers over the native library

Every C# sample in these pages is compiled against the gameplay API, as C# 5, by the test suite
(`DocsTests.WikiCodeSamplesCompile`).

**Overview**
- [[Roadmap]] — every milestone + backlog, issue by issue
- [[Architecture]] — the native / managed layers
- [[Feature-Status-Matrix]] — per-subsystem maturity (complete / partial / stub / missing)
- [[Horror-Game-Readiness]] — the checklist that gated horror development

**Design docs**
- [[Design-Audio-Engine]] · [[Design-Steam-Audio-Integration]] — the audio engine and its HRTF plans
- [[Design-Welle-A-Horror-Essentials]] · [[Design-Spot-Shadows-23]] — horror essentials and spot shadows
- [[Design-Global-Asset-Database]] — machine-wide content-addressed asset library
- [[Design-Asset-Store-Integrations]] — provider tiers, licenses, download pipeline
- [[Design-Claude-Integration]] — MCP server, embedded chat, Sound Studio
- [[Performance-Master-Plan]] — the path from today's numbers to the 10x GPU-driven renderer

**Process**
- [[Contributing-Workflow]] — labels, milestones, issue conventions, how to pick up work

Browse issues by label: [horror-blocker](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Ahorror-blocker) · [area:audio](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aaudio) · [area:rendering](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Arendering) · [area:claude](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aclaude) · [type:epic](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Atype%3Aepic)
