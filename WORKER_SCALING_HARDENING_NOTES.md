# Worker scaling hardening patch

This overlay fixes the two failures shown in the recent logs:

1. `command-center-worker-control-api` could not reach `/var/run/docker.sock`.
2. Docker Compose scale commands were fragile and could be parsed as invalid Docker flags.

## Files changed

- `src/ArgusEngine.CommandCenter.WorkerControl.Api/Services/DockerComposeWorkerScaler.cs`
  - New shared shell-safe helper for Docker status and scaling.
  - Uses `/bin/sh -lc` and supports both Docker Compose V2 (`docker compose`) and legacy `docker-compose`.
  - Logs the exact command before scaling.

- `src/ArgusEngine.CommandCenter.WorkerControl.Api/Services/WorkerAutoscalerBackgroundService.cs`
  - Uses the shared helper.
  - Fixes scale-decision logging argument order.
  - Handles missing current Docker counts cleanly.
  - Removes the broken pipe/grep Docker-count command path.

- `src/ArgusEngine.CommandCenter.WorkerControl.Api/Endpoints/DockerWorkerEndpoints.cs`
  - Manual development-page scale buttons now use the same hardened helper path.

- `deploy/Dockerfile.base-runtime`
  - Adds `docker-cli-compose` so `docker compose` exists inside the Worker Control API container.

- `deploy/docker-compose.worker-control-docker.override.yml`
  - Mounts `/var/run/docker.sock`.
  - Mounts the repo path read-only at the same absolute path used by the compose command.
  - Sets `Argus__Autoscaler__DockerComposePath`, `Argus__Autoscaler__RepoRoot`, and project name.

## Apply

From the repo root:

```bash
unzip argus-worker-scaling-hardening.zip -d .
```

Set the repo root path if it is different from `/home/derekdperez_dev/argus-engine`:

```bash
export ARGUS_REPO_ROOT=/home/derekdperez_dev/argus-engine
```

Rebuild the runtime base image so `docker-cli-compose` is present:

```bash
docker build -t argus-engine-base:local -f deploy/Dockerfile.base-runtime deploy/
```

Rebuild and restart Worker Control API with the override:

```bash
docker compose \
  -f deploy/docker-compose.yml \
  -f deploy/docker-compose.worker-control-docker.override.yml \
  up -d --build command-center-worker-control-api
```

Then confirm Docker and Compose work from inside the container:

```bash
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.worker-control-docker.override.yml exec command-center-worker-control-api sh -lc '
  docker ps >/dev/null &&
  docker compose version &&
  test -f "$Argus__Autoscaler__DockerComposePath"
'
```

Finally test a manual scale:

```bash
curl -fsS -X PUT \
  -H 'Content-Type: application/json' \
  -d '{"desiredCount":3}' \
  http://127.0.0.1:8085/api/workers/worker-http-requester/docker-scale
```

If this succeeds, the development-page scale up/down buttons should use the same working API path.
