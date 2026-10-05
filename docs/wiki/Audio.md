# Audio

Everything about sound in Vortex: the audio components, setting up sounds in the editor without code, the `Vortex.Audio` scripting API with examples you can paste, footsteps with sound containers, the Audio Mixer, reverb zones, the optional Steam Audio layer (HRTF and occlusion) and what ships with an exported game. The member-by-member reference is in the [Scripting API Reference](Scripting-API-Reference#audio), the serialized fields in [Entities and Components](Entities-and-Components#audiosource); new sounds are made in the [Sound Studio](Sound-Studio). In German: [Audio-Anleitung (Deutsch)](Audio-Anleitung-DE).

Menu paths are those of the Avalonia editor (macOS and Linux). Where the Windows editor (WPF) differs, the text says so.

## How audio works

- The engine plays through **miniaudio** on the system's default output device. **WAV, MP3, OGG (Vorbis) and FLAC** files play directly. Without an output device the game keeps running, silently.
- Every sound is referenced by its **project-relative path**, such as `Assets/Audio/door.wav` — in the inspector, in scripts, in sound containers and in materials. The same path works in a shipped game.
- **32 voices** can play at the same time, shared by everything that makes sound (Audio Sources, one-shots, music, editor previews). When all are busy, a new sound takes over the voice of a less important one (see `Priority` below).
- **Five mixer buses:** **Master**, and **Music**, **SFX**, **Ambience** and **UI**, which all feed into Master.

| Played by | Bus | Reverb zones | Priority | Position |
|---|---|---|---|---|
| Audio Source component | its **Output Bus** | yes, scaled by its **Reverb Zone Mix** | its **Priority** (default 128) | follows its entity |
| `Audio.PlayOneShot` | SFX | no | 128 | fixed where it was started |
| `Audio.PlayOneShot2D` | SFX | no | 128 | none (2D) |
| `Audio.Music` | Music | no | 0 — never taken over | none (2D) |

## Components

### Audio Source

Plays one clip, or a [sound container](#sound-containers-vsndc), from an entity. Add it with **Component ▸ Audio ▸ Audio Source** or the inspector's **Add Component ▸ Audio ▸ Audio Source**; **GameObject ▸ Audio ▸ Audio Source** creates a new entity with one. Windows editor: **Component ▸ Audio ▸ Audio Source**, the inspector's **Add Component** menu, or right-click the scene in the Scene Hierarchy ▸ **Audio ▸ Audio Source**. Dropping an audio file onto the inspector adds one as well.

| Field | Default | Range | What it does |
|---|---|---|---|
| `AudioClipPath` (Clip) | empty | | A `.wav`, `.mp3`, `.ogg`, `.flac` or `.vsndc` file, project-relative. |
| `Volume` | 1 | 0–1 | Level of this source. |
| `Pitch` | 1 | 0.25–3 | Playback speed: 2 plays an octave higher and twice as fast. |
| `Loop` | off | | Repeat until stopped. |
| `PlayOnAwake` | on | | Start by itself when play begins — on the first frame, after every script's `Start()`, so a `Start()` that calls `Stop()` wins. |
| `Mute` | off | | Silence without stopping. |
| `Streaming` | off | | Decode while playing instead of loading the whole clip into memory. For music and long ambience. |
| `OutputBus` | SFX | Master, Music, SFX, Ambience, UI | The mixer bus this source plays through (stored as 0–4). |
| `SpatialBlend` | 0 | 0–1 | **0 = 2D** (no position, no distance), **1 = 3D**. Values in between keep the direction but blend the distance falloff toward 2D. |
| `MinDistance` | 1 | 0.1–50 m | Full volume up to this distance. |
| `MaxDistance` | 500 | 1–1000 m | Where the falloff ends. |
| `RolloffMode` | Logarithmic | Logarithmic, Linear, Custom | The falloff curve, see below. **Custom** currently behaves like Logarithmic. |
| `Priority` | 128 | 0–256 | 0 is the most important. With all voices busy, a new sound takes the voice with the highest number (the quietest of equals) if that number is at least its own — otherwise the new sound does not play. A looping source that lost its voice tries again every half second. |
| `StereoPan` | 0 | −1 to 1 | Left/right balance, mainly for 2D sounds. |
| `ReverbZoneMix` | 1 | 0–1 | How much of this source feeds the [reverb](#reverb-zones). |
| `DopplerLevel` | 1 | 0–2 | Pitch shift of moving sources; 0 = off. Higher values are capped at 2. |
| `Spread` | 0 | 0–360° | 0 places the sound clearly left or right; 360 plays it equally in both ears. |
| `EnableHrtf` | off | | Steam Audio binaural rendering — see [HRTF and occlusion](#hrtf-and-occlusion-steam-audio). |
| `EnableOcclusion` | off | | Steam Audio occlusion behind walls. Needs `EnableHrtf` and a 3D source. |

**A new Audio Source is 2D.** Set **Spatial Blend** to 1 for every sound that should come from a place in the world.

**Falloff.** *Logarithmic* keeps full volume up to Min Distance, then halves the level every time the distance doubles (−6 dB) and stops falling at Max Distance — it never reaches silence: with Min 2 and Max 15, the sound is still at about −17 dB however far away you are. *Linear* falls in a straight line to silence at Max Distance; use it for sounds that must not be heard outside their room.

**Children.** Audio Sources, Reverb Zones and the Audio Listener use their entity's place in the world, parents included: a listener on the player's camera under the player rig, or a source on a weapon in the player's hand, moves with it. (A box zone stays axis-aligned.)

### Audio Listener

The listener is the player's ears: 3D sounds are heard relative to its entity's position and facing. It has no settings. Put it on the camera the player looks through (**Component ▸ Audio ▸ Audio Listener**; Windows editor: the inspector's **Add Component ▸ Audio ▸ Audio Listener**).

- Only one listener is used: the first enabled one in the hierarchy; any others are ignored.
- In a scene without a listener, the **main camera** is the listener.
- The listener is picked when play starts and when a scene loads.

### Reverb Zone

A region with its own reverb. Add it with **Component ▸ Audio ▸ Reverb Zone** (or **GameObject ▸ Audio ▸ Reverb Zone** for a new entity); Windows editor: the inspector's **Add Component ▸ Audio ▸ Reverb Zone**, or right-click the scene in the Scene Hierarchy ▸ **Audio ▸ Reverb Zone**. How zones sound: [Reverb zones](#reverb-zones).

| Field | Default | Range | What it does |
|---|---|---|---|
| `Shape` | Sphere | Sphere, Box | Stored as 0 (Sphere) or 1 (Box). |
| `Radius` | 10 | 0.5–100 m | Radius of a sphere zone. |
| `BoxExtents` | (10, 5, 10) | | Half the size of a box zone on each axis, in metres. The box is axis-aligned: the entity's rotation is ignored. The Windows editor's inspector has no field for it yet. |
| `Falloff` | 3 | 0–20 m | Blend distance outside the boundary: full effect inside, none at boundary + Falloff. |
| `DecayTime` | 1.8 | 0.1–20 s | Length of the tail — 0.1 is a dry closet, 20 a cathedral. |
| `WetLevel` | 0.6 | 0–1 | Loudness of the tail. |
| `PreDelayMs` | 20 | 0–200 ms | Gap before the tail starts; bigger rooms have more. |

### Sound containers (.vsndc)

A sound container is a list of clips that plays a different one every time — footsteps, gunshots, creaks, impacts. Its path works anywhere a clip path goes: an Audio Source's clip, `Audio.PlayOneShot` / `PlayOneShot2D` and a material's footstep sound. (Music needs a plain audio file.)

**Create one:** right-click in the **Project** panel's **Audio** tab ▸ **New Sound Container**. It creates `NewSoundContainer.vsndc` (in `Assets/Audio`, or in the folder you are browsing) and opens it in the **Sound Container** editor; the Avalonia editor also has **Assets ▸ Create ▸ Sound Container**. Double-click a `.vsndc` to open it again (also **Window ▸ Sound Container Editor…**). In the editor:

- **Add clips…** (Windows editor: **+ Add clip**) adds clips, several at once; the Avalonia editor also takes files dropped on the list.
- Each clip has a play button to hear it and a **weight** — its relative chance (1 = normal, 2 = twice as often). The Avalonia editor shows the resulting chance per roll.
- **Pitch** and **Volume** set the random range for the whole container (minimum … maximum).
- **Roll (audition like the game)** plays the next pick exactly the way the game will.
- Every change is saved at once.

| Field | Default | What it does |
|---|---|---|
| `entries` | empty | The clips. Each has `clipPath`, `guid` (the clip's asset id, so the container still finds a renamed or moved clip) and `weight`. |
| `pitchMin` / `pitchMax` | 0.95 / 1.05 | Each play picks a pitch in this range and multiplies it onto the caller's pitch. |
| `volumeMin` / `volumeMax` | 0.9 / 1.0 | Each play picks a volume in this range and multiplies it onto the caller's volume. |

Picks come from a shuffled bag in which every clip has tickets in proportion to its weight. The bag refills when it is empty, so over a round every clip gets its share — and the same clip never plays twice in a row (with two or more clips). An Audio Source rolls a new pick every time it starts; a looping one keeps its pick until it restarts. Edits to a container during play are picked up within about two seconds.

The file is plain JSON:

```json
{
  "entries": [
    { "guid": "", "clipPath": "Assets/Audio/step_concrete_1.wav", "weight": 1 },
    { "guid": "", "clipPath": "Assets/Audio/step_concrete_2.wav", "weight": 1 },
    { "guid": "", "clipPath": "Assets/Audio/step_concrete_3.wav", "weight": 0.5 }
  ],
  "pitchMin": 0.95,
  "pitchMax": 1.05,
  "volumeMin": 0.9,
  "volumeMax": 1.0
}
```

## Playing sounds without code

1. **Bring the sound in.** Put the files into `Assets/Audio`, or use **Assets ▸ Import Asset…**. They appear in the **Project** panel's **Audio** tab with a waveform; clicking one plays it.
2. **Add an Audio Source.** Drag the file from the Project panel onto the inspector of the selected entity: the clip is set, and an Audio Source is added if the entity has none. Or add the component and choose the clip with the Clip field's browse button.
3. **Set it up.** For a sound in the world, set **Spatial Blend** to 1 and pick a **Min** and **Max Distance** that fit the space. **Loop** and **Play On Awake** for ambience; the right **Output Bus** so the player's volume settings apply.
4. **Give the scene ears.** An **Audio Listener** on the player's camera (without one, the main camera is used).
5. **Press Play.** Play On Awake sources start on the first frame.

A humming generator in a cellar, for example:

| Field | Value |
|---|---|
| Clip | `Assets/Audio/generator_hum.wav` |
| Loop, Play On Awake | on |
| Spatial Blend | 1 |
| Min Distance / Max Distance | 2 / 15 |
| Rolloff | Linear (silent beyond 15 m) |
| Output Bus | Ambience |

### Preview in the editor

The Audio Source inspector has **Preview ▶** and **Stop ■**: the source plays with its current settings without entering play mode, and changes to volume, pitch and the other fields are heard while it plays. With **Listen from camera (3D)** (on by default for 3D sources), the sound sits at the entity and the editor camera is the listener — fly around or drag the entity and you hear distance and direction change. One preview plays at a time, and entering play mode stops it. Previews go through the source's Output Bus, so the mixer faders apply; they have no reverb and no Doppler. A container rolls a new pick on every preview.

### Gizmos in the viewport

- A speaker icon marks every Audio Source, a green head every Audio Listener (while gizmos are shown: **View ▸ Gizmos**; Windows editor: **View ▸ Toggle Gizmos**).
- The selected Audio Source shows two wireframe spheres: **yellow = Min Distance**, **orange = Max Distance**.
- The selected Reverb Zone shows its boundary in **cyan** and the outer edge of its Falloff in **dark teal**.

The shapes follow inspector edits and entity drags as you make them.

## Scripting

Gameplay audio is driven by scripts through the `Vortex` namespace — `Audio` for one-shots and bus volumes, `Audio.Music` for the music channel, and `AudioSource`, a handle to an entity's Audio Source. Sounds only start while the game runs (play mode, the standalone player, a shipped game). How to create and attach a script: [Scripting: Getting Started](Scripting-Getting-Started).

| Call | What it does |
|---|---|
| `Audio.PlayOneShot(clip, position, volume = 1f, pitch = 1f)` | Plays a sound once at a world position. |
| `Audio.PlayOneShot2D(clip, volume = 1f, pitch = 1f)` | Plays a sound once without a position. |
| `Audio.SetBusVolume(bus, volume)`, `Audio.GetBusVolume(bus)` | Set or read a bus fader, `0..1`. `bus` is `"Master"`, `"Music"`, `"SFX"`, `"Ambience"` or `"UI"` (any case); other names are ignored, and `GetBusVolume` returns 1 for them. |
| `Audio.Music.Play(clip, fadeInSeconds = 0f)` | Starts a music track, replacing the current one. |
| `Audio.Music.CrossFade(clip, seconds)` | Fades the current track out while the new one fades in. |
| `Audio.Music.Stop(fadeOutSeconds = 0f)` | Stops the music. |
| `Audio.Music.IsPlaying`, `Audio.Music.Volume` | Whether a track plays; the music volume, `0..1`. |
| `GetAudioSource()`, `Scene.GetAudioSource(entity)` | A handle to the Audio Source of this or another entity, or `null`. |
| `Play()`, `Stop()`, `Pause()`, `Resume()`, `IsPlaying` | Control the component's sound. |
| `FadeIn(seconds)`, `FadeOut(seconds)`, `FadeTo(target, seconds)` | Sample-accurate fades. |
| `Volume`, `Pitch`, `Loop`, `Clip`, `Enabled` | The component's fields, live. |
| `Physics.GroundStepSound(from, maxDist = 3f)` | The footstep sound of the material below a point (see [Footsteps](#footsteps-with-a-sound-container)). |

Public `string` fields of a script — like the clip paths in the examples below — show up on its Script component in the inspector, so sounds can be swapped without touching the code.

### One-shots

A one-shot takes a voice, plays once and frees the voice again — there is nothing to keep or clean up. `Audio.PlayOneShot` places the sound in the world; `Audio.PlayOneShot2D` plays it flat, for UI clicks, stingers and the player's own weapon. Both take a sound container as well as a clip.

```csharp
using Vortex;

// On a trigger collider in a corridor: the first time the player walks in, a pipe creaks at
// this spot, a stinger hits and the chase music takes over.
public class ScareTrigger : VortexBehaviour
{
    public string CreakSound = "Assets/Audio/pipe_creak.wav";
    public string Stinger = "Assets/Audio/stinger.wav";
    public string ChaseMusic = "Assets/Audio/chase.ogg";

    private bool _done;

    public override void OnTriggerEnter(TriggerHit other)
    {
        if (_done || other.Tag != "Player") return;
        _done = true;

        Audio.PlayOneShot(CreakSound, Position);   // 3D: heard from where the trigger is
        Audio.PlayOneShot2D(Stinger, 0.8f);         // 2D: equally loud wherever you stand
        Audio.Music.CrossFade(ChaseMusic, 2f);      // old track out, new track in, over 2 s
    }
}
```

- 3D one-shots use fixed distance settings: full volume within 1 m, then the logarithmic falloff with Max Distance 500 m — 10 m away, a one-shot plays at a tenth of its level (−20 dB). For a different falloff, another bus or a sound that moves with an object, use an Audio Source.
- One-shots have no reverb: [reverb zones](#reverb-zones) only affect Audio Sources.
- `Position` is relative to the entity's parent. For a child entity, get the world position with `TryGetWorldPose(out position, out rotation)`.

### A sound at a position

Any point in the world will do — a raycast hit, another entity (`Scene.PositionOf(entity)`), the spot where an object landed. Playing a sound does not alert AI: report it with `Perception.MakeNoise` so monsters with an **AI Perception** component can hear it.

```csharp
using Vortex;

// Press E to knock on whatever you are looking at: the knock plays where the view ray hits,
// with a little pitch variation, and AI agents in hearing range notice it.
public class KnockOnWalls : VortexBehaviour
{
    public string KnockSound = "Assets/Audio/knock.wav";

    private readonly System.Random _random = new System.Random();
    private bool _wasDown;

    public override void Update(float dt)
    {
        bool down = Input.GetKey("E");
        if (down && !_wasDown)
        {
            RaycastHit hit;
            if (Physics.Raycast(Position, Forward, 3f, out hit))
            {
                float pitch = 0.95f + 0.1f * (float)_random.NextDouble();
                Audio.PlayOneShot(KnockSound, hit.Point, 1f, pitch);
                Perception.MakeNoise(hit.Point, 0.6f, EntityId);
            }
        }
        _wasDown = down;
    }
}
```

### The entity's own Audio Source

`GetAudioSource()` returns a handle to the Audio Source on the script's own entity, or `null` when there is none. It starts, stops and fades the component's sound, and `Volume` and `Pitch` change it while it plays. Changes made through the handle are undone when play stops, like every other change in play mode.

```csharp
using Vortex;

// A radio. Put it on an entity with an Audio Source: a looping clip, Play On Awake off,
// Spatial Blend 1. E switches it on and off with a fade. Other scripts can pull it under a
// voice line with SendMessage(radio, "duck") and bring it back with "unduck".
public class Radio : VortexBehaviour
{
    public float FadeSeconds = 1.5f;

    private AudioSource _source;
    private bool _on;
    private bool _wasDown;

    public override void Start()
    {
        _source = GetAudioSource();
        if (_source == null) Debug.LogWarning("Radio needs an Audio Source on the same entity.");
    }

    public override void Update(float dt)
    {
        if (_source == null) return;
        bool down = Input.GetKey("E");
        if (down && !_wasDown)
        {
            _on = !_on;
            if (_on) _source.FadeIn(FadeSeconds);   // restarts silent and glides up to Volume
            else _source.FadeOut(FadeSeconds);      // glides to silence, then stops
        }
        _wasDown = down;
    }

    public override void OnMessage(string message, object arg)
    {
        if (_source == null || !_on) return;
        if (message == "duck") _source.FadeTo(0.25f, 0.3f);
        else if (message == "unduck") _source.FadeTo(1f, 1.5f);
    }
}
```

- `Play()` always starts from the beginning (a container rolls a new pick). `FadeIn` starts silent and glides up, `FadeOut` glides down and then stops. `FadeTo(target, seconds)` moves a fade level between 0 and 1 that sits on top of `Volume` — the way to duck or swell a sound that is playing.
- `Clip` and `Loop` apply the next time the source starts.
- `Enabled = false` stops the sound at once; setting it back to `true` does not restart it — call `Play()`.
- `Scene.SetActive(entity, false)` stops the Audio Sources of the entity and its children; they stay stopped when it is activated again. `Scene.Destroy` stops them as well.
- A prefab spawned with `Scene.Instantiate` starts its Play On Awake sources on the next frame, as a scene does when play begins.

The Audio Source of another entity works the same way:

```csharp
// From any script: fade out the hum of the entity named "Generator".
AudioSource hum = Scene.GetAudioSource(Scene.Find("Generator"));
if (hum != null) hum.FadeOut(3f);
```

### Music with crossfade

`Audio.Music` is one music channel: a single track at a time, streamed, looping, on the Music bus, and never taken over by other sounds.

```csharp
using Vortex;

// Music that follows the danger: the calm track while the monster has not spotted the
// player, the chase track while it can see them.
public class MusicDirector : VortexBehaviour
{
    public string CalmTrack = "Assets/Audio/explore.ogg";
    public string ChaseTrack = "Assets/Audio/chase.ogg";
    public string MonsterName = "Monster";   // entity with an AI Perception component

    private long _monster;

    public override void Start()
    {
        _monster = Scene.Find(MonsterName);
        Audio.Music.Volume = 0.8f;
        Audio.Music.Play(CalmTrack, 3f);       // fade in over 3 s
    }

    public override void Update(float dt)
    {
        bool hunted = _monster != 0 && Perception.CanSee(_monster);
        // Asking for the track that is already playing changes nothing, so this is safe every frame.
        Audio.Music.CrossFade(hunted ? ChaseTrack : CalmTrack, hunted ? 1f : 4f);
    }
}
```

- `Play(clip, fadeIn)` replaces a playing track (the old one fades out in a quarter of a second); `CrossFade(clip, seconds)` overlaps both tracks for `seconds`; `Stop(fadeOut)` ends the music.
- Music needs a plain audio file — a `.vsndc` container does not work here.
- Loading another scene stops the music and sets `Audio.Music.Volume` back to 1. Start the next track from a script in the new scene.

The end of a level, from any script:

```csharp
Audio.Music.Stop(5f);   // fade out over 5 s
```

### Bus volumes and a settings menu

`Audio.SetBusVolume(bus, volume)` moves a bus fader from a script, `Audio.GetBusVolume(bus)` reads it — what an options menu needs. Volumes are linear, `0..1` (0.5 is about −6 dB).

```csharp
using Vortex;

// A small audio options panel drawn with the immediate-mode UI. O opens and closes it;
// - and + change a bus by 10 %. The panel reads the current values every frame, so it also
// shows the volumes a shipped game restored from the player's last session.
public class AudioOptions : VortexBehaviour
{
    private static readonly string[] Buses = { "Master", "Music", "SFX", "Ambience", "UI" };

    private bool _open;
    private bool _wasDown;

    public override void Update(float dt)
    {
        bool down = Input.GetKey("O");
        if (down && !_wasDown)
        {
            _open = !_open;
            Cursor.Locked = !_open;   // free the mouse while the panel is open (first-person games lock it)
        }
        _wasDown = down;
        if (!_open) return;

        Color panel = Color.Rgba(14, 14, 18, 230);
        Color button = Color.Rgb(52, 52, 60);
        Color text = Color.Rgb(235, 235, 240);
        float x = 40f, y = 40f;
        UI.Rect(x, y, 380f, 24f + Buses.Length * 46f, panel, 10f);

        for (int i = 0; i < Buses.Length; i++)
        {
            float row = y + 12f + i * 46f;
            float volume = Audio.GetBusVolume(Buses[i]);
            int percent = (int)System.Math.Round(volume * 100f);
            UI.Text(Buses[i] + "   " + percent + " %", x + 16f, row, 220f, 36f, 16f, text);
            if (UI.Button(x + 250f, row, 54f, 36f, "-", button, text, 20f, 6f))
                Audio.SetBusVolume(Buses[i], System.Math.Max(0f, volume - 0.1f));
            if (UI.Button(x + 312f, row, 54f, 36f, "+", button, text, 20f, 6f))
                Audio.SetBusVolume(Buses[i], System.Math.Min(1f, volume + 0.1f));
        }
    }
}
```

- **A shipped game remembers the player's choice.** Every `SetBusVolume` there saves the bus volumes and mutes, and the next start applies them on top of the mixer's defaults (where: [Shipping](#shipping)). Fill your sliders from `GetBusVolume` rather than from fixed defaults — a menu that writes its own defaults overwrites the saved values.
- In editor play nothing is saved: bus volumes go back to the Audio Mixer's values whenever play starts or a scene loads.
- Scripts cannot mute, solo or duck buses; those are set in the [Audio Mixer](#the-audio-mixer-window).
- `Settings.SetMasterVolume` only stores a number and does not change what you hear — use `Audio.SetBusVolume("Master", volume)`.

## Footsteps with a sound container

Footsteps are the classic use of a container: three to six recordings of one step, with pitch and volume variation, so that no two steps sound alike.

1. In the **Audio** tab, right-click ▸ **New Sound Container**, name it (for example `footsteps_concrete.vsndc`), click **Add clips…** and pick the step recordings. Leave the pitch range at 0.95–1.05 and the volume range at 0.9–1.0 to start with, and press **Roll** a few times to hear the variation.
2. Give each floor material its steps: open the material (`.vmat`) in the **Material Editor** and set its **Footstep Sound** to the container (or a single clip). `Physics.GroundStepSound(position)` then returns the footstep sound of the material below any point, so a new floor type never needs code.
3. Play the steps from the player's script:

```csharp
using Vortex;

// Footsteps with variation. Attach to the player (the entity your movement script moves).
// Every StepDistance metres on the ground it plays the footstep sound of the material below
// (Material Editor > Footstep Sound) or, where a material has none, the default container.
// Each play rolls another take with its own pitch and volume.
public class Footsteps : VortexBehaviour
{
    public string DefaultSteps = "Assets/Audio/footsteps_concrete.vsndc";
    public float StepDistance = 2.1f;

    private Vector3 _last;
    private float _distance;

    public override void Start()
    {
        _last = Position;
    }

    public override void Update(float dt)
    {
        Vector3 p = Position;
        float dx = p.X - _last.X;
        float dz = p.Z - _last.Z;
        _last = p;
        if (!Physics.Grounded) return;

        _distance += (float)System.Math.Sqrt(dx * dx + dz * dz);
        if (_distance < StepDistance) return;
        _distance = 0f;

        string sound = Physics.GroundStepSound(p, 3f);
        if (sound == "") sound = DefaultSteps;
        Audio.PlayOneShot(sound, p, 0.7f);
        Perception.MakeNoise(p, 0.3f, EntityId);   // so monsters can hear the player walk
    }
}
```

The Horror template's `Templates/HorrorStarter/Assets/Scripts/Player/FootstepAudio.cs` works the same way, with a longer stride while sprinting.

## The Audio Mixer window

Open it with **Window ▸ Audio Mixer…** (also **Tools ▸ Audio Mixer…**); Windows editor: **Window ▸ Audio Mixer**.

- **One strip per bus:** Master, Music, SFX, Ambience and UI. A fader goes from silence (−∞ dB) up to 0 dB — buses can be turned down, not boosted. In the Avalonia editor, double-click a fader or its dB readout to reset it to 0 dB.
- **M** mutes a bus. **S** solos it: while any bus is soloed, every other bus except Master is muted. Solo is a listening aid and is not saved; the Avalonia editor releases it when the window closes. In the Windows editor, switch solo off before closing the window — otherwise the other buses stay muted until play starts again.
- **Meters** show each bus's level (RMS bar with a peak-hold line) whenever sound flows through it — in play mode, during an inspector preview, while auditioning in the Project panel.
- Every change applies at once and is saved to `ProjectSettings/AudioMixer.json`. That file sets the mix whenever play starts, in the standalone player and in exported games.

### Ducking

A ducking rule turns one bus down while another one is loud — the ambience dips while a stinger plays on SFX, say. **+ Add ducking rule** adds one (it starts as "Music ducks Ambience"); each row reads *When [trigger bus] plays, duck [target bus] by … dB*, followed by attack, release and threshold.

| Setting | Default | What it does |
|---|---|---|
| Trigger bus | Music | The bus that is listened to. |
| Target bus | Ambience | The bus that is turned down. |
| dB | −12 | How far the target goes down (a negative number). |
| Attack | 80 ms | How fast it goes down. |
| Release | 400 ms | How fast it comes back up. |
| Threshold | 0.05 | The trigger bus's level (RMS, 0–1) above which the rule engages. The Windows editor has no field for it and always uses 0.05. |

A bus cannot duck itself, and if two rules use the same pair of buses, the last one wins. There is no dedicated dialogue bus: route voice lines through an Audio Source on one of the five buses — for example UI — and let that bus duck Music or Ambience. Mixer snapshots don't exist; at run time, scripts set bus volumes and fade single sources.

The Avalonia editor's mixer also holds the project's **Steam Audio** switch — see [HRTF and occlusion](#hrtf-and-occlusion-steam-audio).

## Reverb zones

Vortex has one reverb (an algorithmic Freeverb), and it takes on the character of the zone the **listener** is in:

- Inside a zone, the reverb uses that zone's Decay Time, Wet Level and Pre-Delay. In the Falloff band outside the boundary the effect fades smoothly, so walking through a door doesn't click. Where zones overlap, their settings are averaged by how deep the listener is in each. Outside every zone there is no reverb.
- Every Audio Source feeds the reverb in proportion to its **Reverb Zone Mix**: 0 keeps a source dry (a radio in the player's hand, UI sounds), 1 sends all of it.
- What counts is the listener's room, not the source's: while the listener stands in a hall, every Audio Source is heard with the hall's reverb, even one outside it.
- One-shots and music stay dry; only Audio Source components feed the reverb.
- The reverb is fed before the bus faders and its tail plays on the Master bus. Turning down or muting a bus such as Ambience therefore does not silence the reverb of that bus's sources — Master does.
- Zones have no script API. Switch one off with `Scene.SetActive(zoneEntity, false)`.
- A box zone is axis-aligned; rotating its entity has no effect.

When play stops, the reverb is cleared.

## Sound Studio

**Window ▸ Sound Studio…** (also **Assets ▸ Asset Library ▸ Sound Studio…**) turns a description into sound effects — with a free offline synthesizer (UI clicks, impacts, whooshes, risers, heartbeats, wind, drones) or, with your own API keys, ElevenLabs, fal.ai and Stability AI models, optionally with Claude writing the prompts. **Save** keeps a take in your [asset library](Asset-Library); **Save + Add** also copies it into the project's `Assets/Audio`, ready for an Audio Source, a container or a script. Every take keeps its recipe, so you can reopen it later and make a sibling. Sound Studio is part of the Avalonia editor; the full guide is [Sound Studio](Sound-Studio). Free recorded sounds (Freesound, Kenney, Sonniss) are in the [Asset Store](Asset-Store).

## HRTF and occlusion (Steam Audio)

The built-in spatializer gives 3D sounds distance falloff, left/right placement and Doppler. It cannot tell front from back or above from below, and walls do not block sound. The optional **Steam Audio** layer (Valve, version 4.8.1, Apache-2.0) adds two things:

- **HRTF** — binaural rendering for headphones: a sound behind or above the player is heard there.
- **Occlusion** — a source behind a wall gets quieter. Rays are cast against the scene's solid colliders (mesh colliders by their triangles, box colliders as boxes, sphere and capsule colliders as their bounding boxes) about 30 times a second on a thread of their own, never in the audio callback. A fully blocked source drops to 8 % of its level; the sound gets quieter, it is not filtered.

It is off by default, and nothing changes until you turn it on.

### Turning it on

1. **Get the Steam Audio runtime.** It is not part of the Vortex download. Take it from the [Steam Audio 4.8.1 release](https://github.com/ValveSoftware/steam-audio/releases/tag/v4.8.1) and put the library next to the editor's executable: `phonon.dll` on Windows, `libphonon.dylib` (or `phonon.bundle`) on macOS — in the app that is `Vortex Editor.app/Contents/MacOS` — and `libphonon.so` on Linux. In a source checkout on Windows, `ThirdParty\steam-audio\fetch-phonon-dll.ps1` downloads the SDK and copies `phonon.dll` into `x64\Release` and `x64\Debug`.
2. **Switch it on for the project:** **Audio Mixer ▸ Spatial audio ▸ Steam Audio spatialisation (HRTF / occlusion)**. The Windows editor's mixer has no such switch yet: close the Audio Mixer window and set `"steamAudioEnabled": true` in `ProjectSettings/AudioMixer.json` (if the file does not exist, create it with `{ "steamAudioEnabled": true }`).
3. **Opt in per source:** tick **HRTF** on the Audio Source (Windows editor: **HRTF binaural (Steam Audio)**), with Spatial Blend 1. Tick **Occlusion** (**Occlusion behind walls**) on sources that walls should block.

Without the library, or with the project switch off, every source quietly uses the built-in spatializer.

### Limits today

- An HRTF source gets its direction and occlusion from Steam Audio and its distance falloff (Min and Max Distance, Rolloff, Spatial Blend) from the engine, as other 3D sources do. Doppler and Spread do not apply to it.
- The occlusion geometry is the scene's static collision, taken on the first frame of play and after every scene change.
- Colliders that move or appear during play (doors, spawned props) do not change the occlusion.
- All surfaces share one acoustic material. There is no transmission through materials and no Steam Audio reflection or reverb — reverb comes from [reverb zones](#reverb-zones).
- HRTF and occlusion are read when a source starts; toggling them during play applies the next time it starts.

## Shipping

A release build (**File ▸ Build…**; Windows editor: **File ▸ Build**) packs audio like this:

- **Every file under `Assets/`** goes into the game's `Assets.vpak` — all clips and `.vsndc` containers, whether a scene uses them or not (scripts are compiled into the game instead, and each scene gets its own pak). Files outside `Assets/` are not packed, so keep every clip there.
- **`ProjectSettings/AudioMixer.json`** is packed as well: the game starts with your bus levels, mutes, ducking rules and Steam Audio switch.
- Sounds play straight from the pak; no loose audio file is read. Streaming sources and music are decoded from the compressed data while they play.
- **The player's volumes** — whatever `Audio.SetBusVolume` set, plus the bus mutes — are saved per game and applied at every start, on top of the mixer's defaults:

| Platform | File |
|---|---|
| Windows | `%LOCALAPPDATA%\Vortex\<project name>\audio-settings.json` |
| macOS | `~/Library/Application Support/Vortex/<project name>/audio-settings.json` |
| Linux | `~/.local/share/Vortex/<project name>/audio-settings.json` |

- **Steam Audio:** the game needs the runtime library next to its executable. The Windows editor's export is meant to copy `phonon.dll` (about 50 MB) only for projects that switched Steam Audio on; the Avalonia editor's export copies the native libraries of the platform's runtime pack. Check the build folder, and copy the library next to the executable if it is missing — without it the game uses the built-in spatializer.
- A debug build reads the project folder directly and saves no player volumes.

If sounds are missing in a shipped game, look at `player-audio.log` next to the game's `Assets.vpak` — or, when that folder is write-protected, in a `VortexEngine` folder in the same application-data folder as the table above. Clips that are not in the pak or fail to decode are listed there.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| A 3D sound is equally loud everywhere | Spatial Blend is 0 (the default) — set it to 1. |
| A sound is still audible far away | Logarithmic falloff never reaches silence — use Linear for a hard edge at Max Distance. |
| A sound doesn't play when many others do | All 32 voices are taken by more important sounds (lower `Priority` numbers) — lower its number. |
| No reverb on a one-shot | One-shots and music are dry; use an Audio Source. |
| A bus is silent in the editor | It is muted or another bus is soloed in the Audio Mixer — or a script turned it down in the last play run; starting play or opening the Audio Mixer restores the saved levels. |
| The music stopped after `Scene.Load` | Scene changes stop the music; start it again in the new scene. |

## Examples in the Horror template

Every new Horror Starter project contains these scripts:

| Script | Shows |
|---|---|
| `Templates/HorrorStarter/Assets/Scripts/Player/FootstepAudio.cs` | Footsteps from the floor's material (`Physics.GroundStepSound`) as 3D one-shots. |
| `Templates/HorrorStarter/Assets/Scripts/World/HorrorAtmosphere.cs` | A looping room tone through `Audio.Music.Play` with a fade-in. |
| `Templates/HorrorStarter/Assets/Scripts/Weapons/Weapon.cs` | A layered 2D gunshot from a `.vsndc` container with pitch variation. |
| `Templates/HorrorStarter/Assets/Scripts/Weapons/Firearm.cs` | Reload sounds timed to the reload's progress. |
| `Templates/HorrorStarter/Assets/Scripts/World/ShellCasing.cs` | A 3D one-shot per bounce, louder on harder hits. |
| `Templates/HorrorStarter/Assets/Scripts/UI/EscMenu.cs` | Master, effects and music sliders calling `Audio.SetBusVolume`. |

## See also

- [Scripting API Reference](Scripting-API-Reference#audio) — `Audio`, `Audio.Music` and the [`AudioSource`](Scripting-API-Reference#audiosource) handle, member by member.
- [Entities and Components](Entities-and-Components#audiosource) — the serialized fields of Audio Source, Audio Listener and [Reverb Zone](Entities-and-Components#reverbzone).
- [Sound Studio](Sound-Studio) · [Asset Library](Asset-Library) · [Asset Store](Asset-Store) — making and finding sounds.
- [Claude Tools](Claude-Tools#audio) — the audio tools Claude can use in the editor (audition, configure an Audio Source, generate a sound, set a bus volume).
- [Design: Audio Engine](Design-Audio-Engine) · [Design: Steam Audio Integration](Design-Steam-Audio-Integration) — architecture and decisions.
- [Audio-Anleitung (Deutsch)](Audio-Anleitung-DE) — the German guide.
