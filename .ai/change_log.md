# AI Change Log

## 2026-05-01

- Fixed CommandCenter `blazor.web.js` parse failure:
  - Removed deploy-time manual copy/download of `_framework/blazor.web.js` from the Docker image build and hot-swap publish path.
  - Updated `App.razor` to resolve `_framework/blazor.web.js` through `@Assets[...]`.
  - Why: failed fallback downloads can publish `404: Not Found` as JavaScript, causing `Unexpected token ':'` in the browser console.
  - Validation: `dotnet publish src/NightmareV2.CommandCenter/NightmareV2.CommandCenter.csproj -c Release -o deploy/.tmp-check-publish/command-center /p:UseAppHost=false` succeeded; production-mode local probe of `/_framework/blazor.web.js` returned `200 text/javascript` with a JavaScript payload; `bash -n fix_blazor.sh` and `bash -n deploy/lib-nightmare-compose.sh` succeeded; `git diff --check` passed.

- Cleaned Command Center publish warnings:
  - Resolved the remaining technology scanner and worker activity count type warnings so Command Center release build/publish completes with zero warnings.
  - Validation: `dotnet build src/NightmareV2.CommandCenter/NightmareV2.CommandCenter.csproj -c Release` and `dotnet publish src/NightmareV2.CommandCenter/NightmareV2.CommandCenter.csproj -c Release -o .tmp/publish-command-center-warnings /p:UseAppHost=false` both succeeded with `0 Warning(s)`.

- Fixed Command Center ECS worker scale-up when services are missing:
  - Manual worker scaling now creates the ECS service from the latest active worker task definition when the service does not already exist and the requested desired count is greater than zero.
  - Creation uses the existing deployment environment conventions: `ECS_TASK_FAMILY_WORKER_*`, `ECS_SUBNETS`, `ECS_SECURITY_GROUPS`, `ECS_ASSIGN_PUBLIC_IP`, `ECS_LAUNCH_TYPE`, and `ECS_ENABLE_EXECUTE_COMMAND`.
  - Why: `UpdateService` cannot create a missing ECS service, so the app could save desired counts without actually materializing worker services.
- Validation:
  - `dotnet build src/NightmareV2.CommandCenter/NightmareV2.CommandCenter.csproj -c Release` succeeded.
  - Live ECS creation was not run from this local Windows workspace.

- Fixed technology-identification worker Docker publish collision:
  - Legacy `TechIdentificationData` JSON files now publish under `Resources/TechnologyDetection/TechIdentificationData` instead of being flattened into `Resources/TechnologyDetection/technologies`.
  - `TechnologyCatalogLoader` now merges legacy data first and current `technologies` data second, preserving both catalogs while allowing current definitions to override by technology name.
  - Why: Docker publish failed with `NETSDK1152` because both catalogs contained `_.json` at the same relative output path.
- Added target max-depth bulk update workflow:
  - New `PUT /api/targets/max-depth` updates `GlobalMaxDepth` for all targets or an explicit target-id list without changing root domains.
  - `/targets` can apply a depth to all loaded targets or the current filtered target set.
  - `/ops` can apply a depth to selected targets or all targets from the existing targets grid.
  - Fixed the `/targets` bulk import form action to post to the existing multipart `/api/targets/bulk` endpoint.
  - Why: make target crawl-depth tuning quick for one target, selected batches, filtered batches, or the whole corpus.
- Added Admin cloud usage tracking:
  - New `cloud_resource_usage_samples` persistence tracks sampled ECS worker service running counts and current EC2 host uptime metadata.
  - Added `/api/admin/usage` and `/admin` with cumulative ECS worker hours, 2200-hour monthly allowance usage, cumulative EC2 server hours, and HTTP queue traffic estimates.
  - Added `deploy/aws/record-cloud-usage-sample.sh` and wired deploy/autoscaler flows to record cloud usage samples.
  - Updated AWS deploy docs/env example to document usage sampling and the need for a regular autoscaler cadence.
  - Why: give operators visibility into free-tier ECS worker-hour usage and rough application bandwidth.
