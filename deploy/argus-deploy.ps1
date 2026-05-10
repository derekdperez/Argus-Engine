$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir '..')
$project = Join-Path $scriptDir 'ArgusEngine.DeployUi/ArgusEngine.DeployUi.csproj'

Push-Location $repoRoot
try {
    dotnet run --project $project -- @args
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
