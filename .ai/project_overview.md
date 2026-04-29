# NightmareV2 Project Overview

- Purpose: event-driven reconnaissance pipeline for bug bounty target discovery, HTTP probing, and high-value finding extraction.
- Major components:
  - `NightmareV2.CommandCenter`: API + operations UI.
  - `NightmareV2.Gatekeeper`: admission, scope, dedupe, persistence.
  - `NightmareV2.Workers.*`: enumeration, spider HTTP queue drain, high-value scanners, portscan placeholder.
  - `NightmareV2.Infrastructure`: Postgres/Redis/bus integration.
  - `NightmareV2.Contracts`: cross-service event contracts.
- Runtime model: multiple worker services communicate through MassTransit over RabbitMQ with durable outbox dispatch and inbox dedupe; URL fetching runs through a Postgres-backed durable queue drained by spider workers.
- Primary dependencies: .NET 10, MassTransit, Npgsql/EF Core, StackExchange.Redis, Blazor Server UI.
