# Vortex managed layer (.NET 10, cross-platform)

| Project | What it is |
|---|---|
| `Vortex.Core` | The UI-framework-free runtime + tools core: ECS model, scene/prefab/material formats, scripting API + runtime (Roslyn on modern .NET), play-mode services, asset actions, viewport session, game packager, native interop (`VortexAPI`). It compiles the framework-free sources under `Editor/` as linked files, so the WPF editor, the Avalonia editor and the player run **one** implementation. `VORTEX_CORE` selects the few shared-core branches where a WPF type used to leak in. |
| `Vortex.Player` | The standalone game runtime (no UI framework): drives the native `GameHost` window and runs the gameplay layer each tick. Runs an exported game (`player.vortex` + `Assets.vpak`) or a loose project (`--project=`). |
| `Vortex.Editor` | The cross-platform editor shell (Avalonia 11): macOS-style unified toolbar, native menu bar, hierarchy, viewport (native Metal surface embedded in the window), inspector, environment, project browser, console, project hub, dialogs (settings, build, git, audio mixer, material editor). UI only — everything it does goes through `Vortex.Core`. |

## Install as an app (macOS)

```bash
tools/macos/make-app.sh --install --dmg   # dist/macos/Vortex Editor.app (+ .dmg), copied to /Applications
```

The bundle is self-contained (own .NET runtime, native engine, shaders, player for exports, templates).

## Exporting games (macOS, Windows, Linux)

**File ▸ Build (⌘B)** exports the open project for a target platform, branded with the values from
**Project Settings ▸ Build** (product name, version, bundle identifier, icon — a square PNG that becomes the
macOS `.icns`, the Windows exe icon and the Linux icon):

| Target | Result | Extra |
|---|---|---|
| macOS (Apple Silicon / Intel) | `<Name>.app` (Info.plist with name, version, identifier; AppIcon.icns; ad-hoc signed) | `.dmg` |
| Windows (x64) | `<Name>/<Name>.exe` with icon + version info embedded (pure managed PE writer, works on the Mac) | `.zip` |
| Linux (x64) | `<Name>/<Name>` + `<Name>.png` + `.desktop` entry + `run.sh` | `.zip` |

Release builds pack the project (`Assets.vpak`, `Scenes/*.vpak`, compiled `GameScripts.dll`); Debug builds
(host platform only) reference the live project folder so scripts and shaders hot-reload.

Every target needs a **runtime pack** — the standalone player published for that platform, the native engine built
for it and the engine shaders. The pack for the platform the editor runs on is the editor's own installation.
Packs for other platforms are built *on that platform* and installed in the Build dialog
(**Install runtime pack…**, a folder or .zip) into `~/Library/Application Support/VortexEngine/Runtimes/<rid>`;
during development `dist/runtimes/<rid>` is picked up automatically:

```bash
tools/macos/dev.sh pack --zip                         # macOS / Linux: dist/runtimes/<rid>/ (+ .zip)
powershell -File tools\windows\make-runtime-pack.ps1 -Zip   # Windows: dist\runtimes\win-x64\ (+ .zip)
```

The Windows pack needs Visual Studio's C++ tools (the DirectX 12 engine only compiles on Windows); the
`build-runtime-pack-windows` GitHub workflow builds it on a Windows runner and uploads it as an artifact. A Linux
pack additionally needs the native engine ported to Vulkan/SPIR-V, which is still open — the Build dialog says so.

## Developing (macOS)

There is no Visual Studio on the Mac; the workflow is CMake for the C++ engine and the .NET SDK for the editor,
player and core. `tools/macos/dev.sh` wraps the everyday commands:

```bash
tools/macos/dev.sh setup     # Homebrew toolchain (once)
tools/macos/dev.sh build     # native engine (Debug) + .NET layer (Debug)
tools/macos/dev.sh editor    # start the editor from the build tree (hub first; pass a project folder to open it)
tools/macos/dev.sh player Templates/Default3D Match
tools/macos/dev.sh test      # native tests + smoke runs
tools/macos/dev.sh app       # release .app into /Applications
```

