# Argus log error fix overlay

This overlay is based on the current `main` branch layout of:

`https://github.com/derekdperez/argus-engine`

## What changed

### 1. Inbox duplicate handling no longer logs expected duplicate delivery as an EF error

File:

`src/ArgusEngine.Infrastructure/Messaging/EfInboxDeduplicator.cs`

The inbox deduplicator now inserts directly with:

```sql
ON CONFLICT (event_id, consumer) DO NOTHING
```

This keeps at-least-once message redelivery idempotent without throwing and logging a noisy `23505 duplicate key value violates unique constraint "IX_inbox_messages_event_id_consumer"` exception.

### 2. Operations storage metrics now cast JSON/JSONB columns to text before `octet_length`

File:

`src/ArgusEngine.CommandCenter.Operations.Api/OpsStorageMetricsQuery.cs`

The storage metrics query now uses casts such as:

```sql
octet_length(COALESCE(type_details_json::text, ''))
octet_length(COALESCE(redirect_chain_json::text, ''))
octet_length(COALESCE(payload_json::text, ''))
```

This prevents PostgreSQL from trying to coerce `''` into JSON/JSONB while computing dashboard storage-size totals.

## Apply

From the repository root:

```bash
unzip argus-log-error-fix.zip -d .
dotnet build ArgusEngine.slnx
docker compose -f deploy/docker-compose.yml build command-center-operations-api worker-highvalue
docker compose -f deploy/docker-compose.yml up -d --force-recreate command-center-operations-api worker-highvalue
```

If you use the deploy console, rebuild/recreate the same two services after unzipping.
