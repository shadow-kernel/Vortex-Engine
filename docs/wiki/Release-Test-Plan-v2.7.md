# Release-Testplan v2.7.0 „Horror Essentials"

Vollständige Abnahme aller v2.7-Features vor dem Release. Fünf Ebenen — erst wenn alle grün sind, wird getaggt.
Referenz-Testprojekt: `%USERPROFILE%\VortexEngineProjects\WaveTest` (alle Szenen registriert), Wegwerf-Gameplay-Projekt: frische Kopie des Horror-Starter-Templates.

## Ebene A — Automatisierte Regression (ein Befehl, ~5 Minuten)

```powershell
powershell -ExecutionPolicy Bypass -File tools\run-release-tests.ps1
```

Startet 8 Headless-Harnesses nacheinander im echten Player und sammelt PASS/FAIL (Stand heute: **82/82 grün**):

| Szene | Feature | Assertions |
| --- | --- | --- |
| WaveA | Scripting-Welle: Raycast, Instantiate/Destroy, Coroutines, Events, SendMessage, Save | 19 |
| SetActiveTest | SetActive + Renderer/Collider/Audio-Toggles (Raycast-bewiesen) | 11 |
| StairTest | Treppensteigen, Slope-Limit, Ground-Snap, Wand blockt | 7 |
| FieldTest | Script-Feld-Serialisierung inkl. Unbekannt-Feld-Toleranz | 8 |
| SocketTest | Bone-Sockets: Ancestor- + Explizit-Target, Attach/Detach, Cycle-Guard | 13 |
| LayerTest | Bone-Masken-Layer: Gewichts-Blend exakt, Masken-Ausschluss, Layer-Events | 9 |
| SyncTest | Synced-Gruppen: frame-locked über Wraps + Speed-Wechsel, atomare Pause | 8 |
| CamFxTest | CameraFX: Kick-Displacement, Spring-Recovery, Sway, Stacking | 7 |

Rot ⇒ Release stoppen. Die Ergebnisdateien liegen unter `%TEMP%\*_results.txt`.

## Ebene B — Visuelle F12-Sichtprüfungen (~15 Minuten)

