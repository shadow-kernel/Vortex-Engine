# Vortex Animation System — Design & Horror-Game Readiness

> Status: v1 implemented (skeletal animation core + Keyframe Editor).
> This document is both the design record for the animation ecosystem and the
> feature-gap analysis that decides what must exist before full game production
> (first target: a horror game) can start on Vortex.

---

## Part A — Horror-Game Readiness (feature gap analysis)

Audit of every subsystem (code-verified, not aspirational). Ordered by how hard
each gap blocks a real horror game.

### Blockers (must exist before production)

| # | Gap | Current state | Why it blocks a horror game |
|---|-----|---------------|------------------------------|
| 1 | **Audio** | `AudioSystem.cpp` is a 34-line stub; `AudioSource`/`AudioListener` components are dead data; `Settings.MasterVolume` is stored but never read; no decoder, no device, no script API | Horror is 50% sound: ambience loops, 3D positional cues, stingers, footsteps. Plan: XAudio2 mastering+source voices, WAV/OGG decode, listener follows camera in `update_audio`, `Vortex.Audio` facade, wire AudioSource via scene walk. |
| 2 | **Dynamic entities frozen in shipped games** | The GameHost loop submits the render queue once per scene (`App.xaml.cs` submit-once guard); script-moved entities update their native transform but the drawn matrices are baked. Works in editor play (per-frame resubmit) — silently broken standalone | A monster that cannot visibly move. **FIXED in this iteration**: `SceneRenderService.RuntimeDirty` is set by `Transform.SyncToEngine` during play and by active Animators; `GameHostTick` re-submits on it. |
| 3 | **Skeletal animation** | Zero code anywhere (importer drops `aiMesh::mBones` + `aiScene::mAnimations`; 32-byte rigid vertex; no clips, no editor) | Characters ARE the horror. **THIS document's Part B; implemented.** |
| 4 | **Runtime spawn/destroy from scripts** | No `Instantiate`/`Destroy` in `Vortex.*`; `Vortex.World.Add` is render-only; prefab instantiation exists editor-side only | Jump-scares, pickups, projectiles, spawners all need it. Plan: `Vortex.World.Spawn(prefabPath, pos)` → managed PrefabService instantiate + collision rebuild + render submit; `Destroy(entityId)` symmetric. |
| 5 | **Gameplay raycast / line-of-sight** | Only editor-picking raycaster exists; scripts cannot ask "can the monster see the player?" | Enemy perception, flashlight hit tests, interaction prompts ("press E"). Plan: `Vortex.Physics.Raycast(origin, dir, maxDist)` against the existing `CollisionService` world (it already stores tris/shapes). |
| 6 | **Scripted point/spot lights** | `Vortex.Lighting` exposes ambient+directional only; point/spot come from scene components (re-pushed only on scene submit) | Flashlight that follows the player, flickering bulbs, light-switch gameplay. Plan: per-frame light API (`SetPointLight(id,...)`) or make lights part of the per-frame submit path. |

### High value, not strictly blocking

| # | Gap | Notes |
|---|-----|-------|
| 7 | **Shadows** | `Light` component carries full shadow settings — all dead. No shadow-map pass in the renderer. Horror lighting without shadows is a hard sell; a single-directional PCF shadow map is the 80/20. |
| 8 | **Post-processing / fog** | No fog (not even distance fog), vignette, grain, color grading. The scaled-RT → upscale chain is the natural insertion point. Fog + vignette + grain are horror staples and cheap. |
| 9 | **Particles** | Zero code. Dust motes, breath, sparks, drips. See Part C sketch. |
| 10 | **Save/load + settings persistence** | No PlayerPrefs equivalent; options menu choices don't survive restarts. Small: JSON store under `%AppData%/<game>/`. |
| 11 | **AI navigation** | No navmesh/pathfinding. A grid/waypoint A* over collider bounds is enough for corridor horror v1. |
| 12 | **Script-field serialization** | Public fields of behaviours (`WalkSpeed`) are invisible to the inspector and reset every play. Painful for tuning. |

### Already solid (NOT gaps)

2D UI (immediate + retained .vui + visual builder), capsule collide-and-slide with
trigger/collision events, runtime scene transitions with per-scene asset packs,
custom per-material HLSL, DLSS SR+FG + render scale, controller input incl.
DualSense, script hot-reload, prefab workflow (editor-side), project templates,
auto-update, export/installer pipeline.

**Recommended order to production:** Animation (done, Part B) → Audio →
Spawn/Destroy + Raycast → scripted lights → fog/vignette post pass → shadows →
particles → save/load. After step 4 the first playable horror slice is possible;
steps 5–8 raise it to "looks/sounds like a horror game".

