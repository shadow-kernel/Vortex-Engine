# NVIDIA Streamline SDK 2.12.0 (vendored, selective)

For the planned **DLSS 4** integration (super resolution + Multi Frame Generation) in the Vortex renderer.
NOT wired into the engine yet — see `ROADMAP_NATIVE_RENDER.md` Phase 5. This folder only makes the SDK
available so the renderer can later `#include <sl.h>`, link `sl.interposer.lib`, and ship the runtime DLLs.

## Why binaries, not source (this is correct — do not "replace with source")
DLSS is **not buildable from source**. The DLSS feature plugins (`sl.dlss.dll`, `sl.dlss_g.dll`) and the
NGX AI models (`nvngx_dlss.dll`, `nvngx_dlssg.dll`) are **proprietary, closed-source NVIDIA binaries** —
NVIDIA does not publish their source, and there is nothing to compile. This is the official and ONLY
integration model: every DLSS title ships these exact DLLs as redistributables. The "source side" you
compile against is the **headers** (`include/`); the link side is the **import lib**
(`lib/x64/sl.interposer.lib`); the runtime side is the **DLLs** (`bin/x64/`) that must sit next to the exe.
So this layout is the standard, correct one — there is no source build to add for the DLSS parts.

"Building it into the engine build process" = the Phase 5 integration, NOT vendoring source: add the
include dir + link `sl.interposer.lib` in `Engine.vcxproj`, `#include <sl.h>` in the renderer, and a
**post-build copy step** that places the required runtime DLLs into the engine/game output. (Streamline's
own *interposer* IS open source on GitHub and could be vendored + built, but the recommended path for
integrators is the prebuilt `sl.interposer.lib` here — and it would still not remove the proprietary DLLs.)

## What's vendored here (LFS for binaries)
- `include/` — the full Streamline headers (`sl.h`, `sl_dlss.h`, `sl_dlss_g.h`, `sl_reflex.h`, …)
- `lib/x64/sl.interposer.lib` — import lib to link against
- `bin/x64/` — the **Release** runtime DLLs needed for DLSS-SR + DLSS-Frame-Generation:
  - `sl.interposer.dll`, `sl.common.dll`, `sl.dlss.dll`, `sl.dlss_g.dll`, `sl.reflex.dll`, `sl.pcl.dll`
    (+ the other small `sl.*.dll` features for completeness)
  - `nvngx_dlss.dll` (super-resolution model), `nvngx_dlssg.dll` (frame-generation model)
  - the per-feature `*.license.txt`

## Deliberately NOT vendored (to keep the repo lean)
- `nvngx_dlssd.dll` (~41 MB, DLSS ray-reconstruction — only needed for ray-traced DLSS-D)
- `nvngx_deepdvc.dll` (DeepDVC), `bin/x64/development/` (debug builds)
- `docs/`, `symbols/` (PDBs), `source/`, `external/`, `utils/`, `tools/` (~300 MB)

Full SDK: NVIDIA Streamline v2.12.0 (https://github.com/NVIDIAGameWorks/Streamline). Requires an
RTX 50-series GPU for DLSS 4 Multi Frame Generation (target: RTX 5070).
