# Getting Started

Your first hour with Vortex: install the editor, start a game from the **Horror Starter**, change its level, add a
scare of your own and build a game you can hand to a friend. Every step links to the page that goes deeper.

## 1. Install

| Platform | How |
|---|---|
| **Windows 10 / 11** (64-bit, DirectX 12) | Run `VortexEngine-Setup-<version>.exe` from the [latest release](https://github.com/shadow-kernel/Vortex-Engine/releases/latest). *Vortex Engine* in the Start menu is the editor, the same one as on macOS and Linux. *Vortex Engine (Classic)* is the previous Windows editor and ships in v3.0 only. The editor keeps itself up to date: a patch installs by itself when the editor starts; a bigger update shows its notes first (*Help ▸ Check for Updates…*). Coming from v2.x? The old editor offers the update, and afterwards the new one opens. |
| **macOS** on Apple Silicon | Open `Vortex-Editor-<version>.dmg` from the release and drag *Vortex Editor* to Applications. It is not notarised yet: the first time, right-click it ▸ **Open**. Or build it yourself: `tools/macos/make-app.sh --install`. |
| **Linux** x64 (Vulkan) | Build from source: `Scripts/linux-dev.sh --release --editor` (see the README's Linux section). |

## 2. Create your game

The **Project Hub** opens first. On **Create**, pick **Horror Starter**, give the project a name and a folder, and
press **Create Project**.

The first time, the editor downloads the template's models, textures and sounds (about 450 MB, once per Vortex
version — the Create page shows the size, the button the progress). Every later project starts from that copy.
Details and the other templates: [Project Templates](Project-Templates).

The project opens in the **Yard** — a night-time industrial compound with a guard house, containers and two
warehouses. The second scene, `Demo` — a lit brick cellar — is in *File ▸ Open Scene…*.

## 3. Find your way around

| Where | What |
|---|---|
| **Scene** (left) | the entities of the open scene; **+** creates new ones |
| **Files** (left, below) | the project folder |
| **Viewport** (centre) | the 3D view; the small view shows what the selected camera sees |
| **Project · Library · Asset Store · Console** (bottom) | the project's assets, every asset on your machine, free assets to download, messages |
| **Inspector · Environment · Claude** (right) | the selection's components; the scene's light, fog and post effects; the Claude panel |

In the viewport: hold the **right mouse button** and use **W A S D** to fly (**Q / E** down / up, **Shift** faster), the
wheel moves in and out, **F** frames the selection, **W / E / R** switch between move, rotate and scale, **Home**
resets the camera. *Help ▸ Keyboard Shortcuts* lists the rest (on macOS ⌘ where this page says Ctrl).

Press **▶** in the toolbar to play the scene inside the editor and **■** to stop.

## 4. Change the level

- **Place things:** drag a model from the **Project** tab into the viewport, or create primitives and lights with
  *GameObject ▸ 3D Object / Light*.
- **Reuse:** the **Library** tab holds every asset from all your projects, stored once
  ([Asset Library](Asset-Library)).
- **Download:** the **Asset Store** tab searches Poly Haven, ambientCG, Kenney, Freesound and more; a download lands
  in your library and, if you like, straight in the project — with its license recorded
  ([Asset Store](Asset-Store)).
- **Mood:** the **Environment** tab sets the scene's fog, ambient occlusion, bloom, vignette, film grain, colour
  grading and chromatic aberration
  ([Horror Essentials](Horror-Essentials)).

Every change is one undo step (**Ctrl+Z**). *File ▸ Save All* (**Ctrl+Shift+S**) saves the scene and the project.

## 5. Add a scare

The Horror Starter already has a full jump scare (`Assets/Scripts/World/JumpScareTrigger.cs`). Make a smaller one
yourself:

1. *GameObject ▸ Create Empty*, name it `CorridorScare` and move it where the player will walk.
2. *Component ▸ Physics ▸ Box Collider*. In the Inspector, tick **Is Trigger** and size the box to the walkway.
3. *Component ▸ Script ▸ New Script…*, name it `CorridorScare`. The script opens in your code editor; replace its
   contents with:

```csharp
using System.Collections;
using Vortex;

// Fires once when the player walks into this entity's trigger collider:
// a sting, and two seconds of grain and colour fringing.
public class CorridorScare : VortexBehaviour
{
    public string Sting = "Assets/Audio/sting.wav";   // any sound of your project (set it in the Inspector)

    private bool _fired;

    public override void OnTriggerEnter(TriggerHit other)
    {
        if (_fired || other.Tag != "Player") return;
        _fired = true;
        Audio.PlayOneShot2D(Sting);
        StartCoroutine(Shock());
    }

    private IEnumerator Shock()
    {
        PostFx.SetGrain(true, 0.6f, 1.8f);
        PostFx.SetChromaticAberration(true, 1.4f, 1.1f);
        yield return new WaitForSeconds(2f);
        PostFx.SetChromaticAberration(false);
        PostFx.SetGrain(true, 0.25f, 1.6f);   // back to the Yard's own grain
    }
}
```

4. Save the file. Scripts compile when you press **▶** — a mistake shows up in the **Console** with file and line,
   and Play waits until it is fixed. During play, a saved change is picked up when you switch back to the editor.
5. Pick a sound for **Sting** in the Inspector — one of your own, one from the Asset Store, or one you describe in
   the **Sound Studio** (*Window ▸ Sound Studio…*, see [Sound Studio](Sound-Studio)).
6. Press **▶** and walk into the corridor.

How scripts work, from the lifecycle to hot reload: [Scripting: Getting Started](Scripting-Getting-Started). What
the engine offers for horror — shadows, fog, triggers, raycasts, coroutines, saving: [Horror Essentials](Horror-Essentials).
Sound in depth: [Audio](Audio). Every API: [Scripting API Reference](Scripting-API-Reference).

## 6. Let Claude help

Claude can build with you: *Tools ▸ Claude ▸ Connect Claude Code / Desktop…* connects Claude Code or Claude Desktop
to the editor's MCP server, and the **Claude** tab on the right is a chat that works in the open scene. Ask for
"a narrow corridor with four flickering lights and some debris on the floor" and watch it build — every change is
an undo step, and *Tools ▸ Claude ▸ Operations…* shows (and reverts) what it did. Setup and the safety model:
[Claude Integration](Claude-Integration); everything it can do: [Claude Tools](Claude-Tools).

## 7. Build the game

*File ▸ Build…* (**Ctrl+B**) opens **Build Game**: pick the target platform (Windows, macOS or Linux — another
platform's runtime pack installs from the same dialog), the configuration and the output folder, then **Build**.
**Release** packs and obfuscates the assets; **Debug** links your project so script edits hot-reload in the built
game. Before building, the license check lists any asset whose license needs attention; attribution-required assets
are credited in a `CREDITS.md` next to the game.

## Where next

- Upgrade a game when its template improves: [Project Templates ▸ Updating a project](Project-Templates)
- How the engine fits together: [Developer Guide](Developer-Guide) · [Architecture](Architecture)
- What works today, per subsystem: [Feature Status Matrix](Feature-Status-Matrix)
