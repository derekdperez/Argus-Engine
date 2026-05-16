#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${ARGUS_COMMAND_CENTER_URL:-http://localhost:8081}"
DB_HOST="${ARGUS_DB_HOST:-localhost}"
DB_PORT="${ARGUS_DB_PORT:-5432}"
DB_NAME="${ARGUS_DB_NAME:-argus}"
DB_USER="${ARGUS_DB_USER:-argus}"
MAX_WAIT_SECONDS="${ARGUS_E2E_MAX_WAIT_SECONDS:-300}"
POLL_INTERVAL="${ARGUS_E2E_POLL_INTERVAL:-10}"

tmp_dir="$(mktemp -d)"
cleanup() {
  rm -rf "$tmp_dir"
}
trap cleanup EXIT

TARGET_DOMAIN="${ARGUS_E2E_TARGET_DOMAIN:-e2e-recon-$(date +%s).example.com}"
TEST_TARGET_ID=""

pass_count=0
fail_count=0

log_pass() {
  echo "[PASS] $1"
  ((pass_count++))
}

log_fail() {
  echo "[FAIL] $1"
  ((fail_count++))
}

log_info() {
  echo "[INFO] $1"
}

psql_query() {
  PGPASSWORD="${ARGUS_DB_PASSWORD:-argus}" psql -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" -t -c "$1" 2>/dev/null | xargs || echo ""
}

request() {
  curl -fsS "$@"
}

wait_for_command_center() {
  local started_at
  started_at="$(date +%s)"

  while true; do
    if request "$BASE_URL/api/status" >/dev/null 2>&1; then
      return 0
    fi

    local now
    now="$(date +%s)"

    if (( now - started_at > MAX_WAIT_SECONDS )); then
      echo "Command Center did not become ready at $BASE_URL within ${MAX_WAIT_SECONDS}s." >&2
      return 1
    fi

    sleep 2
  done
}

wait_for_db() {
  local started_at
  started_at="$(date +%s)"

  while true; do
    if PGPASSWORD="${ARGUS_DB_PASSWORD:-argus}" psql -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" -c "SELECT 1" >/dev/null 2>&1; then
      return 0
    fi

    local now
    now="$(date +%s)"

    if (( now - started_at > 30 )); then
      echo "Database did not become ready at $DB_HOST:$DB_PORT within 30s." >&2
      return 1
    fi

    sleep 2
  done
}

echo "=== Recon Orchestrator E2E Test Suite ==="
echo ""

log_info "Step 0: Verify prerequisites"

if wait_for_command_center; then
  log_pass "Command Center is reachable at $BASE_URL"
else
  log_fail "Command Center not reachable"
  exit 1
fi

if wait_for_db; then
  log_pass "Database is reachable at $DB_HOST:$DB_PORT"
else
  log_fail "Database not reachable"
  exit 1
fi

log_info "Step 1: Create target $TARGET_DOMAIN"

create_response="$tmp_dir/create-target.json"
request \
  -H 'Content-Type: application/json' \
  -d "{\"rootDomain\":\"$TARGET_DOMAIN\",\"globalMaxDepth\":3}" \
  "$BASE_URL/api/targets" > "$create_response" || {
    log_fail "Failed to create target"
    cat "$create_response" >&2
    exit 1
  }

if grep -q '"id"' "$create_response"; then
  log_pass "Target created successfully"
  TEST_TARGET_ID=$(grep -o '"id":"[^"]*"' "$create_response" | head -1 | cut -d'"' -f4)
  log_info "Target ID: $TEST_TARGET_ID"
else
  log_fail "Target creation response missing ID"
  cat "$create_response" >&2
  exit 1
fi

log_info "Step 2: Attach target to recon orchestrator"

attach_response="$tmp_dir/attach.json"
request \
  -X POST \
  -H 'Content-Type: application/json' \
  -d '{"AttachedBy": "e2e-test"}' \
  "$BASE_URL/api/recon-agent/targets/$TEST_TARGET_ID/attach" > "$attach_response" || {
    log_fail "Failed to attach target to recon orchestrator"
    cat "$attach_response" >&2
    exit 1
  }

if grep -q '"status"' "$attach_response"; then
  log_pass "Target attached to recon orchestrator"
else
  log_fail "Attach response missing status"
  cat "$attach_response" >&2
  exit 1
fi

log_info "Step 3: Verify orchestrator state created in database"

sleep 5

orch_state=$(psql_query "SELECT status FROM recon_orchestrator_states WHERE target_id = '$TEST_TARGET_ID';")
if [[ -n "$orch_state" ]]; then
  log_pass "Orchestrator state created (status: $orch_state)"
else
  log_fail "No orchestrator state found for target"
fi

log_info "Step 4: Wait for provider runs (subfinder/amass) and verify enumeration"

max_wait=180
elapsed=0
while (( elapsed < max_wait )); do
  provider_runs=$(psql_query "SELECT COUNT(*) FROM recon_orchestrator_provider_runs WHERE target_id = '$TEST_TARGET_ID';")
  if (( provider_runs > 0 )); then
    log_pass "Provider runs initiated (count: $provider_runs)"
    break
  fi
  sleep "$POLL_INTERVAL"
  ((elapsed += POLL_INTERVAL))
