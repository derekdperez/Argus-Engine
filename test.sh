#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-full}"
CONFIGURATION="${CONFIGURATION:-Debug}"
RESULTS_DIR="${RESULTS_DIR:-$ROOT/TestResults}"

unit_projects=(
  "$ROOT/tests/NightmareV2.Application.Tests/NightmareV2.Application.Tests.csproj"
  "$ROOT/tests/NightmareV2.Infrastructure.Tests/NightmareV2.Infrastructure.Tests.csproj"
  "$ROOT/tests/NightmareV2.Workers.Spider.Tests/NightmareV2.Workers.Spider.Tests.csproj"
  "$ROOT/tests/NightmareV2.CommandCenter.Tests/NightmareV2.CommandCenter.Tests.csproj"
  "$ROOT/tests/NightmareV2.TechnologyIdentification.Tests/NightmareV2.TechnologyIdentification.Tests.csproj"
  "$ROOT/tests/NightmareV2.Workers.Enum.Tests/NightmareV2.Workers.Enum.Tests.csproj"
)

run_projects() {
  local projects=("$@")
  for project in "${projects[@]}"; do
    echo "==> dotnet test ${project#$ROOT/}"
    dotnet test "$project" --configuration "$CONFIGURATION"
  done
}

case "$MODE" in
  unit)
    run_projects "${unit_projects[@]}"
    ;;

  integration)
    # Current integration-style tests use deterministic in-process fakes/mocks.
    # External Postgres/Redis/RabbitMQ contract tests should be added here when Testcontainers is introduced.
    run_projects \
      "$ROOT/tests/NightmareV2.Infrastructure.Tests/NightmareV2.Infrastructure.Tests.csproj" \
      "$ROOT/tests/NightmareV2.Workers.Enum.Tests/NightmareV2.Workers.Enum.Tests.csproj"
    ;;

  smoke)
    "$ROOT/deploy/smoke-test.sh"
    ;;

  e2e)
    "$ROOT/tests/e2e/command-center-target-workflow.sh"
    ;;

  coverage)
    mkdir -p "$RESULTS_DIR"
    dotnet test "$ROOT/NightmareV2.slnx" \
      --configuration "$CONFIGURATION" \
      --results-directory "$RESULTS_DIR" \
      --collect:"XPlat Code Coverage"
    ;;

  full)
    run_projects "${unit_projects[@]}"
    ;;

  *)
    cat >&2 <<USAGE
Usage: scripts/test.sh [unit|integration|smoke|e2e|coverage|full]

Environment:
  CONFIGURATION=Debug|Release
  RESULTS_DIR=/path/to/TestResults
  BASE_URL=http://localhost:8080   # used by smoke/e2e
USAGE
    exit 2
    ;;
esac