- Added ECS worker deployment and scaling helpers:
  - `deploy/aws/deploy-ecs-services.sh` registers task definitions from ECR images and creates/updates ECS services.
  - `deploy/aws/autoscale-ecs-workers.sh` scales spider, enum, port scan, high-value, and tech-id workers from Command Center queue metrics.
  - `deploy/aws/destroy-ecs-services.sh` explicitly deletes worker or all Nightmare ECS services with confirmation env vars.
  - `deploy/deploy.sh --ecs-workers` now runs the EC2 self-hosted core stack and deploys workers to ECS.
  - `deploy/aws/bootstrap-ecs-from-ec2.sh` derives EC2/VPC settings, creates baseline ECS/IAM/ECR/log/security-group resources, and generates ECS env files for workers.
  - Tightened ECS rerun idempotency: deploy uses immutable source-stamp image tags by default, reuses matching task-definition revisions, and skips ECS service updates when the service already matches desired state.
  - Added `deploy/aws/replace-ecs-worker-tasks.sh` and wired `deploy.sh --ecs-workers` to scale ECS workers to zero before recreating them on updated task definitions/images.
  - Updated AWS env examples, service env guidance, README, scaling guide, and gitignore for non-committed live AWS config.
  - Why: support ECS worker lifecycle management without embedding AWS orchestration into app runtime code.
- Validation:
  - `dotnet test NightmareV2.slnx` passed (18/18).
  - Bash/AWS live validation not run because this Windows environment lacks a usable Bash runtime and AWS CLI/config.

## 2026-04-30

- Added Command Center Events live trace:
  - API: `GET /api/events/live`
  - UI page: `/events`
  - Shows publish rows newest-first, producer, compact payload data, consuming workers, consume latency, and immediate follow-up publishes linked by event causation.
  - Why: provide fast end-to-end visibility into event flow and worker reactions.
- Updated Command Center navigation to include Events.
- Wrapped existing `NightmareDataGrid` column content in explicit `ChildContent` blocks where named toolbar/config templates are used.
  - Why: keep existing grid pages compatible with the shared grid component's named child content contract.
- Validation:
  - `dotnet build src/NightmareV2.CommandCenter/NightmareV2.CommandCenter.csproj -c Release` succeeded.
  - `GET http://localhost:5263/events` returned `200 OK` with the app running in Production mode, plain HTTP, and startup DB initialization skipped.

- Tightened Ops grid data boundaries:
  - `/api/assets` now returns only confirmed/verified assets for the top Ops asset grid.
  - `/api/http-request-queue` now returns queued request rows by default and can include failed rows with `includeFailed=true`.
  - Added a `Show failed` checkbox to the Ops request queue toolbar.
  - Why: keep verified assets separate from pending queue work while preserving access to failed request diagnostics.
- Validation:
  - `dotnet build src/NightmareV2.CommandCenter/NightmareV2.CommandCenter.csproj -c Release` succeeded.

- Serialized Docker BuildKit NuGet package cache access:
  - Added `sharing=locked` to the shared `nightmare-nuget` cache mounts in web, worker, and enum worker Dockerfiles.
  - Why: prevent parallel compose builds from corrupting `/root/.nuget/packages` during concurrent `dotnet restore`/`publish` stages.
- Validation:
  - `dotnet build src/NightmareV2.CommandCenter/NightmareV2.CommandCenter.csproj -c Release` succeeded.
  - `dotnet build src/NightmareV2.Gatekeeper/NightmareV2.Gatekeeper.csproj -c Release` succeeded.
  - Docker compose build could not be run locally because Docker Desktop was not running (`dockerDesktopLinuxEngine` pipe missing).

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

