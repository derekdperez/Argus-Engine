#!/usr/bin/env bash
set -euo pipefail

COMPOSE_FILE="${COMPOSE_FILE:-deploy/docker-compose.yml}"
POSTGRES_SERVICE="${ARGUS_POSTGRES_SERVICE:-postgres}"
POSTGRES_USER="${POSTGRES_USER:-argus}"
POSTGRES_DB="${POSTGRES_DB:-argus_engine}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "Applying Postgres performance indexes to ${POSTGRES_DB} via service ${POSTGRES_SERVICE}..."
docker compose -f "${COMPOSE_FILE}" exec -T "${POSTGRES_SERVICE}" \
  psql -v ON_ERROR_STOP=1 -U "${POSTGRES_USER}" -d "${POSTGRES_DB}" \
  < "${SCRIPT_DIR}/postgres-performance-patch.sql"

if [[ -n "${ARGUS_PURGE_BUS_JOURNAL_DAYS:-}" ]]; then
  echo "Purging bus_journal rows older than ${ARGUS_PURGE_BUS_JOURNAL_DAYS} days..."
  docker compose -f "${COMPOSE_FILE}" exec -T "${POSTGRES_SERVICE}" \
    psql -v ON_ERROR_STOP=1 -U "${POSTGRES_USER}" -d "${POSTGRES_DB}" \
    -v retention_days="${ARGUS_PURGE_BUS_JOURNAL_DAYS}" <<'SQL'
DELETE FROM bus_journal
WHERE occurred_at_utc < now() - (:'retention_days' || ' days')::interval;

VACUUM (ANALYZE) bus_journal;
SQL
fi

echo "Done. Restarting command-center so it uses fresh DB plans and recovers cleanly..."
docker compose -f "${COMPOSE_FILE}" restart command-center

echo
echo "Current connection and table-size snapshot:"
docker compose -f "${COMPOSE_FILE}" exec -T "${POSTGRES_SERVICE}" \
  psql -U "${POSTGRES_USER}" -d "${POSTGRES_DB}" <<'SQL'
SELECT count(*) AS active_connections FROM pg_stat_activity;

SELECT
  relname AS table_name,
  pg_size_pretty(pg_total_relation_size(relid)) AS total_size,
  n_live_tup AS estimated_rows
FROM pg_stat_user_tables
WHERE relname IN ('bus_journal', 'stored_assets', 'worker_heartbeats', 'http_request_queue')
ORDER BY pg_total_relation_size(relid) DESC;
SQL
