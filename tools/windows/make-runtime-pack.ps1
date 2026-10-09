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

Write-Host "== native engine (VortexAPI.dll, $Configuration x64, CMake + Ninja with Jolt)"
# #311: no MSBuild any more — the engine comes from the CMake build (run from a Visual Studio Developer PowerShell /
# after vcvars64.bat so cmake finds MSVC). The DLLs land in x64\<Configuration> like every other Windows build output.
if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) { throw "cmake not found - run from a Visual Studio Developer PowerShell" }
if (-not (Get-Command cl -ErrorAction SilentlyContinue)) { throw "the MSVC compiler (cl) is not on PATH - run from a Visual Studio Developer PowerShell (vcvars64)" }
$BuildDir = Join-Path $Root "build\windows-ninja"
cmake -S $Root -B $BuildDir -G Ninja -DCMAKE_BUILD_TYPE=$Configuration -DVORTEX_BUILD_TESTS=OFF
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }
cmake --build $BuildDir --target VortexAPI
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }
$NativeDir = Join-Path $Root "x64\$Configuration"
New-Item -ItemType Directory -Force $NativeDir | Out-Null
Copy-Item -Force (Join-Path $BuildDir "bin\VortexAPI.dll") $NativeDir
Copy-Item -Force (Join-Path $Root "ThirdParty\assimp6\bin\assimp-vc143-mt.dll") $NativeDir
Get-ChildItem (Join-Path $BuildDir "bin") -Filter "*.dll" | Where-Object { $_.Name -ne "VortexAPI.dll" } | Copy-Item -Destination $NativeDir -Force
$sl = Join-Path $Root "Redist\Streamline\x64"
if (Test-Path $sl) { Get-ChildItem $sl -Filter "*.dll" | Copy-Item -Destination $NativeDir -Force }
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
