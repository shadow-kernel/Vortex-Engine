# Third-Party Notices

Vortex Engine is licensed under the [MIT License](LICENSE). It bundles or links
the following third-party components, each under its own license. All are
permissive (BSD / MIT / Ms-PL / Apache-2.0 / public domain) and compatible with
Vortex's MIT license. The one exception is NVIDIA DLSS/Streamline, which is
optional and governed by NVIDIA's separate proprietary license (see below) — it
is **not** bundled in this source repository.

---

## Bundled in this repository

### Assimp — Open Asset Import Library
- **Use:** model importing (FBX, OBJ, glTF, …). Prebuilt `assimp-vc143-mt` DLL/LIB + headers.
- **License:** BSD 3-Clause. © 2006-2025, assimp team.
- **Text:** [`ThirdParty/assimp6/LICENSE`](ThirdParty/assimp6/LICENSE) · upstream: https://github.com/assimp/assimp

### stb_image
- **Use:** image/texture decoding (`Engine/ThirdParty/stb_image.h`).
- **License:** Dual Public Domain (Unlicense) / MIT. © Sean Barrett.
- **Text:** embedded at the bottom of `Engine/ThirdParty/stb_image.h` · upstream: https://github.com/nothings/stb

### miniaudio
- **Use:** audio engine backend — playback device (WASAPI), mixing node graph, WAV/FLAC/MP3 decoding (`Engine/ThirdParty/miniaudio.h`, v0.11.22).
- **License:** Dual Public Domain (Unlicense) / MIT-0 ("No Attribution"). © David Reid.
- **Text:** embedded at the bottom of `Engine/ThirdParty/miniaudio.h` · upstream: https://github.com/mackron/miniaudio

### stb_vorbis
- **Use:** OGG Vorbis decoding, hooked into miniaudio (`Engine/ThirdParty/stb_vorbis.c`, v1.22).
- **License:** Dual Public Domain (Unlicense) / MIT. © Sean Barrett.
- **Text:** embedded at the bottom of `Engine/ThirdParty/stb_vorbis.c` · upstream: https://github.com/nothings/stb

