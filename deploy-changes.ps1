param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$Version = "2.6.2"
$AssemblyVersion = "2.6.2.0"
$RepoRoot = Get-Location

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Ensure-Directory {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Write-FileUtf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $fullPath = if ([System.IO.Path]::IsPathRooted($Path)) {
        $Path
    } else {
        Join-Path $RepoRoot $Path
    }

    $parent = Split-Path $fullPath -Parent
    Ensure-Directory $parent

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($fullPath, $Content, $utf8NoBom)
}

function Read-FileOrEmpty {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        return Get-Content -LiteralPath $Path -Raw
    }

    return ""
}

function Replace-ArgusNamespaces {
    param([string]$Text)

    return $Text `
        -replace "NightmareV2\.Contracts", "ArgusEngine.Contracts" `
        -replace "NightmareV2\.Domain", "ArgusEngine.Domain" `
        -replace "NightmareV2\.Application", "ArgusEngine.Application" `
        -replace "NightmareV2\.Infrastructure", "ArgusEngine.Infrastructure" `
        -replace "NightmareV2\.CommandCenter", "ArgusEngine.CommandCenter" `
        -replace "NightmareV2\.Gatekeeper", "ArgusEngine.Gatekeeper" `
        -replace "NightmareV2\.Workers", "ArgusEngine.Workers"
}

if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot "ArgusEngine.slnx"))) {
    throw "Run this script from the repo root containing ArgusEngine.slnx."
}

$DomainProject = Join-Path $RepoRoot "src\ArgusEngine.Domain\ArgusEngine.Domain.csproj"
$ContractsProject = Join-Path $RepoRoot "src\ArgusEngine.Contracts\ArgusEngine.Contracts.csproj"
$DomainEntitiesDir = Join-Path $RepoRoot "src\ArgusEngine.Domain\Entities"

if (-not (Test-Path -LiteralPath $DomainProject)) {
    throw "Missing project file: $DomainProject"
}

if (-not (Test-Path -LiteralPath $ContractsProject)) {
    throw "Missing project file: $ContractsProject"
}

Ensure-Directory $DomainEntitiesDir

Write-Step "Bumping repo deployment version to $Version"

Write-FileUtf8NoBom "VERSION" "$Version`n"

$directoryBuildTargetsPath = Join-Path $RepoRoot "Directory.Build.targets"
$targets = Read-FileOrEmpty $directoryBuildTargetsPath

if ([string]::IsNullOrWhiteSpace($targets)) {
    $targets = @"
<Project>
  <PropertyGroup>
    <Version>$Version</Version>
    <PackageVersion>$Version</PackageVersion>
    <AssemblyVersion>$AssemblyVersion</AssemblyVersion>
    <FileVersion>$AssemblyVersion</FileVersion>
    <InformationalVersion>$Version</InformationalVersion>
  </PropertyGroup>
</Project>
"@
} else {
    foreach ($name in @("Version", "PackageVersion", "AssemblyVersion", "FileVersion", "InformationalVersion")) {
        $value = if ($name -in @("AssemblyVersion", "FileVersion")) { $AssemblyVersion } else { $Version }

        if ($targets -match "<$name>.*?</$name>") {
            $targets = $targets -replace "<$name>.*?</$name>", "<$name>$value</$name>"
        } elseif ($targets -match "</PropertyGroup>") {
            $targets = $targets -replace "</PropertyGroup>", "    <$name>$value</$name>`r`n  </PropertyGroup>"
        }
    }
}

Write-FileUtf8NoBom $directoryBuildTargetsPath $targets

Write-Step "Ensuring ArgusEngine.Domain references ArgusEngine.Contracts"

$domainCsprojText = Read-FileOrEmpty $DomainProject

if ($domainCsprojText -notmatch "ArgusEngine\.Contracts\\ArgusEngine\.Contracts\.csproj") {
    if ($domainCsprojText -match "</Project>") {
        $domainCsprojText = $domainCsprojText -replace "</Project>", @"
  <ItemGroup>
    <ProjectReference Include="..\ArgusEngine.Contracts\ArgusEngine.Contracts.csproj" />
  </ItemGroup>
</Project>
"@
    } else {
        $domainCsprojText = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\ArgusEngine.Contracts\ArgusEngine.Contracts.csproj" />
  </ItemGroup>
</Project>
"@
    }

    Write-FileUtf8NoBom $DomainProject $domainCsprojText
}

Write-Step "Creating/fixing StoredAsset.cs"

Write-FileUtf8NoBom "src\ArgusEngine.Domain\Entities\StoredAsset.cs" @"
using ArgusEngine.Contracts;

namespace ArgusEngine.Domain.Entities;

public class StoredAsset
{
    public Guid Id { get; set; }

    public Guid TargetId { get; set; }

    public ReconTarget? Target { get; set; }

    public AssetKind Kind { get; set; }

    public AssetCategory Category { get; set; } = AssetCategory.Host;

    /// <summary>
    /// Normalized identity key, such as URL without fragment or lower-cased host.
    /// </summary>
    public string CanonicalKey { get; set; } = "";

    public string RawValue { get; set; } = "";

    public string? DisplayName { get; set; }

    public int Depth { get; set; }

    public string DiscoveredBy { get; set; } = "";

    /// <summary>
    /// Human-readable description of how the asset was found.
    /// </summary>
    public string DiscoveryContext { get; set; } = "";

    public DateTimeOffset DiscoveredAtUtc { get; set; }

    public DateTimeOffset? LastSeenAtUtc { get; set; }

    public decimal Confidence { get; set; } = 1.0m;

    public string LifecycleStatus { get; set; } = AssetLifecycleStatus.Queued;

    /// <summary>
    /// Type-specific payload, such as URL fetch request/response metadata.
    /// </summary>
    public string? TypeDetailsJson { get; set; }

    public string? FinalUrl { get; set; }

    public int RedirectCount { get; set; }

    public string? RedirectChainJson { get; set; }
}
"@

Write-Step "Patching HttpRequestQueueItem.cs"

$httpQueuePath = Join-Path $RepoRoot "src\ArgusEngine.Domain\Entities\HttpRequestQueueItem.cs"

if (-not (Test-Path -LiteralPath $httpQueuePath)) {
    Write-FileUtf8NoBom $httpQueuePath @"
using System.ComponentModel.DataAnnotations.Schema;
using ArgusEngine.Contracts;

namespace ArgusEngine.Domain.Entities;

public sealed class HttpRequestQueueItem
{
    public Guid Id { get; set; }
    public Guid AssetId { get; set; }
    public StoredAsset? Asset { get; set; }
    public Guid TargetId { get; set; }
    public AssetKind AssetKind { get; set; }
    public string Method { get; set; } = "GET";
    public string RequestUrl { get; set; } = "";
    public string DomainKey { get; set; } = "";
    public string State { get; set; } = HttpRequestQueueState.Queued;
    public int Priority { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public long? DurationMs { get; set; }
    public int? LastHttpStatus { get; set; }
    public string? LastError { get; set; }

    public string? RequestHeadersJson { get; set; }
    public string? RequestBody { get; set; }
    public string? ResponseHeadersJson { get; set; }
    public string? ResponseBody { get; set; }
    public string? ResponseContentType { get; set; }
    public long? ResponseContentLength { get; set; }
    public string? FinalUrl { get; set; }
    public int RedirectCount { get; set; }
    public string? RedirectChainJson { get; set; }

    [Column("request_headers_blob_id")]
    public Guid? RequestHeadersBlobId { get; set; }

    [Column("request_body_blob_id")]
    public Guid? RequestBodyBlobId { get; set; }

    [Column("response_headers_blob_id")]
    public Guid? ResponseHeadersBlobId { get; set; }

    [Column("response_body_blob_id")]
    public Guid? ResponseBodyBlobId { get; set; }

    [Column("redirect_chain_blob_id")]
    public Guid? RedirectChainBlobId { get; set; }

    [Column("response_body_sha256")]
    public string? ResponseBodySha256 { get; set; }

    [Column("response_body_preview")]
    public string? ResponseBodyPreview { get; set; }

    [Column("response_body_truncated")]
    public bool ResponseBodyTruncated { get; set; }
}
"@
} else {
    $httpQueue = Read-FileOrEmpty $httpQueuePath
    $httpQueue = Replace-ArgusNamespaces $httpQueue

    if ($httpQueue -notmatch "using ArgusEngine\.Contracts;") {
        $httpQueue = "using ArgusEngine.Contracts;`r`n" + $httpQueue
    }

    $httpQueue = $httpQueue -replace "namespace\s+[A-Za-z0-9_.]+\.Domain\.Entities;", "namespace ArgusEngine.Domain.Entities;"

    Write-FileUtf8NoBom $httpQueuePath $httpQueue
}

Write-Step "Normalizing ArgusEngine.Domain namespaces"

Get-ChildItem -LiteralPath (Join-Path $RepoRoot "src\ArgusEngine.Domain") -Recurse -File -Include *.cs |
    Where-Object { $_.FullName -notmatch "\\bin\\" -and $_.FullName -notmatch "\\obj\\" } |
    ForEach-Object {
        $text = Read-FileOrEmpty $_.FullName
        $patched = Replace-ArgusNamespaces $text

        if ($patched -ne $text) {
            Write-FileUtf8NoBom $_.FullName $patched
        }
    }

Write-Step "Patch complete"

if (-not $SkipBuild) {
    Write-Step "Running dotnet build .\ArgusEngine.slnx"
    dotnet build .\ArgusEngine.slnx
}