IDEs (pick one):

* **JetBrains Rider** — open `Managed/Vortex.Managed.slnx`; run configurations "Vortex Editor" / "Vortex Player" are
  in `.run/`. Best C# experience (Avalonia previewer, debugger, refactoring).
* **VS Code** — open the repository; `.vscode/` has tasks (build native, build managed, build all) and launch
  configurations (editor / player with the .NET debugger, render test with LLDB). Recommended extensions are listed.
* **Xcode** (C++ engine only) — `tools/macos/dev.sh xcode` generates `build/macos-xcode` and opens it.

The editor loads the native library from the newest `build/<preset>/bin`, so after a C++ change just rebuild the
native target and restart the editor; C# changes need `dotnet build` (or F5 in the IDE).

## Build & run (macOS, development, by hand)

```bash
# native engine first (see the repository README)
cmake --preset macos-debug && cmake --build --preset macos-debug

export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec   # Homebrew dotnet
dotnet build Managed/Vortex.Managed.slnx

# editor (opens the last project or the project hub)
Managed/Vortex.Editor/bin/Debug/net10.0/Vortex.Editor [--project=/path/to/Project] [--scene=Name]

# player
Managed/Vortex.Player/bin/Debug/net10.0/Vortex.Player --project=/path/to/Project [--scene=Name] [--width=1280 --height=720]
```

The native library (`libVortexAPI.dylib` + `Shaders/msl`) is found automatically: next to the binaries, in an `.app` bundle's `Contents/MacOS`, in `$VORTEX_NATIVE_DIR`, or — for development — in the newest `build/<preset>/bin` of the repository.

## Smoke tests (no mouse needed)

```bash
# player: render 6 s, capture a frame, exit
Vortex.Player --project=Templates/Default3D --scene=Match --exit-after=6 --capture=/tmp/frame.bmp
# editor: open, exercise create/undo/redo/components/save/play/export, capture the window, exit
VORTEX_SMOKE_FULL=1 Vortex.Editor --project=/tmp/copy-of-Default3D --smoke=14 --capture=/tmp/edcap
```

## Layering rules

* `Vortex.Core` never references a UI framework. Host services it needs (UI-thread posting, key state, cursor capture, message boxes) are behind the small hook classes in `Input/`, `Threading/` and `Viewport/IViewportHost`.
* Editor shells contain views only; every user action funnels through `Shell/EditorCommands.cs` → core services, so menus, toolbars, context menus and shortcuts share one code path.
* Windows keeps building the original WPF editor from `Vortex.slnx` unchanged; the shared sources carry `#if VORTEX_CORE` only where a WPF type had leaked into otherwise pure code.

## Physics

