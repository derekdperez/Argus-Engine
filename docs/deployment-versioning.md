# Deployment Versioning

Argus Engine displays each deployed .NET component version on the Command Center System Status page.

## Current deployment version

`2.3.0`

## Required deployment rule

Before every deployment, bump the version in:

1. `VERSION`
2. `Directory.Build.targets` / `ArgusEngineDeploymentVersion`
3. `deploy/docker-compose.yml` / `ARGUS_ENGINE_VERSION` default
4. Dockerfile `COMPONENT_VERSION` defaults

The Docker build passes this value into .NET assembly metadata and OCI image labels. The compose file also tags images with the same value, so the website System Status page can distinguish each deployment.

## Verification

```bash
./scripts/verify-deployment-version.sh 2.3.0
```

```powershell
./scripts/verify-deployment-version.ps1 -ExpectedVersion 2.3.0
```
