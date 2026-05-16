# Argus Engine Architecture Guide

> This document is for AI agents working on the Argus Engine codebase. It outlines the architecture and best practices for implementing changes.

## 1. Overall Architecture

### 1.1 Monorepo Structure (~30 Projects)

The Argus Engine is a .NET 10.0 distributed reconnaissance engine organized as a monorepo with approximately 30 projects located in `src/`:

| Category | Projects |
|----------|----------|
| **Contracts** | `ArgusEngine.Contracts`, `ArgusEngine.CommandCenter.Contracts` |
| **Domain** | `ArgusEngine.Domain` |
| **Infrastructure** | `ArgusEngine.Infrastructure` |
| **Application** | `ArgusEngine.Application` |
| **Command Center APIs** | `ArgusEngine.CommandCenter.Discovery.Api`, `Operations.Api`, `Maintenance.Api`, `WorkerControl.Api`, `Updates.Api`, `CloudDeploy.Api`, `Realtime.Host`, `Gateway` |
| **Command Center Web** | `ArgusEngine.CommandCenter.Web` (Blazor UI) |
| **Workers** | `ArgusEngine.Workers.Spider`, `Enumeration`, `HttpRequester`, `PortScan`, `HighValue`, `TechnologyIdentification` |
| **Orchestration** | `ArgusEngine.CommandCenter.Bootstrapper`, `SpiderDispatcher`, `Gatekeeper` |
| **Testing** | `ArgusEngine.UnitTests`, `Infrastructure.Tests`, `IntegrationTests`, `ArchitectureTests`, `CommandCenter.Tests` |

**Solution File**: `/home/derekdperez_dev/argus-engine/ArgusEngine.slnx`

**Version Management**: Centralized in `Directory.Build.targets` (currently 2.6.2). All deployed projects carry this version via `ArgusEngineDeploymentVersion` property.

### 1.2 Layer Breakdown

| Layer | Directory | Purpose | Key Files |
|-------|-----------|---------|-----------|
| **Contracts** | `src/ArgusEngine.Contracts` | Shared DTOs, event envelopes, interfaces | `AssetKind.cs`, `Events/*.cs` |
| **Domain** | `src/ArgusEngine.Domain` | Core entities, value objects, enums | `Entities/*.cs` |
| **Infrastructure** | `src/ArgusEngine.Infrastructure` | EF Core, messaging, persistence | `DependencyInjection.cs`, `Persistence/Data/*.cs` |
| **Application** | `src/ArgusEngine.Application` | Use cases, orchestration | `Workers/*.cs`, `Orchestration/*.cs` |
| **Command Center** | `src/ArgusEngine.CommandCenter.*` | 8 split-API services + Blazor Web | `Program.cs`, `Endpoints/*.cs` |
| **Workers** | `src/ArgusEngine.Workers.*` | Background job processors | `Program.cs`, `Consumers/*.cs` |
| **Gatekeeper** | `src/ArgusEngine.Gatekeeper` | Asset admission control | `Consumers/*.cs` |

### 1.3 Deployment Architecture

**Local Development**: Docker Compose (`deployment/docker-compose.yml`)
- **Gateway**: Port 8081 (routes to downstream services)
- **Web UI**: Port 8082
- **Cloud Deploy API**: Port 8089

**Infrastructure Services**:
- **PostgreSQL**: Main database (port 5432) + FileStore database (port 5432, separate DB)
- **Redis**: Caching and deduplication (port 6379)
- **RabbitMQ**: Message bus (ports 5672, 15672 for management)

**GCP Cloud Run Workers**:
Workers deploy to Cloud Run (region `us-east1`) while infrastructure runs locally:
- `argus-worker-spider`
- `argus-worker-enum`
- `argus-worker-http-requester`
- `argus-worker-portscan`
- `argus-worker-highvalue`
- `argus-worker-techid`

**Deployment Scripts**:
- `./deploy` - Main deployment script (wraps `deploy.py`)
- `./deploy gcp configure` - GCP setup
- `./deploy gcp release` - Build, push, deploy workers

---

## 2. Core Components

### 2.1 Key Interfaces and Implementations

**Infrastructure Dependency Injection** (`src/ArgusEngine.Infrastructure/DependencyInjection.cs`):

