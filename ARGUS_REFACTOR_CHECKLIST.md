# Argus Engine Refactor Checklist Implementation Status

Generated against the latest `main` branch structure that was inspectable from GitHub on 2026-05-02. This overlay intentionally keeps existing `NightmareV2.*` project paths because the repository has not yet completed the full project/namespace rename.

## Phase 4 — Rename NightmareV2 internals to ArgusEngine

- [x] 4.1 Add compatibility configuration helper for `Argus:*` and `Nightmare:*` keys.
- [x] 4.1 Update Command Center startup/middleware/database initialization to use compatibility reads.
- [x] 4.1 Support `ARGUS_*` and `NIGHTMARE_*` startup database skip environment variables.
- [ ] 4.2 Rename solution and project directories to `ArgusEngine.*`.
- [ ] 4.3 Rename all namespaces to `ArgusEngine.*`.
- [ ] 4.4 Rename branded types such as `NightmareDbContext` to `ArgusDbContext`.
- [ ] 4.5 Rename Docker images, compose project, labels, and build stamp variables repo-wide.
- [ ] 4.6 Update all scripts repo-wide.
- [ ] 4.7 Update all public docs repo-wide.

## Phase 5 — Split CommandCenter/Program.cs

- [x] 5.1 Keep `Program.cs` composition-only in the latest repo.
- [x] 5.2 Extract service registration into `Startup/CommandCenterServiceRegistration.cs`.
- [x] 5.3 Extract middleware pipeline into `Startup/CommandCenterMiddleware.cs`.
- [x] 5.4 Extract startup database initialization into `Startup/StartupDatabaseInitializer.cs`.
- [x] 5.5 Extract endpoint registration into `Endpoints/CommandCenterEndpointRegistration.cs`.
- [x] 5.6 Extract target management/root seed/summary services in the latest repo.
- [x] 5.7 Extract AWS/ECS and worker scaling services in the latest repo.
- [ ] Add focused service and endpoint tests for the split.

## Phase 6 — Add auditable Gatekeeper decisions

- [x] 6.1 Add `AssetAdmissionDecision` domain entity.
- [x] 6.2 Add decision kind and reason-code constants.
- [x] 6.3 Add runtime schema initializer for `asset_admission_decisions`.
- [x] 6.4 Add `IAssetAdmissionDecisionWriter` and EF implementation.
- [x] 6.5 Instrument `GatekeeperOrchestrator` early returns, acceptances, and exception path.
- [x] 6.6 Add query API endpoints for admission decisions.
- [ ] 6.7 Add Command Center UI panel.
- [ ] Add tests for accepted, duplicate, out-of-scope, depth exceeded, disabled, persistence skipped, and exception paths.

## Phase 9 — Move HTTP response bodies out of queue rows

- [x] 9.1 Add artifact reference columns to `HttpRequestQueueItem` while retaining old columns.
- [x] 9.2 Add runtime schema initializer and indexes for HTTP artifact columns.
- [x] 9.3 Add `IHttpArtifactStore` and `IHttpArtifactReader`.
- [x] 9.4 Implement EF/file-store-backed HTTP artifact store with SHA-256, size, preview, and truncation metadata.
- [x] 9.5 Add blob metadata fields to `UrlFetchSnapshot`.
- [x] 9.6 Update `HttpRequestQueueWorker.SaveResponseAsync` and retry response persistence to store artifacts outside queue rows.
- [x] 9.7 Update URL confirmation snapshot serialization to include blob references and omit full body text.
- [x] 9.8 Update high-value and technology-identification consumers to read response bodies from artifacts with max-byte limits.
- [x] 9.9 Add HTTP artifact backfill service and maintenance endpoint.
- [ ] 9.10 Remove legacy inline columns in a later migration after backfill verification.

## Phase 10 — Partition/archive high-volume tables

- [x] 10.1 Add data retention options.
- [x] 10.2 Add retention worker and run-result state.
- [x] 10.3 Delete/archive rows in small batches.
- [x] 10.4 Add archive-before-delete tables for useful history.
- [x] 10.5 Add partial indexes for active HTTP queue and outbox operations.
- [x] 10.6 Add bus journal monthly partition creation support.
- [x] 10.7 Add partition maintenance service and hosted runner.
- [x] 10.8 Leave `http_request_queue` unpartitioned until artifacts/retention/indexing have been measured.
- [x] 10.9 Add admin API to view retention status, run retention now, and ensure partitions.
- [ ] Add retention/partition tests.

## Phase 13 — Add first-class observability with OpenTelemetry

- [x] 13.1 Add OpenTelemetry package references to Infrastructure.
- [x] 13.2 Add `AddArgusObservability` extension.
- [x] 13.3 Add Argus meters.
- [x] 13.4 Add Argus tracing source.
- [x] 13.5 Add observable gauges for queue/outbox/findings/assets.
- [x] 13.6 Instrument `HttpRequestQueueWorker`.
- [x] 13.7 Instrument gatekeeper through auditable decision writes and tracing-ready decision points.
- [ ] 13.8 Instrument outbox dispatcher.
- [x] 13.9 Instrument high-value finding creation counter.
- [ ] Wire observability into every worker executable and validate exporter configuration.

## Verification

- [ ] `dotnet build` was not run in this environment.
- [ ] `dotnet test` was not run in this environment.
- [ ] Docker Compose build was not run in this environment.
