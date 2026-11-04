# Copies tools.json and plan.schema.json from the manager's folder (the single source of truth)
# into services/planner/contracts/ so the Docker build context (services/planner) is self-contained.
# Never edit the copies. Run before `docker build`.
$src = Join-Path $PSScriptRoot "..\..\contracts"
$dst = Join-Path $PSScriptRoot "contracts"
New-Item -ItemType Directory -Force $dst | Out-Null
foreach ($f in "tools.json", "plan.schema.json") {
    Copy-Item (Join-Path $src $f) (Join-Path $dst $f) -Force
    Write-Host "synced $f"
}
