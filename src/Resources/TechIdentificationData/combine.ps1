param(
    [Parameter(Mandatory = $false)]
    [string] $InputDirectory = ".",

    [Parameter(Mandatory = $false)]
    [string] $OutputFile = ".\merged-technologies.json"
)

if (-not (Test-Path $InputDirectory)) {
    throw "Input directory does not exist: $InputDirectory"
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputFile)
$outputDirectory = Split-Path -Parent $outputFullPath

if ($outputDirectory -and -not (Test-Path $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$merged = [ordered]@{}

$jsonFiles = Get-ChildItem -Path $InputDirectory -Filter "*.json" -File |
    Where-Object { $_.FullName -ne $outputFullPath } |
    Sort-Object Name

foreach ($file in $jsonFiles) {
    Write-Host "Merging $($file.Name)..."

    $json = Get-Content -Path $file.FullName -Raw | ConvertFrom-Json

    foreach ($property in $json.PSObject.Properties) {
        $key = $property.Name

        if ($merged.Contains($key)) {
            Write-Warning "Duplicate key '$key' found in $($file.Name). Overwriting previous value."
        }

        $merged[$key] = $property.Value
    }
}

$merged |
    ConvertTo-Json -Depth 100 |
    Set-Content -Path $outputFullPath -Encoding UTF8

Write-Host "Merged $($jsonFiles.Count) JSON files into $outputFullPath"