# Release Process

How a change becomes a Vortex release: what runs where, what must pass, how users get it, and what to do when GitHub
gets in the way. Written for maintainers and contributors; players of the engine only need [[Getting-Started]].

## From change to release

1. **Pull request.** `pr-tests.yml` runs on Windows and macOS. Both checks, `windows` and `macos`, are required on `main`.
   - **Windows:**
     - The MSBuild of `Vortex.slnx` (engine, `VortexAPI.dll`, the classic WPF editor).
     - The managed and core tests.
     - The cross-platform editor staged as the installer lays it out (`tools/windows/stage-editor.ps1`), then its smoke run on DX12 / WARP. The smoke checks `mcp`, `claude`, `title bar` and `update window`; the step fails unless `mcp tools` and `title bar` pass.
   - **macOS:**
     - CMake and CTest (the native tests, including the gamepad test).
     - The managed layer.
     - The core tests, which compile every C# sample in this wiki as C# 5. So a wiki page is code too.
2. **Merge into `main`.** `build-macos.yml` builds the Mac app and the DMG and runs the editor smoke on real Metal.
3. **Version bump.** Put it in a release commit, or in the last PR:
   - `Editor/Core/EngineInfo.cs` (`Version`). The updater compares it with the latest release.
   - `Installer/VortexEngine.iss` (`MyAppVersion`). Keep the file's UTF-8 BOM.
   - When Claude tools were added or removed:
     - regenerate the tool reference with `Vortex.Editor --mcp-tools-md=docs/wiki/Claude-Tools.md`;
     - update the tool counts in the README, [[Home]] and the [[Feature-Status-Matrix]].
4. **Tag `v<version>` on `main`.** This starts three workflows:
   - `build-release.yml`: **build** (installer and portable zip), then **installer-test**, then **release**.
   - `build-macos.yml`: the app, the DMG and the smoke. It attaches the DMG to the release and waits up to 30 minutes for the release to appear.
   - `build-runtime-pack-windows.yml`: the Windows runtime pack that exported games are built from (an artifact).
5. **The installer test.** Nothing is published unless it passes. On a clean Windows runner it:
   - installs the previous release, then updates it with this build's installer, silently, as the in-app updater does;
   - checks that the update starts the new editor, that v2.x's relaunch of `Vortex Engine.exe` ends at once, and that the classic editor started on its own stays open;
   - checks the files, the Start menu target and the `.vortex` association;
   - runs the installed editor's smoke (DX12 / WARP);
   - plays a **real in-app update**. The installed editor hands over to the installer (`UpdateService.InstallAndRestart`). Exactly one editor must come back, and closing it must not start it again.
6. **After the release exists:**
   - **Template packs.** Every release needs them, patches too: an installed editor downloads `Template-<Id>.zip` from the release of *its own* version. Build them with `tools/make-template-packs.sh <version>`; a patch with unchanged templates can reuse the last ones. Then run `gh release upload v<version> dist/template-packs/Template-*.zip`.
   - **Notes and Latest.** Replace the workflow's generic body with the release notes and mark the release Latest: `gh release edit v<version> --title "…" --notes-file notes.md --latest`.
   - **Check** that the DMG arrived and that `gh api repos/shadow-kernel/Vortex-Engine/releases/latest` names the new tag.

## How users get it

- **Windows:**
  - *Vortex Engine* in the Start menu is the cross-platform editor (`{app}\Editor\Vortex.Editor.exe`), the same one as on macOS and Linux.
  - v3.0 also installs *Vortex Engine (Classic)*, the previous WPF editor. It goes away in v3.1 (#311).
  - An installed editor checks GitHub when it starts. A **patch** installs by itself. A minor or major release shows its notes first (*Install and Restart*). *Help ▸ Check for Updates…* checks on demand.
- **macOS:** the DMG from the release. It is not notarised yet, so the first time, right-click ▸ **Open**.
- **Linux:** from source (see the README).
- **Templates:** the Horror Starter and Tactical Shooter content downloads once per engine version, the first time a project is created from them ([[Project-Templates]]). *Update from Template…* brings existing projects to the new template version.

## When GitHub Actions is down

- **A job "was not acquired by Runner … even after multiple attempts":** nothing is wrong with the code. Rerun the failed jobs with `gh run rerun <run> --failed`. [githubstatus.com](https://www.githubstatus.com) shows incidents.
- **Build and installer-test are green, but the release job never gets a runner:** publish the *tested* artifacts yourself.

  ```bash
  gh run download <run> -n VortexEngine-Installer-<version> -D rel/installer
  gh run download <run> -n VortexEngine-Portable-<version> -D rel/portable
  (cd rel/portable && zip -qr ../VortexEngine-Portable-<version>.zip .)
  gh release create v<version> --verify-tag --title "Vortex Engine v<version>" --notes-file notes.md --latest rel/installer/*.exe rel/VortexEngine-Portable-<version>.zip
  gh run cancel <run>
  ```

  Cancel the stale run. Otherwise its release job runs later and overwrites the notes.
- **The DMG**, when `build-macos.yml` gave up waiting for the release:

  ```bash
  gh run download <macOS run> -n vortex-editor-macos-arm64 -D rel/dmg
  gh release upload v<version> rel/dmg/*.dmg --clobber
  ```

## Milestones

- **The plan is the GitHub milestones.** [[Roadmap]] (`ROADMAP.md`) and the pinned issue #188 mirror them; update both when a status changes.
- **A milestone closes when every issue in it is closed or moved.**
  - A closed issue has a comment: what was done, how it was checked, and any deviation from its acceptance criteria.
  - A moved issue says why it moved and goes to the milestone where it belongs.
- **Checks that need a person or real hardware** (a GPU, a fresh Windows PC, an e-mail to a third party) become their own issue. They should not hold a milestone open. Example: #315.
- **After the release:** update [[Roadmap]] and #188, then close the milestone.

## Claude, end to end

Before a release that changes the Claude integration, run a real Claude Code session against an isolated editor:

1. Start the editor with a private `VORTEX_APPDATA_DIR`. Its `VortexEngine/editor-prefs.json` must enable the server: `"McpServerEnabled": true, "McpPort": 7431`.
2. Point Claude Code at that server only, through `mcp.json`:

   ```bash
   claude -p "<prompt>" --mcp-config mcp.json --strict-mcp-config --allowedTools "mcp__vortex" --output-format stream-json --verbose < /dev/null
   ```

   where `mcp.json` holds `{ "mcpServers": { "vortex": { "type": "http", "url": "http://127.0.0.1:7431/mcp" } } }`.

The v3.0 checks used one prompt: build a corridor and sound it completely (#99). They also covered play mode (#90) and
the project's own `.mcp.json` (#93). The v3.0 trailer (`docs/showcase/vortex-3.0-claude.webp`) is such a session,
recorded:
- start the editor with `VORTEX_SHOWCASE=<dir> VORTEX_SHOWCASE_SESSION=<seconds>`;
- run Claude Code;
- run `tools/showcase/compose_session.py <dir>`. It turns the frames and Claude's timestamped tool calls into the WebP.
