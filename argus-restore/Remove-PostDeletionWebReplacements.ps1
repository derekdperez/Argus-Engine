[CmdletBinding()]
param(
    [string]$RepoRoot = (Get-Location).Path,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

$pathsToDelete = @(
    "src/ArgusEngine.CommandCenter/Components/Pages/Operations.razor",
    "src/ArgusEngine.CommandCenter/Components/Pages/Operations.razor.css"
)

foreach ($relative in $pathsToDelete) {
    $path = Join-Path $RepoRoot ($relative -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    if (Test-Path -LiteralPath $path) {
        if ($DryRun) {
            Write-Host "DRY-RUN delete $relative"
        } else {
            Remove-Item -LiteralPath $path -Force
            Write-Host "Deleted $relative"
        }
    }
}
