# Build a Vortex RUNTIME PACK for Windows (x64): everything an exported game needs besides the project -
# the standalone player (self-contained), the native engine (VortexAPI.dll, DirectX 12) and the HLSL shaders -
# laid out the way the editor's Build dialog expects:
#
#   dist\runtimes\win-x64\Vortex.Player.exe, Vortex.Player.dll, ...   (published player)
#   dist\runtimes\win-x64\VortexAPI.dll (+ sibling native DLLs)          (native engine)
#   dist\runtimes\win-x64\Shaders\*.hlsl                                  (shaders, flat like a shipped game)
#
# Run this on Windows (Visual Studio 2022 C++ tools + .NET 10 SDK), then copy dist\runtimes\win-x64 (or the .zip)
# to the machine that exports and use Build ▸ "Install runtime pack…" there. Building games for Windows from a Mac
# needs this pack because the native engine can only be compiled on Windows.
#
#   powershell -ExecutionPolicy Bypass -File tools\windows\make-runtime-pack.ps1 [-Configuration Release] [-Zip]
param(
    [string]$Configuration = "Release",
    [switch]$Zip
)
$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $Root
$Out = Join-Path $Root "dist\runtimes\win-x64"

Write-Host "== native engine (VortexAPI.dll, $Configuration x64)"
if (-not (Get-Command msbuild -ErrorAction SilentlyContinue)) { throw "msbuild not found - run from a Visual Studio Developer PowerShell" }
nuget restore Engine\packages.config -SolutionDirectory . | Out-Null
nuget restore VortexAPI\packages.config -SolutionDirectory . | Out-Null
msbuild Vortex.slnx /t:VortexAPI /p:Configuration=$Configuration /p:Platform=x64 /m /v:m
$NativeDir = Join-Path $Root "x64\$Configuration"
$NativeDll = Join-Path $NativeDir "VortexAPI.dll"
if (-not (Test-Path $NativeDll)) { throw "native library missing: $NativeDll" }

Write-Host "== player ($Configuration, win-x64, self-contained)"
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force $Out | Out-Null
dotnet publish Managed\Vortex.Player\Vortex.Player.csproj -c $Configuration -r win-x64 --self-contained true -o $Out -nologo -v q
Copy-Item $NativeDll $Out
# sibling native DLLs the engine loads at runtime (Steam Audio, DLSS/Streamline, ...) - never the managed editor assemblies
Get-ChildItem $NativeDir -Filter "*.dll" | Where-Object { $_.Name -ne "VortexAPI.dll" -and $_.Name -notlike "*.resources.dll" -and $_.Name -notlike "Vortex*.dll" -and $_.Name -notlike "Microsoft.*" -and $_.Name -notlike "System.*" } | ForEach-Object { Copy-Item $_.FullName $Out }
New-Item -ItemType Directory -Force (Join-Path $Out "Shaders") | Out-Null
Copy-Item (Join-Path $Root "Engine\Shaders\*.hlsl") (Join-Path $Out "Shaders")
Get-ChildItem $Out -Recurse -Filter "*.pdb" | Remove-Item -Force
Write-Host "== runtime pack: $Out"
if ($Zip) {
    $ZipFile = Join-Path $Root "dist\runtimes\vortex-runtime-win-x64.zip"
    if (Test-Path $ZipFile) { Remove-Item $ZipFile }
    Compress-Archive -Path (Join-Path $Out "*") -DestinationPath $ZipFile
    Write-Host "   zip: $ZipFile  (install it in the editor: Build > Install runtime pack...)"
}