`run-release-tests.ps1 -Visual` öffnet jede Szene 20 s; F12 speichert das native Backbuffer-Bild nach `~\Pictures\vortex_screenshot.bmp` (Vergleich mit den Referenzbildern der Feature-Issues #23–#33, #50, #175).

| Szene(n) | Sollbild |
| --- | --- |
| BloomTest / BloomOff | Emissive-Würfel glüht weich / glüht NICHT |
| GradeTest | Farbstich/Kontrast gemäß Scene-Settings sichtbar |
| AoTest / AoOff | Kontaktschatten in Ecken/Fugen / flach; Sonnenlicht in beiden identisch |
| CsmTest | Säulen auf 2/14/40 m werfen Sonnenschatten ohne Kantenflimmern bei Kamerafahrt |
| PointShadowTest | 4 Säulen um die Birne werfen radiale Schattenkeile |
| GlassTest | Zwei überlappende Scheiben = doppelte Tönung, roter Würfel korrekt „zerschnitten" |
| MipTest | Schachbrettboden wird zum Horizont GLATT (kein Flimmern beim Kameraschwenk) |
| VmTest / VmOff | Roter Viewmodel-Block VOR der Wand sichtbar / verdeckt |
| VuiTest | Gold-Fokusring, Pfeiltasten/Pad navigieren, Enter aktiviert, Tooltip nach 450 ms |
| SocketTest | Würfel reiten sichtbar auf dem winkenden Arm |

Zusätzlich Schatten-Kill-Switches gegenprüfen: `VORTEX_NO_DIR_SHADOWS=1` / `VORTEX_NO_POINT_SHADOWS=1` müssen die jeweilige Schattenart entfernen (Isolationsbeweis).

## Ebene C — Editor-Smoke (~20 Minuten, manuell)

1. **Projekt anlegen** aus dem Horror-Starter-Template → öffnet ohne Kompatibilitätsdialog, Demo-Szene lädt.
2. **Environment-Panel**: Fog, Bloom (Threshold/Intensity), SSAO (Radius/Intensity), Grading (Exposure/Sättigung) — jeder Regler wirkt live im Viewport; Szene speichern → Editor neu starten → Werte stehen noch.
3. **Inspector-Karten**: Script mit public Fields → FIELDS-Zeilen editieren (int/float/bool/enum/Vector3), Play → Werte wirken; **Bone Attachment** (Bone-Dropdown, Snap to Bone, Capture Offset); **Mesh Renderer** → „First-Person (viewmodel)"-Haken.
4. **Play-Pfade**: Play im Viewport UND „Run in new window" — beide zeigen Post-FX, Schatten, HUD; Editor-Freecam bleibt IMMER effektfrei.
5. **Werkzeuge**: Material-Editor (Live-Preview + Custom Shader), Prefab-Editor (isoliert!), Keyframe-Editor öffnet .vanim, Collision-Editor-Preview, Ctrl+Z quer durch alles, Ctrl+S, Git-Panel zeigt Diffs.
6. **Konsole**: absichtlicher Scriptfehler (Tippfehler) → Fehler mit Datei/Phase in der Console, Editor überlebt. **Achtung C#5**: kein `$""`, kein `?.` in Projektscripts.

## Ebene D — Gameplay-End-to-End im Template (~15 Minuten)

Frische Template-Kopie, Play (zusätzlich einmal als Ship-Build, siehe E):

- **Bewegung**: WASD/Sprint/Springen/Ducken, Treppen hoch ohne Hüpfen, runter ohne Airborne, steile Rampe rutscht ab.
- **Taschenlampe [F]**: wirft echte Schatten (Spot), Batterie leert sich, Flicker; Licht folgt dem Blick im Play-Fenster.
- **Pistole**: Linksklick = Schuss (Mündungsblitz-Lichtpuls, Recoil-Kick mit Feder-Rückkehr, Hülse fliegt + bleibt liegen + verschwindet), HUD zählt 8/24 herunter, Leerklick lädt automatisch, **R** lädt (Balken), **Sprint bricht das Nachladen ab**, Viewmodel clippt an keiner Wand.
- **Welt**: Tür [E], Jump-Scare-Trigger, Monster verfolgt + Footsteps materialabhängig, Fog/Grain/Vignette-Atmosphäre.
- **UI**: Pause (ESC), Menü komplett per Gamepad/Tastatur navigierbar (Fokusring), Quit funktioniert.
- **Gamepad**: kompletter Durchlauf nur mit Controller (inkl. DualSense).

## Ebene E — Ship-Build & Update-Kette (~30 Minuten)

1. **Export Release** (nicht Debug!): Installer + Portable bauen.
2. Auf **sauberem Windows-Profil** (oder VM, ohne Editor-Installation) starten: Texturen/Materialien korrekt (vpak!), Audio spielt, `player-audio.log` sauber, Savegames unter `%APPDATA%\VortexGames\<Projekt>`.
3. **DLSS**: SR-Stufen + FG x2/x3/x4 durchschalten — Real/Shown-FPS-Anzeige plausibel, kein Absturz beim Umschalten. *Bekannt & ok*: leichtes FG-Ghosting auf Viewmodel und skinned Charakteren (Camera-Reprojection-Mvec, Follow-up-Issue existiert).
4. **Auto-Update**: alte Version installieren → Update-Prüfung VOR Engine-Init → Migration eines v2.6-Projekts (Kompatibilitätsdialog → migriert → alles rendert).
5. **F5-Gegenprobe**: Engine unter VS-F5 ≥ 60 FPS (sonst Mixed-Mode/Diagnosetools/XAML-Live-Preview in VS prüfen — kein Engine-Bug).

## Perf-Gates (RTX 5070-Referenz)

| Messung | Gate |
| --- | --- |
| Leere GameHost-Szene | ≥ 1500 FPS |
| 63k Instanzen (Cull-Szene) | ≥ 200 FPS |
| Bloom / SSAO / CSM @720p | je ≤ 0,1 ms |
| Template-Demo Ship-Build | ≥ 500 FPS |

## Nicht als Bug werten (dokumentierte v1-Grenzen)

Viewmodel/Skinned-Ghosting unter DLSS-FG · Freecam-Licht ist weltfest (by design) · Editor-Scripts sind C#5 · Skinned Meshes casten keine Schatten & haben kein Geo-LOD · transparente Materialien werfen keine Schatten · BCn-Kompression kommt mit #179.

## Nach grünem Durchlauf: Release-Ritual

`docs/content/` → `docs/content-2.7/` kopieren, VERSIONS-Eintrag in `docs/js/docs.js`, Changelog/Migration finalisieren, Tag v2.7.0 → CI baut Installer+Portable.
