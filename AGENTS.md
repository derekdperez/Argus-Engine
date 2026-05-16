# Argus Engine — Agent Guide

.NET 10.0 distributed reconnaissance engine: Blazor Command Center UI + microservice APIs + background workers, deployed via Docker Compose.

## Build & test

```bash
dotnet restore ArgusEngine.slnx
dotnet build ArgusEngine.slnx --configuration Release --no-restore
```

```bash
./test.sh all              # unit + integration tests (Release mode)
./test.sh unit             # UnitTests + InfrastructureTests + CommandCenter.Tests
./test.sh integration      # IntegrationTests (requires Docker for Testcontainers)
./test.sh e2e              # full compose-stack E2E against localhost:8080
```

`ARGUS_TEST_NO_BUILD=1` skips `dotnet build` before test (used in CI — build ran earlier).

Single test: `dotnet test src/tests/ArgusEngine.UnitTests/ArgusEngine.UnitTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~MyTest"`

## Architecture

Monorepo at `src/`, ~30 projects. Key boundaries:

| Layer | Directory | Entrypoint |
|---|---|---|
| **Contracts** | `ArgusEngine.Contracts`, `.CommandCenter.Contracts` | Shared DTOs, event envelopes, interfaces |
| **Domain** | `ArgusEngine.Domain` | Core entities, value objects, enums (`AssetKind`) |
| **Infrastructure** | `ArgusEngine.Infrastructure` | EF Core, messaging, persistence |
| **Application** | `ArgusEngine.Application` | Use cases, orchestration |
| **Command Center** | `ArgusEngine.CommandCenter.*` | 7 split-API services + Web (Blazor) + Gateway |
| **Workers** | `ArgusEngine.Workers.*` | Spider, Enumeration, HttpRequester, PortScan, HighValue, TechId |
| **Gatekeeper** | `ArgusEngine.Gatekeeper` | Admission control |
| **Deploy** | `deploy.py` | Python-only deployment console |

Gateway routes: `ArgusEngine.CommandCenter.Gateway/Program.cs` — maps API prefixes to downstream services.

All deployed projects carry centralized version from `Directory.Build.targets` (currently 2.6.2). `VERSION` at root tracks the same.

## Configuration

