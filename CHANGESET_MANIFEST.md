# Changeset Manifest — Argus Engine `2.2.0`

This zip is path-preserved for extraction at the project root.

## Edited files

- `Directory.Build.props`
- `deploy/Dockerfile.web`
- `deploy/Dockerfile.worker`
- `deploy/Dockerfile.worker-enum`
- `deploy/docker-compose.yml`
- `ARGUS_REFACTOR_CHECKLIST.md`

## Added files

- `Directory.Build.targets`
- `VERSION`
- `deploy/.env.version.example`
- `docs/deployment-versioning.md`
- `scripts/verify-deployment-version.sh`
- `scripts/verify-deployment-version.ps1`
- `src/tests/NightmareV2.Infrastructure.Tests/DeploymentVersioningTests.cs`

## Version guarantee

- .NET assembly/package version: `2.2.0`
- .NET assembly/file version: `2.2.0.0`
- Docker `org.opencontainers.image.version`: `2.2.0` unless overridden by `ARGUS_ENGINE_VERSION`
- Compose image tags: `${ARGUS_ENGINE_VERSION:-2.2.0}`

## Notes

The current repository still uses `NightmareV2.*` project names, so this changeset does not attempt the destructive physical rename by unzip alone. It keeps the compatibility environment variables while making the deployable public image names and version labels Argus-branded.
