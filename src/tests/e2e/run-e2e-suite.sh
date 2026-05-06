#!/usr/bin/env bash
# Full E2E suite for a deployed Command Center + worker stack.
#
# These tests deliberately verify both API responses and the database mutations
# behind them. Run from the E2E EC2 host or any machine that can reach the app
# and Docker compose Postgres container.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
COMPOSE_FILE="${ARGUS_E2E_COMPOSE_FILE:-${ROOT}/deploy/docker-compose.yml}"
BASE_URL="${ARGUS_BASE_URL:-http://127.0.0.1:8080}"
MAX_WAIT_SECONDS="${ARGUS_E2E_MAX_WAIT_SECONDS:-180}"

tmp_dir="$(mktemp -d)"
cleanup() {
  rm -rf "$tmp_dir"
}
trap cleanup EXIT

compose() {
  docker compose -f "$COMPOSE_FILE" "$@"
}

pass() {
  printf 'PASS %s\n' "$*"
}

fail() {
  printf 'FAIL %s\n' "$*" >&2
  exit 1
}

wait_for_ready() {
  local started_at now
  started_at="$(date +%s)"
  while true; do
    if curl -fsS "${BASE_URL}/health/ready" >/dev/null 2>&1; then
      pass "Command Center ready"
      return 0
    fi

    now="$(date +%s)"
    if (( now - started_at > MAX_WAIT_SECONDS )); then
      compose logs --tail=120 command-center >&2 || true
      fail "Command Center did not become ready at ${BASE_URL}"
    fi

    sleep 3
  done
}

json_value() {
  python3 - "$1" "$2" <<'PY'
import json
import sys

path, key = sys.argv[1], sys.argv[2]
with open(path, encoding="utf-8") as handle:
    doc = json.load(handle)
value = doc
for part in key.split("."):
    value = value[part]
print(value)
PY
}

sql_escape() {
  printf "%s" "$1" | sed "s/'/''/g"
}

db_scalar() {
  compose exec -T postgres psql -U argus -d argus_engine -tAc "$1" | tr -d '[:space:]'
}

assert_db_equals() {
  local expected="$1"
  local sql="$2"
  local label="$3"
  local actual
  actual="$(db_scalar "$sql")"
  [[ "$actual" == "$expected" ]] || fail "${label}: expected ${expected}, got ${actual}"
  pass "$label"
}

assert_db_nonzero() {
  local sql="$1"
  local label="$2"
  local actual
  actual="$(db_scalar "$sql")"
  [[ "$actual" =~ ^[0-9]+$ ]] || fail "${label}: expected numeric count, got ${actual}"
  (( actual > 0 )) || fail "${label}: expected count > 0"
  pass "$label"
}

require_status() {
  local expected="$1"
  local actual="$2"
  local label="$3"
  local body="${4:-}"
  if [[ "$actual" != "$expected" ]]; then
    [[ -z "$body" ]] || cat "$body" >&2 || true
    fail "${label}: expected HTTP ${expected}, got ${actual}"
  fi
  pass "$label"
}

"${SCRIPT_DIR}/reset-e2e-database.sh"
wait_for_ready

assert_db_equals "0" "SELECT count(*) FROM recon_targets;" "fresh database starts without targets"
assert_db_nonzero "SELECT count(*) FROM worker_switches;" "startup seeded worker switches"

run_id="$(date +%s)"
target_domain="e2e-${run_id}.example.com"
updated_domain="e2e-updated-${run_id}.example.com"

create_body="${tmp_dir}/create-target.json"
create_status="$(curl -sS -o "$create_body" -w '%{http_code}' \
  -H 'Content-Type: application/json' \
  --data-binary "{\"rootDomain\":\"${target_domain}\",\"globalMaxDepth\":2}" \
  "${BASE_URL}/api/targets")"
require_status 201 "$create_status" "target create API returns Created" "$create_body"

target_id="$(json_value "$create_body" "id")"
created_root="$(json_value "$create_body" "rootDomain")"
created_depth="$(json_value "$create_body" "globalMaxDepth")"
[[ "$created_root" == "$target_domain" ]] || fail "target create returned rootDomain ${created_root}"
[[ "$created_depth" == "2" ]] || fail "target create returned globalMaxDepth ${created_depth}"
pass "target create response matches request"