```csharp
// Core services registration pattern:
services.AddDbContextFactory<ArgusDbContext>(ConfigureNpgsql);
services.AddSingleton<IAssetCanonicalizer, DefaultAssetCanonicalizer>();
services.AddSingleton<IAssetDeduplicator, RedisAssetDeduplicator>();
services.AddSingleton<ITargetScopeEvaluator, DnsTargetScopeEvaluator>();
services.AddScoped<IAssetAdmissionDecisionWriter, EfAssetAdmissionDecisionWriter>();
services.AddScoped<IAssetGraphService, EfAssetGraphService>();
services.AddScoped<IAssetPersistence, EfAssetPersistence>();
services.AddScoped<IReconOrchestrator, EfReconOrchestrator>();
services.AddScoped<IHighValueFindingWriter, EfHighValueFindingWriter>();
services.AddScoped<IAssetTagService, EfAssetTagService>();
```

**Worker Interfaces** (`src/ArgusEngine.Application/Workers/`):
- `ISubdomainEnumerationProvider` - Subdomain enumeration (Subfinder, Amass)
- `IPortScanService` - Port scanning
- `IHttpRequestQueueStateMachine` - HTTP request queue state management
- `IWorkerToggleReader` - Worker on/off control
- `ITargetLookup` - Target resolution

### 2.2 Database Schema (Main Tables)

**Primary Database** (`ArgusDbContext` in `src/ArgusEngine.Infrastructure/Persistence/Data/ArgusDbContext.cs`):

| Table | Entity | Purpose |
|-------|--------|---------|
| `recon_targets` | `ReconTarget` | Root domains being scanned |
| `stored_assets` | `StoredAsset` | Discovered assets (subdomains, URLs, etc.) |
| `asset_relationships` | `AssetRelationship` | Parent-child relationships between assets |
| `bus_journal` | `BusJournalEntry` | Message bus observability |
| `worker_heartbeats` | `WorkerHeartbeat` | Worker health tracking |
| `worker_switches` | `WorkerSwitch` | Worker on/off toggles |
| `http_request_queue` | `HttpRequestQueueItem` | HTTP request queue |
| `high_value_findings` | `HighValueFinding` | High-value asset findings |
| `tags`, `asset_tags` | `Tag`, `AssetTag` | Technology tagging |
| `technology_detections` | `TechnologyDetection` | Technology fingerprint matches |
| `technology_observations` | `TechnologyObservation` | Technology scan results |
| `outbox_messages` | `OutboxMessage` | Reliable event publishing |
| `inbox_messages` | `InboxMessage` | Message deduplication |
| `system_errors` | `SystemError` | Centralized application logging |
| `recon_orchestrator_states` | (no entity) | Recon orchestrator per-target state |
| `recon_orchestrator_provider_runs` | (no entity) | Enumeration provider run tracking |

**Schema Management**:
- EF Core migrations via `EnsureCreated()` + schema patches in `ArgusDbSchemaPatches.cs`
- Patches run after EnsureCreated for upgrades
- Uses advisory locks (`pg_advisory_xact_lock`) for safe concurrent execution

### 2.3 Message Bus (RabbitMQ) and Event Flow

**MassTransit Configuration** (`src/ArgusEngine.Infrastructure/Messaging/MassTransitRabbitExtensions.cs`):

```csharp
services.AddArgusRabbitMq(configuration, bus => {
    bus.AddConsumer<HttpResponseDownloadedConsumer>();
    // Add consumers here
});
```

**Event Envelope** (`src/ArgusEngine.Contracts/Events/IEventEnvelope.cs`):
```csharp
public interface IEventEnvelope
{
    Guid EventId { get; }
    Guid CorrelationId { get; }
    Guid CausationId { get; }
    DateTimeOffset OccurredAtUtc { get; }
    string SchemaVersion { get; }
    string Producer { get; }
}
```

**Key Event Types** (`src/ArgusEngine.Contracts/Events/`):
- `TargetCreated` - New target added
- `AssetDiscovered` - New asset found
- `AssetRelationshipDiscovered` - Relationship between assets
- `HttpResponseDownloaded` - HTTP response available for processing
- `SubdomainEnumerationRequested` - Subdomain enumeration requested
- `PortScanRequested` - Port scan requested
- `CriticalHighValueFindingAlert` - High-priority finding alert
- `ScannableContentAvailable` - Content ready for spidering

