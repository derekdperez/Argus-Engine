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

## Debug

`./debug.sh` executes commands from `.ai-debug/debug_commands.sh` and writes JSON results to `debug_results.json` (requires python3).
