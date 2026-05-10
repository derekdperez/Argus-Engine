# Automatic All-in-One Deployment

This overlay adds a deployment-menu option for a local "automatic all-in-one" deploy.

## What it does

`deploy/auto-all-in-one.sh` performs the common local deployment path end-to-end:

1. Repairs repository ownership/permissions when needed.
2. Optionally runs `git pull --ff-only` when the working tree is clean.
3. Defaults every worker type to one replica.
4. Runs a sequential/plain image deployment so build failures are visible.
5. Runs `command-center-bootstrapper` explicitly after dependencies are up.
6. Restarts application services after bootstrap.
7. Verifies Command Center health endpoints.
8. Runs the smoke test when available.
9. Prints contextual error logs on failure.

## Menu usage

```bash
./deploy/deploy-ui.py
```

Choose:

```text
Automatic all-in-one local deploy — preflight, build, bootstrap, verify
```

The same option also appears in the Deploy/update submenu.

## Direct usage

```bash
bash deploy/auto-all-in-one.sh
```

Safe non-interactive defaults:

```bash
bash deploy/auto-all-in-one.sh --yes
```

Full rebuild:

```bash
bash deploy/auto-all-in-one.sh --fresh
```

Reset local compose volumes and rebuild from scratch:

```bash
bash deploy/auto-all-in-one.sh --reset-volumes --fresh
```

## Notes

This is for the local all-in-one Docker Compose deployment, meaning all core APIs,
web UI, dependencies, and workers run on the current machine.
