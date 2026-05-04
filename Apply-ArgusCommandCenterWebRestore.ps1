[CmdletBinding()]
param(
    [string]$RepoRoot = (Get-Location).Path,
    [string]$SourceSha = "717c1c568b38bb4fc84c9b34c54e90ed362d2ffb",
    [switch]$NoBackup,
    [switch]$DryRun,
    [switch]$KeepCurrentOperationsPage,
    [switch]$IncludeAppSettings,
    [switch]$RestoreOldPostDeletionEndpointVersions
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$manifestPath = Join-Path $PSScriptRoot "restored-files.json"
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Missing manifest: $manifestPath"
}

$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
$commandCenterRoot = Join-Path $RepoRoot "src/ArgusEngine.CommandCenter"
if (-not (Test-Path -LiteralPath $commandCenterRoot)) {
    throw "This does not look like the Argus-Engine repo root. Missing: $commandCenterRoot"
}

$backupRoot = Join-Path $RepoRoot (".argus-web-restore-backup/" + (Get-Date -Format "yyyyMMdd-HHmmss"))

function Join-RepoPath {
    param([Parameter(Mandatory)][string]$RelativePath)
    return Join-Path $RepoRoot ($RelativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
}

function Ensure-ParentDirectory {
    param([Parameter(Mandatory)][string]$Path)
    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent)) {
        if ($DryRun) {
            Write-Host "DRY-RUN mkdir $parent"
        } else {
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
        }
    }
}

