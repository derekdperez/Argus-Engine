#!/usr/bin/env bash
set -Eeuo pipefail

BASE_URL="${ARGUS_LOCAL_BASE_URL:-${BASE_URL:-http://127.0.0.1:8081}}"
DIAGNOSTICS_KEY="${ARGUS_DIAGNOSTICS_API_KEY:-${NIGHTMARE_DIAGNOSTICS_API_KEY:-ci-smoke-test-key}}"
ATTEMPTS="${ARGUS_SMOKE_ATTEMPTS:-90}"
SLEEP_SECONDS="${ARGUS_SMOKE_SLEEP_SECONDS:-2}"

curl_probe() {
  local url="$1"
  curl -fsS \
    -H "Accept: application/json" \
    -H "X-Argus-Diagnostics-Key: ${DIAGNOSTICS_KEY}" \
    -H "X-Nightmare-Diagnostics-Key: ${DIAGNOSTICS_KEY}" \
    "$url" >/dev/null
}

wait_for() {
  local url="$1"
  local attempt=1

  until curl_probe "$url"; do
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
  curl_probe "${BASE_URL}${path}"
}

echo "Running split Command Center smoke checks against ${BASE_URL}"

wait_for "${BASE_URL}/health/ready"

check "/health/ready"
check "/api/gateway/routes"
check "/api/status/summary"
check "/api/discovery/routes"
check "/api/workers/control/routes"
check "/api/maintenance/routes"

echo "Split Command Center smoke checks passed."
