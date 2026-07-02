# Fetches the Steam Audio runtime (phonon.dll) for the OPTIONAL audio v2 layer (issue #21) and drops it next to the
# engine build outputs. Only needed if you turn Steam Audio ON for a project (project master switch in the Audio
# Mixer window); the engine builds and runs fine without it — phonon.dll is loaded dynamically at run time and its
# absence just falls back to the v1 spatializer.
#
# The DLL (~50 MB) is intentionally NOT committed (see .gitignore). Run this once after cloning if you want HRTF /
# occlusion. Headers + phonon.lib ARE committed, so a build never depends on the network.
#
#   powershell -ExecutionPolicy Bypass -File ThirdParty\steam-audio\fetch-phonon-dll.ps1

$ErrorActionPreference = "Stop"
$version = "4.8.1"
$url = "https://github.com/ValveSoftware/steam-audio/releases/download/v$version/steamaudio_$version.zip"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent   # repo root (…/ThirdParty/steam-audio -> repo)
$tmp = Join-Path $env:TEMP "steamaudio_$version.zip"
$extract = Join-Path $env:TEMP "steamaudio_$version"

Write-Host "Downloading Steam Audio $version SDK (~180 MB)…"
Invoke-WebRequest -Uri $url -OutFile $tmp

Write-Host "Extracting phonon.dll…"
if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
Expand-Archive -Path $tmp -DestinationPath $extract
$dll = Join-Path $extract "steamaudio\lib\windows-x64\phonon.dll"
if (-not (Test-Path $dll)) { throw "phonon.dll not found in the SDK zip." }

foreach ($cfg in @("Release", "Debug")) {
    $dest = Join-Path $root "x64\$cfg"
    if (Test-Path $dest) {
        Copy-Item $dll (Join-Path $dest "phonon.dll") -Force
        Write-Host "  -> x64\$cfg\phonon.dll"
    }
}
Write-Host "Done. Enable Steam Audio per project in the Audio Mixer window."