**Event Flow**:
1. API receives request -> Publishes event via MassTransit
2. Worker consumes event -> Processes -> Publishes follow-up events
3. Events written to outbox -> OutboxDispatcher publishes to RabbitMQ
4. Bus journal tracks all publishes/consumes for observability

**Outbox Pattern**:
- `IEventOutbox.EnqueueAsync()` - Queue event for reliable delivery
- `OutboxDispatcherWorker` background service dispatches queued events

---

## 3. Worker Types

### 3.1 Worker Overview

| Worker | Project | Purpose | Consumer |
|--------|---------|---------|----------|
| **Spider** | `ArgusEngine.Workers.Spider` | Web crawling, link extraction | `HttpResponseDownloadedConsumer` |
| **Enumeration** | `ArgusEngine.Workers.Enumeration` | Subdomain enumeration (subfinder, amass), runs ReconOrchestrator | `TargetCreatedConsumer`, `SubdomainEnumerationRequestedConsumer` |
| **HTTP Requester** | `ArgusEngine.Workers.HttpRequester` | HTTP requests for URLs | (via queue processing) |
| **PortScan** | `ArgusEngine.Workers.PortScan` | Port scanning | `PortScanRequestedConsumer` |
| **HighValue** | `ArgusEngine.Workers.HighValue` | Pattern matching for sensitive data | `HighValueRegexConsumer`, `HighValuePathGuessConsumer` |
| **TechId** | `ArgusEngine.Workers.TechnologyIdentification` | Technology fingerprinting | `TechnologyIdentificationConsumer` |

### 3.2 Worker Communication and Orchestration

**Worker Program Pattern** (e.g., `src/ArgusEngine.Workers.Spider/Program.cs`):
```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: true);
builder.Services.AddArgusWorkerHeartbeat(WorkerKeys.Spider);
builder.Services.AddArgusRabbitMq(builder.Configuration, bus => {
    bus.AddConsumer<HttpResponseDownloadedConsumer>();
});

var host = builder.Build();
await ArgusDbBootstrap.InitializeAsync(host.Services, ...);
await host.RunAsync();
```

**Orchestrator**:
- `ReconOrchestratorHostedService` runs inside **Enumeration worker**
- Polls active targets and drives subdomain enumeration/spider workflows
- API: `POST /api/recon-agent/targets/{id}/attach` - Attaches target to orchestrator

**Worker Heartbeat**:
- Each worker registers a heartbeat via `AddArgusWorkerHeartbeat()`
- Writes to `worker_heartbeats` table with hostname + worker key
- `CloudRunPortProbeService` listens on PORT for Cloud Run health checks

---

## 4. API Structure

### 4.1 Gateway Routing

**Gateway** (`src/ArgusEngine.CommandCenter.Gateway/Program.cs`):
Routes incoming requests to split-API services based on path prefix:

| Prefix | Downstream Service | Client Name |
|--------|-------------------|-------------|
| `/api/cloud-deploy` | Cloud Deploy API | `command-center-cloud-deploy-api` |
| `/api/workers`, `/api/ec2-workers`, `/api/ops/*` | Worker Control API | `command-center-worker-control-api` |
| `/api/status`, `/api/ops` | Operations API | `command-center-operations-api` |
| `/api/targets`, `/api/assets`, `/api/discovery`, `/api/recon-agent`, `/api/logs`, `/api/worker-logs` | Discovery API | `command-center-discovery-api` |
| `/api/admin`, `/api/maintenance`, `/api/diagnostics`, `/api/bus` | Maintenance API | `command-center-maintenance-api` |
| `/api/development/components` | Updates API | `command-center-updates-api` |
| `/hubs/discovery` | Realtime Host | `command-center-realtime` |
| `/` (default) | Web UI | `command-center-web` |

**Configuration** (via `CommandCenter:Services:{ServiceName}`):
```json
{
  "CommandCenter": {
    "Services": {
      "Web": "http://command-center-web:8080/",
      "Discovery": "http://command-center-discovery-api:8080/"
    }
  }
}
```

### 4.2 Split API Services

**Discovery API** (`src/ArgusEngine.CommandCenter.Discovery.Api/Program.cs`):
- Maps endpoints in `Endpoints/` folder
- Key endpoints: Targets, Assets, Tags, Technologies, HighValueFindings, HttpRequestQueue, ReconAgent

