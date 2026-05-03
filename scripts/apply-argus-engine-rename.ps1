$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
python (Join-Path $repoRoot "scripts/apply-original-checklist-refactor.py") --apply
