#!/usr/bin/env bash

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MODE="${1:-full}"
CONFIGURATION="${CONFIGURATION:-Debug}"
RESULTS_DIR="${RESULTS_DIR:-$ROOT/TestResults}"

unit_projects=(
  "$ROOT/src/tests/ArgusEngine.UnitTests/ArgusEngine.UnitTests.csproj"
  "$ROOT/src/tests/ArgusEngine.CommandCenter.Tests/ArgusEngine.CommandCenter.Tests.csproj"
  "$ROOT/src/tests/ArgusEngine.Infrastructure.Tests/ArgusEngine.Infrastructure.Tests.csproj"
)

integration_projects=(
  "$ROOT/src/tests/ArgusEngine.IntegrationTests/ArgusEngine.IntegrationTests.csproj"
)

run_projects() {
  local projects=("$@")

  for project in "${projects[@]}"; do
    if [[ ! -f "$project" ]]; then
      echo "Missing test project: ${project#$ROOT/}" >&2
      exit 1
    fi

    echo "==> dotnet test ${project#$ROOT/}"
    dotnet test "$project" --configuration "$CONFIGURATION"
  done
}

case "$MODE" in
  unit)
    run_projects "${unit_projects[@]}"
    ;;

  integration)
    run_projects "${integration_projects[@]}"
    ;;

  smoke)
    "$ROOT/deploy/smoke-test.sh"
    ;;

  e2e)
    "$ROOT/src/tests/e2e/command-center-target-workflow.sh"
    ;;

  coverage)
    mkdir -p "$RESULTS_DIR"

    dotnet test "$ROOT/ArgusEngine.slnx" \
      --configuration "$CONFIGURATION" \
      --results-directory "$RESULTS_DIR" \
      --collect:"XPlat Code Coverage"
    ;;

  full)
    run_projects "${unit_projects[@]}" "${integration_projects[@]}"
    ;;

  *)
    cat >&2 <<USAGE
Usage: ./test.sh [unit|integration|smoke|e2e|coverage|full]

Environment:
  CONFIGURATION=Debug|Release
  RESULTS_DIR=/path/to/TestResults
  BASE_URL=http://localhost:8080 # used by smoke/e2e
USAGE
    exit 2
    ;;
esac
