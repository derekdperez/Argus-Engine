#!/usr/bin/env bash
set -e

# Run Unit Tests
echo "Running Unit Tests..."
dotnet test src/tests/ArgusEngine.UnitTests/ArgusEngine.UnitTests.csproj --configuration Release

# Run Infrastructure Unit/Integration Tests (that don't require external services)
echo "Running Infrastructure Tests..."
dotnet test src/tests/ArgusEngine.Infrastructure.Tests/ArgusEngine.Infrastructure.Tests.csproj --configuration Release

# Run Database Integration Tests (requires Docker)
if docker info >/dev/null 2>&1; then
    echo "Running Database Integration Tests (with Testcontainers)..."
    dotnet test src/tests/ArgusEngine.IntegrationTests/ArgusEngine.IntegrationTests.csproj --configuration Release
else
    echo "Skipping Database Integration Tests because Docker is not running."
fi

echo "All tests completed successfully!"
