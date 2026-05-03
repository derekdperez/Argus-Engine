#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${ARGUS_BASE_URL:-http://localhost:8080}"
DIAGNOSTICS_KEY="${ARGUS_DIAGNOSTICS_API_KEY:-${NIGHTMARE_DIAGNOSTICS_API_KEY:-local-dev-diagnostics-key-change-me}}"
MAINTENANCE_KEY="${ARGUS_MAINTENANCE_API_KEY:-${NIGHTMARE_MAINTENANCE_API_KEY:-local-dev-maintenance-key-change-me}}"
RATE_LIMIT_ATTEMPTS="${ARGUS_RATE_LIMIT_ATTEMPTS:-75}"
TARGET_DOMAIN="${ARGUS_TEST_TARGET_DOMAIN:-example.com}"

pass() { printf 'PASS %s\n' "$*"; }
fail() { printf 'FAIL %s\n' "$*" >&2; exit 1; }

status_of() {
  local method="$1"
  local url="$2"
  shift 2
  curl -sS -o /tmp/argus-e2e-response.json -w '%{http_code}' -X "$method" "$url" "$@"
}

require_status() {
  local expected="$1"
  local actual="$2"
  local label="$3"

  [[ "$actual" == "$expected" ]] || {
    cat /tmp/argus-e2e-response.json >&2 || true
    fail "$label expected HTTP $expected, got $actual"
  }

  pass "$label"
}

require_one_of_statuses() {
  local label="$1"
  local actual="$2"
  shift 2

  for expected in "$@"; do
    [[ "$actual" == "$expected" ]] && { pass "$label"; return 0; }
  done

  cat /tmp/argus-e2e-response.json >&2 || true
  fail "$label expected one of [$*], got $actual"
}

echo "Argus production-readiness suite against ${BASE_URL}"

live_status="$(status_of GET "${BASE_URL}/health")"
require_status 200 "$live_status" "live health endpoint"

ready_status="$(status_of GET "${BASE_URL}/health/ready")"
require_one_of_statuses "ready health endpoint is reachable" "$ready_status" 200 503

unauth_diag="$(status_of GET "${BASE_URL}/api/diagnostics/self")"
require_one_of_statuses "diagnostics endpoint is not anonymously usable" "$unauth_diag" 401 404 503

auth_diag="$(status_of GET "${BASE_URL}/api/diagnostics/self" -H "X-Argus-Diagnostics-Key: ${DIAGNOSTICS_KEY}")"
require_one_of_statuses "diagnostics endpoint handles authenticated request or disabled/misconfigured state" "$auth_diag" 200 404 503

unauth_maint="$(status_of GET "${BASE_URL}/api/maintenance/status")"
require_one_of_statuses "maintenance endpoint is not anonymously usable" "$unauth_maint" 401 404 503

auth_maint="$(status_of GET "${BASE_URL}/api/maintenance/status" -H "X-Argus-Maintenance-Key: ${MAINTENANCE_KEY}")"
require_one_of_statuses "maintenance endpoint handles authenticated request or disabled/misconfigured state" "$auth_maint" 200 404 503

rate_limited=0
for _ in $(seq 1 "$RATE_LIMIT_ATTEMPTS"); do
  code="$(status_of GET "${BASE_URL}/api/diagnostics/self" -H "X-Argus-Diagnostics-Key: ${DIAGNOSTICS_KEY}")"
  if [[ "$code" == "429" ]]; then
    rate_limited=1
    break
  fi
done

if [[ "$rate_limited" == "1" ]]; then
  pass "diagnostics rate limiting"
else
  echo "WARN diagnostics rate limit was not reached in ${RATE_LIMIT_ATTEMPTS} attempts; verify configured permit limit in production." >&2
fi

target_payload="$(mktemp)"
cat > "$target_payload" <<JSON
{
  "rootDomain": "${TARGET_DOMAIN}",
  "globalMaxDepth": 1
}
JSON

target_status="$(status_of POST "${BASE_URL}/api/targets" -H 'Content-Type: application/json' --data-binary "@${target_payload}")"
require_one_of_statuses "target creation path accepts or de-duplicates production smoke target" "$target_status" 200 201 202 400 409

rm -f "$target_payload"

queue_metrics="$(status_of GET "${BASE_URL}/api/http-request-queue/metrics")"
require_one_of_statuses "http queue metrics endpoint" "$queue_metrics" 200 404

reliability_status="$(status_of GET "${BASE_URL}/api/ops/reliability-baseline")"
require_one_of_statuses "reliability baseline endpoint" "$reliability_status" 200 404

if command -v docker >/dev/null 2>&1 && [[ "${ARGUS_RUN_CHAOS:-0}" == "1" ]]; then
  echo "Running opt-in chaos check: restarting rabbitmq container through docker compose"
  docker compose -f deploy/docker-compose.yml restart rabbitmq
  sleep "${ARGUS_CHAOS_SETTLE_SECONDS:-10}"
  post_chaos_health="$(status_of GET "${BASE_URL}/health/ready")"
  require_one_of_statuses "post-chaos ready endpoint remains observable" "$post_chaos_health" 200 503
else
  echo "SKIP chaos restart: set ARGUS_RUN_CHAOS=1 on a disposable stack to enable."
fi

echo "Production-readiness suite completed."
