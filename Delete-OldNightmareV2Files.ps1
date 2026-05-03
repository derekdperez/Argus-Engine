<# 
.SYNOPSIS
Deletes old NightmareV2 solution, source, and test paths after the ArgusEngine direct overlay is extracted.

.DESCRIPTION
Run this from the repository root after unzipping the ArgusEngine overlay.
The script removes old tracked NightmareV2 paths that cannot be deleted by zip extraction.

It does not rename databases or delete runtime data. The legacy database names
nightmare_v2 and nightmare_v2_files are intentionally preserved.

.EXAMPLE
.\Delete-OldNightmareV2Files.ps1 -WhatIf

.EXAMPLE
.\Delete-OldNightmareV2Files.ps1 -Force

#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Assert-RepositoryRoot {
    $required = @(
        'Directory.Build.targets',
        'ArgusEngine.slnx',
        'src'
    )

    foreach ($path in $required) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Run this script from the repository root after extracting the ArgusEngine overlay. Missing required path: $path"
        }
    }
}

function Remove-PathIfExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PathToRemove
    )

    if (-not (Test-Path -LiteralPath $PathToRemove)) {
        Write-Host "Skip missing: $PathToRemove"
        return
    }

    $resolved = Resolve-Path -LiteralPath $PathToRemove
    if ($Force -or $PSCmdlet.ShouldProcess($resolved.Path, 'Remove obsolete NightmareV2 path')) {
        Remove-Item -LiteralPath $resolved.Path -Recurse -Force
        Write-Host "Removed: $PathToRemove"
    }
}

Assert-RepositoryRoot

$explicitPaths = @(
    'NightmareV2.sln',
    'NightmareV2.slnx',
    'src/NightmareV2.Application',
    'src/NightmareV2.CommandCenter',
    'src/NightmareV2.Contracts',
    'src/NightmareV2.Domain',
    'src/NightmareV2.Gatekeeper',
    'src/NightmareV2.Infrastructure',
    'src/NightmareV2.Workers.Enum',
    'src/NightmareV2.Workers.Spider',
    'src/NightmareV2.Workers.PortScan',
    'src/NightmareV2.Workers.HighValue',
    'src/NightmareV2.Workers.TechnologyIdentification'
)

foreach ($path in $explicitPaths) {
    Remove-PathIfExists -PathToRemove $path
}

if (Test-Path -LiteralPath 'src') {
    Get-ChildItem -LiteralPath 'src' -Directory -Filter 'NightmareV2.*' |
        ForEach-Object { Remove-PathIfExists -PathToRemove $_.FullName }
}

if (Test-Path -LiteralPath 'src/tests') {
    Get-ChildItem -LiteralPath 'src/tests' -Directory -Filter 'NightmareV2.*.Tests' |
        ForEach-Object { Remove-PathIfExists -PathToRemove $_.FullName }
}

Write-Host ''
Write-Host 'Old NightmareV2 paths deleted. Next recommended checks:'
Write-Host '  dotnet restore ArgusEngine.slnx'
Write-Host '  dotnet build ArgusEngine.slnx'
Write-Host '  dotnet test ArgusEngine.slnx'
