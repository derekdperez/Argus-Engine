# Deployment Versioning

Argus Engine displays each deployed .NET component version on the Command Center System Status page. The displayed value comes from Docker image tags/OCI labels and from .NET assembly metadata.

## Current deployment version

`2.2.0`

## Required rule

Before every deployment, bump the version in:

1. `VERSION`
2. `Directory.Build.targets` / `ArgusEngineDeploymentVersion`
3. `deploy/docker-compose.yml` / `ARGUS_ENGINE_VERSION` default
4. Dockerfile `COMPONENT_VERSION` defaults

The compose file tags all Argus images with `${ARGUS_ENGINE_VERSION:-2.2.0}` and passes the same value into Docker build labels. The Dockerfiles also pass that value into `dotnet publish` as `Version`, `AssemblyVersion`, `FileVersion`, and `InformationalVersion`, so the System Status page will not continue showing stale `2.0.0` values after deployment.

## Verification

Run one of these from the project root before deployment:

```bash
./scripts/verify-deployment-version.sh 2.2.0
```

```powershell
./scripts/verify-deployment-version.ps1 -ExpectedVersion 2.2.0
```

## Compatibility note

The repo still contains `NightmareV2.*` project names internally, but the deployed image names and labels now use `argus-engine/*`. Old `Nightmare__*` environment variables are still supplied temporarily alongside the new `Argus__*` variables.
