# Streamline / DLSS redistributables

The engine loads NVIDIA Streamline at runtime for DLSS (`Engine/Graphics/DX12/DX12Streamline.cpp`).
Those DLLs ship **next to the game exe** — they are not source.

## Nothing here is committed
**No Streamline/DLSS DLLs live in the repo** (`.gitignore` excludes all of `x64/`). They are produced by
`prebuild.ps1`, which runs **automatically as the VortexAPI pre-build step** (and can be run by hand). It's
idempotent, so normal rebuilds are instant, and it never fails your build — if it can't find the DLLs, DLSS
just stays off (the engine falls back to the render-scale upscale).

## prebuild.ps1
```powershell
# default: copy the SL plugins + NGX models from a local NVIDIA Streamline SDK (auto-found under ~\Downloads)
Redist\Streamline\prebuild.ps1
# explicit SDK:
Redist\Streamline\prebuild.ps1 -SdkDir "C:\...\streamline-sdk-vX.Y.Z\bin\x64\development"
# build the sl.*.dll from the ThirdParty/Streamline submodule instead of copying them (models still from SDK):
Redist\Streamline\prebuild.ps1 -BuildFromSource
# re-stage even if present:
Redist\Streamline\prebuild.ps1 -Force
```

What it stages into `x64\`:
- **`sl.*.dll`** (+ `NvLowLatencyVk.dll`, `WinPixEventRuntime.dll`) — small Streamline plugins. Copied from the
  SDK by default, or built from the submodule with `-BuildFromSource` (packman + premake + msbuild, Develop, v145).
- **`nvngx_*.dll`** — the large proprietary NGX models (`nvngx_dlss.dll` ≈ 66 MB). **Cannot be built** — always
  copied from the SDK. Download it from the NVIDIA Streamline releases (github.com/NVIDIA-RTX/Streamline).

## How it reaches the game
`VortexAPI.vcxproj`: a pre-build `Exec` runs `prebuild.ps1`; a post-build `Copy` copies `x64\*.dll` into the build
output (next to `Vortex Engine.exe`). `DX12Streamline` then `LoadLibrary`s `sl.interposer.dll`, and NGX finds
`nvngx_dlss.dll` in the same folder. The exporter bundles the output `*.dll` into the package.

## dev vs ship
- **Development** (default): the SDK's `bin\x64\development\` set — its `nvngx_dlss.dll` allows an unregistered
  app (and draws a small dev overlay). Ideal for testing on an RTX box.
- **Shipping**: use the production signed set + register the app/project id with NVIDIA, and ship the **release**
  `nvngx_dlss.dll` (the development model is not for distribution).
