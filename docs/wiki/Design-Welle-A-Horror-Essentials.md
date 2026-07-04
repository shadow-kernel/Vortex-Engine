# Design: Welle A — „Der Look" (Horror Essentials, v2.7.0)

> **Milestone 2 — [v2.7.0 Horror Essentials](https://github.com/shadow-kernel/Vortex-Engine/milestone/2)** · Teil 1 von 3 (Welle A/B/C).
> **Erstes Spiel:** Untergrund / Bunker / Mine — **komplett dunkel, die Taschenlampe (Spot Light) ist praktisch die einzige Lichtquelle.**
> Dieses Konzept vereinfacht die Prioritäten drastisch: keine Sonne ⇒ **Directional-Shadows ([#24](https://github.com/shadow-kernel/Vortex-Engine/issues/24)) entfallen vorerst**; die **Spot-Schatten der Taschenlampe ([#23](https://github.com/shadow-kernel/Vortex-Engine/issues/23)) sind DAS Herzstück**; kleine Räume ⇒ Shadow-Map-Range gut beherrschbar.

Welle A liefert **den Look**: Schatten, Nebel, Post-FX und eine skriptbare Taschenlampe. Welle B (Interaktion/Schreck: Trigger, Raycast, Coroutines) und Welle C (Spiel-Gerüst: Save/Load, Transitions, Template) folgen separat.

Enthaltene Issues: **[#23](https://github.com/shadow-kernel/Vortex-Engine/issues/23) Spot-Shadow-Mapping** · **[#27](https://github.com/shadow-kernel/Vortex-Engine/issues/27) Fog** · **[#28](https://github.com/shadow-kernel/Vortex-Engine/issues/28) Post-Processing-Framework** · **[#29](https://github.com/shadow-kernel/Vortex-Engine/issues/29) Vignette/Grain/Chromatic-Aberration** · **[#26](https://github.com/shadow-kernel/Vortex-Engine/issues/26) Vortex.Light-API + Flicker**.

---

## 1. Aktuelle Renderer-Architektur (Baseline)

Ein Frame wird in `render_frame()` gebaut — **[Engine/Graphics/DX12/DX12Renderer.cpp:490](../../Engine/Graphics/DX12/DX12Renderer.cpp)** — mit **zwei Pfaden**:

| Pfad | Wann | Ziel-RT | Farbe/Tiefe |
|---|---|---|---|
| **Scaled/Offscreen** | `render_scale < 1.0` **oder** DLSS-FG aktiv (Entscheid `DX12Renderer.cpp:545–553`) | `m_scaled_rt` | R8G8B8A8_UNORM + D32_FLOAT (sampelbar als R32_FLOAT-SRV) |
| **Direct** | sonst (native Auflösung) | Swapchain-Backbuffer | R8G8B8A8_UNORM + `m_depth_buffer` (D32_FLOAT) |

**Pass-Reihenfolge** (beide Pfade): `Skybox → Grid → 3D-Scene → Gizmos → [Motion-Vectors] → [DLSS-SR] → Upscale-Composite → UI-Overlay (Direct2D) → Present`.
Der Upscale-Composite (`DX12Renderer.cpp:652–673`) schreibt **direkt in den Backbuffer**; das **UI-Overlay** (`UIOverlay`, D3D11on12) zeichnet danach, nach `CommandList->Close()` (Zeile 715) und vor `present()` (Zeile 724).

**Lichter:** CPU-Struct `SpotLightData` (64 B) in **`DX12Renderer.h:159–177`**, pro Frame per `memcpy` in den persistent-gemappten Light-CB (`m_light_cb`) geschrieben (**`DX12Renderer_Lights.cpp:40–84`**), gebunden an **b2** (`DX12Renderer_3DScene.cpp:56–57`). GPU-Mirror + analytisches Spot-Shading (Kegel-Fade, **keine** Schatten) in **`standard.hlsl:48–72` / `:281–314`**. Limits: `MAX_POINT_LIGHTS=16`, `MAX_SPOT_LIGHTS=8` (`DX12Renderer.h:156–157`).

**Root-Signature (10 Params)** — **`DX12Pipeline3D.cpp:106–193`**:
`b0`=PerFrame · `b1`=PerObject · `b2`=Lights · `t0–t4`=Material-SRVs (Params 3–7) · `t5`=Bone-Palette-Root-SRV (Param 8, VS-only) · `t6`=Height-Map (Param 9, PS).
**Frei für uns:** `t7+`, `b3+`, `s1+`. Ein **einziger** statischer Sampler `s0` (Aniso, `COMPARISON_FUNC_NEVER`, hart in `:167`).

**Licht-Weg vom Editor zum Shader (wichtig für [#26]):** `SceneRenderService.SubmitEntityLightsRecursive` (**`SceneRenderService.cs:1552–1674`**, Spot @ 1655) liest die `Light`-Component **jeden Frame** neu → `VortexAPI.SubmitSpotLight` (`VortexRendering.cs:749–762`) → nativer Export `AddSpotLight` (`LightingApi.cpp:37–55`). Lichter werden **jeden Frame geleert & neu eingereicht** (`ClearLights`). Script-API `Vortex.Lighting` (`VortexScriptApi.cs:773–781`) kann bisher **nur** Ambient + Directional global — **keine** Kontrolle pro Entity. Die Shadow-Felder in `Light.cs` (`:69–73`, `:160–184`) existieren, werden aber vom nativen Renderer **ignoriert**.

---

## 2. Feature-Designs

### [#23] Spot-Light Shadow-Mapping — *das Herzstück* (Aufwand: **L**)

**Ziel:** Die Taschenlampe wirft echte, harte-bis-weiche Schatten. Requisiten im Bunker verdecken das Licht → bewegte Schattenkanten = maximale Grusel-Wirkung.

**Scope-Entscheidung (v1):** **Genau EINE** 2048² Shadow-Map für den **primären** Spot (die Taschenlampe). Kein Atlas, keine 8 Maps. Erweiterung auf einen kleinen Pool (2–4) später, wenn der Bunker fixe Deckenlichter braucht. Begründung: Konzept = Taschenlampe ist DIE Lichtquelle; ein Shadow-Pass statt acht.

**Ansatz — neuer Shadow-Depth-Pass VOR der 3D-Scene:**

1. **Ressourcen** (neu in `DX12Renderer.h`, init in `DX12Renderer_Init.cpp`):
   - `m_spot_shadow_map`: 2048² **D32_FLOAT**, als **DSV *und* SRV** (Muster: `DX12RenderTarget.cpp:152–203`, R32_TYPELESS → D32-DSV + R32-SRV). SRV-Slot im 1024er-Heap **fest reservieren** (nicht dynamisch, sonst Slot-Erschöpfung, siehe Fallen).
   - `m_shadow_vp`: CBV mit der Light-View-Proj-Matrix (`XMMatrixLookToLH(pos, dir, up) * XMMatrixPerspectiveFovLH(spotAngle, 1, near, range)`), gefüllt aus denselben normalisierten Spot-Daten wie die Licht-Submission (`DX12Renderer_Lights.cpp:62–83`) — **gleiche** Normalisierung, sonst wandern die Schatten.
2. **Shadow-PSO** (neu, minimal): eigene, kleine Root-Sig (`b0` = Shadow-VP), **Depth-only** (kein Pixel-Shader), Front-Face-Cull invertiert oder Depth-Bias gegen Acne. Input-Layout: **rigid only** (Bone-Skinning im Shadow-Caster = Fast-Follow; Bunker-Requisiten sind statisch). Neuer Shader `shadow_depth.hlsl` (nur VS: `pos_world × shadow_vp`).
3. **Main-Root-Sig erweitern** (`DX12Pipeline3D.cpp:106–193`): **10 → 12 Params** — `+ b3` (Shadow-VP, PS) `+ t7`-Table (Shadow-Map-SRV, PS). Zusätzlich **statischer Comparison-Sampler `s1`** (`FILTER_COMPARISON_MIN_MAG_LINEAR_MIP_POINT`, `COMPARISON_FUNC_LESS_EQUAL`, CLAMP; Sampler-Count `:178` auf 2). *(64-DWORD-Budget bleibt eingehalten — Descriptor-Tables = 1 DWORD, CBV-Root = 2.)*
4. **Sampling in `standard.hlsl`** (`:281–314`, nach dem Kegel-Fade des primären Spots): `proj = mul(float4(worldPos,1), ShadowVP); uv = proj.xy/proj.w*0.5+float2(0.5,0.5); d = proj.z/proj.w;` dann **3×3 PCF** via `ShadowMap.SampleCmpLevelZero(ShadowCmpSampler, uv, d - bias)`; Radiance des Spots × `shadowFactor`. Nur der primäre Spot (Index 0) wird beschattet; die übrigen bleiben analytisch.
5. **Shadow-Bias & Normal-Offset** aus der `Light`-Component (`ShadowBias`, `ShadowNormalBias` — bisher ignoriert) endlich durchreichen.

**Verzahnung Editor↔Native:** `SubmitSpotLight` bekommt eine Variante `SubmitSpotLightWithShadow(...)` (oder ein `castsShadows`-Flag + Bias), damit `Light.CastShadows` im Inspector real wirkt.

---

### [#27] Depth-/Height-Fog (Aufwand: **S**) — *entkoppelt, zuerst umsetzbar*

**Ziel:** Dichte, schluckende Dunkelheit; die Taschenlampe „schneidet" einen Kegel in den Nebel. Für die Mine: leichter Boden-Nebel (Height-Fog).

**Ansatz — Forward, direkt in `standard.hlsl`** (kein Post-Pass, keine Abhängigkeit von [#28]):
- Fog-Parameter in **`PerFrameConstants` (b0)** ergänzen (`DX12Renderer.h:469–482`, passt in die 256-B-Grenze bzw. minimale Erweiterung): `fog_color(float3)`, `fog_density(float)`, `fog_height_y(float)`, `fog_height_falloff(float)`, `fog_mode(uint)`.
- Im PS nach der finalen Farbe: `dist = length(cameraPos - worldPos)`; **exp2**-Fog `f = 1 - exp(-(density*dist)^2)`; Height-Term `h = saturate((fog_height_y - worldPos.y) * fog_height_falloff)`; `color = lerp(color, fog_color, saturate(f * h_or_1))`. `density = max(0, density)` gegen NaN.
- Da der Bunker **keinen Himmel** hat, reicht Forward-Fog auf Opak-Geometrie vollständig (falls doch Skybox: gleiche Formel in `skybox.hlsl`).
- Steuerung: neue managed-API `Vortex.Atmosphere.SetFog(...)` → P/Invoke → `PerFrameConstants`. Fog ist **Szenen-Parameter**, nicht kamera-/FOV-abhängig (sonst „poppt" der Nebel bei FOV-Wechsel).

---

### [#28] Post-Processing-Framework + [#29] Vignette/Grain/Chromatic-Aberration (Aufwand: **M**)

**Ziel:** Der „alte-Kamera"-Look: Vignette (Fokus + Angst-Tunnel), Film-Grain (Körnung), Chromatic Aberration (Linsen-Fransen). Zusammen = billige, enorme Realismus-/Dread-Steigerung.

**Ansatz — eine kombinierte Fullscreen-Pass zwischen Scene-Composite und UI:**
- **Routing-Trick:** Post-FX braucht das fertige Bild in einem Offscreen-RT. Statt den Direct-Pfad umzubauen, erweitern wir den Scaled-Pfad-Trigger: `use_scale = (render_scale<1 || FG || post_fx_enabled)`. Dann rendert 3D immer nach `m_scaled_rt`, der Upscale-Composite schreibt in ein **`m_post_input_rt`**, und **ein** kombinierter Post-FX-Pass liest dieses RT und schreibt den **Backbuffer** (danach UI-Overlay wie gehabt).
- **`DX12PostFXPipeline`** (analog `DX12UpscalePipeline.h`): Fullscreen-Triangle (SV_VertexID), Root-Sig = 1 SRV-Table (`t0`=Bild) + `b0`=`PostFXConstants` + `s0`=Linear/Clamp. **Ein** Shader `postfx.hlsl` mit allen drei Effekten, jeweils per Konstante ein/aus + Intensität (statt 3 Pässe → 1 Draw):
  - **Vignette:** radialer Falloff `1 - pow(length(uv-0.5)*2, power) * intensity`.
  - **Film-Grain:** prozeduraler Hash `hash(uv + frameTime)`, mono, `* intensity`; `frameTime`/Seed **extern** (nicht `frameCount` — sonst driften Editor-Previews vs. Gameplay).
  - **Chromatic Aberration:** R/G/B an radial versetzten UVs sampeln (`offset ∝ length(uv-0.5)`, ~1–2 px @1080p, mit Auflösung skaliert), rekombinieren.
- **Ordering-Regel:** Post-FX läuft **nach** DLSS-Upscale (auf Real- *und* KI-Frames gleich) und schreibt **kein** Depth (FG-Tagging bleibt korrekt). Viewport **nicht** intern setzen (Hardcoded-Viewport-Falle).

---

### [#26] Vortex.Light-Runtime-API + Flicker (Aufwand: **S–M**) — *managed-first*

**Ziel:** Game-Scripts steuern Lichter live: Taschenlampe an/aus, Deckenlicht flackert, Intensität pulsiert.

**Schlüssel-Erkenntnis:** `SubmitEntityLightsRecursive` liest die `Light`-Component **jeden Frame neu** (`SceneRenderService.cs:1618–1674`). Ein Script muss also nur die **managed** `Light`-Component ändern — die Änderung fließt automatisch nächsten Frame auf die GPU. **Keine** neuen nativen Exports für den Normalfall nötig (passt zur Philosophie „Gameplay in Scripts, nicht in der Engine").

**Ansatz:**
- **`Vortex.Light`**-Wrapper: `entity.GetLight()` → Objekt mit `.Intensity`, `.Color`, `.Range`, `.Enabled`, `.SpotAngle`, die auf die `Light`-Component schreiben. Auflösung der Entity über `ScriptRuntime.FindEntityByHandle` (`ScriptRuntime.cs:29–35`).
- **Flicker-Helper:** `Vortex.Light.Flicker(speed, min, max, mode)` — kleine managed Utility, moduliert `Intensity` pro `Update()` (Sinus / Perlin / diskret). Kein Engine-Code.
- **Shipped-Game-Falle (verifizieren!):** Im exportierten Spiel muss der Licht-Submit die Component ebenfalls **pro Frame** (oder via Dirty-Flag) neu lesen — analog zum Animation-`RuntimeDirty`-Fix. Falls der Shipped-Pfad Lichter nur einmal einreicht, ergänzen wir einen nativen `SetLightIntensity(entityId,...)`-Export als Fallback. **→ Verifikationspunkt vor Abschluss.**

---

## 3. Reihenfolge, Abhängigkeiten & Aufwand

Ein Entwickler ⇒ nach **Wert** sequenziert (nicht parallel). Keine harten Blocker untereinander.

| # | Feature | Aufwand | Warum diese Position | Berührt |
|---|---|---|---|---|
| 1 | **[#27] Fog** | S | Sofort sichtbare Atmosphäre, 0 Abhängigkeiten, reine Shader-Arbeit | `standard.hlsl`, `PerFrameConstants`, kleine API |
| 2 | **[#26] Light + Flicker** | S–M | Flackernde Taschenlampe = riesiger Grusel-Wert, billig; **schaltet das Spiel-Scripting frei** | managed `Vortex.Light` (+ evtl. 1 Export) |
| 3 | **[#23] Spot-Shadows** | L | Technisches Herzstück; profitiert davon, in fertiger Atmosphäre getestet zu werden | Root-Sig, neues PSO+RT, `standard.hlsl`, Light-Submit |
| 4 | **[#28]+[#29] Post-FX** | M | Finaler „Grade"; Routing-Umbau + kombinierter Pass | `render_frame`, neue `DX12PostFXPipeline`, `postfx.hlsl` |

**Dogfooding-Verzahnung:** Nach Schritt 1–2 startet parallel das **Bunker-Spielprojekt** (leeres dunkles Level + Taschenlampen-Script), sodass jedes weitere Feature (Schatten, Post-FX) sofort im echten Spiel getestet wird — genau das Ziel „alles testen".

---

## 4. Kritische Fallen (aus der Architektur-Analyse)

1. **BGRA vs. RGBA:** `ensure_scaled_rt` nutzt explizit **R8G8B8A8** (`DX12Renderer.cpp:388`), aber `DX12RenderTarget` **defaultet auf BGRA** (`DX12RenderTarget.h:27`). Jedes neue RT (Post-FX-Input, Shadow-Map ist Depth) **explizit R8G8B8A8** anlegen.
2. **Hardcoded Viewport:** `RSSetViewports` wird pro 3D-Pass **einmal** gesetzt (`:571–574`/`:696–699`). Post-FX/Shadow-Pässe dürfen den Viewport der Aufrufer-Kette **nicht** intern überschreiben (Shadow-Pass setzt seinen **eigenen** 2048²-Viewport und stellt zurück).
3. **Depth-State nach Frame-Generation:** bei FG bleibt Depth in `PIXEL_SHADER_RESOURCE` (`:649`). Neue Pässe, die Depth lesen, **belassen** diesen State; wer Depth schreibt (Shadow-Pass nutzt eigene DSV) fasst den Szenen-Depth nicht an.
4. **Geteilter 1024-Slot-SRV-Heap** (Renderer + ResourceRegistry). Shadow-Map-SRV **fest reservieren**, nicht über den dynamischen Textur-Allokator — sonst „silent fail" bei voller Szene.
5. **Shader-ABI-Kopplung:** `PerFrameConstants`/`SpotLight`-Layout ist Byte-für-Byte an C++ gekoppelt (`DX12Renderer.h` ↔ `standard.hlsl`). Jede Padding-Änderung **beidseitig** synchronisieren (bekannter v2.6.9-„Byte-Match"-Fix).
6. **Custom-Material-Shader teilen die Main-Root-Sig** (`create_custom_pso` reuse @ `:63`). Root-Sig-Erweiterung (Params 10–11, `s1`) ist rückwärtskompatibel (bestehende Slots unverändert), aber **alle** PSOs neu erzeugen.
7. **Struct-Bloat vermeiden:** Shadow-VP **nicht** inline in `SpotLightData` (64→128 B sprengt Cache-Line/Packing) — separater CB `b3`.
8. **Skinned Shadow-Caster:** v1 = rigid. Der Monster-Schatten (skinned) ist ein bewusster **Fast-Follow** (Shadow-VS muss dann auch skinnen).

---

## 5. Verifikation (pro Schritt)

Jedes Feature wird **im Editor-Play UND im exportierten Standalone-Build** (F12-Native-Backbuffer-Capture) geprüft — GDI-Capture geht beim FLIP_DISCARD-Fenster nicht.

- **Fog:** Screenshot dunkler Raum, Taschenlampenkegel schneidet sichtbaren Nebel; Height-Fog am Boden.
- **Light/Flicker:** Script lässt Deckenlicht flackern; F12 über mehrere Frames zeigt Intensitätsänderung; **Shipped-Build**-Gegencheck (Falle #26).
- **Shadows:** Requisite vor Taschenlampe → scharfe, korrekt ausgerichtete Schattenkante (keine Acne, kein Peter-Panning); bewegte Lampe → wandernder Schatten.
- **Post-FX:** Vignette/Grain/CA einzeln togglebar; Vorher/Nachher-Screenshot.

---

## 6. Offene Entscheidungen (mit Empfehlung)

| Frage | Empfehlung (Default, wenn kein Einwand) |
|---|---|
| Shadow-Umfang v1 | **Eine** Taschenlampen-Shadow-Map (2048²); Pool später |
| Shadow-Filter | 3×3 PCF (Balance Qualität/Perf) |
| Fog-Verfahren | Forward in `standard.hlsl` (exp2 + Height), kein Post-Pass |
| Post-FX-Struktur | **Ein** kombinierter Pass (Vignette+Grain+CA), einzeln togglebar |
| [#26] Basis | managed-first (Component pro Frame gelesen); nativer Setter nur als Fallback |
| Skinned Shadows | Fast-Follow nach v1 |

---

*Erstellt aus einer 5-Wege-Architektur-Analyse des DX12-Renderers (Frame-Loop, Lighting, Root-Sig/PSO, Post/Upscale-Kette, C#↔Native-Interop). Alle Datei:Zeile-Anker gegen den Stand von `main` zum Zeitpunkt der Analyse; vor Implementierung gegen aktuellen Code gegenprüfen.*
