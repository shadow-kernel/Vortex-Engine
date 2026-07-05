# Scripting API Reference — the `Vortex` namespace

This is the complete surface your gameplay scripts program against. Everything here lives in the **`Vortex`** namespace and is provided by the engine at runtime (source: `Editor/Scripting/VortexScriptApi.cs`). Add `using Vortex;` at the top of a script and all of it is in scope.

For how scripts are structured, compiled and attached, read [[Scripting-Getting-Started]] first.

**Contents**

- Types: [`Vector3`](#vector3) · [`Color`](#color) · [`TriggerHit`](#triggerhit)
- Base class: [`VortexBehaviour`](#vortexbehaviour)
- Static facades: [`Input`](#input) · [`Time`](#time) · [`Scene`](#scene) · [`Cursor`](#cursor) · [`Application`](#application) · [`Camera`](#camera) · [`Physics`](#physics) · [`Animation`](#animation) · [`Audio`](#audio) · [`Lighting`](#lighting) · [`World`](#world) · [`Settings`](#settings)
- UI: [`UI` (immediate mode)](#ui-immediate-mode) · [`Gui` / `VuiHandle` (retained mode)](#gui--vuihandle-retained-mode)
- Handles: [`AudioSource`](#audiosource)
- Advanced: [`IScriptHost`](#iscripthost-advanced)

Convention below: `▸ member` — signature — description.

---

## Value types

### `Vector3`

A 3-component float vector (`X`, `Y`, `Z`). Used for positions, rotations (Euler degrees) and directions.

```csharp
public struct Vector3
{
    public float X, Y, Z;
    public Vector3(float x, float y, float z);

    public static Vector3 Zero;      // (0,0,0)
    public static Vector3 One;       // (1,1,1)
    public static Vector3 Up;        // (0,1,0)
    public static Vector3 Forward;   // (0,0,1)
}
```

### `Color`

An RGBA color with channels in `0..1`. Use the `Rgb`/`Rgba` helpers to build one from `0..255` values.

```csharp
public struct Color
{
    public float R, G, B, A;
    public Color(float r, float g, float b, float a);

    public static Color Rgb(int r, int g, int b);            // alpha = 1
    public static Color Rgba(int r, int g, int b, int a);    // all 0..255
    public Color WithAlpha(float a);                          // copy with new alpha
}
```

### `TriggerHit`

Passed to the collision/trigger callbacks. Identifies the **other** entity involved (who entered your trigger, or the surface you hit).

```csharp
public struct TriggerHit
{
    public long   EntityId;   // script handle of the other entity (0 if it has no script)
    public string Name;       // the other entity's name
    public string Tag;        // the other entity's tag, e.g. "Player", "Enemy"
}
```

---

## `VortexBehaviour`

The base class for all gameplay behaviours (Vortex's `MonoBehaviour`). Derive from it, override the callbacks you need, and attach the class to an entity via a **Script** component.

### Identity

▸ `long EntityId { get; }` — the runtime handle of the entity this behaviour is attached to. Pass it to the static `Animation` / `Physics` APIs to affect this entity from outside, or to compare against a `TriggerHit.EntityId`.

### Transform

▸ `Vector3 Position { get; set; }` — world position of this entity (read/write).
▸ `Vector3 Rotation { get; set; }` — Euler rotation in **degrees** (`X`=pitch, `Y`=yaw, `Z`=roll).
▸ `void Translate(float dx, float dy, float dz)` — move by a delta.
▸ `void Rotate(float dPitch, float dYaw, float dRoll)` — rotate by a delta (degrees).
▸ `Vector3 Forward { get; }` — unit forward vector in world space, from this entity's yaw + pitch.
▸ `Vector3 Right { get; }` — unit right vector (horizontal), from this entity's yaw.

### Appearance

▸ `void SetColor(float r, float g, float b)` — set this entity's base color at runtime (e.g. flash a color when a trigger is touched).

### Animation (on this entity)

▸ `bool PlayAnimation(string clip, float fade = 0f)` — play a clip on this entity's Animator. `clip` is a NAME from the Animator's clip table (e.g. `"Walk"`) or a `.vanim` path. `fade > 0` crossfades from the current pose (seconds). Returns `false` if the entity has no Animator or the clip can't be found.
▸ `void StopAnimation()` — freeze the current pose.
▸ `void SetAnimationSpeed(float speed)` — playback speed multiplier (`1` = authored speed).
▸ `bool IsAnimationPlaying(string clip = null)` — is an animation playing? Pass a clip name to ask about that clip specifically.
▸ `float AnimationTime { get; }` — current playback time in seconds.

### Audio (on this entity)

▸ `AudioSource GetAudioSource()` — this entity's [`AudioSource`](#audiosource) component as a script handle (Play/Stop/Pause/Resume, live Volume/Pitch/fades), or `null` if the entity has none.

### Lifecycle callbacks (override these)

▸ `virtual void Start()` — once, when play begins.
▸ `virtual void Update(float dt)` — every tick; `dt` = seconds since last tick.
▸ `virtual void OnDestroy()` — once, when play stops.
▸ `virtual void OnAnimationEvent(string name)` — the playing clip crossed an authored **event marker** (footstep, attack-hit, …); the marker's name is passed.
▸ `virtual void OnTriggerEnter(TriggerHit other)` — a character first entered this entity's **trigger** collider.
▸ `virtual void OnTriggerStay(TriggerHit other)` — every tick while a character stays inside the trigger.
▸ `virtual void OnTriggerExit(TriggerHit other)` — a character left the trigger.
▸ `virtual void OnCollisionEnter(TriggerHit other)` — a character first touched this entity's **solid** (non-trigger) collider.

---

## `Input`

Keyboard, mouse and gamepad. **All input is dead unless the game window is focused** (works in-editor, in the external window, and in shipped builds), and movement/look is frozen while a gameplay-blocking UI screen is up.

### Keyboard

▸ `static bool GetKey(string key)` — is a key held? Names match WPF keys, e.g. `"W"`, `"Space"`, `"LeftShift"`, `"LeftCtrl"`, `"Left"`, `"Up"`, `"Escape"`.

### Mouse

▸ `static float MouseDeltaX { get; }` — horizontal mouse movement since last tick, in pixels. Non-zero only while the game has captured the cursor (in play, before ESC). Forced to `0` while gameplay is blocked or the window is unfocused. Use it for mouse-look.
▸ `static float MouseDeltaY { get; }` — vertical mouse movement, same rules.

### Window focus

▸ `static bool WindowFocused { get; }` — `true` only while this app's window is the foreground window. All input is ignored otherwise (so an alt-tabbed game can't be driven by stray input).

### Gamepad / controller

Supports Xbox and PlayStation (DualSense/DualShock) pads via `Windows.Gaming.Input` (with a direct DualSense HID path and an XInput fallback). Polled once per tick. Sticks are `-1..1` with dead zones, triggers `0..1`. Frozen to neutral while a gameplay-blocking screen is up or the window isn't focused. Buttons use Xbox-style names regardless of the physical pad.

▸ `static bool GamepadConnected { get; }` — a controller is connected.
▸ `static float LeftStickX { get; }` / `LeftStickY { get; }` — left stick, `-1..1` (X right, Y up).
▸ `static float RightStickX { get; }` / `RightStickY { get; }` — right stick, `-1..1` (for look).
▸ `static float LeftTrigger { get; }` / `RightTrigger { get; }` — triggers, `0..1`.
▸ `static bool GetGamepadButton(string name)` — is a button held? Names: `A B X Y LB RB Back Start LeftStick RightStick DPadUp DPadDown DPadLeft DPadRight`. (On a PlayStation pad these map A=Cross, B=Circle, X=Square, Y=Triangle.)
▸ `static bool GetGamepadButtonDown(string name)` — was the button pressed **this tick** (rising edge)?

---

## `Time`

▸ `static float DeltaTime { get; }` — seconds since the last tick (set by the runtime each frame). Same value passed to `Update(dt)`.

---

## `Scene`

Scene control, entity queries and runtime spawning. The **game** decides when/which scene loads; the switch is **deferred to the end of the current tick**, so it's safe to call from inside `Update`.

▸ `static void Load(string name)` — request switching to the scene named `name` (matches a scene authored in the project).
▸ `static event Action<string> Loading` — fired right before a requested switch is applied — hook it to show a loading screen / fade. Cleared automatically on play stop.

**Entity queries (#39)** — all queries return **script handles** (`long`, `0` = not found): the same ids used by `TriggerHit`, `RaycastHit` and behaviours' `EntityId`.

▸ `static long Find(string name)` — first entity with that exact name, anywhere in the scene.
▸ `static long[] FindByTag(string tag)` — every entity carrying the Tag (set in the Inspector).
▸ `static long Parent(long entity)` / `static long[] Children(long entity)` — hierarchy traversal.
▸ `static string NameOf(long entity)` / `static string TagOf(long entity)`
▸ `static Vector3 PositionOf(long entity)` / `static void SetPositionOf(long entity, Vector3 p)` — read/move ANY entity.
▸ `static T GetBehaviour<T>(long entity)` — the script instance running on another entity: `Scene.GetBehaviour<DoorController>(door)?.Open();`
▸ `static Light GetLight(long entity)` / `static AudioSource GetAudioSource(long entity)` — drive any entity's light/audio from a manager script.
▸ `static void SetActive(long entity, bool active)` — show/hide an entity (+children) at runtime. (Colliders of hidden entities currently stay solid.)

**Runtime spawning (#36)** — THE jump-scare primitive:

▸ `static long Instantiate(string prefabPath, Vector3 position, float yawDegrees = 0)` — spawn a `.ventity` prefab (e.g. `"Assets/Prefabs/Monster.ventity"`). Its scripts `Start()` the same frame, its colliders join the world, it renders immediately. Returns the new entity's handle.
▸ `static void Destroy(long entity)` — remove an entity from the running game (behaviours get `OnDestroy`, rendering + colliders removed). Play mode never alters the authored scene: spawns/destroys are rolled back when play stops.

---

## `Events`

Typed event bus (#38) — decoupled game-wide messaging. Define an event class in your scripts, subscribe in `Start`, publish from anywhere. Subscriptions clear automatically on play stop / scene switch.

▸ `static void Subscribe<T>(Action<T> handler)` / `static void Unsubscribe<T>(Action<T> handler)`
▸ `static void Publish<T>(T evt)` — delivered immediately; a throwing handler is logged and skipped.

```csharp
public class MonsterSpotted { public Vector3 Where; }
// in the monster:   Events.Publish(new MonsterSpotted { Where = Position });
// in the music AI:  Events.Subscribe<MonsterSpotted>(OnSpotted);
```

For DIRECT entity-to-entity calls use `SendMessage(targetEntity, "open")` on the behaviour — the target's `OnMessage(string, object)` runs the same frame.

---

## `Save`

Persistent save data (#40) — PlayerPrefs-style key/value storage **plus save slots**, stored per game under `%APPDATA%\VortexGames\<project>`. Identical in editor play and shipped builds. Auto-flushes on scene switch + play end; call `Flush()` after a checkpoint to be crash-safe.

▸ `static void SetInt/SetFloat/SetString/SetBool(string key, value)`
▸ `static int GetInt(string key, int def = 0)` (+ `GetFloat/GetString/GetBool`)
▸ `static bool HasKey(string key)` / `static void DeleteKey(string key)` / `static void DeleteAll()`
▸ `static void UseSlot(int slot)` / `static int CurrentSlot { get; }` / `static bool SlotExists(int slot)` / `static void DeleteSlot(int slot)`
▸ `static void Flush()` — write to disk now.

---

## `Cursor`

Mouse mode. Locked = captured + hidden for mouse-look (gameplay). Unlocked = free cursor so the player can click UI (lobby / ESC menu / shop). The game sets it; the engine enforces it.

▸ `static bool Locked { get; set; }`

---

## `Application`

▸ `static void Quit()` — quit the game (closes the standalone player / stops play).

---

## `Camera`

Player-view camera control (the live game/play view).

▸ `static void SetFieldOfView(float fovDegrees)` — vertical FOV in degrees (clamped 30–120 by the engine).

---

## `Physics`

Character collision. `MoveCharacter` resolves a capsule (feet position, radius, height) against every **Collider** in the scene with collide-and-slide: the ground is solid, you can't walk through walls/props/models, and you can't clip even up close. Add Colliders to your level objects in the editor; the character itself needs no collider — you pass its capsule each frame.

▸ `static bool Grounded { get; }` — `true` when the last `MoveCharacter` ended resting on a surface. Use it to reset jump/gravity.

▸ `static Vector3 MoveCharacter(Vector3 feet, float radius, float height, Vector3 move)` — move the capsule by `move` (input + gravity) and return the collision-resolved feet position. Call each frame.

▸ `static Vector3 MoveCharacter(Vector3 feet, float radius, float height, Vector3 move, long characterId)` — as above, but `characterId` (e.g. your `EntityId`) registers this character so **other** characters can't walk through it (multiplayer / multiple actors).

**Surface queries** — a ray straight down from `from` (up to `maxDist`, default `3`) against the world colliders. Three flavours for footsteps, from most manual to fully editor-authored:

▸ `static string GroundTag(Vector3 from, float maxDist = 3f)` — the **Tag** of the surface entity below you (e.g. tag floors `"grass"`, `"wood"`, `"metal"`), or `""`. The classic "what am I standing on?" query.
▸ `static string GroundMaterial(Vector3 from, float maxDist = 3f)` — the **material name** of the surface (e.g. `"grass"` from `grass.vmat`), or `""`. Scalable: map material→sound once and every object using that material plays the right footstep, in every scene, with no per-object tagging.
▸ `static string GroundStepSound(Vector3 from, float maxDist = 3f)` — the **footstep sound** assigned to the surface's material in the Material Editor (a project-relative clip / `.vsndc` path), or `""`. Editor-first: a footstep script becomes `Audio.PlayOneShot(Physics.GroundStepSound(pos), pos)` and adding a new surface never touches code.

**General raycast (#35)** — any direction, against the solid colliders. Line-of-sight, interaction rays, "what am I looking at":

▸ `static bool Raycast(Vector3 origin, Vector3 direction, float maxDist, out RaycastHit hit, int layerMask = ~0)` — closest hit with `Point`, `Normal` (faces the origin), `Distance`, `EntityId`, `Name`, `Tag`. `layerMask` filters by entity Layer bit.
▸ `static bool Raycast(Vector3 origin, Vector3 direction, float maxDist, int layerMask = ~0)` — just "did I hit something?".

```csharp
RaycastHit hit;
if (Physics.Raycast(eyePos, Forward, 2.5f, out hit) && hit.Tag == "Door")
    SendMessage(hit.EntityId, "interact");
```

Boxes test as exact oriented boxes, spheres analytically, mesh colliders per triangle.

**Coroutines + timers (#37)** — on every `VortexBehaviour`:

▸ `Coroutine StartCoroutine(IEnumerator routine)` — `yield return new WaitForSeconds(1.5f)` pauses, `yield return null` waits one frame. The first step runs immediately.
▸ `void StopCoroutine(Coroutine c)` / `void StopAllCoroutines()`
▸ `void Invoke(Action action, float delay)` / `void InvokeRepeating(Action action, float delay, float interval)` / `void CancelInvokes()`

```csharp
IEnumerator JumpScare()
{
    GetLight().Enabled = false;                  // lights out
    yield return new WaitForSeconds(1.5f);       // let the dread build
    Scene.Instantiate("Assets/Prefabs/Monster.ventity", Position + Forward * 2f, Rotation.Y + 180f);
    Audio.PlayOneShot("Assets/Audio/sting.wav", Position);
}
```

---

## `Animation`

Skeletal animation on **other** entities (your own entity has `PlayAnimation()` directly on the behaviour). `clip` is a name from the target's Animator clip table (e.g. `"Walk"`) or a `.vanim` path. Build state machines in your scripts with these calls.

▸ `static bool Play(long entityId, string clip, float fade = 0f)` — play a clip; `fade > 0` crossfades (seconds).
▸ `static void Stop(long entityId)` — freeze the entity on its current pose.
▸ `static void SetSpeed(long entityId, float speed)` — playback speed multiplier (`1` = authored).
▸ `static bool IsPlaying(long entityId, string clip = null)` — is an animation playing? Pass a clip name to ask about that clip.
▸ `static float Time(long entityId)` — current playback time in seconds.

---

## `Audio`

Game audio for scripts. Clip paths are **project-relative** (`"Assets/Audio/scream.wav"`) and resolve identically in editor play mode and shipped `.vpak` builds. One-shots use pooled voices that auto-reclaim — nothing to hold or free.

▸ `static void PlayOneShot(string clipPath, Vector3 position, float volume = 1f, float pitch = 1f)` — positional (3D) one-shot at a world position. Distance attenuation uses sensible defaults (min 1, max 500, logarithmic).
▸ `static void PlayOneShot2D(string clipPath, float volume = 1f, float pitch = 1f)` — flat 2D one-shot (UI clicks, stingers), no position/attenuation.
▸ `static void SetBusVolume(string busName, float volume)` — mixer bus volume by name (`"Master"`, `"Music"`, `"SFX"`, `"Ambience"`, `"UI"`), `0..1`. What settings sliders call; applies in real time (and persists in shipped builds).
▸ `static float GetBusVolume(string busName)` — read a bus's volume (`1` if unknown).

### `Audio.Music`

One streamed, looping track at top priority (never stolen), with fades.

▸ `static void Play(string clipPath, float fadeInSeconds = 0f)` — start a track, fading in (`0` = immediate). Replaces a playing track.
▸ `static void CrossFade(string clipPath, float seconds)` — fade the current track out while the new one fades in, overlapping.
▸ `static void Stop(float fadeOutSeconds = 0f)` — stop, optionally fading out.
▸ `static bool IsPlaying { get; }`
▸ `static float Volume { get; set; }` — music channel volume (multiplies the per-track fades).

---

## `Lighting`

Lighting/atmosphere for scripts — flicker, lightning, mood. With submit-once rendering a static scene keeps whatever you last set, so per-frame changes here drive a living, flickering environment.

▸ `static void SetAmbient(float strength)` — global ambient strength (`0` = pitch black, `1` = flat-lit). Dip it for darkness/flicker.
▸ `static void SetDirectional(float dx, float dy, float dz, float r, float g, float b, float intensity)` — the sun/key directional light: direction, color (`0..1`), intensity.
▸ `static void ClearLights()` — clear all lights.

---

## `Atmosphere`

Scene fog (Welle A #27): exp2 distance fog with optional ground mist. The flashlight cone visibly "cuts" into it. Persistent until changed — call once in `Start()` for a static mood, or per frame for weather.

▸ `static void SetFog(float density, float heightY = 0, float heightFalloff = 0, float r = 0.02f, float g = 0.025f, float b = 0.035f)` — `density > 0` enables (try `0.05–0.2`); `heightFalloff > 0` turns it into ground mist below `heightY`. Colors linear `0..1` — keep them dark for horror.
▸ `static void ClearFog()` — fog off.

Authoring note: fog can also be set per scene **without any script** in the editor's **Environment** panel (saved into the `.vscene`, applied automatically in the editor, in play mode and in shipped games). Scripts override the authored values at runtime; leaving play mode restores them.

---

## `PostFx`

Screen post-effects (#28/#29): vignette, animated film grain, chromatic aberration — the horror tension package. Settings are persistent renderer state, apply the **same frame**, and work in the editor viewport, play mode and shipped games. All effects off = the whole post pipeline is bypassed (zero GPU cost).

▸ `static void SetVignette(bool enabled, float intensity = 0.8f, float smoothness = 0.5f, float roundness = 1f, float r = 0, float g = 0, float b = 0)` — darkened screen edges ("claustrophobia dial"). `roundness 1` = circular on any aspect, `0` = follows the screen shape.
▸ `static void SetGrain(bool enabled, float intensity = 0.35f, float size = 1.6f)` — luminance-weighted animated grain (shadows grain more); `size` in output pixels (1–3 is filmic).
▸ `static void SetChromaticAberration(bool enabled, float strength = 0.35f, float falloff = 1.2f)` — radial RGB fringing towards the edges. `0.2–0.6` = unease, `2+` = heavy VHS smear.
▸ `static void ClearAll()` — everything off.

The signature use: ramp the dread as the monster closes in —

```csharp
public class PanicFx : VortexBehaviour
{
    float panic;                       // 0 = calm, 1 = it's right behind you
    public override void Update(float dt)
    {
        float target = MonsterNearby() ? 1f : 0f;               // your game's proximity check
        panic += (target - panic) * System.Math.Min(1f, dt * 0.5f);   // ~2 s ramp
        PostFx.SetGrain(true, 0.1f + 0.5f * panic, 1.6f);
        PostFx.SetVignette(true, 0.6f + 0.6f * panic, 0.45f);
        PostFx.SetChromaticAberration(panic > 0.15f, 0.25f + 1.2f * panic, 1.2f);
    }
}
```

Like fog, all three effects are also authorable per scene in the editor's **Environment** panel (serialized in the `.vscene`, no script needed).

---

## `Debug`

Logging, the in-game dev console, and debug draw (#42) — all work in editor play, the play window AND shipped builds.

▸ `static void Log(object)` / `LogWarning` / `LogError` — to the editor Console panel and the in-game console.
▸ `static void ShowConsole(bool)` — the on-screen dev console overlay; **F9** toggles it any time. Info/warn/error are color-coded.
▸ `static void DrawLine(Vector3 a, Vector3 b, float r = 0.2f, float g = 1f, float b = 0.3f, float duration = 0f)` — wire line; `duration 0` = this frame (re-draw per frame), `> 0` = keep alive that many seconds.
▸ `static void DrawRay(Vector3 origin, Vector3 dir, float length, ...)` — pairs with `Physics.Raycast` to SEE your rays.
▸ `static void DrawSphere(Vector3 center, float radius, ...)` — hearing ranges, trigger radii, blast zones.

Shapes render always-on-top as wireframes (the gizmo pass), so they're visible through walls — exactly what you want from debug draw.

---

## `World`

Script-driven world geometry — assemble a level/backdrop from meshes without authoring a scene file. Render-only (no collision yet); placements persist until `Clear()`.

▸ `static void Add(string meshPath, float x, float y, float z, float yawDegrees, float scale)` — place a model. `meshPath` is absolute or project-relative.
▸ `static void Clear()` — remove all script-placed geometry.

---

## `Settings`

Generic engine settings a game's options menu applies (the UI surfaces values; the script reads the widgets and calls these).

**Display**
▸ `static void SetVSync(bool on)`
▸ `static void ToggleFullscreen()` · `static bool IsFullscreen { get; }` · `static void SetFullscreen(bool on)` (idempotent)
▸ `static void SetResolution(int width, int height)` — windowed only; resizes client area + swapchain.
▸ `static void SetFieldOfView(float degrees)` — renderer-global projection FOV.

**Scaling / upscaling**
▸ `static void SetRenderScale(float scale)` · `static float RenderScale { get; }` — `0.25..2.0`; the 3D scene renders into a scaled RT then upscales (`1.0` = native).
▸ `static void SetDlssMode(int mode)` · `static int DlssMode { get; }` — DLSS Super-Resolution: `0`=Off, `1`=Quality, `2`=Balanced, `3`=Performance, `4`=Ultra Performance. Falls back to bilinear upscale where DLSS isn't supported.
▸ `static void SetFrameGenMode(int mode)` · `static int FrameGenMode { get; }` — DLSS Frame Generation: `0`=Off, `1`=x2, `2`=x3, `3`=x4 (AI-inserted frames; enables Reflex; needs DLSS support).
▸ `static int FrameGenPresentedFps { get; }` — smoothed presented-FPS (real + generated), `0` when FG off.
▸ `static int CurrentFps { get; }` — current **real** (rendered) frames per second.

**Audio**
▸ `static float MasterVolume { get; }` · `static void SetMasterVolume(float v)` — `0..1`.

**GPU info**
▸ `static string GpuName { get; }` — e.g. `"NVIDIA GeForce RTX 5070"`.
▸ `static bool DlssSupported { get; }` — `true` only on an NVIDIA RTX GPU; gate DLSS options on this.

---

## `UI` (immediate mode)

Immediate-mode 2D UI drawn by the engine **over** the 3D (same swapchain). Call these from a behaviour's `Update` each frame; coordinates are **viewport pixels, top-left origin**. This is the generic engine UI — a game builds its own lobby/HUD with it.

**State**
▸ `static float Width { get; }` / `Height { get; }` — viewport size in pixels.
▸ `static float MouseX { get; }` / `MouseY { get; }` — mouse position (top-left origin).
▸ `static bool MouseDown { get; }` — mouse button held.

**Drawing**
▸ `static void Rect(float x, float y, float w, float h, Color c, float radius)` — filled rectangle (`radius > 0` = rounded).
▸ `static void Rect(float x, float y, float w, float h, Color c)` — square-cornered.
▸ `static void Text(string text, float x, float y, float w, float h, float size, Color c, int align, int weight)` — text in a box. `align`: `0` left, `1` center, `2` right. `weight`: `400`/`600`/`700`.
▸ `static void Text(string text, float x, float y, float w, float h, float size, Color c)` — left-aligned, weight 600.
▸ `static void Line(float x1, float y1, float x2, float y2, Color c, float thick)`
▸ `static void Image(string path, float x, float y, float w, float h, Color tint)` — textured quad (PNG/JPG); `tint` multiplies, `tint.A` = opacity.
▸ `static void Image(string path, float x, float y, float w, float h)` — untinted.

**Interaction**
▸ `static bool Hover(float x, float y, float w, float h)` — cursor inside the box.
▸ `static bool Button(float x, float y, float w, float h, string label, Color bg, Color fg, float size, float radius)` — clickable button; returns `true` on click (lightens on hover).

---

## `Gui` / `VuiHandle` (retained mode)

Retained-mode 2D UI: load `.vui` screens (authored in the UI Editor), stack them, and drive them by **stable id**. Sits beside the immediate-mode `UI` facade (both draw into the same frame). Gameplay logic stays in scripts; the canvas is just a renderer/router. See also the [UI routing model](Scripting-Getting-Started#ui-routing-one-class-per-screen).

### `Gui`

▸ `static VuiHandle Load(string name)` — load a `.vui` screen and get a handle.
▸ `static VuiHandle Push(string name)` — load and push a screen onto the stack.
▸ `static void Pop()` — pop the top screen.
▸ `static bool HasScreens { get; }` — any screens active.

### `VuiHandle`

A handle to a loaded screen. Drive named slots and read events by id.

▸ `bool IsValid { get; }`
▸ `void Show()` / `void Hide()`
▸ `void SetValue(string id, float v)` — set a widget's numeric value.
▸ `void SetText(string id, string t)` — set a text/label slot.
▸ `void SetVisible(string id, bool v)` — show/hide an element.
▸ `void SetColor(string id, Color c)`
▸ `void SetImage(string id, string asset)` — set an image element's asset.
▸ `void SetList(string id, IReadOnlyList<IReadOnlyDictionary<string,string>> rows)` — bind rows to a List widget.
▸ `bool WasClicked(string id)` — was a button clicked this frame?
▸ `float GetSlider(string id)` — a slider's current value.
▸ `bool GetToggle(string id)` — a toggle's state.
▸ `string GetText(string id)` — a text field's content.
▸ `int GetStep(string id)` — a stepper's index.
▸ `int GetCapturedKey(string id)` — a key-capture widget's captured virtual-key.

---

## `AudioSource`

Script-side handle to an entity's [AudioSource](Entities-and-Components#audiosource) component — get it via `VortexBehaviour.GetAudioSource()`. `Play/Stop/Pause/Resume` control the component's voice; `Volume`/`Pitch` write through to the component, so inspector and script always agree.

**Playback**
▸ `void Play()` — (re)start the clip from the beginning (regardless of PlayOnAwake).
▸ `void Stop()` / `void Pause()` / `void Resume()`
▸ `bool IsPlaying { get; }`

**Fades** (sample-accurate, no zipper noise)
▸ `void FadeIn(float seconds)` — (re)start silent and glide to full volume.
▸ `void FadeOut(float seconds)` — glide to silence, then stop and free the voice.
▸ `void FadeTo(float target, float seconds)` — glide the fade envelope (`0..1`, on top of `Volume`) to a live target; retargets smoothly mid-fade.

**Live properties**
▸ `float Volume { get; set; }` — `0..1`, audible immediately.
▸ `float Pitch { get; set; }` — audible immediately.
▸ `bool Loop { get; set; }`
▸ `string Clip { get; set; }` — project-relative clip path; takes effect on the next `Play()`.

---

## `IScriptHost` (advanced)

`IScriptHost` is the interface the engine (`ScriptRuntime`) implements to let behaviours touch the live game — it is what backs every facade above (`GetPosition`, `MoveCharacter`, `PlayAnimation`, `UIRect`, …). **You normally never use it directly**; call the friendly facades instead. It is documented here because it is public API and defines the exact host contract:

```csharp
public interface IScriptHost
{
    Vector3 GetPosition(long entityId);
    void    SetPosition(long entityId, Vector3 position);
    Vector3 GetRotation(long entityId);
    void    SetRotation(long entityId, Vector3 eulerDegrees);
    bool    GetKey(string key);

    Vector3 MoveCharacter(Vector3 feet, float radius, float height, Vector3 move, out bool grounded, long selfId);
    string  GroundTag(Vector3 origin, float maxDist);
    string  GroundMaterial(Vector3 origin, float maxDist);
    string  GroundStepSound(Vector3 origin, float maxDist);

    void    LoadScene(string name);
    bool    GetCursorLocked();
    void    SetCursorLocked(bool locked);
    void    QuitGame();
    void    SetCameraFov(float fovDegrees);
    void    SetEntityColor(long entityId, float r, float g, float b);

    bool    PlayAnimation(long entityId, string clip, float fade);
    void    StopAnimation(long entityId);
    void    SetAnimationSpeed(long entityId, float speed);
    bool    IsAnimationPlaying(long entityId, string clip);
    float   GetAnimationTime(long entityId);

    // Immediate-mode UI (viewport pixels, top-left origin)
    void    UIRect(float x, float y, float w, float h, float r, float g, float b, float a, float radius);
    void    UIText(float x, float y, float w, float h, string text, float size, float r, float g, float b, float a, int align, int weight);
    void    UILine(float x1, float y1, float x2, float y2, float r, float g, float b, float a, float thick);
    void    UIImage(float x, float y, float w, float h, string path, float r, float g, float b, float a);
    float   UIWidth();  float UIHeight();
    float   UIMouseX(); float UIMouseY();
    bool    UIMouseDown(); bool UIMousePressed();
}
```

---

## See also

- [[Scripting-Getting-Started]] — project layout, lifecycle, compile/hot-reload, VS integration.
- [[Entities-and-Components]] — the objects (`GameEntity` + components) your scripts drive.
- [[Native-DLL-API]] · [[Managed-Interop-Bindings]] — the DLL layer these facades ultimately call.
