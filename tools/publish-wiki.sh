#!/bin/bash
# Publish docs/wiki/*.md to the GitHub wiki (https://github.com/shadow-kernel/Vortex-Engine/wiki), replacing its pages.
# The wiki is a repository of its own, so links into this repository (../../Engine/...) become GitHub links on the way.
# The macOS / Linux twin of Scripts/publish-wiki.ps1.
#   tools/publish-wiki.sh [wiki remote]
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REMOTE="${1:-https://github.com/shadow-kernel/Vortex-Engine.wiki.git}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
git clone -q "$REMOTE" "$TMP"
find "$TMP" -maxdepth 1 -name '*.md' -delete
for f in "$ROOT"/docs/wiki/*.md; do
  sed -E 's#\]\(\.\./\.\./([^)]*)\)#](https://github.com/shadow-kernel/Vortex-Engine/blob/main/\1)#g' "$f" > "$TMP/$(basename "$f")"
done
cd "$TMP"
git add -A
if git diff --cached --quiet; then echo "The wiki is up to date."; exit 0; fi
git commit -q -m "Sync wiki from docs/wiki ($(date +%F))"
git push -q
echo "Wiki published: https://github.com/shadow-kernel/Vortex-Engine/wiki"
