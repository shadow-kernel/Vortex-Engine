# Creates the "Vortex Engine Roadmap" GitHub Project (v2) and adds every open issue.
# Prerequisite: the gh token needs the project scope ->  gh auth refresh -s project
#               (opens a one-time device code at https://github.com/login/device)
$ErrorActionPreference = "Stop"
$env:Path += ";C:\Program Files\GitHub CLI"
$owner = "shadow-kernel"
$repo = "shadow-kernel/Vortex-Engine"

# Reuse existing project if present
$existing = gh project list --owner $owner --format json | ConvertFrom-Json
$proj = $existing.projects | Where-Object { $_.title -eq "Vortex Engine Roadmap" } | Select-Object -First 1
if (-not $proj) {
    $proj = gh project create --owner $owner --title "Vortex Engine Roadmap" --format json | ConvertFrom-Json
    Write-Host "Created project #$($proj.number)"
} else {
    Write-Host "Reusing project #$($proj.number)"
}
$num = $proj.number

# Priority field (single select) for board grouping
$fields = gh project field-list $num --owner $owner --format json | ConvertFrom-Json
if (-not ($fields.fields | Where-Object { $_.name -eq "Priority" })) {
    gh project field-create $num --owner $owner --name "Priority" --data-type SINGLE_SELECT `
        --single-select-options "P0-critical,P1-high,P2-medium,P3-low" | Out-Null
    Write-Host "Created Priority field"
}

# Add every open issue (idempotent: item-add ignores duplicates)
$issues = gh issue list -R $repo --state open --limit 400 --json number,url | ConvertFrom-Json
$i = 0
foreach ($issue in $issues) {
    $i++
    gh project item-add $num --owner $owner --url $issue.url | Out-Null
    Write-Host "[$i/$($issues.Count)] added #$($issue.number)"
    Start-Sleep -Milliseconds 600
}
Write-Host "Done: https://github.com/users/$owner/projects/$num"
