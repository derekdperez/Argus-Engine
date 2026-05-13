# Argus overlay: Ops counts + Maintenance + Autoscaler fixes

Apply from the project root:

```bash
unzip -o argus-engine-ops-counts-maintenance-autoscale-fix-overlay.zip
bash deploy/apply-argus-ops-counts-maintenance-autoscale-fix.sh
ARGUS_NO_UI=1 bash deploy/auto-all-in-one.sh --yes
```

This patch is intentionally small and source-based:
- OpsRadzen top summary counters use `/api/ops/overview` totals instead of target-grid data.
- Maintenance API registers `HttpQueueArtifactBackfillService`.
- `HttpArtifactBackfillEndpoints` marks that service parameter as `[FromServices]`.
- Worker Control autoscaler falls back to `/workspace` when the configured host repo path is not present inside the container.
