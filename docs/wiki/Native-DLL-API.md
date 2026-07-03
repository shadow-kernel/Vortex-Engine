# Native DLL API — `VortexAPI.dll` C ABI Reference

This is the complete **exported C ABI** of the native engine DLL — the flat `extern "C"` surface that the C# editor calls through P/Invoke, and the boundary a shipped game crosses to reach the C++ engine. If you are writing gameplay, you almost never call these directly — you use the [scripting API](Scripting-API-Reference); the [managed bindings](Managed-Interop-Bindings) wrap every function here in a friendlier C# method. This page documents the raw surface for completeness and for anyone binding the DLL from another language.

> **Sources:** `VortexAPI/ApiCommon.h`, `VortexAPI/DllMain.cpp`, `VortexAPI/Api/*.cpp`, `VortexAPI/MultiViewportAPI.cpp`. The engine core it drives lives in `Engine/` (see [[Architecture]]).

**Contents:** [Export macro & ABI](#export-macro--calling-convention) · [Boundary structs](#structs-crossing-the-boundary) · [Scene](#scene) · [Entity](#entity) · [Render](#render) · [RenderLoop](#renderloop) · [Camera](#camera) · [Lighting](#lighting) · [Skybox](#skybox) · [Audio](#audio) · [Animation](#animation) · [Input](#input) · [Resource](#resource) · [Importer](#importer) · [MeshRenderer](#meshrenderer) · [AssetDatabase](#assetdatabase) · [Runtime](#runtime) · [GameHost](#gamehost) · [MultiViewport](#multiviewport)

---

## Export macro & calling convention

Every exported symbol uses one macro, defined identically in `ApiCommon.h` and `MultiViewportAPI.cpp`:

```cpp
#define EDITOR_INTERFACE extern "C" __declspec(dllexport)
```

- **`extern "C"`** — no C++ name mangling; symbols export under their plain names (exactly the names below, which C# `[DllImport]` binds to).
- **`__declspec(dllexport)`** — emitted into the DLL export table.
- **Calling convention:** unspecified → project default `__cdecl` (x64 build = the single Windows x64 convention). Bind from C# with `CallingConvention.Cdecl`.
- `DllMain.cpp` implements only the standard `DllMain` (debug CRT leak flags) and links `Engine.lib`; it exports none of the API.
- The whole surface lives in `namespace vortex` internally; the exports themselves are at global scope.

### Types that cross the boundary

- **`id::id_type`** — the engine handle type, a 32-bit unsigned integer (`u32`). Used for entities, scenes, meshes, materials, textures, cameras, skyboxes, renderers, prefabs. `id::invalid_id` is the "null" sentinel. (The managed bindings widen these to `long`.)
- **`f32`**=`float`, **`s32`**=`int32`, **`u32`**=`uint32`, **`u64`**=`uint64`, **`u8`**=`uint8`.
- **Booleans** are the native 1-byte C++ `bool` (marshal in C# as `[MarshalAs(UnmanagedType.I1)]`) — **except the Audio API**, which deliberately uses `s32` (0/1).
- **Strings:** `const char*` are UTF-8; `const wchar_t*` (in `UIText`, `UIImage`, `RunGameHost`) are UTF-16.
- **String outputs** use the two-call size-query idiom: pass `null`/`0` for the buffer to get the required count, then call again with an allocated buffer. `char**` params are arrays of caller-allocated fixed-size buffers (one per element).
- **All matrices** crossing the boundary are row-major `float[16]`.

---

## Structs crossing the boundary

```cpp
// ApiCommon.h — the transform payload for entity create/update
struct transform_component { f32 position[3]; f32 rotation[3]; f32 scale[3]; };  // rotation = Euler radians
struct game_entity_descriptor { transform_component transform; };

// CameraApi.cpp
struct camera_descriptor {
    f32 position[3]; f32 rotation[4];      // quaternion
    u8  projection;                        // 0 persp, 1 ortho
    f32 field_of_view, orthographic_size, near_clip, far_clip, aspect_ratio;
    u8  clear_flags; f32 background_color[4];
    s32 depth, culling_mask; u8 camera_type; bool is_enabled;   // camera_type: 0 game,1 main,2 editor
};

// SkyboxApi.cpp
struct skybox_descriptor {
    u8  mode;                              // 0 solid,1 gradient,2 cubemap
    f32 sky_color[3], horizon_color[3], ground_color[3], sun_direction[3], sun_color[3];
    f32 sun_intensity, ambient_intensity, exposure; bool is_enabled;
};

// MultiViewportAPI.cpp  (#pragma pack(push,4))
struct viewport_camera_desc {
    f32 position[3], target[3], up[3];
    f32 fov_degrees, near_clip, far_clip;
    u8  orthographic; u8 _padding[3]; f32 ortho_size;
};
```

---

## Scene

`Api/SceneApi.cpp` — scene lifecycle and per-scene entity creation.

```cpp
id::id_type CreateScene();                                     // create a scene → its id
void        DestroyScene(id::id_type id);
void        ActivateScene(id::id_type id);
void        DeactivateScene(id::id_type id);
id::id_type CreateGameEntityInScene(id::id_type scene_id, game_entity_descriptor* d);   // → entity id
void        RemoveGameEntityInScene(id::id_type scene_id, id::id_type entity_id);
```

---

## Entity

`Api/EntityApi.cpp` — standalone entities, rigidbodies, static colliders, and the play-mode character controller.

```cpp
id::id_type CreateGameEntity(game_entity_descriptor* d);      // → entity id
void        RemoveGameEntity(id::id_type id);
void        SetGameEntityTransform(id::id_type entity_id, game_entity_descriptor* d);   // push live transform

void SetEntityRigidbody(id::id_type entity_id, bool use_gravity, float hx, float hy, float hz);  // gravity dynamic body (AABB half-extents)
void ClearRigidbodies();
void RegisterStaticBox(float cx, float cy, float cz, float hx, float hy, float hz);      // static AABB collider
void ClearColliders();

void CharacterInit(float x, float y, float z, float hx, float hy, float hz);             // player body
void CharacterMove(float wish_x, float wish_z, bool jump, float dt);                     // advance controller
void CharacterGetPosition(float* out_xyz);                                               // → out_xyz[3]
bool CharacterGrounded();

void GetEntityPosition(id::id_type entity_id, float* out_xyz);                           // read live position
```

---

## Render

`Api/RenderApi.cpp` — the largest module: viewport, the second game-window swapchain, the 2D UI overlay, primitives, materials, render-item submission, camera/view, grid/gizmos, stats and culling/LOD/threading.

### Viewport & frame

```cpp
bool InitializeRenderViewport(void* hwnd, unsigned int width, unsigned int height);   // primary DX12 viewport
void ResizeRenderViewport(unsigned int width, unsigned int height);
void RenderFrame();                                                                    // render + present
void SwapRenderQueue();                                                                // swap without presenting (thumbnails)
void OnSceneSwitch();                                                                  // GPU idle + drop caches at transition
void CaptureFrame(const char* path);                                                   // next back buffer → 32-bit BMP
void ShutdownRenderViewport();
```

### Standalone game window (second swapchain)

```cpp
bool CreateGameWindow(void* hwnd, unsigned int width, unsigned int height);
void RenderGameWindow();
void ResizeGameWindow(unsigned int width, unsigned int height);
void DestroyGameWindow();
bool IsGameWindowActive();
```

### 2D UI overlay (drives `Vortex.UI`)

```cpp
void UIBegin(float w, float h);                                                        // begin a UI frame (virtual canvas size)
void UIRect(float x, float y, float w, float h, float r,g,b,a, float radius);          // (rounded) filled rect
void UIText(float x, float y, float w, float h, const wchar_t* text,
            float size, float r,g,b,a, int align, int weight);
void UILine(float x1, float y1, float x2, float y2, float r,g,b,a, float thickness);
void UIImage(float x, float y, float w, float h, const wchar_t* path, float r,g,b,a);  // textured quad (WIC)
void UIPushClip(float x, float y, float w, float h);
void UIPopClip();
```

### Primitive meshes

```cpp
id::id_type CreatePrimitiveCube(float size);
id::id_type CreatePrimitiveSphere(float radius);
id::id_type CreateInvertedSphere(float radius);
id::id_type CreatePrimitivePlane(float width, float height);
id::id_type CreatePrimitiveCylinder(float radius, float height);
id::id_type CreatePrimitiveCone(float radius, float height);
void        DestroyMesh(id::id_type mesh_id);
bool        QueryMeshBounds(id::id_type mesh_id, float* sizeX, float* sizeY, float* sizeZ);
bool        QueryMeshBoundsCenter(id::id_type mesh_id, float* centerX, float* centerY, float* centerZ);
```

### Materials

```cpp
id::id_type CreateMaterial();
void        SetMaterialColor(id::id_type m, float r, float g, float b, float a);
void        SetMaterialTexture(id::id_type m, id::id_type texture_id);                 // albedo
bool        MaterialHasTexture(id::id_type m);
void        DestroyMaterial(id::id_type m);
void        SetMaterialNormalTexture(id::id_type m, id::id_type t);
void        SetMaterialMetallicTexture(id::id_type m, id::id_type t);
void        SetMaterialRoughnessTexture(id::id_type m, id::id_type t);
void        SetMaterialAOTexture(id::id_type m, id::id_type t);
void        SetMaterialMetallic(id::id_type m, float value);
void        SetMaterialRoughness(id::id_type m, float value);
void        SetMaterialNormalStrength(id::id_type m, float value);
void        SetMaterialAO(id::id_type m, float value);
void        SetMaterialUseDirectXNormals(id::id_type m, bool use_directx);
void        SetMaterialUnlit(id::id_type m, bool is_unlit);
void        SetMaterialEmissiveStrength(id::id_type m, float strength);
```

### Render-item submission

```cpp
void SubmitRenderItem(id::id_type mesh_id, id::id_type material_id, float* world_matrix);   // float[16] row-major (identity if null)
void SubmitGizmoItem(id::id_type mesh_id, id::id_type material_id, float* world_matrix);     // always-on-top (depth off)
void SubmitGizmoWireItem(id::id_type mesh_id, id::id_type material_id, float* world_matrix);  // + wireframe
void SubmitMeshInstances(id::id_type mesh_id, id::id_type material_id, const float* world_matrices, int count);  // one instanced draw (count*16 floats)
```

### Camera / view

```cpp
void  SetCamera(float px,py,pz, float tx,ty,tz, float ux,uy,uz);   // position, look-at target, up
void  SetViewFieldOfView(float fov_degrees);
float GetViewFieldOfView();
```

### Grid, gizmos, mode

```cpp
void SetGridVisible(bool visible);
void SetGridSettings(float spacing, float major_line_interval, float extent);
void SetGizmosVisible(bool visible);
bool IsGridVisible();  bool AreGizmosVisible();
void SetWireframeMode(bool enabled);  bool IsWireframeMode();
void SetVSync(bool enabled);          bool IsVSyncEnabled();
id::id_type CreateGizmoArrow(float length, float radius);
id::id_type CreateGizmoCylinder(float length, float radius);
```

### Performance stats & culling/LOD/threading

```cpp
int  GetCurrentFPS();
int  GetDrawCallCount();  int GetVertexCount();
int  GetInstancesTested(); int GetInstancesDrawn();
void SetRenderDistance(float distance);                        // 0 = disabled
void SetLOD(bool enabled, float mid, float farD);             // density LOD (thin distant instances)
void SetGeometricLOD(bool enabled, float mid, float farD);    // decimated low-poly at distance
void SetMultithreading(bool enabled);
void SetMultithreadingForce(bool force);
bool IsMultithreadingActive();
```

---

## RenderLoop

`Api/RenderLoopApi.cpp` — the engine-owned render thread + timing.

```cpp
void  StartRenderLoop();  void StopRenderLoop();  bool IsRenderLoopRunning();
void  SetTargetFPS(int fps);  int GetTargetFPS();
void  SetRenderLoopVSync(bool enabled);  bool IsRenderLoopVSyncEnabled();
float GetDeltaTime();  float GetTotalTime();
```

---

## Camera

`Api/CameraApi.cpp` — engine camera **components** (distinct from the renderer's single view camera above). Uses `camera_descriptor`.

```cpp
id::id_type  CreateCamera(camera_descriptor* d);              // → id (invalid if null)
void         RemoveCamera(id::id_type c);   bool IsCameraAlive(id::id_type c);
id::id_type  GetMainCamera();  id::id_type GetActiveCamera();
void         SetActiveCamera(id::id_type c);  unsigned int GetCameraCount();

void  SetCameraPosition(id::id_type c, float x,y,z);   void GetCameraPosition(id::id_type c, float* x,*y,*z);
void  SetCameraRotation(id::id_type c, float x,y,z,w);  void GetCameraRotation(id::id_type c, float* x,*y,*z,*w);  // quaternion
void  SetCameraFOV(id::id_type c, float fov);           float GetCameraFOV(id::id_type c);   // 60 if invalid
void  SetCameraClipPlanes(id::id_type c, float near, float far);
void  SetCameraProjection(id::id_type c, unsigned char projection);   // 0 persp,1 ortho
void  SetCameraType(id::id_type c, unsigned char type);  unsigned char GetCameraType(id::id_type c);   // 0 game,1 main,2 editor
void  SetCameraEnabled(id::id_type c, bool enabled);    bool IsCameraEnabled(id::id_type c);
void  SetCameraAspectRatio(id::id_type c, float aspect);
void  SetCameraBackgroundColor(id::id_type c, float r,g,b,a);
void  SetCameraDepth(id::id_type c, int depth);

void  GetCameraForward(id::id_type c, float* x,*y,*z);
void  GetCameraRight(id::id_type c, float* x,*y,*z);
void  GetCameraUp(id::id_type c, float* x,*y,*z);
void  GetCameraViewMatrix(id::id_type c, float* out16);        // row-major float[16]
void  GetCameraProjectionMatrix(id::id_type c, float* out16);
void  RenderCameraGizmo(id::id_type c, float r, float g, float b);   // wireframe frustum
void  ApplyCameraToRenderer(id::id_type c);                    // push view + projection to renderer
```

---

## Lighting

`Api/LightingApi.cpp` — clear-and-submit each frame (max 16 point, 8 spot).

```cpp
void ClearLights();
void SetDirectionalLight(float dirX,dirY,dirZ, float r,g,b, float intensity);
void AddPointLight(float px,py,pz, float r,g,b, float intensity, float range);
void AddSpotLight(float px,py,pz, float dirX,dirY,dirZ, float r,g,b,
                  float intensity, float range, float spotAngle, float innerSpotAngle);
void SetAmbientStrength(float strength);
```

---

## Skybox

`Api/SkyboxApi.cpp` — a renderer-level skybox plus skybox **components**. Uses `skybox_descriptor`.

```cpp
// Renderer-level
void         SetSkyboxEnabled(bool enabled);  bool IsSkyboxEnabled();
void         SetSkyboxMode(unsigned int mode);  unsigned int GetSkyboxMode();
void         SetSkyboxColors(float skyR,G,B, float horizonR,G,B, float groundR,G,B);
void         SetSkyboxSolidColor(float r, float g, float b);
void         SetSkyboxSun(float dirX,dirY,dirZ, float r,g,b, float intensity);
// Component
id::id_type  CreateSkyboxComponent(skybox_descriptor* d);     // → id (invalid if null)
void         RemoveSkyboxComponent(id::id_type s);
void         ApplySkyboxToRenderer(id::id_type s);  void ApplyActiveSkybox();
void         SetActiveSkyboxComponent(id::id_type s);
```

---

## Audio

`Api/AudioApi.cpp` — the voice-level audio engine. Voice handles are opaque `u64` with a generation counter (stale handles ignored). **Booleans are `s32` (0/1).** Bus indices: `0` Master, `1` Music, `2` SFX, `3` Ambience, `4` UI.

```cpp
// Clip preload / info
s32 AudioPreloadClip(const char* path);                        // 1 playable, 0 missing/undecodable
s32 AudioValidateClip(const char* path);                       // header-probe only
s32 AudioGetClipInfo(const char* path, f32* duration, s32* sample_rate, s32* channels);
s32 AudioGetWaveform(const char* path, f32* peaks, s32 bin_count);   // per-bin peaks 0..1 (decode off-thread)

// Playback
u64  AudioPlayVoice(const char* path, f32 volume, f32 pitch, f32 pan,
                    s32 loop, s32 priority, s32 stream, s32 out_bus, s32 hrtf, s32 occlusion);   // → voice handle
void AudioStopVoice(u64 h);  void AudioPauseVoice(u64 h);  void AudioResumeVoice(u64 h);
s32  AudioIsVoicePlaying(u64 h);  s32 AudioIsVoiceValid(u64 h);
void AudioSetVoiceVolume(u64 h, f32 v);  void AudioSetVoicePitch(u64 h, f32 p);  void AudioSetVoicePan(u64 h, f32 pan);
void AudioSetVoicePosition(u64 h, f32 x, f32 y, f32 z);
void AudioSetVoiceSpatial(u64 h, f32 spatial_blend, f32 min_dist, f32 max_dist,
                          s32 rolloff_mode, f32 doppler, f32 spread);
void AudioFadeVoice(u64 h, f32 target, f32 seconds, s32 stop_when_done);
void AudioSetVoiceReverbSend(u64 h, f32 send);                 // 0..1

// Steam Audio (no-op unless phonon.dll loads)
void AudioSteamSetEnabled(s32 enabled);
void AudioSteamSetGeometry(const f32* verts, s32 vertex_count, const s32* indices, s32 index_count);

// Mixer buses
void AudioSetBusVolume(s32 bus, f32 volume);  f32 AudioGetBusVolume(s32 bus);
void AudioSetBusMute(s32 bus, s32 mute);      s32 AudioGetBusMute(s32 bus);
void AudioGetBusLevels(s32 bus, f32* peak, f32* rms);
void AudioSetDuck(s32 trigger_bus, s32 target_bus, f32 duck_db, f32 attack_ms, f32 release_ms, f32 threshold);  // duck_db<0 install, >=0 remove
void AudioClearDucks();

// Reverb (one global send bus)
void AudioSetReverbParams(f32 decay_seconds, f32 wet_level, f32 predelay_ms);

// Clip data / listener / device / stats
s32  AudioRegisterClipData(const char* name, const void* data, u64 size);   // play in-RAM (.vpak) data by name
void AudioSetListener(f32 px,py,pz, f32 fx,fy,fz, f32 ux,uy,uz);
s32  AudioHasDevice();
void AudioGetVoiceStats(s32* active, s32* stolen, s32* max_voices);
```

---

## Animation

`Api/AnimationApi.cpp` — skeleton/clip extraction (size-query aware) + skinned submission.

```cpp
int  GetModelSkeletonNodes(const char* filepath, int* out_parents, float* out_local_bind,
                           char** out_names, int max_nodes, int max_name_len);          // → node count
int  GetModelSkeletonNodesFromMemory(const unsigned char* data, int length, const char* ext_hint,
                                     int* out_parents, float* out_local_bind, char** out_names,
                                     int max_nodes, int max_name_len);
int  GetModelSkeletonBones(const char* filepath, int* out_node_indices, float* out_inverse_bind, int max_bones);
int  GetModelSkeletonBonesFromMemory(const unsigned char* data, int length, const char* ext_hint,
                                     int* out_node_indices, float* out_inverse_bind, int max_bones);
int  GetModelAnimationCount(const char* filepath);
int  GetModelAnimationInfo(const char* filepath, int anim_index, char* out_name, int name_cap, float* out_duration_sec);
int  GetModelAnimationData(const char* filepath, int anim_index, float* out, int max_floats);   // flat channel/key encoding
bool IsMeshSkinned(id::id_type mesh_id);
void SubmitSkinnedMeshForRendering(id::id_type mesh_id, id::id_type material_id,
                                   const float* world_matrix, const float* bone_matrices, int bone_count);
```

---

## Input

`Api/InputApi.cpp` — feed raw events in, query state out. `key` = `input::key_code` (Windows virtual key).

```cpp
void InitializeInput();  void ShutdownInput();  void UpdateInput();
void ProcessKeyboardEvent(unsigned int key, bool pressed);
void ProcessMouseButtonEvent(unsigned int button, bool pressed);
void ProcessMouseMoveEvent(float x, float y);
void ProcessMouseScrollEvent(float delta);
bool IsKeyDown(unsigned int key);  bool IsKeyPressed(unsigned int key);  bool IsKeyReleased(unsigned int key);
bool IsShiftDown();  bool IsCtrlDown();  bool IsAltDown();
bool IsMouseButtonDown(unsigned int button);  bool IsMouseButtonPressed(unsigned int button);  bool IsMouseButtonReleased(unsigned int button);
void GetMousePosition(float* x, float* y);  void GetMouseDelta(float* dx, float* dy);  float GetMouseScrollDelta();
void SetCursorLocked(bool locked);  void SetCursorVisible(bool visible);  bool IsCursorLocked();  bool IsCursorVisible();
bool IsGamepadConnected(unsigned int gamepad_id);
bool IsGamepadButtonDown(unsigned int gamepad_id, unsigned int button);
float GetGamepadAxis(unsigned int gamepad_id, unsigned int axis);
void SetGamepadVibration(unsigned int gamepad_id, float left_motor, float right_motor);
```

> The gamepad exports are engine-side stubs; the shipping controller support lives in the C# `Vortex.Input` layer (`Windows.Gaming.Input` + DualSense HID + XInput) — see [scripting `Input`](Scripting-API-Reference#input).

---

## Resource

`Api/ResourceApi.cpp` — path-based resource loading + prefabs.

```cpp
id::id_type LoadMesh(const char* path);
id::id_type LoadTexture(const char* path);
id::id_type LoadMaterial(const char* path);
id::id_type LoadShader(const char* path);
id::id_type LoadAudio(const char* path);
void        UnloadResource(id::id_type handle);
id::id_type LoadPrefab(const char* path);
id::id_type InstantiatePrefab(id::id_type /*scene_id (unused)*/, id::id_type prefab_handle, game_entity_descriptor* d);
void        UnloadPrefab(id::id_type prefab_handle);
```

---

## Importer

`Api/ImporterApi.cpp` — Assimp-backed model/texture import, from disk and from memory (packed pak), plus collision triangle extraction and per-submesh metadata. String outputs are size-query aware.

```cpp
id::id_type ImportModel(const char* filepath);
id::id_type ImportTexture(const char* filepath);
int         ImportModelWithMaterials(const char* filepath, id::id_type* out_mesh_ids,
                                     id::id_type* out_material_ids, id::id_type* out_texture_ids, int max_submeshes);  // → submesh count
id::id_type ImportTextureFromMemory(const unsigned char* data, int length);
int         ImportModelFromMemoryWithMaterials(const unsigned char* data, int length, const char* ext_hint,
                                               const char* virtual_dir, id::id_type* out_mesh_ids,
                                               id::id_type* out_material_ids, id::id_type* out_texture_ids, int max_submeshes);
int         GetModelTriangleData(const char* filepath, float* out_positions, int max_floats);   // expanded triangle verts (collision)
int         GetModelTriangleDataFromMemory(const unsigned char* data, int length, const char* ext_hint, float* out_positions, int max_floats);
int         GetModelSubmeshCount(const char* filepath);
int         GetModelSubmeshNames(const char* filepath, char** out_names, int max_submeshes, int max_name_length);
int         GetModelTexturePaths(const char* filepath, char** out_albedo, char** out_normal, char** out_metallic,
                                 char** out_roughness, char** out_ao, char** out_emissive, int max_submeshes, int max_len);
int         GetModelMaterialProps(const char* filepath, float* out_base_colors, float* out_metallic, float* out_roughness, int max_submeshes);
int         ExtractEmbeddedTextures(const char* filepath, const char* out_dir, char** out_names, int max_textures, int max_len);
id::id_type LoadVMesh(const char* filepath);                   // native .vmesh
bool        ExportMeshToVMesh(id::id_type mesh_id, const char* filepath);
bool        HasAssimpSupport();
```

---

## MeshRenderer

`Api/MeshRendererApi.cpp` — engine-side MeshRenderer components (shadows on by default).

```cpp
id::id_type CreateMeshRenderer(id::id_type entity_id, id::id_type mesh_id, id::id_type material_id);
void        RemoveMeshRenderer(id::id_type renderer_id);
void        SetMeshRendererMesh(id::id_type r, id::id_type mesh_id);
void        SetMeshRendererMaterial(id::id_type r, id::id_type material_id);
id::id_type GetMeshRendererMesh(id::id_type r);
id::id_type GetMeshRendererMaterial(id::id_type r);
void        SubmitAllMeshRenderers();                          // submit all active (up to 4096)
```

---

## AssetDatabase

`Api/AssetDatabaseApi.cpp` — GUID → asset resolution (editor project or shipped manifest).

```cpp
void        InitializeAssetDatabase(const char* project_path);
void        InitializeAssetDatabaseWithManifest(const char* manifest_path);   // shipped game
void        ShutdownAssetDatabase();
const char* GetAssetPathByGuid(const char* guid);              // null on miss
bool        HasAsset(const char* guid);
long        LoadMeshByGuid(const char* guid);
long        LoadTextureByGuid(const char* guid);
long        LoadMaterialByGuid(const char* guid);
```

---

## Runtime

`Api/RuntimeApi.cpp` — the simulation lifecycle + fixed-step clock.

```cpp
void  InitializeRuntime();                                     // resource/prefab + render/physics/audio systems
void  ShutdownRuntime();
void  StepRuntime(float dt);                                   // advance sim (dt clamped 0–0.25, fixed 1/60 accumulator)
float GetGameTime();
void  ResetGameTime();                                         // call when Play starts
```

---

## GameHost

`Api/GameHostApi.cpp` — the standalone game: its own native Win32 window + DX12 swapchain + loop on one thread. `RunGameHost` **blocks** until exit.

```cpp
bool RunGameHost(unsigned int width, unsigned int height, const wchar_t* title);   // create + run loop (blocking)
void SetGameTickCallback(void(*fn)(float));                    // per-tick callback fn(deltaSeconds)
void RequestGameHostExit();
void SetGameHostVSync(bool enabled);

// Input polling
int  GameHostMouseX();  int GameHostMouseY();  bool GameHostMouseDown();
int  GameHostClientWidth();  int GameHostClientHeight();
bool GameHostKeyDown(int vk);
void SetGameHostMouseCaptured(bool captured);  bool GameHostMouseCaptured();
int  GameHostMouseDX();  int GameHostMouseDY();
int  GameHostMouseWheel();
bool GameHostConsumeFocusGained();                             // focus-gained edge (Alt-Tab → hot-reload)
int  GameHostNextChar();                                       // next typed char, -1 if none
int  GameHostNextKeyPressed();                                 // next edge-pressed VK, 0 if none

// Display
void GameHostToggleFullscreen();  bool GameHostIsFullscreen();
void GameHostSetResolution(int w, int h);
void SetRenderScale(float s);  float GetRenderScale();
void SetDlssMode(int mode);    int GetDlssMode();              // 0 off,1 Quality,2 Balanced,3 Performance,4 UltraPerformance
void SetFrameGenMode(int mode); int GetFrameGenMode();  int FrameGenPresentedFps();   // 0 off,1 x2,2 x3,3 x4

// Material shaders (hot-reload)
void SetMaterialShader(int material_id, const char* hlsl_path);   // empty clears
int  ReloadMaterialShaders();  bool AnyMaterialShaderDirty();

// GPU adapter
int  GpuVendorId();  bool GpuSupportsDlss();  int GpuName(char* buf, int cap);   // → bytes written
```

---

## MultiViewport

`MultiViewportAPI.cpp` — secondary offscreen render targets for the editor's multi-viewport, using `viewport_camera_desc`.

```cpp
unsigned int CreateRenderTarget(unsigned int width, unsigned int height);   // → target id
void         DestroyRenderTarget(unsigned int target_id);
bool         ResizeRenderTarget(unsigned int target_id, unsigned int width, unsigned int height);
bool         HasRenderTarget(unsigned int target_id);
void         RenderToTarget(unsigned int target_id, viewport_camera_desc* camera, bool render_grid, bool render_gizmos);
bool         PrepareRenderTargetReadback(unsigned int target_id);
const void*  ReadRenderTargetPixels(unsigned int target_id, unsigned int* out_width, unsigned int* out_height, unsigned int* out_row_pitch);
void         ReleaseRenderTargetPixels(unsigned int target_id);
```

---

## See also

- [[Managed-Interop-Bindings]] — the C# P/Invoke wrappers over every function here.
- [[Scripting-API-Reference]] — the gameplay API that ultimately calls into this DLL.
- [[Architecture]] — how this C interop layer connects the C++ engine to the WPF editor.
