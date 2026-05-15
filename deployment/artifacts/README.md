# Deployment Artifacts

This directory is intentionally part of the Docker build context. It can hold
repo-committed artifacts that remove repeated network/download work on fresh
deployment hosts.

Supported artifact sets:

- `nuget/packages/` - optional .NET global package cache, populated by
  `dotnet restore`.
- `recon-tools/linux-amd64/` - optional `subfinder` and `amass` binaries,
  populated by `deploy/deploy.py gcp build`.

The directories are empty by default so normal source-only development stays
lightweight. If deployment speed on cold hosts matters more than repository
size, run the vendor scripts and commit the generated files plus manifests.
