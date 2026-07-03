# Scripting: Getting Started

Gameplay in Vortex is **100% project-side C#**. The engine ships no hardcoded gameplay — health, weapons, controllers, menus, AI all live in **your** scripts. A script is a C# class that derives from [`Vortex.VortexBehaviour`](Scripting-API-Reference#vortexbehaviour) (the Vortex equivalent of Unity's `MonoBehaviour`), is attached to a scene entity through a **Script** component, and is compiled + run by the engine when you press **▶ Play**.

This page is the end-to-end tour: where scripts live, how they compile and run, how to attach them, and a full first script. For the complete type-by-type surface, see the [[Scripting-API-Reference]]; for the objects your scripts manipulate, see [[Entities-and-Components]].

---

## Where scripts live

Every project keeps its gameplay scripts under:

```
<YourProject>/
└─ Assets/
   └─ Scripts/
      ├─ Player/
      │  └─ PlayerController.cs      ← starter first-person controller (yours to edit)
      ├─ Enemies/
      │  └─ Stalker.cs
      └─ UI/
         └─ PauseMenuActions.cs      ← "one class per .vui screen" (see UI routing)
```

- Scripts can sit in **any subfolder** of `Assets/Scripts` — the engine scans recursively (`*.cs`).
- **Namespaces don't matter** for attachment: the runtime resolves a script by its **simple class name** (see [Attaching a script](#attaching-a-script-to-an-entity)). Two behaviours must not share a class name.
- The path is derived from the open project: `ScriptingService.ScriptsDir` = `<ProjectRoot>/Assets/Scripts` and is created automatically.

> **Reference:** `Editor/Core/Services/ScriptingService.cs`, `Editor/Scripting/ScriptRuntime.cs`.

---

## The `VortexBehaviour` lifecycle

Your class overrides only the callbacks it needs. The runtime calls them like this:

| Callback | When | Notes |
|---|---|---|
| `Start()` | Once, when Play begins (or after a hot-reload) | Cache the starting transform, spawn UI, etc. |
| `Update(float dt)` | Every simulation tick | `dt` is seconds since the last tick. Do movement, input, gameplay here. |
| `OnDestroy()` | Once, when Play stops (or before a hot-reload swap) | Release anything you grabbed. |
| `OnTriggerEnter(TriggerHit other)` | A character first enters this entity's **trigger** collider | `other` identifies who entered. |
| `OnTriggerStay(TriggerHit other)` | Every tick while a character stays inside the trigger | |
| `OnTriggerExit(TriggerHit other)` | A character leaves the trigger | |
| `OnCollisionEnter(TriggerHit other)` | A character first touches a **solid** (non-trigger) collider | |
| `OnAnimationEvent(string name)` | The playing animation clip crosses an authored **event marker** | Footstep frames, attack-hit frames, etc. |

Minimal script (this is exactly the "New Script" template):

```csharp
using Vortex;

// A gameplay behaviour. Attach the compiled class to an entity via a Script component.
public class NewBehaviour : VortexBehaviour
{
    // Called once when play begins.
    public override void Start() { }

    // Called every simulation tick. dt is the time in seconds since the last tick.
    public override void Update(float dt) { }
}
```

Inside a behaviour you already have, for free, on `this`:

- `Position` / `Rotation` — read/write your entity's transform (world position, Euler degrees).
- `Translate(dx,dy,dz)` / `Rotate(dP,dY,dR)` — relative moves.
- `Forward` / `Right` — unit vectors derived from your rotation (great for movement).
- `PlayAnimation("Walk", fade)`, `StopAnimation()`, `SetColor(r,g,b)`, `GetAudioSource()`.
- `EntityId` — the runtime handle for your entity (used with the static `Animation` / `Physics` APIs to affect *other* entities).

