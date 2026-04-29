# Developer feedback loop deployment

This repo now supports two development deployment paths.

## Normal fast image deploy

```bash
./deploy/deploy.sh
```

This remains the safe default. The deploy script fingerprints each app service separately and rebuilds only the service images whose inputs changed. The fingerprint is split internally into:

- **source inputs**: the target project, shared .NET projects, `Directory.Build.props`, and linked resource files;
- **image inputs**: Dockerfile, Compose recipe, and enum tool/wordlist inputs.

When only `worker-spider` changed, only the `worker-spider` image is rebuilt. When shared projects such as `NightmareV2.Infrastructure` change, dependent service images are rebuilt.

## Source-only hot swap

```bash
./deploy/deploy.sh --hot
# or
./deploy/run-local.sh --hot
```

Use this when the stack is already running and the change is .NET source only. The script:

1. detects which service fingerprints changed;
2. checks whether the image recipe changed;
3. for source-only changes, publishes the changed project inside `mcr.microsoft.com/dotnet/sdk:10.0` using the repo-local `.nuget/packages` cache;
4. copies the publish output into the running container at `/app`;
5. restarts only that service.

If the service is not running, or if Dockerfile/Compose/tool inputs changed, the script falls back to a normal image rebuild for that service.

## Cache warm-up

```bash
./deploy/prebuild-cache.sh
```

Run this after cloning, after dependency changes, or before a long debugging session. It builds all app images once and warms:

- Docker build cache layers;
- Dockerfile NuGet cache mounts;
- Go module/build caches for the enum worker tools;
- repo-local `.nuget/packages` used by hot-swap publishing.

## Full rebuild

```bash
./deploy/deploy.sh -fresh
```

Use this after base image problems, suspicious stale output, or major deploy recipe changes. It forces `--pull --no-cache` and recreates containers.

## Dockerfile changes

The worker images were split:

- `deploy/Dockerfile.worker` is now the generic .NET worker image and no longer installs `subfinder` or `amass`.
- `deploy/Dockerfile.worker-enum` is the only image that builds and includes `subfinder`, `amass`, and the enum wordlist.
- `deploy/Dockerfile.web` and both worker Dockerfiles copy only the project dependency closure instead of the entire `src` tree, so unrelated worker edits do not invalidate Docker publish layers.

## Practical workflow

```bash
# Once per machine / after dependencies change
./deploy/prebuild-cache.sh

# Start everything
./deploy/deploy.sh

# Edit one worker or the web app, then hot-swap source-only changes
./deploy/deploy.sh --hot

# If Dockerfile, compose, packages, external tools, or wordlists changed
./deploy/deploy.sh

# If the environment is stale or confusing
./deploy/deploy.sh -fresh
```
