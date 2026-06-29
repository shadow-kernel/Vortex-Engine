# NVIDIA Streamline SDK 2.12.0 (vendored, selective)

For the planned **DLSS 4** integration (super resolution + Multi Frame Generation) in the Vortex renderer.
NOT wired into the engine yet — see `ROADMAP_NATIVE_RENDER.md` Phase 5. This folder only makes the SDK
available so the renderer can later `#include <sl.h>`, link `sl.interposer.lib`, and ship the runtime DLLs.

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
