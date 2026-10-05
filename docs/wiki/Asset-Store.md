# The Asset Store

The **Store** tab of the Asset Browser (third tab, after Explorer and Library) brings free, legally clean assets into the editor. Everything you download lands in your [[Asset-Library]] — stored once, with its source, author and license — and, if you want, straight in the open project. Design: [[Design-Asset-Store-Integrations]].

## Sources

| Source | What | Account | License |
|---|---|---|---|
| **Poly Haven** | models (glTF), PBR texture sets, HDRIs | none | CC0 |
| **ambientCG** | 2,800+ PBR materials → a ready `.vmat` | none | CC0 |
| **Kenney** | curated packs: 3D kits, UI, audio, VFX | none | CC0 |
| **poly.pizza** | low-poly models (Quaternius & more), often rigged | your free API key | CC0 / CC-BY per model |
| **Freesound** | 600,000+ sound effects, audition before download | your free API token | CC0 / CC-BY (NonCommercial hidden by default) |
| **Sketchfab** | 1M+ downloadable models | search: none · download: your API token | per model (NonCommercial hidden by default) |
| **Mixamo** | rigged characters + animations | your free Adobe account | royalty-free in games, no re-sharing |
| **Sonniss GDC** | professional sound libraries | none (download in your browser) | royalty-free, no attribution, no re-sharing |

Keys stay on your machine (`store-keys.json` next to the editor settings, readable only by you). The editor never ships a shared key. Add them with **API Key…** in the Store's top bar, the gear button, or the prompt that appears when a source needs one.

## Searching

Pick a source on the left, type in the Asset Browser's search box, choose a kind (Models / Materials / HDRIs …) and a category. Each tile shows the license as a coloured badge:

- **green** — CC0 / public domain
- **blue** — attribution required (credited automatically on export)
- **orange** — ShareAlike, NoDerivatives or not re-shareable
- **red** — NonCommercial or unknown

A small library icon marks results that are already in your library. Click a result for its license text, author, source, sizes and formats; click a Freesound result to hear its preview.

## Downloading

- **Download + Add to Project** puts the asset into the library and copies it into the project (models in their own folder with their textures; materials as `<name>.vmat` + `textures/`).
- **Download to Library** keeps it in the library only — add it to any project later from the Library tab.
- Double-click a result to download it to the library.

The **Downloads** strip shows progress; you can cancel and retry (partial downloads resume). Files are checked against the checksums the source publishes. Something you already downloaded is not downloaded again.

What arrives:

- **ambientCG / Poly Haven textures** → a `.vmat` with colour, normal (OpenGL convention), roughness/metal/AO (or a packed ARM map) and height wired up.
- **Poly Haven HDRIs** → a `.hdr` file tagged *HDRI* — use it as the skybox texture.
- **Kenney packs** → every model (GLB preferred), sprite, sound and font of the pack becomes its own library asset, tagged with the pack name.
- **Freesound** → the HQ preview (OGG); original-quality downloads need a Freesound login (planned).

## Mixamo and Sonniss (guided)

These have no API, so the Store explains the steps:

- **Mixamo:** open mixamo.com, download FBX files, then drop them on the Mixamo page — or tick *Watch my Downloads folder* and Vortex adds every new FBX by itself. Adding one to a project extracts the skeleton and animation clips.
- **Sonniss:** download and extract a GDC bundle in your browser, then *Index a Folder…* — every WAV becomes a searchable, playable library asset (sub-folders become tags).

Both are marked *not re-shareable*: they never go into a library bundle.

## Licenses in your game

Assets that came through the Store or the library carry their license, author and source in their `.vmeta`. When you **build** the game:

- `CREDITS.md` is written next to the game, crediting every attribution-required asset (author, title, source, license link) plus your project's own `ATTRIBUTIONS.md`.
- Before the build starts, a **license check** lists NonCommercial, NoDerivatives, ShareAlike and unknown-license assets so you can decide before shipping.