- Stabilized startup/runtime health behavior in compose:
  - Disabled worker startup DB bootstrap (`Nightmare__SkipStartupDatabase=true`) for gatekeeper + all worker services.
  - Set `Spider__Http__AllowInsecureSsl=false` for non-development startup validation compliance.
  - Why: prevent concurrent schema-lock timeout crashes and spider options-validation startup failure.
- Updated docker status classifier:
  - Added explicit `filestore-db-init` component and treats one-shot `Exited (0)` as healthy.
  - Why: avoid false red status for expected init-job completion.

- Refactored subdomain enumeration to provider-job architecture:
  - Added `SubdomainEnumerationRequested` event contract and queue-driven flow from `TargetCreatedConsumer`.
  - Added provider abstraction (`ISubdomainEnumerationProvider`) with independent `SubfinderEnumerationProvider` and `AmassEnumerationProvider`.
  - Added `SubdomainEnumerationRequestedConsumer` that executes only requested provider, normalizes/scope-filters/dedupes, and emits `AssetDiscovered` with provider provenance.
  - Added `ToolProcessRunner` and wildcard DNS detection path for amass logging/filtering context.
  - Why: remove single-step/stub enumeration and allow independent parallel provider execution with failure isolation.
- Added bundled high-value subdomain wordlist to enum worker output artifacts and updated enum configuration defaults under `SubdomainEnumeration`.
  - Why: support aggressive amass brute-force mode consistently across deploy/runtime.
- Added enum-focused tests (`tests/NightmareV2.Workers.Enum.Tests`) covering queueing defaults, provider selection isolation, normalization/parsing, max-per-job enforcement, and wildcard detection baseline behavior.
  - Why: lock in behavior for the new provider-job architecture.
- Fixed `OpsSnapshotBuilder` LINQ expression regression (expression-tree pattern matching) after enumeration worker attribution changes.
  - Why: restore full solution compile after ops attribution update.
- Validation:
  - `dotnet build src/NightmareV2.Workers.Enum/NightmareV2.Workers.Enum.csproj -c Release` succeeded.
  - `dotnet test tests/NightmareV2.Workers.Enum.Tests/NightmareV2.Workers.Enum.Tests.csproj -c Release` passed (10/10).
  - `dotnet build NightmareV2.slnx -c Release` succeeded.

- Fixed CommandCenter publish blockers found during EC2 deployment:
  - Removed stale local `OpsRadzen.razor.cs` from the workspace; it duplicated Razor-generated `Http` injection and `OnInitializedAsync`.
  - Added the missing CommandCenter bus journal row DTO and corrected `AssetKind` / `UrlFetchSnapshot` namespace aliases.
  - Why: restore `dotnet publish` for `NightmareV2.CommandCenter` without relying on older contract DTO names.
- Validation: `dotnet publish src/NightmareV2.CommandCenter/NightmareV2.CommandCenter.csproj -c Release -o ./.tmp/publish-command-center /p:UseAppHost=false` succeeded.

- Added manual EC2 worker machine scaling:
  - Persisted EC2 worker machine state and per-machine worker counts, exposed through `/api/ec2-workers/machines`.
  - Added Operations page `EC2 Workers` grid and controls to add up to two EC2 machines, select a machine, set spider/enum/port/high-value/technology-id worker counts, and remove machines.
  - Added EC2 worker compose/apply scripts and AWS deployment configuration notes for SSM-driven remote scaling.
  - Why: provide EC2 worker capacity independent of ECS worker services, with manual operator control from Command Center.
- Validation:
  - `dotnet build src/NightmareV2.CommandCenter/NightmareV2.CommandCenter.csproj -c Release` succeeded.
  - `dotnet test src/tests/NightmareV2.CommandCenter.Tests/NightmareV2.CommandCenter.Tests.csproj -c Release --no-restore` exited 0.
  - `git diff --check` passed with Git CRLF warnings only.
  - `C:\Program Files\Git\bin\bash.exe -n deploy/apply-ec2-worker-scale.sh` succeeded.

