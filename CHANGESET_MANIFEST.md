# Changeset Manifest — Argus Engine final original-checklist overlay

This zip is path-preserved for extraction at the project root.

## Deployment version

- .NET/package version: `2.3.0`
- assembly/file version: `2.3.0.0`
- Docker/Compose default component version: `2.3.0`

## Included categories

- Compatibility config and startup changes
- Command Center split files
- Asset admission audit entity, writer, API, UI, and source-level tests
- HTTP artifact store, queue schema additions, worker integrations, and backfill endpoint
- Data retention/archive/partition services and admin endpoints
- OpenTelemetry metrics/tracing and worker wiring
- Outbox dispatcher observability instrumentation
- Versioning docs, scripts, and tests
- Repo-wide Argus rename migration script

## Important

A zip extraction cannot remove or rename existing directories. After unzipping, run:

```bash
python scripts/apply-original-checklist-refactor.py --apply
```

to perform the destructive `NightmareV2.*` -> `ArgusEngine.*` solution/project/namespace/type rename. The script preserves existing database names and table names.
