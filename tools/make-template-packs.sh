#!/bin/bash
# Template packs (#299): the templates whose models, textures and sounds live in Git LFS, zipped with their real
# content and attached to the GitHub release of a version. Installed editors (CI builds without LFS) download the
# pack of their own version the first time a project is created from the template (Editor/Core/Services/TemplatePacks.cs).
#
#   tools/make-template-packs.sh 3.0.0            -> dist/template-packs/Template-<Id>.zip
#   tools/make-template-packs.sh 3.0.0 --upload   ... and upload them to the v3.0.0 release (gh, --clobber)
#
# Run it on a machine whose template submodules have their LFS content (it runs `git lfs pull` per template).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:?usage: make-template-packs.sh <version> [--upload]}"
UPLOAD=0; [ "${2:-}" = "--upload" ] && UPLOAD=1
OUT="$ROOT/dist/template-packs"
rm -rf "$OUT"; mkdir -p "$OUT"

packed=0
for dir in "$ROOT"/Templates/*/; do
  dir="${dir%/}"; id="$(basename "$dir")"
  [ -f "$dir/template.json" ] || continue
  grep -qs "filter=lfs" "$dir/.gitattributes" || { echo "-- $id: no LFS content, ships with the editor"; continue; }
  echo "== $id"
  (cd "$dir" && git lfs pull)
  # never pack pointer files: every LFS-tracked file must have its real content
  pointers="$(cd "$dir" && git lfs ls-files --name-only | while read -r f; do
      head -c 40 "$f" 2>/dev/null | grep -q "^version https://git-lfs" && echo "$f"; done || true)"
  if [ -n "$pointers" ]; then echo "$id still has LFS pointers (git lfs pull failed?):"; echo "$pointers" | head -5; exit 1; fi
  (cd "$dir" && zip -r -q -X "$OUT/Template-$id.zip" . -x '.git' '.git/*' '.gitattributes' '.gitmodules' '*.DS_Store' 'bin/*' 'obj/*' '.vs/*')
  echo "   $(du -h "$OUT/Template-$id.zip" | cut -f1)  Template-$id.zip"
  packed=$((packed + 1))
done
[ "$packed" -gt 0 ] || { echo "no LFS templates found"; exit 1; }

if [ "$UPLOAD" = "1" ]; then
  gh release upload "v$VERSION" "$OUT"/Template-*.zip --clobber
  echo "uploaded to v$VERSION"
fi
