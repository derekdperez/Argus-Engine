<#
.SYNOPSIS
Pack this project into one JSON file and one ZIP archive.

.DESCRIPTION
Creates packed.json and packed.zip by default, containing:
- JSON transport envelope with:
  - directory paths
  - file paths
  - file metadata
  - file contents as base64
- ZIP archive of the actual project files

Excludes the output/, .git/, bin/, obj/, debug/, artifacts/, and .artifacts/ directory trees.

Supports both Python-style arguments:
  .\pack.ps1 --root . --output packed.json --zip-output packed.zip

and PowerShell-style arguments:
  .\pack.ps1 -Root . -Output packed.json -ZipOutput packed.zip
#>

$ErrorActionPreference = "Stop"

$PackSchemaVersion = 1
$DefaultOutputName = "packed.json"
$DefaultZipName = "packed.zip"
$ExcludedDirNames = @("output", ".git", "bin", "obj", "debug", "artifacts", ".artifacts")

$script:ExcludedDirNameSet = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in $ExcludedDirNames) {
    [void]$script:ExcludedDirNameSet.Add($name)
}

function Show-Usage {
    Write-Host "Usage: .\pack.ps1 [--root <path>] [--output <path>] [--zip-output <path>]"
    Write-Host ""
    Write-Host "Options:"
    Write-Host "  --root, -Root             Project root to pack. Default: current directory."
    Write-Host "  --output, -Output         Output JSON file path. Default: packed.json."
    Write-Host "  --zip-output, -ZipOutput  Output ZIP file path. Default: packed.zip."
    Write-Host "  --help, -Help, -h         Show this help."
}

function Parse-Arguments {
    param([string[]]$Arguments)

    $parsed = [ordered]@{
        Root = "."
        Output = $DefaultOutputName
        ZipOutput = $DefaultZipName
    }

    for ($i = 0; $i -lt $Arguments.Count; $i++) {
        $arg = $Arguments[$i]
        switch -Regex ($arg) {
            '^(--help|-Help|-h|/\?)$' {
                Show-Usage
                exit 0
            }
            '^(--root|-Root)$' {
                if ($i + 1 -ge $Arguments.Count) { throw "Missing value for $arg" }
                $i++
                $parsed.Root = $Arguments[$i]
                continue
            }
            '^(--output|-Output)$' {
                if ($i + 1 -ge $Arguments.Count) { throw "Missing value for $arg" }
                $i++
                $parsed.Output = $Arguments[$i]
                continue
            }
            '^(--zip-output|-ZipOutput|-Zip-Output)$' {
                if ($i + 1 -ge $Arguments.Count) { throw "Missing value for $arg" }
                $i++
                $parsed.ZipOutput = $Arguments[$i]
                continue
            }
            default {
                throw "Unknown argument: $arg"
            }
        }
    }

    return $parsed
}

function Get-UnresolvedFileSystemPath {
    param([Parameter(Mandatory=$true)][string]$PathText)
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PathText)
}

function Join-FullPath {
    param(
        [Parameter(Mandatory=$true)][string]$BasePath,
        [Parameter(Mandatory=$true)][string]$ChildPath
    )
    return [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($BasePath, $ChildPath))
}

function Add-TrailingDirectorySeparator {
    param([Parameter(Mandatory=$true)][string]$PathText)
    if ($PathText.EndsWith([System.IO.Path]::DirectorySeparatorChar) -or $PathText.EndsWith([System.IO.Path]::AltDirectorySeparatorChar)) {
        return $PathText
    }
    return $PathText + [System.IO.Path]::DirectorySeparatorChar
}

function Get-PathComparison {
    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        return [System.StringComparison]::OrdinalIgnoreCase
    }
    return [System.StringComparison]::Ordinal
}

function Get-PathStringComparer {
    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        return [System.StringComparer]::OrdinalIgnoreCase
    }
    return [System.StringComparer]::Ordinal
}

