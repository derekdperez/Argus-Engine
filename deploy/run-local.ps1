<#
.SYNOPSIS
  Compatibility wrapper for the universal Argus deploy script.

.DESCRIPTION
  The deployment logic lives in deploy/deploy.sh so Linux, EC2, and local runs use
  the same incremental/hot-swap behavior.
#>
param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]] $DeployArgs
)

$ErrorActionPreference = "Stop"

$ScriptRoot = $PSScriptRoot
if (-not $ScriptRoot) { $ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }

$DeployScript = Join-Path $ScriptRoot "deploy.sh"
$bash = Get-Command bash.exe -ErrorAction SilentlyContinue
if (-not $bash) {
  $bash = Get-Command bash -ErrorAction SilentlyContinue
}

if (-not $bash) {
  throw "bash was not found. Run deploy/deploy.sh from Git Bash/WSL/Linux, or install Git for Windows."
}

Write-Host "deploy/run-local.ps1 is now a compatibility wrapper for deploy/deploy.sh."
Write-Host "Using the universal incremental deploy path..."

& $bash.Source $DeployScript @DeployArgs
if ($LASTEXITCODE -ne 0) {
  exit $LASTEXITCODE
}
