#!/usr/bin/env bash
# Smoke-test the running Argus Engine Command Center and its dependency diagnostics.
#
# Usage:
#   ./deploy/smoke-test.sh
#   BASE_URL=http://server:8081 ARGUS_DIAGNOSTICS_API_KEY=... ./deploy/smoke-test.sh

set -euo pipefail

BASE_URL="${BASE_URL:-${ARGUS_LOCAL_BASE_URL:-http://localhost:8081}}"
DIAGNOSTICS_KEY="${ARGUS_DIAGNOSTICS_API_KEY:-${NIGHTMARE_DIAGNOSTICS_API_KEY:-ci-smoke-test-key}}"
CURL_TIMEOUT="${CURL_TIMEOUT:-10}"

pass() {
  printf 'PASS %s\n' "$*"
}

fail() {
  printf 'FAIL %s\n' "$*" >&2
  exit 1
}

check_url() {
  local label="$1"
  local url="$2"
  local expected="${3:-200}"
  local code

  code="$(curl -k -sS -o /tmp/argus-smoke-body.txt -w '%{http_code}' --max-time "$CURL_TIMEOUT" "$url" || true)"

  if [[ "$code" == "$expected" ]]; then
    pass "$label ($code) $url"
  else
    printf 'Response body from %s:\n' "$url" >&2
    sed -n '1,120p' /tmp/argus-smoke-body.txt >&2 || true
    fail "$label expected HTTP $expected but got ${code:-curl-failed}: $url"
  fi
}

check_blazor_asset_from_home_page() {
  local html asset url

  html="$(curl -k -sS --max-time "$CURL_TIMEOUT" "${BASE_URL}/" || true)"
  asset="$(
    printf '%s' "$html" |
      grep -Eo '<script[^>]+src="[^"]*blazor\.web[^"]*\.js[^"]*"' |
      head -n 1 |
      sed -E 's/.*src="([^"]+)".*/\1/' || true
  )"

  if [[ -z "$asset" ]]; then
    printf 'Home page body from %s:\n' "${BASE_URL}/" >&2
    printf '%s\n' "$html" | sed -n '1,120p' >&2 || true
    fail "Could not find the rendered Blazor framework script URL on the home page."
  fi

  case "$asset" in
    http://* | https://*) url="$asset" ;;
    /*) url="${BASE_URL%/}${asset}" ;;
    *) url="${BASE_URL%/}/${asset}" ;;
  esac

  check_url "Blazor framework asset" "$url"
}

check_diagnostics() {
  local label="$1"
  local path="$2"
  local code

  code="$(
    curl -k -sS -o /tmp/argus-smoke-body.txt -w '%{http_code}' \
      --max-time "$CURL_TIMEOUT" \
      -H "X-Argus-Diagnostics-Key: ${DIAGNOSTICS_KEY}" \
      -H "X-Nightmare-Diagnostics-Key: ${DIAGNOSTICS_KEY}" \
      "${BASE_URL}${path}" || true
  )"

  if [[ "$code" == "200" ]]; then
    pass "$label ($code) ${BASE_URL}${path}"
    sed -n '1,80p' /tmp/argus-smoke-body.txt
    printf '\n'
  else
    printf 'Response body from %s:\n' "${BASE_URL}${path}" >&2
    sed -n '1,120p' /tmp/argus-smoke-body.txt >&2 || true
    fail "$label expected HTTP 200 but got ${code:-curl-failed}. Check ARGUS_DIAGNOSTICS_API_KEY."
  fi
}

printf 'Argus smoke test against %s\n' "$BASE_URL"

check_url "Live health" "${BASE_URL}/health/live"
check_url "Ready health" "${BASE_URL}/health/ready"
check_blazor_asset_from_home_page
check_url "App stylesheet" "${BASE_URL}/app.css"
check_diagnostics "Diagnostics self" "/api/diagnostics/self"
check_diagnostics "Dependency diagnostics" "/api/diagnostics/dependencies"

pass "Smoke test complete"
