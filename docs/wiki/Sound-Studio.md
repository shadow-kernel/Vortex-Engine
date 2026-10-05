# Sound Studio

**Window → Sound Studio…** (also *Assets → Asset Library → Sound Studio…* and the **Sound Studio** entry at the bottom of the Asset Store's source list) turns a description into sound effects: describe the sound, audition the takes, refine them, and save the one you like into your [[Asset-Library]] — and straight into the project. Every saved take remembers how it was made, so you can reopen it later and make a sibling.

## Backends

| Backend | Good for | Account | Rights |
|---|---|---|---|
| **Procedural (offline)** | UI clicks, beeps, whooshes, impacts, explosions, gunshot placeholders, heartbeats, risers, stingers, wind and drones — synthesized instantly on your machine | none, free | your own work |
| **ElevenLabs Sound Effects** | realistic SFX up to 30 s, seamless loops for ambience | your ElevenLabs API key | paid plans: commercial use in games (no resale as sample packs); free plan: non-commercial with attribution |
| **CassetteAI SFX (fal.ai)** | sound effects | your fal.ai key | the model's terms |
| **Stable Audio Open (fal.ai)** | music beds and ambience (up to 47 s) | your fal.ai key | the model's terms |
| **Stable Audio 2.5 (Stability AI)** | music beds and long ambience up to 3:10 min — 20 credits (≈ $0.20) per take, any length | your Stability AI key | Stability AI's API terms |
| **Stable Audio 3 (Stability AI)** | music and long ambience up to 6:20 min — 26 credits (≈ $0.26) per take | your Stability AI key | Stability AI's API terms |

Keys are your own (**Keys…**) and stay on this machine. The studio shows the estimated cost per take — ElevenLabs uses about 200 credits for an auto-length take and 40 credits per second when you set the length; Stability charges per take, whatever the length (failed takes are free). Takes arrive as MP3; tick **Uncompressed WAV** for ElevenLabs (Creator/Pro plans) or Stability. The Stable Audio models have no loop flag, so the *Seamless loop* switch is hidden for them.

## Making sounds

1. Type a description, or click a preset (*Horror Stinger, Footsteps Stone, Door Creak, Ambience Basement, Whisper, Heartbeat, Gunshot Distant, Reload, Bullet Whiz, Explosion Far, UI Click, Wind Ruins*).
2. Set the length (or leave it on *auto*), *Seamless loop* for ambience, *Prompt influence* (ElevenLabs: higher = more literal) and how many takes you want.
3. **Generate** (⌘/Ctrl+Enter). Each take appears as a card with its waveform — click one to hear it, click another to compare.
4. **Save** puts the take into the library; **Save + Add** also copies it into the project's `Assets/Audio`.

## Claude designs the prompts

With **Claude designs the prompts** switched on (your Anthropic key), Claude acts as the sound designer: it turns a short idea — in any language — into detailed English prompts (source, materials, timing, space, distance, mood), makes takes that are genuinely different (close vs. distant, wood vs. metal …) and labels them. Then talk to it in the *Conversation* box: “dumpfer, mehr Hall, kürzer” rewrites the best prompt and generates again — no retyping. The token use of the session is shown under the settings. Choose the model (Claude Opus 5.5 by default, Sonnet 5.5 or Haiku 4.5 for cheaper sessions).

## Recipes

A saved take keeps its **recipe** — your original idea, the final prompt, the backend, length, loop, influence, the output format, when it was made, the designer model and the conversation notes — in the library entry and in the project file's `.vmeta`. In the Library tab the details pane shows it; **Open in Sound Studio** (details pane, or right-click the sound in the Library or the Asset Browser) loads it back. Generating again makes a **new** sound linked to the original (text-to-audio is not deterministic) — the saved one is never overwritten.
