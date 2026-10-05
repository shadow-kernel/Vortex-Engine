# Horror Essentials

How to make a scene dark, frightening and interactive with what Vortex ships today. Each section covers one tool:
what it does, where to set it in the editor, and a script you can paste.

[Shadows](#shadows) · [Fog and atmosphere](#fog-and-atmosphere) · [Post-FX](#post-fx) ·
[Flickering lights](#flickering-lights) · [Triggers](#triggers) · [Raycasts](#raycasts) ·
[Spawning and removing](#spawning-and-removing) · [Coroutines and timers](#coroutines-and-timers) ·
[Save and load](#save-and-load) · [Changing scenes](#changing-scenes) · [Putting it together](#putting-it-together)

Every member used here is listed in the [Scripting API Reference](Scripting-API-Reference); the components are
described in [Entities & Components](Entities-and-Components). The **Horror Starter** template
([Project Templates](Project-Templates)) uses most of these features — its scripts are named below as working
examples, by their path inside a Horror Starter project.

**Before you paste a sample**

- Save each class as `Assets/Scripts/<ClassName>.cs` and attach it with a **Script** component. One Script component
  runs per entity, so put a second behaviour on a child entity.
- Public fields appear on the Script component in the Inspector; the values in the code are only the defaults.
- The samples recognise the player by its **Tag** `Player`. Set it on the entity whose script moves the player with
  `Physics.MoveCharacter` — the Horror Starter's `Player` entity ships as `Untagged`.
- `Position`, `Rotation` and `Forward` are relative to the entity's parent. For a child entity (a flashlight or an
  interaction ray under the player) the samples read the world pose with `TryGetWorldPose`.
- Stopping play puts back positions, rotations, scales and mesh colours — but not Light settings a script changed.
  The samples that change lights put them back in `OnDestroy()`, which runs when play stops, on a scene switch and
  on `Scene.Destroy`.
- The samples are C# 5, the language the Windows editor's script compiler accepts, so they compile in both editors.

---

## Shadows

Lights cast real-time shadows from shadow maps: each frame the renderer draws the scene from the light into a depth
texture, and every lit pixel checks whether the light can see it. A flashlight that throws moving shadows is the
heart of a horror scene.

### What casts

A light casts shadows while its **Shadow Type** is not `None`. New Light components start at `Soft`, so every new
light casts until you turn it off. Per frame the renderer shadows:

| Light | Shadowed per frame | Shadow map | Edges |
|---|---|---|---|
| Spot | the first 4 that cast | one 2048 × 2048 tile each, covering the cone out to **Range** | hard (one filtered sample per pixel) |
| Point | the first 2 that cast | six 1024 × 1024 cube faces each, out to **Range** | soft (3 × 3 samples) |
| Directional | the scene's directional light | 3 cascades of 2048 × 2048, out to 80 m from the camera | soft (3 × 3 samples) |

"First" means Hierarchy order: top to bottom, children right after their parent. Disabled lights and lights with
**Strength** 0 don't take a slot; a fifth shadow-casting spot renders without shadows. Keep the player — and the
flashlight under it — high in the Hierarchy so the flashlight always gets a slot. (Shadows or not, at most 16 point
and 8 spot lights render per frame.)

Everything drawn with the engine's standard material receives shadows; a custom material shader receives them only
if it samples the shadow maps itself. Opaque meshes cast them, except:

- animated (skinned) meshes — monsters and characters cast no shadow yet;
- meshes on **Render Layer** 1, the first-person viewmodel;
- transparent (alpha-blended) materials.

The Mesh Renderer's **Cast Shadows** and **Receive Shadows** switches are saved with the scene but not read by the
renderer yet.

### Where to set it

The Light component's **Shadows** section in the Inspector:

| Field | Property | Default | Range | From a script (`Light` handle) |
|---|---|---|---|---|
| Shadow Type | `ShadowType` | `Soft` | `None`, `Hard`, `Soft` — `Soft` renders like `Hard` for now | `CastShadows` (`true` sets `Hard`, `false` sets `None`) |
| Strength | `ShadowStrength` | `1` | 0–1 | `ShadowStrength` |
| Bias | `ShadowBias` | `0.05` | 0–0.2 | — |
| Normal Bias | `ShadowNormalBias` | `0.4` | 0–3 (Avalonia editor only) | — |
| Resolution | `ShadowResolution` | `2048` | 512, 1024, 2048, 4096 | — |

- **Strength** is how much of this light a shadow takes away: at `1` the shadowed area gets none of it. Ambient light
  and other lights still reach it, so keep the ambient low (see [Fog and atmosphere](#fog-and-atmosphere)) for black
  shadows.
- **Bias** fights *shadow acne* (stripes across lit surfaces): raise it until they disappear. Too much lets shadows
  come loose from the objects that cast them.
- **Normal Bias** and **Resolution** are stored but not used yet; the map sizes are fixed as in the table above.

**Sun and moon.** Use a Directional Light component; its **Shadow Type** switches the cascades on. Don't drive it
with `Lighting.SetDirectional(...)`: the scene's lights are submitted again every frame in editor play (and after
every runtime change in a built game), which overwrites that call.

### Cost

Each shadow-casting spot draws the casters inside its cone once more per frame, each point light six times (one per
cube face), the directional light three times (one per cascade). Lights set to `None` cost nothing extra. Give shadows
to the flashlight and a few key lamps and set fill lights to `None` — or let a script hand the slots to the lights
near the player, as below. To measure, start the editor or the game with `VORTEX_NO_SPOT_SHADOWS=1`,
`VORTEX_NO_POINT_SHADOWS=1` or `VORTEX_NO_DIR_SHADOWS=1` in the environment and compare frame times.

### Script

```csharp
using Vortex;

// Put it on a Spot or Point light: the light casts shadows only while the player is near,
// so the four spot (two point) shadow slots go to the lights around the player.
public class NearbyShadows : VortexBehaviour
{
    public float MaxDistance = 12f;

    private Light _light;
    private bool _authored;      // the Inspector's setting, put back when play stops
    private long _player;

    public override void Start()
    {
        _light = GetLight();
        if (_light != null) _authored = _light.CastShadows;
        long[] players = Scene.FindByTag("Player");
        _player = players.Length > 0 ? players[0] : 0;
    }

    public override void Update(float dt)
    {
        if (_light == null || _player == 0) return;
        Vector3 lightPos, lightRot, playerPos, playerRot;
        if (!TryGetWorldPose(out lightPos, out lightRot) || !Scene.TryGetWorldPose(_player, out playerPos, out playerRot)) return;

        bool near = Vector3.Distance(lightPos, playerPos) < MaxDistance;
        if (near != _light.CastShadows) _light.CastShadows = near;   // write only when it changes
    }

    public override void OnDestroy()
    {
        if (_light != null) _light.CastShadows = _authored;
    }
}
```

Working example: `Assets/Scripts/Player/FlashlightController.cs` — the Horror Starter's flashlight, a Spot light
under the player: **F** toggles it, the battery drains, a weak battery flickers.

---

## Fog and atmosphere

Distance fog with an optional ground mist, computed per pixel on every surface (the sky is not fogged). At distance
*d* the fog covers `1 − 2^(−(Density × d)²)` of a surface, so a surface **2 / Density** metres away is about 94 % fog:
Density 0.08 hides things at about 25 m, 0.2 at 10 m. The fog blends each surface towards the fog colour by distance
alone, so a surface the flashlight lights stays brighter than its unlit surroundings and stands out of the murk. With
**Ground Mist** above 0 the fog exists only below **Height Y** and
thickens downwards, reaching full strength 1 / Ground Mist metres below it.

This is the only fog mode (there is no linear or exponential variant), and there is no volumetric light yet: the
beam itself is not visible in the air.

### Where to set it

The **Environment** tab, section **Fog** (in the Avalonia editor *Window ▸ Environment*). The values are saved in
the scene and apply in the editor viewport, in play mode and in the built game.

| Field | Property | Default | Range | `Atmosphere.SetFog` parameter |
|---|---|---|---|---|
| (section switch) | `FogEnabled` | off | | — |
| Density | `FogDensity` | `0.08` | 0–0.5 | `density` |
| Height Y | `FogHeightY` | `0` | −20–20 | `heightY` |
| Ground Mist | `FogHeightFalloff` | `0` | 0–2 | `heightFalloff` |
| Color | `FogR`, `FogG`, `FogB` | `0.02`, `0.025`, `0.035` | 0–1, linear | `r`, `g`, `b` |

Keep the colour dark: fog replaces what is behind it with its colour, and a light colour glows in a dark scene.

**Ambient light** decides how dark everything outside your lights is:

| Source | Ambient strength |
|---|---|
| a Skybox component in the scene | from the sky's colours and its **Ambient** field (`AmbientIntensity`, default 0.8, range 0–2); never below 0.2 |
| no Skybox | 0.35 |
| `Lighting.SetAmbient(strength)` | replaces either until the scene ends; 0 = pitch black, 1 = flat-lit |

### Script

`Atmosphere.SetFog(density, heightY = 0f, heightFalloff = 0f, r = 0.02f, g = 0.025f, b = 0.035f)` sets the fog and
`Atmosphere.ClearFog()` removes it. Script values replace the Environment settings until play stops or the scene
changes; then the scene's own settings apply again. Calling it every frame is fine:

```csharp
using Vortex;

// Ground mist that slowly breathes. Put it on any entity; while it runs it replaces the
// fog set in the Environment tab.
public class BreathingMist : VortexBehaviour
{
    public float MinDensity = 0.06f;
    public float MaxDensity = 0.14f;
    public float Period = 9f;          // seconds per breath
    public float HeightY = 1.2f;       // the mist lives below this height ...
    public float GroundMist = 0.6f;    // ... and is thickest at the floor (0 = plain distance fog)
    public float Ambient = 0.03f;      // near black: only what your lights hit is visible

    private float _time;

    public override void Start()
    {
        Lighting.SetAmbient(Ambient);
    }

    public override void Update(float dt)
    {
        _time += dt;
        float k = 0.5f + 0.5f * (float)System.Math.Sin(_time * 2.0 * System.Math.PI / Period);
        Atmosphere.SetFog(MinDensity + (MaxDensity - MinDensity) * k, HeightY, GroundMist, 0.016f, 0.02f, 0.027f);
    }
}
```

Working example: `Assets/Scripts/World/HorrorAtmosphere.cs` sets the ambient light and starts the room tone; the
Horror Starter's fog, vignette and grain are set in the Environment tab.

---

## Post-FX

Screen effects on the finished image — vignette, film grain, chromatic aberration, colour grading (exposure,
contrast, saturation, white balance) and bloom — plus screen-space ambient occlusion, which darkens creases and
contact areas inside the scene render. The screen effects are combined in one pass after tone mapping (bloom adds its
own blur passes), in this order: chromatic aberration, bloom, grain, colour grading, vignette. With all of them off
the pass is skipped and costs nothing.

**Where they show.** The screen effects apply to the game camera: play mode and the built game. The editor viewport
stays clean while you build unless **Preview post effects in viewport** is ticked at the top of the Environment tab
(editor only, never saved). Ambient occlusion is part of the scene render and shows in the viewport like fog.

### Where to set it

The Environment tab, one section per effect. Each section has its own switch — `VignetteEnabled`, `GrainEnabled`,
`AoEnabled`, `BloomEnabled`, `GradeEnabled`, `CaEnabled`, all off by default. Both editors have the same fields and
ranges:

| Section | Field | Property | Default | Range | Script parameter |
|---|---|---|---|---|---|
| Vignette | Intensity | `VignetteIntensity` | 0.8 | 0–1.5 | `intensity` |
| | Smoothness | `VignetteSmoothness` | 0.5 | 0.01–1 | `smoothness` |
| | Roundness | `VignetteRoundness` | 1 | 0–1 (1 = circle, 0 = screen shape) | `roundness` |
| | Color | `VignetteR`, `VignetteG`, `VignetteB` | black | 0–1 | `r`, `g`, `b` |
| Film Grain | Intensity | `GrainIntensity` | 0.35 | 0–1 | `intensity` |
| | Size (px) | `GrainSize` | 1.6 | 1–4 | `size` |
| Ambient Occlusion | Radius | `AoRadius` | 0.6 | 0.1–2 (world units) | `radius` |
| | Intensity | `AoIntensity` | 1 | 0–2 | `intensity` |
| Bloom | Threshold | `BloomThreshold` | 0.75 | 0–1.5 | `threshold` |
| | Soft Knee | `BloomKnee` | 0.5 | 0–1 | `knee` |
| | Intensity | `BloomIntensity` | 0.7 | 0–3 | `intensity` |
| | Scatter | `BloomScatter` | 0.65 | 0–1 | `scatter` |
| Color Grading | Exposure | `Exposure` | 0 | −3–3 (EV stops) | `exposure` |
| | Contrast | `Contrast` | 1 | 0–2 | `contrast` |
| | Saturation | `Saturation` | 1 | 0–2 (0 = grey) | `saturation` |
| | Temperature | `Temperature` | 0 | −1 (cool) – 1 (warm) | `temperature` |
| | Tint | `Tint` | 0 | −1 (green) – 1 (magenta) | `tint` |
| Chromatic Aberration | Strength | `CaStrength` | 0.35 | 0–2 | `strength` |
| | Falloff | `CaFalloff` | 1.2 | 0.5–3 | `falloff` |

- **Vignette** darkens the edges towards its colour — the claustrophobia dial.
- **Film Grain** is animated and stronger in dark areas.
- **Ambient Occlusion** darkens only ambient light; the flashlight's light is untouched. It costs an extra
  half-resolution depth pass plus the occlusion and blur passes.
- **Bloom** makes everything brighter than **Threshold** glow: dying bulbs, exit signs, eyes in the dark.
- **Chromatic Aberration** fringes colours towards the edges: 0.2–0.6 reads as unease, 2 and more as a heavy VHS
  smear.

### Script

The calls take the section switch first; the defaults are the panel's defaults, and scripts are not limited to the
slider ranges:

- `PostFx.SetVignette(enabled, intensity = 0.8f, smoothness = 0.5f, roundness = 1f, r = 0f, g = 0f, b = 0f)`
- `PostFx.SetGrain(enabled, intensity = 0.35f, size = 1.6f)`
- `PostFx.SetAmbientOcclusion(enabled, radius = 0.6f, intensity = 1f)`
- `PostFx.SetBloom(enabled, threshold = 0.75f, knee = 0.5f, intensity = 0.7f, scatter = 0.65f)`
- `PostFx.SetColorGrade(enabled, exposure = 0f, contrast = 1f, saturation = 1f, temperature = 0f, tint = 0f)`
- `PostFx.SetChromaticAberration(enabled, strength = 0.35f, falloff = 1.2f)`
- `PostFx.ClearAll()` switches every effect off.

Like fog, script values replace the Environment settings until play stops or the scene changes. A fear dial that
other scripts turn up:

```csharp
using Vortex;

// One per scene, e.g. on an empty entity named "ScreenFx". Other scripts raise Fear (0..1):
//     FearFx fx = Scene.GetBehaviour<FearFx>(Scene.Find("ScreenFx"));
//     if (fx != null) fx.Fear = 1f;
// The vignette closes in with a heartbeat, grain and colour fringing grow, colours drain.
// While it runs it owns these effects: the Calm values replace the Environment tab's.
public class FearFx : VortexBehaviour
{
    public float Fear = 0f;                  // target 0..1; decays by itself
    public float CalmDownPerSecond = 0.1f;
    public float CalmVignette = 0.55f;
    public float CalmGrain = 0.15f;

    private float _level;                    // smoothed fear
    private float _phase;                    // heartbeat phase

    public override void Update(float dt)
    {
        Fear = System.Math.Max(0f, Fear - CalmDownPerSecond * dt);
        _level += (Fear - _level) * System.Math.Min(1f, dt * 3f);

        _phase += dt * (60f + 90f * _level) / 60f;                  // the heart races
        float beat = (float)System.Math.Pow(System.Math.Abs(System.Math.Sin(_phase * System.Math.PI)), 8.0);

        PostFx.SetVignette(true, CalmVignette + 0.5f * _level + 0.15f * _level * beat, 0.45f, 1f, 0.08f * _level, 0f, 0f);
        PostFx.SetGrain(true, CalmGrain + 0.45f * _level, 1.6f);
        PostFx.SetChromaticAberration(_level > 0.05f, 0.2f + 1.0f * _level, 1.2f);
        PostFx.SetColorGrade(true, -0.4f * _level, 1f + 0.2f * _level, 1f - 0.7f * _level);
    }
}
```

Working example: `Assets/Scripts/World/JumpScareTrigger.cs` surges grain and chromatic aberration during its scare
and settles back afterwards.

---

## Flickering lights

Scripts drive a Light component through a `Light` handle: `GetLight()` for the script's own entity,
`Scene.GetLight(entity)` for any other (both return `null` when there is no Light). A change shows from the next
frame, in editor play and in the built game.

### Where to set it

The Light component in the Inspector holds the starting values; a script changes them while the game runs.

| Field | Property | Default | Range | `Light` handle |
|---|---|---|---|---|
| (component switch) | `IsEnabled` | on | | `Enabled` |
| Type | `LightType` | `Directional` | `Directional`, `Point`, `Spot` | — |
| Color | `ColorR`, `ColorG`, `ColorB` | 1, 0.956, 0.839 | 0–1 | `SetColor(r, g, b)` |
| Intensity | `Intensity` | 2.5 | slider 0–10 | `Intensity` |
| Range | `Range` | 10 | 0.1–100 (point and spot) | `Range` |
| Spot Angle | `SpotAngle` | 30 | 1–179, full cone in degrees | `SpotAngle` |
| Inner Angle | `InnerSpotAngle` | 21 | 0–178, the full-brightness core | `InnerSpotAngle` |

`LightType` also has `Area`, which is not rendered. A switched-off Light still counts as a scene light — the engine
adds its default sun only to scenes without any Light component — so switching every light off really is dark,
apart from the ambient light.

The handle also has `CastShadows` and `ShadowStrength` ([Shadows](#shadows)) and the static helper
`Light.Flicker(time, speed = 12f)`: a smooth, irregular value between 0 and 1 (two detuned sine waves) to multiply
into the intensity every frame. `Time` only has `DeltaTime`, so add up your own clock.

### Script

```csharp
using Vortex;

// Put it on a Point or Spot light: a dying bulb. The Inspector's Intensity is its full brightness.
public class DyingBulb : VortexBehaviour
{
    public float Speed = 14f;                  // flicker speed
    public float Depth = 0.7f;                 // 0 = steady, 1 = flickers down to black
    public float BlackoutsPerSecond = 0.2f;

    private Light _light;
    private float _full;
    private float _time;
    private float _blackout;                   // seconds left of the current blackout
    private System.Random _random;

    public override void Start()
    {
        _light = GetLight();
        if (_light != null) _full = _light.Intensity;
        _random = new System.Random((int)EntityId);            // every bulb flickers differently
        _time = (float)_random.NextDouble() * 100f;
    }

    public override void Update(float dt)
    {
        if (_light == null) return;
        _time += dt;

        if (_blackout > 0f)
        {
            _blackout -= dt;
            _light.Intensity = 0f;
            return;
        }
        if (_random.NextDouble() < BlackoutsPerSecond * dt)
            _blackout = 0.05f + 0.15f * (float)_random.NextDouble();

        float f = Light.Flicker(_time, Speed);                   // 0..1
        _light.Intensity = _full * (1f - Depth + Depth * f);
    }

    public override void OnDestroy()
    {
        if (_light != null) _light.Intensity = _full;            // play mode does not restore Light values
    }
}
```

Working example: `Assets/Scripts/World/LightFlicker.cs` — a bulb mode (nervous flicker, rare blackouts) and a
generator mode (a steady pulse).

---

## Triggers

A collider with **Is Trigger** ticked never blocks anything; it reports when something enters it, stays in it and
leaves it. Jump scares, checkpoints, music zones and level exits all start here.

### Where to set it

Add a **Box Collider** (or a Sphere, Capsule or Mesh Collider) to an entity — usually an empty one, no mesh needed —
and tick **Is Trigger**. Put the script on the **same entity**: the callbacks go to the script of the entity that
owns the collider.

| Field | Property | Default | Notes |
|---|---|---|---|
| Is Trigger | `IsTrigger` | off | on = overlap only, never blocks |
| Center | `Center` | 0, 0, 0 | offset from the entity |
| Size (Box Collider) | `Size` | 1, 1, 1 | |
| Radius (Sphere Collider) | `Radius` | 0.5 | |

Override these on your `VortexBehaviour`:

| Callback | When |
|---|---|
| `OnTriggerEnter(TriggerHit other)` | the first tick something overlaps the trigger |
| `OnTriggerStay(TriggerHit other)` | every tick while it stays inside |
| `OnTriggerExit(TriggerHit other)` | the tick it leaves |
| `OnCollisionEnter(TriggerHit other)` | something first touches a solid (non-trigger) collider |

`other` describes the other side: `EntityId` (its handle, for `Scene.*` and `SendMessage`), `Name` and `Tag`.

**What trips a trigger.** Overlaps are tested once per tick, after everything has moved, against **characters**:
capsules moved with `Physics.MoveCharacter(feet, radius, height, move, EntityId)` — the overload with the id. The
four-parameter overload moves an anonymous capsule that trips nothing. With rigid-body physics running
(`Physics.Simulated`), bodies with a Collider and a Rigidbody trip triggers too — which is why the samples check the
tag. An entity moved by setting its `Position` trips nothing. Both sides are told: the character's script receives
the same callbacks, with `other` describing the trigger entity (its `EntityId` is `0` when that entity has no
script).

The collision world is built when play starts: after a script moves a trigger, call `Physics.RefreshCollider(entity)`.

### Script

```csharp
using Vortex;

// A one-shot scare. Put it on an entity with a Box Collider whose Is Trigger is ticked (no mesh
// needed). The first time the player walks in, a scream comes from the far end of the hall and
// the hall lamp goes out.
public class ScareTrigger : VortexBehaviour
{
    public string ScreamSound = "Assets/Audio/scream.wav";   // any clip of your project
    public string SoundFrom = "HallEnd";                     // entity the scream comes from
    public string LampName = "HallLamp";                     // entity with the Light that dies

    private bool _fired;
    private Light _lamp;
    private bool _lampWasOn;

    public override void Start()
    {
        _lamp = Scene.GetLight(Scene.Find(LampName));          // null if there is no such light
        if (_lamp != null) _lampWasOn = _lamp.Enabled;
    }

    public override void OnTriggerEnter(TriggerHit other)
    {
        if (_fired || other.Tag != "Player") return;           // only the player, only once
        _fired = true;

        Vector3 soundPos, soundRot;
        if (Scene.TryGetWorldPose(Scene.Find(SoundFrom), out soundPos, out soundRot))
            Audio.PlayOneShot(ScreamSound, soundPos);             // 3D: it comes from over there

        if (_lamp != null) _lamp.Enabled = false;
    }

    public override void OnDestroy()
    {
        if (_lamp != null) _lamp.Enabled = _lampWasOn;          // the lamp comes back when play stops
    }
}
```

`OnTriggerExit` closes a zone again — a safe room with its own music:

```csharp
public override void OnTriggerEnter(TriggerHit other)
{
    if (other.Tag == "Player") Audio.Music.CrossFade("Assets/Audio/safe_room.ogg", 3f);
}

public override void OnTriggerExit(TriggerHit other)
{
    if (other.Tag == "Player") Audio.Music.CrossFade("Assets/Audio/dread.ogg", 3f);
}
```

Working example: `Assets/Scripts/World/JumpScareTrigger.cs` fires once, ramps the post effects in a coroutine and
spawns the monster behind the player.

---

## Raycasts

`Physics.Raycast` sends a ray against the solid colliders and reports the closest hit: what the player looks at,
what the flashlight points at, whether the monster can see the player, where a shot lands.

| Call | Returns |
|---|---|
| `Physics.Raycast(origin, direction, maxDist, out RaycastHit hit, layerMask = ~0)` | `true` and the closest hit within `maxDist` |
| `Physics.Raycast(origin, direction, maxDist, layerMask = ~0)` | only whether something is there |

| `RaycastHit` field | Meaning |
|---|---|
| `Point` | world position of the hit |
| `Normal` | surface normal, unit length, facing the ray's origin |
| `Distance` | from `origin` to `Point` |
| `EntityId` | handle of the entity that owns the collider hit (for `Scene.*`, `SendMessage`, `Scene.GetBehaviour<T>`) |
| `Name`, `Tag` | that entity's name and tag |

### Where to set it

What a ray hits is decided by the colliders in the scene:

- Entities with a solid Collider: the level, and props with a Rigidbody at their current position. Trigger colliders
  are ignored.
- Characters are not colliders: rays pass through the player and through anything moved with `MoveCharacter`. To make
  a monster hittable give it a Collider; while it moves, a **Kinematic** Rigidbody keeps the collider with it (with
  rigid-body physics running), otherwise call `Physics.RefreshCollider` after each move.
- `layerMask` filters by the hit entity's **Layer** (Inspector): bit *n* stands for layer *n*, so `~(1 << 3)`
  ignores layer 3.
- `direction` does not need to be normalised. `Debug.DrawLine` and `Debug.DrawRay` show a ray in the game view while
  you tune it.

### Script

```csharp
using Vortex;

// Put it on the flashlight (a Spot light under the player). While the light is on, whatever it
// points at within its Range gets an "illuminated" message with the distance: a shade that
// freezes in the beam, a swarm that scatters.
public class FlashlightBeam : VortexBehaviour
{
    public bool ShowRay = false;   // draw the ray while tuning

    private Light _light;

    public override void Start()
    {
        _light = GetLight();
    }

    public override void Update(float dt)
    {
        if (_light == null || !_light.Enabled) return;

        Vector3 origin, euler;
        if (!TryGetWorldPose(out origin, out euler)) return;     // world pose, through the parents
        Vector3 direction = Quaternion.FromEuler(euler).Forward;  // where the beam points (+Z)

        RaycastHit hit;
        if (!Physics.Raycast(origin, direction, _light.Range, out hit)) return;
        if (ShowRay) Debug.DrawLine(origin, hit.Point);
        if (hit.Tag == "Shade") SendMessage(hit.EntityId, "illuminated", hit.Distance);
    }
}
```

The shade's script receives the message in `OnMessage`:

```csharp
public override void OnMessage(string message, object arg)
{
    if (message == "illuminated" && arg is float)
    {
        float distance = (float)arg;
        Debug.Log(Scene.NameOf(EntityId) + " is lit from " + distance + " m");
    }
}
```

Line of sight is a ray that hits nothing on the way:

```csharp
// Can the monster see the player? Nothing solid between them means yes.
// (Both are root entities, so PositionOf is their world position.)
long monster = Scene.Find("Monster");
long player = Scene.Find("Player");
Vector3 eyes = Scene.PositionOf(monster) + new Vector3(0f, 1.6f, 0f);
Vector3 toPlayer = Scene.PositionOf(player) - eyes;
bool canSee = !Physics.Raycast(eyes, toPlayer, toPlayer.Length);
```

Working examples: `Assets/Scripts/Weapons/Weapon.cs` fires its hitscan shots as rays from the player's eye;
`Assets/Scripts/World/ShellCasing.cs` finds the floor under a spent shell with a ray straight down.

---

## Spawning and removing

`Scene.Instantiate` puts a prefab into the running game — the figure that is suddenly there — and `Scene.Destroy`
takes an entity out again.

### Where to set it

A prefab is a `.ventity` file. Build the entity in a scene (children, components, scripts and all), then right-click
it in the Hierarchy ▸ **Save as Prefab…**; it is saved under `Assets/Prefabs/`. Pass the project-relative path to
`Instantiate`, e.g. `"Assets/Prefabs/Monster.ventity"`.

| Call | What happens |
|---|---|
| `Scene.Instantiate(prefabPath, position, yawDegrees = 0f)` | Adds the prefab at the scene root at `position`, turned `yawDegrees` around the vertical axis. Its colliders and rigid bodies join the world, it renders this frame, and its scripts' `Start()` runs immediately. Returns the new entity's handle — `0`, with an error in the Console, when the prefab is not found. |
| `Scene.Destroy(entity)` | Removes the entity and its children: their scripts get `OnDestroy()`, their coroutines and timers stop, mesh and colliders go, and the handle stops working. Works on entities placed in the scene too. |
| `Scene.SetActive(entity, active)` | Hides or shows an entity and its children. Rendering, colliders and audio follow; scripts keep their state. Cheaper than spawning for things that come and go. |

Play mode never changes the scene file: when play stops, and on a scene switch, spawned entities are removed and
destroyed or hidden ones come back.

### Script

```csharp
using Vortex;

// An apparition: walk into this trigger and a figure stands at the far end of the room — for
// a few seconds. Put it on an entity with a Box Collider whose Is Trigger is ticked, and place an
// empty entity named like SpawnPoint where the figure should appear.
public class Apparition : VortexBehaviour
{
    public string Prefab = "Assets/Prefabs/Monster.ventity";
    public string SpawnPoint = "ApparitionSpot";
    public float LifeSeconds = 3f;

    private long _figure;
    private bool _done;

    public override void OnTriggerEnter(TriggerHit other)
    {
        if (_done || other.Tag != "Player") return;
        _done = true;

        Vector3 pos, euler;
        long spot = Scene.Find(SpawnPoint);
        if (spot == 0 || !Scene.TryGetWorldPose(spot, out pos, out euler))
        {
            Debug.LogWarning("Apparition: no entity named " + SpawnPoint);
            return;
        }
        _figure = Scene.Instantiate(Prefab, pos, euler.Y);       // 0 if the prefab was not found
        if (_figure != 0) Invoke(Vanish, LifeSeconds);
    }

    private void Vanish()
    {
        Scene.Destroy(_figure);       // its scripts get OnDestroy; mesh and colliders are gone
        _figure = 0;
    }
}
```

For something that appears and disappears again and again, place it in the scene, hide it once from a script's
`Start()` — an entity saved as inactive is active again when a built game starts or the scene is switched to — and
toggle it:

```csharp
long ghost = Scene.Find("Ghost");
Scene.SetActive(ghost, false);   // gone: not rendered, no collision, its audio stops
Scene.SetActive(ghost, true);    // back, its scripts' state unchanged
```

Working examples: `Assets/Scripts/World/JumpScareTrigger.cs` spawns `Assets/Prefabs/Monster.ventity` behind the
player; the monster's own `Assets/Scripts/World/MonsterStalker.cs` removes itself with `Invoke` and `Scene.Destroy`.

---

## Coroutines and timers

A coroutine is a method that can pause — wait two seconds, slam the door, kill the lights — written from top to
bottom instead of as a state machine in `Update`. Timers run a method later, once or repeatedly.

### Where to set it

There is nothing to set in the editor. A coroutine is a method that returns `IEnumerator` (add
`using System.Collections;`) and is started with `StartCoroutine(...)`:

| Inside the coroutine | It continues |
|---|---|
| `yield return null;` | next frame |
| `yield return new WaitForSeconds(s);` | on the first frame after `s` seconds have passed |
| `yield return` anything else, including a `Coroutine` | next frame — it does **not** wait for that coroutine |
| `yield break;` or the end of the method | never: the coroutine is finished |

There is no `WaitUntil`; loop instead: `while (!_doorOpen) yield return null;`.

| Call (on `VortexBehaviour`) | Effect |
|---|---|
| `StartCoroutine(routine)` | runs `routine` up to its first `yield` at once and returns a `Coroutine` handle; its `IsRunning` turns `false` when it finishes or is stopped |
| `StopCoroutine(handle)`, `StopAllCoroutines()` | stops one, or every, coroutine of this behaviour |
| `Invoke(action, delay)` | runs `action` once after `delay` seconds |
| `InvokeRepeating(action, delay, interval)` | after `delay`, then every `interval` seconds |
| `CancelInvokes()` | cancels this behaviour's pending `Invoke` and `InvokeRepeating` calls |

Coroutines and timers advance once per frame, after every script's `Update` and `LateUpdate`; `Time.DeltaTime`
inside them is that frame's time step. They stop by themselves when their entity is destroyed, when the scene
changes and when play stops. An exception in a coroutine is logged to the Console and ends that coroutine. `action`
is any `System.Action`: a method name (`Invoke(Vanish, 3f)`) or a lambda.

### Script

```csharp
using System.Collections;
using Vortex;

// A timed scare. Send it "start" from anywhere — SendMessage(Scene.Find("LightsOut"), "start") —
// and the lamps tagged CorridorLight die one after another, something breathes in the dark,
// and the lamps stutter back on.
public class LightsOut : VortexBehaviour
{
    public string LampTag = "CorridorLight";
    public string PowerDownSound = "Assets/Audio/power_down.wav";
    public string BreathSound = "Assets/Audio/breath.wav";

    private long[] _lamps;
    private Coroutine _running;

    public override void Start()
    {
        _lamps = Scene.FindByTag(LampTag);
    }

    public override void OnMessage(string message, object arg)
    {
        if (message != "start") return;
        if (_running != null && _running.IsRunning) return;      // already playing
        _running = StartCoroutine(Sequence());
    }

    private IEnumerator Sequence()
    {
        Audio.PlayOneShot2D(PowerDownSound);
        for (int i = 0; i < _lamps.Length; i++)                   // one by one
        {
            SetLamp(_lamps[i], false);
            yield return new WaitForSeconds(0.35f);
        }

        yield return new WaitForSeconds(2f);                      // darkness
        Audio.PlayOneShot2D(BreathSound, 0.8f);
        yield return new WaitForSeconds(3f);

        for (int blink = 0; blink < 6; blink++)                   // they stutter back
        {
            SetAll(blink % 2 == 1);
            yield return new WaitForSeconds(0.06f + 0.04f * blink);
        }
        SetAll(true);
    }

    public override void OnDestroy()
    {
        if (_lamps != null) SetAll(true);                         // lamps back on when play stops
    }

    private void SetAll(bool on)
    {
        for (int i = 0; i < _lamps.Length; i++) SetLamp(_lamps[i], on);
    }

    private static void SetLamp(long lamp, bool on)
    {
        Light light = Scene.GetLight(lamp);
        if (light != null) light.Enabled = on;
    }
}
```

A repeating timer — a creak somewhere in the house every twelve seconds:

```csharp
public override void Start()
{
    InvokeRepeating(Creak, 5f, 12f);   // first after 5 s, then every 12 s
}

private void Creak()
{
    Audio.PlayOneShot2D("Assets/Audio/creak.wav", 0.6f);
}
```

Waiting for another coroutine means polling its handle:

```csharp
private IEnumerator Outer()
{
    Coroutine inner = StartCoroutine(Inner());
    while (inner.IsRunning) yield return null;   // "yield return inner" would only wait one frame
    Debug.Log("inner finished");
}

private IEnumerator Inner()
{
    yield return new WaitForSeconds(1f);
}
```

Working examples: `Assets/Scripts/World/SlidingDoor.cs` slides a door frame by frame with `yield return null`;
`Assets/Scripts/World/JumpScareTrigger.cs` times its scare with `WaitForSeconds`.

---

## Save and load

`Save` stores values under string keys — the checkpoint, the doors already opened, the scares already survived — and
writes them to one save file per **slot**. It works the same in editor play and in the built game.

### Where it is stored

There is nothing to set in the editor. Each slot is a file in the user's application-data folder, per project: on
Windows `%APPDATA%\VortexGames\<project name>\save_slot<N>.dat`. Editor play reads and writes the same files as your
built game, so a test session leaves a real save behind — clear it with `Save.DeleteSlot(0)` to start fresh.

| Member | Notes |
|---|---|
| `SetInt(key, value)`, `SetFloat(…)`, `SetString(…)`, `SetBool(…)` | changes the value in memory |
| `GetInt(key, def = 0)`, `GetFloat(key, def = 0f)`, `GetString(key, def = "")`, `GetBool(key, def = false)` | `def` when the key is missing or was written with another type — read a key with the type you wrote it |
| `HasKey(key)`, `DeleteKey(key)`, `DeleteAll()` | `DeleteAll()` empties the current slot |
| `Flush()` | writes the current slot to disk now |
| `UseSlot(n)`, `CurrentSlot` | switches the slot (the old one is written first); slot 0 is the default |
| `SlotExists(n)`, `DeleteSlot(n)` | whether slot `n` has a file yet (it gets one when first written); deletes it |

Values are written by `Flush()`, on every scene switch and when play mode ends. Call `Flush()` yourself right after
a checkpoint — don't count on quitting the game to write it. `Save` keeps only what your scripts put in it; it does
not snapshot the scene.

### Script

```csharp
using Vortex;

// A checkpoint: an entity with a Box Collider whose Is Trigger is ticked, standing where the
// player should come back. Walking through it records the checkpoint and writes the save file.
public class Checkpoint : VortexBehaviour
{
    public string SceneName = "Demo";   // the scene this checkpoint is in (scripts can't ask)

    public override void OnTriggerEnter(TriggerHit other)
    {
        if (other.Tag != "Player") return;
        string spawn = Scene.NameOf(EntityId);                   // this entity is the spawn marker
        if (Save.GetString("checkpoint.spawn") == spawn && Save.GetString("checkpoint.scene") == SceneName) return;

        Save.SetString("checkpoint.scene", SceneName);
        Save.SetString("checkpoint.spawn", spawn);
        Save.Flush();                                            // on disk now, even if the game crashes
        Debug.Log("Checkpoint: " + spawn);
    }
}
```

Remember what already happened, so a reload doesn't replay it — for example in the `ScareTrigger` above:

```csharp
public string Id = "hallway_scream";          // unique per scare
private bool _fired;

public override void Start()
{
    _fired = Save.GetBool("scare." + Id);     // survived before: stay quiet
}

public override void OnTriggerEnter(TriggerHit other)
{
    if (_fired || other.Tag != "Player") return;
    _fired = true;
    Save.SetBool("scare." + Id, true);        // on disk with the next Flush or scene switch
    // ... the scare itself
}
```

Back to the last checkpoint when the player dies. The player controller places itself on the `spawn` marker in its
`Start()` — see [Changing scenes](#changing-scenes):

```csharp
private void ReloadCheckpoint()
{
    Save.SetString("spawn", Save.GetString("checkpoint.spawn"));   // where to stand after the load
    Scene.Load(Save.GetString("checkpoint.scene", "Demo"));        // the current scene works too
}
```

The Horror Starter has no save system of its own — these samples are the starting point.

---

## Changing scenes

`Scene.Load(name)` switches to another scene of the project: yard, cellar, tunnels. `name` is the scene's name in the
project (case does not matter). The call only records the request — the switch happens at the end of the current
tick, so it is safe from `Update`, a trigger or a coroutine. A name that matches no scene does nothing.

### Where to set it

The scenes are the ones listed in the project. For the script there are two members:

| Member | Effect |
|---|---|
| `Scene.Load(name)` | requests the switch; it happens at the end of this tick |
| `Scene.Loading` | `event Action<string>`: its handlers run inside `Scene.Load`, with the new scene's name, before anything changes. Handlers are removed on every switch, so subscribe in `Start()` |

A switch, step by step:

1. `Scene.Loading` handlers run, inside the `Scene.Load` call.
2. At the end of the tick the old scene stops: every script gets `OnDestroy()`; coroutines, timers, `Events`
   subscriptions and `Scene.Loading` handlers are dropped; what `Instantiate`, `Destroy` and `SetActive` did is
   undone; `Save` is written to disk; all audio stops (sources, one-shots, music); `Gui` screens close.
3. The new scene loads, its Environment settings apply (fog, post effects — scripted fog, post effects and ambient
   from the old scene are gone), its **Play On Awake** audio starts, and its scripts run `Start()`.

There is no asynchronous loading or progress callback: the game waits while the next scene loads. Fade out first, as
below, so the wait happens on a black screen.

**Coming back to a scene.** A scene that was already loaded in this run — including the current one, loaded again for
a checkpoint — is not read from disk again. Step 2 undoes spawns, destroys and `SetActive`, and the scripts start
fresh, but positions, rotations and component values that scripts changed stay as they were. Let every scripted
object set its own state in `Start()` with absolute values from `Save`, as the door in
[Putting it together](#putting-it-together) does.

**Keeping data across scenes.** Use `Save`: it stays in memory and is written to disk during the switch. Don't use
static fields — in editor play the scripts are recompiled for every scene, so statics start over (a packed Release
build loads its compiled scripts once, so there they would survive, and the two would behave differently).

### Script

```csharp
using System.Collections;
using Vortex;

// A level exit: an entity with a Box Collider whose Is Trigger is ticked. The player walks in,
// the screen fades to black, and the next scene starts with the player at ArrivalMarker.
public class LevelExit : VortexBehaviour
{
    public string NextScene = "Demo";
    public string ArrivalMarker = "FromYard";     // an empty entity in the next scene
    public float FadeSeconds = 1.2f;

    private bool _leaving;

    public override void OnTriggerEnter(TriggerHit other)
    {
        if (_leaving || other.Tag != "Player") return;
        _leaving = true;
        StartCoroutine(Leave());
    }

    private IEnumerator Leave()
    {
        float t = 0f;
        while (t < FadeSeconds)
        {
            t += Time.DeltaTime;
            float k = t / FadeSeconds;
            if (k > 1f) k = 1f;
            PostFx.SetColorGrade(true, -10f * k);      // exposure in EV stops: -10 is black
            yield return null;
        }
        Save.SetString("spawn", ArrivalMarker);        // read by the player controller's Start()
        Scene.Load(NextScene);                         // switches at the end of this tick
    }
}
```

The next scene starts with its own Environment settings, at full brightness. On arrival, the player controller —
the script that calls `Physics.MoveCharacter` — stands on the marker before it reads `Position`:

```csharp
public override void Start()
{
    string spawn = Save.GetString("spawn");
    long marker = spawn != "" ? Scene.Find(spawn) : 0;
    Vector3 pos, euler;
    if (marker != 0 && Scene.TryGetWorldPose(marker, out pos, out euler))
    {
        Position = pos;                              // the player is a root entity: local = world
        Rotation = new Vector3(0f, euler.Y, 0f);     // face the way the marker faces
    }
    Save.DeleteKey("spawn");                         // a one-time hand-over
}
```

`Scene.Loading` lets any script store its state on the way out, whoever called `Scene.Load` — here the player's
health (start a new game with `Save.DeleteAll()` so old values don't carry over):

```csharp
private float _health = 100f;

public override void Start()
{
    _health = Save.GetFloat("player.health", 100f);   // carried over, or full health
    Scene.Loading += OnLeavingScene;
}

private void OnLeavingScene(string nextScene)
{
    Save.SetFloat("player.health", _health);
}
```

---

## Putting it together

A cellar door: look at it and press **E**, and it swings open with a creak. Opening it is a checkpoint — after a death
the player comes back behind the door, and the door is still open, in this run and after a restart. Two scripts and
this hierarchy:

```text
CellarDoorHinge       Script: CheckpointDoor — an empty entity on the hinge line
└─ Cellar Door        Mesh Renderer + Box Collider, Tag "Interactable"; offset so one edge is on the hinge
AfterCellarDoor       an empty entity past the door, where the player comes back
Player                the player controller (Tag "Player")
└─ Interactor         Script: LookInteractor — no offset of its own, so it looks where the player looks
```

In the Horror Starter, `Player` carries the view (its script places it at eye height and turns it), so
`LookInteractor` goes on its `Interactor` child in place of `Interactor.cs`.

```csharp
using Vortex;

// 1 of 2 — on a child of the entity that carries the player's view. Shows a prompt when an
// "Interactable" is straight ahead and sends it "interact" on E (keyboard) or X (gamepad).
public class LookInteractor : VortexBehaviour
{
    public float Reach = 2.5f;

    private bool _wasPressed;

    public override void Update(float dt)
    {
        Vector3 eye, euler;
        if (!TryGetWorldPose(out eye, out euler)) return;
        Vector3 forward = Quaternion.FromEuler(euler).Forward;

        RaycastHit hit;
        bool canUse = Physics.Raycast(eye, forward, Reach, out hit) && hit.Tag == "Interactable";

        if (canUse && UI.Width > 10f)
            UI.Text("[E]  " + hit.Name, UI.Width * 0.5f - 120f, UI.Height * 0.6f, 240f, 24f, 14f,
                    Color.Rgba(230, 230, 235, 230), 1, 600);

        bool pressed = Input.GetKey("E") || Input.GetGamepadButton("X");
        if (pressed && !_wasPressed && canUse)
        {
            // The collider often sits on a child mesh; the script that reacts is on its parent.
            long target = hit.EntityId;
            if (Scene.GetBehaviour<VortexBehaviour>(target) == null) target = Scene.Parent(target);
            SendMessage(target, "interact");
        }
        _wasPressed = pressed;
    }
}
```

```csharp
using System.Collections;
using Vortex;

// 2 of 2 — on the door's hinge entity. "interact" swings the door open with a creak; opening it
// is a checkpoint, and the door stays open after a reload or a restart.
public class CheckpointDoor : VortexBehaviour
{
    public string Id = "cellar_door";               // unique per door: the save key
    public string SceneName = "Demo";               // this scene's name, for the checkpoint
    public string SpawnMarker = "AfterCellarDoor";  // where the player comes back
    public float ClosedYaw = 0f;                    // the hinge's local yaw when closed ...
    public float OpenYaw = 100f;                    // ... and when open (absolute: no drift on reloads)
    public float SwingSeconds = 1.6f;
    public string CreakSound = "Assets/Audio/door_creak.wav";

    private bool _open;
    private bool _swinging;

    public override void Start()
    {
        _open = Save.GetBool("door." + Id);
        SetYaw(_open ? OpenYaw : ClosedYaw);
        Physics.RefreshCollider(EntityId);          // the collision follows the restored pose
    }

    public override void OnMessage(string message, object arg)
    {
        if (message == "interact" && !_open && !_swinging) StartCoroutine(Swing());
    }

    private IEnumerator Swing()
    {
        _swinging = true;
        Vector3 pos, euler;
        if (TryGetWorldPose(out pos, out euler)) Audio.PlayOneShot(CreakSound, pos);

        float t = 0f;
        while (t < SwingSeconds)
        {
            t += Time.DeltaTime;
            float k = t / SwingSeconds;
            if (k > 1f) k = 1f;
            k = 1f - (1f - k) * (1f - k);               // ease out: a push, then a slow creak
            SetYaw(ClosedYaw + (OpenYaw - ClosedYaw) * k);
            yield return null;
        }
        Physics.RefreshCollider(EntityId);              // re-bake the panel's collider (children included)
        _open = true;
        _swinging = false;

        Save.SetBool("door." + Id, true);               // the door stays open
        Save.SetString("checkpoint.scene", SceneName);  // and this is the new checkpoint
        Save.SetString("checkpoint.spawn", SpawnMarker);
        Save.Flush();
    }

    private void SetYaw(float yaw)
    {
        Vector3 r = Rotation;
        Rotation = new Vector3(r.X, yaw, r.Z);
    }
}
```

What happens:

1. Every frame `LookInteractor` casts a ray along the view ([Raycasts](#raycasts)). When it hits `Cellar Door`
   (tagged `Interactable`) it shows **[E] Cellar Door**.
2. **E** sends `"interact"`. `Cellar Door` has no script, so the message goes to its parent, the hinge.
3. `CheckpointDoor` swings in a coroutine ([Coroutines and timers](#coroutines-and-timers)), plays the creak at the
   hinge, re-bakes the collision so the doorway opens up, and saves the door and the checkpoint
   ([Save and load](#save-and-load)).
4. When the player dies, `ReloadCheckpoint()` loads the scene again. The door's `Start()` reads `door.cellar_door`
   and opens it at once; the player controller's `Start()` reads `spawn` and stands at `AfterCellarDoor`
   ([Changing scenes](#changing-scenes)).

The Horror Starter's own door, `Assets/Scripts/World/SlidingDoor.cs`, answers the same `"interact"` message with a
slide instead of a swing, and saves nothing.

---

## See also

- [Scripting API Reference](Scripting-API-Reference) — every type and member used on this page
- [Scripting: Getting Started](Scripting-Getting-Started) — how scripts are attached, compiled and hot-reloaded
- [Entities & Components](Entities-and-Components) — Light, Collider, Skybox and Mesh Renderer in full
- [Audio](Audio) — the sound half of every scare: one-shots, music, ambience, reverb zones
- [Getting Started](Getting-Started) — the first hour with the Horror Starter, including a first scare
- [Project Templates](Project-Templates) — the Horror Starter and how projects pick up template fixes
- [Horror Game Readiness](Horror-Game-Readiness) — the checklist these features were built for; the design notes
  [Welle A](Design-Welle-A-Horror-Essentials) and [Spot shadows](Design-Spot-Shadows-23) predate the implementation —
  this page describes what shipped