target_domain_sql="$(sql_escape "$target_domain")"
target_id_sql="$(sql_escape "$target_id")"
assert_db_equals "1" "SELECT count(*) FROM recon_targets WHERE \"Id\" = '${target_id_sql}'::uuid AND \"RootDomain\" = '${target_domain_sql}' AND \"GlobalMaxDepth\" = 2;" "target create persisted recon_targets row"
assert_db_nonzero "SELECT count(*) FROM http_request_queue WHERE target_id = '${target_id_sql}'::uuid AND request_url ILIKE '%${target_domain_sql}%';" "target create seeded HTTP queue rows"
assert_db_nonzero "SELECT count(*) FROM outbox_messages WHERE message_type ILIKE '%TargetCreated%' AND payload_json::text ILIKE '%${target_domain_sql}%';" "target create persisted TargetCreated outbox message"

list_body="${tmp_dir}/targets.json"
curl -fsS "${BASE_URL}/api/targets" >"$list_body"
TARGET_ID="$target_id" TARGET_DOMAIN="$target_domain" TARGETS_FILE="$list_body" python3 - <<'PY'
import json
import os

with open(os.environ["TARGETS_FILE"], encoding="utf-8") as handle:
    rows = json.load(handle)

if not any(row.get("id") == os.environ["TARGET_ID"] and row.get("rootDomain") == os.environ["TARGET_DOMAIN"] for row in rows):
    raise SystemExit("created target was not returned by /api/targets")
PY
pass "target list API reflects created target"

update_body="${tmp_dir}/update-target.json"
update_status="$(curl -sS -o "$update_body" -w '%{http_code}' \
  -X PUT \
  -H 'Content-Type: application/json' \
  --data-binary "{\"rootDomain\":\"${updated_domain}\",\"globalMaxDepth\":4}" \
  "${BASE_URL}/api/targets/${target_id}")"
require_status 200 "$update_status" "target update API returns OK" "$update_body"

updated_domain_sql="$(sql_escape "$updated_domain")"
assert_db_equals "1" "SELECT count(*) FROM recon_targets WHERE \"Id\" = '${target_id_sql}'::uuid AND \"RootDomain\" = '${updated_domain_sql}' AND \"GlobalMaxDepth\" = 4;" "target update persisted changed domain and depth"

queue_settings_status="$(curl -sS -o "${tmp_dir}/queue-settings.out" -w '%{http_code}' \
  -X PUT \
  -H 'Content-Type: application/json' \
  --data-binary '{"enabled":false,"globalRequestsPerMinute":37,"perDomainRequestsPerMinute":3,"maxConcurrency":5,"requestTimeoutSeconds":17}' \
  "${BASE_URL}/api/http-request-queue/settings")"
require_status 204 "$queue_settings_status" "HTTP queue settings API returns NoContent" "${tmp_dir}/queue-settings.out"
assert_db_equals "1" "SELECT count(*) FROM http_request_queue_settings WHERE id = 1 AND enabled = false AND global_requests_per_minute = 37 AND per_domain_requests_per_minute = 3 AND max_concurrency = 5 AND request_timeout_seconds = 17;" "HTTP queue settings API persisted database changes"

worker_toggle_status="$(curl -sS -o "${tmp_dir}/worker-toggle.out" -w '%{http_code}' \
  -X PUT \
  -H 'Content-Type: application/json' \
  --data-binary '{"enabled":false}' \
  "${BASE_URL}/api/workers/Gatekeeper")"
require_status 204 "$worker_toggle_status" "worker toggle API returns NoContent" "${tmp_dir}/worker-toggle.out"
assert_db_equals "1" "SELECT count(*) FROM worker_switches WHERE \"WorkerKey\" = 'Gatekeeper' AND \"IsEnabled\" = false;" "worker toggle API persisted database change"

scaling_status="$(curl -sS -o "${tmp_dir}/worker-scaling.out" -w '%{http_code}' \
  -X PUT \
  -H 'Content-Type: application/json' \
  --data-binary '{"minTasks":2,"maxTasks":6,"targetBacklogPerTask":11}' \
  "${BASE_URL}/api/workers/scaling-settings/worker-spider")"
require_status 200 "$scaling_status" "worker scaling settings API returns OK" "${tmp_dir}/worker-scaling.out"
assert_db_equals "1" "SELECT count(*) FROM worker_scaling_settings WHERE scale_key = 'worker-spider' AND min_tasks = 2 AND max_tasks = 6 AND target_backlog_per_task = 11;" "worker scaling settings API persisted database change"

echo "E2E API/database suite completed successfully for ${target_id}."
