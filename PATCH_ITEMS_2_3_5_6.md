# Latest-main patch for items 2, 3, 5, and 6

This zip was regenerated against the current GitHub `main` layout that already includes the restored Command Center, diagnostics/data-maintenance endpoints, and existing e2e scripts. It preserves those additions and layers the requested changes on top.

# Argus Engine patch: items 2, 3, 5, and 6

This patch is rebased onto the latest `main` branch layout where the Command Center restore overlay and the expanded endpoint set are already present.

## Item 2 — Command Center UX and live operational status

Implemented:

- Reworked `Status.razor` into an operational dashboard backed by `/api/status/summary`.
- Preserved the latest header/nav layout and converted navigation to styled `NavLink` buttons.
- Added status models for components, workers, queues, dependencies, SLO indicators, and alerts.
- Added a SignalR-aware `DiscoveryRealtimeClient` that now handles domain events, target queue events, worker events, queue events, and status snapshots.
- Added a realtime publisher abstraction that can be used by endpoints/services as UI updates are made event-driven.
- Replaced the fixed UTC-5 formatter with a DST-aware Eastern Time formatter.

## Item 3 — E2E pipeline coverage

Implemented:

- Added `src/tests/e2e/gatekeeper-pipeline.sh`.
- The script waits for Command Center, creates a target through `/api/targets`, verifies it is readable through `/api/targets`, verifies root HTTP queue seeding through `/api/http-request-queue`, and validates `/api/status/summary`.

Run it after the local stack is up:

```bash
ARGUS_COMMAND_CENTER_URL=http://localhost:8080 bash src/tests/e2e/gatekeeper-pipeline.sh
```

## Item 5 — Argus rename compatibility

Implemented:

- `ArgusConfiguration` now supports:
  - `Argus:*`
  - `Nightmare:*`
  - `ARGUS_*`
  - `NIGHTMARE_*`
- Current Argus configuration values win over legacy Nightmare values.
- Added compatibility key generation helper.
- Added xUnit tests covering current, legacy, environment-style, and typed value resolution.
- Added `docs/argus-engine-rename-checklist.md`.

## Item 6 — actionable observability

Implemented:

- Added operational metric name constants.
- Extended `ArgusMeters` with metrics for queue age/depth, worker desired/running counts, dependency health, realtime UI events, config alias usage, and operational alerts.
- Added `/api/status/summary` as a machine-readable operational snapshot.
- Added `docs/observability-actionable.md` with SLOs, alert thresholds, dashboard guidance, and runbook pointers.

## Validation

After unzipping this patch into the project root, run:

```bash
dotnet restore ArgusEngine.slnx
dotnet build ArgusEngine.slnx
dotnet test ArgusEngine.slnx
ARGUS_COMMAND_CENTER_URL=http://localhost:8080 bash src/tests/e2e/gatekeeper-pipeline.sh
```
