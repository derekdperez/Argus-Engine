# Architecture Notes

- Core pattern: Raw `AssetDiscovered` events are gatekept, canonicalized, deduped, persisted, then republished as Indexed events.
- HTTP probing architecture currently relies on a durable Postgres queue drained by `HttpRequestQueueWorker` in spider service.
- Event contracts now carry envelope metadata (`EventId`, `CorrelationId`, `CausationId`, `OccurredAtUtc`, `SchemaVersion`, `Producer`) for tracing and idempotency groundwork.
- Reliability gap reduced: `ScannableContentAvailable` publishing in `EfAssetPersistence` now retries and fails loudly after retry exhaustion instead of silent swallow.
- Ops baseline expanded with explicit reliability/worker introspection endpoints:
  - `/api/ops/reliability-slo`
  - `/api/workers/capabilities`
  - `/api/workers/health`
- Security hardening added for diagnostics and maintenance endpoints: enabled mode now requires explicit API key configuration.
- Eventing now uses durable outbox + dispatcher for write-then-publish paths, and inbox dedupe for consumers. This is the primary anti-loss and anti-duplicate boundary.
- Bus journal capture is now buffered/batched via channel worker; direct per-event EF insert path has been removed from observers.
- Spider architecture is now single-path through `HttpRequestQueueWorker`; legacy direct `AssetDiscovered` spider consumer path has been removed.
- CommandCenter API routing now has feature modules in `Endpoints/*` for targets, HTTP queue, bus journal, and worker ops/reliability.
- Startup DB bootstrap logic is centralized in `StartupDatabaseBootstrap` and reused across services.
- Command Center now exposes Docker runtime introspection (`/api/ops/docker-status`) and a UI status page (`/status`) backed by shelling out to Docker CLI (`docker ps`, `docker inspect`, `docker logs`).
- Compose deploy now mounts Docker socket read-only into Command Center and runtime image includes Docker CLI so status endpoint can query host daemon.
- Compose worker services now skip startup DB bootstrap; schema/bootstrap ownership effectively sits with Command Center startup path in current local-stack orchestration.
