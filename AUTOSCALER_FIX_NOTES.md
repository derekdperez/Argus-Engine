# Argus Engine Autoscaler Fix

This bundle contains only modified or added files.

## Files

- `src/ArgusEngine.CommandCenter.WorkerControl.Api/Services/WorkerAutoscalerBackgroundService.cs`
  - Replaces the shell-pipeline Docker worker count query with JSON parsing from `docker ps`.
  - Fixes the scale decision log argument order.
  - Removes the non-nullable `int == null` check.
  - Removes `--no-recreate` from the scale command so Compose can add replicas.
  - Adds configurable Docker Compose project name support.

- `deploy/Dockerfile.base-runtime`
  - Adds `docker-cli-compose`, required for the `docker compose` command used by the autoscaler.

- `deploy/docker-compose.autoscaler-fix.yml`
  - Adds Docker socket access and the repository mount to `command-center-worker-control-api`.

## Apply

Unzip this bundle at the repository root, then rebuild and restart the worker control API:

```bash
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.autoscaler-fix.yml up -d --build command-center-worker-control-api
```

Then watch logs:

```bash
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.autoscaler-fix.yml logs -f command-center-worker-control-api
```

Expected log:

```text
Worker autoscaler background service started.
```
