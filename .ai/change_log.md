# AI Change Log

## 2026-04-29

- Standardized event envelope fields across contract events (`EventId`, `CausationId`, `SchemaVersion`, `Producer`; `OccurredAtUtc` exposed consistently).
  - Why: establish traceability and idempotency foundation for reliability redesign.
- Updated all known event emitters to populate envelope metadata and causation chains.
  - Why: avoid empty metadata and enable immediate ops debugging value.
- Hardened diagnostics/maintenance endpoint behavior so enabled mode requires configured API keys.
  - Why: prevent accidental exposure in production-like environments.
- Added reliability baseline endpoints:
  - `/api/ops/reliability-slo`
  - `/api/workers/capabilities`
  - `/api/workers/health`
  - Why: provide measurable reliability and worker-operational signals before deeper refactors.
- Removed silent event-loss behavior in `EfAssetPersistence` by adding publish retries and terminal failure.
  - Why: eliminate unobservable publish drops for scanner-trigger events.
- Validation: `dotnet build NightmareV2.slnx -c Release` succeeded.

- Added transactional messaging foundations:
  - `outbox_messages` + `inbox_messages` entities and schema patches.
  - `IEventOutbox` + `EfEventOutbox` + `OutboxDispatcherWorker` retry/poison flow.
  - `IInboxDeduplicator` + `EfInboxDeduplicator` used in key consumers.
  - Why: prevent silent publish loss and support idempotent consumption.
- Replaced direct bus-journal hot-path writes with bounded async batch pipeline (`BusJournalBuffer` + observers).
  - Why: remove per-event DB write contention and preserve observability under load.
- Implemented pipeline upgrades:
  - adaptive spider concurrency controller and queue state-machine enforcement.
  - real enumeration + port scan services behind interfaces.
  - removed legacy `SpiderAssetDiscoveredConsumer` duplicate path.
  - Why: single execution path, higher throughput, and less divergence risk.
- Refactored CommandCenter route registration into endpoint modules:
  - `TargetEndpoints`, `HttpRequestQueueEndpoints`, `BusJournalEndpoints`, `WorkerOpsEndpoints`.
  - Why: reduce `Program.cs` sprawl and make bounded-context ownership explicit.
- Added reliability baseline endpoint `/api/ops/reliability-baseline` with configurable error budget thresholds and rollback recommendation signal.
  - Why: capture operational baseline and define explicit SLO breach triggers.
- Validation: `dotnet build NightmareV2.slnx -c Release` succeeded after all changes.

- Added Docker runtime status feature for operations:
  - API: `GET /api/ops/docker-status`
  - UI page: `/status`
  - Includes container/image rollups, per-component health (green/yellow/red/gray), and last 300 log lines per container.
  - Why: provide immediate runtime visibility for compose/ECS troubleshooting from Command Center.
- Added deployment wiring for Docker runtime introspection:
  - `deploy/Dockerfile.web` installs Docker CLI (`docker.io`).
  - `deploy/docker-compose.yml` mounts `/var/run/docker.sock` read-only into `command-center`.
  - Why: make `/api/ops/docker-status` functional in containerized runtime.
- Validation: `dotnet build src/NightmareV2.CommandCenter/NightmareV2.CommandCenter.csproj -c Release` succeeded.
