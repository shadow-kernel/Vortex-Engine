# The Asset Library

Every asset you import into any Vortex project also lands in your machine-wide **asset library** — stored once (identified by the SHA-256 of its bytes), searchable from every project, ready to be added to the next game with one click. Design and internals: [[Design-Global-Asset-Database]].

## The Library tab

The **Library** tab sits in the bottom dock next to **Project**, **Asset Store** and **Console** (*Window → Library*, ⌘/Ctrl+7).

- **Search** with the tab's search box — it matches names, file names and tags.
- **Type chips** (All, Models, Textures, Materials, Audio, Animations, Prefabs, Other) narrow the grid.
- **Tags:** the tag button filters by library tags (every checked tag must match).
- **Sort:** Name, Type, Date Added or Size.
- **Saved filters** (bookmark button): save the current search + type + tags under a name and recall it later.
- The badge on a tile shows where the asset came from (the project it was imported into, *Added*, a store provider, …).

Select an asset to see its details on the right: a preview, size and format facts, tags (add or remove them right there), its source and license, the files that travel with it (a model's textures, materials and clips) and **every project that uses it**. Rename it in the name field — projects keep their own file names.

Audio tiles play when you click them (or press Space); Esc stops the preview. No project needs to be open to listen.

## Looking at an asset without adding it

**Shift-** or **⌘/Ctrl-double-click** a model, texture or material (or press **Space**, or use **Preview** in the details pane or the context menu) to open it in the viewer — the Model Viewer for models, the texture and material previews for the others — straight from the library. Nothing is copied into the project: the file and the files that travel with it (buffers, textures) are cloned into the library's temporary folder for the viewer, and the window title says *(Library)*.

## Adding an asset to your project

- **Double-click** a tile (without Shift / ⌘), press **Enter**, or use **Add to Project** in the details pane or the context menu — the asset is copied into the type's default folder (`Assets/Models`, `Assets/Textures`, `Assets/Audio`, …). **Add to Project in Folder…** lets you pick the folder.
- **Drag** a tile into the viewport, onto an inspector slot (e.g. an Audio Source's clip) or onto a folder in the file tree — the asset is added first, then dropped.
- **Add to Scene** (models and prefabs) adds the asset and places it in front of the camera.

A model comes with everything next to it (buffers, textures, materials, animation clips) in its own folder. The copy gets a fresh `.vmeta` with the library's tags and content hash. If the project already contains exactly these bytes, the editor offers to **show** the existing file instead of making a second copy.

## Importing

Imports work as before — and are added to the library in the background (they never wait for it). When the import dialog sees a file the library already has, it says so:

- **Use the library entry** — takes the library's name and tags; the library keeps one entry.
- **Import as new** — the library gets a second entry with this import's name and tags. The bytes are still stored only once.

With several files, each known file has an *import as new* switch in the file list.

## Filling the library from existing projects

**Library tools (⋯) → Index Existing Projects…** lists the projects from the Project Hub (add any other project folder with *Add Folder…*). Indexing stores every asset once — the same texture in three projects becomes one entry used by three projects — carries the tags of `.vmeta` files over and writes the content hash into existing `.vmeta` files. Nothing else in your projects changes. You can stop it at any time and run it again later; it continues where it stopped.

**Add Files** in the Library tab's header (or dropping files from Finder/Explorer onto the tab) adds loose files without any project. **Asset Store** in the same header opens the free asset sources — everything downloaded there lands in the library too.

## Tags

Library tags are separate from a project's tags: they are copied in when an asset is registered and copied out when it is added to a project. **Tag Manager…** renames a tag everywhere (renaming onto an existing tag merges both), merges and deletes tags. Select several tiles (⌘/Ctrl-click, Shift-click, ⌘/Ctrl+A) to add or remove tags in bulk.

## Maintenance

**Library tools → Maintenance…**

- How much space the library uses — by type, by source, the largest assets.
- **Verify Files** re-reads every stored file and checks it against its hash.
- **Find Problems** looks for stray or unreferenced stored files, missing files and stale thumbnails; **Fix Problems** repairs them after you have seen the list.
- **Collect Garbage** frees stored files nothing refers to any more.

Deleting assets from the library (context menu, Delete key or the details pane) never touches projects that use them — they keep their copies.

**Bundles:** *Export to Bundle…* writes the selected assets (or the current results) to a `.vlib.zip` you can import on another PC (*Import Bundle…*); assets whose license forbids redistribution are left out.

## Settings

**Library tools → Library Settings…**

- **Location** — *Move Library…* copies the library to another folder or drive, verifies every file and removes the old copy. Close other Vortex editors first.
- **Size cap** — the library warns when it grows past the cap; it never deletes anything on its own.
- **Automatic registration** — switch off adding imports automatically, and choose which asset types are taken (scripts and scenes are off by default). *Add Files to Library* always works.

## Content hashes in your project

Every asset's `.vmeta` can carry a `ContentHash` (SHA-256 of the file). Imports and *Add to Project* write it; **Library tools → Backfill Content Hashes (Project)** hashes all assets of the open project at once.

## Where it lives

| Platform | Folder |
|---|---|
| Windows | `%LOCALAPPDATA%\VortexEngine\AssetDB` |
| macOS | `~/Library/Application Support/VortexEngine/AssetDB` |
| Linux | `~/.local/share/VortexEngine/AssetDB` |

The library uses the SQLite that ships with the operating system. The Windows editor (WPF) already adds every import to the library; the Library tab comes to Windows with the Avalonia editor ([#183](https://github.com/shadow-kernel/Vortex-Engine/issues/183)).
