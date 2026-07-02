# Audio-Anleitung (Deutsch)

So verwendest du das Vortex-Audiosystem (v2.6.0) — vom ersten Sound bis zu 3D-Schritten, Musik, Mixer,
Hall und binauralem HRTF. Kein Engine-Code nötig: Alles läuft über Komponenten im Editor und die
`Vortex.Audio`-Skript-API.

## 1. Ein Sound abspielen (Editor, ohne Code)

1. Lege deine Audio-Datei (`.wav`, `.mp3`, `.ogg`, `.flac`) in `Assets/Audio/`. Sie erscheint im
   **Project-Browser** unter dem Tab **Audio** mit einer Waveform-Vorschau. Ein Klick spielt sie an.
2. Wähle eine Entity → **Add Component → Audio → Audio Source** (oder ziehe die Audio-Datei direkt aus dem
   Browser auf den Inspector der Entity).
3. Stelle im Inspector ein:
   - **Clip** – die Audio-Datei (oder ein Sound-Container, siehe §4).
   - **Volume / Pitch / Loop / Play On Awake** – Grundlautstärke, Tonhöhe, Schleife, Auto-Start.
   - **Spatial Blend** – `0` = flaches 2D (UI, Musik), `1` = volles 3D (Weltklang).
   - **Min/Max Distance + Rolloff** – ab wann und wie der Klang mit Entfernung leiser wird.
   - **Output Bus** – Master / Music / SFX / Ambience / UI (siehe §5).
4. Setze **eine** `AudioListener`-Komponente auf deine Spielkamera — das sind die „Ohren".
5. Der **Preview ▶**-Button im Inspector spielt den Sound schon im Edit-Modus (optional „Listen from camera"
   = 3D relativ zur Editor-Kamera). Kein Play-Modus nötig.

### 3D-Gizmos im Viewport
Ist eine `AudioSource` ausgewählt, zeigt der Viewport zwei Wireframe-Netz-Kugeln — **gelb = Min-Distanz**,
**orange = Max-Distanz** — plus ein **Lautsprecher-Icon** an jeder Audio-Entity (und ein Kopf-Icon an jedem
Listener). So platzierst du Klangquellen präzise; die Kugeln aktualisieren sich live beim Ziehen und beim
Ändern der Distanzwerte.

## 2. Sounds per Skript abspielen (VortexBehaviour)

```csharp
public class ScareTrigger : VortexBehaviour
{
    public override void OnTriggerEnter(TriggerHit hit)
    {
        if (hit.Tag != "Player") return;

        // 3D-One-Shot an einer Weltposition (Knacken, Schuss, Türklirren):
        Audio.PlayOneShot("Assets/Audio/creak.wav", Position, volume: 1f);

        // 2D-One-Shot (UI-Klick, Stinger — keine Entfernung, keine Richtung):
        Audio.PlayOneShot2D("Assets/Audio/stinger.wav");

        // Musik: gestreamt, loopt, mit Fade & CrossFade:
        Audio.Music.CrossFade("Assets/Audio/chase.ogg", seconds: 2f);
    }
}
```

**Die AudioSource dieser Entity direkt steuern** (Play/Stop + weiche Fades):

```csharp
var src = GetAudioSource();
src.FadeIn(3f);          // Ambience langsam einschleichen (kriechende Bedrohung)
src.FadeTo(0.2f, 1f);    // z.B. Herzschlag unter Dialog ducken
src.Stop();              // sofort stoppen
```

**Bus-Lautstärke** (das rufen deine Optionen-Slider auf — im ausgelieferten Spiel bleibt die Einstellung
über Neustarts erhalten):

```csharp
Audio.SetBusVolume("SFX", 0.7f);
Audio.SetBusVolume("Music", 0.4f);
```

## 3. Beispiel: 3D-Schritte mit Variation