**Operations API** (`src/ArgusEngine.CommandCenter.Operations.Api/Program.cs`):
- System status, operations control

**Maintenance API** (`src/ArgusEngine.CommandCenter.Maintenance.Api/Program.cs`):
- Admin, diagnostics, logs, bus journal

**Worker Control API** (`src/ArgusEngine.CommandCenter.WorkerControl.Api/Program.cs`):
- Worker management, EC2 workers, GCP workers

**Cloud Deploy API** (`src/ArgusEngine.CommandCenter.CloudDeploy.Api/Program.cs`):
- GCP hybrid deployment management

### 4.3 Key Endpoints Pattern

**Endpoint Registration** (e.g., `src/ArgusEngine.CommandCenter.Discovery.Api/Endpoints/TargetEndpoints.cs`):
```csharp
public static class TargetEndpoints
{
    public static IEndpointRouteBuilder MapTargetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/targets", async (ArgusDbContext db, ...) => { ... });
        app.MapPost("/api/targets", async (CreateTargetRequest dto, ...) => { ... });
        app.MapPut("/api/targets/{id:guid}", ...);
        app.MapDelete("/api/targets/{id:guid}", ...);
        return app;
    }
}
```

---

## 5. Change Implementation Procedures

### 5.1 How to Add a New API Endpoint

1. **Identify the correct API service** based on path:
   - Targets/Assets/Discovery -> `ArgusEngine.CommandCenter.Discovery.Api`
   - Status/Ops -> `ArgusEngine.CommandCenter.Operations.Api`
   - Admin/Diagnostics/Logs -> `ArgusEngine.CommandCenter.Maintenance.Api`

2. **Create or extend endpoint file** in `Endpoints/`:
   ```csharp
   // In Endpoints/NewResourceEndpoints.cs
   public static class NewResourceEndpoints
   {
       public static IEndpointRouteBuilder MapNewResourceEndpoints(this IEndpointRouteBuilder app)
       {
           app.MapGet("/api/new-resource", async (ArgusDbContext db, CancellationToken ct) => {
               // Implementation
               return Results.Ok(...);
           });
           
           app.MapPost("/api/new-resource", async (CreateRequest dto, ...) => {
               // Implementation
               return Results.Created(...);
           });
           
           return app;
       }
   }
   ```

3. **Register endpoints** in the API's `Program.cs`:
   ```csharp
   // In Maintenance.Api/Program.cs
   app.MapNewResourceEndpoints();
   ```

4. **Update Gateway routing** in `SelectClientName()`:
   ```csharp
   if (path.StartsWithSegments("/api/new-resource", ...))
   {
       return GatewayServiceRoutes.MaintenanceClientName;
   }
   ```

5. **Update GatewayRouteDiagnostics** in same file to include the new prefix

6. **Rebuild and restart** the affected containers

### 5.2 How to Add a New Worker

1. **Create new worker project** in `src/ArgusEngine.Workers.NewWorker/`

2. **Create Program.cs** with standard pattern:
   ```csharp
   var builder = Host.CreateApplicationBuilder(args);
   
   builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: true);
   builder.Services.AddArgusWorkerHeartbeat(WorkerKeys.NewWorker);
   builder.Services.AddScoped<IWorkerHealthCheck, NewWorkerHealthCheck>();
   
   builder.Services.AddArgusRabbitMq(builder.Configuration, bus => {
       bus.AddConsumer<NewWorkerConsumer>();
   });
   
   var host = builder.Build();
   await ArgusDbBootstrap.InitializeAsync(host.Services, ...);
   await host.RunAsync();
   ```

3. **Add consumer class**:
   ```csharp
   public sealed class NewWorkerConsumer : IConsumer<NewWorkerMessage>
   {
       public async Task Consume(ConsumeContext<NewWorkerMessage> context)
       {
           // Processing logic
       }
   }
   ```

4. **Add to docker-compose.yml**:
   ```yaml
   worker-new:
     image: argus-engine/worker-new:${VERSION}
     environment:
       <<: *common-env
     depends_on:
       - rabbitmq
       - postgres
   ```

5. **Update deploy.py** to include the new worker

### 5.3 How to Modify Database Schema

