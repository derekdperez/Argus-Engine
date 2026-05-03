# Argus Engine Refactor / Hardening Checklist

Source: original implementation plan supplied in chat.

## Deployment versioning

- [x] Central deployment version bumped to `2.3.0` / `2.3.0.0`.
- [x] All .NET projects inherit the same forced deployment version through `Directory.Build.targets`.
- [x] Docker/Compose defaults and verification scripts use `2.3.0`.
- [x] Added tests that fail if deployment versioning regresses.

## Phase 4 — Rename NightmareV2 internals to ArgusEngine

- [x] 4.1 Add compatibility configuration helper for `Argus:*` and `Nightmare:*` keys.
- [x] 4.1 Update startup/middleware/database initialization to use compatibility reads.
- [x] 4.1 Support `ARGUS_*` and `NIGHTMARE_*` startup database skip environment variables.
- [x] 4.2 Add idempotent repo migration script to rename solution and project directories to `ArgusEngine.*`.
- [x] 4.3 Add namespace migration from `NightmareV2.*` to `ArgusEngine.*`.
- [x] 4.4 Add branded type migration from `NightmareDbContext`, `NightmareRuntimeOptions`, `NightmareDbSeeder`, and `NightmareDbSchemaPatches` to Argus names while preserving database tables.
- [x] 4.5 Rename Docker image/compose/build-stamp values to Argus names with compatibility during transition.
- [x] 4.6 Update script coverage for `NIGHTMARE_`, `Nightmare`, `nightmare-v2`, `NightmareV2`, and `nightmare_v2`.
- [x] 4.7 Add docs/migration notes; public branding is Argus Engine with NightmareV2 only in migration notes.

## Phase 5 — Split CommandCenter/Program.cs

- [x] 5.1 Keep `Program.cs` composition-only and under 75 lines.
- [x] 5.2 Extract service registration into `Startup/CommandCenterServiceRegistration.cs`.
- [x] 5.3 Extract middleware pipeline into `Startup/CommandCenterMiddleware.cs`.
- [x] 5.4 Extract startup database initialization into `Startup/StartupDatabaseInitializer.cs`.
- [x] 5.5 Extract endpoint registration into `Endpoints/CommandCenterEndpointRegistration.cs`.
- [x] 5.6 Extract target management/root seed/summary services.
- [x] 5.7 Extract AWS/ECS and worker scaling services.
- [x] Add focused static tests for Program/endpoint split acceptance criteria.

## Phase 6 — Add auditable Gatekeeper decisions

- [x] 6.1 Add `AssetAdmissionDecision` domain entity.
- [x] 6.2 Add decision kind and reason-code constants.
- [x] 6.3 Add runtime schema initializer for `asset_admission_decisions`.
- [x] 6.4 Add `IAssetAdmissionDecisionWriter` and EF implementation.
- [x] 6.5 Instrument `GatekeeperOrchestrator` early returns, accepted path, and exception path.
- [x] 6.6 Add query API endpoints for admission decisions.
- [x] 6.7 Add Command Center `Asset Admission` UI page and navigation link.
- [x] Add tests covering required gatekeeper decision paths at source level.

## Phase 9 — Move HTTP response bodies out of queue rows

- [x] 9.1 Add artifact reference columns to `HttpRequestQueueItem` while retaining old columns.
- [x] 9.2 Add runtime schema initializer and indexes for HTTP artifact columns.
- [x] 9.3 Add `IHttpArtifactStore` and `IHttpArtifactReader`.
- [x] 9.4 Implement EF/file-store-backed HTTP artifact store with SHA-256, size, preview, and truncation metadata.
- [x] 9.5 Add blob metadata fields to `UrlFetchSnapshot`.
- [x] 9.6 Update HTTP queue response persistence to store artifacts outside queue rows.
- [x] 9.7 Update URL confirmation snapshot serialization to include blob references and omit full body text.
- [x] 9.8 Update high-value and technology-identification consumers to read response bodies from artifacts with max-byte limits.
- [x] 9.9 Add HTTP artifact backfill service and maintenance endpoint.
- [x] 9.10 Legacy inline columns intentionally remain until backfill verification; do not drop them in this release.

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
- [x] Add retention/partition source-level tests.

## Phase 13 — Add first-class observability with OpenTelemetry

- [x] 13.1 Add OpenTelemetry package references.
- [x] 13.2 Add `AddArgusObservability` extension.
- [x] 13.3 Add Argus meters.
- [x] 13.4 Add Argus tracing source.
- [x] 13.5 Add observable gauges for queue/outbox/findings/assets.
- [x] 13.6 Instrument `HttpRequestQueueWorker`.
- [x] 13.7 Instrument Gatekeeper decision writes and tracing points.
- [x] 13.8 Instrument Outbox dispatcher.
- [x] 13.9 Instrument high-value finding creation.
- [x] Wire observability into Command Center, Gatekeeper, and worker executables.

## Verification

- [ ] `dotnet build` was not run in this sandbox.
- [ ] `dotnet test` was not run in this sandbox.
- [ ] Docker Compose build was not run in this sandbox.
