SPOT-LIGHT SHADOW MAPPING — IMPLEMENTATION MAP (issue #23, "the flashlight")
Scope v1: ONE shadow-casting spot light per frame (the flashlight = first shadow-enabled spot submitted), hard shadows via SampleCmpLevelZero, skinned meshes RECEIVE but don't cast (matches the custom-shader-skips-skinned precedent).

═══════════════ 1. FRAME-GRAPH INSERTION POINT ═══════════════

Record the depth-only shadow pass in `DX12Renderer::render_frame()` (Engine/Graphics/DX12/DX12Renderer.cpp) between `m_command_list->Reset()` (L538) and the `use_scale` branch (L560/L681) — before any back-buffer/scaled-RT barriers and clears, on the same command list. Both the scaled and direct paths then consume the same shadow map.

Pass recording recipe (matches existing conventions — every pass sets its own viewport):
1. shadow depth → `D3D12_RESOURCE_STATE_DEPTH_WRITE` (tracked helper, DX12RenderTarget-style; do NOT reuse DX12DepthBuffer — it has no SRV and no state tracking, DX12DepthBuffer.cpp:38-73)
2. `RSSetViewports/Scissor` = shadow-map dims (2048² from `Light.Resolution`)
3. `ClearDepthStencilView(1.0f)`
4. `OMSetRenderTargets(0, nullptr, FALSE, &shadowDSV)` — no RTV
5. depth-only PSO + draw casters
6. transition depth → `PIXEL_SHADER_RESOURCE` before the scene passes sample it (per-frame DEPTH_WRITE↔PSR round-trip; keep it entirely separate from the m_scaled_rt depth state machine that FG deliberately leaves in PSR across frames, DX12Renderer.cpp:569/649)

Resource: lazily created via the `ensure_*` pattern (mirror `ensure_scaled_rt`, DX12Renderer.cpp:373-390, flush-before-recreate), using the existing sampleable-depth recipe: R32_TYPELESS resource + D32_FLOAT DSV + R32_FLOAT SRV (DX12RenderTarget.cpp:152-203). CRITICAL: the SRV must be created into a RESERVED slot of ResourceRegistry's shared shader-visible heap (ResourceRegistry_Textures.cpp:109-145) — NOT a private per-RT heap — because the scene pass binds the registry heap (DX12Renderer_3DScene.cpp:60-65) and only one CBV_SRV_UAV heap can be bound. Reserve the slot once at init (bump allocator never frees; a fixed reserved index survives shadow-map resize/recreate).

Geometry: instance-VB packing happens INSIDE `render_3d_scene` keyed to the main camera frustum (DX12Renderer_3DScene.cpp:237-406), so a pass recorded before it has no packed instances. v1: run a dedicated shadow pack over `m_render_queue` into a separate small `m_shadow_instance_vb` (own buffer — do NOT reuse the gizmo tail-slot region, DX12Renderer_3DScene.cpp:552-553), culled against the light cone (cheap sphere-vs-instance-AABB using light pos + Range). This avoids the "main-frustum-culled shadows miss off-screen casters" artifact without restructuring the pack.

Other scene paths (shadows missing otherwise): `render_game_window` (DX12Renderer_Queue.cpp:35-91, insert before render_skybox at L75 — this is the SHIPPED standalone game, i.e. the actual horror-game target) and `render_scene_to_target` (DX12Renderer_RenderTargets.cpp:63-349, previews — optional; binding the always-existing shadow map with strength 0 is acceptable v1).

Shadow VS trick (no new HLSL file): create a second 256-byte PerFrame CB region (`m_shadow_pass_cb`) whose `view_projection` = light VP, bind it at root param 0 during the shadow pass, and reuse the existing standard.hlsl VSMain blob with a depth-only PSO. Zero changes to `precompile_builtins` (DX12ShaderCompiler.cpp:186-224) — standard.vs.cso is already in the ship list.

═══════════════ 2. ROOT SIGNATURE / PSO ADDITIONS (compat-safe) ═══════════════

`DX12Pipeline3D::create_root_signature` (DX12Pipeline3D.cpp:106-193), strictly ADDITIVE per the documented convention (comment L111-115, height-map param-9 precedent):
- **param 10** = SRV descriptor table, register **t7**, visibility PIXEL — the shadow map. All existing `SetGraphicsRoot*` indices 0-9 in DX12Renderer_3DScene.cpp AND DX12Renderer_RenderTargets.cpp stay untouched.
- **static sampler s1** (NumStaticSamplers 1→2, desc array at L160-179): `FILTER_COMPARISON_MIN_MAG_LINEAR_MIP_POINT`, ADDRESS_BORDER with white border, `COMPARISON_FUNC_LESS_EQUAL`, visibility PIXEL. Static samplers consume no root space and shift no indices.
- Cost: 14→15 of 64 DWORDs. (The rootsig reader's alternative — a separate b3 CBV param 11 — is unnecessary: appending to PerFrameConstants b0 auto-propagates to the offscreen path via the `m_frame_constants` copy at DX12Renderer_RenderTargets.cpp:128.)

Compatibility is automatic: the root sig is created once in initialize() (DX12Pipeline3D.cpp:24) before any PSO; every custom-material PSO is compiled against `m_root_signature` (create_custom_pso, DX12Pipeline3D.cpp:63); shaders not declaring t7/s1 against a superset root sig are legal D3D12. Old user shaders with the short PerFrame cbuffer stay valid (root CBV has no size validation).

Binding rule: once standard.hlsl references t7, param 10 MUST be bound at the start of every pass using the standard PS — render_3d_scene (~3DScene.cpp:57), gizmo pass (~:548), render_to_target (~RenderTargets.cpp:242), game-window path (reuses render_3d_scene). Create the shadow map eagerly at init so a valid descriptor always exists (cleared-to-1.0 depth + strength 0 = no visual effect); an unbound-but-referenced table is device-removal territory.

New depth-only PSO in `DX12Pipeline3D::create_pso` (DX12Pipeline3D.cpp:195-344), `m_shadow_pso`:
- VS = standard VS blob, `PS = {nullptr, 0}`
- `NumRenderTargets = 0`, `RTVFormats[0] = DXGI_FORMAT_UNKNOWN`, `DSVFormat = DXGI_FORMAT_D32_FLOAT`
- Input layout = the existing rigid two-slot layout (POSITION/NORMAL/TEXCOORD slot0 + INSTANCEWORLD rows slot1, L197-206) so the instance-VB path is reused
- `DepthBias ≈ 100`, `SlopeScaledDepthBias ≈ 1.5`, `DepthBiasClamp = 0` (currently all 0 at L212-214)
- CULL_BACK (or CULL_NONE if peter-panning beats acne in the bunker; tune during F12 verification)

═══════════════ 3. CBUFFER / STRUCT ADDITIONS (byte-matched, APPEND-ONLY) ═══════════════

`PerFrameConstants` (DX12Renderer.h:473-494, alignas(256), 160 of 256 bytes used — fog precedent comment L486-487). Append at offset 160, still inside the existing 256-byte buffer (DX12Renderer_Init.cpp:31 — buffer width unchanged, no resize):

```cpp
// C++ (append after fog_padding @156):
DirectX::XMFLOAT4X4 shadow_view_projection; // @160, 64B, row-major
float shadow_strength;    // @224  0 = shadows off (the always-bound-dummy gate)
float shadow_bias;        // @228  receiver-side depth bias
float shadow_map_texel;   // @232  1.0f / resolution (for optional PCF later)
float shadow_padding;     // @236  -> 240 used of 256
```
```hlsl
// standard.hlsl PerFrame b0 (append after FogPadding, mirror field-for-field):
row_major float4x4 ShadowViewProjection;
float ShadowStrength;
float ShadowBias;
float ShadowMapTexel;
float ShadowPadding;
// + Texture2D ShadowMap : register(t7);
// + SamplerComparisonState ShadowSampler : register(s1);
```
skinned.hlsl needs ZERO changes: shadow is computed purely in PS from worldPos, PS_IN stays semantically identical (skinned VS reuses standard's PSMain blob — skinned.hlsl:2-3 constraint). GPUSpotLight/SpotLightData untouched in v1 (single global shadow light; the `padding[3]`/`spotPadding` slot stays free for a future per-spot shadow index).

PS sampling, multiplied into the spot radiance inside the `theta > outerCos` gate (standard.hlsl ~L322):
```hlsl
float shadow = 1.0;
if (ShadowStrength > 0.0) {
    float4 sp = mul(float4(input.worldPos, 1.0), ShadowViewProjection);
    float3 ndc = sp.xyz / sp.w;
    float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
    if (sp.w > 0.0 && all(saturate(suv) == suv)) {
        float lit = ShadowMap.SampleCmpLevelZero(ShadowSampler, suv, ndc.z - ShadowBias);
        shadow = lerp(1.0, lit, ShadowStrength);
    }
}
radiance *= shadow;   // only in the SPOT loop, L303-335
```
(SM5 / vs_5_0-ps_5_0 compatible — FXC toolchain, no DXC.)

═══════════════ 4. LIGHT-VIEW-PROJECTION MATH (spot cone) ═══════════════

Computed in `update_per_frame_constants` (DX12Renderer_Lights.cpp:14-85) alongside the existing VP build, from the first shadow-enabled `SpotLightData`:

```cpp
XMVECTOR pos = XMLoadFloat3(&s.position);
XMVECTOR dir = XMVector3Normalize(XMLoadFloat3(&s.direction)); // already CPU-normalized (L69-76)
XMVECTOR up  = fabsf(XMVectorGetY(dir)) > 0.99f
             ? XMVectorSet(0,0,1,0) : XMVectorSet(0,1,0,0);     // avoid parallel-up degenerate
XMMATRIX view = XMMatrixLookToLH(pos, dir, up);
float fovY = XMConvertToRadians(s.spot_angle);   // SpotAngle is FULL cone degrees end-to-end
                                                  // (shader does cos(radians(angle*0.5)), standard.hlsl:312)
                                                  // full angle in degrees == the perspective FOV directly
XMMATRIX proj = XMMatrixPerspectiveFovLH(fovY, 1.0f, 0.05f, max(s.range, 0.1f));
XMMATRIX lightVP = view * proj;  // store with the SAME row-major/transpose treatment
                                  // as view_projection (mul(vec,mat) convention)
```
Key couplings: far plane = `range` (the shader's hard `dist < range` gate + Attenuation already zero light beyond it, so no wasted precision); FOV = outer `spot_angle` (the `theta > outerCos` gate zeros everything outside — border-white s1 sampler makes out-of-map samples "lit", which the cone gate then masks anyway). Do NOT touch the four hardcoded 0.1/1000 camera projections (update_per_frame_constants, render_grid, render_skybox, DLSS eval desc) — the light projection is fully independent.

═══════════════ 5. MANAGED API + EDITOR TOUCHPOINT ═══════════════

The ECS `Light` component (Editor/ECS/Components/Lighting/Light.cs:30) ALREADY serializes `ShadowType` (None/Hard/Soft), `ShadowStrength`, `Bias`, `Resolution` — they just never reach native. Plumbing:

- **VortexAPI/Api/LightingApi.cpp** (~L37): extend `AddSpotLight(...)` with `int castShadows, float shadowStrength, float shadowBias, int shadowResolution` (internal ABI, both sides in-repo — safe to change in lockstep). Renderer stores which spot index requested shadows; first wins (log/ignore extras).
- **Editor/DllWrapper/Rendering/VortexRendering.cs** (region "Lighting System", 690-786): update the `AddSpotLight` DllImport + `SubmitSpotLight` wrapper signature.
- **Editor/Core/Services/SceneRenderService.cs** `SubmitEntityLightsRecursive` spot dispatch (~L1750): pass `light.ShadowType != ShadowType.None`, `ShadowStrength`, `Bias`, `Resolution` (treat Soft as Hard v1).
- **Editor/Scripting/VortexScriptApi.cs** `Vortex.Light` class (785-815): add
  `public bool CastShadows { get => …ShadowType != None; set { comp.ShadowType = value ? Hard : None; Dirty(); } }` and `public float ShadowStrength { … Dirty(); }` — the `Dirty()` → `SceneRenderService.RuntimeDirty` pattern (L790) already makes the shipped GameHost submit-once loop re-submit. Flashlight script then just does `GetLight().CastShadows = true;`.
- **Inspector**: the Light component card (DynamicInspectorView-driven) — expose ShadowType dropdown + ShadowStrength/Bias/Resolution fields next to the existing Intensity/Range/SpotAngle fields so it's authorable without scripts.

═══════════════ 6. ORDERED COMMIT-SIZED STEPS ═══════════════

1. **feat(engine): shadow-map resource + root-sig param 10 (t7) + s1 comparison sampler + depth-only PSO** — DX12Pipeline3D.cpp (:106-193 root sig, :195-344 new m_shadow_pso) / DX12Pipeline3D.h; shadow depth resource + tracked-state helpers + reserved registry SRV slot: DX12Renderer.h, DX12Renderer_Init.cpp, ResourceRegistry_Textures.cpp (reserve API). t7 not yet referenced by any shader → zero visual change, safe checkpoint. Verify: editor renders identically, no debug-layer errors.
2. **feat(engine): PerFrameConstants shadow fields + standard.hlsl mirror** — DX12Renderer.h:473-494 append (offsets 160-236) + Engine/Shaders/standard.hlsl PerFrame cbuffer + t7/s1 declarations + PS sampling gated on `ShadowStrength > 0` (zeroed → still no visual change). Bind param 10 at pass start in DX12Renderer_3DScene.cpp (~:57 scene, ~:548 gizmo) and DX12Renderer_RenderTargets.cpp (~:242). Re-export re-runs precompile_builtins automatically (standard.hlsl already in kBuiltins).
3. **feat(engine): shadow depth pass in render_frame** — DX12Renderer.cpp (insert after L538): `ensure_shadow_map(resolution)`, light-VP compute in DX12Renderer_Lights.cpp update_per_frame_constants, m_shadow_pass_cb (light VP at param 0 trick), shadow instance pack (light-cone cull) into m_shadow_instance_vb, draw, DEPTH_WRITE→PSR transition. First visible shadows in the editor viewport.
4. **feat(engine): shadows in game window + preview path** — DX12Renderer_Queue.cpp (before L75) and (optional strength-0 bind only) DX12Renderer_RenderTargets.cpp. This is the commit that puts shadows in the SHIPPED game.
5. **feat(api): shadow params through the light submit chain** — LightingApi.cpp, VortexRendering.cs, SceneRenderService.cs (~1750), plus DX12Renderer add_spot_light signature + "first shadow spot" selection.
6. **feat(editor/scripting): Vortex.Light.CastShadows/ShadowStrength + inspector fields** — VortexScriptApi.cs:785-815, Light inspector card.
7. **verify + tune**: F12 native back-buffer capture in GameHost (per vortex-visual-verification memory) with the bunker flashlight; tune DepthBias/SlopeScaledDepthBias/ShadowBias against acne vs peter-panning; confirm DLSS SR/FG path unaffected (shadow pass is before the use_scale branch and touches no tagged resource).

═══════════════ 7. TOP RISKS (reader-flagged) ═══════════════

1. **Descriptor-heap exclusivity**: scene/gizmo/preview passes bind ResourceRegistry's heap; a shadow SRV in a private DX12RenderTarget-style heap (mvec/upscale pattern) CANNOT be sampled mid-scene-pass. Must live in the registry heap (reserved slot; bump allocator never frees — recreate-on-resize would otherwise leak slots).
2. **Byte-ABI lockstep**: PerFrameConstants ↔ standard.hlsl PerFrame is byte-coupled with no shared header, append-only (fog precedent). Same edit in both files in the same commit; any insert/reorder silently corrupts all lighting.
3. **Unbound-table UB**: once standard.hlsl references t7, EVERY pass using the standard PS in BOTH duplicated draw paths (DX12Renderer_3DScene.cpp AND DX12Renderer_RenderTargets.cpp) must bind param 10 or risk device removal — including gizmo pass and previews. Eager-create + always-bind eliminates the failure mode.
4. **Three independent scene paths**: render_frame, render_game_window, render_scene_to_target each record passes separately — shadows only in render_frame means a shadowless shipped game (the 92c2067 stale-spot-bytes bug was exactly this duplication class).
5. **Geometry ordering**: instance packing is main-camera-frustum-keyed inside render_3d_scene; without a dedicated shadow pack, off-screen casters (wall behind the player) produce missing/popping shadows.
6. **Cross-frame state machines**: FG leaves m_scaled_rt depth in PSR across present (restored L569); the shadow depth needs its OWN tracked resource and per-frame DEPTH_WRITE↔PSR round-trip — DX12DepthBuffer (no SRV, no tracking) would silently break.
7. **Shipped-build gating**: any NEW shadow .hlsl entry point must be added to precompile_builtins kBuiltins or shipped Release silently lacks shadows (avoided entirely by the reuse-standard-VS-with-rebound-b0 trick).
8. **Custom material shaders**: they reuse the same root sig (safe with additive param 10/s1) but keep their own lighting code — they simply won't receive shadows until their .hlsl adds the sampling snippet; document in the wiki shader page.
9. **Angle convention**: SpotAngle travels as FULL-cone degrees end-to-end (shader halves+cos); the light projection must use the full angle as FOV directly — halving it twice gives a shadow frustum smaller than the lit cone (visible clipped-shadow ring at the cone edge, masked only partially by border-white).

Key files (all under C:/Users/Administrator/Documents/GitHub/Vortex-Engine/): Engine/Graphics/DX12/{DX12Renderer.cpp, DX12Renderer.h, DX12Renderer_3DScene.cpp, DX12Renderer_Lights.cpp, DX12Renderer_RenderTargets.cpp, DX12Renderer_Queue.cpp, DX12Renderer_Init.cpp, DX12Pipeline3D.cpp, DX12RenderTarget.cpp, DX12ShaderCompiler.cpp}, Engine/Graphics/Resources/ResourceRegistry_Textures.cpp, Engine/Shaders/standard.hlsl, VortexAPI/Api/LightingApi.cpp, Editor/DllWrapper/Rendering/VortexRendering.cs, Editor/Core/Services/SceneRenderService.cs, Editor/ECS/Components/Lighting/Light.cs, Editor/Scripting/VortexScriptApi.cs