1. **For new tables/entities**: Add to `ArgusDbContext`:
   ```csharp
   public DbSet<NewEntity> NewEntities => Set<NewEntity>();
   
   // In OnModelCreating:
   modelBuilder.Entity<NewEntity>(e => {
       e.ToTable("new_entities");
       e.HasKey(x => x.Id);
       // Configure properties
   });
   ```

2. **For schema patches** (adding columns to existing tables):
   Add to `ArgusDbSchemaPatches.ApplyAfterEnsureCreatedAsync()`:
   ```csharp
   await db.Database.ExecuteSqlRawAsync(
       "ALTER TABLE existing_table ADD COLUMN new_column VARCHAR(256);",
       cancellationToken).ConfigureAwait(false);
   ```

3. **Run tests** to verify migration works:
   ```bash
   ./test.sh integration
   ```

### 5.4 How to Add a New UI Tab/Component

1. **Add tab to CommandCenter.razor**:
   - Add new enum value to `CommandTab` enum
   - Add tab button in the `<nav class="cc-tabs">` section
   - Add tab content section with `@if (_activeTab == CommandTab.NewTab)`
   - Add state variables in `@code` section
   - Add load method in `SelectTabAsync()` for lazy loading
   - Add filter/search state variables as needed

2. **Create endpoint** in appropriate API service (see 5.1)

3. **Add CSS** to `CommandCenter.razor.css` if needed

4. **Rebuild web container**:
   ```bash
   docker compose -f deployment/docker-compose.yml build command-center-web
   docker compose -f deployment/docker-compose.yml up -d command-center-web
   ```

### 5.5 How to Add a New Event/Message Type

1. **Create event class** in `src/ArgusEngine.Contracts/Events/`:
   ```csharp
   public sealed record NewEvent(
       Guid TargetId,
       string Payload,
       // IEventEnvelope fields
   ) : IEventEnvelope
   {
       public Guid EventId { get; init; } = NewId.NextGuid();
       public Guid CorrelationId { get; init; }
       public Guid CausationId { get; init; }
       public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
       public string SchemaVersion { get; init; } = "1.0";
       public string Producer { get; init; } = "service-name";
   }
   ```

2. **Add consumer** in appropriate worker:
   ```csharp
   public sealed class NewEventConsumer : IConsumer<NewEvent>
   {
       public async Task Consume(ConsumeContext<NewEvent> context) { ... }
   }
   ```

3. **Register consumer** in worker's `Program.cs`:
   ```csharp
   builder.Services.AddArgusRabbitMq(builder.Configuration, bus => {
       bus.AddConsumer<NewEventConsumer>();
   });
   ```

4. **Publish event** from API:
   ```csharp
   await publishEndpoint.Publish(new NewEvent(...), cancellationToken);
   ```

### 5.6 Container Rebuild and Deployment Process

**Local Development**:
```bash
# Rebuild specific service
docker compose -f deployment/docker-compose.yml build command-center-discovery-api

# Restart service
docker compose -f deployment/docker-compose.yml up -d command-center-discovery-api

# Hot reload (auto-detects changes)
./deploy deploy --hot
```

**Full Stack**:
```bash
# Start full stack
docker compose -f deployment/docker-compose.yml up -d --build

# Check health
curl http://localhost:8081/health/ready
```

**GCP Deployment**:
```bash
./deploy gcp configure   # Setup GCP environment
./deploy gcp release     # Build, push, deploy workers
```

---

## 6. Best Practices

### 6.1 Configuration Management

**Environment Loading Order** (via `scripts/development/common.sh`):
1. `.env.local` (highest priority)
2. `.env` (fallback)

**Configuration Keys**:
- Database: `ConnectionStrings__Postgres`, `ConnectionStrings__FileStore`
- Redis: `ConnectionStrings__Redis`
- RabbitMQ: `RabbitMq__Host`, `RabbitMq__Username`, etc.
- Workers: `Argus__Postgres__MaxPoolSize`, `Argus__SkipStartupDatabase`
- Diagnostics: `Argus__Diagnostics__ApiKey`, `Argus__Diagnostics__Enabled`

**Configuration Examples**:
- `deployment/config/argus.local.env.example`
- `deployment/config/argus.dev.env.example`

### 6.2 Testing Approach

