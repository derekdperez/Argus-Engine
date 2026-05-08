# Repo-Local Deployment Artifacts

Argus can optionally keep selected pre-downloaded or pre-built artifacts in git
to make cold deployments faster. This is most useful for fresh EC2 builders,
new Codespaces, short-lived CI runners, or any host without warm Docker BuildKit
caches.

## What Is Supported

### NuGet Global Package Cache

Run:

```bash
./deploy/vendor-nuget-packages.sh
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
./deploy/vendor-recon-tools.sh
```

This builds the pinned `subfinder` and `amass` packages once and writes the
Linux amd64 binaries to:

```text
deploy/artifacts/recon-tools/linux-amd64/
```

`deploy/Dockerfile.base-recon` uses these binaries when both are present. If
either binary is missing, it falls back to `go install`.

Tradeoff: committed binaries must be refreshed when the pinned Go package
versions change.

## Useful But Heavier Option

For the fastest possible bootstrap on machines that cannot pull from a nearby
registry, export Docker base images into git-managed tarballs:

```bash
./deploy/build-base-images.sh
docker save argus-engine-base:local | gzip > deploy/artifacts/images/argus-engine-base.local.tar.gz
docker save argus-recon-base:local | gzip > deploy/artifacts/images/argus-recon-base.local.tar.gz
```

Then load them before deployment:

```bash
docker load -i deploy/artifacts/images/argus-engine-base.local.tar.gz
docker load -i deploy/artifacts/images/argus-recon-base.local.tar.gz
```

This is intentionally not automated by default because image tarballs are large.
Prefer ECR or another registry cache when available.

## Recommended Commit Policy

Commit these artifacts only when they provide measurable deployment speedup:

- Commit `deploy/artifacts/recon-tools` for reliable enum-worker builds.
- Commit `deploy/artifacts/nuget/packages` for air-gapped or routinely cold
  builders.
- Avoid committing app publish output unless a release process also verifies the
  artifact fingerprint against the source revision.
