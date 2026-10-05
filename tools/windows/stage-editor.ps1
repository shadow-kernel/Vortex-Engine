# Stage the cross-platform (Avalonia, .NET 10) editor the way the Windows installer ships it (#183):
#
#   <Out>\Vortex.Editor.exe, ...           the editor, self-contained (no .NET runtime to install)
#   <Out>\player\Vortex.Player.exe, ...    the game host its Build dialog exports Windows games with
#
# <Out> defaults to x64\<Configuration>\Editor, next to the solution build: the editor loads ..\VortexAPI.dll and uses
# ..\Shaders and ..\Templates, which it shares with the WPF editor (Installer\VortexEngine.iss copies the folder to
# {app}\Editor). Run after the solution build:
#
#   powershell -ExecutionPolicy Bypass -File tools\windows\stage-editor.ps1 [-Configuration Release] [-Out <folder>]
param(
    [string]$Configuration = "Release",
    [string]$Out = ""
)
$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $Root
if ($Out -eq "") { $Out = Join-Path $Root "x64\$Configuration\Editor" }
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }

Write-Host "== editor -> $Out"
dotnet publish Managed\Vortex.Editor\Vortex.Editor.csproj -c $Configuration -r win-x64 --self-contained true -o $Out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publishing the editor failed" }

Write-Host "== player -> $Out\player"
dotnet publish Managed\Vortex.Player\Vortex.Player.csproj -c $Configuration -r win-x64 --self-contained true -o (Join-Path $Out "player") -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publishing the player failed" }

foreach ($f in @("Vortex.Editor.exe", "player\Vortex.Player.exe", "player\Vortex.Player.dll")) {
    if (-not (Test-Path (Join-Path $Out $f))) { throw "missing after publish: $f" }
}
$mb = [math]::Round(((Get-ChildItem -Recurse $Out | Measure-Object -Sum Length).Sum / 1MB), 0)
Write-Host "== staged ($mb MB)"
