#!/usr/bin/env bash
# Recreate the E2E databases on the compose host and restart the app stack.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
COMPOSE_FILE="${ARGUS_E2E_COMPOSE_FILE:-${ROOT}/deploy/docker-compose.yml}"
BASE_URL="${ARGUS_BASE_URL:-http://127.0.0.1:8080}"
SNAPSHOT_SQL="${ARGUS_E2E_DB_SNAPSHOT_SQL:-}"
MAX_WAIT_SECONDS="${ARGUS_E2E_MAX_WAIT_SECONDS:-180}"

compose() {
  docker compose -f "$COMPOSE_FILE" "$@"
}

wait_for_ready() {
  local started_at now
  started_at="$(date +%s)"
  while true; do
    if curl -fsS "${BASE_URL}/health/ready" >/dev/null 2>&1; then
      return 0
    fi

    now="$(date +%s)"
    if (( now - started_at > MAX_WAIT_SECONDS )); then
      echo "Command Center did not become ready at ${BASE_URL} within ${MAX_WAIT_SECONDS}s." >&2
      compose logs --tail=120 command-center >&2 || true
      return 1
    fi

    sleep 3
  done
}

echo "Resetting E2E databases for a fresh known state..."

compose up -d postgres redis rabbitmq

compose stop \
  command-center \
  gatekeeper \
  worker-spider \
  worker-enum \
  worker-portscan \
  worker-highvalue \
  worker-techid >/dev/null 2>&1 || true

compose exec -T postgres psql -U argus -d postgres -v ON_ERROR_STOP=1 <<'SQL'
SELECT pg_terminate_backend(pid)
FROM pg_stat_activity
WHERE datname IN ('argus_engine', 'argus_engine_files')
  AND pid <> pg_backend_pid();

DROP DATABASE IF EXISTS argus_engine;
DROP DATABASE IF EXISTS argus_engine_files;
CREATE DATABASE argus_engine OWNER argus;
CREATE DATABASE argus_engine_files OWNER argus;
SQL

if [[ -n "$SNAPSHOT_SQL" ]]; then
  if [[ ! -f "$SNAPSHOT_SQL" ]]; then
    echo "ARGUS_E2E_DB_SNAPSHOT_SQL points to a missing file: ${SNAPSHOT_SQL}" >&2
    exit 2
  fi

  echo "Restoring E2E database snapshot from ${SNAPSHOT_SQL}..."
  compose exec -T postgres psql -U argus -d argus_engine -v ON_ERROR_STOP=1 <"$SNAPSHOT_SQL"
else
  echo "No ARGUS_E2E_DB_SNAPSHOT_SQL supplied; Command Center startup will bootstrap an empty schema."
fi

compose up -d command-center
wait_for_ready
compose up -d gatekeeper worker-spider worker-enum worker-portscan worker-highvalue worker-techid

echo "E2E database reset complete."
