# Managed Interop Bindings — `Editor.DllWrapper`

These C# classes are the **managed binding layer** over the native [`VortexAPI.dll`](Native-DLL-API). They live under `Editor/DllWrapper/` and use P/Invoke (`[DllImport("VortexAPI.dll", CallingConvention = Cdecl)]`) to call the engine's C ABI. The editor and the scripting facades (`Vortex.Settings`, `Vortex.Lighting`, `Vortex.Audio` bus control, …) call *these*, not the raw DLL.

The public surface is carried by two types (both namespace `Editor.DllWrapper`):

- **`VortexAPI`** — one `public static partial class` split across many files, each part covering a domain (Core, Rendering, Camera, Animation, Input, Resources, Gizmos). All parts share `_dllName = "VortexAPI.dll"` and `_cc = CallingConvention.Cdecl`. The pattern throughout: a `private static extern` P/Invoke paired with a `public static` wrapper that validates handles and wraps the call in `try/catch` (so a mismatched/older DLL degrades gracefully instead of throwing).
- **`VortexAudio`** — a separate `public static class` for voice-level audio.

**Marshalling recap:** `[MarshalAs(UnmanagedType.I1)] bool` for C `bool`; ANSI/`LPStr` for most paths, `LPUTF8Str` in audio, `LPWStr` in some UI/host calls; matrices are row-major `float[16]`; entity/gizmo rotations are Euler degrees while camera rotations are quaternions `(x,y,z,w)`.