---

## Part B — Skeletal Animation Ecosystem (implemented)

### B.0 Goals

1. Static FBX/GLB imports become **walking, animated characters**.
2. A **Keyframe Editor** with a large 3D preview: bone tree, timeline, keyframe
   authoring, playback — matching the existing editor design language.
3. **Full ecosystem integration**: import pipeline, asset browser, inspector,
   prefabs, scripts (`PlayAnimation("Walk")`), play mode (all three drivers),
   standalone export. No half-wired features.
4. Respect the house rules: *gameplay in scripts, not engine* — the native side
   is a dumb GPU-skinning executor; clip sampling/blending/state logic is managed.

### B.1 Architecture overview

```
FBX/GLB ──Assimp6──► ModelImporter (native)
                     ├─ per-vertex bone indices/weights  ──► 52-byte skinned vertex VB
                     ├─ skeleton (names, parents, inverse-bind, local-bind)
                     └─ animation clips (pos/rot/scale keys per bone)
                                    │ interop (ImporterApi/AnimationApi)
                                    ▼
             Editor/managed:  SkeletonDef + .vanim clips (JSON)
                                    │
   AnimationService (singleton) — samples clips, slerps, composes hierarchy,
   builds bone palette = invBind × boneWorld (row-vector, System.Numerics)
                                    │ SubmitSkinnedMesh(mesh, mat, world, palette)
                                    ▼
             Native renderer: skinned PSO (skinned.hlsl) + bone-palette
             StructuredBuffer (root SRV t5) → GPU skinning
```

**Key decision — managed pose evaluation.** All gameplay, scene walking and
world-matrix math already live in C#; the keyframe editor is C#; scripts are C#.
Evaluating poses managed-side keeps one implementation for editor preview, play
mode and shipped games, and keeps the native layer render-only. Palette upload
is a bulk float array per character per frame (≤ 256 × 64 B = 16 KB), far below
the proven `SubmitMeshInstances` throughput. A native fast path can come later
for crowds.

**Key decision — interleaved 52-byte skinned vertex, single VB.**
`pos(12) + normal(12) + uv(8) + boneIndices(4×u8) + weights(4×f32)` = 52 bytes.
`Mesh::create_from_vertices` already takes an arbitrary stride, and the rigid
input layout only reads offsets 0/12/24 — so a skinned mesh can still be drawn
by every existing rigid path (previews, thumbnails, custom shaders) with zero
changes; the skinned PSO additionally reads offsets 32/36. No second vertex
stream, no new Mesh API.

**Key decision — clips are `.vanim` JSON keyed by bone *name*.**
Clips are standalone assets (System.Text.Json, camelCase — the `.vui` family),
generated from FBX at import (`animations/<clip>.vanim` beside `materials/`,
mirroring the per-submesh `.vmat` pattern) and creatable from scratch in the
Keyframe Editor. Name-keyed tracks mean a clip survives mesh re-import and can
be shared between models with compatible skeletons. Packaging/VFS loading is
free (everything under `Assets/` ships in the vpak).

**Key decision — no animator-controller graph in v1.** State machines are game
logic → they live in scripts (`PlayAnimation`/`CrossFade` from `VortexBehaviour`).
A visual state-machine editor can layer on later without changing the data model.

### B.2 Native layer (Engine + VortexAPI)

