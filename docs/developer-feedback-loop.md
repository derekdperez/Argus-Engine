# Developer feedback loop deployment

`deploy.py` is the only deployment source of truth. It owns local Compose deploys, Google Cloud worker deployment, smoke checks, manifest validation, logs, scaling, and service operations without calling repository shell scripts.

## Normal fast deploy

```bash
./deploy deploy --hot
```

This is the safe default for day-to-day development. It brings the Compose stack up and can target selected services when you pass service names.

## Image rebuild

```bash
./deploy deploy --image command-center-web worker-spider
```

Use this when Dockerfile inputs, package dependencies, static assets, or runtime image contents changed.

## Full rebuild

```bash
./deploy deploy --fresh
```

Use this after base image problems, suspicious stale output, or major deploy recipe changes. It rebuilds images without cache and recreates containers.

## Google Cloud workers

```bash
./deploy deploy --gcp-workers
./deploy gcp scale worker-spider=2:10 worker-enum=2
```

GCP is a primary deployment path. The Python deploy console provisions required GCP services, builds/pushes worker images, deploys Cloud Run worker services, and supports autoscaling ranges or explicit counts.

## Practical workflow

```bash
# Validate manifests
./deploy validate

# Start or update everything
./deploy deploy --hot

# Rebuild changed service images when image inputs changed
./deploy deploy --image

# Smoke check the running stack
./deploy smoke

# If the environment is stale or confusing
./deploy deploy --fresh
```