function Backup-ExistingPath {
    param([Parameter(Mandatory)][string]$RelativePath)
    if ($NoBackup) { return }
    $src = Join-RepoPath $RelativePath
    if (-not (Test-Path -LiteralPath $src)) { return }

    $dest = Join-Path $backupRoot ($RelativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    Ensure-ParentDirectory -Path $dest
    if ($DryRun) {
        Write-Host "DRY-RUN backup $RelativePath -> $dest"
    } else {
        Copy-Item -LiteralPath $src -Destination $dest -Force
    }
}

function Convert-ToArgusCommandCenterText {
    param([Parameter(Mandatory)][string]$Content)

    # Minimal compatibility edits:
    # 1. Namespace/project prefix migrated to current ArgusEngine.*.
    # 2. Current repo only has AddArgusRabbitMq; the old compatibility alias was removed later.
    # 3. The current static asset name is argus-ui.js.
    $converted = $Content `
        -replace 'NightmareV2', 'ArgusEngine' `
        -replace 'AddNightmareRabbitMq', 'AddArgusRabbitMq' `
        -replace 'nightmare-ui\.js', 'argus-ui.js' `
        -replace 'nightmareUi', 'argusUi'

    return $converted
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )

    Ensure-ParentDirectory -Path $Path
    if ($DryRun) {
        Write-Host "DRY-RUN write $Path"
        return
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

function Download-RawFile {
    param(
        [Parameter(Mandatory)][string]$OldPath,
        [Parameter(Mandatory)][string]$NewPath,
        [bool]$Binary = $false
    )

    $url = "https://raw.githubusercontent.com/derekdperez/Argus-Engine/$SourceSha/$OldPath"
    $target = Join-RepoPath $NewPath

    Backup-ExistingPath -RelativePath $NewPath
    Ensure-ParentDirectory -Path $target

    if ($DryRun) {
        Write-Host "DRY-RUN download $OldPath -> $NewPath"
        return
    }

    if ($Binary) {
        Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing
        return
    }

    $tmp = [System.IO.Path]::GetTempFileName()
    try {
        Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
        $content = [System.IO.File]::ReadAllText($tmp)
        $content = Convert-ToArgusCommandCenterText -Content $content

        # One old startup using appears to point at a transient Razor-generated namespace.
        # It is not needed and can break the current compile after the namespace migration.
        if ($NewPath -eq "src/ArgusEngine.CommandCenter/Startup/CommandCenterServiceRegistration.cs") {
            $content = $content -replace 'using ArgusEngine\.CommandCenter\.Components\.Pages\.Operations;\s*', ''

            # Preserve post-deletion current functionality that is not part of the lost pages,
            # but is required by current endpoints retained below.
            if ($content -notmatch 'HttpQueueArtifactBackfillService') {
                $content = $content -replace '(services\.AddCommandCenterApplicationServices\(\);\s*)', "`$1services.AddScoped<HttpQueueArtifactBackfillService>();`r`n        "
            }
        }

        Write-Utf8NoBom -Path $target -Content $content
    }
    finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
}

function Remove-CurrentReplacementPage {
    param([Parameter(Mandatory)][string]$RelativePath)

    if ($KeepCurrentOperationsPage) {
        Write-Host "Keeping current replacement page because -KeepCurrentOperationsPage was supplied: $RelativePath"
        return
    }

    $path = Join-RepoPath $RelativePath
    if (-not (Test-Path -LiteralPath $path)) { return }

    Backup-ExistingPath -RelativePath $RelativePath
    if ($DryRun) {
        Write-Host "DRY-RUN delete $RelativePath"
    } else {
        Remove-Item -LiteralPath $path -Force
    }
}

function Write-CuratedProgram {
    $relative = "src/ArgusEngine.CommandCenter/Program.cs"
    $target = Join-RepoPath $relative
    Backup-ExistingPath -RelativePath $relative

    $content = @'
using ArgusEngine.CommandCenter.Components;
using ArgusEngine.CommandCenter.Endpoints;
using ArgusEngine.CommandCenter.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCommandCenterServices(builder.Configuration, builder.Environment);

var app = builder.Build();

await app.InitializeCommandCenterDatabasesAsync().ConfigureAwait(false);

app.UseCommandCenterMiddleware();
app.MapCommandCenterEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync().ConfigureAwait(false);
'@

    Write-Utf8NoBom -Path $target -Content $content
}

function Write-CuratedEndpointRegistration {
    $relative = "src/ArgusEngine.CommandCenter/Endpoints/CommandCenterEndpointRegistration.cs"
    $target = Join-RepoPath $relative
    Backup-ExistingPath -RelativePath $relative

    $content = @'
using ArgusEngine.CommandCenter.DataMaintenance;
using ArgusEngine.CommandCenter.Diagnostics;
using ArgusEngine.CommandCenter.Hubs;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class CommandCenterEndpointRegistration
{
    public static WebApplication MapCommandCenterEndpoints(this WebApplication app)
    {
        // Preserved current/post-deletion endpoints.
        app.MapAssetAdmissionDecisionEndpoints();
        app.MapDataRetentionAdminEndpoints();
        app.MapHttpArtifactBackfillEndpoints();

        // Restored deleted web-application endpoints from 717c1c5.
        app.MapAdminUsageEndpoints();
        app.MapAssetEndpoints();
        app.MapAssetGraphEndpoints();
        app.MapBusJournalEndpoints();
        app.MapDataMaintenanceEndpoints();
        app.MapDiagnosticsEndpoints();
        app.MapEc2WorkerEndpoints();
        app.MapEventTraceEndpoints();
        app.MapFileStoreEndpoints();
        app.MapHighValueFindingEndpoints();
        app.MapHttpRequestQueueEndpoints();
        app.MapOpsEndpoints();
        app.MapTagEndpoints();
        app.MapTargetEndpoints();
        app.MapToolRestartEndpoints();
        app.MapWorkerEndpoints();

        app.MapHub<DiscoveryHub>("/hubs/discovery");

        return app;
    }
}
'@

    Write-Utf8NoBom -Path $target -Content $content
}

function Write-CuratedMiddleware {
    $relative = "src/ArgusEngine.CommandCenter/Startup/CommandCenterMiddleware.cs"
    $target = Join-RepoPath $relative
    Backup-ExistingPath -RelativePath $relative

    $content = @'
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ArgusEngine.CommandCenter.Security;
using ArgusEngine.Infrastructure.Configuration;

namespace ArgusEngine.CommandCenter.Startup;

public static class CommandCenterMiddleware
{
    public static WebApplication UseCommandCenterMiddleware(this WebApplication app)
    {
        var listenPlainHttp = app.Configuration.GetArgusValue("ListenPlainHttp", false);

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            if (!listenPlainHttp)
            {
                app.UseHsts();
            }
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

        if (!listenPlainHttp)
        {
            app.UseHttpsRedirection();
        }

        // Preserved current readiness/liveness endpoints.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

        // Restored deleted protection for diagnostic/data-maintenance endpoints.
        app.UseSensitiveEndpointProtection();

        // Required for .NET 9+ static web asset endpoint routing and conventional wwwroot assets.
        app.MapStaticAssets();
        app.UseStaticFiles();
        app.UseAntiforgery();

        return app;
    }
}
'@

    Write-Utf8NoBom -Path $target -Content $content
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

Write-Host "Restoring Command Center web app files from $SourceSha"
Write-Host "Repo root: $RepoRoot"

# Remove current replacement page that conflicts with the old OpsRadzen routes.
Remove-CurrentReplacementPage -RelativePath "src/ArgusEngine.CommandCenter/Components/Pages/Operations.razor"
Remove-CurrentReplacementPage -RelativePath "src/ArgusEngine.CommandCenter/Components/Pages/Operations.razor.css"

foreach ($entry in $manifest.files) {
    $newPath = [string]$entry.new

    # Current appsettings are intentionally preserved by default because they may contain
    # new post-deletion connection/file-store/data-retention settings.
    if (-not $IncludeAppSettings -and ($newPath.EndsWith("appsettings.json") -or $newPath.EndsWith("appsettings.Development.json"))) {
        Write-Host "Skipping current configuration file by default: $newPath"
        continue
    }

    Download-RawFile -OldPath ([string]$entry.old) -NewPath $newPath -Binary ([bool]$entry.binary)
}

if ($RestoreOldPostDeletionEndpointVersions -and $manifest.PSObject.Properties.Name -contains "optional_old_versions_current_files") {
    Write-Host "Restoring old versions of current admission/data-retention/artifact endpoint files because -RestoreOldPostDeletionEndpointVersions was supplied."
    foreach ($entry in $manifest.optional_old_versions_current_files) {
        Download-RawFile -OldPath ([string]$entry.old) -NewPath ([string]$entry.new) -Binary ([bool]$entry.binary)
    }
} else {
    Write-Host "Preserving current admission/data-retention/artifact endpoint files by default."
}

# These three are intentionally curated instead of blindly restored:
# - Program.cs needs the old Razor component routing plus the current split startup shape.
# - Endpoint registration needs all old web endpoints plus current post-deletion endpoints.
# - Middleware needs old sensitive endpoint protection plus current health/static-asset behavior.
Write-CuratedProgram
Write-CuratedEndpointRegistration
Write-CuratedMiddleware

Write-Host ""
Write-Host "Restore overlay complete."
if (-not $NoBackup) {
    Write-Host "Backups of overwritten/deleted files were written under: $backupRoot"
}
Write-Host ""
Write-Host "Recommended validation:"
Write-Host "  dotnet build ArgusEngine.slnx"
Write-Host "  git diff -- src/ArgusEngine.CommandCenter"