done

if (( provider_runs == 0 )); then
  log_fail "No provider runs found after ${max_wait}s"
fi

log_info "Step 5: Verify subdomains discovered and persisted"

elapsed=0
while (( elapsed < max_wait )); do
  subdomains=$(psql_query "SELECT COUNT(*) FROM stored_assets WHERE target_id = '$TEST_TARGET_ID' AND asset_kind = 'Subdomain';")
  if (( subdomains > 0 )); then
    log_pass "Subdomains discovered and persisted (count: $subdomains)"
    break
  fi
  sleep "$POLL_INTERVAL"
  ((elapsed += POLL_INTERVAL))
done

if (( subdomains == 0 )); then
  log_fail "No subdomains persisted after ${max_wait}s"
fi

log_info "Step 6: Verify spider seeds queued in http_request_queue"

elapsed=0
while (( elapsed < max_wait )); do
  queued=$(psql_query "SELECT COUNT(*) FROM http_request_queue WHERE target_id = '$TEST_TARGET_ID';")
  if (( queued > 0 )); then
    log_pass "Spider seeds queued (count: $queued)"
    break
  fi
  sleep "$POLL_INTERVAL"
  ((elapsed += POLL_INTERVAL))
done

if (( queued == 0 )); then
  log_fail "No spider seeds queued after ${max_wait}s"
fi

log_info "Step 7: Verify HTTP requests processed"

elapsed=0
while (( elapsed < max_wait )); do
  processed=$(psql_query "SELECT COUNT(*) FROM http_request_queue WHERE target_id = '$TEST_TARGET_ID' AND state = 'Completed';")
  if (( processed > 0 )); then
    log_pass "HTTP requests processed (count: $processed)"
    break
  fi
  sleep "$POLL_INTERVAL"
  ((elapsed += POLL_INTERVAL))
done

if (( processed == 0 )); then
  log_fail "No HTTP requests completed after ${max_wait}s"
fi

log_info "Step 8: Verify spider worker extracted links (new URLs discovered)"

elapsed=0
while (( elapsed < max_wait )); do
  urls=$(psql_query "SELECT COUNT(*) FROM stored_assets WHERE target_id = '$TEST_TARGET_ID' AND asset_kind = 'Url';")
  if (( urls > 0 )); then
    log_pass "Spider extracted links - URLs discovered (count: $urls)"
    break
  fi
  sleep "$POLL_INTERVAL"
  ((elapsed += POLL_INTERVAL))
done

if (( urls == 0 )); then
  log_fail "No URLs discovered by spider after ${max_wait}s"
fi

log_info "Step 9: Verify orchestrator snapshot shows progress"

snapshot_response="$tmp_dir/snapshot.json"
if request "$BASE_URL/api/recon-agent/targets/$TEST_TARGET_ID" > "$snapshot_response"; then
  if grep -q '"status"' "$snapshot_response"; then
    subdomain_count=$(grep -o '"subdomainCount":[0-9]*' "$snapshot_response" | cut -d: -f2)
    url_count=$(grep -o '"urlCount":[0-9]*' "$snapshot_response" | cut -d: -f2)
    log_pass "Orchestrator snapshot retrieved (subdomains: ${subdomain_count:-0}, URLs: ${url_count:-0})"
  else
    log_fail "Snapshot response missing status"
  fi
else
  log_fail "Failed to retrieve orchestrator snapshot"
fi

log_info "Step 10: Verify orchestrator state shows completion or progress"

final_state=$(psql_query "SELECT status FROM recon_orchestrator_states WHERE target_id = '$TEST_TARGET_ID';")
if [[ -n "$final_state ]]; then
  log_pass "Final orchestrator state: $final_state"
else
  log_fail "Could not retrieve final orchestrator state"
fi

echo ""
echo "=== Test Summary ==="
echo "Passed: $pass_count"
echo "Failed: $fail_count"

if (( fail_count > 0 )); then
  echo ""
  echo "=== Debug Info ==="
  echo "Orchestrator state:"
  psql_query "SELECT id, target_id, status, provider_counts, subdomain_counts, created_at, updated_at FROM recon_orchestrator_states WHERE target_id = '$TEST_TARGET_ID';" | head -20

  echo ""
  echo "Provider runs:"
  psql_query "SELECT id, provider_name, status, created_at FROM recon_orchestrator_provider_runs WHERE target_id = '$TEST_TARGET_ID';" | head -20

  echo ""
  echo "Stored assets (first 10):"
  psql_query "SELECT id, asset_kind, asset_value FROM stored_assets WHERE target_id = '$TEST_TARGET_ID' LIMIT 10;"

  echo ""
  echo "HTTP queue (first 10):"
  psql_query "SELECT id, url, state FROM http_request_queue WHERE target_id = '$TEST_TARGET_ID' LIMIT 10;"
fi

exit $((fail_count > 0 ? 1 : 0))