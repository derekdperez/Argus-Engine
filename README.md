# Argus Engine

Argus Engine is a distributed .NET reconnaissance platform built around a gatekeeping pipeline, a web-based command center, shared contracts/domain/application layers, and multiple background workers. The current repository is a multi-project solution under `src/`, not just a static site. ([repo root](https://github.com/derekdperez/Argus-Engine), [src layout](https://github.com/derekdperez/Argus-Engine/tree/main/src))

## What is in this repository

The solution is organized into these main projects:

- `ArgusEngine.CommandCenter` — web UI and operational/admin endpoints. It uses Razor Components, Radzen, SignalR, health checks, and startup database initialization. ([CommandCenter](https://github.com/derekdperez/Argus-Engine/tree/main/src/ArgusEngine.CommandCenter), [Program.cs](https://raw.githubusercontent.com/derekdperez/Argus-Engine/refs/heads/main/src/ArgusEngine.CommandCenter/Program.cs), [service registration](https://raw.githubusercontent.com/derekdperez/Argus-Engine/refs/heads/main/src/ArgusEngine.CommandCenter/Startup/CommandCenterServiceRegistration.cs), [middleware](https://raw.githubusercontent.com/derekdperez/Argus-Engine/refs/heads/main/src/ArgusEngine.CommandCenter/Startup/CommandCenterMiddleware.cs), [endpoint registration](https://raw.githubusercontent.com/derekdperez/Argus-Engine/refs/heads/main/src/ArgusEngine.CommandCenter/Endpoints/CommandCenterEndpointRegistration.cs))
- `ArgusEngine.Gatekeeper` — admission pipeline consumers that accept discovered assets and asset relationships, deduplicate them, validate them against scope, persist them, audit the decision, and republish accepted work for downstream processing. ([Gatekeeper project](https://github.com/derekdperez/Argus-Engine/tree/main/src/ArgusEngine.Gatekeeper), [consumers](https://github.com/derekdperez/Argus-Engine/tree/main/src/ArgusEngine.Gatekeeper/Consumers), [GatekeeperOrchestrator](https://github.com/derekdperez/Argus-Engine/blob/main/src/ArgusEngine.Application/Gatekeeping/GatekeeperOrchestrator.cs))
- `ArgusEngine.Workers.*` — background workers for enumeration, high-value analysis, port scanning, spidering, and technology identification. ([src layout](https://github.com/derekdperez/Argus-Engine/tree/main/src), [Spider Program.cs](https://raw.githubusercontent.com/derekdperez/Argus-Engine/refs/heads/main/src/ArgusEngine.Workers.Spider/Program.cs))
- `ArgusEngine.Application` — application-layer logic for assets, events, gatekeeping, file store access, retention, worker orchestration, sagas, HTTP, and analysis flows. ([Application project](https://github.com/derekdperez/Argus-Engine/tree/main/src/ArgusEngine.Application))
- `ArgusEngine.Domain` — domain entities. ([Domain project](https://github.com/derekdperez/Argus-Engine/tree/main/src/ArgusEngine.Domain))
- `ArgusEngine.Contracts` — shared contracts and event types exchanged across services. ([Contracts project](https://github.com/derekdperez/Argus-Engine/tree/main/src/ArgusEngine.Contracts))
- `ArgusEngine.Infrastructure` — persistence, messaging, observability, health checks, data retention, file storage, and supporting infrastructure wiring. ([Infrastructure project](https://github.com/derekdperez/Argus-Engine/tree/main/src/ArgusEngine.Infrastructure))

## High-level architecture

At a high level, the system works like this:

1. Discovery events enter the platform.
2. The Gatekeeper canonicalizes and deduplicates candidate assets, enforces scope/depth rules, persists accepted assets, records admission decisions, and publishes accepted work to downstream processing.
3. Background workers consume work and enrich the asset graph.
4. The Command Center exposes UI and operational endpoints for monitoring and maintenance.

The Gatekeeper orchestration explicitly handles cases such as max depth exceeded, duplicate canonical keys, out-of-scope assets, persistence failures, and accepted new assets. Accepted IP assets can enqueue downstream port-scan work. ([GatekeeperOrchestrator](https://github.com/derekdperez/Argus-Engine/blob/main/src/ArgusEngine.Application/Gatekeeping/GatekeeperOrchestrator.cs))

## Messaging and storage

Argus Engine currently wires messaging through MassTransit with RabbitMQ. The messaging layer includes an event outbox, inbox deduplication, bus journal helpers, and an outbox dispatcher worker. The RabbitMQ integration notes that development uses RabbitMQ and production is intended to be swappable to MassTransit Amazon SQS. ([messaging folder](https://github.com/derekdperez/Argus-Engine/tree/main/src/ArgusEngine.Infrastructure/Messaging), [MassTransitRabbitExtensions.cs](https://raw.githubusercontent.com/derekdperez/Argus-Engine/refs/heads/main/src/ArgusEngine.Infrastructure/Messaging/MassTransitRabbitExtensions.cs), [OutboxDispatcherWorker.cs](https://raw.githubusercontent.com/derekdperez/Argus-Engine/refs/heads/main/src/ArgusEngine.Infrastructure/Messaging/OutboxDispatcherWorker.cs))

Infrastructure wiring includes PostgreSQL, Redis, file-store persistence, health checks, worker services, and data-retention services. In development, fallback connection strings still reference legacy `nightmare_v2` database names. ([DependencyInjection.cs](https://raw.githubusercontent.com/derekdperez/Argus-Engine/main/src/ArgusEngine.Infrastructure/DependencyInjection.cs))

The codebase also includes a dedicated file-store path for HTTP artifacts rather than keeping large response bodies directly on queue rows. That architecture is enforced by infrastructure tests. ([OriginalChecklistImplementationTests.cs](https://github.com/derekdperez/Argus-Engine/blob/main/src/tests/ArgusEngine.Infrastructure.Tests/OriginalChecklistImplementationTests.cs))

## Command Center

The Command Center is the operator-facing web app. It registers Razor Components, Radzen, SignalR, Argus infrastructure/services, startup database initialization, and health endpoints. Endpoint registration includes asset-admission decisions, data-retention admin endpoints, HTTP artifact backfill endpoints, and a SignalR hub at `/hubs/discovery`. ([service registration](https://raw.githubusercontent.com/derekdperez/Argus-Engine/refs/heads/main/src/ArgusEngine.CommandCenter/Startup/CommandCenterServiceRegistration.cs), [middleware](https://raw.githubusercontent.com/derekdperez/Argus-Engine/refs/heads/main/src/ArgusEngine.CommandCenter/Startup/CommandCenterMiddleware.cs), [endpoint registration](https://raw.githubusercontent.com/derekdperez/Argus-Engine/refs/heads/main/src/ArgusEngine.CommandCenter/Endpoints/CommandCenterEndpointRegistration.cs), [CommandCenter structure](https://github.com/derekdperez/Argus-Engine/tree/main/src/ArgusEngine.CommandCenter))

## Observability

Observability is a first-class concern in the repo. There is a dedicated observability package in infrastructure, and tests assert that observability is wired into the Command Center, Gatekeeper, and all worker executables. The retention/outbox path is also instrumented with metrics and tracing. ([Observability folder](https://github.com/derekdperez/Argus-Engine/tree/main/src/ArgusEngine.Infrastructure/Observability), [OriginalChecklistImplementationTests.cs](https://github.com/derekdperez/Argus-Engine/blob/main/src/tests/ArgusEngine.Infrastructure.Tests/OriginalChecklistImplementationTests.cs))

## Tests

The repository contains:

- `ArgusEngine.CommandCenter.Tests`
- `ArgusEngine.Infrastructure.Tests`
- `src/tests/e2e` shell-based end-to-end checks

Current tests include both architecture/checklist assertions and some repository-level workflow scripts. ([tests folder](https://github.com/derekdperez/Argus-Engine/tree/main/src/tests), [CommandCenterChecklistTests.cs](https://raw.githubusercontent.com/derekdperez/Argus-Engine/main/src/tests/ArgusEngine.CommandCenter.Tests/CommandCenterChecklistTests.cs), [e2e folder](https://github.com/derekdperez/Argus-Engine/tree/main/src/tests/e2e))

## Runtime and toolchain

The repository currently targets:

- `.NET 10`
- nullable reference types enabled
- implicit usings enabled
- `LangVersion=latest`

See `Directory.Build.props` for the shared SDK configuration. ([Directory.Build.props](https://github.com/derekdperez/Argus-Engine/blob/main/Directory.Build.props))

## Repository status and caveats

This repo still contains signs of an ongoing rename/migration from `NightmareV2` to `ArgusEngine`, including compatibility environment variables and development database names. ([DependencyInjection.cs](https://raw.githubusercontent.com/derekdperez/Argus-Engine/main/src/ArgusEngine.Infrastructure/DependencyInjection.cs), [StartupDatabaseInitializer.cs](https://raw.githubusercontent.com/derekdperez/Argus-Engine/refs/heads/main/src/ArgusEngine.CommandCenter/Startup/StartupDatabaseInitializer.cs))

One worker currently disables TLS certificate validation for broad reconnaissance. Treat this repository as operator-controlled infrastructure code and review the security implications before deployment. ([Spider Program.cs](https://raw.githubusercontent.com/derekdperez/Argus-Engine/refs/heads/main/src/ArgusEngine.Workers.Spider/Program.cs))

## Getting started

This repository does not currently have a clear root-level quickstart that matches the codebase. A practical onboarding flow should be:

1. Install the .NET SDK version required by `Directory.Build.props`.
2. Review the projects under `src/` to decide which services you want to run.
3. Configure PostgreSQL, Redis, RabbitMQ, and any file-store/database settings required by `ArgusEngine.Infrastructure`.
4. Start the Command Center, Gatekeeper, and the workers you need for your workflow.
5. Use the health endpoints and Command Center UI to validate service readiness.

Because the current root README is inaccurate, treat the source tree as the primary documentation until more complete setup docs are added. ([repo root README](https://github.com/derekdperez/Argus-Engine), [src layout](https://github.com/derekdperez/Argus-Engine/tree/main/src))

## Responsible use

This codebase is clearly designed for reconnaissance-style workflows. Only run it against systems and scopes you are explicitly authorized to assess.