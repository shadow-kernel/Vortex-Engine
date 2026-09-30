# Avalonia editor — parity with the Windows (WPF) editor

Goal: every window, dialog, mini editor and gesture of the WPF editor (`Editor/`) exists in the Avalonia editor
(`Managed/Vortex.Editor`) on macOS, with the same behaviour. The WPF sources are the reference implementation — port
behaviour, not look: the Avalonia editor has its own theme (`Theme/VortexTheme.axaml`, brushes `Vx*Brush`, icons via
`Controls/VxIcon` with names from `Theme/Icons.axaml`, styles `Classes = { "small", "secondary", "icon", … }`).

## Shared foundation (already in place — use it, don't duplicate it)
| API | File | Use |
|---|---|---|
| `PreviewRenderer.Render(PreviewScene, w, h, PreviewCamera)` → `PreviewImage` (BGRA) | `Managed/Vortex.Core/Rendering/PreviewRenderer.cs` | offscreen 3D render of meshes (+ bone palettes, attachments via world matrices, gizmos via `SubmitGizmos` + `RenderGizmos`), studio lighting; `RenderModelFile`, `RenderMaterialFile`, `RenderPrefabFile`, `RenderPrimitive`, `ComputeFrame` |
| `PreviewModel.Load(model) / LoadPrefab(ventity, projectRoot) / MaterialSphere(vmat) / FromOwned(meshes, materials) / CreatePrimitive(name)` | same file | engine resources for an interactive preview; `Dispose()` releases them |
| `VortexEditor.Controls.PreviewViewport` | `Controls/PreviewViewport.cs` | interactive orbit preview control: `LoadAsset(path)`, `Model` (owned) / `Scene` (caller-owned), `Camera`, `Continuous`, `AutoRotate`, `BeforeRender`, `RenderNow()`, `LastImage` |
| `VortexEditor.Services.ThumbnailService.Request(fullPath, size, bitmap => …)` | `Services/ThumbnailService.cs` | async thumbnails (images decoded, models/materials/prefabs/primitives rendered), memory + disk cache `<project>/.ve/thumbs`, `Invalidate(path)` |
| `VortexEditor.Shell.EditorWindows` | `Shell/EditorWindows.cs` | open ANY editor window; `OpenEditorFor(path)` (Shift+double-click), `OpenLargePreview(path)` (Ctrl/Cmd+double-click), `Show(window)` |
| `VortexEditor.Services.AssetActions.AddToScene / OpenDefault` | `Services/AssetActions.cs` | plain double-click / drop-into-scene behaviour (implemented by the Asset Browser package) |
| `VortexEditor.Shell.SmokeRegistry.Add(name, async () => bool)`, `Capture(visual, "file.png")`, `Settle(ms)` | `Shell/SmokeRegistry.cs` | self-registering smoke checks (`[ModuleInitializer]` in YOUR file) + window screenshots |
| `VortexEditor.Panels.Inspector.ComponentEditors.Custom[typeof(T)] = (c, e) => rows` | `Panels/Inspector/ComponentEditors.cs` | register an inspector card from your own file |
| `PropertyRows.Row/FloatBox/IntBox/SliderRow/Vector3/Bool/Enum/Text/AssetPath` | `Panels/Inspector/PropertyRows.cs` | property editors (AssetPath accepts drag & drop) |
| drag & drop of assets | format `"vortex/asset"` = project-relative path string; OS file drops via `DataFormats.Files` | Asset Browser drags, inspector fields / viewport / hierarchy accept |
| `Dialogs.Alert / Confirm / Prompt` | `Shell/Dialogs.cs` | message boxes |

Asset double-click convention (WPF, keep it): **plain** = default action (models / prefabs / primitives → add to the
scene, scene → open, script → open in the code editor, sound container → its editor, clip → animation editor),
**Shift** = the asset's editor (`EditorWindows.OpenEditorFor`), **Ctrl/Cmd** = large preview window
(`EditorWindows.OpenLargePreview`).

## Rules for every work package
* Only edit the files your package owns (table below). Need a hook elsewhere? Use the registries above or report the
  one-line change you need — don't edit another package's files.
* Rendering calls (anything that touches `VortexAPI` render/mesh functions) run on the UI thread.
* Keep `Editor/Editor.csproj` (the Windows WPF build) compiling: shared code under `Editor/` must stay WPF-free where it is
  linked into `Vortex.Core`; new shared files under `Editor/` need a `<Compile Include>` in `Editor/Editor.csproj`.
* Build: `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec dotnet build Managed/Vortex.Managed.slnx -c Debug`.
* Run (never touch the user's settings — ALWAYS set a private app-data dir):
  `VORTEX_NATIVE_DIR=/Users/danielbrueckmann/DEV/Vortex-Engine/build/macos-debug/bin VORTEX_APPDATA_DIR=<tmp>/appdata
  VORTEX_SMOKE_FULL=1 Managed/Vortex.Editor/bin/Debug/net10.0/Vortex.Editor --project=<copy of a project> --smoke=20 --capture=<tmp>/cap`
  Test projects: copy `Templates/HorrorStarter` or `Templates/Default3D` from `/Users/danielbrueckmann/DEV/Vortex-Engine`
  (rsync, never edit the originals). Look at your window screenshots (`SmokeRegistry.Capture`).
* No commits to `main`, no pushes — commit on your worktree branch; the coordinator merges.
