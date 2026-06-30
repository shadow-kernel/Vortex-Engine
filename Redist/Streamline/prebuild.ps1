<#
.SYNOPSIS
  Prepares the NVIDIA Streamline / DLSS runtime DLLs into Redist\Streamline\x64\ BEFORE the build.
  Nothing here is committed to the repo (see .gitignore) — this script produces it all.

.DESCRIPTION
  Runs automatically as the VortexAPI pre-build step (and can be run by hand). Idempotent: if the
  DLLs are already staged it exits immediately, so normal rebuilds are instant. DLSS is optional —
  if the source DLLs can't be found this script WARNS and exits 0 (the engine just runs without DLSS,
  falling back to the render-scale upscale); it never fails your build.

  Two ways to get the small sl.*.dll plugins:
    (default)        copy them from a local NVIDIA Streamline SDK (signed, complete).
    -BuildFromSource build them from the ThirdParty/Streamline submodule (packman + premake + msbuild,
                     Develop config, v145 toolset). The proprietary nvngx_*.dll models cannot be built
                     and are always copied from the SDK.

.PARAMETER SdkDir
  A Streamline SDK 'bin\x64' (or '...\development') folder. Auto-detected under ~\Downloads if omitted.
.PARAMETER BuildFromSource
  Build the sl.*.dll from the submodule instead of copying them from the SDK.
.PARAMETER Force
  Re-stage even if the DLLs already exist.
#>
param(
  [string]$SdkDir = "",
  [switch]$BuildFromSource,
  [switch]$Force
)

$ErrorActionPreference = "Stop"
$root   = Split-Path -Parent $MyInvocation.MyCommand.Path           # Redist\Streamline
$dest   = Join-Path $root "x64"
$repo   = (Resolve-Path (Join-Path $root "..\..")).Path             # repo root
New-Item -ItemType Directory -Force -Path $dest | Out-Null

function Info($m){ Write-Host "[prebuild-streamline] $m" }
function Warn($m){ Write-Host "[prebuild-streamline] $m" -ForegroundColor Yellow }

# Idempotent fast-path: everything already staged.
$needSl    = -not (Test-Path (Join-Path $dest "sl.interposer.dll"))
$needModel = -not (Test-Path (Join-Path $dest "nvngx_dlss.dll"))
if (-not $Force -and -not $needSl -and -not $needModel) { Info "DLLs already staged -> skip."; exit 0 }

# Locate a Streamline SDK (needed for the models always, and for the sl.*.dll unless -BuildFromSource).
function Find-Sdk {
  $cands = @()
  if ($SdkDir) { $cands += $SdkDir }
  $dl = Join-Path $env:USERPROFILE "Downloads"
  if (Test-Path $dl) {
    Get-ChildItem $dl -Directory -Filter "streamline-sdk-*" -ErrorAction SilentlyContinue |
      Sort-Object Name -Descending | ForEach-Object {
        $cands += (Join-Path $_.FullName "bin\x64\development")
        $cands += (Join-Path $_.FullName "bin\x64")
      }
  }
  return ($cands | Where-Object { $_ -and (Test-Path (Join-Path $_ "nvngx_dlss.dll")) } | Select-Object -First 1)
}
$sdk = Find-Sdk

# ---- sl.*.dll plugins ----
if ($needSl -or $Force) {
  if ($BuildFromSource) {
    Info "Building sl.*.dll from the ThirdParty/Streamline submodule (Develop, v145)..."
    $sl = Join-Path $repo "ThirdParty\Streamline"
    if (-not (Test-Path (Join-Path $sl "premake.lua"))) {
      Info "Initializing submodule..."; & git -C $repo submodule update --init ThirdParty/Streamline | Out-Host
    }
    Push-Location $sl
    try {
      if (-not (Test-Path ".\tools\premake5\premake5.exe")) { & cmd /c ".\tools\packman\packman.cmd pull -p windows-x86_64 project.xml" | Out-Host }
      & .\tools\premake5\premake5.exe vs2022 --file=.\premake.lua | Out-Host
      $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
      $msb = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
      & $msb ".\_project\vs2022\streamline.sln" /t:Build /p:Configuration=Develop /p:Platform=x64 /p:PlatformToolset=v145 /m /v:minimal /nologo | Out-Host
      Get-ChildItem ".\_artifacts" -Directory | ForEach-Object {
        $dll = Join-Path $_.FullName "Develop_x64\$($_.Name).dll"
        if (Test-Path $dll) { Copy-Item $dll $dest -Force }
      }
      Info "Built + staged sl.*.dll."
    } finally { Pop-Location }
  }
  elseif ($sdk) {
    Info "Copying sl.*.dll plugins from SDK: $sdk"
    Copy-Item (Join-Path $sdk "sl.*.dll") $dest -Force
    foreach ($h in @("NvLowLatencyVk.dll","WinPixEventRuntime.dll")) {
      $p = Join-Path $sdk $h; if (Test-Path $p) { Copy-Item $p $dest -Force }
    }
  }
  else {
    Warn "No Streamline SDK found and -BuildFromSource not set -> sl.*.dll not staged. DLSS will be OFF."
    Warn "Get the SDK (github.com/NVIDIA-RTX/Streamline releases) then re-run, or pass -SdkDir / -BuildFromSource."
  }
}

# ---- nvngx_*.dll models (proprietary, cannot be built) ----
if ($needModel -or $Force) {
  if ($sdk) {
    Info "Copying NGX models from SDK: $sdk"
    Get-ChildItem (Join-Path $sdk "nvngx_*.dll") | ForEach-Object {
      Copy-Item $_.FullName $dest -Force
      Info ("  + " + $_.Name + "  (" + [math]::Round($_.Length/1MB,1) + " MB)")
    }
  }
  else { Warn "No SDK found -> nvngx_*.dll models not staged. DLSS will be OFF (render-scale fallback)." }
}

Info "Done -> $dest"
exit 0
