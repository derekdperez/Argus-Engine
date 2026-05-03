param(
    [string]$ExpectedVersion = "2.2.0"
)

$ErrorActionPreference = "Stop"
$failed = $false

function Assert-Contains([string]$Path, [string]$Pattern) {
    $text = Get-Content -Raw -Path $Path
    if ($text -notmatch [regex]::Escape($Pattern)) {
        Write-Error "$Path does not contain expected text: $Pattern"
        $script:failed = $true
    }
}

Assert-Contains "VERSION" $ExpectedVersion
Assert-Contains "Directory.Build.targets" "<ArgusEngineDeploymentVersion>$ExpectedVersion</ArgusEngineDeploymentVersion>"
Assert-Contains "deploy/Dockerfile.web" "ARG COMPONENT_VERSION=$ExpectedVersion"
Assert-Contains "deploy/Dockerfile.worker" "ARG COMPONENT_VERSION=$ExpectedVersion"
Assert-Contains "deploy/Dockerfile.worker-enum" "ARG COMPONENT_VERSION=$ExpectedVersion"
Assert-Contains "deploy/docker-compose.yml" "ARGUS_ENGINE_VERSION:-$ExpectedVersion"

$stale = Get-ChildItem -Path deploy,src -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' } |
    Select-String -Pattern 'COMPONENT_VERSION: .*2\.0\.0|COMPONENT_VERSION=2\.0\.0|VERSION_.*:-2\.0\.0' -SimpleMatch:$false

if ($stale) {
    $stale | ForEach-Object { Write-Error "Stale deployment version default: $($_.Path):$($_.LineNumber): $($_.Line)" }
    $failed = $true
}

if ($failed) { exit 1 }
