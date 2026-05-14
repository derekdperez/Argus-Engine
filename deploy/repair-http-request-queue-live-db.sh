#!/usr/bin/env bash
set -euo pipefail

# Repairs live databases where http_request_queue has NOT NULL retry columns
# but the legacy startup backfill inserts rows without explicitly setting them.
#
# Run from the repository root:
#   ./deploy/repair-http-request-queue-live-db.sh
#
# Optional overrides:
#   COMPOSE_FILE=deploy/docker-compose.yml
#   POSTGRES_SERVICE=postgres
#   POSTGRES_USER=argus
#   POSTGRES_DB=argus_engine
#   COMMAND_CENTER_SERVICE=command-center

COMPOSE_FILE="${COMPOSE_FILE:-deploy/docker-compose.yml}"
POSTGRES_SERVICE="${POSTGRES_SERVICE:-postgres}"
POSTGRES_USER="${POSTGRES_USER:-argus}"
POSTGRES_DB="${POSTGRES_DB:-argus_engine}"
COMMAND_CENTER_SERVICE="${COMMAND_CENTER_SERVICE:-command-center}"

echo "Stopping ${COMMAND_CENTER_SERVICE} so it does not retry the failing bootstrap while the DB is repaired..."
sudo docker compose -f "$COMPOSE_FILE" stop "$COMMAND_CENTER_SERVICE" >/dev/null

echo "Applying http_request_queue defaults and fixing existing NULL retry values..."
sudo docker compose -f "$COMPOSE_FILE" exec -T "$POSTGRES_SERVICE" \
  psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" <<'SQL'
DO $repair$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.tables
        WHERE table_schema = 'public'
          AND table_name = 'http_request_queue'
    ) THEN
        RAISE NOTICE 'public.http_request_queue does not exist yet; nothing to repair.';
        RETURN;
    END IF;

    ALTER TABLE public.http_request_queue
        ALTER COLUMN attempt_count SET DEFAULT 0,
        ALTER COLUMN max_attempts SET DEFAULT 3,
        ALTER COLUMN redirect_count SET DEFAULT 0,
        ALTER COLUMN response_body_truncated SET DEFAULT false;

    UPDATE public.http_request_queue
    SET attempt_count = 0
    WHERE attempt_count IS NULL;

    UPDATE public.http_request_queue
    SET max_attempts = 3
    WHERE max_attempts IS NULL;

    UPDATE public.http_request_queue
    SET redirect_count = 0
    WHERE redirect_count IS NULL;

    UPDATE public.http_request_queue
    SET response_body_truncated = false
    WHERE response_body_truncated IS NULL;
END
$repair$;

SELECT
    column_name,
    column_default,
    is_nullable
FROM information_schema.columns
WHERE table_schema = 'public'
  AND table_name = 'http_request_queue'
  AND column_name IN (
      'attempt_count',
      'max_attempts',
      'redirect_count',
      'response_body_truncated'
  )
