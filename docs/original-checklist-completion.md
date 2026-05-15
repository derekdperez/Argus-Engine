# Original Checklist Completion Notes

This overlay completes the source-level changes requested in the original Argus Engine / NightmareV2 refactor and hardening plan.

## How to apply

From the repository root:

```bash
unzip argus-engine-rest-of-original-checklist-v2.6.1-overlay.zip -d .
python scripts/apply-original-checklist-refactor.py --dry-run
python scripts/apply-original-checklist-refactor.py --apply
python scripts/validate-original-checklist.py
```

Then run the full validation locally:

```bash
dotnet restore ArgusEngine.slnx
dotnet build ArgusEngine.slnx
dotnet test ArgusEngine.slnx
docker compose -f deployment/docker-compose.yml build
docker compose -f deployment/docker-compose.yml -f deployment/docker-compose.observability.yml up -d --build
```

## Destructive rename boundary

The zip can add `ArgusEngine.slnx` and the migration script, but unzipping alone cannot delete or move the existing `src/NightmareV2.*` directories in an already checked-out repository. The script performs the project directory, project file, namespace, and branded type renames.

Database names and existing table names remain stable. Do not rename `nightmare_v2`, `nightmare_v2_files`, or existing production tables without a separate migration/backfill plan.

## Deployment version

This overlay bumps the deployable version to `2.6.1` / `2.6.1.0`.