### Steam Audio (Valve Phonon SDK)
- **Use:** optional audio v2 layer (issue #21) — HRTF binaural spatialization + ray-traced occlusion, plugged into
  miniaudio's node graph as a custom per-voice node. Off by default (opt-in per project + per source). Headers +
  import lib are committed (`ThirdParty/steam-audio/include`, `ThirdParty/steam-audio/lib/windows-x64/phonon.lib`);
  the runtime `phonon.dll` is **loaded dynamically at run time** and is fetched separately (not committed — see
  `ThirdParty/steam-audio/fetch-phonon-dll.ps1`), so the engine builds and boots without it.
- **License:** Apache License 2.0. © Valve Corporation. Vendorable into this MIT repo with the NOTICE preserved.
- **Text:** [`ThirdParty/steam-audio/LICENSE`](ThirdParty/steam-audio/LICENSE) · upstream: https://github.com/ValveSoftware/steam-audio (v4.8.1)

---

## Fetched at configure time by the CMake build (non-Windows only; not committed here)

### DirectXMath
- **Use:** the engine's math library on every platform. On Windows it comes with the Windows SDK; the CMake
  build for macOS/Linux downloads the pinned `may2026` release tarball (`cmake/VortexDependencies.cmake`).
  `ThirdParty/sal/sal.h` is a Vortex-authored stub of the SAL annotations those headers expect, not third-party code.
- **License:** MIT. © Microsoft Corporation. Upstream: https://github.com/microsoft/DirectXMath

### Recast & Detour (recastnavigation)
- **Use:** AI & Navigation (issues #108-#110) — navmesh baking (Recast), the tiled navmesh with path / point / raycast
  queries (Detour) and agent steering with local avoidance (DetourCrowd), statically linked into the engine
  (`Engine/Navigation`). The CMake build fetches the pinned `v1.6.0` tag (`Engine/CMakeLists.txt`,
  `VORTEX_ENABLE_RECAST`); the Visual Studio projects build the no-navigation stub until they link it too.
- **License:** zlib. © 2009 Mikko Mononen (memon@inside.org). Vendorable into this MIT repo; the zlib license only
  requires that the notice is kept in source distributions and that altered versions are marked as such (none are).
- **Text:** `License.txt` in the fetched source (`build/<preset>/_deps/recastnavigation-src/License.txt`) · upstream:
  https://github.com/recastnavigation/recastnavigation

## Linked into the editor (restored via NuGet at build; not committed here)

### Dirkster.AvalonDock
- **Use:** docking layout for the WPF editor.
- **License:** Microsoft Public License (Ms-PL). Its copyright notice is retained per Ms-PL §3(C).
- Upstream: https://github.com/Dirkster99/AvalonDock

### Microsoft .NET libraries
`System.Text.Json`, `System.Text.Encodings.Web`, `System.IO.Pipelines`,
`Microsoft.Bcl.AsyncInterfaces`, `System.Buffers`, `System.Memory`,
`System.Numerics.Vectors`, `System.Runtime.CompilerServices.Unsafe`,
`System.Threading.Tasks.Extensions`, `System.ValueTuple`.
- **License:** MIT. © Microsoft Corporation. Upstream: https://github.com/dotnet/runtime

## The cross-platform editor and player (`Managed/`, bundled in the self-contained macOS / Linux apps)

### Avalonia
- **Use:** the UI framework of the cross-platform editor (`Avalonia`, `.Desktop`, `.Themes.Fluent`, `.Fonts.Inter`,
  `.Controls.ColorPicker`).
- **License:** MIT. Copyright 2013-2025 © The AvaloniaUI Project. Upstream: https://github.com/AvaloniaUI/Avalonia

### SkiaSharp / HarfBuzzSharp (via Avalonia)
- **Use:** 2D rendering and text shaping of the editor UI; SkiaSharp also encodes the viewport images the Claude
  tools return.
- **License:** MIT, © Microsoft Corporation. The native assets contain Skia (BSD-3-Clause, © Google LLC) and HarfBuzz
  (the "Old MIT" license). Upstream: https://github.com/mono/SkiaSharp

### Tmds.DBus.Protocol (via Avalonia, Linux)
- **License:** MIT. © Tom Deseyn. Upstream: https://github.com/tmds/Tmds.DBus

### Roslyn (Microsoft.CodeAnalysis.CSharp)
- **Use:** compiles the project's C# scripts in-process (`Managed/Vortex.Core/Scripting/RoslynScriptCompiler.cs`).
- **License:** MIT. © Microsoft Corporation. Upstream: https://github.com/dotnet/roslyn

### Model Context Protocol C# SDK
- **Use:** the editor's Claude MCP server (`Managed/Vortex.Editor/Claude/`) — packages `ModelContextProtocol.AspNetCore`,
  `ModelContextProtocol`, `ModelContextProtocol.Core` (2.2.0).
- **License:** Apache License 2.0. © Model Context Protocol a Series of LF Projects, LLC.
  Text: https://www.apache.org/licenses/LICENSE-2.0 · upstream: https://github.com/modelcontextprotocol/csharp-sdk

### ASP.NET Core (Kestrel) and Microsoft.Extensions
- **Use:** the HTTP host of the MCP server (framework reference `Microsoft.AspNetCore.App`, bundled by the self-contained
  publish) and its dependencies `Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Caching.Abstractions`.
- **License:** MIT. © .NET Foundation and Contributors / © Microsoft Corporation.
  Upstream: https://github.com/dotnet/aspnetcore · https://github.com/dotnet/extensions

### SDL3 (macOS / Linux)
- **Use:** window, input and the SDL GPU renderer (Metal / Vulkan) of the CMake build; linked from the system package
  (`brew install sdl3` / `libsdl3-dev`) and copied into the macOS app bundle.
- **License:** zlib. © 1997-2025 Sam Lantinga. Upstream: https://github.com/libsdl-org/SDL

### Jolt Physics (macOS / Linux CMake build)
- **Use:** Physics v2 (rigid bodies, characters, ragdolls, constraints) — fetched at a pinned tag and statically linked
  (`Engine/CMakeLists.txt`, `VORTEX_ENABLE_JOLT`).
- **License:** MIT. © 2021 Jorrit Rouwe. Upstream: https://github.com/jrouwe/JoltPhysics

---

## Optional — NVIDIA DLSS / Streamline (NOT bundled)

Vortex can optionally use **NVIDIA Streamline** (DLSS Super-Resolution and Frame
Generation). It is included **only as a git submodule pointer** to NVIDIA's own
public repository — no NVIDIA source or binaries are stored in this repository.

- **Streamline SDK:** © NVIDIA CORPORATION — https://github.com/NVIDIA-RTX/Streamline (MIT for the SL layer; see the submodule's `license.txt` and `3rd-party-licenses.md`).
- **DLSS / NGX models and runtime DLLs** (`nvngx_dlss*.dll`, `sl.*.dll`) are **NVIDIA-proprietary**, governed by the *NVIDIA RTX SDKs License* (`ThirdParty/Streamline/external/ngx-sdk/license.txt`). They are **not** committed to this repo; they are fetched at build time from a locally-installed NVIDIA SDK (`Redist/Streamline/prebuild.ps1`) and are optional — Vortex builds and runs fine without them (DLSS is simply disabled).
- **If you ship a build with DLSS enabled**, NVIDIA's terms require you to: attribute NVIDIA (splash screen / About box / credits), notify NVIDIA before commercial release (https://developer.nvidia.com/sw-notification), and ship the production (not development) DLSS model registered with NVIDIA. These obligations are yours as the distributor of a DLSS-enabled build; they do not apply to this MIT source repository.

---

## Assets

The default 3D template ([`Templates/Default3D`](https://github.com/shadow-kernel/Vortex-Engine-3D-Template)) bundles:

- **Kenney models** (furniture set) — CC0 1.0 (public domain), by [Kenney](https://kenney.nl).
- **Textures** — procedurally generated by the Vortex authors (CC0-equivalent, no third-party rights).
- See that repository's `CREDITS.md` for the full asset attribution.
