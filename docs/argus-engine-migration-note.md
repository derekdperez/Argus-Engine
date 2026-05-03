# Argus Engine migration note

Argus Engine was previously developed under the internal codename NightmareV2.

During the migration window, keep these backward-compatible inputs supported:

- `Nightmare:*` configuration keys
- `NIGHTMARE_*` environment variables
- existing database names such as `nightmare_v2` and `nightmare_v2_files`
- existing database table names such as `recon_targets`, `stored_assets`, `http_request_queue`, `outbox_messages`, `inbox_messages`, `bus_journal`, `high_value_findings`, `technology_detections`, and `asset_relationships`

Use `scripts/apply-argus-engine-rename.ps1` or `scripts/apply-argus-engine-rename.sh` when the team is ready to do the repo-wide path/namespace rename in one controlled commit.