- `ASPNETCORE_ENVIRONMENT`: `Development` (local) or `Production` (CI/deploy)
- Gateway routing: `CommandCenter:Services:{Web|Discovery|Operations|...}` — URI per downstream
- Core: `Argus__Postgres__*`, `Argus__FileStore__*`, `ConnectionStrings__*`, `RabbitMq__*`
- Diagnostics: `Argus__Diagnostics__ApiKey` / `Argus__Diagnostics__Enabled`
- Startup opt-out: `Argus__SkipStartupDatabase` (set `"true"` for workers that don't need DB)
- Load order: `.env.local` → `.env` (via `scripts/development/common.sh`)
- Env examples: `deployment/config/argus.{local,dev,staging,production}.env.example`

## Development stack

```bash
cd deploy
# Full compose: docker compose -f deployment/docker-compose.yml up -d --build
```

Requires: **postgres, redis, rabbitmq** + all command-center services + gatekeeper + workers.

Local services: `./deploy deploy --hot` (incremental, auto-detects changes).

Health: `http://localhost:8081/health/live`, `http://localhost:8081/health/ready`  
Smoke: `./deploy smoke` (set `BASE_URL`, `ARGUS_DIAGNOSTICS_API_KEY`)  
Diagnostics: `/api/diagnostics/self` and `/api/diagnostics/dependencies` (header auth via `X-Argus-Diagnostics-Key`)

Deprecated env var alias: `NIGHTMARE_DIAGNOSTICS_API_KEY` may still appear in legacy configuration.

## Workers

| Worker | Dockerfile | Notes |
|---|---|---|
| spider, http-requester, portscan, highvalue, techid | `Dockerfile.worker` | Generic runtime |
| enumeration | `Dockerfile.worker-enum` | Vendored subfinder/amass |
| bootstrapper, spider-dispatcher, gatekeeper | `Dockerfile.worker` | Internal orchestrators |

## Testing quirks

- **Integration tests** spin up Postgres via Testcontainers — Docker required
- **E2E tests** need full compose stack up (`src/tests/e2e/run-e2e-suite.sh`)
- **Architecture tests** (`ArgusEngine.ArchitectureTests`) verify event envelope metadata and `AssetKind` enum stability — break if contracts change
- Release builds only (`Configuration=Release`) — no Debug config used in CI or test.sh

## CI pipeline (`.github/workflows/ci.yml`)

1. `dotnet restore` → `dotnet build` → `./test.sh all`
2. Validate Docker Compose manifests (`./deploy validate --ci`)
3. Docker build (matrix per service)
4. Compose startup smoke test (full stack with `docker-compose.ci.yml`)

Release (`release-main.yml`): triggered on push to `main` touching `src/`, `deploy/`, `scripts/`, etc. Uses `detect-affected-services.py` for incremental ECR publishing.

## Services added

### command-center-cloud-deploy-api
- Project: `ArgusEngine.CommandCenter.CloudDeploy.Api` — hybrid GCP/local deployment management
- Routes: `/api/cloud-deploy/*` (preflight, build, push, deploy, scale, teardown, core start/stop)
- Port: 8089, Dockerfile: `Dockerfile.commandcenter-host`
- Gateway: routed via `/api/cloud-deploy` prefix to `CloudDeployClientName`
- Registered in `deploy.py`, `docker-compose.yml`, `service-catalog.tsv`

## Recon orchestrator

The recon orchestrator (`ReconOrchestratorHostedService`) runs inside the **Enumeration worker** (`ArgusEngine.Workers.Enumeration`). It polls active targets and drives subdomain enumeration/spider workflows.

### Recon agent API
- Endpoint: `POST /api/recon-agent/targets/{targetId:guid}/attach` — attaches a target to the recon orchestrator
- Endpoint: `GET /api/recon-agent/targets/{targetId:guid}` — gets orchestrator snapshot
- Registered in `ArgusEngine.CommandCenter.Discovery.Api/Program.cs`
- Gateway route: `/api/recon-agent` → discovery API

## UI changes

### Right-click context menu
- File: `wwwroot/reconContextMenu.js`
- **Target rows**: "Assign Recon Orchestrator", "Enumerate Subdomains", "Spider"
- **Subdomain rows**: "Spider Subdomain"
- Uses toast notifications for feedback
- Data attributes: `data-target-id` on target root domain spans, `data-subdomain-key` on subdomain spans

### Status dashboard
- Component: `Components/StatusDashboard.razor`
- Shows green/red indicators for: Command Center, Postgres, Redis, RabbitMQ, Google Cloud workers, Local workers
- Polls `/health/ready` every 15 seconds

### Tab styling
- Tabs styled as proper tab controls with active indicator and smooth fade-in transitions
- "Argus Engine" eyebrow removed from hero header
- High Value Assets / Top 10 Technologies panels removed

## Known issues & fixes

### NpgsqlRetryingExecutionStrategy
- **Problem**: `ReconDbCommands` used `db.Database.OpenConnectionAsync()` which triggers the retry strategy. This strategy doesn't support user-initiated transactions, causing failures in `EfReconOrchestrator.AttachToTargetAsync`.
- **Fix**: Replaced with direct `DbConnection` management bypassing the execution strategy (in `ReconDbCommands.cs`).

### JSON curly braces in SQL
- **Problem**: `ReconOrchestratorSql.cs` used `'{}'` in SQL for JSON defaults. EF's `ExecuteSqlRawAsync` interprets `{...}` as format placeholders.
- **Fix**: Escaped to `'{{}}'`.

### CA1805/CA1725 build errors
- Fixed `AutoAttachNewTargets` explicit `= false` default (CA1805)
- Fixed parameter name mismatch `error` → `errorMessage` in `EfReconProviderRunRecorder.cs` (CA1725)

## GCP hybrid deployment

Workers deploy to Cloud Run (region `us-east1`). Infrastructure services (Postgres, Redis, RabbitMQ) run locally on GCE.

### Deployment flow
```bash
./deploy gcp configure          # setup deployment/gcp/.env + service-env
./deploy gcp provision          # enable APIs + create Artifact Registry
./deploy gcp release            # build, push, deploy all workers
```

### Worker connectivity
- Workers connect to host via **public IP** with firewall rules for ports 5432, 6379, 5672, 15672
- Host public IP: `34.148.132.67`
- Health probe: `CloudRunPortProbeService` (in `Infrastructure/Messaging/`) listens on PORT for Cloud Run health checks
- All 6 workers (spider, http-requester, enum, portscan, highvalue, techid) deployed and serving

### Cloud Run URLs
```
argus-worker-spider:    https://argus-worker-spider-x43swxblna-ue.a.run.app
argus-worker-enum:      https://argus-worker-enum-x43swxblna-ue.a.run.app
argus-worker-http-requester: https://argus-worker-http-requester-x43swxblna-ue.a.run.app
argus-worker-portscan:  https://argus-worker-portscan-x43swxblna-ue.a.run.app
argus-worker-highvalue: https://argus-worker-highvalue-x43swxblna-ue.a.run.app
argus-worker-techid:    https://argus-worker-techid-x43swxblna-ue.a.run.app
```

### Infrastructure endpoints (public)
- Gateway: `http://34.148.132.67:8081/`
- Web UI: `http://34.148.132.67:8082/`
- Cloud Deploy API: `http://34.148.132.67:8089/`

## Gateway routes summary

Routes are defined in `ArgusEngine.CommandCenter.Gateway/Program.cs` — `SelectClientName()` method.

| Prefix | Downstream service |
|---|---|
| `/api/cloud-deploy` | command-center-cloud-deploy-api |
| `/api/workers`, `/api/ec2-workers`, `/api/ops/*` | command-center-worker-control-api |
| `/api/status`, `/api/ops` | command-center-operations-api |
| `/api/targets`, `/api/assets`, `/api/discovery`, `/api/recon-agent`, etc. | command-center-discovery-api |
| `/api/admin`, `/api/maintenance`, `/api/diagnostics` | command-center-maintenance-api |
| `/api/development/components` | command-center-updates-api |
| `/hubs/discovery` | command-center-realtime |
| `/` (default) | command-center-web |

## Latest UI state

### Summary cards (in header)
- 9 cards: Targets, Assets, HTTP Queue, High Value, Technologies, Workers, Events, Version, Build
- Halved size, single row at all widths, "Database total" removed
- Version/build from ops overview API

### Grid changes (ArgusDataGrid)
- Toolbar: only Search + Configure above each grid
- Export CSV, top pager, rows dropdown, density removed from top
- Virtualized/Paged/grouped labels removed
- Rows dropdown + paginator at bottom
- Target toolbar: name/depth/create/reload/enum/spider buttons removed

### Context menu (`reconContextMenu.js`)
- Right-click on target rows: Assign Recon Orchestrator, Enumerate Subdomains, Spider
- Right-click on subdomain rows: Spider Subdomain
- Action results panel (collapsible) with toast notifications
- System events panel below showing last 30 bus journal entries

### Recon agent API
- `POST /api/recon-agent/targets/{id}/attach` → registers target with recon orchestrator
- `GET /api/recon-agent/targets/{id}` → get orchestrator snapshot
- Registered in discovery API Program.cs, routed via gateway

### Known issues
- `high-value-path-guess_error` queue has ~3871 stuck messages with 0 consumers
- `buildTime` may show "unknown" if ARGUS_BUILD_TIME_UTC env var not set
- Azure/AWS workers show as "not configured" (no API integration)
- GCP auto-scale from UI needs `GcpDeploy:ProjectId` env var set

## Working in parallel

### Agent identities

- **codex**: senior architect / lead implementer / final decision-maker
- **agent-alpha**: focused implementation helper
- **agent-beta**: DevOps, scripting, procedures, diagnostics helper

### Protocol

1. `git fetch --all --prune && git pull --rebase --autostash` before any work
2. Run `bash .ai-coordination/scripts/coordinate.sh status` to check locks and agents
3. Claim files before editing: `bash .ai-coordination/scripts/coordinate.sh claim <agent-name> <file-path> "<purpose>"`
4. Do not edit files locked by another agent
5. Do not overwrite another agent's changes (no reset --hard, clean -fd, push --force)
6. Push small coherent commits: `bash .ai-coordination/scripts/coordinate.sh push <agent-name> "<area>: <summary>"`
7. Release files when done: `bash .ai-coordination/scripts/coordinate.sh release <agent-name> <file-path>`
8. During long work: `bash .ai-coordination/scripts/coordinate.sh heartbeat <agent-name>`
9. Codex has final authority on architecture, service boundaries, contracts, and conflict resolution
10. agent-alpha/agent-beta must stop and request a Codex decision before modifying protected files (csproj, contracts, gateway, compose, Program.cs, etc.)

### Protected files (require codex approval for helpers)

- *.sln, *.slnx, *.csproj, Directory.Build.*, Directory.Packages.props
- src/*Contracts*/**
- src/ArgusEngine.Domain/**
- EF migrations or schema-changing persistence files
- src/ArgusEngine.CommandCenter.Gateway/**
- cross-service API routing
- Program.cs, Startup*.cs, service registration extensions
- deploy.py (topology changes), deployment/docker-compose*.yml, deployment/gcp/**
- auth, secrets, diagnostics keys, CORS, firewall, IAM, proxy routing

### Merge/conflict rules

- Preserve both agents' work whenever possible
- If behavior conflicts, helper agents stop and record a blocker: `bash .ai-coordination/scripts/coordinate.sh block <agent-name> "<reason>"`
- Only Codex may discard another agent's work, with reason recorded in decision log
- Codex records decisions with: `bash .ai-coordination/scripts/coordinate.sh decision codex "<summary>" <scope> <files>`

## Debug

`./debug.sh` executes commands from `.ai-debug/debug_commands.sh` and writes JSON results to `debug_results.json` (requires python3).
