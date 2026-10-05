#!/bin/bash
# Day-to-day development commands for Vortex on macOS (run from anywhere).
#
#   tools/macos/dev.sh setup          install the toolchain with Homebrew
#   tools/macos/dev.sh build          build the native engine (Debug) + the .NET layer (Debug)
#   tools/macos/dev.sh editor [proj]  run the editor from the build tree (Debug), optionally with a project folder
#   tools/macos/dev.sh player <proj> [scene]   run a project in the standalone player
#   tools/macos/dev.sh test           native tests + editor/player smoke runs
#   tools/macos/dev.sh app            build the release .app and install it into /Applications
#   tools/macos/dev.sh pack [--zip]   build a runtime pack (player + engine + shaders) for exporting games
#   tools/macos/dev.sh xcode          generate an Xcode project for the native engine and open it
#   tools/macos/dev.sh ide            open the .NET solution in Rider (or VS Code)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"; cd "$ROOT"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
export PATH="$DOTNET_ROOT:/opt/homebrew/bin:/usr/local/bin:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
cmd="${1:-help}"; shift || true
case "$cmd" in
  setup)
    xcode-select -p >/dev/null 2>&1 || xcode-select --install
    brew install cmake ninja assimp sdl3 dotnet
    echo "ok — next: tools/macos/dev.sh build" ;;
  build)
    cmake --preset macos-debug >/dev/null
    cmake --build --preset macos-debug
    dotnet build Managed/Vortex.Managed.slnx -c Debug -nologo -v q
    echo "ok — run: tools/macos/dev.sh editor" ;;
  editor)
    exec Managed/Vortex.Editor/bin/Debug/net10.0/Vortex.Editor ${1:+--project="$1"} ;;
  player)
    [ -n "${1:-}" ] || { echo "usage: dev.sh player <project-folder> [scene]"; exit 1; }
    exec Managed/Vortex.Player/bin/Debug/net10.0/Vortex.Player --project="$1" ${2:+--scene="$2"} ;;
  test)
    ctest --preset macos-debug
    build/macos-debug/bin/VortexRenderTest
    TMP="$(mktemp -d)"; cp -R Templates/Default3D "$TMP/proj"
    Managed/Vortex.Player/bin/Debug/net10.0/Vortex.Player --project="$TMP/proj" --scene=Match --exit-after=4 --capture="$TMP/player.bmp"
    VORTEX_SMOKE_FULL=1 Managed/Vortex.Editor/bin/Debug/net10.0/Vortex.Editor --project="$TMP/proj" --scene=Match --smoke=14 --capture="$TMP/editor" | grep -a "SMOKE" || true
    rm -rf "$TMP"; echo "smoke runs done" ;;
  app)
    exec tools/macos/make-app.sh --install --dmg "$@" ;;
  pack)
    exec tools/make-runtime-pack.sh "$@" ;;
  xcode)
    cmake --preset macos-xcode >/dev/null
    open build/macos-xcode/VortexEngine.xcodeproj 2>/dev/null || open build/macos-xcode/*.xcodeproj ;;
  ide)
    if [ -d "/Applications/Rider.app" ] || [ -d "$HOME/Applications/Rider.app" ]; then open -a Rider Managed/Vortex.Managed.slnx
    elif command -v code >/dev/null; then code "$ROOT"
    else open -a "Visual Studio Code" "$ROOT" 2>/dev/null || open "$ROOT"; fi ;;
  *)
    sed -n 2,13p "$0" ;;
esac
