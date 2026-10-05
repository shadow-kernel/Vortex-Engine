#!/bin/bash
# Build a Vortex RUNTIME PACK for the platform this script runs on (macOS or Linux): everything an exported
# game needs besides the project - the standalone player (self-contained), the native engine library and the
# engine shaders - laid out the way the editor's Build dialog expects:
#
#   dist/runtimes/<rid>/Vortex.Player, Vortex.Player.dll, ...   (published player)
#   dist/runtimes/<rid>/libVortexAPI.dylib | libVortexAPI.so     (native engine)
#   dist/runtimes/<rid>/Shaders/msl | Shaders/hlsl                (shaders)
#
# The editor finds packs in dist/runtimes/<rid> (development), next to itself in Runtimes/<rid>, or installed via
# Build ▸ "Install runtime pack…" (a .zip of this folder). The pack for the platform the editor runs on is not
# needed - the editor uses its own installation. Use this to produce packs for OTHER machines of the same platform
# (e.g. an Intel Mac pack on an Intel Mac) or to ship a pack with a CI build.
#
#   tools/make-runtime-pack.sh [--debug] [--zip]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
export PATH="$DOTNET_ROOT:/opt/homebrew/bin:/usr/local/bin:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
CONFIG=Release; ZIP=0
for a in "$@"; do case "$a" in --debug) CONFIG=Debug;; --zip) ZIP=1;; esac; done
OS="$(uname -s)"; ARCH="$(uname -m)"
case "$OS" in
  Darwin) RID="osx-arm64"; [ "$ARCH" = "x86_64" ] && RID="osx-x64"; PRESET="macos-release"; [ "$CONFIG" = "Debug" ] && PRESET="macos-debug"; LIB="libVortexAPI.dylib"; SHADERS="msl";;
  Linux)  RID="linux-x64"; PRESET="linux-release"; [ "$CONFIG" = "Debug" ] && PRESET="linux-debug"; LIB="libVortexAPI.so"; SHADERS=".";;
  *) echo "unsupported OS $OS"; exit 1;;
esac
OUT="$ROOT/dist/runtimes/$RID"
echo "== native engine ($PRESET)"
cmake --preset "$PRESET" >/dev/null
cmake --build --preset "$PRESET"
NATIVE="$ROOT/build/$PRESET/bin"
[ -f "$NATIVE/$LIB" ] || { echo "native library missing: $NATIVE/$LIB"; exit 1; }
echo "== player ($CONFIG, $RID, self-contained)"
rm -rf "$OUT"; mkdir -p "$OUT"
dotnet publish Managed/Vortex.Player/Vortex.Player.csproj -c "$CONFIG" -r "$RID" --self-contained true -o "$OUT" -nologo -v q
cp "$NATIVE/$LIB" "$OUT/"
for lib in "$NATIVE"/lib*.dylib "$NATIVE"/lib*.so*; do [ -f "$lib" ] && cp "$lib" "$OUT/" || true; done
mkdir -p "$OUT/Shaders"; cp -R "$NATIVE/Shaders/." "$OUT/Shaders/"
[ -d "$OUT/Shaders/$SHADERS" ] || { echo "shaders missing: $OUT/Shaders/$SHADERS"; exit 1; }
find "$OUT" -name "*.pdb" -delete
echo "== runtime pack: $OUT"
if [ "$ZIP" = "1" ]; then
  ZIPF="$ROOT/dist/runtimes/vortex-runtime-$RID.zip"; rm -f "$ZIPF"
  (cd "$OUT" && zip -qr "$ZIPF" .)
  echo "   zip: $ZIPF  (install it in the editor: Build ▸ Install runtime pack…)"
fi