- Fixed Postgres connection exhaustion risk:
  - Centralized default Npgsql pool caps in infrastructure (`Nightmare:Postgres:MaxPoolSize` default 8, `Nightmare:FileStore:MaxPoolSize` default 4) without overriding explicit connection-string pool settings.
  - Raised local compose Postgres `max_connections` default to 300 and exposed matching deploy/env knobs.
  - Propagated pool cap settings into ECS service env generation/examples.
  - Why: prevent the default 10 spider + 10 enum worker compose stack, ECS workers, and EC2 workers from exhausting Postgres clients through uncapped per-process pools.
- Validation:
  - `dotnet build src/NightmareV2.CommandCenter/NightmareV2.CommandCenter.csproj -c Release` succeeded.
  - `dotnet test src/tests/NightmareV2.Infrastructure.Tests/NightmareV2.Infrastructure.Tests.csproj -c Release --no-restore` exited 0.
  - `docker compose -f deploy/docker-compose.yml config --quiet` succeeded.
  - `git diff --check` passed with Git CRLF warnings only.

- Condensed Operations target controls and corrected target rollups:
  - Reworked the `/ops` Targets control panel into a compact header/control row with target totals, confirmed subdomain/URL totals, add-target input, filter/search/export, and max-depth controls.
  - Added confirmed URL count to `TargetSummary` and the target grid/export.
  - Changed target rollups so Subdomains, URLs, and Asset Count are confirmed-only; Queued Requests now comes only from the HTTP request queue.
  - Why: match the compact Operations target layout and avoid mixing queued/unconfirmed asset counts into target grid metrics.
- Validation:
  - `dotnet build src/NightmareV2.CommandCenter/NightmareV2.CommandCenter.csproj -c Release` succeeded.
  - `dotnet test src/tests/NightmareV2.CommandCenter.Tests/NightmareV2.CommandCenter.Tests.csproj -c Release --no-restore` exited 0.
  - `git diff --check` passed with Git CRLF warnings only.

- Added Operations worker scale controls and ECS manual desired-count path:
  - Worker grid shows scalable ECS services with `-1`, textbox `Set`, and `+1` controls.
  - Manual desired counts are persisted in `worker_scale_targets`; Command Center can update ECS immediately via AWS ECS SDK when AWS region/credentials are available.
  - `deploy/aws/autoscale-ecs-workers.sh` reads `/api/workers/scale-overrides` and honors manual counts before queue-driven scaling.
  - Why: let operators add/remove spider, enum, port scan, high-value, and technology-id workers directly from Operations without losing settings to the autoscaler.
- Validation:
  - `dotnet build src/NightmareV2.CommandCenter/NightmareV2.CommandCenter.csproj` succeeded.
  - `dotnet test NightmareV2.slnx` passed (18/18).
  - `git diff --check` passed with Git CRLF warnings only.
  - `bash -n deploy/aws/autoscale-ecs-workers.sh` could not run on this Windows host because `/bin/bash` is unavailable.

- Promoted the Radzen operations workspace to the default operations surface:
  - `/` and `/ops` now route to the full operations workspace; the old basic page moved to `/ops-basic`, and `/ops-radzen` remains as an alternate direct link.
  - Updated operations summary cards, target rollup columns, fixed UTC-5 display formatting, compact Radzen density, and button-style nav links.
  - Added target rollups for subdomains, confirmed assets, queued HTTP requests, and last activity time.
  - Added fallbacks so worker controls render seeded worker keys and RabbitMQ management URL can be inferred from `RabbitMq:Host`.
- Validation: `dotnet publish src/NightmareV2.CommandCenter/NightmareV2.CommandCenter.csproj -c Release -o ./.tmp/publish-command-center /p:UseAppHost=false` succeeded.
