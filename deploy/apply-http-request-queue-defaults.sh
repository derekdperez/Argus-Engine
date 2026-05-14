#!/usr/bin/env bash
set -euo pipefail

# Run from the repository root.
# Optional overrides:
#   COMPOSE_FILE=deploy/docker-compose.yml
#   ARGUS_POSTGRES_SERVICE=postgres

COMPOSE_FILE="${COMPOSE_FILE:-deploy/docker-compose.yml}"
POSTGRES_SERVICE="${ARGUS_POSTGRES_SERVICE:-postgres}"
SQL_FILE="deploy/http-request-queue-defaults.sql"

if [[ ! -f "$SQL_FILE" ]]; then
  echo "Missing $SQL_FILE. Run this script from the repository root." >&2
  exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "docker is required but was not found in PATH." >&2
  exit 1
fi

echo "Applying http_request_queue retry-column defaults..."
sudo docker compose -f "$COMPOSE_FILE" exec -T "$POSTGRES_SERVICE" sh -lc \
  'psql -v ON_ERROR_STOP=1 -U "${POSTGRES_USER:-argus}" -d "${POSTGRES_DB:-argus_engine}"' \
  < "$SQL_FILE"

