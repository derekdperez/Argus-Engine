#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}"
DOMAIN="${DOMAIN:-qa-$(date +%s)-$RANDOM.example.com}"
CREATED_ID=""

cleanup() {
  if [[ -n "$CREATED_ID" ]]; then
    curl -fsS -X DELETE "$BASE_URL/api/targets/$CREATED_ID" >/dev/null || true
  fi
}
trap cleanup EXIT

echo "==> Checking Command Center health at $BASE_URL"
curl -fsS "$BASE_URL/health" >/dev/null

echo "==> Creating target $DOMAIN"
create_response="$(curl -fsS \
  -H 'Content-Type: application/json' \
  -d "{\"rootDomain\":\"$DOMAIN\",\"globalMaxDepth\":3}" \
  "$BASE_URL/api/targets")"

CREATED_ID="$(python -c 'import json,sys; print(json.load(sys.stdin)["id"])' <<<"$create_response")"
created_root="$(python -c 'import json,sys; print(json.load(sys.stdin)["rootDomain"])' <<<"$create_response")"
created_depth="$(python -c 'import json,sys; print(json.load(sys.stdin)["globalMaxDepth"])' <<<"$create_response")"

if [[ "$created_root" != "$DOMAIN" || "$created_depth" != "3" ]]; then
  echo "Unexpected create response: $create_response" >&2
  exit 1
fi

echo "==> Verifying target appears in list"
list_response="$(curl -fsS "$BASE_URL/api/targets")"
LIST_RESPONSE="$list_response" python - "$CREATED_ID" "$DOMAIN" <<'PY'
import json
import os
import sys

target_id = sys.argv[1]
domain = sys.argv[2]
rows = json.loads(os.environ["LIST_RESPONSE"])
if not any(row.get("id") == target_id and row.get("rootDomain") == domain for row in rows):
    raise SystemExit(f"created target {target_id} / {domain} was not returned by /api/targets")
PY

echo "==> Command Center target workflow passed for $DOMAIN"
