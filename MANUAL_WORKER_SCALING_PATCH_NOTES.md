# Manual Worker Scaling Patch

This overlay wires the Development page worker scale buttons to the Worker Control API Docker Compose scale endpoints.

## Files changed

- `src/ArgusEngine.CommandCenter.Web/Components/Pages/Development.razor`
  - Uses `WorkerControlApiClient` for worker status and scale up/down actions.
  - Scale down now changes the Docker Compose desired count instead of killing a container directly.

- `src/ArgusEngine.CommandCenter.Web/Clients/WorkerControlApiClient.cs`
  - Adds typed helper methods for `/api/workers/docker-status` and `/api/workers/{serviceName}/docker-scale`.

- `src/ArgusEngine.CommandCenter.WorkerControl.Api/Program.cs`
  - Maps `MapDockerWorkerEndpoints()` so the existing Docker worker endpoints are reachable through `/api/workers/...`.

- `src/ArgusEngine.CommandCenter.WorkerControl.Api/Endpoints/DockerWorkerEndpoints.cs`
  - Reads Compose path/project from configuration.
  - Uses `docker compose -p argus-engine -f <compose-file> up -d --no-build --no-deps --scale ...`.

- `deploy/docker-compose.worker-control-docker.override.yml`
  - Mounts the Docker socket and repository path into `command-center-worker-control-api`.

## Deploy

From the repository root:

```bash
unzip argus-manual-worker-scaling-fix.zip -d .
docker compose \
  -f deploy/docker-compose.yml \
  -f deploy/docker-compose.worker-control-docker.override.yml \
  up -d --build command-center-worker-control-api command-center-web command-center-gateway
```

Then open:

```text
http://104.196.4.155:8081/development
```

Use the Local Worker Scaling section. The Scale Up and Scale Down buttons should now call the Worker Control API and run Docker Compose scaling.
