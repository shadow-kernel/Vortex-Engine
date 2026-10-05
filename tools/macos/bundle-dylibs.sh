#!/bin/bash
# Make the native libraries in a folder self-contained: every non-system library they link from Homebrew (SDL3,
# assimp, and whatever those pull in — also @rpath references of the copied libraries, such as brotli's
# libbrotlicommon) is copied next to them and found through @loader_path, then re-signed (ad hoc; a rewritten Mach-O
# must be signed again or dyld refuses it on Apple Silicon). Without this the app, the runtime pack and every exported
# Mac game only start on a Mac with the same Homebrew packages installed.
#   tools/macos/bundle-dylibs.sh <folder-with-libVortexAPI.dylib>
set -e
DIR="$1"
[ -d "$DIR" ] || { echo "bundle-dylibs: no folder $DIR"; exit 1; }
BREW_LIBS="/opt/homebrew/lib /usr/local/lib"

own_id() { otool -D "$1" | tail -n +2; }
deps() { otool -L "$1" | awk 'NR>1 {print $1}' | grep -vxF "$(own_id "$1")" || true; }

copy_in() {   # $1 = source file -> $DIR, install name @loader_path/<name>, finds its own @rpath deps next to it
  local base; base="$(basename "$1")"
  cp -L "$1" "$DIR/$base"
  chmod u+w "$DIR/$base"
  install_name_tool -id "@loader_path/$base" "$DIR/$base" 2>/dev/null
  install_name_tool -add_rpath "@loader_path" "$DIR/$base" 2>/dev/null || true
  echo "   bundled $base"
}

changed=1
while [ "$changed" = 1 ]; do
  changed=0
  for lib in "$DIR"/*.dylib; do
    [ -f "$lib" ] || continue
    for dep in $(deps "$lib"); do
      case "$dep" in
        /opt/homebrew/*|/usr/local/*)
          base="$(basename "$dep")"
          if [ ! -f "$DIR/$base" ]; then copy_in "$dep"; changed=1; fi
          install_name_tool -change "$dep" "@loader_path/$base" "$lib" 2>/dev/null ;;
        @rpath/*)
          # resolved through the library's rpaths; a copied library has @loader_path among them — make sure the
          # target is here (from Homebrew's lib folders when it is not)
          base="${dep#@rpath/}"
          if [ ! -f "$DIR/$base" ]; then
            for d in $BREW_LIBS; do
              if [ -f "$d/$base" ]; then copy_in "$d/$base"; changed=1; break; fi
            done
          fi ;;
      esac
    done
  done
done
# re-sign what was rewritten; then nothing may still point into Homebrew, and every @rpath target must be here
for lib in "$DIR"/*.dylib; do codesign --force --sign - "$lib" >/dev/null 2>&1 || true; done
left=""
for lib in "$DIR"/*.dylib; do
  for dep in $(deps "$lib"); do
    case "$dep" in
      /opt/homebrew/*|/usr/local/*) left="$left $(basename "$lib") -> $dep;" ;;
    esac
  done
done
[ -z "$left" ] || { echo "bundle-dylibs: still linked from Homebrew:$left"; exit 1; }
