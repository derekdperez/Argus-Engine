$ErrorActionPreference = "Stop"

if (-not (Test-Path "ArgusEngine.slnx")) {
    throw "Run from the repository root."
}

$projectPath = "src/ArgusEngine.Workers.Orchestration/ArgusEngine.Workers.Orchestration.csproj"
$slnx = Get-Content "ArgusEngine.slnx" -Raw

if ($slnx -notmatch [regex]::Escape($projectPath)) {
    $entry = '  <Project Path="src/ArgusEngine.Workers.Orchestration/ArgusEngine.Workers.Orchestration.csproj" />' + [Environment]::NewLine
    $anchor = '  <Project Path="src/ArgusEngine.Workers.PortScan/ArgusEngine.Workers.PortScan.csproj" />' + [Environment]::NewLine

    if ($slnx.Contains($anchor)) {
        $slnx = $slnx.Replace($anchor, $entry + $anchor)
    } else {
        $slnx = $slnx.Replace("</Solution>", $entry + "</Solution>")
    }

    Set-Content "ArgusEngine.slnx" $slnx -NoNewline
}

Write-Host "ReconOrchestrator overlay applied."
Write-Host "Optional build check: dotnet build src/ArgusEngine.Workers.Orchestration/ArgusEngine.Workers.Orchestration.csproj"
