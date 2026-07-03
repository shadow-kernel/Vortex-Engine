# Developer Guide — Programming Vortex Engine

This is the entry point to Vortex Engine's **developer documentation**: how the engine is structured, how you program against it from a game project, and a complete reference for every interface, namespace and function you can call — the scripting API you write gameplay in, the entity/component model it runs on, and the full native-DLL surface underneath.

If you just want to write gameplay, start at [[Scripting-Getting-Started]]. If you want the total picture of what the engine exposes, this page maps it.

---

## The mental model: three layers, one boundary

Vortex is split so the heavy native engine can be reused by both the editor and every game you ship. Everything a game does crosses exactly one boundary — a flat C API in **`VortexAPI.dll`**.

```
┌─────────────────────────────────────────────────────────────┐
│  YOUR GAME  —  Assets/Scripts/*.cs  (C#, VortexBehaviour)    │   ← you write this
│  namespace Vortex: Input, Physics, Audio, UI, Scene, ...     │
└───────────────┬─────────────────────────────────────────────┘
                │  compiled + hosted by ScriptRuntime (IScriptHost)
┌───────────────▼─────────────────────────────────────────────┐
│  MANAGED LAYER  —  Editor.ECS (GameEntity/Component)         │   ← the model + bindings
│                    Editor.DllWrapper (P/Invoke bindings)     │
└───────────────┬─────────────────────────────────────────────┘
                │  P/Invoke — CallingConvention.Cdecl
┌───────────────▼─────────────────────────────────────────────┐
│  NATIVE DLL  —  VortexAPI.dll   extern "C" __declspec(...)   │   ← the boundary (~163 exports)
└───────────────┬─────────────────────────────────────────────┘
                │  static link
┌───────────────▼─────────────────────────────────────────────┐
│  ENGINE  —  Engine.lib  (C++20, Direct3D 12)                │   ← renderer/physics/audio/import
└─────────────────────────────────────────────────────────────┘
```

- **You write** C# scripts in your project's `Assets/Scripts`. They see only the clean **`Vortex`** namespace.
- The **managed layer** (`Editor.ECS` + `Editor.DllWrapper`) holds the scene model and the P/Invoke bindings.
- The **native DLL** (`VortexAPI.dll`) is the `extern "C"` boundary — ~163 exported functions.
- The **engine** (`Engine.lib`) is the C++20 / D3D12 core, statically linked into shipped games.

For the subsystem-level design (renderer passes, culling, DLSS, asset pipeline, VUI), see [[Architecture]]. This guide is about the **programmable surface**.

---

## Where each thing is documented

| You want to… | Read |
|---|---|
| Write your first gameplay script, understand the lifecycle, compile & hot-reload, VS setup | [[Scripting-Getting-Started]] |
| Look up every type/method you can call from a script (`Input`, `Physics`, `Audio`, `UI`, `Scene`, `Camera`, `Lighting`, `Settings`, `Gui`, …) | [[Scripting-API-Reference]] |
| Understand entities and components — `GameEntity`, `Transform`, `MeshRenderer`, `Camera`, `Light`, `Collider`, `Rigidbody`, `AudioSource`, `Animator`, `Script`, … | [[Entities-and-Components]] |
| Call or bind the DLL directly — the exported C ABI, calling convention, boundary structs | [[Native-DLL-API]] |
| Use the C# P/Invoke wrappers (`Editor.DllWrapper.VortexAPI`, `VortexAudio`) | [[Managed-Interop-Bindings]] |
| See the big-picture engine design and file map | [[Architecture]] |

---

## Namespaces at a glance

| Namespace | Assembly | What lives there |
|---|---|---|
| **`Vortex`** | editor/engine assembly (referenced by your scripts) | The gameplay API: `VortexBehaviour`, `Input`, `Time`, `Physics`, `Audio`, `Scene`, `Camera`, `Cursor`, `Application`, `Lighting`, `World`, `Settings`, `UI`, `Gui`/`VuiHandle`, `Animation`, `AudioSource`, `Vector3`, `Color`, `TriggerHit`, `IScriptHost`. → [[Scripting-API-Reference]] |
| **`Editor.ECS`** (+ `.Components.*`, `.Math`) | editor | The scene model: `GameEntity`, `Component`, all component types, `Vector3`/`Quaternion`. → [[Entities-and-Components]] |
| **`Editor.DllWrapper`** (+ `.EngineAPIStructs`) | editor | P/Invoke bindings: `VortexAPI` (partial), `VortexAudio`, handle/descriptor structs. → [[Managed-Interop-Bindings]] |
| **`Editor.Scripting`** | editor | `ScriptRuntime` (the `IScriptHost` implementation), `VortexScriptApi` (the `Vortex` namespace source), `DualSenseHid`. |
| *(C, global scope)* | `VortexAPI.dll` | The exported `extern "C"` functions. → [[Native-DLL-API]] |

