# Horror Starter

A ready-to-play first-person horror foundation. Press **Play** (or export) and you are in a dark
bunker room with a shadow-casting flashlight.

## Controls

| Key | Action |
|-----|--------|
| WASD + mouse | move + look (Shift sprint, Ctrl/C crouch, Space jump) |
| F | flashlight on/off (battery drains while on) |
| E | interact (the bunker door) |
| ESC | pause (Q quits) |
| F9 | in-game dev console |

## What's inside (all plain project scripts — read them, change them)

- `Player/PlayerControllerFP` — Quake-feel FP movement through the engine's collide-and-slide.
- `Player/FlashlightController` — F-toggle, battery drain/recharge, dying-bulb flicker, HUD bar.
- `Player/Interactor` — raycast + `[E]` prompt; sends `"interact"` to anything tagged **Interactable**.
- `Player/FootstepAudio` — material-driven steps: assign a footstep sound to a floor **material**
  in the Material Editor and every floor using it just works (no sounds ship with the template).
- `World/SlidingDoor` — coroutine slide + `Physics.RefreshCollider` so the doorway really opens.
- `World/JumpScareTrigger` — walk past the door: post-FX panic ramp + the stalker spawns behind you.
- `World/MonsterStalker` — drifts toward you, looms, despawns.
- `World/HorrorAtmosphere` — crushes the ambient light.

Fog, vignette and film grain are **authored in the Environment panel** (saved in the scene) —
no script needed. The one-behaviour-per-entity rule is why the player's scripts live on child
entities (Flashlight, Feet, Hands).
