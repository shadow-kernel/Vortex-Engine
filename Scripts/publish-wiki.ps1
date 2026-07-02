# Publishes docs/wiki/ to the GitHub wiki repository.
# Prerequisite (one-time): the wiki must be initialized once via the GitHub web UI
# (repo -> Wiki -> "Create the first page" -> Save). After that this script owns the content.
param(
    [string]$WikiRemote = "https://github.com/shadow-kernel/Vortex-Engine.wiki.git"
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $repoRoot "docswiki"
if (-not (Test-Path $srcDir)) { throw "docs/wiki not found" }

$tmp = Join-Path $env:TEMP ("vortex-wiki-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
git clone $WikiRemote $tmp
if ($LASTEXITCODE -ne 0) {
    throw "Clone failed. Initialize the wiki once via the web UI (Wiki -> Create the first page), then re-run."
}

Get-ChildItem $tmp -Filter *.md | Remove-Item -Force
Copy-Item (Join-Path $srcDir "*.md") $tmp -Force

Push-Location $tmp
try {
    git add -A
    git -c core.safecrlf=false commit -m "Sync wiki from docs/wiki ($(Get-Date -Format yyyy-MM-dd))"
    if ($LASTEXITCODE -eq 0) { git push } else { Write-Host "Nothing to publish - wiki is up to date." }
}
finally {
    Pop-Location
    Remove-Item $tmp -Recurse -Force
}
Write-Host "Wiki published: https://github.com/shadow-kernel/Vortex-Engine/wiki"