---

## How a game project is wired

A Vortex game project is a folder with:

```
MyGame/
├─ Assets/
│  ├─ Scripts/        *.cs — your VortexBehaviour gameplay (compiled on Play)
│  ├─ Audio/          *.wav/.ogg — referenced by project-relative path
│  ├─ ...             models, textures, .vmat, .ventity, .vui, .vanim, scenes
├─ MyGameScripts.csproj + .sln   (generated — references the real engine assembly for IntelliSense)
```

- **Scripts** are ordinary `.cs` files under `Assets/Scripts`. They derive from `Vortex.VortexBehaviour` and are attached to entities via a `Script` component. On **▶ Play**, `ScriptRuntime` compiles them all into one assembly, instantiates a behaviour per `Script` component, and drives `Start`/`Update`/events. See the full flow in [[Scripting-Getting-Started]].
- **The DLL is not referenced by your `.csproj` as a P/Invoke target** — you never call `VortexAPI.dll` yourself. Your scripts call the `Vortex.*` facades; `ScriptRuntime` (as `IScriptHost`) and the `Editor.DllWrapper` layer do the P/Invoke for you. This is the "the DLL is integrated for you" contract: the whole native surface in [[Native-DLL-API]] is reachable, but the friendly `Vortex` API and the ECS model are what you touch.
- **Shipping:** a Release build compiles your scripts to `Game.dll` and packs assets into `.vpak`; the standalone player links `Engine.lib` and loads `VortexAPI.dll` — the exact same runtime path as in-editor play.

### The golden rule

> **Gameplay lives in project scripts (`VortexBehaviour`), never hardcoded in the engine.** Health, weapons, controllers, menus, AI — all yours. The engine stays generic. This is why the scripting API is the primary surface, and why the DLL/ECS references exist mainly so you understand what's underneath (and can extend the engine itself).

---

## A minimal end-to-end example

```csharp
using Vortex;

// Attach this class (via a Script component) to an entity in your scene.
public class Spinner : VortexBehaviour
{
    public float DegreesPerSecond = 90f;

    public override void Update(float dt)
    {
        Rotate(0f, DegreesPerSecond * dt, 0f);          // transform  → IScriptHost.SetRotation
        if (Input.GetKey("Space"))                       // input      → IScriptHost.GetKey
            Audio.PlayOneShot2D("Assets/Audio/ping.wav"); // audio      → AudioPlaybackService → VortexAudio → DLL
    }
}
```

Every call here is a `Vortex.*` facade → `IScriptHost` / a service → `Editor.DllWrapper` P/Invoke → `VortexAPI.dll` → `Engine.lib`. You wrote the top line; the four layers below are the documented surface.

---

## Building the engine itself

If you're contributing to the engine (not just making a game), the build is: clone with submodules, restore NuGet, build the solution (`Engine → VortexAPI.dll → Editor`). Prereqs and exact commands are in the [README](https://github.com/shadow-kernel/Vortex-Engine#-getting-started) and [CONTRIBUTING.md](https://github.com/shadow-kernel/Vortex-Engine/blob/main/CONTRIBUTING.md); the layer/file map is in [[Architecture]]. When you add a native export, you touch three of the pages above in order: [[Native-DLL-API]] (the `extern "C"` function), [[Managed-Interop-Bindings]] (the P/Invoke wrapper), and — if it's meant for gameplay — [[Scripting-API-Reference]] (a `Vortex.*` facade).

---

## See also

- [[Scripting-Getting-Started]] · [[Scripting-API-Reference]] · [[Entities-and-Components]] · [[Native-DLL-API]] · [[Managed-Interop-Bindings]]
- [[Architecture]] — subsystem design & file map
- [[Feature-Status-Matrix]] — per-subsystem maturity · [[Roadmap]] — where the API is heading
