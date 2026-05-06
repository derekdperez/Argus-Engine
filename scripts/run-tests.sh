#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="${CONFIGURATION:-Release}"
MODE="${1:-all}"

run_dotnet_tests() {
  local project="$1"
  local args=()
  if [[ "${ARGUS_TEST_NO_BUILD:-0}" == "1" ]]; then
    args+=(--no-build)
  fi
  echo "Running ${project}..."
  dotnet test "$project" --configuration "$CONFIGURATION" "${args[@]}"
}

run_unit_tests() {
  run_dotnet_tests "src/tests/ArgusEngine.UnitTests/ArgusEngine.UnitTests.csproj"
  run_dotnet_tests "src/tests/ArgusEngine.Infrastructure.Tests/ArgusEngine.Infrastructure.Tests.csproj"
  run_dotnet_tests "src/tests/ArgusEngine.CommandCenter.Tests/ArgusEngine.CommandCenter.Tests.csproj"
}

run_integration_tests() {
  if docker info >/dev/null 2>&1; then
    run_dotnet_tests "src/tests/ArgusEngine.IntegrationTests/ArgusEngine.IntegrationTests.csproj"
  else
    echo "Skipping database integration tests because Docker is not running."
  fi
}

run_e2e_tests() {
  "src/tests/e2e/run-e2e-suite.sh"
}

case "$MODE" in
  unit)
    run_unit_tests
    ;;
  integration)
    run_integration_tests
    ;;
  e2e)
    run_e2e_tests
    ;;
  all)
    run_unit_tests
    run_integration_tests
    ;;
  *)
    echo "Usage: $0 [unit|integration|e2e|all]" >&2
    exit 2
    ;;
esac

echo "Test mode '${MODE}' completed successfully."