1. Rechtsklick im Project-Browser → **Sound Container** (`.vsndc`). Füge mehrere Footstep-`.wav` hinzu,
   setze pro Eintrag ein **Gewicht** und eine **Pitch-/Volume-Range**. Der Container würfelt bei jedem
   Abspielen eine andere Variante (Shuffle-Bag, keine sofortigen Wiederholungen).
2. Im Bewegungs-Skript pro Schritt:

```csharp
// footsteps.vsndc statt einer einzelnen Datei — jede Wiedergabe klingt leicht anders:
Audio.PlayOneShot("Assets/Audio/footsteps.vsndc", Position);
```

Da es ein 3D-One-Shot ist, hören andere Spieler (oder der Listener) die Schritte richtungs- und
entfernungsabhängig.

## 4. Mixer, Ducking und Pegel

Öffne **Window → Audio Mixer**. Fünf feste Busse (Master, Music, SFX, Ambience, UI) mit Fadern, Mutes und
**Live-Pegelanzeigen**. **Ducking** senkt automatisch einen Bus, wenn ein anderer laut wird (z.B. Musik
runter, sobald Dialog spielt) — als Regel im Fenster einstellbar. Die Mixer-Einstellungen liegen in
`ProjectSettings/AudioMixer.json` und gelten **identisch** im Play-Modus und im ausgelieferten Spiel (sie
werden in die `.vpak` gepackt).

## 5. Hall-Zonen (ReverbZone)

**Add Component → Audio → Reverb Zone**. Wähle **Kugel** oder **Box** als Form. Betritt der Listener die
Zone, blendet der algorithmische Hall (Freeverb) mit weichem Übergang (Falloff) ein. Pro `AudioSource`
regelt **Reverb Zone Mix**, wie viel Signal in den Hall geht. Die Zonen-Grenze und die Falloff-Hülle werden
im Viewport als Wireframe-Netz angezeigt.

## 6. HRTF & Verdeckung (Steam Audio — optional, v2)

Für „das Monster ist **hinter** dir und einen Raum weiter": Vortex integriert **Steam Audio** (Valve) als
optionale v2-Schicht mit echtem binauralem HRTF (Vorne/Hinten/Oben/Unten auf Kopfhörern) und
**strahlengestützter Verdeckung** an der Kollisionsgeometrie.

Standardmäßig **aus** (ändert nichts am v1-Klang). So aktivierst du es:

1. `phonon.dll` bereitstellen: `ThirdParty\steam-audio\fetch-phonon-dll.ps1` einmal ausführen (lädt die
   ~50 MB Laufzeit neben die App). Ohne die DLL fällt alles automatisch auf den v1-Spatializer zurück.
2. Im **Audio Mixer** den Projekt-Master-Schalter **Steam Audio** einschalten.
3. Pro `AudioSource` im Inspector **HRTF binaural** aktivieren (und optional **Occlusion behind walls** —
   braucht HRTF + eine 3D-Quelle).

Verdeckung nutzt die Kollisionsgeometrie deiner Szene: Steht eine Box-/Mesh-Wand zwischen Quelle und
Listener, wird der Klang gedämpft; ein offener Durchgang stellt ihn wieder her. Die Ray-Simulation läuft in
einem eigenen Thread, nicht im Audio-Callback — auch bei 20+ verdeckten Quellen keine Aussetzer.
Details: [[Design-Steam-Audio-Integration]].

## 7. Auslieferung

Beim **Build/Export** werden alle referenzierten Clips, Container und die Mixer-Konfiguration in die `.vpak`
gepackt — kein loses Datei-Lesen im ausgelieferten Spiel. Streaming (Musik/lange Ambience) läuft direkt aus
den gepackten Daten. Lautstärke-Einstellungen, die der Spieler zur Laufzeit ändert, bleiben über Neustarts
erhalten (`%LocalAppData%\Vortex\<Spiel>\audio-settings.json`). `phonon.dll` wird nur mitgeliefert, wenn das
Projekt Steam Audio aktiviert hat.

---

Siehe auch: [[Design-Audio-Engine]] (Architektur v1) · [[Design-Steam-Audio-Integration]] (Architektur v2).