function Get-RelativePosixPath {
    param(
        [Parameter(Mandatory=$true)][string]$PathText,
        [Parameter(Mandatory=$true)][string]$RootPath
    )

    $rootFull = [System.IO.Path]::GetFullPath($RootPath)
    $pathFull = [System.IO.Path]::GetFullPath($PathText)
    $comparison = Get-PathComparison

    if ([string]::Equals($pathFull, $rootFull, $comparison)) {
        return "."
    }

    $rootWithSeparator = Add-TrailingDirectorySeparator $rootFull
    if (-not $pathFull.StartsWith($rootWithSeparator, $comparison)) {
        throw "Path is not inside root. Path: $pathFull Root: $rootFull"
    }

    $relative = $pathFull.Substring($rootWithSeparator.Length)
    $relative = $relative.Replace([System.IO.Path]::DirectorySeparatorChar, '/')
    $relative = $relative.Replace([System.IO.Path]::AltDirectorySeparatorChar, '/')
    return $relative
}

function Test-ExcludedDirName {
    param([Parameter(Mandatory=$true)][string]$Name)
    return $script:ExcludedDirNameSet.Contains($Name.Trim())
}

function Get-Sha256Hex {
    param([Parameter(Mandatory=$true)][byte[]]$Bytes)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($Bytes)
    }
    finally {
        $sha.Dispose()
    }

    return (($hash | ForEach-Object { $_.ToString("x2") }) -join "")
}