| Piece | Where | What |
|---|---|---|
| Skinned vertex | `IMeshGenerator.h` | `SkinnedVertex { float3 pos; float3 normal; float2 uv; u8 idx[4]; float w[4]; }` (52 B) |
| Bone read | `ModelImporter_Process.cpp` | node-hierarchy pass (fixes the existing "transforms dropped" flattening), `aiMesh::mBones` → 4 influences (LimitBoneWeights), renormalized |
| Skeleton/clips | `ModelImporter.h/.cpp` | `SkeletonData { name, parent, offset(invBind), localBind }[]`, `AnimationClipData { name, duration, tps, channels[bone→pos/rot/scale keys] }[]` on `ImportedModelData` |
| Mesh upload | `ResourceRegistry_Lod.cpp` | skinned submeshes upload 52-B vertices; **skip LOD-chain registration** (decimator can't carry weights) |
| Shader | `Engine/Shaders/skinned.hlsl` | clone of standard VS + `StructuredBuffer<float4x4> Bones : register(t5)`; 4-influence skin of position+normal before the instance-world multiply; same PS as standard |
| Root signature | `DX12Pipeline3D.cpp` | param 8 = root SRV (t5, vertex visibility) — appended, params 0–7 untouched so every existing bind is stable |
| Skinned PSO | `DX12Pipeline3D` | rigid layout + `BLENDINDICES R8G8B8A8_UINT @32` + `BLENDWEIGHT R32G32B32A32_FLOAT @36`, slot 0; INSTANCEWORLD slot 1 kept |
| Palette buffer | `DX12Renderer_Init.cpp` | persistently-mapped UPLOAD `float4x4[65536]` (4 MB), linear cursor reset per submit batch |
| Submission | `DX12Renderer_Queue.cpp` | `submit_skinned_item(mesh, mat, world, bones, count)` → copies palette, `RenderItem.bone_offset/bone_count` |
| Draw | `DX12Renderer_3DScene.cpp` | skinned items drawn as their own runs (skinned PSO + `SetGraphicsRootShaderResourceView(8, paletteVA + offset·64)`), frustum-cull with inflated sphere; mirrored in `render_scene_to_target` so **editor previews animate** |
| Interop | `VortexAPI/Api/AnimationApi.cpp` | `GetModelBoneCount/GetModelBones[FromMemory]`, `GetModelAnimationCount/Names/GetModelAnimationChannel*` (two-call size-query idiom), `SubmitSkinnedMeshForRendering` |

Known accepted limitation (documented): DLSS motion vectors are still
camera-reprojection only → fast-moving skinned characters can ghost under
SR/FG until a per-object motion-vector pass exists.

### B.3 Managed layer (Editor)

| Piece | Where | What |
|---|---|---|
| `.vanim` DTO | `Editor/Core/Animation/VortexAnimClip.cs` | `{ vanim:1, name, model, durationSec, frameRate, loop, tracks:[{bone, pos:[{t,x,y,z}], rot:[{t,x,y,z,w}], scale:[...]}], events:[{t,name}] }`; VFS-aware `Load` |
| Skeleton model | `Editor/Core/Animation/SkeletonDef.cs` | bones (name/parent/invBind/localBind) fetched via interop, path-keyed cache |
| Evaluation | `Editor/Core/Animation/AnimationService.cs` | clip cache; per-Animator state (clip, time, speed, loop, crossfade pair); `Step(dt)`; sampling = binary-search keys, `System.Numerics` lerp/slerp; palette = `invBind × boneWorld` (row-vector, matches DirectXMath); bind-pose palette for edit mode; animation **events** fired into scripts |
| Component | `Editor/ECS/Components/Animation/Animator.cs` | `[DataContract]`: clip table (name→path), `DefaultClip`, `PlayOnStart`, `Speed`, `Loop`; registered in `GameEntity` `[KnownType]` + `DataSerializer.KnownTypes` (scene/prefab serialization) |
| Inspector | `DynamicInspectorView` factory + programmatic `AnimatorInspector` | clip list add/remove, `.vanim` picker + drag&drop, open Keyframe Editor button |
| Submission | `SceneRenderService.SubmitEntity` | entities with Animator+skinned mesh route through `SubmitSkinnedMesh` with the service's palette; `RuntimeDirty` flag makes GameHostTick re-submit (also fixes rigid script-motion standalone) |
| Script API | `VortexScriptApi.cs` + `ScriptingService.ApiTemplate()` | `VortexBehaviour.PlayAnimation(name, fade)`, `StopAnimation`, `SetAnimationSpeed`, `IsAnimationPlaying`, + `Vortex.Animation` facade; wired through `IScriptHost` like Physics/Lighting |
| Import | `ModelImportService` | writes `animations/<clip>.vanim` per FBX clip at import; skeleton presence flagged in import result dialog |
| Asset UX | `AssetBrowserView` etc. | `.vanim` icon/type/tab classification, create-menu "Animation Clip", double-click → Keyframe Editor, `AssetType.Animation`, `FileExtensions.Animation` |

Tick topology: `AnimationService.Step(dt)` runs inside `ScriptRuntime.Update`
(after behaviours, so a same-frame `PlayAnimation` takes effect) — one call site
that all three play drivers (editor viewport, external window, native GameHost)
already funnel through. The Keyframe Editor has its own playback clock.

### B.4 Keyframe Editor (`Editor/Editors/AnimationEditor/`)

Programmatic WPF window (house pattern — no XAML registration), 1500×940,
design tokens from `DialogStyles`. Layout:

```
┌ toolbar: clip name · duration · FPS · snap ·  |◀ ◀ ▶/⏸ ▶|  · loop · time ─┐
├──────────────┬──────────────────────────────────────────┬────────────────┤
│ Model + bone │                                          │ Key inspector  │
│ hierarchy    │            BIG 3D PREVIEW                │ (selected bone │
│ tree; clip   │   (orbit/zoom, skinned pose at playhead, │ pos/rot/scale  │
│ list         │    bone overlay, click-select bones)     │ at time, add/  │
│ 300px        │                                          │ del key) 330px │
├──────────────┴──────────────────────────────────────────┴────────────────┤
│ TIMELINE: ruler · per-bone tracks · keyframe diamonds (drag/select/del)  │
│ playhead scrub · wheel zoom · snap-to-frame                     ~240px   │
└──────────────────────────────────────────────────────────────────────────┘
```

- **Preview** = `AnimationPreviewControl` (clone of `CollisionPreviewControl`):
  offscreen render via a new `AssetPreviewRenderer.RenderSkinnedMeshes(...)`
  that accepts a bone palette; bone links drawn on the managed Canvas overlay
  using the same projected-camera math; playback via `CompositionTarget.Rendering`
  gated on a playing flag; follows the `ActivePreviewDialogs`/`RequestResubmit`/
  `DestroyPreviewTarget` lifecycle contract.
- **TimelineControl**: raw Canvas + shapes (house idiom), ruler with adaptive
  tick density, track rows for bones that have keys (+ the selected bone),
  diamond keys with drag-move/click-select/Del, double-click empty = add key,
  playhead drag = scrub, Ctrl+wheel zoom.
- **Authoring model**: select bone → edit local pos/rot/scale numerically (or
  nudge buttons) at the playhead → "Key" button writes keys; "Key Pose" keys
  all posed bones. Every mutation goes through `UndoRedoManager` (global
  Ctrl+Z works; scrub-drags coalesce via the 500 ms merge window).
- **Sources**: opens `.vanim` files directly, or "extract" view for FBX-embedded
  clips (converted to `.vanim` on save).

### B.5 Explicit non-goals for v1 (planned follow-ups)

- Animator-controller state-machine *graph editor* (scripts cover it).
- Per-object/per-bone DLSS motion vectors (characters may ghost under FG).
- Weight-aware LOD decimation for skinned meshes (LOD skipped instead).
- Viewport rotate-gizmo bone manipulation in the preview (numeric v1).
- Morph targets / blend shapes; root motion extraction; IK.

---

## Part C — Particle system (design sketch, next feature)

Same architectural split as animation: **managed simulation, native instanced
rendering** — particles are just `SubmitMeshInstances` of camera-facing quads.

- `ParticleEmitter` component (`[DataContract]`): shape (point/sphere/cone/box),
  rate, lifetime, start speed/size/rotation/color (+ random ranges), gravity,
  drag, color-over-life + size-over-life (2-key gradients v1), texture path,
  additive/alpha blend, max particles, world/local space, looping/burst.
- `ParticleService` (peer of AnimationService): per-emitter ring buffers,
  `Step(dt)` in the same tick, builds world matrices (camera-facing billboard)
  → one `SubmitMeshInstances` call per emitter (quad mesh + unlit material).
  63k instances at 240 FPS is already proven; thousands of particles are cheap.
- Native additions: an unlit-additive PSO variant + soft-particle depth fade
  (later). v1 ships with the existing unlit path.
- `.vfx` JSON asset + a small emitter editor window later; inspector-only first.

---

## Part D — Prefab system: intended usage (answer to "why prefabs?")

A model file is *geometry only*. A prefab (`.ventity`) is a **configured
entity subtree**: model + materials + colliders + scripts + Animator + children
(lights, trigger volumes, audio sources), saved as one reusable asset.

Concrete horror-game examples:
- **Door_Creaky.ventity** = door mesh + hinge script + interaction trigger
  collider + (future) creak AudioSource. Place 40 of them; tweak the creak
  radius once → *Apply to Prefab* → all 40 update (`ApplyToPrefab` re-stamps
  every instance in the scene, keeping their transforms).
- **Monster.ventity** = skinned mesh + Animator (Idle/Walk/Attack clips) +
  capsule collider + `MonsterAI.cs`. The upcoming `Vortex.World.Spawn` API
  (gap #4) makes prefabs the unit of **runtime spawning** — you can't spawn a
  "model + 5 hand-wired components", only a prefab.
- **Flashlight pickup**, **save point**, **note/lore pickup** — anything that
  appears more than once with the same wiring.

Rule of thumb: *drag a model into the scene when it's dumb scenery; make it a
prefab the moment it has behaviour or repeats.* Without prefabs every copy is
hand-assembled and edits don't propagate.
