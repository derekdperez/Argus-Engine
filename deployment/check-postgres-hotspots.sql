-- Argus Engine Postgres hotspot check.
-- Run with: docker compose -f deployment/docker-compose.yml exec -T postgres psql -U argus -d argus_engine < deployment/check-postgres-hotspots.sql

SELECT count(*) AS active_connections
FROM pg_stat_activity;

SELECT
  datname,
  usename,
  state,
  count(*) AS connections
FROM pg_stat_activity
GROUP BY datname, usename, state
ORDER BY connections DESC;

SELECT
  relname AS table_name,
  pg_size_pretty(pg_total_relation_size(relid)) AS total_size,
  n_live_tup AS estimated_rows,
  n_dead_tup AS estimated_dead_rows,
  last_analyze,
  last_autoanalyze
FROM pg_stat_user_tables
WHERE relname IN ('bus_journal', 'stored_assets', 'worker_heartbeats', 'http_request_queue')
ORDER BY pg_total_relation_size(relid) DESC;

SELECT
  schemaname,
  tablename,
  indexname,
  pg_size_pretty(pg_relation_size(indexrelid)) AS index_size
FROM pg_stat_user_indexes
WHERE tablename IN ('bus_journal', 'stored_assets', 'worker_heartbeats')
ORDER BY pg_relation_size(indexrelid) DESC;