**Test Categories**:
```bash
./test.sh unit        # UnitTests, InfrastructureTests, CommandCenter.Tests
./test.sh integration  # IntegrationTests (requires Docker for Testcontainers)
./test.sh e2e         # Full compose-stack E2E (requires stack up)
./test.sh all         # unit + integration
```

**Test Projects**:
- `ArgusEngine.UnitTests` - Unit tests
- `ArgusEngine.Infrastructure.Tests` - Infrastructure tests
- `ArgusEngine.CommandCenter.Tests` - Command center tests
- `ArgusEngine.IntegrationTests` - Database integration (Testcontainers)
- `ArgusEngine.ArchitectureTests` - Architecture rules validation

**Architecture Tests** verify:
- Event envelope metadata consistency
- `AssetKind` enum stability (numeric values are stable persistence values)

### 6.3 Logging Conventions

**Structured Logging** via `LoggerMessage.Define`:
```csharp
private static readonly Action<ILogger, string, Exception?> LogProcessing =
    LoggerMessage.Define<string>(
        LogLevel.Information,
        new EventId(1, nameof(LogProcessing)),
        "Processing {ItemId}");

// Usage:
LogProcessing(logger, itemId, null);
```

**Logging Scopes**:
```csharp
using var scope = logger.BeginScope(new Dictionary<string, object?> {
    ["TargetId"] = targetId,
    ["AssetId"] = assetId
});
```

**Centralized Logging**:
- Logs are written to `system_errors` table via `ArgusDatabaseLoggerProvider`
- Workers log with component names like `worker-spider`, `worker-enum`, `gatekeeper`
- Access via `/api/logs` (app logs) and `/api/worker-logs` (worker logs) endpoints

### 6.4 Transaction Handling Gotchas

**NpgsqlRetryingExecutionStrategy Issue** (Critical):
- **Problem**: `DbContext.Database.OpenConnectionAsync()` triggers the retry strategy, which doesn't support user-initiated transactions
- **Affected Code**: `ReconDbCommands` in the recon orchestrator, `EfAssetGraphService`
- **Fix**: Use direct `DbConnection` management bypassing the execution strategy:
  ```csharp
  // In EfAssetGraphService
  db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
  await using var tx = await db.Database.BeginTransactionAsync(ct);
  ```
- **Container Note**: Always rebuild containers after making code fixes - the old container continues running with the bug

**JSON Curly Braces in SQL**:
- **Problem**: `ExecuteSqlRawAsync` interprets `{...}` as format placeholders
- **Fix**: Escape to `'{{}}'` for literal JSON defaults

### 6.5 Git Workflow

**Pre-commit Checks**:
```bash
# Build before committing
dotnet build ArgusEngine.slnx --configuration Release

# Run tests
./test.sh unit

# Check git status
git status
```

**Commit Process** (from AGENTS.md):
1. `git pull` before starting
2. Make changes
3. Verify build: `dotnet build`
4. `git add <files>`
5. `git commit -m "description"`
6. `git push` after session

**Version Updates**:
- Update `Directory.Build.targets` for version changes
- Update `VERSION` file (single line, e.g., `2.6.2`)

---

## 7. Key File Reference

| Purpose | File Path |
|---------|-----------|
| Solution | `ArgusEngine.slnx` |
| Version | `Directory.Build.targets`, `VERSION` |
| Gateway Routing | `src/ArgusEngine.CommandCenter.Gateway/Program.cs` |
| Database Context | `src/ArgusEngine.Infrastructure/Persistence/Data/ArgusDbContext.cs` |
| Schema Patches | `src/ArgusEngine.Infrastructure/Persistence/Data/ArgusDbSchemaPatches.cs` |
| DI Container | `src/ArgusEngine.Infrastructure/DependencyInjection.cs` |
| Messaging Setup | `src/ArgusEngine.Infrastructure/Messaging/MassTransitRabbitExtensions.cs` |
| Docker Compose | `deployment/docker-compose.yml` |
| Deploy Script | `deploy.py` |
| Test Runner | `test.sh` |
| CI Workflows | `.github/workflows/` |
| Blazor UI | `src/ArgusEngine.CommandCenter.Web/Components/Pages/CommandCenter.razor` |
| Context Menu | `src/ArgusEngine.CommandCenter.Web/wwwroot/reconContextMenu.js` |
| Config Examples | `deployment/config/argus.*.env.example` |
| Agent Guide | `AGENTS.md` |