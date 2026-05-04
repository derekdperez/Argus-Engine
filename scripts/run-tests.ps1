$ErrorActionPreference = "Stop"

Write-Host "Running Unit Tests..." -ForegroundColor Cyan
dotnet test src/tests/ArgusEngine.UnitTests/ArgusEngine.UnitTests.csproj --configuration Release

Write-Host "Running Infrastructure Tests..." -ForegroundColor Cyan
dotnet test src/tests/ArgusEngine.Infrastructure.Tests/ArgusEngine.Infrastructure.Tests.csproj --configuration Release

# Check for Docker
$dockerRunning = $false
try {
    docker info > $null 2>&1
    $dockerRunning = $true
} catch {
    $dockerRunning = $false
}

if ($dockerRunning) {
    Write-Host "Running Database Integration Tests (with Testcontainers)..." -ForegroundColor Cyan
    dotnet test src/tests/ArgusEngine.IntegrationTests/ArgusEngine.IntegrationTests.csproj --configuration Release
} else {
    Write-Warning "Skipping Database Integration Tests because Docker is not running."
}

Write-Host "All tests completed successfully!" -ForegroundColor Green
