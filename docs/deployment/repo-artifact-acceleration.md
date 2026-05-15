# Repo-Local Deployment Artifacts

Argus can optionally keep selected pre-downloaded or pre-built artifacts in git
to make cold deployments faster. This is most useful for fresh EC2 builders,
new Codespaces, short-lived CI runners, or any host without warm Docker BuildKit
caches.

## What Is Supported

### NuGet Global Package Cache

Run:

```bash
./dotnet restore
```

This restores every deployed service project into:

```text
deploy/artifacts/nuget/packages/
```

The service Dockerfiles copy those packages into the BuildKit NuGet cache before
`dotnet restore`. Missing packages still download normally, so the artifact set
can be partial during transition.

Tradeoff: this can add many files to git. Use it when cold deployment speed is
more important than repository size.

### Recon Tool Binaries

Run:

```bash
./deploy/deploy.py gcp build
```

This downloads the pinned `subfinder` and `amass` release archives, verifies the
published checksums, and writes the Linux amd64 binaries to:

```text
deploy/artifacts/recon-tools/linux-amd64/
```

`deploy/Dockerfile.worker-enum` copies these binaries directly into the enum
worker image. `deploy/Dockerfile.base-recon` is also zero-download and only
packages these committed binaries. If either binary is missing, the Docker build
fails immediately instead of compiling Go projects during deployment.

Tradeoff: committed binaries must be refreshed when the pinned release versions
change.

## Useful But Heavier Option

For the fastest possible bootstrap on machines that cannot pull from a nearby
registry, export Docker base images into git-managed tarballs:

```bash
./docker buildx build
docker save argus-engine-base:local | gzip > deploy/artifacts/images/argus-engine-base.local.tar.gz
docker save argus-recon-base:local | gzip > deploy/artifacts/images/argus-recon-base.local.tar.gz
```

Then load them before deployment:

```bash
./docker load
```

`deploy/deploy.py` also tries this loader before rebuilding missing base images.
This is intentionally opt-in because image tarballs are large. Prefer ECR or
another registry cache when available.

## Recommended Commit Policy

Commit these artifacts only when they provide measurable deployment speedup:

- Commit `deploy/artifacts/recon-tools` for reliable enum-worker builds.
- Commit `deploy/artifacts/nuget/packages` for air-gapped or routinely cold
  builders.
- Avoid committing app publish output unless a release process also verifies the
  artifact fingerprint against the source revision.