**Contents:** [Shared structs & handles](#shared-interop-types) · [Core (VortexCore)](#core--vortexcorecs) · [Rendering (VortexRendering)](#rendering--vortexrenderingcs) · [Camera (VortexCamera)](#camera--vortexcameracs) · [Audio (VortexAudio)](#audio--vortexaudiocs) · [Animation (VortexAnimation)](#animation--vortexanimationcs) · [Input (VortexInput)](#input--vortexinputcs) · [Resources (VortexResources)](#resources--vortexresourcescs) · [Gizmos](#gizmos)

---

## Shared interop types

Defined in `VortexAPI.cs` (namespace `Editor.EngineAPIStructs`), passed by the entity/prefab calls:

```csharp
[StructLayout(LayoutKind.Sequential)] public class TransformComponent {
    public Vector3 Position; public Vector3 Rotation; /*Euler*/ public Vector3 Scale = new Vector3(1,1,1);
}
[StructLayout(LayoutKind.Sequential)] public class GameEntityDescriptor {
    public TransformComponent Transform = new TransformComponent();
}
```

Handle wrapper structs (returned by create calls; carry an `IsValid` and an `Invalid` sentinel):

- **`SceneHandle`** — `long Id; bool IsValid; static SceneHandle Invalid;`
- **`CameraHandle`** — `long Id; bool IsValid; static CameraHandle Invalid;`

---

## Core — `VortexCore.cs`

`VortexAPI` partial. Runtime lifecycle, scenes, entities, and the play-mode physics/character controller.

### Runtime

| Wrapper | Native | Description |
|---|---|---|
| `InitEngineRuntime()` | `InitializeRuntime` | Initialize the engine runtime. |
| `ShutdownEngineRuntime()` | `ShutdownRuntime` | Shut down the runtime. |
| `StepEngineRuntime(float dt)` | `StepRuntime` | Advance one sim tick (before rendering, in play). |
| `float GameTime()` | `GetGameTime` | Fixed-timestep game clock, seconds. |
| `ResetGameClock()` | `ResetGameTime` | Reset the clock (Play start). |

### Scenes

`CreateEngineScene() → SceneHandle` · `DestroyEngineScene(SceneHandle)` · `ActivateEngineScene(SceneHandle)` · `DeactivateEngineScene(SceneHandle)`.

### Entities & physics

| Wrapper | Description |
|---|---|
| `long CreateGameEntity(GameEntity)` / `CreateGameEntity(GameEntity, SceneHandle)` | Create an entity from its `Transform` (globally / in a scene). |
| `RemoveGameEntity(GameEntity)` / `RemoveGameEntity(GameEntity, SceneHandle)` | Remove an entity. |
| `SetEntityTransform(long entityId, Vector3 pos, Vector3 rotEuler, Vector3 scale)` | Push a live transform to the engine. |
| `RegisterRigidbody(long entityId, bool useGravity, float hx, hy, hz)` | Register a gravity dynamic body (AABB half-extents). |
| `ClearAllRigidbodies()` | Clear play-mode rigidbodies. |
| `Vector3 ReadEntityPosition(long entityId)` | Read engine-side position. |
| `AddStaticBox(float cx,cy,cz, hx,hy,hz)` / `ClearAllColliders()` | Static collision boxes. |
| `InitCharacter(float x,y,z, hx,hy,hz)` | Init the play-mode player body. |
| `MoveCharacter(float wishX, wishZ, bool jump, float dt)` | Move the character. |
| `Vector3 GetCharacterPosition()` / `bool IsCharacterGrounded()` | Character state. |

---

## Rendering — `VortexRendering.cs`

`VortexAPI` partial — the largest module. Viewport, the standalone game window, the native GameHost, the 2D UI overlay, render submission, grid/gizmos, stats, the render loop, multi-viewport, lighting and skybox.

### Viewport

`bool InitRenderViewport(IntPtr hwnd, uint w, uint h)` · `ResizeRender(uint w, uint h)` · `RenderOnce()` · `SwapRenderQueue()` · `OnSceneSwitch()` · `CaptureFrame(string path)` · `ShutdownRender()`.

### Standalone game window (2nd swapchain)

`bool CreateGameWindow(IntPtr hwnd, uint w, uint h)` · `RenderGameWindow()` · `ResizeGameWindow(uint w, uint h)` · `DestroyGameWindow()` · `bool IsGameWindowActive()`.

### Native GameHost

Register a per-frame managed tick with the `GameTickDelegate` (keep it GC-rooted while the loop runs):

```csharp
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GameTickDelegate(float dt);
```

| Wrapper | Description |
|---|---|
| `bool RunGameHost(uint w, uint h, string title)` | Create window + run loop (**blocks** the caller). |
| `SetGameTickCallback(GameTickDelegate fn)` | Per-frame managed tick. |
| `RequestGameHostExit()` | Request loop exit. |
| `SetGameHostVSync(bool)` | VSync. |
| `int GameHostMouseX/Y()`, `bool GameHostMouseDown()` | Mouse polling. |
| `int GameHostClientWidth/Height()` | Client size. |
| `bool GameHostKeyDown(int vk)` | Key held. |
| `SetGameHostMouseCaptured(bool)`, `bool GameHostMouseCaptured()`, `int GameHostMouseDX/DY()` | FPS mouse-look capture + delta. |
| `int GameHostMouseWheel()` | Wheel notches (then cleared). |
| `bool GameHostConsumeFocusGained()` | Focus-regained edge (hot-reload trigger). |
| `int GameHostNextChar()` / `int GameHostNextKeyPressed()` | Retained-UI text / keybind capture. |
| `GameHostToggleFullscreen()`, `bool GameHostIsFullscreen()`, `GameHostSetResolution(int,int)` | Display. |
| `SetRenderScale(float)` / `float GetRenderScale()` | Render scale 0.25–2.0. |
| `SetDlssMode(int)` / `int GetDlssMode()` | DLSS super-resolution. |
| `SetFrameGenMode(int)` / `int GetFrameGenMode()` / `int FrameGenPresentedFps()` | DLSS Frame Gen. |
| `SetMaterialShader(int materialId, string hlslPath)` | Bind custom `.hlsl` (empty = built-in). |
| `int ReloadMaterialShaders()` / `bool AnyMaterialShaderDirty()` | Shader hot-reload. |
| `int GpuVendorId()` / `bool GpuSupportsDlss()` / `string GpuName()` | GPU adapter. |

### 2D UI overlay

`UIBegin(w,h)` · `UIRect(x,y,w,h, r,g,b,a, radius)` · `UIText(x,y,w,h, text, size, r,g,b,a, align, weight)` · `UILine(x1,y1,x2,y2, r,g,b,a, thick)` · `UIImage(x,y,w,h, path, r,g,b,a)` · `UIPushClip(x,y,w,h)` · `UIPopClip()`.

### View camera & render submission

| Wrapper | Description |
|---|---|
| `SetViewCamera(float posX,posY,posZ, targetX=0,targetY=0,targetZ=0, upX=0,upY=1,upZ=0)` | Set the live view camera. |
| `SetViewFOV(float fovDegrees)` | View FOV. |
| `SubmitMeshForRendering(long meshId, long materialId, float[] world = null)` | Submit a mesh (identity if null). |
| `SubmitGizmoForRendering(...)` / `SubmitGizmoWireForRendering(...)` | Always-on-top gizmo passes. |
| `SubmitMeshInstanced(long meshId, long materialId, float[] worldMatrices, int count)` | Instanced draw. |

### Grid / gizmos / wireframe / VSync

`ShowGrid(bool)` · `ConfigureGrid(float spacing=1, majorLineInterval=10, extent=100)` · `ShowGizmos(bool)` · `SetVSyncEnabled(bool)` · `SetWireframe(bool)` — plus cached properties `IsGridVisible`, `AreGizmosVisible`, `IsVSyncOn`, `IsWireframeMode`.

### Performance stats & culling/LOD

Properties: `CurrentFPS`, `DrawCalls`, `VertexCount`, `InstancesTested`, `InstancesDrawn`, `MultithreadingActive`. Methods: `RenderDistance(float)`, `Lod(bool, mid, far)`, `GeometricLod(bool, mid, far)`, `Multithreading(bool)`, `MultithreadingForce(bool)`.

### Render loop

`StartEngineRenderLoop()` · `StopEngineRenderLoop()` · `bool IsEngineRenderLoopRunning` · `SetEngineTargetFPS(int)` · `int EngineTargetFPS` · `float DeltaTime` · `float TotalTime`.

### Multi-viewport

`ViewportCameraDesc` (`[StructLayout(Sequential, Pack=4)]`) with factory helpers `CreateOrthographic(...)` / `CreatePerspective(...)`. Methods: `uint CreateSecondaryRenderTarget(uint w, uint h)` · `DestroySecondaryRenderTarget(uint)` · `bool ResizeSecondaryRenderTarget(uint, uint, uint)` · `bool HasSecondaryRenderTarget(uint)` · `RenderToSecondaryTarget(uint, ViewportCameraDesc, bool renderGrid=false, bool renderGizmos=false)` · `bool PrepareSecondaryRenderTargetReadback(uint)` · `IntPtr ReadSecondaryRenderTargetPixels(uint, out uint w, out uint h, out uint rowPitch)` · `ReleaseSecondaryRenderTargetPixels(uint)`.

### Lighting

`ClearAllLights()` · `SetDirectionalLightParams(dirX,dirY,dirZ, r,g,b, intensity)` · `SubmitPointLight(px,py,pz, r,g,b, intensity, range)` (max 16/frame) · `SubmitSpotLight(px,py,pz, dirX,dirY,dirZ, r,g,b, intensity, range, spotAngle, innerSpotAngle)` (max 8/frame) · `SetAmbientLightStrength(float)`.

> These back the scripting [`Vortex.Lighting`](Scripting-API-Reference#lighting) facade.

### Skybox

`enum SkyboxMode { SolidColor=0, Gradient=1, Texture=2 }`. Immediate: `EnableSkybox(bool)` · `bool IsSkyboxOn` · `SetSkyboxRenderMode(SkyboxMode)` · `SetSkyboxGradient(...)` · `SetSkyboxColor(r,g,b)` · `ConfigureSkyboxSun(...)`. Component (`SkyboxDescriptor` with `CreateGradient`/`CreateSolidColor` factories): `long CreateRuntimeSkybox(SkyboxDescriptor)` · `RemoveRuntimeSkybox(long)` · `ApplyRuntimeSkybox(long)` · `ApplyActiveRuntimeSkybox()` · `SetActiveRuntimeSkybox(long)`.

---

## Camera — `VortexCamera.cs`

`VortexAPI` partial. Engine camera **components** (not the single renderer view camera).

**Enums:** `CameraProjectionType { Perspective=0, Orthographic=1 }`, `CameraClearFlagsType { Skybox, SolidColor, DepthOnly, Nothing }`, `CameraTypeEnum { GameCamera=0, MainCamera=1, EditorCamera=2 }`.

**`CameraDescriptor`** (`[StructLayout(Sequential)]`): position(3), rotation quaternion(4), projection, FOV, ortho size, near/far, aspect, clear flags, background(4), depth, culling mask, camera type, enabled — with `Default` / `MainCameraDefault` statics.

| Group | Members |
|---|---|
| Create/destroy | `CameraHandle CreateEngineCamera(CameraDescriptor)` · `CreateEngineCamera()` · `CreateMainCamera()` · `DestroyEngineCamera(CameraHandle)` · `bool IsEngineCameraValid(CameraHandle)` |
| Query | `GetEngineMainCamera()` · `GetEngineActiveCamera()` · `SetEngineActiveCamera(CameraHandle)` · `int GetEngineCameraCount()` |
| Transform | `SetEngineCameraPosition(...)` · `(x,y,z) GetEngineCameraPosition(...)` · `SetEngineCameraRotation(x,y,z,w)` · `GetEngineCameraRotation(...)` · `GetEngineCameraForward/Right/Up(...)` |
| Properties | `SetEngineCameraFOV`/`GetEngineCameraFOV` · `SetEngineCameraClipPlanes` · `SetEngineCameraProjection(CameraProjectionType)` · `SetEngineCameraType`/`GetEngineCameraType(CameraTypeEnum)` · `SetEngineCameraEnabled`/`IsEngineCameraEnabled` · `SetEngineCameraAspectRatio` · `SetEngineCameraBackgroundColor` · `SetEngineCameraDepth` |
| Matrices/gizmo | `float[] GetEngineCameraViewMatrix(...)` · `GetEngineCameraProjectionMatrix(...)` · `RenderEngineCameraGizmo(...)` · `ApplyEngineCameraToRenderer(...)` |

---

## Audio — `VortexAudio.cs`

`public static class VortexAudio`. Voice handles are opaque `ulong` (`InvalidVoice = 0`). Paths marshal as `LPUTF8Str`. Bus constants: `BusMaster=0, BusMusic=1, BusSfx=2, BusAmbience=3, BusUi=4, BusCount=5`; `BusNames = {"Master","Music","SFX","Ambience","UI"}`.

| Group | Members |
|---|---|
| Names | `int BusIndexFromName(string)` |
| Clip info | `bool PreloadClip(string)` · `bool ValidateClip(string)` · `bool GetClipInfo(string, out float dur, out int rate, out int ch)` · `float[] GetWaveform(string, int bins)` |
| Playback | `ulong PlayVoice(string path, float volume, pitch, pan, bool loop, int priority, bool stream=false, int bus=BusSfx, bool hrtf=false, bool occlusion=false)` · `StopVoice/PauseVoice/ResumeVoice(ulong)` · `bool IsVoicePlaying/IsVoiceValid(ulong)` |
| Voice props | `SetVoiceVolume/SetVoicePitch/SetVoicePan(ulong, float)` · `SetVoicePosition(ulong, x,y,z)` · `SetVoiceSpatial(ulong, spatialBlend, minDist, maxDist, int rolloff, doppler, spread)` · `FadeVoice(ulong, target, seconds, bool stopWhenDone=false)` · `SetVoiceReverbSend(ulong, float)` |
| Buses | `SetBusVolume/GetBusVolume(int)` · `SetBusMute/GetBusMute(int)` · `GetBusLevels(int, out peak, out rms)` · `SetDuck(triggerBus, targetBus, duckDb, attackMs, releaseMs, threshold=0.05f)` · `ClearDucks()` |
| Reverb | `SetReverbParams(decaySeconds, wetLevel, predelayMs)` |
| Steam Audio | `SteamSetEnabled(bool)` · `SteamSetGeometry(float[] verts, int[] indices)` |
| Data/listener/stats | `bool RegisterClipData(string name, byte[] data)` · `SetListener(px,py,pz, fx,fy,fz, ux,uy,uz)` · `bool HasDevice()` · `GetVoiceStats(out active, out stolen, out max)` |

> `Vortex.Audio.SetBusVolume` and the [`AudioSource`](Scripting-API-Reference#audiosource) script handle ultimately route through here.

---

## Animation — `VortexAnimation.cs`

`VortexAPI` partial. Skeleton/clip extraction (returns friendly arrays) + skinned submission.

- `class SkeletonNodeInfo { string Name; int Parent; float[] LocalBind; }`
- `class SkeletonBoneInfo { int NodeIndex; float[] InverseBind; }`

| Wrapper | Description |
|---|---|
| `SkeletonNodeInfo[] GetSkeletonNodes(string filepath)` / `GetSkeletonNodesFromMemory(byte[], string extHint)` | Node hierarchy. |
| `SkeletonBoneInfo[] GetSkeletonBones(string filepath)` / `GetSkeletonBonesFromMemory(byte[], string extHint)` | Bone palette. |
| `int GetAnimationCount(string filepath)` | Number of embedded clips. |
| `bool GetAnimationInfo(string filepath, int index, out string name, out float durationSec)` | Clip name + duration. |
| `float[] GetAnimationData(string filepath, int index)` | Raw flattened channel data. |
| `bool MeshIsSkinned(long meshId)` | Mesh carries skinning data. |
| `SubmitSkinnedMesh(long meshId, long materialId, float[] world, float[] bonePalette, int boneCount)` | Submit skinned mesh. |

---

## Input — `VortexInput.cs`

`VortexAPI` partial. Feeds the engine input system and queries it.

**Enums:** `KeyCode : uint` (Windows virtual keys), `MouseButton { Left, Right, Middle, X1, X2 }`, `GamepadButton { A, B, X, Y, LeftBumper, RightBumper, Back, Start, LeftStick, RightStick, DPadUp, DPadDown, DPadLeft, DPadRight }`, `GamepadAxis { LeftStickX, LeftStickY, RightStickX, RightStickY, LeftTrigger, RightTrigger }`.

| Group | Members |
|---|---|
| Init | `InitInput()` · `ShutdownInputSystem()` · `UpdateInputState()` |
| Events | `SendKeyEvent(KeyCode, bool)` · `SendMouseButtonEvent(MouseButton, bool)` · `SendMouseMoveEvent(x,y)` · `SendMouseScrollEvent(delta)` |
| Keyboard | `bool GetKeyDown/GetKeyPressed/GetKeyReleased(KeyCode)` · `bool GetShiftDown/GetCtrlDown/GetAltDown()` |
| Mouse | `bool GetMouseButtonDown/Pressed/Released(MouseButton)` · `(x,y) GetMousePos()` · `(dx,dy) GetMouseMovement()` · `float GetMouseScroll()` |
| Cursor | `LockCursor(bool)` · `ShowCursor(bool)` · `bool CursorLocked` · `bool CursorVisible` |
| Gamepad (stub) | `bool IsGamepadActive(int)` · `bool GetGamepadButtonDown(int, GamepadButton)` · `float GetGamepadAxisValue(int, GamepadAxis)` · `SetGamepadRumble(int, left, right)` |

---

## Resources — `VortexResources.cs`

`VortexAPI` partial. Primitives, materials, resource loading, model import, MeshRenderer components, mesh bounds.

### Primitives & materials

`long CreateCubeMesh(float size=1)` · `CreateSphereMesh(float r=0.5f)` · `CreateInvertedSphereMesh(float r=0.5f)` · `CreatePlaneMesh(float w=1, h=1)` · `CreateCylinderMesh(float r=0.5f, h=1)` · `CreateConeMesh(float r=0.5f, h=1)` · `DeleteMesh(long)`.

`long CreateNewMaterial()` · `SetMaterialBaseColor(long, r,g,b, a=1)` · `SetMaterialAlbedoTexture/NormalMap/MetallicMap/RoughnessMap/AOMap(long, long tex)` · `SetMaterialMetallicValue/RoughnessValue/NormalStrengthValue/AOValue(long, float)` · `SetMaterialNormalFormat(long, bool useDirectX)` · `SetMaterialAsUnlit(long, bool)` · `SetMaterialEmissiveBrightness(long, float)` · `bool HasMaterialTexture(long)` · `DeleteMaterial(long)`.

### Resource loading

`long LoadMeshResource/LoadTextureResource/LoadMaterialResource/LoadShaderResource/LoadAudioResource(string path)` · `UnloadResourceHandle(long)` · `long LoadPrefabResource(string)` · `long InstantiatePrefabInScene(SceneHandle, long prefabHandle, GameEntity)` · `UnloadPrefabResource(long)`.

### Model import

Return types: `SubmeshImportData { long MeshId, MaterialId, TextureId }`, `SubmeshTextureSet { string Albedo, Normal, Metallic, Roughness, AO, Emissive }`, `SubmeshMaterialProps { float[] BaseColor; float Metallic, Roughness }`.

| Wrapper | Description |
|---|---|
| `long ImportModelFromFile(string)` / `long ImportTextureFromFile(string)` | Import model / texture. |
| `long ImportTextureFromBytes(byte[])` | Import texture from RAM. |
| `long LoadVMeshFromFile(string)` / `bool SaveMeshToVMesh(long, string)` | Native `.vmesh`. |
| `bool IsAssimpAvailable()` | Assimp support. |
| `SubmeshImportData[] ImportModelWithMaterialsFromFile(string)` | Multi-material import (max 64). |
| `SubmeshImportData[] ImportModelFromBytes(byte[], string extHint, string virtualDir)` | Multi-material import from RAM. |
| `int GetSubmeshCount(string)` | Submesh count. |
| `float[] GetModelTriangles(string)` / `GetModelTrianglesFromMemory(byte[], string extHint)` | Collision triangle positions. |
| `string[] GetSubmeshNames(string, int max=64)` | Submesh names. |
| `SubmeshTextureSet[] GetSubmeshTexturePaths(string, int max=64)` | Per-submesh texture paths. |
| `SubmeshMaterialProps[] GetSubmeshMaterialProps(string, int max=64)` | Per-submesh PBR props. |
| `string[] ExtractEmbeddedTextures(string, string outDir, int max=64, int maxLen=260)` | Extract embedded textures. |

### MeshRenderer components & bounds

`long CreateMeshRendererComponent(long entityId, long meshId, long materialId)` · `DestroyMeshRendererComponent(long)` · `UpdateMeshRendererMesh/Material(long, long)` · `long GetMeshFromRenderer/GetMaterialFromRenderer(long)` · `bool GetMeshBounds(long, out sizeX,sizeY,sizeZ)` · `bool GetMeshBoundsCenter(long, out cx,cy,cz)` · `SubmitMeshRenderersToQueue()`.

---

## Gizmos

`Editor/DllWrapper/Gizmos/*.cs` are additional `VortexAPI` partials that build world matrices in C# and push meshes through the depth-disabled `SubmitGizmoForRendering` / `SubmitGizmoWireForRendering` passes. They are editor tooling, summarized here:

| File | Public surface |
|---|---|
| `TransformGizmo.cs` | Move tool + shared gizmo state. `enum GizmoType { Translate, Rotate, Scale }`; properties `HoveredAxis`, `IsDraggingGizmo`, `DraggingAxis`, `CurrentGizmoType`; `InitializeGizmos()`, `RenderGizmo(...)`, `RenderTransformGizmo(...)`. |
| `RotationGizmo.cs` | `RenderRotationGizmo(posX,posY,posZ, scale=1)` — three axis circles. |
| `ScaleGizmo.cs` | `RenderScaleGizmo(posX,posY,posZ, scale=1)` — axis bars + center cube. |
| `SelectionOutline.cs` | `RenderSelectionOutline(...)` — orange 12-edge box around the selection. |
| `ColliderGizmo.cs` | `RenderColliderBox/Sphere/Capsule/MeshWire(...)` — green wireframe collider previews. |
| `AudioGizmo.cs` | `RenderAudioSourceIcon/AudioListenerIcon/AudioRangeSpheres/ReverbZoneGizmo(...)` — camera-facing icons + range shapes. |
| `CameraGizmo.cs` | `RenderCameraIcon(...)`, `RenderCameraGizmo(...)` — floating camera icon + FOV frustum. |

---

## See also

- [[Native-DLL-API]] — the C ABI these wrappers bind to.
- [[Scripting-API-Reference]] — the gameplay API layered on top.
- [[Entities-and-Components]] — the model whose `SyncToEngine` calls flow through here.
