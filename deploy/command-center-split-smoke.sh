#!/usr/bin/env bash

set -Eeuo pipefail

BASE_URL="${ARGUS_LOCAL_BASE_URL:-http://127.0.0.1:8081}"
ATTEMPTS="${ARGUS_SMOKE_ATTEMPTS:-90}"
SLEEP_SECONDS="${ARGUS_SMOKE_SLEEP_SECONDS:-2}"

wait_for() {
  local url="$1"
  local attempt=1

  until curl -fsS "$url" >/dev/null 2>&1; do
    if (( attempt >= ATTEMPTS )); then
      echo "ERROR: Timed out waiting for $url" >&2
      return 1
    fi

    sleep "$SLEEP_SECONDS"
    attempt=$((attempt + 1))
  done
}

check() {
  local path="$1"
  echo "GET $path"
  curl -fsS "$BASE_URL$path" >/dev/null
}

wait_for "$BASE_URL/health/ready"

check "/health/ready"
check "/api/gateway/routes"
check "/api/status/summary"
check "/api/discovery/routes"
check "/api/workers/control/routes"
check "/api/maintenance/routes"

echo "Split CommandCenter smoke checks passed."