Everything else comes from the static `Vortex.*` facades: [`Input`](Scripting-API-Reference#input), [`Time`](Scripting-API-Reference#time), [`Physics`](Scripting-API-Reference#physics), [`Audio`](Scripting-API-Reference#audio), [`Scene`](Scripting-API-Reference#scene), [`Camera`](Scripting-API-Reference#camera), [`Cursor`](Scripting-API-Reference#cursor), [`UI`](Scripting-API-Reference#ui-immediate-mode), [`Gui`](Scripting-API-Reference#gui--vuihandle-retained-mode), [`Lighting`](Scripting-API-Reference#lighting), [`Settings`](Scripting-API-Reference#settings), [`World`](Scripting-API-Reference#world) and [`Application`](Scripting-API-Reference#application).

---

## Attaching a script to an entity

A script only runs if a scene entity carries a **Script** component pointing at it:

1. Select the entity in the **Scene Hierarchy**.
2. In the **Inspector**, **Add Component → Script**.
3. Pick your class from the script list (or type its name into `ScriptClassName`).

Under the hood the `Script` component stores just the **class name** (`ScriptClassName`). On Play, [`ScriptRuntime`](#how-scripts-compile--run) walks the scene, and for every entity whose `Script.ScriptClassName` matches a compiled `VortexBehaviour` subclass, it instantiates that class and binds it to the entity.

> Each behaviour gets a **unique internal handle** (not the raw engine entity id) so a script can only ever move *its own* entity — no id collisions.

---

## How scripts compile & run

You never run a build step by hand. When you press **▶ Play**:

1. **Compile** — `ScriptRuntime` gathers every `Assets/Scripts/**/*.cs` (excluding the legacy `VortexScripting.cs` stub) and compiles them into **one in-memory assembly** using the in-box C# compiler (`CSharpCodeProvider`). No external toolchain, no NuGet.
2. The scripts are compiled **against the real engine assembly**, so the `Vortex.*` types they call are the exact implementations that run — the API can never "drift" from what you see.
3. **Instantiate** — for each entity with a `Script` component, the matching `VortexBehaviour` is created and bound.
4. **Start** — `Start()` is called on every behaviour.
5. **Tick** — each frame: `Time.DeltaTime` is set, the gamepad is polled, then `Update(dt)` runs on every behaviour, then Animators advance, then queued trigger/collision events dispatch.
6. **Stop** — `OnDestroy()` runs on every behaviour; the collision + animation state is reset.

If compilation **fails**, the error (file, line, message) is surfaced in the console/overlay and Play does not start with broken code.

### Hot-reload

While playing, edit a script in Visual Studio, save, then **Alt-Tab back** to the editor/game window. If any script changed on disk, the runtime **recompiles and re-runs** the scene with fresh state — live in the in-editor viewport, the external game window, and Debug builds.

- A **compile error during hot-reload keeps the running scripts alive** and just logs the first error line — a typo never kills the running game.
- Hot-reload is a **dev-only** feature; a shipped game runs from a precompiled assembly (see below).

---

## Visual Studio integration (IntelliSense)

The editor generates a real VS solution next to your project so you get full IntelliSense on the `Vortex.*` API:

- `<ProjectName>Scripts.csproj` + `.sln` are written to the project root.
- The `.csproj` **references the real engine assembly** (the same DLL the runtime compiles against), so every type and method — `Physics.GroundMaterial`, `Audio.PlayOneShot`, `Vector3`, `VortexBehaviour`, … — resolves in VS exactly as it will run. There is no hand-maintained stub to go stale.
- The `.csproj` targets `net48`, includes `Assets\Scripts\**\*.cs`, and is (re)written each time you open the scripts so the reference path stays correct for your machine.

Open it from the editor (**Scripts → Open in Visual Studio**) or double-click the generated `.sln`.

---

## Shipping: precompiled gameplay

When you **Build Game** (Release), the exporter compiles your scripts into **`Game.dll`** and packs your assets into `.vpak`. The standalone player loads that precompiled assembly through `ScriptRuntime.PrecompiledAssembly` instead of compiling `.cs` at startup — fast boot, and no source shipped with the game. A **Debug** build instead source-links back to your project so hot-reload keeps editing the same files.

The runtime path is identical in every context (in-editor play, external window, shipped build), so *what you test is what ships*.

---

## Full example: first-person `PlayerController`

This is the starter controller every new 3D project gets in `Assets/Scripts/Player/PlayerController.cs`. It is **game code** — every field is yours to tune. It is attached to the **Main Camera** in the default scene: mouse looks, WASD walks relative to where you look, Shift sprints, Space jumps, Ctrl/C crouches. Velocity eases toward the target each frame for a smooth feel. Note the deliberate NaN/Inf guards before any value reaches the transform — a bad Euler/position would reach the native quaternion/matrix math and crash the engine.

```csharp
using Vortex;

public class PlayerController : VortexBehaviour
{
    public float WalkSpeed   = 6f;     // units/sec
    public float SprintSpeed = 10f;    // while holding Shift
    public float CrouchSpeed = 3f;     // while crouching
    public float Accel       = 14f;    // how fast velocity ramps to the target
    public float MouseSens    = 0.10f; // degrees per pixel of mouse movement
    public float JumpSpeed   = 7.5f;
    public float Gravity     = 20f;
    public float CrouchDrop  = 0.7f;   // how far the eye lowers when crouching

    private float _standEyeY;          // standing eye height, captured at spawn
    private float _vx, _vz;            // smoothed horizontal velocity
    private float _vy;                 // vertical velocity
    private bool  _grounded;
    private bool  _jumpHeld;           // edge-trigger jump
    private float _pitch, _yaw;        // look angles (degrees)

    public override void Start()
    {
        _standEyeY = Position.Y;
        var r = Rotation; _pitch = r.X; _yaw = r.Y;
        _vx = _vz = _vy = 0f;
        _grounded = true;
    }

    public override void Update(float dt)
    {
        if (dt <= 0f) return;

        // ---- Look: mouse (locked while playing) + arrow keys; clamp pitch, no roll ----
        _yaw   += Input.MouseDeltaX * MouseSens;
        _pitch += Input.MouseDeltaY * MouseSens;
        if (Input.GetKey("Left"))  _yaw   -= 90f * dt;
        if (Input.GetKey("Right")) _yaw   += 90f * dt;
        if (Input.GetKey("Up"))    _pitch -= 90f * dt;
        if (Input.GetKey("Down"))  _pitch += 90f * dt;
        if (_pitch > 89f) _pitch = 89f; else if (_pitch < -89f) _pitch = -89f;
        _yaw %= 360f;
        Rotation = new Vector3(_pitch, _yaw, 0f);

        // ---- Target horizontal velocity (relative to facing) ----
        bool crouch = Input.GetKey("LeftCtrl") || Input.GetKey("C");
        bool sprint = Input.GetKey("LeftShift");
        float speed = crouch ? CrouchSpeed : (sprint ? SprintSpeed : WalkSpeed);

        double yawRad = _yaw * System.Math.PI / 180.0;
        Vector3 f = new Vector3((float)System.Math.Sin(yawRad), 0f, (float)System.Math.Cos(yawRad));
        Vector3 r = new Vector3((float)System.Math.Cos(yawRad), 0f, (float)-System.Math.Sin(yawRad));
        float dx = 0f, dz = 0f;
        if (Input.GetKey("W")) { dx += f.X; dz += f.Z; }
        if (Input.GetKey("S")) { dx -= f.X; dz -= f.Z; }
        if (Input.GetKey("D")) { dx += r.X; dz += r.Z; }
        if (Input.GetKey("A")) { dx -= r.X; dz -= r.Z; }
        float len = (float)System.Math.Sqrt(dx * dx + dz * dz);
        float tx = 0f, tz = 0f;
        if (len > 0.001f) { tx = dx / len * speed; tz = dz / len * speed; }

        // ---- Ease velocity toward the target ----
        float kk = Accel * dt; if (kk > 1f) kk = 1f;
        _vx += (tx - _vx) * kk;
        _vz += (tz - _vz) * kk;

        // ---- Apply move + jump/gravity ----
        float eyeY = crouch ? _standEyeY - CrouchDrop : _standEyeY;
        Vector3 p = Position;
        p.X += _vx * dt;
        p.Z += _vz * dt;

        bool jump = Input.GetKey("Space");
        if (_grounded)
        {
            if (jump && !_jumpHeld) { _vy = JumpSpeed; _grounded = false; } // tap to jump
            else { p.Y = eyeY; }
        }
        if (!_grounded)
        {
            _vy -= Gravity * dt;
            p.Y += _vy * dt;
            if (p.Y <= eyeY) { p.Y = eyeY; _vy = 0f; _grounded = true; }
        }
        _jumpHeld = jump;
        Position = p;
    }
}
```

To collide with real level geometry instead of a flat floor, replace the manual `p.Y` handling with [`Physics.MoveCharacter`](Scripting-API-Reference#physics) (collide-and-slide against every Collider in the scene).

---

## UI routing: "one class per screen"

Retained-mode `.vui` screens fire named button actions. The runtime routes a button on `PauseMenu.vui` to a class named **`PauseMenuActions`** (screen file name + `Actions`) with a matching public parameterless method — found among your attached behaviours, or auto-instantiated from the gameplay assembly on first click (no scene wiring needed). If no such class exists it falls back to any running behaviour that has the method. See [`Gui` / `VuiHandle`](Scripting-API-Reference#gui--vuihandle-retained-mode) for driving screens by id.

```csharp
using Vortex;

// Handles buttons on PauseMenu.vui — no scene entity required.
public class PauseMenuActions : VortexBehaviour
{
    public void OnResume() { Gui.Pop(); Cursor.Locked = true; }
    public void OnQuit()   { Application.Quit(); }
}
```

---

## Common recipes

**Read input & move (already shown above)** — `Input.GetKey`, `Input.MouseDeltaX/Y`, `Input.LeftStickX`, etc.

**Play a 3D one-shot when a trigger is tripped:**
```csharp
public override void OnTriggerEnter(TriggerHit hit)
{
    if (hit.Tag != "Player") return;
    Audio.PlayOneShot("Assets/Audio/stinger.wav", Position, 1f);
    Audio.Music.CrossFade("Assets/Audio/chase.ogg", 2f);
}
```

**Surface-aware footsteps (editor-authored, no code dictionary):**
```csharp
string step = Physics.GroundStepSound(Position);   // sound assigned to the surface's material
if (step != "") Audio.PlayOneShot(step, Position);
```

**Switch scenes (deferred to end of tick — safe from `Update`):**
```csharp
Scene.Load("MainMenu");
```

**Draw an immediate-mode HUD:**
```csharp
public override void Update(float dt)
{
    UI.Text($"HP {_hp}", 24, 24, 200, 40, 28, Color.Rgb(255, 80, 80));
    if (UI.Button(UI.Width - 140, 24, 120, 44, "Quit", Color.Rgb(40,40,48), Color.Rgb(255,255,255), 20, 8))
        Application.Quit();
}
```

---

## See also

- [[Scripting-API-Reference]] — every type and member in the `Vortex` namespace.
- [[Entities-and-Components]] — the `GameEntity` / `Component` model your scripts run on.
- [[Native-DLL-API]] & [[Managed-Interop-Bindings]] — the DLL surface underneath.
- [[Architecture]] — how the three layers fit together.
