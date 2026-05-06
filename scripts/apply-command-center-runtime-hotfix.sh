#!/usr/bin/env bash
set -euo pipefail

COMPOSE_FILE="${COMPOSE_FILE:-deploy/docker-compose.yml}"
POSTGRES_SERVICE="${POSTGRES_SERVICE:-postgres}"
POSTGRES_USER="${POSTGRES_USER:-argus}"
POSTGRES_DB="${POSTGRES_DB:-argus_engine}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SQL_FILE="${SCRIPT_DIR}/2026-05-06-command-center-runtime-hotfix.sql"

docker compose -f "${COMPOSE_FILE}" exec -T "${POSTGRES_SERVICE}" \
  psql -U "${POSTGRES_USER}" -d "${POSTGRES_DB}" -v ON_ERROR_STOP=1 < "${SQL_FILE}"
