# Stage the cross-platform (Avalonia, .NET 10) editor the way the Windows installer ships it (#183):
#
#   <Out>\Vortex.Editor.exe, ...           the editor, self-contained (no .NET runtime to install)
#   <Out>\player\Vortex.Player.exe, ...    the game host its Build dialog exports Windows games with
#
# <Out> defaults to x64\<Configuration>\Editor, next to the native build: the editor loads ..\VortexAPI.dll and uses
# ..\Shaders and ..\Templates (Installer\VortexEngine.iss copies the folder to {app}\Editor). The project templates are
# staged here as well (see the end). Run after the native build:
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

# The project templates (git submodules under Templates\) next to the editor: the "Create New Project" dialog finds them
# in <Out>\..\Templates (installed: {app}\Templates). The classic editor's csproj used to copy them and #311 retired it —
# the 3.1.0 installer shipped none. Git metadata, build output and the per-project Library stay behind; the big
# templates' LFS assets come from the release's template packs (#299).
$templates = Join-Path (Split-Path $Out -Parent) "Templates"
if (Test-Path $templates) { Remove-Item -Recurse -Force $templates }
foreach ($t in Get-ChildItem (Join-Path $Root "Templates") -Directory) {
    $null = robocopy $t.FullName (Join-Path $templates $t.Name) /E /XD .git bin obj Library Build .vs /XF .git /NFL /NDL /NJH /NJS /NP /R:1 /W:1
    if ($LASTEXITCODE -ge 8) { throw "copying the template $($t.Name) failed (robocopy $LASTEXITCODE)" }
}
& cmd.exe /c "exit 0"   # robocopy's success codes (1-7) must not fail a CI step
if (-not (Test-Path (Join-Path $templates "Default3D\project.vortex"))) { throw "the 3D Starter template was not staged (git submodule update --init?)" }
Write-Host "== templates -> $templates ($((Get-ChildItem $templates -Directory | ForEach-Object Name) -join ', '))"