function Get-FileModeBits {
    param([Parameter(Mandatory=$true)][string]$FilePath)

    # Python's os.stat(path).st_mode & 0o777 on Windows generally maps normal files
    # to 0666 and read-only files to 0444. This preserves that useful behavior.
    try {
        $attrs = [System.IO.File]::GetAttributes($FilePath)
        if (($attrs -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
            return 292 # 0444
        }
        return 438 # 0666
    }
    catch {
        return 438
    }
}

function New-FileRecord {
    param(
        [Parameter(Mandatory=$true)][System.IO.FileInfo]$File,
        [Parameter(Mandatory=$true)][string]$RootPath
    )

    $raw = [System.IO.File]::ReadAllBytes($File.FullName)
    return [ordered]@{
        path = Get-RelativePosixPath -PathText $File.FullName -RootPath $RootPath
        size_bytes = [int64]$File.Length
        sha256 = Get-Sha256Hex -Bytes $raw
        mode = [int](Get-FileModeBits -FilePath $File.FullName)
        encoding = "base64"
        content = [System.Convert]::ToBase64String($raw)
    }
}

function Test-ShouldSkipFile {
    param(
        [Parameter(Mandatory=$true)][System.IO.FileInfo]$File,
        [Parameter(Mandatory=$true)][string]$RootPath,
        [Parameter(Mandatory=$true)]$GeneratedOutputs
    )

    $resolved = [System.IO.Path]::GetFullPath($File.FullName)
    if ($GeneratedOutputs.Contains($resolved)) {
        return $true
    }

    $relative = Get-RelativePosixPath -PathText $resolved -RootPath $RootPath
    $parts = $relative -split '/'
    if ($parts.Count -gt 1) {
        for ($i = 0; $i -lt ($parts.Count - 1); $i++) {
            if (Test-ExcludedDirName $parts[$i]) {
                return $true
            }
        }
    }

    return $false
}

function Get-SortedChildDirectories {
    param([Parameter(Mandatory=$true)][string]$DirectoryPath)
    return @(Get-ChildItem -LiteralPath $DirectoryPath -Directory -Force | Sort-Object -Property Name)
}

function Get-SortedChildFiles {
    param([Parameter(Mandatory=$true)][string]$DirectoryPath)
    return @(Get-ChildItem -LiteralPath $DirectoryPath -File -Force | Sort-Object -Property Name)
}

function Build-PackPayload {
    param(
        [Parameter(Mandatory=$true)][string]$RootPath,
        [Parameter(Mandatory=$true)][string]$OutputPath,
        [Parameter(Mandatory=$true)][string]$ZipPath
    )

    $rootFull = [System.IO.Path]::GetFullPath($RootPath)
    $outputFull = [System.IO.Path]::GetFullPath($OutputPath)
    $zipFull = [System.IO.Path]::GetFullPath($ZipPath)

    if (-not [System.IO.Directory]::Exists($rootFull)) {
        throw "Root directory not found: $rootFull"
    }

    $generatedOutputs = New-Object "System.Collections.Generic.HashSet[string]" (Get-PathStringComparer)
    [void]$generatedOutputs.Add($outputFull)
    [void]$generatedOutputs.Add($zipFull)

    $directories = New-Object "System.Collections.Generic.List[string]"
    $files = New-Object "System.Collections.Generic.List[object]"

    function Walk-ForPayload {
        param([Parameter(Mandatory=$true)][string]$CurrentPath)

        $relCurrent = Get-RelativePosixPath -PathText $CurrentPath -RootPath $rootFull
        if ($relCurrent -ne ".") {
            [void]$directories.Add($relCurrent)
        }

        $childFiles = Get-SortedChildFiles -DirectoryPath $CurrentPath
        foreach ($file in $childFiles) {
            if (Test-ShouldSkipFile -File $file -RootPath $rootFull -GeneratedOutputs $generatedOutputs) {
                continue
            }
            [void]$files.Add((New-FileRecord -File $file -RootPath $rootFull))
        }

        $childDirs = Get-SortedChildDirectories -DirectoryPath $CurrentPath
        foreach ($dir in $childDirs) {
            if (Test-ExcludedDirName $dir.Name) {
                continue
            }
            Walk-ForPayload -CurrentPath $dir.FullName
        }
    }

    Walk-ForPayload -CurrentPath $rootFull

    $directoriesSorted = @($directories | Sort-Object -Unique)
    $filesSorted = @($files | Sort-Object { [string]($_.path).ToLowerInvariant() })

    return [ordered]@{
        pack_schema_version = $PackSchemaVersion
        generated_at_utc = ([System.DateTime]::UtcNow.ToString("o"))
        root = "."
        excluded_directories = @($ExcludedDirNames | Sort-Object)
        directory_count = [int]$directoriesSorted.Count
        file_count = [int]$filesSorted.Count
        directories = @($directoriesSorted)
        files = @($filesSorted)
    }
}

function Write-ZipArchive {
    param(
        [Parameter(Mandatory=$true)][string]$RootPath,
        [Parameter(Mandatory=$true)][string]$ZipPath,
        [Parameter(Mandatory=$true)][string]$OutputPath
    )

    try { Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue } catch {}
    try { Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue } catch {}

    $rootFull = [System.IO.Path]::GetFullPath($RootPath)
    $zipFull = [System.IO.Path]::GetFullPath($ZipPath)
    $outputFull = [System.IO.Path]::GetFullPath($OutputPath)

    $zipParent = [System.IO.Path]::GetDirectoryName($zipFull)
    if ($zipParent) {
        [System.IO.Directory]::CreateDirectory($zipParent) | Out-Null
    }
    if ([System.IO.File]::Exists($zipFull)) {
        [System.IO.File]::Delete($zipFull)
    }

    $generatedOutputs = New-Object "System.Collections.Generic.HashSet[string]" (Get-PathStringComparer)
    [void]$generatedOutputs.Add($outputFull)
    [void]$generatedOutputs.Add($zipFull)

    $fileCount = 0
    $totalBytes = [int64]0

    $zip = [System.IO.Compression.ZipFile]::Open($zipFull, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        function Walk-ForZip {
            param([Parameter(Mandatory=$true)][string]$CurrentPath)

            $childFiles = Get-SortedChildFiles -DirectoryPath $CurrentPath
            foreach ($file in $childFiles) {
                if (Test-ShouldSkipFile -File $file -RootPath $rootFull -GeneratedOutputs $generatedOutputs) {
                    continue
                }

                $arcName = Get-RelativePosixPath -PathText $file.FullName -RootPath $rootFull
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $zip,
                    $file.FullName,
                    $arcName,
                    [System.IO.Compression.CompressionLevel]::Optimal
                ) | Out-Null

                $script:ZipFileCountForCurrentRun++
                $script:ZipTotalBytesForCurrentRun += [int64]$file.Length
            }

            $childDirs = Get-SortedChildDirectories -DirectoryPath $CurrentPath
            foreach ($dir in $childDirs) {
                if (Test-ExcludedDirName $dir.Name) {
                    continue
                }
                Walk-ForZip -CurrentPath $dir.FullName
            }
        }

        $script:ZipFileCountForCurrentRun = 0
        $script:ZipTotalBytesForCurrentRun = [int64]0
        Walk-ForZip -CurrentPath $rootFull
        $fileCount = $script:ZipFileCountForCurrentRun
        $totalBytes = $script:ZipTotalBytesForCurrentRun
    }
    finally {
        $zip.Dispose()
        Remove-Variable -Name ZipFileCountForCurrentRun -Scope Script -ErrorAction SilentlyContinue
        Remove-Variable -Name ZipTotalBytesForCurrentRun -Scope Script -ErrorAction SilentlyContinue
    }

    return [ordered]@{
        FileCount = [int]$fileCount
        TotalBytes = [int64]$totalBytes
    }
}

