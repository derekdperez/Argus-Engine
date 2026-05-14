-- Command Center performance indexes
-- Run manually against the Argus PostgreSQL database after deployment.
--
-- IMPORTANT:
--   * CREATE INDEX CONCURRENTLY cannot run inside a transaction block.
--   * Table/column names below match the current snake_case naming used by the service SQL.
--     If your database was created with quoted PascalCase EF names, adapt the identifiers first.
--   * Run during a low-traffic window and verify with EXPLAIN ANALYZE before/after.

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_stored_assets_status_discovered_desc
    ON stored_assets (lifecycle_status, discovered_at_utc DESC);

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_stored_assets_target_kind_status_discovered_desc
    ON stored_assets (target_id, kind, lifecycle_status, discovered_at_utc DESC);

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_stored_assets_target_seen_desc
    ON stored_assets (target_id, last_seen_at_utc DESC);

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_http_request_queue_state_created_desc
    ON http_request_queue (state, created_at_utc DESC);

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_http_request_queue_target_state_created_desc
    ON http_request_queue (target_id, state, created_at_utc DESC);

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_high_value_findings_status_discovered_desc
    ON high_value_findings (investigation_status, discovered_at_utc DESC);

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_high_value_findings_asset_discovered_desc
    ON high_value_findings (asset_id, discovered_at_utc DESC);

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_technology_observations_target_last_seen_desc
    ON technology_observations (target_id, last_seen_utc DESC);

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_technology_observations_name_last_seen_desc
    ON technology_observations (technology_name, last_seen_utc DESC);

-- Optional search acceleration for PostgreSQL if pg_trgm is available.
-- CREATE EXTENSION IF NOT EXISTS pg_trgm;
-- CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_stored_assets_canonical_key_trgm
--     ON stored_assets USING gin (canonical_key gin_trgm_ops);
-- CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_stored_assets_raw_value_trgm
--     ON stored_assets USING gin (raw_value gin_trgm_ops);
-- CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_http_request_queue_request_url_trgm
--     ON http_request_queue USING gin (request_url gin_trgm_ops);
