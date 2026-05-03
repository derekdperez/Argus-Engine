# Changeset Manifest — Direct File Overlay v2.6.1

Generated: 2026-05-03T05:03:09.174125Z

This zip contains direct files for extraction into the project root.

## Version

- Deployment version: `2.6.1`
- Assembly/File version: `2.6.1.0`

## Deletion cleanup

After extracting, run the included root script to remove old tracked NightmareV2 paths:

```powershell
.\Delete-OldNightmareV2Files.ps1 -WhatIf
.\Delete-OldNightmareV2Files.ps1 -Force
```

## File count

94 files

## Files

- `ARGUS_REFACTOR_CHECKLIST.md`
- `ArgusEngine.slnx`
- `CHANGESET_MANIFEST.md`
- `DIRECT_OVERLAY_README.md`
- `Delete-OldNightmareV2Files.ps1`
- `Directory.Build.props`
- `Directory.Build.targets`
- `OBSOLETE_NIGHTMAREV2_PATHS_TO_REMOVE.md`
- `VERSION`
- `deploy/.env.version.example`
- `deploy/Dockerfile.web`
- `deploy/Dockerfile.worker`
- `deploy/Dockerfile.worker-enum`
- `deploy/docker-compose.observability.yml`
- `deploy/docker-compose.yml`
- `deploy/observability/grafana/dashboards/argus-engine-overview.json`
- `deploy/observability/grafana/provisioning/dashboards/dashboards.yml`
- `deploy/observability/grafana/provisioning/datasources/datasources.yml`
- `deploy/observability/otel-collector-config.yml`
- `deploy/observability/prometheus.yml`
- `docs/argus-engine-migration-note.md`
- `docs/deployment-versioning.md`
- `docs/observability.md`
- `docs/original-checklist-completion.md`
- `scripts/verify-deployment-version.ps1`
- `scripts/verify-deployment-version.sh`
- `src/ArgusEngine.Application/ArgusEngine.Application.csproj`
- `src/ArgusEngine.Application/Assets/UrlFetchSnapshot.cs`
- `src/ArgusEngine.Application/DataRetention/DataRetentionOptions.cs`
- `src/ArgusEngine.Application/DataRetention/DataRetentionRunResult.cs`
- `src/ArgusEngine.Application/FileStore/HttpArtifactOptions.cs`
- `src/ArgusEngine.Application/FileStore/HttpArtifactRef.cs`
- `src/ArgusEngine.Application/FileStore/IHttpArtifactReader.cs`
- `src/ArgusEngine.Application/FileStore/IHttpArtifactStore.cs`
- `src/ArgusEngine.Application/Gatekeeping/AssetAdmissionDecisionInput.cs`
- `src/ArgusEngine.Application/Gatekeeping/GatekeeperOrchestrator.cs`
- `src/ArgusEngine.Application/Gatekeeping/IAssetAdmissionDecisionWriter.cs`
- `src/ArgusEngine.Application/HighValue/HighValueScanOptions.cs`
- `src/ArgusEngine.Application/TechnologyIdentification/TechnologyIdentificationScanOptions.cs`
- `src/ArgusEngine.CommandCenter/ArgusEngine.CommandCenter.csproj`
- `src/ArgusEngine.CommandCenter/Components/Layout/MainLayout.razor`
- `src/ArgusEngine.CommandCenter/Components/Pages/AssetAdmission.razor`
- `src/ArgusEngine.CommandCenter/DataMaintenance/HttpQueueArtifactBackfillService.cs`
- `src/ArgusEngine.CommandCenter/Endpoints/AssetAdmissionDecisionEndpoints.cs`
- `src/ArgusEngine.CommandCenter/Endpoints/CommandCenterEndpointRegistration.cs`
- `src/ArgusEngine.CommandCenter/Endpoints/DataRetentionAdminEndpoints.cs`
- `src/ArgusEngine.CommandCenter/Endpoints/HttpArtifactBackfillEndpoints.cs`
- `src/ArgusEngine.CommandCenter/Startup/CommandCenterMiddleware.cs`
- `src/ArgusEngine.CommandCenter/Startup/CommandCenterServiceRegistration.cs`
- `src/ArgusEngine.CommandCenter/Startup/StartupDatabaseInitializer.cs`
- `src/ArgusEngine.Contracts/ArgusEngine.Contracts.csproj`
- `src/ArgusEngine.Domain/ArgusEngine.Domain.csproj`
- `src/ArgusEngine.Domain/Entities/AssetAdmissionDecision.cs`
- `src/ArgusEngine.Domain/Entities/AssetAdmissionDecisionKind.cs`
- `src/ArgusEngine.Domain/Entities/AssetAdmissionReasonCode.cs`
- `src/ArgusEngine.Domain/Entities/HttpRequestQueueItem.cs`
- `src/ArgusEngine.Gatekeeper/ArgusEngine.Gatekeeper.csproj`
- `src/ArgusEngine.Gatekeeper/Program.cs`
- `src/ArgusEngine.Infrastructure/ArgusEngine.Infrastructure.csproj`
- `src/ArgusEngine.Infrastructure/Configuration/ArgusConfiguration.cs`
- `src/ArgusEngine.Infrastructure/DataRetention/DataRetentionRunState.cs`
- `src/ArgusEngine.Infrastructure/DataRetention/DataRetentionWorker.cs`
- `src/ArgusEngine.Infrastructure/DataRetention/IPartitionMaintenanceService.cs`
- `src/ArgusEngine.Infrastructure/DataRetention/PostgresPartitionMaintenanceHostedService.cs`
- `src/ArgusEngine.Infrastructure/DataRetention/PostgresPartitionMaintenanceService.cs`
- `src/ArgusEngine.Infrastructure/DependencyInjection.cs`
- `src/ArgusEngine.Infrastructure/FileStore/EfHttpArtifactStore.cs`
- `src/ArgusEngine.Infrastructure/FileStore/HttpRequestQueueArtifactSchemaInitializer.cs`
- `src/ArgusEngine.Infrastructure/Gatekeeping/AssetAdmissionDecisionSchemaInitializer.cs`
- `src/ArgusEngine.Infrastructure/Gatekeeping/EfAssetAdmissionDecisionWriter.cs`
- `src/ArgusEngine.Infrastructure/Messaging/OutboxDispatcherWorker.cs`
- `src/ArgusEngine.Infrastructure/Observability/ArgusMeters.cs`
- `src/ArgusEngine.Infrastructure/Observability/ArgusMetrics.cs`
- `src/ArgusEngine.Infrastructure/Observability/ArgusObservabilityExtensions.cs`
- `src/ArgusEngine.Infrastructure/Observability/ArgusTracing.cs`
- `src/ArgusEngine.Workers.Enum/ArgusEngine.Workers.Enum.csproj`
- `src/ArgusEngine.Workers.Enum/Program.cs`
- `src/ArgusEngine.Workers.HighValue/ArgusEngine.Workers.HighValue.csproj`
- `src/ArgusEngine.Workers.HighValue/Consumers/HighValueRegexConsumer.cs`
- `src/ArgusEngine.Workers.HighValue/Program.cs`
- `src/ArgusEngine.Workers.PortScan/ArgusEngine.Workers.PortScan.csproj`
- `src/ArgusEngine.Workers.PortScan/Program.cs`
- `src/ArgusEngine.Workers.Spider/ArgusEngine.Workers.Spider.csproj`
- `src/ArgusEngine.Workers.Spider/HttpRequestQueueWorker.cs`
- `src/ArgusEngine.Workers.Spider/Program.cs`
- `src/ArgusEngine.Workers.TechnologyIdentification/ArgusEngine.Workers.TechnologyIdentification.csproj`
- `src/ArgusEngine.Workers.TechnologyIdentification/Consumers/TechnologyIdentificationConsumer.cs`
- `src/ArgusEngine.Workers.TechnologyIdentification/Program.cs`
- `src/tests/ArgusEngine.CommandCenter.Tests/ArgusEngine.CommandCenter.Tests.csproj`
- `src/tests/ArgusEngine.CommandCenter.Tests/CommandCenterChecklistTests.cs`
- `src/tests/ArgusEngine.Infrastructure.Tests/ArgusEngine.Infrastructure.Tests.csproj`
- `src/tests/ArgusEngine.Infrastructure.Tests/DeploymentVersioningTests.cs`
- `src/tests/ArgusEngine.Infrastructure.Tests/ObservabilityStackTests.cs`
- `src/tests/ArgusEngine.Infrastructure.Tests/OriginalChecklistImplementationTests.cs`


## Build fix 2.6.1

- Fixed NuGet restore failure caused by executable projects referencing OpenTelemetry `1.10.0` while `ArgusEngine.Infrastructure` referenced `1.15.x`.
- Updated Gatekeeper and worker OpenTelemetry package versions to match Infrastructure.
- Bumped deployment version to `2.6.1 / 2.6.1.0`.
- Added `BUILD_FIX_2.6.1.md`.
