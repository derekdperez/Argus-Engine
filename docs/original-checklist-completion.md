# Original Checklist Completion Notes

This overlay completes the remaining implementation work from the original Argus Engine / NightmareV2 hardening plan.

## Important extraction behavior

Unzipping the overlay writes changed and added files. A zip extraction cannot remove old directories or rename existing project directories. To complete the destructive repo-wide `NightmareV2.*` -> `ArgusEngine.*` rename, run:

```bash
python scripts/apply-original-checklist-refactor.py --apply
```

or:

```bash
./scripts/apply-argus-engine-rename.sh
```

The script keeps existing database names and table names stable.

## Deployment version

This overlay bumps the deployment version to `2.3.0` / `2.3.0.0`.

## Verification commands

```bash
dotnet build ArgusEngine.slnx
dotnet test ArgusEngine.slnx
docker compose -f deploy/docker-compose.yml build
```