Vortex 2.7 simulates rigid bodies with [Jolt Physics](https://github.com/jrouwe/JoltPhysics) (issues #100/#102/#106/#107).
Everything is authored in the editor; scripts only need the physics API when they want to push things around.

### Making a prop physical

1. Select the entity (a crate, barrel, bottle …) and add a **Collider** — Box, Sphere, Capsule or Mesh. Several
   colliders on one entity form one compound body; a Mesh Collider becomes a static triangle mesh, or a convex
   hull when *Convex* is on (a *moving* mesh collider always simulates as its hull).
2. Add a **Rigidbody** and pick the body type:
   * **Dynamic** — falls, stacks, rolls, gets pushed by the player and by other bodies. Set *Mass (kg)*, *Drag*
     (linear damping), *Angular drag*, *Use gravity* and the *Freeze* axes (e.g. freeze rotation X/Z for a
     character-like prop). Mass 0 lets the engine derive the mass from the shape's volume.
   * **Kinematic** — you move it (a script writing `Position`/`SetWorldPose`, or an animation) and it pushes
     dynamic bodies out of its way: doors, lifts, moving platforms. Its collider is re-baked for the character
     controller automatically whenever it moves.
   * **Static** (or no Rigidbody at all) — level geometry.
3. Press **Play**. Entities without a Collider but with a Rigidbody use their render mesh's shape as a fallback.

Entities tagged **`Debris`** simulate on a separate layer that collides with the world and other props but never
blocks the player (shell casings, small clutter).

### Physics materials

Every Collider has a physics material: **Friction** (0 = ice … 1 = rubber, default 0.5) and **Bounciness**
(restitution, default 0). Edit them in the collider's inspector card. When two bodies touch, the engine combines
both materials.

### Triggers

A Collider with **Is Trigger** on is a sensor: nothing collides with it, but whatever enters it is reported.
The player character reports through the collision world as before; simulated props (a barrel rolling into a
pressure plate) report through the physics world — both arrive at the same script callbacks:
`OnTriggerEnter / OnTriggerStay / OnTriggerExit` on the trigger's behaviour **and** on the entering entity's
behaviour, each seeing the other as the `TriggerHit`. Solid first contacts between props raise `OnCollisionEnter`
on both entities.

### The player and props

The character controller (`Physics.MoveCharacter`) treats dynamic bodies like walls — you cannot walk through a
crate — but every push is turned into an impulse on the body, so barrels and crates slide away instead of
blocking you (a light box never leaves faster than you pushed it; heavy ones barely move). Standing on a crate
works like standing on any surface.

### Script API

```csharp
// A shot hits something: knock it over (Weapon.cs style)
if (Physics.Raycast(eye, dir, 40f, out var hit) && Physics.HasRigidbody(hit.EntityId))
    Physics.AddImpulseAtPoint(hit.EntityId, dir * 12f, hit.Point);

// Explosion: shove everything within 4 m away from the blast
foreach (var id in Physics.OverlapSphere(Position, 4f))
    Physics.AddImpulse(id, (Scene.PositionOf(id) - Position).Normalized * 25f);

// A prop's own behaviour: throw itself when the player interacts, read its speed later
public override void OnMessage(string msg, object arg)
{
    if (msg == "throw") { Physics.SetKinematic(EntityId, false); Velocity = Forward * 8f + Vector3.Up * 3f; }
}
public override void Update(float dt) { if (Velocity.Length > 6f) Debug.DrawSphere(Position, 0.5f); }
```

`Physics.AddForce / AddForceAtPoint / AddImpulse / AddImpulseAtPoint / AddTorque`, `SetVelocity / GetVelocity`,
`SetAngularVelocity / GetAngularVelocity`, `SetKinematic`, `WakeUp / IsSleeping`, `SetGravity`, `OverlapSphere`,
`HasRigidbody`, `GetMass` work on any entity handle (from `Scene.Find`, a `RaycastHit`, a `TriggerHit`, `EntityId`).
Behaviours also get `AddForce`, `AddImpulse`, `AddTorque`, `Velocity`, `AngularVelocity` and `HasRigidbody` for their
own entity. `Physics.Raycast` casts against the live physics world when it runs (static level, props at their
current pose, triggers excluded); with an explicit entity-layer mask it uses the managed collision world.
`Physics.Simulated` tells whether rigid-body physics is running at all.

The simulation runs at a fixed 60 Hz (at most 4 steps per frame), after the behaviours' `Update` and before
`LateUpdate`/animation, in editor play, the play window and the standalone player alike.

### Debug view

**View ▸ Toggle Physics Debug (play)** draws every physics body as cyan wire lines over the running game — the
Jolt shapes, not the green authored collider nets — so you can see why a prop does not rest the way you expect.
Scripts can read the same lines through `PhysicsService.GetDebugLines()`.

### Engine builds without Jolt

The Windows Visual Studio build (and any engine built with `-DVORTEX_ENABLE_JOLT=OFF`) contains the physics API as a
stub: `PhysicsInit()` returns 0. The managed side logs one notice
(`[Physics] rigid-body physics unavailable: …`) and keeps the previous behaviour — static collision, character
controller, triggers, footsteps all work, Rigidbody props simply stay where they were placed, and every physics
call from a script returns false / zero.
