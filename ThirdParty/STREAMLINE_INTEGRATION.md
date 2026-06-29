# NVIDIA Streamline — integrated AS SOURCE (git submodule)

The Streamline SDK (for DLSS 4: Super Resolution + Multi Frame Generation) is vendored **as a git
submodule of its source repo**, NOT as committed binaries. Our repo stores only a commit pointer.

- Submodule: [`ThirdParty/Streamline`](Streamline) → `https://github.com/NVIDIA-RTX/Streamline.git`, pinned to **v2.12.0**.
- **No compiled artifacts are committed to this repo** (no `.dll`, `.lib`, `.exe`). Streamline is built
  from source; its build fetches NVIDIA's proprietary runtime DLLs (`nvngx_dlss.dll`, `nvngx_dlssg.dll`,
  `sl.*.dll`) via **packman** into the submodule's gitignored build output — they never enter our git tree.

## First checkout / build
```sh
git submodule update --init --recursive ThirdParty/Streamline   # fetch the source
cd ThirdParty/Streamline && ./setup.bat                          # packman pulls build deps + NGX runtime
./build.bat -Release                                             # -> _artifacts/sl.interposer/Release/*
```
(The Streamline repo uses premake + packman; `setup.bat` then `build.bat` produce `sl.interposer.lib` and
the `sl.*` / `nvngx_*` runtime DLLs under `ThirdParty/Streamline/_artifacts/` and `bin/`.)

## Build-process integration (Phase 5 — DLSS 4)
The engine build links + ships Streamline from this submodule, never from committed binaries:
1. `Engine.vcxproj` adds `ThirdParty/Streamline/include` to the include path and links the built
   `sl.interposer.lib` from `_artifacts`.
2. A pre-build / MSBuild target runs `setup.bat`+`build.bat` (once / if `_artifacts` is missing) so the
   SDK is built from source on a fresh clone.
3. A post-build step copies the required runtime DLLs (`sl.interposer.dll`, `sl.common.dll`, `sl.dlss.dll`,
   `nvngx_dlss.dll`, `sl.dlss_g.dll`, `nvngx_dlssg.dll`, `sl.reflex.dll`, `sl.pcl.dll`) next to the engine
   output and into the exported game pak's runtime folder.

DLSS itself is RTX-only (frame generation: RTX 50-series, target RTX 5070); the off-path is unaffected on
other GPUs. See `ROADMAP_NATIVE_RENDER.md` Phase 5.
