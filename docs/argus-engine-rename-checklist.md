# Argus Engine rename compatibility checklist

The project has moved from the NightmareV2 codename to Argus Engine, but some deployed environments may still contain older configuration names, service names, database names, or table names. The safest migration path is to keep backwards compatibility at the boundary and avoid opportunistic production data renames.

## Rules

1. Public documentation, new code, new configuration, UI labels, metrics, and API contracts should use `Argus`.
2. Legacy `Nightmare` names should be accepted only at compatibility boundaries.
3. Current `Argus` values always win over legacy `Nightmare` values.
4. Do not rename production database objects without a dedicated migration plan, backfill, rollback, and verification window.
5. Deprecation should happen only after all running environments are confirmed to use `Argus` names.

## Supported configuration aliases

The shared `ArgusConfiguration` helper resolves values in this order:

1. `Argus:<Key>`
2. `Nightmare:<Key>`
3. `ARGUS_<KEY>`
4. `NIGHTMARE_<KEY>`

For example, `GetArgusValue("WorkerScaling:MaxDesiredCount")` can read any of the following keys:

- `Argus:WorkerScaling:MaxDesiredCount`
- `Nightmare:WorkerScaling:MaxDesiredCount`
- `ARGUS_WORKER_SCALING_MAX_DESIRED_COUNT`
- `NIGHTMARE_WORKER_SCALING_MAX_DESIRED_COUNT`

## Migration phases

### Phase 1 — compatibility

- Accept both Argus and Nightmare configuration.
- Emit metrics when legacy aliases are used.
- Keep existing database object names stable.
- Update docs and scripts to prefer Argus names.

### Phase 2 — deprecation

- Add warnings to deployment logs when legacy keys are used.
- Add dashboard panels for legacy-key usage.
- Remove legacy names from examples and templates.
- Create a release note with the target removal version.

### Phase 3 — removal

- Remove legacy aliases only after all environments are clean.
- Remove old service names only after infrastructure has been migrated.
- Rename database objects only with a dedicated migration and rollback plan.
