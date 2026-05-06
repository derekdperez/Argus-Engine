# Argus Engine web/Command Center DB timeout patch

This patch adds production-safe Postgres indexes for the Command Center Ops dashboard queries that are timing out over `bus_journal`, `stored_assets`, and `worker_heartbeats`.

## Apply

From the repository root:

```bash
chmod +x deploy/apply-postgres-performance-patch.sh
sudo ./deploy/apply-postgres-performance-patch.sh
```

Optional: purge old bus journal rows while applying the patch:

```bash
ARGUS_PURGE_BUS_JOURNAL_DAYS=7 sudo ./deploy/apply-postgres-performance-patch.sh
```

## Check status

```bash
sudo docker compose -f deploy/docker-compose.yml exec -T postgres \
  psql -U argus -d argus_engine < deploy/check-postgres-hotspots.sql
```

## Notes

`postgres-performance-patch.sql` uses `CREATE INDEX CONCURRENTLY`, so it does not run inside a transaction and can be re-run safely.
