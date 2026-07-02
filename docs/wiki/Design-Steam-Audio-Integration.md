# Design — Steam Audio Integration (Audio v2: HRTF + Occlusion)

> Status: **design + foundational integration** (Epic #5, issue #21). Ships in v2.6.0 **disabled by
> default** (opt-in per project + per source); the v1 spatializer (#9) stays the default path.

## 1. Why

Audio v1 (miniaudio's built-in spatializer, #9) gives distance attenuation, stereo panning and Doppler.
That is "the sound is to my left and far away" — good, but not **"the monster is behind you and one room
over."** Steam Audio (Valve, `phonon`, v4.8.1, Apache-2.0) adds the two things a horror game needs:

- **HRTF binaural rendering** — real front/back and above/below discrimination on headphones, via a
  head-related transfer function instead of amplitude panning.
- **Ray-traced occlusion / transmission** — a source behind a wall is physically muffled; opening a
  doorway path restores it, computed against the actual level geometry.

Steam Audio is **not an output engine** — it is a buffer-in / buffer-out DSP library explicitly built for
custom engines. It slots into miniaudio's node graph as a custom `ma_node`, which is exactly the seam the
v1 core (#6) was designed around.

## 2. Scope for v2.6.0 vs. later

| Capability | v2.6.0 | Later |
|---|---|---|
| Vendored SDK + build wiring + graceful fallback | ✅ | |
| Per-voice HRTF binaural node (opt-in) | ✅ | |
| Real-time ray-traced occlusion from collision geometry | ✅ | |
| Off-audio-thread simulation job | ✅ | |
| Per-source + per-project toggles, v1 fallback | ✅ | |
| Transmission through materials (per-material absorption) | partial (single default material) | per-material |
| Baked reflections / reverb (vs. real-time) | | ✅ (stretch) |
| Ambisonic reverb bus | | ✅ |

**Default is OFF.** Enabling Steam Audio is opt-in at two levels (project master switch + per-`AudioSource`
flag), so a shipped game is bit-for-bit the v1 behaviour unless a designer turns it on. This keeps the
v2.6.0 release safe: a new native dependency and a simulation thread cannot regress existing games.

## 3. Where it sits in the node graph

The v1 per-voice chain (from #9 / #15) is:

```
ma_sound (engineNode.spatializer)  ──►  ma_splitter  ──►  bus group (dry)
                                              └────────►  reverb node (wet)
```

Steam Audio replaces the **built-in spatializer** for an opted-in voice with a custom node placed between
the sound and the splitter:

```
ma_sound (spatialization DISABLED)  ──►  [ iplBinauralEffect + occlusion gain ]  ──►  ma_splitter  ──► buses
                                                        ▲
                                          per-voice IPLSource params (direction, occlusion, distance)
```

- When a voice opts in, we call `ma_sound_set_spatialization_enabled(false)` so miniaudio does **not** also
  pan/attenuate it — Steam Audio owns direction. Distance attenuation is applied by Steam Audio's direct
  effect (or kept on miniaudio's min/max model; see §7).
- The node is a standard `ma_node_vtable` with 1 input bus / 1 output bus, `onProcess` calling
  `iplBinauralEffectApply` (and applying the latest occlusion/transmission gain from the simulator).
- The splitter/reverb send downstream is unchanged — the wet send still feeds the freeverb node (#15), so
  algorithmic reverb zones keep working alongside HRTF. (Steam Audio's own reflections are a later item.)

**Format:** the binaural effect wants deinterleaved float. miniaudio nodes run float32; we keep the node at
the engine channel count (stereo out) and mono→binaural in `onProcess`. Steam Audio audio buffers
(`IPLAudioBuffer`) wrap miniaudio's frame pointers with the SDK's deinterleave helpers.

## 4. Coordinate space / handedness

The engine is **left-handed** (+x right, +y up, **+z forward**, DirectX). miniaudio's spatializer is
right-handed, so the listener push in `set_listener` already **negates z** (`AudioEngine.cpp`). Steam Audio
uses a right-handed convention as well (OpenAL/SOFA lineage), so the same z-mirror applies: listener and
source positions/directions fed to `IPLCoordinateSpace3`/`IPLSource` use `(x, y, -z)`, matching the vector
we already hand to miniaudio. Distances are handedness-independent.

The per-voice **direction to the listener** (the binaural input) is computed in game space from the cached
`internal_listener_position()` and the voice position, then mirrored once at the SDK boundary.

## 5. Geometry — occlusion input

Occlusion needs the level's solid geometry. The engine already has it in **`CollisionService`**
(`Editor/Core/Services/Physics/CollisionService.cs`): every mesh collider is stored as a flat world-space
triangle array (`V3[] Tris`, 9 floats per triangle), built from the native mesh via
`VortexAPI.GetModelTriangles` / `GetModelTriangleDataFromMemory`.

Export path:

1. On scene load / collision rebuild, `CollisionService` already resolves each mesh collider's world-space
   triangles. We add a one-shot **geometry publish**: flatten every `Kind.Tris` shape (plus box/sphere/
   capsule approximated as triangle boxes) into one `float[]` vertex array + `int[]` index array.
2. Hand that to the native side once (`SteamAudioSetGeometry(verts, vcount, indices, icount)`), which builds
   an `IPLStaticMesh` under a single `IPLScene` and commits it (`iplSceneCommit`).
3. Static level geometry is built once per scene; there is no per-frame geometry upload. Moving occluders
   are out of scope for v1 (they would need an `IPLInstancedMesh` per mover).

A single default acoustic material (mid absorption/transmission/scattering) is used for all triangles in
v2.6.0; per-material acoustics is a later refinement keyed off the render material.

## 6. Threading — the simulation runs off the audio thread

Ray tracing for occlusion is **too expensive for the audio callback**. Steam Audio separates *simulation*
(rays, thread-safe, slow) from *effect application* (in the audio callback, cheap):

- A dedicated **simulation thread** owns the `IPLSimulator`. Each frame-ish (e.g. 30–60 Hz, decoupled from
  both the render and audio threads) it sets each active `IPLSource`'s inputs (position, listener), runs
  `iplSimulatorRunDirect` (occlusion + transmission rays), and reads back per-source occlusion/transmission
  factors.
- Those factors are published to each voice via **atomics** (a single float occlusion gain + transmission
  gain per voice slot — the same lock-free pattern the meter nodes (#13) use).
- The audio callback's `onProcess` only *reads* the latest published gain and multiplies — no locks, no
  allocation, no rays on the audio thread. Worst case it uses a slightly stale gain (imperceptible at
  30 Hz+ update).
- Source add/remove is double-buffered: the sim thread swaps its active-source list at a safe point.

This satisfies "simulation off the audio thread — no dropouts with 20+ occluded sources": the audio
callback cost is O(voices) multiplies; the ray cost lives on its own thread.

## 7. Per-source settings & fallback

New serialized fields on `AudioSource` (`Editor/ECS/Components/Audio/AudioSource.cs`):

- `EnableHrtf` (bool, default false) — route this voice through the binaural node.
- `EnableOcclusion` (bool, default false) — include this voice in the occlusion simulation.

Project master switch: `AudioSettings.SteamAudioEnabled` (project settings, default false). Steam Audio is
only initialized if the master switch is on **and** `iplContextCreate` succeeds; otherwise every voice uses
the v1 spatializer unchanged.

Fallback ladder (each step degrades safely to the previous):
1. `phonon.dll` missing / `iplContextCreate` fails → Steam Audio disabled globally, v1 for everything.
2. Master switch off → v1 for everything (no SDK init cost).
3. Per-voice `EnableHrtf` false → v1 spatializer for that voice.
4. `EnableOcclusion` false → HRTF direction only, occlusion gain fixed at 1.0.

Distance attenuation: v1's min/max/rolloff model (already tuned + designer-facing via the range-sphere
gizmos, #18) stays authoritative — Steam Audio applies **direction + occlusion**, the miniaudio distance
gain is kept (Steam Audio's own distance model is disabled) so the two never double-attenuate.

## 8. Build & packaging

- SDK vendored at `ThirdParty/steam-audio/` — `include/phonon.h` (+ headers) and
  `lib/windows-x64/phonon.lib` / `phonon.dll` (Windows x64 only; the SDK's other platforms/plugins are not
  vendored). License text kept at `ThirdParty/steam-audio/LICENSE.md`.
- `Engine.vcxproj`: `phonon.h` include dir + `phonon.lib` additional dependency; a post-build step copies
  `phonon.dll` next to the engine outputs (same pattern as Streamline/DLSS).
- Game export: when Steam Audio is enabled for a project, `phonon.dll` ships next to the game exe (it is a
  runtime dependency, not packed into the .vpak).
- `THIRD-PARTY-NOTICES.md`: Apache-2.0 entry with the upstream link and the vendored LICENSE path.

## 9. Native module shape

`Engine/Runtime/Systems/SteamAudio.h/.cpp` (compiled into `Engine.lib`), owned by the audio engine:

```cpp
namespace vortex::runtime::audio::steam {
    bool  initialize(u32 sample_rate, u32 frame_size);   // iplContextCreate + HRTF; false => disabled
    bool  is_available();                                 // master switch AND init ok
    void  shutdown();

    // geometry (called from the C# collision publish, once per scene)
    void  set_geometry(const float* verts, u32 vcount, const int* indices, u32 icount);

    // per-voice lifecycle — returns/takes a steam source handle stored on the voice slot
    ipl_source create_source();
    void       destroy_source(ipl_source);
    ma_node*   binaural_node_for(voice_slot&);           // the ma_node to splice before the splitter

    // listener + per-source params (called each voices_update from game space; mirrored internally)
    void set_listener(f32 px,f32 py,f32 pz, f32 fx,f32 fy,f32 fz, f32 ux,f32 uy,f32 uz);
    void set_source(ipl_source, f32 px,f32 py,f32 pz, bool occlusion);
}
```

The simulation thread lives inside this module; `voices_update` publishes source positions, the sim thread
consumes them, and `onProcess` reads back the atomics.

## 10. Verification plan

- **Build/link:** engine builds and links `phonon.lib`; `phonon.dll` present next to the exe.
- **Init + fallback:** with the SDK present, `initialize` succeeds; deleting `phonon.dll` degrades cleanly to
  v1 with no crash (both logged).
- **Occlusion (measurable):** a looping source + a wall (box collider) between it and the listener — the
  per-process WASAPI session peak drops when occlusion is on vs off; sidestepping the wall restores it.
- **HRTF (auditory):** front/back A/B is inherently a listening test — documented for manual QA; automated
  verification is limited to "the binaural node is in the chain and processes frames" and stable output
  levels.
- **Shipped build:** exported game with Steam Audio enabled boots with `phonon.dll` alongside and does not
  regress v1 audio when disabled.

## 11. References

- Steam Audio C API: https://valvesoftware.github.io/steam-audio/doc/capi/
- Upstream: https://github.com/ValveSoftware/steam-audio (v4.8.1, Apache-2.0)
- Vortex audio v1 design: [Design-Audio-Engine](Design-Audio-Engine.md)
