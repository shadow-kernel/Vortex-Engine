# Project Templates

The Project Hub's **Create** page starts a new game from a template:

| Template | What you get | Download |
|---|---|---|
| **Empty Project** | Camera, directional light, ground plane | — |
| **Default 3D** | Lobby + Match scenes, player controller, UI | ~3 MB |
| **Horror Starter** | CoD-feel night shooter: yard + cellar maps, two weapons, arms rig, 30+ props | ~450 MB |
| **Tactical Shooter** | Shooting range, operator body, ragdolls, weapon set | ~275 MB |

## How templates arrive

The templates' models, textures and sounds live in Git LFS. The installer and the CI builds don't carry that content
(LFS bandwidth), so an installed editor finds the templates' structure without their big files. The first time you
create a project from such a template, the editor **downloads the template pack of your Vortex version** from the
GitHub release (`Template-<Name>.zip`) — the Create page shows the size, the Create button the progress — and keeps it
for every later project:

- macOS / Linux: `~/Library/Application Support/VortexEngine/Templates/<version>/` (Linux: `~/.config/VortexEngine/…`)
- Windows: `%APPDATA%\VortexEngine\Templates\<version>\`

Delete the folder to free the space; the next project downloads the pack again. Projects themselves never depend on the
cache — they are full copies.

A source checkout with `git lfs pull` in the template submodules uses its own files and downloads nothing.

## For maintainers: publishing the packs

Each release needs the packs of its version. On a machine with the LFS content:

```bash
tools/make-template-packs.sh 3.0.0 --upload
```

It pulls the LFS objects per template, refuses to pack pointer files, writes `dist/template-packs/Template-<Id>.zip`
and uploads them to the `v3.0.0` release. Code: `Editor/Core/Services/TemplatePacks.cs` (detection, download, cache,
zip-slip-safe unpacking), used by both Project Hubs.
