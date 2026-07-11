# Entities & Components (ECS) Reference

This page documents the **managed entity–component model** in the `Editor.ECS` namespace tree — the objects a scene is built from, that the Inspector edits, that serialize into `.ventity`/scene files, and that your [scripts](Scripting-Getting-Started) attach to. It is a **pure editor-side data model**: the real per-frame simulation runs in the native C++ engine (see [[Native-DLL-API]]); these C# types are view-models that mirror their state into the engine via [`Editor.DllWrapper.VortexAPI`](Managed-Interop-Bindings).

> **Sources:** `Editor/ECS/GameEntity.cs`, `Editor/ECS/Component.cs`, `Editor/ECS/Math/VectorTypes.cs`, `Editor/ECS/Components/**`.

**Contents:** [Serialization model](#serialization-model) · [`GameEntity`](#gameentity) · [`Component` (base)](#component-base-class) · [Math types](#math-types) · Components: [Transform](#transform) · [MeshRenderer](#meshrenderer) · [SpriteRenderer](#spriterenderer) · [Camera](#camera) · [Skybox](#skybox) · [Light](#light) · [Rigidbody](#rigidbody) · [Collider](#collider-and-subclasses) · [AudioSource / AudioListener](#audiosource) · [ReverbZone](#reverbzone) · [Animator](#animator) · [Script](#script)

---

## Serialization model

The model uses `System.Runtime.Serialization` **DataContract** serialization:

- `[DataContract(Name=…)]` marks a serializable type; the `Name` is the on-disk contract name.
- `[DataMember(Name=…, Order=…)]` marks a persisted member — `Name` is the **on-disk key** (not the C# property name), `Order` fixes ordering.
- `[IgnoreDataMember]` excludes a member (runtime-only, e.g. native handles).

Entities/prefabs are saved as **`.ventity`** files; scenes hold entity trees. `GameEntity` carries a long `[KnownType(...)]` whitelist naming every concrete component type the serializer may encounter in its `Components` collection.

**Conventions that hold across all components:**

- Every component derives from [`Component`](#component-base-class) and inherits `Id` (`id`, Order 0) and `IsEnabled` (`isEnabled`, Order 1), plus the runtime `Entity` back-reference — not re-listed per component below.
- Base-class members use `Order` 0–1; derived members typically start at `Order 10`.
- Native handles (`EntityId`, `*Handle`, `EngineCameraId`, …) are `[IgnoreDataMember]` and use the `ID.INVALID_ID` sentinel.

---

## `GameEntity`

**Namespace:** `Editor.ECS` · **Base:** `Core.ViewModelBase` · implements `IEngineEntity` · `[DataContract(Name="GameEntity")]`

A scene object: a container of components arranged in a parent/child hierarchy. Every entity has exactly one [`Transform`](#transform).

### Serialized properties

| Property | Type | Key / Order | Description |
|---|---|---|---|
| `Id` | `Guid` | `id` / 0 | Stable unique id. |
| `Name` | `string` | `name` / 1 | Display name. |
| `IsActive` | `bool` | `isActive` / 2 | Active flag; setter triggers `SyncEngineStateRecursive` (engine registration) unless deserializing. Default `true`. |
| `IsStatic` | `bool` | `isStatic` / 3 | Marks the entity static. |
| `Layer` | `int` | `layer` / 4 | Layer index. |
| `Tag` | `string` | `tag` / 5 | Tag string (default `"Untagged"`). Read in scripts via `TriggerHit.Tag`. |
| `Children` | `ObservableCollection<GameEntity>` | `children` / 6 | Child entities. |
| `Components` | `ObservableCollection<Component>` | `components` / 7 | The component list. |
| `PrefabPath` | `string` | `prefabPath` / 8 | If a prefab instance, the project-relative `.ventity` path; null/empty otherwise. |
| `IsFolder` | `bool` | `isFolder` / 8 | Organizational folder only (no components). |
| `IsLockedToParent` | `bool` | `isLockedToParent` / 9 | Can't be individually selected/moved (e.g. submeshes of an imported model). |

### Runtime / UI properties (`[IgnoreDataMember]`)

| Property | Type | Description |
|---|---|---|
| `EntityId` | `long` | Runtime engine-side entity handle (`ID.INVALID_ID` until registered). |
| `IsPrefabInstance` | `bool` (get) | True when `PrefabPath` is non-empty. |
| `Transform` | `Transform` (get) | The entity's Transform (cached). |
| `Parent` | `GameEntity` | Parent entity. |
| `Scene` | `Core.Data.Scene` | Owning scene. |
| `IsExpanded` / `IsSelected` | `bool` | Hierarchy UI state. |
| `ActiveInHierarchy` | `bool` (get) | Effective active state, accounting for the scene and all ancestors. |

### Constructors

- `GameEntity()` — new `Guid`, empty collections.
- `GameEntity(string name)` — sets `Name` **and adds a default `Transform`** (every entity gets one).
- `GameEntity(Core.Data.Scene scene, string name)` — also sets `Scene`.

### Component management

| Method | Description |
|---|---|
| `void AddComponent(Component component)` | Sets `component.Entity = this` and adds it (undoable). |
| `T AddComponent<T>() where T : Component, new()` | Create, attach and return a new `T` (undoable). |
| `void RemoveComponent(Component component)` | Remove (undoable). **Transform cannot be removed** (silently ignored). |
| `T GetComponent<T>() where T : Component` | First component of type `T`, or `null`. |
| `T[] GetComponents<T>() where T : Component` | All components of type `T`. |
| `bool HasComponent<T>() where T : Component` | Whether a `T` exists. |
| `internal void AddComponentDirect(Component)` | Add without undo (used internally). |

### Hierarchy management

| Method | Description |
|---|---|
| `void AddChild(GameEntity child)` / `void RemoveChild(GameEntity child)` | Add/remove a child (undoable). |
| `void SetParent(GameEntity parent)` | Reparent, inheriting the parent's `Scene`. |
| `GameEntity Find(string name)` | Recursively find a descendant by name. |
| `string GetPath()` | Full slash-delimited path from the root (e.g. `/Root/Child`). |

### Lifecycle / serialization

| Method | Description |
|---|---|
| `void RegenerateIds()` | New `Guid`s for the entity + all components + children (copy/paste). |
| `internal void SyncEngineStateRecursive(bool parentActive = true)` | Create/remove the engine-side entity by active state and sync MeshRenderers; recurses into children. |
| `[OnDeserialized]` handler | Re-links children's `Parent`/`Scene`, restores each component's `Entity` back-reference, re-caches the Transform. |

---

## `Component` (base class)

**Namespace:** `Editor.ECS` · **Base:** `Core.ViewModelBase` · implements `IEngineComponent` · `[DataContract(Name="Component")]`

Abstract base for all components — **data only**, no update/start lifecycle (that lives in the C++ engine and in [`Script`](#script) behaviours). Engine sync is per-component (e.g. `Transform.SyncToEngine`, `MeshRenderer.SyncToEngine`).

| Member | Type | Key / Order | Description |
|---|---|---|---|
| `Id` | `Guid` | `id` / 0 | Unique component id. |
| `IsEnabled` | `bool` | `isEnabled` / 1 | Enabled flag (default `true`). |
| `Entity` | `GameEntity` | `[IgnoreDataMember]` | Owning-entity back-reference (set on add, re-linked on deserialize). |
| `DisplayName` | `string` (abstract, get) | `[IgnoreDataMember]` | Inspector name (overridden per component). |
| `IconCode` | `string` (abstract, get) | `[IgnoreDataMember]` | Segoe MDL2 glyph. |
| `IconColor` | `string` (virtual, get) | `[IgnoreDataMember]` | Icon color hex (base default `#C5C5C5`). |

Protected constructors `Component()` / `Component(GameEntity)`; `void RegenerateId()` assigns a fresh `Guid`.

---

## Math types

Both structs live in `Editor.ECS` (`Math/VectorTypes.cs`) and are `[DataContract]`. Note: this is the **editor-side** `Vector3` — distinct from the [scripting `Vortex.Vector3`](Scripting-API-Reference#vector3) (the runtime converts between them).

### `Vector3`

Fields `X` (`x`/0), `Y` (`y`/1), `Z` (`z`/2). Statics: `Zero, One, Up, Down, Forward, Back, Right, Left`. Ctors `Vector3(x,y,z)`, `Vector3(value)`. Members: `Magnitude`, `SqrMagnitude`, `Normalized`. Operators `+ - * / == !=` and unary `-`. Statics: `Dot`, `Cross`, `Distance`, `Lerp(a,b,t)` (t clamped).

### `Quaternion`

Fields `X Y Z W`. `Identity`. Ctor `Quaternion(x,y,z,w)`. Statics `Euler(x,y,z)` / `Euler(Vector3)` (from Euler degrees). Member `EulerAngles` (get). Operator `*`.

---

## Components

Below, only members **beyond** the inherited `Id`/`IsEnabled` are listed. "DN/Icon" gives the Inspector `DisplayName` and glyph color.

### Transform

**`Editor.ECS.Components`** · DN `"Transform"`, teal. **Every entity has exactly one; it cannot be removed.**

| Property | Type | Key / Order | Description |
|---|---|---|---|
| `LocalPosition` | `Vector3` | `localPosition` / 10 | Local position; setter calls `SyncToEngine()`. |
| `LocalRotation` | `Vector3` | `localRotation` / 11 | Local rotation (Euler **degrees**); setter syncs. |
| `LocalScale` | `Vector3` | `localScale` / 12 | Local scale (default `One`); setter syncs. |
| `LocalEulerAngles` | `Vector3` | `[IgnoreDataMember]` | Inspector alias for local rotation. |

Methods: `internal void SyncToEngine()` (push to the engine entity; sets `SceneRenderService.RuntimeDirty`), `void SetLocalPositionFromEngine(Vector3)` (display-only update while physics owns the transform during play), `void Reset()`.

### MeshRenderer

**`Editor.ECS.Components.Rendering`** · DN `"Mesh Renderer"`, teal. Renders a mesh with a material.

| Property | Type | Key / Order | Description |
|---|---|---|---|
| `MeshPath` | `string` | `meshPath` / 10 | Mesh file; setter reloads handle + syncs. |
| `MaterialPath` | `string` | `materialPath` / 11 | Material file; setter reloads + syncs. |
| `CastShadows` | `bool` | `castShadows` / 12 | Default `true`. |
| `ReceiveShadows` | `bool` | `receiveShadows` / 13 | Default `true`. |
| `RenderLayer` | `int` | `renderLayer` / 14 | `0` world (all cameras), `1` first-person viewmodel (FP overlay pass: own FOV, cleared depth, no shadow cast; hidden in the editor build view — FP toolbar toggle shows it for placement, "FP Preview (In-Game)" view mode shows the real game frame), `2` third-person only (visible in the editor, hidden for the local player while playing). |
| `ColorR/G/B/A` | `float` | `colorR..colorA` / 15–18 | Base color (RGB default `0.7`, A `1`). |
| `Metallic` | `float` | `metallic` / 19 | PBR metallic (default `0`). |
| `Roughness` | `float` | `roughness` / 20 | PBR roughness (default `0.5`). |
| `NormalStrength` | `float` | `normalStrength` / 21 | 0–2 (default `1`). |
| `TexturePath` | `string` | `texturePath` / 22 | Albedo texture (persisted for reload). |
| `ShaderPath` | `string` | `shaderPath` / 23 | Custom `.hlsl`. |
| `MaterialHandle` | `long` | `[IgnoreDataMember]` | Native material handle (imported models). |
| `HasImportedMaterial` | `bool` (get) | `[IgnoreDataMember]` | True if a valid preloaded material handle exists. |

Methods: `internal void SyncToEngine()`, `internal void RemoveFromEngine()`.

### SpriteRenderer

**`Editor.ECS.Components.Rendering`** · DN `"Sprite Renderer"`, purple. 2D sprite.

`SpritePath` (`spritePath`/10) · `ColorR/G/B/A` (`colorR..colorA`/11–14, default `1`) · `SortingOrder` (`sortingOrder`/15) · `FlipX` (`flipX`/16) · `FlipY` (`flipY`/17).

### Camera

**`Editor.ECS.Components.Rendering`** · DN `"Camera"`, purple if main else blue.

**Enums:** `CameraProjection { Perspective, Orthographic }` · `CameraClearFlags { Skybox, SolidColor, DepthOnly, Nothing }` · `CameraType { GameCamera=0, MainCamera=1, EditorCamera=2 }`.

| Property | Type | Key / Order | Default |
|---|---|---|---|
| `Projection` | `CameraProjection` | `projection` / 10 | `Perspective` |
| `ClearFlags` | `CameraClearFlags` | `clearFlags` / 11 | `Skybox` |
| `FieldOfView` | `float` | `fov` / 12 | `60` |
| `OrthographicSize` | `float` | `orthoSize` / 13 | `5` |
| `NearClip` / `FarClip` | `float` | `nearClip` / 14, `farClip` / 15 | `0.1` / `1000` |
| `IsMainCamera` | `bool` | `isMainCamera` / 16 | |
| `Depth` | `int` | `depth` / 17 | lower renders first |
| `BackgroundR/G/B` | `float` | `bgR/bgG/bgB` / 18–20 | `0/0/0.3` |
| `CullingMask` | `int` | `cullingMask` / 21 | `-1` (all) |
| `CameraType` | `CameraType` | `cameraType` / 22 | `GameCamera` |
| `EngineCameraId` | `long` | `[IgnoreDataMember]` | `-1` |

### Skybox

**`Editor.ECS.Components.Rendering`** · DN `"Skybox"`, sky-blue. Environment lighting + background (IBL-style ambient). *(Not in the `[KnownType]` whitelist.)*

**Enum:** `SkyboxType { SolidColor, Gradient, Cubemap, Texture }`.

`SkyboxType` (/10, default `Gradient`) · `AmbientIntensity` (/11, 0–2, default `0.8`) · `IsEnabled` (/12) · `TopColorR/G/B` (/20–22, `0.7/0.8/1.0`) · `BottomColorR/G/B` (/30–32, `0.3/0.3/0.4`) · `HorizonColorR/G/B` (/40–42, `0.8/0.85/0.95`) · `Exposure` (/50, 0.1–4, default `1`) · `CubemapPath` (/60) · `TexturePath` (/61) · `SkyboxMeshPath` (/62). Method `(float r,g,b) GetAmbientColor()`.

### Light

**`Editor.ECS.Components.Lighting`** · DN `"{LightType} Light"`, gold.

**Enums:** `LightType { Directional, Point, Spot, Area }` · `ShadowType { None, Hard, Soft }`.

| Property | Type | Key / Order | Default |
|---|---|---|---|
| `LightType` | `LightType` | `lightType` / 10 | `Directional` |
| `ShadowType` | `ShadowType` | `shadowType` / 11 | `Soft` |
| `Intensity` | `float` | `intensity` / 12 | `2.5` |
| `Range` | `float` | `range` / 13 | `10` |
| `SpotAngle` / `InnerSpotAngle` | `float` | `spotAngle` / 14, `innerSpotAngle` / 15 | `30` / `21` |
| `ColorR/G/B` | `float` | `colorR/G/B` / 16–18 | `1 / 0.956 / 0.839` |
| `ShadowStrength` | `float` | `shadowStrength` / 19 | `1` |
| `ShadowBias` / `ShadowNormalBias` | `float` | `shadowBias` / 20, `shadowNormalBias` / 21 | `0.05` / `0.4` |
| `ShadowResolution` | `int` | `shadowResolution` / 22 | `2048` |
| `CullingMask` | `int` | `cullingMask` / 23 | `-1` |
| `IsEnabled` | `bool` | `isEnabled` / 24 | `true` |

Ctor `Light(GameEntity, LightType)` applies type-specific defaults.

### Rigidbody

**`Editor.ECS.Components.Physics`** · DN `"Rigidbody"`, tan.

**Enums:** `RigidbodyType { Dynamic, Kinematic, Static }` · `RigidbodyInterpolation { None, Interpolate, Extrapolate }` · `CollisionDetectionMode { Discrete, Continuous, ContinuousDynamic, ContinuousSpeculative }`.

`Mass` (/10, `1`) · `Drag` (/11, `0`) · `AngularDrag` (/12, `0.05`) · `UseGravity` (/13, `true`) · `BodyType` (/14, `Dynamic`) · `Interpolation` (/15, `None`) · `CollisionDetection` (/16, `Discrete`) · `FreezePositionX/Y/Z` (/20–22) · `FreezeRotationX/Y/Z` (/23–25).

### Collider (and subclasses)

**`Editor.ECS.Components.Physics`** · DN `"{ColliderType} Collider"`, green.

**Enum:** `ColliderType { Box, Sphere, Capsule, Mesh, Convex }`.

**`PhysicsMaterial`** (plain `[DataContract]`, not a component): `Friction` (`0.5`), `Bounciness` (`0`), `FrictionCombine` (`0`), `BounceCombine` (`0`).

**`Collider` base:** `ColliderType` (`colliderType`/10, default `Box`) · `IsTrigger` (`isTrigger`/11) · `Center` (`center`/12) · `Material` (`material`/13, a `PhysicsMaterial`).

Subclasses add a shape member (Order 20+):

| Subclass | Contract | Extra members |
|---|---|---|
| `BoxCollider` | `BoxCollider` | `Size : Vector3` (`size`/20, default `One`) |
| `SphereCollider` | `SphereCollider` | `Radius : float` (`radius`/20, default `0.5`) |
| `CapsuleCollider` | `CapsuleCollider` | `Radius`(/20,`0.5`), `Height`(/21,`2`), `Direction:int`(/22, 0=X 1=Y 2=Z, default `1`) |
| `MeshCollider` | `MeshCollider` | `MeshPath:string`(/20), `Convex:bool`(/21) |

> Mark a collider `IsTrigger` to receive [`OnTriggerEnter/Stay/Exit`](Scripting-API-Reference#vortexbehaviour); leave it solid for `OnCollisionEnter`. Colliders feed the runtime `Physics.MoveCharacter` collide-and-slide.

### AudioSource

**`Editor.ECS.Components.Audio`** · DN `"Audio Source"`, tan. Access from scripts via [`VortexBehaviour.GetAudioSource()`](Scripting-API-Reference#audiosource).

**Enum:** `AudioRolloffMode { Logarithmic, Linear, Custom }`.

| Property | Type | Key / Order | Default |
|---|---|---|---|
| `AudioClipPath` | `string` | `audioClipPath` / 10 | |
| `Volume` / `Pitch` | `float` | `volume` / 11, `pitch` / 12 | `1` / `1` |
| `Loop` | `bool` | `loop` / 13 | `false` |
| `PlayOnAwake` | `bool` | `playOnAwake` / 14 | `true` |
| `Mute` | `bool` | `mute` / 15 | `false` |
| `SpatialBlend` | `float` | `spatialBlend` / 16 | `0` (0=2D, 1=3D) |
| `MinDistance` / `MaxDistance` | `float` | `minDistance` / 17, `maxDistance` / 18 | `1` / `500` |
| `RolloffMode` | `AudioRolloffMode` | `rolloffMode` / 19 | `Logarithmic` |
| `Priority` | `int` | `priority` / 20 | `128` (0 highest) |
| `StereoPan` | `float` | `stereoPan` / 21 | `0` |
| `ReverbZoneMix` | `float` | `reverbZoneMix` / 22 | `1` |
| `DopplerLevel` | `float` | `dopplerLevel` / 23 | `1` |
| `Spread` | `float` | `spread` / 24 | `0` |
| `Streaming` | `bool` | `streaming` / 25 | `false` (stream long clips) |
| `OutputBus` | `int` | `outputBus` / 26 | `2` (SFX; 0 Master,1 Music,2 SFX,3 Ambience,4 UI) |
| `EnableHrtf` | `bool` | `enableHrtf` / 27 | `false` (Steam Audio binaural) |
| `EnableOcclusion` | `bool` | `enableOcclusion` / 28 | `false` (requires HRTF) |

**`AudioListener`** (same file): no serialized members; place on the main camera. DN `"Audio Listener"`.

### ReverbZone

**`Editor.ECS.Components.Audio`** · DN `"Reverb Zone"`, tan. While the listener is inside, global reverb takes this zone's character; the boundary blends over `Falloff`.

**Enum:** `ReverbZoneShape { Sphere, Box }` (note: `Shape` is stored as `int`).

`Shape` (`shape`/10, `0`=Sphere) · `Radius` (/11, `10`) · `BoxExtents:Vector3` (/12, `(10,5,10)`) · `Falloff` (/13, `3`) · `DecayTime` (/14, `1.8` — 0.1 dry … 20 cathedral) · `WetLevel` (/15, `0.6`) · `PreDelayMs` (/16, `20`).

### Animator

**`Editor.ECS.Components.Animation`** · DN `"Animator"`, purple. A named clip table + playback defaults. Play clips from scripts via [`PlayAnimation`](Scripting-API-Reference#vortexbehaviour) / [`Animation.Play`](Scripting-API-Reference#animation); state machines live in scripts.

**`AnimatorClipEntry`** (`[DataContract(Name="AnimatorClip")]`, plain data): `Name` (`name`/0) · `Path` (`path`/1, project-relative `.vanim`).

**`Animator`:** `Clips : List<AnimatorClipEntry>` (`clips`/10) · `DefaultClip : string` (`defaultClip`/11) · `PlayOnStart : bool` (`playOnStart`/12, default `true`) · `Speed : float` (`speed`/13, default `1`). Method `string ResolveClipPath(string nameOrPath)` — resolves a clip-table name to its `.vanim` path (a direct `.vanim` path passes through).

### TwoBoneIk

**`Editor.ECS.Components.Animation`** · DN `"Two-Bone IK"`, purple (#179). Runtime two-bone IK on the entity that carries the Animator: pulls a 3-joint limb (chain derived from the tip: mid = parent, root = grandparent) so the tip bone reaches a target rigidly coupled to ANOTHER bone of the same skeleton — the "support hand grips the weapon" setup. Solved in model space inside the palette evaluation (after clips/layers/bone overrides, before skinning), so submeshes, bone sockets and bone queries all see the IK'd pose. The editor viewport previews the IK'd pose live while tuning (bind pose + IK).

`TipBone : string` (`tipBone`/10, e.g. `mixamorig:LeftHand`) · `TargetBone : string` (`targetBone`/11, e.g. `mixamorig:RightHand`) · `TargetOffsetPosition : Vector3` (`targetOffsetPosition`/12 — grip position in the target bone's local frame, MODEL units; author via the inspector's **Capture From Current Pose**) · `TargetOffsetRotation : Vector3` (`targetOffsetRotation`/13, engine-ZXY Euler degrees) · `Weight : float` (`weight`/14, 0–1, default `1`; blend at runtime via `Animation.SetIkWeight(entity, tipBone, w)`) · `PoleAngle : float` (`poleAngle`/15, degrees around the root→target axis; `0` keeps the animation's bend plane) · `ApplyTipRotation : bool` (`applyTipRotation`/16, default `true` — orient the wrist to the grip).

### Script

**`Editor.ECS.Components.Scripting`** · DN = the `ScriptClassName` (or `"Script"`), yellow. Binds a [`VortexBehaviour`](Scripting-API-Reference#vortexbehaviour) class to this entity.

`ScriptPath : string` (`scriptPath`/10) · `ScriptClassName : string` (`scriptClassName`/11 — the class name the runtime resolves and instantiates) · `IsCompiled : bool` (`[IgnoreDataMember]`). Ctor `Script(GameEntity, string scriptPath)` derives `ScriptClassName` from the file name.

---

## How the model reaches the engine

1. You edit a component in the Inspector → its setter calls `SyncToEngine()` (Transform, MeshRenderer) or the entity's `SyncEngineStateRecursive`.
2. Those call into [`Editor.DllWrapper.VortexAPI`](Managed-Interop-Bindings) (P/Invoke) …
3. … which crosses into the native [`VortexAPI.dll`](Native-DLL-API) C ABI …
4. … which drives the C++ engine's renderer/physics/audio systems.

At **Play**, [`ScriptRuntime`](Scripting-Getting-Started#how-scripts-compile--run) walks the entity tree, instantiates a behaviour for each `Script` component, and drives the whole thing frame by frame.

## See also

- [[Scripting-Getting-Started]] · [[Scripting-API-Reference]] — the gameplay layer on top of this model.
- [[Managed-Interop-Bindings]] · [[Native-DLL-API]] — the layers underneath.
