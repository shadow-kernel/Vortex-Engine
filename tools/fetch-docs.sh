#!/usr/bin/env bash
# Downloads the docs website's pages — every page docs/js/docs.js lists — so the core tests can compile their C# samples
# (DocsTests, VORTEX_DOCS_DIR). The docs repository is private; the website is public, and it is what readers see.
#   tools/fetch-docs.sh <folder>        → <folder>/docs/content/<page>.md
set -euo pipefail
out="${1:?usage: tools/fetch-docs.sh <folder>}"
base="${VORTEX_DOCS_URL:-https://engine.vortexstudio.dev/docs}"
mkdir -p "$out/docs/content"
curl -fsSL --retry 3 "$base/js/docs.js" -o "$out/docs.js"
pages=$(grep -o "file: '[A-Za-z0-9_-]*'" "$out/docs.js" | sed "s/file: '\(.*\)'/\1/" | sort -u)
for p in $pages; do curl -fsSL --retry 3 "$base/content/$p.md" -o "$out/docs/content/$p.md"; done
echo "fetched $(ls "$out/docs/content" | wc -l | tr -d ' ') pages from $base"