function New-Utf8NoBomEncoding {
    return New-Object System.Text.UTF8Encoding -ArgumentList $false
}

$parsedArgs = Parse-Arguments -Arguments $args

$rootPath = [System.IO.Path]::GetFullPath((Get-UnresolvedFileSystemPath ([string]$parsedArgs.Root)))
if (-not [System.IO.Directory]::Exists($rootPath)) {
    throw "Root directory not found: $rootPath"
}

if ([System.IO.Path]::IsPathRooted([string]$parsedArgs.Output)) {
    $outputPath = [System.IO.Path]::GetFullPath((Get-UnresolvedFileSystemPath ([string]$parsedArgs.Output)))
}
else {
    $outputPath = Join-FullPath -BasePath $rootPath -ChildPath ([string]$parsedArgs.Output)
}

if ([System.IO.Path]::IsPathRooted([string]$parsedArgs.ZipOutput)) {
    $zipPath = [System.IO.Path]::GetFullPath((Get-UnresolvedFileSystemPath ([string]$parsedArgs.ZipOutput)))
}
else {
    $zipPath = Join-FullPath -BasePath $rootPath -ChildPath ([string]$parsedArgs.ZipOutput)
}

$payload = Build-PackPayload -RootPath $rootPath -OutputPath $outputPath -ZipPath $zipPath
$innerJsonText = $payload | ConvertTo-Json -Depth 100 -Compress
$utf8NoBom = New-Utf8NoBomEncoding
$innerJsonBytes = $utf8NoBom.GetBytes($innerJsonText)

$outerPayload = [ordered]@{
    transport_schema_version = 1
    transport_encoding = "base64-json"
    generated_at_utc = ([System.DateTime]::UtcNow.ToString("o"))
    payload_sha256 = Get-Sha256Hex -Bytes $innerJsonBytes
    payload_base64 = [System.Convert]::ToBase64String($innerJsonBytes)
}

$outputParent = [System.IO.Path]::GetDirectoryName($outputPath)
if ($outputParent) {
    [System.IO.Directory]::CreateDirectory($outputParent) | Out-Null
}
$outerJsonText = ($outerPayload | ConvertTo-Json -Depth 20) + "`n"
[System.IO.File]::WriteAllText($outputPath, $outerJsonText, $utf8NoBom)

$zipResult = Write-ZipArchive -RootPath $rootPath -ZipPath $zipPath -OutputPath $outputPath
$zipSizeBytes = [int64]0
if ([System.IO.File]::Exists($zipPath)) {
    $zipSizeBytes = (Get-Item -LiteralPath $zipPath).Length
}

$totalBytes = [int64]0
foreach ($item in @($payload.files)) {
    $totalBytes += [int64]$item.size_bytes
}

Write-Host "Packed root: $rootPath"
Write-Host "Packed JSON: $outputPath"
Write-Host "Packed ZIP: $zipPath"
Write-Host "Directories: $($payload.directory_count)"
Write-Host "Files in JSON: $($payload.file_count)"
Write-Host "Files in ZIP: $($zipResult.FileCount)"
Write-Host "Original bytes (JSON payload source): $totalBytes"
Write-Host "Original bytes (ZIP source): $($zipResult.TotalBytes)"
Write-Host "ZIP size bytes: $zipSizeBytes"
