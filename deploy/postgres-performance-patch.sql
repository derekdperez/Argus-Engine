-- Argus Engine Postgres performance patch for Command Center/Ops dashboard timeouts.
-- Run with psql against the argus_engine database.
--
-- Notes:
--   * CREATE INDEX CONCURRENTLY cannot run inside a transaction block.
--   * This script is safe to re-run; index creation uses IF NOT EXISTS.
--   * On very large bus_journal/stored_assets tables, index creation can take a while.

\echo 'Applying Argus Engine Postgres performance patch...'

SET statement_timeout = '0';
SET lock_timeout = '5s';

CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Supports Ops dashboard aggregate queries such as:
--   WHERE direction = 'Consume' AND occurred_at_utc >= ...
--   WHERE direction = 'Publish' AND occurred_at_utc >= ...
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_argus_bus_journal_direction_occurred_at
    ON bus_journal (direction, occurred_at_utc DESC);

-- Supports WorkerActivityQuery:
--   WHERE direction = 'Consume'
--     AND consumer_type IS NOT NULL
--     AND occurred_at_utc >= ...
--   ORDER BY id DESC
--   LIMIT 15000
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_argus_bus_journal_consume_recent_id
    ON bus_journal (id DESC, occurred_at_utc DESC)
    WHERE direction = 'Consume' AND consumer_type IS NOT NULL;

-- Supports per-worker dashboard queries that use string contains/ILIKE over consumer_type.
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_argus_bus_journal_consumer_type_trgm
    ON bus_journal USING gin (consumer_type gin_trgm_ops)
    WHERE direction = 'Consume' AND consumer_type IS NOT NULL;

-- Supports asset summary counts and latest-asset lookups.
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_argus_stored_assets_discovered_at
    ON stored_assets ("DiscoveredAtUtc" DESC);

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_argus_stored_assets_lifecycle_kind
    ON stored_assets ("LifecycleStatus", "Kind");

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_argus_stored_assets_kind_discovered_at
    ON stored_assets ("Kind", "DiscoveredAtUtc" DESC);

-- Supports attribution lookups by DiscoveredBy/discovered_by.
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_argus_stored_assets_discovered_by_trgm
    ON stored_assets USING gin (discovered_by gin_trgm_ops);

-- Helpful for heartbeat freshness checks and future dashboard filtering.
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_argus_worker_heartbeats_last_seen
    ON worker_heartbeats ("LastHeartbeatUtc" DESC);

ANALYZE bus_journal;
ANALYZE stored_assets;
ANALYZE worker_heartbeats;
ANALYZE http_request_queue;

\echo 'Argus Engine Postgres performance patch complete.'
