#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${ARGUS_COMMAND_CENTER_URL:-http://localhost:8080}"
TARGET_DOMAIN="${ARGUS_E2E_TARGET_DOMAIN:-e2e-$(date +%s).example.com}"
MAX_WAIT_SECONDS="${ARGUS_E2E_MAX_WAIT_SECONDS:-120}"

tmp_dir="$(mktemp -d)"
cleanup() {
  rm -rf "$tmp_dir"
}
trap cleanup EXIT

request() {
  curl -fsS "$@"
}

wait_for_command_center() {
  local started_at
  started_at="$(date +%s)"

  while true; do
    if request "$BASE_URL/api/status/summary" >/dev/null 2>&1 || request "$BASE_URL/api/targets" >/dev/null 2>&1; then
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

echo "Waiting for Command Center at $BASE_URL..."
wait_for_command_center

echo "Creating target $TARGET_DOMAIN..."
create_response="$tmp_dir/create-target.json"
request \
  -H 'Content-Type: application/json' \
  -d "{\"rootDomain\":\"$TARGET_DOMAIN\",\"globalMaxDepth\":2}" \
  "$BASE_URL/api/targets" > "$create_response"

echo "Verifying target list..."
targets_response="$tmp_dir/targets.json"
request "$BASE_URL/api/targets" > "$targets_response"

if ! grep -q "$TARGET_DOMAIN" "$targets_response"; then
  echo "Target $TARGET_DOMAIN was not returned by /api/targets." >&2
  cat "$targets_response" >&2
  exit 1
fi

echo "Verifying HTTP request queue seed work..."
queue_response="$tmp_dir/http-queue.json"
request "$BASE_URL/api/http-request-queue?take=100" > "$queue_response"

if ! grep -q "$TARGET_DOMAIN" "$queue_response"; then
  echo "Expected root HTTP queue seed work for $TARGET_DOMAIN, but it was not found." >&2
  cat "$queue_response" >&2
  exit 1
fi

echo "Verifying operational status snapshot..."
status_response="$tmp_dir/status.json"
request "$BASE_URL/api/status/summary" > "$status_response"

if ! grep -q '"status"' "$status_response"; then
  echo "Status snapshot did not contain a status field." >&2
  cat "$status_response" >&2
  exit 1
fi

echo "E2E gatekeeper pipeline smoke test passed for $TARGET_DOMAIN."
